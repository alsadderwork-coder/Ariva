// The scripted demo on a developer machine (ARV-064, ARV-139b): two evenings played together, in real time, through the
// whole pipeline started by Ariva.AppHost: the reference evening at the fictional Demo International Airport (DMO, seed
// 9303) and the illustrative AUH Terminal A arrivals evening (AUH-TA, seed 9304). Node 22, no packages.
//
//   node scripts/demo-local.mjs prepare      once: demo accounts and the AppHost settings, in .demo/ (git-ignored)
//   dotnet run --project Platform/Cloud/Ariva.AppHost -- "--AppHost:HostEnvironmentFile=<repository>/.demo/apphost-environment.json"
//   node scripts/demo-local.mjs start        registers and calibrates the sensors, loads them into the simulator, plays
//   node scripts/demo-local.mjs status       the demo clock and the sensors
//   node scripts/demo-local.mjs stop         pauses the simulator and unloads the sensors
//   node scripts/demo-local.mjs accounts     the demo accounts again
//
// start takes --at 17:40 (demo clock, default 17:40), --until 19:40 (default), --speed 1 (demo minutes per real minute),
// --sensors all|events (default all: every queue's counting sensor at DMO and every seeded sensor at AUH-TA), --sites
// both|DMO|AUH-TA (default both) and --no-align (start exactly at --at rather than up to 14 minutes earlier, which keeps
// the 15-minute bins on the scenario's and gives the sensors time to settle). Hosts: ARIVA_MAIN_URL (default
// http://localhost:51001) and ARIVA_SIMULATION_URL (http://localhost:51020). Runbook: wiki/10, section 4.11.
//
// AUH-TA's sensors come from the illustrative seed (ARV-139a), in Commissioning and without a credential. start issues
// each the credential it plays with (POST devices/{id}/credential, the administrator's recent second factor) and records
// its calibration (POST devices/{id}/calibrations), as an installer would; no device authentication is relaxed. It also
// gives AUH-TA the scenario's three alert rules through the alert rules API, once, while the site has none.
//
// Local only: the accounts sign in without a second factor (Auth:TotpRequired false). Ariva.Api.Main refuses to start
// with that setting, or with development accounts, unless both its host environment and Application:Environment are
// vm-local. The script talks only to loopback addresses; --remote allows another host, for a deliberate test only.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const DIR = path.join(ROOT, '.demo');
const ACCOUNTS = path.join(DIR, 'accounts.json');
const APPHOST = path.join(DIR, 'apphost-environment.json');
const MAIN = (process.env.ARIVA_MAIN_URL || 'http://localhost:51001').replace(/\/$/, '');
const SIMULATION = (process.env.ARIVA_SIMULATION_URL || 'http://localhost:51020').replace(/\/$/, '');
// The administrator's password and code and the simulator key go to these hosts: loopback only, unless asked.
for (const url of [MAIN, SIMULATION]) {
	let host = '';
	try {
		host = new URL(url).hostname;
	} catch {
		host = '';
	}
	if (!['localhost', '127.0.0.1', '[::1]', '::1'].includes(host) && !process.argv.includes('--remote')) {
		console.error(`demo-local: ${url} is not on this machine; the demo sends credentials to it. Add --remote if that is really meant.`);
		process.exit(1);
	}
}
const WEB = process.env.ARIVA_WEB_URL || 'http://localhost:51010';

/** The demo accounts: who sees each scripted event (wiki/15). */
const PEOPLE = [
	{
		userName: 'demo.admin',
		roles: ['SystemAdministrator'],
		sites: ['*'],
		totp: true,
		shows: 'everything; AUH-TA (illustrative): 18:12 A-VIS breach, 18:25 Q-RES-04 offline, 19:13 A-EG-OV overflow'
	},
	{ userName: 'demo.border', roles: ['BorderShiftSupervisor'], sites: ['DMO'], totp: false, shows: '18:05 R-001 on A-VIS, 18:20 R-003 for S-17' },
	{ userName: 'demo.handler', roles: ['HandlerStationManager'], sites: ['DMO'], totp: false, shows: '19:10 R-004 on check-in island C' },
	{ userName: 'demo.terminal', roles: ['TerminalDutyManager'], sites: ['DMO'], totp: false, shows: 'check-in and security, terminal-wide' }
];

