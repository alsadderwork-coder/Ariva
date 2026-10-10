import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-052: the topology screen, airport to desk, on the admin API of ARV-014 and ARV-015. An administrator builds and
// changes the tree; the operational roles browse their sites read only (a border supervisor without the desk code
// mappings); names are text, never markup (CWE-79); the server refuses what the screen hides (CWE-863).

test.skip(!databaseAvailable, 'the topology screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
// The payload test adds terminals to the airport the first test creates.
test.describe.configure({ mode: 'serial' });

/** A fresh IATA code for this run's airport (deployment-wide and unique). */
const iata = Array.from(crypto.randomBytes(3), (b) => String.fromCharCode(65 + (b % 26))).join('');
// Checkpoint codes are unique across a site (ARV-055): each run uses its own in the shared site E2E1.
const checkpointCode = `I${iata}`;

function column(page: Page, entity: string) {
	return page.getByTestId(`column-${entity}`);
}

async function open(page: Page, account = accounts().webAdmin): Promise<void> {
	await signInThroughUi(page, account);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Topology' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Topology');
}

/** Opens the demo airport down to its arrival immigration desks. */
async function browseToDemoDesks(page: Page): Promise<void> {
	await column(page, 'airport')
		.getByRole('button', { name: /^DMO\b/ })
		.click();
	await column(page, 'terminal').getByRole('button', { name: /^T1\b/ }).click();
	await column(page, 'level')
		.getByRole('button', { name: /^ARR\b/ })
		.click();
	await column(page, 'checkpoint')
		.getByRole('button', { name: /^IMM\b/ })
		.click();
	await expect(column(page, 'desk').getByRole('button', { name: /^AR-01\b/ })).toBeVisible();
}

