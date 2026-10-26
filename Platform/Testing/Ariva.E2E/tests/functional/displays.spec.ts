import crypto from 'node:crypto';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { createClient } from 'redis';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, totpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, earlyInStep, fillSecret, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-058: passenger displays. A terminal duty manager sets up a board for the demo airport's departure immigration
// queues (D-CIT, D-VIS) in Arabic, English, Portuguese and Swahili after a step-up; the player's address with its
// credential is shown once. The player, at 1920 by 1080 and with no user session, shows each queue's band in every
// language, holds it within the hysteresis, widens it for a degraded nowcast, and shows the neutral message for stale
// or missing data. Ariva.Api.Stream is not part of the E2E run, so the suite plants the live snapshots Stream writes.

test.skip(!databaseAvailable, 'the displays need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
const code = `E2E-${crypto.randomBytes(2).toString('hex').toUpperCase()}`;
const api = `${hosts.main}/api/v1/admin/displays`;
const board = `${hosts.main}/api/v1/display/board`;
// Every free text the board shows carries a probe: shown as text, never run or parsed as markup.
const name = `Departures ${xssPayloads[2]}`;
const place = `Gate hall ${xssPayloads[4]}`;
const fallback = { ar: 'يرجى اتباع اللافتات', en: 'Please follow the signs', pt: `Siga as indicações ${xssPayloads[0]}`, sw: 'Tafadhali fuata alama' };
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
let playerAddress = '';

function snapshot(zone: string, nowcastMinutes: number | null, options: { degraded?: boolean; ageSeconds?: number } = {}) {
	return {
		zoneKey: `DMO/${zone}`,
		minuteUtc: new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000).toISOString(),
		queueLength: 20,
		lengthFromSensors: true,
		lengthDegraded: false,
		nowcastMinutes,
		throughputPerMinute: 3,
		noService: null,
		nowcastDegraded: options.degraded ?? false,
		publishedUtc: new Date(Date.now() - (options.ageSeconds ?? 0) * 1000).toISOString()
	};
}

/** Signs in with the second factor 20 minutes old, so creating a display asks for a fresh code. */
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
	const account = accounts().webDisplays;
	await page.goto('/login');
	await fillSignIn(page, account, totpCode(account.totpSecret!, -1));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
}

async function player(browser: Browser): Promise<{ page: Page; close: () => Promise<void> }> {
	const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
	const page = await context.newPage();
	return { page, close: () => context.close() };
}

test.afterAll(async () => {
	if (!redisUrl) return;
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await redis.del([`${instance}live:zone:DMO/D-CIT`, `${instance}live:zone:DMO/D-VIS`]);
	} finally {
		await redis.quit();
	}
});

