import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

// WCAG 2.2 AA checks with axe-core on every built screen, in light and dark mode and in Arabic.
// A new screen adds itself to `screens`; a violation fails the build like any other test.

const screens = [{ name: 'live operations', path: '/' }];
const tags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function expectNoViolations(page: Page): Promise<void> {
	const results = await new AxeBuilder({ page }).withTags(tags).analyze();
	const summary = results.violations.map(
		(violation) => `${violation.id} (${violation.impact}): ${violation.nodes.map((node) => node.target.join(' ')).join(', ')}`
	);
	expect(summary, 'axe violations').toEqual([]);
}

for (const screen of screens) {
	test.describe(`accessibility: ${screen.name}`, () => {
		test('light mode, English', async ({ page }) => {
			await page.emulateMedia({ colorScheme: 'light' });
			await page.goto(screen.path);
			await page.waitForLoadState('networkidle');
			await expectNoViolations(page);
		});

		test('dark mode, English', async ({ page }) => {
			await page.emulateMedia({ colorScheme: 'dark' });
			await page.goto(screen.path);
			await page.waitForLoadState('networkidle');
			await expect(page.locator('html')).toHaveClass(/\bdark\b/);
			await expectNoViolations(page);
		});

		test('light mode, Arabic', async ({ page }) => {
			await page.emulateMedia({ colorScheme: 'light' });
			await page.goto(screen.path);
			await page.getByTestId('language-toggle').click();
			await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
			await expectNoViolations(page);
		});

		test('collapsed sidebar', async ({ page }) => {
			await page.goto(screen.path);
			await page.getByTestId('sidebar-toggle').click();
			await expect(page.getByTestId('app-sidebar')).toHaveAttribute('data-state', 'collapsed');
			await expectNoViolations(page);
		});
	});
}
