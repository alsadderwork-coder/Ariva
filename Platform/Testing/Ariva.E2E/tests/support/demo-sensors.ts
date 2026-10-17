import { expect } from '@playwright/test';
import { call } from './accounts';
import { hosts } from './hosts';

// The demo airport's (DMO) sensors as the scripted demo (ARV-064, tests/demo) and the validation rehearsal against the live
// pipeline (ARV-104j, tests/rehearsal) register them: each through the admin API, as an installer would, never in the database.

const admin = `${hosts.main}/api/v1/admin`;

/**
 * The scenario's counting sensors (ScenarioModel.cs): the lead sensor of each of the 16 queues and of the three overflow
 * bands, which Ariva adds to their queue, so that every queue on the screens is live; plus S-17, a second sensor over the
 * arrivals Visitors hall that only reports its health, for the 18:20 outage. Check-in islands C and D report in the Xovis
 * dialect, the rest in the canonical one.
 */
export const dmoSensors = [
	{ sensor: 'S-01', zone: 'SEC-N', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-06', zone: 'SEC-S', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-11', zone: 'SEC-N', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-12', zone: 'A-CRW', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-13', zone: 'A-CIT', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-15', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-17', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-21', zone: 'A-RES', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-23', zone: 'A-EG', level: 'ARR', dialect: 'Canonical' },
	// The A-VIS queue includes its overflow band A-OV: without the band's lead sensor the queue has no full sensor
	// reading and its length is only the engine's estimate, which R-001 does not judge.
	{ sensor: 'S-25', zone: 'A-VIS', level: 'ARR', dialect: 'Canonical' },
	{ sensor: 'S-27', zone: 'D-CRW', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-28', zone: 'D-CIT', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-30', zone: 'D-RES', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-32', zone: 'D-VIS', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-37', zone: 'D-EG', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-39', zone: 'D-VIS', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-40', zone: 'CI-A', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-45', zone: 'CI-B', level: 'DEP', dialect: 'Canonical' },
	{ sensor: 'S-50', zone: 'CI-C', level: 'DEP', dialect: 'Xovis' },
	{ sensor: 'S-55', zone: 'CI-D', level: 'DEP', dialect: 'Xovis' }
] as const;

export type DmoSensor = (typeof dmoSensors)[number];

/** A sensor as the simulator's sensor emulator takes it (PUT api/v1/simulation/sensors/devices): never logged, never returned. */
export interface EmulatedSensor {
	site?: string;
	sensor: string;
	dialect: string;
	credential: string;
}

/**
 * Registers each planned sensor as a DMO device under its own code (a device from an earlier run is reused with a new
 * credential: a calibrated device cannot be deleted and its code stays taken) and calibrates it, so it is Commissioned (Stream
 * counts only calibrated devices, and only they are watched for silence, R-003). <paramref name="token"/> is an administrator's
 * with a fresh second factor (registering is critical). Returns the credentials for the sensor emulator.
 */
export async function prepareDmoSensors(token: string, plan: readonly DmoSensor[]): Promise<EmulatedSensor[]> {
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

	const credentials: EmulatedSensor[] = [];
	const existing = await (await call('GET', `${admin}/devices?siteCode=DMO&pageSize=200`, { token })).json();
	const known = new Map<string, { id: string; state: string }>(((existing.data ?? existing) as { code: string; id: string; state: string }[]).map((d) => [d.code, d]));
	for (const device of plan) {
		let id: string;
		let credential: string;
		const found = known.get(device.sensor);
		if (found && found.state !== 'Retired') {
			id = found.id;
			const rotated = await call('POST', `${admin}/devices/${id}/credential`, { token });
			// The answer carries the device credential: it goes into the message (a step title even when it passes) only on failure.
			expect(rotated.status(), rotated.status() === 200 ? `${device.sensor} credential rotated` : await rotated.text()).toBe(200);
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
			expect(registered.status(), registered.status() === 201 ? `${device.sensor} registered` : await registered.text()).toBe(201);
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
	return credentials;
}
