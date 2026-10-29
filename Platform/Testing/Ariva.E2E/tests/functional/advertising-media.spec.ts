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
// object URL (media-src blob:) without a CSP violation. ARV-194: Approve is offered only for a draft whose picture (or
// video) the screen loaded, drafts' pictures first however old, and opens it at its full size on the board's background;
// the dialog's own Approve sends the approval, and only once the browser decoded the picture (a video's first frame) there.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
const advertiser = `${xssPayloads[1]} Gulf Air`.slice(0, 120);
let site: string;

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

	// ARV-194: nothing is approved until the dialog's own Approve is pressed; Cancel and Escape send nothing.
	const approvals: string[] = [];
	page.on('request', (request) => {
		if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/approve')) approvals.push(request.url());
	});
	const review = page.getByTestId('media-review-dialog');

	// ARV-194: a video draft offers approval only once it is loaded, and its dialog's Approve once its first frame shows.
	const video = row(page, 'Spring video');
	await expect(video.getByTestId('media-approve')).toHaveCount(0);
	await expect(video.getByTestId('media-approve-unavailable')).toHaveText('Load the video to see it before approving.');
	await video.getByTestId('media-load-video').click();
	await video.getByTestId('media-approve').click();
	await expect(review.getByTestId('media-review-video')).toHaveAttribute('src', /^blob:/);
	await expect(review.getByTestId('media-review-cancel'), 'focus starts on Cancel, not on the video controls before it').toBeFocused();
	await expect(review.getByTestId('media-approve-confirm'), 'once its first frame is decoded').toBeEnabled();
	expect(await review.getByTestId('media-review-video').evaluate((element: HTMLVideoElement) => element.readyState)).toBeGreaterThanOrEqual(2);
	await review.getByTestId('media-review-cancel').click();
	await expect(review).toHaveCount(0);

	// ARV-194: Approve opens the picture at its full size on the board's background, its details as text.
	const draft = row(page, 'Winter sun');
	await draft.getByTestId('media-approve').click();
	await expect(review).toBeVisible();
	await expect(review.getByTestId('media-review-campaign')).toHaveText('Winter sun');
	await expect(review.getByTestId('media-review-advertiser')).toHaveText(advertiser.trim());
	await expect(review.getByTestId('media-review-details')).toContainText('8 by 4 pixels');
	await expect(page.locator(injected)).toHaveCount(0);
	await expect(review.getByTestId('media-review-cancel'), 'focus starts on Cancel').toBeFocused();
	const image = review.getByTestId('media-review-image');
	await expect(image).toHaveAttribute('src', /^blob:/);
	await expect.poll(() => image.evaluate((element: HTMLImageElement) => element.complete && element.naturalWidth > 0)).toBe(true);
	const [shownWidth, naturalWidth] = await image.evaluate((element: HTMLImageElement) => [element.clientWidth, element.naturalWidth]);
	expect(shownWidth, 'shown at its own size, not the list thumbnail').toBe(naturalWidth);
	await expect(image.locator('..'), "the board's background").toHaveCSS('background-color', 'rgb(11, 18, 32)');
	await expect(review.getByTestId('media-approve-confirm')).toBeEnabled();
	await review.getByTestId('media-review-cancel').click();
	await expect(review).toHaveCount(0);
	await draft.getByTestId('media-approve').click();
	await expect(review).toBeVisible();
	await page.keyboard.press('Escape');
	await expect(review).toHaveCount(0);
	expect(approvals, 'Cancel and Escape send nothing').toEqual([]);
	await expect(draft).toHaveAttribute('data-status', 'Draft');
	await draft.getByTestId('media-approve').click();
	await review.getByTestId('media-approve-confirm').click();
	await expect(draft).toHaveAttribute('data-status', 'Approved');
	await expect(draft).toContainText('Playing at the site today');
	await expect(draft).toContainText(`approved by ${approver.userName}`);
	// The sign-in's password step answers 401 (a second factor is asked for).
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('a draft whose picture did not load, or does not decode, offers no approval', async ({ page }) => {
	// ARV-194: the review token proves the details did not change, not that the approver saw the picture; without the
	// picture there is nothing to approve from.
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	const manager = (await signIn(accounts().mediaWebManager)).accessToken;
	async function draftOf(campaign: string, file: Blob = new Blob([png], { type: 'image/png' })): Promise<string> {
		const body = new FormData();
		body.append('file', file, file.type === 'video/webm' ? 'draft.webm' : 'draft.png');
		body.append('advertiser', 'Gulf Air');
		body.append('campaign', campaign);
		body.append('startsOn', new Date().toISOString().slice(0, 10));
		body.append('endsOn', new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10));
		const created = await call('POST', `${hosts.main}/api/v1/admin/sites/${site}/advertising/media`, { token: manager, form: body });
		await expectStatus(created, 201);
		return ((await created.json()) as { id: string }).id;
	}
	const blockedId = await draftOf('Blocked preview');
	const brokenId = await draftOf('Broken picture');
	const brokenVideoId = await draftOf('Broken video', new Blob([videoFixture('vp9-opus.webm')], { type: 'video/webm' }));
	await page.route(`**/api/v1/admin/sites/${site}/advertising/media/${blockedId}/content`, (route) =>
		route.fulfill({ status: 404, contentType: 'application/problem+json', json: { status: 404, title: 'Not Found' } })
	);
	// Answered as a PNG, but its bytes are not a picture the browser can decode.
	await page.route(`**/api/v1/admin/sites/${site}/advertising/media/${brokenId}/content`, (route) =>
		route.fulfill({ status: 200, contentType: 'image/png', body: Buffer.from('not a picture at all, only bytes') })
	);
	await page.route(`**/api/v1/admin/sites/${site}/advertising/media/${brokenVideoId}/content`, (route) =>
		route.fulfill({ status: 200, contentType: 'video/webm', body: Buffer.from('not a video at all, only bytes') })
	);

	const approver = accounts().mediaWebApprover;
	await signInThroughUi(page, approver, await unusedTotpCode(approver.totpSecret!));
	await openDisplays(page, site);
	const blocked = row(page, 'Blocked preview');
	await expect(blocked).toHaveAttribute('data-status', 'Draft');
	await expect(blocked.getByTestId('media-preview')).toHaveCount(0);
	await expect(blocked.getByTestId('media-approve')).toHaveCount(0);
	await expect(blocked.getByTestId('media-approve-unavailable')).toHaveText('Its picture is not loaded, so it cannot be approved here.');

	const approvals: string[] = [];
	page.on('request', (request) => {
		if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/approve')) approvals.push(request.url());
	});
	const broken = row(page, 'Broken picture');
	await broken.getByTestId('media-approve').click();
	const review = page.getByTestId('media-review-dialog');
	await expect(review.getByTestId('media-review-failed')).toHaveText('It could not be shown in this browser, so it cannot be approved here.');
	await expect(review.getByTestId('media-approve-confirm')).toBeDisabled();
	await review.getByTestId('media-review-cancel').click();
	await expect(review).toHaveCount(0);

	// A video whose first frame never decodes has no approval path either.
	const brokenVideo = row(page, 'Broken video');
	await brokenVideo.getByTestId('media-load-video').click();
	await brokenVideo.getByTestId('media-approve').click();
	await expect(review.getByTestId('media-review-video')).toHaveAttribute('src', /^blob:/);
	await expect(review.getByTestId('media-review-failed')).toHaveText('It could not be shown in this browser, so it cannot be approved here.');
	await expect(review.getByTestId('media-approve-confirm')).toBeDisabled();
	await review.getByTestId('media-review-cancel').click();
	await expect(review).toHaveCount(0);
	expect(approvals, 'nothing is sent for a picture that was not shown').toEqual([]);
	await expect(broken).toHaveAttribute('data-status', 'Draft');
	await expect(brokenVideo).toHaveAttribute('data-status', 'Draft');
	allowStatuses(guards, 401, 404);
	await guards.expectClean();
});

