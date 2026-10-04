import fs from 'node:fs/promises';
import { expect, test, type Page } from '@playwright/test';
import pg from 'pg';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-061: the Reports screen on the ARV-060 report. The suite plants yesterday's 05:00 local hour of the demo airport's
// departures visa lane (D-VIS) with its histograms, as Stream writes them, and one alert owned by border shift
// supervisors whose rule name is markup. A border shift supervisor reads the lane, its hours and the alert (as text) and
// saves the server's CSV files; a terminal duty manager reads the same lane without the border alert; schedules are made,
// changed and deleted through the screen with names shown as text; a handler station manager has no Reports item, gets
// the no-access panel on a direct visit and is refused by the server; Arabic mirrors the screen.

test.skip(!databaseAvailable, 'the reports screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const reportsApi = `${hosts.main}/api/v1/sites/DMO/reports`;
const schedulesApi = `${hosts.main}/api/v1/report-schedules`;
const runId = Date.now().toString(36);
const ruleName = `<img src=x onerror=alert(1)> ${runId}`;
const scheduleName = `"><svg/onload=alert(1)> ${runId}`;

// DMO is in Asia/Dubai (UTC+4, no daylight saving): yesterday's local date and its 05:00 local in UTC.
const dubaiNow = new Date(Date.now() + 4 * 3_600_000);
const yesterday = new Date(Date.UTC(dubaiNow.getUTCFullYear(), dubaiNow.getUTCMonth(), dubaiNow.getUTCDate() - 1));
const date = yesterday.toISOString().slice(0, 10);
const fiveLocal = new Date(yesterday.getTime() + 5 * 3_600_000 - 4 * 3_600_000);

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

async function openReports(page: Page): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Reports' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Reports');
	await expect(page.getByTestId('report-date')).toHaveValue(date);
	await expect(page.getByTestId('daily-report')).toBeVisible();
}

const lane = (page: Page) => page.locator('[data-testid="lane-row"][data-zone="D-VIS"]');

