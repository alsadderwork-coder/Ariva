import fs from 'node:fs';
import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, integrationSeedsFile, login, totpCode } from '../support/accounts';
import { expectNoLeak, expectProblemDetails } from '../support/api-assertions';
import { hosts, kafkaAvailable } from '../support/hosts';

// ARV-048: the AMAN feed and immigration endpoints with the simulator's AMAN over both transports. Over Kafka, the AMAN
// records of a demo minute reach Ariva.Api.Integration's consumers and are stored as the feed aman-kafka; over REST,
// AMAN's own integration client (immigration:write, X-TOTP-Code on every call) sends each contract as a batch. AMAN's desk
// and e-gate codes are resolved through the demo airport's AMAN desk code mappings. The endpoints read strictly (unknown
// members, numeric enums), enforce one-minute intervals, sums and small-cell suppression per record, keep a record once
// by its source event id, replay a batch under its Idempotency-Key, and refuse a record of another site, a call without
// the code, and a client without the scope.

test.skip(!databaseAvailable, 'the feed is stored in the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true) with the DMO demo seed');
test.describe.configure({ mode: 'serial' });

const feeds = `${hosts.simulation}/api/v1/simulation/feeds`;
const operator = {
	Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}`
};
const immigration = (contract: string, site = 'DMO') => `${hosts.integration}/api/v1/integration/sites/${site}/immigration/${contract}`;
const inside = () => `10.${1 + Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250)}.${1 + Math.floor(Math.random() * 250)}`;
const run = crypto.randomBytes(4).toString('hex');

type Credentials = {
	client: { clientId: string };
	clientSecret: string;
	totpSecret: string;
};
let admin = '';
let aman: { credentials: Credentials; token: string };
let flightsOnly: { credentials: Credentials; token: string };
// The simulator's AMAN client: never exchanged here, so its TOTP steps are the simulator's own (Ariva's replay guard).
let emulated: Credentials;

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

async function register(name: string, kind: string, scopes: string[], perRequestTotp: boolean): Promise<Credentials> {
	const created = await call('POST', `${hosts.main}/api/v1/admin/integration-clients`, {
		token: admin,
		data: {
			name,
			kind,
			scopes,
			siteCodes: ['DMO'],
			// The simulator calls Ariva directly on localhost: 127.0.0.1, or ::1 where IPv6 is up (CI).
			allowedNetworks: ['127.0.0.0/8', '::1/128', '10.0.0.0/8'],
			requireTotpPerRequest: perRequestTotp
		}
	});
	expect(created.status(), await created.text()).toBe(201);
	const credentials: Credentials = await created.json();
	fs.appendFileSync(integrationSeedsFile, credentials.totpSecret + '\n' + credentials.clientSecret + '\n');
	return credentials;
}

async function client(name: string, kind: string, scopes: string[], perRequestTotp: boolean) {
	const credentials = await register(name, kind, scopes, perRequestTotp);
	const exchanged = await call('POST', `${hosts.integration}/api/v1/auth`, {
		data: {
			clientId: credentials.client.clientId,
			clientSecret: credentials.clientSecret,
			totpCode: totpCode(credentials.totpSecret)
		},
		address: inside()
	});
	expect(exchanged.status(), await exchanged.text()).toBe(200);
	return { credentials, token: (await exchanged.json()).accessToken as string };
}

const send = (contract: string, body: unknown, key: string, options: { site?: string; code?: boolean; token?: string; raw?: string } = {}) =>
	call('POST', immigration(contract, options.site), {
		token: options.token ?? aman.token,
		address: inside(),
		raw: options.raw ?? JSON.stringify(body),
		headers: {
			'Content-Type': 'application/json',
			'Idempotency-Key': key,
			...(options.code === false ? {} : { 'X-TOTP-Code': totpCode(aman.credentials.totpSecret) })
		}
	});

const minuteStart = () => {
	const now = new Date();
	now.setUTCSeconds(0, 0);
	return now.toISOString().replace('.000Z', 'Z');
};

async function until<T>(read: () => Promise<T>, done: (value: T) => boolean, seconds = 60): Promise<T> {
	const end = Date.now() + seconds * 1000;
	let value = await read();
	while (!done(value) && Date.now() < end) {
		await new Promise((resolve) => setTimeout(resolve, 500));
		value = await read();
	}
	return value;
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().amanFeedAdmin;
	const signedIn = await login(userName, password, undefined, undefined, {
		code: totpCode(totpSecret!)
	});
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;
	aman = await client(`E2E AMAN ${run}`, 'Immigration', ['immigration:write'], true);
	flightsOnly = await client(`E2E AODB only ${run}`, 'Aodb', ['flights:write'], false);
	emulated = await register(`E2E emulated AMAN ${run}`, 'Immigration', ['immigration:write'], true);
});

test('the AMAN emulator feeds Ariva over Kafka, codes resolved through the AMAN desk code mappings', async () => {
	test.skip(!kafkaAvailable, 'needs ARIVA_E2E_KAFKA_BOOTSTRAP');
	const played = await call('POST', `${feeds}/play`, {
		headers: operator,
		data: { minute: 1100 }
	});
	expect(played.status(), await played.text()).toBe(200);
	const kafka = (await played.json()).aman.transports.find((t: { transport: string }) => t.transport === 'kafka');
	expect(kafka, JSON.stringify(kafka)).toMatchObject({
		configured: true,
		failures: 0
	});

	const rows = await until(
		() => query<{ n: string }>(`SELECT count(*) AS n FROM border_desk_interval WHERE feed = 'aman-kafka' AND source_event_id LIKE 'aman-%-dk-%-1100'`),
		(r) => Number(r[0].n) > 10
	);
	expect(Number(rows[0].n), 'the minute 18:20 desk intervals arrived over Kafka').toBeGreaterThan(10);
	const mapped = await query<{ code: string; kind: string }>(
		`SELECT d.code, d.kind FROM border_desk_interval b JOIN desk d ON d.id = b.desk_id WHERE b.feed = 'aman-kafka' AND b.desk_code = 'IN09' LIMIT 1`
	);
	expect(mapped[0], 'AMAN IN09 is Ariva AR-09').toMatchObject({
		code: 'AR-09',
		kind: 'Desk'
	});
	const sessions = await until(
		() => query<{ n: string }>(`SELECT count(*) AS n FROM border_desk_session WHERE feed = 'aman-kafka'`),
		(r) => Number(r[0].n) > 0
	);
	expect(Number(sessions[0].n)).toBeGreaterThan(0);
	const gates = await until(
		() => query<{ n: string }>(`SELECT count(*) AS n FROM border_egate_interval WHERE feed = 'aman-kafka' AND desk_id IS NOT NULL`),
		(r) => Number(r[0].n) > 0
	);
	expect(Number(gates[0].n)).toBeGreaterThan(0);
});

test('AMAN and another immigration system feed Ariva over REST with their own clients', async () => {
	// AMAN (both sides, X-TOTP-Code on every call) also publishes on Kafka, so its REST records may find the Kafka copy
	// first and be unchanged; the mock immigration system (departures, REST only, its own source event ids) shows the
	// records stored as a client feed.
	const other = await register(`E2E immigration system ${run}`, 'Immigration', ['immigration:write'], false);
	const loaded = await call('PUT', `${feeds}/clients`, {
		headers: operator,
		data: {
			aman: { clientId: emulated.client.clientId, clientSecret: emulated.clientSecret, totpSecret: emulated.totpSecret, perRequestTotp: true },
			immigration: { clientId: other.client.clientId, clientSecret: other.clientSecret, totpSecret: other.totpSecret, perRequestTotp: false }
		}
	});
	expect(loaded.status(), await loaded.text()).toBe(200);

	for (const minute of [1120, 1121]) {
		const played = await call('POST', `${feeds}/play`, { headers: operator, data: { minute } });
		expect(played.status(), await played.text()).toBe(200);
		const status = await played.json();
		for (const system of [status.aman, status.immigration]) {
			const rest = system.transports.find((t: { transport: string }) => t.transport === 'rest');
			expect(rest, JSON.stringify(rest)).toMatchObject({ configured: true, failures: 0, lastStatus: 200 });
		}
	}

	const feed = 'api-' + other.client.clientId.slice(3);
	const stored = await query<{ n: string; mapped: string }>(
		`SELECT count(*) AS n, count(desk_id) AS mapped FROM border_desk_interval WHERE feed = $1 AND desk_code LIKE 'OUT%'`,
		[feed]
	);
	expect(Number(stored[0].n), 'departure desk intervals stored as the immigration system feed').toBeGreaterThan(10);
	expect(stored[0].mapped, 'OUT codes are mapped to the departure desks').toBe(stored[0].n);
	for (const clientId of [emulated.client.clientId, other.client.clientId]) {
		const calls = await query<{ route: string; status: number }>(
			`SELECT route, status FROM integration_call WHERE client_id = $1 AND route LIKE '%immigration%'`,
			[clientId]
		);
		expect(calls.length).toBeGreaterThanOrEqual(4);
		expect(
			calls.every((c) => c.status === 200),
			JSON.stringify(calls)
		).toBe(true);
	}
});

test('the immigration endpoints check every record and keep each once', async () => {
	const at = minuteStart();
	const desk = (id: string, extra: Record<string, unknown> = {}) => ({
		siteCode: 'DMO',
		deskCode: 'IN07',
		intervalStartUtc: at,
		intervalSeconds: 60,
		transactionsProcessed: 2,
		documentsProcessed: 3,
		meanServiceSeconds: 41,
		p90ServiceSeconds: 60,
		meanCycleSeconds: 30,
		laneCategory: 'VIS',
		sourceEventId: `e2e-${run}-${id}`,
		...extra
	});
	const body = {
		items: [
			desk('ok'),
			desk('thirty', { intervalSeconds: 30 }),
			desk('sums', { transactionsProcessed: 5 }),
			desk('other-site', { siteCode: 'BEY' }),
			desk('unmapped', { deskCode: 'NOSUCH9' }),
			desk('script', { deskCode: '<script>' })
		]
	};
	const answer = await send('desk-interval-stats', body, `e2e-${run}-desks`);
	expect(answer.status(), await answer.text()).toBe(200);
	const result = await answer.json();
	expect(result).toMatchObject({ received: 6, applied: 2, refused: 4 });
	expect(result.items[1].errors[0]).toContain('intervalSeconds is 60');
	expect(result.items[2].errors[0]).toContain('sums do not add up');
	expect(result.items[3].errors[0]).toBe('siteCode is the site of the call.');
	expect(result.items[4].warnings[0]).toContain('no AMAN desk code mapping');
	// A code outside the mapping shape is refused and never echoed, as the key or anywhere in the answer.
	expect(result.items[5].key).toBeNull();
	expect(JSON.stringify(result)).not.toContain('script');

	// The same key and body: the stored answer. The same records under a new key: already received.
	const replay = await send('desk-interval-stats', body, `e2e-${run}-desks`);
	expect(replay.headers()['idempotent-replayed']).toBe('true');
	expect(await replay.json()).toEqual(result);
	const again = await (await send('desk-interval-stats', body, `e2e-${run}-desks-again`)).json();
	expect(again).toMatchObject({ applied: 0, unchanged: 2, refused: 4 });

	const gate = (rejects: Record<string, number>, rejected: number, id: string) => ({
		siteCode: 'DMO',
		gateCode: 'EGIN2',
		intervalStartUtc: at,
		intervalSeconds: 60,
		attempts: 10,
		accepted: 10 - rejected,
		rejected,
		rejectsByCategory: rejects,
		meanCycleSeconds: 18,
		sourceEventId: `e2e-${run}-${id}`
	});
	const gates = await (
		await send(
			'egate-interval-stats',
			{
				items: [
					gate({ DocumentRead: 3 }, 3, 'g-ok'),
					gate({ Other: 1, BiometricCapture: 2 }, 3, 'g-small'),
					gate({ DocumentRead: 2147483647, BiometricCapture: 2147483647 }, 3, 'g-overflow')
				]
			},
			`e2e-${run}-gates`
		)
	).json();
	expect(gates.items[0].applied).toBe(true);
	expect(gates.items[1].errors[0]).toContain('small-cell suppression');
	// Counts that would overflow a sum are refused like any other bad count: 200 with the record refused.
	expect(gates.items[2].errors[0]).toContain('coarse categories');
});

test('the immigration endpoints refuse what is not the contract, a call without the code and a client without the scope', async () => {
	const session = {
		siteCode: 'DMO',
		deskCode: 'IN07',
		state: 'Opened',
		laneCategory: 'VIS',
		occurredAtUtc: minuteStart(),
		sourceEventId: `e2e-${run}-s1`
	};
	for (const [why, raw] of [
		['an unknown member', JSON.stringify({ items: [{ ...session, officerId: 'OFFICER-123' }] })],
		['a numeric enum', JSON.stringify({ items: [{ ...session, state: 1 }] })],
		['an enum in another case', JSON.stringify({ items: [{ ...session, state: 'opened' }] })],
		['a padded enum', JSON.stringify({ items: [{ ...session, state: ' Opened' }] })],
		['comma-joined enum names', JSON.stringify({ items: [{ ...session, state: 'Opened, Closed' }] })],
		['a time without an offset', JSON.stringify({ items: [{ ...session, occurredAtUtc: minuteStart().replace('Z', '') }] })],
		['no items', JSON.stringify({ items: [] })],
		['not JSON', 'items=1']
	] as const) {
		const refused = await send('desk-sessions', null, `e2e-${run}-bad-${why.replace(/\W/g, '')}`, { raw });
		expect(refused.status(), why).toBe(400);
		expectNoLeak(await refused.text(), why);
		expect(await refused.text()).not.toContain('OFFICER-123');
	}

	const noCode = await send('desk-sessions', { items: [session] }, `e2e-${run}-nocode`, { code: false });
	expect(noCode.status(), 'per-request TOTP').toBe(401);
	const scope = await send('desk-sessions', { items: [session] }, `e2e-${run}-scope`, { token: flightsOnly.token });
	await expectProblemDetails(scope, 403);
	const site = await send('desk-sessions', { items: [session] }, `e2e-${run}-site`, { site: 'BEY' });
	expect(site.status(), 'a site the client is not bound to').toBe(403);
	const ok = await send('desk-sessions', { items: [session] }, `e2e-${run}-session`);
	expect(ok.status(), await ok.text()).toBe(200);
	expect((await ok.json()).applied).toBe(1);
});
