import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-037: alert rules through the API. The demo seed's R-001 to R-005 are on DMO; an administrator creates, edits and
// deletes a rule there (codes go on from the seed's, enums travel by their exact names, deletes are soft, need a second
// factor in the last 15 minutes and the code is not given again); typed values only: numbers, other cases, padding or
// comma-joined names for an enum, a comparator that does not fit the metric, zones outside the published profile are
// refused, hostile names are kept as plain text; callers of other sites see nothing, roles a caller does not hold are
// refused, and handlers read but never write.

test.skip(!databaseAvailable, 'alert rules need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/admin/alert-rules`;
let administrator: string;
let ruleId: string;
let ruleCode: string;

const rule = (overrides: Record<string, unknown> = {}) => ({
	siteCode: 'DMO',
	name: 'E2E visitors wave',
	zones: ['A-VIS', 'D-VIS'],
	metric: 'Nowcast',
	comparator: 'GreaterThan',
	threshold: 20,
	minQueueLength: 5,
	clearThreshold: 15,
	sustainMinutes: 2,
	clearAfterMinutes: 2,
	severity: 'Warning',
	ownerRole: 'BorderShiftSupervisor',
	escalateAfterMinutes: 10,
	escalateToRole: 'TerminalDutyManager',
	escalationContact: null,
	notifyByEmail: false,
	enabled: true,
	...overrides
});

const number = (code: string) => Number(code.slice(2));

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().alertAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	administrator = (await signedIn.json()).accessToken;
});

test('the demo seed gives DMO the prototype rules R-001 to R-005', async () => {
	const page = await call('GET', `${api}?siteCode=DMO&pageSize=500`, { token: administrator });
	expect(page.status(), await page.text()).toBe(200);
	const body = await page.json();
	const seeded = body.data.filter((r: any) => /^R-00[1-5]$/.test(r.code));
	// Another run may have deleted some; a fresh database has all five.
	expect(seeded.length).toBeGreaterThan(0);
	const r001 = seeded.find((r: any) => r.code === 'R-001');
	if (r001) expect(r001).toMatchObject({ metric: 'Nowcast', comparator: 'GreaterThan', threshold: 15, severity: 'Critical', ownerRole: 'BorderShiftSupervisor' });
});

test('an administrator creates, edits and deletes a rule; the code is never given again', async () => {
	const created = await call('POST', api, { token: administrator, data: rule() });
	expect(created.status(), await created.text()).toBe(201);
	const view = await created.json();
	ruleId = view.id;
	ruleCode = view.code;
	expect(view.code).toMatch(/^R-[0-9]{3,4}$/);
	expect(number(view.code)).toBeGreaterThan(5);
	expect(view).toMatchObject({ siteCode: 'DMO', metric: 'Nowcast', comparator: 'GreaterThan', severity: 'Warning', zones: ['A-VIS', 'D-VIS'] });
	expect(created.headers()['location']).toContain(`/api/v1/admin/alert-rules/${ruleId}`);

	const updated = await call('PUT', `${api}/${ruleId}`, { token: administrator, data: rule({ threshold: 25, enabled: false }) });
	expect(updated.status(), await updated.text()).toBe(200);
	expect(await updated.json()).toMatchObject({ code: ruleCode, threshold: 25, enabled: false });
	expect((await call('PUT', `${api}/${ruleId}`, { token: administrator, data: rule({ siteCode: 'E2E1' }) })).status(), 'a rule stays in its site').toBe(400);

	const passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;
	const unconfirmed = await call('DELETE', `${api}/${ruleId}`, { token: passwordOnly });
	expect(unconfirmed.status(), 'deleting is critical: a second factor in the last 15 minutes').toBe(401);
	expect(unconfirmed.headers()['www-authenticate']).toContain('insufficient_user_authentication');
	expect((await call('GET', `${api}/${ruleId}`, { token: administrator })).status(), 'still there').toBe(200);
	expect((await call('DELETE', `${api}/${ruleId}`, { token: administrator })).status()).toBe(204);
	expect((await call('GET', `${api}/${ruleId}`, { token: administrator })).status()).toBe(404);
	expect((await call('DELETE', `${api}/${ruleId}`, { token: administrator })).status(), 'deleted twice').toBe(404);

	const next = await call('POST', api, { token: administrator, data: rule({ name: 'E2E after the delete' }) });
	expect(next.status(), await next.text()).toBe(201);
	const nextView = await next.json();
	expect(number(nextView.code)).toBeGreaterThan(number(ruleCode));
	await call('DELETE', `${api}/${nextView.id}`, { token: administrator });
});

