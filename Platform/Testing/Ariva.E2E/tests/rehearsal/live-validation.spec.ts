import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, claimsOf, databaseAvailable, login, unusedTotpCode, type Account } from '../support/accounts';
import { dmoSensors, prepareDmoSensors } from '../support/demo-sensors';
import { hosts, kafkaAvailable } from '../support/hosts';
import { expectStatus } from '../support/expect-status';

// ARV-104j: the validation rehearsal against the live pipeline. ARV-104i's E2E rehearsal plants a perfect pipeline's stored
// results, so the criteria that measure Ariva's own engines pass there by construction. Here Ariva measures for itself: the
// simulator plays the reference evening (seed 9303, the demo airport DMO) through the real Ingest, Stream and Cronz hosts in
// real time, as the scripted demo does (ARV-064), from a warm-up before the rehearsed window to well past it, so the pipeline
// stays live through the rehearsal and the close. Once the window has played, the simulator's observers (two Validation
// observer accounts of DMO from the run's account seed, the first with its authenticator) capture the window's counts, tracer
// runs and desk states through the capture API, as for ARV-104i; the campaigns are closed with the manager's second factor
// and their results frozen. The test writes nothing to the database: devices, campaigns and closes go through Ariva's API,
// the ground truth through the observers' capture calls; it only reads (in a read-only transaction, with the hosts' runtime
// login) the outages Stream recorded and the availability ledger's unavailable minutes, for the record.
//
// The three outcomes of the story, from Ariva's own measurements: with no injected error every criterion meets its target;
// a 6 percent count error on `A-VIS entry` fails count accuracy for that line only; the bins under S-17's outage (18:20 to
// 18:30) are Degraded from the pipeline's own outage detection. Every assertion is soft, so one run reports every criterion
// that misses its target, and the run's record (rehearsal-record.json, attached and printed) is written before them: a failure
// here is a finding about Ariva's engines (or the harness), never something to loosen.
//
// ARIVA_DEMO_START and ARIVA_DEMO_END (demo minutes, quarter hours; default 1080 and 1170, 18:00 to 19:30) set the rehearsed
// window; the sensors play from 10 to 24 minutes before it (so that its quarter hours fall on the wall's) until an hour past
// it, and the rehearsal starts 15 minutes after it (the window's last entrants have left and its last bins are final). Run it
// alone, on fresh services: node scripts/verify.mjs rehearsal (wiki 07 section 8; docs/harness/test-cadence.md).

test.skip(!databaseAvailable || !kafkaAvailable || !process.env.ARIVA_E2E_REDIS_URL, 'the live pipeline needs the E2E database, Kafka and Redis');

