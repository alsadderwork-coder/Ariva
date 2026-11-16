import { expect, test } from '@playwright/test';
import { guardPage } from '../support/browser-guards';
import { homeHeading } from '../support/shell';
import { webUrl } from '../support/hosts';
import { accounts } from '../support/accounts';
import { xssPayloads } from '../support/payloads';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// The web shell served by `vite preview` with the production headers (the same values nginx sends).

/** Directives the production policy must contain (CWE-79). */
const requiredDirectives = [
	"default-src 'self'",
	"script-src 'self' 'sha256-",
	"style-src 'self'",
	// blob: shows a floor plan fetched with the user's token (ARV-053); nothing else is added.
	"img-src 'self' data: blob:;",
	// blob: plays an advertisement video fetched with credentials (ARV-142b); no other media source.
	"media-src 'self' blob:;",
	"connect-src 'self'",
	"object-src 'none'",
	"base-uri 'none'",
	"form-action 'self'",
	"frame-ancestors 'none'"
];

const requiredHeaders: Readonly<Record<string, string>> = {
	'x-content-type-options': 'nosniff',
	'x-frame-options': 'DENY',
	'referrer-policy': 'no-referrer',
	'permissions-policy': 'camera=(), microphone=(), geolocation=()',
	'cross-origin-opener-policy': 'same-origin'
};

