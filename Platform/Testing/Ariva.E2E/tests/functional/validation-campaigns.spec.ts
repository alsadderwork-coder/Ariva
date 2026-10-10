import crypto from 'node:crypto';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Browser, type Page, type Request } from '@playwright/test';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, login, signIn, signToken, unusedTotpCode, type Account } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { homeHeading } from '../support/shell';
import { allowStatuses, databaseAvailable, earlyInStep, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';

// ARV-104h: the validation campaign screens (/validation, wiki 12 "Validation campaigns", wiki 07 section 8). A border shift
// supervisor plans a campaign on the screen (queue zones, lines, local days, placeholder targets and border desks), starts it,
// follows its progress (bins captured per line, tracer runs by wait range, desk minutes), reads every observer's counts with
// their correction reasons, and closes it, which asks for a fresh second factor (the step-up dialog). The results view of a
// closed campaign shows each criterion's value, target and verdict, the tables per line, tracer zone and desk, the
// ground-truth proof and the frozen revision's SHA-256 content hash, as the server projects them per caller: a terminal duty
// manager reads them without the desk section. Administrators hold View and Manage but never Capture, so their pass has no
// capture control. Observers and handler station managers have no item and see the no-access panel. Campaign, zone and line
// names and correction reasons are user text shown to other roles: every one renders as text (CWE-79).
//
// The suite works at its own site E2EW (Asia/Dubai) with a zone profile it publishes once, apart from validation.spec.ts's
// E2EV and the tablet's E2EO. The ground truth (counts, a correction, tracer runs, a desk log) is sent through the API by the
// site's observer.

test.skip(!databaseAvailable, 'the campaign screens need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const site = 'E2EW';
const admin = `${hosts.main}/api/v1/admin`;
const campaignsUrl = `${hosts.main}/api/v1/sites/${site}/validation/campaigns`;
const captureUrl = `${hosts.main}/api/v1/sites/${site}/validation/capture/campaigns`;
const quarter = 15 * 60_000;
const minute = 60_000;
const run = crypto.randomBytes(3).toString('hex');

/** User text that would run if it were rendered as markup. */
const zoneXss = `Q-B ${xssPayloads[1]}`;
const lineXss = `${xssPayloads[0]} entry`;
/** Elements and attributes a successful injection would create. */
const injectedSelectors = ['img[src="x"]', 'svg[onload]', 'iframe', '[onerror]', '[onload]', 'a[href^="javascript:"]', 'main script'];
const deskCodes = ['WD01', 'WD02', 'WD03'];

const dubaiDay = (at: number) =>
	new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(at));
/** The start of the bin `k` quarter hours before the current one, UTC without milliseconds. */
const binStart = (k: number) => new Date((Math.floor(Date.now() / quarter) - k) * quarter).toISOString().replace('.000Z', 'Z');
const campaignsPage = (query: string) => `/validation?site=${site}&${query}`;

interface Profile {
	version: number;
	queueA: string;
	queueB: string;
	entryA: string;
	exitA: string;
	xssLine: string;
	exitB: string;
}

let administrator: string, supervisor: string, observer: string;
let profile: Profile;
let desks: Record<string, string>;
/** The campaign the supervisor plans on the screen (first test) and closes (second test). */
let planned = { id: '', name: '' };

async function expectNothingInjected(page: Page): Promise<void> {
	for (const selector of injectedSelectors) await expect(page.locator(selector), selector).toHaveCount(0);
}

/** Local and session storage as Ariva writes them; SvelteKit's own keys (sveltekit:scroll, sveltekit:snapshot) aside. */
function storedByAriva(page: Page): Promise<string> {
	return page.evaluate(() =>
		JSON.stringify({
			local: { ...localStorage },
			session: Object.fromEntries(Object.entries(sessionStorage).filter(([key]) => !key.startsWith('sveltekit:')))
		})
	);
}

async function expectAxeClean(page: Page): Promise<void> {
	const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
	expect(
		results.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`),
		'axe violations'
	).toEqual([]);
}

/** E2EW's published zone profile: Q-A with Entry A and Exit A, and a queue zone and a line named with markup. */
async function ensureProfile(): Promise<Profile> {
	const api = `${admin}/zone-profiles`;
	const history = await (await call('GET', `${api}?siteCode=${site}`, { token: supervisor })).json();
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
		const airport = await create('airports', { iataCode: iata, name: `E2E campaigns ${iata}`, timeZoneId: 'Asia/Dubai' });
		const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'Terminal 1', siteCode: site });
		const levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });

		const draft = await call('POST', `${api}/drafts`, { token: supervisor, data: { siteCode: site, name: 'Campaign hall' } });
		expect(draft.status(), await draft.text()).toBe(201);
		const id = (await draft.json()).profile.id as string;
		const zone = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/zones`, { token: supervisor, data: { levelId, ...data } });
			expect(response.status(), await response.text()).toBe(201);
			return (await response.json()).id as string;
		};
		const line = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/lines`, { token: supervisor, data: { levelId, ...data } });
			expect(response.status(), await response.text()).toBe(201);
		};
		const queueA = await zone({ name: 'Q-A', kind: 'Queue', polygon: '10 10,34 10,34 22,10 22' });
		const queueB = await zone({ name: zoneXss, kind: 'Queue', polygon: '50 10,74 10,74 22,50 22' });
		await line({ name: 'Entry A', role: 'Entry', startX: 10, startY: 12, endX: 10, endY: 16, zoneId: queueA });
		await line({ name: 'Exit A', role: 'Exit', startX: 30, startY: 22, endX: 34, endY: 22, zoneId: queueA });
		await line({ name: lineXss, role: 'Entry', startX: 50, startY: 12, endX: 50, endY: 16, zoneId: queueB });
		await line({ name: 'Exit B', role: 'Exit', startX: 70, startY: 22, endX: 74, endY: 22, zoneId: queueB });
		const review = await (await call('GET', `${api}/${id}/validation`, { token: supervisor })).json();
		expect(review.publishable, JSON.stringify(review)).toBe(true);
		const publish = await call('POST', `${api}/${id}/publish`, { token: supervisor, data: { geometryHash: review.geometryHash } });
		expect(publish.status(), await publish.text()).toBe(200);
		published = await publish.json();
	}
	const view = await (await call('GET', `${api}/${published.id}`, { token: supervisor })).json();
	const zoneId = (name: string) => view.zones.find((z: any) => z.name === name).id as string;
	const lineId = (name: string) => view.lines.find((l: any) => l.name === name).id as string;
	return {
		version: published.version,
		queueA: zoneId('Q-A'),
		queueB: zoneId(zoneXss),
		entryA: lineId('Entry A'),
		exitA: lineId('Exit A'),
		xssLine: lineId(lineXss),
		exitB: lineId('Exit B')
	};
}

/** E2EW's border checkpoint WIMM with desks WD01 to WD03, created by the administrator the first time. */
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
	const border = checkpoints.find((c) => c.code === 'WIMM') ?? (await create('checkpoints', { levelId: level.id, code: 'WIMM', name: 'Campaign immigration', kind: 'Immigration' }));
	const existing = await list('desks', `parentId=${border.id}&pageSize=100`);
	const ids: Record<string, string> = {};
	for (const code of deskCodes)
		ids[code] = (existing.find((d) => d.code === code) ?? (await create('desks', { checkpointId: border.id, code, kind: 'Desk', laneCategories: ['CIT'] }))).id;
	return ids;
}

/** Plans a campaign over both queue zones, three lines and two desks for yesterday and today (Dubai), and starts it. */
async function runningCampaign(name: string): Promise<string> {
	const created = await call('POST', campaignsUrl, {
		token: supervisor,
		data: {
			name,
			profileVersion: profile.version,
			zoneIds: [profile.queueA, profile.queueB],
			lineIds: [profile.entryA, profile.exitA, profile.xssLine],
			days: [dubaiDay(Date.now() - 86_400_000), dubaiDay(Date.now())],
			deskIds: [desks.WD01, desks.WD02]
		}
	});
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id as string;
	const started = await call('POST', `${campaignsUrl}/${id}/start`, { token: supervisor });
	expect(started.status(), await started.text()).toBe(200);
	return id;
}

/**
 * The observer's ground truth through the API: Entry A counted in two ended bins, the later one corrected with `reason`;
 * four tracer runs in Q-A (waits of 3, 12 and 25 minutes and one left unserved after 8); WD01 Serving for a whole bin.
 */
async function groundTruth(id: string, reason: string): Promise<void> {
	const counts: string[] = [];
	for (const [k, crossingsIn] of [[3, 14], [2, 11]] as [number, number][]) {
		const count = await call('POST', `${captureUrl}/${id}/counts`, { token: observer, data: { lineId: profile.entryA, binStartUtc: binStart(k), crossingsIn, crossingsOut: 2 } });
		expect(count.status(), await count.text()).toBe(201);
		counts.push((await count.json()).id);
	}
	const corrected = await call('POST', `${captureUrl}/${id}/counts/${counts[1]}/corrections`, {
		token: observer,
		data: { crossingsIn: 12, crossingsOut: 2, reason }
	});
	expect(corrected.status(), await corrected.text()).toBe(201);

	const now = Date.now();
	const at = (ago: number) => new Date(now - ago).toISOString();
	const runs = [
		{ zoneId: profile.queueA, tracerCode: 'T-01', joinedUtc: at(40 * minute), exitedUtc: at(37 * minute), abandoned: false },
		{ zoneId: profile.queueA, tracerCode: 'T-02', joinedUtc: at(36 * minute), exitedUtc: at(24 * minute), abandoned: false },
		{ zoneId: profile.queueA, tracerCode: 'T-03', joinedUtc: at(35 * minute), exitedUtc: at(10 * minute), abandoned: false },
		{ zoneId: profile.queueA, tracerCode: 'T-04', joinedUtc: at(20 * minute), exitedUtc: at(12 * minute), abandoned: true }
	];
	const batch = await call('POST', `${captureUrl}/${id}/tracer-runs`, {
		token: observer,
		headers: { 'Idempotency-Key': crypto.randomUUID() },
		data: { deviceClockUtc: new Date().toISOString(), runs }
	});
	expect(batch.status(), await batch.text()).toBe(201);

	const logged = await call('POST', `${captureUrl}/${id}/desk-observations`, {
		token: observer,
		headers: { 'Idempotency-Key': crypto.randomUUID() },
		data: { binStartUtc: binStart(1), desks: [{ deskId: desks.WD01, states: Array.from({ length: 15 }, () => 'Serving') }] }
	});
	expect(logged.status(), await logged.text()).toBe(201);
}

/** A campaign at the demo airport (DMO), planned by its border supervisor: another site's campaign for the E2EW accounts. */
async function otherSiteCampaign(): Promise<string> {
	const border = (await signIn(accounts().webBorder)).accessToken;
	const profiles = await (await call('GET', `${admin}/zone-profiles?siteCode=DMO`, { token: border })).json();
	const published = profiles.find((p: any) => p.status === 'Published');
	const view = await (await call('GET', `${admin}/zone-profiles/${published.id}`, { token: border })).json();
	const zone = view.zones.find((z: any) => z.kind === 'Queue');
	const created = await call('POST', `${hosts.main}/api/v1/sites/DMO/validation/campaigns`, {
		token: border,
		data: { name: `Other site ${run}`, profileVersion: published.version, zoneIds: [zone.id], lineIds: [], days: [dubaiDay(Date.now())] }
	});
	expect(created.status(), await created.text()).toBe(201);
	return (await created.json()).id;
}

/** Signs in through the form with a code the server has not taken yet (the account has an authenticator). */
async function signInWithCode(page: Page, account: Account): Promise<void> {
	await ownAddress(page);
	await page.goto('/login');
	await fillSignIn(page, account, await unusedTotpCode(account.totpSecret!));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
}

/**
 * Signs in through the form with the second factor 20 minutes old, so closing asks for a fresh code (step-up). The aged
 * token lives in the page's memory only: a reload would take a new one from the refresh cookie, so the test moves by links.
 */
async function signInAged(page: Page, account: Account): Promise<void> {
	await ownAddress(page);
	await page.route('**/api/auth/login', async (route) => {
		const response = await route.fetch();
		if (response.status() !== 200) return route.fulfill({ response });
		const body = await response.json();
		const claims = claimsOf(body.accessToken);
		body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
		return route.fulfill({ response, json: body });
	});
	await page.goto('/login');
	await fillSignIn(page, account, await unusedTotpCode(account.totpSecret!));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
}

/** Opens the campaigns screen through the sidebar. */
async function openCampaigns(page: Page): Promise<void> {
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation campaigns', exact: true }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Validation campaigns');
}

/** A page signed in as `account` in a context of its own (another role beside the test's page). */
async function pageAs(browser: Browser, account: Account): Promise<Page> {
	const context = await browser.newContext({ baseURL: test.info().project.use.baseURL, locale: 'en-US' });
	const page = await context.newPage();
	await signInThroughUi(page, account);
	return page;
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().webCampaignSupervisor;
	const signedIn = await login(userName, password, undefined, undefined, { code: await unusedTotpCode(totpSecret!) });
	expect(signedIn.status(), 'the campaign supervisor signs in with a second factor').toBe(200);
	supervisor = (await signedIn.json()).accessToken;
	[administrator, observer] = await Promise.all(
		[accounts().SystemAdministrator, accounts().webCampaignObserver].map(async (a) => (await signIn(a)).accessToken)
	);
	profile = await ensureProfile();
	desks = await ensureDesks();
});

test('a border shift supervisor plans a campaign on the screen, starts it, follows its progress and reads every observer\'s counts as text', async ({ page }) => {
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	await signInWithCode(page, accounts().webCampaignSupervisor);
	// The sign-in's first answer asks for the second factor (401); any later 401 still fails the test.
	allowStatuses(guards, 401);
	await openCampaigns(page);
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation campaigns', exact: true })).toHaveAttribute('aria-current', 'page');
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation capture', exact: true })).toHaveCount(0);
	const storedBefore = await storedByAriva(page);

	// The form: the published version, the no-names hint, zones bring their lines, days are added, desks are offered.
	await page.getByTestId('new-campaign').click();
	const form = page.getByTestId('campaign-form');
	await expect(form.getByTestId('campaign-form-version')).toContainText(`Published version v${profile.version}`);
	await expect(form.locator('#campaign-name-hint')).toHaveText(/Never name a person/);
	planned.name = `${xssPayloads[1]} Pilot week ${run}`;
	await form.getByLabel('Campaign name').fill(planned.name);
	await form.getByTestId('campaign-form-create').click();
	await expect(form.getByTestId('campaign-form-problems')).toContainText('Choose 1 to 50 queue zones.');
	await form.getByTestId('campaign-form-zone').filter({ hasText: 'Q-A' }).locator('input').check();
	await form.getByTestId('campaign-form-zone').filter({ hasText: 'Q-B' }).locator('input').check();
	await expect(form.getByTestId('campaign-form-line')).toHaveCount(4);
	await form.getByTestId('campaign-form-line').filter({ hasText: 'Exit B' }).locator('input').uncheck();
	for (const day of [dubaiDay(Date.now() - 86_400_000), dubaiDay(Date.now())]) {
		await form.getByTestId('campaign-form-day').fill(day);
		await form.getByTestId('campaign-form-add-day').click();
	}
	await expect(form.getByTestId('campaign-form-chosen-day')).toHaveCount(2);
	await expect(form.getByTestId('campaign-form-zone-days')).toHaveText('4 zone-days of at most 400 (queue zones times days).');
	await expect(form.getByTestId('campaign-form-desk')).toHaveText(['WIMM WD01', 'WIMM WD02', 'WIMM WD03']);
	await form.getByTestId('campaign-form-desk').filter({ hasText: 'WD01' }).locator('input').check();
	await form.getByTestId('campaign-form-desk').filter({ hasText: 'WD02' }).locator('input').check();
	const created = page.waitForResponse(
		(r) => r.request().method() === 'POST' && new URL(r.url()).pathname === `/api/v1/sites/${site}/validation/campaigns`
	);
	await form.getByTestId('campaign-form-create').click();
	const answer = await created;
	expect(answer.status()).toBe(201);
	const body = answer.request().postDataJSON();
	expect(body).toMatchObject({ name: planned.name, profileVersion: profile.version, targetBinsPerLine: null, targetTracerRuns: null });
	expect(body.zoneIds.sort()).toEqual([profile.queueA, profile.queueB].sort());
	expect(body.lineIds.sort()).toEqual([profile.entryA, profile.exitA, profile.xssLine].sort());
	expect(body.deskIds.sort()).toEqual([desks.WD01, desks.WD02].sort());
	planned.id = (await answer.json()).id;

	// The campaign: planned, its name as text, placeholder targets; then started.
	await expect(page).toHaveURL(new RegExp(`campaign=${planned.id}`));
	await expect(page.getByTestId('campaign-name')).toHaveText(planned.name);
	await expect(page.getByTestId('campaign-status')).toHaveText('Planned');
	await expect(page.getByTestId('campaign-targets')).toContainText('20 bins per line, 30 tracer runs');
	await expect(page.getByTestId('campaign-targets')).toContainText('Placeholders until the KPI annex sets them');
	await page.getByTestId('start-campaign').click();
	await expect(page.getByTestId('campaign-status')).toHaveText('Running');
	expect((await (await call('GET', `${campaignsUrl}/${planned.id}`, { token: supervisor })).json()).status).toBe('Running');

	// Ground truth from the observer, then the progress: bins per line, runs by wait range, desk minutes.
	const reason = `${xssPayloads[2]} recounted from the video ${run}`;
	await groundTruth(planned.id, reason);
	await page.reload();
	await expect(page.getByTestId('campaign-name')).toHaveText(planned.name);
	const line = (name: string) => page.locator(`[data-testid="progress-line"][data-line="${name}"]`);
	await expect(line('Entry A').getByTestId('progress-bins')).toHaveText('2 of 20');
	await expect(line('Exit A').getByTestId('progress-bins')).toHaveText('0 of 20');
	await expect(page.getByTestId('progress-line').filter({ hasText: lineXss }).getByTestId('progress-bins')).toHaveText('0 of 20');
	const zoneA = page.locator('[data-testid="progress-tracer-zone"][data-zone="Q-A"]');
	await expect(zoneA.getByTestId('progress-range')).toHaveText(['1', '0', '1', '1', '0']);
	await expect(zoneA.getByTestId('progress-timed')).toHaveText('3');
	await expect(zoneA.getByTestId('progress-abandoned')).toHaveText('1');
	await expect(page.getByTestId('progress-tracer-zone').filter({ hasText: zoneXss }).getByTestId('progress-timed')).toHaveText('0');
	await expect(page.getByTestId('progress-runs-target')).toHaveText('3 timed runs of the target 30');
	await expect(page.locator('[data-testid="progress-desk"][data-desk="WIMM WD01"] [data-testid="progress-desk-minutes"]')).toHaveText('15');
	await expect(page.locator('[data-testid="progress-desk"][data-desk="WIMM WD02"] [data-testid="progress-desk-minutes"]')).toHaveText('0');

	// Every observer's counts: current revisions, then every revision; the reason as text, never markup.
	await page.getByTestId('campaign-tab-counts').click();
	await expect(page).toHaveURL(/view=counts/);
	await expect(page.getByTestId('counts-no-names')).toContainText('never name a person');
	await expect(page.getByTestId('count-row')).toHaveCount(2);
	await expect(page.getByTestId('count-in')).toHaveText(['12', '14']);
	await expect(page.getByTestId('count-reason')).toHaveText([reason, '']);
	// Observers by the start of their Ariva user id, never a name.
	await expect(page.getByTestId('count-observer')).toHaveText(Array(2).fill(String(claimsOf(observer).sub).slice(0, 8)));
	await page.getByTestId('counts-every').check();
	await expect(page.getByTestId('count-row')).toHaveCount(3);
	await expect(page.locator('[data-testid="count-row"][data-current="false"] [data-testid="count-in"]')).toHaveText('11');

	await expectNothingInjected(page);
	// Nothing of the campaign in web storage: Ariva's own keys are unchanged, and SvelteKit's (scroll positions and an
	// empty snapshot, written on navigation) hold no campaign id, name, reason or token.
	expect(await storedByAriva(page), 'nothing in web storage').toBe(storedBefore);
	const everything = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
	for (const secret of [planned.id, run, reason, String(claimsOf(observer).sub)]) expect(everything, 'web storage').not.toContain(secret);
	expect(everything, 'web storage').not.toMatch(/eyJ[\w-]+\.[\w-]+/);
	await guards.expectClean();
});

test('closing a campaign asks for a fresh second factor: Escape keeps it running, a code closes it and its results are frozen with their hash', async ({ page }) => {
	test.skip(!developmentSigningKey() || !planned.id, 'needs the run key and the campaign planned by the previous test');
	test.setTimeout(150_000);
	const guards = await guardPage(page);
	await earlyInStep();
	await signInAged(page, accounts().webCampaignSupervisor);
	const storedBefore = await storedByAriva(page);
	await openCampaigns(page);
	await page.locator(`[data-testid="campaign-row"] a[href*="campaign=${planned.id}"]`).click();
	await expect(page.getByTestId('campaign-name')).toHaveText(planned.name);
	await expect(page.getByTestId('campaign-status')).toHaveText('Running');

	// The close asks first on the page, then Ariva asks for a second factor; Escape leaves the campaign as it was.
	await page.getByTestId('close-campaign').click();
	await expect(page.getByTestId('close-campaign-confirm')).toContainText('Nothing is captured or corrected afterwards');
	await page.getByTestId('confirm-close-campaign').click();
	const dialog = page.getByTestId('step-up-dialog');
	await expect(dialog).toBeVisible();
	await page.keyboard.press('Escape');
	await expect(dialog).toBeHidden();
	await expect(page.getByTestId('campaign-action-problem')).toHaveText('The campaign was not closed: closing needs a fresh code from your authenticator app.');
	await expect(page.getByTestId('campaign-status')).toHaveText('Running');
	expect((await (await call('GET', `${campaignsUrl}/${planned.id}`, { token: supervisor })).json()).status).toBe('Running');

	// Again with a fresh code: closed, and the results are frozen as revision 1 with their SHA-256.
	await page.getByTestId('close-campaign').click();
	await page.getByTestId('confirm-close-campaign').click();
	await expect(dialog).toBeVisible();
	await page.locator('#step-up-code').fill(await unusedTotpCode(accounts().webCampaignSupervisor.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();
	await expect(dialog).toBeHidden();
	await expect(page.getByTestId('campaign-status')).toHaveText('Closed');
	await expect(page.getByTestId('close-campaign')).toHaveCount(0);
	expect((await (await call('GET', `${campaignsUrl}/${planned.id}`, { token: supervisor })).json()).status).toBe('Closed');

	await page.getByTestId('campaign-tab-results').click();
	await expect(page.getByTestId('results-frozen')).toBeVisible({ timeout: 60_000 });
	const served = await (await call('GET', `${campaignsUrl}/${planned.id}/results`, { token: supervisor })).json();
	expect(served.revision).toMatchObject({ number: 1, revisions: 1 });
	await expect(page.getByTestId('results-hash')).toHaveText(served.revision.contentSha256);
	await expect(page.getByTestId('results-revision')).toContainText('Revision 1 of 1');
	await expect(page.getByTestId('criterion-row')).toHaveCount(7);
	await expect(page.getByTestId('results-desks-section')).toBeVisible();
	await expectNothingInjected(page);
	// After the step-up and the close, still nothing of the campaign, its results or the new token in web storage.
	expect(await storedByAriva(page), 'nothing in web storage').toBe(storedBefore);
	const everything = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
	for (const secret of [planned.id, run, served.revision.contentSha256]) expect(everything, 'web storage').not.toContain(secret);
	expect(everything, 'web storage').not.toMatch(/eyJ[\w-]+\.[\w-]+/);
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('a closed campaign\'s results show each criterion, the tables, the proof and the frozen hash; a terminal duty manager reads them without desks', async ({ page, browser }) => {
	test.setTimeout(150_000);
	const name = `Results ${run}`;
	const id = await runningCampaign(name);
	await groundTruth(id, `Missed a family of four ${run}`);
	const closed = await call('POST', `${campaignsUrl}/${id}/close`, { token: supervisor });
	expect(closed.status(), await closed.text()).toBe(200);
	const served = await call('GET', `${campaignsUrl}/${id}/results`, { token: supervisor });
	expect(served.status(), await served.text()).toBe(200);
	const frozen = await served.json();
	// A recomputation (the supervisor's second factor is fresh) adds revision 2 with a reason made of markup: free text that
	// every Validation.View holder of the site reads.
	const recomputeReason = `${xssPayloads[1]} ${xssPayloads[4]} desk logs re-entered ${run}`;
	const recomputed = await call('POST', `${campaignsUrl}/${id}/results/revisions`, { token: supervisor, data: { reason: recomputeReason } });
	expect(recomputed.status(), await recomputed.text()).toBe(201);
	const second = await recomputed.json();
	expect(second.revision).toMatchObject({ number: 2, revisions: 2, reason: recomputeReason });

	/** The latest revision with its reason as text, then revision 1 read back by the picker with its own hash. */
	const expectRevisions = async (view: Page) => {
		await expect(view.getByTestId('results-revision')).toContainText('Revision 2 of 2');
		await expect(view.getByTestId('results-hash')).toHaveText(second.revision.contentSha256);
		await expect(view.getByTestId('results-reason')).toHaveText(`Recomputed because: ${recomputeReason}`);
		await expect(view.getByTestId('results-reason-hint')).toContainText('never who');
		await expectNothingInjected(view);
		await view.getByTestId('results-revision-pick').selectOption('1');
		await expect(view.getByTestId('results-revision')).toContainText('Revision 1 of 2', { timeout: 60_000 });
		await expect(view.getByTestId('results-hash')).toHaveText(frozen.revision.contentSha256);
		await expect(view.getByTestId('results-reason')).toHaveCount(0);
		await view.getByTestId('results-revision-pick').selectOption('2');
		await expect(view.getByTestId('results-revision')).toContainText('Revision 2 of 2', { timeout: 60_000 });
	};

	// An administrator (border data included): every criterion, the desk table, the proof, the hash. No capture control.
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	const sidebar = page.getByTestId('app-sidebar');
	await expect(sidebar.getByRole('link', { name: 'Validation campaigns', exact: true })).toBeVisible();
	await expect(sidebar.getByRole('link', { name: 'Validation capture', exact: true })).toHaveCount(0);
	await page.goto(campaignsPage(`campaign=${id}&view=results`));
	await expect(page.getByTestId('campaign-name')).toHaveText(name);
	await expect(page.getByTestId('campaign-results')).toBeVisible({ timeout: 60_000 });
	await expectRevisions(page);
	await expect(page.getByTestId('criterion-row')).toHaveCount(7);
	const criterion = (key: string) => page.locator(`[data-testid="criterion-row"][data-criterion="${key}"]`);
	await expect(criterion('CountAccuracy').getByTestId('criterion-target')).toHaveText('At least 95.0%');
	await expect(criterion('WaitBias').getByTestId('criterion-target')).toHaveText('Within plus or minus 5.0%');
	await expect(criterion('NowcastError').getByTestId('criterion-target')).toHaveText('At most 2.0 min');
	await expect(criterion('Availability').getByTestId('criterion-target')).toHaveText('At least 99.0%');
	// Each verdict as the server judged it, in words (the E2E site has no sensors, so most criteria have no data).
	const words: Record<string, string> = { Pass: 'Pass', Fail: 'Fail', NoData: 'No data' };
	for (const row of [...second.criteria, second.desks.verdict]) {
		await expect(criterion(row.criterion), row.criterion).toHaveAttribute('data-verdict', row.verdict);
		await expect(criterion(row.criterion).getByTestId('criterion-verdict'), row.criterion).toContainText(words[row.verdict]);
	}
	await expect(page.getByTestId('results-review-flag').filter({ hasText: 'placeholders' })).toHaveCount(1);
	await expect(page.locator('[data-testid="results-line"][data-line="Entry A"]')).toBeVisible();
	await expect(page.getByTestId('results-line').filter({ hasText: lineXss })).toHaveCount(1);
	await expect(page.locator('[data-testid="results-tracer-zone"][data-zone="Q-A"] td').first()).toHaveText('4');
	await expect(page.getByTestId('results-tracer-overall')).toBeVisible();
	await expect(page.locator('[data-testid="results-desk"][data-desk="WIMM WD01"]')).toBeVisible();
	await expect(page.getByTestId('results-proof-section')).toBeVisible();
	await expect(page.getByTestId('results-proof-zone')).toHaveCount(2);
	await expect(page.getByTestId('results-no-calibrations')).toBeVisible();
	await expect(page.getByTestId('new-campaign')).toHaveCount(0);
	await expect(page.locator('[data-testid^="tally"], [data-testid^="capture"], [data-testid^="tracer-"]')).toHaveCount(0);
	await expectNothingInjected(page);

	// Light, dark and Arabic, as every screen (axe, WCAG 2.2 AA), with the right-to-left reading order.
	await expectAxeClean(page);
	await page.getByTestId('theme-toggle').click();
	await expect(page.locator('html')).toHaveClass(/\bdark\b/);
	await expectAxeClean(page);
	await page.getByTestId('theme-toggle').click();
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('حملات التحقق');
	await expect(page.getByTestId('campaign-tab-results')).toHaveText('النتائج');
	await expect(criterion('CountAccuracy').getByTestId('criterion-target')).toHaveText('95.0% على الأقل');
	const first = (await page.getByTestId('campaign-tab-progress').boundingBox())!;
	const last = (await page.getByTestId('campaign-tab-results').boundingBox())!;
	expect(first.x, 'the first tab is on the start side, the right in Arabic').toBeGreaterThan(last.x);
	expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
	await expectAxeClean(page);
	await guards.expectClean();

	// A terminal duty manager (airport side): the same revision and hash, no desk criterion, no desk table, no desk progress.
	const duty = await pageAs(browser, accounts().webCampaignDuty);
	const dutyGuards = await guardPage(duty);
	await duty.goto(campaignsPage(`campaign=${id}&view=results`));
	await expect(duty.getByTestId('campaign-results')).toBeVisible({ timeout: 60_000 });
	await expectRevisions(duty);
	await expect(duty.getByTestId('criterion-row')).toHaveCount(6);
	await expect(duty.locator('[data-testid="criterion-row"][data-criterion="DeskStateAgreement"]')).toHaveCount(0);
	await expect(duty.getByTestId('results-desks-section')).toHaveCount(0);
	await expect(duty.getByText('WD01')).toHaveCount(0);
	await duty.getByTestId('campaign-tab-progress').click();
	await expect(duty.locator('[data-testid="progress-line"][data-line="Entry A"] [data-testid="progress-bins"]')).toHaveText('2 of 20');
	await expect(duty.getByTestId('progress-desks-section')).toHaveCount(0);
	await expect(duty.getByText('WD01')).toHaveCount(0);
	await dutyGuards.expectClean();
	await duty.context().close();
});

test('campaign, zone and line names and correction reasons render as text on the list, the progress, the counts and the results', async ({ page }) => {
	test.setTimeout(120_000);
	const name = `${xssPayloads[4]} ${xssPayloads[0]} Names ${run}`;
	const id = await runningCampaign(name);
	const reason = `${xssPayloads[3]} ${xssPayloads[1]} ${run}`;
	await groundTruth(id, reason);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webCampaignDuty);
	await openCampaigns(page);
	const row = page.getByTestId('campaign-row').filter({ hasText: `Names ${run}` });
	await expect(row.getByTestId('campaign-link')).toHaveText(name);
	await expect(row).toHaveAttribute('data-status', 'Running');
	await row.getByTestId('campaign-link').click();
	await expect(page.getByTestId('campaign-name')).toHaveText(name);
	await expect(page.getByTestId('progress-line').filter({ hasText: lineXss })).toContainText(zoneXss);
	await expect(page.getByTestId('progress-tracer-zone').filter({ hasText: zoneXss })).toHaveCount(1);
	await page.getByTestId('campaign-tab-counts').click();
	await expect(page.getByTestId('count-reason').first()).toHaveText(reason);
	await page.getByTestId('counts-line').selectOption({ label: lineXss });
	await expect(page.getByTestId('counts-none')).toBeVisible();
	await page.getByTestId('campaign-tab-results').click();
	await expect(page.getByTestId('results-live')).toBeVisible({ timeout: 60_000 });
	await expect(page.getByTestId('results-line').filter({ hasText: lineXss })).toHaveCount(1);
	await expect(page.locator('[data-testid="results-proof-zone"]').filter({ hasText: zoneXss })).toHaveCount(1);
	await expectNothingInjected(page);

	// The duty manager plans without desks: the form offers none (desk states are border data).
	await page.getByTestId('campaigns-back').click();
	await page.getByTestId('new-campaign').click();
	await expect(page.getByTestId('campaign-form-zone')).toHaveCount(2);
	await expect(page.getByTestId('campaign-form-desks')).toHaveCount(0);
	await page.getByTestId('campaign-form-cancel').click();
	await guards.expectClean();
});

test('the address is checked before any request: another site, a path, another site\'s campaign or markup never reach Ariva', async ({ page }) => {
	test.setTimeout(120_000);
	const own = await runningCampaign(`Address ${run}`);
	const other = await otherSiteCampaign();
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webCampaignDuty);
	// Every request from here on, decoded, so an encoded path or markup would show too.
	const requests: string[] = [];
	page.on('request', (request: Request) => {
		const url = request.url();
		let decoded = url;
		try {
			decoded = decodeURIComponent(url);
		} catch {
			// not decodable: kept as sent
		}
		requests.push(decoded);
	});

	// Each hostile address is opened in turn; soft assertions report every one that is not refused.
	const opens = async (query: string) => {
		await page.goto(`/validation?${query}`);
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('Validation campaigns');
	};
	const expectOwnList = async (what: string) => {
		await expect.soft(page.getByTestId('campaigns-site'), what).toHaveValue(site);
		await expect.soft(page.getByTestId('campaign-row').filter({ hasText: `Address ${run}` }), what).toHaveCount(1);
		await expect.soft(page.getByTestId('campaign-detail'), what).toHaveCount(0);
	};
	// A site the caller does not hold, and a site code with a path in it: the caller's own site.
	await opens('site=E2EV');
	await expectOwnList('another site');
	await opens('site=E2EW%2F..%2Fadmin');
	await expectOwnList('a site with a path');
	// A campaign that is a path: the list.
	await opens(`site=${site}&campaign=..%2F..%2Fadmin%2Fusers`);
	await expectOwnList('a campaign that is a path');
	// Another site's campaign by its GUID in capitals, with this site and with the other one: not found at the caller's site.
	for (const query of [`site=${site}&campaign=${other.toUpperCase()}`, `site=DMO&campaign=${other.toUpperCase()}`]) {
		await opens(query);
		await expect.soft(page.getByTestId('campaign-missing'), query).toBeVisible();
		await expect.soft(page.getByTestId('campaigns-site'), query).toHaveValue(site);
	}
	// A view made of markup: the Progress tab.
	await opens(`site=${site}&campaign=${own}&view=%3Cscript%3Ealert(1)%3C%2Fscript%3E`);
	await expect.soft(page.getByTestId('campaign-name')).toHaveText(`Address ${run}`);
	await expect.soft(page.getByTestId('campaign-tab-progress')).toHaveAttribute('aria-selected', 'true');
	await expect.soft(page.getByTestId('progress-lines')).toBeVisible();
	await expectNothingInjected(page);

	// (The home screen's own reads at the caller's site, such as its floor plans, may still be on their way: they are not
	// hostile, so the check looks for the values the addresses carried.)
	const api = requests.filter((url) => url.includes('/api/'));
	for (const hostile of ['E2EV', 'DMO', '..', '<script', 'alert(', 'admin/users', other.toUpperCase()])
		expect.soft(api.filter((url) => url.includes(hostile)), `requests carrying ${hostile}`).toEqual([]);
	// The other site's campaign was asked for only at the caller's site, by its GUID in lower case (answered 404).
	const asked = api.filter((url) => url.includes(other)).map((url) => new URL(url).pathname);
	expect(asked.length, 'the campaign asked for').toBeGreaterThan(0);
	expect([...new Set(asked)]).toEqual([`/api/v1/sites/${site}/validation/campaigns/${other}`]);
	allowStatuses(guards, 404);
	await guards.expectClean();
});

for (const role of [
	{ name: 'a validation observer', account: () => accounts().webCampaignObserver },
	{ name: 'a handler station manager', account: () => accounts().webHandler }
]) {
	test(`${role.name} has no campaigns screen and asks the API for nothing about it`, async ({ page }) => {
		const guards = await guardPage(page);
		const calls: string[] = [];
		page.on('request', (request: Request) => {
			if (/\/validation\/campaigns/.test(request.url())) calls.push(request.url());
		});
		await signInThroughUi(page, role.account());
		await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Validation campaigns', exact: true })).toHaveCount(0);
		await page.goto(campaignsPage('view=results'));
		await expect(page.getByTestId('no-access')).toBeVisible();
		await expect(page.getByTestId('campaigns-site')).toHaveCount(0);
		expect(calls, 'no campaign request').toEqual([]);
		await guards.expectClean();
	});
}
