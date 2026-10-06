import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import pg from 'pg';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, totpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
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

// Leaves nothing behind: this run's rules (soft-deleted, as a delete through the API would) and the planted minutes.
test.afterAll(async () => {
	const db = database();
	await db.connect();
	try {
		await db.query(`UPDATE alert_rule SET deleted_on = now(), deleted_by = 'e2e-cleanup' WHERE site_code = 'DMO' AND name LIKE $1 AND deleted_on IS NULL`, [
			`%${run}`
		]);
		await db.query(`DELETE FROM queue_minute WHERE zone_key = 'DMO/A-CRW' AND minute_utc >= $1 AND minute_utc < $2`, [at('03:00'), at('03:30')]);
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

test('a new rule cannot use a metric the evaluation does not judge yet, and R-002 is evaluated', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await openRules(page);
	// ARV-115: the stream stores each overflow band's occupancy per minute, so the demo seed's R-002 is judged.
	await expect(page.locator('[data-testid="rule-row"][data-code="R-002"]')).toBeVisible();
	await expect(page.locator('[data-testid="rule-row"][data-code="R-002"]').getByTestId('not-evaluated')).toHaveCount(0);
	await expect(page.locator('[data-testid="rule-row"][data-code="R-001"]').getByTestId('not-evaluated')).toHaveCount(0);
	await page.getByTestId('add-rule').click();
	const metrics = await page.locator('#rule-metric option').evaluateAll((o) => o.map((x) => (x as HTMLOptionElement).value));
	expect(metrics).toContain('OverflowOccupied');
	expect(metrics).not.toContain('DesksBelowPlan');
	expect(metrics).toContain('Nowcast');
	// The server refuses a rule on a metric it does not judge yet (there is no staffing plan).
	const token = (await signIn(accounts().webBorder)).accessToken;
	const first = (await (await call('GET', `${api}?siteCode=DMO`, { token })).json()).data[0];
	const refused = await call('POST', api, { token, data: { ...first, name: 'Desks probe', metric: 'DesksBelowPlan', threshold: 1 } });
	expect(refused.status()).toBe(400);
	allowStatuses(guards, 400);
	await guards.expectClean();
});
