import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { expect, test } from '@playwright/test';
import { createClient } from 'redis';
import { accounts, call, databaseAvailable, login, signIn, unusedTotpCode, type Account } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';
import { staffingDatabase as database } from '../support/staffing';

// ARV-187 (owner decision 2026-10-16): the predicted nowcast (PredictedNowcast, a breach projected 15 to 60 minutes ahead)
// steps a lane queue with its share of the arrival wave, so its values carry the lane split that only holders of
// ArrivalWaveLanes.View see. It is border data like open desks below recommendation: rules are created, changed and
// backtested only by holders of the permission (others: 403 with the one BorderOnly text), belong to a border role (the
// supervisor or the administrator, never no owner) and escalate to one or to no role, with any of the four comparators and
// on overflow bands too; other roles never find them (search leaves them out, get, update and delete answer 404). A rule
// from before the story that an airport-side role owns is hidden from that role, repaired by an administrator and removed by
// a supervisor. Alerts on the metric reach border roles only, whatever their owner and escalation roles say: the alerts API
// (with the raised value and the predicted minute), the live hub, the daily report and its CSV. Ariva.Api.Stream does not run
// in the E2E run, so alerts are planted as the evaluation writes them (the live evaluation, the not-evaluated rule and the
// email sender are proven by the integration PredictedAlertTests and AlertEmailTests).

