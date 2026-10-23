import { expect, test, type Page } from '@playwright/test';
import { accounts, call, signIn, unusedTotpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { provisionLoadSite, secondFactorSignIn } from '../support/load-site';
import { videoFixture } from '../support/media';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-142a: the advertisement library on the Passenger displays screen, at a site of this spec's own whose policy allows
// advertising. A duty manager uploads an image with an advertiser, a campaign and a file name carrying markup (all shown as
// text), sees it as a draft
// with its preview and no approve button on its own upload; a second duty manager, signed in with a second factor,
// approves it and it plays; a change sends it back to draft after a warning; retiring asks on the page. Too large a file
// and a refused file are explained on the form. The library is not on the screen of a site whose policy is off, and the
// screen mirrors in Arabic. ARV-142b: a video shows its length, loads its preview only when asked and plays it from an
// object URL (media-src blob:) without a CSP violation. The screen opens on DMO; its answer, held until the chosen site is
// shown and let through last, never replaces the chosen site's policy or library (the race behind the 2026-10-21 failure).
// An administrator who changes site keeps no policy form begun at DMO, and DMO's zones, answered last, are not offered.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
const advertiser = `${xssPayloads[1]} Gulf Air`.slice(0, 120);
let site: string;
/** DMO's published zone profile, whose answer a test holds back. */
let dmoProfile: string;

async function openDisplays(page: Page, siteCode: string): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Passenger displays');
	await page.locator('#displays-site').selectOption(siteCode);
}

function row(page: Page, campaign: string) {
	return page.getByTestId('media-row').filter({
		has: page.getByTestId('media-campaign-text').getByText(campaign, { exact: true })
	});
}

