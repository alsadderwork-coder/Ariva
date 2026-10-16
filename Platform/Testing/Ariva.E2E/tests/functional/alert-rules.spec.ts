import crypto from 'node:crypto';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import pg from 'pg';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, totpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { plantDesksShortCase, staffingDatabase } from '../support/staffing';
import { allowStatuses, databaseAvailable, earlyInStep, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';

// ARV-056: the alert rules screen on the rule API (ARV-037) and its backtest (ARV-038). A border shift supervisor
// creates a rule at the demo airport, previews when it would have fired on stored minutes, and the saved rule's backtest
// through the API gives the same first fire time; the live evaluation is the same fold (AlertEvaluationTests checks a
// rule ticked live minute by minute against its backtest, since Ariva.Api.Stream is not part of the E2E run). The rule is
// disabled, duplicated, changed and deleted after a step-up. Names are text; a handler station manager reads only.

test.skip(!databaseAvailable, 'the alert rules screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/admin/alert-rules`;
const run = crypto.randomBytes(3).toString('hex');
const ruleName = `E2E crew queue ${run}`;
// Yesterday 03:00 to 03:40 UTC: a quiet window of the reference zone that other suites do not write.
const day = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
const at = (time: string) => `${day}T${time}:00Z`;
const fromLocal = `${day}T02:50`;
const toLocal = `${day}T03:40`;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

/** What Ariva.Api.Stream stores for the crew lane: 5 people until 03:10, then 50 for twenty minutes. */
async function plantMinutes(): Promise<void> {
	const db = database();
	await db.connect();
	try {
		for (let minute = 0; minute < 30; minute++) {
			const time = new Date(Date.parse(at('03:00')) + minute * 60_000);
			await db.query(
				`INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, queue_length, length_from_sensors, length_degraded, nowcast_minutes,
				                           throughput_per_minute, nowcast_degraded, updated_on)
				 VALUES ('DMO/A-CRW', $1, 1, 'Final', $2, true, false, $3, 4, false, now())
				 ON CONFLICT (zone_key, minute_utc) DO UPDATE SET queue_length = EXCLUDED.queue_length, nowcast_minutes = EXCLUDED.nowcast_minutes,
				                                                  length_degraded = false, updated_on = now()`,
				[time, minute < 10 ? 5 : 50, minute < 10 ? 1.2 : 12.5]
			);
		}
	} finally {
		await db.end();
	}
}

/**
 * ARV-114b: what Ariva.Api.Stream stores for security south (SEC-S) yesterday 05:00 to 06:00: its queue minutes and four
 * final health bins, the 05:15 one with 15 of its 20 tracks exited (75 percent), the others at 90 percent or more.
 */
async function plantHealth(): Promise<void> {
	const db = database();
	await db.connect();
	try {
		await clearHealth(db);
		for (let minute = 0; minute < 60; minute++) {
			await db.query(
				`INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, queue_length, length_from_sensors, length_degraded, nowcast_minutes,
				                           throughput_per_minute, nowcast_degraded, updated_on)
				 VALUES ('DMO/SEC-S', $1, 1, 'Final', 12, true, false, 3.2, 4, false, now())`,
				[new Date(Date.parse(at('05:00')) + minute * 60_000)]
			);
		}
		for (const [start, exited] of [
			['05:00', 20],
			['05:15', 15],
			['05:30', 19],
			['05:45', 18]
		] as const) {
			await db.query(
				`INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
				     conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
				     track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
				 VALUES ('DMO/SEC-S', $1, 1, 15, 'Final', 1, 20, 20, 10, 10, 0, 20, $2, $3, 0, 0, 0, 0, $4, 15, 15, 0, now())`,
				[at(start), exited, 20 - exited, exited / 20]
			);
		}
	} finally {
		await db.end();
	}
}

async function clearHealth(db: pg.Client): Promise<void> {
	await db.query(`DELETE FROM queue_minute WHERE zone_key = 'DMO/SEC-S' AND minute_utc >= $1 AND minute_utc < $2`, [at('05:00'), at('06:00')]);
	await db.query(`DELETE FROM zone_health_bin WHERE zone_key = 'DMO/SEC-S' AND start_utc >= $1 AND start_utc < $2`, [at('05:00'), at('06:00')]);
}

// Leaves nothing behind: this run's rules (soft-deleted, as a delete through the API would) and the planted minutes.
test.afterAll(async () => {
	const db = database();
	await db.connect();
	try {
		await db.query(`UPDATE alert_rule SET deleted_on = now(), deleted_by = 'e2e-cleanup' WHERE site_code = 'DMO' AND name LIKE $1 AND deleted_on IS NULL`, [
			`%${run}`
		]);
		await db.query(`DELETE FROM queue_minute WHERE zone_key = 'DMO/A-CRW' AND minute_utc >= $1 AND minute_utc < $2`, [at('03:00'), at('03:30')]);
		await clearHealth(db);
	} finally {
		await db.end();
	}
});

/** Signs in with the second factor 20 minutes old, so deleting asks for a fresh code. */
async function signInAged(page: Page): Promise<void> {
	await ownAddress(page);
	await page.route('**/api/auth/login', async (route) => {
		const response = await route.fetch();
		if (response.status() !== 200) return route.fulfill({ response });
		const body = await response.json();
		const claims = claimsOf(body.accessToken);
		body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
		return route.fulfill({ response, json: body });
	});
	const account = accounts().webRules;
	await page.goto('/login');
	await fillSignIn(page, account, totpCode(account.totpSecret!, -1));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
}

async function openRules(page: Page): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Alert rules' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Alert rules');
	// The demo airport's seeded rules are listed for every role that reads rules.
	await expect(page.getByTestId('rule-row').first()).toBeVisible();
}

