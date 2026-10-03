import { expect, test } from '@playwright/test';
import { guardPage } from '../support/browser-guards';
import { homeHeading } from '../support/shell';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// The application shell follows Aman.Web's design system: sidebar with grouped navigation, sticky header with a
// breadcrumb, light and dark modes from the same tokens, and a right to left mirror in Arabic. The shell is behind the
// sign-in (ARV-051): every test signs in as a terminal duty manager first, then reloads, which takes a new access token
// from the refresh cookie.

test.skip(!databaseAvailable, 'signing in needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.beforeEach(async ({ page }) => {
	await signInThroughUi(page);
});

test.describe('application shell', () => {
	test('shows the grouped sidebar, the breadcrumb and the active item', async ({ page }) => {
		const guards = await guardPage(page);
		await page.goto('/');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);

		const sidebar = page.getByTestId('app-sidebar');
		await expect(sidebar).toBeVisible();
		await expect(sidebar).toHaveAttribute('data-state', 'expanded');
		for (const group of ['Operations', 'Oversight', 'Administration']) {
			await expect(sidebar.getByText(group, { exact: true })).toBeVisible();
		}
		await expect(sidebar.getByRole('link', { name: homeHeading.en })).toHaveAttribute('aria-current', 'page');
		await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText(homeHeading.en);
		await guards.expectClean();
	});

	test('screens that are not built yet are shown as planned, not as links', async ({ page }) => {
		await page.goto('/');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		const sidebar = page.getByTestId('app-sidebar');

		await expect(sidebar.getByRole('navigation').getByRole('link')).toHaveCount(6);
		const planned = sidebar.locator('[aria-disabled="true"]');
		await expect(planned.first()).toHaveAttribute('title', /Planned in ARV-\d{3}/);
		expect(await planned.count()).toBeGreaterThan(0);
	});

	test('collapsing the sidebar to the icon rail survives a reload', async ({ page }) => {
		const guards = await guardPage(page);
		await page.goto('/');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		const sidebar = page.getByTestId('app-sidebar');

		await page.getByTestId('sidebar-toggle').click();
		await expect(sidebar).toHaveAttribute('data-state', 'collapsed');
		await expect.poll(async () => (await sidebar.boundingBox())?.width).toBeLessThanOrEqual(64);

		await page.reload();
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await expect(page.getByTestId('app-sidebar')).toHaveAttribute('data-state', 'collapsed');

		await page.getByTestId('sidebar-toggle').click();
		await expect(page.getByTestId('app-sidebar')).toHaveAttribute('data-state', 'expanded');
		await guards.expectClean();
	});

	test('dark mode switches the tokens and survives a reload without a CSP violation', async ({ page }) => {
		const guards = await guardPage(page);
		await page.emulateMedia({ colorScheme: 'light' });
		await page.goto('/');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		const html = page.locator('html');
		const background = () => page.evaluate(() => getComputedStyle(document.body).backgroundColor);

		await expect(html).toHaveClass(/\bariva\b/);
		await expect(html).toHaveClass(/\blight\b/);
		const lightBackground = await background();

		await page.getByTestId('theme-toggle').click();
		await expect(html).toHaveClass(/\bdark\b/);
		expect(await background()).not.toBe(lightBackground);

		await page.reload();
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await expect(html).toHaveClass(/\bdark\b/);
		await guards.expectClean();
	});

	test('in Arabic the sidebar moves to the right and the page mirrors', async ({ page }) => {
		const guards = await guardPage(page);
		await page.goto('/');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);

		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.ar);

		const sidebarBox = await page.getByTestId('app-sidebar').boundingBox();
		const mainBox = await page.locator('main').boundingBox();
		expect(sidebarBox && mainBox && sidebarBox.x > mainBox.x, 'sidebar is on the right').toBeTruthy();
		await guards.expectClean();
	});

	test('the skip link moves focus to the main content', async ({ page }) => {
		await page.goto('/');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);

		await page.keyboard.press('Tab');
		const skip = page.getByRole('link', { name: 'Skip to content' });
		await expect(skip).toBeFocused();
		await page.keyboard.press('Enter');

		await expect(page).toHaveURL(/#main$/);
		await expect(page.locator('main')).toBeFocused();
	});
});