test('a duty manager sets up a four-language board after a step-up; the player address is shown once', async ({ page }) => {
	test.skip(!developmentSigningKey(), 'needs the run key to age the token');
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	await earlyInStep();
	await signInAged(page);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Passenger displays');

	await page.getByTestId('add-display').click();
	const form = page.getByTestId('display-form');
	await form.getByLabel('Code').fill(code);
	await form.getByLabel('Name', { exact: true }).fill(name);
	await form.getByLabel('Location (optional)').fill(place);
	// Arabic and English are chosen by default; Portuguese and Swahili follow in that order.
	await form.getByRole('checkbox', { name: 'Portuguese' }).check();
	await form.getByRole('checkbox', { name: 'Swahili' }).check();
	await expect(form.getByTestId('language-order')).toHaveText('Shown in this order: Arabic, English, Portuguese, Swahili');
	await form.getByLabel('Band width (minutes)').fill('5');
	await form.getByLabel('Hysteresis (minutes)').fill('1');
	await form.getByLabel('Stale after (seconds)').fill('120');

	const labels = [
		{ zone: 'D-CIT', ar: 'المواطنون', en: 'Citizens', pt: 'Cidadãos', sw: 'Raia' },
		{ zone: 'D-VIS', ar: 'الزوار', en: xssPayloads[1], pt: 'Visitantes', sw: 'Wageni' }
	];
	await form.getByTestId('add-entry').click();
	for (const [index, entry] of labels.entries()) {
		const row = form.getByTestId('display-entry').nth(index);
		await row.getByLabel('Queue zone').selectOption(entry.zone);
		await row.getByLabel('Label (Arabic)').fill(entry.ar);
		await row.getByLabel('Label (English)').fill(entry.en);
		await row.getByLabel('Label (Portuguese)').fill(entry.pt);
		await row.getByLabel('Label (Swahili)').fill(entry.sw);
	}
	await form.getByLabel('Message (Arabic)').fill(fallback.ar);
	await form.getByLabel('Message (English)').fill(fallback.en);
	await form.getByLabel('Message (Portuguese)').fill(fallback.pt);
	await form.getByLabel('Message (Swahili)').fill(fallback.sw);
	await form.getByTestId('save-display').click();

	// Creating a display issues its player's credential: the second factor is 20 minutes old, so a fresh code is asked.
	const dialog = page.getByTestId('step-up-dialog');
	await expect(dialog).toBeVisible();
	await fillSecret(page.locator('#step-up-code'), totpCode(accounts().webDisplays.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();
	const reveal = page.getByTestId('credential-reveal');
	await expect(reveal).toBeVisible();
	playerAddress = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();
	expect(new RegExp(`^${webUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/display\\?code=${code}#key=ardp_`).test(playerAddress), "the player's address with its key").toBe(true);
	await page.getByTestId('credential-done').click();
	await expect(reveal).toHaveCount(0);
	expect((await page.content()).includes('#key='), 'gone once done').toBe(false);

	const row = page.locator(`[data-testid="display-row"][data-code="${code}"]`);
	await expect(row).toContainText('ar, en, pt, sw');
	await expect(row).toContainText('D-CIT, D-VIS');
	await expect(row).toContainText(name);
	await expect(row).toContainText(place);
	await expect(page.locator(injected)).toHaveCount(0);

	// The server keeps the prefix only.
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const listed = await (await call('GET', `${api}?siteCode=DMO`, { token })).text();
	const credential = decodeURIComponent(playerAddress.split('#key=')[1]);
	expect(listed.includes(credential.slice(0, 13)), 'the list shows the prefix').toBe(true);
	expect(listed.includes(credential), 'the list never shows the credential').toBe(false);
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('the player shows bands in every language, holds them within the hysteresis, widens a degraded one and hides stale data', async ({ browser }) => {
	test.skip(!redisUrl || !playerAddress, 'needs the run Redis and the display of the first test');
	test.setTimeout(150_000);
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	const { page, close } = await player(browser);
	const guards = await guardPage(page);
	try {
		const plant = async (...snapshots: ReturnType<typeof snapshot>[]) => {
			for (const s of snapshots) await redis.set(`${instance}live:zone:${s.zoneKey}`, JSON.stringify(s), { EX: 3600 });
		};
		await plant(snapshot('D-CIT', 12.4));
		await redis.del(`${instance}live:zone:DMO/D-VIS`);

		await page.goto(playerAddress);
		await expect.poll(() => new URL(page.url()).hash.includes('key='), { message: 'the credential leaves the address bar' }).toBe(false);
		const entries = page.getByTestId('board-entry');
		await expect(entries).toHaveCount(2);
		const citizens = entries.nth(0);
		const visitors = entries.nth(1);
		await expect(citizens).toHaveAttribute('data-band', '10-15');
		await expect(citizens).toContainText('Citizens');
		await expect(citizens).toContainText('المواطنون');
		await expect(citizens).toContainText('10 to 15 min');
		await expect(citizens).toContainText('من 10 إلى 15 دقيقة');
		await expect(citizens).toContainText('10 a 15 min');
		await expect(citizens).toContainText('Dakika 10 hadi 15');
		await expect(citizens.locator('[lang="ar"]').first()).toHaveAttribute('dir', 'rtl');
		// No data for the visitors' queue: the neutral message in each language, never a number; the label is text.
		await expect(visitors).toHaveAttribute('data-band', 'none');
		for (const message of Object.values(fallback)) await expect(visitors).toContainText(message);
		await expect(visitors).toContainText(xssPayloads[1]);
		await expect(page.locator(injected)).toHaveCount(0);
		await expect(page).toHaveTitle(name);

		// 15.6 minutes is past the band's edge but within the 1-minute hysteresis: the band holds.
		await plant(snapshot('D-CIT', 15.6));
		await page.waitForTimeout(12_000);
		await expect(citizens).toHaveAttribute('data-band', '10-15');
		// 16.5 minutes leaves it: the next band.
		await plant(snapshot('D-CIT', 16.5));
		await expect(citizens).toHaveAttribute('data-band', '15-20', { timeout: 20_000 });
		// A degraded nowcast shows a band twice as wide.
		await plant(snapshot('D-CIT', 16.5, { degraded: true }));
		await expect(citizens).toHaveAttribute('data-band', '15-25', { timeout: 20_000 });
		// Data older than the display's 120 seconds: the neutral message, never the old number.
		await plant(snapshot('D-CIT', 16.5, { ageSeconds: 300 }));
		await expect(citizens).toHaveAttribute('data-band', 'none', { timeout: 20_000 });
		await expect(citizens).toContainText(fallback.en);
		await expect(citizens).not.toContainText('min');

		// The board fills a 16:9 screen without scrolling.
		const size = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight }));
		expect(size.width).toBeLessThanOrEqual(1920);
		expect(size.height).toBeLessThanOrEqual(1080);
		// Every row is on the screen, four languages and all (the type shrinks to fit).
		for (const entry of await entries.all()) {
			const box = (await entry.boundingBox())!;
			expect(box.y + box.height, 'the row ends on the screen').toBeLessThanOrEqual(1080);
		}
		await page.screenshot({ path: test.info().outputPath('board-1920x1080.png') });
		await guards.expectClean();
	} finally {
		await close();
		await redis.quit();
	}
});

test('only the display credential opens a board: users, other codes and wrong keys get 401', async ({ browser }) => {
	test.skip(!playerAddress, 'needs the display of the first test');
	const credential = decodeURIComponent(playerAddress.split('#key=')[1]);
	const user = (await signIn(accounts().webAdmin)).accessToken;
	expect((await call('GET', `${board}?code=${code}`, { token: user })).status(), 'a user token').toBe(401);
	expect((await call('GET', `${board}?code=${code}`, { headers: { 'X-Ariva-Display-Key': credential } })).status()).toBe(200);
	expect((await call('GET', `${board}?code=DMO-OTHER`, { headers: { 'X-Ariva-Display-Key': credential } })).status(), 'another code').toBe(401);
	const wrong = credential.slice(0, 47) + (credential.endsWith('A') ? 'B' : 'A');
	expect((await call('GET', `${board}?code=${code}`, { headers: { 'X-Ariva-Display-Key': wrong } })).status(), 'a wrong key').toBe(401);
	expect((await call('GET', `${board}?code=${code}`)).status(), 'no key').toBe(401);
	const answer = await call('GET', `${board}?code=${code}`, { headers: { 'X-Ariva-Display-Key': credential } });
	expect(answer.headers()['cache-control']).toContain('no-store');
	expect((await answer.text()).includes(credential), 'the board never repeats the key').toBe(false);

	// A player with a wrong key shows the set-up message, not a board.
	const { page, close } = await player(browser);
	try {
		const guards = await guardPage(page);
		// Absolute, so the step leaves the fragment and the near-credential out (ARV-152).
		await page.goto(`${webUrl}/display?code=${code}#key=${encodeURIComponent(wrong)}`);
		await expect(page.getByTestId('board')).toHaveAttribute('data-state', 'refused');
		await expect(page.getByTestId('board-refused').first()).toContainText('This display is not set up');
		allowStatuses(guards, 401);
		await guards.expectClean();
	} finally {
		await close();
	}
});

test('a handler station manager has no displays; a border supervisor sets them up for the immigration halls; Arabic mirrors', async ({ page }) => {
	const handler = (await signIn(accounts().webHandler)).accessToken;
	expect((await call('GET', `${api}?siteCode=DMO`, { token: handler })).status()).toBe(403);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.locator(`[data-testid="display-row"][data-code="${code}"]`)).toBeVisible();
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('شاشات المسافرين');
	await guards.expectClean();
});