test.beforeAll(async () => {
	const db = database();
	await db.connect();
	try {
		await db.query('DELETE FROM queue_minute WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc < $3', [
			'DMO/D-VIS',
			fiveLocal,
			new Date(fiveLocal.getTime() + 3_600_000)
		]);
		// 20 waits of 4 minutes and 10 of 20: the hour's P90 (rank 27 of 30) is in the long bucket, above the 15 minute target.
		await db.query(
			`INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
			 VALUES ('DMO/D-VIS', $1, 1, 'Final', 20, 20, 20, 33, ARRAY[8], ARRAY[20], now()),
			        ('DMO/D-VIS', $2, 1, 'Final', 10, 10, 10, 38, ARRAY[40], ARRAY[10], now())`,
			[fiveLocal, new Date(fiveLocal.getTime() + 60_000)]
		);
		await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, metric, severity, owner_role, raised_utc, raised_value, state,
			                    resolved_utc, resolution, resolved_by)
			 SELECT gen_random_uuid(), site_code, id, code, $1, 'D-VIS', metric, 'Critical', 'BorderShiftSupervisor', $2, 1, 'Resolved', $3, 'Manual', 'e2e'
			 FROM alert_rule WHERE code = 'R-002' AND site_code = 'DMO'`,
			[ruleName, new Date(fiveLocal.getTime() + 5 * 60_000), new Date(fiveLocal.getTime() + 25 * 60_000)]
		);
	} finally {
		await db.end();
	}
});

test.afterAll(async () => {
	const db = database();
	await db.connect();
	try {
		await db.query('DELETE FROM alert WHERE rule_name = $1', [ruleName]);
		await db.query('DELETE FROM queue_minute WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc < $3', [
			'DMO/D-VIS',
			fiveLocal,
			new Date(fiveLocal.getTime() + 3_600_000)
		]);
		// A schedule a failed run left behind.
		await db.query('UPDATE report_schedule SET deleted_on = now(), deleted_by = $2 WHERE name = $1 AND deleted_on IS NULL', [scheduleName, 'e2e']);
	} finally {
		await db.end();
	}
});

test('a border shift supervisor reads the lane, its hours and the alerts, and saves the server CSV files', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await openReports(page);

	// The server's figures, as the screen shows them.
	const token = (await signIn(accounts().webBorder)).accessToken;
	const report = await (await call('GET', `${reportsApi}/daily?date=${date}`, { token })).json();
	const visa = report.lanes.find((l: { zone: string }) => l.zone === 'D-VIS');
	expect(visa.peak.start).toBe('05:00');
	expect(visa.peak.p90Minutes).toBeGreaterThan(15);
	await expect(lane(page).getByTestId('lane-peak')).toContainText('05:00 to 06:00');
	const oneDecimal = new Intl.NumberFormat('en', { maximumFractionDigits: 1 });
	await expect(lane(page).getByTestId('lane-p90')).toHaveText(`${oneDecimal.format(visa.p90Minutes)} min`);
	await expect(lane(page)).toContainText('Visitors');

	await lane(page).getByTestId('toggle-hours').click();
	const hour = page.locator('[data-testid="hour-row"][data-start="05:00"]');
	await expect(hour).toContainText('30');
	await expect(hour).toContainText('Final');

	// The alert's markup name is text: no element made from it, nothing run.
	const name = page.getByTestId('alert-rule-name').filter({ hasText: runId });
	await expect(name).toHaveText(ruleName);
	await expect(page.locator('main img[src="x"]')).toHaveCount(0);
	await expect(page.getByTestId('headline-alerts')).toContainText(String(report.headline.alerts));

	// The CSV files are the server's, saved as they are.
	for (const section of ['hours', 'alerts'] as const) {
		const [download] = await Promise.all([page.waitForEvent('download'), page.getByTestId(`export-${section}`).click()]);
		expect(download.suggestedFilename()).toBe(`ariva-DMO-${date}-${section}.csv`);
		const text = await fs.readFile((await download.path())!, 'utf8');
		expect(text.charCodeAt(0), 'a byte order mark for spreadsheets').toBe(0xfeff);
		if (section === 'hours') expect(text).toContain('D-VIS');
		else expect(text).toContain(ruleName);
	}

	// Another day: the date field asks the server again.
	await page.getByTestId('report-today').click();
	await expect(page.getByTestId('report-today-note')).toBeVisible();
	await page.getByTestId('report-yesterday').click();
	await expect(page.getByTestId('report-date')).toHaveValue(date);
	await expect(lane(page).getByTestId('lane-peak')).toContainText('05:00 to 06:00');
	expect(guards.dialogs).toEqual([]);
	await guards.expectClean();
});

test('a terminal duty manager reads the same lane without the border alert', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().web);
	await openReports(page);
	await expect(lane(page).getByTestId('lane-peak')).toContainText('05:00 to 06:00');
	await expect(page.getByTestId('alert-rule-name').filter({ hasText: runId })).toHaveCount(0);
	const token = (await signIn(accounts().web)).accessToken;
	const report = await (await call('GET', `${reportsApi}/daily?date=${date}`, { token })).json();
	expect(
		report.alerts.some((a: { ruleName: string }) => a.ruleName === ruleName),
		'the server leaves it out'
	).toBe(false);
	await guards.expectClean();
});

test('schedules are made, changed and deleted through the screen, names shown as text', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await openReports(page);
	await page.getByTestId('tab-schedules').click();
	await expect(page.getByTestId('tab-schedules')).toHaveAttribute('aria-selected', 'true');

	await page.getByTestId('add-schedule').click();
	const form = page.getByTestId('schedule-form');
	await form.getByLabel('Name').fill(scheduleName);
	await form.getByLabel('Send at').fill('07:30');
	// Not sent during the run: saved disabled.
	await form.getByTestId('schedule-enabled').uncheck();
	await form.getByTestId('save-schedule').click();
	await expect(form.getByTestId('schedule-errors')).toContainText('Choose between 1 and 20 recipients.');

	const recipient = (user: string) => form.locator(`[data-testid="recipient"][data-user="${user}"]`);
	await expect(form.getByTestId('recipient-list')).not.toContainText(accounts().webHandler.userName);
	await recipient(accounts().webBorder.userName).check();
	await recipient(accounts().web.userName).check();
	await form.getByTestId('save-schedule').click();
	await expect(form).toHaveCount(0);
	// The confirmation names the schedule as text too, outside the page's main region.
	await expect(page.getByText(`Schedule ${scheduleName} saved.`)).toBeVisible();
	await expect(page.locator('svg[onload]')).toHaveCount(0);

	const row = page.getByTestId('schedule-row').filter({ hasText: runId });
	await expect(row.getByTestId('schedule-name')).toHaveText(scheduleName);
	await expect(row).toContainText('07:30');
	await expect(row).toContainText('Disabled');
	await expect(page.locator('main svg[onload]')).toHaveCount(0);
	const id = await row.getAttribute('data-id');

	await row.getByTestId('edit-schedule').click();
	await expect(form.getByLabel('Name')).toHaveValue(scheduleName);
	await recipient(accounts().web.userName).uncheck();
	await form.getByLabel('Send at').fill('08:15');
	await form.getByTestId('save-schedule').click();
	await expect(row).toContainText('08:15');

	const token = (await signIn(accounts().webBorder)).accessToken;
	const stored = await (await call('GET', `${schedulesApi}/${id}`, { token })).json();
	expect(stored.recipients.map((r: { userName: string }) => r.userName)).toEqual([accounts().webBorder.userName]);
	expect(stored).toMatchObject({ name: scheduleName, sendAt: '08:15', enabled: false, template: 'DailyPeaks' });

	await row.getByTestId('delete-schedule').click();
	await row.getByTestId('confirm-delete-schedule').click();
	await expect(row).toHaveCount(0);
	expect((await call('GET', `${schedulesApi}/${id}`, { token })).status()).toBe(404);
	await guards.expectClean();
});

test('a handler station manager has no reports and the server refuses it; Arabic mirrors the screen', async ({ page }) => {
	const handler = (await signIn(accounts().webHandler)).accessToken;
	expect((await call('GET', `${reportsApi}/daily?date=${date}`, { token: handler })).status()).toBe(403);
	expect((await call('GET', `${schedulesApi}?siteCode=DMO`, { token: handler })).status()).toBe(403);

	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webHandler);
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Reports' })).toHaveCount(0);
	await page.goto('/reports');
	await expect(page.getByTestId('no-access')).toBeVisible();
	await expect(page.getByTestId('daily-report')).toHaveCount(0);
	await guards.expectClean();

	const context = await page.context().browser()!.newContext();
	const other = await context.newPage();
	try {
		const otherGuards = await guardPage(other);
		await signInThroughUi(other, accounts().webBorder);
		await openReports(other);
		await other.getByTestId('language-toggle').click();
		await expect(other.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(other.getByRole('heading', { level: 1 })).toHaveText('التقارير');
		await expect(other.getByTestId('tab-schedules')).toHaveText('الجداول');
		await expect(lane(other).getByTestId('lane-peak')).toContainText('من 05:00 إلى 06:00');
		allowStatuses(otherGuards, 404);
		await otherGuards.expectClean();
	} finally {
		await context.close();
	}
});
