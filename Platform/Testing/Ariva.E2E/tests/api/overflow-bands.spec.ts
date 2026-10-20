import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';
import { expectStatus } from '../support/expect-status';

// ARV-120: GET api/v1/sites/{siteCode}/overflow-bands (LiveQueue.View, site-scoped) answers, for each queue zone of the site's
// published profile that has overflow bands, each band's latest minute with a reading in overflow_minute within the last
// 6 minutes, the most people it held then, and its state (Occupied, Empty, NotHeard); the zone is Occupied when any band
// is. Ariva.Api.Stream does not run in the E2E run, so the suite plants band minutes as the stream writes them (ARV-115)
// for the demo airport's departures visitors (D-VIS, band D-OV), security north (SEC-N, band SEC-OV) and check-in islands
// B and C, which no other suite plants: the live and immigration screens' own suite uses the arrivals visitors (A-VIS).
// Beyond 500 bands the read is refused with 409 and a fixed text, never cut short (CWE-120); a profile holds at most 500
// zones, so no site here can reach it: the unit tests prove the bound (OverflowBandStatesTests.Within_*) and the 409
// (OverflowBandsEndpointTests), and overflow-quality.spec.ts the screens' answer to it.

test.skip(!databaseAvailable, 'overflow bands need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = (site = 'DMO') => `${hosts.main}/api/v1/sites/${site}/overflow-bands`;
const minute = (minutesAgo: number) => new Date(Math.floor(Date.now() / 60_000) * 60_000 - minutesAgo * 60_000);

type Planted = { key: string; band: string; at: Date; max: number };
let planted: Planted[] = [];

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

const plantedKeys = ['DMO/D-VIS', 'DMO/SEC-N', 'DMO/CI-B', 'DMO/CI-C', 'DMO-X/SEC-N'];

async function clear(db: pg.Client): Promise<void> {
	// Band minutes are evidence: the runtime role cannot delete them, the migration login (this client) can.
	await db.query('DELETE FROM overflow_minute WHERE zone_key = ANY($1) AND minute_utc >= $2', [plantedKeys, new Date(Date.now() - 3 * 3_600_000)]);
}

/** The published DMO profile's overflow bands by queue zone, as the endpoint must list them. */
async function publishedBands(db: pg.Client): Promise<Record<string, string[]>> {
	const rows = await db.query(
		`SELECT q.name AS zone, b.name AS band FROM zone b
		   JOIN zone_profile p ON p.id = b.profile_id
		   JOIN zone q ON q.id = b.queue_zone_id AND q.profile_id = b.profile_id
		  WHERE p.site_code = 'DMO' AND p.status = 'Published' AND b.kind = 'Overflow' AND q.kind = 'Queue'`
	);
	const byZone: Record<string, string[]> = {};
	for (const { zone, band } of rows.rows as { zone: string; band: string }[]) (byZone[zone] ??= []).push(band);
	return byZone;
}

let border: string, terminal: string, handler: string, admin: string, observer: string, elsewhere: string;
let expectedBands: Record<string, string[]> = {};

test.beforeAll(async () => {
	[border, terminal, handler, admin, observer, elsewhere] = await Promise.all(
		[
			accounts().dmoBorder,
			accounts().dmoTerminal,
			accounts().dmoHandler,
			accounts().webAdmin,
			accounts().ValidationObserver,
			accounts().BorderShiftSupervisor
		].map(async (a) => (await signIn(a)).accessToken)
	);
	const db = database();
	await db.connect();
	try {
		await clear(db);
		expectedBands = await publishedBands(db);
		planted = [
			// D-OV: empty three minutes ago, 14 people two minutes ago (the latest reading decides).
			{ key: 'DMO/D-VIS', band: 'D-OV', at: minute(3), max: 0 },
			{ key: 'DMO/D-VIS', band: 'D-OV', at: minute(2), max: 14 },
			// SEC-OV: reported empty; another site's key of the same names is never read for DMO.
			{ key: 'DMO/SEC-N', band: 'SEC-OV', at: minute(3), max: 0 },
			{ key: 'DMO-X/SEC-N', band: 'SEC-OV', at: minute(1), max: 40 },
			// CI-B-OV: last heard 20 minutes ago, outside the window, so not heard.
			{ key: 'DMO/CI-B', band: 'CI-B-OV', at: minute(20), max: 9 },
			// A band the published profile does not have, under CI-C's key: not read.
			{ key: 'DMO/CI-C', band: 'CI-C-OLD', at: minute(2), max: 30 }
		];
		for (const p of planted)
			await db.query(
				`INSERT INTO overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on)
				 VALUES ($1, $2, $3, 12, 0, $4, now())
				 ON CONFLICT (zone_key, band_name, minute_utc) DO UPDATE SET max_occupancy = EXCLUDED.max_occupancy, min_occupancy = 0`,
				[p.key, p.band, p.at, p.max]
			);
	} finally {
		await db.end();
	}
});

test.afterAll(async () => {
	const db = database();
	await db.connect();
	try {
		await clear(db);
	} finally {
		await db.end();
	}
});

type Band = { band: string; state: string; minuteUtc: string | null; maxOccupancy: number | null };
/** A planted minute as the API writes a UTC time (ISO 8601 without fractional seconds, ending in Z). */
const written = (key: string, band: string, max: number) =>
	planted
		.find((p) => p.key === key && p.band === band && p.max === max)!
		.at.toISOString()
		.replace('.000Z', 'Z');
type Zone = { zone: string; state: string; bands: Band[] };

test('each queue zone with bands, with each band’s latest reading of the last 6 minutes and the zone’s state', async () => {
	const answer = await call('GET', api(), { token: border });
	await expectStatus(answer, 200, 'the border shift supervisor reads the bands');
	const view = await answer.json();
	expect(view).toMatchObject({ siteCode: 'DMO', freshMinutes: 6 });
	expect(Math.abs(Date.parse(view.asOfUtc) - Date.now()), 'answered on Ariva’s clock, now').toBeLessThan(60_000);

	const zones = view.zones as Zone[];
	expect(zones.map((z) => z.zone), 'every queue zone of the published profile with a band, in name order, and nothing else').toEqual(
		Object.keys(expectedBands).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
	);
	for (const zone of zones)
		expect(zone.bands.map((b) => b.band), `${zone.zone}'s bands`).toEqual([...expectedBands[zone.zone]].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)));

	const byName = Object.fromEntries(zones.map((z) => [z.zone, z]));
	expect(byName['D-VIS'], 'D-OV held 14 people in its latest minute').toEqual({
		zone: 'D-VIS',
		state: 'Occupied',
		bands: [{ band: 'D-OV', state: 'Occupied', minuteUtc: written('DMO/D-VIS', 'D-OV', 14), maxOccupancy: 14 }]
	});
	expect(byName['SEC-N'], 'SEC-OV reported empty; DMO-X/SEC-N is another key').toEqual({
		zone: 'SEC-N',
		state: 'Empty',
		bands: [{ band: 'SEC-OV', state: 'Empty', minuteUtc: written('DMO/SEC-N', 'SEC-OV', 0), maxOccupancy: 0 }]
	});
	expect(byName['CI-B'], 'a reading 20 minutes old is not heard: never read as empty').toEqual({
		zone: 'CI-B',
		state: 'NotHeard',
		bands: [{ band: 'CI-B-OV', state: 'NotHeard', minuteUtc: null, maxOccupancy: null }]
	});
	expect(byName['CI-C'].bands, 'a band name the profile does not have is not read').toEqual([
		{ band: 'CI-C-OV', state: 'NotHeard', minuteUtc: null, maxOccupancy: null }
	]);
	expect(byName['A-CIT'], 'a queue zone without a band is not listed').toBeUndefined();
	// Zone-level occupancy only: nothing of desks, tracks or people.
	// Every band whole or a refusal: an answer never says it was cut short.
	expect(Object.keys(view).sort()).toEqual(['asOfUtc', 'freshMinutes', 'siteCode', 'zones']);
	expect(Object.keys(byName['D-VIS'].bands[0]).sort()).toEqual(['band', 'maxOccupancy', 'minuteUtc', 'state']);
});