test.skip(!databaseAvailable, 'predicted-breach rules need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const rulesApi = `${hosts.main}/api/v1/admin/alert-rules`;
const alertsApi = `${hosts.main}/api/v1/alerts`;
const reportsApi = `${hosts.main}/api/v1/sites/DMO/reports`;
const redisUrl = process.env.ARIVA_E2E_REDIS_URL;

// The rule service's answers, as the web app matches them (Ariva.Core AlertRuleErrors; one text for both border metrics).
const borderOnly =
	'Open desks below recommendation and predicted nowcast are border data: only a caller with ArrivalWaveLanes.View creates, changes, imports or backtests a rule on them.';
const borderRolesOnly =
	'A rule on open desks below recommendation or predicted nowcast belongs to a border role: its owner is the Border shift supervisor or the system administrator (never empty), and it escalates to one of them or to no role.';
const roleNotHeld = 'Only an administrator can give a rule to, escalate it to, or take it from a role the caller does not hold.';
const unknownMetric = 'Unknown metric, comparator or severity.';

// DMO is in Asia/Dubai (UTC+4). The planted alerts are the day before yesterday (06:40 UTC), so they never change the alert
// count of yesterday's report that the reports suites read at the same time.
const dubaiNow = new Date(Date.now() + 4 * 3_600_000);
const yesterday = new Date(Date.UTC(dubaiNow.getUTCFullYear(), dubaiNow.getUTCMonth(), dubaiNow.getUTCDate() - 1));
const alertDay = new Date(yesterday.getTime() - 86_400_000);
const date = alertDay.toISOString().slice(0, 10);
const alertRaised = new Date(alertDay.getTime() + (6 * 60 + 40) * 60_000);
const iso = (value: Date) => value.toISOString().replace(/\.\d{3}Z$/, 'Z');
const runId = Date.now().toString(36);

/** The last minute that has ended, and the hour before it: the backtest that would carry a projection. */
const range = () => {
	const ended = new Date(Math.floor(Date.now() / 60_000) * 60_000);
	return { fromUtc: iso(new Date(ended.getTime() - 3_600_000)), toUtc: iso(ended) };
};

let border: string, terminal: string, handler: string, admin: string, observer: string, elsewhere: string;
let borderStepped: string, terminalStepped: string;
const created: string[] = [];
const plantedAlerts: string[] = [];

const rule = (overrides: Record<string, unknown> = {}) => ({
	siteCode: 'DMO',
	name: `E2E wave ahead ${runId}`,
	zones: ['A-VIS'],
	metric: 'PredictedNowcast',
	comparator: 'GreaterThan',
	threshold: 20,
	minQueueLength: null,
	clearThreshold: null,
	sustainMinutes: 2,
	clearAfterMinutes: 2,
	severity: 'Warning',
	ownerRole: 'BorderShiftSupervisor',
	escalateAfterMinutes: null,
	escalateToRole: null,
	escalationContact: null,
	notifyByEmail: false,
	enabled: true,
	leadMinutes: 30,
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

async function sql(text: string, values: unknown[]): Promise<number> {
	const db = database();
	await db.connect();
	try {
		return (await db.query(text, values)).rowCount ?? 0;
	} finally {
		await db.end();
	}
}

test.beforeAll(async () => {
	[border, terminal, handler, admin, observer, elsewhere] = await Promise.all(
		[accounts().dmoBorder, accounts().dmoTerminal, accounts().dmoHandler, accounts().webAdmin, accounts().ValidationObserver, accounts().BorderShiftSupervisor].map(
			async (a) => (await signIn(a)).accessToken
		)
	);
	borderStepped = await stepped(accounts().predBorder);
	terminalStepped = await stepped(accounts().predTerminal);
});

test.afterAll(async () => {
	if (plantedAlerts.length > 0)
		await sql(
			`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = greatest(now(), raised_utc), resolved_by = 'e2e-cleanup', resolution_note = 'cleanup'
			 WHERE id = ANY($1::uuid[]) AND state <> 'Resolved'`,
			[plantedAlerts]
		);
	// A legacy rule is put back to a border owner first, so the delete reaches it whatever a failed test left.
	if (created.length > 0)
		await sql(`UPDATE alert_rule SET owner_role = 'BorderShiftSupervisor', escalate_after_minutes = NULL, escalate_to_role = NULL WHERE id = ANY($1::uuid[])`, [
			created
		]);
	for (const id of created) await call('DELETE', `${rulesApi}/${id}`, { token: borderStepped });
});

test('a border shift supervisor creates, reads, changes, backtests and deletes a rule, below its threshold and on an overflow band', async () => {
	const wave = await create(rule({ comparator: 'LessThan', threshold: 5, clearThreshold: 8, zones: ['A-VIS', 'A-OV'] }));
	expect(wave).toMatchObject({
		metric: 'PredictedNowcast', comparator: 'LessThan', threshold: 5, clearThreshold: 8, leadMinutes: 30, ownerRole: 'BorderShiftSupervisor', zones: ['A-VIS', 'A-OV']
	});
	expect((await call('GET', `${rulesApi}/${wave.id}`, { token: border })).status()).toBe(200);
	const changed = await call('PUT', `${rulesApi}/${wave.id}`, {
		token: border,
		data: rule({ comparator: 'LessThan', threshold: 6, clearThreshold: 9, zones: ['A-VIS', 'A-OV'], escalateAfterMinutes: 10, escalateToRole: 'BorderShiftSupervisor' })
	});
	expect(changed.status(), await changed.text()).toBe(200);
	expect(await changed.json()).toMatchObject({ threshold: 6, escalateToRole: 'BorderShiftSupervisor', leadMinutes: 30 });

	const backtest = await call('POST', `${rulesApi}/backtest`, { token: border, data: { rule: rule({ zones: ['A-VIS', 'A-OV'] }), ...range() } });
	expect(backtest.status(), await backtest.text()).toBe(200);
	const found = await backtest.json();
	expect(found).toMatchObject({ targets: 2, truncated: false });
	expect(found.alerts.length).toBe(found.count);
	const byAdmin = await call('POST', `${rulesApi}/backtest`, { token: admin, data: { rule: rule({ ownerRole: 'SystemAdministrator' }), ...range() } });
	expect(byAdmin.status(), 'the administrator sees border data too').toBe(200);

	expect((await call('DELETE', `${rulesApi}/${wave.id}`, { token: border })).status(), 'deleting needs a recent second factor').toBe(401);
	expect((await call('DELETE', `${rulesApi}/${wave.id}`, { token: borderStepped })).status()).toBe(204);
	expect((await call('GET', `${rulesApi}/${wave.id}`, { token: border })).status()).toBe(404);
	created.splice(created.indexOf(wave.id), 1);
});

test('the rule belongs to a border role; anything else answers 400 with nothing echoed', async () => {
	for (const [why, token, data, detail] of [
		['owned by the terminal duty manager', border, rule({ ownerRole: 'TerminalDutyManager' }), borderRolesOnly],
		['owned by the handler station manager', admin, rule({ ownerRole: 'HandlerStationManager' }), borderRolesOnly],
		['no owner, even from an administrator', admin, rule({ ownerRole: null }), borderRolesOnly],
		['escalated to the terminal duty manager', admin, rule({ escalateAfterMinutes: 10, escalateToRole: 'TerminalDutyManager' }), borderRolesOnly],
		['escalated to the handler station manager', admin, rule({ ownerRole: 'SystemAdministrator', escalateAfterMinutes: 10, escalateToRole: 'HandlerStationManager' }), borderRolesOnly],
		['no owner, firing below', admin, rule({ ownerRole: null, comparator: 'LessThan' }), borderRolesOnly],
		['lower case', border, rule({ metric: 'predictednowcast' }), unknownMetric],
		['padded', border, rule({ metric: 'PredictedNowcast ' }), unknownMetric],
		['two words', border, rule({ metric: 'Predicted Nowcast' }), unknownMetric],
		['a number', border, rule({ metric: '7' }), unknownMetric]
	] as const) {
		const refused = await call('POST', rulesApi, { token, data });
		const text = await refused.text();
		expect(refused.status(), `${why}: ${text}`).toBe(400);
		expect(JSON.parse(text).detail, why).toBe(detail);
		expectNoLeak(text, why);
	}
	const backtest = await call('POST', `${rulesApi}/backtest`, { token: border, data: { rule: rule({ ownerRole: null }), ...range() } });
	expect(backtest.status(), 'a backtest judges the rule as it would be created').toBe(400);
	expect((await backtest.json()).detail).toBe(borderRolesOnly);
});

test('other roles are refused 403 on create, change and backtest, and never find a predicted rule (404)', async () => {
	const wave = await create(rule({ name: `E2E wave hidden ${runId}` }));
	const forbidden = async (method: string, url: string, token: string, data?: unknown) => {
		const answer = await call(method, url, { token, data });
		const text = await answer.text();
		expect(answer.status(), `${method} ${url}: ${text}`).toBe(403);
		return text;
	};

	// The terminal duty manager holds the alert rule permissions and the live queues, but not the lane split.
	expect(JSON.parse(await forbidden('POST', rulesApi, terminal, rule({ ownerRole: 'TerminalDutyManager' }))).detail).toBe(borderOnly);
	expect(JSON.parse(await forbidden('POST', rulesApi, terminal, rule())).detail).toBe(borderOnly);
	expect(JSON.parse(await forbidden('POST', `${rulesApi}/backtest`, terminal, { rule: rule({ ownerRole: 'TerminalDutyManager' }), ...range() })).detail,
		'the backtest ending now, which would carry a projection').toBe(borderOnly);
	const backwards = range();
	expect(JSON.parse(await forbidden('POST', `${rulesApi}/backtest`, terminal, { rule: rule(), fromUtc: backwards.toUtc, toUtc: backwards.fromUtc })).detail,
		'refused before the range is checked').toBe(borderOnly);
	const r005 = ((await (await call('GET', `${rulesApi}?siteCode=DMO&pageSize=500`, { token: terminal })).json()).data as any[]).find((r) => r.code === 'R-005');
	expect(r005, 'the seed rule R-005 the duty manager sees').toBeDefined();
	const moved = await forbidden('PUT', `${rulesApi}/${r005.id}`, terminal, rule({ name: r005.name, ownerRole: r005.ownerRole, zones: r005.zones }));
	expect(JSON.parse(moved).detail, 'a rule it sees, changed to the predicted nowcast').toBe(borderOnly);
	expect((await (await call('GET', `${rulesApi}/${r005.id}`, { token: terminal })).json()).metric).toBe('Nowcast');
	const listed = ((await (await call('GET', `${rulesApi}?siteCode=DMO&pageSize=500`, { token: terminal })).json()).data as any[]).map((r) => r.metric);
	expect(listed, 'search leaves both border metrics out').not.toContain('PredictedNowcast');
	expect(listed).not.toContain('DesksBelowPlan');
	const searched = await call('GET', `${rulesApi}?siteCode=DMO&metric=PredictedNowcast`, { token: terminal });
	expect(searched.status()).toBe(200);
	expect((await searched.json()).totalCount).toBe(0);
	expect((await call('GET', `${rulesApi}/${wave.id}`, { token: terminal })).status()).toBe(404);
	expect((await call('PUT', `${rulesApi}/${wave.id}`, { token: terminal, data: rule({ name: 'Taken over', ownerRole: 'TerminalDutyManager' }) })).status()).toBe(404);
	expect((await call('DELETE', `${rulesApi}/${wave.id}`, { token: terminalStepped })).status(), 'the duty manager cannot delete it').toBe(404);

	// The handler station manager reads rules but writes none; the observer holds no alert permission.
	await forbidden('POST', rulesApi, handler, rule());
	expect(((await (await call('GET', `${rulesApi}?siteCode=DMO&pageSize=500`, { token: handler })).json()).data as any[]).map((r) => r.metric)).not.toContain('PredictedNowcast');
	expect((await call('GET', `${rulesApi}/${wave.id}`, { token: handler })).status()).toBe(404);
	await forbidden('POST', rulesApi, observer, rule());
	await forbidden('POST', `${rulesApi}/backtest`, observer, { rule: rule(), ...range() });
	expect((await call('POST', rulesApi, { data: rule() })).status()).toBe(401);
	expect((await call('POST', `${rulesApi}/backtest`, { data: { rule: rule(), ...range() } })).status()).toBe(401);
	expect((await call('GET', `${rulesApi}/${wave.id}`)).status()).toBe(401);

	// Another site's supervisor: its own permission, not this site.
	expect((await call('POST', rulesApi, { token: elsewhere, data: rule() })).status()).toBe(400);
	expect((await call('POST', `${rulesApi}/backtest`, { token: elsewhere, data: { rule: rule(), ...range() } })).status()).toBe(400);
	expect((await call('GET', `${rulesApi}/${wave.id}`, { token: elsewhere })).status()).toBe(404);

	const still = await call('GET', `${rulesApi}/${wave.id}`, { token: border });
	expect(await still.json(), 'nothing the other roles tried changed it').toMatchObject({ name: `E2E wave hidden ${runId}`, metric: 'PredictedNowcast' });
});

test('a rule from before the story owned by the duty manager is hidden from it, repaired by an administrator and removed by a supervisor', async () => {
	// As a release before ARV-187 could have left it: made here by changing a supervisor's rule in the database (kept for as
	// short a time as possible, since a demo airport rule that fails the border checks refuses its export).
	const legacy = await create(rule({ name: `E2E wave legacy ${runId}`, zones: ['A-CIT'] }));
	const toDuty = () => sql(`UPDATE alert_rule SET owner_role = 'TerminalDutyManager' WHERE id = $1`, [legacy.id]);
	try {
		expect(await toDuty()).toBe(1);
		expect((await call('GET', `${rulesApi}/${legacy.id}`, { token: terminal })).status(), 'its old owner no longer finds it').toBe(404);
		expect((await call('DELETE', `${rulesApi}/${legacy.id}`, { token: terminalStepped })).status()).toBe(404);
		expect((await (await call('GET', `${rulesApi}?siteCode=DMO&metric=PredictedNowcast`, { token: terminal })).json()).totalCount).toBe(0);
		const seen = await call('GET', `${rulesApi}/${legacy.id}`, { token: border });
		expect(await seen.json(), 'the supervisor sees it as it is').toMatchObject({ ownerRole: 'TerminalDutyManager', metric: 'PredictedNowcast' });

		const disabled = await call('PUT', `${rulesApi}/${legacy.id}`, {
			token: border,
			data: rule({ name: `E2E wave legacy ${runId}`, zones: ['A-CIT'], ownerRole: 'TerminalDutyManager', enabled: false })
		});
		expect(disabled.status(), 'disabled or not, it needs a border owner first').toBe(400);
		expect((await disabled.json()).detail).toBe(borderRolesOnly);
		const taken = await call('PUT', `${rulesApi}/${legacy.id}`, { token: border, data: rule({ name: `E2E wave legacy ${runId}`, zones: ['A-CIT'] }) });
		expect(taken.status(), 'the supervisor cannot take it from the duty manager').toBe(400);
		expect((await taken.json()).detail).toBe(roleNotHeld);

		const repaired = await call('PUT', `${rulesApi}/${legacy.id}`, { token: admin, data: rule({ name: `E2E wave legacy ${runId}`, zones: ['A-CIT'] }) });
		expect(repaired.status(), await repaired.text()).toBe(200);
		expect(await repaired.json()).toMatchObject({ ownerRole: 'BorderShiftSupervisor', metric: 'PredictedNowcast' });

		expect(await toDuty()).toBe(1);
		expect((await call('DELETE', `${rulesApi}/${legacy.id}`, { token: borderStepped })).status(), 'a supervisor removes a legacy rule it sees').toBe(204);
		created.splice(created.indexOf(legacy.id), 1);
	} finally {
		await sql(`UPDATE alert_rule SET owner_role = 'BorderShiftSupervisor' WHERE id = $1 AND deleted_on IS NULL`, [legacy.id]);
	}
	expect((await call('GET', `${rulesApi}/${legacy.id}`, { token: border })).status()).toBe(404);
});

test('alerts on the metric reach border roles only, with their predicted value: the alerts API, the live hub and the daily report', async () => {
	test.setTimeout(60_000);
	const channelRule = await create(rule({ name: `E2E wave channels ${runId}`, zones: ['A-CRW', 'A-CIT', 'A-RES', 'A-VIS'] }));
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
				                    escalate_to_role, raised_utc, raised_value, predicted_for_utc, state, escalated_utc, escalated_by)
				 SELECT gen_random_uuid(), site_code, id, code, name, $2, NULL, metric, severity, $3::varchar, CASE WHEN $4::varchar IS NULL THEN NULL ELSE 10 END,
				        $4::varchar, $5, 48.2, $5::timestamptz + interval '30 minutes', CASE WHEN $6 THEN 'Escalated' ELSE 'Raised' END,
				        CASE WHEN $6 THEN $5::timestamptz + interval '10 minutes' END, CASE WHEN $6 THEN 'e2e' END
				 FROM alert_rule WHERE id = $1 RETURNING id`,
				[channelRule.id, zone, owner, escalateTo, alertRaised, escalated]
			);
			ids[key] = planted.rows[0].id;
			plantedAlerts.push(ids[key]);
		}
	} finally {
		await db.end();
	}

	const listed = async (token: string) => (await (await call('GET', `${alertsApi}?siteCode=DMO&pageSize=500`, { token })).json()).data as any[];
	for (const [token, who] of [
		[terminal, 'terminal duty manager'],
		[handler, 'handler station manager']
	] as const) {
		const seen = await listed(token);
		expect(seen.filter((a) => a.metric === 'PredictedNowcast' || a.predictedForUtc !== null), `${who}: no predicted value or minute at all`).toEqual([]);
		for (const id of Object.values(ids)) {
			expect(seen.map((a) => a.id), `${who}: ${id}`).not.toContain(id);
			expect((await call('GET', `${alertsApi}/${id}`, { token })).status(), who).toBe(404);
			expect((await call('POST', `${alertsApi}/${id}/acknowledge`, { token, data: { note: 'Mine' } })).status(), who).toBe(404);
		}
		expect((await (await call('GET', `${alertsApi}?siteCode=DMO&ruleCode=${channelRule.code}`, { token })).json()).totalCount, `${who}: not even counted`).toBe(0);
	}
	const bySupervisor = (await listed(border)).map((a) => a.id);
	expect(bySupervisor).toEqual(expect.arrayContaining([ids.owned, ids.ownerless, ids.hub]));
	expect(bySupervisor, 'the supervisor is neither owner nor escalation role of the duty manager’s row').not.toContain(ids.airport);
	expect((await listed(admin)).map((a) => a.id)).toEqual(expect.arrayContaining(Object.values(ids)));
	const owned = await (await call('GET', `${alertsApi}/${ids.owned}`, { token: border })).json();
	expect(owned, 'the border sees the predicted value and minute').toMatchObject({
		metric: 'PredictedNowcast', raisedValue: 48.2, predictedForUtc: iso(new Date(alertRaised.getTime() + 30 * 60_000))
	});

	// The daily report and its alerts CSV, for the duty manager and for the supervisor.
	const daily = async (token: string) => ((await (await call('GET', `${reportsApi}/daily?date=${date}`, { token })).json()).alerts as any[]).map((a) => a.ruleCode);
	expect(await daily(terminal)).not.toContain(channelRule.code);
	expect((await call('GET', `${reportsApi}/daily?date=${date}`, { token: handler })).status(), 'the handler station manager reads no daily report').toBe(403);
	expect(await daily(border)).toContain(channelRule.code);
	const csv = async (token: string) => (await call('GET', `${reportsApi}/daily.csv?date=${date}&section=alerts`, { token })).text();
	expect(await csv(terminal), 'the rule name is unique to this run').not.toContain(`E2E wave channels ${runId}`);
	expect(await csv(border)).toContain(`E2E wave channels ${runId}`);

	// The live hub: a notice the Stream host would publish without an owner, a wait control, and the supervisor's acknowledgement.
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
				alertId: crypto.randomUUID(), siteCode: 'DMO', ruleCode: channelRule.code, ruleName: 'E2E wave channels', zoneName: 'A-CIT', deviceCode: null,
				metric: 'PredictedNowcast', severity: 'Warning', state: 'Raised', raisedUtc: new Date().toISOString(), raisedValue: 48.2,
				ownerRole: null, escalateToRole: null, escalated: false, resolvedUtc: null, publishedUtc: new Date().toISOString()
			};
			const control = { ...notice, alertId: crypto.randomUUID(), ruleCode: 'R-005', metric: 'Nowcast' };
			await redis.publish(`${instance}live:alerts`, JSON.stringify(notice));
			await redis.publish(`${instance}live:alerts`, JSON.stringify(control));
			const acknowledged = await call('POST', `${alertsApi}/${ids.hub}/acknowledge`, { token: border, data: { note: 'Opening a lane' } });
			expect(acknowledged.status(), await acknowledged.text()).toBe(200);

			await expect.poll(() => received.border.map((n) => n.alertId), { timeout: 10_000 }).toEqual(expect.arrayContaining([notice.alertId, control.alertId, ids.hub]));
			await expect.poll(() => received.terminal.map((n) => n.alertId), { timeout: 10_000 }).toContain(control.alertId);
			await expect.poll(() => received.handler.map((n) => n.alertId), { timeout: 10_000 }).toContain(control.alertId);
			await new Promise((resolve) => setTimeout(resolve, 1000));
			for (const name of ['terminal', 'handler'])
				expect(received[name].filter((n) => n.metric === 'PredictedNowcast'), `${name}: no predicted notice, with or without an owner`).toEqual([]);
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
	// A hostile text in the metric filter is refused, not run.
	for (const metric of sqlInjectionPayloads) {
		const refused = await call('GET', `${rulesApi}?siteCode=DMO&metric=${encodeURIComponent(metric)}`, { token: terminal });
		expect(refused.status(), metric).toBe(400);
		expectNoLeak(await refused.text(), metric);
	}
});