// ARV-140: display layouts. The display above (Arabic first, four languages) is edited through the API like any other
// setting; the queue region keeps its share whatever the second region does, and the player follows a layout change at
// its next poll without a restart.

/** The display of the first test as a request, with the given changes. */
async function displayRequest(token: string, changes: Record<string, unknown> = {}): Promise<{ id: string; request: Record<string, unknown> }> {
	const listed = (await (await call('GET', `${api}?siteCode=DMO`, { token })).json()) as Record<string, any>[];
	const current = listed.find((display) => display.code === code)!;
	expect(current, 'the display of the first test').toBeTruthy();
	const request = {
		siteCode: 'DMO', code, name: current.name, location: current.location, orientation: current.orientation, languages: current.languages,
		bandMinutes: current.bandMinutes, hysteresisMinutes: current.hysteresisMinutes, staleSeconds: current.staleSeconds,
		entries: current.entries, fallback: current.fallback, enabled: current.enabled,
		layout: current.layout, queueShare: current.queueShare, secondRegion: current.secondRegion, ...changes
	};
	return { id: current.id, request };
}

async function change(token: string, changes: Record<string, unknown>): Promise<void> {
	const { id, request } = await displayRequest(token, changes);
	const response = await call('PUT', `${api}/${id}`, { token, data: request });
	await expectStatus(response, 200);
}

