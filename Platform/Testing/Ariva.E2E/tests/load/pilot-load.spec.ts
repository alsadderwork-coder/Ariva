import { expect, test } from '@playwright/test';
import { createClient } from 'redis';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { accounts, call, databaseAvailable } from '../support/accounts';
import { expectStatus } from '../support/expect-status';
import { hosts, kafkaAvailable } from '../support/hosts';
import { createAirport } from '../support/airports';
import { randomLetters, screenSessions, secondFactorSignIn } from '../support/load-site';
import { staffingDatabase } from '../support/staffing';

// ARV-124a: the pilot hall through the whole pipeline, alone and in real time (ARIVA_E2E_PILOT_LOAD=1 --project=pilot-load,
// or node scripts/verify.mjs pilot-load; Ariva.Api.Stream runs in this mode only). This spec builds what the pilot site would
// have, as an installer would through the admin API: one arrivals hall with an immigration checkpoint, five queue zones
// (CIT, RES, VIS, CRW and the e-gates' EG) with their entry and exit lines, 20 desks and 6 e-gates mapped to AMAN's codes,
// 30 T3 sensors (6 a zone), 70 more for the burst on 11 zones of their own (6 or 7 a zone, as the D5 site's 100 spread over
// its halls), all calibrated, 10 displays and the sessions of 50 dashboards. AUH-TA's 84 seeded sensors play their evening beside it (the simulator, in real time), so Stream also holds
// the illustrative site's zones. Then the harness (Ariva.LoadTests, ARIVA_LOAD_MODE=pilot) runs the hall at its rate, the
// burst at 15,000 positions a second and the recovery, and this spec reads what the run left once Stream has settled: the
// entries and exits Stream counted against the crossings Ingest accepted, each zone's stream state
// (stream_zone_state.state_bytes), each site's desk feed state, the outbox, the archive's rows, Redis, and every staffing
// run's duration (TickerQ's own line in Ariva.Api.Cronz's log). The record is pilot-report.md and pilot-measurements.json, attached to the
// report, for wiki 05's sizing notes (lab measurements). The second test judges Stream's capacity on the same run (it fails
// until ARV-230 and ARV-232). Credentials reach the harness in its environment only.

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
test.skip(!databaseAvailable || !kafkaAvailable || !redisUrl, 'the pilot run needs the E2E database, Kafka and Redis');
test.describe.configure({ mode: 'serial', retries: 0 });

const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..', '..', '..', 'Ariva.LoadTests', 'bin', process.env.ARIVA_E2E_CONFIGURATION || 'Debug', 'net10.0', 'Ariva.LoadTests.dll');
const output = path.resolve(here, '..', '..', '..', '..', '..', '.verify', 'load', 'pilot');
const admin = `${hosts.main}/api/v1/admin`;
const sensors = `${hosts.simulation}/api/v1/simulation/sensors`;
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };

/** The hall's queue zones, side by side on the level (24 x 12 m each, 30 m apart), with their lanes and desks (Proposed). */
const lanes = [
	{ lane: 'CIT', desks: 5 },
	{ lane: 'RES', desks: 5 },
	{ lane: 'VIS', desks: 7 },
	{ lane: 'CRW', desks: 3 },
	{ lane: 'EG', desks: 0 }
];
const gates = 6;
const pilotPerZone = 6;
/** The burst's 70 sensors on zones of their own: 7, 7, 7, 7, then 6 on each of the other seven. */
const burstZones = 11;
const burstSensors = 70;
const screens = 50;
const displays = 10;

interface PilotDevice {
	zone: string;
	key: string;
	burst: boolean;
}

let site = '';
let hallZones: string[] = [];
let allZones: string[] = [];
let token = '';
const deviceIds: string[] = [];
const displayIds: string[] = [];
const auhIds: string[] = [];
let pilotJson = '';
let screenTokens: string[] = [];
const displayKeys: string[] = [];
const displayCodes: string[] = [];
let runStarted = new Date();
// What the run left, for the second test (serial: it runs only after the first).
let report: any;
let measurements: any;

