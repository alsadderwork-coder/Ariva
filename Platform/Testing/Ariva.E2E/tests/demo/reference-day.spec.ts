import { expect, test, type Page } from '@playwright/test';
import { accounts, call, databaseAvailable, login, totpCode } from '../support/accounts';
import { hosts, kafkaAvailable } from '../support/hosts';
import { signInThroughUi } from '../support/web-auth';

// ARV-064: the scripted demo. The simulator plays the reference day (seed 9303, the fictional Demo International Airport
// DMO) from 17:40 in real time through the whole pipeline: its sensor emulator pushes to Ariva.Api.Ingest as devices
// registered and calibrated here, Ingest writes Kafka, Ariva.Api.Stream computes the queues, keeps the live snapshots in
// Redis and evaluates the seeded alert rules, and Ariva.Api.Main relays both to the live screens. Nothing is planted.
// The three scripted events, each shown to the role that owns it:
//   18:05          a visitor-heavy arrival wave: the arrivals Visitors (A-VIS) nowcast passes 15 minutes and R-001 fires
//                  for the border shift supervisor;
//   18:20 to 18:30 sensor S-17 over the arrivals hall is offline: R-003 names it, and the device goes Offline;
//   19:10          handler B's check-in island C (CI-C) after a shift change: the 15-minute bins breach the 15-minute
//                  P90 target and R-004 fires for the handler station manager, once the bin has ended.
// The run takes the evening in real time (about two hours); ARIVA_DEMO_START and ARIVA_DEMO_END (demo minutes, 1060 is
// 17:40) shorten it, and an event outside the range is skipped. Run it alone: ARIVA_E2E_DEMO=1 npx playwright test
// --project=demo, or node scripts/verify.mjs demo. The runbook is wiki/10-Operations-Runbook.md, section 4.11.

test.skip(!databaseAvailable || !kafkaAvailable, 'the demo needs the E2E database, Kafka and Redis');
test.describe.configure({ mode: 'serial' });

const start = Number(process.env.ARIVA_DEMO_START ?? 1060);
const end = Number(process.env.ARIVA_DEMO_END ?? 1180);
const admin = `${hosts.main}/api/v1/admin`;
const sensors = `${hosts.simulation}/api/v1/simulation/sensors`;
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };

