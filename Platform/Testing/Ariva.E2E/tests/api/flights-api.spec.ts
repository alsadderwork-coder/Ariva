import fs from 'node:fs';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, integrationSeedsFile, login, totpCode } from '../support/accounts';
import { expectNoLeak, expectProblemDetails } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-043: the Integration API for any AODB. A client with flights:write and allocations:write for DMO sends flight
// legs, flight events and counter allocations in batches of 1 to 500 items and at most 1 MB, each with an
// Idempotency-Key. Every item is checked on its own (a bad one never stops the others) and answered in order; a retry
// with the same key and body gets the first answer again (Idempotent-Replayed) and applies nothing twice, also when the
// retries arrive at the same time; the same key with another body is 422. Malformed, oversized, unknown-member and
// injection payloads are refused without echoing them and without reaching the database.

test.skip(!databaseAvailable, 'the Integration API needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const adminApi = `${hosts.main}/api/v1/admin/integration-clients`;
const site = (code: string, path: string) => `${hosts.integration}/api/v1/integration/sites/${code}/${path}`;
const inside = () => `10.${1 + Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250)}.${1 + Math.floor(Math.random() * 250)}`;
const run = Date.now().toString(36).toUpperCase();
const key = (n: number) => `E2E-${run}-${n}`;
const at = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();

type Credentials = { client: { id: string; clientId: string }; clientSecret: string; totpSecret: string };

let admin: string;
let aodb: { credentials: Credentials; token: string };
let flightsOnly: { credentials: Credentials; token: string };

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

async function query<T = Record<string, unknown>>(sql: string, values: unknown[] = []): Promise<T[]> {
	const db = database();
	await db.connect();
	try {
		return (await db.query(sql, values)).rows as T[];
	} finally {
		await db.end();
	}
}

async function client(name: string, scopes: string[]): Promise<{ credentials: Credentials; token: string }> {
	const created = await call('POST', adminApi, {
		token: admin,
		data: { name, kind: 'Aodb', scopes, siteCodes: ['DMO'], allowedNetworks: ['10.0.0.0/8'] }
	});
	expect(created.status(), await created.text()).toBe(201);
	const credentials: Credentials = await created.json();
	fs.appendFileSync(integrationSeedsFile, credentials.totpSecret + '\n');
	const exchanged = await call('POST', `${hosts.integration}/api/v1/auth`, {
		data: { clientId: credentials.client.clientId, clientSecret: credentials.clientSecret, totpCode: totpCode(credentials.totpSecret) },
		address: inside()
	});
	expect(exchanged.status(), await exchanged.text()).toBe(200);
	return { credentials, token: (await exchanged.json()).accessToken };
}

const send = (path: string, body: unknown, idempotencyKey: string | null, token = aodb.token, extra: Record<string, string> = {}, code = 'DMO') =>
	call('POST', site(code, path), {
		token,
		address: inside(),
		raw: typeof body === 'string' ? body : JSON.stringify(body),
		headers: { 'Content-Type': 'application/json', ...(idempotencyKey === null ? {} : { 'Idempotency-Key': idempotencyKey }), ...extra }
	});

const leg = (n: number, direction = 'Arrival', more: Record<string, unknown> = {}) => ({
	flightKey: `E2E-${run}-${n}`,
	carrier: 'RJ',
	number: String(100 + n),
	direction,
	scheduledUtc: at(120 + n),
	origin: direction === 'Arrival' ? 'AMM' : 'DMO',
	destination: direction === 'Arrival' ? 'DMO' : 'CAI',
	...more
});

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().feedAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;
	aodb = await client('E2E AODB flights', ['flights:write', 'allocations:write']);
	flightsOnly = await client('E2E AODB flights only', ['flights:write']);
});

