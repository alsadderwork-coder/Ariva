import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page, type Request, type Route, type TestInfo } from '@playwright/test';
import { accounts, call, claimsOf, login, signIn, unusedTotpCode } from '../support/accounts';
import { guardPage, type PageGuards } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { homeHeading } from '../support/shell';
import { allowStatuses, databaseAvailable, fillSignIn, signInThroughUi } from '../support/web-auth';

// ARV-104c: the observer tablet (/validation/capture, wiki 07 section 8). A Validation observer chooses a running campaign
// and a line, taps In and Out, and each 15-minute bin is sent once it has ended with a random Idempotency-Key that is kept
// for its retries; unsent bins stay in memory behind a retry banner. The tracer timer records Join and Exit (or Left
// unserved) on the tablet's clock and sends each run with the tablet's clock reading; the measured offset is shown, and a
// tablet more than 5 minutes off is told to set its time automatically. The campaign's own creator or starter gets a
// plain refusal. Roles without Validation.Capture see no capture screen. Campaign, zone and line names and correction
// reasons are user text rendered as text only; tracer codes are labels, never names.
//
// The suite works at its own site E2EO (Asia/Dubai), with a zone profile it publishes once, so validation.spec.ts's E2EV
// is untouched. Bins must have ended on the server's clock before they are sent, so the tally tests run the page on
// Playwright's clock an hour in the past and move it forward bin by bin. Since ARV-104c1 the tally's bins follow Ariva's
// clock (the Date header of its answers) like the desk log's minutes, so those tests serve that header from the page's
// clock too (TabletClock, below).
//
// ARV-104d: the desk state log on the same screen (the Desk log tab). The observer chooses the desks it watches and taps
// each desk's state every minute (Serving, Idle, Paused, Closed); each 15-minute bin of Ariva's clock goes as one batch
// once it has ended, with 15 states per desk (null where not observed) and its own random key, kept for its retries.
// The minutes follow Ariva's clock, read from the Date header of its answers, not the tablet's: the desk tests serve that
// header from the page's clock with a skew of their choosing (TabletClock). Desk states are border data: a campaign's
// desks are listed only to an account that may observe them, so an observer who also holds an airport role sees no desk
// log. Desk codes cannot hold markup (TopologyCodes), so the XSS probes rewrite the answers that carry them.

test.skip(!databaseAvailable, 'validation capture needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const site = 'E2EO';
const admin = `${hosts.main}/api/v1/admin`;
const campaignsUrl = `${hosts.main}/api/v1/sites/${site}/validation/campaigns`;
const captureUrl = `${hosts.main}/api/v1/sites/${site}/validation/capture/campaigns`;
const quarter = 15 * 60_000;

/** User text that would run if it were rendered as markup. */
const zoneXss = `Q-B ${xssPayloads[1]}`;
const lineXss = `${xssPayloads[0]} entry`;
/** Elements and attributes a successful injection would create. */
const injectedSelectors = ['img[src="x"]', 'svg[onload]', 'iframe', '[onerror]', '[onload]', 'a[href^="javascript:"]'];

const dubaiDay = (at: number) =>
	new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(at));
const dubaiClock = (at: number) =>
	new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Dubai', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(new Date(at));
/** A count sent by the tablet (not a correction). */
const countsPost = /\/validation\/capture\/campaigns\/[^/]+\/counts$/;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

interface Profile {
	queueA: string;
	queueB: string;
	entryA: string;
	exitA: string;
	xssLine: string;
	version: number;
}

let administrator: string, manager: string, lead: string, observer: string;
let profile: Profile;
/** E2EO's border desks OD01 to OD05 at checkpoint OIMM (ARV-104d), by code. */
let desks: Record<string, string>;

/** E2EO's published zone profile: Q-A with Entry A and Exit A, and a queue zone and a line named with markup. */
async function ensureProfile(): Promise<Profile> {
	const api = `${admin}/zone-profiles`;
	const history = await (await call('GET', `${api}?siteCode=${site}`, { token: manager })).json();
	let published = history.find((p: any) => p.status === 'Published');
	if (!published) {
		for (const old of history.filter((p: any) => p.status === 'Draft')) await call('DELETE', `${api}/${old.id}`, { token: administrator });
		const create = async (entity: string, data: unknown) => {
			const response = await call('POST', `${admin}/${entity}`, { token: administrator, data });
			expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
			return (await response.json()).id as string;
		};
		const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
		const iata = Array.from({ length: 3 }, () => letters[Math.floor(Math.random() * 26)]).join('');
		const airport = await create('airports', { iataCode: iata, name: `E2E tablet ${iata}`, timeZoneId: 'Asia/Dubai' });
		const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'Terminal 1', siteCode: site });
		const levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });

		const draft = await call('POST', `${api}/drafts`, { token: manager, data: { siteCode: site, name: 'Tablet hall' } });
		expect(draft.status(), await draft.text()).toBe(201);
		const id = (await draft.json()).profile.id as string;
		const zone = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/zones`, { token: manager, data: { levelId, ...data } });
			expect(response.status(), await response.text()).toBe(201);
			return (await response.json()).id as string;
		};
		const line = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/lines`, { token: manager, data: { levelId, ...data } });
			expect(response.status(), await response.text()).toBe(201);
		};
		const queueA = await zone({ name: 'Q-A', kind: 'Queue', polygon: '10 10,34 10,34 22,10 22' });
		const queueB = await zone({ name: zoneXss, kind: 'Queue', polygon: '50 10,74 10,74 22,50 22' });
		await line({ name: 'Entry A', role: 'Entry', startX: 10, startY: 12, endX: 10, endY: 16, zoneId: queueA });
		await line({ name: 'Exit A', role: 'Exit', startX: 30, startY: 22, endX: 34, endY: 22, zoneId: queueA });
		await line({ name: lineXss, role: 'Entry', startX: 50, startY: 12, endX: 50, endY: 16, zoneId: queueB });
		await line({ name: 'Exit B', role: 'Exit', startX: 70, startY: 22, endX: 74, endY: 22, zoneId: queueB });
		const review = await (await call('GET', `${api}/${id}/validation`, { token: manager })).json();
		expect(review.publishable, JSON.stringify(review)).toBe(true);
		const publish = await call('POST', `${api}/${id}/publish`, { token: manager, data: { geometryHash: review.geometryHash } });
		expect(publish.status(), await publish.text()).toBe(200);
		published = await publish.json();
	}
	const view = await (await call('GET', `${api}/${published.id}`, { token: manager })).json();
	const zoneId = (name: string) => view.zones.find((z: any) => z.name === name).id as string;
	const lineId = (name: string) => view.lines.find((l: any) => l.name === name).id as string;
	return {
		version: published.version,
		queueA: zoneId('Q-A'),
		queueB: zoneId(zoneXss),
		entryA: lineId('Entry A'),
		exitA: lineId('Exit A'),
		xssLine: lineId(lineXss)
	};
}

/** Plans a campaign over both queue zones and three lines for yesterday and today (Dubai), and starts it. */
async function runningCampaign(name: string, token = lead): Promise<string> {
	const created = await call('POST', campaignsUrl, {
		token,
		data: {
			name,
			profileVersion: profile.version,
			zoneIds: [profile.queueA, profile.queueB],
			lineIds: [profile.entryA, profile.exitA, profile.xssLine],
			days: [dubaiDay(Date.now() - 86_400_000), dubaiDay(Date.now())]
		}
	});
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id as string;
	const started = await call('POST', `${campaignsUrl}/${id}/start`, { token });
	expect(started.status(), await started.text()).toBe(200);
	return id;
}

// ARV-104d: desks and the tablet's clock.

const minute = 60_000;
/** The validation API's answers: their Date header is Ariva's clock for the desk log. */
const validationApi = /\/api\/v1\/sites\/[^/]+\/validation\//;
/** A desk batch sent by the tablet (not a correction). */
const deskPost = /\/validation\/capture\/campaigns\/[^/]+\/desk-observations$/;
const deskCorrections = /\/desk-observations\/[^/]+\/corrections$/;
/** The observer's own desk states (a GET with a query). */
const deskReads = /\/validation\/capture\/campaigns\/[^/]+\/desk-observations\?/;
const deskCodes = ['OD01', 'OD02', 'OD03', 'OD04', 'OD05'];
const deskStateCycle = ['Serving', 'Idle', 'Paused', 'Closed'] as const;
/** What the tablet says while it waits for Ariva's clock (ARV-104d, ARV-104c1: the tally and the desk log alike). */
const clockLostText =
	"The tablet's clock changed (set by hand or from the network, or the tablet slept), so Ariva's clock is being read again. Until it is, nothing is counted or logged and no bin is closed; the desk minutes logged before are kept, and the line bin being counted will not be sent.";
const clockImplausibleText =
	"The tablet's clock is more than 15 minutes from Ariva's. Set the tablet's date and time to automatic: until the difference is under 15 minutes, nothing is counted or logged and no bin is closed.";
/** UTC ISO 8601 without milliseconds, as the tablet sends bin starts and the API answers minutes. */
const isoMinute = (at: number) => new Date(at).toISOString().replace('.000Z', 'Z');

/** E2EO's border checkpoint OIMM with desks OD01 to OD05, created by the administrator the first time. */
async function ensureDesks(): Promise<Record<string, string>> {
	const list = async (entity: string, query: string) => {
		const response = await call('GET', `${admin}/${entity}?${query}`, { token: administrator });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(200);
		const body = await response.json();
		return (body.data ?? body) as any[];
	};
	const create = async (entity: string, data: unknown) => {
		const response = await call('POST', `${admin}/${entity}`, { token: administrator, data });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
		return response.json();
	};
	const level = (await list('levels', `siteCode=${site}&pageSize=10`))[0];
	const checkpoints = await list('checkpoints', `siteCode=${site}&pageSize=100`);
	const border = checkpoints.find((c) => c.code === 'OIMM') ?? (await create('checkpoints', { levelId: level.id, code: 'OIMM', name: 'Tablet immigration', kind: 'Immigration' }));
	const existing = await list('desks', `parentId=${border.id}&pageSize=100`);
	const ids: Record<string, string> = {};
	for (const code of deskCodes)
		ids[code] = (existing.find((d) => d.code === code) ?? (await create('desks', { checkpointId: border.id, code, kind: 'Desk', laneCategories: ['CIT'] }))).id;
	return ids;
}

/** A running campaign with OD01 to OD05 in scope: planned by the border manager (desks are border data), started by the lead. */
async function deskCampaign(name: string): Promise<string> {
	const created = await call('POST', campaignsUrl, {
		token: manager,
		data: {
			name,
			profileVersion: profile.version,
			zoneIds: [profile.queueA, profile.queueB],
			lineIds: [profile.entryA, profile.exitA, profile.xssLine],
			days: [dubaiDay(Date.now() - 86_400_000), dubaiDay(Date.now())],
			deskIds: deskCodes.map((code) => desks[code])
		}
	});
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id as string;
	const started = await call('POST', `${campaignsUrl}/${id}/start`, { token: lead });
	expect(started.status(), await started.text()).toBe(200);
	return id;
}

/**
 * The tablet's clock in the desk tests: Playwright's clock on the page, and Ariva's clock as the Date header of every
 * validation answer gives it, `skewMs` behind the tablet's. Ariva's real clock runs an hour or so later, so every minute
 * the page logs has ended on the server. While the page's clock is paused the test moves it (runToAriva), and an answer
 * waits until a move has finished, so its Date header and the page's clock agree to the second.
 */
class TabletClock {
	#shift = 0;
	#paused: number | null = null;
	#busy: Promise<void> = Promise.resolve();

	constructor(
		private readonly page: Page,
		private skewMs: number
	) {}