test.beforeAll(async () => {
	const admin = await secondFactorSignIn(accounts().mediaWebAdmin);
	site = (await provisionLoadSite(accounts().mediaWebAdmin, 'AW', 'Load Zone', admin)).site;
	const policyUrl = `${hosts.main}/api/v1/admin/sites/${site}/advertising-policy`;
	const { publishedProfileId } = await (await call('GET', policyUrl, { token: admin })).json();
	const allowed = await call('PUT', policyUrl, {
		token: admin,
		data: {
			allowed: true,
			excludedZones: [],
			reason: 'Airport operator letter AO-WEB-142a, commercial team',
			reviewedProfileId: publishedProfileId
		}
	});
	await expectStatus(allowed, 200);
	const dmoProfiles: { id: string; status: string }[] = await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles?siteCode=DMO`, { token: admin })).json();
	const dmoPublished = dmoProfiles.find((profile) => profile.status === 'Published');
	expect(dmoPublished, 'DMO has a published zone profile').toBeTruthy();
	dmoProfile = dmoPublished!.id;
});

test('a duty manager uploads an image as a draft; the markup in its details shows as text and the uploader is not offered approval', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().mediaWebManager);
	await openDisplays(page, site);

	const library = page.getByTestId('advertising-library');
	await expect(library.getByRole('heading', { level: 2 })).toHaveText('Advertisement library');
	await expect(library.getByTestId('media-empty')).toBeVisible();
	await library.getByTestId('media-file').setInputFiles({ name: 'winter.png', mimeType: 'image/png', buffer: png });
	await library.getByTestId('media-advertiser').fill(advertiser);
	await library.getByTestId('media-campaign').fill('Winter sun');
	await library.getByTestId('media-upload').click();

	const uploaded = row(page, 'Winter sun');
	await expect(uploaded).toHaveAttribute('data-status', 'Draft');
	await expect(uploaded.getByTestId('media-advertiser-text')).toHaveText(advertiser.trim());
	await expect(uploaded).toContainText('Draft, waiting for approval');
	await expect(uploaded).toContainText('8 by 4 pixels');
	await expect(uploaded.getByTestId('media-preview')).toHaveAttribute('src', /^blob:/);
	await expect(uploaded.getByTestId('media-approve'), 'the uploader is not offered approval').toHaveCount(0);

	// The campaign and the file name are shown back too: markup in both stays text.
	const campaign = xssPayloads[2];
	const fileName = '<img src=x onerror=alert(1)>.png';
	await library.getByTestId('media-file').setInputFiles({ name: fileName, mimeType: 'image/png', buffer: png });
	await library.getByTestId('media-advertiser').fill('Gulf Air');
	await library.getByTestId('media-campaign').fill(campaign);
	await library.getByTestId('media-upload').click();
	const probed = row(page, campaign);
	await expect(probed).toHaveAttribute('data-status', 'Draft');
	await expect(probed.getByTestId('media-campaign-text')).toHaveText(campaign);
	await expect(probed).toContainText(fileName);
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
});

test('too large a file and a refused file are explained on the form, without an echo', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().mediaWebManager);
	await openDisplays(page, site);
	const library = page.getByTestId('advertising-library');

	await library.getByTestId('media-file').setInputFiles({
		name: 'large.png',
		mimeType: 'image/png',
		buffer: Buffer.concat([png, Buffer.alloc(10 * 1024 * 1024)])
	});
	await expect(library.getByTestId('media-problems')).toHaveText('The image is larger than 10 MB.');

	await library.getByTestId('media-file').setInputFiles({
		name: 'poster.png',
		mimeType: 'image/png',
		buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"><script>alert(2)</script></svg>')
	});
	await library.getByTestId('media-advertiser').fill('Gulf Air');
	await library.getByTestId('media-campaign').fill('Refused poster');
	await library.getByTestId('media-upload').click();
	await expect(library.getByTestId('media-problems')).toContainText('is not a PNG, JPEG or WebP image');
	await expect(library.getByTestId('media-problems')).not.toContainText('<script');
	await expect(row(page, 'Refused poster')).toHaveCount(0);
	await expect(page.locator(injected)).toHaveCount(0);
	allowStatuses(guards, 400);
	await guards.expectClean();
});

test('a duty manager uploads a video; it shows its length, loads only when asked and plays from an object URL; Arabic too', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().mediaWebManager);
	await openDisplays(page, site);
	const library = page.getByTestId('advertising-library');

	await library.getByTestId('media-file').setInputFiles({ name: 'spring.webm', mimeType: 'video/webm', buffer: videoFixture('vp9-opus.webm') });
	await library.getByTestId('media-advertiser').fill('Gulf Air');
	await library.getByTestId('media-campaign').fill('Spring video');
	await library.getByTestId('media-upload').click();

	const video = row(page, 'Spring video');
	await expect(video).toHaveAttribute('data-status', 'Draft');
	await expect(video).toContainText('160 by 90 pixels');
	await expect(video.getByTestId('media-length')).toHaveText('2 seconds');
	await expect(video.getByTestId('media-video'), 'a video loads only when asked').toHaveCount(0);
	await video.getByTestId('media-load-video').click();
	const player = video.getByTestId('media-video');
	await expect(player).toHaveAttribute('src', /^blob:/);
	await expect
		.poll(() => player.evaluate((element: HTMLVideoElement) => element.readyState), { message: 'its metadata is read from the object URL' })
		.toBeGreaterThanOrEqual(1);
	expect(await player.evaluate((element: HTMLVideoElement) => Math.round(element.duration))).toBe(2);

	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(video.getByTestId('media-length')).toHaveText('2 ثانية');
	await expect(player).toHaveAttribute('aria-label', 'إعلان فيديو Gulf Air: Spring video');
	await guards.expectClean();
});

test('a second duty manager with a second factor approves the draft and it plays; its own upload offers no approval', async ({ page }) => {
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	const approver = accounts().mediaWebApprover;
	await signInThroughUi(page, approver, await unusedTotpCode(approver.totpSecret!));
	await openDisplays(page, site);

	const library = page.getByTestId('advertising-library');
	await library.getByTestId('media-file').setInputFiles({ name: 'own.png', mimeType: 'image/png', buffer: png });
	await library.getByTestId('media-advertiser').fill('Gulf Air');
	await library.getByTestId('media-campaign').fill('Approver own');
	await library.getByTestId('media-upload').click();
	await expect(row(page, 'Approver own')).toHaveAttribute('data-status', 'Draft');
	await expect(row(page, 'Approver own').getByTestId('media-approve'), 'its own upload').toHaveCount(0);

	const draft = row(page, 'Winter sun');
	await draft.getByTestId('media-approve').click();
	await expect(draft).toHaveAttribute('data-status', 'Approved');
	await expect(draft).toContainText('Playing at the site today');
	await expect(draft).toContainText(`approved by ${approver.userName}`);
	// The sign-in's password step answers 401 (a second factor is asked for).
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('a change sends an approved item back to draft after a warning; retiring asks on the page; Arabic mirrors', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().mediaWebManager);
	await openDisplays(page, site);

	const approved = row(page, 'Winter sun');
	await expect(approved).toHaveAttribute('data-status', 'Approved');
	await approved.getByTestId('media-edit').click();
	await expect(approved.getByTestId('media-edit-warning')).toContainText('sends it back to draft');
	await approved.getByTestId('media-edit-campaign').fill('Winter sun extended');
	await approved.getByTestId('media-save').click();
	const changed = row(page, 'Winter sun extended');
	await expect(changed).toHaveAttribute('data-status', 'Draft');
	await expect(changed).not.toContainText('Playing at the site today');

	await changed.getByTestId('media-retire').click();
	await changed.getByTestId('confirm-media-retire').click();
	await expect(changed).toHaveAttribute('data-status', 'Retired');
	await expect(changed.getByTestId('media-edit')).toHaveCount(0);
	await expect(changed.getByTestId('media-retire')).toHaveCount(0);

	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByTestId('advertising-library').getByRole('heading', { level: 2 })).toHaveText('مكتبة الإعلانات');
	await expect(changed).toContainText('متوقف نهائيا');
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
});

test('the library is not on the screen of a site whose advertising policy is off', async ({ page }) => {
	const guards = await guardPage(page);
	const manager = (await signIn(accounts().mediaWebManager)).accessToken;
	expect((await call('GET', `${hosts.main}/api/v1/admin/sites/DMO/advertising/media`, { token: manager })).status(), 'the API refuses it too').toBe(403);
	await signInThroughUi(page, accounts().mediaWebManager);
	await openDisplays(page, 'DMO');
	await expect(page.getByTestId('advertising-policy')).toHaveAttribute('data-allowed', 'false');
	await expect(page.getByTestId('advertising-library')).toHaveCount(0);
	await guards.expectClean();
});

test('a late answer for the site the screen opens on never replaces the chosen site and its library', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().mediaWebManager);

	// The screen opens on DMO (advertising off) and asks for its policy; that answer is held until the chosen site is shown.
	let heldSeen = () => {};
	const held = new Promise<void>((resolve) => (heldSeen = resolve));
	let release = () => {};
	const released = new Promise<void>((resolve) => (release = resolve));
	let delivered = () => {};
	const lateDelivered = new Promise<void>((resolve) => (delivered = resolve));
	await page.route(
		'**/api/v1/admin/sites/DMO/advertising-policy',
		async (route) => {
			heldSeen();
			await released;
			await route.fulfill({ response: await route.fetch() });
			delivered();
		},
		{ times: 1 }
	);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Passenger displays');
	await held;
	await page.locator('#displays-site').selectOption(site);
	const policy = page.getByTestId('advertising-policy');
	await expect(policy).toHaveAttribute('data-allowed', 'true');
	await expect(page.getByTestId('advertising-library').getByRole('heading', { level: 2 })).toHaveText('Advertisement library');

	// DMO's answer arrives last; the page has a second to show it and still shows the chosen site's policy and library.
	release();
	await lateDelivered;
	await page.waitForTimeout(1_000);
	await expect(page.locator('#displays-site')).toHaveValue(site);
	await expect(policy, 'the chosen site policy is still shown').toHaveAttribute('data-allowed', 'true');
	await expect(page.getByTestId('advertising-library').getByTestId('media-file')).toBeAttached();
	await guards.expectClean();
});

test('an administrator who changes site keeps no policy form, policy or zones of the site the screen opened on', async ({ page }) => {
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	const admin = accounts().mediaWebAdmin;
	await signInThroughUi(page, admin, await unusedTotpCode(admin.totpSecret!));

	// DMO's displays and policy are on the screen; its zone profile is held, so its load is still running. Only the displays
	// screen's request is held: the live operations screen the sign-in lands on reads the same profile.
	let holding = false;
	let heldSeen = () => {};
	const held = new Promise<void>((resolve) => (heldSeen = resolve));
	let release = () => {};
	const released = new Promise<void>((resolve) => (release = resolve));
	let delivered = () => {};
	const lateDelivered = new Promise<void>((resolve) => (delivered = resolve));
	await page.route(`**/api/v1/admin/zone-profiles/${dmoProfile}`, async (route) => {
		if (!holding || !new URL(route.request().frame().url()).pathname.startsWith('/displays')) return route.fallback();
		holding = false;
		heldSeen();
		await released;
		await route.fulfill({ response: await route.fetch() });
		delivered();
	});
	holding = true;
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Passenger displays');
	await held;
	const policy = page.getByTestId('advertising-policy');
	await expect(policy).toHaveAttribute('data-allowed', 'false');
	await policy.getByTestId('edit-advertising').click();
	await policy.getByTestId('advertising-allowed').check();
	await policy.getByTestId('advertising-reason-input').fill('Airport operator letter AO-WEB-SWITCH');

	// The chosen site's policy is shown without the form begun at DMO, which could otherwise be saved to this site.
	await page.locator('#displays-site').selectOption(site);
	await expect(page.getByTestId('add-display'), 'the chosen site is loaded').toBeEnabled();
	await expect(policy).toHaveAttribute('data-allowed', 'true');
	await expect(policy.getByTestId('advertising-form')).toHaveCount(0);

	// DMO's zones arrive last; the display form offers the chosen site's queue zone only.
	release();
	await lateDelivered;
	await page.waitForTimeout(1_000);
	await page.getByTestId('add-display').click();
	const entry = page.getByTestId('display-form').getByTestId('display-entry').first();
	await expect(entry.getByLabel('Queue zone').locator('option')).toHaveText(['Load Zone']);
	await expect(policy, 'the chosen site policy is still shown').toHaveAttribute('data-allowed', 'true');
	// The sign-in's password step answers 401 (a second factor is asked for).
	allowStatuses(guards, 401);
	await guards.expectClean();
});