test.beforeAll(async () => {
	test.setTimeout(20 * 60_000);
	token = await secondFactorSignIn(accounts().loadAdmin);
	const create = async (resource: string, data: unknown) => {
		const response = await call('POST', `${admin}/${resource}`, { token, data });
		await expectStatus(response, 201, resource);
		return response.json();
	};
	// A site of its own each run (PL and four letters), so reruns never share a zone.
	site = `PL${randomLetters(4)}`;
	await expectStatus(await call('POST', `${admin}/sites`, { token, data: { code: site, name: `Pilot hall ${site}` } }), 201, 'site');
	const { id: airport } = await createAirport((iataCode) =>
		call('POST', `${admin}/airports`, { token, data: { iataCode, name: `E2E pilot ${site}`, timeZoneId: 'Asia/Dubai' } })
	);
	const terminal = (await create('terminals', { airportId: airport, code: 'T1', name: 'T1', siteCode: site })).id as string;
	const hall = (await create('levels', { terminalId: terminal, code: 'ARR', name: 'Arrivals hall', floorNumber: 0, widthMetres: 500, depthMetres: 40 })).id as string;
	const immigration = (await create('checkpoints', { levelId: hall, code: 'IMM', name: 'Arrival immigration', kind: 'Immigration' })).id as string;

	// 20 desks on AMAN's codes IN01 to IN20, 6 e-gates on EG1 to EG6, each mapped as the site's AMAN connector would name them.
	const desks: { code: string; lane: string }[] = [];
	let n = 0;
	for (const { lane, desks: count } of lanes) {
		for (let i = 0; i < count; i++) {
			n++;
			const code = `IN${String(n).padStart(2, '0')}`;
			const desk = (await create('desks', { checkpointId: immigration, code: `D${String(n).padStart(2, '0')}`, kind: 'Desk', laneCategories: [lane] })).id;
			await create('desk-code-mappings', { system: 'Aman', externalCode: code, deskId: desk });
			desks.push({ code, lane });
		}
	}
	const gateCodes: string[] = [];
	for (let g = 1; g <= gates; g++) {
		const gate = (await create('desks', { checkpointId: immigration, code: `G${g}`, kind: 'EGate', laneCategories: ['EG'] })).id;
		await create('desk-code-mappings', { system: 'Aman', externalCode: `EG${g}`, deskId: gate });
		gateCodes.push(`EG${g}`);
	}

	// The hall's profile: the five queue zones on the hall's level.
	const draft = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals hall' });
	const profile = draft.id ?? draft.profile?.id;
	const zones = [
		...lanes.map((l, i) => ({ name: `Q-${l.lane}`, lane: l.lane as string | undefined, x0: 10 + 30 * i, sensors: pilotPerZone, burst: false })),
		...Array.from({ length: burstZones }, (_, i) => ({
			name: `B-${String(i + 1).padStart(2, '0')}`, lane: undefined, x0: 160 + 30 * i, sensors: i < burstSensors % burstZones ? Math.ceil(burstSensors / burstZones) : Math.floor(burstSensors / burstZones), burst: true
		}))
	];
	expect(zones.filter((z) => z.burst).reduce((n, z) => n + z.sensors, 0)).toBe(burstSensors);
	hallZones = zones.filter((z) => !z.burst).map((z) => z.name);
	allZones = zones.map((z) => z.name);
	for (const zone of zones) {
		const x = zone.x0;
		const made = await create(`zone-profiles/${profile}/zones`, {
			name: zone.name, kind: 'Queue', levelId: hall, polygon: `${x} 10,${x + 24} 10,${x + 24} 22,${x} 22`, ...(zone.lane ? { laneCategory: zone.lane } : {})
		});
		const zoneId = made.id ?? made.profile?.id;
		await create(`zone-profiles/${profile}/lines`, { name: `${zone.name} entry`, role: 'Entry', levelId: hall, startX: x, startY: 12, endX: x, endY: 16, zoneId });
		await create(`zone-profiles/${profile}/lines`, { name: `${zone.name} exit`, role: 'Exit', levelId: hall, startX: x + 20, startY: 22, endX: x + 24, endY: 22, zoneId });
	}
	const review = await (await call('GET', `${admin}/zone-profiles/${profile}/validation`, { token })).json();
	expect(review.publishable, 'the hall profile is publishable').toBe(true);
	await expectStatus(await call('POST', `${admin}/zone-profiles/${profile}/publish`, { token, data: { geometryHash: review.geometryHash } }), 200, 'publish');

	// 30 T3 sensors (6 a hall zone), the burst's 70 on their own zones, and the flood's own device on the first hall zone, each
	// calibrated: Stream counts only calibrated devices. Codes carry the run's site.
	const devices: PilotDevice[] = [];
	let flood: PilotDevice | undefined;
	let d = 0;
	for (const zone of zones) {
		for (let i = 0; i < zone.sensors + (zone === zones[0] ? 1 : 0); i++) {
			d++;
			const registered = await create('devices', {
				code: `${site}-${String(d).padStart(3, '0')}`, family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Canonical', clockSource: 'Ntp',
				placement: { levelId: hall, x: zone.x0 + 2 + (i % 10) * 2, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: zone.name }
			});
			deviceIds.push(registered.device.id);
			const calibrated = await call('POST', `${admin}/devices/${registered.device.id}/calibrations`, {
				token,
				data: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: 97, waitTimeErrorMinutes: 0.3 }
			});
			await expectStatus(calibrated, 201, 'calibration');
			const device = { zone: zone.name, key: registered.credential as string, burst: zone.burst };
			if (i === zone.sensors) flood = device;
			else devices.push(device);
		}
	}

	for (let i = 1; i <= displays; i++) {
		const code = `${site}-B${i}`;
		const display = await create('displays', {
			siteCode: site, code, name: `Board ${i}`, location: 'Arrivals hall', orientation: 'Landscape', languages: ['en'], bandMinutes: 5, hysteresisMinutes: 2,
			staleSeconds: 120, entries: zones.filter((z) => !z.burst).map((z) => ({ zone: z.name, labels: { en: z.lane! } })), fallback: { en: 'Please follow the signs' }
		});
		displayIds.push(display.display.id);
		displayKeys.push(display.credential);
		displayCodes.push(code);
	}

	pilotJson = JSON.stringify({ site, zones: zones.map((z) => ({ name: z.name, x0: z.x0 })), devices, flood, desks, gates: gateCodes });
	screenTokens = await screenSessions(accounts().loadScreen, screens);

	// AUH-TA's seeded sensors (the illustrative seed, ARV-139a) play their evening beside the hall, each issued a credential and
	// calibrated by an administrator as the demo does (tests/demo/reference-day.spec.ts).
	let seeded: { code: string; id: string }[] = [];
	await expect
		.poll(async () => {
			const listed = await (await call('GET', `${admin}/devices?siteCode=AUH-TA&pageSize=200`, { token })).json();
			seeded = (listed.data ?? listed) as { code: string; id: string }[];
			return seeded.length;
		}, { timeout: 120_000, intervals: [2_000] })
		.toBe(84);
	const credentials: { site: string; sensor: string; dialect: string; credential: string }[] = [];
	for (const device of seeded) {
		const issued = await call('POST', `${admin}/devices/${device.id}/credential`, { token });
		await expectStatus(issued, 200, `${device.code} credential`);
		auhIds.push(device.id);
		const calibrations = await (await call('GET', `${admin}/devices/${device.id}/calibrations`, { token })).json();
		if (((calibrations.data ?? calibrations) as unknown[]).length === 0) {
			const calibrated = await call('POST', `${admin}/devices/${device.id}/calibrations`, {
				token,
				data: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: 97, waitTimeErrorMinutes: 0.3 }
			});
			await expectStatus(calibrated, 201, `${device.code} calibration`);
		}
		credentials.push({ site: 'AUH-TA', sensor: device.code, dialect: 'Canonical', credential: (await issued.json()).credential });
	}
	await expectStatus(await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: credentials } }), 200, 'simulator devices');

	// Ingest keeps device records for a minute: let it see the calibrations before the first push. Then AUH-TA's evening from
	// 18:00 (its visitor wave), on the wall's quarter hours as the demo starts it.
	await new Promise((resolve) => setTimeout(resolve, 65_000));
	await new Promise((resolve) => setTimeout(resolve, 60_000 - (Date.now() % 60_000) + 1_000));
	const wall = new Date().getUTCHours() * 60 + new Date().getUTCMinutes();
	const playFrom = 1080 - ((((1080 - wall) % 15) + 15) % 15);
	await expectStatus(await call('POST', `${sensors}/start`, { headers: operator, data: { minute: playFrom, speed: 1, untilMinute: playFrom + 60 } }), 200, 'simulator start');
});