	/** Ariva's time now, as the answers' Date header gives it. */
	ariva(): number {
		return (this.#paused ?? Date.now() + this.#shift) - this.skewMs;
	}

	/** Installs the page's clock at Ariva's `at` plus the skew; it flows until paused. */
	async install(at: number): Promise<void> {
		await this.page.clock.install({ time: at + this.skewMs });
		this.#shift = at + this.skewMs - Date.now();
	}

	async pauseAtAriva(at: number): Promise<void> {
		await this.page.clock.pauseAt(at + this.skewMs);
		this.#paused = at + this.skewMs;
	}

	/** Moves the paused clock to Ariva's `at`, firing every timer on the way. */
	async runToAriva(at: number): Promise<void> {
		const from = this.#paused;
		if (from === null) throw new Error('pause the clock first');
		const target = at + this.skewMs;
		if (target <= from) return;
		let done = () => {};
		this.#busy = new Promise<void>((resolve) => (done = resolve));
		try {
			await this.page.clock.runFor(target - from);
			this.#paused = target;
		} finally {
			done();
		}
	}

	/**
	 * The tablet's own clock jumps by `byMs` (set by hand or from the network) while Ariva's runs on: the page's wall clock
	 * moves, its monotonic clock (performance.now) does not.
	 */
	async jumpTablet(byMs: number): Promise<void> {
		if (this.#paused === null) throw new Error('pause the clock first');
		await this.page.clock.setSystemTime(this.#paused + byMs);
		this.#paused += byMs;
		this.skewMs += byMs;
	}

	/** Lets the page's clock flow again (from where it was paused). */
	async resume(): Promise<void> {
		await this.page.clock.resume();
		this.#shift = (this.#paused ?? Date.now() + this.#shift) - Date.now();
		this.#paused = null;
	}

	/** Answers a request from Ariva with its Date header on this clock, the JSON body changed by `change` if given. */
	async answer(route: Route, change?: (body: any) => unknown): Promise<void> {
		await this.#busy;
		const response = await route.fetch();
		const headers: Record<string, string> = { ...response.headers(), date: new Date(this.ariva()).toUTCString() };
		delete headers['content-encoding'];
		delete headers['content-length'];
		if (!change) return route.fulfill({ response, headers });
		return route.fulfill({ response, headers, body: JSON.stringify(change(await response.json())) });
	}

	/** Every validation answer carries this clock's Date header. */
	async serveDates(): Promise<void> {
		await this.page.route(validationApi, (route) => this.answer(route));
	}
}

/** A desk card of the running log, by its code at OIMM. */
const deskCard = (page: Page, code: string) => page.locator(`[data-testid="desk-card"][data-desk="OIMM ${code}"]`);
const tapState = (page: Page, code: string, state: string) => deskCard(page, code).locator(`[data-testid="desk-state"][data-state="${state}"]`).click();
const currentMinute = (page: Page) => page.locator('[data-testid="desk-minute"][aria-current="time"]');

/** Opens the capture screen and chooses the campaign by its name. */
async function openCampaign(page: Page, name: string): Promise<void> {
	await page.goto('/validation/capture');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Validation capture');
	await page.getByTestId('campaign-option').filter({ hasText: name }).click();
	await expect(page.getByTestId('chosen-campaign')).toHaveText(name);
}

async function expectNothingInjected(page: Page): Promise<void> {
	for (const selector of injectedSelectors) await expect(page.locator(selector), selector).toHaveCount(0);
}

/** Drops the console lines of a request this test cut on purpose (Chromium logs the failed fetch). */
function allowCutRequests(guards: PageGuards): void {
	const unexpected = guards.consoleErrors.filter((message) => !/net::ERR_/.test(message));
	guards.consoleErrors.splice(0, guards.consoleErrors.length, ...unexpected);
}

async function expectAxeClean(page: Page): Promise<void> {
	const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
	expect(
		results.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`),
		'axe violations'
	).toEqual([]);
}

/**
 * Nothing on the screen reaches past either edge of the viewport (in Arabic an overflow goes past the left edge, where
 * the page cannot scroll, so the scroll width alone does not show it). Tables scroll inside their own frame.
 */
async function expectInsideViewport(page: Page): Promise<void> {
	const outside = await page.evaluate(() =>
		Array.from(document.querySelectorAll('main *'))
			.filter((element) => !element.closest('.overflow-x-auto, .sr-only'))
			.filter((element) => {
				const box = element.getBoundingClientRect();
				return box.width > 0 && (box.left < -1 || box.right > window.innerWidth + 1);
			})
			.map((element) => `${element.tagName.toLowerCase()}${element.getAttribute('data-testid') ? `[${element.getAttribute('data-testid')}]` : ''}`)
	);
	expect(outside, 'elements outside the viewport').toEqual([]);
}

/** Keeps a full-page screenshot with the test's output (for review) and attaches it to the report. */
async function keep(page: Page, testInfo: TestInfo, name: string): Promise<void> {
	const target = testInfo.outputPath(`${name}.png`);
	await page.screenshot({ path: target, fullPage: true });
	await testInfo.attach(name, { path: target });
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().webValidationManager;
	const signedIn = await login(userName, password, undefined, undefined, { code: await unusedTotpCode(totpSecret!) });
	expect(signedIn.status(), 'the validation manager signs in with a second factor').toBe(200);
	manager = (await signedIn.json()).accessToken;
	[administrator, lead, observer] = await Promise.all(
		[accounts().SystemAdministrator, accounts().webValidationLead, accounts().webObserver].map(async (a) => (await signIn(a)).accessToken)
	);
	profile = await ensureProfile();
	desks = await ensureDesks();
});

test('an observer tallies a line per bin; each ended bin is sent with its own key, and a lost answer is retried without counting twice', async ({ page }, testInfo) => {
	const name = `Tablet tally ${Date.now()}`;
	const campaign = await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation capture', exact: true })).toBeVisible();

	// The page runs an hour behind, so the bins it closes have ended on the server's clock too, on a planned day; and the
	// tablet's own clock runs 2.5 minutes ahead of Ariva's (the Date header of its answers): the bins are Ariva's quarter
	// hours, not the tablet's (ARV-104c1).
	const first = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	const tablet = new TabletClock(page, 150_000);
	await tablet.install(first - 90_000);
	await tablet.serveDates();
	const posts: { key: string | null; body: any }[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && /\/validation\/capture\/campaigns\/[^/]+\/counts$/.test(request.url()))
			posts.push({ key: request.headers()['idempotency-key'] ?? null, body: request.postDataJSON() });
	});
	await openCampaign(page, name);
	const storage = () => page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
	const storedBefore = await storage();

	await page.getByTestId('line-option').filter({ hasText: 'Entry A' }).click();
	await expect(page.getByTestId('counting-line')).toHaveText('Counting Entry A');
	// Counting started inside a bin: that part bin is shown as such and never sent.
	await expect(page.getByTestId('part-bin')).toBeVisible();
	await page.getByTestId('tally-in').click();
	await tablet.pauseAtAriva(first - 30_000);
	await tablet.runToAriva(first + 2_000);
	await expect(page.getByTestId('skipped-bin')).toContainText(`The bin from ${dubaiClock(first - quarter)} was a part bin and was not sent.`);
	await expect(page.getByTestId('part-bin')).toHaveCount(0);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first)} to ${dubaiClock(first + quarter)}`);
	expect(posts, 'a part bin is never sent').toEqual([]);

	// A full bin: 3 in, 1 out (an extra In undone).
	for (let i = 0; i < 4; i++) await page.getByTestId('tally-in').click();
	await page.getByTestId('undo-in').click();
	await page.getByTestId('tally-out').click();
	await expect(page.getByTestId('tally-in-count')).toHaveText('3');
	await expect(page.getByTestId('tally-out-count')).toHaveText('1');
	await expect(page.getByTestId('ariva-clock-off')).toContainText(/The tablet's clock is 2 min (29|30|31) s ahead of Ariva's/);
	// The tablet's own quarter hour passes at Ariva's 12.5 minutes: the bin goes on and nothing is sent until Ariva's ends.
	await tablet.runToAriva(first + quarter - 150_000 + 10_000);
	expect(posts, "nothing is sent at the tablet's own quarter hour").toEqual([]);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first)} to ${dubaiClock(first + quarter)}`);
	await expect(page.getByTestId('tally-in-count')).toHaveText('3');
	await tablet.runToAriva(first + quarter + 2_000);
	await expect(page.getByTestId('history-row')).toHaveCount(1);
	await expect(page.getByTestId('history-in').first()).toHaveText('3');
	await expect(page.getByTestId('history-out').first()).toHaveText('1');
	await expect(page.getByTestId('tally-in-count')).toHaveText('0');

	// The next bin's answer is lost after the server recorded it: the bin waits behind the retry banner.
	let cut = 0;
	await page.route(/\/validation\/capture\/campaigns\/[^/]+\/counts$/, async (route) => {
		if (route.request().method() !== 'POST') return route.continue();
		cut++;
		await route.fetch();
		await route.abort('connectionreset');
	});
	await page.getByTestId('tally-in').click();
	await page.getByTestId('tally-in').click();
	await tablet.runToAriva(first + 2 * quarter + 2_000);
	await expect(page.getByTestId('unsent-banner')).toBeVisible();
	await expect(page.getByTestId('unsent-item')).toHaveCount(1);
	await expect(page.getByTestId('unsent-item')).toContainText(`Entry A, bin ${dubaiClock(first + quarter)}`);
	await expect(page.getByTestId('unsent-item')).toContainText('No connection to Ariva');
	expect(cut).toBe(1);
	// The unsent bin and its key live in memory only: web storage is as it was.
	expect(await storage()).toBe(storedBefore);
	expect(await storage()).not.toContain(posts[1].key);
	await keep(page, testInfo, 'tally-unsent');
	// The campaign cannot be changed while one of its bins is unsent (the banner would leave with it).
	await expect(page.getByTestId('change-campaign')).toBeDisabled();

	// Leaving the screen now would lose the bin: the screen holds the navigation and asks. SvelteKit follows a link only
	// after the next frame (at most 100 ms, on the page's clock), and this test holds that clock paused: run it on a moment.
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Account security' }).click();
	await tablet.runToAriva(tablet.ariva() + 200);
	await expect(page.getByTestId('leave-warning')).toBeVisible();
	await page.getByTestId('leave-stay').click();
	await expect(page).toHaveURL(/\/validation\/capture$/);

	// The connection is back: the retry timer (10 s, doubling) sends the bin again on its own, with the same key.
	await page.unroute(/\/validation\/capture\/campaigns\/[^/]+\/counts$/);
	await tablet.runToAriva(first + 2 * quarter + 33_000);
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	await expect(page.getByTestId('history-row')).toHaveCount(2);

	// The first bin once; the second bin as often as it was sent, always with the lost send's key; every key a random UUID.
	const bin = (at: number, crossingsIn: number, crossingsOut: number) => ({
		lineId: profile.entryA,
		binStartUtc: new Date(at).toISOString().replace('.000Z', 'Z'),
		crossingsIn,
		crossingsOut
	});
	expect(posts[0].body).toEqual(bin(first, 3, 1));
	expect(posts.length).toBeGreaterThanOrEqual(3);
	for (const post of posts.slice(1)) {
		expect(post.body).toEqual(bin(first + quarter, 2, 0));
		expect(post.key).toBe(posts[1].key);
	}
	for (const post of posts) expect(post.key).toMatch(uuid);
	expect(posts[1].key).not.toBe(posts[0].key);

	// The server holds one count per bin, never two.
	const stored = await (await call('GET', `${captureUrl}/${campaign}/counts?currentOnly=false&pageSize=100`, { token: observer })).json();
	expect(stored.data.map((c: any) => [c.binStartUtc, c.crossingsIn, c.crossingsOut, c.revision]).sort()).toEqual(
		[
			[new Date(first).toISOString().replace('.000Z', 'Z'), 3, 1, 1],
			[new Date(first + quarter).toISOString().replace('.000Z', 'Z'), 2, 0, 1]
		].sort()
	);

	// A correction is a new revision with a reason; the reason is user text, shown as text.
	const reason = `${xssPayloads[4]} missed group`;
	await page.getByTestId('history-row').filter({ hasText: dubaiClock(first) }).first().getByTestId('correct-count').click();
	await expect(page.getByTestId('correction-form')).toContainText('Never name a person.');
	await page.getByTestId('correction-in').fill('4');
	await page.getByTestId('correction-reason').fill(reason);
	await page.getByTestId('correction-save').click();
	const corrected = page.getByTestId('history-row').filter({ hasText: 'missed group' });
	await expect(corrected).toHaveCount(1);
	await expect(corrected.getByTestId('history-reason')).toHaveText(reason);
	await expect(corrected.getByTestId('history-in')).toHaveText('4');
	await expectNothingInjected(page);

	await page.getByTestId('stop-counting').click();
	await page.getByTestId('confirm-stop-counting').click();
	await expect(page.getByTestId('skipped-bin')).toContainText('Counting stopped');
	await expect(page.getByTestId('line-option')).toHaveCount(3);

	allowCutRequests(guards);
	await guards.expectClean();
});

test('a correction whose answer is lost is frozen and sent again as it is; a key used for another correction is refused in plain words', async ({ page }) => {
	const name = `Tablet correction ${Date.now()}`;
	const campaign = await runningCampaign(name);
	// A count of the last bin that has ended, recorded by the observer before the screen opens.
	const binUtc = new Date(Math.floor(Date.now() / quarter) * quarter - quarter).toISOString().replace('.000Z', 'Z');
	const created = await call('POST', `${captureUrl}/${campaign}/counts`, {
		token: observer,
		data: { lineId: profile.entryA, binStartUtc: binUtc, crossingsIn: 5, crossingsOut: 2 }
	});
	expect(created.status(), await created.text()).toBe(201);
	const revisions = async () =>
		((await (await call('GET', `${captureUrl}/${campaign}/counts?currentOnly=false&pageSize=100`, { token: observer })).json()).data as any[])
			.map((c) => [c.revision, c.crossingsIn, c.reason])
			.sort((a, b) => a[0] - b[0]);

	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const sent: { key: string | null; body: any }[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && /\/corrections$/.test(request.url()))
			sent.push({ key: request.headers()['idempotency-key'] ?? null, body: request.postDataJSON() });
	});
	await openCampaign(page, name);
	await page.getByTestId('line-option').filter({ hasText: 'Entry A' }).click();
	await expect(page.getByTestId('history-row')).toHaveCount(1);

	// The answer is lost after Ariva recorded the correction.
	await page.route(/\/corrections$/, async (route) => {
		await route.fetch();
		await route.abort('connectionreset');
	});
	await page.getByTestId('correct-count').click();
	await page.getByTestId('correction-in').fill('6');
	await page.getByTestId('correction-reason').fill('A group of three missed');
	await page.getByTestId('correction-save').click();
	await expect(page.getByTestId('correction-problem')).toContainText('may or may not be saved');
	// Frozen: the body that went with the key cannot change; the ways on are Send again (as it is) and Cancel.
	for (const id of ['correction-in', 'correction-out', 'correction-reason']) await expect(page.getByTestId(id), id).not.toBeEditable();
	await expect(page.getByTestId('correction-save')).toHaveText('Send again');
	await page.unroute(/\/corrections$/);
	await page.getByTestId('correction-save').click();
	await expect(page.getByTestId('correction-form')).toHaveCount(0);
	await expect(page.getByTestId('history-in')).toHaveText(['6']);
	await expect(page.getByTestId('history-reason')).toHaveText(['A group of three missed']);
	expect(sent).toHaveLength(2);
	expect(sent[0].key).toMatch(uuid);
	expect(sent[1].key, 'Send again keeps the key').toBe(sent[0].key);
	expect(sent[1].body, 'and the body').toEqual(sent[0].body);
	expect(sent[0].body).toEqual({ crossingsIn: 6, crossingsOut: 2, reason: 'A group of three missed' });
	expect(await revisions(), 'recorded once').toEqual([
		[1, 5, null],
		[2, 6, 'A group of three missed']
	]);

	// Another correction goes out with the form's key first: Ariva refuses the form's own body (409) and the screen says so.
	let raced = 0;
	await page.route(/\/corrections$/, async (route) => {
		const other = await call('POST', `${hosts.main}${new URL(route.request().url()).pathname}`, {
			token: observer,
			headers: { 'Idempotency-Key': route.request().headers()['idempotency-key'] ?? '' },
			data: { crossingsIn: 7, crossingsOut: 2, reason: 'Recounted from the video' }
		});
		raced = other.status();
		await route.continue();
	});
	await page.getByTestId('correct-count').click();
	await page.getByTestId('correction-in').fill('8');
	await page.getByTestId('correction-reason').fill('Two more people');
	await page.getByTestId('correction-save').click();
	await expect(page.getByTestId('correction-notice')).toHaveText(
		"Ariva already holds another correction sent with this form's key, so this one was not saved. Your counts are shown again: correct the latest revision if it is still wrong."
	);
	expect(raced).toBe(201);
	await expect(page.getByTestId('correction-form')).toHaveCount(0);
	await expect(page.getByTestId('history-reason')).toHaveText(['Recounted from the video']);
	expect(await revisions()).toEqual([
		[1, 5, null],
		[2, 6, 'A group of three missed'],
		[3, 7, 'Recounted from the video']
	]);

	allowCutRequests(guards);
	allowStatuses(guards, 409);
	await guards.expectClean();
});

test('signing out drops the unsent bins: nothing is sent afterwards, and nothing under the next account on the tablet', async ({ page }) => {
	test.setTimeout(120_000);
	const name = `Tablet sign-out ${Date.now()}`;
	const campaign = await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const first = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	const tablet = new TabletClock(page, 0);
	await tablet.install(first - 90_000);
	await tablet.serveDates();
	const posts: unknown[] = [];
	page.on('request', (request: Request) => {
		if (request.method() !== 'POST' || !countsPost.test(request.url())) return;
		const token = (request.headers()['authorization'] ?? '').replace(/^Bearer /, '');
		posts.push(token ? claimsOf(token).sub : null);
	});
	await openCampaign(page, name);
	const storage = () => page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
	const storedBefore = await storage();

	// No count reaches Ariva: every send is cut, and the one held below is answered only once the next account is in.
	let holdNext = false;
	let release = () => {};
	const released = new Promise<void>((resolve) => (release = resolve));
	let heldSeen = () => {};
	const held = new Promise<void>((resolve) => (heldSeen = resolve));
	await page.route(countsPost, async (route) => {
		if (route.request().method() !== 'POST') return route.fallback();
		if (holdNext) {
			holdNext = false;
			heldSeen();
			await released;
			// A success, as if Ariva had recorded the bin: the old send loop then went on to the next bin with the token in use.
			return route
				.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify({ id: '00000000-0000-4000-8000-000000000001', crossingsIn: 1, crossingsOut: 0 }) })
				.catch(() => {});
		}
		return route.abort('connectionreset');
	});
	await page.getByTestId('line-option').filter({ hasText: 'Entry A' }).click();
	await tablet.pauseAtAriva(first - 30_000);
	await tablet.runToAriva(first + 2_000);
	await page.getByTestId('tally-in').click();
	await tablet.runToAriva(first + quarter + 2_000);
	await page.getByTestId('tally-out').click();
	await tablet.runToAriva(first + 2 * quarter + 2_000);
	await expect(page.getByTestId('unsent-item')).toHaveCount(2);

	// A retry is out when the observer signs out; the next observer signs in on the same screen before it is answered.
	await tablet.resume();
	holdNext = true;
	await page.getByTestId('retry-unsent').click();
	await held;
	const sentBefore = posts.length;
	const observerA = claimsOf(observer).sub;
	expect(posts.every((subject) => subject === observerA)).toBe(true);
	await page.getByTestId('sign-out').click();
	await expect(page).toHaveURL(/\/login/);
	await fillSignIn(page, accounts().webObserver2);
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
	await expect(page.getByTestId('user-card')).toContainText('e2e.webobserver2');
	release();

	// Nothing more leaves, at once or on the retry timer (up to 5 minutes), and the next observer stays signed in.
	await page.clock.runFor(6 * 60_000);
	await page.waitForTimeout(1_000);
	expect(posts, 'no count is sent after the sign-out').toHaveLength(sentBefore);
	expect(posts.every((subject) => subject === observerA), 'no count under the next account').toBe(true);
	await expect(page.getByTestId('user-card')).toContainText('e2e.webobserver2');

	// The next observer's tablet starts empty, and nothing was written to web storage.
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation capture', exact: true }).click();
	await expect(page.getByTestId('campaign-option').filter({ hasText: name })).toBeVisible();
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	expect(await storage()).toBe(storedBefore);
	expect(posts).toHaveLength(sentBefore);

	// Ariva holds no count of either observer for the campaign.
	const nextObserver = (await signIn(accounts().webObserver2)).accessToken;
	for (const token of [observer, nextObserver]) {
		const counts = await (await call('GET', `${captureUrl}/${campaign}/counts?pageSize=100`, { token })).json();
		expect(counts.data).toEqual([]);
	}
	allowCutRequests(guards);
	await guards.expectClean();
});

test('a tracer run is timed from Join to Exit and sent with the tablet clock; a lost answer is retried with the same key and a new clock reading', async ({ page }, testInfo) => {
	const name = `Tablet tracers ${Date.now()}`;
	const campaign = await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const posts: { key: string | null; body: any }[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && /\/tracer-runs$/.test(request.url()))
			posts.push({ key: request.headers()['idempotency-key'] ?? null, body: request.postDataJSON() });
	});
	await openCampaign(page, name);
	await page.getByTestId('capture-tab-tracers').click();
	await expect(page.getByTestId('clock-offset-pending')).toBeVisible();

	// Codes are labels from the roster: markup, names and other digits are refused on the tablet and never rendered.
	for (const probe of [...xssPayloads, 'Ahmed', 'T-٠٧', 'T-7', 'T07', 'X-07']) {
		await page.getByTestId('tracer-code').fill(probe);
		await page.getByTestId('tracer-join').click();
		await expect(page.getByTestId('tracer-problem'), probe).toHaveText('A tracer code is T, a hyphen and 2 or 3 digits (T-01 to T-999); never a name.');
		await expect(page.getByTestId('active-run')).toHaveCount(0);
	}
	await expectNothingInjected(page);

	// The zone list shows the zone names as text.
	await expect(page.getByTestId('tracer-zone').locator('option', { hasText: zoneXss })).toHaveCount(1);
	await page.getByTestId('tracer-code').fill('t-07');
	await page.getByTestId('tracer-join').click();
	await expect(page.getByTestId('tracer-problem')).toHaveText('Choose the queue zone the tracer joins.');
	await page.getByTestId('tracer-zone').selectOption({ label: 'Q-A' });
	await page.getByTestId('tracer-join').click();
	await expect(page.getByTestId('active-code')).toHaveText(['T-07']);
	await page.getByTestId('tracer-code').fill('T-07');
	await page.getByTestId('tracer-join').click();
	await expect(page.getByTestId('tracer-problem')).toHaveText('This tracer is already in a queue; record their exit first.');

	// The answer to the first send is lost after the server recorded it.
	await page.route(/\/tracer-runs$/, async (route) => {
		if (route.request().method() !== 'POST') return route.continue();
		await route.fetch();
		await route.abort('connectionreset');
	});
	await page.waitForTimeout(1_500);
	await page.getByRole('button', { name: 'T-07 exits, served' }).click();
	await expect(page.getByTestId('active-run')).toHaveCount(0);
	await expect(page.getByTestId('unsent-item')).toHaveCount(1);
	await expect(page.getByTestId('unsent-item')).toContainText('T-07 in Q-A');
	await keep(page, testInfo, 'tracers-unsent');
	await expect(page.getByTestId('unsent-item')).toContainText('No connection to Ariva');
	await page.unroute(/\/tracer-runs$/);
	await page.waitForTimeout(1_100);
	await page.getByTestId('retry-unsent').click();
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	await expect(page.getByTestId('recorded-code')).toHaveText(['T-07']);
	await expect(page.getByTestId('clock-offset-value')).toContainText("s against Ariva's clock");

	expect(posts).toHaveLength(2);
	expect(posts[0].key).toMatch(uuid);
	expect(posts[1].key, 'a retry keeps its batch key').toBe(posts[0].key);
	expect(posts[1].body.runs).toEqual(posts[0].body.runs);
	expect(posts[0].body.runs[0]).toMatchObject({ zoneId: profile.queueA, tracerCode: 'T-07', abandoned: false });
	expect(Date.parse(posts[1].body.deviceClockUtc), 'the clock is read again at every send').toBeGreaterThan(Date.parse(posts[0].body.deviceClockUtc));

	// A tracer who leaves without being served; a run started by mistake is dropped and never sent.
	await page.getByTestId('tracer-code').fill('T-08');
	await page.getByTestId('tracer-join').click();
	await page.getByTestId('tracer-code').fill('T-09');
	await page.getByTestId('tracer-join').click();
	await page.getByTestId('active-run').filter({ hasText: 'T-09' }).getByTestId('cancel-run').click();
	await page.getByTestId('confirm-cancel-run').click();
	await expect(page.getByTestId('active-code')).toHaveText(['T-08']);
	await page.waitForTimeout(1_100);
	await page.getByRole('button', { name: 'T-08 left without being served' }).click();
	await expect(page.getByTestId('recorded-code')).toHaveText(['T-08', 'T-07']);
	await expect(page.getByTestId('recorded-run').filter({ hasText: 'T-08' })).toContainText('Abandoned');
	await keep(page, testInfo, 'tracers-recorded');
	expect(posts).toHaveLength(3);

	// The server holds each run once.
	const runs = await (await call('GET', `${captureUrl}/${campaign}/tracer-runs?pageSize=100`, { token: observer })).json();
	expect(runs.data.map((r: any) => [r.tracerCode, r.abandoned]).sort()).toEqual([
		['T-07', false],
		['T-08', true]
	]);
	allowCutRequests(guards);
	await guards.expectClean();
});

test('a send without an answer within 30 seconds is kept, then sent again on its own after the retry delay', async ({ page }) => {
	test.setTimeout(120_000);
	const name = `Tablet timeout ${Date.now()}`;
	const campaign = await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const posts: { key: string | null; clock: number }[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && /\/tracer-runs$/.test(request.url()))
			posts.push({ key: request.headers()['idempotency-key'] ?? null, clock: Date.parse(request.postDataJSON().deviceClockUtc) });
	});
	// The first send is never answered.
	let release = () => {};
	const released = new Promise<void>((resolve) => (release = resolve));
	let calls = 0;
	await page.route(/\/tracer-runs$/, async (route) => {
		if (route.request().method() !== 'POST') return route.continue();
		if (++calls === 1) {
			await released;
			return route.abort('timedout').catch(() => {});
		}
		return route.continue();
	});
	await openCampaign(page, name);
	await page.getByTestId('capture-tab-tracers').click();
	await page.getByTestId('tracer-code').fill('T-13');
	await page.getByTestId('tracer-zone').selectOption({ label: 'Q-A' });
	await page.getByTestId('tracer-join').click();
	await page.waitForTimeout(1_100);
	const exited = Date.now();
	await page.getByRole('button', { name: 'T-13 exits, served' }).click();
	await expect(page.getByTestId('unsent-item')).toContainText('T-13 in Q-A: Sending');
	await expect(page.getByTestId('unsent-item')).toContainText('No connection to Ariva', { timeout: 45_000 });
	expect(Date.now() - exited, 'given up after 30 seconds').toBeGreaterThanOrEqual(29_000);
	release();

	// No click: the retry timer sends it again 10 seconds later, with the same key and the clock read again.
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0, { timeout: 25_000 });
	await expect(page.getByTestId('recorded-code')).toHaveText(['T-13']);
	expect(posts).toHaveLength(2);
	expect(posts[1].key).toBe(posts[0].key);
	expect(posts[1].clock - posts[0].clock).toBeGreaterThanOrEqual(35_000);
	const runs = await (await call('GET', `${captureUrl}/${campaign}/tracer-runs?pageSize=100`, { token: observer })).json();
	expect(runs.data.map((r: any) => r.tracerCode)).toEqual(['T-13']);
	allowCutRequests(guards);
	await guards.expectClean();
});

test('a tablet whose clock is more than 5 minutes off is told to set its time automatically', async ({ page }) => {
	const name = `Tablet clock ${Date.now()}`;
	await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	await page.clock.install({ time: Date.now() - 10 * 60_000 });
	await openCampaign(page, name);
	await page.getByTestId('capture-tab-tracers').click();
	await page.getByTestId('tracer-code').fill('T-11');
	await page.getByTestId('tracer-zone').selectOption({ label: 'Q-A' });
	await page.getByTestId('tracer-join').click();
	await page.clock.runFor(2_000);
	await page.getByRole('button', { name: 'T-11 exits, served' }).click();
	await expect(page.getByTestId('refused-item')).toContainText('Set the tablet\'s date and time to automatic');
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	await page.getByTestId('discard-refused').click();
	await expect(page.getByTestId('refused-item')).toHaveCount(0);
	allowStatuses(guards, 400);
	await guards.expectClean();
});

test('the account that started a campaign is refused on it with a plain message', async ({ page }) => {
	const name = `Tablet own ${Date.now()}`;
	const dual = (await signIn(accounts().webValidationDual)).accessToken;
	await runningCampaign(name, dual);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webValidationDual);
	await openCampaign(page, name);
	await page.getByTestId('capture-tab-tracers').click();
	await page.getByTestId('tracer-code').fill('T-12');
	await page.getByTestId('tracer-zone').selectOption({ label: 'Q-A' });
	await page.getByTestId('tracer-join').click();
	await page.waitForTimeout(1_100);
	await page.getByRole('button', { name: 'T-12 exits, served' }).click();
	await expect(page.getByTestId('refused-item')).toContainText('You created or started this campaign, so you cannot count for it');
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	allowStatuses(guards, 403);
	await guards.expectClean();
});

test('campaign, zone and line names render as text', async ({ page }) => {
	const name = `${xssPayloads[2]} Tablet names ${Date.now()}`;
	await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	await page.goto('/validation/capture');
	await expect(page.getByTestId('campaign-name').filter({ hasText: name })).toHaveText(name);
	await page.getByTestId('campaign-option').filter({ hasText: name }).click();
	await expect(page.getByTestId('chosen-campaign')).toHaveText(name);
	await expect(page.getByTestId('line-option').filter({ hasText: lineXss })).toContainText(`Entry line, ${zoneXss}`);
	await page.getByTestId('line-option').filter({ hasText: lineXss }).click();
	await expect(page.getByTestId('counting-line')).toHaveText(`Counting ${lineXss}`);
	await expectNothingInjected(page);
	await page.getByTestId('stop-counting').click();
	await page.getByTestId('confirm-stop-counting').click();
	await guards.expectClean();
});

// ARV-104d: the desk state log.

test("an observer logs four desks minute by minute on Ariva's clock; each bin is sent once it has ended, and a lost answer is sent again with the same key", async ({
	page
}, testInfo) => {
	test.setTimeout(240_000);
	const name = `Tablet desks ${Date.now()}`;
	const campaign = await deskCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);

	// Ariva's bin an hour back; the tablet's own clock runs 2.5 minutes ahead of Ariva's (the Date header of its answers).
	const bin = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	const tablet = new TabletClock(page, 150_000);
	await tablet.install(bin - 2 * minute);
	await tablet.serveDates();
	const posts: { key: string | null; body: any }[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && deskPost.test(request.url()))
			posts.push({ key: request.headers()['idempotency-key'] ?? null, body: request.postDataJSON() });
	});
	const storage = () => page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));

	await page.goto('/validation/capture');
	const option = page.getByTestId('campaign-option').filter({ hasText: name });
	await expect(option.getByTestId('campaign-desks')).toHaveText('5 desks to log');
	await option.click();
	await page.getByTestId('capture-tab-desks').click();
	await expect(page.getByTestId('desk-option')).toHaveCount(5);
	await expect(page.getByTestId('desk-option-code')).toHaveText(deskCodes);
	await expect(page.getByTestId('desk-history-empty')).toBeVisible();
	const storedBefore = await storage();
	for (const code of deskCodes.slice(0, 4)) await page.getByTestId('desk-option').filter({ hasText: code }).click();
	await expect(page.getByTestId('desks-chosen')).toHaveText('4 desks chosen (at most 20)');

	await tablet.pauseAtAriva(bin + 20_000);
	await page.getByTestId('start-desks').click();
	await expect(page.getByTestId('desk-log-title')).toHaveText('Logging 4 desks');
	await expect(page.getByTestId('desk-bin')).toContainText(`Bin ${dubaiClock(bin)} to ${dubaiClock(bin + quarter)}`);
	// The minutes follow Ariva's clock: minute 0 of Ariva's bin is now, though the tablet's own clock reads 2.5 minutes later.
	await expect(page.getByTestId('ariva-clock-off')).toContainText(/The tablet's clock is 2 min (29|30|31) s ahead of Ariva's/);
	await expect(page.getByTestId('ariva-clock-time')).toContainText(new RegExp(`${dubaiClock(bin)}:(19|20)`));
	await expect(page.getByTestId('desk-target')).toHaveText(`Tap the state each desk shows in minute ${dubaiClock(bin)} to ${dubaiClock(bin + minute)}.`);
	await expect(currentMinute(page)).toHaveAttribute('data-minute', '0');
	await expect(page.locator('[data-testid="desk-minute"][data-minute="1"]')).toBeDisabled();

	// Fifteen minutes, four desks, one tap each; OD04 is not watched in minutes 5 and 6 (not observed, sent as null).
	const table: (string | null)[][] = [0, 1, 2, 3].map((d) =>
		Array.from({ length: 15 }, (_, m) => (d === 3 && (m === 5 || m === 6) ? null : deskStateCycle[(d + m) % 4]))
	);
	for (let m = 0; m < 15; m++) {
		await expect(currentMinute(page)).toHaveAttribute('data-minute', String(m));
		for (let d = 0; d < 4; d++) {
			const state = table[d][m];
			if (state) await tapState(page, deskCodes[d], state);
		}
		await expect(deskCard(page, 'OD01').locator('[data-testid="desk-state"][aria-pressed="true"]')).toHaveAttribute('data-state', table[0][m]!);
		if (m === 9) {
			// A wrong tap of an earlier minute is put right before the bin goes: OD02 was Closed in minute 8, not Serving.
			await page.locator('[data-testid="desk-minute"][data-minute="8"]').click();
			await expect(page.getByTestId('desk-earlier')).toContainText(`You are filling in minute ${dubaiClock(bin + 8 * minute)}`);
			await tapState(page, 'OD02', 'Closed');
			table[1][8] = 'Closed';
			await page.getByTestId('desk-back-to-now').click();
			await expect(page.getByTestId('desk-earlier')).toHaveCount(0);
			await expect(deskCard(page, 'OD02').getByTestId('desk-strip-minute').nth(8)).toHaveAttribute('data-state', 'Closed');
		}
		// The tablet's own clock passed its quarter hour at Ariva's 12.5 minutes: nothing goes before Ariva's bin has ended.
		if (m === 13) expect(posts, "nothing is sent before the bin ends on Ariva's clock").toEqual([]);
		if (m < 14) await tablet.runToAriva(bin + (m + 1) * minute + 20_000);
	}
	await expect(deskCard(page, 'OD04').getByTestId('desk-strip-minute').nth(5)).toHaveAttribute('data-state', '');
	await keep(page, testInfo, 'desks-logging');

	await tablet.runToAriva(bin + quarter + 2_000);
	await expect(page.getByTestId('desk-history-row')).toHaveCount(4);
	await expect(page.getByTestId('desk-history-desk')).toHaveText(deskCodes.slice(0, 4).map((code) => `OIMM ${code}`));
	expect(posts).toHaveLength(1);
	expect(posts[0].key).toMatch(uuid);
	expect(posts[0].body).toEqual({
		binStartUtc: isoMinute(bin),
		desks: deskCodes.slice(0, 4).map((code, d) => ({ deskId: desks[code], states: table[d] }))
	});
	// The next bin runs on: the minute in progress is minute 0 again.
	await expect(currentMinute(page)).toHaveAttribute('data-minute', '0');
	await expect(page.getByTestId('desk-bin')).toContainText(`Bin ${dubaiClock(bin + quarter)} to ${dubaiClock(bin + 2 * quarter)}`);

	// Ariva holds the 58 minutes observed, once each; the grid shows them, OD02's minute 8 as corrected before sending.
	const stored = async () =>
		((await (await call('GET', `${captureUrl}/${campaign}/desk-observations?currentOnly=false&pageSize=500`, { token: observer })).json()).data as any[]).map(
			(o) => `${o.deskCode} ${o.minuteUtc} ${o.state} ${o.revision}`
		);
	const first = await stored();
	expect(first).toHaveLength(58);
	expect(first).toContain(`OD02 ${isoMinute(bin + 8 * minute)} Closed 1`);
	expect(first.filter((o) => o.startsWith('OD04'))).toHaveLength(13);
	await expect(
		page.getByTestId('desk-history-row').filter({ hasText: 'OD02' }).locator('[data-testid="desk-history-cell"][data-minute="8"]')
	).toHaveAttribute('data-state', 'Closed');
	await keep(page, testInfo, 'desks-sent');

	// The next bin's answer is lost after Ariva recorded it: the batch waits behind the banner.
	await page.route(deskPost, async (route) => {
		if (route.request().method() !== 'POST') return route.fallback();
		await route.fetch();
		await route.abort('connectionreset');
	});
	await tapState(page, 'OD01', 'Serving');
	await tablet.runToAriva(bin + quarter + minute + 20_000);
	await tapState(page, 'OD01', 'Idle');
	await tablet.runToAriva(bin + 2 * quarter + 2_000);
	await expect(page.getByTestId('unsent-item')).toHaveCount(1);
	await expect(page.getByTestId('unsent-item')).toContainText(`Desk log, bin ${dubaiClock(bin + quarter)} (1 desk)`);
	await expect(page.getByTestId('unsent-item')).toContainText('No connection to Ariva');
	// Nothing is kept on the tablet itself, and the campaign stays while the batch is unsent.
	expect(await storage()).toBe(storedBefore);
	expect(await storage()).not.toContain(posts[1].key);
	await expect(page.getByTestId('change-campaign')).toBeDisabled();
	await keep(page, testInfo, 'desks-unsent');

	// The connection is back: the retry timer (10 s) sends it again on its own, with the same key and the same body.
	await page.unroute(deskPost);
	await tablet.runToAriva(bin + 2 * quarter + 13_000);
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	expect(posts.length).toBeGreaterThanOrEqual(3);
	expect(posts[1].key).toMatch(uuid);
	expect(posts[1].key).not.toBe(posts[0].key);
	expect(posts[1].body).toEqual({
		binStartUtc: isoMinute(bin + quarter),
		desks: [{ deskId: desks.OD01, states: ['Serving', 'Idle', ...Array.from({ length: 13 }, () => null)] }]
	});
	for (const post of posts.slice(2)) {
		expect(post.key).toBe(posts[1].key);
		expect(post.body).toEqual(posts[1].body);
	}
	const second = await stored();
	expect(second).toHaveLength(60);
	expect(second.filter((o) => o.includes(isoMinute(bin + quarter)))).toEqual([`OD01 ${isoMinute(bin + quarter)} Serving 1`]);

	// Stopping sends the minutes that have ended; none of this bin was observed, so nothing more goes.
	const sent = posts.length;
	await page.getByTestId('stop-desks').click();
	await page.getByTestId('confirm-stop-desks').click();
	await expect(page.getByTestId('desks-stopped')).toContainText(`The minutes before ${dubaiClock(bin + 2 * quarter)} are sent`);
	await expect(page.getByTestId('start-desks')).toBeEnabled();
	expect(posts).toHaveLength(sent);
	await expect(page.getByTestId('change-campaign')).toBeEnabled();

	allowCutRequests(guards);
	await guards.expectClean();
});

test('a desk correction whose answer is lost is frozen and sent again as it is; a key used for another correction is refused in plain words', async ({ page }) => {
	const name = `Tablet desk correction ${Date.now()}`;
	const campaign = await deskCampaign(name);
	// OD01 in the last bin that has ended, logged by the observer before the screen opens.
	const bin = Math.floor(Date.now() / quarter) * quarter - quarter;
	const logged = await call('POST', `${captureUrl}/${campaign}/desk-observations`, {
		token: observer,
		headers: { 'Idempotency-Key': `e2e-desk-${Date.now()}` },
		data: { binStartUtc: isoMinute(bin), desks: [{ deskId: desks.OD01, states: ['Serving', 'Serving', 'Idle', ...Array.from({ length: 12 }, () => null)] }] }
	});
	expect(logged.status(), await logged.text()).toBe(201);
	const revisions = async () =>
		((await (await call('GET', `${captureUrl}/${campaign}/desk-observations?currentOnly=false&pageSize=500`, { token: observer })).json()).data as any[])
			.filter((o) => o.minuteUtc === isoMinute(bin + minute))
			.map((o) => [o.revision, o.state, o.reason])
			.sort((a, b) => a[0] - b[0]);

	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const sent: { key: string | null; body: any }[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && deskCorrections.test(request.url()))
			sent.push({ key: request.headers()['idempotency-key'] ?? null, body: request.postDataJSON() });
	});
	await openCampaign(page, name);
	await page.getByTestId('capture-tab-desks').click();
	await expect(page.getByTestId('desk-history-row')).toHaveCount(1);
	const cell = (m: number) => page.getByTestId('desk-history-row').filter({ hasText: 'OD01' }).locator(`[data-testid="desk-history-cell"][data-minute="${m}"]`);
	await expect(cell(1)).toHaveAttribute('data-state', 'Serving');
	await expect(page.getByTestId('desk-history-cell')).toHaveCount(3);

	// The answer is lost after Ariva recorded the correction.
	await page.route(deskCorrections, async (route) => {
		await route.fetch();
		await route.abort('connectionreset');
	});
	await cell(1).click();
	const form = page.getByTestId('desk-correction-form');
	await expect(form).toContainText('Never name a person');
	await expect(page.getByTestId('desk-correction-recorded')).toHaveText('Recorded: Serving (revision 1)');
	const reason = `${xssPayloads[4]} tapped the wrong desk`;
	await form.getByText('Idle', { exact: true }).click();
	await page.getByTestId('desk-correction-reason').fill(reason);
	await page.getByTestId('desk-correction-save').click();
	await expect(page.getByTestId('desk-correction-problem')).toContainText('may or may not be saved');
	// Frozen: the body that went with the key cannot change; the ways on are Send again (as it is) and Cancel.
	await expect(page.getByTestId('desk-correction-reason')).not.toBeEditable();
	for (const radio of await page.getByTestId('desk-correction-state').all()) await expect(radio).toBeDisabled();
	await expect(page.getByTestId('desk-correction-save')).toHaveText('Send again');
	await page.unroute(deskCorrections);
	await page.getByTestId('desk-correction-save').click();
	await expect(form).toHaveCount(0);
	await expect(cell(1)).toHaveAttribute('data-state', 'Idle');
	await expect(page.getByTestId('desk-correction-row-reason')).toHaveText([reason]);
	await expectNothingInjected(page);
	expect(sent).toHaveLength(2);
	expect(sent[0].key).toMatch(uuid);
	expect(sent[1].key, 'Send again keeps the key').toBe(sent[0].key);
	expect(sent[1].body, 'and the body').toEqual(sent[0].body);
	expect(sent[0].body).toEqual({ state: 'Idle', reason });
	expect(await revisions(), 'recorded once').toEqual([
		[1, 'Serving', null],
		[2, 'Idle', reason]
	]);

	// Another correction goes out with the form's key first: Ariva refuses the form's own body (409) and the screen says so.
	let raced = 0;
	await page.route(deskCorrections, async (route) => {
		const other = await call('POST', `${hosts.main}${new URL(route.request().url()).pathname}`, {
			token: observer,
			headers: { 'Idempotency-Key': route.request().headers()['idempotency-key'] ?? '' },
			data: { state: 'Paused', reason: 'Checked against the desk sheet' }
		});
		raced = other.status();
		await route.continue();
	});
	await cell(1).click();
	await form.getByText('Serving', { exact: true }).click();
	await page.getByTestId('desk-correction-reason').fill('Desk reopened');
	await page.getByTestId('desk-correction-save').click();
	await expect(page.getByTestId('desk-correction-notice')).toHaveText(
		"Ariva already holds another correction sent with this form's key, so this one was not saved. Your desk log is shown again: correct the latest revision if it is still wrong."
	);
	expect(raced).toBe(201);
	await expect(form).toHaveCount(0);
	await expect(cell(1)).toHaveAttribute('data-state', 'Paused');
	expect(await revisions()).toEqual([
		[1, 'Serving', null],
		[2, 'Idle', reason],
		[3, 'Paused', 'Checked against the desk sheet']
	]);

	allowCutRequests(guards);
	allowStatuses(guards, 409);
	await guards.expectClean();
});

test('signing out drops the unsent desk logs: nothing is sent afterwards, and nothing under the next account on the tablet', async ({ page }) => {
	test.setTimeout(150_000);
	const name = `Tablet desk sign-out ${Date.now()}`;
	const campaign = await deskCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const bin = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	const tablet = new TabletClock(page, 0);
	await tablet.install(bin - minute);
	await tablet.serveDates();
	const posts: unknown[] = [];
	page.on('request', (request: Request) => {
		if (request.method() !== 'POST' || !deskPost.test(request.url())) return;
		const token = (request.headers()['authorization'] ?? '').replace(/^Bearer /, '');
		posts.push(token ? claimsOf(token).sub : null);
	});
	await openCampaign(page, name);
	const storage = () => page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
	const storedBefore = await storage();
	await page.getByTestId('capture-tab-desks').click();
	await page.getByTestId('desk-option').filter({ hasText: 'OD01' }).click();
	await tablet.pauseAtAriva(bin + 10_000);
	await page.getByTestId('start-desks').click();

	// No desk log reaches Ariva: every send is cut, and the one held below is answered only once the next account is in.
	let holdNext = false;
	let release = () => {};
	const released = new Promise<void>((resolve) => (release = resolve));
	let heldSeen = () => {};
	const held = new Promise<void>((resolve) => (heldSeen = resolve));
	await page.route(deskPost, async (route) => {
		if (route.request().method() !== 'POST') return route.fallback();
		if (holdNext) {
			holdNext = false;
			heldSeen();
			await released;
			// A success, as if Ariva had recorded the batch: an old send loop would then go on to the next batch.
			return route
				.fulfill({
					status: 201,
					contentType: 'application/json',
					body: JSON.stringify({ id: '00000000-0000-4000-8000-000000000002', observations: [] })
				})
				.catch(() => {});
		}
		return route.abort('connectionreset');
	});
	await tapState(page, 'OD01', 'Serving');
	await tablet.runToAriva(bin + quarter + 2_000);
	await tapState(page, 'OD01', 'Idle');
	await tablet.runToAriva(bin + 2 * quarter + 2_000);
	await expect(page.getByTestId('unsent-item')).toHaveCount(2);

	// A retry is out when the observer signs out; the next observer signs in on the same screen before it is answered.
	await tablet.resume();
	holdNext = true;
	await page.getByTestId('retry-unsent').click();
	await held;
	const sentBefore = posts.length;
	const observerA = claimsOf(observer).sub;
	expect(posts.every((subject) => subject === observerA)).toBe(true);
	await page.getByTestId('sign-out').click();
	await expect(page).toHaveURL(/\/login/);
	await fillSignIn(page, accounts().webObserver2);
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
	await expect(page.getByTestId('user-card')).toContainText('e2e.webobserver2');
	release();

	// Nothing more leaves, at once or on the retry timer (up to 5 minutes), and the next observer stays signed in.
	await page.clock.runFor(6 * 60_000);
	await page.waitForTimeout(1_000);
	expect(posts, 'no desk log is sent after the sign-out').toHaveLength(sentBefore);
	expect(posts.every((subject) => subject === observerA), 'no desk log under the next account').toBe(true);
	await expect(page.getByTestId('user-card')).toContainText('e2e.webobserver2');

	// The next observer's tablet starts empty, and nothing was written to web storage.
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation capture', exact: true }).click();
	await page.getByTestId('campaign-option').filter({ hasText: name }).click();
	await page.getByTestId('capture-tab-desks').click();
	await expect(page.getByTestId('desk-history-empty')).toBeVisible();
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	await expect(page.getByTestId('desk-card')).toHaveCount(0);
	expect(await storage()).toBe(storedBefore);
	expect(posts).toHaveLength(sentBefore);

	// Ariva holds no desk state of either observer for the campaign.
	const nextObserver = (await signIn(accounts().webObserver2)).accessToken;
	for (const token of [observer, nextObserver]) {
		const states = await (await call('GET', `${captureUrl}/${campaign}/desk-observations?pageSize=100`, { token })).json();
		expect(states.data).toEqual([]);
	}
	allowCutRequests(guards);
	await guards.expectClean();
});

test('an observer who also holds an airport role sees no desk log: the server lists no desk and the tablet asks for no desk state', async ({ page }) => {
	const name = `Tablet desks hidden ${Date.now()}`;
	await deskCampaign(name);
	const guards = await guardPage(page);
	const deskCalls: string[] = [];
	page.on('request', (request: Request) => {
		if (/desk-observations/.test(request.url())) deskCalls.push(request.url());
	});
	await signInThroughUi(page, accounts().webValidationDual);
	const listed = page.waitForResponse((r) => /\/validation\/capture\/campaigns$/.test(new URL(r.url()).pathname) && r.request().method() === 'GET');
	await page.goto('/validation/capture');
	const answer = (await (await listed).json()) as any[];
	expect(answer.find((c) => c.name === name)).toMatchObject({ desksIncluded: false, desks: [] });
	const option = page.getByTestId('campaign-option').filter({ hasText: name });
	await expect(option).toBeVisible();
	await expect(option.getByTestId('campaign-desks')).toHaveCount(0);
	await option.click();
	await expect(page.getByTestId('chosen-campaign')).toHaveText(name);
	await expect(page.getByTestId('capture-tab-tracers')).toBeVisible();
	await expect(page.getByTestId('capture-tab-desks')).toHaveCount(0);
	await expect(page.locator('#capture-panel-desks')).toHaveCount(0);
	await expect(page.getByTestId('desk-option')).toHaveCount(0);
	// The arrow keys move between the two tabs there are.
	await page.getByTestId('capture-tab-tracers').focus();
	await page.keyboard.press('ArrowRight');
	await expect(page.getByTestId('capture-tab-tally')).toBeFocused();
	expect(deskCalls).toEqual([]);
	await guards.expectClean();
});

test('desk codes and correction reasons render as text, and a 403 on desk logs, reads and corrections is a plain message', async ({ page }) => {
	test.setTimeout(150_000);
	const name = `Tablet desk markup ${Date.now()}`;
	const campaign = await deskCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const bin = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	const tablet = new TabletClock(page, 0);
	await tablet.install(bin - minute);
	await tablet.serveDates();

	// Desk and checkpoint codes are upper case letters, digits and hyphens, so Ariva cannot store markup in them; the
	// answers that carry them are rewritten here, as a future code rule or a tampered answer could.
	const checkpointXss = xssPayloads[2];
	const codeXss = (i: number) => `${xssPayloads[1]}${i}`;
	await page.route(/\/validation\/capture\/campaigns$/, (route) =>
		route.request().method() !== 'GET'
			? route.fallback()
			: tablet.answer(route, (list: any[]) =>
					list.map((c) => (c.name === name ? { ...c, desks: c.desks.map((d: any, i: number) => ({ ...d, checkpoint: checkpointXss, code: codeXss(i) })) } : c))
				)
	);
	/** A 403 as the validation API writes it, or without detail as a refused permission is answered (status code pages). */
	const forbidden = (detail?: string) => ({
		status: 403,
		contentType: 'application/problem+json',
		body: JSON.stringify(
			detail
				? { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.4', title: 'Not allowed', status: 403, detail }
				: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.4', title: 'Forbidden', status: 403 }
		)
	});
	const deskRole = 'Desk states are border data: an account with an airport role logs and reads them only if it also sees border desks.';
	const ownCampaign = 'You created or started this campaign; its counts come from other observers.';

	// The observer's own desk states are refused (403): a plain message, no grid.
	await page.route(deskReads, (route) => route.fulfill(forbidden(deskRole)));
	await openCampaign(page, name);
	await page.getByTestId('capture-tab-desks').click();
	await expect(page.getByTestId('desk-history-problem')).toHaveText(
		'Desk states are border data: Ariva does not show them to this account, because it also holds an airport role.'
	);
	await expect(page.getByTestId('desk-option-code')).toHaveText([0, 1, 2, 3, 4].map(codeXss));
	await expect(page.getByTestId('desk-option').first()).toContainText(checkpointXss);
	await page.getByTestId('desk-option').first().click();
	await tablet.pauseAtAriva(bin + 10_000);
	await page.getByTestId('start-desks').click();
	await expect(page.getByTestId('desk-card-code')).toHaveText([codeXss(0)]);
	await expect(page.getByTestId('desk-card')).toContainText(checkpointXss);
	await expectNothingInjected(page);

	// A desk batch refused for the account's roles, then for the campaign's own creator: plain messages, nothing retried.
	let refusal: string | undefined = deskRole;
	const refusedPosts: string[] = [];
	await page.route(deskPost, (route) => {
		if (route.request().method() !== 'POST') return route.fallback();
		refusedPosts.push(route.request().headers()['idempotency-key'] ?? '');
		return route.fulfill(forbidden(refusal));
	});
	const logOneMinute = async (state: string) => {
		await page.getByTestId('desk-state').filter({ hasText: state }).click();
		await tablet.runToAriva(tablet.ariva() + minute);
		await page.getByTestId('stop-desks').click();
		await page.getByTestId('confirm-stop-desks').click();
	};
	await logOneMinute('Serving');
	await expect(page.getByTestId('refused-item')).toContainText(
		'Desk states are border data: Ariva takes no desk log from this account, because it also holds an airport role. Nothing was recorded.'
	);
	await expect(page.getByTestId('refused-item')).toContainText(`Desk log, bin ${dubaiClock(bin)} (1 desk)`);
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);
	await page.getByTestId('discard-refused').click();
	refusal = ownCampaign;
	await page.getByTestId('start-desks').click();
	// The minute already sent stays closed; the log goes on from the next one.
	await expect(page.locator('[data-testid="desk-minute"][data-minute="0"]')).toBeDisabled();
	await logOneMinute('Idle');
	await expect(page.getByTestId('refused-item')).toContainText(
		'You created or started this campaign, so you cannot log desks for it: its desk logs come from other observers. Nothing was recorded.'
	);
	await page.getByTestId('discard-refused').click();
	// Any other 403 (here a permission taken away during the shift) is not labelled as either: it shows Ariva's own text.
	refusal = undefined;
	await page.getByTestId('start-desks').click();
	await logOneMinute('Paused');
	await expect(page.getByTestId('refused-item')).toContainText('Ariva refused this desk log: Forbidden');
	await expect(page.getByTestId('refused-item')).not.toContainText('You created or started');
	await expect(page.getByTestId('refused-item')).not.toContainText('border data');
	await page.getByTestId('discard-refused').click();
	await tablet.runToAriva(tablet.ariva() + 6 * minute);
	expect(refusedPosts, 'a refusal is final: never sent again').toHaveLength(3);
	expect(new Set(refusedPosts).size).toBe(3);

	// The observer's states as Ariva would answer them, with markup in the codes and in a correction's reason.
	const reason = `${xssPayloads[0]} ${xssPayloads[5]}`;
	const observation = {
		id: '00000000-0000-4000-8000-0000000000d1',
		campaignId: campaign,
		batchId: null,
		deskId: desks.OD01,
		checkpoint: checkpointXss,
		deskCode: codeXss(0),
		minuteUtc: isoMinute(bin + 2 * minute),
		observerId: String(claimsOf(observer).sub),
		revision: 2,
		current: true,
		state: 'Paused',
		reason,
		correctsId: '00000000-0000-4000-8000-0000000000d0',
		recordedUtc: new Date(bin + 3 * minute).toISOString()
	};
	await page.unroute(deskPost);
	await page.unroute(deskReads);
	await page.route(deskReads, (route) =>
		route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ data: [observation], totalCount: 1, pageIndex: 1, pageSize: 500 }) })
	);
	await page.getByTestId('change-campaign').click();
	await page.getByTestId('campaign-option').filter({ hasText: name }).click();
	await page.getByTestId('capture-tab-desks').click();
	await expect(page.getByTestId('desk-history-desk')).toHaveText([`${checkpointXss} ${codeXss(0)}`]);
	await expect(page.getByTestId('desk-correction-row-reason')).toHaveText([reason]);
	await expectNothingInjected(page);

	// A correction refused for the account's roles (403): a plain message in the form.
	await page.route(deskCorrections, (route) => route.fulfill(forbidden(deskRole)));
	await page.getByTestId('desk-history-cell').click();
	await expect(page.getByTestId('desk-correction-form')).toContainText(`Correct ${checkpointXss} ${codeXss(0)}`);
	await page.getByTestId('desk-correction-form').getByText('Serving', { exact: true }).click();
	await page.getByTestId('desk-correction-reason').fill(`${xssPayloads[3]} wrong desk`);
	await page.getByTestId('desk-correction-save').click();
	await expect(page.getByTestId('desk-correction-problem')).toHaveText(
		'Desk states are border data: Ariva takes no desk log from this account, because it also holds an airport role. Nothing was recorded.'
	);
	// An unrecognised 403 on the correction: Ariva's own text, not the campaign creator's message.
	await page.unroute(deskCorrections);
	await page.route(deskCorrections, (route) => route.fulfill(forbidden()));
	await page.getByTestId('desk-correction-save').click();
	await expect(page.getByTestId('desk-correction-problem')).toHaveText('The correction was not saved: Forbidden');
	await expectNothingInjected(page);

	allowStatuses(guards, 403);
	await guards.expectClean();
});

test("the desk log never places a minute without Ariva's clock: a tablet more than 15 minutes off waits, and after its clock jumps offline nothing is placed until Ariva answers again", async ({
	page
}) => {
	test.setTimeout(150_000);
	const name = `Tablet desk clock ${Date.now()}`;
	const campaign = await deskCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const bin = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	// The tablet's clock starts 20 minutes ahead of Ariva's: a reading beyond 15 minutes is not used (security review L4).
	const tablet = new TabletClock(page, 20 * minute);
	await tablet.install(bin - 2 * minute);
	await tablet.serveDates();
	const posts: any[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && deskPost.test(request.url())) posts.push(request.postDataJSON());
	});
	await openCampaign(page, name);
	await page.getByTestId('capture-tab-desks').click();
	await expect(page.getByTestId('ariva-clock-held')).toHaveText(clockImplausibleText);
	await expect(page.getByTestId('ariva-clock-time')).toHaveCount(0);
	await page.getByTestId('desk-option').filter({ hasText: 'OD01' }).click();
	await expect(page.getByTestId('start-desks')).toBeDisabled();

	// The tablet's time is set right (its clock jumps 20 minutes back): Ariva's clock is read again at the next tick and
	// the log may start. (The test's answers wait until a clock move has finished, so moves around a reading stay short:
	// a reading whose round trip looks longer than 10 seconds is rightly discarded.)
	await tablet.pauseAtAriva(bin + 10_000);
	await tablet.jumpTablet(-20 * minute);
	await tablet.runToAriva(bin + 11_000);
	await expect(page.getByTestId('ariva-clock-held')).toHaveCount(0);
	await tablet.runToAriva(bin + 20_000);
	await expect(page.getByTestId('ariva-clock-off')).toHaveCount(0);
	await page.getByTestId('start-desks').click();
	await expect(page.getByTestId('desk-target')).toHaveText(`Tap the state each desk shows in minute ${dubaiClock(bin)} to ${dubaiClock(bin + minute)}.`);
	await tapState(page, 'OD01', 'Serving');

	// Offline, the tablet's clock jumps 2.5 minutes back (set by hand): the old offset would now place taps 2.5 minutes
	// early, so Ariva's clock is lost and nothing is placed until it is read again (security review L2).
	const offline = (route: Route) => route.abort('internetdisconnected');
	await page.route(validationApi, offline);
	await tablet.jumpTablet(-150_000);
	// A tap in the same second as the jump, before the screen's next tick, finds the jump itself and places nothing
	// (ARV-104c1: every use of Ariva's clock checks for a jump; the strip below still shows only Serving).
	await tapState(page, 'OD01', 'Idle');
	await expect(page.getByTestId('desk-held')).toBeVisible();
	await tablet.runToAriva(bin + minute + 30_000);
	await expect(page.getByTestId('ariva-clock-held')).toHaveText(clockLostText);
	await expect(page.getByTestId('desk-held')).toBeVisible();
	await expect(page.getByTestId('desk-target')).toHaveCount(0);
	await expect(page.getByTestId('desk-bin')).toHaveText(`Bin ${dubaiClock(bin)} to ${dubaiClock(bin + quarter)} (Ariva's clock, site local time); sent when it ends`);
	for (const button of await deskCard(page, 'OD01').getByTestId('desk-state').all()) await expect(button).toBeDisabled();
	for (const button of await page.getByTestId('desk-minute').all()) await expect(button).toBeDisabled();
	const strip = () => deskCard(page, 'OD01').getByTestId('desk-strip-minute').evaluateAll((cells) => cells.map((c) => c.getAttribute('data-state')));
	expect(await strip()).toEqual(['Serving', ...Array.from({ length: 14 }, () => '')]);

	// Still offline past the end of the bin on the tablet's own clock, now 2.5 minutes behind Ariva's (a log that went on
	// with the tablet's clock or the old offset would close and send the bin here): the bin is not closed on a guess, and
	// nothing is sent.
	await tablet.runToAriva(bin + quarter + 155_000);
	expect(posts, 'no bin is sent while Ariva\'s clock is unknown').toEqual([]);
	await expect(page.getByTestId('desk-held')).toBeVisible();
	await expect(page.getByTestId('desk-bin')).toHaveText(`Bin ${dubaiClock(bin)} to ${dubaiClock(bin + quarter)} (Ariva's clock, site local time); sent when it ends`);
	expect(await strip()).toEqual(['Serving', ...Array.from({ length: 14 }, () => '')]);

	// The connection is back: Ariva's clock is read again (the tablet now 2.5 minutes behind it), the bin goes with the one
	// minute logged before the jump, and the log goes on in Ariva's current minute.
	await page.unroute(validationApi, offline);
	// A second at a time, until the next reading (at most 10 seconds away) has come back.
	await expect(async () => {
		await tablet.runToAriva(tablet.ariva() + 1_000);
		await expect(page.getByTestId('ariva-clock-held')).toHaveCount(0, { timeout: 500 });
	}).toPass({ timeout: 30_000 });
	await expect(page.getByTestId('ariva-clock-off')).toContainText(/The tablet's clock is 2 min (29|30|31) s behind Ariva's/);
	await tablet.runToAriva(tablet.ariva() + 2_000);
	await expect.poll(() => posts.length).toBe(1);
	expect(posts[0]).toEqual({ binStartUtc: isoMinute(bin), desks: [{ deskId: desks.OD01, states: ['Serving', ...Array.from({ length: 14 }, () => null)] }] });
	// Ariva's minute now (the page's reading is half a second ahead of the test's clock, which stands on a whole second).
	const current = Math.floor(tablet.ariva() / minute) * minute;
	const index = (current - (bin + quarter)) / minute;
	await expect(page.getByTestId('desk-target')).toHaveText(`Tap the state each desk shows in minute ${dubaiClock(current)} to ${dubaiClock(current + minute)}.`);
	await tapState(page, 'OD01', 'Idle');
	expect(await strip()).toEqual(Array.from({ length: 15 }, (_, m) => (m === index ? 'Idle' : '')));
	const stored = ((await (await call('GET', `${captureUrl}/${campaign}/desk-observations?pageSize=100`, { token: observer })).json()).data as any[]).map(
		(o) => `${o.deskCode} ${o.minuteUtc} ${o.state}`
	);
	expect(stored).toEqual([`OD01 ${isoMinute(bin)} Serving`]);
	await expect(page.getByTestId('unsent-banner')).toHaveCount(0);

	allowCutRequests(guards);
	await guards.expectClean();
});

// ARV-104c1: the line tally on Ariva's clock, with the desk log's holds.

test("the line tally never counts without Ariva's clock: a tablet more than 15 minutes off cannot start, and after its clock jumps offline nothing is counted and the bin is not sent", async ({
	page
}) => {
	test.setTimeout(150_000);
	const name = `Tablet tally clock ${Date.now()}`;
	await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const first = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	// The tablet's clock starts 20 minutes ahead of Ariva's: beyond 15 minutes the reading is not used.
	const tablet = new TabletClock(page, 20 * minute);
	await tablet.install(first - 2 * minute);
	await tablet.serveDates();
	const posts: any[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && countsPost.test(request.url())) posts.push(request.postDataJSON());
	});
	await openCampaign(page, name);
	await expect(page.getByTestId('ariva-clock-held')).toHaveText(clockImplausibleText);
	await expect(page.getByTestId('ariva-clock-time')).toHaveCount(0);
	for (const option of await page.getByTestId('line-option').all()) await expect(option).toBeDisabled();

