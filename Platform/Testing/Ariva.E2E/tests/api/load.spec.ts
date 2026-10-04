import { HubConnection, HubConnectionBuilder, HubConnectionState, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { expect, test } from '@playwright/test';
import { createClient } from 'redis';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { accounts, databaseAvailable, signIn } from '../support/accounts';
import { hosts, kafkaAvailable } from '../support/hosts';
import { type LoadSite, provisionLoadSite, screenSessions } from '../support/load-site';

// ARV-071: the load harness (Platform/Testing/Ariva.LoadTests) against the run's hosts. This spec provisions what a
// site would have (a published queue zone, 41 sensors, the displays, signed-in screen sessions) and runs the harness:
// 40 sensors push track batches at their rate and three times it while the screens hold the live hub and the displays
// poll their boards; then bodies over 256 KB and a device past its rate. Smoke size here (the E2E gate);
// ARIVA_LOAD_MODE=full is the lab measurement recorded in wiki 05. Credentials reach the harness in its environment,
// never on its command line.

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
test.skip(!databaseAvailable || !kafkaAvailable || !redisUrl, 'the load run needs the E2E database, Kafka and Redis');
// No retries: a retry would provision the site again, and a load run is measured once.
test.describe.configure({ mode: 'serial', retries: 0 });

const full = process.env.ARIVA_LOAD_MODE === 'full';
const here = path.dirname(fileURLToPath(import.meta.url));
const harness = path.resolve(here, '..', '..', '..', 'Ariva.LoadTests', 'bin', process.env.ARIVA_E2E_CONFIGURATION || 'Debug', 'net10.0', 'Ariva.LoadTests.dll');
const output = path.resolve(here, '..', '..', '..', '..', '..', '.verify', 'load', full ? 'full' : 'smoke');
const screens = full ? 150 : 40;

let loadSite: LoadSite;
let screenTokens: string[] = [];
const deviceKeys: string[] = [];
const displayKeys: string[] = [];
const displayCodes: string[] = [];

test.beforeAll(async () => {
	test.setTimeout(180_000);
	loadSite = await provisionLoadSite(accounts().loadAdmin, 'LD');
	const { site, zone, levelId, create } = loadSite;

	// 40 measured sensors and one kept apart for the flood. Device and display codes carry the run's site.
	for (let i = 1; i <= 41; i++) {
		const device = await create('devices', {
			code: `${site}-${String(i).padStart(2, '0')}`, family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Canonical', clockSource: 'Ntp',
			placement: { levelId, x: 12 + (i % 20), y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: zone }
		});
		deviceKeys.push(device.credential);
	}

	for (let i = 1; i <= (full ? 50 : 10); i++) {
		const code = `${site}-B${i}`;
		const display = await create('displays', {
			siteCode: site, code, name: `Board ${i}`, location: 'Arrivals hall', orientation: 'Landscape', languages: ['en'],
			bandMinutes: 5, hysteresisMinutes: 2, staleSeconds: 120, entries: [{ zone, labels: { en: 'All passports' } }], fallback: { en: 'Please follow the signs' }
		});
		displayKeys.push(display.credential);
		displayCodes.push(code);
	}

	// Five screens a session, inside the hub's eight per session: a wall of screens signs in more than once.
	screenTokens = await screenSessions(accounts().loadScreen, screens);
});

test('sensors, screens and displays under load: no failures, and the limits answer 413 and 429', async ({}, testInfo) => {
	test.setTimeout(full ? 900_000 : 240_000);
	expect(fs.existsSync(harness), `the harness is built (${harness})`).toBe(true);
	// Only what dotnet needs to start, and the run's settings: the runner's own secrets stay out of the harness.
	const passThrough = ['PATH', 'HOME', 'TMPDIR', 'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'DOTNET_NOLOGO', 'SystemRoot', 'USERPROFILE', 'TEMP', 'TMP'];
	const env = {
		...Object.fromEntries(passThrough.filter((name) => process.env[name] !== undefined).map((name) => [name, process.env[name]!])),
		ARIVA_LOAD_MODE: full ? 'full' : 'smoke',
		ARIVA_LOAD_INGEST_URL: hosts.ingest,
		ARIVA_LOAD_MAIN_URL: hosts.main,
		ARIVA_LOAD_ZONE_KEY: `${loadSite.site}/${loadSite.zone}`,
		ARIVA_LOAD_DEVICE_KEYS: deviceKeys.slice(0, 40).join(','),
		ARIVA_LOAD_FLOOD_KEY: deviceKeys[40],
		ARIVA_LOAD_DISPLAY_KEYS: displayKeys.join(','),
		ARIVA_LOAD_DISPLAY_CODES: displayCodes.join(','),
		ARIVA_LOAD_ACCESS_TOKENS: screenTokens.join(','),
		ARIVA_LOAD_REDIS_URL: redisUrl,
		ARIVA_LOAD_REDIS_INSTANCE: process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:',
		ARIVA_LOAD_OUTPUT: output
	};
	const log: string[] = [];
	const code = await new Promise<number | null>((resolve) => {
		const child = spawn('dotnet', [harness], { env, stdio: ['ignore', 'pipe', 'pipe'] });
		child.stdout.on('data', (d) => log.push(String(d)));
		child.stderr.on('data', (d) => log.push(String(d)));
		child.on('exit', resolve);
	});
	const report = JSON.parse(fs.readFileSync(path.join(output, 'load-report.json'), 'utf8'));
	await testInfo.attach('load-report.md', { path: path.join(output, 'load-report.md'), contentType: 'text/markdown' });
	expect(report.problems, log.join('')).toEqual([]);
	expect(code).toBe(0);

	const [steady, burst, oversize, flood] = report.ingest;
	expect(steady.outcomes['202']).toBeGreaterThan(0);
	expect(burst.outcomes['202']).toBeGreaterThan(0);
	expect(steady.failures + burst.failures).toBe(0);
	expect(oversize.outcomes['413']).toBe(oversize.requests);
	expect(flood.outcomes['429']).toBeGreaterThan(0);
	expect(flood.outcomes['429 without Retry-After'] ?? 0).toBe(0);
	expect(report.live.screens).toBe(screens);
	expect(report.live.delivered).toBeGreaterThanOrEqual(0.95 * report.live.published * report.live.screens);
	expect(report.live.published).toBeGreaterThan(0);
	expect(report.live.boards.outcomes['200']).toBe(report.live.boards.requests);
});

test('one session holds at most eight hub connections: the ninth is closed, the eight keep receiving (CWE-400)', async () => {
	const zoneKey = `${loadSite.site}/${loadSite.zone}`;
	const { accessToken } = await signIn(accounts().loadScreen);
	const connect = () =>
		new HubConnectionBuilder()
			.withUrl(`${hosts.main}/hubs/live`, { transport: HttpTransportType.WebSockets, skipNegotiation: true, accessTokenFactory: () => accessToken })
			.configureLogging(LogLevel.None)
			.build();
	const eight: HubConnection[] = [];
	const received = new Array<number>(8).fill(0);
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		for (let i = 0; i < 8; i++) {
			const hub = connect();
			hub.on('zone', (snapshot: { zoneKey: string }) => {
				if (snapshot.zoneKey === zoneKey) received[i]++;
			});
			await hub.start();
			await hub.invoke('JoinZone', zoneKey);
			eight.push(hub);
		}

		// The ninth on the same session: the hub closes it on connect (too_many_connections), whether or not start() saw it.
		const ninth = connect();
		await ninth.start().catch(() => undefined);
		await expect.poll(() => ninth.state, { timeout: 10_000 }).toBe(HubConnectionState.Disconnected);
		await expect(ninth.invoke('JoinZone', zoneKey)).rejects.toThrow();

		const snapshot = {
			zoneKey, minuteUtc: new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000).toISOString(), queueLength: 9, lengthFromSensors: true,
			lengthDegraded: false, nowcastMinutes: 2.5, throughputPerMinute: 4, noService: null, nowcastDegraded: false, publishedUtc: new Date().toISOString()
		};
		await expect
			.poll(async () => {
				await redis.publish(`${process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:'}live:zones`, JSON.stringify(snapshot));
				return received.every((n) => n > 0);
			}, { timeout: 15_000, intervals: [500, 1000] })
			.toBe(true);
		expect(eight.every((hub) => hub.state === HubConnectionState.Connected)).toBe(true);

		// The cap counts open connections: once one closes, the session may open another.
		await eight.pop()!.stop();
		const replacement = connect();
		await expect.poll(async () => {
			await replacement.start().catch(() => undefined);
			return replacement.state;
		}, { timeout: 15_000, intervals: [1000] }).toBe(HubConnectionState.Connected);
		await replacement.invoke('JoinZone', zoneKey);
		eight.push(replacement);
	} finally {
		await Promise.all(eight.map((hub) => hub.stop()));
		await redis.quit();
	}
});
