import crypto from 'node:crypto';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page, type Route } from '@playwright/test';
import pg from 'pg';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-120: the overflow marker per queue on the live and immigration screens, the data-quality panel per zone (conservation
// residual, track completion, occupancy range) for DataQuality.View holders only, and the health-check alarms per zone on
// the devices screen. Ariva.Api.Stream and the rule evaluation's own results are not part of the E2E run, so the suite
// plants what they write: an occupied minute of the arrivals visitors' overflow band (A-VIS, band A-OV) in overflow_minute,
// health bins of A-VIS and A-RES in zone_health_bin (the bin before the current one final, the current one provisional), and
// open alerts of two health-check rules created for this run, one owned by border shift supervisors (D-CIT, and a zone name
// that is markup) and one by terminal duty managers (CI-D). Names are text everywhere: a rule and a zone named with markup are
// shown as written and never run; route-fulfilled answers put markup in zone and band names on the live and immigration screens.

test.skip(!databaseAvailable, 'the screens need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const run = crypto.randomBytes(3).toString('hex');
/** Every rule this suite creates carries the mark, so a later run (or a retry, which starts a new worker) retires earlier ones. */
const mark = 'ARV-120 e2e';
const hostileRule = `<img src=x onerror=alert(1)> residual (${mark} ${run})`;
const hostileZone = `"><svg/onload=alert(2)> ${run}`;
const hostileBand = `'><img src=x onerror=alert(3)>`;
/** Elements and attributes a successful injection would create. */
const injected = ['img[src="x"]', 'svg[onload]', 'iframe', '[onerror]', '[onload]', 'a[href^="javascript:"]'];
// Codes that must never reach a terminal duty manager: Ariva's desk and gate codes and AMAN's (as immigration.spec.ts).
const deskMarkers = ['AR-0', 'AR-1', 'AR-2', 'AG-', 'DP-', 'DG-', 'IN0', 'EGIN', 'OUT0', 'EGOUT'];

const binMs = 15 * 60_000;
const currentBin = new Date(Math.floor(Date.now() / binMs) * binMs);
const earlierBin = new Date(currentBin.getTime() - binMs);
const bandMinute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 2 * 60_000);
const rules: { border: { id: string; code: string }; terminal: { id: string; code: string } } = {
	border: { id: '', code: '' },
	terminal: { id: '', code: '' }
};

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

/** Retires this suite's rules as a delete through the API would (soft), with their open alerts. */
async function retireRules(db: pg.Client): Promise<void> {
	await db.query(
		`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = now(), resolved_by = 'e2e-cleanup', resolution_note = 'e2e cleanup'
		  WHERE state <> 'Resolved' AND rule_id IN (SELECT id FROM alert_rule WHERE site_code = 'DMO' AND name LIKE $1)`,
		[`%(${mark} %`]
	);
	await db.query(`UPDATE alert_rule SET deleted_on = now(), deleted_by = 'e2e-cleanup' WHERE site_code = 'DMO' AND deleted_on IS NULL AND name LIKE $1`, [
		`%(${mark} %`
	]);
}

async function clearPlanted(db: pg.Client): Promise<void> {
	// Health rows and band minutes are evidence: the runtime role cannot delete them, the migration login (this client) can.
	const since = new Date(Date.now() - 3 * 3_600_000);
	await db.query(`DELETE FROM zone_health_bin WHERE zone_key IN ('DMO/A-VIS', 'DMO/A-RES') AND start_utc >= $1`, [since]);
	await db.query(`DELETE FROM overflow_minute WHERE zone_key = 'DMO/A-VIS' AND minute_utc >= $1`, [since]);
}

/** A health bin as Ariva.Api.Stream writes it (zone_health_bin, ARV-114a). */
async function plantBin(
	db: pg.Client,
	zone: string,
	start: Date,
	status: 'Final' | 'Provisional',
	b: { occupancy: [number, number | null]; residual: number | null; tracks: [number, number]; rate: number | null; minutes: [number, number, number] }
): Promise<void> {
	await db.query(
		`INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
		     conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
		     track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
		 VALUES ($1, $2, 1, 15, $3, 12, 20, 18, $4, $5, $6, $7, $8, 0, $9, 0, 0, $10, $11, $12, $13, $14, now())`,
		[
			`DMO/${zone}`,
			start,
			status,
			b.occupancy[0],
			b.occupancy[1],
			b.residual,
			b.tracks[0],
			b.tracks[1],
			status === 'Final' ? b.tracks[0] - b.tracks[1] : 0,
			status === 'Final' ? 0 : b.tracks[0] - b.tracks[1],
			b.rate,
			...b.minutes
		]
	);
}

/** A health-check rule of this run at the demo airport, owned by one role (created through the API, as the rule form does). */
async function createRule(token: string, values: Record<string, unknown>): Promise<{ id: string; code: string }> {
	const created = await call('POST', `${hosts.main}/api/v1/admin/alert-rules`, {
		token,
		data: {
			siteCode: 'DMO',
			comparator: 'GreaterThan',
			minQueueLength: null,
			clearThreshold: null,
			sustainMinutes: 1,
			clearAfterMinutes: 1,
			escalateAfterMinutes: null,
			escalateToRole: null,
			escalationContact: null,
			notifyByEmail: false,
			enabled: true,
			...values
		}
	});
	expect(created.status(), created.status() === 201 ? `rule ${values.metric} created` : await created.text()).toBe(201);
	const rule = await created.json();
	return { id: rule.id, code: rule.code };
}

test.beforeAll(async () => {
	const earlier = database();
	await earlier.connect();
	try {
		await retireRules(earlier);
	} finally {
		await earlier.end();
	}
	const admin = (await signIn(accounts().webAdmin)).accessToken;
	// Enabled, so the evaluation's housekeeping leaves their open alerts alone; no bin of their zones is ever planted, so the
	// evaluation itself raises and clears nothing for them.
	rules.border = await createRule(admin, {
		name: hostileRule,
		zones: ['D-CIT'],
		metric: 'ConservationResidual',
		threshold: 4,
		severity: 'Warning',
		ownerRole: 'BorderShiftSupervisor'
	});
	rules.terminal = await createRule(admin, {
		name: `E2E track completion (${mark} ${run})`,
		zones: ['CI-D'],
		metric: 'TrackCompletion',
		comparator: 'LessThan',
		threshold: 90,
		severity: 'Critical',
		ownerRole: 'TerminalDutyManager'
	});

	const db = database();
	await db.connect();
	try {
		await clearPlanted(db);
		await db.query(
			`INSERT INTO overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on)
			 VALUES ('DMO/A-VIS', 'A-OV', $1, 12, 3, 12, now())
			 ON CONFLICT (zone_key, band_name, minute_utc) DO UPDATE SET min_occupancy = 3, max_occupancy = 12`,
			[bandMinute]
		);
		// A-VIS: the earlier bin final with 3 people unaccounted for, 17 of 20 tracks exited and 2 minutes above capacity; the
		// current bin provisional (its end not measured yet, 4 of 10 tracks exited so far). A-RES: the earlier bin final and healthy.
		await plantBin(db, 'A-VIS', earlierBin, 'Final', { occupancy: [10, 12], residual: 3, tracks: [20, 17], rate: 0.85, minutes: [15, 15, 2] });
		await plantBin(db, 'A-VIS', currentBin, 'Provisional', { occupancy: [12, null], residual: null, tracks: [10, 4], rate: 0.4, minutes: [5, 5, 0] });
		await plantBin(db, 'A-RES', earlierBin, 'Final', { occupancy: [5, 5], residual: 0, tracks: [20, 20], rate: 1, minutes: [15, 15, 0] });
		// The rules' open alerts as the evaluation writes them (the bin each judged named with it).
		for (const [rule, zone, value] of [
			[rules.border, 'D-CIT', 7],
			[rules.border, hostileZone, 5],
			[rules.terminal, 'CI-D', 75]
		] as const) {
			await db.query(
				`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
				                    escalate_to_role, escalation_contact, raised_utc, raised_value, bin_start_utc, state)
				 SELECT gen_random_uuid(), site_code, id, code, name, $2, null, metric, severity, owner_role, escalate_after_minutes, escalate_to_role,
				        escalation_contact, date_trunc('minute', now()), $3, $4, 'Raised'
				   FROM alert_rule WHERE id = $1`,
				[rule.id, zone, value, earlierBin]
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
		await clearPlanted(db);
		await retireRules(db);
	} finally {
		await db.end();
	}
});

/**
 * No element or attribute an injection would create. The names are on the page as text (their words, onerror among them,
 * are in the text and in data attributes), so the check looks for elements and live attributes, never for words; the
 * page guards add that no dialog opened, no script ran into an error and the CSP saw nothing.
 */
async function expectNothingInjected(page: Page): Promise<void> {
	for (const selector of injected) await expect(page.locator(selector), selector).toHaveCount(0);
}

/** A value inside a CSS attribute selector's double quotes. */
const css = (value: string) => value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
/** WCAG 2.2 AA (axe, as accessibility.spec.ts) with the new panels filled: markers, badges and alarms on screen. */
async function expectAccessible(page: Page): Promise<void> {
	const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
	expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`), 'axe violations').toEqual([]);
}

const zoneRow = (page: Page, zone: string) => page.locator(`[data-testid="zone-row"][data-zone="${css(zone)}"]`);
const qualityRows = (page: Page, zone: string) =>
	page.locator(`[data-testid="data-quality"] [data-testid="quality-row"][data-zone="${css(zone)}"]`);

test('a border shift supervisor sees the overflow marker and each selected zone’s data quality on the live screen', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');

	// A-OV held 12 people two minutes ago: A-VIS is marked, a queue without an occupied band is not.
	const marker = zoneRow(page, 'A-VIS').getByTestId('overflow-marker');
	await expect(marker).toContainText('Overflow in use');
	await expect(marker).toHaveAttribute('data-bands', 'A-OV');
	await expect(marker).toContainText('A-OV: 12 people');
	await expect(zoneRow(page, 'A-RES').getByTestId('overflow-marker')).toHaveCount(0);

	// The selected zone's bins of the last two hours, newest first.
	await zoneRow(page, 'A-VIS').getByRole('button').click();
	const panel = page.getByTestId('data-quality');
	await expect(panel.getByRole('heading', { level: 2 })).toHaveText('Data quality: A-VIS');
	const rows = qualityRows(page, 'A-VIS');
	await expect(rows).toHaveCount(2);
	await expect(rows.nth(0)).toHaveAttribute('data-status', 'Provisional');
	await expect(rows.nth(0).getByTestId('quality-residual')).toHaveText('Not measured');
	await expect(rows.nth(0).getByTestId('quality-completion')).toHaveText('40% so far (4 of 10)');
	await expect(rows.nth(0).getByTestId('quality-completion')).toHaveAttribute('data-tone', 'neutral');
	await expect(rows.nth(0).getByTestId('quality-occupancy')).toHaveText('Within capacity (5 min)');
	await expect(rows.nth(1)).toHaveAttribute('data-status', 'Final');
	await expect(rows.nth(1).getByTestId('quality-residual')).toHaveText('+3 people');
	await expect(rows.nth(1).getByTestId('quality-residual')).toHaveAttribute('data-tone', 'warning');
	await expect(rows.nth(1).getByTestId('quality-completion')).toHaveText('85% (17 of 20)');
	await expect(rows.nth(1).getByTestId('quality-completion')).toHaveAttribute('data-tone', 'warning');
	await expect(rows.nth(1).getByTestId('quality-occupancy')).toHaveText('2 of 15 min outside');
	await expect(rows.nth(1).getByTestId('quality-occupancy')).toHaveAttribute('data-tone', 'warning');
	await expectAccessible(page);

	// Another zone: its own bins, healthy.
	await zoneRow(page, 'A-RES').getByRole('button').click();
	await expect(panel.getByRole('heading', { level: 2 })).toHaveText('Data quality: A-RES');
	const healthy = qualityRows(page, 'A-RES');
	await expect(healthy).toHaveCount(1);
	await expect(healthy.getByTestId('quality-residual')).toHaveText('0 people');
	await expect(healthy.getByTestId('quality-completion')).toHaveText('100% (20 of 20)');
	await expect(healthy.getByTestId('quality-occupancy')).toHaveText('Within capacity (15 min)');
	for (const cell of ['quality-residual', 'quality-completion', 'quality-occupancy'])
		await expect(healthy.getByTestId(cell)).toHaveAttribute('data-tone', 'success');

	// A zone without bins says so.
	await zoneRow(page, 'A-CIT').getByRole('button').click();
	await expect(page.getByTestId('data-quality-empty')).toHaveText('No health bins in the last 2 hours');

	// Arabic mirrors the screen and translates the panel and the marker.
	await zoneRow(page, 'A-VIS').getByRole('button').click();
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(panel.getByRole('heading', { level: 2 })).toHaveText('جودة البيانات: A-VIS');
	await expect(marker).toContainText('منطقة الفائض مستخدمة');
	await expect(rows.nth(1).getByTestId('quality-completion')).toContainText('(17 من 20)');
	await guards.expectClean();
});

test('immigration: the lane with an occupied band is marked, and each lane queue’s latest bin is shown', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Immigration');

	const visitors = page.locator('[data-testid="lane-row"][data-lane="VIS"]');
	await expect(visitors.getByTestId('overflow-marker')).toContainText('Overflow in use');
	await expect(page.locator('[data-testid="lane-row"][data-lane="RES"]').getByTestId('overflow-marker')).toHaveCount(0);

	const panel = page.getByTestId('data-quality');
	await expect(panel.getByRole('heading', { level: 2 })).toHaveText('Data quality');
	// One row per lane queue of the arrivals hall: the latest bin only.
	await expect(qualityRows(page, 'A-VIS')).toHaveCount(1);
	await expect(qualityRows(page, 'A-VIS')).toHaveAttribute('data-status', 'Provisional');
	await expect(qualityRows(page, 'A-VIS').getByTestId('quality-zone')).toHaveText('Visitors (A-VIS)');
	await expect(qualityRows(page, 'A-RES').getByTestId('quality-completion')).toHaveText('100% (20 of 20)');
	await expect(qualityRows(page, 'A-CIT')).toContainText('No bin yet');
	await expectAccessible(page);

	// The departures hall's lanes have their own queues: none of the arrivals' rows.
	await page.getByTestId('hall-Emigration').click();
	await expect(qualityRows(page, 'D-VIS')).toContainText('No bin yet');
	await expect(qualityRows(page, 'A-VIS')).toHaveCount(0);
	await guards.expectClean();
});

test('a terminal duty manager sees the zone-level panels but no border data: no desk code, no border-owned alarm', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().web);
	await expect(zoneRow(page, 'A-VIS').getByTestId('overflow-marker')).toContainText('Overflow in use');
	await zoneRow(page, 'A-VIS').getByRole('button').click();
	await expect(qualityRows(page, 'A-VIS')).toHaveCount(2);

	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
	await expect(page.getByTestId('desks-hidden')).toBeVisible();
	await expect(qualityRows(page, 'A-RES')).toHaveCount(1);
	const html = `${await page.locator('main').innerText()}\n${await page.locator('main').innerHTML()}`;
	for (const code of deskMarkers) expect(html, `${code} on the page`).not.toContain(code);

	// The health-check alarms the duty manager's role owns, and none of the border shift supervisors'.
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Devices' }).click();
	const alarms = page.getByTestId('health-alarms');
	await expect(alarms.locator(`[data-testid="health-alarm"][data-rule="${rules.terminal.code}"]`)).toHaveCount(1);
	await expect(alarms.locator(`[data-testid="health-alarm"][data-rule="${rules.terminal.code}"]`)).toContainText('Track completion: 75 percent');
	await expect(alarms.locator(`[data-testid="health-alarm"][data-rule="${rules.border.code}"]`)).toHaveCount(0);
	expect(await page.locator('main').innerText(), 'nothing of the border-owned alarms').not.toContain(hostileRule);
	await guards.expectClean();
});

test('a handler station manager sees the marker but no data-quality panel, and the server refuses its health read', async ({ page }) => {
	const guards = await guardPage(page);
	const asked: string[] = [];
	page.on('request', (request) => {
		if (request.url().includes('/zone-health')) asked.push(request.url());
	});
	await signInThroughUi(page, accounts().webHandler);
	await expect(zoneRow(page, 'A-VIS').getByTestId('overflow-marker')).toContainText('Overflow in use');
	await zoneRow(page, 'A-VIS').getByRole('button').click();
	await expect(page.getByTestId('wait-chart')).toBeVisible();
	await expect(page.getByTestId('data-quality')).toHaveCount(0);
	expect(asked, 'the screen asks nothing of the health checks').toEqual([]);

	const handler = (await signIn(accounts().webHandler)).accessToken;
	const read = await call(
		'GET',
		`${hosts.main}/api/v1/sites/DMO/zone-health?zone=A-VIS&from=${encodeURIComponent(earlierBin.toISOString())}&to=${encodeURIComponent(new Date().toISOString())}`,
		{ token: handler }
	);
	expect(read.status(), 'DataQuality.View is not the handler’s').toBe(403);
	await guards.expectClean();
});

test('the devices screen lists each zone’s health-check alarms with rule and zone names as text', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Devices' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');
	const alarms = page.getByTestId('health-alarms');
	await expect(alarms.getByRole('heading', { level: 2 })).toHaveText('Health-check alarms');

	const mine = alarms.locator(`[data-testid="health-alarm"][data-rule="${rules.border.code}"]`);
	await expect(mine).toHaveCount(2);
	const cit = alarms.locator('[data-testid="health-alarm-zone"][data-zone="D-CIT"]');
	await expect(cit.getByRole('heading', { level: 3 })).toHaveText('D-CIT');
	await expect(cit.locator(`[data-rule="${rules.border.code}"]`)).toContainText(hostileRule);
	await expect(cit.locator(`[data-rule="${rules.border.code}"]`)).toContainText('Conservation residual: 7 people');
	await expect(cit.locator(`[data-rule="${rules.border.code}"]`)).toContainText('Warning');
	await expect(cit.locator(`[data-rule="${rules.border.code}"]`)).toContainText('Raised');
	// A zone named with markup is its own group, its name shown as written.
	const hostile = alarms.locator('[data-testid="health-alarm-zone"]').filter({ hasText: `svg/onload=alert(2)> ${run}` });
	await expect(hostile.getByRole('heading', { level: 3 })).toHaveText(hostileZone);
	// The duty manager's alarm is not the supervisor's.
	await expect(alarms.locator(`[data-testid="health-alarm"][data-rule="${rules.terminal.code}"]`)).toHaveCount(0);
	await expectNothingInjected(page);
	await expectAccessible(page);
	await guards.expectClean();
});

test('an administrator sees every role’s health-check alarms', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Devices' }).click();
	const alarms = page.getByTestId('health-alarms');
	await expect(alarms.locator(`[data-testid="health-alarm"][data-rule="${rules.border.code}"]`)).toHaveCount(2);
	await expect(alarms.locator(`[data-testid="health-alarm"][data-rule="${rules.terminal.code}"]`)).toHaveCount(1);
	await expect(alarms.locator('[data-testid="health-alarm-zone"][data-zone="CI-D"]')).toContainText('Critical');
	await guards.expectClean();
});

test('a refused overflow read marks no queue and says so on the live and immigration screens', async ({ page }) => {
	const guards = await guardPage(page);
	// What the server answers beyond 500 bands (CWE-120: refused, never cut short); A-OV's occupied minute is still planted.
	await page.route('**/api/v1/sites/DMO/overflow-bands', (route: Route) =>
		route.fulfill({
			status: 409,
			json: { title: 'Conflict', status: 409, detail: "The site's published zone profile has more overflow bands than one answer holds (500)." }
		})
	);
	await signInThroughUi(page, accounts().webBorder);
	await expect(page.getByTestId('overflow-refused')).toHaveText(
		"Overflow bands are not marked: the site's zone profile has more bands than one answer holds (500)."
	);
	await expect(zoneRow(page, 'A-VIS')).toBeVisible();
	await expect(page.getByTestId('overflow-marker')).toHaveCount(0);

	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
	await expect(page.locator('[data-testid="lane-row"][data-lane="VIS"]')).toBeVisible();
	await expect(page.getByTestId('overflow-refused')).toBeVisible();
	await expect(page.getByTestId('overflow-marker')).toHaveCount(0);
	// Chromium logs each refused answer as a failed resource; nothing else may be logged.
	allowStatuses(guards, 409);
	await guards.expectClean();
});

/** Answers the overflow and health reads with markup in the zone and band names, as a hostile profile would name them. */
async function hostileAnswers(page: Page, zoneOf: (name: string) => string): Promise<void> {
	await page.route('**/api/v1/sites/DMO/overflow-bands', (route: Route) =>
		route.fulfill({
			json: {
				siteCode: 'DMO',
				asOfUtc: new Date().toISOString(),
				freshMinutes: 6,
				zones: [
					{
						zone: hostileZone,
						state: 'Occupied',
						bands: [{ band: hostileBand, state: 'Occupied', minuteUtc: bandMinute.toISOString(), maxOccupancy: 7 }]
					}
				]
			}
		})
	);
	await page.route('**/api/v1/sites/DMO/zone-health?**', (route: Route) => {
		const zone = new URL(route.request().url()).searchParams.get('zone') ?? '';
		return route.fulfill({
			json: {
				siteCode: 'DMO',
				zone: zoneOf(zone),
				fromUtc: earlierBin.toISOString(),
				toUtc: new Date().toISOString(),
				truncated: false,
				bins: [
					{
						startUtc: earlierBin.toISOString(),
						lengthMinutes: 15,
						revision: 1,
						status: 'Final',
						zoneProfileVersion: 12,
						entries: 20,
						exits: 18,
						occupancyStart: 10,
						occupancyEnd: 12,
						conservationResidual: 0,
						tracksEntered: 20,
						tracksExited: 20,
						tracksAbandoned: 0,
						tracksFragmented: 0,
						tracksCensored: 0,
						tracksRejected: 0,
						tracksOpen: 0,
						trackCompletionRate: 1,
						occupancyMinutes: 15,
						capacityMinutes: 15,
						minutesOutsideCapacity: 0
					}
				]
			}
		});
	});
}

test('zone and band names with markup render as text on the live and immigration screens', async ({ page }) => {
	const guards = await guardPage(page);
	// The published profile as a hostile editor would have named a queue zone (A-CIT); the hub refuses to join it.
	await page.route('**/api/v1/admin/zone-profiles/*', async (route: Route) => {
		const response = await route.fetch();
		const profile = await response.json();
		for (const zone of profile.zones ?? []) if (zone.name === 'A-CIT' && zone.kind === 'Queue') zone.name = hostileZone;
		return route.fulfill({ response, json: profile });
	});
	await page.route('**/api/v1/sites/DMO/immigration', async (route: Route) => {
		const response = await route.fetch();
		const view = await response.json();
		for (const hall of view.halls ?? []) for (const queue of hall.queues ?? []) if (queue.zone === 'A-CIT') queue.zone = hostileZone;
		return route.fulfill({ response, json: view });
	});
	await hostileAnswers(page, (zone) => zone);
	await signInThroughUi(page, accounts().webBorder);

	const row = zoneRow(page, hostileZone);
	await expect(row.getByRole('rowheader')).toHaveText(hostileZone);
	await expect(row.getByTestId('overflow-marker')).toHaveAttribute('data-bands', hostileBand);
	await expect(row.getByTestId('overflow-marker')).toContainText(`${hostileBand}: 7 people`);
	await row.getByRole('button').click();
	await expect(page.getByTestId('data-quality').getByRole('heading', { level: 2 })).toHaveText(`Data quality: ${hostileZone}`);
	await expect(qualityRows(page, hostileZone)).toHaveCount(1);
	await expectNothingInjected(page);

	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
	const citizens = page.locator('[data-testid="lane-row"][data-lane="CIT"]');
	await expect(citizens.getByTestId('overflow-marker')).toHaveAttribute('data-bands', hostileBand);
	await expect(qualityRows(page, hostileZone).getByTestId('quality-zone')).toHaveText(`Citizens (${hostileZone})`);
	await expectNothingInjected(page);
	await guards.expectClean();
});
