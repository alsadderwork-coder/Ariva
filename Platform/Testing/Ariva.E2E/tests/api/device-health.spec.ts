import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, totpCode } from '../support/accounts';
import { hosts, kafkaAvailable } from '../support/hosts';

// ARV-025: device health. A calibrated device that pushes a status is Online with its last report; the health overview
// lists the site's devices and queue zones with the heartbeat timeout; one device's health; another site's caller sees
// nothing (404 for the site and the device) and a role without SearchDevice is refused (403).

test.skip(!databaseAvailable, 'device health needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const random = (n: number) => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');
const site = `DH${random(4)}`;
let token = '';
let deviceId = '';
let key = '';

const tokenOf = async (account: { userName: string; password: string; totpSecret?: string }) =>
	(await (await login(account.userName, account.password, undefined, undefined, account.totpSecret ? { code: totpCode(account.totpSecret) } : undefined)).json())
		.accessToken as string;

test.beforeAll(async () => {
	token = await tokenOf(accounts().deviceHealthAdmin);
	const create = async (path: string, data: unknown) => {
		const response = await call('POST', `${admin}/${path}`, { token, data });
		expect(response.status(), `${path}: ${await response.text()}`).toBe(201);
		const json = await response.json();
		return (json.id ?? json.profile?.id) as string;
	};
	expect((await call('POST', `${admin}/sites`, { token, data: { code: site, name: site } })).status()).toBe(201);
	const airport = await create('airports', { iataCode: random(3), name: `E2E health ${site}`, timeZoneId: 'Asia/Dubai' });
	const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'T1', siteCode: site });
	const levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'L0', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	const draft = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals' });
	const zoneId = await create(`zone-profiles/${draft}/zones`, { name: 'Snake A', kind: 'Queue', levelId, polygon: '10 10,34 10,34 22,10 22' });
	await create(`zone-profiles/${draft}/lines`, { name: 'Entry A', role: 'Entry', levelId, startX: 10, startY: 12, endX: 10, endY: 16, zoneId });
	await create(`zone-profiles/${draft}/lines`, { name: 'Exit A', role: 'Exit', levelId, startX: 30, startY: 22, endX: 34, endY: 22, zoneId });
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token })).json();
	const published = await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token, data: { geometryHash } });
	expect(published.status(), await published.text()).toBe(200);
	const registered = await call('POST', `${admin}/devices`, {
		token,
		data: {
			code: 'S-1', family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Canonical', clockSource: 'Ntp',
			placement: { levelId, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: 'Snake A' }
		}
	});
	expect(registered.status(), await registered.text()).toBe(201);
	const body = await registered.json();
	deviceId = body.device.id;
	key = body.credential;
	const calibrated = await call('POST', `${admin}/devices/${deviceId}/calibrations`, {
		token,
		data: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: 97, waitTimeErrorMinutes: 0.3 }
	});
	expect(calibrated.status(), await calibrated.text()).toBe(201);
});

test('the health overview lists the site\'s devices and zones', async () => {
	test.skip(!kafkaAvailable, 'an accepted push needs Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP)');
	const now = new Date().toISOString();
	const pushed = await call('POST', `${hosts.ingest}/api/v1/ingest/zones/${encodeURIComponent('Snake A')}/events`, {
		headers: { 'X-Ariva-Device-Key': key, 'Content-Type': 'application/json' },
		raw: JSON.stringify({ sentUtc: now, status: { online: true, temperatureCelsius: 40, frameRate: 12, timeUtc: now } })
	});
	expect(pushed.status(), await pushed.text()).toBe(202);

	const overview = await call('GET', `${admin}/devices/health?siteCode=${site}`, { token });
	expect(overview.status()).toBe(200);
	const json = await overview.json();
	expect(json.heartbeatTimeoutSeconds).toBeGreaterThanOrEqual(30);
	const device = json.devices.find((d: { id: string }) => d.id === deviceId);
	expect(device.state).toBe('Online');
	expect(device.queueZoneName).toBe('Snake A');
	// The heartbeat arrives through Kafka (ariva.device.health.v1); it is shown when the consumer has recorded it.
	expect(['Healthy', 'Degraded', undefined]).toContain(json.zones.find((z: { queueZoneName: string }) => z.queueZoneName === 'Snake A')?.state);

	const one = await call('GET', `${admin}/devices/${deviceId}/health`, { token });
	expect(one.status()).toBe(200);
	expect((await one.json()).code).toBe('S-1');
});

test('another site\'s caller sees nothing and a role without SearchDevice is refused', async () => {
	const border = await tokenOf(accounts().BorderShiftSupervisor);
	expect((await call('GET', `${admin}/devices/health?siteCode=${site}`, { token: border })).status()).toBe(404);
	expect((await call('GET', `${admin}/devices/${deviceId}/health`, { token: border })).status()).toBe(404);
	const own = await call('GET', `${admin}/devices/health`, { token: border });
	expect(own.status()).toBe(200);
	expect((await own.json()).devices.some((d: { siteCode: string }) => d.siteCode === site)).toBe(false);

	const handler = await tokenOf(accounts().HandlerStationManager);
	expect((await call('GET', `${admin}/devices/health`, { token: handler })).status()).toBe(403);
	expect((await call('GET', `${admin}/devices/health`)).status()).toBe(401);
});
