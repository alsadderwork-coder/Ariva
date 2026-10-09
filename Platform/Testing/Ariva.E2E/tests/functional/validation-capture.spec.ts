import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page, type Request, type TestInfo } from '@playwright/test';
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
// is untouched. Bins must have ended on the server's clock before they are sent, so the tally test runs the page on
// Playwright's clock an hour in the past and moves it forward bin by bin.

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

/** Moves the page's clock to `target` (page time), firing every timer on the way. */
async function runPageClockTo(page: Page, target: number): Promise<void> {
	const pageNow = await page.evaluate(() => Date.now());
	if (target > pageNow) await page.clock.runFor(target - pageNow);
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
});

test('an observer tallies a line per bin; each ended bin is sent with its own key, and a lost answer is retried without counting twice', async ({ page }, testInfo) => {
	const name = `Tablet tally ${Date.now()}`;
	const campaign = await runningCampaign(name);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webObserver);
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation capture', exact: true })).toBeVisible();

	// The page runs an hour behind: the bins it closes have ended on the server's clock too, on a planned day.
	const first = Math.floor(Date.now() / quarter) * quarter - 4 * quarter;
	await page.clock.install({ time: first - 90_000 });
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
	await runPageClockTo(page, first + 2_000);
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
	await runPageClockTo(page, first + quarter + 2_000);
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
	await runPageClockTo(page, first + 2 * quarter + 2_000);
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

	// Leaving the screen now would lose the bin: the screen holds the navigation and asks.
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Account security' }).click();
	await expect(page.getByTestId('leave-warning')).toBeVisible();
	await page.getByTestId('leave-stay').click();
	await expect(page).toHaveURL(/\/validation\/capture$/);

	// The connection is back: the retry timer (10 s, doubling) sends the bin again on its own, with the same key.
	await page.unroute(/\/validation\/capture\/campaigns\/[^/]+\/counts$/);
	await page.clock.runFor(31_000);
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
	await page.clock.install({ time: first - 90_000 });
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
		if (route.request().method() !== 'POST') return route.continue();
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
	await runPageClockTo(page, first + 2_000);
	await page.getByTestId('tally-in').click();
	await runPageClockTo(page, first + quarter + 2_000);
	await page.getByTestId('tally-out').click();
	await runPageClockTo(page, first + 2 * quarter + 2_000);
	await expect(page.getByTestId('unsent-item')).toHaveCount(2);

	// A retry is out when the observer signs out; the next observer signs in on the same screen before it is answered.
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
