import { expect, test, type Page } from '@playwright/test';
import { guardPage } from '../support/browser-guards';
import { homeHeading } from '../support/shell';
import { domMarkers, xssPayloads } from '../support/payloads';

// Script payloads in the URL must never run (no dialog), never be injected as markup and never trip the CSP
// (CWE-79). Svelte renders text through interpolation only; {@html} is banned by the security scanner (SEC-110).

/** Elements and attributes a successful injection would create. */
const injectedSelectors = ['img[src="x"]', 'svg[onload]', 'iframe', '[onerror]', '[onload]', 'a[href^="javascript:"]'];

async function expectNothingInjected(page: Page): Promise<void> {
	for (const selector of injectedSelectors) {
		await expect(page.locator(selector), selector).toHaveCount(0);
	}

	const html = await page.content();
	for (const marker of domMarkers) {
		expect(html, `rendered DOM must not contain ${marker}`).not.toContain(marker);
	}
	expect(await page.locator('script:not([src])').count(), 'only the SvelteKit bootstrap script is inline').toBe(1);
}

test.describe('XSS payloads in the URL', () => {
	xssPayloads.forEach((payload, index) => {
		test(`payload ${index + 1} in the query and the hash is neither executed nor injected`, async ({ page }) => {
			const guards = await guardPage(page);
			const value = encodeURIComponent(payload);

			await page.goto(`/?q=${value}&next=${value}&lang=${value}#${value}`);

			await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
			await page.waitForLoadState('networkidle');
			await expectNothingInjected(page);
			await guards.expectClean();
		});
	});

	test('a raw payload in the hash is neither executed nor injected', async ({ page }) => {
		const guards = await guardPage(page);

		await page.goto('/#<img src=x onerror=alert(document.domain)>');
		await page.evaluate(() => {
			window.location.hash = '<svg/onload=alert(1)>';
		});

		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await expectNothingInjected(page);
		await guards.expectClean();
	});

	test('a payload in the path renders the not found page without executing it', async ({ page }) => {
		const guards = await guardPage(page);

		const response = await page.goto(`/${encodeURIComponent('<script>alert(1)</script>')}`);

		expect(response?.status()).toBe(404);
		await expect(page.getByText('404')).toBeVisible();
		await expectNothingInjected(page);
		// Two console errors are expected for a route miss: the browser logs the 404 document response and the
		// SvelteKit client router logs "Not found: <path>" (the path stays URL encoded). Nothing else may be logged.
		const expected404 = /status of 404|^\w+: Not found: \/%3Cscript%3E/;
		const unexpected = guards.consoleErrors.filter((message) => !expected404.test(message));
		guards.consoleErrors.splice(0, guards.consoleErrors.length, ...unexpected);
		await guards.expectClean();
	});
});