/** Every queue's counting sensor and the overflow bands', as reference-day.spec.ts loads them (ScenarioModel.cs). */
const SENSORS = [
	['S-01', 'SEC-N', 'DEP', 'Canonical'], ['S-06', 'SEC-S', 'DEP', 'Canonical'], ['S-11', 'SEC-N', 'DEP', 'Canonical'],
	['S-12', 'A-CRW', 'ARR', 'Canonical'], ['S-13', 'A-CIT', 'ARR', 'Canonical'], ['S-15', 'A-VIS', 'ARR', 'Canonical'],
	['S-17', 'A-VIS', 'ARR', 'Canonical'], ['S-21', 'A-RES', 'ARR', 'Canonical'], ['S-23', 'A-EG', 'ARR', 'Canonical'],
	['S-25', 'A-VIS', 'ARR', 'Canonical'], ['S-27', 'D-CRW', 'DEP', 'Canonical'], ['S-28', 'D-CIT', 'DEP', 'Canonical'],
	['S-30', 'D-RES', 'DEP', 'Canonical'], ['S-32', 'D-VIS', 'DEP', 'Canonical'], ['S-37', 'D-EG', 'DEP', 'Canonical'],
	['S-39', 'D-VIS', 'DEP', 'Canonical'], ['S-40', 'CI-A', 'DEP', 'Canonical'], ['S-45', 'CI-B', 'DEP', 'Canonical'],
	['S-50', 'CI-C', 'DEP', 'Xovis'], ['S-55', 'CI-D', 'DEP', 'Xovis']
].map(([sensor, zone, level, dialect]) => ({ sensor, zone, level, dialect }));
const EVENT_SENSORS = ['S-13', 'S-15', 'S-17', 'S-21', 'S-25', 'S-50', 'S-55'];

/**
 * AUH-TA (ARV-139b): every seeded sensor plays (the seed's 84, AuhTerminalALayout.cs); with --sensors events only those of
 * the three scripted events: the visitors' queue and band leads and desk sensors, the residents' lead and Q-RES-04, the
 * smart gates' queue and band leads.
 */
const AUH_SITE = 'AUH-TA';
const AUH_EVENT_SENSORS = ['Q-VIS-01', 'O-VIS-01', 'D-VIS-01', 'D-VIS-02', 'D-VIS-03', 'D-VIS-04', 'D-VIS-05', 'Q-RES-01', 'Q-RES-04', 'Q-EG-01', 'O-EG-01'];

/** The AUH-TA scenario's seeded rules (ScenarioSite SeedRules), as Ariva rules: the same shape as DMO's R-001 to R-003. */
const AUH_QUEUES = ['A-CRW', 'A-DIP', 'A-CIT', 'A-RES', 'A-GCC', 'A-VIS', 'A-TRF', 'A-EG'];
const AUH_RULES = [
	{
		name: 'Nowcast above 15 min', zones: AUH_QUEUES, metric: 'Nowcast', comparator: 'GreaterThan', threshold: 15, minQueueLength: 10, clearThreshold: 12,
		sustainMinutes: 1, clearAfterMinutes: 1, severity: 'Critical', ownerRole: 'BorderShiftSupervisor', escalateAfterMinutes: 10, escalateToRole: null,
		escalationContact: 'Border operations duty officer', notifyByEmail: false, enabled: true
	},
	{
		name: 'Overflow band occupied', zones: AUH_QUEUES, metric: 'OverflowOccupied', comparator: 'IsTrue', threshold: null, minQueueLength: null,
		clearThreshold: null, sustainMinutes: 3, clearAfterMinutes: 3, severity: 'Warning', ownerRole: null, escalateAfterMinutes: 15, escalateToRole: null,
		escalationContact: null, notifyByEmail: false, enabled: true
	},
	{
		name: 'Sensor offline', zones: [...AUH_QUEUES, 'A-VIS-OV', 'A-EG-OV'], metric: 'SensorOffline', comparator: 'IsTrue', threshold: null,
		minQueueLength: null, clearThreshold: null, sustainMinutes: 1, clearAfterMinutes: 1, severity: 'Warning', ownerRole: null, escalateAfterMinutes: 15,
		escalateToRole: null, escalationContact: 'Systems', notifyByEmail: false, enabled: true
	}
];

