import { expect, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { accounts, call, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-021: the device registry through the API. Registering a device and rotating its credential are critical
// actions (401 insufficient_user_authentication without a second factor in the last 15 minutes, RFC 9470); the
// credential is returned once, with Cache-Control no-store, and never again; a device starts in Commissioning, goes
// Online after a calibration at or above 95 percent and back to Commissioning when moved; a supervisor cannot retire
// and an administrator retires only with a recent second factor;
// a retired device keeps no credential and changes no more; other sites and other roles see nothing.

test.skip(!databaseAvailable, 'devices need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const api = `${admin}/devices`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const random = (n: number) => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');
const site = `DV${random(4)}`;
let passwordOnly: string;
let withSecondFactor: string;
let levelId: string;
let deviceId: string;
let credential: string;

const body = (code: string, overrides: Record<string, unknown> = {}, placement: Record<string, unknown> = {}) => ({
	code,
	family: 'StereoVision',
	model: 'PC2SE',
	transport: 'HttpsPush',
	dialect: 'Xovis',
	clockSource: 'Ntp',
	...overrides,
	placement: { levelId, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: 'Snake A', ...placement }
});

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().stepUpAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status()).toBe(200);
	withSecondFactor = (await signedIn.json()).accessToken;
	passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;

	const create = async (path: string, data: unknown) => {
		const response = await call('POST', `${admin}/${path}`, { token: withSecondFactor, data });
		expect(response.status(), `${path}: ${await response.text()}`).toBe(201);
		const json = await response.json();
		return (json.id ?? json.profile?.id) as string;
	};
	expect((await call('POST', `${admin}/sites`, { token: withSecondFactor, data: { code: site, name: `E2E devices ${site}` } })).status()).toBe(201);
	const airport = await create('airports', { iataCode: random(3), name: `E2E devices ${site}`, timeZoneId: 'Asia/Dubai' });
	const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'Terminal 1', siteCode: site });
	levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	const draft = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals' });
	const queue = await create(`zone-profiles/${draft}/zones`, { name: 'Snake A', kind: 'Queue', levelId, polygon: '10 10,34 10,34 22,10 22' });
	await create(`zone-profiles/${draft}/lines`, { name: 'Entry A', role: 'Entry', levelId, startX: 10, startY: 12, endX: 10, endY: 16, zoneId: queue });
	await create(`zone-profiles/${draft}/lines`, { name: 'Exit A', role: 'Exit', levelId, startX: 30, startY: 22, endX: 34, endY: 22, zoneId: queue });
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token: withSecondFactor })).json();
	expect((await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token: withSecondFactor, data: { geometryHash } })).status()).toBe(200);
});

test('registering needs a recent second factor and returns the credential once', async () => {
	const refused = await call('POST', api, { token: passwordOnly, data: body('S-1') });
	expect(refused.status(), 'register without a recent second factor').toBe(401);
	expect(refused.headers()['www-authenticate']).toContain('insufficient_user_authentication');
	const handler = (await signIn(accounts().HandlerStationManager)).accessToken;
	expect((await call('POST', api, { token: handler, data: body('S-1') })).status(), 'handler').toBe(403);

	const registered = await call('POST', api, { token: withSecondFactor, data: body('S-1') });
	expect(registered.status(), await registered.text()).toBe(201);
	expect(registered.headers()['cache-control']).toContain('no-store');
	const json = await registered.json();
	deviceId = json.device.id;
	credential = json.credential;
	expect(credential).toMatch(/^ardk_[A-Za-z0-9_-]{43}$/);
	expect(json.device.state).toBe('Commissioning');
	expect(json.device.footprint.text).toBe('10 x 10 m');
	expect(json.device.footprint.note).toContain('Assumed');

	const read = await (await call('GET', `${api}/${deviceId}`, { token: passwordOnly })).text();
	expect(read).toContain(credential.slice(0, 13));
	expect(read, 'a read never shows the credential').not.toContain(credential.slice(13));
	expect(createHash('sha256').update(credential).digest('hex')).toMatch(/^[0-9a-f]{64}$/);
});

