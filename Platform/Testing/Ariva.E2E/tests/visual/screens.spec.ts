import { expect, type Locator, type Page, test } from '@playwright/test';
import pg from 'pg';
import { createClient } from 'redis';
import { accounts, call, login, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-075: visual regression baselines for the key screens (live operations, immigration, the displays screen and a
// passenger display, and since ARV-104h a validation campaign) in English and Arabic, light and dark, so an unintended change
// of layout or design tokens (Aman design system parity) fails the run. Rendered only by the pinned Playwright image (scripts/visual-browser.mjs); run on
// its own and first, on the fresh demo seed. The live values are planted (fixed numbers in Redis, as Ariva.Api.Stream
// writes them) and what moves with the clock (times, the arrival wave, charts) is masked.

test.skip(!databaseAvailable, 'the screens need the E2E database');
// Independent tests, so one changed screen does not hide the others; no retries (a changed pixel is not flaky).
test.describe.configure({ retries: 0 });

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';

// Arabic through the header's language toggle, as a user switches (the choice lasts until the next full page load).
const variants = [
	{ name: 'en-light', arabic: false, colorScheme: 'light' },
	{ name: 'en-dark', arabic: false, colorScheme: 'dark' },
	{ name: 'ar-light', arabic: true, colorScheme: 'light' },
	{ name: 'ar-dark', arabic: true, colorScheme: 'dark' }
] as const;

async function language(page: Page, arabic: boolean): Promise<void> {
	if (!arabic) return;
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
}

let playerPath = '';
/** What the run planted, removed afterwards so the suites that follow start from the demo seed. */
const planted = { keys: [] as string[], displays: [] as { id: string; token: string }[] };

/** Fixed snapshots for every published DMO queue zone: the same numbers on every run, published just now. */
async function plantSnapshots(): Promise<void> {
	const db = new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
	await db.connect();
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		const zones = await db.query(
			`SELECT z.name FROM zone z JOIN zone_profile p ON p.id = z.profile_id
			 WHERE p.site_code = 'DMO' AND p.status = 'Published' AND z.kind = 'Queue' ORDER BY z.name`
		);
		const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000).toISOString();
		for (const [index, { name }] of (zones.rows as { name: string }[]).entries()) {
			const snapshot = {
				zoneKey: `DMO/${name}`,
				minuteUtc: minute,
				queueLength: 8 + ((index * 7) % 40),
				lengthFromSensors: true,
				lengthDegraded: index % 9 === 4,
				nowcastMinutes: Math.round((2 + ((index * 3.7) % 19)) * 10) / 10,
				throughputPerMinute: 4,
				noService: null,
				nowcastDegraded: false,
				publishedUtc: new Date().toISOString()
			};
			await redis.set(`${instance}live:zone:${snapshot.zoneKey}`, JSON.stringify(snapshot), { EX: 3600 });
			planted.keys.push(`${instance}live:zone:${snapshot.zoneKey}`);
		}
	} finally {
		await redis.quit();
		await db.end();
	}
}

/** A passenger display with two queues and both languages; returns the player's path with its credential. */
async function createDisplay(): Promise<string> {
	// A TOTP code is accepted once: a second worker in the same 30 second step waits for the next code.
	const admin = accounts().visualAdmin;
	let signedIn = await login(admin.userName, admin.password, undefined, undefined, { code: totpCode(admin.totpSecret!) });
	for (let attempt = 0; signedIn.status() === 401 && attempt < 3; attempt++) {
		await new Promise((resolve) => setTimeout(resolve, 31_000 - (Date.now() % 30_000)));
		signedIn = await login(admin.userName, admin.password, undefined, undefined, { code: totpCode(admin.totpSecret!) });
	}
	expect(signedIn.status()).toBe(200);
	const token = (await signedIn.json()).accessToken;
	// Each worker plants its own display (the code is not on screen).
	const code = `VIS-${Array.from({ length: 5 }, () => 'ABCDEFGHJKLMNPQRSTUVWXYZ'[Math.floor(Math.random() * 24)]).join('')}`;
	const created = await call('POST', `${hosts.main}/api/v1/admin/displays`, {
		token,
		data: {
			siteCode: 'DMO', code, name: 'Arrivals hall', location: 'Arrivals hall', orientation: 'Landscape', languages: ['en', 'ar'],
			bandMinutes: 5, hysteresisMinutes: 2, staleSeconds: 600,
			entries: [
				{ zone: 'A-RES', labels: { en: 'Residents', ar: 'المقيمون' } },
				{ zone: 'A-VIS', labels: { en: 'Visitors', ar: 'الزوار' } }
			],
			fallback: { en: 'Please follow the signs', ar: 'يرجى اتباع اللوحات' }
		}
	});
	expect(created.status(), await created.text()).toBe(201);
	const display = await created.json();
	planted.displays.push({ id: display.display.id, token });
	return `/display?code=${code}#key=${encodeURIComponent(display.credential)}`;
}

const campaignName = 'Arrivals hall A, pilot week 1';
let campaignId = '';

/**
 * ARV-104h: a planned validation campaign at the demo airport over its published zone profile (two queue zones and their
 * lines, today, placeholder targets), planned once and found again by name on a later run, so the screen is the same every
 * time. Its dates and times are in time elements, which the clock masks cover.
 */