function fail(message) {
	console.error(`demo-local: ${message}`);
	process.exit(1);
}

function base32(bytes) {
	const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
	let bits = 0;
	let value = 0;
	let out = '';
	for (const byte of bytes) {
		value = (value << 8) | byte;
		bits += 8;
		while (bits >= 5) {
			out += alphabet[(value >>> (bits - 5)) & 31];
			bits -= 5;
		}
	}
	return bits > 0 ? out + alphabet[(value << (5 - bits)) & 31] : out;
}

function fromBase32(text) {
	const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
	let bits = 0;
	let value = 0;
	const out = [];
	for (const c of text.replace(/=+$/, '').toUpperCase()) {
		value = (value << 5) | alphabet.indexOf(c);
		bits += 5;
		if (bits >= 8) {
			out.push((value >>> (bits - 8)) & 255);
			bits -= 8;
		}
	}
	return Buffer.from(out);
}

function totp(secret, at = Date.now()) {
	const counter = Buffer.alloc(8);
	counter.writeBigInt64BE(BigInt(Math.floor(at / 30_000)));
	const hash = crypto.createHmac('sha1', fromBase32(secret)).update(counter).digest();
	const offset = hash[hash.length - 1] & 0x0f;
	const binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
	return String(binary % 1_000_000).padStart(6, '0');
}

function readState() {
	if (!fs.existsSync(ACCOUNTS)) fail('run "node scripts/demo-local.mjs prepare" first, then start the AppHost with its settings.');
	return JSON.parse(fs.readFileSync(ACCOUNTS, 'utf8'));
}

function prepare() {
	fs.mkdirSync(DIR, { recursive: true, mode: 0o700 });
	if (fs.existsSync(ACCOUNTS)) {
		console.log('demo-local: .demo/accounts.json exists; keeping its accounts (delete .demo to start over).');
	} else {
		// Long and random, free of the user name and the product name, so the password policy accepts them.
		const people = PEOPLE.map((p) => ({
			...p,
			password: crypto.randomBytes(18).toString('base64url'),
			totpSecret: p.totp ? base32(crypto.randomBytes(20)) : undefined
		}));
		const state = { simulationKey: crypto.randomBytes(32).toString('base64url'), people };
		fs.writeFileSync(ACCOUNTS, JSON.stringify(state, null, 2) + '\n', { mode: 0o600 });
	}
	// The modes above apply only to new files; tighten what already exists too (Windows ignores them: keep the
	// repository in your own profile).
	fs.chmodSync(DIR, 0o700);
	fs.chmodSync(ACCOUNTS, 0o600);

	const state = readState();
	const main = { Auth__TotpRequired: 'false' };
	state.people.forEach((p, i) => {
		const prefix = `Auth__DevelopmentUsers__${i}__`;
		main[`${prefix}UserName`] = p.userName;
		main[`${prefix}Password`] = p.password;
		main[`${prefix}Temporary`] = 'false';
		p.roles.forEach((role, r) => (main[`${prefix}Roles__${r}`] = role));
		p.sites.forEach((site, s) => (main[`${prefix}Sites__${s}`] = site));
		if (p.totpSecret) main[`${prefix}TotpSecret`] = p.totpSecret;
	});
	// A second operator key beside the AppHost's own (which stays in the dashboard's parameters); the simulator keeps
	// only its SHA-256.
	const simulation = {
		Simulation__Control__Keys__1__Name: 'demo-local',
		Simulation__Control__Keys__1__Sha256: crypto.createHash('sha256').update(state.simulationKey).digest('hex'),
		Simulation__Control__Keys__1__Scopes__0: 'read',
		Simulation__Control__Keys__1__Scopes__1: 'control'
	};
	fs.writeFileSync(APPHOST, JSON.stringify({ 'api-main': main, simulation }, null, 2) + '\n', { mode: 0o600 });
	fs.chmodSync(APPHOST, 0o600);
	console.log('demo-local: wrote .demo/accounts.json and .demo/apphost-environment.json (git-ignored, owner-only).');
	console.log('Start Ariva with them:');
	// An absolute path: dotnet run may start the AppHost in another working directory.
	console.log(`  dotnet run --project Platform/Cloud/Ariva.AppHost -- "--AppHost:HostEnvironmentFile=${APPHOST}"`);
	console.log('then, once the dashboard shows every resource running: node scripts/demo-local.mjs start');
}