	// The tablet's time is set right (its clock jumps 20 minutes back): Ariva's clock is read again at the next tick.
	await tablet.pauseAtAriva(first - 20_000);
	await tablet.jumpTablet(-20 * minute);
	await tablet.runToAriva(first - 19_000);
	await expect(page.getByTestId('ariva-clock-held')).toHaveCount(0);
	await tablet.runToAriva(first - 10_000);
	await page.getByTestId('line-option').filter({ hasText: 'Entry A' }).click();
	await expect(page.getByTestId('part-bin')).toBeVisible();
	await tablet.runToAriva(first + 2_000);
	await expect(page.getByTestId('part-bin')).toHaveCount(0);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first)} to ${dubaiClock(first + quarter)}`);
	await page.getByTestId('tally-in').click();
	await page.getByTestId('tally-in').click();
	await expect(page.getByTestId('tally-in-count')).toHaveText('2');

	// Offline, the tablet's clock jumps 2.5 minutes back (set by hand): the old offset would now put counts in the wrong
	// bin edges, so Ariva's clock is lost, In and Out count nothing, and the bin in progress becomes a part bin.
	const offline = (route: Route) => route.abort('internetdisconnected');
	await page.route(validationApi, offline);
	await tablet.jumpTablet(-150_000);
	// A tap in the same second as the jump, before the screen's next tick, finds the jump itself and counts nothing.
	await page.getByTestId('tally-in').click();
	await expect(page.getByTestId('tally-held')).toBeVisible();
	await expect(page.getByTestId('tally-in-count')).toHaveText('2');
	await tablet.runToAriva(first + 5 * minute);
	await expect(page.getByTestId('ariva-clock-held')).toHaveText(clockLostText);
	await expect(page.getByTestId('tally-held')).toBeVisible();
	await expect(page.getByTestId('tally-left')).toHaveCount(0);
	for (const id of ['tally-in', 'tally-out', 'undo-in', 'undo-out']) await expect(page.getByTestId(id), id).toBeDisabled();
	await expect(page.getByTestId('tally-in-count')).toHaveText('2');
	await expect(page.getByTestId('part-bin')).toBeVisible();

	// Still offline past the end of the bin on the tablet's own clock, now 2.5 minutes behind Ariva's (a tally that went
	// on with the tablet's clock or the old offset would close the bin here): no bin is closed on a guess, the bin shown
	// and the last bin skipped are as before the jump, and nothing is sent.
	await tablet.runToAriva(first + quarter + 155_000);
	expect(posts, "no bin is sent while Ariva's clock is unknown").toEqual([]);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first)} to ${dubaiClock(first + quarter)}`);
	await expect(page.getByTestId('skipped-bin')).toContainText(`The bin from ${dubaiClock(first - quarter)} was a part bin and was not sent.`);

	// Back online: Ariva's clock is read again (a second at a time, so each reading answers within its step); the
	// interrupted bin is not sent, and counting goes on in Ariva's current bin, a part bin too.
	await page.unroute(validationApi, offline);
	await expect(async () => {
		await tablet.runToAriva(tablet.ariva() + 1_000);
		await expect(page.getByTestId('ariva-clock-held')).toHaveCount(0, { timeout: 500 });
	}).toPass({ timeout: 30_000 });
	await expect(page.getByTestId('ariva-clock-off')).toContainText(/The tablet's clock is 2 min (29|30|31) s behind Ariva's/);
	await tablet.runToAriva(tablet.ariva() + 2_000);
	await expect(page.getByTestId('skipped-bin')).toContainText(`The bin from ${dubaiClock(first)} was a part bin and was not sent.`);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first + quarter)} to ${dubaiClock(first + 2 * quarter)}`);
	await expect(page.getByTestId('tally-in')).toBeEnabled();
	await expect(page.getByTestId('part-bin')).toBeVisible();
	expect(posts).toEqual([]);
	await page.getByTestId('stop-counting').click();
	await page.getByTestId('confirm-stop-counting').click();
	await expect(page.getByTestId('line-option')).toHaveCount(3);

	allowCutRequests(guards);
	await guards.expectClean();
});

test("a forward jump of the tablet's clock between 5 and 30 seconds near a bin's end holds the tally and the desk log: neither closes its bin early, the counted bin is a part bin, and the desk minutes go at Ariva's bin end", async ({
	page
}) => {
	test.setTimeout(150_000);
	const name = `Tablet forward jump ${Date.now()}`;
	await deskCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const bin = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	const tablet = new TabletClock(page, 0);
	await tablet.install(bin - 90_000);
	await tablet.serveDates();
	const counts: any[] = [];
	const deskBatches: any[] = [];
	page.on('request', (request: Request) => {
		if (request.method() !== 'POST') return;
		if (countsPost.test(request.url())) counts.push(request.postDataJSON());
		if (deskPost.test(request.url())) deskBatches.push(request.postDataJSON());
	});
	await openCampaign(page, name);

	// A full bin on both: the tally starts just before Ariva's quarter hour, the desk log just after it.
	await tablet.pauseAtAriva(bin - 20_000);
	await page.getByTestId('line-option').filter({ hasText: 'Entry A' }).click();
	await tablet.runToAriva(bin + 2_000);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(bin)} to ${dubaiClock(bin + quarter)}`);
	await expect(page.getByTestId('part-bin')).toHaveCount(0);
	await page.getByTestId('tally-in').click();
	await page.getByTestId('capture-tab-desks').click();
	await page.getByTestId('desk-option').filter({ hasText: 'OD01' }).click();
	await page.getByTestId('start-desks').click();
	await tapState(page, 'OD01', 'Serving');
	await tablet.runToAriva(bin + quarter - 30_000);
	await tapState(page, 'OD01', 'Idle');

	// The tablet's clock jumps 20 seconds forward: more than the 5 seconds a jump needs, less than the 30 seconds that make
	// a pause, so only the jump check sees it. With the old offset both would end their bin 20 seconds early; instead they
	// wait for Ariva's clock (the reading in flight across the move is discarded), still 5 seconds before its quarter hour.
	await tablet.jumpTablet(20_000);
	await tablet.runToAriva(bin + quarter - 5_000);
	expect(counts, "no count is sent before the end of Ariva's bin").toEqual([]);
	expect(deskBatches, "no desk batch is sent before the end of Ariva's bin").toEqual([]);
	await expect(page.getByTestId('desk-bin')).toHaveText(`Bin ${dubaiClock(bin)} to ${dubaiClock(bin + quarter)} (Ariva's clock, site local time); sent when it ends`);
	await expect(page.getByTestId('desk-held')).toBeVisible();

	// Ariva's clock is read again (a second at a time). At Ariva's quarter hour the desk log sends its two minutes, and the
	// tally's bin, which missed the wait, is a part bin and is not sent.
	await expect(async () => {
		await tablet.runToAriva(tablet.ariva() + 1_000);
		await expect(page.getByTestId('ariva-clock-held')).toHaveCount(0, { timeout: 500 });
	}).toPass({ timeout: 30_000 });
	await tablet.runToAriva(bin + quarter + 2_000);
	await expect.poll(() => deskBatches.length).toBe(1);
	expect(deskBatches[0]).toEqual({
		binStartUtc: isoMinute(bin),
		desks: [{ deskId: desks.OD01, states: ['Serving', ...Array.from({ length: 13 }, () => null), 'Idle'] }]
	});
	await page.getByTestId('capture-tab-tally').click();
	await expect(page.getByTestId('skipped-bin')).toContainText(`The bin from ${dubaiClock(bin)} was a part bin and was not sent.`);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(bin + quarter)} to ${dubaiClock(bin + 2 * quarter)}`);
	expect(counts).toEqual([]);
	await page.getByTestId('stop-counting').click();
	await page.getByTestId('confirm-stop-counting').click();
	await expect(page.getByTestId('line-option')).toHaveCount(3);

	await guards.expectClean();
});

test("without a readable Date header the line tally follows the tablet's clock and says so; a first reading of Ariva's clock more than 5 seconds away makes the bin in progress a part bin", async ({
	page
}) => {
	test.setTimeout(150_000);
	const name = `Tablet tally no clock ${Date.now()}`;
	await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	const first = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	// The tablet's clock runs 20 seconds ahead of Ariva's (more than the 5 seconds that make a first reading break the bin,
	// less than the 30 seconds a pause needs), and at first no answer tells Ariva's clock (as when the web app and Ariva's
	// API are not on one origin): every Date header is unreadable.
	const skew = 20_000;
	const tablet = new TabletClock(page, skew);
	await tablet.install(first - 90_000);
	await tablet.serveDates();
	const noClock = async (route: Route) => {
		const response = await route.fetch();
		const headers: Record<string, string> = { ...response.headers(), date: 'unreadable' };
		delete headers['content-encoding'];
		delete headers['content-length'];
		await route.fulfill({ response, headers });
	};
	await page.route(validationApi, noClock);
	const posts: any[] = [];
	page.on('request', (request: Request) => {
		if (request.method() === 'POST' && countsPost.test(request.url())) posts.push(request.postDataJSON());
	});
	await openCampaign(page, name);
	await expect(page.getByTestId('ariva-clock-unknown')).toHaveText(
		"Ariva's clock has not been read on this screen, so line counts and desk logs follow the tablet's clock. Set the tablet's date and time to automatic."
	);
	// The time shown is labelled as the tablet's own, and the card does not claim the counts keep to Ariva's clock.
	await expect(page.getByTestId('ariva-clock-time')).toContainText("site local time (Asia/Dubai), on the tablet's own clock.");
	await expect(page.getByTestId('ariva-clock-time')).not.toContainText("keep to Ariva's clock");

	// The tally follows the tablet's clock: its bin turns at the tablet's quarter hour, 20 seconds before Ariva's.
	await tablet.pauseAtAriva(first + quarter - skew - 20_000);
	await page.getByTestId('line-option').filter({ hasText: 'Entry A' }).click();
	await expect(page.getByTestId('part-bin')).toBeVisible();
	await tablet.runToAriva(first + quarter - skew + 2_000);
	await expect(page.getByTestId('skipped-bin')).toContainText(`The bin from ${dubaiClock(first)} was a part bin and was not sent.`);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first + quarter)} to ${dubaiClock(first + 2 * quarter)}`);
	await expect(page.getByTestId('part-bin')).toHaveCount(0);
	await page.getByTestId('tally-in').click();
	await page.getByTestId('tally-in').click();

	// The bin is sent at the tablet's quarter hour, Ariva's clock still unread, and the next bin starts whole on the
	// tablet's clock.
	await tablet.runToAriva(first + 2 * quarter - skew + 2_000);
	await expect.poll(() => posts.length).toBe(1);
	expect(posts[0]).toEqual({ lineId: profile.entryA, binStartUtc: isoMinute(first + quarter), crossingsIn: 2, crossingsOut: 0 });
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first + 2 * quarter)} to ${dubaiClock(first + 3 * quarter)}`);
	await expect(page.getByTestId('part-bin')).toHaveCount(0);
	await expect(page.getByTestId('ariva-clock-unknown')).toBeVisible();

	// Ariva's answers become readable. The screen asks for Ariva's clock on its own (every 10 seconds, doubling to a
	// minute), so the test runs on 5 seconds at a time (each reading answers within its step) until it comes. The first
	// reading puts the tally's time 20 seconds back: less than a pause, but the bin in progress began on the tablet's
	// clock, so it becomes a part bin, never sent, and the bins follow Ariva's clock from its end.
	await page.unroute(validationApi, noClock);
	await expect(async () => {
		await tablet.runToAriva(tablet.ariva() + 5_000);
		await expect(page.getByTestId('ariva-clock-unknown')).toHaveCount(0, { timeout: 500 });
	}).toPass({ timeout: 60_000 });
	await expect(page.getByTestId('ariva-clock-time')).toContainText("Line counts and desk logs keep to Ariva's clock, not the tablet's.");
	await expect(page.getByTestId('part-bin')).toBeVisible();
	// (A reading answered in the step after it was asked may be a step old, so the run goes 10 seconds past the hour.)
	await tablet.runToAriva(first + 3 * quarter + 10_000);
	await expect(page.getByTestId('skipped-bin')).toContainText(`The bin from ${dubaiClock(first + 2 * quarter)} was a part bin and was not sent.`);
	await expect(page.getByTestId('tally-bin')).toHaveText(`Bin ${dubaiClock(first + 3 * quarter)} to ${dubaiClock(first + 4 * quarter)}`);
	await expect(page.getByTestId('part-bin')).toHaveCount(0);
	expect(posts).toHaveLength(1);
	await page.getByTestId('stop-counting').click();
	await page.getByTestId('confirm-stop-counting').click();
	await expect(page.getByTestId('line-option')).toHaveCount(3);

	await guards.expectClean();
});

for (const viewport of [
	{ width: 1280, height: 800, name: 'landscape' },
	{ width: 800, height: 1280, name: 'portrait' }
]) {
	test.describe(`on a ${viewport.name} tablet`, () => {
		test.use({ viewport: { width: viewport.width, height: viewport.height }, hasTouch: true });

		test(`the tablet layout works on touch in English and Arabic (${viewport.width}x${viewport.height})`, async ({ page }, testInfo) => {
			const name = `Tablet layout ${viewport.name} ${Date.now()}`;
			/** A screenshot kept with the test's output for review. */
			const shot = async (file: string) => {
				const target = testInfo.outputPath(`${file}.png`);
				await page.screenshot({ path: target, fullPage: true });
				return target;
			};
			await runningCampaign(name);
			const guards = await guardPage(page);
			await signInThroughUi(page, accounts().webObserver);
			await page.getByTestId('go-capture').click();
			await expect(page.getByRole('heading', { level: 1 })).toHaveText('Validation capture');
			await page.getByTestId('campaign-option').filter({ hasText: name }).tap();
			await page.getByTestId('line-option').filter({ hasText: 'Exit A' }).tap();
			await page.getByTestId('tally-in').tap();
			await page.getByTestId('tally-out').tap();
			await page.getByTestId('tally-out').tap();
			await expect(page.getByTestId('tally-in-count')).toHaveText('1');
			await expect(page.getByTestId('tally-out-count')).toHaveText('2');

			// Large targets, nothing wider than the screen.
			for (const id of ['tally-in', 'tally-out']) {
				const box = (await page.getByTestId(id).boundingBox())!;
				expect(box.height, id).toBeGreaterThanOrEqual(180);
				expect(box.width, id).toBeGreaterThanOrEqual(150);
			}
			const undo = (await page.getByTestId('undo-in').boundingBox())!;
			expect(undo.height).toBeGreaterThanOrEqual(44);
			expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
			await expectInsideViewport(page);
			await expectAxeClean(page);
			await page.getByTestId('theme-toggle').click();
			await expect(page.locator('html')).toHaveClass(/\bdark\b/);
			await expectAxeClean(page);
			await page.getByTestId('theme-toggle').click();
			await expect(page.locator('html')).not.toHaveClass(/\bdark\b/);
			await testInfo.attach(`tally-${viewport.name}-en`, { path: await shot(`tally-${viewport.name}-en`) });

			// Keyboard: Enter and Space on the In button count; the arrow keys move between the tabs.
			await page.getByTestId('tally-in').focus();
			await page.keyboard.press('Enter');
			await page.keyboard.press('Space');
			await expect(page.getByTestId('tally-in-count')).toHaveText('3');
			await page.getByTestId('capture-tab-tally').focus();
			await page.keyboard.press('ArrowRight');
			await expect(page.getByTestId('capture-tab-tracers')).toBeFocused();
			await expect(page.getByTestId('capture-tab-tracers')).toHaveAttribute('aria-selected', 'true');
			await expectInsideViewport(page);
			await expectAxeClean(page);

			await page.getByTestId('language-toggle').click();
			await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
			await expect(page.getByRole('heading', { level: 1 })).toHaveText('التقاط بيانات التحقق');
			await expect(page.getByTestId('capture-tab-tracers')).toHaveText('المتتبعون');
			await expect(page.getByText('متتبع ينضم إلى الطابور')).toBeVisible();
			expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
			await expectInsideViewport(page);
			await expectAxeClean(page);
			await testInfo.attach(`tracers-${viewport.name}-ar`, { path: await shot(`tracers-${viewport.name}-ar`) });

			await page.getByTestId('capture-tab-tally').tap();
			await expect(page.getByTestId('tally-in')).toContainText('دخول');
			await expect(page.getByTestId('tally-in-count')).toHaveText('3');
			const inBox = (await page.getByTestId('tally-in').boundingBox())!;
			const outBox = (await page.getByTestId('tally-out').boundingBox())!;
			expect(inBox.x, 'In is on the start side, the right in Arabic').toBeGreaterThan(outBox.x);
			await expectInsideViewport(page);
			await expectAxeClean(page);
			await testInfo.attach(`tally-${viewport.name}-ar`, { path: await shot(`tally-${viewport.name}-ar`) });

			await page.getByTestId('stop-counting').tap();
			await page.getByTestId('confirm-stop-counting').tap();
			await expect(page.getByTestId('line-option')).toHaveCount(3);
			await guards.expectClean();
		});

		test(`the desk log works on touch in English and Arabic (${viewport.width}x${viewport.height})`, async ({ page }, testInfo) => {
			const name = `Tablet desk layout ${viewport.name} ${Date.now()}`;
			const shot = async (file: string) => {
				const target = testInfo.outputPath(`${file}.png`);
				await page.screenshot({ path: target, fullPage: true });
				return target;
			};
			await deskCampaign(name);
			const guards = await guardPage(page);
			await signInThroughUi(page, accounts().webObserver);
			await page.goto('/validation/capture');
			await page.getByTestId('campaign-option').filter({ hasText: name }).tap();
			await page.getByTestId('capture-tab-desks').tap();
			for (const box of await Promise.all((await page.getByTestId('desk-option').all()).map((o) => o.boundingBox())))
				expect(box!.height, 'desk choice').toBeGreaterThanOrEqual(44);
			await page.getByTestId('choose-all-desks').tap();
			await expect(page.getByTestId('desks-chosen')).toHaveText('5 desks chosen (at most 20)');
			await page.getByTestId('start-desks').tap();
			await expect(page.getByTestId('desk-card')).toHaveCount(5);
			const serving = deskCard(page, 'OD01').locator('[data-testid="desk-state"][data-state="Serving"]');
			await serving.tap();
			await expect(serving).toHaveAttribute('aria-pressed', 'true');
			await expect(deskCard(page, 'OD01').getByTestId('desk-card-state')).toHaveText('Serving');

			// Large targets: the four states of a desk and the minutes of the bin; nothing wider than the screen.
			for (const button of await deskCard(page, 'OD01').getByTestId('desk-state').all()) {
				const box = (await button.boundingBox())!;
				expect(box.height, 'state button height').toBeGreaterThanOrEqual(56);
				expect(box.width, 'state button width').toBeGreaterThanOrEqual(64);
			}
			for (const button of await page.getByTestId('desk-minute').all()) {
				const box = (await button.boundingBox())!;
				expect(box.height, 'minute button height').toBeGreaterThanOrEqual(44);
				expect(box.width, 'minute button width').toBeGreaterThanOrEqual(32);
			}
			expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
			await expectInsideViewport(page);
			await expectAxeClean(page);
			await page.getByTestId('theme-toggle').click();
			await expect(page.locator('html')).toHaveClass(/\bdark\b/);
			await expectAxeClean(page);
			await page.getByTestId('theme-toggle').click();
			await expect(page.locator('html')).not.toHaveClass(/\bdark\b/);
			await testInfo.attach(`desks-${viewport.name}-en`, { path: await shot(`desks-${viewport.name}-en`) });

			// Keyboard: Enter on a state records it; the arrow keys move between the three tabs.
			const idle = deskCard(page, 'OD02').locator('[data-testid="desk-state"][data-state="Idle"]');
			await idle.focus();
			await page.keyboard.press('Enter');
			await expect(idle).toHaveAttribute('aria-pressed', 'true');
			await page.getByTestId('capture-tab-desks').focus();
			await page.keyboard.press('ArrowRight');
			await expect(page.getByTestId('capture-tab-tally')).toBeFocused();
			await page.keyboard.press('ArrowLeft');
			await expect(page.getByTestId('capture-tab-desks')).toBeFocused();
			await expect(page.getByTestId('capture-tab-desks')).toHaveAttribute('aria-selected', 'true');

			await page.getByTestId('language-toggle').click();
			await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
			await expect(page.getByTestId('capture-tab-desks')).toHaveText('سجل المكاتب');
			await expect(serving).toContainText('يخدم');
			await expect(page.getByTestId('desk-log-title')).toHaveText('تسجيل 5 مكاتب');
			const minute0 = (await page.locator('[data-testid="desk-minute"][data-minute="0"]').boundingBox())!;
			const minute1 = (await page.locator('[data-testid="desk-minute"][data-minute="1"]').boundingBox())!;
			expect(minute0.x, 'the first minute is on the start side, the right in Arabic').toBeGreaterThan(minute1.x);
			const first = (await serving.boundingBox())!;
			const last = (await deskCard(page, 'OD01').locator('[data-testid="desk-state"][data-state="Closed"]').boundingBox())!;
			expect(first.x, 'Serving comes first, on the right in Arabic').toBeGreaterThan(last.x);
			// In Arabic the arrow keys follow the reading order: the left arrow goes on to the next tab.
			await page.getByTestId('capture-tab-desks').focus();
			await page.keyboard.press('ArrowLeft');
			await expect(page.getByTestId('capture-tab-tally')).toBeFocused();
			await page.getByTestId('capture-tab-desks').tap();
			expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
			await expectInsideViewport(page);
			await expectAxeClean(page);
			await testInfo.attach(`desks-${viewport.name}-ar`, { path: await shot(`desks-${viewport.name}-ar`) });

			await page.getByTestId('stop-desks').tap();
			await page.getByTestId('confirm-stop-desks').tap();
			await expect(page.getByTestId('desk-option')).toHaveCount(5);
			await guards.expectClean();
		});
	});
}

for (const role of [
	{ name: 'a terminal duty manager', account: () => accounts().web },
	{ name: 'an administrator', account: () => accounts().webAdmin }
]) {
	test(`${role.name} has no capture screen and asks the API for nothing about it`, async ({ page }) => {
		const account = role.account();
		const guards = await guardPage(page);
		const validationCalls: string[] = [];
		page.on('request', (request: Request) => {
			if (/\/api\/v1\/.*\/validation\//.test(request.url())) validationCalls.push(request.url());
		});
		await signInThroughUi(page, account);
		await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation capture', exact: true })).toHaveCount(0);
		await expect(page.getByTestId('go-capture')).toHaveCount(0);
		await page.goto('/validation/capture');
		await expect(page.getByTestId('no-access')).toBeVisible();
		await expect(page.getByTestId('campaign-option')).toHaveCount(0);
		expect(validationCalls).toEqual([]);
		await guards.expectClean();
	});
}
