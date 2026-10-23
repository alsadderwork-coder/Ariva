import { expect, test, type Page } from '@playwright/test';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-241: a screen with a site selector loads its first site (DMO) as it opens; when another site is chosen before that
// load answered, the late answer must leave the chosen site's data on the screen. Each test holds one request of DMO's load,
// chooses an empty site (E2SW, made by the seed for webSwitchObserver), waits for that site's load, then lets DMO's answer
// through: DMO's rows must never appear under E2SW. The Passenger displays screen has its own test (advertising-media.spec.ts)
// and Validation capture its own (validation-capture.spec.ts).

test.skip(!databaseAvailable, 'the screens need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

const site = 'E2SW';

test.beforeAll(async () => {
	const token = (await signIn(accounts().switchAdmin)).accessToken;
	const sites = await call('GET', `${hosts.main}/api/v1/sites`, { token });
	await expectStatus(sites, 200);
	const codes = ((await sites.json()) as { code: string }[]).map((s) => s.code);
	if (!codes.includes(site)) {
		const created = await call('POST', `${hosts.main}/api/v1/admin/sites`, { token, data: { code: site, name: 'E2E site switch' } });
		await expectStatus(created, 201);
	}
});

interface Held {
	asked: () => number;
	release: () => void;
}

/** Holds every request matching <paramref name="pattern"/> until released, counting them. */
async function hold(page: Page, pattern: RegExp): Promise<Held> {
	let release = (): void => {};
	const held = new Promise<void>((resolve) => (release = resolve));
	let asked = 0;
	await page.route(pattern, async (route) => {
		asked++;
		await held;
		await route.continue();
	});
	return { asked: () => asked, release: () => release() };
}

/** Lets DMO's held answer through and gives the page's handlers a moment to run on it. */
async function answerLate(page: Page, held: Held, pattern: RegExp): Promise<void> {
	const late = page.waitForResponse(pattern);
	held.release();
	await late;
	await page.waitForTimeout(500);
}

async function open(page: Page, link: string): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: link, exact: true }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(link);
}

