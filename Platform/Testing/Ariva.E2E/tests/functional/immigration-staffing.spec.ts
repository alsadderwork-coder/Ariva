import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page, type Request } from '@playwright/test';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import {
	apiTime,
	plantStaffingMinute,
	routedAnswer,
	routedLane,
	staffingDatabase,
	staffingPlan,
	staffingRoute,
	type PlantedInterval,
	type PlantedLane,
	type RoutedLane
} from '../support/staffing';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-181b1: the immigration screen shows ARV-181a's indicative staffing recommendation (formulas F16 pilot version) to the
// holders of ArrivalWaveLanes.View only (border shift supervisors and administrators): the lane split of the arrival wave is
// border data, so the terminal duty manager, who holds the screen's Immigration.View, gets no panel and sends no request,
// and the server refuses the others. Ariva.Api.Stream does not run in the E2E run, so the job's own rows have no
// recommendation (their reasons show as text); the suite plants the residents lane's minutes (A-RES; the API suite plants
// A-VIS) for the coming minutes, before the job's run of each, so the planted numbers are the latest while the suite reads
// them. Names the server writes reach the screen only through allowlists, and routed answers with markup in every text
// field inject nothing.

test.skip(!databaseAvailable, 'the staffing panel needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = (site = 'DMO') => `${hosts.main}/api/v1/sites/${site}/staffing-recommendation`;
const isStaffingRequest = (request: Request) => /\/staffing-recommendation(\?|$)/.test(new URL(request.url()).pathname);
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
/** Planted minutes from the first one: the suite reads them for at most this many minutes. */
const plantedMinutes = 10;

const residents: PlantedLane = {
	zoneKey: 'DMO/A-RES',
	lane: 'RES',
	desks: 6,
	openNow: 3,
	openNowDegraded: false,
	queueNow: 25,
	targetMinutes: 10,
	busyCap: 0.85,
	throughputPerDesk: 0.6316,
	throughputSource: 'Measured',
	laneGap: 'None'
};

const none = { atLimit: false, shortfall: null, noServiceOpen: false };
// Now short by one desk; the next quarter hour at the lane's limit (6 desks) and short by 2; then at the limit with the
// target out of reach (waits of 120 minutes or more); a flight without a passenger figure in the fifth, and every interval
// after it without a recommendation (the backlog carried into them is not known).
const intervals: PlantedInterval[] = [
	{ ...none, arrivals: 40, recommended: 4, bound: 'Wait', waitRecommended: 8, waitOpen: 12.5, gap: 'None' },
	{ ...none, arrivals: 120, recommended: 6, bound: 'Busy', atLimit: true, shortfall: 2, waitRecommended: 18, waitOpen: 64, gap: 'None' },
	{ ...none, arrivals: 200, recommended: 6, bound: 'Both', atLimit: true, waitRecommended: 120, waitOpen: 120, gap: 'None' },
	{ ...none, arrivals: 30, recommended: 2, bound: 'Wait', waitRecommended: 3, waitOpen: 2.5, gap: 'None' },
	{ ...none, arrivals: null, recommended: null, bound: 'None', waitRecommended: null, waitOpen: null, gap: 'FlightWithoutPassengers' },
	...Array.from({ length: 8 }, () => ({ ...none, arrivals: 20, recommended: null, bound: 'None' as const, waitRecommended: null, waitOpen: null, gap: 'BacklogUnknown' }))
];

/** The planted intervals as the screen shows them: arrivals, the recommended desks, both waits, or the reason. */
const shown = [
	{ arrivals: '40', recommended: '4 for the wait', waitRecommended: '8 min', waitOpen: '12.5 min' },
	{ arrivals: '120', recommended: 'All 6 desks, short by 2 for the busy cap', waitRecommended: '18 min', waitOpen: '64 min' },
	{ arrivals: '200', recommended: 'All 6 desks, target out of reach for both', waitRecommended: 'Over 120 min', waitOpen: 'Over 120 min' },
	{ arrivals: '30', recommended: '2 for the wait', waitRecommended: '3 min', waitOpen: '2.5 min' },
	{ arrivals: 'Not known', why: 'Why none: A flight without a passenger figure' },
	...Array.from({ length: 8 }, () => ({ arrivals: '20', why: 'Why none: Queue not known: earlier arrivals missing' }))
];

/** The reasons in English, as the screen words them; a gap name never reaches the screen. */
const reasonTexts: Record<string, string> = {
	NoQueueLength: 'No queue length now',
	NoThroughput: 'No throughput measured or set',
	FlightWithoutPassengers: 'A flight without a passenger figure',
	BacklogUnknown: 'Queue not known: earlier arrivals missing',
	ArrivalsTruncated: 'Too many arriving flights to read',
	NoDesks: 'No desk in service',
	NoLaneDemand: 'No arrivals forecast for this lane',
	ArrivalsOutOfRange: 'Arrivals out of range',
	Stale: 'No recent recommendation'
};

let firstMinute: Date;

test.beforeAll(async () => {
	// Planted between seconds 0 and 40 of a minute, before the job's run of it (second 45): the job writes each minute once
	// (ON CONFLICT DO NOTHING), so it leaves every planted minute as planted and the planted values stay the latest ones the
	// API reads until the last planted minute has passed.
	while (new Date().getUTCSeconds() >= 40) await sleep(1_000);
	firstMinute = new Date(Math.floor(Date.now() / 60_000) * 60_000);
	const db = staffingDatabase();
	await db.connect();
	try {
		for (let j = 0; j < plantedMinutes; j++) await plantStaffingMinute(db, residents, new Date(firstMinute.getTime() + j * 60_000), intervals);
	} finally {
		await db.end();
	}
});

test.afterAll(async () => {
	if (!firstMinute) return;
	const db = staffingDatabase();
	await db.connect();
	try {
		await db.query(`DELETE FROM lane_staffing WHERE zone_key = 'DMO/A-RES' AND minute_utc >= $1 AND minute_utc < $2`, [
			firstMinute,
			new Date(firstMinute.getTime() + plantedMinutes * 60_000)
		]);
	} finally {
		await db.end();
	}
});

/** Records every request the page sends to the staffing API. */
function staffingRequests(page: Page): string[] {
	const seen: string[] = [];
	page.on('request', (request) => {
		if (isStaffingRequest(request)) seen.push(request.url());
	});
	return seen;
}

async function openImmigration(page: Page): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Immigration');
	await expect(page.locator('[data-testid="lane-row"][data-lane="CIT"]')).toBeVisible();
}

