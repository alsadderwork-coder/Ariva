import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, totpCode } from '../support/accounts';
import { hosts, kafkaAvailable } from '../support/hosts';

// ARV-028: the sensor emulator plays a 10-minute accelerated run (18:15 to 18:25 at speed 60) of the reference day to
// Ingest as devices registered for the DMO demo airport (canonical and Xovis), and Ingest accepts exactly the events the
// emulator expected. Ingest answers 202 only once a push's events are written to Kafka, so the accepted counts are the
// events that reached the sensing topics. S-17 is offline from 18:20 and sends half the minutes. The controls need the
// operator key's control scope; device credentials never come back out of the simulator.

test.skip(!databaseAvailable, 'the emulator run needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true) and the DMO demo seed');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const sensors = `${hosts.simulation}/api/v1/simulation/sensors`;
const simulationKey = process.env.ARIVA_E2E_SIMULATION_KEY ?? '';
const operator = { Authorization: `Bearer ${simulationKey}` };

type Device = { sensor: string; zone: string; level: string; dialect: 'Canonical' | 'Xovis' };
const plan: Device[] = [
	{ sensor: 'S-15', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-17', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-50', zone: 'CI-C', level: 'DEP', dialect: 'Xovis' }
];
const credentials: { sensor: string; dialect: string; credential: string }[] = [];
const registeredIds: string[] = [];
let adminToken = '';

test.beforeAll(async () => {
	const account = accounts().emulatorAdmin;
	const token = (await (await login(account.userName, account.password, undefined, undefined, { code: totpCode(account.totpSecret!) })).json())
		.accessToken as string;
	adminToken = token;
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
	const run = Date.now().toString(36).toUpperCase();
	for (const device of plan) {
		const { x, y } = centre(device.zone);
		const registered = await call('POST', `${admin}/devices`, {
			token,
			data: {
				code: `${device.sensor}-${run}`, family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: device.dialect, clockSource: 'Ntp',
				placement: { levelId: levelId(device.level), x, y, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: device.zone }
			}
		});
		expect(registered.status(), await registered.text()).toBe(201);
		const body = await registered.json();
		registeredIds.push(body.device.id);
		credentials.push({ sensor: device.sensor, dialect: device.dialect, credential: body.credential });
	}
});

// Leave no working credential behind, in the simulator or in Ariva, when the suite runs against a shared environment.
test.afterAll(async () => {
	await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: [] } });
	for (const id of registeredIds) await call('POST', `${admin}/devices/${id}/retire`, { token: adminToken });
});

test('the controls need the control scope', async () => {
	expect((await call('POST', `${sensors}/start`, { data: { minute: 1 } })).status()).toBe(401);
	expect((await call('POST', `${sensors}/start`, { headers: { Authorization: `Bearer ${credentials[0]?.credential ?? 'x'}` }, data: { minute: 1 } })).status()).toBe(401);
	expect((await call('GET', sensors, { headers: operator })).status()).toBe(200);
});

test('a 10-minute accelerated run reaches Ingest event for event', async () => {
	test.skip(!kafkaAvailable, 'an accepted push needs Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP)');
	test.setTimeout(90_000);
	const loaded = await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: credentials } });
	expect(loaded.status(), await loaded.text()).toBe(200);
	expect(await loaded.text()).not.toContain('ardk_');

	const started = await call('POST', `${sensors}/start`, { headers: operator, data: { minute: 1095, speed: 60, untilMinute: 1105 } });
	expect(started.status(), await started.text()).toBe(200);

	let status = await (await call('GET', sensors, { headers: operator })).json();
	for (let i = 0; i < 60 && status.running; i++) {
		await new Promise((resolve) => setTimeout(resolve, 500));
		status = await (await call('GET', sensors, { headers: operator })).json();
	}
	expect(status.running).toBe(false);
	expect(status.nextMinute).toBe(1105);
	expect(JSON.stringify(status)).not.toContain('ardk_');

	const bySensor = Object.fromEntries(status.devices.map((d: { sensor: string }) => [d.sensor, d]));
	expect(bySensor['S-15'].pushes).toBe(10);
	expect(bySensor['S-17'].pushes, 'S-17 is offline from 18:20').toBe(5);
	expect(bySensor['S-50'].pushes).toBe(10);
	for (const device of status.devices) {
		expect(device.failures, `${device.sensor}: ${device.lastError}`).toBe(0);
		expect(device.acceptedEvents, device.sensor).toBe(device.expectedEvents);
	}
	expect(bySensor['S-15'].acceptedEvents, 'the Visitors wave crosses the lines').toBeGreaterThan(20);
	expect(bySensor['S-50'].acceptedEvents, 'Xovis: two line intervals and the occupancy a minute').toBe(30);
});
