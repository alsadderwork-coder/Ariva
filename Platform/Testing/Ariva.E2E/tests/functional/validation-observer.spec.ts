import { expect, test } from '@playwright/test';
import { accounts } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-104a: the validation observer role has no operational screen. Its only permission is Validation.Capture, so after
// sign-in the sidebar offers only the observer tablet (Validation capture, ARV-104c; validation-capture.spec.ts), the
// home screen says the role has no live view, links to the tablet and asks the API for nothing, and the screens it might
// type its way to show no access; its role is named in the user card in English and Arabic. The server refuses every
// call regardless (validation.spec.ts and the permission matrix).

test.skip(!databaseAvailable, 'sign-in needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

const modules = ['Live operations', 'Immigration', 'Alert rules', 'Passenger displays', 'Reports', 'Topology', 'Zones', 'Devices', 'Users and access'];

test('a validation observer signs in to no operational screen, only the capture tablet', async ({ page }) => {
	const guards = await guardPage(page);
	const apiCalls: string[] = [];
	page.on('request', (request) => {
		if (/\/api\/v1\//.test(request.url())) apiCalls.push(request.url());
	});

	await signInThroughUi(page, accounts().ValidationObserver);

	const sidebar = page.getByTestId('app-sidebar');
	for (const name of modules) await expect(sidebar.getByRole('link', { name, exact: true }), name).toHaveCount(0);
	await expect(sidebar.getByRole('navigation').getByRole('link')).toHaveText(['Validation capture']);
	await expect(page.getByTestId('no-live-access')).toBeVisible();
	await expect(page.getByTestId('go-capture')).toHaveAttribute('href', '/validation/capture');
	await expect(page.getByTestId('user-card')).toContainText('Validation observer');
	await expect(page.getByTestId('user-card')).toContainText('E2EV');
	expect(apiCalls, 'the home screen asks the API for nothing the role cannot have').toEqual([]);

	for (const path of ['/users', '/reports']) {
		await page.goto(path);
		await expect(page.getByTestId('no-access'), path).toBeVisible();
	}

	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByTestId('user-card')).toContainText('مراقب التحقق');
	await guards.expectClean();
});
