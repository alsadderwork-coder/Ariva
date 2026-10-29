import crypto from 'node:crypto';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { createClient } from 'redis';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage, type PageGuards } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { provisionLoadSite, secondFactorSignIn } from '../support/load-site';
import { videoFixture } from '../support/media';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-143b: a split display set to advertising plays its scheduled playlist in its second region, at a site of this spec's own.
// A duty manager uploads two images, a video and a draft image; the administrator approves all but the draft as the second
// person, allows advertising, and creates the display (Arabic first, so the queues are on the right) and an instruction slide.
// The player fetches each advertisement once with its own credential into an object URL: an image in an img element, the
// video muted in a video element (it ends after its two seconds and holds its last frame until its slot ends), the slide as
// the slide view shows it. Weights 2 and 1 play A, B, A in each cycle. The wait rule pauses advertising at its minutes and
// resumes below them by the hysteresis. A failing item shows instructions for its slot, the next plays on time, and it is
// tried again at its next turn. A network loss plays on from the cache until the board goes stale. A draft never plays, a
// plan stops at its end by the server's clock, the display form sets the wait rule, and turning the site's advertising off
// shows instructions at the next board, lets the cached bytes go and fetches nothing more.
// ARV-144: the player reports the plays of the advertisements it showed (start by the server's clock, time on screen,
// completed or cut short) in a batch with its own credential, and the server takes them once.
// ARV-197: the player measures on the monotonic clock: a screen clock moved back while the network is down still lets the
// board go stale and the plan stop, a screen clock moved forward (as after a sleep) makes it stale at once, and a board that
// arrives late never moves the player's clock of the server back.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const week = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
const admins = `${hosts.main}/api/v1/admin`;
const plan = (at: string) => `${admins}/sites/${at}/advertising`;
const mediaPath = '/api/v1/display/advertising/';
const displayName = `Arrivals ${xssPayloads[2]}`;
const slideTitle = `Keep your boarding pass ready ${xssPayloads[1]}`;

let site: string;
let zone: string;
let admin: string;
let manager: string;
let display: string;
let displayId: string;
let credential: string;
let imageA: string;
let imageB: string;
let video: string;
let draft: string;
let slide: string;
let first: string;
let share: string;
let withDraft: string;
let schedule: string;

/** The display's settings: split, advertising in its second region, Arabic first, stale after 60 seconds. */
function displayRequest(changes: Record<string, unknown> = {}) {
	return {
		siteCode: site,
		code: display,
		name: displayName,
		location: null,
		orientation: 'Landscape',
		languages: ['ar', 'en'],
		bandMinutes: 5,
		hysteresisMinutes: 1,
		staleSeconds: 60,
		entries: [{ zone, labels: { ar: 'القادمون', en: 'Arrivals' } }],
		fallback: { ar: 'يرجى اتباع اللافتات', en: 'Please follow the signs' },
		enabled: true,
		layout: 'Split',
		queueShare: 60,
		secondRegion: 'Advertising',
		advertisingPauseMinutes: null,
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

async function upload(content: Buffer, fileName: string, type: string, campaign: string): Promise<string> {
	const body = new FormData();
	body.append('file', new Blob([new Uint8Array(content)], { type }), fileName);
	body.append('advertiser', 'Gulf Air');
	body.append('campaign', campaign);
	body.append('startsOn', localDay(-1));
	body.append('endsOn', localDay(30));
	const response = await call('POST', `${plan(site)}/media`, { token: manager, form: body });
	await expectStatus(response, 201);
	return (await response.json()).id;
}

/** Approves an item as a second person with a second factor, naming it as it is shown (its review token). */
async function approve(id: string): Promise<void> {
	const shown = await call('GET', `${plan(site)}/media/${id}`, { token: admin });
	await expectStatus(shown, 200);
	const approved = await call('POST', `${plan(site)}/media/${id}/approve`, { token: admin, data: { reviewed: (await shown.json()).reviewToken } });
	await expectStatus(approved, 200);
}

async function playlist(name: string, items: { kind: string; id: string; seconds: number; weight?: number }[]): Promise<string> {
	const made = await call('POST', `${plan(site)}/playlists`, { token: manager, data: { name, items } });
	await expectStatus(made, 201);
	return (await made.json()).id;
}

function scheduled(playlistId: string) {
	return { playlistId, scope: 'Display', targets: [display], days: week, opens: '00:00', closes: '24:00' };
}

/** The board as the player gets it, with the display's credential. */
async function boardNow(): Promise<Record<string, any>> {
	const response = await call('GET', `${hosts.main}/api/v1/display/board?code=${display}`, { headers: { 'X-Ariva-Display-Key': credential } });
	await expectStatus(response, 200);
	return response.json();
}

/** Schedules the playlist on the display all day and waits until its board carries it. */
async function plays(playlistId: string): Promise<void> {
	const changed = await call('PUT', `${plan(site)}/schedules/${schedule}`, { token: manager, data: scheduled(playlistId) });
	await expectStatus(changed, 200);
	await expect.poll(async () => (await boardNow()).advertising?.playlistId ?? null, { timeout: 20_000 }).toBe(playlistId);
}

async function changeDisplay(changes: Record<string, unknown>): Promise<void> {
	const response = await call('PUT', `${admins}/displays/${displayId}`, { token: manager, data: displayRequest(changes) });
	await expectStatus(response, 200);
}

function snapshot(nowcastMinutes: number | null, ageSeconds = 0) {
	return {
		zoneKey: `${site}/${zone}`,
		minuteUtc: new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000).toISOString(),
		queueLength: 20,
		lengthFromSensors: true,
		lengthDegraded: false,
		nowcastMinutes,
		throughputPerMinute: 3,
		noService: null,
		nowcastDegraded: false,
		publishedUtc: new Date(Date.now() - ageSeconds * 1000).toISOString()
	};
}

/** Plants the display's queue zone's live snapshot, as Ariva.Api.Stream writes it (Stream is not part of the E2E run). */
async function plant(nowcastMinutes: number | null): Promise<void> {
	if (!redisUrl) return;
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await redis.set(`${instance}live:zone:${site}/${zone}`, JSON.stringify(snapshot(nowcastMinutes)), { EX: 3600 });
	} finally {
		await redis.quit();
	}
}

/** One change of the advertising region as the page recorded it. */
interface Entry {
	at: number;
	slot: string;
	place: string;
	item: string;
	state: string;
	reason: string;
	shown: string;
	media: string;
}

/** A slot the region showed: its place and item, when it started and ended, and what it showed meanwhile. */
interface Slot {
	slot: number;
	place: number;
	item: number;
	start: number;
	end: number | null;
	states: Set<string>;
	reasons: Set<string>;
	shown: Set<string>;
	media: Set<string>;
}

/**
 * Records, every 100 ms from the first paint, each change of the advertising region (its slot, place, item, state and reason,
 * and the element shown with its advertisement), and every image or video that failed in it.
 */