async function expectAccessible(page: Page): Promise<void> {
	const results = await new AxeBuilder({ page }).include('[data-testid="staffing"]').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
	expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`), 'axe violations in the panel').toEqual([]);
}

test('a border shift supervisor sees each arrivals lane now and the residents lane’s next 3 hours as planted', async ({ page }) => {
	const guards = await guardPage(page);
	const requests = staffingRequests(page);
	await signInThroughUi(page, accounts().webBorder);
	await openImmigration(page);

	const panel = page.getByTestId('staffing');
	await expect(panel).toHaveAttribute('data-state', 'ready');
	await expect(panel.getByRole('heading', { level: 2 })).toHaveText('Recommended open desks');
	await expect(panel.getByTestId('staffing-indicative')).toHaveText('Indicative');
	await expect(panel).toContainText('no roster, shift or break rules');
	await expect(panel.getByTestId('staffing-settings')).toContainText('Target wait 10 min; each desk at most 85% busy.');
	expect(requests.length, 'the panel reads the recommendation').toBeGreaterThanOrEqual(1);
	expect(requests.every((url) => url.endsWith('/api/v1/sites/DMO/staffing-recommendation')), 'the chosen site only').toBe(true);

	// The current quarter hour: open now is the desk-state count, labelled as such, not the lanes table's AMAN sessions.
	const summary = panel.getByTestId('staffing-summary');
	// The summary's own header row (each lane's detail table nests its own).
	await expect(summary.locator(':scope > thead')).toContainText('Open now (desk states)');
	await expect(panel.getByTestId('staffing-open-hint')).toContainText('AMAN');
	const row = summary.locator('[data-testid="staffing-summary-row"][data-zone="A-RES"]');
	await expect(row.locator('th')).toHaveText('Residents A-RES');
	await expect(row.getByTestId('staffing-open-now')).toHaveText('3');
	await expect(row.getByTestId('staffing-recommended-now')).toHaveText('4');
	await expect(row.getByTestId('staffing-difference')).toHaveText('Short by 1');
	await expect(row.getByTestId('staffing-difference').locator('[data-status="warning"]')).toHaveText('Short by 1');
	await expect(row.getByTestId('staffing-wait-open')).toHaveText('12.5 min');
	await expect(row.getByTestId('staffing-wait-recommended')).toHaveText('8 min');

	// The lanes in the screen's order; the first lane short of desks now opens its next 3 hours.
	const order = await summary.getByTestId('staffing-summary-row').evaluateAll((rows) => rows.map((r) => r.getAttribute('data-lane')));
	const rank = ['CIT', 'RES', 'VIS', 'CRW', 'EG'];
	expect(order, 'citizens, residents, visitors, crew, e-gates').toEqual([...order].sort((a, b) => rank.indexOf(a!) - rank.indexOf(b!)));
	await expect(row.getByTestId('staffing-toggle')).toHaveAttribute('aria-expanded', 'true');

	const detail = panel.locator('[data-testid="staffing-lane"][data-zone="A-RES"]');
	await expect(detail).toBeVisible();
	await expect(detail.locator('caption')).toContainText('Residents (A-RES), next 3 hours by quarter hour');
	await expect(detail.locator('caption')).toContainText('Target wait 10 min, each desk at most 85% busy.');
	await expect(detail.getByTestId('staffing-cycle')).toHaveText('1.6 min per passenger at a desk (measured).');
	await expect(detail.locator('thead')).toContainText('Predicted wait, 3 open now');
	const rows = detail.getByTestId('staffing-row');
	await expect(rows).toHaveCount(13);

	// The rows follow the plan of a planted minute: now to the next quarter hour, then 12 quarter hours.
	const minute = new Date(String(await rows.first().getAttribute('data-start')));
	expect(minute.getTime() - firstMinute.getTime(), 'a planted minute').toBeGreaterThanOrEqual(0);
	expect(minute.getTime() - firstMinute.getTime(), 'a planted minute').toBeLessThan(plantedMinutes * 60_000);
	const starts = await rows.evaluateAll((list) => list.map((r) => r.getAttribute('data-start')));
	expect(starts).toEqual(staffingPlan(minute).map((slot) => apiTime(slot.start)));
	await expect(rows.first().getByTestId('staffing-from')).toContainText('Now to ');
	await expect(rows.nth(1).getByTestId('staffing-from').locator('time')).toHaveAttribute('datetime', starts[1]!);

	for (const [k, expected] of shown.entries()) {
		const line = rows.nth(k);
		await expect(line.getByTestId('staffing-arrivals'), `interval ${k}: arrivals`).toHaveText(expected.arrivals);
		if ('why' in expected) {
			await expect(line.getByTestId('staffing-why'), `interval ${k}: why none`).toHaveText(expected.why!);
			await expect(line.getByTestId('staffing-recommended')).toHaveCount(0);
		} else {
			await expect(line.getByTestId('staffing-recommended'), `interval ${k}: recommended`).toHaveText(expected.recommended!);
			await expect(line.getByTestId('staffing-wait-recommended'), `interval ${k}: wait, recommended`).toHaveText(expected.waitRecommended!);
			await expect(line.getByTestId('staffing-wait-open'), `interval ${k}: wait, open now`).toHaveText(expected.waitOpen!);
		}
	}

	// The job's own lanes (no queue length without Stream): each reason is words, never the name the server writes.
	const reasons = await summary.locator('[data-testid="staffing-summary-row"]:not([data-zone="A-RES"]) [data-testid="staffing-reason"]').allInnerTexts();
	for (const text of reasons) expect(Object.values(reasonTexts), text).toContain(text.trim());
	expect(await panel.innerText(), 'no gap, bound or source name on the screen').not.toMatch(
		/\b(NoQueueLength|NoThroughput|FlightWithoutPassengers|BacklogUnknown|ArrivalsTruncated|NoDesks|NoLaneDemand|ArrivalsOutOfRange|Stale|Wait|Busy|Both|Measured|Fallback)\b/
	);
	await expectAccessible(page);

	// The departures hall has no recommendation in this version: one line says so, and no panel.
	await page.getByTestId('hall-Emigration').click();
	await expect(page.getByTestId('staffing-departures')).toContainText('arrivals hall only');
	await expect(page.getByTestId('staffing')).toHaveCount(0);
	await page.getByTestId('hall-Immigration').click();
	await expect(page.getByTestId('staffing')).toHaveAttribute('data-state', 'ready');
	await expect(page.getByTestId('staffing-departures')).toHaveCount(0);
	await guards.expectClean();
});

test('an administrator sees the panel too, here in dark mode', async ({ page }) => {
	const guards = await guardPage(page);
	await page.emulateMedia({ colorScheme: 'dark' });
	await signInThroughUi(page, accounts().webAdmin);
	await openImmigration(page);
	await expect(page.locator('html')).toHaveClass(/\bdark\b/);
	const row = page.locator('[data-testid="staffing-summary-row"][data-zone="A-RES"]');
	await expect(page.getByTestId('staffing')).toHaveAttribute('data-state', 'ready');
	await expect(row.getByTestId('staffing-difference')).toHaveText('Short by 1');
	await expect(page.locator('[data-testid="staffing-lane"][data-zone="A-RES"]')).toBeVisible();
	await expectAccessible(page);
	await guards.expectClean();
});

test('a terminal duty manager holds the screen but not the lane split: no panel and no request to the staffing API', async ({ page }) => {
	const guards = await guardPage(page);
	const requests = staffingRequests(page);
	await signInThroughUi(page, accounts().web);
	await openImmigration(page);
	await page.waitForLoadState('networkidle');
	await expect(page.getByTestId('lanes-table')).toBeVisible();
	await expect(page.getByTestId('staffing')).toHaveCount(0);
	await page.getByTestId('hall-Emigration').click();
	await expect(page.getByTestId('hall-Emigration')).toHaveAttribute('aria-selected', 'true');
	await expect(page.getByTestId('staffing-departures')).toHaveCount(0);
	await page.getByTestId('hall-Immigration').click();
	await page.waitForLoadState('networkidle');
	await expect(page.getByTestId('staffing')).toHaveCount(0);
	expect(requests, 'no request to the staffing API').toEqual([]);
	expect(await page.locator('main').innerText()).not.toContain('Recommended open desks');

	// The server is the authority: the API refuses the terminal duty manager anyway.
	const token = (await signIn(accounts().web)).accessToken;
	expect((await call('GET', api(), { token })).status()).toBe(403);
	await guards.expectClean();
});

test('a handler station manager has no panel even on the screen’s address, and the API refuses it', async ({ page }) => {
	const token = (await signIn(accounts().webHandler)).accessToken;
	const refused = await call('GET', api(), { token });
	expect(refused.status()).toBe(403);
	expect(await refused.text()).not.toContain('A-RES');

	const guards = await guardPage(page);
	const requests = staffingRequests(page);
	await signInThroughUi(page, accounts().webHandler);
	await page.goto('/immigration');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Immigration');
	await expect(page.getByTestId('immigration-problem')).toBeVisible();
	await page.waitForLoadState('networkidle');
	await expect(page.getByTestId('staffing')).toHaveCount(0);
	expect(requests, 'no request to the staffing API').toEqual([]);
	// The immigration read itself is refused (403): the browser logs it.
	allowStatuses(guards, 403);
	await guards.expectClean();
});

test('the next 3 hours open and close from the keyboard, with a visible focus', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await openImmigration(page);
	await expect(page.getByTestId('staffing')).toHaveAttribute('data-state', 'ready');
	const toggle = page.locator('[data-testid="staffing-toggle"][data-zone="A-RES"]');
	const detail = page.locator('[data-testid="staffing-lane"][data-zone="A-RES"]');
	await expect(toggle).toHaveAccessibleName('Next 3 hours, Residents (A-RES)');
	await expect(toggle).toHaveAttribute('aria-controls', /.+/);
	expect(await page.locator(`#${await toggle.getAttribute('aria-controls')}`).getByTestId('staffing-lane').count()).toBe(1);

	// From the hall tabs, Tab reaches the toggle.
	await page.getByTestId('hall-Immigration').focus();
	let reached = false;
	for (let presses = 0; presses < 40 && !reached; presses++) {
		await page.keyboard.press('Tab');
		reached = await toggle.evaluate((element) => element === document.activeElement);
	}
	expect(reached, 'the toggle is in the tab order').toBe(true);
	expect(await toggle.evaluate((element) => getComputedStyle(element).boxShadow), 'a visible focus ring (2 pixels)').toContain('2px');

	await expect(toggle).toHaveAttribute('aria-expanded', 'true');
	await page.keyboard.press('Enter');
	await expect(toggle).toHaveAttribute('aria-expanded', 'false');
	await expect(detail).toBeHidden();
	await page.keyboard.press('Space');
	await expect(toggle).toHaveAttribute('aria-expanded', 'true');
	await expect(detail).toBeVisible();
	await guards.expectClean();
});

