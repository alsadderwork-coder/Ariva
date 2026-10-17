import { expect, type Locator, type Page, test } from '@playwright/test';
import pg from 'pg';
import { createClient } from 'redis';
import { accounts, call, login, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';
import { apiTime, routedAnswer, routedLane, staffingRoute, type RoutedAnswer, type RoutedInterval, type RoutedLane } from '../support/staffing';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-075: visual regression baselines for the key screens (live operations, immigration, the displays screen and a
// passenger display, and since ARV-104h a validation campaign) in English and Arabic, light and dark, so an unintended change
// of layout or design tokens (Aman design system parity) fails the run. Rendered only by the pinned Playwright image (scripts/visual-browser.mjs); run on
// its own and first, on the fresh demo seed. The live values are planted (fixed numbers in Redis, as Ariva.Api.Stream
// writes them) and what moves with the clock (times, the arrival wave, charts) is masked. The immigration screen's staffing
// panel (ARV-181b1) reads a fixed answer with fixed times instead (routed in the browser), so it is compared in full.

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

/** The health bins and band minutes planted for the DMO queue zones (the migration login can delete them; the runtime role cannot). */
async function clearHealth(db: pg.Client, since: Date): Promise<void> {
	await db.query(`DELETE FROM zone_health_bin WHERE zone_key LIKE 'DMO/%' AND start_utc >= $1`, [since]);
	await db.query(`DELETE FROM overflow_minute WHERE zone_key = 'DMO/A-VIS' AND minute_utc >= $1`, [since]);
}
/** What the run planted, removed afterwards so the suites that follow start from the demo seed. */
const planted = { keys: [] as string[], displays: [] as { id: string; token: string }[], healthSince: null as Date | null };

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

/**
 * Fixed snapshots for every published DMO queue zone: the same numbers on every run, published just now. Since ARV-120 also
 * one final health bin per queue zone (the bin before the current one, fixed values by zone; its time is masked) and an
 * occupied minute of the arrivals visitors' overflow band (A-OV), as Ariva.Api.Stream writes them, so the data-quality
 * panels and the overflow marker are on screen.
 */
async function plantSnapshots(): Promise<void> {
	const db = database();
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
		const binMs = 15 * 60_000;
		const bin = new Date(Math.floor(Date.now() / binMs) * binMs - binMs);
		planted.healthSince = new Date(bin.getTime() - binMs);
		await clearHealth(db, planted.healthSince);
		for (const [index, { name }] of (zones.rows as { name: string }[]).entries()) {
			const exited = 20 - (index % 4);
			await db.query(
				`INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end,
				     conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open,
				     track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
				 VALUES ($1, $2, 1, 15, 'Final', 12, 20, 20, 8, 8, $3, 20, $4, 0, $5, 0, 0, 0, $6, 15, 15, $7, now())`,
				[`DMO/${name}`, bin, index % 3, exited, 20 - exited, exited / 20, index % 5 === 2 ? 1 : 0]
			);
		}
		await db.query(
			`INSERT INTO overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on)
			 VALUES ('DMO/A-VIS', 'A-OV', $1, 12, 4, 12, now()) ON CONFLICT (zone_key, band_name, minute_utc) DO NOTHING`,
			[new Date(Math.floor(Date.now() / 60_000) * 60_000 - 2 * 60_000)]
		);
	} finally {
		await redis.quit();
		await db.end();
	}
}

/** A layout for a planted display (ARV-140); the default is the full view in English first, as ARV-058 planted it. */
interface DisplayLayout {
	orientation?: 'Landscape' | 'Portrait';
	languages?: string[];
	layout?: 'Full' | 'Split';
	queueShare?: number;
}

let displayToken = '';

/** A passenger display with two queues and both languages; returns the player's path with its credential. */
async function createDisplay(shape: DisplayLayout = {}): Promise<string> {
	// A TOTP code is accepted once: a second worker in the same 30 second step waits for the next code. A worker signs in once.
	if (!displayToken) {
		const admin = accounts().visualAdmin;
		let signedIn = await login(admin.userName, admin.password, undefined, undefined, { code: totpCode(admin.totpSecret!) });
		for (let attempt = 0; signedIn.status() === 401 && attempt < 3; attempt++) {
			await new Promise((resolve) => setTimeout(resolve, 31_000 - (Date.now() % 30_000)));
			signedIn = await login(admin.userName, admin.password, undefined, undefined, { code: totpCode(admin.totpSecret!) });
		}
		expect(signedIn.status()).toBe(200);
		displayToken = (await signedIn.json()).accessToken;
	}
	const token = displayToken;
	// Each worker plants its own display (the code is not on screen).
	const code = `VIS-${Array.from({ length: 5 }, () => 'ABCDEFGHJKLMNPQRSTUVWXYZ'[Math.floor(Math.random() * 24)]).join('')}`;
	const created = await call('POST', `${hosts.main}/api/v1/admin/displays`, {
		token,
		data: {
			siteCode: 'DMO', code, name: 'Arrivals hall', location: 'Arrivals hall', orientation: shape.orientation ?? 'Landscape', languages: shape.languages ?? ['en', 'ar'],
			bandMinutes: 5, hysteresisMinutes: 2, staleSeconds: 600,
			entries: [
				{ zone: 'A-RES', labels: { en: 'Residents', ar: 'المقيمون' } },
				{ zone: 'A-VIS', labels: { en: 'Visitors', ar: 'الزوار' } }
			],
			fallback: { en: 'Please follow the signs', ar: 'يرجى اتباع اللوحات' },
			layout: shape.layout ?? 'Full', queueShare: shape.queueShare ?? 60, secondRegion: 'Instructions'
		}
	});
	expect(created.status(), created.status() === 201 ? 'the display is created' : await created.text()).toBe(201);
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
	expect(created.status(), created.status() === 201 ? 'the campaign is planned' : await created.text()).toBe(201);
	return (await created.json()).id;
}

/**
 * Everything that moves with the clock, whatever the screen. The time elements inside `fixed` (a CSS selector) are not
 * masked: the test fixed their values, so they are compared like any other text.
 */
function clockMasks(page: Page, fixed?: string): Locator[] {
	return [page.locator(fixed ? `time:not(${fixed} time)` : 'time'), page.getByTestId('board-updated'), page.getByTestId('environment-chip')];
}

/** The staffing panel's first computed minute and the answer's time: 18:07 and 40 seconds later at the demo airport (Asia/Dubai, the project's time zone). */
const staffingMinute = new Date('2026-10-16T14:07:00Z');
const staffingAsOf = new Date('2026-10-16T14:07:40Z');

/**
 * ARV-181b1: the immigration screen's staffing panel reads GET api/v1/sites/DMO/staffing-recommendation every minute, and
 * what the job stores there (its values, the computed-at time, the quarter-hour rows and so which lane opens) changes with
 * the clock. The test answers the request itself with this fixed answer, in the API's shape, so the panel is the same in
 * every run and variant. Now the citizens lane has a desk spare, the residents lane matches and the visitors lane is short
 * by 2, so its next 3 hours open (at its limit of 8 desks and short by 1 from 18:15, out of reach from 18:30, a flight
 * without a passenger figure at 20:30 and the queue unknown after it); the crew lane has no queue length and the e-gates
 * no open count.
 */
function fixedStaffing(): RoutedAnswer {
	const lane = (zone: string, code: string, values: Partial<RoutedLane>, now: Partial<RoutedInterval>) => routedLane(zone, code, values, now, staffingMinute);
	const none = { recommended: null, bound: 'None', waitRecommendedMinutes: null, waitOpenNowMinutes: null };
	const visitors = lane('A-VIS', 'VIS', { desks: 8, openNow: 4, queueNow: 36, cycleMinutes: 2.143 }, { arrivals: 24, recommended: 6, waitRecommendedMinutes: 9.5, waitOpenNowMinutes: 16.8 });
	const later: Partial<RoutedInterval>[] = [
		{ arrivals: 95, recommended: 8, bound: 'Busy', atLimit: true, shortfallDesks: 1, waitRecommendedMinutes: 18, waitOpenNowMinutes: 64 },
		{ arrivals: 140, recommended: 8, bound: 'Both', atLimit: true, waitRecommendedMinutes: 120, waitOpenNowMinutes: 120 },
		{ arrivals: 70, recommended: 7, waitRecommendedMinutes: 9.6, waitOpenNowMinutes: 48 },
		{ arrivals: 45, recommended: 5, waitRecommendedMinutes: 7.2, waitOpenNowMinutes: 12.4 },
		{ arrivals: 30, recommended: 4, waitRecommendedMinutes: 6.1, waitOpenNowMinutes: 5.3 },
		{ arrivals: 52, recommended: 5, waitRecommendedMinutes: 8.4, waitOpenNowMinutes: 14.9 },
		{ arrivals: 38, recommended: 4, waitRecommendedMinutes: 7.7, waitOpenNowMinutes: 9.2 },
		{ arrivals: 20, recommended: 2, waitRecommendedMinutes: 4.5, waitOpenNowMinutes: 2.1 },
		{ arrivals: 12, recommended: 2, waitRecommendedMinutes: 3.2, waitOpenNowMinutes: 1.4 },
		{ ...none, arrivals: null, gap: 'FlightWithoutPassengers' },
		{ ...none, arrivals: 18, gap: 'BacklogUnknown' },
		{ ...none, arrivals: 22, gap: 'BacklogUnknown' }
	];
	later.forEach((values, k) => Object.assign(visitors.intervals[k + 1], values));
	const crew = lane('A-CRW', 'CRW', { openNow: 1, queueNow: null, gap: 'NoQueueLength' }, {});
	crew.intervals.forEach((interval) => Object.assign(interval, { ...none, arrivals: 6, gap: 'NoQueueLength' }));
	const gates = lane('A-EG', 'EG', { openNow: null, queueNow: 22, cycleMinutes: 0.4 }, { arrivals: 44, recommended: 2, waitRecommendedMinutes: 6.5 });
	gates.intervals.forEach((interval) => (interval.waitOpenNowMinutes = null));
	return routedAnswer(
		[
			lane('A-CIT', 'CIT', { openNow: 4, queueNow: 8, cycleMinutes: 1.25 }, { arrivals: 30, recommended: 3, waitRecommendedMinutes: 2.6, waitOpenNowMinutes: 2 }),
			lane('A-RES', 'RES', { queueNow: 29 }, { arrivals: 26, recommended: 3, waitRecommendedMinutes: 9.8, waitOpenNowMinutes: 9.8 }),
			visitors,
			crew,
			gates
		],
		staffingAsOf
	);
}

async function settle(page: Page): Promise<void> {
	await page.waitForLoadState('networkidle');
	await page.evaluate(() => document.fonts.ready);
}

test.afterAll(async () => {
	if (planted.healthSince) {
		const db = database();
		await db.connect();
		try {
			await clearHealth(db, planted.healthSince);
		} finally {
			await db.end();
		}
	}
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
			// The data-quality panel's bin times as well (ARV-120): their width follows their digits, and the text after them moves.
			await page.locator('[data-testid="data-quality"] time').evaluateAll((times) => times.forEach((time) => (time.textContent = '00:00')));
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
			// The border shift supervisor holds ArrivalWaveLanes.View, so the screen shows the staffing panel: a fixed answer.
			const staffing = fixedStaffing();
			await page.route(staffingRoute, (route) => route.fulfill({ json: staffing }));
			await signInThroughUi(page, accounts().webBorder);
			await language(page, variant.arabic);
			// Through the sidebar, so the language chosen stays (a full load would start in English again).
			await page.locator('a[href="/immigration"]').first().click();
			await expect(page.getByTestId('lanes')).toBeVisible();
			await expect(page.locator('[data-testid="data-quality"] [data-testid="quality-row"]').first()).toBeVisible();
			// The panel shows the fixed answer: the visitors lane, short of desks now, opens its rows from the fixed minute.
			await expect(page.getByTestId('staffing')).toHaveAttribute('data-state', 'ready');
			const visitors = page.locator('[data-testid="staffing-lane"][data-zone="A-VIS"]');
			await expect(visitors).toBeVisible();
			await expect(visitors.getByTestId('staffing-row').first()).toHaveAttribute('data-start', apiTime(staffingMinute));
			await settle(page);
			// The data-quality panel's bin times (ARV-120): their width follows their digits, and the text after them moves.
			await page.locator('[data-testid="data-quality"] time').evaluateAll((times) => times.forEach((time) => (time.textContent = '00:00')));
			await expect(page).toHaveScreenshot(`immigration-${variant.name}.png`, {
				// Desks and e-gates follow AMAN's records, which the simulator keeps sending; the lanes' waits are the planted snapshots.
				// The staffing panel's times are the fixed answer's, so they stay visible.
				mask: [...clockMasks(page, '[data-testid="staffing"]'), page.getByTestId('desk-grid'), page.getByTestId('egates')]
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

		// ARV-140: the split layouts, Arabic first on a landscape screen (the queues on the right) and English first on a
		// portrait one (the queues on top), the second region showing the first instruction slide (ARV-141). The board keeps
		// its own palette, so one colour scheme is enough.
		for (const split of [
			{ name: 'landscape-ar', viewport: { width: 1920, height: 1080 }, shape: { orientation: 'Landscape', languages: ['ar', 'en'], layout: 'Split', queueShare: 60 } },
			{ name: 'portrait-en', viewport: { width: 1080, height: 1920 }, shape: { orientation: 'Portrait', languages: ['en', 'ar'], layout: 'Split', queueShare: 60 } }
		] as const) {
			test(`passenger display split ${split.name}, ${variant.name}`, async ({ page }) => {
				test.skip(variant.name !== 'en-light', 'the board keeps its own palette and languages whatever the browser');
				test.setTimeout(150_000);
				const path = await createDisplay({ ...split.shape, languages: [...split.shape.languages] });
				await page.setViewportSize(split.viewport);
				await page.goto(path);
				await expect(page.getByTestId('board')).toHaveAttribute('data-layout', 'Split');
				await expect(page.getByTestId('board-entry')).toHaveCount(2);
				// ARV-141: the demo seed's arrivals slides apply to A-RES and A-VIS, so the second region rotates them; the
				// screenshot is of the first, the passport slide, which stays 10 seconds.
				await expect(page.getByTestId('board-second')).toHaveAttribute('data-state', 'slides');
				await expect(page.getByTestId('board-slide')).toHaveAttribute('data-icon', 'Passport');
				await settle(page);
				await expect(page).toHaveScreenshot(`passenger-display-split-${split.name}-${variant.name}.png`, { mask: clockMasks(page) });
			});
		}
	});
}