test.afterAll(async () => {
	await call('POST', `${sensors}/pause`, { headers: operator });
	await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: [] } });
	if (!token) return;
	// No working credential left behind: the hall's devices are retired and its displays deleted, AUH-TA's credentials rotated
	// once more and dropped. The screens' sessions are left to expire (their access tokens last minutes; no refresh token was
	// kept). The provisioning's sign-in has expired by now (the run lasts longer than an access token), so the administrator
	// signs in again. Every step is tried, then any that failed is reported.
	const fresh = await secondFactorSignIn(accounts().loadAdmin);
	const failed: string[] = [];
	const attempt = async (what: string, method: string, url: string, ok: number) => {
		const status = (await call(method, url, { token: fresh })).status();
		if (status !== ok) failed.push(`${what} ${status}`);
	};
	for (const id of deviceIds) await attempt('retire device', 'POST', `${admin}/devices/${id}/retire`, 200);
	for (const id of displayIds) await attempt('delete display', 'DELETE', `${admin}/displays/${id}`, 204);
	for (const id of auhIds) await attempt('rotate AUH-TA credential', 'POST', `${admin}/devices/${id}/credential`, 200);
	expect(failed, 'every credential of the run is withdrawn').toEqual([]);
});

test('the pilot hall and the D5 burst through the whole pipeline: no failures, the limits answer 413 and 429, the screens and boards keep going', async ({}, testInfo) => {
	test.setTimeout(45 * 60_000);
	expect(fs.existsSync(harness), `the harness is built (${harness})`).toBe(true);
	runStarted = new Date();
	// Only what dotnet needs to start, and the run's settings: the runner's own secrets stay out of the harness.
	const passThrough = ['PATH', 'HOME', 'TMPDIR', 'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'DOTNET_NOLOGO', 'SystemRoot', 'USERPROFILE', 'TEMP', 'TMP'];
	const env = {
		...Object.fromEntries(passThrough.filter((name) => process.env[name] !== undefined).map((name) => [name, process.env[name]!])),
		ARIVA_LOAD_MODE: 'pilot',
		ARIVA_LOAD_INGEST_URL: hosts.ingest,
		ARIVA_LOAD_MAIN_URL: hosts.main,
		ARIVA_LOAD_PILOT: pilotJson,
		ARIVA_LOAD_DISPLAY_KEYS: displayKeys.join(','),
		ARIVA_LOAD_DISPLAY_CODES: displayCodes.join(','),
		ARIVA_LOAD_ACCESS_TOKENS: screenTokens.join(','),
		ARIVA_LOAD_KAFKA_BOOTSTRAP: process.env.ARIVA_E2E_KAFKA_BOOTSTRAP!,
		ARIVA_LOAD_OUTPUT: output,
		...Object.fromEntries(['ARIVA_LOAD_STEADY_SECONDS', 'ARIVA_LOAD_BURST_SECONDS', 'ARIVA_LOAD_RECOVERY_SECONDS'].filter((n) => process.env[n]).map((n) => [n, process.env[n]!]))
	};
	const log: string[] = [];
	// A report left by an earlier run must never stand in for this one's.
	fs.rmSync(output, { recursive: true, force: true });
	// The database connections each host holds while the run goes (every 2 seconds, by the hosts' application names).
	const connections = new Map<string, number>();
	let connectionsTotal = 0;
	const watcher = staffingDatabase();
	await watcher.connect();
	const sampling = setInterval(() => {
		watcher
			.query("SELECT coalesce(nullif(application_name, ''), usename) AS name, count(*)::int AS n FROM pg_stat_activity WHERE backend_type = 'client backend' GROUP BY 1")
			.then(({ rows }) => {
				connectionsTotal = Math.max(connectionsTotal, rows.reduce((n, r) => n + r.n, 0));
				for (const r of rows) connections.set(r.name, Math.max(connections.get(r.name) ?? 0, r.n));
			})
			.catch(() => undefined);
	}, 2_000);
	const code = await new Promise<number | null>((resolve) => {
		const child = spawn('dotnet', [harness], { env, stdio: ['ignore', 'pipe', 'pipe'] });
		child.stdout.on('data', (d) => log.push(String(d)));
		child.stderr.on('data', (d) => log.push(String(d)));
		child.on('exit', resolve);
	});
	clearInterval(sampling);
	await watcher.end();
	report = JSON.parse(fs.readFileSync(path.join(output, 'pilot-report.json'), 'utf8'));
	expect(new Date(report.startedUtc).getTime(), 'the report is this run\'s').toBeGreaterThanOrEqual(runStarted.getTime() - 1_000);
	await testInfo.attach('pilot-report.md', { path: path.join(output, 'pilot-report.md'), contentType: 'text/markdown' });

	// Stream applies an event once its zone's watermark passes it (90 seconds of lateness) and checkpoints every 5 seconds:
	// two and a half minutes after the last push, every crossing the harness sent is counted or refused.
	await new Promise((resolve) => setTimeout(resolve, 150_000));
	measurements = { ...(await measure(report.crossingsSent)), connections: { peakTotal: connectionsTotal, peakByHost: Object.fromEntries(connections) } };
	fs.writeFileSync(path.join(output, 'pilot-measurements.json'), JSON.stringify(measurements, null, 2));
	await testInfo.attach('pilot-measurements.json', { path: path.join(output, 'pilot-measurements.json'), contentType: 'application/json' });

	// Findings excuse nothing silently: each is on the report as an annotation, and only the bounded ARV-233 one exists.
	for (const finding of report.findings ?? []) testInfo.annotations.push({ type: 'finding', description: finding });
	// The stable parts of FloodRule's finding only, so a change of its wording cannot excuse something else.
	const floodReset = (f: string) => f.startsWith('flood: ') && f.includes('got a connection reset instead of their 429') && f.endsWith('; ARV-233).');
	expect((report.findings ?? []).every(floodReset), 'only the flood resets are a finding').toBe(true);
	expect(report.problems, log.join('')).toEqual([]);
	// 0, or 2 when only Stream's capacity fell short (the second test's verdict).
	expect([0, 2]).toContain(code);
	const [steady, burst, recovery, oversize, flood] = report.ingest;
	for (const pushes of [steady, burst, recovery]) {
		expect(pushes.failures, pushes.name).toBe(0);
		expect(pushes.outcomes['429'] ?? 0, pushes.name).toBe(0);
	}
	expect(oversize.outcomes['413']).toBe(oversize.requests);
	expect(flood.outcomes['429']).toBeGreaterThan(0);
	expect(flood.outcomes['429 without Retry-After'] ?? 0).toBe(0);
	expect(report.shape.burstPositionsPerSecond).toBe(15_000);
	expect(report.live.screens).toBe(screens);
	expect(report.live.boards.outcomes['200']).toBe(report.live.boards.requests);

	// What the pipeline made of it: AMAN's records reached the border tables mapped to the hall's desks, Stream wrote every
	// hall zone's queue minutes and state, and the staffing job ran on them.
	expect(measurements.border.deskIntervals, 'AMAN desk minutes stored, mapped').toBeGreaterThan(0);
	expect(measurements.border.unmapped, 'every AMAN code of the hall is mapped').toBe(0);
	expect(measurements.stream.hall.length, 'every hall zone has its stream state').toBe(lanes.length);
	expect(measurements.stream.auh.length, 'AUH-TA zones have their stream state').toBeGreaterThan(0);
	expect(measurements.queueMinutes, 'queue minutes of the hall').toBeGreaterThan(0);
	expect(measurements.staffing.runs, 'TickerQ logged the staffing runs with their duration').toBeGreaterThan(0);
	expect(measurements.staffing.laneRows, 'staffing rows for the hall').toBeGreaterThan(0);
});

// Stream's capacity at the pilot profile, judged on the same run. It fails until ARV-230 and ARV-232 (ARV-124a stays open
// until it passes): the queue engine holds every track position for its lateness (90 s) and checkpoints each zone's whole
// state every 5 s, about 18 MB of JSON a hall zone, so Stream stopped three to six minutes into the run in ARV-124a's runs 3
// to 7 (out of the heap its pod allows, 1,125 MiB of a 1500Mi limit, in runs 3, 4 and 7, whose logs were read; wiki 05).
test('Stream keeps up with the pilot hall and the D5 burst: it runs throughout, catches up after the burst and counts every crossing', async () => {
	expect(report, 'the first test ran the harness').toBeDefined();
	expect(report.capacity).toEqual([]);
	expect(report.stream.queueEngine.catchUpSeconds).not.toBeNull();
	expect(measurements.desks.deskMinutes, 'Stream\'s desk feed wrote the hall\'s desk minutes from AMAN\'s records').toBeGreaterThan(0);
	// Every crossing Ingest accepted is counted by Stream, in the hall and on the burst's zones alike.
	for (const zone of measurements.counting) {
		expect(zone.sentIn, zone.zone).toBeGreaterThan(0);
		expect(zone.countedIn / zone.sentIn, `${zone.zone}: entries counted of those sent`).toBeGreaterThanOrEqual(0.98);
		expect(zone.countedOut / zone.sentOut, `${zone.zone}: exits counted of those sent`).toBeGreaterThanOrEqual(0.98);
	}
});

/** Reads what the run left in the database, Redis and Ariva.Api.Cronz's log. */
async function measure(sent: Record<string, { in: number; out: number }>) {
	const db = staffingDatabase();
	await db.connect();
	// Reads only: the migration login can read the tables the runtime login reads, and pg_stat_activity, but here it writes nothing.
	await db.query('BEGIN READ ONLY');
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		const rows = async (sql: string, values: unknown[] = []) => (await db.query(sql, values)).rows;
		const hallKeys = hallZones.map((z) => `${site}/${z}`);
		const allKeys = allZones.map((z) => `${site}/${z}`);
		const counted = await rows('SELECT zone_key, sum(entries)::int AS entries, sum(exits)::int AS exits FROM queue_minute WHERE zone_key = ANY($1) GROUP BY zone_key', [allKeys]);
		const counting = allKeys.map((key) => {
			const row = counted.find((r) => r.zone_key === key);
			return { zone: key, sentIn: sent[key]?.in ?? 0, sentOut: sent[key]?.out ?? 0, countedIn: row?.entries ?? 0, countedOut: row?.exits ?? 0 };
		});
		const state = await rows('SELECT zone_key, state_bytes, pg_column_size(state) AS stored_bytes, updated_on FROM stream_zone_state ORDER BY state_bytes DESC');
		const deskFeed = await rows('SELECT site_code, octet_length(state::text) AS state_bytes, pg_column_size(state) AS stored_bytes, updated_on FROM desk_feed_state ORDER BY site_code');
		const outbox = (await rows('SELECT count(*)::int AS unsent, min(created_on) AS oldest FROM outbox_message WHERE sent_on IS NULL'))[0];
		const outboxTopics = await rows('SELECT topic, count(*)::int AS rows, count(*) FILTER (WHERE sent_on IS NULL)::int AS unsent FROM outbox_message WHERE created_on >= $1 GROUP BY topic ORDER BY topic', [runStarted]);
		const archive = (await rows('SELECT count(*)::bigint AS events FROM sensing_event WHERE site_code = $1 AND received_utc >= $2', [site, runStarted]))[0];
		const sizes = await rows(
			"SELECT h AS hypertable, hypertable_size(h::regclass) AS bytes FROM unnest(ARRAY['sensing_event','sensing_batch','queue_minute','desk_minute','lane_staffing']) AS h"
		);
		const deskIntervals = (await rows("SELECT count(*)::int AS n, count(*) FILTER (WHERE desk_id IS NULL)::int AS unmapped FROM border_desk_interval WHERE site_code = $1", [site]))[0];
		const deskMinutes = (await rows("SELECT count(*)::int AS n FROM desk_minute WHERE desk_code LIKE $1 || '/%' AND minute_utc >= $2", [site, runStarted]))[0];
		const queueMinutes = (await rows('SELECT count(*)::int AS n FROM queue_minute WHERE zone_key = ANY($1) AND minute_utc >= $2', [hallKeys, runStarted]))[0];
		const laneRows = (await rows('SELECT count(*)::int AS n FROM lane_staffing WHERE zone_key = ANY($1) AND computed_utc >= $2', [hallKeys, runStarted]))[0];
		const memory = await redis.info('memory');
		const keys = await redis.dbSize();

		// Every staffing run since the harness started: TickerQ logs each function's run with its time ("TickerQ Job completed:
		// {Function} ({JobId}) in {ExecutionTime}ms"), and the job its own warning when a run takes its whole minute.
		// The run's own log folder only (playwright.config.ts sets it for every run); without it there is nothing of this run to read.
		const cronzLog = process.env.ARIVA_E2E_LOG_DIR ? path.join(process.env.ARIVA_E2E_LOG_DIR, 'Ariva.Api.Cronz.log') : '';
		// The run's own log folder (playwright.config.ts) of about half an hour; a log beyond 200 MB is not this run's to read whole.
		if (fs.existsSync(cronzLog)) expect(fs.statSync(cronzLog).size, 'Cronz log of this run').toBeLessThan(200 * 1024 * 1024);
		const lines = (fs.existsSync(cronzLog) ? fs.readFileSync(cronzLog, 'utf8').split('\n') : [])
			.filter((l) => l.includes('StaffingRecommendations') || l.includes('Staffing recommendation'))
			.map((l) => {
				try {
					return JSON.parse(l);
				} catch {
					return null;
				}
			})
			.filter((e) => e && new Date(e.Timestamp ?? 0) >= runStarted);
		const staffing = lines.filter((e: any) => e.Properties?.Function === 'StaffingRecommendations' && e.Properties?.ExecutionTime !== undefined);
		const elapsed = staffing.map((e: any) => Number(e.Properties.ExecutionTime)).filter((v: number) => Number.isFinite(v)).sort((a: number, b: number) => a - b);
		return {
			site,
			counting,
			stream: {
				hall: state.filter((r) => hallKeys.includes(r.zone_key)),
				burstZones: state.filter((r) => allKeys.includes(r.zone_key) && !hallKeys.includes(r.zone_key)),
				auh: state.filter((r) => String(r.zone_key).startsWith('AUH-TA/')),
				largest: state.slice(0, 10)
			},
			deskFeed,
			outbox: { ...outbox, byTopicSinceRun: outboxTopics },
			archive: { hallEventsSinceRun: Number(archive.events), sizes },
			border: { deskIntervals: deskIntervals.n, unmapped: deskIntervals.unmapped },
			desks: { deskMinutes: deskMinutes.n },
			queueMinutes: queueMinutes.n,
			staffing: {
				runs: elapsed.length,
				medianMs: elapsed.length ? elapsed[Math.floor(elapsed.length / 2)] : null,
				maxMs: elapsed.length ? elapsed[elapsed.length - 1] : null,
				overran: lines.filter((e: any) => String(e.MessageTemplate ?? '').includes('its whole minute')).length,
				laneRows: laneRows.n
			},
			redis: {
				keys,
				usedMemory: /used_memory:(\d+)/.exec(memory)?.[1],
				usedMemoryPeak: /used_memory_peak:(\d+)/.exec(memory)?.[1]
			}
		};
	} finally {
		await redis.quit();
		await db.query('ROLLBACK').catch(() => undefined);
		await db.end();
	}
}