test.describe('web shell', () => {
	test('loads with the Ariva title, no console errors and no CSP violations', async ({ page }) => {
		test.skip(!databaseAvailable, 'signing in needs the E2E database');
		const guards = await guardPage(page);
		await signInThroughUi(page);

		const response = await page.goto('/');

		expect(response?.status()).toBe(200);
		await expect(page).toHaveTitle('Ariva');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await expect(page.locator('html')).toHaveAttribute('lang', 'en');
		await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
		await page.waitForLoadState('networkidle');
		await guards.expectClean();
	});

	test('the page response carries the production CSP and security headers', async ({ page }) => {
		const response = await page.goto('/');
		const headers = response?.headers() ?? {};
		const csp = headers['content-security-policy'] ?? '';

		for (const directive of requiredDirectives) {
			expect(csp, 'Content-Security-Policy').toContain(directive);
		}
		expect(csp).not.toContain("'unsafe-inline'");
		expect(csp).not.toContain("'unsafe-eval'");
		for (const [name, value] of Object.entries(requiredHeaders)) {
			expect(headers[name], name).toBe(value);
		}
		expect(headers['access-control-allow-origin']).toBeUndefined();
	});

	test('static assets carry the security headers too', async ({ request }) => {
		const response = await request.get(`${webUrl}/favicon.svg`);

		expect(response.status()).toBe(200);
		expect(response.headers()['content-security-policy']).toContain("default-src 'self'");
		for (const [name, value] of Object.entries(requiredHeaders)) {
			expect(response.headers()[name], name).toBe(value);
		}
	});

	test('switching the language to Arabic sets lang="ar" and dir="rtl"', async ({ page }) => {
		test.skip(!databaseAvailable, 'signing in needs the E2E database');
		const guards = await guardPage(page);
		await signInThroughUi(page);
		const toggle = page.getByTestId('language-toggle');

		await toggle.click();

		await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(page).toHaveTitle('أريفا');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.ar);
		await expect(toggle).toContainText('English');

		await toggle.click();

		await expect(page.locator('html')).toHaveAttribute('lang', 'en');
		await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
		await guards.expectClean();
	});

	test('the command palette opens with Ctrl+K and goes to a screen by keyboard; a search is text, never markup (ARV-299)', async ({ page }) => {
		test.skip(!databaseAvailable, 'signing in needs the E2E database');
		const guards = await guardPage(page);
		await signInThroughUi(page);
		const palette = page.getByTestId('command-palette');
		await expect(palette).toBeHidden();
		await page.keyboard.press('Control+k');
		await expect(palette).toBeVisible();
		const search = palette.getByRole('combobox', { name: 'Search screens and commands' });
		await expect(search).toBeFocused();

		await search.fill('zon');
		await expect(palette.getByRole('option')).toHaveText(['Zones']);
		await expect(palette.getByRole('option', { name: 'Zones' })).toHaveAttribute('aria-selected', 'true');
		await page.keyboard.press('Enter');
		await expect(palette).toBeHidden();
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('Zones');

		// The header's search button opens it too; Escape closes it. Arrow keys move the choice.
		await page.getByTestId('palette-open').click();
		await expect(search).toBeFocused();
		await expect(search).toHaveValue('');
		await search.fill('dev');
		await page.keyboard.press('ArrowDown');
		await page.keyboard.press('ArrowUp');
		await expect(palette.getByRole('option', { name: 'Devices' })).toHaveAttribute('aria-selected', 'true');
		await page.keyboard.press('Escape');
		await expect(palette).toBeHidden();

		// A search is a field's value: nothing typed is run or rendered as markup.
		await page.keyboard.press('Control+k');
		for (const payload of xssPayloads.slice(0, 3)) {
			await search.fill(payload);
			await expect(palette.getByText('Nothing matches')).toBeVisible();
		}
		await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);
		expect(guards.dialogs, 'no script ran').toEqual([]);
		await page.keyboard.press('Escape');

		// Never over another modal dialog (a step-up waiting for a code): Ctrl+K does nothing until it is closed.
		await page.evaluate(() => {
			const other = document.createElement('dialog');
			other.id = 'other-modal';
			other.textContent = 'Another dialog';
			document.body.append(other);
			other.showModal();
		});
		await page.keyboard.press('Control+k');
		await expect(palette).toBeHidden();
		await page.evaluate(() => (document.getElementById('other-modal') as HTMLDialogElement).close());
		await page.keyboard.press('Control+k');
		await expect(palette).toBeVisible();
		await page.keyboard.press('Escape');
		await guards.expectClean();
	});

	test('the command palette lists only the screens the sidebar shows, and searches in Arabic (ARV-299)', async ({ page }) => {
		test.skip(!databaseAvailable, 'signing in needs the E2E database');
		await signInThroughUi(page, accounts().webHandler);
		const screens = await page.getByTestId('app-sidebar').getByRole('navigation').getByRole('link').allInnerTexts();
		expect(screens).not.toContain('Users and access');
		await page.keyboard.press('Control+k');
		const palette = page.getByTestId('command-palette');
		const options = (await palette.getByRole('option').allInnerTexts()).map((text) => text.trim());
		expect(options.slice(0, screens.length), 'the same screens, in the same order').toEqual(screens.map((text) => text.trim()));
		expect(options, 'and three commands').toHaveLength(screens.length + 3);
		await page.keyboard.press('Escape');

		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await page.keyboard.press('Control+k');
		await expect(palette.getByRole('combobox')).toHaveAttribute('placeholder', 'اكتب اسم شاشة أو أمر');
		await palette.getByRole('combobox').fill('المناطق');
		await expect(palette.getByRole('option')).toHaveText(['المناطق']);
		await page.keyboard.press('Enter');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('المناطق');
	});

	test('the shell refuses to be framed by another page', async ({ page }) => {
		// setContent waits for the load event, which waits for the iframe to load or to be blocked.
		await page.setContent(`<iframe id="victim" src="${webUrl}/" width="400" height="300"></iframe>`, {
			waitUntil: 'load'
		});

		const framed = page.frames().filter((frame) => frame !== page.mainFrame());

		expect(framed).toHaveLength(1);
		expect(framed[0].url(), 'a blocked frame never commits the shell URL').not.toContain(webUrl);
		await expect(page.frameLocator('#victim').getByRole('heading', { level: 1 })).toHaveCount(0);
	});
});