test('a supervisor creates a rule, previews its first fire time, and the saved rule fires at the same minute; disable, duplicate, change and delete', async ({
	page
}) => {
	test.skip(!developmentSigningKey(), 'needs the run key to age the token');
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	await plantMinutes();
	await earlyInStep();
	await signInAged(page);
	await openRules(page);

	await expect(page.getByTestId('add-rule')).toBeEnabled();
	await page.getByTestId('add-rule').click();
	const form = page.getByTestId('rule-form');
	await form.getByLabel('Name').fill(ruleName);
	await form.getByRole('checkbox', { name: 'A-CRW' }).check();
	await form.getByLabel('Metric').selectOption('QueueLength');
	await form.getByLabel('Condition').selectOption('GreaterThan');
	await form.getByLabel('Threshold (people)').fill('40');
	await form.getByLabel('Sustain (minutes)').fill('3');
	await form.getByLabel('Clear after (minutes)').fill('2');
	await form.getByLabel('Severity').selectOption('Critical');

	// The preview: above 40 people from 03:10, held for three minutes, so the first alert at 03:12.
	await form.getByLabel('From (UTC)').fill(fromLocal);
	await form.getByLabel('To (UTC)').fill(toLocal);
	await form.getByTestId('run-backtest').click();
	const summary = form.getByTestId('backtest-summary');
	await expect(summary).toHaveAttribute('data-count', '1');
	await expect(summary).toContainText(`Would have fired once, first at ${day} 03:12 UTC.`);
	const previewed = (await summary.getAttribute('data-first'))!;
	expect(Date.parse(previewed)).toBe(Date.parse(at('03:12')));
	await expect(form.getByTestId('backtest-alert')).toHaveCount(1);
	await expect(form.getByTestId('backtest-alert')).toContainText('A-CRW');

	await form.getByTestId('save-rule').click();
	await expect(page.getByText(/^Rule R-\d+ saved\.$/)).toBeVisible();
	const row = page.getByTestId('rule-row').filter({ hasText: ruleName });
	await expect(row).toHaveCount(1);
	await expect(row).toContainText('Queue length > 40 people for 3 min');
	await expect(row).toContainText('A-CRW');
	await expect(row).toContainText('Border shift supervisor');
	const code = (await row.getAttribute('data-code'))!;

	// The server agrees, and the saved rule judged again gives the same first minute as the preview.
	const token = (await signIn(accounts().webBorder)).accessToken;
	const saved = (
		await (
			await call('GET', `${api}?siteCode=DMO&text=${encodeURIComponent(run)}`, {
				token
			})
		).json()
	).data[0];
	expect(saved).toMatchObject({
		code,
		metric: 'QueueLength',
		comparator: 'GreaterThan',
		threshold: 40,
		sustainMinutes: 3,
		severity: 'Critical',
		zones: ['A-CRW']
	});
	const again = await call('POST', `${api}/backtest`, {
		token,
		data: { rule: saved, fromUtc: at('02:50'), toUtc: at('03:40') }
	});
	expect(again.status(), await again.text()).toBe(200);
	expect(Date.parse((await again.json()).firstRaisedUtc)).toBe(Date.parse(previewed));

	// Disable and enable again.
	await row.getByTestId('toggle-rule').click();
	await expect(row).toHaveAttribute('data-enabled', 'false');
	expect((await (await call('GET', `${api}/${saved.id}`, { token })).json()).enabled).toBe(false);
	await row.getByTestId('toggle-rule').click();
	await expect(row).toHaveAttribute('data-enabled', 'true');

	// Duplicate starts a new rule from this one.
	await row.getByTestId('duplicate-rule').click();
	await expect(form.getByLabel('Name')).toHaveValue(`Copy of ${ruleName}`);
	await expect(form.getByLabel('Threshold (people)')).toHaveValue('40');
	await form.getByRole('button', { name: 'Cancel' }).click();
	await expect(form).toHaveCount(0);

	// Change the threshold above anything stored: the preview says it would not have fired.
	await row.getByTestId('edit-rule').click();
	await expect(form.getByRole('heading', { level: 2 })).toHaveText(`Edit ${code}`);
	await form.getByLabel('Threshold (people)').fill('60');
	await form.getByLabel('From (UTC)').fill(fromLocal);
	await form.getByLabel('To (UTC)').fill(toLocal);
	await form.getByTestId('run-backtest').click();
	await expect(form.getByTestId('backtest-summary')).toHaveText('Would not have fired in this range.');
	await form.getByTestId('save-rule').click();
	await expect(row).toContainText('Queue length > 60 people for 3 min');

	// Deleting is critical: the second factor is 20 minutes old, so the dialog asks for a fresh code.
	await row.getByTestId('delete-rule').click();
	await row.getByTestId('confirm-delete-rule').click();
	const dialog = page.getByTestId('step-up-dialog');
	await expect(dialog).toBeVisible();
	await page.locator('#step-up-code').fill(totpCode(accounts().webRules.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();
	await expect(page.getByText(`Rule ${code} deleted.`)).toBeVisible();
	await expect(page.getByTestId('rule-row').filter({ hasText: ruleName })).toHaveCount(0);
	expect((await call('GET', `${api}/${saved.id}`, { token })).status()).toBe(404);
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('a supervisor creates a track completion rule on a queue zone, previews it on the stored bins, and the saved rule fires at the same minute', async ({
	page
}) => {
	// ARV-114b: the health checks are typed metrics on the form (no expression); they watch queue zones only.
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	await plantHealth();
	await signInThroughUi(page, accounts().webBorder);
	await openRules(page);

	await page.getByTestId('add-rule').click();
	const form = page.getByTestId('rule-form');
	const name = `E2E completion ${run}`;
	await form.getByLabel('Name').fill(name);
	await expect(form.getByRole('checkbox', { name: 'SEC-OV' }), 'an overflow band is offered for the other metrics').toHaveCount(1);
	await form.getByLabel('Metric').selectOption('TrackCompletion');
	await expect(form.getByTestId('health-check-hint')).toContainText('Below 90 percent marks the bin Degraded');
	await expect(form.getByRole('checkbox', { name: 'SEC-OV' }), 'a health check watches queue zones only').toHaveCount(0);
	await expect(form.getByRole('checkbox', { name: 'A-OV' })).toHaveCount(0);
	await form.getByRole('checkbox', { name: 'SEC-S' }).check();
	await form.getByLabel('Condition').selectOption('LessThan');
	await form.getByLabel('Threshold (percent)').fill('90');
	await form.getByLabel('Sustain (minutes)').fill('1');
	await form.getByLabel('Clear after (minutes)').fill('1');

	// The preview: the 05:15 bin (75 percent) governs from 05:29, the minute its end falls in, until the 05:30 bin (95 percent) does at 05:44.
	await form.getByLabel('From (UTC)').fill(`${day}T04:50`);
	await form.getByLabel('To (UTC)').fill(`${day}T06:10`);
	await form.getByTestId('run-backtest').click();
	const summary = form.getByTestId('backtest-summary');
	await expect(summary).toHaveAttribute('data-count', '1');
	await expect(summary).toContainText(`Would have fired once, first at ${day} 05:29 UTC.`);
	const alert = form.getByTestId('backtest-alert');
	await expect(alert).toHaveCount(1);
	await expect(alert).toContainText('SEC-S');
	await expect(alert).toContainText(`${day} 05:44 UTC`);
	await expect(alert.locator('td').last()).toHaveText('75');
	const previewed = (await summary.getAttribute('data-first'))!;

	await form.getByTestId('save-rule').click();
	await expect(page.getByText(/^Rule R-\d+ saved\.$/)).toBeVisible();
	const row = page.getByTestId('rule-row').filter({ hasText: name });
	await expect(row).toHaveCount(1);
	await expect(row).toContainText('Track completion < 90 percent for 1 min');
	await expect(row).toContainText('SEC-S');

	// The server agrees: the typed rule as saved, and its own backtest fires at the previewed minute.
	const token = (await signIn(accounts().webBorder)).accessToken;
	const found = await call('GET', `${api}?siteCode=DMO&metric=TrackCompletion&text=${encodeURIComponent(run)}`, { token });
	expect(found.status(), 'the saved rule is listed by its metric').toBe(200);
	const saved = (await found.json()).data.find((r: { name: string }) => r.name === name);
	expect(saved).toMatchObject({ metric: 'TrackCompletion', comparator: 'LessThan', threshold: 90, zones: ['SEC-S'], sustainMinutes: 1, minQueueLength: null });
	const again = await call('POST', `${api}/backtest`, { token, data: { rule: saved, fromUtc: at('04:50'), toUtc: at('06:10') } });
	expect(again.status(), again.status() === 200 ? 'the saved rule backtested' : await again.text()).toBe(200);
	expect(Date.parse((await again.json()).firstRaisedUtc)).toBe(Date.parse(previewed));

	// An overflow zone sent anyway is refused by the server, whatever the form shows.
	const band = await call('POST', api, { token, data: { ...saved, name: `E2E band ${run}`, zones: ['SEC-N', 'SEC-OV'] } });
	expect(band.status(), 'a health check on an overflow band').toBe(400);
	await guards.expectClean();
});

test('rule names are shown as text: script payloads run nothing (CWE-79)', async ({ page }) => {
	const guards = await guardPage(page);
	const token = (await signIn(accounts().webBorder)).accessToken;
	// Created disabled, so they never raise anything; removed after the suite.
	for (const payload of [xssPayloads[1], xssPayloads[5]]) {
		const response = await call('POST', api, {
			token,
			data: {
				siteCode: 'DMO',
				name: `${payload} ${run}`,
				zones: ['A-CRW'],
				metric: 'Nowcast',
				comparator: 'GreaterThan',
				threshold: 30,
				sustainMinutes: 2,
				clearAfterMinutes: 2,
				severity: 'Info',
				ownerRole: 'BorderShiftSupervisor',
				notifyByEmail: false,
				enabled: false
			}
		});
		expect(response.status(), await response.text()).toBe(201);
	}
	await signInThroughUi(page, accounts().webBorder);
	await openRules(page);
	for (const payload of [xssPayloads[1], xssPayloads[5]]) {
		await expect(page.getByTestId('rule-row').filter({ hasText: `${payload} ${run}` })).toHaveCount(1);
	}
	await expect(page.locator('[data-testid="rules-table"] img, [data-testid="rules-table"] svg[onload], iframe')).toHaveCount(0);

	// The names in the form too: editing, duplicating ("Copy of" the name) and a preview of the payload-named rule.
	const row = page.getByTestId('rule-row').filter({ hasText: `${xssPayloads[5]} ${run}` });
	await row.getByTestId('edit-rule').click();
	const form = page.getByTestId('rule-form');
	await expect(form.getByLabel('Name')).toHaveValue(`${xssPayloads[5]} ${run}`);
	await form.getByTestId('run-backtest').click();
	await expect(form.getByTestId('backtest-summary').or(form.getByTestId('backtest-problem'))).toBeVisible();
	await form.getByRole('button', { name: 'Cancel' }).click();
	await page
		.getByTestId('rule-row')
		.filter({ hasText: `${xssPayloads[1]} ${run}` })
		.getByTestId('duplicate-rule')
		.click();
	await expect(form.getByLabel('Name')).toHaveValue(`Copy of ${xssPayloads[1]} ${run}`);
	const html = await form.innerHTML();
	// The form's own icons are SVG; a payload would bring an image, a frame, a script or an event handler.
	for (const marker of ['<img', '<iframe', '<script', 'onerror=', 'onload=']) expect(html, marker).not.toContain(marker);
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror]')).toHaveCount(0);
	await guards.expectClean();
});

test('a handler station manager reads the rules only; the server refuses a change; Arabic mirrors the screen', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webHandler);
	await openRules(page);
	await expect(page.getByTestId('rules-table')).toBeVisible();
	await expect(page.getByTestId('rule-row').first()).toBeVisible();
	for (const action of ['add-rule', 'edit-rule', 'duplicate-rule', 'delete-rule', 'toggle-rule']) {
		await expect(page.getByTestId(action), action).toHaveCount(0);
	}
	const token = (await signIn(accounts().webHandler)).accessToken;
	const first = (await (await call('GET', `${api}?siteCode=DMO`, { token })).json()).data[0];
	expect(
		(
			await call('PUT', `${api}/${first.id}`, {
				token,
				data: { ...first, enabled: !first.enabled }
			})
		).status()
	).toBe(403);
	expect(
		(
			await call('POST', `${api}/backtest`, {
				token,
				data: { rule: first, fromUtc: at('02:50'), toUtc: at('03:40') }
			})
		).status()
	).toBe(403);

	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('قواعد التنبيه');
	allowStatuses(guards, 404);
	await guards.expectClean();
});

/** The open desks below recommendation case (ARV-181b2), planted on the citizens lane yesterday from 04:00 UTC. */
const desksStart = at('04:00');
const desksFrom = at('03:55');
const desksTo = at('04:25');
/** The 403 the server answers a caller without ArrivalWaveLanes.View for either border metric (one generic text since ARV-187). */
const borderOnlyText =
	'Not allowed: Open desks below recommendation and predicted nowcast are border data: only a caller with ArrivalWaveLanes.View creates, changes, imports or backtests a rule on them.';

/** The values of a select's options, in order. */
async function optionValues(page: Page, select: string): Promise<string[]> {
	return page.locator(`${select} option`).evaluateAll((o) => o.map((x) => (x as HTMLOptionElement).value));
}

/** A border rule as the API takes it, for the suites' own checks (the form builds its own). */
function borderRule(name: string, overrides: Record<string, unknown> = {}) {
	return {
		siteCode: 'DMO',
		name,
		zones: ['A-CIT'],
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
		enabled: false,
		leadMinutes: null,
		...overrides
	};
}

test.describe('open desks below recommendation (ARV-181b2)', () => {
	// Ariva.Api.Stream does not run in the E2E run, so the staffing job's own rows have no recommendation: the suite plants
	// interval 0 of the citizens lane (A-CIT; the API suite plants A-CRW) yesterday as the job writes it, and removes it after.
	test.beforeAll(async () => {
		const db = staffingDatabase();
		await db.connect();
		try {
			await plantDesksShortCase(db, 'DMO/A-CIT', 'CIT', new Date(desksStart), new Date(desksFrom), new Date(desksTo));
		} finally {
			await db.end();
		}
	});

	test.afterAll(async () => {
		const db = staffingDatabase();
		await db.connect();
		try {
			await db.query('DELETE FROM lane_staffing WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc <= $3', ['DMO/A-CIT', desksFrom, desksTo]);
		} finally {
			await db.end();
		}
	});

	test('a border shift supervisor creates a rule on a lane, previews its first fire minute, and no rule is marked not evaluated', async ({ page }) => {
		test.setTimeout(90_000);
		const guards = await guardPage(page);
		await signInThroughUi(page, accounts().webBorder);
		await openRules(page);
		// Every metric is judged since ARV-181b2, so no rule is marked "Not evaluated yet" (R-002 since ARV-115).
		await expect(page.locator('[data-testid="rule-row"][data-code="R-002"]')).toBeVisible();
		await expect(page.getByTestId('not-evaluated')).toHaveCount(0);

		await page.getByTestId('add-rule').click();
		const form = page.getByTestId('rule-form');
		const name = `E2E desks short ${run}`;
		await form.getByLabel('Name').fill(name);
		const metrics = await optionValues(page, '#rule-metric');
		expect(metrics).toEqual(expect.arrayContaining(['DesksBelowPlan', 'Nowcast', 'OverflowOccupied', 'ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange']));
		await expect(form.locator('#rule-metric option[value="DesksBelowPlan"]')).toHaveText('Open desks below recommendation');
		await expect(form.getByRole('checkbox', { name: 'A-OV', exact: true }), 'an overflow band is offered for the nowcast').toHaveCount(1);
		await expect(form.getByTestId('border-hint')).toHaveCount(0);

		// A rule that fires below, moved to the border metric, fires above; the border choices only.
		await form.getByLabel('Condition').selectOption('LessThan');
		await form.getByLabel('Metric').selectOption('DesksBelowPlan');
		await expect(form.getByTestId('border-hint')).toHaveText(
			"Border data: how many desks each arrivals lane is short of the current quarter hour's indicative recommendation. The rule and its alerts reach the Border shift supervisor and the system administrator only."
		);
		await expect(form.getByLabel('Condition')).toHaveValue('GreaterThan');
		expect(await optionValues(page, '#rule-comparator')).toEqual(['GreaterThan', 'GreaterOrEqual']);
		expect(await optionValues(page, '#rule-owner'), 'the supervisor’s own border role, never every role').toEqual(['BorderShiftSupervisor']);
		expect(await optionValues(page, '#rule-escalate-to')).toEqual(['', 'BorderShiftSupervisor']);
		await expect(form.getByRole('checkbox', { name: 'A-OV', exact: true }), 'queue zones only').toHaveCount(0);
		await expect(form.getByRole('checkbox', { name: 'SEC-OV', exact: true })).toHaveCount(0);

		await form.getByRole('checkbox', { name: 'A-CIT', exact: true }).check();
		await form.getByLabel('Condition').selectOption('GreaterOrEqual');
		await form.getByLabel('Threshold (desks)').fill('2');
		await form.getByLabel('Sustain (minutes)').fill('3');
		await form.getByLabel('Clear after (minutes)').fill('2');

		// Held at 04:05, 04:07 and 04:09 (no row at 04:06, a degraded open count at 04:08): raised at 04:09 with 3 desks
		// short, cleared at 04:15 (04:14 and 04:15 at 0; the gap at 04:10 and the unknown count at 04:11 are skipped).
		await form.getByLabel('From (UTC)').fill(`${day}T03:55`);
		await form.getByLabel('To (UTC)').fill(`${day}T04:25`);
		await form.getByTestId('run-backtest').click();
		const summary = form.getByTestId('backtest-summary');
		await expect(summary).toHaveAttribute('data-count', '1');
		await expect(summary).toContainText(`Would have fired once, first at ${day} 04:09 UTC.`);
		const alert = form.getByTestId('backtest-alert');
		await expect(alert).toHaveCount(1);
		await expect(alert).toContainText('A-CIT');
		await expect(alert).toContainText(`${day} 04:15 UTC`);
		await expect(alert.locator('td').last()).toHaveText('3');
		const previewed = (await summary.getAttribute('data-first'))!;
		expect(Date.parse(previewed)).toBe(Date.parse(at('04:09')));

		const axe = await new AxeBuilder({ page }).include('[data-testid="rule-form"]').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
		expect(axe.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`), 'axe violations in the form').toEqual([]);

		await form.getByTestId('save-rule').click();
		await expect(page.getByText(/^Rule R-\d+ saved\.$/)).toBeVisible();
		const row = page.getByTestId('rule-row').filter({ hasText: name });
		await expect(row).toHaveCount(1);
		await expect(row).toContainText('Open desks below recommendation ≥ 2 desks for 3 min');
		await expect(row).toContainText('A-CIT');
		await expect(row).toContainText('Border shift supervisor');
		await expect(row.getByTestId('not-evaluated')).toHaveCount(0);

		// The server agrees, and the saved rule's own backtest fires at the previewed minute.
		const token = (await signIn(accounts().webBorder)).accessToken;
		const saved = (await (await call('GET', `${api}?siteCode=DMO&metric=DesksBelowPlan&text=${encodeURIComponent(run)}`, { token })).json()).data.find(
			(r: { name: string }) => r.name === name
		);
		expect(saved).toMatchObject({ metric: 'DesksBelowPlan', comparator: 'GreaterOrEqual', threshold: 2, zones: ['A-CIT'], ownerRole: 'BorderShiftSupervisor', escalateToRole: null });
		const again = await call('POST', `${api}/backtest`, { token, data: { rule: saved, fromUtc: desksFrom, toUtc: desksTo } });
		expect(again.status(), again.status() === 200 ? 'the saved rule backtested' : await again.text()).toBe(200);
		expect(Date.parse((await again.json()).firstRaisedUtc)).toBe(Date.parse(previewed));

		// Editing starts on the border choices; Arabic mirrors the form and translates the hint.
		await row.getByTestId('edit-rule').click();
		await expect(form.getByLabel('Metric')).toHaveValue('DesksBelowPlan');
		await expect(form.getByLabel('Owner role')).toHaveValue('BorderShiftSupervisor');
		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(form.locator('#rule-metric option[value="DesksBelowPlan"]')).toHaveText('مكاتب مفتوحة أقل من التوصية');
		await expect(form.getByTestId('border-hint')).toContainText('بيانات حدودية');
		await guards.expectClean();
	});

	test('an administrator gives a border rule to a border role only, never to every role', async ({ page }) => {
		const guards = await guardPage(page);
		await signInThroughUi(page, accounts().webAdmin);
		await openRules(page);
		await page.getByTestId('add-rule').click();
		const form = page.getByTestId('rule-form');
		expect(await optionValues(page, '#rule-owner'), 'the nowcast may belong to every role').toContain('');
		await form.getByLabel('Metric').selectOption('DesksBelowPlan');
		expect(await optionValues(page, '#rule-owner')).toEqual(['BorderShiftSupervisor', 'SystemAdministrator']);
		expect(await optionValues(page, '#rule-escalate-to')).toEqual(['', 'BorderShiftSupervisor', 'SystemAdministrator']);
		await form.getByLabel('Owner role').selectOption('SystemAdministrator');
		// Back to the nowcast: the administrator is not an owner the other metrics offer, so the first operational role is.
		await form.getByLabel('Metric').selectOption('Nowcast');
		await expect(form.getByLabel('Owner role')).toHaveValue('BorderShiftSupervisor');
		expect(await optionValues(page, '#rule-owner')).toEqual(['', 'BorderShiftSupervisor', 'TerminalDutyManager', 'HandlerStationManager']);
		await expect(form.getByTestId('border-hint')).toHaveCount(0);
		await guards.expectClean();
	});

	test('a terminal duty manager is not offered the metric, does not find border rules, and the server refuses a crafted request (403)', async ({ page }) => {
		test.setTimeout(90_000);
		const guards = await guardPage(page);
		// A border rule with a script payload for a name: never listed for the duty manager, so nothing of it reaches the page.
		const borderToken = (await signIn(accounts().webBorder)).accessToken;
		const hiddenName = `${xssPayloads[1]} desks ${run}`;
		const hidden = await call('POST', api, { token: borderToken, data: borderRule(hiddenName) });
		expect(hidden.status(), await hidden.text()).toBe(201);

		await signInThroughUi(page, accounts().web);
		await openRules(page);
		await expect(page.getByTestId('rule-row').filter({ hasText: `desks ${run}` })).toHaveCount(0);
		await expect(page.getByTestId('rule-row').filter({ hasText: `E2E desks short ${run}` }), 'the supervisor’s rule of the first test').toHaveCount(0);
		await expect(page.getByTestId('rule-row').filter({ hasText: 'Open desks below recommendation' })).toHaveCount(0);
		await expect(page.getByTestId('not-evaluated')).toHaveCount(0);
		await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror]')).toHaveCount(0);

		await page.getByTestId('add-rule').click();
		const form = page.getByTestId('rule-form');
		const metrics = await optionValues(page, '#rule-metric');
		expect(metrics, 'border data is not offered').not.toContain('DesksBelowPlan');
		expect(metrics, 'the predicted nowcast is border data too (ARV-187)').not.toContain('PredictedNowcast');
		expect(metrics).toEqual(expect.arrayContaining(['Nowcast', 'OverflowOccupied', 'ConservationResidual', 'TrackCompletion', 'OccupancyOutOfRange']));

		// Hiding the metric is a convenience: a request changed on its way to the server is refused there, and the form
		// shows the refusal as it shows any other (create and preview alike).
		await page.route('**/api/v1/admin/alert-rules**', async (route) => {
			const request = route.request();
			if (request.method() !== 'POST') return route.continue();
			const body = request.postDataJSON();
			const border = { metric: 'DesksBelowPlan', comparator: 'GreaterOrEqual', minQueueLength: null, ownerRole: 'BorderShiftSupervisor' };
			const changed = body.rule ? { ...body, rule: { ...body.rule, ...border } } : { ...body, ...border };
			return route.continue({ postData: JSON.stringify(changed) });
		});
		await form.getByLabel('Name').fill(`E2E desks probe ${run}`);
		await form.getByRole('checkbox', { name: 'A-CIT', exact: true }).check();
		await form.getByTestId('run-backtest').click();
		await expect(form.getByTestId('backtest-problem')).toHaveText(borderOnlyText);
		await form.getByTestId('save-rule').click();
		await expect(form.getByTestId('rule-problems')).toHaveText(borderOnlyText);
		await page.unrouteAll({ behavior: 'wait' });

		// Nothing was created, and the border rule stays hidden from the duty manager (404) and listed for the supervisor.
		const terminalToken = (await signIn(accounts().web)).accessToken;
		const probe = await call('GET', `${api}?siteCode=DMO&text=${encodeURIComponent(`desks probe ${run}`)}`, { token: borderToken });
		expect((await probe.json()).totalCount).toBe(0);
		const id = (await hidden.json()).id;
		expect((await call('GET', `${api}/${id}`, { token: terminalToken })).status()).toBe(404);
		expect((await call('GET', `${api}/${id}`, { token: borderToken })).status()).toBe(200);
		allowStatuses(guards, 403);
		await guards.expectClean();
	});
});

/** The 400 the server answers a border rule whose owner or escalation is not a border role (one generic text since ARV-187). */
const borderRolesText =
	'Not valid: A rule on open desks below recommendation or predicted nowcast belongs to a border role: its owner is the Border shift supervisor or the system administrator (never empty), and it escalates to one of them or to no role.';
const predictedHint =
	"Border data: the wait expected at the lead time, from the queue now and its lane's share of the arrival wave. The rule and its alerts reach the Border shift supervisor and the system administrator only.";

/** A predicted nowcast rule as the API takes it, owned by the Border shift supervisor (the suites' own setup). */
function predictedRule(name: string, overrides: Record<string, unknown> = {}) {
	return borderRule(name, { zones: ['A-VIS'], metric: 'PredictedNowcast', comparator: 'GreaterThan', threshold: 20, leadMinutes: 30, ...overrides });
}

/**
 * Turns a stored rule into one an upgrade to ARV-187 finds: a predicted nowcast rule owned by the terminal duty manager or
 * escalated to the handler station manager, which the rule checks have refused since (the table's own checks allow it).
 */
async function makeLegacy(id: string, change: 'owner' | 'escalation'): Promise<void> {
	const db = database();
	await db.connect();
	try {
		const updated = await db.query(
			change === 'owner'
				? `UPDATE alert_rule SET owner_role = 'TerminalDutyManager' WHERE id = $1 AND deleted_on IS NULL`
				: `UPDATE alert_rule SET escalate_to_role = 'HandlerStationManager' WHERE id = $1 AND deleted_on IS NULL`,
			[id]
		);
		expect(updated.rowCount, `the rule ${id} changed in the database`).toBe(1);
	} finally {
		await db.end();
	}
}

test.describe('predicted nowcast is border data (ARV-187)', () => {
	// The rules an upgrade finds, made in the administrator's test and looked for in the duty manager's (serial).
	const legacy = {
		owner: `E2E predicted legacy owner ${run}`,
		escalation: `E2E predicted legacy escalation ${run}`,
		disabled: `E2E predicted legacy disabled ${run}`
	};
	const legacyIds: Partial<Record<keyof typeof legacy, string>> = {};

	test('a border shift supervisor creates a predicted nowcast rule with any condition and an overflow zone, and previews it', async ({ page }) => {
		test.setTimeout(90_000);
		const guards = await guardPage(page);
		await signInThroughUi(page, accounts().webBorder);
		await openRules(page);
		await expect(page.getByTestId('not-evaluated')).toHaveCount(0);

		await page.getByTestId('add-rule').click();
		const form = page.getByTestId('rule-form');
		const name = `E2E predicted ${run}`;
		await form.getByLabel('Name').fill(name);
		await expect(form.locator('#rule-metric option[value="PredictedNowcast"]')).toHaveText('Predicted nowcast');

		// Only the roles are the border ones: a rule that fires below stays below, and the overflow zones stay offered.
		await form.getByLabel('Condition').selectOption('LessThan');
		await form.getByLabel('Metric').selectOption('PredictedNowcast');
		await expect(form.getByTestId('border-hint')).toHaveText(predictedHint);
		await expect(form.getByLabel('Condition')).toHaveValue('LessThan');
		expect(await optionValues(page, '#rule-comparator'), 'all four conditions').toEqual(['GreaterThan', 'GreaterOrEqual', 'LessThan', 'LessOrEqual']);
		expect(await optionValues(page, '#rule-owner'), 'the supervisor’s own border role, never every role').toEqual(['BorderShiftSupervisor']);
		expect(await optionValues(page, '#rule-escalate-to')).toEqual(['', 'BorderShiftSupervisor']);
		await expect(form.getByLabel('Lead time (minutes)')).toHaveValue('30');
		await expect(form.getByRole('checkbox', { name: 'A-OV', exact: true }), 'an overflow zone takes its queue’s arrivals').toHaveCount(1);
		await form.getByRole('checkbox', { name: 'A-VIS', exact: true }).check();
		await form.getByRole('checkbox', { name: 'A-OV', exact: true }).check();
		await form.getByLabel('Threshold (minutes)').fill('5');

		// The preview answers the supervisor (whatever the stored minutes hold), never with a refusal.
		await form.getByLabel('From (UTC)').fill(fromLocal);
		await form.getByLabel('To (UTC)').fill(toLocal);
		await form.getByTestId('run-backtest').click();
		await expect(form.getByTestId('backtest-summary')).toBeVisible();
		await expect(form.getByTestId('backtest-problem')).toHaveCount(0);

		const axe = await new AxeBuilder({ page }).include('[data-testid="rule-form"]').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
		expect(axe.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`), 'axe violations in the form').toEqual([]);

		await form.getByTestId('save-rule').click();
		await expect(page.getByText(/^Rule R-\d+ saved\.$/)).toBeVisible();
		const row = page.getByTestId('rule-row').filter({ hasText: name });
		await expect(row).toHaveCount(1);
		await expect(row).toContainText('Predicted nowcast < 5 minutes for 3 min');
		await expect(row).toContainText('A-VIS');
		await expect(row).toContainText('A-OV');
		await expect(row).toContainText('Border shift supervisor');
		await expect(row.getByTestId('not-evaluated-border'), 'a rule meeting the border checks is evaluated').toHaveCount(0);

		// The server agrees.
		const token = (await signIn(accounts().webBorder)).accessToken;
		const found = await call('GET', `${api}?siteCode=DMO&metric=PredictedNowcast&text=${encodeURIComponent(run)}`, { token });
		expect(found.status(), 'the saved rule is listed by its metric').toBe(200);
		const saved = (await found.json()).data.find((r: { name: string }) => r.name === name);
		expect(saved).toMatchObject({ metric: 'PredictedNowcast', comparator: 'LessThan', threshold: 5, leadMinutes: 30, ownerRole: 'BorderShiftSupervisor', escalateToRole: null });
		expect([...saved.zones].sort()).toEqual(['A-OV', 'A-VIS']);

		// Editing opens on the rule as saved; Arabic mirrors the form and translates the name and the hint.
		await row.getByTestId('edit-rule').click();
		await expect(form.getByLabel('Metric')).toHaveValue('PredictedNowcast');
		await expect(form.getByLabel('Condition')).toHaveValue('LessThan');
		await expect(form.getByLabel('Owner role')).toHaveValue('BorderShiftSupervisor');
		await expect(form.getByRole('checkbox', { name: 'A-OV', exact: true })).toBeChecked();
		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(form.locator('#rule-metric option[value="PredictedNowcast"]')).toHaveText('الانتظار المتوقع');
		await expect(form.getByTestId('border-hint')).toContainText('بيانات حدودية');
		await expect(form.getByTestId('border-hint')).toContainText('موجة الوصول');
		await guards.expectClean();
	});

	test('an administrator sees the predicted nowcast rules of airport-side roles marked not evaluated, and saving one with a border owner repairs it', async ({
		page
	}) => {
		test.setTimeout(90_000);
		const guards = await guardPage(page);
		// Created by the supervisor, then changed in the database as rules from before ARV-187 are stored.
		const borderToken = (await signIn(accounts().webBorder)).accessToken;
		for (const key of Object.keys(legacy) as (keyof typeof legacy)[]) {
			const overrides =
				key === 'escalation'
					? { enabled: true, escalateAfterMinutes: 10, escalateToRole: 'BorderShiftSupervisor' }
					: { enabled: key === 'owner' };
			const response = await call('POST', api, { token: borderToken, data: predictedRule(legacy[key], overrides) });
			expect(response.status(), await response.text()).toBe(201);
			legacyIds[key] = (await response.json()).id;
		}
		await makeLegacy(legacyIds.owner!, 'owner');
		await makeLegacy(legacyIds.escalation!, 'escalation');
		await makeLegacy(legacyIds.disabled!, 'owner');

		await signInThroughUi(page, accounts().webAdmin);
		await openRules(page);
		const row = (name: string) => page.getByTestId('rule-row').filter({ hasText: name });
		await expect(row(legacy.owner)).toHaveCount(1);
		await expect(row(legacy.owner)).toContainText('Terminal duty manager');
		await expect(row(legacy.owner).getByTestId('not-evaluated-border')).toHaveText('Not evaluated: fix its border settings');
		await expect(row(legacy.escalation).getByTestId('not-evaluated-border'), 'an escalation to an airport-side role').toHaveCount(1);
		await expect(row(legacy.disabled)).toHaveAttribute('data-enabled', 'false');
		await expect(row(legacy.disabled).getByTestId('not-evaluated-border'), 'a disabled rule is not judged anyway').toHaveCount(0);
		await expect(page.getByTestId('not-evaluated'), 'the other badge is for metrics without inputs').toHaveCount(0);

		// The server refuses to keep the airport-side role: disabling the escalated rule fails with the border roles text.
		await row(legacy.escalation).getByTestId('toggle-rule').click();
		await expect(page.getByText(borderRolesText)).toBeVisible();
		await expect(row(legacy.escalation)).toHaveAttribute('data-enabled', 'true');

		// Editing opens on the border choices (the duty manager's place taken by the first border role); saving repairs it.
		const code = (await row(legacy.owner).getAttribute('data-code'))!;
		await row(legacy.owner).getByTestId('edit-rule').click();
		const form = page.getByTestId('rule-form');
		await expect(form.getByLabel('Metric')).toHaveValue('PredictedNowcast');
		await expect(form.getByLabel('Owner role')).toHaveValue('BorderShiftSupervisor');
		expect(await optionValues(page, '#rule-owner'), 'border roles only, never every role').toEqual(['BorderShiftSupervisor', 'SystemAdministrator']);
		await expect(form.getByTestId('border-hint')).toHaveText(predictedHint);
		await form.getByTestId('save-rule').click();
		await expect(page.getByText(`Rule ${code} saved.`)).toBeVisible();
		await expect(row(legacy.owner)).toContainText('Border shift supervisor');
		await expect(row(legacy.owner).getByTestId('not-evaluated-border')).toHaveCount(0);
		await expect(row(legacy.escalation).getByTestId('not-evaluated-border'), 'the other rule waits for its own repair').toHaveCount(1);
		const repaired = await call('GET', `${api}/${legacyIds.owner}`, { token: borderToken });
		expect(repaired.status()).toBe(200);
		expect(await repaired.json()).toMatchObject({ metric: 'PredictedNowcast', ownerRole: 'BorderShiftSupervisor', enabled: true });
		allowStatuses(guards, 400);
		await guards.expectClean();
	});

	test('a terminal duty manager is not offered the predicted nowcast, finds none of its rules, and the server refuses a crafted request (403)', async ({ page }) => {
		test.setTimeout(90_000);
		const guards = await guardPage(page);
		// A predicted nowcast rule with a script payload for a name: never listed for the duty manager, so nothing of it reaches the page.
		const borderToken = (await signIn(accounts().webBorder)).accessToken;
		const hiddenName = `${xssPayloads[5]} predicted ${run}`;
		const hidden = await call('POST', api, { token: borderToken, data: predictedRule(hiddenName) });
		expect(hidden.status(), await hidden.text()).toBe(201);

		await signInThroughUi(page, accounts().web);
		await openRules(page);
		// The condition column starts with the metric's name; a rule's own name, which could hold the same words, is in another column.
		const conditions = page.getByTestId('rule-row').locator('td:nth-child(3)');
		await expect(conditions.first()).toBeVisible();
		await expect(conditions.filter({ hasText: /^Predicted nowcast / }), 'no predicted nowcast rule, whoever owns it').toHaveCount(0);
		await expect(page.getByTestId('rule-row').filter({ hasText: hiddenName })).toHaveCount(0);
		await expect(page.getByTestId('rule-row').filter({ hasText: legacy.disabled }), 'its own role’s rule from before ARV-187 too').toHaveCount(0);
		await expect(page.getByTestId('not-evaluated')).toHaveCount(0);
		await expect(page.getByTestId('not-evaluated-border')).toHaveCount(0);
		await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror]')).toHaveCount(0);

		await page.getByTestId('add-rule').click();
		const form = page.getByTestId('rule-form');
		const metrics = await optionValues(page, '#rule-metric');
		expect(metrics, 'border data is not offered').not.toContain('PredictedNowcast');
		expect(metrics).not.toContain('DesksBelowPlan');
		expect(metrics).toEqual(expect.arrayContaining(['Nowcast', 'BinP90', 'QueueLength']));

		// Hiding the metric is a convenience: a request changed on its way to the server is refused there, owned by the duty
		// manager's own role as the form sends it, and the form shows the refusal (create and preview alike).
		await page.route('**/api/v1/admin/alert-rules**', async (route) => {
			const request = route.request();
			if (request.method() !== 'POST') return route.continue();
			const body = request.postDataJSON();
			const predicted = { metric: 'PredictedNowcast', leadMinutes: 30, minQueueLength: null };
			const changed = body.rule ? { ...body, rule: { ...body.rule, ...predicted } } : { ...body, ...predicted };
			return route.continue({ postData: JSON.stringify(changed) });
		});
		await form.getByLabel('Name').fill(`E2E predicted probe ${run}`);
		await form.getByRole('checkbox', { name: 'A-VIS', exact: true }).check();
		await expect(form.getByLabel('Owner role')).toHaveValue('TerminalDutyManager');
		await form.getByLabel('From (UTC)').fill(fromLocal);
		await form.getByLabel('To (UTC)').fill(toLocal);
		await form.getByTestId('run-backtest').click();
		await expect(form.getByTestId('backtest-problem')).toHaveText(borderOnlyText);
		await form.getByTestId('save-rule').click();
		await expect(form.getByTestId('rule-problems')).toHaveText(borderOnlyText);
		await page.unrouteAll({ behavior: 'wait' });

		// Nothing was created; the duty manager finds no predicted nowcast rule (its own role's included) and reads none.
		const probe = await call('GET', `${api}?siteCode=DMO&text=${encodeURIComponent(`predicted probe ${run}`)}`, { token: borderToken });
		expect((await probe.json()).totalCount).toBe(0);
		const terminalToken = (await signIn(accounts().web)).accessToken;
		const listed = await call('GET', `${api}?siteCode=DMO&metric=PredictedNowcast`, { token: terminalToken });
		expect(listed.status()).toBe(200);
		expect((await listed.json()).totalCount, 'no predicted nowcast rule for the duty manager').toBe(0);
		const id = (await hidden.json()).id;
		expect((await call('GET', `${api}/${id}`, { token: terminalToken })).status()).toBe(404);
		expect((await call('GET', `${api}/${legacyIds.disabled}`, { token: terminalToken })).status(), 'its own role’s rule from before ARV-187').toBe(404);
		expect((await call('GET', `${api}/${id}`, { token: borderToken })).status()).toBe(200);
		allowStatuses(guards, 403);
		await guards.expectClean();
	});
});