test('Arabic mirrors the panel with Latin digits', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await openImmigration(page);
	await expect(page.getByTestId('staffing')).toHaveAttribute('data-state', 'ready');
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	const panel = page.getByTestId('staffing');
	await expect(panel.getByRole('heading', { level: 2 })).toHaveText('المكاتب الموصى بفتحها');
	await expect(panel.getByTestId('staffing-indicative')).toHaveText('استرشادي');
	const row = panel.locator('[data-testid="staffing-summary-row"][data-zone="A-RES"]');
	await expect(row.locator('th')).toContainText('المقيمون');
	await expect(row.getByTestId('staffing-open-now')).toHaveText('3');
	await expect(row.getByTestId('staffing-difference')).toHaveText('ينقص 1');
	await expect(row.getByTestId('staffing-wait-open')).toHaveText('12.5 د');
	const detail = panel.locator('[data-testid="staffing-lane"][data-zone="A-RES"]');
	await expect(detail.getByTestId('staffing-row').nth(4).getByTestId('staffing-why')).toContainText('رحلة بلا عدد ركاب');
	await expectAccessible(page);
	// The page allows a 404 here as the immigration suite does (the language switch reads the user again).
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('spare, matching, no desk open, an approximate count, a stale lane, a refusal and a failure are words', async ({ page }) => {
	const guards = await guardPage(page);
	await page.route(staffingRoute, (route) =>
		route.fulfill({
			json: routedAnswer([
				routedLane('A-CIT', 'CIT', { openNow: 5 }, { recommended: 3 }),
				routedLane('A-RES', 'RES', { openNow: 0 }, { recommended: 2, waitOpenNowMinutes: null, noServiceOpenNow: true }),
				routedLane('A-VIS', 'VIS', { openNow: 4, openNowDegraded: true }),
				{ ...routedLane('A-CRW', 'CRW'), minuteUtc: null, targetMinutes: null, busyCap: null, desks: null, openNow: null, queueNow: null, cycleMinutes: null, throughputSource: null, gap: 'Stale', intervals: [] },
				routedLane('A-EG', 'EG', { openNow: null }, { recommended: 1 })
			])
		})
	);
	await signInThroughUi(page, accounts().webBorder);
	await openImmigration(page);
	const panel = page.getByTestId('staffing');
	await expect(panel).toHaveAttribute('data-state', 'ready');
	const row = (zone: string) => panel.locator(`[data-testid="staffing-summary-row"][data-zone="${zone}"]`);
	await expect(row('A-CIT').getByTestId('staffing-difference')).toHaveText('Spare 2');
	await expect(row('A-RES').getByTestId('staffing-difference')).toHaveText('Short by 2');
	await expect(row('A-RES').getByTestId('staffing-wait-open')).toHaveText('No desk open');
	await expect(row('A-VIS').getByTestId('staffing-difference')).toHaveText('Matches');
	await expect(row('A-VIS').getByTestId('staffing-degraded')).toHaveAttribute('title', 'Approximate: a desk state is degraded');
	await expect(row('A-VIS').getByTestId('staffing-open-now')).toHaveText('≈4 (Approximate: a desk state is degraded)');
	await expect(row('A-CRW').getByTestId('staffing-reason')).toHaveText('No recent recommendation');
	await expect(row('A-CRW').getByTestId('staffing-toggle')).toHaveCount(0);
	await expect(row('A-EG').getByTestId('staffing-difference')).toHaveText('Open count not known');
	// The first lane short now opens; the spare one before it does not.
	await expect(row('A-CIT').getByTestId('staffing-toggle')).toHaveAttribute('aria-expanded', 'false');
	await expect(row('A-RES').getByTestId('staffing-toggle')).toHaveAttribute('aria-expanded', 'true');
	await expect(panel.locator('[data-testid="staffing-lane"][data-zone="A-RES"] thead')).toContainText('Predicted wait, 0 open now');

	await page.unroute(staffingRoute);
	await page.route(staffingRoute, (route) =>
		route.fulfill({ status: 409, contentType: 'application/problem+json', body: JSON.stringify({ status: 409, title: 'Conflict' }) })
	);
	await page.reload();
	await expect(page.getByTestId('staffing-refused')).toContainText('more arrivals lanes than one answer holds (100)');
	await expect(page.getByTestId('staffing-summary')).toHaveCount(0);

	await page.unroute(staffingRoute);
	await page.route(staffingRoute, (route) => route.fulfill({ status: 500, contentType: 'application/problem+json', body: '{"status":500}' }));
	await page.reload();
	await expect(page.getByTestId('staffing-failed')).toContainText('could not be read');
	allowStatuses(guards, 409, 500);
	await guards.expectClean();
});

/** Elements and attributes a successful injection would create. */
const injectedSelectors = ['img[src="x"]', 'svg[onload]', 'iframe', '[onerror]', '[onload]', 'a[href^="javascript:"]'];

test('CWE-79: markup in zone names, lane codes, reasons, bounds and sources is text, never markup or a message key', async ({ page }) => {
	const guards = await guardPage(page);
	const [script, image, svg, link, frame, template] = xssPayloads;
	const hostile: RoutedLane[] = [
		routedLane(script, 'CIT', { throughputSource: image }, { bound: svg }),
		routedLane(image, svg, { gap: 'NoQueueLength' }, { recommended: null, bound: 'None', waitRecommendedMinutes: null, waitOpenNowMinutes: null, gap: frame }),
		routedLane(svg, 'VIS', {}, { recommended: null, bound: 'None', waitRecommendedMinutes: null, waitOpenNowMinutes: null, gap: 'toString' }),
		{ ...routedLane(link, 'CRW'), gap: template, intervals: [] },
		routedLane(template, 'RES', { openNow: 1 }, { bound: 'constructor' })
	];
	// Interval 1 of the first lane: a reason made of markup.
	hostile[0].intervals[1] = { ...hostile[0].intervals[1], recommended: null, bound: 'None', waitRecommendedMinutes: null, waitOpenNowMinutes: null, gap: script };
	await page.route(staffingRoute, (route) => route.fulfill({ json: routedAnswer(hostile) }));
	await signInThroughUi(page, accounts().webBorder);
	await openImmigration(page);
	const panel = page.getByTestId('staffing');
	await expect(panel).toHaveAttribute('data-state', 'ready');
	for (const toggle of await panel.getByTestId('staffing-toggle').all())
		if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
	await expect(panel.locator('[data-testid="staffing-toggle"][aria-expanded="false"]')).toHaveCount(0);

	const row = (index: number) => panel.getByTestId('staffing-summary-row').nth(index);
	const zones = await panel.getByTestId('staffing-zone').allTextContents();
	expect(zones.sort(), 'zone names shown as they came').toEqual([script, image, svg, link, template].sort());
	// Sorted by lane order: CIT, RES, VIS, CRW, then the unknown category as it came.
	await expect(row(0).getByTestId('staffing-zone')).toHaveText(script);
	await expect(row(1).getByTestId('staffing-difference')).toHaveText('Short by 3');
	await expect(row(2).getByTestId('staffing-reason')).toHaveText('toString');
	await expect(row(3).getByTestId('staffing-reason')).toHaveText(template);
	await expect(row(4).locator('th')).toHaveText(`${svg} ${image}`);
	await expect(row(4).getByTestId('staffing-reason')).toHaveText(frame);

	const first = panel.locator('[data-testid="staffing-lane"]').first();
	await expect(first.getByTestId('staffing-row').first().getByTestId('staffing-bound')).toHaveText(svg);
	await expect(first.getByTestId('staffing-row').nth(1).getByTestId('staffing-why')).toHaveText(`Why none: ${script}`);
	await expect(first.getByTestId('staffing-cycle')).toHaveText(`1.6 min per passenger at a desk (${image}).`);
	await expect(panel.locator('[data-testid="staffing-lane"]').nth(1).getByTestId('staffing-row').first().getByTestId('staffing-bound')).toHaveText('constructor');

	for (const selector of injectedSelectors) await expect(page.locator(selector), selector).toHaveCount(0);
	expect(await page.locator('script:not([src])').count(), 'only the SvelteKit bootstrap script is inline').toBe(1);
	await guards.expectClean();
});
