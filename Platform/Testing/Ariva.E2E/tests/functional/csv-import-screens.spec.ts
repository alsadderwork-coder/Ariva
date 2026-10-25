import crypto from 'node:crypto';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, login, signIn, signToken, unusedTotpCode, type Account } from '../support/accounts';
import { createAirport } from '../support/airports';
import { guardPage } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { allowStatuses, databaseAvailable, fillSecret, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-123b: the CSV import screens on the API of ARV-123a. On the devices screen a holder of Device.Create imports the
// as-built pack's device list: choose a file (type and size checked before anything is sent), check it (a preview, which
// asks for a fresh second factor through the step-up dialog the registration uses), read the refusals by row and column,
// and commit the same file; each new credential is shown once in the dialog, copied, and gone once the dialog closes,
// which needs a confirmation. In the topology screen's desk mappings panel a holder of DeskCodeMapping.Create imports
// desk code mappings the same way, without a second factor. Row content (a model or a zone named with markup) is text.
// Credentials are checked by their format inside an expect and never printed (ARV-152, CWE-532).
// The site is new each run (CIW and a random tail; no other suite uses it), built through the admin API; everything the
// suite creates is removed at the end except the site and its published zone profile version.

test.skip(!databaseAvailable, 'the import screens need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });
// The device import's dialog shows live credentials of the suite's devices: a trace kept on failure would hold them (CWE-532).
test.use({ trace: 'off' });

const admin = `${hosts.main}/api/v1/admin`;
const deviceUrl = `${hosts.ingest}/api/v1/ingest/device`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const random = (n: number) => Array.from(crypto.randomBytes(n), (b) => letters[b % 26]).join('');
const tail = random(4);
const site = `CIW${tail}`;
/** The airport's code, drawn when the airport is created (another code when one is taken). */
let iata = '';
const hostileModel = '<img src=x onerror=alert(1)>';
const hostileZone = `<img src=x onerror=alert(2)> ${tail}`;
/** Elements and attributes a successful injection would create. */
const injected = 'img[src="x"], svg[onload], iframe, [onerror], [onload], a[href^="javascript:"]';
/** A device credential: the marker and 43 base64url characters. Tested inside an expect, never printed. */
const credentialFormat = /^ardk_[A-Za-z0-9_-]{43}$/;
const deviceHeader = 'code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone';
const mappingHeader = 'externalSystem,externalCode,checkpoint,desk';
const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

const account = (): Account => accounts().webCsvImport;
let stepped = '';

const row = (code: string, model = 'PC2SE', x = 20, zone = 'Snake A', orientation = 0) =>
	`${code},StereoVision,${model},HttpsPush,Xovis,Ntp,T1,L0,${x},16,5,${orientation},${zone}`;
const csv = (header: string, ...rows: string[]) => `${header}\r\n${rows.join('\r\n')}\r\n`;
const csvFile = (name: string, text: string) => ({ name, mimeType: 'text/csv', buffer: Buffer.from(text, 'utf8') });

/** A multipart body for the API calls of the setup and the refusal checks. */
function form(text: string): FormData {
	const body = new FormData();
	body.append('file', new Blob([text], { type: 'text/csv' }), 'import.csv');
	return body;
}

async function secondFactor(entry: Account): Promise<string> {
	const signedIn = await login(entry.userName, entry.password, undefined, undefined, { code: await unusedTotpCode(entry.totpSecret!) });
	expect(signedIn.status(), `sign-in of ${entry.userName}`).toBe(200);
	return (await signedIn.json()).accessToken;
}

async function create(path: string, data: unknown): Promise<string> {
	const response = await call('POST', `${admin}/${path}`, { token: stepped, data });
	await expectStatus(response, 201, path);
	const json = await response.json();
	return (json.id ?? json.profile?.id) as string;
}

/** Every record of a kind at the site (searches page at most 500). */
async function search(entity: string, token = stepped): Promise<any[]> {
	const response = await call('GET', `${admin}/${entity}?siteCode=${site}&pageSize=500`, { token });
	return response.status() === 200 ? (await response.json()).data : [];
}

/**
 * The site: one level, a published profile with two queue zones (Snake A, and one named with markup, apart on the level),
 * an immigration checkpoint (desk D01, e-gate EG01) and a check-in counter C01.
 */
async function siteWithTopology(): Promise<void> {
	const created = await call('POST', `${admin}/sites`, { token: stepped, data: { code: site, name: `E2E CSV import screens ${site}` } });
	await expectStatus(created, 201, site);
	const airport = await createAirport((code) =>
		call('POST', `${admin}/airports`, { token: stepped, data: { iataCode: code, name: `E2E CSV import screens ${code}`, timeZoneId: 'Asia/Dubai' } })
	);
	const airportId = airport.id;
	iata = airport.iataCode;
	const terminal = await create('terminals', { airportId, code: 'T1', name: 'Terminal 1', siteCode: site });
	const level = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	const immigration = await create('checkpoints', { levelId: level, code: 'IMM', name: 'Immigration', kind: 'Immigration' });
	await create('desks', { checkpointId: immigration, code: 'D01', kind: 'Desk', laneCategories: ['CIT'] });
	await create('desks', { checkpointId: immigration, code: 'EG01', kind: 'EGate', laneCategories: ['EG'] });
	const checkIn = await create('checkpoints', { levelId: level, code: 'CHK', name: 'Check-in', kind: 'CheckIn' });
	await create('desks', { checkpointId: checkIn, code: 'C01', kind: 'Counter' });
	const draft = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals' });
	for (const [name, x, mark] of [
		['Snake A', 10, 'A'],
		[hostileZone, 50, 'H']
	] as const) {
		const zone = await create(`zone-profiles/${draft}/zones`, { name, kind: 'Queue', levelId: level, polygon: `${x} 10,${x + 24} 10,${x + 24} 22,${x} 22` });
		await create(`zone-profiles/${draft}/lines`, { name: `Entry ${mark}`, role: 'Entry', levelId: level, startX: x, startY: 12, endX: x, endY: 16, zoneId: zone });
		await create(`zone-profiles/${draft}/lines`, { name: `Exit ${mark}`, role: 'Exit', levelId: level, startX: x + 20, startY: 22, endX: x + 24, endY: 22, zoneId: zone });
	}
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token: stepped })).json();
	const published = await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token: stepped, data: { geometryHash } });
	await expectStatus(published, 200, 'publish');
}