test('typed values only: numbers for enums, misfit comparators, unknown zones and roles are refused', async () => {
	for (const [why, overrides] of [
		['metric as a number', { metric: '0' }],
		['metric in another case', { metric: 'nowcast' }],
		['metric with padding', { metric: 'Nowcast ' }],
		['metric names joined', { metric: 'QueueLength,BinP90' }],
		['combined severity flags', { severity: 'Critical,Warning' }],
		['severity names joined', { severity: 'Info,Warning' }],
		['comparator names joined', { comparator: 'GreaterThan,GreaterOrEqual' }],
		['IsTrue on a number', { comparator: 'IsTrue' }],
		['clear threshold on the firing side', { clearThreshold: 30 }],
		['threshold beyond the unit', { threshold: 601 }],
		['zone outside the published profile', { zones: ['A-VIS', 'NOT-A-ZONE'] }],
		['role outside the catalogue', { ownerRole: 'Root' }],
		['escalation without minutes', { escalateAfterMinutes: null }],
		['sustain out of range', { sustainMinutes: 0 }],
		['name with a control character', { name: 'Rule\u0007' }],
		['name with a bidirectional override', { name: 'Rule \u202Eevil' }],
		['an expression field', { expression: 'queue > 10', name: 'E2E expression' }]
	] as const) {
		// An expression field is an unknown member, refused like every other (ARV-089): the rule is the typed fields and
		// nothing evaluates an extra one (CWE-94).
		const response = await call('POST', api, { token: administrator, data: rule(overrides) });
		expect(response.status(), `${why}: ${await response.text()}`).toBe(400);
		expectNoLeak(await response.text(), why);
	}
});

test('hostile names are stored and returned as plain text', async () => {
	for (const name of ['<script>alert(1)</script>', "'; DROP TABLE alert_rule; --", '{{7*7}} ${7*7}', '=cmd|\' /C calc\'!A0', 'قاعدة الانتظار']) {
		const created = await call('POST', api, { token: administrator, data: rule({ name }) });
		expect(created.status(), `${name}: ${await created.text()}`).toBe(201);
		const view = await created.json();
		expect(view.name).toBe(name);
		expect(created.headers()['content-type']).toContain('application/json');
		const read = await call('GET', `${api}/${view.id}`, { token: administrator });
		expect((await read.json()).name).toBe(name);
		await call('DELETE', `${api}/${view.id}`, { token: administrator });
	}
});

test("other sites' callers see nothing, roles they lack are refused, and handlers only read", async () => {
	const probe = await (await call('POST', api, { token: administrator, data: rule({ name: 'E2E scope probe' }) })).json();
	try {
		const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
		expect((await call('GET', `${api}/${probe.id}`, { token: border })).status(), 'a DMO rule from E2E1').toBe(404);
		expect((await call('PUT', `${api}/${probe.id}`, { token: border, data: rule() })).status()).toBe(404);
		expect((await call('DELETE', `${api}/${probe.id}`, { token: border })).status(), 'step-up is asked first; nothing about the rule is told').toBe(401);
		expect((await call('GET', `${api}/${probe.id}`, { token: administrator })).status(), 'still there').toBe(200);
		expect((await call('POST', api, { token: border, data: rule() })).status(), 'create in a site outside the caller').toBe(400);
		const listed = await call('GET', api, { token: border });
		expect(listed.status()).toBe(200);
		expect((await listed.json()).data.filter((r: any) => r.siteCode === 'DMO')).toHaveLength(0);
		const zones = await call('POST', api, { token: border, data: rule({ siteCode: 'E2E1', escalateToRole: null, escalationContact: null }) });
		expect(zones.status(), 'E2E1 has no published profile with these zones').toBe(400);
		const lacked = await call('POST', api, { token: border, data: rule({ siteCode: 'E2E1', ownerRole: 'SystemAdministrator' }) });
		expect(lacked.status(), 'a role the creator does not hold').toBe(400);
		expect(await lacked.text(), 'refused for the role, not only for the zones').toContain('a role the caller does not hold');
		// ARV-056: a rule without an owner belongs to every role of the site, handlers included: an administrator's choice.
		const supervisor = (await signIn(accounts().webBorder)).accessToken;
		const everyRole = await call('POST', api, {
			token: supervisor,
			data: rule({ zones: ['A-CRW'], ownerRole: null, escalateAfterMinutes: null, escalateToRole: null, enabled: false })
		});
		expect(everyRole.status(), 'every role of the site from a supervisor').toBe(400);
		expect(await everyRole.text()).toContain('a role the caller does not hold');

		const handler = (await signIn(accounts().HandlerStationManager)).accessToken;
		expect((await call('GET', api, { token: handler })).status()).toBe(200);
		expect((await call('POST', api, { token: handler, data: rule({ siteCode: 'E2E1' }) })).status()).toBe(403);
		expect((await call('PUT', `${api}/${probe.id}`, { token: handler, data: rule() })).status()).toBe(403);
		expect((await call('DELETE', `${api}/${probe.id}`, { token: handler })).status()).toBe(403);
		expect((await call('GET', api)).status(), 'anonymous').toBe(401);
	} finally {
		await call('DELETE', `${api}/${probe.id}`, { token: administrator });
	}
});