async function call(method, url, { token, key, body } = {}) {
	const headers = { Accept: 'application/json' };
	if (token) headers.Authorization = `Bearer ${token}`;
	if (key) headers.Authorization = `Bearer ${key}`;
	if (body !== undefined) headers['Content-Type'] = 'application/json';
	let response;
	try {
		response = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
	} catch (e) {
		fail(`${method} ${url} did not answer (${e.cause?.code ?? e.message}). Is the AppHost running?`);
	}
	const text = await response.text();
	let json = null;
	try {
		json = text ? JSON.parse(text) : null;
	} catch {
		json = null;
	}
	return { status: response.status, json, text };
}

const items = (body) => body?.data ?? body ?? [];

async function waitFor(url, what) {
	for (let attempt = 0; attempt < 60; attempt++) {
		try {
			if ((await fetch(url)).ok) return;
		} catch {
			// not up yet
		}
		if (attempt === 0) console.log(`demo-local: waiting for ${what} (${url})`);
		await new Promise((resolve) => setTimeout(resolve, 5000));
	}
	fail(`${what} is not ready after 5 minutes.`);
}

async function signInAdmin(state) {
	const admin = state.people.find((p) => p.userName === 'demo.admin');
	// A code is accepted once: wait for a fresh 30-second step if this one may already have been used.
	if (Date.now() % 30_000 > 25_000) await new Promise((resolve) => setTimeout(resolve, 30_000 - (Date.now() % 30_000) + 500));
	const response = await call('POST', `${MAIN}/api/auth/login`, { body: { userName: admin.userName, password: admin.password, code: totp(admin.totpSecret) } });
	if (response.status !== 200) fail(`demo.admin could not sign in (${response.status}): was the AppHost started with --AppHost:HostEnvironmentFile=.demo/apphost-environment.json?`);
	return response.json.accessToken;
}

function minuteOf(text, fallback) {
	if (text === undefined) return fallback;
	const m = /^(\d{1,2}):(\d{2})$/.exec(text);
	if (!m || Number(m[1]) > 23 || Number(m[2]) > 59) fail(`"${text}" is not a time such as 17:40.`);
	return Number(m[1]) * 60 + Number(m[2]);
}

function option(name) {
	const index = process.argv.indexOf(`--${name}`);
	return index > 0 ? process.argv[index + 1] : undefined;
}

const clock = (minute) => `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`;

