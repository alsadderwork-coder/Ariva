import { expect, test } from '@playwright/test';
import { call } from '../support/accounts';
import { hosts } from '../support/hosts';
import { sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-027, ARV-139b: the simulator's scenario endpoints, now per scenario site: the reference site DMO (seed 9303) and the
// illustrative AUH Terminal A arrivals hall AUH-TA (seed 9304). Reading needs an operator key with the read scope,
// re-running a site's day the control scope (CWE-306); a site the simulator does not play, or an attack payload in its
// place, is 404 without being echoed (CWE-501, CWE-79, CWE-89). The AUH-TA day replays its scripted evening: the
// visitors' nowcast passes 15 minutes at 18:12, Q-RES-04 is offline from 18:25 to 18:35, and the smart gates' queue
// spills into its band from 19:12. The emulated AUH-TA AODB serves its ACRIS answer only with the AODB key.

const scenario = `${hosts.simulation}/api/v1/simulation/scenario`;
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };
const acrisKey = process.env.ARIVA_E2E_ACRIS_KEY ?? '';

test('reading a site needs an operator key; the sites are DMO and AUH-TA', async () => {
	expect((await call('GET', `${scenario}?site=AUH-TA`)).status()).toBe(401);
	expect((await call('GET', `${scenario}/sites`, { headers: { Authorization: 'Bearer sim-guess-0000000000000000000000000' } })).status()).toBe(401);

	const sites = await call('GET', `${scenario}/sites`, { headers: operator });
	expect(sites.status()).toBe(200);
	const body = (await sites.json()) as { siteCode: string; queues: number; sensors: number; departures: number }[];
	expect(body.map((s) => s.siteCode)).toEqual(['DMO', 'AUH-TA']);
	expect(body[1]).toMatchObject({ queues: 8, sensors: 84, departures: 0 });

	const reference = await (await call('GET', scenario, { headers: operator })).json();
	expect(reference.siteCode, 'no site is the reference site').toBe('DMO');
});

test('the AUH-TA day replays its scripted evening', async () => {
	const queue = async (zone: string, minute: number) =>
		(await call('GET', `${scenario}/queues/${zone}?minute=${minute}&site=AUH-TA`, { headers: operator })).json();

	expect((await queue('A-VIS', 1091)).nowcastMinutes).toBeLessThanOrEqual(15);
	expect((await queue('A-VIS', 1092)).nowcastMinutes, 'the visitors nowcast passes 15 minutes at 18:12').toBeGreaterThan(15);
	expect((await queue('A-EG', 1158)).length, 'the smart gates queue is above its 250-person snake').toBeGreaterThan(250);

	const sensors = (await (await call('GET', `${scenario}/sensors?minute=1110&site=AUH-TA`, { headers: operator })).json()) as {
		sensor: string;
		offline: boolean;
		queueZone: string;
	}[];
	expect(sensors.find((s) => s.sensor === 'Q-RES-04')).toMatchObject({ offline: true, queueZone: 'A-RES' });
	expect(sensors.filter((s) => s.offline)).toHaveLength(1);

	const alerts = (await (await call('GET', `${scenario}/alerts?site=AUH-TA`, { headers: operator })).json()) as { ruleId: string; raisedAt: number }[];
	const evening = alerts.filter((a) => a.raisedAt >= 1020 && a.raisedAt < 1260).map((a) => `${a.ruleId} ${a.raisedAt}`);
	expect(evening).toEqual(['R-001 1092', 'R-003 1105', 'R-001 1148', 'R-002 1154']);
});

test('a site the simulator does not play is 404 and never echoed, whatever is sent in its place', async () => {
	const payloads = ['AUH-TB', 'auh-ta', 'AUH-TA ', ...sqlInjectionPayloads.slice(0, 4), ...xssPayloads.slice(0, 4)];
	for (const payload of payloads) {
		const site = encodeURIComponent(payload);
		for (const path of [`?site=${site}`, `/queues?minute=10&site=${site}`, `/alerts?site=${site}`, `/sensors?minute=10&site=${site}`]) {
			const response = await call('GET', `${scenario}${path}`, { headers: operator });
			expect(response.status(), `${path}`).toBe(404);
			const text = await response.text();
			expect(text).not.toContain(payload.trim() || 'AUH-TA ');
			expect(text).not.toContain('<script');
		}
	}
});

test('only a control key re-runs a site, and a re-run of AUH-TA leaves DMO alone', async () => {
	expect((await call('PUT', scenario, { data: { seed: 9304, site: 'AUH-TA' } })).status()).toBe(401);
	const unknown = await call('PUT', scenario, { headers: operator, data: { seed: 9304, site: '<img src=x onerror=alert(1)>' } });
	expect([400, 404]).toContain(unknown.status());
	expect(await unknown.text()).not.toContain('onerror');

	const dmoBefore = await (await call('GET', scenario, { headers: operator })).json();
	// Its own default seed, so a functional test reading the AUH-TA day meanwhile sees the same evening.
	const rerun = await call('PUT', scenario, { headers: operator, data: { seed: 9304, site: 'AUH-TA' } });
	expect([200, 429]).toContain(rerun.status());
	if (rerun.status() === 200) expect(await rerun.json()).toMatchObject({ siteCode: 'AUH-TA', seed: 9304, runBy: 'e2e' });
	const dmoAfter = await (await call('GET', scenario, { headers: operator })).json();
	expect(dmoAfter).toMatchObject({ seed: dmoBefore.seed, runAt: dmoBefore.runAt });
});

test('the AUH-TA AODB serves its ACRIS flights only with the AODB key', async () => {
	const route = `${hosts.simulation}/aodb/sites/AUH-TA/acris/flights`;
	expect((await call('GET', route)).status()).toBe(401);
	expect((await call('GET', route, { headers: { 'X-Api-Key': 'guess' } })).status()).toBe(401);
	expect((await call('GET', `${hosts.simulation}/aodb/sites/XYZ/acris/flights`, { headers: { 'X-Api-Key': acrisKey } })).status()).toBe(404);

	const played = await call('POST', `${hosts.simulation}/api/v1/simulation/feeds/play`, { headers: operator, data: { minute: 1100 } });
	expect(played.status(), await played.text()).toBe(200);
	const flights = (await (await call('GET', route, { headers: { 'X-Api-Key': acrisKey } })).json()) as {
		arrivalAirport: string;
		arrival: { terminal: string };
	}[];
	expect(flights.length).toBeGreaterThan(10);
	expect(flights.every((f) => f.arrivalAirport === 'AUH' && f.arrival.terminal === 'A')).toBe(true);
});