// ARV-038: the backtest judges a rule (saved or not) on the stored minutes of an ended range of up to a day.
test('a backtest returns the count and first fire time, within bounds, for creators only', async () => {
	const now = Date.now();
	const fromUtc = new Date(now - 3 * 3600_000).toISOString();
	const toUtc = new Date(now - 60_000).toISOString();
	const ok = await call('POST', `${api}/backtest`, { token: administrator, data: { rule: rule(), fromUtc, toUtc } });
	expect(ok.status(), await ok.text()).toBe(200);
	const result = await ok.json();
	expect(result).toMatchObject({ targets: 2, truncated: false });
	expect(result.count).toBeGreaterThanOrEqual(0);
	expect(result.alerts.length).toBe(result.count);
	expect(result.firstRaisedUtc === null || typeof result.firstRaisedUtc === 'string').toBe(true);

	// ARV-187: a predicted nowcast rule is border data, so it escalates to a border role or none (the default rule's
	// escalation to the duty manager is refused; tests/api/predicted-breach-border.spec.ts covers the rest).
	const predicted = await call('POST', `${api}/backtest`, {
		token: administrator,
		data: { rule: rule({ metric: 'PredictedNowcast', leadMinutes: 30, minQueueLength: null, escalateAfterMinutes: null, escalateToRole: null }), fromUtc, toUtc }
	});
	expect(predicted.status(), await predicted.text()).toBe(200);
	expect((await predicted.json()).targetsWithData, 'no arrival-wave projection before ARV-047').toBe(0);

	for (const [why, data] of [
		['longer than a day', { rule: rule(), fromUtc: new Date(now - 26 * 3600_000).toISOString(), toUtc }],
		['not yet ended', { rule: rule(), fromUtc, toUtc: new Date(now + 3600_000).toISOString() }],
		['not UTC', { rule: rule(), fromUtc: fromUtc.replace('Z', ''), toUtc: toUtc.replace('Z', '') }],
		['backwards', { rule: rule(), fromUtc: toUtc, toUtc: fromUtc }],
		['lead time on a nowcast', { rule: rule({ leadMinutes: 30 }), fromUtc, toUtc }],
		['lead time out of range', { rule: rule({ metric: 'PredictedNowcast', leadMinutes: 90, minQueueLength: null }), fromUtc, toUtc }],
		['no rule', { fromUtc, toUtc }]
	] as const) {
		const refused = await call('POST', `${api}/backtest`, { token: administrator, data });
		expect(refused.status(), `${why}: ${await refused.text()}`).toBe(400);
	}

	const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
	expect((await call('POST', `${api}/backtest`, { token: border, data: { rule: rule(), fromUtc, toUtc } })).status(), 'a site outside the caller').toBe(400);
	const handler = (await signIn(accounts().HandlerStationManager)).accessToken;
	expect((await call('POST', `${api}/backtest`, { token: handler, data: { rule: rule({ siteCode: 'E2E1' }), fromUtc, toUtc } })).status()).toBe(403);
	expect((await call('POST', `${api}/backtest`, { data: { rule: rule(), fromUtc, toUtc } })).status()).toBe(401);
});

// ARV-114b: the continuous health checks of F18 as typed alert metrics (ConservationResidual in people, TrackCompletion in
// percent, OccupancyOutOfRange in minutes), judged per queue zone on the stored zone_health_bin rows. Ariva.Api.Stream is not
// part of the E2E run, so the suite plants a security queue's minutes and bins yesterday 05:00 to 06:00 UTC (SEC-N, which no
// other suite writes): the 05:15 bin's residual is 7 people, 15 of its 20 tracks exited (75 percent) and 2 minutes left its
// capacity; every other bin is healthy. Each metric's rule fires at 05:29, the last minute of that bin (the minute its end
// falls in), and clears at 05:44; HealthAlertTests checks the same fold ticked live against the backtest on TimescaleDB.