/** The smallest text in a region, in CSS pixels, over the elements the selector finds. */
async function smallestText(page: Page, testId: string, selector: string): Promise<number> {
	return page.getByTestId(testId).evaluate(
		(region, inner) => Math.min(...Array.from(region.querySelectorAll(inner), (element) => parseFloat(getComputedStyle(element).fontSize))),
		selector
	);
}

const queueText = '[data-testid="board-label"], [data-testid="board-band"], [data-testid="board-fallback"]';

test('a layout is a display setting: the share and the second region are checked, advertising is refused, a handler cannot change it', async () => {
	test.skip(!playerAddress, 'needs the display of the first test');
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const { id, request } = await displayRequest(token);
	expect(request).toMatchObject({ layout: 'Full', queueShare: 60, secondRegion: 'Instructions' });

	const refused: [string, Record<string, unknown>][] = [
		['a share under half the screen', { layout: 'Split', queueShare: 49 }],
		['a share over three quarters', { layout: 'Split', queueShare: 76 }],
		['an unknown layout', { layout: 'Corner' }],
		['a layout in another case', { layout: 'split' }],
		['a layout given as a number', { layout: '1' }],
		['markup for a layout', { layout: xssPayloads[0] }],
		['an unknown second region', { layout: 'Split', secondRegion: 'Video' }],
		['a second region given as a number', { layout: 'Split', secondRegion: '0' }]
	];
	for (const [label, changes] of refused) {
		const response = await call('PUT', `${api}/${id}`, { token, data: { ...request, ...changes } });
		expect(response.status(), label).toBe(400);
	}
	// Advertising is refused with its reason: DMO's advertising policy is off (ARV-145; no spec turns it on).
	const advertising = await call('PUT', `${api}/${id}`, { token, data: { ...request, layout: 'Split', secondRegion: 'Advertising' } });
	expect(advertising.status()).toBe(400);
	expect(await advertising.text()).toContain("Advertising is off for this display's site or for one of its zones");

	// The station manager has no display permission, so the layout is closed to them like every other setting.
	const handler = (await signIn(accounts().webHandler)).accessToken;
	expect((await call('PUT', `${api}/${id}`, { token: handler, data: { ...request, layout: 'Split' } })).status()).toBe(403);
	// A supervisor of another site: this site's display answers like one that does not exist, and nothing changes.
	const elsewhere = (await signIn(accounts().siteUser)).accessToken;
	const foreign = await call('PUT', `${api}/${id}`, { token: elsewhere, data: { ...request, layout: 'Split', queueShare: 70 } });
	expect(foreign.status(), 'a display of another site').toBe(404);
	expect(await foreign.text()).not.toContain(code);
	expect((await displayRequest(token)).request, 'nothing refused was kept').toMatchObject({ layout: 'Full', queueShare: 60, secondRegion: 'Instructions' });
});