test('a batch of legs is applied item by item, and a retry with its key gets the same answer without applying it again', async () => {
	const body = { messageTimeUtc: at(-1), items: [leg(1), leg(2, 'Departure'), leg(3)] };
	const first = await send('flights/batch', body, key(1));
	expect(first.status(), await first.text()).toBe(200);
	expect(first.headers()['idempotent-replayed']).toBeUndefined();
	const answer = await first.json();
	expect(answer).toMatchObject({ received: 3, applied: 3, unchanged: 0, refused: 0 });
	expect(answer.items.map((i: { index: number }) => i.index)).toEqual([0, 1, 2]);

	const again = await send('flights/batch', body, key(1));
	expect(again.status()).toBe(200);
	expect(again.headers()['idempotent-replayed']).toBe('true');
	expect(await again.text()).toBe(await first.text());

	const reused = await send('flights/batch', { ...body, items: [leg(1)] }, key(1));
	await expectProblemDetails(reused, 422);

	// Keys are per client: another client's batch under the same key is its own, never the first client's answer.
	const other = await send('flights/batch', body, key(1), flightsOnly.token);
	expect(other.status(), await other.text()).toBe(200);
	expect(other.headers()['idempotent-replayed']).toBeUndefined();
	expect(await other.json(), 'applied as its own feed: nothing newer than what it sent itself').toMatchObject({ received: 3, refused: 0 });

	const sameBodyNewKey = await send('flights/batch', body, key(2));
	expect(await sameBodyNewKey.json(), 'a new key applies again, and nothing is newer').toMatchObject({ applied: 0, unchanged: 3 });

	const feed = 'api-' + aodb.credentials.client.clientId.slice(3);
	const rows = await query<{ n: string }>(`SELECT count(*) AS n FROM flight_leg WHERE site_code = 'DMO' AND flight_key LIKE $1`, [`E2E-${run}-%`]);
	expect(Number(rows[0].n)).toBe(3);
	const fresh = await query(`SELECT feed FROM feed_freshness WHERE site_code = 'DMO' AND feed = $1`, [feed]);
	expect(fresh, 'the client is its own feed').toHaveLength(1);
});

test('retries sent at the same time apply the batch once and all get its answer', async () => {
	// Four: the E2E host handles two batches at once and queues two (playwright.config.ts).
	const body = { items: [leg(10), leg(11)] };
	const answers = await Promise.all(Array.from({ length: 4 }, () => send('flights/batch', body, key(10))));
	const texts = await Promise.all(answers.map((a) => a.text()));
	expect(answers.map((a) => a.status())).toEqual(Array(4).fill(200));
	expect(new Set(texts).size, 'one answer for all').toBe(1);
	expect(answers.filter((a) => a.headers()['idempotent-replayed'] !== 'true')).toHaveLength(1);
	expect(JSON.parse(texts[0])).toMatchObject({ applied: 2 });
	const events = await query<{ n: string }>(`SELECT count(*) AS n FROM flight_leg WHERE flight_key IN ($1, $2)`, [`E2E-${run}-10`, `E2E-${run}-11`]);
	expect(Number(events[0].n)).toBe(2);
});

test('events and counter allocations reach known legs; allocations need their own scope', async () => {
	await send('flights/batch', { items: [leg(20), leg(21, 'Departure')] }, key(20));

	const events = await send('flights/events', {
		items: [
			{ flightKey: `E2E-${run}-20`, eventType: 'Estimated', timeUtc: at(125) },
			{ flightKey: `E2E-${run}-404`, eventType: 'Landed', timeUtc: at(1) },
			{ flightKey: `E2E-${run}-20`, eventType: 'landed', timeUtc: at(1) }
		]
	}, key(21));
	expect(events.status(), await events.text()).toBe(200);
	const eventAnswer = await events.json();
	expect(eventAnswer).toMatchObject({ received: 3, applied: 1, refused: 2 });
	expect(eventAnswer.items[1].errors.length, 'an unknown leg').toBeGreaterThan(0);
	expect(eventAnswer.items[2].errors.length, 'event names are exact').toBeGreaterThan(0);

	const allocation = { items: [{ flightKey: `E2E-${run}-21`, checkpointCode: 'CI', counterCodes: ['Z99', 'Z98'], openUtc: at(30), closeUtc: at(150) }] };
	const allocated = await send('allocations/batch', allocation, key(22));
	expect(allocated.status(), await allocated.text()).toBe(200);
	const allocationAnswer = await allocated.json();
	expect(allocationAnswer.items[0].errors).toEqual([]);
	expect(allocationAnswer.items[0].warnings.length, 'unmapped counters are kept apart and reported').toBeGreaterThan(0);

	const refused = await send('allocations/batch', allocation, key(23), flightsOnly.token);
	expect(refused.status(), 'allocations:write is a scope of its own').toBe(403);
	expect((await send('flights/batch', { items: [leg(24)] }, key(24), aodb.token, {}, 'E2E1')).status(), 'a site the client is not bound to').toBe(403);
	expect((await call('POST', site('DMO', 'flights/batch'), { raw: JSON.stringify({ items: [leg(25)] }), headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key(25) }, address: inside() })).status(), 'no token').toBe(401);
});

