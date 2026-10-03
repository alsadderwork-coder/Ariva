import crypto from 'node:crypto';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { createClient } from 'redis';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, totpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, earlyInStep, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';

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
	await page.locator('#step-up-code').fill(totpCode(accounts().webDisplays.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();
	const reveal = page.getByTestId('credential-reveal');
	await expect(reveal).toBeVisible();
	playerAddress = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();
	expect(playerAddress).toMatch(new RegExp(`^${webUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/display\\?code=${code}#key=ardp_`));
	await page.getByTestId('credential-done').click();
	await expect(reveal).toHaveCount(0);
	expect(await page.content(), 'gone once done').not.toContain('#key=');

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
	expect(listed).toContain(credential.slice(0, 13));
	expect(listed).not.toContain(credential);
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
		await expect(page, 'the credential leaves the address bar').not.toHaveURL(/#key=/);
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
	expect(await answer.text()).not.toContain(credential);

	// A player with a wrong key shows the set-up message, not a board.
	const { page, close } = await player(browser);
	try {
		const guards = await guardPage(page);
		await page.goto(`/display?code=${code}#key=${encodeURIComponent(wrong)}`);
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