async function start() {
	const state = readState();
	const at = minuteOf(option('at'), 1060);
	const until = minuteOf(option('until'), 1180);
	const speed = Number(option('speed') ?? 1);
	if (!(until > at)) fail('--until must be after --at.');
	if (!(speed > 0 && speed <= 60)) fail('--speed is above 0 and at most 60.');
	const which = option('sensors') ?? 'all';
	if (!['all', 'events'].includes(which)) fail('--sensors is all or events.');
	const sites = option('sites') ?? 'both';
	if (!['both', 'DMO', AUH_SITE].includes(sites)) fail('--sites is both, DMO or AUH-TA.');
	const plan = sites === AUH_SITE ? [] : which === 'events' ? SENSORS.filter((s) => EVENT_SENSORS.includes(s.sensor)) : SENSORS;

	await waitFor(`${MAIN}/health/readiness`, 'Ariva.Api.Main');
	await waitFor(`${SIMULATION}/health/readiness`, 'the simulator');
	const token = await signInAdmin(state);
	const admin = `${MAIN}/api/v1/admin`;

	const profiles = items((await call('GET', `${admin}/zone-profiles?siteCode=DMO`, { token })).json);
	if (profiles.length === 0 && plan.length > 0) fail('DMO has no zone profile: the demo seed (Seed:DemoTopology, vm-local) has not run.');
	const profile = plan.length > 0 ? (await call('GET', `${admin}/zone-profiles/${profiles[0].id}`, { token })).json : null;
	const levels = items((await call('GET', `${admin}/levels?siteCode=DMO`, { token })).json);
	const levelId = (code) => levels.find((l) => l.code === code)?.id;
	const centre = (name) => {
		const zone = profile.zones.find((z) => z.name === name);
		const points = typeof zone.polygon === 'string' ? zone.polygon.split(',').map((p) => p.trim().split(' ').map(Number)) : zone.polygon.map((p) => [p.x, p.y]);
		return { x: points.reduce((s, p) => s + p[0], 0) / points.length, y: points.reduce((s, p) => s + p[1], 0) / points.length };
	};

	// A calibrated device cannot be deleted and keeps its code: one from an earlier run is reused with a new credential.
	const known = new Map(items((await call('GET', `${admin}/devices?siteCode=DMO&pageSize=200`, { token })).json).map((d) => [d.code, d]));
	const credentials = [];
	for (const device of plan) {
		let id;
		let credential;
		const found = known.get(device.sensor);
		if (found && found.state !== 'Retired') {
			id = found.id;
			const rotated = await call('POST', `${admin}/devices/${id}/credential`, { token });
			if (rotated.status !== 200) fail(`${device.sensor}: new credential refused (${rotated.status}) ${rotated.text}`);
			credential = rotated.json.credential;
		} else {
			const { x, y } = centre(device.zone);
			const registered = await call('POST', `${admin}/devices`, {
				token,
				body: {
					code: device.sensor, family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: device.dialect, clockSource: 'Ntp',
					placement: { levelId: levelId(device.level), x, y, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: device.zone }
				}
			});
			if (registered.status !== 201) fail(`${device.sensor}: registration refused (${registered.status}) ${registered.text}`);
			id = registered.json.device.id;
			credential = registered.json.credential;
		}
		// Commissioned: Stream counts only calibrated devices, and only they are watched for silence (R-003).
		if (items((await call('GET', `${admin}/devices/${id}/calibrations`, { token })).json).length === 0) {
			const calibrated = await call('POST', `${admin}/devices/${id}/calibrations`, {
				token,
				body: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: 97, waitTimeErrorMinutes: 0.3 }
			});
			if (calibrated.status !== 201) fail(`${device.sensor}: calibration refused (${calibrated.status}) ${calibrated.text}`);
		}
		credentials.push({ sensor: device.sensor, dialect: device.dialect, credential });
	}
	console.log(`demo-local: ${credentials.length} DMO sensors registered and calibrated.`);
	if (sites !== 'DMO') credentials.push(...(await prepareAuh(admin, token, which)));

	const sensors = `${SIMULATION}/api/v1/simulation/sensors`;
	const key = state.simulationKey;
	await call('POST', `${sensors}/pause`, { key });
	const loaded = await call('PUT', `${sensors}/devices`, { key, body: { devices: credentials } });
	if (loaded.status !== 200) fail(`the simulator refused the sensors (${loaded.status}) ${loaded.text}`);
	console.log('demo-local: sensors loaded; Ingest picks up the calibrations within a minute, waiting 65 s.');
	await new Promise((resolve) => setTimeout(resolve, 65_000));

	let from = at;
	if (!process.argv.includes('--no-align') && speed === 1) {
		// The 15-minute bins follow the wall clock: start on the next whole minute, up to 14 demo minutes before --at, so
		// that the demo's quarter hours fall on the wall's. The sensors are live from then on, so none goes Offline while
		// waiting, and those minutes absorb the sensors' first readings while Ariva learns their clocks (F19).
		await new Promise((resolve) => setTimeout(resolve, 60_000 - (Date.now() % 60_000) + 1000));
		const wall = new Date().getUTCHours() * 60 + new Date().getUTCMinutes();
		from = at - ((((at - wall) % 15) + 15) % 15);
	}
	const started = await call('POST', `${sensors}/start`, { key, body: { minute: from, speed, untilMinute: until } });
	if (started.status !== 200) fail(`the simulator did not start (${started.status}) ${started.text}`);
	console.log(`demo-local: playing ${clock(from)} to ${clock(until)} at speed ${speed} (the demo proper from ${clock(at)}). Open ${WEB} and sign in:`);
	accounts(state);
}