test('LiveQueue.View and the site decide who reads it', async () => {
	const url = api();
	const reference = await (await call('GET', url, { token: border })).json();
	for (const [who, token] of [
		['the terminal duty manager', terminal],
		['a handler station manager (the live screen shows every queue zone of its site)', handler],
		['an administrator', admin]
	] as const) {
		const answer = await call('GET', url, { token });
		await expectStatus(answer, 200, who);
		expect((await answer.json()).zones, `${who} reads the same zones`).toEqual(reference.zones);
	}
	expect((await call('GET', url, { token: observer })).status(), 'a validation observer has no live view').toBe(403);
	expect((await call('GET', url)).status(), 'anonymous').toBe(401);
	expect((await call('GET', url, { token: 'not-a-token' })).status(), 'a token that is not one').toBe(401);

	// Another site's supervisor: 404 like a site that does not exist, with nothing of the data in the answer.
	const foreign = await call('GET', url, { token: elsewhere });
	expect(foreign.status(), 'a supervisor of another site').toBe(404);
	const text = await foreign.text();
	for (const leak of ['D-VIS', 'D-OV', 'Occupied', 'zones']) expect(text, leak).not.toContain(leak);
	expect((await call('GET', api('E2E1'), { token: border })).status(), 'a site the caller does not reach').toBe(404);
	expect((await call('GET', api('ZZ9'), { token: admin })).status(), 'a site that does not exist').toBe(404);
	expect((await call('GET', api('dmo'), { token: border })).status(), 'a site code is exact').toBe(404);
	expect((await call('GET', api('DMO-X'), { token: admin })).status(), 'a key prefix is not a site').toBe(404);
});

test('attack payloads in the path answer 404 without an echo, and the query string is never read', async () => {
	for (const payload of [...sqlInjectionPayloads, ...xssPayloads, '../DMO', 'DMO%00', 'DMO/../E2E1', 'D'.repeat(300)]) {
		const answer = await call('GET', api(encodeURIComponent(payload)), { token: admin });
		expect(answer.status(), `${payload.slice(0, 30)} is no site`).toBeLessThan(500);
		expect([400, 404], payload.slice(0, 30)).toContain(answer.status());
		const text = await answer.text();
		for (const fragment of markupFragments) expect(text, fragment).not.toContain(fragment);
		expect(text).not.toContain('UNION');
	}
	expect((await call('GET', api(), { token: border })).status(), 'the table is still there').toBe(200);

	// Query values change nothing: no site, zone or window can be chosen through them.
	const plain = await (await call('GET', api(), { token: border })).json();
	const queried = await call('GET', `${api()}?siteCode=E2E1&zone=${encodeURIComponent("D-VIS' OR '1'='1")}&from=2000-01-01T00:00:00Z&freshMinutes=999`, {
		token: border
	});
	expect(queried.status(), 'a query string is ignored').toBe(200);
	expect((await queried.json()).zones).toEqual(plain.zones);

	for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
		const answer = await call(method, api(), { token: admin, data: { zones: [] } });
		expect(answer.status(), `${method} is not served`).toBeGreaterThanOrEqual(400);
		expect(answer.status(), `${method} is not served`).toBeLessThan(500);
	}
});
