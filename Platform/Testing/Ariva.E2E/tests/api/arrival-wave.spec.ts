import fs from 'node:fs';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, integrationSeedsFile, login, totpCode } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-047: the arrival-wave projection of a site (formulas.md F14). An AODB client sends arriving legs and an
// immigration client sends AMAN's lane demand for one of them through the Integration API; a border shift supervisor
// of DMO then sees the flights landing within the window (and those landed whose passengers are still reaching the
// hall), each with its lane split (AMAN's where received, the default mix otherwise), and the predicted hall arrivals
// per minute. Other roles and sites are refused as the permission matrix says.

test.skip(!databaseAvailable, 'the projection reads legs sent through the Integration API, which needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

type Credentials = { client: { id: string; clientId: string }; clientSecret: string; totpSecret: string };
type Lanes = { cit: number | null; res: number | null; vis: number | null; crw: number | null; eGate: number | null; total: number };
type Flight = {
	flightKey: string;
	inBlockSource: string;
	landed: boolean;
	passengers: number | null;
	passengerSource: string | null;
	laneSource: string | null;
	lanes: Lanes;
	hallFirstUtc: string;
};
type Wave = {
	siteCode: string;
	windowMinutes: number;
	delayMinutes: number;
	egateRejectRate: number | null;
	rejectLane: string | null;
	flights: Flight[];
	minutes: { minuteUtc: string; lanes: Lanes }[];
};

const inside = () => `10.${1 + Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250)}.${1 + Math.floor(Math.random() * 250)}`;
const run = Date.now().toString(36).toUpperCase();
const key = (n: number) => `AW-${run}-${n}`;
const at = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();
const wave = (site = 'DMO', query = '') => `${hosts.main}/api/v1/sites/${site}/arrival-wave${query}`;

let admin = '';

async function client(name: string, kind: string, scopes: string[]) {
	const created = await call('POST', `${hosts.main}/api/v1/admin/integration-clients`, {
		token: admin,
		data: { name, kind, scopes, siteCodes: ['DMO'], allowedNetworks: ['10.0.0.0/8'], requireTotpPerRequest: false }
	});
	expect(created.status(), await created.text()).toBe(201);
	const credentials: Credentials = await created.json();
	fs.appendFileSync(integrationSeedsFile, credentials.totpSecret + '\n' + credentials.clientSecret + '\n');
	const exchanged = await call('POST', `${hosts.integration}/api/v1/auth`, {
		data: { clientId: credentials.client.clientId, clientSecret: credentials.clientSecret, totpCode: totpCode(credentials.totpSecret) },
		address: inside()
	});
	expect(exchanged.status(), await exchanged.text()).toBe(200);
	return (await exchanged.json()).accessToken as string;
}

const send = (token: string, path: string, body: unknown, idempotencyKey: string) =>
	call('POST', `${hosts.integration}/api/v1/integration/sites/DMO/${path}`, {
		token,
		address: inside(),
		raw: JSON.stringify(body),
		headers: { 'Content-Type': 'application/json', 'Idempotency-Key': idempotencyKey }
	});

const arrival = (n: number, more: Record<string, unknown>) => ({
	flightKey: key(n),
	carrier: 'RJ',
	number: String(700 + n),
	direction: 'Arrival',
	scheduledUtc: at(15),
	origin: 'AMM',
	destination: 'DMO',
	...more
});

async function signIn(name: keyof ReturnType<typeof accounts>) {
	const { userName, password } = accounts()[name];
	const signedIn = await login(userName, password);
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	return (await signedIn.json()).accessToken as string;
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().arrivalWaveAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;

	const aodb = await client(`E2E arrival wave AODB ${run}`, 'Aodb', ['flights:write']);
	const legs = await send(
		aodb,
		'flights/batch',
		{
			messageTimeUtc: at(-1),
			items: [
				arrival(1, { estimatedUtc: at(10), paxEstimate: 100 }),
				arrival(2, { onBlockUtc: at(-2), paxEstimate: 160 }),
				arrival(3, { estimatedUtc: at(50), paxEstimate: 100 }),
				arrival(4, { estimatedUtc: at(12), seats: 180 })
			]
		},
		`aw-${run}-legs`
	);
	expect(legs.status(), await legs.text()).toBe(200);
	expect(await legs.json()).toMatchObject({ applied: 4, refused: 0 });

	const immigration = await client(`E2E arrival wave AMAN ${run}`, 'Immigration', ['immigration:write']);
	const demand = await send(
		immigration,
		'immigration/inbound-lane-demand',
		{
			items: [
				{
					siteCode: 'DMO',
					flightKey: key(2),
					scheduledArrivalUtc: at(15),
					boardedTotal: 200,
					passengersByLane: { CIT: 40, RES: 20, VIS: 90, CRW: 4 },
					eGateEligible: 30,
					computedAtUtc: at(-1),
					sourceEventId: `aw-${run}-ld-2`
				}
			]
		},
		`aw-${run}-demand`
	);
	expect(demand.status(), await demand.text()).toBe(200);
	expect(await demand.json()).toMatchObject({ applied: 1, refused: 0 });
});