async function recordPlayback(page: Page): Promise<void> {
	await page.addInitScript(() => {
		const log: Record<string, string | number>[] = [];
		const errors: string[] = [];
		const events: string[] = [];
		const target = window as unknown as { __arivaPlayback: typeof log; __arivaMediaErrors: string[]; __arivaMediaEvents: string[] };
		target.__arivaPlayback = log;
		target.__arivaMediaErrors = errors;
		target.__arivaMediaEvents = events;
		// What the region's images did, by this machine's clock: each one inserted (and whether the browser already had its
		// bytes), loaded or failed, with the region's slot then; and each change of the page's play counters. A failure's
		// timeline prints them (the proof of play test).
		const slotNow = () => document.querySelector('[data-testid="board-advertising"]')?.getAttribute('data-slot') ?? '';
		const describe = (image: HTMLImageElement) =>
			`${image.getAttribute('data-media-id') ?? ''} slot ${slotNow()} complete ${image.complete} width ${image.naturalWidth}`;
		for (const type of ['load', 'error']) {
			document.addEventListener(
				type,
				(event) => {
					const element = event.target as HTMLElement | null;
					if (element?.getAttribute?.('data-testid') === 'board-advertising-image') events.push(`${Date.now()} ${type} ${describe(element as HTMLImageElement)}`);
				},
				true
			);
		}
		new MutationObserver((changes) => {
			for (const change of changes)
				for (const node of Array.from(change.addedNodes)) {
					if (!(node instanceof Element)) continue;
					const images = [node, ...Array.from(node.querySelectorAll('img'))].filter(
						(element): element is HTMLImageElement =>
							element instanceof HTMLImageElement && element.getAttribute('data-testid') === 'board-advertising-image'
					);
					for (const image of images) {
						events.push(`${Date.now()} insert ${describe(image)}`);
						requestAnimationFrame(() => events.push(`${Date.now()} frame ${describe(image)}`));
					}
				}
		}).observe(document, { childList: true, subtree: true });
		let counters = '';
		document.addEventListener(
			'error',
			(event) => {
				const element = event.target as HTMLElement | null;
				const id = element?.getAttribute?.('data-testid') ?? '';
				if (id === 'board-advertising-image' || id === 'board-advertising-video') errors.push(element?.getAttribute('data-media-id') ?? '');
			},
			true
		);
		setInterval(() => {
			const region = document.querySelector('[data-testid="board-advertising"]');
			if (!region) return;
			const shown = region.querySelector('[data-testid^="board-advertising-"]');
			const entry: Record<string, string> = {
				slot: region.getAttribute('data-slot') ?? '',
				place: region.getAttribute('data-place') ?? '',
				item: region.getAttribute('data-item') ?? '',
				state: region.getAttribute('data-state') ?? '',
				reason: region.getAttribute('data-reason') ?? '',
				shown: shown?.getAttribute('data-testid') ?? '',
				media: shown?.getAttribute('data-media-id') ?? ''
			};
			const last = log.at(-1);
			if (!last || Object.keys(entry).some((key) => last[key] !== entry[key])) log.push({ ...entry, at: Date.now() });
			const board = document.querySelector('[data-testid="board"]');
			const now = `queued ${board?.getAttribute('data-plays-queued') ?? ''} sent ${board?.getAttribute('data-plays-sent') ?? ''}`;
			if (now !== counters) events.push(`${Date.now()} ${(counters = now)}`);
		}, 100);
	});
}

async function playbackLog(page: Page): Promise<Entry[]> {
	return page.evaluate(() => [...((window as unknown as { __arivaPlayback?: Entry[] }).__arivaPlayback ?? [])]);
}

async function mediaEvents(page: Page): Promise<string[]> {
	return page.evaluate(() => [...((window as unknown as { __arivaMediaEvents?: string[] }).__arivaMediaEvents ?? [])]);
}

async function mediaErrors(page: Page): Promise<string[]> {
	return page.evaluate(() => [...((window as unknown as { __arivaMediaErrors?: string[] }).__arivaMediaErrors ?? [])]);
}

/**
 * The page guards of a test that shows a broken image: Playwright's trace recorder fetches the image's blob: URL to snapshot
 * it, and the page's CSP refuses that fetch (connect-src has no blob:, rightly), which the guards would report. Only that
 * refusal is set aside; every other console error, page error, dialog and CSP violation still fails the test.
 */
async function expectCleanButTheRecordersBlobFetches(guards: PageGuards): Promise<void> {
	// Chromium words the refusal in two ways ("Refused to connect to ... because it violates", and in later builds
	// "Connecting to ... violates"); both name a blob: URL of this origin and connect-src, and nothing else is set aside.
	const recorder =
		/^(?:Refused to connect to 'blob:http:\/\/localhost:\d+\/[0-9a-f-]{36}' because it violates|Connecting to 'blob:http:\/\/localhost:\d+\/[0-9a-f-]{36}' violates) the following Content Security Policy directive: "connect-src /;
	expect(guards.dialogs, 'dialogs opened by the page').toEqual([]);
	expect(guards.pageErrors, 'uncaught page errors').toEqual([]);
	expect(guards.consoleErrors.filter((message) => !recorder.test(message)), 'console errors and CSP messages').toEqual([]);
	expect(
		// The event names the scheme only, and no source file or line: the fetch came from no script of the page.
		(await guards.cspViolations()).filter((violation) => violation !== 'connect-src blocked blob (:0)'),
		'Content Security Policy violations'
	).toEqual([]);
}

function slotsOf(log: Entry[]): Slot[] {
	const slots: Slot[] = [];
	for (const entry of log) {
		if (entry.slot === '' || entry.place === '') continue;
		let last = slots.at(-1);
		if (!last || last.slot !== Number(entry.slot)) {
			if (last) last.end = entry.at;
			last = {
				slot: Number(entry.slot),
				place: Number(entry.place),
				item: Number(entry.item),
				start: entry.at,
				end: null,
				states: new Set(),
				reasons: new Set(),
				shown: new Set(),
				media: new Set()
			};
			slots.push(last);
		}
		last.states.add(entry.state);
		if (entry.reason) last.reasons.add(entry.reason);
		if (entry.shown) last.shown.add(entry.shown);
		if (entry.media) last.media.add(entry.media);
	}
	return slots;
}

/** Waits for `count` whole cycles of `length` places, each starting at place 0, and returns them. */
async function cycles(page: Page, length: number, count: number, timeout: number, after = 0): Promise<Slot[][]> {
	let found: Slot[][] = [];
	await expect
		.poll(
			async () => {
				const slots = slotsOf(await playbackLog(page)).filter((s) => s.end !== null && s.start >= after);
				found = [];
				for (let i = 0; i + length <= slots.length; i++) {
					if (slots[i].place !== 0) continue;
					found.push(slots.slice(i, i + length));
					i += length - 1;
				}
				return found.length;
			},
			{ timeout, intervals: [1_000] }
		)
		.toBeGreaterThanOrEqual(count);
	return found;
}

/** A slot lasted its seconds (the region's timer, read every 100 ms by the page). */
function lasted(slot: Slot, seconds: number, label: string): void {
	expect(slot.end, `${label}: ended`).not.toBeNull();
	expect(Math.abs(slot.end! - slot.start - seconds * 1000), `${label}: ${slot.end! - slot.start} ms for a ${seconds}-second slot`).toBeLessThanOrEqual(1_000);
}

interface Player {
	page: Page;
	guards: PageGuards;
	/** The advertisements the page asked for, by id, in order. */
	media: string[];
	close: () => Promise<void>;
}

/**
 * A 1920 by 1080 screen with no user session, at the player's address with the display's credential in the fragment; `query`
 * adds to the address's query (the proof of play test shortens the player's turn with `&playsEvery=20`).
 */