test('Live operations keeps the chosen site when the first site answers last', async ({ page }) => {
	const guards = await guardPage(page);
	const dmo = /\/api\/v1\/admin\/zone-profiles\?siteCode=DMO$/;
	// The screen the sign-in lands on: DMO's load starts with it.
	const held = await hold(page, dmo);
	await signInThroughUi(page, accounts().switchAdmin);
	await expect.poll(held.asked, { message: 'the screen asked for its first site' }).toBe(1);
	const chosen = page.waitForResponse(new RegExp(`/api/v1/admin/zone-profiles\\?siteCode=${site}$`));
	await page.locator('#live-site').selectOption(site);
	await chosen;
	await expect(page.getByTestId('zone-row')).toHaveCount(0);

	await answerLate(page, held, dmo);

	await expect(page.locator('#live-site')).toHaveValue(site);
	await expect(page.getByTestId('zone-row'), "DMO's queue zones never show under the chosen site").toHaveCount(0);
	// A site without zones or plans answers some reads with 404.
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('Devices keeps the chosen site when the first site answers last', async ({ page }) => {
	const guards = await guardPage(page);
	// DMO's levels: the E2E database has no device at DMO, so its device list would look like an empty site's. Live operations,
	// where the sign-in lands, reads them too, so the hold starts once that read has answered.
	const dmo = /\/api\/v1\/admin\/levels\?siteCode=DMO&/;
	const home = page.waitForResponse(dmo);
	await signInThroughUi(page, accounts().switchAdmin);
	await home;
	const held = await hold(page, dmo);
	await open(page, 'Devices');
	await expect.poll(held.asked, { message: 'the screen asked for its first site' }).toBe(1);
	const levels = page.locator('section[aria-labelledby="coverage-title"] select option');
	// The chosen site's devices are read after its levels: once they answered, its load has set everything DMO's could.
	const chosen = page.waitForResponse(new RegExp(`/api/v1/admin/devices\\?siteCode=${site}&`));
	await page.locator('#devices-site').selectOption(site);
	await chosen;
	await expect(page.getByText('No devices at this site yet')).toBeVisible();
	await expect(levels).toHaveCount(0);

	await answerLate(page, held, dmo);

	await expect(page.locator('#devices-site')).toHaveValue(site);
	await expect(levels, "DMO's levels never show under the chosen site").toHaveCount(0);
	await expect(page.getByTestId('device-row')).toHaveCount(0);
	await expect(page.getByText('No devices at this site yet')).toBeVisible();
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('Zones keeps the chosen site when the first site answers last', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().switchAdmin);
	// Only this screen reads the desks; the site's load waits for them with its levels and plans.
	const dmo = /\/api\/v1\/admin\/desks\?siteCode=DMO&/;
	const held = await hold(page, dmo);
	await open(page, 'Zones');
	await expect.poll(held.asked, { message: 'the screen asked for its first site' }).toBe(1);
	const chosen = page.waitForResponse(new RegExp(`/api/v1/admin/zone-profiles\\?siteCode=${site}$`));
	await page.locator('#zones-site').selectOption(site);
	await chosen;
	await expect(page.locator('#zones-level option')).toHaveCount(0);

	await answerLate(page, held, dmo);

	await expect(page.locator('#zones-site')).toHaveValue(site);
	await expect(page.locator('#zones-level option'), "DMO's levels never show under the chosen site").toHaveCount(0);
	await expect(page.getByTestId('zones-list')).toHaveCount(0);
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('Display instructions keeps the chosen site when the first site answers last', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().switchAdmin);
	const dmo = /\/api\/v1\/admin\/display-slides\?siteCode=DMO$/;
	const held = await hold(page, dmo);
	await open(page, 'Display instructions');
	await expect.poll(held.asked, { message: 'the screen asked for its first site' }).toBe(1);
	const chosen = page.waitForResponse(new RegExp(`/api/v1/admin/display-slides\\?siteCode=${site}$`));
	await page.locator('#slides-site').selectOption(site);
	await chosen;
	await expect(page.getByText('No instruction slides at this site yet')).toBeVisible();

	await answerLate(page, held, dmo);

	await expect(page.locator('#slides-site')).toHaveValue(site);
	await expect(page.getByTestId('slide-row'), "DMO's slides never show under the chosen site").toHaveCount(0);
	await expect(page.getByText('No instruction slides at this site yet')).toBeVisible();
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('Display instructions keeps the chosen site when a reorder at the first site answers last', async ({ page }) => {
	// Security review round 1, M2: the reorder is held, then answered with DMO's slides as the server would, never sent, so
	// DMO's order does not change for the other specs.
	const guards = await guardPage(page);
	const token = (await signIn(accounts().switchAdmin)).accessToken;
	const listed = await call('GET', `${hosts.main}/api/v1/admin/display-slides?siteCode=DMO`, { token });
	await expectStatus(listed, 200);
	const dmoSlides = (await listed.json()) as unknown[];
	expect(dmoSlides.length, 'DMO has slides to move').toBeGreaterThan(1);
	await signInThroughUi(page, accounts().switchAdmin);
	await open(page, 'Display instructions');
	await expect(page.getByTestId('slide-row').first()).toBeVisible();
	let release = (): void => {};
	const held = new Promise<void>((resolve) => (release = resolve));
	let sent = 0;
	await page.route(/\/api\/v1\/admin\/display-slides\/order\?siteCode=DMO$/, async (route) => {
		sent++;
		await held;
		await route.fulfill({ status: 200, json: dmoSlides });
	});
	await page.getByRole('button', { name: 'Move slide 1 down' }).click();
	await expect.poll(() => sent, { message: 'the reorder was sent' }).toBe(1);
	const chosen = page.waitForResponse(new RegExp(`/api/v1/admin/display-slides\\?siteCode=${site}$`));
	await page.locator('#slides-site').selectOption(site);
	await chosen;
	await expect(page.getByText('No instruction slides at this site yet')).toBeVisible();

	release();
	await page.waitForTimeout(500);

	await expect(page.locator('#slides-site')).toHaveValue(site);
	await expect(page.getByTestId('slide-row'), "DMO's slides never show under the chosen site").toHaveCount(0);
	await expect(page.getByText('No instruction slides at this site yet')).toBeVisible();
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('Alert rules keeps the chosen site when the first site answers last', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().switchAdmin);
	const dmo = /\/api\/v1\/admin\/alert-rules\?siteCode=DMO&pageSize=500$/;
	const held = await hold(page, dmo);
	await open(page, 'Alert rules');
	await expect.poll(held.asked, { message: 'the screen asked for its first site' }).toBe(1);
	const chosen = page.waitForResponse(new RegExp(`/api/v1/admin/alert-rules\\?siteCode=${site}&`));
	await page.locator('#rules-site').selectOption(site);
	await chosen;
	await expect(page.getByText('No alert rules at this site yet')).toBeVisible();

	await answerLate(page, held, dmo);

	await expect(page.locator('#rules-site')).toHaveValue(site);
	await expect(page.getByTestId('rule-row'), "DMO's rules never show under the chosen site").toHaveCount(0);
	await expect(page.getByText('No alert rules at this site yet')).toBeVisible();
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('Immigration keeps the chosen site when the first site answers last', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().switchAdmin);
	const dmo = /\/api\/v1\/sites\/DMO\/immigration$/;
	const held = await hold(page, dmo);
	await open(page, 'Immigration');
	await expect.poll(held.asked, { message: 'the screen asked for its first site' }).toBe(1);
	const chosen = page.waitForResponse(new RegExp(`/api/v1/sites/${site}/immigration$`));
	await page.locator('#immigration-site').selectOption(site);
	await chosen;
	await expect(page.getByText('No lane queues or desks reported for this hall yet.').first()).toBeVisible();

	await answerLate(page, held, dmo);

	await expect(page.locator('#immigration-site')).toHaveValue(site);
	await expect(page.getByTestId('lane-row'), "DMO's lanes never show under the chosen site").toHaveCount(0);
	allowStatuses(guards, 404);
	await guards.expectClean();
});