async function plantCampaign(): Promise<string> {
	const { userName, password } = accounts().webBorder;
	const signedIn = await login(userName, password);
	expect(signedIn.status()).toBe(200);
	const token = (await signedIn.json()).accessToken;
	const campaigns = `${hosts.main}/api/v1/sites/DMO/validation/campaigns`;
	const found = await (await call('GET', `${campaigns}?text=${encodeURIComponent(campaignName)}`, { token })).json();
	const existing = found.data.find((c: { name: string }) => c.name === campaignName);
	if (existing) return existing.id;
	const profiles = await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles?siteCode=DMO`, { token })).json();
	const published = profiles.find((p: { status: string }) => p.status === 'Published');
	const view = await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles/${published.id}`, { token })).json();
	const zones = view.zones.filter((z: { kind: string; name: string }) => z.kind === 'Queue' && ['A-RES', 'A-VIS'].includes(z.name));
	const zoneIds = zones.map((z: { id: string }) => z.id);
	const lineIds = view.lines.filter((l: { zoneId: string | null }) => l.zoneId !== null && zoneIds.includes(l.zoneId)).map((l: { id: string }) => l.id);
	const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
	const created = await call('POST', campaigns, {
		token,
		data: { name: campaignName, profileVersion: published.version, zoneIds, lineIds, days: [today], targetBinsPerLine: null, targetTracerRuns: null }
	});
	expect(created.status(), await created.text()).toBe(201);
	return (await created.json()).id;
}

/** Everything that moves with the clock, whatever the screen. */
function clockMasks(page: Page): Locator[] {
	return [page.locator('time'), page.getByTestId('board-updated'), page.getByTestId('environment-chip')];
}

async function settle(page: Page): Promise<void> {
	await page.waitForLoadState('networkidle');
	await page.evaluate(() => document.fonts.ready);
}

test.afterAll(async () => {
	for (const { id, token } of planted.displays) {
		await call('DELETE', `${hosts.main}/api/v1/admin/displays/${id}`, { token });
	}
	if (redisUrl && planted.keys.length > 0) {
		const redis = createClient({ url: redisUrl });
		await redis.connect();
		await redis.del(planted.keys);
		await redis.quit();
	}
});

test.beforeAll(async () => {
	test.skip(!redisUrl, "the live screens need the run's Redis (ARIVA_E2E_REDIS_URL)");
	await plantSnapshots();
});

for (const variant of variants) {
	test.describe(variant.name, () => {
		test.use({ colorScheme: variant.colorScheme });

		test(`sign-in, ${variant.name}`, async ({ page }) => {
			await page.goto('/login');
			await expect(page.locator('input[autocomplete="username"]')).toBeVisible();
			await language(page, variant.arabic);
			await settle(page);
			await expect(page).toHaveScreenshot(`sign-in-${variant.name}.png`, { mask: clockMasks(page) });
		});

		test(`live operations, ${variant.name}`, async ({ page }) => {
			await signInThroughUi(page, accounts().webBorder);
			await language(page, variant.arabic);
			await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
			await expect(page.locator('[data-testid="zone-row"][data-zone="A-RES"]')).not.toContainText('Stale');
			await settle(page);
			// The publication times change the table's column widths as their digits do: one fixed text for all of them.
			await page.locator('[data-testid="zone-row"] td:last-child').evaluateAll((cells) => cells.forEach((cell) => (cell.textContent = '00:00')));
			await expect(page).toHaveScreenshot(`live-operations-${variant.name}.png`, {
				// The zones table's last column is each snapshot's publication time.
				mask: [
					...clockMasks(page),
					page.locator('[data-testid="zone-row"] td:last-child'),
					page.getByTestId('arrival-strip'),
					page.getByTestId('wait-chart'),
					page.getByTestId('alerts-panel'),
					page.getByTestId('desk-states')
				]
			});
		});

		test(`immigration, ${variant.name}`, async ({ page }) => {
			await signInThroughUi(page, accounts().webBorder);
			await language(page, variant.arabic);
			// Through the sidebar, so the language chosen stays (a full load would start in English again).
			await page.locator('a[href="/immigration"]').first().click();
			await expect(page.getByTestId('lanes')).toBeVisible();
			await settle(page);
			await expect(page).toHaveScreenshot(`immigration-${variant.name}.png`, {
				// Desks and e-gates follow AMAN's records, which the simulator keeps sending; the lanes' waits are the planted snapshots.
				mask: [...clockMasks(page), page.getByTestId('desk-grid'), page.getByTestId('egates')]
			});
		});

		test(`validation campaign, ${variant.name}`, async ({ page }) => {
			campaignId ||= await plantCampaign();
			await signInThroughUi(page, accounts().webBorder);
			await language(page, variant.arabic);
			// Through the sidebar and the list, so the language chosen stays (a full load would start in English again).
			await page.locator('a[href="/validation"]').first().click();
			await page.locator(`[data-testid="campaign-row"] a[href*="campaign=${campaignId}"]`).click();
			await expect(page.getByTestId('campaign-name')).toHaveText(campaignName);
			await expect(page.getByTestId('progress-tracers')).toBeVisible();
			await settle(page);
			// The planned day and time change their width as their digits do: one fixed text for all of them (masked too).
			await page.locator('main time').evaluateAll((times) => times.forEach((time) => (time.textContent = '00:00')));
			await expect(page).toHaveScreenshot(`validation-campaign-${variant.name}.png`, {
				// The demo profile's geometry hash differs with every fresh seed.
				mask: [...clockMasks(page), page.getByTestId('campaign-geometry')]
			});
		});

		// The player shows every language of its display at once, so only the colour scheme varies.
		test(`passenger display, ${variant.name}`, async ({ page }) => {
			test.skip(variant.arabic, 'the player is bilingual whatever the browser language');
			test.setTimeout(150_000);
			playerPath ||= await createDisplay();
			await page.setViewportSize({ width: 1920, height: 1080 });
			await page.goto(playerPath);
			await expect(page.getByTestId('board-entry')).toHaveCount(2);
			await settle(page);
			await expect(page).toHaveScreenshot(`passenger-display-${variant.name}.png`, { mask: clockMasks(page) });
		});
	});
}
