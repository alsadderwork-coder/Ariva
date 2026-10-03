import fs from 'node:fs';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, integrationSeedsFile, login, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-029: the AODB, AMAN and immigration emulators of Ariva.Simulation.Api. The emulated AODB, given an integration
// client an administrator registered for DMO (flights:write), pushes a demo minute's schedule to Ariva's AIDX endpoint
// over its own token exchange, and Ariva applies the legs as that client's feed; its ACRIS answer needs its API key.
// The mock AMAN Integration API exchanges client id, secret and a TOTP code for a token, once per step, and serves what
// the emulated AMAN published to a caller with the token and a current code. Feed controls need the control scope, and
// client secrets never come back out of the simulator.

test.skip(!databaseAvailable, 'the emulators write to the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true) and need the DMO demo seed');
test.describe.configure({ mode: 'serial' });

const feeds = `${hosts.simulation}/api/v1/simulation/feeds`;
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };
const amanSecret = process.env.ARIVA_E2E_MOCK_AMAN_SECRET ?? '';
const amanSeed = process.env.ARIVA_E2E_MOCK_AMAN_SEED ?? '';
const acrisKey = process.env.ARIVA_E2E_ACRIS_KEY ?? '';
let admin = '';

async function query<T = Record<string, unknown>>(sql: string, values: unknown[] = []): Promise<T[]> {
	const db = new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
	await db.connect();
	try {
		return (await db.query(sql, values)).rows as T[];
	} finally {
		await db.end();
	}
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().emulatorFeedAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;
});

test('feed controls need the control scope and client secrets never come back out', async () => {
	expect((await call('POST', `${feeds}/play`, { data: { minute: 1110 } })).status()).toBe(401);
	const status = await call('GET', feeds, { headers: operator });
	expect(status.status()).toBe(200);
	const codes = await (await call('GET', `${feeds}/aman-codes`, { headers: operator })).json();
	expect(codes).toContainEqual({ amanCode: 'IN07', arivaCode: 'AR-07' });
	const refused = await call('PUT', `${feeds}/clients`, {
		headers: operator,
		data: { aodb: { clientId: 'ic_short', clientSecret: 'ics_leaked-on-purpose', totpSecret: 'not base32' } }
	});
	expect(refused.status()).toBe(400);
	expect(await refused.text()).not.toContain('leaked-on-purpose');
});

test('the emulated AODB pushes the demo schedule to Ariva as AIDX with its own client', async () => {
	const created = await call('POST', `${hosts.main}/api/v1/admin/integration-clients`, {
		token: admin,
		// The simulator calls Ariva directly (no X-Forwarded-For) on localhost, which resolves to ::1 where IPv6 is up (CI).
		data: {
			name: 'E2E emulated AODB',
			kind: 'Aodb',
			scopes: ['flights:write'],
			siteCodes: ['DMO'],
			allowedNetworks: ['127.0.0.0/8', '::1/128', '10.0.0.0/8']
		}
	});
	expect(created.status(), await created.text()).toBe(201);
	const credentials = await created.json();
	// The seed and the secret are scanned for in the host logs at teardown.
	fs.appendFileSync(integrationSeedsFile, credentials.totpSecret + '\n' + credentials.clientSecret + '\n');

	const loaded = await call('PUT', `${feeds}/clients`, {
		headers: operator,
		data: { aodb: { clientId: credentials.client.clientId, clientSecret: credentials.clientSecret, totpSecret: credentials.totpSecret, perRequestTotp: false } }
	});
	expect(loaded.status(), await loaded.text()).toBe(200);
	expect(await loaded.text()).not.toContain(credentials.clientSecret);

	const played = await call('POST', `${feeds}/play`, { headers: operator, data: { minute: 1110 } });
	expect(played.status(), await played.text()).toBe(200);
	const aodb = (await played.json()).aodb;
	expect(aodb, JSON.stringify(aodb)).toMatchObject({ aidxConfigured: true, failures: 0, lastStatus: 200 });
	expect(aodb.legsPushed).toBeGreaterThan(50);

	// The legs are the client's own feed. (The demo clock may be playing too, in sensor-emulator.spec.ts; the AODB then also
	// pushes those minutes, so the count of legs pushed can exceed the legs a single minute holds.)
	const feed = 'api-' + String(credentials.client.clientId).slice(3).toLowerCase();
	const rows = await query<{ n: string }>(`SELECT count(*) AS n FROM flight_leg WHERE site_code = 'DMO' AND lower(feed) = $1`, [feed]);
	expect(Number(rows[0].n), 'the demo schedule is in Ariva as the client feed').toBeGreaterThan(50);
	const calls = await query<{ status: number }>(`SELECT status FROM integration_call WHERE client_id = $1 AND route LIKE '%aodb/aidx'`, [
		credentials.client.clientId
	]);
	expect(calls.length).toBeGreaterThan(0);
	expect(calls.every((c) => c.status === 200)).toBe(true);
});

test('the emulated AODB serves ACRIS flights only with its key', async () => {
	expect((await call('GET', `${hosts.simulation}/aodb/acris/flights`)).status()).toBe(401);
	expect((await call('GET', `${hosts.simulation}/aodb/acris/flights`, { headers: { 'X-Api-Key': 'guess' } })).status()).toBe(401);
	const flights = await call('GET', `${hosts.simulation}/aodb/acris/flights`, { headers: { 'X-Api-Key': acrisKey } });
	expect(flights.status()).toBe(200);
	const body = await flights.json();
	expect(body.length).toBeGreaterThan(50);
	expect(body[0]).toHaveProperty('flightNumber.airlineCode');
	const unchanged = await call('GET', `${hosts.simulation}/aodb/acris/flights`, {
		headers: { 'X-Api-Key': acrisKey, 'If-Modified-Since': flights.headers()['last-modified'] }
	});
	expect([200, 304]).toContain(unchanged.status()); // 200 when the demo clock moved on in between
});

test('the mock AMAN exchanges a TOTP code once and serves its feed to a current code', async () => {
	const auth = (code: string, secret = amanSecret) =>
		call('POST', `${hosts.simulation}/aman/api/v1/auth`, { data: { clientId: 'e2e-aman-connector', clientSecret: secret, totpCode: code } });
	const wrong = await auth(totpCode(amanSeed), 'not-the-secret');
	expect(wrong.status()).toBe(401);
	expect(await wrong.text()).toBe('{"error":"invalid_client"}');

	// A step that was not used yet: the next one, still within the window.
	const code = totpCode(amanSeed, 1);
	const issued = await auth(code);
	expect(issued.status(), await issued.text()).toBe(200);
	const replay = await auth(code);
	expect(replay.status(), 'the same step twice').toBe(401);
	const token = (await issued.json()).accessToken as string;

	expect((await call('POST', `${feeds}/play`, { headers: operator, data: { minute: 1111 } })).status()).toBe(200);
	const noCode = await call('GET', `${hosts.simulation}/aman/api/v1/feed/desk-interval-stats`, { token });
	expect(noCode.status()).toBe(401);
	const page = await call('GET', `${hosts.simulation}/aman/api/v1/feed/desk-interval-stats?limit=10`, {
		token,
		headers: { 'X-TOTP-Code': totpCode(amanSeed) }
	});
	expect(page.status(), await page.text()).toBe(200);
	const envelope = await page.json();
	expect(envelope.hasErrors).toBe(false);
	expect(envelope.data.items.length).toBe(10);
	expect(envelope.data.items[0]).toMatchObject({ siteCode: 'DMO', intervalSeconds: 60 });
	expect(JSON.stringify(envelope)).not.toMatch(/officer|passport|nationality/i);
});