test('a running player switches to the split screen at its next poll; the queues come first in Arabic and keep their share', async ({ browser }) => {
	test.skip(!redisUrl || !playerAddress, 'needs the run Redis and the display of the first test');
	test.setTimeout(150_000);
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	const { page, close } = await player(browser);
	const guards = await guardPage(page);
	try {
		await change(token, { layout: 'Full', orientation: 'Landscape', languages: ['ar', 'en', 'pt', 'sw'] });
		await redis.set(`${instance}live:zone:DMO/D-CIT`, JSON.stringify(snapshot('D-CIT', 12.4)), { EX: 3600 });
		await redis.del(`${instance}live:zone:DMO/D-VIS`);
		await page.goto(playerAddress);
		const boardView = page.getByTestId('board');
		await expect(boardView).toHaveAttribute('data-layout', 'Full');
		await expect(page.getByTestId('board-entry').nth(0)).toHaveAttribute('data-band', '10-15');
		// Marks this page: a reload would lose it.
		await page.evaluate(() => ((window as unknown as { arivaSamePage: boolean }).arivaSamePage = true));

		await change(token, { layout: 'Split', queueShare: 60 });
		await expect(boardView).toHaveAttribute('data-layout', 'Split', { timeout: 25_000 });
		expect(await page.evaluate(() => (window as unknown as { arivaSamePage?: boolean }).arivaSamePage), 'no restart').toBe(true);

		// The audit trail has the change with the layout before and after.
		const audit = await call('GET', `${hosts.main}/api/v1/admin/audit-entries?action=Display.Updated`, { token });
		expect(audit.status()).toBe(200);
		expect(JSON.stringify(await audit.json())).toContain(code);

		// Arabic first: the board reads right to left, so the queue region is on the right and the instructions on the left.
		await expect(boardView).toHaveAttribute('dir', 'rtl');
		const queue = (await page.getByTestId('board-queue').boundingBox())!;
		const second = (await page.getByTestId('board-second-region').boundingBox())!;
		expect(queue.width, 'the queue region keeps its share').toBeGreaterThanOrEqual(1920 * 0.6 - 1);
		expect(queue.x + queue.width, 'on the right').toBeGreaterThanOrEqual(1919);
		expect(second.x + second.width, 'the second region never overlaps the queues').toBeLessThanOrEqual(queue.x + 0.5);
		expect(queue.height).toBeGreaterThanOrEqual(1079);

		const entries = page.getByTestId('board-entry');
		await expect(entries).toHaveCount(2);
		await expect(entries.nth(0)).toHaveAttribute('data-band', '10-15');
		await expect(entries.nth(0)).toContainText('من 10 إلى 15 دقيقة');
		await expect(entries.nth(1)).toHaveAttribute('data-band', 'none');
		await expect(entries.nth(1)).toContainText(fallback.ar);
		for (const entry of await entries.all()) {
			const box = (await entry.boundingBox())!;
			expect(box.x, 'the row is inside the queue region').toBeGreaterThanOrEqual(queue.x - 0.5);
			expect(box.y + box.height, 'the row ends on the screen').toBeLessThanOrEqual(1080);
		}

		// The second region: instructions in each of the display's languages, Arabic first and right to left.
		await expect(page.getByTestId('board-second')).toHaveAttribute('data-state', 'ready');
		const blocks = page.getByTestId('board-instructions');
		await expect(blocks).toHaveCount(4);
		await expect(blocks.nth(0)).toHaveAttribute('lang', 'ar');
		await expect(blocks.nth(0)).toHaveAttribute('dir', 'rtl');
		await expect(blocks.nth(1)).toContainText('While you wait');

		// Readable from across a hall: the queues' text at 24 px or more, the instructions at 18 px or more.
		expect(await smallestText(page, 'board-queue', queueText)).toBeGreaterThanOrEqual(24);
		expect(await smallestText(page, 'board-second-region', 'h2, li')).toBeGreaterThanOrEqual(18);
		const size = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight }));
		expect(size).toEqual({ width: 1920, height: 1080 });
		await page.screenshot({ path: test.info().outputPath('split-ar-1920x1080.png') });

		// Stale queue data inside the region: the neutral message, never the old number; the instructions stay.
		await redis.set(`${instance}live:zone:DMO/D-CIT`, JSON.stringify(snapshot('D-CIT', 12.4, { ageSeconds: 300 })), { EX: 3600 });
		await expect(entries.nth(0)).toHaveAttribute('data-band', 'none', { timeout: 25_000 });
		await expect(entries.nth(0)).toContainText(fallback.en);
		await expect(entries.nth(0)).not.toContainText('min');
		await expect(page.getByTestId('board-second')).toHaveAttribute('data-state', 'ready');
		await guards.expectClean();
	} finally {
		await close();
		await redis.quit();
	}
});

// Each orientation at its screen size, with the display's first language in each direction; the queue share varies too.
const splitCases = [
	{ name: 'landscape, English first', orientation: 'Landscape', viewport: { width: 1920, height: 1080 }, languages: ['en', 'ar', 'pt', 'sw'], share: 75, dir: 'ltr' },
	{ name: 'portrait, Arabic first', orientation: 'Portrait', viewport: { width: 1080, height: 1920 }, languages: ['ar', 'en', 'pt', 'sw'], share: 50, dir: 'rtl' },
	{ name: 'portrait, English first', orientation: 'Portrait', viewport: { width: 1080, height: 1920 }, languages: ['en', 'ar', 'pt', 'sw'], share: 60, dir: 'ltr' }
];

