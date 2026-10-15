import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-181a: GET api/v1/sites/{siteCode}/staffing-recommendation (ArrivalWaveLanes.View: border data, site-scoped) answers
// the indicative staffing recommendation (formulas F16 pilot version) that Ariva.Api.Cronz computes once a minute into
// lane_staffing: per arrivals-hall lane of the published profile, 13 intervals of the next 3 hours with the arrivals, the
// recommended open desks and what set them, the shortfall at the desk limit and the predicted waits, or the reason there is
// none. Ariva.Api.Stream does not run in the E2E run, so the suite plants one lane's minute as the job writes it (the owner's
// preview numbers for A-VIS at the demo airport) a few seconds after the job's own run of that minute, and checks that the
// job itself writes the other lanes; it reads the answer per role, site and payload. Lane aggregates only: no desk code.

test.skip(!databaseAvailable, 'the staffing recommendation needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = (site = 'DMO') => `${hosts.main}/api/v1/sites/${site}/staffing-recommendation`;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** The 13 intervals the job plans from a minute: to the next quarter hour, then 12 quarter hours. */
function plan(minute: Date): { start: Date; minutes: number }[] {
	const quarter = 15 * 60_000;
	const next = new Date(Math.floor(minute.getTime() / quarter) * quarter + quarter);
	const slots = [{ start: minute, minutes: Math.round((next.getTime() - minute.getTime()) / 60_000) }];
	for (let k = 1; k < 13; k++) slots.push({ start: new Date(next.getTime() + (k - 1) * quarter), minutes: 15 });
	return slots;
}

// The owner's preview (2026-10-15) for the first three intervals; a flight without passengers in the fifth.
const preview = [
	{ arrivals: 120, recommended: 5, bound: 'Wait', waitRecommended: 9, waitOpen: 14.5 },
	{ arrivals: 180, recommended: 7, bound: 'Busy', waitRecommended: 10, waitOpen: 31.2 },
	{ arrivals: 90, recommended: 4, bound: 'Both', waitRecommended: 6, waitOpen: 6 },
	{ arrivals: 60, recommended: 3, bound: 'Wait', waitRecommended: 4, waitOpen: 4 }
];

let planted: Date;
let border: string, terminal: string, handler: string, admin: string, observer: string, elsewhere: string;

async function plant(db: pg.Client): Promise<void> {
	// Planted between seconds 0 and 40 of a minute: after the job's run of the minute before, more than a minute before its
	// run of the next, so the planted minute stays the latest while the tests read it.
	while (new Date().getUTCSeconds() >= 40) await sleep(1_000);
	planted = new Date(Math.floor(Date.now() / 60_000) * 60_000);
	const slots = plan(planted);
	await db.query('BEGIN');
	try {
		await db.query(`DELETE FROM lane_staffing WHERE zone_key = 'DMO/A-VIS' AND minute_utc = $1`, [planted]);
		for (const [k, slot] of slots.entries()) {
			const p = preview[k];
			const gap = p ? 'None' : k === 4 ? 'FlightWithoutPassengers' : 'BacklogUnknown';
			await db.query(
				`INSERT INTO lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
				                            wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now,
				                            open_now_degraded, desks, target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc)
				 VALUES ('DMO/A-VIS', $1, $2, 'VIS', $3, $4, $5, $6, $7, false, NULL, $8, $9, false, $10, $11, 'None', $12, 42, 4, false, 15, 10, 0.85,
				         0.6316, 'Measured', $13)
				 ON CONFLICT (zone_key, minute_utc, interval_index) DO UPDATE SET arrivals = EXCLUDED.arrivals, recommended = EXCLUDED.recommended,
				   bound = EXCLUDED.bound, wait_recommended = EXCLUDED.wait_recommended, wait_open = EXCLUDED.wait_open, gap = EXCLUDED.gap`,
				[
					planted,
					k,
					slot.start,
					slot.minutes,
					p ? p.arrivals : k === 4 ? null : 30,
					p ? p.recommended : null,
					p ? p.bound : 'None',
					p ? p.waitRecommended : null,
					p ? p.waitOpen : null,
					p ? 0 : null,
					gap,
					new Date(planted.getTime() - 60_000),
					new Date(planted.getTime() + 1_000)
				]
			);
		}
		await db.query('COMMIT');
	} catch (error) {
		await db.query('ROLLBACK');
		throw error;
	}
}

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
		await plant(db);
	} finally {
		await db.end();
	}
});

test.afterAll(async () => {
	// Planted rows go; the runtime role cannot delete them, the migration login (this client) can.
	const db = database();
	await db.connect();
	try {
		await db.query(`DELETE FROM lane_staffing WHERE zone_key = 'DMO/A-VIS' AND minute_utc = $1`, [planted]);
	} finally {
		await db.end();
	}
});

