import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, login, signIn, unusedTotpCode } from '../support/accounts';
import { expectApiSecurityHeaders, expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-104i: the validation rehearsal. The simulator knows the reference evening's truth (seed 9303) and acts as validation
// observers through Ariva.Api.Main's capture API, signed in as Validation observer accounts through the normal sign-in (the
// first with its authenticator, so a TOTP code goes with its password), with optional injected error. The suite works at its
// own site E2ER (Asia/Dubai), whose published profile names its queue zones, lines and desks as the demo airport does (A-VIS,
// A-RES, their entry and exit lines, desks AR-05 to AR-12), and lays the evening on yesterday in Dubai.
//
// Ariva's side: the E2E stack runs no Stream host, so the suite plants what a perfect pipeline would have stored for that evening
// (queue minutes and bins, zone health, line counts, desk minutes, the availability ledger, and S-17's outage from 18:20 to
// 18:30 as a zone outage), from the simulator's truth endpoint, with the database owner, as other suites plant stream outputs.
// The observers' side is real: every count, tracer run and desk log passes the capture API's own checks. The three outcomes of
// the story: with no injected error every criterion passes; a 6 percent count error on one line fails count accuracy for that
// line only; the bins under S-17's outage are Degraded in the results. The control endpoints need the control scope (401
// without a key, 403 with a read key), never echo a credential or a hostile value, and refuse oversized bodies (413). The
// campaign's creator, signed in by the simulator, is refused by Ariva on every capture, and the simulator reports it.

test.skip(!databaseAvailable, 'the rehearsal needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const site = 'E2ER';
const admin = `${hosts.main}/api/v1/admin`;
const campaigns = `${hosts.main}/api/v1/sites/${site}/validation/campaigns`;
const simulation = `${hosts.simulation}/api/v1/simulation/validation`;
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };
const reader = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_READ_KEY ?? ''}` };
const minute = 60_000;

// The reference evening laid on yesterday in Dubai (UTC+4, no daylight saving): its 00:00 is the day before at 20:00Z, so the
// scenario's clock minute m is that instant plus m minutes, and every bin has ended long before the suite runs.
const dubaiNow = new Date(Date.now() + 4 * 3_600_000);
const yesterday = new Date(Date.UTC(dubaiNow.getUTCFullYear(), dubaiNow.getUTCMonth(), dubaiNow.getUTCDate() - 1));
const localDay = yesterday.toISOString().slice(0, 10);
const dayStart = yesterday.getTime() - 4 * 3_600_000;
const dayStartUtc = new Date(dayStart).toISOString().replace('.000Z', 'Z');
/** The rehearsed window: 18:00 to 20:00, with the Visitors wave, S-17's outage (18:20 to 18:30) and the hall emptying at 18:30. */
const evening = { fromMinute: 1080, toMinute: 1200 };
/** What is planted around it: the bin before (bins read either side) and 90 minutes after (the tracers' and nowcasts' waits). */
const planted = { fromMinute: 1065, toMinute: 1290 };
const at = (clockMinute: number) => new Date(dayStart + clockMinute * minute);
const utc = (clockMinute: number) => at(clockMinute).toISOString().replace('.000Z', 'Z');
const zones = ['A-VIS', 'A-RES'] as const;
const lineNames = ['A-VIS entry', 'A-VIS exit', 'A-RES entry', 'A-RES exit'];
const deskLanes: Record<string, string> = { 'AR-05': 'RES', 'AR-06': 'RES', 'AR-08': 'VIS', 'AR-09': 'VIS', 'AR-10': 'VIS', 'AR-11': 'VIS', 'AR-12': 'VIS' };
const criteria = ['CountAccuracy', 'WaitError', 'WaitBias', 'TrackCompletion', 'NowcastError', 'Availability'];

interface Scope {
	version: number;
	zones: Record<string, string>;
	lines: Record<string, string>;
	desks: Record<string, string>;
}

interface TruthMinute {
	startUtc: string;
	entries: number;
	exits: number;
	waits: number;
	meanWaitMinutes: number | null;
}

interface Truth {
	scenarioSite: string;
	scenarioSeed: number;
	queues: { queue: string; minutes: TruthMinute[] }[];
	desks: { desk: string; queue: string; states: (string | null)[] }[];
	outages: { sensor: string; queueZone: string; fromUtc: string; toUtc: string }[];
}

let manager: string, administrator: string, dual: string;
let scope: Scope;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

/** E2ER's topology (the administrator) and published profile (the manager, with a second factor), created the first time. */
async function ensureScope(): Promise<Scope> {
	const list = async (entity: string, query: string) => {
		const response = await call('GET', `${admin}/${entity}?${query}`, { token: administrator });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(200);
		const body = await response.json();
		return (body.data ?? body) as any[];
	};
	const create = async (entity: string, data: unknown) => {
		const response = await call('POST', `${admin}/${entity}`, { token: administrator, data });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
		return response.json();
	};

	let level = (await list('levels', `siteCode=${site}&pageSize=10`))[0];
	if (!level) {
		const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
		const iata = Array.from({ length: 3 }, () => letters[Math.floor(Math.random() * 26)]).join('');
		const airport = await create('airports', { iataCode: iata, name: `E2E rehearsal ${iata}`, timeZoneId: 'Asia/Dubai' });
		const terminal = await create('terminals', { airportId: airport.id, code: 'T1', name: 'Terminal 1', siteCode: site });
		level = await create('levels', { terminalId: terminal.id, code: 'ARR', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	}

	const checkpoints = await list('checkpoints', `siteCode=${site}&pageSize=100`);
	const immigration = checkpoints.find((c) => c.code === 'IMM') ?? (await create('checkpoints', { levelId: level.id, code: 'IMM', name: 'Arrival immigration', kind: 'Immigration' }));
	const existing = await list('desks', `parentId=${immigration.id}&pageSize=100`);
	const desks: Record<string, string> = {};
	for (const [code, lane] of Object.entries(deskLanes))
		desks[code] = (existing.find((d) => d.code === code) ?? (await create('desks', { checkpointId: immigration.id, code, kind: 'Desk', laneCategories: [lane] }))).id;

	const api = `${admin}/zone-profiles`;
	const history = await (await call('GET', `${api}?siteCode=${site}`, { token: manager })).json();
	let published = history.find((p: any) => p.status === 'Published');
	if (!published) {
		for (const old of history.filter((p: any) => p.status === 'Draft')) await call('DELETE', `${api}/${old.id}`, { token: administrator });
		const draft = await call('POST', `${api}/drafts`, { token: manager, data: { siteCode: site, name: 'Rehearsal hall' } });
		expect(draft.status(), await draft.text()).toBe(201);
		const id = (await draft.json()).profile.id as string;
		const zone = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/zones`, { token: manager, data: { levelId: level.id, ...data } });
			expect(response.status(), await response.text()).toBe(201);
			return (await response.json()).id as string;
		};
		const line = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/lines`, { token: manager, data: { levelId: level.id, ...data } });
			expect(response.status(), await response.text()).toBe(201);
		};
		const visitors = await zone({ name: 'A-VIS', kind: 'Queue', polygon: '10 10,34 10,34 22,10 22' });
		const residents = await zone({ name: 'A-RES', kind: 'Queue', polygon: '50 10,74 10,74 22,50 22' });
		await line({ name: 'A-VIS entry', role: 'Entry', startX: 10, startY: 12, endX: 10, endY: 16, zoneId: visitors });
		await line({ name: 'A-VIS exit', role: 'Exit', startX: 30, startY: 22, endX: 34, endY: 22, zoneId: visitors });
		await line({ name: 'A-RES entry', role: 'Entry', startX: 50, startY: 12, endX: 50, endY: 16, zoneId: residents });
		await line({ name: 'A-RES exit', role: 'Exit', startX: 70, startY: 22, endX: 74, endY: 22, zoneId: residents });
		const review = await (await call('GET', `${api}/${id}/validation`, { token: manager })).json();
		expect(review.publishable, JSON.stringify(review)).toBe(true);
		const publish = await call('POST', `${api}/${id}/publish`, { token: manager, data: { geometryHash: review.geometryHash } });
		expect(publish.status(), await publish.text()).toBe(200);
		published = await publish.json();
	}

	const view = await (await call('GET', `${api}/${published.id}`, { token: manager })).json();
	return {
		version: published.version,
		zones: Object.fromEntries(zones.map((name) => [name, view.zones.find((z: any) => z.name === name).id as string])),
		lines: Object.fromEntries(lineNames.map((name) => [name, view.lines.find((l: any) => l.name === name).id as string])),
		desks
	};
}

/** The evening's truth from the simulator: per queue and minute, the people in and out and the entrants' mean wait; the desks; the outages. */
async function readTruth(): Promise<Truth> {
	const answer = await call(
		'GET',
		`${simulation}/truth?site=DMO&dayStartUtc=${dayStartUtc}&fromMinute=${planted.fromMinute}&toMinute=${planted.toMinute}&queues=A-VIS,A-RES`,
		{ headers: reader }
	);
	expect(answer.status(), await answer.text()).toBe(200);
	return answer.json();
}

/**
 * What a perfect Ariva would have stored for the evening (the Stream host does not run in the E2E stack), written with the
 * database owner after clearing what an earlier run planted at E2ER for the same evening. Each zone's minutes (entries, exits,
 * the entrants' mean realised wait as the waits' mean, and as the published nowcast the next minute's entrants' mean wait, no
 * live part where nobody who enters the next minute has a wait),
 * final Good bins, health bins whose every track completed, the four lines' crossings per minute, each desk's minutes in the
 * state an observer sees, the availability ledger, and the scenario's sensor outages as zone outages (S-17 over A-VIS).
 */
async function plantSystem(truth: Truth) {
	const zoneKeys = zones.map((z) => `${site}/${z}`);
	const deskKeys = Object.keys(deskLanes).map((code) => `${site}/IMM/${code}`);
	const from = at(planted.fromMinute - 15).toISOString();
	const to = at(planted.toMinute + 15).toISOString();
	const db = database();
	await db.connect();
	try {
		await db.query('BEGIN');
		// One planting at a time (a retry or a repeated run plants the same rows): the next waits, then clears and plants again.
		await db.query("SELECT pg_advisory_xact_lock(hashtext('e2e-validation-rehearsal'))");
		await db.query('DELETE FROM queue_minute WHERE zone_key = ANY($1) AND minute_utc >= $2 AND minute_utc < $3', [zoneKeys, from, to]);
		await db.query('DELETE FROM queue_bin WHERE zone_key = ANY($1) AND start_utc >= $2 AND start_utc < $3', [zoneKeys, from, to]);
		await db.query('DELETE FROM zone_health_bin WHERE zone_key = ANY($1) AND start_utc >= $2 AND start_utc < $3', [zoneKeys, from, to]);
		await db.query('DELETE FROM line_minute WHERE zone_key = ANY($1) AND minute_utc >= $2 AND minute_utc < $3', [zoneKeys, from, to]);
		await db.query('DELETE FROM zone_outage WHERE zone_key = ANY($1) AND from_utc >= $2 AND from_utc < $3', [zoneKeys, from, to]);
		await db.query('DELETE FROM desk_minute WHERE desk_code = ANY($1) AND minute_utc >= $2 AND minute_utc < $3', [deskKeys, from, to]);
		await db.query('DELETE FROM availability_minute WHERE site_code = $1 AND minute_utc >= $2 AND minute_utc < $3', [site, from, to]);

		for (const queue of truth.queues) {
			const key = `${site}/${queue.queue}`;
			const minutes = queue.minutes;
			await db.query(
				`INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, nowcast_minutes, nowcast_degraded, updated_on)
				 SELECT $1, m, $2::integer, 'Final', e, x, w, mw, nc, CASE WHEN nc IS NOT NULL THEN false END, now()
				   FROM unnest($3::timestamptz[], $4::bigint[], $5::bigint[], $6::bigint[], $7::float8[], $8::float8[]) AS t(m, e, x, w, mw, nc)`,
				[
					key,
					scope.version,
					minutes.map((m) => m.startUtc),
					minutes.map((m) => m.entries),
					minutes.map((m) => m.exits),
					minutes.map((m) => m.waits),
					minutes.map((m) => m.meanWaitMinutes),
					minutes.map((_, i) => minutes[i + 1]?.meanWaitMinutes ?? null)
				]
			);
			for (let first = 0; first + 15 <= minutes.length; first += 15) {
				const bin = minutes.slice(first, first + 15);
				const sum = (pick: (m: TruthMinute) => number) => bin.reduce((total, m) => total + pick(m), 0);
				const [entries, exits, waits] = [sum((m) => m.entries), sum((m) => m.exits), sum((m) => m.waits)];
				await db.query(
					`INSERT INTO queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, abandoned, fragmented, censored, reanchored,
					                        rejected, open_people, late_events, profile_version, updated_on)
					 VALUES ($1, $2::timestamptz, 1, 15, 'Final', 'Good', $3::bigint, $4::bigint, $5::bigint, 0, 0, 0, 0, 0, 0, 0, $6::integer, now())`,
					[key, bin[0].startUtc, entries, exits, waits, scope.version]
				);
				const clock = planted.fromMinute + first;
				if (clock < evening.fromMinute || clock >= evening.toMinute) continue;
				await db.query(
					`INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, tracks_entered, tracks_exited,
					                              tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open, track_completion_rate,
					                              occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
					 VALUES ($1, $2::timestamptz, 1, 15, 'Final', $3::integer, $4::bigint, $5::bigint, $4::bigint, $4::bigint, 0, 0, 0, 0, 0, $6::float8, 15, 15, 0, now())`,
					[key, bin[0].startUtc, scope.version, entries, exits, entries > 0 ? 1 : null]
				);
			}

			const inEvening = minutes.slice(evening.fromMinute - planted.fromMinute, evening.toMinute - planted.fromMinute);
			for (const [line, role, pick] of [
				[`${queue.queue} entry`, 'Entry', (m: TruthMinute) => [m.entries, 0]],
				[`${queue.queue} exit`, 'Exit', (m: TruthMinute) => [0, m.exits]]
			] as [string, string, (m: TruthMinute) => number[]][]) {
				await db.query(
					`INSERT INTO line_minute (zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out, updated_on)
					 SELECT $1, $2, $3, 'Ariva', m, $4::integer, i, o, now() FROM unnest($5::timestamptz[], $6::bigint[], $7::bigint[]) AS t(m, i, o)`,
					[key, line, role, scope.version, inEvening.map((m) => m.startUtc), inEvening.map((m) => pick(m)[0]), inEvening.map((m) => pick(m)[1])]
				);
			}
		}

		for (const outage of truth.outages)
			await db.query('INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on) VALUES ($1, $2, $3, $4, true, now())', [
				`${site}/${outage.queueZone}`,
				outage.sensor,
				outage.fromUtc,
				outage.toUtc
			]);

		const minutes = Array.from({ length: evening.toMinute - evening.fromMinute }, (_, i) => evening.fromMinute + i);
		for (const [code, lane] of Object.entries(deskLanes)) {
			const states = truth.desks.find((d) => d.desk === code)!.states;
			const state = (m: number) => states[m - planted.fromMinute];
			const seconds = (name: string) => minutes.map((m) => (state(m) === name ? 60 : 0));
			await db.query(
				`INSERT INTO desk_minute (desk_code, lane, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, transactions,
				                          sensor_derived_seconds, present_seconds, degraded, updated_on)
				 SELECT $1, $2, m, c, i, s, p, 0, 0, 0, 0, false, now()
				   FROM unnest($3::timestamptz[], $4::float8[], $5::float8[], $6::float8[], $7::float8[]) AS t(m, c, i, s, p)`,
				[`${site}/IMM/${code}`, lane, minutes.map(utc), seconds('Closed'), seconds('Idle'), seconds('Serving'), seconds('Paused')]
			);
		}

		await db.query(
			`INSERT INTO availability_minute (site_code, minute_utc, local_date, calendar, state, reasons, zones_expected, zones_stale, zones_missing, zones_lagging,
			                                  profile_version, rule_version, decided_utc)
			 SELECT $1, m, $2::date, 'Operating', 'Available', '{}'::text[], 2, 0, 0, 0, $3::integer, 1, m + interval '2 minutes' FROM unnest($4::timestamptz[]) AS t(m)`,
			[site, localDay, scope.version, minutes.map(utc)]
		);
		await db.query('COMMIT');
	} catch (error) {
		await db.query('ROLLBACK');
		throw error;
	} finally {
		await db.end();
	}
}

/** The two observer accounts the simulator signs in as (the first with its authenticator's seed), Ariva-issued, given at run time. */
const observers = () =>
	[accounts().rehearsalObserver1, accounts().rehearsalObserver2].map((a) => ({ userName: a.userName, password: a.password, totpSecret: a.totpSecret ?? null }));

async function useObservers(list: { userName: string; password: string; totpSecret: string | null }[]) {
	const answer = await call('PUT', `${simulation}/observers`, { headers: operator, data: { observers: list } });
	expect(answer.status(), await answer.text()).toBe(200);
	const text = await answer.text();
	for (const entry of list) for (const secret of [entry.userName, entry.password, entry.totpSecret]) if (secret) expect(text, 'no credential comes back').not.toContain(secret);
}

/** A campaign over both zones, their four lines and the seven desks for yesterday, planned and started by <token>. */
async function runningCampaign(name: string, token = manager): Promise<string> {
	const created = await call('POST', campaigns, {
		token,
		data: {
			name,
			profileVersion: scope.version,
			zoneIds: Object.values(scope.zones),
			lineIds: Object.values(scope.lines),
			days: [localDay],
			targetBinsPerLine: 4,
			targetTracerRuns: 6,
			deskIds: Object.values(scope.desks)
		}
	});
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id as string;
	const started = await call('POST', `${campaigns}/${id}/start`, { token });
	expect(started.status(), await started.text()).toBe(200);
	return id;
}

async function rehearse(body: Record<string, unknown>): Promise<any> {
	const answer = await call('POST', `${simulation}/rehearsals`, {
		headers: operator,
		data: { siteCode: site, scenarioSite: 'DMO', scenarioSeed: 9303, dayStartUtc, ...evening, ...body }
	});
	expect(answer.status(), await answer.text()).toBe(200);
	return answer.json();
}

async function results(id: string): Promise<any> {
	const answer = await call('GET', `${campaigns}/${id}/results`, { token: manager });
	expect(answer.status(), await answer.text()).toBe(200);
	return answer.json();
}

const verdicts = (served: any) => Object.fromEntries(served.criteria.map((c: any) => [c.criterion, c.verdict]));

test.beforeAll(async () => {
	test.setTimeout(180_000);
	const { userName, password, totpSecret } = accounts().rehearsalManager;
	const signedIn = await login(userName, password, undefined, undefined, { code: await unusedTotpCode(totpSecret!) });
	expect(signedIn.status(), 'the rehearsal manager signs in with a second factor').toBe(200);
	manager = (await signedIn.json()).accessToken;
	[administrator, dual] = await Promise.all([accounts().SystemAdministrator, accounts().rehearsalDual].map(async (a) => (await signIn(a)).accessToken));
	scope = await ensureScope();
	const truth = await readTruth();
	expect([truth.scenarioSite, truth.scenarioSeed], 'the reference day').toEqual(['DMO', 9303]);
	expect(truth.outages.map((o) => [o.sensor, o.queueZone, o.fromUtc, o.toUtc])).toEqual([['S-17', 'A-VIS', utc(1100), utc(1110)]]);
	await plantSystem(truth);
	await useObservers(observers());
});

test('the control endpoints need the control scope, never echo a credential, and refuse hostile values and oversized bodies', async () => {
	test.setTimeout(120_000);
	// No key, a guessed key: 401 everywhere; a read key reads the status and the truth and gets 403 on the controls.
	for (const [method, path] of [
		['GET', ''],
		['GET', `/truth?dayStartUtc=${dayStartUtc}&fromMinute=1080&toMinute=1090`],
		['PUT', '/observers'],
		['POST', '/rehearsals']
	]) {
		expect((await call(method, `${simulation}${path}`, { data: method === 'GET' ? undefined : {} })).status(), `${method} ${path}`).toBe(401);
		expect((await call(method, `${simulation}${path}`, { headers: { Authorization: 'Bearer sim-guess-000000000000000000000000' }, data: method === 'GET' ? undefined : {} })).status()).toBe(401);
	}
	expect((await call('PUT', `${simulation}/observers`, { headers: reader, data: { observers: observers() } })).status()).toBe(403);
	expect((await call('POST', `${simulation}/rehearsals`, { headers: reader, data: { siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, ...evening } })).status()).toBe(403);
	// An Ariva user token is not an operator key (the permission matrix checks every role too).
	expect((await call('GET', simulation, { token: manager })).status()).toBe(401);

	// The status shows observers by number: never a user name, password or seed.
	const status = await call('GET', simulation, { headers: reader });
	expect(status.status()).toBe(200);
	expectApiSecurityHeaders(status);
	const statusText = await status.text();
	for (const entry of observers()) for (const secret of [entry.userName, entry.password, entry.totpSecret]) if (secret) expect(statusText).not.toContain(secret);
	expect((await status.json()).observers.map((o: any) => [o.observer, o.secondFactor])).toEqual([
		[1, true],
		[2, false]
	]);

	// Hostile values: refused with 400 and never echoed; nothing reaches Ariva.
	const hostile = [...sqlInjectionPayloads, ...xssPayloads];
	const refusedBodies: Record<string, unknown>[] = [
		...hostile.map((payload) => ({ siteCode: payload, campaignId: crypto.randomUUID(), dayStartUtc, ...evening })),
		...hostile.map((payload) => ({ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc: payload, ...evening })),
		...hostile.map((payload) => ({ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, ...evening, countErrorPercent: 6, countErrorLines: [payload + '\n'] })),
		// A line name is letters, digits, spaces and - _ . ( ) / only (it comes back in the report): markup is refused.
		...xssPayloads.map((payload) => ({ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, ...evening, countErrorPercent: 6, countErrorLines: [payload] })),
		{ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, ...evening, scenarioSite: xssPayloads[1] },
		{ siteCode: site, campaignId: '00000000-0000-0000-0000-000000000000', dayStartUtc, ...evening },
		{ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, fromMinute: 600, toMinute: 841 },
		{ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, ...evening, countErrorPercent: 51 },
		{ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, ...evening, tracerErrorMinutes: -31 },
		{ siteCode: site, campaignId: crypto.randomUUID(), dayStartUtc, ...evening, missedMinutesPercent: 101 }
	];
	for (const data of refusedBodies) {
		const refused = await call('POST', `${simulation}/rehearsals`, { headers: operator, data });
		expect(refused.status(), JSON.stringify(data)).toBe(400);
		const text = await refused.text();
		for (const fragment of [...markupFragments, 'OR 1=1', "OR '1'='1", 'pg_sleep', 'UNION SELECT', 'javascript:']) expect(text, JSON.stringify(data)).not.toContain(fragment);
		expectNoLeak(text, 'rehearsal refusal');
	}
	for (const raw of ['{"siteCode":', '[1,2,3]', `{"campaignId":"${xssPayloads[0].replaceAll('"', '\\"')}"}`, `{"${xssPayloads[0].replaceAll('"', '\\"')}": 1, "siteCode": 5}`]) {
		const refused = await call('POST', `${simulation}/rehearsals`, { headers: { ...operator, 'Content-Type': 'application/json' }, raw });
		expect(refused.status(), raw).toBe(400);
		for (const fragment of markupFragments) expect(await refused.text()).not.toContain(fragment);
	}
	for (const payload of hostile) {
		const truth = await call('GET', `${simulation}/truth?dayStartUtc=${encodeURIComponent(payload)}&fromMinute=${encodeURIComponent(payload)}&toMinute=1090&queues=${encodeURIComponent(payload)}`, { headers: reader });
		expect(truth.status()).toBe(400);
		const unknown = await call('GET', `${simulation}/truth?site=${encodeURIComponent(payload)}&dayStartUtc=${dayStartUtc}&fromMinute=1080&toMinute=1090`, { headers: reader });
		expect(unknown.status()).toBe(404);
		for (const text of [await truth.text(), await unknown.text()]) for (const fragment of markupFragments) expect(text).not.toContain(fragment);
	}
	const badObservers = await call('PUT', `${simulation}/observers`, { headers: operator, data: { observers: [{ userName: `${xssPayloads[0]}\u0007`, password: 'leaked-password-0001', totpSecret: 'not base32 leaked!' }] } });
	expect(badObservers.status()).toBe(400);
	for (const fragment of [...markupFragments, 'leaked']) expect(await badObservers.text()).not.toContain(fragment);

	// Bodies over the 16 KB limit: 413 before anything is read into a model.
	const padding = ' '.repeat(17 * 1024);
	const json = { ...operator, 'Content-Type': 'application/json' };
	expect((await call('POST', `${simulation}/rehearsals`, { headers: json, raw: padding + JSON.stringify({ siteCode: site }) })).status()).toBe(413);
	expect((await call('PUT', `${simulation}/observers`, { headers: json, raw: padding + JSON.stringify({ observers: observers() }) })).status()).toBe(413);

	// A campaign that does not run at the site: the observers sign in, Ariva lists no such campaign, nothing is sent.
	const nowhere = await rehearse({ campaignId: crypto.randomUUID() });
	expect(nowhere).toMatchObject({ outcome: 'NoObserverCanCapture', counts: { planned: 0, recorded: 0 } });
	expect(nowhere.observers.map((o: any) => o.state)).toEqual(['CampaignNotRunning', 'CampaignNotRunning']);
});

test('with no injected error every criterion passes, and the bins under S-17 outage are Degraded', async () => {
	test.setTimeout(180_000);
	const id = await runningCampaign(`Rehearsal ${Date.now()}`);

	const report = await rehearse({ campaignId: id });
	expect(report).toMatchObject({ campaignId: id, siteCode: site, scenarioSite: 'DMO', scenarioSeed: 9303, outcome: 'Completed', runBy: 'e2e' });
	expect(report.observers.map((o: any) => o.state), 'both observers signed in, the first with its authenticator').toEqual(['Capturing', 'Capturing']);
	expect(report.plan).toMatchObject({ lines: 4, linesWithoutTruth: 0, zones: 2, zonesWithoutTruth: 0, desks: 7, desksWithoutTruth: 0, desksNotShown: 0 });
	expect(report.counts, 'four lines, eight bins from 18:00 to 20:00').toMatchObject({ planned: 32, recorded: 32, replayed: 0, conflicts: 0, refused: 0, failed: 0, notSent: 0 });
	expect(report.tracerBatches.recorded).toBe(report.tracerBatches.planned);
	expect(report.deskBatches.recorded).toBe(report.deskBatches.planned);
	expect(report.tracerRuns).toBeGreaterThanOrEqual(6);
	expect(report.deskMinutes, 'seven desks, 120 minutes').toBe(840);
	expect(report.refusals).toEqual([]);

	// Sent again: Ariva returns what it stored for each Idempotency-Key, and stores nothing twice.
	const again = await rehearse({ campaignId: id });
	expect(again.counts).toMatchObject({ recorded: 0, replayed: 32 });
	expect(again.tracerBatches).toMatchObject({ recorded: 0, replayed: report.tracerBatches.planned });
	expect(again.deskBatches).toMatchObject({ recorded: 0, replayed: report.deskBatches.planned });
	const counts = await (await call('GET', `${campaigns}/${id}/counts?pageSize=100`, { token: manager })).json();
	expect(counts.totalCount).toBe(32);
	// The observers are two Ariva accounts, never the campaign's planner; tracers are labels, never names.
	expect(new Set(counts.data.map((c: any) => c.observerId)).size).toBe(2);
	const runs = await (await call('GET', `${campaigns}/${id}/tracer-runs?pageSize=100`, { token: manager })).json();
	expect(runs.totalCount).toBe(report.tracerRuns);
	expect(runs.data.every((r: any) => /^T-[0-9]{2,3}$/.test(r.tracerCode))).toBe(true);

	// Closed with the manager's second factor: frozen as revision 1, every criterion passes.
	const closed = await call('POST', `${campaigns}/${id}/close`, { token: manager });
	expect(closed.status(), await closed.text()).toBe(200);
	const served = await results(id);
	expect(served).toMatchObject({ status: 'Closed', revision: { number: 1 } });
	expect(verdicts(served), JSON.stringify(served.criteria)).toEqual(Object.fromEntries(criteria.map((c) => [c, 'Pass'])));
	expect(served.desks.verdict, JSON.stringify(served.desks.verdict)).toMatchObject({ criterion: 'DeskStateAgreement', verdict: 'Pass' });
	expect(served.review, JSON.stringify(served.leftOut)).not.toContain('UnusableRows');
	expect(served.review).not.toContain('PlaceholderTargets');
	for (const line of served.counts.lines) expect(line.lowestAccuracy, line.lineName).toBe(1);

	// S-17 was offline from 18:20 to 18:30: the A-VIS bin of 18:15 is Degraded on both lines (left out of the verdict, shown), every other bin Good.
	const degraded = served.counts.bins.filter((b: any) => b.standing === 'Degraded').map((b: any) => [b.lineName, b.binStartUtc]);
	expect(degraded.sort()).toEqual([
		['A-VIS entry', utc(1095)],
		['A-VIS exit', utc(1095)]
	]);
	expect(served.counts.bins.filter((b: any) => b.standing !== 'Degraded').every((b: any) => b.standing === 'Good')).toBe(true);
	expect(served.counts.lines.find((l: any) => l.lineName === 'A-VIS entry').excludedBins).toBeGreaterThanOrEqual(1);
	expect(served.trackCompletion.find((z: any) => z.queueZone === 'A-VIS').bins.degraded).toBe(1);
	expect(served.tracers.overall.excluded, 'the tracers whose wait spans the outage are set apart').toBeGreaterThanOrEqual(1);
});

test('a 6 percent count error on one line fails count accuracy for that line only', async () => {
	test.setTimeout(180_000);
	const id = await runningCampaign(`Rehearsal 6 percent ${Date.now()}`);

	const report = await rehearse({ campaignId: id, countErrorPercent: 6, countErrorLines: ['A-VIS entry'], seed: 9303 });
	expect(report).toMatchObject({ outcome: 'Completed', errors: { countErrorPercent: 6, countErrorLines: ['A-VIS entry'] }, plan: { countErrorLines: 1 } });
	expect(report.counts).toMatchObject({ planned: 32, recorded: 32, refused: 0 });

	const served = await results(id);
	const count = served.criteria.find((c: any) => c.criterion === 'CountAccuracy');
	expect(count.verdict, JSON.stringify(count)).toBe('Fail');
	expect(count.reason).toBe('NotMet');
	expect(Object.fromEntries(count.units.map((u: any) => [u.lineName, u.verdict]))).toEqual({
		'A-RES entry': 'Pass',
		'A-RES exit': 'Pass',
		'A-VIS entry': 'Fail',
		'A-VIS exit': 'Pass'
	});
	// About 1 - 6/106 per bin on that line (rounding of small bins aside), below the 95 percent target.
	expect(count.units.find((u: any) => u.lineName === 'A-VIS entry').value).toBeLessThan(0.95);
	const others = verdicts(served);
	delete others.CountAccuracy;
	expect(others, JSON.stringify(served.criteria)).toEqual(Object.fromEntries(criteria.filter((c) => c !== 'CountAccuracy').map((c) => [c, 'Pass'])));
	expect(served.review).toContain('CampaignNotClosed');
});

test('the campaign creator signed in by the simulator is refused by Ariva on every capture, and the simulator reports it', async () => {
	test.setTimeout(180_000);
	// A border shift supervisor who also holds the observer role plans and starts the campaign, then the simulator signs in as it.
	const id = await runningCampaign(`Rehearsal own ${Date.now()}`, dual);
	const own = accounts().rehearsalDual;
	try {
		await useObservers([{ userName: own.userName, password: own.password, totpSecret: null }]);
		const report = await rehearse({ campaignId: id });
		expect(report.observers.map((o: any) => o.state)).toEqual(['Capturing']);
		expect(report.counts).toMatchObject({ planned: 32, recorded: 0, refused: 32 });
		expect(report.tracerBatches.refused).toBe(report.tracerBatches.planned);
		expect(report.deskBatches.refused).toBe(report.deskBatches.planned);
		expect(report.tracerRuns).toBe(0);
		expect(report.refusals.length).toBeGreaterThan(0);
		expect(report.refusals.every((r: any) => r.status === 403)).toBe(true);
		const stored = await (await call('GET', `${campaigns}/${id}/counts?pageSize=100`, { token: manager })).json();
		expect(stored.totalCount, 'nothing is stored for the creator').toBe(0);
	} finally {
		await useObservers(observers());
	}
});
