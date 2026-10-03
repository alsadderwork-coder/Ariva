import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import pg from 'pg';
import { accounts, call, databaseAvailable, integrationSeedsFile, login, signIn, totpCode } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-042: integration clients. An administrator with a recent second factor registers clients (secret and TOTP seed
// shown once) bound to DMO and to the 10.0.0.0/8 network the suite forwards from. A client exchanges its client id,
// secret and current TOTP code for a token at POST /api/v1/auth (AMAN shape); every failure is the same 401
// invalid_client: wrong secret, wrong code, a code used twice, an address outside its networks, a disabled or locked
// client. A lock stops the exchange only; disabling, rotating or changing a client stops its tokens (token version). The token reaches only Integration API endpoints of its scopes and sites (403 otherwise); a user token there
// and the integration token on a user endpoint both answer 401. A client whose policy needs a code on every call gets
// 401 without one. Calls are recorded with their payload's SHA-256.

test.skip(!databaseAvailable, 'integration clients need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const adminApi = `${hosts.main}/api/v1/admin/integration-clients`;
const authUrl = `${hosts.integration}/api/v1/auth`;
const check = (site: string, family = 'flights') => `${hosts.integration}/api/v1/integration/sites/${site}/${family}/check`;
const inside = () => `10.${1 + Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250)}.${1 + Math.floor(Math.random() * 250)}`;
const invalidClient = { error: 'invalid_client' };

type Credentials = { client: { id: string; clientId: string }; clientSecret: string; totpSecret: string; totpUri: string };

let admin: string;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

async function register(name: string, overrides: Record<string, unknown> = {}): Promise<Credentials> {
	const created = await call('POST', adminApi, {
		token: admin,
		data: { name, kind: 'Aodb', scopes: ['flights:write'], siteCodes: ['DMO'], allowedNetworks: ['10.0.0.0/8'], ...overrides }
	});
	expect(created.status(), await created.text()).toBe(201);
	expect(created.headers()['cache-control']).toContain('no-store');
	const credentials: Credentials = await created.json();
	// The log scan (global-teardown.ts) looks for every seed shown in the run; secrets it finds by their ics_ shape.
	fs.appendFileSync(integrationSeedsFile, credentials.totpSecret + '\n');
	return credentials;
}

const exchange = (clientId: string, clientSecret: string, totpCode: string, address = inside()) =>
	call('POST', authUrl, { data: { clientId, clientSecret, totpCode }, address });

async function tokenOf(c: Credentials, offset = 0): Promise<string> {
	const answer = await exchange(c.client.clientId, c.clientSecret, totpCode(c.totpSecret, offset));
	expect(answer.status(), await answer.text()).toBe(200);
	const body = await answer.json();
	expect(Object.keys(body).sort()).toEqual(['accessToken', 'expiresAt', 'sessionId']);
	return body.accessToken;
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().integrationAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;
});

test('an administrator registers a client and sees its secret and seed once; a password-only session cannot', async () => {
	const c = await register('E2E AODB register');
	expect(c.client.clientId).toMatch(/^ic_[a-z2-7]{26}$/);
	expect(c.clientSecret).toMatch(/^ics_[A-Za-z0-9_-]{43}$/);
	expect(c.totpUri).toContain(`secret=${c.totpSecret}`);

	const read = await call('GET', `${adminApi}/${c.client.id}`, { token: admin });
	expect(read.status()).toBe(200);
	const text = await read.text();
	expect(text).not.toContain(c.clientSecret);
	expect(text).not.toContain(c.totpSecret);

	const passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;
	const refused = await call('POST', adminApi, { token: passwordOnly, data: { name: 'x', kind: 'Aodb', scopes: ['flights:write'], siteCodes: ['DMO'] } });
	expect(refused.status(), 'registering a client is critical: a second factor in the last 15 minutes').toBe(401);
	for (const [why, body] of [
		['unknown scope', { scopes: ['flights:write', 'admin:all'] }],
		['no site', { siteCodes: [] }],
		['host bits', { allowedNetworks: ['10.0.0.1/8'] }],
		['unknown kind', { kind: 'aodb' }]
	] as const) {
		const data: Record<string, unknown> = Object.assign({ name: 'E2E bad', kind: 'Aodb', scopes: ['flights:write'], siteCodes: ['DMO'] }, body);
		const bad = await call('POST', adminApi, { token: admin, data });
		expect(bad.status(), why).toBe(400);
		expectNoLeak(await bad.text(), why);
	}
});

test('the exchange gives a token for the right secret and a fresh code, once per code, and the same 401 for everything else', async () => {
	const c = await register('E2E AODB exchange');
	const id = c.client.clientId;
	// One code for the first exchange and the replay, so a step boundary between them cannot hide the replay.
	const code = totpCode(c.totpSecret);
	expect((await exchange(id, c.clientSecret, code)).status()).toBe(200);

	const wrongSecret = c.clientSecret.slice(0, -1) + (c.clientSecret.endsWith('A') ? 'B' : 'A');
	for (const [why, answer] of [
		['the same code twice', await exchange(id, c.clientSecret, code)],
		['wrong secret', await exchange(id, wrongSecret, totpCode(c.totpSecret, 1))],
		['wrong code', await exchange(id, c.clientSecret, code === '000000' ? '000001' : '000000')],
		['unknown client', await exchange('ic_' + 'a'.repeat(26), c.clientSecret, code)],
		['injection as client id', await exchange("' OR 1=1 --", c.clientSecret, code)],
		['outside the networks', await exchange(id, c.clientSecret, totpCode(c.totpSecret, 1), '192.0.2.10')],
		['no body', await call('POST', authUrl, { data: {}, address: inside() })]
	] as const) {
		expect(answer.status(), why).toBe(401);
		expect(await answer.json(), why).toEqual(invalidClient);
		expect(answer.headers()['cache-control'], why).toContain('no-store');
	}

	// A later step's code still works: the failures above were within the lockout threshold.
	await tokenOf(c, 1);
});

test('a token reaches only its scopes and sites, and neither kind of token works on the other side', async () => {
	const c = await register('E2E AODB scope');
	const token = await tokenOf(c);
	const caller = await call('GET', check('DMO'), { token, address: inside() });
	expect(caller.status(), await caller.text()).toBe(200);
	expect(await caller.json()).toMatchObject({ clientId: c.client.clientId, scopes: ['flights:write'], siteCodes: ['DMO'] });

	expect((await call('GET', check('DMO', 'immigration'), { token, address: inside() })).status(), 'a scope it does not hold').toBe(403);
	expect((await call('GET', check('E2E1'), { token, address: inside() })).status(), 'a site it is not bound to').toBe(403);
	expect((await call('GET', check('DMO'), { token, address: '192.0.2.10' })).status(), 'from outside its networks').toBe(401);
	expect((await call('GET', check('DMO'), { address: inside() })).status(), 'no token').toBe(401);

	const user = (await signIn(accounts().dmoBorder)).accessToken;
	expect((await call('GET', check('DMO'), { token: user, address: inside() })).status(), 'a user token on the Integration API').toBe(401);
	expect((await call('GET', `${hosts.main}/api/v1/sites`, { token })).status(), 'an integration token on a user endpoint').toBe(401);
	expect((await call('GET', `${hosts.integration}/api/v1/integration/sites/DMO/flights/check`, { token: c.clientSecret, address: inside() })).status(),
		'a secret is not a token').toBe(401);

	// Calls are recorded, the refused ones too.
	const db = database();
	await db.connect();
	try {
		const rows = await db.query(`SELECT status, site_code, scope FROM integration_call WHERE client_id = $1 ORDER BY at_utc`, [c.client.clientId]);
		expect(rows.rows.map((r) => r.status), 'the outside call was refused at authentication and is recorded too').toEqual(expect.arrayContaining([200, 403, 401]));
		expect(rows.rows.every((r) => r.scope === 'flights:write' || r.scope === 'immigration:write')).toBe(true);
	} finally {
		await db.end();
	}
});

test('a client whose policy needs a code on every call gets 401 without one', async () => {
	const c = await register('E2E immigration', { kind: 'Immigration', scopes: ['immigration:write'] });
	const token = await tokenOf(c);
	expect((await call('GET', check('DMO', 'immigration'), { token, address: inside() })).status()).toBe(401);
	expect((await call('GET', check('DMO', 'immigration'), { token, address: inside(), headers: { 'X-TOTP-Code': '12345' } })).status()).toBe(401);
	const ok = await call('GET', check('DMO', 'immigration'), { token, address: inside(), headers: { 'X-TOTP-Code': totpCode(c.totpSecret) } });
	expect(ok.status(), await ok.text()).toBe(200);
	expect(await ok.json()).toMatchObject({ requireTotpPerRequest: true });
});

test('ten failures lock the exchange until an administrator unlocks it; disabling or rotating stops its tokens at once', async () => {
	const c = await register('E2E AODB lockout');
	const id = c.client.clientId;
	// A token from the previous step, taken before the lock: a lock stops new tokens, not the ones already held.
	const before = await tokenOf(c, -1);
	for (let i = 0; i < 10; i++) expect((await exchange(id, 'ics_' + 'x'.repeat(43), totpCode(c.totpSecret))).status()).toBe(401);
	expect((await exchange(id, c.clientSecret, totpCode(c.totpSecret))).status(), 'locked, even with the right credentials').toBe(401);
	const locked = await (await call('GET', `${adminApi}/${c.client.id}`, { token: admin })).json();
	expect(locked, 'the count starts again at the lock').toMatchObject({ locked: true, failedAttempts: 0 });
	expect((await call('GET', check('DMO'), { token: before, address: inside() })).status(), 'a token held before the lock').toBe(200);

	const unlocked = await call('POST', `${adminApi}/${c.client.id}/unlock`, { token: admin });
	expect(unlocked.status(), await unlocked.text()).toBe(200);
	const token = await tokenOf(c);
	expect((await call('GET', check('DMO'), { token, address: inside() })).status()).toBe(200);

	// Every token carries the client's token version; a rotation moves it on, so the tokens before it stop at once.
	const rotated = await call('POST', `${adminApi}/${c.client.id}/secret`, { token: admin });
	expect(rotated.status()).toBe(200);
	const fresh = await rotated.json();
	expect(fresh.clientSecret).not.toBe(c.clientSecret);
	expect(fresh.totpSecret).toBeNull();
	expect((await call('GET', check('DMO'), { token, address: inside() })).status(), 'a token from before the rotation').toBe(401);
	expect((await exchange(id, c.clientSecret, totpCode(c.totpSecret, 1))).status(), 'the old secret').toBe(401);
	const newToken = await tokenOf({ ...c, clientSecret: fresh.clientSecret }, 1);

	expect((await call('POST', `${adminApi}/${c.client.id}/disable`, { token: admin })).status()).toBe(200);
	expect((await call('GET', check('DMO'), { token: newToken, address: inside() })).status(), 'disabled').toBe(401);
});
