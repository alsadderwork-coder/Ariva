import { expect, test } from '@playwright/test';
import { guardPage } from '../support/browser-guards';
import { homeHeading } from '../support/shell';
import { webUrl } from '../support/hosts';

// The web shell served by `vite preview` with the production headers (the same values nginx sends).

/** Directives the production policy must contain (CWE-79). */
const requiredDirectives = [
	"default-src 'self'",
	"script-src 'self' 'sha256-",
	"style-src 'self'",
	"img-src 'self' data:",
	"connect-src 'self'",
	"object-src 'none'",
	"base-uri 'self'",
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
		const guards = await guardPage(page);

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
		const guards = await guardPage(page);
		await page.goto('/');
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