test('an administrator builds an airport down to its desks, edits a desk, maps its AMAN code and deletes one', async ({ page }) => {
	const guards = await guardPage(page);
	await open(page);
	await page.getByLabel('Site').selectOption('E2E1');

	await page.getByTestId('add-airport').click();
	await page.getByLabel('IATA code').fill(iata);
	await page.getByTestId('create-airport').getByLabel('Name', { exact: true }).fill(`E2E web ${iata}`);
	// The zone is chosen from the IANA list (grouped by area, with the UTC offset), never typed.
	const zone = page.getByTestId('create-airport').getByRole('combobox', { name: 'Time zone (IANA)' });
	await expect(zone.locator('optgroup[label="Asia"] option[value="Asia/Amman"]')).toHaveCount(1);
	await zone.selectOption('Asia/Amman');
	await page.getByTestId('create-airport').getByRole('button', { name: 'Create' }).click();
	await column(page, 'airport')
		.getByRole('button', { name: new RegExp(`^${iata}\\b`) })
		.click();
	await expect(page.getByTestId('edit-airport').getByRole('combobox', { name: 'Time zone (IANA)' })).toHaveValue('Asia/Amman');

	await page.getByTestId('add-terminal').click();
	await page.getByTestId('create-terminal').getByLabel('Code').fill('T7');
	await page.getByTestId('create-terminal').getByLabel('Name').fill('Terminal 7');
	await page.getByTestId('create-terminal').getByRole('button', { name: 'Create' }).click();
	await column(page, 'terminal').getByRole('button', { name: /^T7\b/ }).click();
	await expect(page.getByTestId('entity-panel')).toContainText('E2E1');

	await page.getByTestId('add-level').click();
	await page.getByTestId('create-level').getByLabel('Code').fill('L1');
	await page.getByTestId('create-level').getByLabel('Name').fill('Arrivals hall');
	await page.getByTestId('create-level').getByRole('button', { name: 'Create' }).click();
	await column(page, 'level').getByRole('button', { name: /^L1\b/ }).click();

	await page.getByTestId('add-checkpoint').click();
	await page.getByTestId('create-checkpoint').getByLabel('Code').fill(checkpointCode);
	await page.getByTestId('create-checkpoint').getByLabel('Name').fill('Immigration');
	await page.getByTestId('create-checkpoint').getByLabel('Kind').selectOption('Immigration');
	await page.getByTestId('create-checkpoint').getByRole('button', { name: 'Create' }).click();
	await column(page, 'checkpoint')
		.getByRole('button', { name: new RegExp(`^${checkpointCode}\\b`) })
		.click();

	// A range of three desks for citizens.
	await page.getByTestId('add-desk').click();
	await page.getByRole('button', { name: 'Add a range' }).click();
	const range = page.getByTestId('create-range');
	await range.getByLabel('Prefix', { exact: true }).fill('D');
	await range.getByLabel('From', { exact: true }).fill('1');
	await range.getByLabel('To', { exact: true }).fill('3');
	await range.getByLabel('Digits', { exact: true }).fill('2');
	await range.getByLabel('Kind').selectOption('Desk');
	await range.getByRole('checkbox', { name: /CIT/ }).check();
	await range.getByRole('button', { name: 'Create' }).click();
	await expect(page.getByText('3 desks created.')).toBeVisible();
	await expect(column(page, 'desk').getByRole('listitem')).toHaveCount(3);

	// Edit D02: a name, visitors too, out of service; then its AMAN code.
	await column(page, 'desk')
		.getByRole('button', { name: /^D02\b/ })
		.click();
	const panel = page.getByTestId('entity-panel');
	await panel.getByLabel('Name', { exact: true }).fill('Desk two');
	await panel.getByRole('checkbox', { name: /VIS/ }).check();
	await panel.getByLabel('In service').uncheck();
	await panel.getByRole('button', { name: 'Save' }).click();
	await expect(column(page, 'desk').getByRole('button', { name: /^D02\b/ })).toContainText('Out of service');
	await panel.getByLabel('Their code').fill(`${iata}02`);
	await panel.getByRole('button', { name: 'Add the code' }).click();
	await expect(page.getByTestId('desk-mappings').getByRole('listitem')).toContainText(`${iata}02`);
	// Removing a code asks first, on the page.
	await page.getByTestId('remove-mapping').click();
	await page.getByTestId('confirm-remove-mapping').click();
	await expect(page.getByTestId('desk-mappings')).toContainText('No codes mapped');

	// Delete D03, with the confirmation on the page.
	await column(page, 'desk')
		.getByRole('button', { name: /^D03\b/ })
		.click();
	await page.getByTestId('delete').click();
	await page.getByTestId('confirm-delete').click();
	await expect(column(page, 'desk').getByRole('listitem')).toHaveCount(2);

	// The server agrees.
	const admin = (await signIn(accounts().webAdmin)).accessToken;
	const desks = await call('GET', `${hosts.main}/api/v1/admin/desks?siteCode=E2E1&text=D02`, { token: admin });
	const d02 = ((await desks.json()).data as { code: string; name: string; laneCategories: string[]; inService: boolean; checkpointId: string }[]).find(
		(d) => d.name === 'Desk two'
	);
	expect(d02).toMatchObject({ code: 'D02', laneCategories: ['CIT', 'VIS'], inService: false });
	await guards.expectClean();
});

test('names are shown as text: script payloads are neither executed nor injected', async ({ page }) => {
	const guards = await guardPage(page);
	await open(page);
	await page.getByLabel('Site').selectOption('E2E1');
	await column(page, 'airport')
		.getByRole('button', { name: new RegExp(`^${iata}\\b`) })
		.click();

	for (const [index, payload] of xssPayloads.slice(0, 4).entries()) {
		await page.getByTestId('add-terminal').click();
		await page.getByTestId('create-terminal').getByLabel('Code').fill(`X${index}`);
		await page.getByTestId('create-terminal').getByLabel('Name').fill(payload);
		await page.getByTestId('create-terminal').getByRole('button', { name: 'Create' }).click();
		const item = column(page, 'terminal').getByRole('button', { name: new RegExp(`^X${index}\\b`) });
		await expect(item).toContainText(payload);
		await item.click();
		await expect(page.getByTestId('entity-panel').getByRole('heading', { level: 2 })).toContainText(payload);
	}

	const html = await page.content();
	expect(html).not.toContain('<img src="x"');
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload], a[href^="javascript:"]')).toHaveCount(0);
	await guards.expectClean();
});