test('a border shift supervisor of the site sees the flights landing soon with their lane split and the hall curve', async () => {
	const token = await signIn('dmoBorder');
	const answer = await call('GET', wave(), { token });
	expect(answer.status(), await answer.text()).toBe(200);
	const projection: Wave = await answer.json();
	expect(projection).toMatchObject({ siteCode: 'DMO', windowMinutes: 30, rejectLane: 'VIS' });
	// ARV-049: the e-gate reject rate (F12), measured from AMAN or the reference, for the lane split's readers.
	expect(projection.egateRejectRate).toBeGreaterThanOrEqual(0);
	expect(projection.egateRejectRate).toBeLessThanOrEqual(1);
	expect(projection.minutes).toHaveLength(30 + projection.delayMinutes + 12);

	const mine = new Map(projection.flights.filter((f) => f.flightKey.startsWith(`AW-${run}-`)).map((f) => [f.flightKey, f]));
	expect([...mine.keys()].sort()).toEqual([key(1), key(2), key(4)]);
	expect(mine.get(key(1))).toMatchObject({
		inBlockSource: 'Estimated',
		landed: false,
		passengers: 100,
		passengerSource: 'PaxEstimate',
		laneSource: 'DefaultMix'
	});
	// The reference mix: VIS 0.35, CIT 0.35 and RES 0.20 less the e-gate share 0.40, crew 0.02; transfers (0.08) removed.
	expect(mine.get(key(1))!.lanes).toEqual({ cit: 21, res: 12, vis: 35, crw: 2, eGate: 22, total: 92 });
	expect(mine.get(key(2))).toMatchObject({ inBlockSource: 'OnBlock', landed: true, passengers: 200, passengerSource: 'Aman', laneSource: 'Aman' });
	expect(mine.get(key(2))!.lanes).toEqual({ cit: 40, res: 20, vis: 90, crw: 4, eGate: 30, total: 184 });
	expect(mine.get(key(4))).toMatchObject({ passengerSource: 'Seats', passengers: 144 });

	// Every minute adds up across lanes, and the curve holds at least this run's passengers that reach the hall from now on.
	for (const minute of projection.minutes) {
		const { cit, res, vis, crw, eGate, total } = minute.lanes;
		expect(Math.abs(cit! + res! + vis! + crw! + eGate! - total)).toBeLessThan(0.05);
	}
	expect(projection.minutes.reduce((sum, m) => sum + m.lanes.total, 0)).toBeGreaterThan(92 + 132);

	const hour = await (await call('GET', wave('DMO', '?minutes=60'), { token })).json();
	expect(hour.flights.map((f: Flight) => f.flightKey)).toContain(key(3));
});

test('the projection is refused to other roles, other sites and bad windows without echoing input', async () => {
	expect((await call('GET', wave())).status()).toBe(401);
	expect((await call('GET', wave(), { token: await signIn('dmoHandler') })).status()).toBe(403);
	expect((await call('GET', wave(), { token: await signIn('siteUser') })).status(), 'a supervisor of another site').toBe(404);
	expect((await call('GET', wave('ZZ9'), { token: admin })).status()).toBe(404);

	// A terminal duty manager sees the wave as totals: the split by lane is border data.
	const totals = await call('GET', wave('DMO'), { token: await signIn('dmoTerminal') });
	expect(totals.status()).toBe(200);
	const terminal: Wave = await totals.json();
	const mine = terminal.flights.find((f) => f.flightKey === key(2))!;
	expect(mine).toMatchObject({ laneSource: null, lanes: { cit: null, res: null, vis: null, crw: null, eGate: null, total: 184 } });
	expect(terminal).toMatchObject({ egateRejectRate: null, rejectLane: null });
	expect(terminal.minutes.every((m) => m.lanes.vis === null && m.lanes.total >= 0)).toBe(true);

	for (const query of ['?minutes=4', '?minutes=121', '?minutes=<script>']) {
		const refused = await call('GET', wave('DMO', query), { token: admin });
		expect(refused.status(), query).toBe(400);
		expectNoLeak(await refused.text(), query);
		expect(await refused.text()).not.toContain('<script>');
	}
});
