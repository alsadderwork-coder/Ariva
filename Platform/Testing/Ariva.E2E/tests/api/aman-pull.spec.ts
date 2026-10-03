import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, login, totpCode } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-050: Ariva pulls AMAN's feed from AMAN's Integration API where AMAN's Kafka is not shared. An administrator
// registers an AmanFeed outbound endpoint for DMO with TOTP client credentials (the mock AMAN of the simulator); the
// Integration host exchanges the client id, secret and a TOTP code for a token, calls each contract's feed with the
// token and an X-TOTP-Code, keeps its position per contract and hands the records to the immigration intake. A wrong
// secret is recorded as a failure without the secret anywhere; other authentication kinds and paths are refused.

test.skip(!databaseAvailable, 'the pull writes to the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const endpoints = `${hosts.main}/api/v1/admin/outbound-endpoints`;
const feeds = `${hosts.simulation}/api/v1/simulation/feeds`;
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };
const secret = process.env.ARIVA_E2E_MOCK_AMAN_PULL_SECRET ?? '';
const seed = process.env.ARIVA_E2E_MOCK_AMAN_PULL_SEED ?? '';
const run = Date.now().toString(36);
// The simulator on the loopback address the hosts allow for lab partners (Integration:Outbound:LabHosts).
const aman = `http://127.0.0.1:${new URL(hosts.simulation).port || '51020'}/aman/api/v1/`;
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

async function until<T>(read: () => Promise<T>, done: (value: T) => boolean, seconds = 90): Promise<T> {
	const end = Date.now() + seconds * 1000;
	let value = await read();
	while (!done(value) && Date.now() < end) {
		await new Promise((resolve) => setTimeout(resolve, 1000));
		value = await read();
	}
	return value;
}

const endpoint = (code: string, clientSecret: string, more: Record<string, unknown> = {}) => ({
	code,
	name: `E2E AMAN pull ${code}`,
	purpose: 'AmanFeed',
	siteCodes: ['DMO'],
	connection: {
		baseUrl: aman,
		allowedNetworks: ['127.0.0.0/8'],
		authKind: 'TotpClientCredentials',
		tokenPath: '/auth',
		clientId: 'e2e-aman-pull',
		totpPerRequest: true,
		pullPath: '/feed/',
		pollSeconds: 30,
		retryCount: 0,
		...more
	},
	secret: { clientSecret, totpSeed: seed }
});

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().amanPullAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;
});

test('an AMAN pull is registered only with TOTP client credentials below a feed path', async () => {
	for (const [why, body] of [
		[
			'an API key',
			{ ...endpoint(`ap-${run}-k`, secret), connection: { ...endpoint('x', secret).connection, authKind: 'ApiKeyHeader', headerName: 'X-Api-Key' } }
		],
		['a path without a slash', endpoint(`ap-${run}-p`, secret, { pullPath: '/feed' })],
		['two sites', { ...endpoint(`ap-${run}-s`, secret), siteCodes: ['DMO', 'E2E1'] }]
	] as const) {
		const refused = await call('POST', endpoints, { token: admin, data: body });
		expect(refused.status(), why).toBe(400);
		expectNoLeak(await refused.text(), why);
		expect(await refused.text()).not.toContain(secret);
	}
});

test('the Integration host pulls every contract from the mock AMAN with a token and a TOTP code per call', async () => {
	const created = await call('POST', endpoints, { token: admin, data: endpoint(`ap-${run}`, secret) });
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id as string;
	expect(await created.text()).not.toContain(secret);
	const wrong = await call('POST', endpoints, { token: admin, data: endpoint(`ap-${run}-w`, 'not-the-secret-of-this-client') });
	expect(wrong.status(), await wrong.text()).toBe(201);

	// The AMAN emulator fills the mock's feed when it plays a minute.
	const played = await call('POST', `${feeds}/play`, { headers: operator, data: { minute: 1110 } });
	expect(played.status(), await played.text()).toBe(200);

	const pulled = await until(
		() =>
			query<{ contract: string; after: string }>(
				`SELECT contract, after_sequence::text AS after FROM aman_pull_cursor WHERE endpoint_id = $1 ORDER BY contract`,
				[id]
			),
		(rows) => rows.some((r) => r.contract === 'desk-interval-stats' && Number(r.after) > 0)
	);
	expect(pulled.map((r) => r.contract)).toContain('desk-interval-stats');
	const status = await until(
		() => query<{ last_status: string; consecutive_failures: number }>(`SELECT last_status, consecutive_failures FROM outbound_endpoint WHERE id = $1`, [id]),
		(rows) => rows[0]?.last_status?.startsWith('Pulled') === true
	);
	expect(status[0], JSON.stringify(status[0])).toMatchObject({ consecutive_failures: 0 });
	expect(status[0].last_status).toMatch(/^Pulled \d+ AMAN records: \d+ applied, \d+ unchanged, 0 refused, 0 unreadable\.$/);

	// The records are in Ariva (from this pull or, the same records, from AMAN's other transports of the emulator).
	const stored = await query<{ n: string }>(
		`SELECT count(*) AS n FROM border_desk_interval WHERE site_code = 'DMO' AND received_utc > now() - interval '10 minutes'`
	);
	expect(Number(stored[0].n)).toBeGreaterThan(0);

	// The wrong secret: refused by AMAN, recorded as a failure, the secret nowhere in Ariva's answers.
	const failed = await until(
		() =>
			query<{ last_status: string; consecutive_failures: number }>(`SELECT last_status, consecutive_failures FROM outbound_endpoint WHERE code = $1`, [
				`ap-${run}-w`
			]),
		(rows) => (rows[0]?.consecutive_failures ?? 0) > 0
	);
	expect(failed[0].last_status).toBe('AMAN answered 401.');
	const listed = await call('GET', endpoints, { token: admin });
	expect(listed.status()).toBe(200);
	const text = await listed.text();
	expect(text).not.toContain(secret);
	expect(text).not.toContain('not-the-secret-of-this-client');
	expect(text).not.toContain(seed);

	// Stop both pulls so later runs and other suites see a quiet mock.
	for (const code of [`ap-${run}`, `ap-${run}-w`]) {
		const row = await query<{ id: string }>(`SELECT id FROM outbound_endpoint WHERE code = $1`, [code]);
		expect((await call('POST', `${endpoints}/${row[0].id}/disable`, { token: admin })).status()).toBe(200);
	}
});
