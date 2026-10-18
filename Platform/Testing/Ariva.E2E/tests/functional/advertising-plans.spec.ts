import crypto from 'node:crypto';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { provisionLoadSite, secondFactorSignIn } from '../support/load-site';
import { xssPayloads } from '../support/payloads';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-143a: the Advertising screen, at sites of this spec's own. A duty manager builds a playlist from the site's library (an
// approved image; a draft shows as not playing today) and its instruction slides, orders it and gives each item its seconds
// and weight; markup in the name shows as text everywhere (list, schedule list, timeline). The playlist is scheduled for the
// display's queue zone all day, every day, and the timeline shows it on the display today; a schedule naming the display
// itself for a short window wins over it. Retiring the playlist leaves the instructions on the timeline and marks its
// schedule as not applied; restoring brings both back. The screen mirrors in Arabic. A display set to show instructions says
// so on its timeline. A site whose advertising policy is off shows a note and asks for no playlist or schedule (no 403
// answer).

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
const run = crypto.randomBytes(2).toString('hex').toUpperCase();
const winterName = `${xssPayloads[1]} Winter ${run}`;
const peakName = `Peak hour ${run}`;
const slideTitle = `Have your passport ready ${run}`;

let site: string;
let offSite: string;
let zone: string;
let display: string;
let displayId: string;
let admin: string;
let manager: string;
let approvedImage: string;
let draftImage: string;
let slide: string;
let winter: string;
let peak: string;

const admins = `${hosts.main}/api/v1/admin`;
const plan = (at: string) => `${admins}/sites/${at}/advertising`;

/** The display's settings: split, advertising in its second region, showing the site's queue zone. */
function displayRequest(changes: Record<string, unknown> = {}) {
	return {
		siteCode: site,
		code: display,
		name: 'Arrivals hall',
		location: null,
		orientation: 'Landscape',
		languages: ['en'],
		bandMinutes: 5,
		hysteresisMinutes: 1,
		staleSeconds: 150,
		entries: [{ zone, labels: { en: 'Arrivals' } }],
		fallback: { en: 'Please follow the signs' },
		enabled: true,
		layout: 'Split',
		queueShare: 60,
		secondRegion: 'Advertising',
		...changes
	};
}

/** The site's local day (Asia/Dubai, provisionLoadSite) moved by some days, "yyyy-MM-dd". */
function localDay(offset = 0): string {
	const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai' }).format(new Date());
	const date = new Date(`${today}T00:00:00Z`);
	date.setUTCDate(date.getUTCDate() + offset);
	return date.toISOString().slice(0, 10);
}

async function upload(campaign: string): Promise<string> {
	const body = new FormData();
	body.append('file', new Blob([png], { type: 'image/png' }), 'plan.png');
	body.append('advertiser', 'Gulf Air');
	body.append('campaign', campaign);
	body.append('startsOn', localDay(-1));
	body.append('endsOn', localDay(30));
	const response = await call('POST', `${plan(site)}/media`, { token: manager, form: body });
	expect(response.status(), await response.text()).toBe(201);
	return (await response.json()).id;
}

/** Approves the image as a second person with a second factor, naming it as it is shown (its review token). */
async function approve(id: string): Promise<void> {
	const shown = await call('GET', `${plan(site)}/media/${id}`, { token: admin });
	expect(shown.status(), await shown.text()).toBe(200);
	const approved = await call('POST', `${plan(site)}/media/${id}/approve`, { token: admin, data: { reviewed: (await shown.json()).reviewToken } });
	expect(approved.status(), await approved.text()).toBe(200);
}

async function openAdvertising(page: Page, siteCode: string): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Advertising', exact: true }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Advertising');
	await page.locator('#advertising-site').selectOption(siteCode);
	await expect(page.getByTestId('plans-page')).toHaveAttribute('data-site', siteCode);
}

function playlistRow(page: Page, name: string): Locator {
	return page.getByTestId('playlist-row').filter({ has: page.getByTestId('playlist-row-name').getByText(name, { exact: true }) });
}

function scheduleRow(page: Page, target: string): Locator {
	return page.getByTestId('schedule-row').filter({ has: page.getByTestId('schedule-row-target').getByText(target, { exact: true }) });
}