async function openPlayer(browser: Browser, before?: (page: Page) => Promise<unknown>, query = ''): Promise<Player> {
	const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
	const page = await context.newPage();
	const guards = await guardPage(page);
	await recordPlayback(page);
	const media: string[] = [];
	page.on('request', (request) => {
		const path = new URL(request.url()).pathname;
		if (path.startsWith(mediaPath)) media.push(path.slice(mediaPath.length));
	});
	if (before) await before(page);
	await page.goto(`${webUrl}/display?code=${display}${query}#key=${encodeURIComponent(credential)}`);
	await expect.poll(() => new URL(page.url()).hash.includes('key='), { message: 'the credential leaves the address bar' }).toBe(false);
	return { page, guards, media, close: () => context.close() };
}

/** Drops the console errors of requests the test aborted on purpose. */
function dropAborted(guards: PageGuards, pattern = /net::ERR_FAILED/): void {
	guards.consoleErrors.splice(0, guards.consoleErrors.length, ...guards.consoleErrors.filter((message) => !pattern.test(message)));
}

const times = (list: string[], id: string) => list.filter((value) => value === id).length;

test.beforeAll(async () => {
	test.setTimeout(240_000);
	admin = await secondFactorSignIn(accounts().playerAdmin);
	const provisioned = await provisionLoadSite(accounts().playerAdmin, 'PY', 'Load Zone', admin);
	site = provisioned.site;
	zone = provisioned.zone;
	const policyUrl = `${admins}/sites/${site}/advertising-policy`;
	const { publishedProfileId } = await (await call('GET', policyUrl, { token: admin })).json();
	const allowed = await call('PUT', policyUrl, {
		token: admin,
		data: { allowed: true, excludedZones: [], reason: 'Airport operator letter AO-WEB-143b, commercial team', reviewedProfileId: publishedProfileId }
	});
	await expectStatus(allowed, 200);

	// Four uploads a run (each person may attempt 30 in 10 minutes, and a retry repeats them).
	manager = (await signIn(accounts().playerManager)).accessToken;
	imageA = await upload(png, 'winter.png', 'image/png', 'Winter sun');
	imageB = await upload(png, 'spring.png', 'image/png', 'Spring fares');
	video = await upload(videoFixture('vp9-opus.webm'), 'summer.webm', 'video/webm', 'Summer video');
	draft = await upload(png, 'draft.png', 'image/png', 'Autumn draft');
	for (const id of [imageA, imageB, video]) await approve(id);

	// Creating a display issues its credential: the administrator's token carries a fresh second factor.
	display = `PY-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
	const made = await call('POST', `${admins}/displays`, { token: admin, data: displayRequest() });
	await expectStatus(made, 201);
	const created = await made.json();
	displayId = created.display.id;
	credential = created.credential;

	const slideMade = await call('POST', `${admins}/display-slides`, {
		token: admin,
		data: {
			siteCode: site,
			scope: 'Site',
			targets: [],
			icon: 'Passport',
			durationSeconds: 8,
			pinned: false,
			texts: { ar: { title: 'جهّز بطاقة صعود الطائرة', body: null }, en: { title: slideTitle, body: xssPayloads[0] } }
		}
	});
	await expectStatus(slideMade, 201);
	slide = (await slideMade.json()).id;

	first = await playlist(`First ${crypto.randomBytes(2).toString('hex')}`, [
		{ kind: 'Media', id: imageA, seconds: 5 },
		// Asked for 8 seconds: a video's slot is its own length rounded up, 3 seconds for the 2,008 ms fixture (ARV-143a).
		{ kind: 'Media', id: video, seconds: 8 },
		{ kind: 'Slide', id: slide, seconds: 6 }
	]);
	share = await playlist(`Share ${crypto.randomBytes(2).toString('hex')}`, [
		{ kind: 'Media', id: imageA, seconds: 5, weight: 2 },
		{ kind: 'Media', id: imageB, seconds: 5, weight: 1 }
	]);
	withDraft = await playlist(`Draft ${crypto.randomBytes(2).toString('hex')}`, [
		{ kind: 'Media', id: draft, seconds: 5 },
		{ kind: 'Media', id: imageA, seconds: 5 }
	]);
	const scheduleMade = await call('POST', `${plan(site)}/schedules`, { token: manager, data: scheduled(first) });
	await expectStatus(scheduleMade, 201);
	schedule = (await scheduleMade.json()).id;
	await plant(7);
});

test.afterAll(async () => {
	if (!redisUrl || !site) return;
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await redis.del(`${instance}live:zone:${site}/${zone}`);
	} finally {
		await redis.quit();
	}
});

test('an image plays from a blob: URL in an img, then the video muted in a video element, then a slide, each for its slot', async ({ browser }) => {
	test.setTimeout(150_000);
	await plays(first);
	const board = await boardNow();
	expect(board.advertising.items.map((item: { kind: string; mediaId: string | null }) => [item.kind, item.mediaId])).toEqual([
		['Media', imageA],
		['Media', video],
		['Slide', null]
	]);
	expect(board.advertising.order).toEqual([0, 1, 2]);
	const player = await openPlayer(browser);
	try {
		const { page, guards, media } = player;
		const region = page.getByTestId('board-advertising');
		await expect(page.getByTestId('board-second-region')).toHaveAttribute('data-second-region', 'Advertising', { timeout: 20_000 });

		const image = region.getByTestId('board-advertising-image');
		await expect(image).toBeVisible({ timeout: 30_000 });
		// Read at once: the image's slot is 5 seconds.
		await expect
			.poll(() =>
				image.evaluate((element: HTMLImageElement) => ({
					source: element.src.slice(0, 5),
					id: element.getAttribute('data-media-id'),
					width: element.naturalWidth,
					state: element.closest('[data-testid="board-advertising"]')?.getAttribute('data-state'),
					kind: element.closest('[data-testid="board-advertising"]')?.getAttribute('data-kind')
				}))
			)
			.toEqual({ source: 'blob:', id: imageA, width: 8, state: 'playing', kind: 'Media' });

		// The video: muted, without controls or loop, from an object URL; it ends after its two seconds and holds its last
		// frame until its slot (its own length rounded up: three seconds) is over.
		const film = region.getByTestId('board-advertising-video');
		await expect(film).toBeVisible({ timeout: 15_000 });
		await expect(region).toHaveAttribute('data-place', '1');
		await expect(film).toHaveAttribute('src', /^blob:/);
		await expect(film).toHaveAttribute('data-media-id', video);
		await expect(film).toHaveJSProperty('muted', true);
		await expect(film).toHaveJSProperty('controls', false);
		await expect(film).toHaveJSProperty('loop', false);
		await expect(film).toHaveJSProperty('autoplay', true);
		await expect(film).toHaveJSProperty('ended', true, { timeout: 7_000 });
		expect(await film.evaluate((element: HTMLVideoElement) => element.currentTime), 'it played to its end').toBeGreaterThan(1.5);
		await expect(region).toHaveAttribute('data-place', '1');
		await expect(film, 'the last frame holds until the slot ends').toBeVisible();

		// The slide, in the display's languages, Arabic first and right to left; markup in its words is text.
		const slideView = region.getByTestId('board-advertising-slide');
		await expect(slideView).toBeVisible({ timeout: 10_000 });
		await expect(region).toHaveAttribute('data-kind', 'Slide');
		const texts = slideView.getByTestId('slide-text');
		await expect(texts).toHaveCount(2);
		await expect(texts.nth(0)).toHaveAttribute('lang', 'ar');
		await expect(texts.nth(0)).toHaveAttribute('dir', 'rtl');
		await expect(texts.nth(1)).toContainText(slideTitle);
		await expect(texts.nth(1)).toContainText(xssPayloads[0]);
		await expect(page.locator(injected)).toHaveCount(0);
		await expect(page).toHaveTitle(displayName);

		// One whole cycle at its slots: 5, 3 and 6 seconds, each item shown as it should be, nothing falling back.
		expect(board.advertising.items.map((item: { seconds: number }) => item.seconds), "the video's slot is its own length").toEqual([5, 3, 6]);
		const [cycle] = await cycles(page, 3, 1, 60_000);
		expect(cycle.map((s) => s.item)).toEqual([0, 1, 2]);
		expect([...cycle[0].shown]).toEqual(['board-advertising-image']);
		expect([...cycle[0].media]).toEqual([imageA]);
		expect([...cycle[1].shown]).toEqual(['board-advertising-video']);
		expect([...cycle[1].media]).toEqual([video]);
		expect([...cycle[2].shown]).toEqual(['board-advertising-slide']);
		for (const s of cycle) expect([...s.states]).toEqual(['playing']);
		lasted(cycle[0], 5, 'the image');
		lasted(cycle[1], 3, 'the video');
		lasted(cycle[2], 6, 'the slide');
		expect(times(media, imageA), 'the image is fetched once').toBe(1);
		expect(times(media, video), 'the video is fetched once').toBe(1);
		await expect(region).toHaveAttribute('data-cached', '2');

		// Arabic first: the queues on the right at their share, the advertising on the left; nothing scrolls.
		await expect(page.getByTestId('board')).toHaveAttribute('dir', 'rtl');
		const queue = (await page.getByTestId('board-queue').boundingBox())!;
		const second = (await page.getByTestId('board-second-region').boundingBox())!;
		expect(queue.width, 'the queue region keeps its share').toBeGreaterThanOrEqual(1920 * 0.6 - 1);
		expect(second.x + second.width, 'the second region never overlaps the queues').toBeLessThanOrEqual(queue.x + 0.5);
		if (redisUrl) await expect(page.getByTestId('board-entry').first()).toHaveAttribute('data-band', '5-10');
		const size = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight }));
		expect(size).toEqual({ width: 1920, height: 1080 });
		await page.screenshot({ path: test.info().outputPath('advertising-1920x1080.png') });
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test('the share of airtime: weights 2 and 1 play A, B, A in every cycle, each image fetched once', async ({ browser }) => {
	test.setTimeout(150_000);
	await plays(share);
	expect((await boardNow()).advertising.order).toEqual([0, 1, 0]);
	const player = await openPlayer(browser);
	try {
		const { page, guards, media } = player;
		const found = await cycles(page, 3, 2, 90_000);
		for (const [n, cycle] of found.slice(0, 2).entries()) {
			expect(cycle.map((s) => s.item), `cycle ${n + 1}`).toEqual([0, 1, 0]);
			expect(cycle.map((s) => [...s.media]), `cycle ${n + 1}`).toEqual([[imageA], [imageB], [imageA]]);
			for (const [k, s] of cycle.entries()) lasted(s, 5, `cycle ${n + 1}, place ${k}`);
			const airtime = (id: string) => cycle.filter((s) => s.media.has(id)).reduce((sum, s) => sum + (s.end! - s.start), 0);
			expect(airtime(imageA) / airtime(imageB), 'A plays twice as long as B').toBeGreaterThan(1.6);
			expect(airtime(imageA) / airtime(imageB)).toBeLessThan(2.4);
		}
		expect(times(media, imageA)).toBe(1);
		expect(times(media, imageB)).toBe(1);
		await expect(page.getByTestId('board-advertising')).toHaveAttribute('data-cached', '2');
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test('a draft image in the playlist never plays: the board leaves it out and the player never asks for it', async ({ browser }) => {
	test.setTimeout(120_000);
	await plays(withDraft);
	const board = await boardNow();
	expect(board.advertising.items.map((item: { mediaId: string }) => item.mediaId)).toEqual([imageA]);
	expect(board.advertising.order).toEqual([0]);
	expect(JSON.stringify(board)).not.toContain(draft);
	const refused = await call('GET', `${hosts.main}${mediaPath}${draft}?code=${display}`, { headers: { 'X-Ariva-Display-Key': credential } });
	expect(refused.status(), 'the server serves no draft to a display').toBe(404);

	const player = await openPlayer(browser);
	try {
		const { page, guards, media } = player;
		await expect(page.getByTestId('board-advertising-image')).toHaveAttribute('data-media-id', imageA, { timeout: 30_000 });
		// One place: each slot shows A again, as a new slot.
		await expect.poll(async () => slotsOf(await playbackLog(page)).filter((s) => s.end !== null).length, { timeout: 30_000 }).toBeGreaterThanOrEqual(2);
		for (const s of slotsOf(await playbackLog(page))) {
			expect([...s.media].filter((id) => id !== imageA), 'only A plays').toEqual([]);
			expect(s.item).toBe(0);
		}
		expect(media, 'never asked for the draft').not.toContain(draft);
		expect(times(media, imageA)).toBe(1);
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test("a plan stops when its end passes by the server's clock, without a newer board, and plays again with one", async ({ browser }) => {
	test.setTimeout(150_000);
	await plays(share);
	// Every board says the plan ends 15 seconds after the first one's server time.
	let ends: string | null = null;
	const player = await openPlayer(browser, (page) =>
		page.route(
			(url) => url.pathname === '/api/v1/display/board',
			async (route) => {
				const response = await route.fetch();
				if (response.status() !== 200) return route.fulfill({ response });
				const body = await response.json();
				ends ??= new Date(Date.parse(body.serverUtc) + 15_000).toISOString();
				if (body.advertising) body.advertising.untilUtc = ends;
				return route.fulfill({ response, json: body });
			}
		)
	);
	try {
		const { page, guards } = player;
		const region = page.getByTestId('board-advertising');
		await expect(region).toHaveAttribute('data-state', 'playing', { timeout: 20_000 });
		await expect(region).toHaveAttribute('data-state', 'fallback', { timeout: 25_000 });
		await expect(region).toHaveAttribute('data-reason', 'expired');
		await expect(region.getByTestId('board-second')).toBeVisible();
		await expect(region.locator('img, video')).toHaveCount(0);
		// The queues are not touched: the board itself is live.
		await expect(page.getByTestId('board')).toHaveAttribute('data-state', 'live');
		// A board with the real end (the site's next local midnight at the latest) plays the plan again.
		await page.unrouteAll({ behavior: 'ignoreErrors' });
		await expect(region).toHaveAttribute('data-state', 'playing', { timeout: 25_000 });
		await expect(region.getByTestId('board-advertising-image')).toBeVisible({ timeout: 15_000 });
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

/**
 * Makes this browser show an image it has already loaded the way Chromium 153 does (CI): a blob: URL that loaded once is
 * complete, with its size, the moment an img is given it again, so the region's image starts its play while the slot that
 * ended is still being torn down. Chromium 141 (this repository's local browser) loads it again first; without this its
 * runs would not cover the case that lost plays on CI (2026-10-18). Applied before the page's own scripts run.
 */
async function showLoadedImagesAtOnce(page: Page): Promise<void> {
	await page.addInitScript(() => {
		const loaded = new Set<string>();
		document.addEventListener(
			'load',
			(event) => {
				const image = event.target;
				if (image instanceof HTMLImageElement && image.src.startsWith('blob:')) loaded.add(image.src);
			},
			true
		);
		const complete = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'complete')!;
		const width = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'naturalWidth')!;
		Object.defineProperty(HTMLImageElement.prototype, 'complete', {
			configurable: true,
			get(this: HTMLImageElement) {
				return loaded.has(this.src) || complete.get!.call(this);
			}
		});
		Object.defineProperty(HTMLImageElement.prototype, 'naturalWidth', {
			configurable: true,
			get(this: HTMLImageElement) {
				return loaded.has(this.src) ? Math.max(1, width.get!.call(this)) : width.get!.call(this);
			}
		});
	});
}

/** One play as the player sends it (ARV-144). */
interface SentPlay {
	mediaId: string;
	startedUtc: string;
	durationMilliseconds: number;
	completed: boolean;
}

test("proof of play: the plays shown go in one batch, started by the server's clock, for their time on screen, completed or cut short", async ({ browser }) => {
	test.setTimeout(180_000);
	await plays(share);
	// Until both images are held, the boards play B then A, which fetches them however slowly their bytes come. The board after
	// that brings the playlist's own order (A, B, A), a changed plan, so the player starts again at its first place with the
	// bytes in memory, and every board from then on ends the plan 12.5 seconds after that board's server time: A and B play
	// their 5-second slots, and A again is cut short about 2.5 seconds into its slot (by the server's clock). The plays before
	// it are reported too, and one whose image came late in its slot is cut short, not completed. Anchored on the first board
	// instead, an image whose bytes came late left time for fewer plays, and the server refused it as completed (CI, 2026-10-18).
	let warm = false;
	let anchor: number | null = null;
	/** When each board left for the player (its serverUtc), for the timeline a failure prints. */
	const boards: number[] = [];
	const player = await openPlayer(
		browser,
		async (page) => {
			await showLoadedImagesAtOnce(page);
			return page.route(
				(url) => url.pathname === '/api/v1/display/board',
				async (route) => {
					const response = await route.fetch();
					if (response.status() !== 200) return route.fulfill({ response });
					const body = await response.json();
					// The board's server time as it reaches the player (this machine's clock): the time the server spends composing it
					// and this route's own hop would put the player's clock behind by seconds on a slow runner, and the next, quicker
					// board would move it ahead by as much, ending the plan seconds early (CI, 2026-10-18; since ARV-197 a late
					// board no longer moves the clock back, but a quicker one still moves it ahead).
					body.serverUtc = new Date().toISOString();
					boards.push(Date.parse(body.serverUtc));
					if (body.advertising && !warm) {
						body.advertising.order = [1, 0];
						body.advertising.untilUtc = new Date(Date.parse(body.serverUtc) + 60_000).toISOString();
					} else if (body.advertising) {
						anchor ??= Date.parse(body.serverUtc);
						body.advertising.untilUtc = new Date(anchor + 12_500).toISOString();
					}
					return route.fulfill({ response, json: body });
				}
			);
		},
		// The player's turn at 60 seconds (it holds the parameter to 12 to 60), after the plan's end.
		'&playsEvery=60'
	);
	try {
		const { page, guards } = player;
		const board = page.getByTestId('board');
		const region = page.getByTestId('board-advertising');
		const posted = page.waitForResponse(
			(response) => new URL(response.url()).pathname === '/api/v1/display/plays' && response.request().method() === 'POST',
			{ timeout: 90_000 }
		);
		await expect(region).toHaveAttribute('data-cached', '2', { timeout: 30_000 });
		warm = true;
		await expect.poll(() => anchor, { timeout: 15_000, message: 'the board after the warm-up starts the plan again' }).not.toBeNull();
		await expect(region).toHaveAttribute('data-reason', 'expired', { timeout: 25_000 });
		// The plays wait for the player's turn, in memory only.
		await expect(board).toHaveAttribute('data-plays-sent', '0');
		const queued = Number(await board.getAttribute('data-plays-queued'));
		expect(queued, 'the warm-up plays and the three after it').toBeGreaterThanOrEqual(3);
		const storage = await page.evaluate(() => JSON.stringify([{ ...localStorage }, { ...sessionStorage }]));
		expect(storage, 'no play in web storage').not.toContain(imageA);

		// One batch at the turn, with the display's credential in its header only.
		const response = await posted;
		const request = response.request();
		await expectStatus(response, 200);
		const url = new URL(request.url());
		expect([...url.searchParams.keys()]).toEqual(['code']);
		expect(url.searchParams.get('code')).toBe(display);
		expect(request.url().includes(encodeURIComponent(credential)), 'the key never travels in the address').toBe(false);
		const headers = await request.allHeaders();
		expect(headers['x-ariva-display-key'] === credential, 'the key travels in its header').toBe(true);
		expect(headers.authorization === undefined, 'no bearer token').toBe(true);
		expect(headers.cookie === undefined, 'no cookie').toBe(true);
		expect(headers['content-type']).toContain('application/json');

		const sent = request.postDataJSON() as { plays: SentPlay[] };
		expect(Object.keys(sent)).toEqual(['plays']);
		expect(sent.plays).toHaveLength(queued);
		for (const play of sent.plays) {
			expect(Object.keys(play).sort()).toEqual(['completed', 'durationMilliseconds', 'mediaId', 'startedUtc']);
			expect(play.startedUtc, 'UTC with milliseconds').toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
			expect(Number.isInteger(play.durationMilliseconds)).toBe(true);
			expect(play.completed && play.durationMilliseconds < 3_000, 'completed only when it filled its 5-second slot').toBe(false);
		}
		expect(await response.json(), 'the server took them all, the warm-up plays included').toEqual({ accepted: queued, duplicates: 0, refused: 0 });

		// From the restart on: started by the server's clock, a slot apart; each slot's time on screen.
		const after = sent.plays.filter((play) => Date.parse(play.startedUtc) >= anchor!);
		// The boards, every play by its start from the restart, its time on screen and whether it completed, and the region's
		// slots by this machine's clock: what a failure on a slow runner needs to be read without its trace.
		const name = (id: string) => (id === imageA ? 'A' : id === imageB ? 'B' : id);
		const timeline = [
			...boards.map((at) => `board@${at - anchor!}`),
			...sent.plays.map((play) => `${name(play.mediaId)}@${Date.parse(play.startedUtc) - anchor!}+${play.durationMilliseconds}${play.completed ? 'c' : 'x'}`),
			...slotsOf(await playbackLog(page)).map((s) => `slot ${s.slot}@${s.start - anchor!}..${s.end === null ? '' : s.end - anchor!}[${[...s.media].map(name).join(',')}|${[...s.reasons].join(',')}]`),
			...(await mediaEvents(page)).map((event) => {
				const [at, ...rest] = event.split(' ');
				return `${Number(at) - anchor!}:${rest.join(' ').replace(imageA, 'A').replace(imageB, 'B')}`;
			})
		].join(' | ');
		expect(after.map((play) => play.mediaId), `the advertisements shown after the restart, in order: ${timeline}`).toEqual([imageA, imageB, imageA]);
		const [first, second, third] = after;
		const starts = after.map((play) => Date.parse(play.startedUtc));
		expect(starts[0] - anchor!, 'A was held: it showed as the plan started again').toBeLessThan(1_000);
		expect(starts[1] - starts[0]).toBeGreaterThan(4_000);
		expect(starts[1] - starts[0]).toBeLessThan(6_000);
		expect(starts[2] - starts[1], 'B played its whole slot').toBeGreaterThan(4_000);
		expect(starts[2] - starts[1]).toBeLessThan(6_000);
		expect(first.completed, 'A played its whole slot').toBe(true);
		expect(first.durationMilliseconds).toBeGreaterThan(4_000);
		expect(first.durationMilliseconds).toBeLessThanOrEqual(6_000);
		expect(Math.abs(starts[0] + first.durationMilliseconds - starts[1]), 'A was on screen until B showed').toBeLessThan(750);
		expect(second.completed, 'B played its whole slot').toBe(true);
		expect(second.durationMilliseconds).toBeGreaterThan(4_000);
		expect(second.durationMilliseconds).toBeLessThanOrEqual(6_000);
		expect(Math.abs(starts[1] + second.durationMilliseconds - starts[2])).toBeLessThan(750);
		expect(third.completed, "A again was cut short by the plan's end").toBe(false);
		expect(third.durationMilliseconds).toBeGreaterThan(1_000);
		expect(third.durationMilliseconds).toBeLessThan(4_800);
		const cut = starts[2] + third.durationMilliseconds - anchor!;
		expect(cut, `cut ${cut} ms after the restart, at the plan's end (12.5 s; the player looks once a second)`).toBeGreaterThan(12_000);
		expect(cut).toBeLessThan(14_500);

		// What the region showed after the restart: the same three advertisements, the last until the plan ran out.
		const shown = slotsOf(await playbackLog(page)).filter((s) => s.media.size > 0 && s.start >= anchor!);
		expect(shown.map((s) => [...s.media])).toEqual([[imageA], [imageB], [imageA]]);
		expect([...shown[2].reasons]).toContain('expired');

		// Answered for: nothing waits, nothing was dropped.
		await expect(board).toHaveAttribute('data-plays-sent', String(queued));
		await expect(board).toHaveAttribute('data-plays-queued', '0');
		await expect(board).toHaveAttribute('data-plays-dropped', '0');

		// The same batch again counts once: the server already has every play.
		const again = await call('POST', `${hosts.main}/api/v1/display/plays?code=${display}`, { headers: { 'X-Ariva-Display-Key': credential }, data: sent });
		await expectStatus(again, 200);
		expect(await again.json()).toEqual({ accepted: 0, duplicates: queued, refused: 0 });
		await expect(page.locator(injected)).toHaveCount(0);
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test('the wait rule: a nowcast at the pause minutes shows instructions, held within the hysteresis, and advertising resumes below it', async ({ browser }) => {
	test.skip(!redisUrl, 'needs the run Redis to plant the nowcasts');
	test.setTimeout(180_000);
	await plays(share);
	await changeDisplay({ advertisingPauseMinutes: 20 });
	await plant(12);
	const player = await openPlayer(browser);
	try {
		const { page, guards } = player;
		const region = page.getByTestId('board-advertising');
		const entry = page.getByTestId('board-entry').first();
		await expect(region).toHaveAttribute('data-state', 'playing', { timeout: 20_000 });
		await expect(entry).toHaveAttribute('data-band', '10-15');

		// 20 minutes reaches the rule: instructions at the next board, the queues' band as ever.
		await plant(20);
		await expect(region).toHaveAttribute('data-state', 'paused', { timeout: 25_000 });
		await expect(region).toHaveAttribute('data-reason', 'wait');
		await expect(region.getByTestId('board-second')).toBeVisible();
		await expect(region.locator('img, video')).toHaveCount(0);
		await expect(entry).toHaveAttribute('data-band', '20-25');

		// 19.5 minutes is below the rule but within the one-minute hysteresis: the pause holds over two boards.
		await plant(19.5);
		await page.waitForTimeout(22_000);
		await expect(region).toHaveAttribute('data-state', 'paused');
		// 18.5 minutes is below it by more than the hysteresis: advertising resumes.
		await plant(18.5);
		await expect(region).toHaveAttribute('data-state', 'playing', { timeout: 25_000 });
		await expect(region.getByTestId('board-advertising-image')).toBeVisible({ timeout: 15_000 });
		await guards.expectClean();
	} finally {
		await player.close();
		await changeDisplay({ advertisingPauseMinutes: null });
		await plant(7);
	}
});

test('an item that fails shows instructions for its slot, the next plays on time, and it is tried again at its next turn', async ({ browser }) => {
	test.setTimeout(240_000);
	await plays(share);
	const isB = (url: URL) => url.pathname === `${mediaPath}${imageB}`;
	const player = await openPlayer(browser, (page) => page.route(isB, (route) => route.fulfill({ status: 500, body: '' })));
	try {
		const { page, guards, media } = player;
		// B answers 500: its slot shows instructions for its 5 seconds, and A plays on time after it.
		const [failing] = await cycles(page, 3, 1, 60_000);
		const [a, b, again] = failing;
		expect([...a.media]).toEqual([imageA]);
		expect([...b.reasons]).toContain('failed');
		expect(b.states.has('fallback')).toBe(true);
		expect([...b.media], 'B never showed').toEqual([]);
		lasted(b, 5, "the failed item's slot");
		expect([...again.media], 'the next place plays on time').toEqual([imageA]);
		expect([...again.states]).toEqual(['playing']);
		expect(times(media, imageB), 'fetched ahead and again at its turn').toBeGreaterThanOrEqual(2);

		// B now answers bytes that are not an image: the img fails, its slot shows instructions, the next place plays A.
		await page.unrouteAll({ behavior: 'wait' });
		await page.route(isB, (route) => route.fulfill({ status: 200, contentType: 'image/png', body: 'not an image' }));
		const broken = Date.now();
		await expect.poll(() => mediaErrors(page), { timeout: 45_000 }).toContain(imageB);
		const [brokenCycle] = await cycles(page, 3, 1, 45_000, broken);
		const failedSlot = brokenCycle[1];
		expect([...failedSlot.reasons]).toContain('failed');
		expect([...brokenCycle[2].media]).toEqual([imageA]);
		expect([...brokenCycle[2].states]).toEqual(['playing']);

		// B comes back: tried again at its next turn, it plays.
		await page.unrouteAll({ behavior: 'wait' });
		const restored = Date.now();
		await expect
			.poll(
				async () =>
					slotsOf(await playbackLog(page)).some(
						(s) => s.start > restored && s.end !== null && s.place === 1 && s.media.has(imageB) && !s.states.has('fallback')
					),
				{ timeout: 60_000, intervals: [1_000] }
			)
			.toBe(true);
		allowStatuses(guards, 500);
		await expectCleanButTheRecordersBlobFetches(guards);
	} finally {
		await player.close();
	}
});

test('a network loss: the items play on from the cache until the board goes stale, then come back with the network', async ({ browser }) => {
	test.setTimeout(240_000);
	await plays(share);
	const player = await openPlayer(browser);
	try {
		const { page, guards, media } = player;
		const region = page.getByTestId('board-advertising');
		// Both images fetched once (A for its slot, B ahead of its turn).
		await expect(region).toHaveAttribute('data-cached', '2', { timeout: 30_000 });
		const fetched = media.length;
		let refused = 0;
		await page.route(
			(url) => url.pathname.startsWith('/api/v1/display/'),
			(route) => {
				refused++;
				return route.abort();
			}
		);
		const lost = Date.now();
		// 28 seconds without the network: both images play on from the cache. Four 5-second slots both start and end within
		// it whatever the phase (a slot that began just before the loss is not counted, so 25 seconds left only a few
		// milliseconds for the fourth to end under load). The last good board is at most 10 seconds older, so this stays well
		// within the display's 60 seconds, after which the board is stale and its plan has run out (the server sends a plan's
		// end at most the display's stale threshold after its own clock).
		await page.waitForTimeout(28_000);
		const during = slotsOf(await playbackLog(page)).filter((s) => s.start >= lost && s.end !== null);
		expect(during.length).toBeGreaterThanOrEqual(4);
		for (const s of during) expect([...s.states], `place ${s.place} played on`).toEqual(['playing']);
		expect(new Set(during.flatMap((s) => [...s.media]))).toEqual(new Set([imageA, imageB]));
		expect(refused, 'the board polls failed').toBeGreaterThanOrEqual(2);
		expect(media.length, 'nothing was fetched again').toBe(fetched);
		await expect(region).toHaveAttribute('data-cached', '2');

		// Without a good board for the display's 60 seconds the plan stops (it runs out by the server's clock as the board goes
		// stale): the instructions, and the queues' neutral message, never an old number.
		await expect(region).toHaveAttribute('data-state', 'fallback', { timeout: 50_000 });
		await expect(region).toHaveAttribute('data-reason', /^(stale|expired)$/);
		const stopped = Date.now();
		expect(stopped - lost, 'stopped within the stale threshold of the last good board').toBeLessThanOrEqual(75_000);
		await expect(page.getByTestId('board')).toHaveAttribute('data-state', 'stale', { timeout: 5_000 });
		await expect(region).toHaveAttribute('data-reason', 'stale');
		await expect(page.getByTestId('board-entry').first()).toHaveAttribute('data-band', 'none');
		await expect(region.getByTestId('board-second')).toBeVisible();

		// The network comes back: the next board plays the plan again, from the cache.
		await page.unrouteAll({ behavior: 'ignoreErrors' });
		await expect(region).toHaveAttribute('data-state', 'playing', { timeout: 25_000 });
		await expect(region.getByTestId('board-advertising-image')).toBeVisible({ timeout: 15_000 });
		await expect(region).toHaveAttribute('data-cached', '2');
		expect(media.filter((id) => id === imageA || id === imageB).length, 'still fetched once each').toBe(2);
		dropAborted(guards);
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test('a screen clock moved back while the network is down: the board still goes stale and the plan stops at its threshold', async ({ browser }) => {
	// ARV-197: the age of the last good board and the server's clock are measured on the monotonic clock. On the screen's own
	// clock, one moved back while the kiosk was offline kept the old board live and the plan playing until it caught up.
	test.setTimeout(180_000);
	await plays(share);
	const player = await openPlayer(browser, (page) => page.clock.install());
	try {
		const { page, guards } = player;
		const board = page.getByTestId('board');
		const region = page.getByTestId('board-advertising');
		await expect(region).toHaveAttribute('data-state', 'playing', { timeout: 30_000 });
		await expect(board).toHaveAttribute('data-state', 'live');
		await page.route(
			(url) => url.pathname.startsWith('/api/v1/display/'),
			(route) => route.abort()
		);
		const serverClock = async () => Number(await board.getAttribute('data-server-clock'));
		const before = await serverClock();
		// The screen's clock goes back an hour (Date only: setSystemTime leaves performance.now() as it was), then the display's
		// 60 seconds pass and more.
		await page.clock.setSystemTime(Date.now() - 3_600_000);
		await page.clock.fastForward(75_000);
		await expect(board, 'stale by the monotonic clock').toHaveAttribute('data-state', 'stale', { timeout: 10_000 });
		await expect(region).toHaveAttribute('data-state', 'fallback');
		await expect(region).toHaveAttribute('data-reason', /^(stale|expired)$/);
		await expect(page.getByTestId('board-entry').first()).toHaveAttribute('data-band', 'none');
		await expect(region.getByTestId('board-second')).toBeVisible();
		// The player's clock of the server, which ends a plan and stamps a play, ran on with the monotonic clock: 75 seconds
		// forward (and the moments this test took), not an hour back nor stopped at the last board's time.
		await expect
			.poll(async () => (await serverClock()) - before, { message: "the player's clock of the server ran on", timeout: 10_000 })
			.toBeGreaterThanOrEqual(74_000);
		expect((await serverClock()) - before, 'not by the screen clock').toBeLessThan(120_000);
		dropAborted(guards);
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test('a screen clock moved forward, or a kiosk that slept, while the network is down: the board goes stale at once', async ({ browser }) => {
	// ARV-197: the monotonic clock may stand still while a kiosk sleeps, so the board is stale by the screen's own clock too
	// (the safe side: the next good board clears it). The player's clock of the server stays on the monotonic clock.
	test.setTimeout(120_000);
	await plays(share);
	const player = await openPlayer(browser, (page) => page.clock.install());
	try {
		const { page, guards } = player;
		const board = page.getByTestId('board');
		const region = page.getByTestId('board-advertising');
		await expect(region).toHaveAttribute('data-state', 'playing', { timeout: 30_000 });
		await expect(board).toHaveAttribute('data-state', 'live');
		await page.route(
			(url) => url.pathname.startsWith('/api/v1/display/'),
			(route) => route.abort()
		);
		const serverClock = async () => Number(await board.getAttribute('data-server-clock'));
		const before = await serverClock();
		const started = Date.now();
		// The screen's clock goes 75 seconds forward (Date only), as after a sleep the monotonic clock did not count.
		await page.clock.setSystemTime(Date.now() + 75_000);
		await expect(board, 'stale by the screen clock').toHaveAttribute('data-state', 'stale', { timeout: 5_000 });
		await expect(region).toHaveAttribute('data-state', 'fallback');
		await expect(region).toHaveAttribute('data-reason', 'stale');
		await expect(page.getByTestId('board-entry').first()).toHaveAttribute('data-band', 'none');
		expect((await serverClock()) - before, "the player's clock of the server did not jump with it").toBeLessThan(Date.now() - started + 5_000);
		dropAborted(guards);
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test("a board that arrives late does not move the player's clock of the server back", async ({ browser }) => {
	// ARV-197 (seen by the proof of play test, ARV-144): every other board here carries a server time 7 seconds old, as if it
	// had been held that long on its way. Taken as it came, it put the player's clock of the server back 7 seconds, so a plan
	// that had run out played again and a play was counted twice; the clock keeps the quicker boards' time instead.
	test.setTimeout(120_000);
	let late = false;
	let lateBoards = 0;
	const player = await openPlayer(browser, (page) =>
		page.route(
			(url) => url.pathname === '/api/v1/display/board',
			async (route) => {
				const response = await route.fetch();
				if (response.status() !== 200) return route.fulfill({ response });
				const body = await response.json();
				body.serverUtc = new Date(Date.now() - (late ? 7_000 : 0)).toISOString();
				if (late) lateBoards++;
				late = !late;
				return route.fulfill({ response, json: body });
			}
		)
	);
	try {
		const { page, guards } = player;
		const board = page.getByTestId('board');
		await expect(board).toHaveAttribute('data-server-clock', /^\d+$/, { timeout: 30_000 });
		// Three boards or more, read four times a second.
		const seen: { at: number; clock: number }[] = [];
		const until = Date.now() + 32_000;
		while (Date.now() < until) {
			seen.push({ at: Date.now(), clock: Number(await board.getAttribute('data-server-clock')) });
			await page.waitForTimeout(250);
		}
		expect(lateBoards, 'late boards came').toBeGreaterThanOrEqual(1);
		for (let i = 1; i < seen.length; i++) expect(seen[i].clock, `never back (read ${i})`).toBeGreaterThanOrEqual(seen[i - 1].clock);
		// The page updates its clock every second, and a board's way through this route takes a moment.
		for (const sample of seen) expect(Math.abs(sample.clock - sample.at), "the quicker boards' time").toBeLessThan(2_500);
		await guards.expectClean();
	} finally {
		await player.close();
	}
});

test('the display form sets the wait rule for a split display with advertising; the server keeps it to 1 to 240 minutes', async ({ page }) => {
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().playerManager);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Passenger displays' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Passenger displays');
	await page.locator('#displays-site').selectOption(site);
	const row = page.locator(`[data-testid="display-row"][data-code="${display}"]`);
	await expect(row.getByTestId('display-advertising-pause-summary')).toHaveText('Advertising plays whatever the wait');

	await row.getByTestId('edit-display').click();
	const form = page.getByTestId('display-form');
	const pause = form.getByTestId('display-advertising-pause');
	await expect(pause).toHaveValue('');
	await expect(form.getByLabel('Advertising gives way to instructions at a wait of (minutes, optional)')).toBeVisible();
	await pause.fill('25');
	await form.getByTestId('save-display').click();
	await expect(form).toHaveCount(0);
	await expect(row.getByTestId('display-advertising-pause-summary')).toHaveText('Advertising gives way to instructions at a wait of 25 minutes');
	const saved = await (await call('GET', `${admins}/displays/${displayId}`, { token: manager })).json();
	expect(saved.advertisingPauseMinutes).toBe(25);

	// Changing another setting keeps the rule: every save sends it.
	await row.getByTestId('edit-display').click();
	await expect(pause).toHaveValue('25');
	const place = `Gate hall ${xssPayloads[4]}`;
	await form.getByLabel('Location (optional)').fill(place);
	await form.getByTestId('save-display').click();
	await expect(form).toHaveCount(0);
	await expect(row).toContainText(place);
	await expect(row.getByTestId('display-advertising-pause-summary')).toHaveText('Advertising gives way to instructions at a wait of 25 minutes');
	const relocated = await (await call('GET', `${admins}/displays/${displayId}`, { token: manager })).json();
	expect(relocated).toMatchObject({ location: place, advertisingPauseMinutes: 25 });

	// Shown again in the form; only for a split display with advertising, and kept while it is hidden.
	await row.getByTestId('edit-display').click();
	await expect(pause).toHaveValue('25');
	await form.locator('#display-second').selectOption('Instructions');
	await expect(pause).toHaveCount(0);
	await form.locator('#display-second').selectOption('Advertising');
	await expect(pause).toHaveValue('25');
	await pause.fill('');
	await form.getByTestId('save-display').click();
	await expect(form).toHaveCount(0);
	await expect(row.getByTestId('display-advertising-pause-summary')).toHaveText('Advertising plays whatever the wait');
	expect((await (await call('GET', `${admins}/displays/${displayId}`, { token: manager })).json()).advertisingPauseMinutes).toBeNull();

	// Arabic: the details and the field's label mirror.
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(row.getByTestId('display-advertising-pause-summary')).toHaveText('تُعرض الإعلانات أيًا كان الانتظار');
	await row.getByTestId('edit-display').click();
	await expect(form.getByLabel('تفسح الإعلانات المجال للتعليمات عند انتظار يبلغ (دقائق، اختياري)')).toBeVisible();
	await page.getByTestId('language-toggle').click();
	await expect(page.locator(injected)).toHaveCount(0);

	// The server's range: 1 to 240 whole minutes, or none.
	for (const [label, value] of [
		['zero', 0],
		['241', 241],
		['a negative wait', -5],
		['a fraction', 20.5],
		['markup', xssPayloads[0]],
		['true', true]
	] as const) {
		const response = await call('PUT', `${admins}/displays/${displayId}`, { token: manager, data: displayRequest({ advertisingPauseMinutes: value }) });
		expect(response.status(), label).toBe(400);
		expect(await response.text(), `${label}: no echo`).not.toContain('<script>');
	}
	for (const value of [1, 240, null]) {
		await changeDisplay({ advertisingPauseMinutes: value });
		expect((await (await call('GET', `${admins}/displays/${displayId}`, { token: manager })).json()).advertisingPauseMinutes).toBe(value);
	}
	await guards.expectClean();
});

test('advertising turned off: the next board shows instructions, the cached bytes are let go and nothing more is fetched', async ({ browser }) => {
	test.setTimeout(180_000);
	await plays(share);
	const player = await openPlayer(browser);
	try {
		const { page, guards, media } = player;
		const region = page.getByTestId('board-advertising');
		await expect(region).toHaveAttribute('data-cached', '2', { timeout: 30_000 });
		const image = region.getByTestId('board-advertising-image');
		await expect(image).toBeVisible({ timeout: 15_000 });
		const shownUrl = (await image.getAttribute('src'))!;
		expect(shownUrl).toMatch(/^blob:/);

		// The administrator turns the site's advertising off (a critical change: a fresh second factor).
		const token = await secondFactorSignIn(accounts().playerAdmin);
		const policyUrl = `${admins}/sites/${site}/advertising-policy`;
		const { publishedProfileId } = await (await call('GET', policyUrl, { token })).json();
		const fetched = media.length;
		const off = await call('PUT', policyUrl, {
			token,
			data: { allowed: false, excludedZones: [], reason: 'Airport operator letter AO-WEB-143b, advertising paused', reviewedProfileId: publishedProfileId }
		});
		await expectStatus(off, 200);

		// Within one board: instructions, and the cache let go.
		await expect(page.getByTestId('board-second-region')).toHaveAttribute('data-second-region', 'Instructions', { timeout: 15_000 });
		await expect(region).toHaveAttribute('data-state', 'fallback');
		await expect(region).toHaveAttribute('data-reason', 'none');
		await expect(region).toHaveAttribute('data-cached', '0');
		await expect(region.getByTestId('board-second')).toBeVisible();
		await expect(region.locator('img, video')).toHaveCount(0);
		// The object URL shown before no longer resolves: its bytes are gone with the page's cache.
		const gone = await page.evaluate(
			(source) =>
				new Promise<boolean>((resolve) => {
					const probe = new Image();
					probe.addEventListener('load', () => resolve(false));
					probe.addEventListener('error', () => resolve(true));
					probe.src = source;
				}),
			shownUrl
		);
		expect(gone, 'the object URL was revoked').toBe(true);

		// Another board and more: no advertisement is asked for.
		await page.waitForTimeout(12_000);
		expect(media.length, 'nothing fetched once advertising is off').toBe(fetched);
		const answer = await boardNow();
		expect(answer.secondRegion).toBe('Instructions');
		expect(answer.advertising).toBeNull();
		const refused = await call('GET', `${hosts.main}${mediaPath}${imageA}?code=${display}`, { headers: { 'X-Ariva-Display-Key': credential } });
		expect(refused.status(), 'the server serves nothing while advertising is off').toBe(404);
		// The probe of the revoked URL is the only console error.
		dropAborted(guards, /ERR_FILE_NOT_FOUND/);
		await guards.expectClean();
	} finally {
		await player.close();
	}
});
