import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-022: devices authenticate on Ariva.Api.Ingest with their own credential and nothing else. Missing key 401, wrong
// key 401, a user token on a device endpoint 401, a device key on a user endpoint 401, another device's zone 403; the
// allowed networks and a rotation take effect at once with the cache backplane (Redis), within a minute without it
// (Ingest caches the device for at most a minute), so those checks poll.

test.skip(!databaseAvailable, 'device authentication needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const ingest = `${hosts.ingest}/api/v1/ingest`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const random = (n: number) => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');
const site = `DA${random(4)}`;
let withSecondFactor: string;
let deviceId: string;
let key: string;

const asDevice = (url: string, credential: string, address?: string) => call('GET', url, { headers: { 'X-Ariva-Device-Key': credential }, address });

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().stepUpAdmin;
	withSecondFactor = (await (await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) })).json()).accessToken;
	const create = async (path: string, data: unknown) => {
		const response = await call('POST', `${admin}/${path}`, { token: withSecondFactor, data });
		expect(response.status(), `${path}: ${await response.text()}`).toBe(201);
		const json = await response.json();
		return (json.id ?? json.profile?.id) as string;
	};
	expect((await call('POST', `${admin}/sites`, { token: withSecondFactor, data: { code: site, name: site } })).status()).toBe(201);
	const airport = await create('airports', { iataCode: random(3), name: `E2E device auth ${site}`, timeZoneId: 'Asia/Dubai' });
	const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'T1', siteCode: site });
	const levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'L0', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	const draft = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals' });
	for (const [name, x] of [['Snake A', 10], ['Snake B', 50]] as const) {
		const zoneId = await create(`zone-profiles/${draft}/zones`, { name, kind: 'Queue', levelId, polygon: `${x} 10,${x + 24} 10,${x + 24} 22,${x} 22` });
		await create(`zone-profiles/${draft}/lines`, { name: `${name} entry`, role: 'Entry', levelId, startX: x, startY: 12, endX: x, endY: 16, zoneId });
		await create(`zone-profiles/${draft}/lines`, { name: `${name} exit`, role: 'Exit', levelId, startX: x + 20, startY: 22, endX: x + 24, endY: 22, zoneId });
	}
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token: withSecondFactor })).json();
	expect((await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token: withSecondFactor, data: { geometryHash } })).status()).toBe(200);
	const registered = await call('POST', `${admin}/devices`, {
		token: withSecondFactor,
		data: {
			code: 'S-1', family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Xovis', clockSource: 'Ntp',
			placement: { levelId, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: 'Snake A' }
		}
	});
	expect(registered.status(), await registered.text()).toBe(201);
	({ credential: key, device: { id: deviceId } } = await registered.json());
});

test('missing key 401, wrong key 401, user token 401, own credential 200', async () => {
	expect((await call('GET', `${ingest}/device`)).status(), 'missing key').toBe(401);
	expect((await asDevice(`${ingest}/device`, key.slice(0, 47) + (key[47] === 'A' ? 'B' : 'A'))).status(), 'wrong key').toBe(401);
	const user = (await signIn(accounts().SystemAdministrator)).accessToken;
	expect((await call('GET', `${ingest}/device`, { token: user })).status(), 'user token on a device endpoint').toBe(401);
	expect((await call('GET', `${admin}/devices`, { headers: { 'X-Ariva-Device-Key': key } })).status(), 'device key on a user endpoint').toBe(401);
	const self = await asDevice(`${ingest}/device`, key);
	expect(self.status()).toBe(200);
	expect((await self.json()).queueZoneName).toBe('Snake A');
	const bearer = await call('GET', `${ingest}/device`, { token: key });
	expect(bearer.status(), 'Bearer works too').toBe(200);
});

test("another device's zone is 403, its own zone's geometry 200", async () => {
	const own = await asDevice(`${ingest}/zones/${encodeURIComponent('Snake A')}`, key);
	expect(own.status()).toBe(200);
	expect((await own.json()).lines.length).toBe(2);
	expect((await asDevice(`${ingest}/zones/${encodeURIComponent('Snake B')}`, key)).status(), "another device's zone").toBe(403);
});

test('allowed networks and rotation take effect within the cache lifetime', async () => {
	test.setTimeout(240_000);
	const within = { timeout: 75_000, intervals: [1_000, 2_000, 5_000] };
	const access = (allowedSources: string[]) => call('PUT', `${admin}/devices/${deviceId}/access`, { token: withSecondFactor, data: { allowedSources, clientCertificateSha256: '' } });
	expect((await access(['10.20.0.0/24'])).status()).toBe(200);
	await expect.poll(async () => (await asDevice(`${ingest}/device`, key, '10.20.1.17')).status(), { ...within, message: 'outside the network' }).toBe(401);
	expect((await asDevice(`${ingest}/device`, key, '10.20.0.17')).status(), 'inside the network').toBe(200);
	expect((await access([])).status()).toBe(200);
	await expect.poll(async () => (await asDevice(`${ingest}/device`, key, '10.20.1.17')).status(), within).toBe(200);

	const rotated = (await (await call('POST', `${admin}/devices/${deviceId}/credential`, { token: withSecondFactor })).json()).credential as string;
	await expect.poll(async () => (await asDevice(`${ingest}/device`, key)).status(), { ...within, message: 'the old key' }).toBe(401);
	expect((await asDevice(`${ingest}/device`, rotated)).status(), 'the new key').toBe(200);
});