test('malformed, oversized and unknown-member bodies are refused as a whole without echoing them', async () => {
	const good = { items: [leg(30)] };
	// One at a time: the E2E host takes two batches at once and queues two.
	const cases: [string, () => ReturnType<typeof call>, number][] = [
		['no Idempotency-Key', () => send('flights/batch', good, null), 400],
		['a short Idempotency-Key', () => send('flights/batch', good, 'abc'), 400],
		['an Idempotency-Key with SQL', () => send('flights/batch', good, "k' OR '1'='1"), 400],
		['not JSON', () => send('flights/batch', '<flights/>', key(31), aodb.token, { 'Content-Type': 'application/xml' }), 415],
		['broken JSON', () => send('flights/batch', '{"items":[{"flightKey":', key(32)), 400],
		['an unknown member', () => send('flights/batch', { items: [{ ...leg(33), adminOverride: true }] }, key(33)), 400],
		['a repeated member', () => send('flights/batch', `{"items":[${JSON.stringify(leg(34))}],"items":[]}`, key(34)), 400],
		['a number as text', () => send('flights/batch', { items: [{ ...leg(35), seats: '180' }] }, key(35)), 400],
		['no items', () => send('flights/batch', { items: [] }, key(36)), 400],
		['501 items', () => send('flights/batch', { items: Array.from({ length: 501 }, (_, i) => leg(1000 + i)) }, key(37)), 400],
		['a message time with an offset', () => send('flights/batch', { messageTimeUtc: '2026-10-03T10:00:00+03:00', items: [leg(38)] }, key(38)), 400],
		['a message time in the future', () => send('flights/batch', { messageTimeUtc: at(60), items: [leg(39)] }, key(39)), 400],
		['more than 1 MB', () => send('flights/batch', { items: [leg(40, 'Arrival', { gate: 'x'.repeat(1_100_000) })] }, key(40)), 413]
	];
	for (const [why, sending, status] of cases) {
		const answer = await sending();
		const text = await answer.text();
		expect(answer.status(), `${why}: ${text.slice(0, 300)}`).toBe(status);
		expect(answer.headers()['content-type'], why).toContain('application/problem+json');
		expectNoLeak(text, why);
		for (const echoed of ['adminOverride', "OR '1'='1", '<flights/>']) expect(text, why).not.toContain(echoed);
	}

	const rows = await query<{ n: string }>(`SELECT count(*) AS n FROM flight_leg WHERE flight_key LIKE $1 AND flight_key <> $2`, [`E2E-${run}-3%`, `E2E-${run}-3`]);
	expect(Number(rows[0].n), 'nothing of a refused batch was applied').toBe(0);
	const recorded = await query<{ status: number }>(`SELECT status FROM integration_call WHERE client_id = $1 AND status = 413`, [aodb.credentials.client.clientId]);
	expect(recorded.length, 'an oversized call is recorded too').toBeGreaterThan(0);
});