/** The sensors of the three events and the queues they show, by the scenario's sensor plan (ScenarioModel.cs). */
const plan = [
	{ sensor: 'S-15', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-17', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	// The A-VIS queue includes its overflow band A-OV: without the band's lead sensor the queue has no full sensor
	// reading and its length is only the engine's estimate, which R-001 does not judge.
	{ sensor: 'S-25', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-13', zone: 'A-CIT', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-21', zone: 'A-RES', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-50', zone: 'CI-C', level: 'DEP', dialect: 'Xovis' },
	{ sensor: 'S-55', zone: 'CI-D', level: 'DEP', dialect: 'Xovis' }
] as const;

const clock = (minute: number) => `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`;

let token = '';

async function signInAdmin(): Promise<string> {
	const account = accounts().demoAdmin;
	const response = await login(account.userName, account.password, undefined, undefined, { code: totpCode(account.totpSecret!) });
	expect(response.status(), 'demo administrator sign-in').toBe(200);
	return (await response.json()).accessToken as string;
}

async function simulatorMinute(): Promise<{ running: boolean; nextMinute: number }> {
	return (await call('GET', sensors, { headers: operator })).json();
}

/** Waits until the demo clock has completed the given minute (the sensors have pushed it). */
async function waitForDemoMinute(minute: number): Promise<void> {
	for (;;) {
		const status = await simulatorMinute();
		if (status.nextMinute > minute) return;
		expect(status.running, `the demo clock stopped before ${clock(minute)}`).toBe(true);
		await new Promise((resolve) => setTimeout(resolve, 15_000));
	}
}

/**
 * Re-checks a screen condition every 20 seconds until it holds or the time runs out. The screens update live, so the
 * page is only reloaded every five minutes, as a safety net.
 */
async function eventually(page: Page, check: () => Promise<boolean>, minutes: number, what: string): Promise<void> {
	const until = Date.now() + minutes * 60_000;
	let reloaded = Date.now();
	for (;;) {
		if (await check()) return;
		if (Date.now() > until) throw new Error(`not shown within ${minutes} minutes: ${what}`);
		await new Promise((resolve) => setTimeout(resolve, 20_000));
		if (Date.now() - reloaded > 5 * 60_000) {
			await page.reload();
			reloaded = Date.now();
		}
	}
}

/** The live hub's messages (truncated) and the console, attached to the test when it fails. */
function record(page: Page): string[] {
	const lines: string[] = [];
	page.on('console', (message) => lines.push(`${new Date().toISOString()} console ${message.type()}: ${message.text().slice(0, 300)}`));
	page.on('websocket', (socket) => socket.on('framereceived', (frame) => lines.push(`${new Date().toISOString()} hub: ${String(frame.payload).slice(0, 300)}`)));
	return lines;
}

test.afterEach(async ({ page }, info) => {
	if (info.status !== info.expectedStatus) await info.attach('screen', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
});

/** The wait (nowcast minutes) a zone row shows in its third column. */
async function shownWait(page: Page, zone: string): Promise<number> {
	const text = (await page.locator(`[data-testid="zone-row"][data-zone="${zone}"] td`).nth(1).textContent().catch(() => '')) ?? '';
	const value = Number(text.trim().replace(/,/g, ''));
	return Number.isFinite(value) ? value : 0;
}

test.beforeAll(async () => {
	test.setTimeout(30 * 60_000);
	token = await signInAdmin();
	const profiles = await (await call('GET', `${admin}/zone-profiles?siteCode=DMO`, { token })).json();
	expect(profiles.length, 'the DMO demo seed has published its zone profile').toBeGreaterThan(0);
	const profile = await (await call('GET', `${admin}/zone-profiles/${profiles[0].id}`, { token })).json();
	const levels = await (await call('GET', `${admin}/levels?siteCode=DMO`, { token })).json();
	const levelId = (code: string) => (levels.data ?? levels).find((l: { code: string }) => l.code === code).id as string;
	const centre = (name: string) => {
		const zone = profile.zones.find((z: { name: string }) => z.name === name);
		const points: number[][] = typeof zone.polygon === 'string'
			? zone.polygon.split(',').map((p: string) => p.trim().split(' ').map(Number))
			: zone.polygon.map((p: { x: number; y: number }) => [p.x, p.y]);
		return { x: points.reduce((s, p) => s + p[0], 0) / points.length, y: points.reduce((s, p) => s + p[1], 0) / points.length };
	};

	// Each sensor is a DMO device under its own code, so R-003 names S-17. A device from an earlier run is reused with a new
	// credential (a calibrated device cannot be deleted and its code stays taken).
	const credentials: { sensor: string; dialect: string; credential: string }[] = [];
	const existing = await (await call('GET', `${admin}/devices?siteCode=DMO&pageSize=200`, { token })).json();
	const known = new Map<string, { id: string; state: string }>(((existing.data ?? existing) as { code: string; id: string; state: string }[]).map((d) => [d.code, d]));
	for (const device of plan) {
		let id: string;
		let credential: string;
		const found = known.get(device.sensor);
		if (found && found.state !== 'Retired') {
			id = found.id;
			const rotated = await call('POST', `${admin}/devices/${id}/credential`, { token });
			expect(rotated.status(), await rotated.text()).toBe(200);
			credential = (await rotated.json()).credential;
		} else {
			const { x, y } = centre(device.zone);
			const registered = await call('POST', `${admin}/devices`, {
				token,
				data: {
					code: device.sensor, family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: device.dialect, clockSource: 'Ntp',
					placement: { levelId: levelId(device.level), x, y, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: device.zone }
				}
			});
			expect(registered.status(), await registered.text()).toBe(201);
			const body = await registered.json();
			id = body.device.id;
			credential = body.credential;
		}
		// Commissioned: Stream counts only calibrated devices, and only they are watched for silence (R-003).
		const calibrations = await (await call('GET', `${admin}/devices/${id}/calibrations`, { token })).json();
		if (((calibrations.data ?? calibrations) as unknown[]).length === 0) {
			const calibrated = await call('POST', `${admin}/devices/${id}/calibrations`, {
				token,
				data: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: 97, waitTimeErrorMinutes: 0.3 }
			});
			expect(calibrated.status(), await calibrated.text()).toBe(201);
		}
		credentials.push({ sensor: device.sensor, dialect: device.dialect, credential });
	}
	const loaded = await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: credentials } });
	expect(loaded.status(), await loaded.text()).toBe(200);
	// Ingest keeps device records for a minute: let it see the calibrations before the first push.
	await new Promise((resolve) => setTimeout(resolve, 65_000));

	// The 15-minute bins are aligned to the clock: start on a wall minute that puts the demo's quarter hours on the
	// wall's (17:40 is 10 past a quarter), so the 19:00, 19:15 and 19:30 bins are the scenario's.
	const offset = (((start % 15) - (new Date().getUTCMinutes() % 15)) + 15) % 15;
	const wait = offset * 60_000 - (Date.now() % 60_000) + 1_000;
	if (wait > 0) await new Promise((resolve) => setTimeout(resolve, wait));

	const started = await call('POST', `${sensors}/start`, { headers: operator, data: { minute: start, speed: 1, untilMinute: end } });
	expect(started.status(), await started.text()).toBe(200);
});