type Interval = {
	startUtc: string;
	minutes: number;
	arrivals: number | null;
	recommended: number | null;
	bound: string;
	atLimit: boolean;
	shortfallDesks: number | null;
	waitRecommendedMinutes: number | null;
	waitOpenNowMinutes: number | null;
	noServiceOpenNow: boolean;
	gap: string;
};
type Lane = { zone: string; zoneKey: string; lane: string; gap: string; openNow: number | null; intervals: Interval[]; [key: string]: unknown };

const reasons = ['None', 'NoQueueLength', 'NoThroughput', 'FlightWithoutPassengers', 'BacklogUnknown', 'ArrivalsTruncated', 'NoDesks', 'NoLaneDemand', 'Stale'];

test('each arrivals-hall lane with its 13 intervals, the owner’s preview for the planted visitors lane', async () => {
	const answer = await call('GET', api(), { token: border });
	expect(answer.status(), answer.status() === 200 ? 'the border shift supervisor reads it' : await answer.text()).toBe(200);
	const view = await answer.json();
	expect(view).toMatchObject({ siteCode: 'DMO', intervalMinutes: 15, indicative: true });
	expect(Object.keys(view).sort()).toEqual(['asOfUtc', 'indicative', 'intervalMinutes', 'lanes', 'siteCode']);

	const lanes = view.lanes as Lane[];
	const db = database();
	await db.connect();
	let expected: string[];
	try {
		// The published profile's queue zones with a lane category on a level with an Immigration checkpoint (the reference
		// profile's A-CRW, A-CIT, A-RES, A-VIS and A-EG; the departures hall's D-* are on the Emigration level).
		const rows = await db.query(
			`SELECT DISTINCT z.name FROM zone z JOIN zone_profile p ON p.id = z.profile_id
			  WHERE p.site_code = 'DMO' AND p.status = 'Published' AND z.kind = 'Queue' AND z.lane_category IS NOT NULL
			    AND EXISTS (SELECT 1 FROM checkpoint c WHERE c.level_id = z.level_id AND c.site_code = 'DMO' AND c.deleted_on IS NULL AND c.kind = 'Immigration')`
		);
		expected = (rows.rows as { name: string }[]).map((r) => r.name).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
	} finally {
		await db.end();
	}
	expect(expected).toContain('A-VIS');
	expect(expected.some((zone) => zone.startsWith('D-')), 'no departures lane').toBe(false);
	expect(lanes.map((l) => l.zone), 'the arrivals hall’s lane queues, in name order; the departures hall has none in this version').toEqual(expected);
	for (const lane of lanes) expect(reasons, `${lane.zone}: ${lane.gap}`).toContain(lane.gap);

	const vis = lanes.find((l) => l.zone === 'A-VIS')!;
	expect(vis).toMatchObject({
		zoneKey: 'DMO/A-VIS',
		lane: 'VIS',
		minuteUtc: planted.toISOString().replace('.000Z', 'Z'),
		targetMinutes: 10,
		busyCap: 0.85,
		desks: 15,
		openNow: 4,
		openNowDegraded: false,
		queueNow: 42,
		throughputSource: 'Measured',
		gap: 'None'
	});
	expect(vis.cycleMinutes as number).toBeCloseTo(1.583, 3);
	expect(vis.intervals).toHaveLength(13);
	expect(vis.intervals.map((i) => i.startUtc)).toEqual(plan(planted).map((s) => s.start.toISOString().replace('.000Z', 'Z')));
	for (const [k, p] of preview.entries())
		expect(vis.intervals[k], `interval ${k}`).toMatchObject({
			arrivals: p.arrivals,
			recommended: p.recommended,
			bound: p.bound,
			waitRecommendedMinutes: p.waitRecommended,
			waitOpenNowMinutes: p.waitOpen,
			gap: 'None'
		});
	expect(vis.intervals[4]).toMatchObject({ arrivals: null, recommended: null, gap: 'FlightWithoutPassengers' });
	expect(vis.intervals.slice(5).every((i) => i.gap === 'BacklogUnknown' && i.recommended === null && i.arrivals === 30)).toBe(true);

	// Lane aggregates only: no desk, checkpoint or desk key in the answer.
	const text = JSON.stringify(view);
	for (const leak of ['AR-', 'AG-', 'IMM/', 'IN0']) expect(text, leak).not.toContain(leak);
});

