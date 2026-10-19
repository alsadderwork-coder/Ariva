import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { canary } from '../support/log-canary';
import { apiHosts, type HostName } from '../support/hosts';

// ARV-007 (CWE-532): send credentials the way clients do (Authorization header, refresh cookie, TOTP header, SignalR
// access_token query parameter, a password in a JSON body, a display player's key header). global-teardown.ts then fails the run if any of them, or any
// other bearer token or JWT, appears in a host log.
test.describe('credentials never reach the logs', () => {
	for (const host of apiHosts) {
		test(`${host.name} handles requests carrying credentials`, async ({ request }) => {
			const headers = {
				Authorization: `Bearer ${canary.jwt}`,
				Cookie: `__Secure-ariva_rt=${canary.refreshCookie}`,
				'X-TOTP-Code': canary.totpCode
			};

			const probe = await request.get(`${host.url}/health/readiness`, { headers });
			const protectedRoute = await request.get(`${host.url}/api/does-not-exist?access_token=${canary.opaqueToken}`, { headers });
			const hub = await request.get(`${host.url}/hubs/live?access_token=${canary.jwt}`);
			const board = await request.get(`${host.url}/api/v1/display/board?code=E2E-CANARY`, { headers: { 'X-Ariva-Display-Key': canary.displayKey } });
			const login = await request.post(`${host.url}/api/auth/login`, {
				headers: { Authorization: `Basic ${canary.basic}` },
				data: { userName: 'canary-user', password: canary.password }
			});

			expect(probe.status()).toBe(200);
			// Default deny: nothing behind the placeholder scheme accepts these credentials.
			for (const response of [protectedRoute, hub, login, board]) {
				expect([401, 404, 405]).toContain(response.status());
			}
		});
	}
});

// ARV-091 (ASVS 5.0 V16.2.1, V16.3.2): a refused request is a security event in the host's own log. An anonymous call to a
// protected route is challenged (401) and logged as SecurityEvent.AuthorizationChallenged (9115) at Warning, with who
// (CallerUserId anonymous), from where (CallerAddress, the client the trusted proxy names), the trace id of the answer, the
// method and the endpoint's route pattern (none here), and never the refused input: the canary sent in the query is in no
// host log line (global-teardown.ts scans every line for it). Each host writes at most 3 such events a second per source and
// 60 per kind, so a request whose event the throttle left out is sent again.
const logFiles: Partial<Record<HostName, string>> = {
	main: 'Ariva.Api.Main.log',
	ingest: 'Ariva.Api.Ingest.log',
	integration: 'Ariva.Api.Integration.log',
	cronz: 'Ariva.Api.Cronz.log'
};

type LogLine = { Level?: string; TraceId?: string; Properties?: Record<string, unknown> & { EventId?: { Id?: number } } };

function eventsOf(file: string, eventId: number): LogLine[] {
	if (!fs.existsSync(file)) return [];
	return fs
		.readFileSync(file, 'utf8')
		.split(/\r?\n/)
		.filter((line) => line.includes(`"Id":${eventId}`))
		.map((line) => JSON.parse(line) as LogLine)
		.filter((line) => line.Properties?.EventId?.Id === eventId);
}

test.describe('refused requests are security events with who and where (ARV-091)', () => {
	for (const host of apiHosts.filter((h) => logFiles[h.name])) {
		test(`${host.name} logs a challenged request without the refused input`, async ({ request }) => {
			const directory = process.env.ARIVA_E2E_LOG_DIR;
			expect(directory, 'the hosts write LogFile__Path to this run\'s folder').toBeTruthy();
			const file = path.join(directory!, logFiles[host.name]!);
			const client = '198.51.100.91';

			let logged: LogLine | undefined;
			for (let attempt = 0; attempt < 10 && !logged; attempt++) {
				const response = await request.get(`${host.url}/api/v1/e2e-refused?probe=${encodeURIComponent(`<script>${canary.refusedInput}`)}`, {
					headers: { 'X-Forwarded-For': client }
				});
				expect(response.status()).toBe(401);
				const body = await response.text();
				expect(body).not.toContain(canary.refusedInput);
				const traceId = String((JSON.parse(body) as { traceId?: string }).traceId ?? '').split('-')[1];
				expect(traceId, 'the answer names its trace').toMatch(/^[0-9a-f]{32}$/);
				for (const deadline = Date.now() + 2_000; !logged && Date.now() < deadline; ) {
					logged = eventsOf(file, 9115).find((line) => line.TraceId === traceId);
					if (!logged) await new Promise((resolve) => setTimeout(resolve, 100));
				}
			}

			expect(logged, `${host.name} wrote SecurityEvent.AuthorizationChallenged for the request`).toBeDefined();
			expect(logged!.Level).toBe('Warning');
			expect(logged!.Properties).toMatchObject({
				CallerUserId: 'anonymous',
				CallerAddress: client,
				Method: 'GET',
				Endpoint: '(none)',
				Reason: 'NotAuthenticated'
			});
			expect(logged!.Properties).not.toHaveProperty('RequestPath');
			expect(JSON.stringify(logged)).not.toContain(canary.refusedInput);
		});
	}
});
