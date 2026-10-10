import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-114a: the continuous health checks of F18 per queue zone and bin, served by GET api/v1/sites/{siteCode}/zone-health
// (DataQuality.View, site-scoped). Ariva.Api.Stream writes zone_health_bin with every bin result; it does not run in the
// E2E run, so the suite plants the rows the golden replay of the reference evening (seed 9303, GoldenReplayTests) gives
// for the arrivals Visitors zone A-VIS and Handler B's island C (CI-C), as the stream writes them, and reads them back per
// role and site. A later revision of a bin supersedes an earlier one; a range is at most 31 days.

test.skip(!databaseAvailable, 'zone health needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = (site = 'DMO') => `${hosts.main}/api/v1/sites/${site}/zone-health`;
const evening = { from: '2026-09-28T17:00:00Z', to: '2026-09-28T20:30:00Z' };
const query = (zone: string, from = evening.from, to = evening.to) =>
	`?zone=${encodeURIComponent(zone)}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`;

type Row = {
	zone: string;
	start: string;
	revision: number;
	status: 'Provisional' | 'Final';
	entries: number;
	exits: number;
	occupancyStart: number | null;
	occupancyEnd: number | null;
	residual: number | null;
	tracks: [number, number, number, number, number, number, number];
	rate: number | null;
	minutes: [number, number, number];
};

// The golden replay's final bins (entries, exits, occupancy at both ends, residual, tracks entered, exited, abandoned,
// fragmented, censored, rejected, open, completion rate, occupancy minutes observed, checked against capacity, outside).
const golden: Row[] = [
	{ zone: 'A-VIS', start: '2026-09-28T18:00:00Z', revision: 1, status: 'Final', entries: 243, exits: 108, occupancyStart: 10, occupancyEnd: 145,
		residual: 0, tracks: [243, 243, 0, 0, 0, 0, 0], rate: 1, minutes: [15, 15, 0] },
	{ zone: 'A-VIS', start: '2026-09-28T18:15:00Z', revision: 1, status: 'Final', entries: 48, exits: 120, occupancyStart: 145, occupancyEnd: 73,
		residual: 0, tracks: [48, 48, 0, 0, 0, 0, 0], rate: 1, minutes: [15, 15, 0] },
	{ zone: 'A-VIS', start: '2026-09-28T18:30:00Z', revision: 1, status: 'Final', entries: 0, exits: 73, occupancyStart: 73, occupancyEnd: 0,
		residual: 0, tracks: [0, 0, 0, 0, 0, 0, 0], rate: null, minutes: [15, 15, 0] },
	{ zone: 'CI-C', start: '2026-09-28T19:00:00Z', revision: 1, status: 'Final', entries: 62, exits: 29, occupancyStart: 0, occupancyEnd: 33,
		residual: 0, tracks: [62, 62, 0, 0, 0, 0, 0], rate: 1, minutes: [15, 15, 0] }
];
// An earlier revision of a bin, superseded by a recomputation's revision 2 (the golden values).
const superseded: Row = { ...golden[1], revision: 1, residual: 4, occupancyEnd: 69, minutes: [15, 15, 2] };
const planted: Row[] = [golden[0], superseded, { ...golden[1], revision: 2 }, golden[2], golden[3]];

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

async function clear(db: pg.Client): Promise<void> {
	// Rows are evidence: the runtime role cannot delete them, the migration login (this client) can.
	await db.query("DELETE FROM zone_health_bin WHERE zone_key IN ('DMO/A-VIS', 'DMO/CI-C') AND start_utc >= $1 AND start_utc < $2", [
		'2026-09-28T00:00:00Z',
		'2026-09-29T00:00:00Z'
	]);
}

let border: string, terminal: string, handler: string, elsewhere: string, admin: string;

test.beforeAll(async () => {
	[border, terminal, handler, elsewhere, admin] = await Promise.all(
		[accounts().reportBorder, accounts().reportTerminal, accounts().dmoHandler, accounts().BorderShiftSupervisor, accounts().webAdmin].map(
			async (a) => (await signIn(a)).accessToken
		)
	);
	const db = database();
	await db.connect();
	try {
		await clear(db);
		for (const r of planted)
			await db.query(
				`INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start,
				   occupancy_end, conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected,
				   tracks_open, track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
				 VALUES ($1, $2, $3, 15, $4, 12, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, now())`,
				[`DMO/${r.zone}`, r.start, r.revision, r.status, r.entries, r.exits, r.occupancyStart, r.occupancyEnd, r.residual, ...r.tracks, r.rate, ...r.minutes]
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

const view = (r: Row) => ({
	startUtc: r.start,
	lengthMinutes: 15,
	revision: r.revision,
	status: r.status,
	zoneProfileVersion: 12,
	entries: r.entries,
	exits: r.exits,
	occupancyStart: r.occupancyStart,
	occupancyEnd: r.occupancyEnd,
	conservationResidual: r.residual,
	tracksEntered: r.tracks[0],
	tracksExited: r.tracks[1],
	tracksAbandoned: r.tracks[2],
	tracksFragmented: r.tracks[3],
	tracksCensored: r.tracks[4],
	tracksRejected: r.tracks[5],
	tracksOpen: r.tracks[6],
	trackCompletionRate: r.rate,
	occupancyMinutes: r.minutes[0],
	capacityMinutes: r.minutes[1],
	minutesOutsideCapacity: r.minutes[2]
});

test('the reference evening reads back per bin, the latest revision of each', async () => {
	const answer = await call('GET', api() + query('A-VIS'), { token: border });
	expect(answer.status(), await answer.text()).toBe(200);
	const health = await answer.json();
	expect(health).toMatchObject({ siteCode: 'DMO', zone: 'A-VIS', fromUtc: evening.from, toUtc: evening.to, truncated: false });
	expect(health.bins).toEqual([view(golden[0]), view({ ...golden[1], revision: 2 }), view(golden[2])]);

	const island = await (await call('GET', api() + query('CI-C', '2026-09-28T19:00:00Z', '2026-09-28T19:15:00Z'), { token: terminal })).json();
	expect(island.bins, "Handler B's island C at 19:00, the duty manager's read").toEqual([view(golden[3])]);
	const one = await (await call('GET', api() + query('A-VIS', '2026-09-28T18:15:00Z', '2026-09-28T18:15:00.001Z'), { token: admin })).json();
	expect(one.bins.map((b: { startUtc: string }) => b.startUtc), 'a bin belongs to the range of its start').toEqual(['2026-09-28T18:15:00Z']);
	const empty = await (await call('GET', api() + query('A-VIS', '2026-08-01T00:00:00Z', '2026-08-31T00:00:00Z'), { token: border })).json();
	expect(empty.bins, 'a range without bins').toEqual([]);
});

test('the permission and the site decide who reads it', async () => {
	const url = api() + query('A-VIS');
	expect((await call('GET', url, { token: terminal })).status(), 'the terminal duty manager').toBe(200);
	expect((await call('GET', url, { token: admin })).status(), 'an administrator').toBe(200);
	expect((await call('GET', url, { token: handler })).status(), 'a handler sees its own counters only').toBe(403);
	expect((await call('GET', url)).status()).toBe(401);

	// Another site's supervisor: 404 like a site that does not exist, with nothing of the data in the answer.
	const foreign = await call('GET', url, { token: elsewhere });
	expect(foreign.status()).toBe(404);
	const text = await foreign.text();
	for (const leak of ['A-VIS', 'bins', 'entries', '243']) expect(text, leak).not.toContain(leak);
	expect((await call('GET', api('E2E1') + query('A-VIS'), { token: border })).status(), 'a site the caller does not reach').toBe(404);
	// The site is checked before the query: an invalid query against a site the caller does not reach is still 404, so
	// a 400 never confirms that the site exists.
	const foreignInvalid = await call('GET', api('E2E1') + query('A-VIS', 'yesterday', evening.to), { token: border });
	expect(foreignInvalid.status(), 'an invalid query against a site the caller does not reach').toBe(404);
	expect(await foreignInvalid.text()).not.toContain('yesterday');
	expect((await call('GET', api('ZZ9') + query('A-VIS'), { token: admin })).status(), 'a site that does not exist').toBe(404);
	expect((await call('GET', api('dmo') + query('A-VIS'), { token: admin })).status(), 'a site code is exact').toBe(404);
});

test('a range over 31 days or not in UTC is refused, without echoing the query', async () => {
	const refused: [string, string, string][] = [
		['A-VIS', '2026-09-01T00:00:00Z', '2026-10-02T00:00:01Z'],
		['A-VIS', '2026-01-01T00:00:00Z', '2026-12-31T00:00:00Z'],
		['A-VIS', evening.to, evening.from],
		['A-VIS', evening.from, evening.from],
		['A-VIS', '2026-09-28T17:00:00', evening.to],
		['A-VIS', '2026-09-28T17:00:00+04:00', evening.to],
		['A-VIS', 'yesterday', evening.to],
		['A-VIS', "2026-09-28' OR '1'='1", evening.to],
		['', evening.from, evening.to],
		[' A-VIS', evening.from, evening.to],
		['x'.repeat(201), evening.from, evening.to],
		['A-VIS\u0000', evening.from, evening.to]
	];
	for (const [zone, from, to] of refused) {
		const answer = await call('GET', api() + query(zone, from, to), { token: border });
		expect(answer.status(), `${zone.slice(0, 20)} ${from} ${to}`).toBe(400);
		const text = await answer.text();
		expect(text).not.toContain('OR');
		expect(text).not.toContain('yesterday');
	}

	expect((await call('GET', `${api()}?zone=A-VIS`, { token: border })).status(), 'no range').toBe(400);
	expect((await call('GET', api() + query('A-VIS', '2026-09-01T00:00:00Z', '2026-10-02T00:00:00Z'), { token: border })).status(), 'exactly 31 days').toBe(200);
});

test('a zone that is no queue zone of the site answers 404, whatever the payload', async () => {
	for (const zone of ['NOPE', 'A-OV', "A-VIS' OR '1'='1", "A-VIS'; DROP TABLE zone_health_bin; --", '<script>alert(1)</script>', '../CI-C', 'a-vis'])
		expect((await call('GET', api() + query(zone), { token: border })).status(), zone).toBe(404);
	expect((await call('GET', api() + query('A-VIS'), { token: border })).status(), 'the table is still there').toBe(200);
});
