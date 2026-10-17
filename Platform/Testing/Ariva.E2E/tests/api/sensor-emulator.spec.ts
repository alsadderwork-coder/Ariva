import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, totpCode } from '../support/accounts';
import { hosts, kafkaAvailable } from '../support/hosts';

// ARV-028: the sensor emulator plays a 10-minute accelerated run (18:15 to 18:25 at speed 60) of the reference day to
// Ingest as devices registered for the DMO demo airport (canonical and Xovis), and Ingest accepts exactly the events the
// emulator expected. Ingest answers 202 only once a push's events are written to Kafka, so the accepted counts are the
// events that reached the sensing topics. S-17 is offline from 18:20 and sends half the minutes. The controls need the
// operator key's control scope; device credentials never come back out of the simulator.
// ARV-139b: the same for the AUH-TA scenario as the illustrative seed's own sensors, which it leaves in Commissioning
// without a credential: an administrator issues each a credential (POST devices/{id}/credential, which needs a recent
// second factor) and records its calibration through the devices API, exactly as an installer would; the emulator then
// plays them for site AUH-TA. Q-RES-04 is offline from 18:25 and sends half the minutes of 18:20 to 18:30.

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
const seededIds: string[] = [];
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

// Leave no working credential behind, in the simulator or in Ariva, when the suite runs against a shared environment. The
// seeded AUH-TA sensors stay (the demo plays them); their credentials are rotated once more and the new ones dropped.
test.afterAll(async () => {
	await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: [] } });
	for (const id of registeredIds) await call('POST', `${admin}/devices/${id}/retire`, { token: adminToken });
	for (const id of seededIds) await call('POST', `${admin}/devices/${id}/credential`, { token: adminToken });
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

test('the seeded AUH-TA sensors play the AUH-TA evening once issued a credential and calibrated', async () => {
	test.skip(!kafkaAvailable, 'an accepted push needs Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP)');
	test.setTimeout(120_000);
	// The visitors' queue lead, its band's lead and a desk sensor over IC-20 to IC-23; the residents' lead and Q-RES-04.
	const auhPlan = ['Q-VIS-01', 'O-VIS-01', 'D-VIS-01', 'Q-RES-01', 'Q-RES-04'];
	const listed = await (await call('GET', `${admin}/devices?siteCode=AUH-TA&pageSize=200`, { token: adminToken })).json();
	const seeded = new Map<string, { id: string; state: string }>(((listed.data ?? listed) as { code: string; id: string; state: string }[]).map((d) => [d.code, d]));
	expect(seeded.size, 'the illustrative seed placed its 84 sensors').toBe(84);

	const auhCredentials: { site: string; sensor: string; dialect: string; credential: string }[] = [];
	for (const sensor of auhPlan) {
		const device = seeded.get(sensor);
		expect(device, sensor).toBeDefined();
		seededIds.push(device!.id);
		const issued = await call('POST', `${admin}/devices/${device!.id}/credential`, { token: adminToken });
		expect(issued.status(), await issued.text()).toBe(200);
		const calibrations = await (await call('GET', `${admin}/devices/${device!.id}/calibrations`, { token: adminToken })).json();
		if (((calibrations.data ?? calibrations) as unknown[]).length === 0) {
			const calibrated = await call('POST', `${admin}/devices/${device!.id}/calibrations`, {
				token: adminToken,
				data: { method: 'ManualCountTally', sampleSize: 200, countingAccuracyPercent: 97, waitTimeErrorMinutes: 0.3 }
			});
			expect(calibrated.status(), await calibrated.text()).toBe(201);
		}
		auhCredentials.push({ site: 'AUH-TA', sensor, dialect: 'Canonical', credential: (await issued.json()).credential });
	}

	// A credential Ariva did not issue is refused before it reaches Ingest; a DMO sensor code is not an AUH-TA sensor.
	const forged = await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: [{ site: 'AUH-TA', sensor: 'Q-VIS-01', dialect: 'Canonical', credential: 'letmein' }] } });
	expect(forged.status()).toBe(400);
	const crossed = await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: [{ ...auhCredentials[0], sensor: 'S-15' }] } });
	expect(crossed.status()).toBe(400);

	const loaded = await call('PUT', `${sensors}/devices`, { headers: operator, data: { devices: auhCredentials } });
	expect(loaded.status(), await loaded.text()).toBe(200);
	expect(await loaded.text()).not.toContain('ardk_');
	const started = await call('POST', `${sensors}/start`, { headers: operator, data: { minute: 1100, speed: 60, untilMinute: 1110 } });
	expect(started.status(), await started.text()).toBe(200);

	let status = await (await call('GET', sensors, { headers: operator })).json();
	for (let i = 0; i < 60 && status.running; i++) {
		await new Promise((resolve) => setTimeout(resolve, 500));
		status = await (await call('GET', sensors, { headers: operator })).json();
	}
	expect(status.running).toBe(false);
	expect(JSON.stringify(status)).not.toContain('ardk_');
	const bySensor = Object.fromEntries(status.devices.map((d: { sensor: string }) => [d.sensor, d]));
	for (const device of status.devices) {
		expect(device.site, device.sensor).toBe('AUH-TA');
		expect(device.failures, `${device.sensor}: ${device.lastError}`).toBe(0);
		expect(device.acceptedEvents, device.sensor).toBe(device.expectedEvents);
	}
	expect(bySensor['Q-RES-04'].pushes, 'Q-RES-04 is offline from 18:25').toBe(5);
	expect(bySensor['Q-VIS-01'].pushes).toBe(10);
	expect(bySensor['Q-VIS-01'].acceptedEvents, 'the visitors wave crosses the lines').toBeGreaterThan(20);
	expect(bySensor['D-VIS-01'].acceptedEvents, 'the counters staff and service zones each minute').toBeGreaterThan(10);
	expect(bySensor['O-VIS-01'].role).toBe('OverflowLead');

	const health = await (await call('GET', `${admin}/devices?siteCode=AUH-TA&pageSize=200`, { token: adminToken })).json();
	const states = new Map(((health.data ?? health) as { code: string; state: string }[]).map((d) => [d.code, d.state]));
	for (const sensor of auhPlan) expect(states.get(sensor), `${sensor} is calibrated`).not.toBe('Commissioning');
	expect(states.get('Q-VIS-02'), 'a sensor the run did not touch stays as the seed left it').toBe('Commissioning');
});