const yesterday = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
const morning = (time: string) => `${yesterday}T${time}:00Z`;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

const healthRule = (overrides: Record<string, unknown> = {}) =>
	rule({
		name: 'E2E health check',
		zones: ['SEC-N'],
		metric: 'TrackCompletion',
		comparator: 'LessThan',
		threshold: 90,
		minQueueLength: null,
		clearThreshold: null,
		sustainMinutes: 1,
		clearAfterMinutes: 1,
		enabled: false,
		...overrides
	});

async function clearHealth(db: pg.Client): Promise<void> {
	await db.query(`DELETE FROM queue_minute WHERE zone_key = 'DMO/SEC-N' AND minute_utc >= $1 AND minute_utc < $2`, [morning('05:00'), morning('06:00')]);
	await db.query(`DELETE FROM zone_health_bin WHERE zone_key = 'DMO/SEC-N' AND start_utc >= $1 AND start_utc < $2`, [morning('05:00'), morning('06:00')]);
}

/** SEC-N's queue minutes and final health bins yesterday 05:00 to 06:00, as the stream stores them (the migration login plants and removes them). */
async function plantHealth(): Promise<void> {
	const db = database();
	await db.connect();
	try {
		await clearHealth(db);
		for (let minute = 0; minute < 60; minute++) {
			await db.query(
				`INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, queue_length, length_from_sensors, length_degraded, nowcast_minutes,
				                           throughput_per_minute, nowcast_degraded, updated_on)
				 VALUES ('DMO/SEC-N', $1, 1, 'Final', 12, true, false, 3.2, 4, false, now())`,
				[new Date(Date.parse(morning('05:00')) + minute * 60_000)]
			);
		}
		for (const [start, residual, exited, outside] of [
			['05:00', 0, 20, 0],
			['05:15', 7, 15, 2],
			['05:30', 0, 20, 0],
			['05:45', 0, 19, 0]
		] as const) {
			await db.query(
				`INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
				     conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
				     track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
				 VALUES ('DMO/SEC-N', $1, 1, 15, 'Final', 1, 20, 20, 10, $2, $3, 20, $4, $5, 0, 0, 0, 0, $6, 15, 15, $7, now())`,
				[morning(start), 10 - residual, residual, exited, 20 - exited, exited / 20, outside]
			);
		}
	} finally {
		await db.end();
	}
}