test('injection payloads in items are refused item by item and never reach the database', async () => {
	const hostile = [
		{ ...leg(50), flightKey: "E2E' OR 1=1 --" },
		{ ...leg(51), carrier: '<script>alert(1)</script>' },
		{ ...leg(52), number: '1; DROP TABLE flight_leg' },
		{ ...leg(53), origin: '../../etc/passwd' },
		{ ...leg(54), gate: "'); DELETE FROM flight_leg; --" },
		{ ...leg(55), direction: 'Arrival' + String.fromCharCode(0) },
		{ ...leg(56), codeshares: ['XR1214', '${jndi:ldap://x}'] },
		{ ...leg(57), status: '__proto__' },
		leg(58)
	];
	const answer = await send('flights/batch', { items: hostile }, key(50));
	const text = await answer.text();
	expect(answer.status(), text).toBe(200);
	const body = JSON.parse(text);
	expect(body).toMatchObject({ received: hostile.length, applied: 1, refused: hostile.length - 1 });
	expect(body.items[hostile.length - 1]).toMatchObject({ applied: true, errors: [] });
	for (const echoed of ['<script>', 'DROP TABLE', 'DELETE FROM', 'jndi', 'passwd']) expect(text).not.toContain(echoed);
	expectNoLeak(text, 'per-item results');

	const legs = await query<{ n: string }>(`SELECT count(*) AS n FROM flight_leg WHERE site_code = 'DMO'`);
	expect(Number(legs[0].n), 'the table is still there').toBeGreaterThan(0);
	const stored = await query<{ flight_key: string }>(`SELECT flight_key FROM flight_leg WHERE flight_key LIKE $1 ORDER BY flight_key`, [`E2E-${run}-5%`]);
	expect(stored.map((r) => r.flight_key)).toEqual([`E2E-${run}-58`]);
});

test('batches beyond those a host handles at once get 429 before their body is read, and apply nothing', async () => {
	const many = 16;
	const bodies = Array.from({ length: many }, (_, r) => ({ items: Array.from({ length: 500 }, (_, i) => ({ ...leg(0), flightKey: `E2E-${run}-C${r}-${i}`, number: String(1 + (i % 999)) })) }));
	const answers = await Promise.all(bodies.map((body, r) => send('flights/batch', body, key(7000 + r))));
	const statuses = answers.map((a) => a.status());
	expect(statuses.every((s) => s === 200 || s === 429), statuses.join(',')).toBe(true);
	expect(statuses.filter((s) => s === 429).length, 'two at once and two waiting on the E2E host').toBeGreaterThan(0);
	for (const [r, answer] of answers.entries()) {
		const rows = await query<{ n: string }>(`SELECT count(*) AS n FROM flight_leg WHERE flight_key LIKE $1`, [`E2E-${run}-C${r}-%`]);
		expect(Number(rows[0].n), `batch ${r}: ${answer.status()}`).toBe(answer.status() === 200 ? 500 : 0);
	}
});

test('a client beyond its batches a minute gets 429 with Retry-After; another client is not held back', async () => {
	const busy = await client('E2E AODB busy', ['flights:write']);
	const statuses: number[] = [];
	let retryAfter: string | undefined;
	for (let i = 0; i < 64; i++) {
		// No Idempotency-Key: cheap 400s that still count against the allowance, which is taken first.
		const answer = await send('flights/batch', { items: [leg(80)] }, null, busy.token);
		statuses.push(answer.status());
		if (answer.status() === 429) retryAfter ??= answer.headers()['retry-after'];
	}
	expect(statuses.slice(0, 60).every((s) => s === 400), statuses.join(',')).toBe(true);
	expect(statuses.slice(60).every((s) => s === 429), statuses.join(',')).toBe(true);
	expect(Number(retryAfter)).toBeGreaterThan(0);
	expect((await send('flights/batch', { items: [leg(81)] }, null, aodb.token)).status(), 'the allowance is per client').toBe(400);
});

test('every batch call is recorded with its payload hash', async () => {
	const rows = await query<{ route: string; status: number; payload_sha256: string | null }>(
		`SELECT route, status, payload_sha256 FROM integration_call WHERE client_id = $1 AND route LIKE '%flights/batch' ORDER BY at_utc`,
		[aodb.credentials.client.clientId]
	);
	expect(rows.length).toBeGreaterThan(5);
	expect(rows.filter((r) => r.status === 200).every((r) => /^[0-9a-f]{64}$/.test(r.payload_sha256 ?? '')), 'accepted calls carry their SHA-256').toBe(true);
});