test('registration refuses duplicates, numbers for kinds, hidden characters and footprints that miss the zone', async () => {
	expect((await call('POST', api, { token: withSecondFactor, data: body('S-1') })).status(), 'same code').toBe(409);
	expect((await call('POST', api, { token: withSecondFactor, data: body('S-2', { family: '1' }) })).status(), 'family as a number').toBe(400);
	expect((await call('POST', api, { token: withSecondFactor, data: body('S-2', { model: 'PC2SE‮gpj.exe' }) })).status(), 'right-to-left override').toBe(400);
	const far = await call('POST', api, { token: withSecondFactor, data: body('S-2', {}, { x: 90 }) });
	expect(far.status()).toBe(400);
	expect(await far.text()).toContain('does not reach');
	expect((await call('POST', api, { token: withSecondFactor, data: body('S-2', {}, { queueZoneName: 'Snake Z' }) })).status(), 'unknown zone').toBe(400);
	const footprint = await (await call('GET', `${api}/assumed-footprint?family=Lidar&mountingHeightMetres=8`, { token: passwordOnly })).json();
	expect(footprint.text).toBe('10 m radius');
});

test('a calibration at 95 percent sets the device online and a move sends it back to commissioning', async () => {
	const calibrate = (accuracy: number) =>
		call('POST', `${api}/${deviceId}/calibrations`, { token: passwordOnly, data: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: accuracy, waitTimeErrorMinutes: 0.5 } });
	const failed = await calibrate(94.9);
	expect(failed.status()).toBe(201);
	expect((await failed.json()).deviceState).toBe('Commissioning');
	const passed = await calibrate(95);
	expect((await passed.json()).deviceState).toBe('Online');

	const moved = await call('PUT', `${api}/${deviceId}/placement`, {
		token: passwordOnly,
		data: { levelId, x: 24, y: 16, mountingHeightMetres: 5, orientationDegrees: 45, queueZoneName: 'Snake A' }
	});
	expect((await moved.json()).state).toBe('Commissioning');
	expect((await (await call('GET', `${api}/${deviceId}/calibrations`, { token: passwordOnly })).json()).length).toBe(2);
});

test('rotation replaces the credential, other sites see nothing, and retiring revokes it', async () => {
	expect((await call('POST', `${api}/${deviceId}/credential`, { token: passwordOnly })).status(), 'rotation without a second factor').toBe(401);
	const rotated = await call('POST', `${api}/${deviceId}/credential`, { token: withSecondFactor });
	expect(rotated.status()).toBe(200);
	expect((await rotated.json()).credential).not.toBe(credential);

	const otherSite = (await signIn(accounts().siteUser)).accessToken;
	expect((await call('GET', `${api}/${deviceId}`, { token: otherSite })).status(), 'another site').toBe(404);
	expect((await call('POST', `${api}/${deviceId}/retire`, { token: otherSite })).status(), 'a supervisor cannot retire').toBe(403);
	expect((await call('DELETE', `${api}/${deviceId}`, { token: passwordOnly })).status(), 'a calibrated device is retired, not removed').toBe(409);

	expect((await call('POST', `${api}/${deviceId}/retire`, { token: passwordOnly })).status(), 'retiring without a second factor').toBe(401);
	const retired = await call('POST', `${api}/${deviceId}/retire`, { token: withSecondFactor });
	expect((await retired.json()).state).toBe('Retired');
	expect((await call('PUT', `${api}/${deviceId}`, { token: passwordOnly, data: { model: 'PC3', transport: 'Mqtt', dialect: 'Xovis', clockSource: 'Ptp' } })).status()).toBe(409);
	const search = await (await call('GET', `${api}?siteCode=${site}&state=Retired`, { token: passwordOnly })).json();
	expect(search.totalCount).toBe(1);
});