/** Signs in with the second factor 20 minutes old, so the device import asks for a fresh code (as devices.spec.ts). */
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
	await page.goto('/login');
	await fillSignIn(page, account(), await unusedTotpCode(account().totpSecret!));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
}

/**
 * Opens the devices screen at the suite's site and its import dialog. A full page load takes a new token from the refresh
 * cookie and the browser's language, so a test that aged its token or chose Arabic is already on the screen.
 */
async function openDeviceImport(page: Page, navigate = true): Promise<Locator> {
	if (navigate) await page.goto('/devices');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');
	await page.locator('#devices-site').selectOption(site);
	await expect(page.getByTestId('import-devices')).toBeEnabled();
	await page.getByTestId('import-devices').click();
	const dialog = page.getByTestId('csv-import-dialog');
	await expect(dialog.getByRole('heading', { level: 2 })).toHaveText('Import devices from a CSV file');
	await expect(dialog).toContainText(site);
	return dialog;
}

/** Opens the topology screen at the suite's site down to a desk, and the mapping import of its panel. */
async function openMappingImport(page: Page, desk = 'D01', navigate = true): Promise<Locator> {
	if (navigate) await page.goto('/topology');
	await page.locator('#topology-site').selectOption(site);
	const column = (entity: string) => page.getByTestId(`column-${entity}`);
	await column('airport')
		.getByRole('button', { name: new RegExp(`^${iata}\\b`) })
		.click();
	await column('terminal').getByRole('button', { name: /^T1\b/ }).click();
	await column('level').getByRole('button', { name: /^L0\b/ }).click();
	await column('checkpoint').getByRole('button', { name: /^IMM\b/ }).click();
	await column('desk')
		.getByRole('button', { name: new RegExp(`^${desk}\\b`) })
		.click();
	await expect(page.getByTestId('desk-mappings')).toBeVisible();
	await page.getByTestId('import-mappings').click();
	return page.getByTestId('csv-import-dialog');
}