test.describe('health-check metrics (ARV-114b)', () => {
	test.beforeAll(plantHealth);
	test.afterAll(async () => {
		const db = database();
		await db.connect();
		try {
			await clearHealth(db);
		} finally {
			await db.end();
		}
	});

	test('each health check is a typed number in its unit, created on a queue zone and backtested on the stored bins', async () => {
		for (const [metric, comparator, threshold, raisedValue] of [
			['ConservationResidual', 'GreaterThan', 4, 7],
			['TrackCompletion', 'LessThan', 90, 75],
			['OccupancyOutOfRange', 'GreaterThan', 0, 2]
		] as const) {
			const created = await call('POST', api, { token: administrator, data: healthRule({ name: `E2E ${metric}`, metric, comparator, threshold }) });
			expect(created.status(), created.status() === 201 ? `${metric} created` : await created.text()).toBe(201);
			const view = await created.json();
			try {
				expect(view).toMatchObject({ metric, comparator, threshold, zones: ['SEC-N'], minQueueLength: null, leadMinutes: null });
				const read = await call('GET', `${api}/${view.id}`, { token: administrator });
				expect(read.status(), read.status() === 200 ? `${metric} read back` : await read.text()).toBe(200);
				expect((await read.json()).metric, 'the metric travels by its exact name').toBe(metric);

				const backtest = await call('POST', `${api}/backtest`, {
					token: administrator,
					data: { rule: healthRule({ metric, comparator, threshold }), fromUtc: morning('05:00'), toUtc: morning('06:00') }
				});
				expect(backtest.status(), backtest.status() === 200 ? `${metric} backtested` : await backtest.text()).toBe(200);
				const result = await backtest.json();
				expect(result, `${metric}: one alert while the 05:15 bin governs`).toMatchObject({ count: 1, targets: 1, targetsWithData: 1, truncated: false });
				expect(Date.parse(result.firstRaisedUtc)).toBe(Date.parse(morning('05:29')));
				expect(result.alerts[0]).toMatchObject({ zoneName: 'SEC-N', value: raisedValue });
				expect(Date.parse(result.alerts[0].clearedUtc), `${metric} clears once the healthy 05:30 bin governs`).toBe(Date.parse(morning('05:44')));
				expect(Date.parse(result.alerts[0].binStartUtc), 'the alert names the bin it judged').toBe(Date.parse(morning('05:15')));
			} finally {
				await call('DELETE', `${api}/${view.id}`, { token: administrator });
			}
		}
	});

	test("unknown metric names, values outside a health check's unit and overflow zones are refused (CWE-94)", async () => {
		for (const [why, overrides] of [
			['an unknown metric name', { metric: 'ConservationResiduals' }],
			['a metric in another case', { metric: 'trackCompletion' }],
			['a metric with padding', { metric: ' TrackCompletion' }],
			['health metric names joined', { metric: 'TrackCompletion,OccupancyOutOfRange' }],
			['a metric as a number', { metric: '8' }],
			['an expression for a metric', { metric: 'tracks_exited / tracks_entered < 0.9' }],
			['a template for a metric', { metric: '${7*7}' }],
			['SQL for a metric', { metric: "TrackCompletion' OR '1'='1" }],
			['markup for a metric', { metric: '<script>alert(1)</script>' }],
			['a metric longer than its field', { metric: 'TrackCompletion'.repeat(4) }],
			['completion above 100 percent', { threshold: 100.5 }],
			['a negative completion', { threshold: -1 }],
			['a residual beyond 100,000 people', { metric: 'ConservationResidual', comparator: 'GreaterThan', threshold: 100_001 }],
			['occupancy minutes beyond a day', { metric: 'OccupancyOutOfRange', comparator: 'GreaterThan', threshold: 1441 }],
			['IsTrue on a health check', { metric: 'OccupancyOutOfRange', comparator: 'IsTrue', threshold: null }],
			['a minimum queue on a health check', { minQueueLength: 5 }],
			['a lead time on a health check', { leadMinutes: 30 }],
			['a clear threshold on the firing side', { clearThreshold: 80 }],
			['an overflow zone for a health check', { zones: ['SEC-N', 'SEC-OV'] }],
			['only an overflow zone for a health check', { zones: ['SEC-OV'] }]
		] as const) {
			const response = await call('POST', api, { token: administrator, data: healthRule(overrides) });
			const body = await response.text();
			expect(response.status(), response.status() === 400 ? `${why}: refused` : body).toBe(400);
			expectNoLeak(body, why);
			if (why.includes('overflow zone')) expect(body.includes('queue zones only'), `${why}: refused for the zone kind`).toBe(true);
			const backtest = await call('POST', `${api}/backtest`, {
				token: administrator,
				data: { rule: healthRule(overrides), fromUtc: morning('05:00'), toUtc: morning('06:00') }
			});
			expect(backtest.status(), backtest.status() === 400 ? `${why}: no backtest` : await backtest.text()).toBe(400);
		}
	});

	test('every rule endpoint keeps its permission for a health-check rule (CWE-862): anonymous, handlers and other sites are refused', async () => {
		const created = await call('POST', api, { token: administrator, data: healthRule({ name: 'E2E health authorization' }) });
		expect(created.status(), created.status() === 201 ? 'created' : await created.text()).toBe(201);
		const id = (await created.json()).id;
		try {
			const range = { fromUtc: morning('05:00'), toUtc: morning('06:00') };
			// Anonymous: 401 on every endpoint.
			for (const [method, url, data] of [
				['GET', api, undefined],
				['GET', `${api}/${id}`, undefined],
				['POST', api, healthRule()],
				['PUT', `${api}/${id}`, healthRule()],
				['DELETE', `${api}/${id}`, undefined],
				['POST', `${api}/backtest`, { rule: healthRule(), ...range }]
			] as const) {
				expect((await call(method, url, { data })).status(), `anonymous ${method} ${url.replace(hosts.main, '')}`).toBe(401);
			}

			// A handler station manager of the site reads rules but neither writes nor backtests them.
			const handler = (await signIn(accounts().dmoHandler)).accessToken;
			expect((await call('GET', `${api}/${id}`, { token: handler })).status(), 'a handler reads').toBe(200);
			expect((await call('POST', api, { token: handler, data: healthRule() })).status(), 'a handler creates').toBe(403);
			expect((await call('PUT', `${api}/${id}`, { token: handler, data: healthRule({ threshold: 50 }) })).status(), 'a handler edits').toBe(403);
			expect((await call('DELETE', `${api}/${id}`, { token: handler })).status(), 'a handler deletes').toBe(403);
			expect((await call('POST', `${api}/backtest`, { token: handler, data: { rule: healthRule(), ...range } })).status(), 'a handler backtests').toBe(403);

			// A supervisor of another site (E2E1): the DMO rule does not exist for them, and DMO's bins are not theirs to judge.
			const elsewhere = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
			expect((await call('GET', `${api}/${id}`, { token: elsewhere })).status(), 'a DMO health rule from E2E1').toBe(404);
			expect((await call('PUT', `${api}/${id}`, { token: elsewhere, data: healthRule({ threshold: 50 }) })).status(), 'edit from E2E1').toBe(404);
			expect((await call('POST', api, { token: elsewhere, data: healthRule() })).status(), 'create at DMO from E2E1').toBe(400);
			const foreign = await call('POST', `${api}/backtest`, { token: elsewhere, data: { rule: healthRule(), ...range } });
			expect(foreign.status(), 'backtest of DMO bins from E2E1').toBe(400);
			expect((await foreign.text()).includes('SEC-N'), 'the refusal says nothing of the bins').toBe(false);
			const listed = await call('GET', `${api}?siteCode=DMO&metric=TrackCompletion`, { token: elsewhere });
			expect(listed.status(), 'the search answers').toBe(200);
			expect((await listed.json()).data, 'no DMO rule is listed for E2E1').toHaveLength(0);
			// At their own site, DMO's queue zone is not one of E2E1's zones (CWE-863): refused as an unknown zone.
			const ownSite = await call('POST', api, {
				token: elsewhere,
				data: healthRule({ siteCode: 'E2E1', escalateAfterMinutes: null, escalateToRole: null, escalationContact: null })
			});
			const ownSiteBody = await ownSite.text();
			expect(ownSite.status(), ownSite.status() === 400 ? 'SEC-N at E2E1' : ownSiteBody).toBe(400);
			expect(ownSiteBody.includes('Every zone in scope must be a zone of the site'), 'refused for the zone').toBe(true);
			// An edit cannot slip an overflow band into a health rule either.
			const band = await call('PUT', `${api}/${id}`, { token: administrator, data: healthRule({ zones: ['SEC-N', 'SEC-OV'] }) });
			const bandBody = await band.text();
			expect(band.status(), band.status() === 400 ? 'SEC-OV added by an edit' : bandBody).toBe(400);
			expect(bandBody.includes('queue zones only'), 'refused for the zone kind').toBe(true);
			expect((await (await call('GET', `${api}/${id}`, { token: administrator })).json()).zones, 'the rule keeps its zones').toEqual(['SEC-N']);
			expect((await call('GET', `${api}/${id}`, { token: administrator })).status(), 'still there').toBe(200);
		} finally {
			await call('DELETE', `${api}/${id}`, { token: administrator });
		}
	});

	test('a search by metric takes the health checks by their exact names only', async () => {
		const created = await call('POST', api, {
			token: administrator,
			data: healthRule({ name: 'E2E health search', metric: 'OccupancyOutOfRange', comparator: 'GreaterThan', threshold: 0 })
		});
		expect(created.status(), created.status() === 201 ? 'created' : await created.text()).toBe(201);
		const id = (await created.json()).id;
		try {
			const found = await call('GET', `${api}?siteCode=DMO&metric=OccupancyOutOfRange&pageSize=500`, { token: administrator });
			expect(found.status(), 'search by a health metric').toBe(200);
			expect((await found.json()).data.map((r: { id: string }) => r.id)).toContain(id);
			for (const metric of ['occupancyoutofrange', 'OccupancyOutOfRange,TrackCompletion', '9', "OccupancyOutOfRange' OR '1'='1"]) {
				const refused = await call('GET', `${api}?siteCode=DMO&metric=${encodeURIComponent(metric)}`, { token: administrator });
				expect(refused.status(), `metric filter ${metric}`).toBe(400);
				expectNoLeak(await refused.text(), metric);
			}
		} finally {
			await call('DELETE', `${api}/${id}`, { token: administrator });
		}
	});
});
