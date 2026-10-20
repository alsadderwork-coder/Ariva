import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, unusedTotpCode, type Account } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, earlyInStep, fillSecret, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-145: the advertising policy panel of the Passenger displays screen. A system administrator allows advertising at a
// site of this spec's own (created here, without zones, so no other spec's displays or boards depend on it) after a fresh
// second factor, with a reason carrying markup that shows as text; a duty manager sees the policy without a change button,
// and the screen mirrors in Arabic. The display form offers Advertising only where the site's policy allows every zone the
// display shows (the policy read is answered by the test for DMO, whose own policy stays off; the API refuses it anyway,
// api/advertising-policy.spec.ts).

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const site = `AD${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
const reason = `${xssPayloads[1]} airport operator letter AO-WEB-145`;

/**
 * Signs in with the second factor 20 minutes old, so saving the policy asks for a fresh code. Each code is one this
 * account has not used (a retry of the serial group signs in again, and a code counts once).
 */
async function signInAged(page: Page, account: Account): Promise<void> {
	await ownAddress(page);
	await page.route('**/api/auth/login', async (route) => {
		const response = await route.fetch();
		if (response.status() !== 200) return route.fulfill({ response });
		const body = await response.json();
		const claims = claimsOf(body.accessToken);
		body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
		return route.fulfill({ response, json: body });
	});
	await page.goto('/login');
	await fillSignIn(page, account, await unusedTotpCode(account.totpSecret!));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
}

async function openDisplays(page: Page, siteCode: string): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Passenger displays');
	await page.locator('#displays-site').selectOption(siteCode);
}

/**
 * Answers DMO's advertisement library with an empty one: a policy this spec mocks as allowing advertising shows the library
 * (ARV-142a), which the API refuses with 403 for DMO, whose own policy stays off.
 */
async function emptyLibrary(page: Page): Promise<void> {
	await page.route('**/api/v1/admin/sites/DMO/advertising/media', (route) => route.fulfill({ json: [] }));
}

test.beforeAll(async () => {
	const admin = (await signIn(accounts().webAdmin)).accessToken;
	const created = await call('POST', `${hosts.main}/api/v1/admin/sites`, { token: admin, data: { code: site, name: 'E2E advertising screen' } });
	await expectStatus(created, 201);
});

test('a system administrator allows advertising with a reason after a second factor; the reason shows as text', async ({ page }) => {
	test.skip(!developmentSigningKey(), 'needs the run key to age the token');
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	const account = accounts().advertisingWebAdmin;
	await earlyInStep();
	await signInAged(page, account);
	await openDisplays(page, site);

	const panel = page.getByTestId('advertising-policy');
	await expect(panel).toHaveAttribute('data-allowed', 'false');
	await expect(panel).toContainText('Advertising off');
	await panel.getByTestId('edit-advertising').click();
	const form = panel.getByTestId('advertising-form');
	await expect(form).toContainText('The site has no published queue zones.');
	await expect(form.getByTestId('advertising-profile-version')).toHaveText('The site has no published zone profile, so no display can show advertising yet.');
	await form.getByTestId('advertising-allowed').check();
	await expect(form.getByTestId('advertising-reason-input')).toHaveAttribute('minlength', '10');
	await form.getByTestId('advertising-reason-input').fill(reason);
	await form.getByTestId('save-advertising').click();

	// The second factor is 20 minutes old, so a fresh code is asked before the change is sent again.
	const dialog = page.getByTestId('step-up-dialog');
	await expect(dialog).toBeVisible();
	await fillSecret(page.locator('#step-up-code'), await unusedTotpCode(account.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();
	await expect(panel).toHaveAttribute('data-allowed', 'true');
	await expect(panel).toContainText('Advertising allowed');
	await expect(panel.getByTestId('advertising-reason')).toContainText(reason);
	await expect(page.locator(injected)).toHaveCount(0);
	allowStatuses(guards, 401);
	await guards.expectClean();

	// The screen in Arabic mirrors, and the reason stays text.
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(panel.getByRole('heading', { level: 2 })).toHaveText('سياسة الإعلانات');
	await expect(panel).toContainText('الإعلانات مسموحة');
	await expect(page.locator(injected)).toHaveCount(0);
});

test('a duty manager sees the policy and its reason but cannot change it', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().advertisingManager);
	await openDisplays(page, site);
	const panel = page.getByTestId('advertising-policy');
	await expect(panel).toHaveAttribute('data-allowed', 'true');
	await expect(panel.getByTestId('advertising-reason')).toContainText('AO-WEB-145');
	await expect(panel.getByTestId('edit-advertising')).toHaveCount(0);
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
});

test('the display form offers advertising only where the policy allows every zone the display shows', async ({ page }) => {
	const guards = await guardPage(page);
	// DMO's own policy stays off; the screen is shown one that allows advertising except in D-CIT.
	await emptyLibrary(page);
	await page.route('**/api/v1/admin/sites/DMO/advertising-policy', (route) =>
		route.fulfill({
			json: {
				siteCode: 'DMO', allowed: true, excludedZones: ['D-CIT'], reason: 'Airport operator letter AO-WEB-146', changedOn: new Date().toISOString(),
				unpublishedZones: [], displaysFallingBack: [], profileChanged: false, publishedProfileId: null, publishedProfileVersion: 12,
				confirmedProfileVersion: 12, publishedQueueZones: ['D-CIT', 'D-RES', 'D-VIS']
			}
		})
	);
	await signInThroughUi(page, accounts().advertisingManager);
	await openDisplays(page, 'DMO');
	await expect(page.getByTestId('advertising-excluded')).toHaveText('Off in these zones: D-CIT');

	await page.getByTestId('add-display').click();
	const form = page.getByTestId('display-form');
	await form.getByLabel('Layout', { exact: true }).selectOption('Split');
	const advertising = form.locator('#display-second option[value="Advertising"]');
	const entry = form.getByTestId('display-entry').first();
	await entry.getByLabel('Queue zone').selectOption('D-VIS');
	await expect(advertising).toBeEnabled();
	await expect(form.getByTestId('display-second-hint')).toHaveAttribute('data-allowed', 'true');
	await entry.getByLabel('Queue zone').selectOption('D-CIT');
	await expect(advertising).toBeDisabled();
	await expect(form.getByTestId('display-second-hint')).toHaveText("Advertising is off for this site or for one of these zones (the site's advertising policy).");
	await guards.expectClean();
});

for (const [label, answer, warning] of [
	['a newer zone profile', { profileChanged: true, publishedProfileVersion: 13, confirmedProfileVersion: 12 }, 'advertising-profile-changed'],
	['an excluded zone no longer published', { unpublishedZones: ['D-RES'], excludedZones: ['D-RES'], publishedQueueZones: ['D-CIT', 'D-VIS'] }, 'advertising-unpublished']
] as const) {
	test(`with ${label} the panel warns, in English and Arabic, and the form does not offer advertising`, async ({ page }) => {
		const guards = await guardPage(page);
		// DMO's own policy stays off; the screen is shown one the server would refuse to advertise under.
		await emptyLibrary(page);
		await page.route('**/api/v1/admin/sites/DMO/advertising-policy', (route) =>
			route.fulfill({
				json: Object.assign(
					{
						siteCode: 'DMO', allowed: true, excludedZones: [], reason: 'Airport operator letter AO-WEB-147', changedOn: new Date().toISOString(),
						unpublishedZones: [], displaysFallingBack: [], profileChanged: false, publishedProfileId: null, publishedProfileVersion: 12,
						confirmedProfileVersion: 12, publishedQueueZones: ['D-CIT', 'D-RES', 'D-VIS']
					},
					answer
				)
			})
		);
		await signInThroughUi(page, accounts().advertisingManager);
		await openDisplays(page, 'DMO');
		await expect(page.getByTestId(warning)).toBeVisible();
		if (warning === 'advertising-profile-changed')
			await expect(page.getByTestId(warning)).toContainText('Zone profile version 13 has been published since this policy was saved against version 12');

		await page.getByTestId('add-display').click();
		const form = page.getByTestId('display-form');
		await form.getByLabel('Layout', { exact: true }).selectOption('Split');
		await form.getByTestId('display-entry').first().getByLabel('Queue zone').selectOption('D-VIS');
		await expect(form.locator('#display-second option[value="Advertising"]')).toBeDisabled();
		await expect(form.getByTestId('display-second-hint')).toHaveAttribute('data-allowed', 'false');

		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(page.getByTestId(warning)).toBeVisible();
		await expect(page.getByTestId(warning)).toContainText(warning === 'advertising-profile-changed' ? 'النسخة 13' : 'D-RES');
		await expect(form.locator('#display-second option[value="Advertising"]')).toBeDisabled();
		await guards.expectClean();
	});
}

test('a save refused because another profile was published shows the new version and its zones, and the next save confirms it', async ({ page }) => {
	// Round 4 of the security review: the zones offered come with the policy, from the same profile the save confirms.
	const guards = await guardPage(page);
	const v12 = '0199a000-0000-7000-8000-00000000a012';
	const v13 = '0199a000-0000-7000-8000-00000000a013';
	let published = { id: v12, version: 12, zones: ['D-CIT', 'D-RES', 'D-VIS'] };
	const sent: Record<string, unknown>[] = [];
	// What a save kept, read back by the page's refresh after it, as the API does.
	let saved: Record<string, unknown> | null = null;
	const policy = () => ({
		siteCode: 'DMO', allowed: false, excludedZones: [], reason: null, changedOn: null, unpublishedZones: [], displaysFallingBack: [], profileChanged: false,
		publishedProfileId: published.id, publishedProfileVersion: published.version, confirmedProfileVersion: null, publishedQueueZones: published.zones
	});
	await emptyLibrary(page);
	await page.route('**/api/v1/admin/sites/DMO/advertising-policy', async (route) => {
		if (route.request().method() === 'GET') return route.fulfill({ json: saved ?? policy() });
		const body = route.request().postDataJSON() as Record<string, unknown>;
		sent.push(body);
		if (body.reviewedProfileId !== published.id)
			return route.fulfill({ status: 409, contentType: 'application/problem+json', json: { status: 409, title: 'Conflict', detail: 'stale' } });
		saved = { ...policy(), allowed: true, reason: body.reason, excludedZones: body.excludedZones, confirmedProfileVersion: published.version };
		return route.fulfill({ json: saved });
	});
	await signInThroughUi(page, accounts().webAdmin);
	await openDisplays(page, 'DMO');
	const panel = page.getByTestId('advertising-policy');
	await panel.getByTestId('edit-advertising').click();
	const form = panel.getByTestId('advertising-form');
	await expect(form.getByTestId('advertising-profile-version')).toContainText('zone profile version 12');
	await expect(form.locator('[data-testid="advertising-zone"][data-zone="D-NEW"]')).toHaveCount(0);

	// A duty manager publishes version 13, with a new zone over the hall, while the administrator reviews version 12.
	published = { id: v13, version: 13, zones: ['D-CIT', 'D-NEW', 'D-RES', 'D-VIS'] };
	await form.getByTestId('advertising-allowed').check();
	await form.getByTestId('advertising-reason-input').fill('Airport operator letter AO-WEB-148');
	await form.getByTestId('save-advertising').click();
	await expect(form.getByTestId('advertising-problems')).toBeVisible();
	expect(sent[0].reviewedProfileId, 'the first save names the version reviewed').toBe(v12);
	await expect(form.getByTestId('advertising-profile-version')).toContainText('zone profile version 13');
	await form.locator('[data-testid="advertising-zone"][data-zone="D-NEW"]').check();
	await form.getByTestId('save-advertising').click();
	await expect(panel).toHaveAttribute('data-allowed', 'true');
	expect(sent[1]).toMatchObject({ reviewedProfileId: v13, excludedZones: ['D-NEW'] });
	allowStatuses(guards, 409);
	await guards.expectClean();
});