for (const split of splitCases) {
	test(`the split screen, ${split.name}: the queues first at their share, the instructions after them, all readable`, async ({ browser }) => {
		test.skip(!redisUrl || !playerAddress, 'needs the run Redis and the display of the first test');
		test.setTimeout(90_000);
		const token = (await signIn(accounts().webAdmin)).accessToken;
		const redis = createClient({ url: redisUrl });
		await redis.connect();
		const context = await browser.newContext({ viewport: split.viewport });
		const page = await context.newPage();
		const guards = await guardPage(page);
		try {
			await change(token, { layout: 'Split', queueShare: split.share, orientation: split.orientation, languages: split.languages });
			await redis.set(`${instance}live:zone:DMO/D-CIT`, JSON.stringify(snapshot('D-CIT', 12.4)), { EX: 3600 });
			await page.goto(playerAddress);
			const boardView = page.getByTestId('board');
			await expect(boardView).toHaveAttribute('data-layout', 'Split');
			await expect(boardView).toHaveAttribute('dir', split.dir);
			await expect(page.getByTestId('board-entry').nth(0)).toHaveAttribute('data-band', '10-15');
			await expect(page.getByTestId('board-second')).toHaveAttribute('data-state', 'ready');

			const { width, height } = split.viewport;
			const queue = (await page.getByTestId('board-queue').boundingBox())!;
			const second = (await page.getByTestId('board-second-region').boundingBox())!;
			if (split.orientation === 'Portrait') {
				// On top, whatever the language, and the full width.
				expect(queue.y).toBeLessThanOrEqual(0.5);
				expect(queue.height, 'the queue region keeps its share').toBeGreaterThanOrEqual((height * split.share) / 100 - 1);
				expect(queue.width).toBeGreaterThanOrEqual(width - 1);
				expect(second.y, 'the second region is below and never overlaps').toBeGreaterThanOrEqual(queue.y + queue.height - 0.5);
			} else {
				// English first: left to right, so the queue region is on the left.
				expect(queue.x).toBeLessThanOrEqual(0.5);
				expect(queue.width, 'the queue region keeps its share').toBeGreaterThanOrEqual((width * split.share) / 100 - 1);
				expect(second.x, 'the second region is after it and never overlaps').toBeGreaterThanOrEqual(queue.x + queue.width - 0.5);
			}
			for (const entry of await page.getByTestId('board-entry').all()) {
				const box = (await entry.boundingBox())!;
				expect(box.y + box.height, 'the row ends inside the queue region').toBeLessThanOrEqual(queue.y + queue.height + 0.5);
			}
			await expect(page.getByTestId('board-instructions').nth(0)).toHaveAttribute('lang', split.languages[0]);
			expect(await smallestText(page, 'board-queue', queueText)).toBeGreaterThanOrEqual(24);
			expect(await smallestText(page, 'board-second-region', 'h2, li')).toBeGreaterThanOrEqual(split.share === 75 ? 14 : 18);
			const size = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight }));
			expect(size).toEqual({ width, height });
			await page.screenshot({ path: test.info().outputPath(`split-${split.orientation.toLowerCase()}-${split.languages[0]}.png`) });
			await guards.expectClean();
		} finally {
			await context.close();
			await redis.quit();
		}
	});
}