test('a terminal duty manager browses the demo airport read only, desk codes included', async ({ page }) => {
	const guards = await guardPage(page);
	await open(page, accounts().web);
	await expect(page.getByTestId('topology-read-only')).toBeVisible();
	await expect(page.getByLabel('Site').locator('option')).toHaveText([/^DMO\b/]);
	await browseToDemoDesks(page);
	await expect(page.locator('[data-testid^="add-"]')).toHaveCount(0);

	await column(page, 'desk')
		.getByRole('button', { name: /^AR-01\b/ })
		.click();
	const panel = page.getByTestId('entity-panel');
	await expect(panel.getByRole('button', { name: 'Save' })).toHaveCount(0);
	await expect(panel.getByTestId('delete')).toHaveCount(0);
	await expect(panel.getByLabel('Name', { exact: true })).toHaveAttribute('readonly', '');
	await expect(page.getByTestId('desk-mappings')).toContainText('IN01');
	await expect(page.getByTestId('add-mapping')).toHaveCount(0);
	await guards.expectClean();
});

test('a border supervisor browses without the desk code mappings; the server refuses the changes the screen hides and filters other sites', async ({
	page
}) => {
	await open(page, accounts().webBorder);
	await browseToDemoDesks(page);
	await column(page, 'desk')
		.getByRole('button', { name: /^AR-01\b/ })
		.click();
	await expect(page.getByTestId('entity-panel')).toBeVisible();
	await expect(page.getByTestId('desk-mappings')).toHaveCount(0);

	const token = (await signIn(accounts().webBorder)).accessToken;
	const desks = await call('GET', `${hosts.main}/api/v1/admin/desks?siteCode=DMO&text=AR-01`, { token });
	const desk = ((await desks.json()).data as { id: string; code: string }[]).find((d) => d.code === 'AR-01')!;
	expect((await call('PUT', `${hosts.main}/api/v1/admin/desks/${desk.id}`, { token, data: { name: 'changed' } })).status()).toBe(403);
	expect((await call('DELETE', `${hosts.main}/api/v1/admin/desks/${desk.id}`, { token })).status()).toBe(403);
	expect((await call('GET', `${hosts.main}/api/v1/admin/desk-code-mappings?deskId=${desk.id}`, { token })).status()).toBe(403);
	// Asking for another site's terminals finds nothing: the server filters the search to the caller's sites.
	const other = await call('GET', `${hosts.main}/api/v1/admin/terminals?siteCode=E2E1`, { token });
	expect(other.status()).toBe(200);
	const page2 = (await other.json()) as { totalCount: number; data: { siteCode: string }[] };
	expect(page2.totalCount).toBe(0);
	expect(page2.data.filter((t) => t.siteCode === 'E2E1')).toEqual([]);
});

test('in Arabic the columns mirror and the kinds are translated', async ({ page }) => {
	const guards = await guardPage(page);
	await open(page, accounts().web);
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('الطوبولوجيا');
	await column(page, 'airport')
		.getByRole('button', { name: /^DMO\b/ })
		.click();
	await column(page, 'terminal').getByRole('button', { name: /^T1\b/ }).click();
	await column(page, 'level')
		.getByRole('button', { name: /^ARR\b/ })
		.click();
	await expect(column(page, 'checkpoint').getByRole('button', { name: /^IMM\b/ })).toContainText('القدوم');
	const first = await column(page, 'airport').boundingBox();
	const second = await column(page, 'terminal').boundingBox();
	expect(first && second && first.x > second.x, 'the first column is on the right').toBeTruthy();
	await guards.expectClean();
});