test.afterAll(async () => {
	await call('POST', `${sensors}/pause`, { headers: operator });
	await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: [] } });
});

test('18:05: the visitor wave passes 15 minutes on A-VIS and R-001 reaches the border shift supervisor', async ({ page }) => {
	test.skip(start > 1085 || end < 1092, 'the range leaves out 18:05');
	const frames = record(page);
	await waitForDemoMinute(1085);
	await signInThroughUi(page, accounts().webBorder);
	await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
	try {
		await eventually(page, async () => (await shownWait(page, 'A-VIS')) > 15, 8, 'the A-VIS nowcast above 15 minutes');
	} finally {
		await test.info().attach('hub', { body: frames.join('\n'), contentType: 'text/plain' });
	}
	const alert = page.locator('[data-testid="alert"][data-rule="R-001"]').filter({ hasText: 'A-VIS' });
	await eventually(page, async () => (await alert.count()) > 0, 6, 'R-001 for A-VIS');
	await expect(alert.first()).toContainText('Nowcast above 15 min');
	await page.screenshot({ path: test.info().outputPath('18-05-visitor-wave.png'), fullPage: true });
});

test('18:20 to 18:30: sensor S-17 is offline, R-003 names it and the device goes Offline', async ({ page }) => {
	test.skip(start > 1100 || end < 1106, 'the range leaves out 18:20');
	await waitForDemoMinute(1103);
	await signInThroughUi(page, accounts().webBorder);
	const alert = page.locator('[data-testid="alert"][data-rule="R-003"]').filter({ hasText: 'S-17' });
	await eventually(page, async () => (await alert.count()) > 0, 6, 'R-003 for S-17');
	await page.screenshot({ path: test.info().outputPath('18-20-sensor-offline.png'), fullPage: true });

	const health = await (await call('GET', `${admin}/devices/health?siteCode=DMO`, { token: await signInAdmin() })).json();
	const s17 = ((health.devices ?? health.data ?? health) as { code: string; state: string }[]).find((d) => d.code === 'S-17');
	expect(s17?.state, 'S-17 in the device health overview').toBe('Offline');
});

test('19:10: check-in island C breaches its 15-minute P90 and R-004 reaches the handler station manager', async ({ page }) => {
	test.skip(start > 1150 || end < 1175, 'the range leaves out 19:10 and the bins after it');
	await waitForDemoMinute(1160);
	await signInThroughUi(page, accounts().webHandler);
	const alert = page.locator('[data-testid="alert"][data-rule="R-004"]').filter({ hasText: 'CI-C' });
	await eventually(page, async () => (await alert.count()) > 0, Math.max(5, end - 1160 + 5), 'R-004 for CI-C');
	await page.screenshot({ path: test.info().outputPath('19-10-check-in-island-c.png'), fullPage: true });
});