test('the second region failing to load leaves the queue region whole', async ({ browser }) => {
	test.skip(!redisUrl || !playerAddress, 'needs the run Redis and the display of the first test');
	test.setTimeout(90_000);
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await change(token, { layout: 'Split', queueShare: 60, orientation: 'Landscape', languages: ['ar', 'en', 'pt', 'sw'] });
		await redis.set(`${instance}live:zone:DMO/D-CIT`, JSON.stringify(snapshot('D-CIT', 12.4)), { EX: 3600 });
		// The instructions cannot be fetched, then come back as something that is not instructions.
		for (const failure of ['abort', 'garbage'] as const) {
			const { page, close } = await player(browser);
			try {
				const guards = await guardPage(page);
				await page.route('**/board/instructions.json', (route) =>
					failure === 'abort' ? route.abort() : route.fulfill({ status: 200, contentType: 'application/json', body: '{"__proto__":{"title":"x","steps":["y"]},"en":{"title":1}}' })
				);
				await page.goto(playerAddress);
				await expect(page.getByTestId('board')).toHaveAttribute('data-layout', 'Split');
				await expect(page.getByTestId('board-second')).toHaveAttribute('data-state', 'failed');
				await expect(page.getByTestId('board-instructions')).toHaveCount(0);
				await expect(page.getByTestId('board-entry').nth(0)).toHaveAttribute('data-band', '10-15');
				const queue = (await page.getByTestId('board-queue').boundingBox())!;
				expect(queue.width, `${failure}: the queue region keeps its share`).toBeGreaterThanOrEqual(1920 * 0.6 - 1);
				expect(await smallestText(page, 'board-queue', queueText)).toBeGreaterThanOrEqual(24);
				expect(await page.evaluate(() => 'title' in ({} as Record<string, unknown>)), 'no prototype was touched').toBe(false);
				// The aborted request is the only console error.
				guards.consoleErrors.splice(0, guards.consoleErrors.length, ...guards.consoleErrors.filter((message) => !/net::ERR_FAILED/.test(message)));
				await guards.expectClean();
			} finally {
				await close();
			}
		}
	} finally {
		// The display goes back to the full view, as the first test created it.
		await change(token, { layout: 'Full', queueShare: 60, orientation: 'Landscape', languages: ['ar', 'en', 'pt', 'sw'] });
		await redis.quit();
	}
});

test('the instructions are text: markup in them is shown as it is, never run or parsed', async ({ browser }) => {
	test.skip(!playerAddress, 'needs the display of the first test');
	test.setTimeout(90_000);
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const { page, close } = await player(browser);
	try {
		await change(token, { layout: 'Split', queueShare: 60, orientation: 'Landscape', languages: ['en', 'ar', 'pt', 'sw'] });
		const guards = await guardPage(page);
		// ARV-141 will make this text configurable; the player already treats it as text.
		await page.route('**/board/instructions.json', (route) =>
			route.fulfill({ status: 200, contentType: 'application/json', json: { en: { title: xssPayloads[1], steps: [xssPayloads[0], xssPayloads[2], xssPayloads[4]] } } })
		);
		await page.goto(playerAddress);
		const second = page.getByTestId('board-second-region');
		await expect(page.getByTestId('board-second')).toHaveAttribute('data-state', 'ready');
		// Each language without its own instructions shows the English ones, once.
		await expect(page.getByTestId('board-instructions')).toHaveCount(1);
		await expect(second.locator('h2')).toHaveText(xssPayloads[1]);
		await expect(second.locator('li').nth(0)).toHaveText(xssPayloads[0]);
		await expect(second.locator('script, img, svg, iframe, [onerror], [onload]')).toHaveCount(0);
		await expect(page.locator(injected)).toHaveCount(0);
		await guards.expectClean();
	} finally {
		await change(token, { layout: 'Full', queueShare: 60, orientation: 'Landscape', languages: ['ar', 'en', 'pt', 'sw'] });
		await close();
	}
});

test('the full view at 1080 by 1920 in a portrait display is unchanged by the layouts: every row on the screen', async ({ browser }) => {
	test.skip(!redisUrl || !playerAddress, 'needs the run Redis and the display of the first test');
	test.setTimeout(90_000);
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const context = await browser.newContext({ viewport: { width: 1080, height: 1920 } });
	const page = await context.newPage();
	const guards = await guardPage(page);
	try {
		await change(token, { layout: 'Full', orientation: 'Portrait', languages: ['en', 'ar', 'pt', 'sw'] });
		await page.goto(playerAddress);
		await expect(page.getByTestId('board')).toHaveAttribute('data-layout', 'Full');
		await expect(page.getByTestId('board-entry')).toHaveCount(2);
		await expect(page.getByTestId('board-second-region')).toHaveCount(0);
		for (const entry of await page.getByTestId('board-entry').all()) {
			const box = (await entry.boundingBox())!;
			expect(box.y + box.height).toBeLessThanOrEqual(1920);
		}
		expect(await smallestText(page, 'board', queueText)).toBeGreaterThanOrEqual(24);
		await guards.expectClean();
	} finally {
		await change(token, { layout: 'Full', orientation: 'Landscape', languages: ['ar', 'en', 'pt', 'sw'] });
		await context.close();
	}
});
