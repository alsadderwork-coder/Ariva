import { expect, test } from '@playwright/test';
import { canary } from '../support/log-canary';
import { apiHosts } from '../support/hosts';

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