/**
 * AUH-TA (ARV-139b): the illustrative seed's own sensors, each issued a credential and calibrated through the devices API
 * (never a relaxed check: the seed leaves them in Commissioning without a credential), and the scenario's alert rules
 * while the site has none. Returns the simulator's device entries for site AUH-TA.
 */
async function prepareAuh(admin, token, which) {
	const listed = items((await call('GET', `${admin}/devices?siteCode=${AUH_SITE}&pageSize=200`, { token })).json);
	if (listed.length === 0) fail('AUH-TA has no sensors: the illustrative seed (Seed:DemoTopology, ARV-139a) has not run. Use --sites DMO to play DMO alone.');
	const seeded = listed.filter((d) => d.state !== 'Retired' && (which === 'all' || AUH_EVENT_SENSORS.includes(d.code)));
	const devices = [];
	for (const device of seeded) {
		const issued = await call('POST', `${admin}/devices/${device.id}/credential`, { token });
		if (issued.status !== 200) fail(`${device.code}: credential refused (${issued.status}) ${issued.text}`);
		if (items((await call('GET', `${admin}/devices/${device.id}/calibrations`, { token })).json).length === 0) {
			const calibrated = await call('POST', `${admin}/devices/${device.id}/calibrations`, {
				token,
				body: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: 97, waitTimeErrorMinutes: 0.3 }
			});
			if (calibrated.status !== 201) fail(`${device.code}: calibration refused (${calibrated.status}) ${calibrated.text}`);
		}
		devices.push({ site: AUH_SITE, sensor: device.code, dialect: 'Canonical', credential: issued.json.credential });
	}

	const rules = items((await call('GET', `${admin}/alert-rules?siteCode=${AUH_SITE}`, { token })).json);
	if (rules.length === 0) {
		for (const rule of AUH_RULES) {
			const created = await call('POST', `${admin}/alert-rules`, { token, body: { siteCode: AUH_SITE, ...rule } });
			if (created.status !== 201) fail(`AUH-TA rule "${rule.name}" refused (${created.status}) ${created.text}`);
		}
		console.log(`demo-local: AUH-TA alert rules created (${AUH_RULES.length}).`);
	}

	console.log(`demo-local: ${devices.length} AUH-TA sensors issued a credential and calibrated.`);
	return devices;
}

function accounts(state = readState()) {
	for (const p of state.people) {
		console.log(`  ${p.userName.padEnd(14)} ${p.password}  ${p.roles.join(', ')}: ${p.shows}${p.totpSecret ? ' (code: node scripts/demo-local.mjs code)' : ''}`);
	}
}

async function status() {
	const state = readState();
	const response = await call('GET', `${SIMULATION}/api/v1/simulation/sensors`, { key: state.simulationKey });
	if (response.status !== 200) fail(`the simulator answered ${response.status}`);
	const s = response.json;
	console.log(`demo clock ${s.clock}, ${s.running ? 'running' : 'paused'} at speed ${s.speed}${s.untilMinute != null ? `, stops at ${clock(s.untilMinute)}` : ''}; ${s.devices.length} sensors`);
}

async function stop() {
	const state = readState();
	const sensors = `${SIMULATION}/api/v1/simulation/sensors`;
	await call('POST', `${sensors}/pause`, { key: state.simulationKey });
	await call('PUT', `${sensors}/devices`, { key: state.simulationKey, body: { devices: [] } });
	console.log('demo-local: simulator paused and sensors unloaded. The devices stay registered for the next run.');
}

const command = process.argv[2];
switch (command) {
	case 'prepare':
		prepare();
		break;
	case 'start':
		await start();
		break;
	case 'status':
		await status();
		break;
	case 'stop':
		await stop();
		break;
	case 'accounts':
		accounts();
		break;
	case 'code': {
		const admin = readState().people.find((p) => p.totpSecret);
		console.log(`${admin.userName}: ${totp(admin.totpSecret)} (valid for ${30 - Math.floor((Date.now() % 30_000) / 1000)} s)`);
		break;
	}
	default:
		console.log(
			'usage: node scripts/demo-local.mjs prepare | start [--at 17:40] [--until 19:40] [--speed 1] [--sensors all|events] [--sites both|DMO|AUH-TA] [--no-align] | status | stop | accounts | code'
		);
		process.exit(command ? 1 : 0);
}