async function expectAccessible(page: Page): Promise<void> {
	// Measure once every CSS colour transition has ended (a theme or language switch starts them; see validation-capture.spec.ts).
	await page.waitForFunction(() => document.getAnimations().every((a) => !(a instanceof CSSTransition) || a.playState !== 'running'));
	const results = await new AxeBuilder({ page }).include('[data-testid="csv-import-dialog"]').withTags(tags).analyze();
	const summary = results.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`);
	expect(summary, 'axe violations in the import dialog').toEqual([]);
}

test.beforeAll(async () => {
	test.setTimeout(120_000);
	stepped = await secondFactor(account());
	await siteWithTopology();
});

test.afterAll(async () => {
	test.setTimeout(120_000);
	if (!stepped) return;
	// Removing a device is critical: a fresh second factor, since the suite may outlast the first one's 15 minutes.
	const fresh = await secondFactor(account());
	for (const device of await search('devices', fresh)) await call('DELETE', `${admin}/devices/${device.id}`, { token: fresh });
	for (const mapping of await search('desk-code-mappings', fresh)) await call('DELETE', `${admin}/desk-code-mappings/${mapping.id}`, { token: fresh });
	const history = await call('GET', `${admin}/zone-profiles?siteCode=${site}`, { token: fresh });
	for (const draft of history.status() === 200 ? (await history.json()).filter((p: any) => p.status === 'Draft') : [])
		await call('DELETE', `${admin}/zone-profiles/${draft.id}`, { token: fresh });
	for (const entity of ['desks', 'checkpoints', 'levels', 'terminals'])
		for (const record of await search(entity, fresh)) await call('DELETE', `${admin}/${entity}/${record.id}`, { token: fresh });
	const list = await call('GET', `${admin}/airports?pageSize=500`, { token: fresh });
	for (const airport of list.status() === 200 ? (await list.json()).data.filter((a: any) => a.iataCode === iata) : [])
		await call('DELETE', `${admin}/airports/${airport.id}`, { token: fresh });
});

test('an administrator imports devices after a step-up; the credentials are shown once and gone when the dialog closes', async ({ page, context }) => {
	test.skip(!developmentSigningKey(), 'needs the run key to age the token');
	test.setTimeout(180_000);
	await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: webUrl });
	const guards = await guardPage(page);
	const logged: string[] = [];
	page.on('console', (message) => logged.push(message.text()));

	await signInAged(page);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Devices' }).click();
	const dialog = await openDeviceImport(page, false);
	await dialog.getByLabel('CSV file').setInputFiles(csvFile('devices.csv', csv(deviceHeader, row('S-01'), row('S-02', 'PC2SE', 24))));
	await dialog.getByRole('button', { name: 'Check the file' }).click();

	// Importing devices is critical and the second factor is 20 minutes old: the step-up dialog of the registration asks
	// for a fresh code over the import dialog, and the preview is sent again once it is given.
	const stepUp = page.getByTestId('step-up-dialog');
	await expect(stepUp).toBeVisible();
	await fillSecret(page.locator('#step-up-code'), await unusedTotpCode(account().totpSecret!));
	const previewed = page.waitForResponse((response) => response.url().includes('/device-imports/preview') && response.status() === 200);
	await stepUp.getByRole('button', { name: 'Confirm' }).click();
	await expect(stepUp).toBeHidden();
	const previewToken: string = (await (await previewed).json()).previewToken ?? '';

	await expect(dialog.getByTestId('import-ready')).toContainText('Every row passes.');
	await expect(dialog.getByTestId('import-summary')).toContainText('2 rows: 2 pass, nothing refused.');
	await expect(dialog.getByTestId('import-row')).toHaveCount(2);
	const commit = dialog.getByTestId('import-commit');
	await expect(commit).toHaveText('Register 2 devices');
	await expect(commit).toBeEnabled();
	expect(previewToken.length > 0, 'the preview answered a token').toBe(true);
	expect((await page.content()).includes(previewToken), 'the preview token is never on the page').toBe(false);
	await commit.click();

	const shown = dialog.getByTestId('import-credentials');
	await expect(shown).toBeVisible();
	await expect(shown.getByTestId('import-credentials-warning')).toContainText('Shown this once and never again');
	await expect(shown.getByTestId('import-credential')).toHaveCount(2);
	expect((await shown.getByTestId('import-credential').locator('th').allTextContents()).map((c) => c.trim())).toEqual(['S-01', 'S-02']);
	const values = (await shown.getByTestId('import-credential-value').allTextContents()).map((v) => v.trim());
	expect(values.length === 2 && values.every((v) => credentialFormat.test(v)), 'each credential has the device credential format').toBe(true);
	expect(new Set(values).size, 'a credential per device').toBe(2);
	expect((await call('GET', deviceUrl, { token: values[0] })).status(), 'the shown credential works at Ingest').toBe(200);

	// Copy all: one CSV line per device, code then credential.
	await shown.getByTestId('import-copy-all').click();
	await expect(shown.getByTestId('import-copy-status')).toContainText('Copied.');
	const clipboard = await page.evaluate(() => navigator.clipboard.readText());
	expect(clipboard === ['code,credential', `S-01,${values[0]}`, `S-02,${values[1]}`].join('\r\n'), 'the clipboard holds every code and credential').toBe(true);

	// Closing needs the confirmation: Escape (twice, which the browser may enforce) and the close button keep the credentials.
	await expect(dialog.getByTestId('import-close')).toBeDisabled();
	await page.keyboard.press('Escape');
	await expect(dialog.getByTestId('import-confirm-first')).toBeVisible();
	await page.keyboard.press('Escape');
	await expect(dialog).toBeVisible();
	await expect(shown.getByTestId('import-credential')).toHaveCount(2);
	await dialog.getByTestId('import-confirm-copied').check();
	await dialog.getByTestId('import-close').click();
	await expect(dialog).toHaveCount(0);

	// Nothing kept: not on the page, in web storage, in the address or in the console; the token neither.
	const html = await page.content();
	const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
	expect(values.some((v) => html.includes(v)) || html.includes('ardk_'), 'no credential on the page once the dialog closed').toBe(false);
	expect(stored.includes('ardk_'), 'no credential in web storage').toBe(false);
	expect(page.url().includes('ardk_'), 'no credential in the address').toBe(false);
	expect(logged.some((message) => message.includes('ardk_')), 'no credential in the console').toBe(false);
	expect([html, stored, page.url()].some((text) => text.includes(previewToken)), 'the preview token is kept nowhere').toBe(false);

	// The list is refreshed: both devices in Commissioning; the registry never answers a credential.
	const table = page.getByTestId('devices-table');
	await expect(table.getByRole('row', { name: /S-01/ })).toContainText('Commissioning');
	await expect(table.getByRole('row', { name: /S-02/ })).toContainText('Commissioning');
	const registry = await (await call('GET', `${admin}/devices?siteCode=${site}&pageSize=500`, { token: stepped })).text();
	expect(values.some((v) => registry.includes(v.slice(13))), 'the registry never holds a credential').toBe(false);
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('a file with refused rows lists each refusal by row and column, notes the first 100, and cannot be committed', async ({ page }) => {
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	await signInThroughUi(page, account(), await unusedTotpCode(account().totpSecret!));
	const dialog = await openDeviceImport(page);

	const tooFar = Array.from({ length: 120 }, (_, i) => row(`B-${i + 1}`, 'PC2SE', 20, 'Snake A', 400));
	await dialog.getByLabel('CSV file').setInputFiles(csvFile('refused.csv', csv(deviceHeader, row('B-OK'), row('B-F', '=1+1'), ...tooFar)));
	await dialog.getByRole('button', { name: 'Check the file' }).click();
	await expect(dialog.getByTestId('import-refused')).toContainText('121 refusals');
	await expect(dialog.getByTestId('import-refusals-first')).toHaveText('The first 100 of 121 are listed.');
	await expect(dialog.getByTestId('import-refusal')).toHaveCount(100);
	const first = dialog.getByTestId('import-refusal').nth(0).locator('td');
	await expect(first).toHaveText(['3', '3 (C)', /^A cell may not start with =, \+, -, @/]);
	await expect(dialog.getByTestId('import-refusal').nth(1).locator('td')).toHaveText(['4', '12 (L)', /^The number is outside its column's range/]);
	expect(await dialog.getByTestId('import-refusals').textContent(), 'no refusal repeats a cell').not.toContain('=1+1');
	await expect(dialog.getByTestId('import-row')).toHaveCount(1);
	await expect(dialog.getByTestId('import-ready')).toHaveCount(0);
	await expect(dialog.getByTestId('import-commit')).toBeDisabled();
	await expectAccessible(page);

	// A file refused as a whole (its header is not the schema) is answered with the fixed text, in the dialog.
	await dialog.getByLabel('CSV file').setInputFiles(csvFile('header.csv', 'code,family\r\nS-01,StereoVision\r\n'));
	await dialog.getByRole('button', { name: 'Check the file' }).click();
	await expect(dialog.getByTestId('import-failure')).toContainText('The header names each column once, by its exact name');
	await expect(dialog.getByTestId('import-commit')).toBeDisabled();
	await dialog.getByTestId('import-close').click();
	await expect(dialog).toHaveCount(0);
	expect((await search('devices')).map((d) => d.code).filter((code: string) => code.startsWith('B-')), 'a preview registers nothing').toEqual([]);
	// A sign-in with an authenticator answers 401 for its second factor first; the refused header answers 400.
	allowStatuses(guards, 400, 401);
	await guards.expectClean();
});

test('row content from the file is text: a model and a zone named with markup run nothing and inject nothing', async ({ page }) => {
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	await signInThroughUi(page, account(), await unusedTotpCode(account().totpSecret!));
	const dialog = await openDeviceImport(page);
	await dialog.getByLabel('CSV file').setInputFiles(csvFile('hostile.csv', csv(deviceHeader, row('H-01', hostileModel), row('H-02', 'PC2SE', 62, hostileZone))));
	await dialog.getByRole('button', { name: 'Check the file' }).click();
	await expect(dialog.getByTestId('import-ready')).toBeVisible();
	const rows = dialog.getByTestId('import-row');
	await expect(rows).toHaveCount(2);
	await expect(rows.nth(0).locator('td').nth(3)).toHaveText(hostileModel);
	await expect(rows.nth(1).locator('td').nth(6)).toHaveText(hostileZone);
	await expect(page.locator(injected)).toHaveCount(0);
	const html = await page.content();
	expect(html.includes('<img src=x') || html.includes('<img src="x"'), 'the markup is escaped text, never an element').toBe(false);

	await dialog.getByTestId('import-close').click();
	await expect(dialog).toHaveCount(0);
	expect((await search('devices')).map((d) => d.code).filter((code: string) => code.startsWith('H-')), 'a preview registers nothing').toEqual([]);
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('an oversized file, an empty one or one that is not CSV is refused before anything is sent', async ({ page }) => {
	const guards = await guardPage(page);
	const sent: string[] = [];
	page.on('request', (request) => {
		if (request.url().includes('-imports/')) sent.push(request.url());
	});
	await signInThroughUi(page, accounts().webAdmin);
	const dialog = await openDeviceImport(page);
	const input = dialog.getByLabel('CSV file');
	const check = dialog.getByRole('button', { name: 'Check the file' });

	await expect(dialog.getByTestId('import-file-name')).toHaveText('No file chosen');
	await input.setInputFiles({ name: 'big.csv', mimeType: 'text/csv', buffer: Buffer.alloc(1024 * 1024 + 1, 0x61) });
	await expect(dialog.getByTestId('import-file-problem')).toHaveText('The file is larger than 1 MB: split it into smaller files.');
	await expect(dialog.getByTestId('import-file-name')).toHaveText('big.csv');
	await expect(check).toBeDisabled();
	await input.setInputFiles({ name: 'devices.txt', mimeType: 'text/plain', buffer: Buffer.from(csv(deviceHeader, row('S-09'))) });
	await expect(dialog.getByTestId('import-file-problem')).toHaveText('Choose a .csv file (comma separated text).');
	await expect(check).toBeDisabled();
	await input.setInputFiles({ name: 'devices.csv', mimeType: 'application/pdf', buffer: Buffer.from(csv(deviceHeader, row('S-09'))) });
	await expect(dialog.getByTestId('import-file-problem')).toHaveText('Choose a .csv file (comma separated text).');
	await input.setInputFiles({ name: 'empty.csv', mimeType: 'text/csv', buffer: Buffer.alloc(0) });
	await expect(dialog.getByTestId('import-file-problem')).toHaveText('The file is empty.');
	await expect(check).toBeDisabled();
	// Exactly 1 MB is taken (the server reads it and judges the rows).
	await input.setInputFiles({ name: 'limit.csv', mimeType: 'text/csv', buffer: Buffer.alloc(1024 * 1024, 0x61) });
	await expect(dialog.getByTestId('import-file-problem')).toHaveCount(0);
	await expect(check).toBeEnabled();

	await dialog.getByTestId('import-close').click();
	await expect(dialog).toHaveCount(0);
	expect(sent, 'no import request left the browser').toEqual([]);
	await guards.expectClean();
});

test('an administrator imports desk code mappings from the desk mappings panel, without a second factor', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	const dialog = await openMappingImport(page);
	await expect(dialog.getByRole('heading', { level: 2 })).toHaveText('Import desk code mappings from a CSV file');
	await expect(dialog).toContainText(site);

	// An AMAN code for a check-in counter is refused at its desk column; nothing can be committed.
	await dialog.getByLabel('CSV file').setInputFiles(csvFile('refused.csv', csv(mappingHeader, `Aman,${tail}-01,IMM,D01`, `Aman,${tail}-02,CHK,C01`)));
	await dialog.getByRole('button', { name: 'Check the file' }).click();
	await expect(dialog.getByTestId('import-refusal').locator('td')).toHaveText(['3', '4 (D)', 'An Aman code maps an immigration desk or e-gate, an Aodb code a check-in counter.']);
	await expect(dialog.getByTestId('import-commit')).toBeDisabled();

	const codes = [`${tail}-01`, `${tail}-EG1`, `${tail}/C01`];
	await dialog
		.getByLabel('CSV file')
		.setInputFiles(csvFile('mappings.csv', csv(mappingHeader, `Aman,${codes[0]},IMM,D01`, `Aman,${codes[1]},IMM,EG01`, `Aodb,${codes[2]},CHK,C01`)));
	await dialog.getByRole('button', { name: 'Check the file' }).click();
	await expect(dialog.getByTestId('import-ready')).toBeVisible();
	await expect(dialog.getByTestId('import-row')).toHaveCount(3);
	await expect(dialog.getByTestId('import-row').nth(2).locator('td')).toHaveText(['4', 'AODB', codes[2], 'CHK', 'C01']);
	await dialog.getByRole('button', { name: 'Create 3 mappings' }).click();
	await expect(dialog).toHaveCount(0);
	await expect(page.getByText('3 desk code mappings created.')).toBeVisible();
	// The panel reloads its desk's codes; the server holds all three.
	await expect(page.getByTestId('desk-mappings').getByRole('listitem')).toContainText(codes[0]);
	expect((await search('desk-code-mappings')).map((m) => m.externalCode).sort()).toEqual([...codes].sort());
	allowStatuses(guards, 400);
	await guards.expectClean();
});

test('a user past their import allowance, or a busy site, is told so in words of its own', async ({ page }) => {
	// ARV-155: the server's 429 for the user's own allowance (not the host's one upload at a time) and its 409 for a site
	// whose lock another import or change holds; the answers are stubbed, since the suite raises the allowance for its runs.
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	const answers = [
		{ status: 429, title: 'Too many requests', detail: 'Too many import previews or commits for now: try again after the time Retry-After gives.' },
		{ status: 409, title: 'Conflict', detail: 'Another import or change at this site is in progress: try again in a moment.' }
	];
	await page.route('**/desk-code-mapping-imports/preview', (route) => {
		const answer = answers.shift();
		return answer
			? route.fulfill({ status: answer.status, contentType: 'application/problem+json', headers: { 'Retry-After': '60' }, body: JSON.stringify(answer) })
			: route.continue();
	});
	const dialog = await openMappingImport(page);
	await dialog.getByLabel('CSV file').setInputFiles(csvFile('mappings.csv', csv(mappingHeader, `Aman,${tail}-L1,IMM,D01`)));

	await dialog.getByRole('button', { name: 'Check the file' }).click();
	await expect(dialog.getByTestId('import-failure')).toHaveText('You have checked or imported many files in the last few minutes: wait a few minutes and try again.');
	await dialog.getByRole('button', { name: 'Check the file' }).click();
	await expect(dialog.getByTestId('import-failure')).toHaveText('Another import or change at this site is in progress: try again in a moment.');
	await expect(dialog.getByTestId('import-commit')).toBeDisabled();
	await dialog.getByTestId('import-close').click();
	allowStatuses(guards, 429, 409);
	await guards.expectClean();
});

test('in Arabic the import dialog is translated, its reasons too, and mirrors', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	await page.goto('/topology');
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	const dialog = await openMappingImport(page, 'EG01', false);
	await expect(dialog.getByRole('heading', { level: 2 })).toHaveText('استيراد رموز المكاتب من ملف CSV');
	await expect(dialog.getByTestId('import-choose')).toHaveText('اختر ملفاً');
	await expect(dialog.getByTestId('import-file-name')).toHaveText('لم يُختر ملف');
	await dialog.getByLabel('ملف CSV').setInputFiles(csvFile('refused.csv', csv(mappingHeader, `Aodb,${tail}-X1,IMM,EG01`)));
	await dialog.getByRole('button', { name: 'افحص الملف' }).click();
	await expect(dialog.getByTestId('import-refusal').locator('td').nth(2)).toHaveText('يُربط رمز Aman بمكتب جوازات أو بوابة إلكترونية، ورمز Aodb بكاونتر تسجيل.');
	await expect(dialog.getByTestId('import-refused')).toContainText('حالات الرفض: 1');

	// Right to left: the close button sits on the left of the title, the commit button on the left of the others.
	const title = await dialog.getByRole('heading', { level: 2 }).boundingBox();
	const dismiss = await dialog.getByTestId('import-dismiss').boundingBox();
	expect(title && dismiss && dismiss.x < title.x, 'the close button is on the left').toBeTruthy();
	const commit = await dialog.getByTestId('import-commit').boundingBox();
	const cancel = await dialog.getByTestId('import-close').boundingBox();
	expect(commit && cancel && commit.x < cancel.x, 'the commit button is on the left').toBeTruthy();
	await expectAccessible(page);
	await dialog.getByTestId('import-close').click();
	await expect(dialog).toHaveCount(0);
	allowStatuses(guards, 400);
	await guards.expectClean();
});

test('the import actions are hidden without the create permissions, and the server refuses them', async ({ page }) => {
	// A terminal duty manager reads a desk's codes but maps none: the panel has no import.
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().web);
	await page.goto('/topology');
	const column = (entity: string) => page.getByTestId(`column-${entity}`);
	await column('airport').getByRole('button', { name: /^DMO\b/ }).click();
	await column('terminal').getByRole('button', { name: /^T1\b/ }).click();
	await column('level').getByRole('button', { name: /^ARR\b/ }).click();
	await column('checkpoint').getByRole('button', { name: /^IMM\b/ }).click();
	await column('desk').getByRole('button', { name: /^AR-01\b/ }).click();
	await expect(page.getByTestId('desk-mappings')).toContainText('IN01');
	await expect(page.getByTestId('import-mappings')).toHaveCount(0);
	await guards.expectClean();

	const duty = (await signIn(accounts().web)).accessToken;
	const mappingFile = csv(mappingHeader, 'Aman,X-01,IMM,AR-01');
	expect((await call('POST', `${admin}/sites/DMO/desk-code-mapping-imports/preview`, { token: duty, form: form(mappingFile) })).status(), 'mapping preview without the permission').toBe(403);
	const handler = (await signIn(accounts().webHandler)).accessToken;
	expect((await call('POST', `${admin}/sites/DMO/device-imports/preview`, { token: handler, form: form(csv(deviceHeader, row('S-01'))) })).status(), 'device preview without the permission').toBe(403);
});

test('the devices screen offers the import only with Device.Create', async ({ page }) => {
	// Every role that reads the devices screen may register devices (a handler has no such screen), so the screen's rule
	// is shown on an answer of /api/auth/me without Device.Create; the server refuses such callers (the test above).
	await page.route('**/api/auth/me', async (route) => {
		const response = await route.fetch();
		const body = await response.json();
		body.permissions = (body.permissions as string[]).filter((p) => p !== 'Device.Create');
		return route.fulfill({ response, json: body });
	});
	await signInThroughUi(page, accounts().webAdmin);
	await page.goto('/devices');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');
	await expect(page.getByTestId('devices-table')).toBeVisible();
	await expect(page.getByTestId('import-devices')).toHaveCount(0);
	await expect(page.getByTestId('register-device')).toHaveCount(0);
});