/** The playlists the timeline shows on a day, in order ('' while the display shows its instructions). */
function playing(day: Locator): Promise<string[]> {
	return day.getByTestId('timeline-segment').evaluateAll((segments) => segments.map((segment) => segment.getAttribute('data-playlist') ?? ''));
}

test.beforeAll(async () => {
	test.setTimeout(180_000);
	admin = await secondFactorSignIn(accounts().planWebAdmin);
	const provisioned = await provisionLoadSite(accounts().planWebAdmin, 'AP', 'Load Zone', admin);
	site = provisioned.site;
	zone = provisioned.zone;
	// A second site whose advertising policy stays off.
	offSite = (await provisionLoadSite(accounts().planWebAdmin, 'AQ', 'Load Zone', admin)).site;
	const policyUrl = `${hosts.main}/api/v1/admin/sites/${site}/advertising-policy`;
	const { publishedProfileId } = await (await call('GET', policyUrl, { token: admin })).json();
	const allowed = await call('PUT', policyUrl, {
		token: admin,
		data: {
			allowed: true,
			excludedZones: [],
			reason: 'Airport operator letter AO-WEB-143a, commercial team',
			reviewedProfileId: publishedProfileId
		}
	});
	expect(allowed.status(), await allowed.text()).toBe(200);

	manager = (await signIn(accounts().planWebManager)).accessToken;
	approvedImage = await upload('Winter sun');
	await approve(approvedImage);
	draftImage = await upload('Spring draft');

	const slideMade = await call('POST', `${admins}/display-slides`, {
		token: admin,
		data: { siteCode: site, scope: 'Site', targets: [], icon: 'Passport', durationSeconds: 8, pinned: false, texts: { en: { title: slideTitle, body: null } } }
	});
	expect(slideMade.status(), await slideMade.text()).toBe(201);
	slide = (await slideMade.json()).id;

	// Creating a display issues its credential: the administrator's token carries a fresh second factor.
	display = `AP-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
	const made = await call('POST', `${admins}/displays`, { token: admin, data: displayRequest() });
	expect(made.status(), await made.text()).toBe(201);
	displayId = (await made.json()).display.id;

	// The playlist the display's own schedule plays (test 2): the slide alone.
	const peakMade = await call('POST', `${plan(site)}/playlists`, {
		token: manager,
		data: { name: peakName, items: [{ kind: 'Slide', id: slide, seconds: 10 }] }
	});
	expect(peakMade.status(), await peakMade.text()).toBe(201);
	peak = (await peakMade.json()).id;
});

test('a duty manager builds a playlist of an approved image and a slide, with seconds and weights; markup in its name shows as text', async ({ page }) => {
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().planWebManager);
	await openAdvertising(page, site);
	await expect(page.getByTestId('playlists').getByRole('heading', { level: 2 })).toHaveText('Playlists');

	const form = page.getByTestId('playlist-form');
	await expect(form).toHaveAttribute('data-mode', 'create');
	await form.getByTestId('playlist-name').fill(winterName);
	await form.getByTestId('playlist-media-choice').selectOption(approvedImage);
	await form.getByTestId('playlist-add-media').click();
	await expect(form.getByTestId('playlist-media-choice').locator(`option[value="${approvedImage}"]`), 'an item is offered once').toHaveCount(0);
	await form.getByTestId('playlist-media-choice').selectOption(draftImage);
	await form.getByTestId('playlist-add-media').click();
	await form.getByTestId('playlist-slide-choice').selectOption(slide);
	await form.getByTestId('playlist-add-slide').click();

	const items = form.getByTestId('playlist-item');
	await expect(items).toHaveCount(3);
	await expect(items.nth(0).getByTestId('playlist-item-label')).toHaveText('Gulf Air: Winter sun');
	await expect(items.nth(0).getByTestId('item-not-playing'), 'the approved image plays today').toHaveCount(0);
	await expect(items.nth(1).getByTestId('item-not-playing')).toHaveText('Not playing today (draft or outside its dates)');
	await expect(items.nth(2)).toHaveAttribute('data-kind', 'Slide');
	await expect(items.nth(2).getByTestId('playlist-item-label')).toHaveText(slideTitle);

	// The draft leaves the list; the slide moves first; seconds and weights per item.
	await items.nth(1).getByTestId('item-remove').click();
	await expect(items).toHaveCount(2);
	await items.nth(1).getByTestId('item-up').click();
	await expect(items.nth(0)).toHaveAttribute('data-kind', 'Slide');
	await expect(items.nth(1)).toHaveAttribute('data-kind', 'Media');
	await expect(items.nth(0).getByTestId('item-down')).toBeEnabled();
	await expect(items.nth(1).getByTestId('item-down')).toBeDisabled();
	await items.nth(0).getByTestId('item-seconds').fill('12');
	await items.nth(1).getByTestId('item-seconds').fill('20');
	await items.nth(1).getByTestId('item-weight').fill('3');
	await expect(page.locator(injected)).toHaveCount(0);
	await form.getByTestId('playlist-save').click();

	const row = playlistRow(page, winterName);
	await expect(row).toHaveAttribute('data-status', 'Active');
	await expect(row.getByTestId('playlist-row-count')).toHaveText('2 items');
	await expect(row.getByTestId('playlist-row-seconds')).toHaveText('32 s');
	await expect(row.getByTestId('playlist-row-silent')).toHaveCount(0);
	await expect(form.getByTestId('playlist-name'), 'the form is ready for the next playlist').toHaveValue('');
	winter = (await row.getAttribute('data-id'))!;

	const saved = await (await call('GET', `${plan(site)}/playlists/${winter}`, { token: manager })).json();
	expect(saved.name).toBe(winterName);
	expect(saved.items.map((i: { kind: string; id: string; seconds: number; weight: number }) => [i.kind, i.id, i.seconds, i.weight])).toEqual([
		['Slide', slide, 12, 1],
		['Media', approvedImage, 20, 3]
	]);

	// Changing it brings its items back into the form.
	await row.getByTestId('playlist-edit').click();
	await expect(form).toHaveAttribute('data-mode', 'edit');
	await expect(form.getByTestId('playlist-name')).toHaveValue(winterName);
	await expect(form.getByTestId('playlist-item')).toHaveCount(2);
	await expect(form.getByTestId('item-weight').nth(1)).toHaveValue('3');
	await form.getByTestId('playlist-cancel').click();
	await expect(form).toHaveAttribute('data-mode', 'create');
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
});

test('the playlist scheduled for the display zone all day plays on the display today; a schedule naming the display wins its window', async ({ page }) => {
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().planWebManager);
	await openAdvertising(page, site);

	const form = page.getByTestId('schedule-form');
	await form.getByTestId('schedule-playlist').selectOption(winter);
	await form.getByTestId('schedule-scope-zone').check();
	await form.getByTestId('schedule-targets').fill(zone);
	for (const day of await form.getByTestId('schedule-day').all()) await day.check();
	await form.getByTestId('schedule-opens').fill('00:00');
	await form.getByTestId('schedule-end-of-day').check();
	await expect(form.getByTestId('schedule-closes')).toBeDisabled();
	await form.getByTestId('schedule-save').click();

	const zoned = scheduleRow(page, `Displays showing zones: ${zone}`);
	await expect(zoned).toHaveAttribute('data-status', 'Active');
	await expect(zoned.getByTestId('schedule-row-playlist')).toHaveText(winterName);
	await expect(zoned.getByTestId('schedule-row-days')).toHaveText('Every day');
	await expect(zoned.getByTestId('schedule-row-window')).toHaveText('00:00 to 24:00');

	await page.getByTestId('timeline-display').selectOption(display);
	await expect(page.getByTestId('timeline-zone')).toHaveText('Local time of the site: Asia/Dubai');
	await expect(page.getByTestId('advertising-timeline-instructions-note')).toHaveCount(0);
	const days = page.getByTestId('timeline-day');
	await expect(days).toHaveCount(7);
	const today = days.first();
	await expect(today).toHaveAttribute('data-day', localDay());
	await expect(today).toContainText('Today');
	await expect.poll(() => playing(today)).toEqual([winterName]);
	await expect(today.getByTestId('timeline-segment')).toHaveAttribute('data-from', '00:00');
	await expect(today.getByTestId('timeline-segment')).toHaveAttribute('data-to', '24:00');
	await expect.poll(() => playing(days.last())).toEqual([winterName]);

	// A second schedule naming the display itself, every day from 10:00 to 11:00: more specific, so it wins its window.
	await form.getByTestId('schedule-playlist').selectOption(peak);
	await form.getByTestId('schedule-scope-display').check();
	await form.getByTestId('schedule-targets').fill(display);
	await form.getByTestId('schedule-opens').fill('10:00');
	await form.getByTestId('schedule-end-of-day').uncheck();
	await form.getByTestId('schedule-closes').fill('09:00');
	await expect(form.getByTestId('schedule-next-day'), 'a closing time before the opening time is on the next day').toHaveText(
		'Closes at 09:00 on the next day.'
	);
	await form.getByTestId('schedule-closes').fill('11:00');
	await expect(form.getByTestId('schedule-next-day')).toHaveCount(0);
	await form.getByTestId('schedule-save').click();

	const named = scheduleRow(page, `Displays: ${display}`);
	await expect(named).toHaveAttribute('data-status', 'Active');
	await expect(named.getByTestId('schedule-row-window')).toHaveText('10:00 to 11:00');
	await expect.poll(() => playing(today)).toEqual([winterName, peakName, winterName]);
	const slot = today.locator('[data-testid="timeline-segment"][data-from="10:00"]');
	await expect(slot).toHaveAttribute('data-to', '11:00');
	await expect(slot).toHaveAttribute('data-playlist', peakName);
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
});

test('retiring the playlist leaves the instructions on the timeline and its schedule not applied; restoring brings both back', async ({ page }) => {
	test.setTimeout(90_000);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().planWebManager);
	await openAdvertising(page, site);
	await page.getByTestId('timeline-display').selectOption(display);
	const today = page.getByTestId('timeline-day').first();
	await expect.poll(() => playing(today)).toEqual([winterName, peakName, winterName]);

	const row = playlistRow(page, winterName);
	await row.getByTestId('playlist-retire').click();
	await row.getByTestId('confirm-playlist-retire').click();
	await expect(row).toHaveAttribute('data-status', 'Retired');
	await expect(row.getByTestId('playlist-edit')).toHaveCount(0);
	const zoned = scheduleRow(page, `Displays showing zones: ${zone}`);
	await expect(zoned.getByTestId('schedule-not-applied')).toHaveText('Playlist retired: not applied');
	await expect(zoned).toHaveAttribute('data-applied', 'false');
	await expect.poll(() => playing(today)).toEqual(['', peakName, '']);
	await expect(today.getByTestId('timeline-segment').first()).toContainText('Instructions');
	for (const day of await page.getByTestId('timeline-day').all()) expect(await playing(day), 'the retired playlist plays on no day').not.toContain(winterName);
	await expect(page.getByTestId('schedule-playlist').locator(`option[value="${winter}"]`), 'a retired playlist is not offered').toHaveCount(0);

	// The display's own schedule retired and restored too.
	const named = scheduleRow(page, `Displays: ${display}`);
	await named.getByTestId('schedule-retire').click();
	await expect(named).toHaveAttribute('data-status', 'Retired');
	await expect.poll(() => playing(today)).toEqual(['']);
	await named.getByTestId('schedule-restore').click();
	await expect(named).toHaveAttribute('data-status', 'Active');

	await row.getByTestId('playlist-restore').click();
	await expect(row).toHaveAttribute('data-status', 'Active');
	await expect(zoned.getByTestId('schedule-not-applied')).toHaveCount(0);
	await expect(zoned).toHaveAttribute('data-applied', 'true');
	await expect.poll(() => playing(today)).toEqual([winterName, peakName, winterName]);
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
});

test('the screen mirrors in Arabic: headings, days and a timeline that runs from the start side', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().planWebManager);
	await openAdvertising(page, site);
	await page.getByTestId('timeline-display').selectOption(display);
	const today = page.getByTestId('timeline-day').first();
	await expect.poll(() => playing(today)).toEqual([winterName, peakName, winterName]);

	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('الإعلانات');
	await expect(page.getByTestId('playlists').getByRole('heading', { level: 2 })).toHaveText('قوائم التشغيل');
	await expect(page.getByTestId('schedules').getByRole('heading', { level: 2 })).toHaveText('الجداول');
	await expect(page.getByTestId('timeline').getByRole('heading', { level: 2 })).toHaveText('الجدول الزمني');
	await expect(scheduleRow(page, `الشاشات التي تعرض المناطق: ${zone}`).getByTestId('schedule-row-days')).toHaveText('كل يوم');
	await expect(today).toContainText('اليوم');
	await expect(playlistRow(page, winterName).getByTestId('playlist-row-count')).toHaveText('عنصران');

	// Right to left, the day starts on the right: midnight's segment lies to the right of the 10:00 one.
	const segments = today.getByTestId('timeline-segment');
	const midnight = (await segments.nth(0).boundingBox())!;
	const ten = (await segments.nth(1).boundingBox())!;
	expect(midnight.x, 'the first hours are on the right').toBeGreaterThan(ten.x);
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
});

test('a display set to show instructions says so on the timeline: it plays no advertising whatever the schedules say', async ({ page }) => {
	const changed = await call('PUT', `${admins}/displays/${displayId}`, { token: manager, data: displayRequest({ secondRegion: 'Instructions' }) });
	expect(changed.status(), await changed.text()).toBe(200);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().planWebManager);
	await openAdvertising(page, site);
	await page.getByTestId('timeline-display').selectOption(display);
	await expect(page.getByTestId('advertising-timeline-instructions-note')).toHaveText(
		'This display is set to show instructions: it plays no advertising whatever the schedules say.'
	);
	// The schedules stay as they are: the week still shows what they would play.
	await expect.poll(() => playing(page.getByTestId('timeline-day').first())).toEqual([winterName, peakName, winterName]);
	await guards.expectClean();
});

test('a site whose advertising policy is off shows the note and asks for no playlist or schedule', async ({ page }) => {
	const guards = await guardPage(page);
	const refused: string[] = [];
	const asked: string[] = [];
	page.on('response', (response) => {
		if (response.status() === 403) refused.push(response.url());
	});
	page.on('request', (request) => {
		if (request.url().includes(`/sites/${offSite}/advertising/`)) asked.push(request.url());
	});
	await signInThroughUi(page, accounts().planWebManager);
	await openAdvertising(page, offSite);
	const off = page.getByTestId('plans-off');
	await expect(off).toHaveText('Advertising is off for this site; a system administrator allows it on Passenger displays.');
	await expect(page.getByTestId('plans-page')).toHaveAttribute('data-state', 'off');
	await expect(page.getByTestId('playlists')).toHaveCount(0);
	await expect(page.getByTestId('schedules')).toHaveCount(0);
	await expect(page.getByTestId('timeline')).toHaveCount(0);

	// The same screen at the site that allows advertising, then back: the note again, still without a request.
	await page.locator('#advertising-site').selectOption(site);
	await expect(page.getByTestId('plans-page')).toHaveAttribute('data-state', 'ready');
	await page.locator('#advertising-site').selectOption(offSite);
	await expect(off).toBeVisible();
	await page.waitForLoadState('networkidle');
	expect(asked, 'no advertising call for the site whose policy is off').toEqual([]);
	expect(refused, 'no 403 answer').toEqual([]);
	await guards.expectClean();
});

// Roles without Advertising.View or Advertising.Manage: no sidebar item, the no-access panel, and no plan request (the
// server refuses them anyway, api/advertising-plans.spec.ts).
for (const [role, account] of [
	['a handler station manager', () => accounts().webHandler],
	['a border shift supervisor', () => accounts().webBorder]
] as const) {
	test(`${role} has no advertising screen and asks for no plan`, async ({ page }) => {
		const guards = await guardPage(page);
		const asked: string[] = [];
		page.on('request', (request) => {
			if (request.url().includes('/advertising/')) asked.push(request.url());
		});
		await signInThroughUi(page, account());
		await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Advertising' })).toHaveCount(0);
		await page.goto('/advertising');
		await expect(page.getByTestId('no-access')).toBeVisible();
		await page.waitForLoadState('networkidle');
		expect(asked, 'no advertising call').toEqual([]);
		await guards.expectClean();
	});
}