test("drafts' pictures load first, however old, so an old draft can still be approved", async ({ page }) => {
	// ARV-194: the screen fetches 40 pictures at most; 45 newer approved items once took every place before an old draft.
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	const item = (n: number, status: 'Draft' | 'Approved') => ({
		id: `0199a000-0000-7000-8000-${String(n).padStart(12, '0')}`, siteCode: site, contentType: 'image/png', sizeBytes: png.length, sha256: '0'.repeat(64),
		widthPixels: 8, heightPixels: 4, durationMilliseconds: null, originalFileName: null, advertiser: 'Gulf Air', campaign: `Campaign ${n}`,
		startsOn: '2026-01-01', endsOn: '2026-12-31', status, uploadedBy: 'e2e.mediawebmgr', uploadedOn: null, approvedBy: status === 'Approved' ? 'e2e.other' : null,
		approvedOn: null, retiredOn: null, playable: status === 'Approved', canApprove: status === 'Draft', reviewToken: `token-${n}`
	});
	const list = [...Array.from({ length: 45 }, (_, n) => item(n + 1, 'Approved')), item(99, 'Draft')];
	const fetched: string[] = [];
	let release = (): void => {};
	const held = new Promise<void>((resolve) => (release = resolve));
	await page.route(`**/api/v1/admin/sites/${site}/advertising/media`, (route) => route.fulfill({ json: list }));
	await page.route(`**/api/v1/admin/sites/${site}/advertising/media/*/content`, async (route) => {
		fetched.push(route.request().url());
		await held;
		return route.fulfill({ body: png, contentType: 'image/png' });
	});
	const approver = accounts().mediaWebApprover;
	await signInThroughUi(page, approver, await unusedTotpCode(approver.totpSecret!));
	await openDisplays(page, site);

	const old = row(page, 'Campaign 99');
	await expect(old.getByTestId('media-approve-unavailable'), 'while its picture is fetched').toHaveText('Its picture is loading.');
	release();
	await expect(old.getByTestId('media-preview')).toHaveAttribute('src', /^blob:/);
	await expect(old.getByTestId('media-approve')).toBeVisible();
	await expect.poll(() => fetched.length).toBe(40);
	expect(fetched[0], 'the draft first').toContain(item(99, 'Draft').id);
	await expect(row(page, 'Campaign 45').getByTestId('media-preview'), 'the oldest approved items wait').toHaveCount(0);
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

test('a late answer for the site the screen opened with leaves the site chosen since on the screen', async ({ page }) => {
	// The screen loads its first site (DMO, advertising off) as it opens; choosing another site before that load answers must
	// keep the chosen site's policy and library whichever answer comes last (the 2026-10-22 story gate saw DMO's late
	// answer hide the library of the site chosen).
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().mediaWebManager);
	let release = (): void => {};
	const held = new Promise<void>((resolve) => (release = resolve));
	let asked = 0;
	await page.route(/\/api\/v1\/admin\/sites\/DMO\/advertising-policy$/, async (route) => {
		asked++;
		await held;
		await route.continue();
	});
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Passenger displays');
	await expect.poll(() => asked, { message: 'the screen asked for its first site' }).toBe(1);
	await page.locator('#displays-site').selectOption(site);
	const library = page.getByTestId('advertising-library');
	await expect(library).toBeVisible();
	await expect(page.getByTestId('advertising-policy')).toHaveAttribute('data-allowed', 'true');

	const late = page.waitForResponse(/\/api\/v1\/admin\/sites\/DMO\/advertising-policy$/);
	release();
	await late;
	// Nothing on the page may change after the late answer; give the page's handlers a moment to run before looking again.
	await page.waitForTimeout(500);
	await expect(page.locator('#displays-site')).toHaveValue(site);
	await expect(page.getByTestId('advertising-policy')).toHaveAttribute('data-allowed', 'true');
	await expect(library).toBeVisible();
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