test('Ariva.Api.Cronz writes every arrivals-hall lane once a minute', async () => {
	test.setTimeout(200_000);
	// The lanes the suite does not plant: the job's own rows, written within the last few minutes.
	const db = database();
	await db.connect();
	try {
		let latest: Date | null = null;
		for (let attempt = 0; attempt < 30; attempt++) {
			const row = await db.query(`SELECT max(minute_utc) AS latest, count(DISTINCT zone_key) AS lanes FROM lane_staffing
			                             WHERE zone_key = ANY($1) AND minute_utc >= now() - interval '3 minutes'`, [['DMO/A-CIT', 'DMO/A-CRW', 'DMO/A-RES', 'DMO/A-EG']]);
			latest = row.rows[0].latest;
			if (latest && Number(row.rows[0].lanes) === 4) break;
			await sleep(5_000);
		}
		expect(latest, 'a minute of the job within the last 3 minutes').not.toBeNull();
		const counts = await db.query(`SELECT zone_key, count(*)::int AS rows FROM lane_staffing WHERE zone_key = ANY($1) AND minute_utc = $2 GROUP BY zone_key`, [
			['DMO/A-CIT', 'DMO/A-CRW', 'DMO/A-RES', 'DMO/A-EG'],
			latest
		]);
		for (const row of counts.rows as { zone_key: string; rows: number }[]) expect(row.rows, `${row.zone_key}: 13 intervals once`).toBe(13);
		const departures = await db.query(`SELECT count(*)::int AS rows FROM lane_staffing WHERE zone_key LIKE 'DMO/D-%'`);
		expect(departures.rows[0].rows, 'the departures hall gets no recommendation in this version').toBe(0);
	} finally {
		await db.end();
	}
	const eg = ((await (await call('GET', api(), { token: border })).json()).lanes as Lane[]).find((l) => l.zone === 'A-EG');
	if (eg) expect(eg.intervals.every((i) => i.recommended === null), 'e-gates are not desks: no recommendation for the e-gate lane').toBe(true);
});

test('ArrivalWaveLanes.View and the site decide who reads it', async () => {
	const url = api();
	expect((await call('GET', url, { token: admin })).status(), 'an administrator').toBe(200);
	for (const [who, token] of [
		['the terminal duty manager (the arrival wave’s totals only: the lane split is border data)', terminal],
		['a handler station manager', handler],
		['a validation observer', observer]
	] as const) {
		const answer = await call('GET', url, { token });
		expect(answer.status(), who).toBe(403);
		const text = await answer.text();
		for (const leak of ['A-VIS', 'lanes', 'recommended']) expect(text, `${who}: ${leak}`).not.toContain(leak);
	}
	expect((await call('GET', url)).status(), 'anonymous').toBe(401);
	expect((await call('GET', url, { token: 'not-a-token' })).status(), 'a token that is not one').toBe(401);

	// Another site's supervisor: 404 like a site that does not exist, with nothing of the data in the answer.
	const foreign = await call('GET', url, { token: elsewhere });
	expect(foreign.status(), 'a supervisor of another site').toBe(404);
	const text = await foreign.text();
	for (const leak of ['A-VIS', 'VIS', 'lanes', 'recommended']) expect(text, leak).not.toContain(leak);
	expect((await call('GET', api('E2E1'), { token: border })).status(), 'a site the caller does not reach').toBe(404);
	expect((await call('GET', api('ZZ9'), { token: admin })).status(), 'a site that does not exist').toBe(404);
	expect((await call('GET', api('dmo'), { token: border })).status(), 'a site code is exact').toBe(404);
});

test('attack payloads in the path answer 404 without an echo, and the query string is never read', async () => {
	for (const payload of [...sqlInjectionPayloads, ...xssPayloads, '../DMO', 'DMO%00', 'DMO/../E2E1', 'D'.repeat(300)]) {
		const answer = await call('GET', api(encodeURIComponent(payload)), { token: admin });
		expect(answer.status(), `${payload.slice(0, 30)} is no site`).toBeLessThan(500);
		expect([400, 404], payload.slice(0, 30)).toContain(answer.status());
		const body = await answer.text();
		for (const fragment of markupFragments) expect(body, fragment).not.toContain(fragment);
		expect(body).not.toContain('UNION');
	}
	expect((await call('GET', api(), { token: border })).status(), 'the table is still there').toBe(200);

	// Query values change nothing: no site, lane, window or target can be chosen through them.
	const plain = await (await call('GET', api(), { token: border })).json();
	const queried = await call('GET', `${api()}?siteCode=E2E1&lane=${encodeURIComponent("VIS' OR '1'='1")}&targetMinutes=0&busyCap=2&minutes=999`, {
		token: border
	});
	expect(queried.status(), 'a query string is ignored').toBe(200);
	const answer = await queried.json();
	expect(answer.siteCode).toBe('DMO');
	expect(answer.lanes.map((l: Lane) => `${l.zone}/${l.lane}`), 'the same site and lanes').toEqual(plain.lanes.map((l: Lane) => `${l.zone}/${l.lane}`));
	expect(answer.lanes.filter((l: Lane) => l.targetMinutes !== null).every((l: Lane) => l.targetMinutes === 10 && l.busyCap === 0.85), 'no target or cap from the query').toBe(true);

	for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
		const response = await call(method, api(), { token: admin, data: { lanes: [] } });
		expect(response.status(), `${method} is not served`).toBeGreaterThanOrEqual(400);
		expect(response.status(), `${method} is not served`).toBeLessThan(500);
	}
});
