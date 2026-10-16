import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { expect, test } from '@playwright/test';
import { createClient } from 'redis';
import { accounts, call, databaseAvailable, login, signIn, unusedTotpCode, type Account } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';
import { staffingDatabase as database } from '../support/staffing';

// ARV-181b2: the DesksBelowPlan alert, "Open desks below recommendation" (formulas F16): per lane minute, the recommendation
// of the current interval (interval 0 of lane_staffing) minus the desks open now, at least 0. Border data on every channel:
// rules are created, changed and backtested only by holders of ArrivalWaveLanes.View (others: 403 with the BorderOnly text),
// belong to a border role (the supervisor or the administrator, never no owner), escalate to one or to no role, fire above
// their threshold and watch queue zones only; other roles never find them (search leaves them out, get, update and delete
// answer 404). Alerts on the metric reach border roles only, whatever their owner and escalation roles say: the alerts API,
// the live hub, the daily report and its CSV. Ariva.Api.Stream does not run in the E2E run, so the suite plants yesterday's
// interval-0 rows of the crew lane (A-CRW) as the staffing job writes them for the backtest, and plants alerts as the
// evaluation writes them (the live evaluation is proven by the integration StaffingAlertTests).

test.skip(!databaseAvailable, 'the open desks below recommendation alert needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const rulesApi = `${hosts.main}/api/v1/admin/alert-rules`;
const alertsApi = `${hosts.main}/api/v1/alerts`;
const reportsApi = `${hosts.main}/api/v1/sites/DMO/reports`;
const redisUrl = process.env.ARIVA_E2E_REDIS_URL;

// The rule service's answers, as the web app matches them (Ariva.Core AlertRuleErrors).
const borderOnly = 'Open desks below recommendation is border data: only a caller with ArrivalWaveLanes.View creates, changes, imports or backtests a rule on it.';
const borderRolesOnly =
	'A rule on open desks below recommendation belongs to a border role: its owner is the Border shift supervisor or the system administrator (never empty), and it escalates to one of them or to no role.';
const borderComparator = 'A rule on open desks below recommendation fires at or above its threshold: compare with GreaterThan or GreaterOrEqual.';
const queueZonesOnly = 'ConservationResidual, TrackCompletion, OccupancyOutOfRange and DesksBelowPlan are judged per queue zone: choose queue zones only.';

// DMO is in Asia/Dubai (UTC+4): yesterday's local date, and 06:00 UTC of it (10:00 local) where the case is planted. The
// planted alerts are the day before (06:30 UTC), so they never change the alert count of yesterday's report that the
// reports suites read at the same time.
const dubaiNow = new Date(Date.now() + 4 * 3_600_000);
const yesterday = new Date(Date.UTC(dubaiNow.getUTCFullYear(), dubaiNow.getUTCMonth(), dubaiNow.getUTCDate() - 1));
const six = new Date(yesterday.getTime() + 6 * 3_600_000);
const alertDay = new Date(yesterday.getTime() - 86_400_000);
const date = alertDay.toISOString().slice(0, 10);
const alertRaised = new Date(alertDay.getTime() + 6.5 * 3_600_000);
const at = (minutes: number) => new Date(six.getTime() + minutes * 60_000);
const iso = (value: Date) => value.toISOString().replace('.000Z', 'Z');
const runId = Date.now().toString(36);

let border: string, terminal: string, handler: string, admin: string, observer: string, elsewhere: string;
let borderStepped: string, terminalStepped: string;
const created: string[] = [];
const plantedAlerts: string[] = [];
let channelRule: { id: string; code: string };

const rule = (overrides: Record<string, unknown> = {}) => ({
	siteCode: 'DMO',
	name: `E2E desks short ${runId}`,
	zones: ['A-CRW'],
	metric: 'DesksBelowPlan',
	comparator: 'GreaterOrEqual',
	threshold: 2,
	minQueueLength: null,
	clearThreshold: null,
	sustainMinutes: 3,
	clearAfterMinutes: 2,
	severity: 'Warning',
	ownerRole: 'BorderShiftSupervisor',
	escalateAfterMinutes: null,
	escalateToRole: null,
	escalationContact: null,
	notifyByEmail: false,
	enabled: true,
	...overrides
});

async function stepped(account: Account): Promise<string> {
	const signedIn = await login(account.userName, account.password, undefined, undefined, { code: await unusedTotpCode(account.totpSecret!) });
	expect(signedIn.status(), `sign-in of ${account.userName}`).toBe(200);
	return (await signedIn.json()).accessToken;
}

async function create(data: unknown, token = border): Promise<{ id: string; code: string }> {
	const answer = await call('POST', rulesApi, { token, data });
	expect(answer.status(), answer.status() === 201 ? 'create' : await answer.text()).toBe(201);
	const body = await answer.json();
	created.push(body.id);
	return body;
}

/** The planted case of the plan, interval 0 of each minute: 8 desks, a measured throughput, a queue of 40, 30 arriving. */
const rows: { minute: number; recommended: number | null; openNow: number | null; degraded?: boolean; gap?: string; arrivals?: boolean }[] = [
	...[0, 1, 2, 3, 4].map((minute) => ({ minute, recommended: 4, openNow: 4 })),
	{ minute: 5, recommended: 6, openNow: 4 },
	{ minute: 7, recommended: 6, openNow: 4 },
	{ minute: 8, recommended: 7, openNow: 4, degraded: true },
	{ minute: 9, recommended: 7, openNow: 4 },
	{ minute: 10, recommended: null, openNow: 4, gap: 'FlightWithoutPassengers' },
	{ minute: 11, recommended: 5, openNow: null },
	{ minute: 12, recommended: 5, openNow: 4 },
	{ minute: 13, recommended: 6, openNow: 4 },
	{ minute: 14, recommended: 4, openNow: 4 },
	{ minute: 15, recommended: 3, openNow: 5 },
	{ minute: 16, recommended: 4, openNow: 4 },
	...[17, 18, 19].map((minute) => ({ minute, recommended: 8, openNow: 0, arrivals: false }))
];

test.beforeAll(async () => {
	[border, terminal, handler, admin, observer, elsewhere] = await Promise.all(
		[accounts().dmoBorder, accounts().dmoTerminal, accounts().dmoHandler, accounts().webAdmin, accounts().ValidationObserver, accounts().BorderShiftSupervisor].map(
			async (a) => (await signIn(a)).accessToken
		)
	);
	borderStepped = await stepped(accounts().desksBorder);
	terminalStepped = await stepped(accounts().desksTerminal);
	const db = database();
	await db.connect();
	try {
		await db.query('DELETE FROM lane_staffing WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc <= $3', ['DMO/A-CRW', at(0), at(21)]);
		for (const r of rows) {
			const minute = at(r.minute);
			await db.query(
				`INSERT INTO lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
				                            wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now,
				                            open_now_degraded, desks, target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc)
				 VALUES ('DMO/A-CRW', $1, 0, 'CRW', $1, $2, $3, $4, $5, $6, NULL, $7, $8, false, NULL, $9, 'None', $10, 40, $11, $12, 8, 10, 0.85, 0.5, 'Measured', $13)`,
				[
					minute,
					15 - (minute.getUTCMinutes() % 15),
					r.arrivals === false ? null : 30,
					r.recommended,
					r.recommended == null || r.recommended === 0 ? 'None' : 'Wait',
					r.recommended === 8,
					r.recommended == null ? null : 8,
					r.recommended == null || r.openNow == null ? null : 12,
					r.gap ?? 'None',
					new Date(minute.getTime() - 60_000),
					r.openNow,
					r.degraded ?? false,
					new Date(minute.getTime() + 45_000)
				]
			);
		}
	} finally {
		await db.end();
	}
});

test.afterAll(async () => {
	const db = database();
	await db.connect();
	try {
		await db.query(
			`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = greatest(now(), raised_utc), resolved_by = 'e2e-cleanup', resolution_note = 'cleanup'
			 WHERE id = ANY($1::uuid[]) AND state <> 'Resolved'`,
			[plantedAlerts]
		);
		await db.query('DELETE FROM lane_staffing WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc <= $3', ['DMO/A-CRW', at(0), at(21)]);
	} finally {
		await db.end();
	}
	for (const id of created) await call('DELETE', `${rulesApi}/${id}`, { token: borderStepped });
});

test('a border shift supervisor creates, reads, changes, backtests and deletes a rule; the backtest finds the case’s one alert', async () => {
	const desks = await create(rule());
	expect(desks).toMatchObject({ metric: 'DesksBelowPlan', comparator: 'GreaterOrEqual', threshold: 2, ownerRole: 'BorderShiftSupervisor', zones: ['A-CRW'] });
	const read = await call('GET', `${rulesApi}/${desks.id}`, { token: border });
	expect(read.status()).toBe(200);
	const changed = await call('PUT', `${rulesApi}/${desks.id}`, { token: border, data: rule({ clearThreshold: 1, escalateAfterMinutes: 10, escalateToRole: 'BorderShiftSupervisor' }) });
	expect(changed.status(), await changed.text()).toBe(200);
	expect(await changed.json()).toMatchObject({ clearThreshold: 1, escalateToRole: 'BorderShiftSupervisor' });

	// Held at 06:05, 06:07 and 06:09 (no row at 06:06, a degraded open count at 06:08), raised at 06:09 with 3 desks short;
	// cleared at 06:14 and 06:15 (the gap at 06:10, the unknown open count at 06:11 and 06:13's 2 short are not clear minutes);
	// the rows without arrivals at 06:17 to 06:19 fail the read-back check and never raise again.
	const backtest = await call('POST', `${rulesApi}/backtest`, { token: border, data: { rule: rule(), fromUtc: iso(at(0)), toUtc: iso(at(21)) } });
	expect(backtest.status(), await backtest.text()).toBe(200);
	const found = await backtest.json();
	expect(found).toMatchObject({ count: 1, firstRaisedUtc: iso(at(9)), truncated: false, targets: 1, targetsWithData: 1 });
	expect(found.alerts).toEqual([
		{ zoneName: 'A-CRW', deviceCode: null, raisedUtc: iso(at(9)), clearedUtc: iso(at(15)), value: 3, binStartUtc: null, predictedForUtc: null }
	]);
	const byAdmin = await call('POST', `${rulesApi}/backtest`, { token: admin, data: { rule: rule({ ownerRole: 'SystemAdministrator' }), fromUtc: iso(at(0)), toUtc: iso(at(21)) } });
	expect((await byAdmin.json()).firstRaisedUtc, 'the administrator sees border data too').toBe(iso(at(9)));

	expect((await call('DELETE', `${rulesApi}/${desks.id}`, { token: border })).status(), 'deleting needs a recent second factor').toBe(401);
	expect((await call('DELETE', `${rulesApi}/${desks.id}`, { token: borderStepped })).status()).toBe(204);
	expect((await call('GET', `${rulesApi}/${desks.id}`, { token: border })).status()).toBe(404);
	created.splice(created.indexOf(desks.id), 1);
});

test('the rule belongs to a border role, fires above and watches queue zones; anything else answers 400 with nothing echoed', async () => {
	for (const [why, token, data, detail] of [
		['owned by the terminal duty manager', border, rule({ ownerRole: 'TerminalDutyManager' }), borderRolesOnly],
		['owned by the handler station manager', admin, rule({ ownerRole: 'HandlerStationManager' }), borderRolesOnly],
		['no owner, even from an administrator', admin, rule({ ownerRole: null }), borderRolesOnly],
		['escalated to the terminal duty manager', admin, rule({ escalateAfterMinutes: 10, escalateToRole: 'TerminalDutyManager' }), borderRolesOnly],
		['fires below', border, rule({ comparator: 'LessThan' }), borderComparator],
		['an overflow band', border, rule({ zones: ['A-OV'] }), queueZonesOnly],
		['a threshold above 1,000 desks', border, rule({ threshold: 1001 }), 'The threshold of DesksBelowPlan is from 0 to 1000.'],
		['lower case', border, rule({ metric: 'desksbelowplan' }), 'Unknown metric, comparator or severity.'],
		['padded', border, rule({ metric: 'DesksBelowPlan ' }), 'Unknown metric, comparator or severity.'],
		['a number', border, rule({ metric: '6' }), 'Unknown metric, comparator or severity.']
	] as const) {
		const refused = await call('POST', rulesApi, { token, data });
		const text = await refused.text();
		expect(refused.status(), `${why}: ${text}`).toBe(400);
		expect(JSON.parse(text).detail, why).toBe(detail);
		expectNoLeak(text, why);
	}
	const backtest = await call('POST', `${rulesApi}/backtest`, { token: border, data: { rule: rule({ ownerRole: null }), fromUtc: iso(at(0)), toUtc: iso(at(21)) } });
	expect(backtest.status(), 'a backtest judges the rule as it would be created').toBe(400);
	expect((await backtest.json()).detail).toBe(borderRolesOnly);
});

test('other roles are refused 403 on create, change and backtest, and never find a border rule (404)', async () => {
	const desks = await create(rule({ name: `E2E desks hidden ${runId}` }));
	const forbidden = async (method: string, url: string, token: string, data?: unknown) => {
		const answer = await call(method, url, { token, data });
		const text = await answer.text();
		expect(answer.status(), `${method} ${url}: ${text}`).toBe(403);
		return text;
	};

	// The terminal duty manager holds the alert rule permissions, but not the lane split.
	expect(JSON.parse(await forbidden('POST', rulesApi, terminal, rule({ ownerRole: 'TerminalDutyManager' }))).detail).toBe(borderOnly);
	expect(JSON.parse(await forbidden('POST', rulesApi, terminal, rule())).detail).toBe(borderOnly);
	expect(JSON.parse(await forbidden('POST', `${rulesApi}/backtest`, terminal, { rule: rule(), fromUtc: iso(at(0)), toUtc: iso(at(21)) })).detail).toBe(borderOnly);
	const r005 = ((await (await call('GET', `${rulesApi}?siteCode=DMO&pageSize=500`, { token: terminal })).json()).data as any[]).find((r) => r.code === 'R-005');
	// R-005 is the demo seed's Nowcast rule the duty manager sees (a missing seed rule fails here rather than skipping the check).
	expect(r005, 'the seed rule R-005 the duty manager sees').toBeDefined();
	const moved = await forbidden('PUT', `${rulesApi}/${r005.id}`, terminal, rule({ name: r005.name, zones: ['A-CRW'] }));
	expect(JSON.parse(moved).detail, 'a rule it sees, changed to the border metric').toBe(borderOnly);
	expect((await (await call('GET', `${rulesApi}/${r005.id}`, { token: terminal })).json()).metric).toBe('Nowcast');
	const listed = ((await (await call('GET', `${rulesApi}?siteCode=DMO&pageSize=500`, { token: terminal })).json()).data as any[]).map((r) => r.metric);
	expect(listed, 'search leaves border rules out').not.toContain('DesksBelowPlan');
	expect((await (await call('GET', `${rulesApi}?siteCode=DMO&metric=DesksBelowPlan`, { token: terminal })).json()).totalCount).toBe(0);
	expect((await call('GET', `${rulesApi}/${desks.id}`, { token: terminal })).status()).toBe(404);
	expect((await call('PUT', `${rulesApi}/${desks.id}`, { token: terminal, data: rule({ name: 'Taken over' }) })).status()).toBe(404);
	expect((await call('DELETE', `${rulesApi}/${desks.id}`, { token: terminalStepped })).status(), 'the duty manager cannot delete it').toBe(404);

	// The handler station manager reads rules but writes none; the observer holds no alert permission.
	await forbidden('POST', rulesApi, handler, rule());
	expect(((await (await call('GET', `${rulesApi}?siteCode=DMO&pageSize=500`, { token: handler })).json()).data as any[]).map((r) => r.metric)).not.toContain('DesksBelowPlan');
	expect((await call('GET', `${rulesApi}/${desks.id}`, { token: handler })).status()).toBe(404);
	await forbidden('POST', rulesApi, observer, rule());
	expect((await call('POST', rulesApi, { data: rule() })).status()).toBe(401);
	expect((await call('GET', `${rulesApi}/${desks.id}`)).status()).toBe(401);

	// Another site's supervisor: its own permission, not this site.
	const other = await call('POST', rulesApi, { token: elsewhere, data: rule() });
	expect(other.status()).toBe(400);
	expect((await call('GET', `${rulesApi}/${desks.id}`, { token: elsewhere })).status()).toBe(404);

	const still = await call('GET', `${rulesApi}/${desks.id}`, { token: border });
	expect(await still.json(), 'nothing the other roles tried changed it').toMatchObject({ name: `E2E desks hidden ${runId}`, metric: 'DesksBelowPlan' });
});

test('alerts on the metric reach border roles only: the alerts API, the live hub and the daily report', async () => {
	test.setTimeout(60_000);
	channelRule = await create(rule({ name: `E2E desks channels ${runId}`, zones: ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS'] }));
	const db = database();
	await db.connect();
	const ids: Record<string, string> = {};
	try {
		// As the evaluation writes them, and as rows from before the story or changed in the database could hold them.
		for (const [key, zone, owner, escalateTo, escalated] of [
			['owned', 'A-CRW', 'BorderShiftSupervisor', null, false],
			['ownerless', 'A-CIT', null, null, false],
			['airport', 'A-RES', 'TerminalDutyManager', 'TerminalDutyManager', true],
			['hub', 'A-VIS', null, null, false]
		] as const) {
			const planted = await db.query(
				`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
				                    escalate_to_role, raised_utc, raised_value, state, escalated_utc, escalated_by)
				 SELECT gen_random_uuid(), site_code, id, code, name, $2, NULL, metric, severity, $3::varchar, CASE WHEN $4::varchar IS NULL THEN NULL ELSE 10 END,
				        $4::varchar, $5, 3, CASE WHEN $6 THEN 'Escalated' ELSE 'Raised' END, CASE WHEN $6 THEN $5::timestamptz + interval '10 minutes' END,
				        CASE WHEN $6 THEN 'e2e' END
				 FROM alert_rule WHERE id = $1 RETURNING id`,
				[channelRule.id, zone, owner, escalateTo, alertRaised, escalated]
			);
			ids[key] = planted.rows[0].id;
			plantedAlerts.push(ids[key]);
		}
	} finally {
		await db.end();
	}

	const listed = async (token: string) => ((await (await call('GET', `${alertsApi}?siteCode=DMO&pageSize=500`, { token })).json()).data as any[]).map((a) => a.id);
	for (const [token, who] of [
		[terminal, 'terminal duty manager'],
		[handler, 'handler station manager']
	] as const) {
		for (const id of Object.values(ids)) {
			expect(await listed(token), `${who}: ${id}`).not.toContain(id);
			expect((await call('GET', `${alertsApi}/${id}`, { token })).status(), who).toBe(404);
			expect((await call('POST', `${alertsApi}/${id}/acknowledge`, { token, data: { note: 'Mine' } })).status(), who).toBe(404);
		}
		expect((await (await call('GET', `${alertsApi}?siteCode=DMO&ruleCode=${channelRule.code}`, { token })).json()).totalCount, `${who}: not even counted`).toBe(0);
	}
	const seen = await listed(border);
	expect(seen).toEqual(expect.arrayContaining([ids.owned, ids.ownerless, ids.hub]));
	expect(seen, 'the supervisor is neither owner nor escalation role of the duty manager’s row').not.toContain(ids.airport);
	expect(await listed(admin)).toEqual(expect.arrayContaining(Object.values(ids)));

	// The daily report and its alerts CSV, for the duty manager and for the supervisor.
	const daily = async (token: string) => ((await (await call('GET', `${reportsApi}/daily?date=${date}`, { token })).json()).alerts as any[]).map((a) => a.ruleCode);
	expect(await daily(terminal)).not.toContain(channelRule.code);
	expect(await daily(border)).toContain(channelRule.code);
	const csv = async (token: string) => (await call('GET', `${reportsApi}/daily.csv?date=${date}&section=alerts`, { token })).text();
	expect(await csv(terminal), 'the rule name is unique to this run').not.toContain(`E2E desks channels ${runId}`);
	expect(await csv(border)).toContain(`E2E desks channels ${runId}`);

	// The live hub: a notice the Stream host would publish without an owner, and the supervisor's acknowledgement.
	test.skip(!redisUrl, 'the live hub needs the run\'s Redis (ARIVA_E2E_REDIS_URL)');
	const hub = (token: string): HubConnection =>
		new HubConnectionBuilder()
			.withUrl(`${hosts.main}/hubs/live`, { transport: HttpTransportType.WebSockets, skipNegotiation: true, accessTokenFactory: () => token })
			.configureLogging(LogLevel.None)
			.build();
	const hubs = { border: hub(border), terminal: hub(terminal), handler: hub(handler) };
	const received: Record<string, any[]> = { border: [], terminal: [], handler: [] };
	for (const [name, connection] of Object.entries(hubs)) connection.on('alert', (n) => received[name].push(n));
	await Promise.all(Object.values(hubs).map((h) => h.start()));
	try {
		for (const connection of Object.values(hubs)) expect(await connection.invoke('JoinAlerts', 'DMO')).toBe(1);
		const redis = createClient({ url: redisUrl });
		await redis.connect();
		try {
			const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
			const notice = {
				alertId: crypto.randomUUID(), siteCode: 'DMO', ruleCode: channelRule.code, ruleName: 'E2E desks channels', zoneName: 'A-CRW', deviceCode: null,
				metric: 'DesksBelowPlan', severity: 'Warning', state: 'Raised', raisedUtc: new Date().toISOString(), raisedValue: 3,
				ownerRole: null, escalateToRole: null, escalated: false, resolvedUtc: null, publishedUtc: new Date().toISOString()
			};
			const control = { ...notice, alertId: crypto.randomUUID(), ruleCode: 'R-005', metric: 'Nowcast' };
			await redis.publish(`${instance}live:alerts`, JSON.stringify(notice));
			await redis.publish(`${instance}live:alerts`, JSON.stringify(control));
			const acknowledged = await call('POST', `${alertsApi}/${ids.hub}/acknowledge`, { token: border, data: { note: 'Opening two desks' } });
			expect(acknowledged.status(), await acknowledged.text()).toBe(200);

			await expect.poll(() => received.border.map((n) => n.alertId), { timeout: 10_000 }).toEqual(expect.arrayContaining([notice.alertId, control.alertId, ids.hub]));
			await expect.poll(() => received.terminal.map((n) => n.alertId), { timeout: 10_000 }).toContain(control.alertId);
			await expect.poll(() => received.handler.map((n) => n.alertId), { timeout: 10_000 }).toContain(control.alertId);
			await new Promise((resolve) => setTimeout(resolve, 1000));
			for (const name of ['terminal', 'handler'])
				expect(received[name].filter((n) => n.metric === 'DesksBelowPlan'), `${name}: no border notice, with or without an owner`).toEqual([]);
		} finally {
			await redis.quit();
		}
	} finally {
		await Promise.all(Object.values(hubs).map((h) => h.stop()));
	}
});

test('hostile rule names are stored and answered as text; hostile zones are refused without an echo', async () => {
	for (const name of [...xssPayloads, ...sqlInjectionPayloads]) {
		const kept = await create(rule({ name: `${name} ${runId}`.slice(0, 200) }));
		const read = await call('GET', `${rulesApi}/${kept.id}`, { token: border });
		expect(read.headers()['content-type']).toContain('application/json');
		expect((await read.json()).name).toBe(`${name} ${runId}`.slice(0, 200));
	}
	for (const zone of [...xssPayloads, ...sqlInjectionPayloads]) {
		const refused = await call('POST', rulesApi, { token: border, data: rule({ zones: [zone] }) });
		const text = await refused.text();
		expect(refused.status(), zone).toBe(400);
		for (const fragment of [...markupFragments, zone]) expect(text, `${zone}: nothing echoed`).not.toContain(fragment);
		expectNoLeak(text, zone);
	}
});