const site = 'DMO';
const timeZone = 'Asia/Dubai';
const admin = `${hosts.main}/api/v1/admin`;
const campaigns = `${hosts.main}/api/v1/sites/${site}/validation/campaigns`;
const sensors = `${hosts.simulation}/api/v1/simulation/sensors`;
const validation = `${hosts.simulation}/api/v1/simulation/validation`;
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };
const reader = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_READ_KEY ?? ''}` };
const minute = 60_000;
const bin = 15 * minute;

const start = Number(process.env.ARIVA_DEMO_START ?? 1080);
const end = Number(process.env.ARIVA_DEMO_END ?? 1170);
/** Demo minutes the sensors play before the window at least (clock learning, the queues' and desks' state, the nowcast's history). */
const warmUp = 10;
/** Demo minutes after the window before the rehearsal (the default window's last entrants wait at most 7 minutes). */
const tail = 15;
/** Demo minutes the sensors play past the window: the pipeline stays live through the rehearsals and the closes. */
const hold = 60;

const zones = ['A-VIS', 'A-RES'] as const;
const lineNames = ['A-VIS entry', 'A-VIS exit', 'A-RES entry', 'A-RES exit'];
/** Arrival immigration desks: AR-05 to AR-07 on the residents' lane, AR-08 to AR-12 on the visitors' (the runbook's choice). */
const deskCodes = ['AR-05', 'AR-06', 'AR-07', 'AR-08', 'AR-09', 'AR-10', 'AR-11', 'AR-12'];
const criteria = ['CountAccuracy', 'WaitError', 'WaitBias', 'TrackCompletion', 'NowcastError', 'Availability'];
/** Compared tracer runs over both zones the campaigns ask for (as ARV-104i's rehearsal). */
const targetTracerRuns = 6;

const clock = (m: number) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
const iso = (ms: number) => new Date(ms).toISOString();
const localDate = (ms: number) => new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(ms));

interface Scope {
	version: number;
	zones: Record<string, string>;
	lines: Record<string, string>;
	desks: Record<string, string>;
}

interface Truth {
	scenarioSite: string;
	scenarioSeed: number;
	queues: { queue: string; minutes: { startUtc: string; entries: number; exits: number }[] }[];
	outages: { sensor: string; queueZone: string; fromUtc: string; toUtc: string }[];
}

/** The window laid on real time: the UTC bins [firstBin, endBin) and the rehearsal's minutes, from the instant the play laid 00:00 on. */
interface Window {
	clockDayStartUtc: string;
	firstBin: number;
	endBin: number;
	fromMinute: number;
	toMinute: number;
	offsetMs: number;
}

function checkSettings() {
	const problems = [
		...(Number.isInteger(start) && Number.isInteger(end) && start % 15 === 0 && end % 15 === 0 ? [] : ['ARIVA_DEMO_START and ARIVA_DEMO_END are quarter hours (demo minutes)']),
		...(start - warmUp - 14 >= 0 ? [] : ['ARIVA_DEMO_START leaves no warm-up']),
		...(end - start >= 30 && end - start <= 240 ? [] : ['the window is 30 to 240 minutes (two bins at least, the simulator rehearses at most 240 minutes)']),
		...(end + hold <= 1440 ? [] : ['ARIVA_DEMO_END must leave an hour of the day'])
	];
	expect(problems, 'rehearsal window').toEqual([]);
}

async function signInWithSecondFactor(account: Account): Promise<string> {
	const signedIn = await login(account.userName, account.password, undefined, undefined, { code: await unusedTotpCode(account.totpSecret!) });
	expect(signedIn.status(), `${account.userName} signs in with a second factor`).toBe(200);
	return (await signedIn.json()).accessToken as string;
}

/**
 * ARV-152 (CWE-863): the two observer accounts as the admin users API shows them: the Validation observer role and nothing
 * else, and DMO as their only site. The simulator is handed accounts that can capture at DMO and do nothing more. Returns
 * their user ids.
 */
async function expectObserversOnlyCapture(administrator: string): Promise<string[]> {
	const ids: string[] = [];
	for (const account of [accounts().liveObserver1, accounts().liveObserver2]) {
		const found = await call('GET', `${admin}/users?text=${encodeURIComponent(account.userName)}&pageSize=10`, { token: administrator });
		await expectStatus(found, 200, `${account.userName} looked up`);
		const user = ((await found.json()).data as any[]).find((u) => u.userName === account.userName);
		expect(user, `${account.userName} exists`).toBeTruthy();
		expect({ roles: user.roles, allSites: user.allSites, sites: user.sites }, `${account.userName}: Validation observer only, at DMO only`).toEqual({
			roles: ['ValidationObserver'],
			allSites: false,
			sites: [site]
		});
		ids.push(user.id as string);
	}
	return ids;
}

/**
 * Every current count of a campaign, read on one page (at most 500; the longest window has 128: four lines, 16 bins, two
 * observers), and the page must hold the total: no later page can hide a third counter.
 */
async function countsOf(manager: string, id: string): Promise<any[]> {
	const answer = await call('GET', `${campaigns}/${id}/counts?pageSize=500`, { token: manager });
	await expectStatus(answer, 200, "the campaign's counts");
	const listed = await answer.json();
	expect(listed.data.length, 'every count of the campaign on one page').toBe(listed.totalCount);
	return listed.data;
}

async function sensorStatus(): Promise<any> {
	const answer = await call('GET', sensors, { headers: operator });
	expect(answer.status()).toBe(200);
	return answer.json();
}

/** Waits until the sensors have pushed demo minute <lastMinute>; fails at once when the play stops before it. */
async function waitUntilPlayed(lastMinute: number, minutes: number): Promise<void> {
	const until = Date.now() + minutes * minute;
	for (;;) {
		const status = await sensorStatus();
		if (status.nextMinute > lastMinute) return;
		if (!status.running) throw new Error(`the play stopped at ${clock(status.nextMinute)}, before ${clock(lastMinute)}`);
		if (Date.now() > until) throw new Error(`the evening did not reach ${clock(lastMinute)} in ${minutes} minutes`);
		await new Promise((resolve) => setTimeout(resolve, 30_000));
	}
}

/** DMO's published profile version, the two queue zones, their entry and exit lines, and the eight desks, by id. */
async function readScope(token: string): Promise<Scope> {
	const list = async (path: string) => {
		const answer = await call('GET', `${admin}/${path}`, { token });
		await expectStatus(answer, 200, path);
		const body = await answer.json();
		return (body.data ?? body) as any[];
	};
	const published = (await list(`zone-profiles?siteCode=${site}`)).find((p) => p.status === 'Published');
	expect(published, 'DMO has a published zone profile (the demo seed)').toBeDefined();
	const profile = await (await call('GET', `${admin}/zone-profiles/${published.id}`, { token })).json();
	const immigration = (await list(`checkpoints?siteCode=${site}&pageSize=100`)).find((c) => c.code === 'IMM');
	const desks = await list(`desks?parentId=${immigration.id}&pageSize=100`);
	const pick = (items: any[], names: readonly string[], key: string) =>
		Object.fromEntries(names.map((name) => {
			const found = items.find((item) => item[key] === name);
			expect(found, `${name} in the DMO seed`).toBeDefined();
			return [name, found.id as string];
		}));
	return { version: published.version, zones: pick(profile.zones, zones, 'name'), lines: pick(profile.lines, lineNames, 'name'), desks: pick(desks, deskCodes, 'code') };
}

/** The simulator's truth over the window: per queue and minute the people in and out, and the sensor outages. */
async function readTruth(window: Window): Promise<Truth> {
	const answer = await call('GET', `${validation}/truth?site=${site}&dayStartUtc=${window.clockDayStartUtc}&fromMinute=${start - 1}&toMinute=${end + 1}&queues=${zones.join(',')}`, {
		headers: reader
	});
	await expectStatus(answer, 200);
	return answer.json();
}

/**
 * A scenario outage in Ariva's terms: whole minutes. The emulator lays the day from about a second past a minute, so its
 * outage runs from and to a second past the minute, while Stream records an outage from the minute the device fell silent
 * to the start of the minute the first push after it covers (owner decision on F4, 2026-10-10, ARV-148): the bin after the
 * outage is judged again. Rounding both ends down to their minute is that rule, not a tolerance.
 */
function inMinutes(o: { fromUtc: string; toUtc: string }): { from: number; to: number } {
	const from = Date.parse(o.fromUtc);
	const to = Date.parse(o.toUtc);
	return { from: from - (from % minute), to: to - (to % minute) };
}

/**
 * The bins a count can be judged in, per line: whole UTC bins of the window in which people crossed the line (with nobody
 * there is no accuracy, F18) and outside the scenario's outages of the line's zone (Degraded, set apart). The campaigns ask
 * for that many judged bins on every line (the smallest over the lines): every such bin of the scarcest line must be judged.
 */
function judgeableBins(truth: Truth, window: Window): Record<string, number[]> {
	const bins: Record<string, number[]> = {};
	for (const queue of truth.queues) {
		for (const [line, pick] of [[`${queue.queue} entry`, 'entries'], [`${queue.queue} exit`, 'exits']] as const) {
			const people = new Map<number, number>();
			for (const m of queue.minutes) {
				const middle = Date.parse(m.startUtc) + 30_000;
				if (middle < window.firstBin || middle >= window.endBin) continue;
				const binStart = middle - (middle % bin);
				people.set(binStart, (people.get(binStart) ?? 0) + m[pick]);
			}
			bins[line] = [...people.entries()]
				.filter(([binStart, count]) => count > 0 && !truth.outages.some((o) => o.queueZone === queue.queue && binStart < inMinutes(o).to && binStart + bin > inMinutes(o).from))
				.map(([binStart]) => binStart)
				.sort((a, b) => a - b);
		}
	}
	return bins;
}

async function runningCampaign(manager: string, scope: Scope, name: string, days: string[], targetBinsPerLine: number): Promise<string> {
	const created = await call('POST', campaigns, {
		token: manager,
		data: {
			name,
			profileVersion: scope.version,
			zoneIds: Object.values(scope.zones),
			lineIds: Object.values(scope.lines),
			days,
			targetBinsPerLine,
			targetTracerRuns,
			deskIds: Object.values(scope.desks)
		}
	});
	await expectStatus(created, 201);
	const id = (await created.json()).id as string;
	const started = await call('POST', `${campaigns}/${id}/start`, { token: manager });
	await expectStatus(started, 200);
	return id;
}

async function rehearse(window: Window, body: Record<string, unknown>): Promise<any> {
	const answer = await call('POST', `${validation}/rehearsals`, {
		headers: operator,
		data: { siteCode: site, scenarioSite: site, scenarioSeed: 9303, dayStartUtc: window.clockDayStartUtc, fromMinute: window.fromMinute, toMinute: window.toMinute, ...body }
	});
	await expectStatus(answer, 200);
	return answer.json();
}

/** Closes a campaign with the manager's fresh second factor (closing is critical) and reads its frozen results (revision 1). */
async function closeAndFreeze(id: string): Promise<any> {
	const manager = await signInWithSecondFactor(accounts().liveRehearsalManager);
	const closed = await call('POST', `${campaigns}/${id}/close`, { token: manager });
	await expectStatus(closed, 200);
	let served: any;
	await expect
		.poll(
			async () => {
				const answer = await call('GET', `${campaigns}/${id}/results`, { token: manager });
				await expectStatus(answer, 200);
				served = await answer.json();
				return served.revision?.number ?? 0;
			},
			{ message: 'the results are frozen as revision 1 at the close', timeout: 5 * minute, intervals: [5_000] }
		)
		.toBe(1);
	return served;
}

/** The four tables the record reads; the runtime role may select from each of them. */
const pipelineTables = ['zone_outage', 'availability_minute', 'queue_bin', 'queue_minute'] as const;

/**
 * ARV-152 (CWE-269): the record is read with the hosts' runtime login (Database__Username and Database__Password, a member of
 * ariva_runtime only), never the migration login that owns the schema. ariva_runtime may select from every table the
 * migration login creates (script 0001's default privileges), and no script takes SELECT on zone_outage (0019),
 * availability_minute (0042), queue_bin or queue_minute (0018) back from it (they revoke only deletes, truncates and, for
 * some, updates), so the runtime login reads all four.
 */
function runtimeLogin(): pg.ClientConfig {
	const user = process.env.Database__Username ?? '';
	const password = process.env.Database__Password ?? '';
	expect(user.length > 0 && password.length > 0, 'the record is read with the runtime login: Database__Username and Database__Password are set').toBe(true);
	expect(user, 'the runtime login, never the migration login').not.toBe(process.env.Database__Migration__Username || 'postgres');
	return {
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user,
		password
	};
}

/** Before the evening plays: the runtime login connects and may read each table the record reads, in a read-only transaction. */
async function expectRuntimeLoginReads(): Promise<void> {
	const db = new pg.Client(runtimeLogin());
	await db.connect();
	try {
		await db.query('BEGIN READ ONLY');
		// A plain login: no superuser, no row level security bypass, no role creation (CWE-269).
		const attributes = await db.query('SELECT rolsuper, rolbypassrls, rolcreaterole FROM pg_roles WHERE rolname = current_user');
		expect(attributes.rows[0], 'the runtime login is neither a superuser nor bypasses row level security nor creates roles').toEqual({
			rolsuper: false,
			rolbypassrls: false,
			rolcreaterole: false
		});
		for (const table of pipelineTables) {
			const allowed = await db.query('SELECT has_table_privilege(current_user, $1, \'SELECT\') AS allowed', [table]);
			expect(allowed.rows[0].allowed, `the runtime login may select from ${table}`).toBe(true);
		}
		await db.query('COMMIT');
	} finally {
		await db.end();
	}
}

/** Reads, in a read-only transaction with the runtime login, what Stream and Cronz recorded for DMO over the play: the device outages and the minutes not available. */
async function readPipelineRecords(fromMs: number, toMs: number) {
	const db = new pg.Client(runtimeLogin());
	await db.connect();
	try {
		await db.query('BEGIN READ ONLY');
		const outages = await db.query(
			`SELECT zone_key, device_code, from_utc, to_utc, closed FROM zone_outage
			  WHERE zone_key LIKE 'DMO/%' AND from_utc < $2 AND to_utc > $1 ORDER BY from_utc, zone_key, device_code LIMIT 201`,
			[iso(fromMs), iso(toMs)]
		);
		// A read that reaches its bound fails rather than giving a list cut short ("no other device was out" reads it whole).
		expect(outages.rows.length, 'the outages read whole (at most 200)').toBeLessThanOrEqual(200);
		const unavailable = await db.query(
			`SELECT minute_utc, state, calendar, reasons, zones_expected, zones_stale, zones_missing, zones_lagging FROM availability_minute
			  WHERE site_code = 'DMO' AND state <> 'Available' ORDER BY minute_utc LIMIT 501`
		);
		expect(unavailable.rows.length, 'the unavailable minutes read whole (at most 500)').toBeLessThanOrEqual(500);
		const decided = await db.query(`SELECT min(minute_utc) AS first, max(minute_utc) AS last, count(*)::int AS minutes FROM availability_minute WHERE site_code = 'DMO'`);
		// The rehearsed zones' stored bins (every revision): the crossings counted and those the stream took as late.
		const bins = await db.query(
			`SELECT zone_key, start_utc, revision, status, quality, entries, exits, late_events, fragmented, open_people FROM queue_bin
			  WHERE zone_key = ANY($1) AND start_utc >= $2 AND start_utc < $3 ORDER BY zone_key, start_utc, revision LIMIT 200`,
			[zones.map((z) => `${site}/${z}`), iso(fromMs), iso(toMs)]
		);
		const firstLive = await db.query(`SELECT min(minute_utc) AS first FROM queue_minute WHERE zone_key LIKE 'DMO/%'`);
		await db.query('COMMIT');
		return {
			outages: outages.rows.map((r) => ({ zone: r.zone_key, device: r.device_code, fromUtc: iso(r.from_utc.getTime()), toUtc: iso(r.to_utc.getTime()), closed: r.closed })),
			queueBins: bins.rows.map((r) => ({
				zone: r.zone_key,
				startUtc: iso(r.start_utc.getTime()),
				revision: r.revision,
				status: r.status,
				quality: r.quality,
				entries: Number(r.entries),
				exits: Number(r.exits),
				lateEvents: Number(r.late_events),
				fragmented: Number(r.fragmented),
				openPeople: Number(r.open_people)
			})),
			firstQueueMinuteUtc: firstLive.rows[0].first ? iso(firstLive.rows[0].first.getTime()) : null,
			availabilityLedger: {
				firstMinuteUtc: decided.rows[0].first ? iso(decided.rows[0].first.getTime()) : null,
				lastMinuteUtc: decided.rows[0].last ? iso(decided.rows[0].last.getTime()) : null,
				minutes: decided.rows[0].minutes,
				notAvailable: unavailable.rows.map((r) => ({
					minuteUtc: iso(r.minute_utc.getTime()),
					state: r.state,
					calendar: r.calendar,
					reasons: r.reasons,
					zones: { expected: r.zones_expected, stale: r.zones_stale, missing: r.zones_missing, lagging: r.zones_lagging }
				}))
			}
		};
	} finally {
		await db.end();
	}
}

/** What the record keeps of a campaign's frozen results: the hash, every criterion, and the comparison behind each. */
function resultsRecord(served: any) {
	const round = (v: number | null | undefined, digits = 4) => (v === null || v === undefined ? null : Number(v.toFixed(digits)));
	return {
		status: served.status,
		revision: served.revision,
		profileVersion: served.profileVersion,
		geometryHash: served.geometryHash,
		plannedDays: served.plannedDays,
		targets: served.targets,
		criteria: served.criteria.map((c: any) => ({
			criterion: c.criterion,
			verdict: c.verdict,
			reason: c.reason,
			value: round(c.value),
			judgedValue: round(c.judgedValue),
			target: c.target,
			judged: c.judged,
			required: c.required,
			excluded: c.excluded,
			units: (c.units ?? []).map((u: any) => ({ queueZone: u.queueZone, lineName: u.lineName, value: round(u.value), judged: u.judged, required: u.required, excluded: u.excluded, verdict: u.verdict }))
		})),
		deskStateAgreement: served.desks
			? {
					verdict: served.desks.verdict,
					overall: served.desks.overall,
					desks: served.desks.desks.map((d: any) => ({ desk: d.deskCode, minutes: d.minutes, judged: d.judged, agreeing: d.agreeing, strictAgreement: round(d.strictAgreement), confusion: d.confusion }))
				}
			: null,
		review: served.review,
		lines: served.counts.lines.map((l: any) => ({ line: l.lineName, judgedBins: l.judgedBins, excludedBins: l.excludedBins, lowestAccuracy: round(l.lowestAccuracy), pooledAccuracy: round(l.pooledAccuracy) })),
		bins: served.counts.bins.map((b: any) => ({ line: b.lineName, binStartUtc: b.binStartUtc, standing: b.standing, manual: b.manualCount, system: b.systemCount, accuracy: round(b.accuracy) })),
		tracers: { overall: served.tracers.overall, zones: served.tracers.zones },
		trackCompletion: served.trackCompletion,
		nowcast: { overall: served.nowcast.overall?.published, zones: served.nowcast.zones.map((z: any) => ({ queueZone: z.queueZone, published: z.published })), coverage: served.nowcast.coverageOverall },
		availability: served.availability.total,
		leftOut: served.leftOut
	};
}

const verdicts = (served: any) => Object.fromEntries(served.criteria.map((c: any) => [c.criterion, c.verdict]));
const describe = (c: any) => `${c.criterion}: ${c.verdict} (${c.reason}), value ${c.value}, target ${c.target}, judged ${c.judged} of ${c.required}, excluded ${c.excluded}`;

test.afterAll(async () => {
	await call('POST', `${sensors}/pause`, { headers: operator });
	await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: [] } });
});

test('the reference evening through the live pipeline: every criterion from Ariva\'s own measurements, a 6 percent count error, and S-17\'s outage', async ({}, info) => {
	test.setTimeout(4 * 60 * minute);
	checkSettings();
	const record: Record<string, any> = { story: 'ARV-104j', site, scenario: { site, seed: 9303 }, window: { from: clock(start), to: clock(end) } };
	try {
		record.commit = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
		record.uncommittedChanges = execFileSync('git', ['status', '--porcelain', '--untracked-files=no'], { encoding: 'utf8' }).trim().length > 0;
	} catch {
		record.commit = null;
	}

	// 0. Before two hours of play: the runtime login reads the record's tables, and the observers can only capture at DMO.
	await expectRuntimeLoginReads();
	const administrator = await signInWithSecondFactor(accounts().demoAdmin);
	await expectObserversOnlyCapture(administrator);

	// 1. The sensors: every DMO sensor registered and calibrated through the admin API, loaded into the sensor emulator.
	const devices = await prepareDmoSensors(administrator, dmoSensors);
	const loaded = await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices } });
	await expectStatus(loaded, 200);
	const scope = await readScope(administrator);
	// Ingest keeps device records for a minute: let it see the calibrations before the first push.
	await new Promise((resolve) => setTimeout(resolve, 65_000));

	// 2. The play: start on the next whole minute with the warm-up that puts the window's quarter hours on the wall's (Ariva's
	// bins are UTC quarter hours), and play an hour past the window so the pipeline is live through the closes.
	await new Promise((resolve) => setTimeout(resolve, minute - (Date.now() % minute) + 1_000));
	const wall = new Date().getUTCHours() * 60 + new Date().getUTCMinutes();
	let preRoll = ((start - wall) % 15 + 15) % 15;
	while (preRoll < warmUp) preRoll += 15;
	const playFrom = start - preRoll;
	const started = await call('POST', `${sensors}/start`, { headers: operator, data: { minute: playFrom, speed: 1, untilMinute: end + hold } });
	await expectStatus(started, 200);
	record.play = { from: clock(playFrom), until: clock(end + hold), startedUtc: new Date().toISOString(), warmUpMinutes: preRoll };

	// 3. Wait until the evening has played the window and the tail.
	await waitUntilPlayed(end + tail, preRoll + end - start + tail + 10);

	const status = await sensorStatus();
	record.pipeline = {
		devices: status.devices.map((d: any) => ({ sensor: d.sensor, pushes: d.pushes, expectedEvents: d.expectedEvents, acceptedEvents: d.acceptedEvents, failures: d.failures }))
	};
	// Ingest took everything the sensors sent (S-17 sends nothing while offline, which is no failure).
	for (const device of status.devices.filter((d: any) => d.site === site))
		expect.soft([device.sensor, device.failures, device.acceptedEvents], `${device.sensor}: pushes refused or events dropped by Ingest`).toEqual([device.sensor, 0, device.expectedEvents]);

	// The instant the play laid the scenario's 00:00 on (speed 1): the observers' truth is laid on it.
	const played = await (await call('GET', validation, { headers: reader })).json();
	expect(played.clockDayStartUtc, 'the sensor emulator played at speed 1').toBeTruthy();
	const atStart = Date.parse(played.clockDayStartUtc) + start * minute;
	const firstBin = Math.round(atStart / bin) * bin;
	const offsetMs = atStart - firstBin;
	expect(Math.abs(offsetMs), "the window's first quarter hour falls on the wall's (within a minute)").toBeLessThan(minute);
	// The rehearsal plans the bins wholly inside its window (RehearsalPlanner.BinsWithin). The play starts a second or so after a
	// whole minute, so the scenario's 18:00 is a second past the wall's quarter hour: the window then starts a minute early to
	// keep its first bin (and ends a minute late when the play started before the minute).
	const window: Window = {
		clockDayStartUtc: played.clockDayStartUtc,
		firstBin,
		endBin: firstBin + (end - start) * minute,
		fromMinute: offsetMs > 0 ? start - 1 : start,
		toMinute: offsetMs < 0 ? end + 1 : end,
		offsetMs
	};
	record.clock = { clockDayStartUtc: window.clockDayStartUtc, offsetMs, binsUtc: [iso(window.firstBin), iso(window.endBin)], rehearsalMinutes: [window.fromMinute, window.toMinute] };

	// 4. The campaigns' scope and targets: the two queue zones, their entry and exit lines, eight desks, the local day(s) of the
	// window, every judgeable bin of the scarcest line, and six tracer runs.
	const truth = await readTruth(window);
	expect([truth.scenarioSite, truth.scenarioSeed], 'the reference day').toEqual([site, 9303]);
	const judgeable = judgeableBins(truth, window);
	const targetBinsPerLine = Math.min(...lineNames.map((l) => judgeable[l].length));
	expect(targetBinsPerLine, 'every line has a bin with people outside the outage').toBeGreaterThan(0);
	const days = [...new Set([localDate(window.firstBin), localDate(window.endBin - 1)])];
	const outage = truth.outages.find((o) => o.sensor === 'S-17');
	const outageBins = outage
		? Array.from({ length: (window.endBin - window.firstBin) / bin }, (_, i) => window.firstBin + i * bin).filter(
				(b) => b < inMinutes(outage).to && b + bin > inMinutes(outage).from
			)
		: [];
	record.campaignScope = {
		zones,
		lines: lineNames,
		desks: deskCodes,
		days,
		targetBinsPerLine,
		targetTracerRuns,
		judgeableBins: Object.fromEntries(Object.entries(judgeable).map(([line, list]) => [line, list.map(iso)])),
		scenarioOutages: truth.outages
	};

	// 5. The observers: Ariva accounts from the run's seed, never the account that plans, starts or closes the campaigns. Read
	// again through the admin users API right before the simulator gets them (a fresh sign-in: the first token has expired).
	const observerIds = await expectObserversOnlyCapture(await signInWithSecondFactor(accounts().demoAdmin));
	const observers = [accounts().liveObserver1, accounts().liveObserver2].map((a) => ({ userName: a.userName, password: a.password, totpSecret: a.totpSecret ?? null }));
	const put = await call('PUT', `${validation}/observers`, { headers: operator, data: { observers } });
	expect(put.status(), 'the observers are given to the simulator').toBe(200);
	const putText = await put.text();
	for (const entry of observers) for (const secret of [entry.userName, entry.password, entry.totpSecret]) if (secret) expect(putText, 'no credential comes back').not.toContain(secret);

	const manager = await signInWithSecondFactor(accounts().liveRehearsalManager);
	const managerId = claimsOf(manager).sub as string;
	const day = days.join(' and ');
	record.campaigns = [];

	// 6. No injected error.
	const exact = await runningCampaign(manager, scope, `Live rehearsal ${day} ${clock(start)} no error`, days, targetBinsPerLine);
	const exactReport = await rehearse(window, { campaignId: exact });
	const exactCounts = await countsOf(manager, exact);
	const exactServed = await closeAndFreeze(exact);
	record.campaigns.push({ name: 'no injected error', campaignId: exact, errors: null, report: reportRecord(exactReport), results: resultsRecord(exactServed) });

	// 7. A 6 percent count error on one line.
	const skewed = await runningCampaign(manager, scope, `Live rehearsal ${day} ${clock(start)} 6 percent`, days, targetBinsPerLine);
	const skewedReport = await rehearse(window, { campaignId: skewed, countErrorPercent: 6, countErrorLines: ['A-VIS entry'], seed: 9303 });
	const skewedCounts = await countsOf(manager, skewed);
	const skewedServed = await closeAndFreeze(skewed);
	record.campaigns.push({ name: '6 percent count error on A-VIS entry', campaignId: skewed, errors: { countErrorPercent: 6, countErrorLines: ['A-VIS entry'] }, report: reportRecord(skewedReport), results: resultsRecord(skewedServed) });

	// 8. The record, before any assertion on the results: what Stream and Cronz recorded beside the frozen results.
	Object.assign(record.pipeline, await readPipelineRecords(window.firstBin - (preRoll + 5) * minute, window.endBin + hold * minute));
	record.recordedUtc = new Date().toISOString();
	const text = JSON.stringify(record, null, 2);
	fs.writeFileSync(info.outputPath('rehearsal-record.json'), text);
	await info.attach('rehearsal-record', { body: text, contentType: 'application/json' });
	console.log(`rehearsal record (${info.outputPath('rehearsal-record.json')}):`);
	for (const campaign of record.campaigns) {
		console.log(`  ${campaign.name}: revision ${campaign.results.revision?.number} sha256 ${campaign.results.revision?.contentSha256}`);
		for (const c of campaign.results.criteria) console.log(`    ${describe(c)}`);
		if (campaign.results.deskStateAgreement) console.log(`    desks: ${describe(campaign.results.deskStateAgreement.verdict)}`);
	}

	// The observers' side: both observers signed in (the first with its authenticator), everything planned was recorded.
	for (const [name, report] of [['no injected error', exactReport], ['6 percent', skewedReport]] as const) {
		expect.soft(report.outcome, name).toBe('Completed');
		expect.soft(report.observers.map((o: any) => o.state), `${name}: both observers capture`).toEqual(['Capturing', 'Capturing']);
		expect.soft(report.plan, name).toMatchObject({ lines: 4, linesWithoutTruth: 0, zones: 2, zonesWithoutTruth: 0, desks: deskCodes.length, desksWithoutTruth: 0, desksNotShown: 0 });
		expect.soft(report.counts, `${name}: every count recorded`).toMatchObject({ planned: lineNames.length * ((end - start) / 15), recorded: lineNames.length * ((end - start) / 15), refused: 0, failed: 0, notSent: 0 });
		expect.soft(report.tracerBatches.recorded, `${name}: every tracer batch recorded`).toBe(report.tracerBatches.planned);
		expect.soft(report.deskBatches.recorded, `${name}: every desk batch recorded`).toBe(report.deskBatches.planned);
		expect.soft(report.deskMinutes, `${name}: eight desks, every minute of the window`).toBe(deskCodes.length * (end - start));
		expect.soft(report.refusals, name).toEqual([]);
	}
	// The ground truth is independent on both campaigns: the two observer accounts counted, never the account that planned,
	// started and closed the campaign.
	for (const [name, counts] of [['no injected error', exactCounts], ['6 percent', skewedCounts]] as const) {
		const counted = new Set<string>(counts.map((c: any) => c.observerId));
		expect.soft([...counted].sort(), `${name}: the two observer accounts counted`).toEqual([...observerIds].sort());
		expect.soft(counted.has(managerId), `${name}: the campaign manager never captures`).toBe(false);
	}

	// No injected error: every criterion meets its target from Ariva's own measurements, frozen at the close.
	expect.soft(exactServed, 'frozen at the close').toMatchObject({ status: 'Closed', revision: { number: 1 } });
	expect.soft(exactServed.revision?.contentSha256 ?? '', 'the frozen document has its SHA-256').toMatch(/^[0-9a-f]{64}$/);
	for (const c of exactServed.criteria) if (criteria.includes(c.criterion)) expect.soft(c.verdict, describe(c)).toBe('Pass');
	expect.soft(exactServed.criteria.map((c: any) => c.criterion).sort(), 'every criterion judged').toEqual([...criteria].sort());
	expect.soft(exactServed.desks?.verdict?.verdict, exactServed.desks?.verdict ? describe(exactServed.desks.verdict) : 'no desk section').toBe('Pass');
	expect.soft(exactServed.review, 'no stored row refused').not.toContain('UnusableRows');
	expect.soft(exactServed.review, 'the targets are the campaign\'s own').not.toContain('PlaceholderTargets');
	// The window's results are final (the pipeline plays on through the close, so the planned day's latest bins are not: the
	// review may say ProvisionalResults for them, outside the window).
	expect.soft(exactServed.tracers.overall.standings.provisional, 'every tracer of the window compared with final results').toBe(0);

	// S-17's outage, from the pipeline's own outage detection: Stream recorded it, and the bins under it are Degraded on both
	// A-VIS lines and set apart; nothing at A-RES is, and every other bin is Good.
	const recordedOutages = record.pipeline.outages as { zone: string; device: string; fromUtc: string; toUtc: string }[];
	// A window over 18:20 to 18:30 must hold the scenario's S-17 outage: its checks are never skipped silently.
	if (window.fromMinute <= 1100 && window.toMinute >= 1110) expect(outage, "the scenario's S-17 outage in this window").toBeTruthy();
	if (outage && outageBins.length > 0) {
		const s17 = recordedOutages.filter((o) => o.device === 'S-17' && o.zone === `${site}/A-VIS`);
		expect.soft(s17.length, 'Stream recorded S-17 out over A-VIS').toBeGreaterThan(0);
		expect
			.soft(s17.some((o) => Date.parse(o.fromUtc) <= inMinutes(outage).from && Date.parse(o.toUtc) >= inMinutes(outage).to), `S-17's recorded outage covers the scenario's ${iso(inMinutes(outage).from)} to ${iso(inMinutes(outage).to)} (${outage.fromUtc} to ${outage.toUtc} in whole minutes)`)
			.toBe(true);
		for (const served of [exactServed, skewedServed]) {
			for (const line of ['A-VIS entry', 'A-VIS exit']) {
				for (const b of outageBins) {
					const found = served.counts.bins.find((x: any) => x.lineName === line && Date.parse(x.binStartUtc) === b);
					expect.soft(found?.standing, `${line} ${iso(b)} under S-17's outage`).toBe('Degraded');
				}
				// F4 (owner, 2026-10-10): the outage ends at the start of the minute the first push after it covers, so the bin
				// that starts there is judged again, not set apart.
				const after = Math.ceil(inMinutes(outage).to / bin) * bin; // the first bin starting at or after the outage's end
				if (after >= window.firstBin && after < window.endBin) {
					const next = served.counts.bins.find((x: any) => x.lineName === line && Date.parse(x.binStartUtc) === after);
					expect.soft(next?.standing, `${line} ${iso(after)}, the bin after S-17's outage, is judged again`).toBe('Good');
				}
			}
		}
		expect.soft(exactServed.trackCompletion.find((z: any) => z.queueZone === 'A-VIS')?.bins?.degraded ?? 0, 'the A-VIS health bin under the outage is Degraded').toBeGreaterThanOrEqual(1);
	} else test.info().annotations.push({ type: 'note', description: 'the window leaves out S-17\'s outage (18:20 to 18:30): its bins are not checked' });
	expect.soft(recordedOutages.filter((o) => o.device !== 'S-17').map((o) => `${o.device} ${o.zone} ${o.fromUtc} to ${o.toUtc}`), 'no other device was out').toEqual([]);
	const notGood = exactServed.counts.bins.filter((b: any) => b.standing !== 'Good');
	for (const b of notGood) {
		const binStart = Date.parse(b.binStartUtc);
		const zone = b.lineName.startsWith('A-VIS') ? `${site}/A-VIS` : `${site}/A-RES`;
		const underOutage = b.standing === 'Degraded' && recordedOutages.some((o) => o.zone === zone && binStart < Date.parse(o.toUtc) && binStart + bin > Date.parse(o.fromUtc));
		expect.soft(underOutage, `${b.lineName} ${b.binStartUtc} is ${b.standing}: only a bin under an outage Stream recorded is set apart`).toBe(true);
	}
	if (outage && outageBins.length > 0)
		expect.soft(exactServed.tracers.overall.excluded, 'the tracers whose wait spans the outage are set apart').toBeGreaterThanOrEqual(1);

	// A 6 percent count error on A-VIS entry: count accuracy fails for that line only; the other criteria as without error.
	const count = skewedServed.criteria.find((c: any) => c.criterion === 'CountAccuracy');
	expect.soft(count?.verdict, describe(count)).toBe('Fail');
	expect.soft(count?.reason).toBe('NotMet');
	expect.soft(Object.fromEntries((count?.units ?? []).map((u: any) => [u.lineName, u.verdict])), 'count accuracy per line').toEqual({
		'A-RES entry': 'Pass',
		'A-RES exit': 'Pass',
		'A-VIS entry': 'Fail',
		'A-VIS exit': 'Pass'
	});
	expect.soft(count?.units?.find((u: any) => u.lineName === 'A-VIS entry')?.value, 'below the 95 percent target on that line').toBeLessThan(0.95);
	const others = verdicts(skewedServed);
	delete others.CountAccuracy;
	const othersWithout = verdicts(exactServed);
	delete othersWithout.CountAccuracy;
	expect.soft(others, 'every other criterion as without the error').toEqual(othersWithout);
	expect.soft(skewedServed.revision?.contentSha256 ?? '', 'frozen too').toMatch(/^[0-9a-f]{64}$/);
});

/** What the record keeps of a rehearsal's report: the tallies, the plan and the observers' states (no account, no credential). */
function reportRecord(report: any) {
	return {
		outcome: report.outcome,
		observers: report.observers,
		plan: report.plan,
		counts: report.counts,
		tracerBatches: report.tracerBatches,
		deskBatches: report.deskBatches,
		tracerRuns: report.tracerRuns,
		deskMinutes: report.deskMinutes,
		refusals: report.refusals
	};
}
