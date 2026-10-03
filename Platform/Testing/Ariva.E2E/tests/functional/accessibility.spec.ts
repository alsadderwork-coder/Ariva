import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// WCAG 2.2 AA checks with axe-core on every built screen, in light and dark mode and in Arabic.
// A new screen adds itself to `screens`; a violation fails the build like any other test.

// Signed-in screens sign in first (ARV-051); the sign-in page itself needs no account.
const screens = [
	{ name: 'sign in', path: '/login', signedIn: false },
	{ name: 'live operations', path: '/', signedIn: true },
	{ name: 'account security', path: '/account', signedIn: true },
	{ name: 'topology', path: '/topology', signedIn: true }
];
/** Opens the screen and waits for its heading (a signed-in screen first takes a token from the refresh cookie). */
async function open(page: Page, path: string): Promise<void> {
	await page.goto(path);
	await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
}

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
		test.skip(screen.signedIn && !databaseAvailable, 'signing in needs the E2E database');
		test.beforeEach(async ({ page }) => {
			if (screen.signedIn) await signInThroughUi(page);
		});

		test('light mode, English', async ({ page }) => {
			await page.emulateMedia({ colorScheme: 'light' });
			await open(page, screen.path);
			await page.waitForLoadState('networkidle');
			await expectNoViolations(page);
		});

		test('dark mode, English', async ({ page }) => {
			await page.emulateMedia({ colorScheme: 'dark' });
			await open(page, screen.path);
			await page.waitForLoadState('networkidle');
			await expect(page.locator('html')).toHaveClass(/\bdark\b/);
			await expectNoViolations(page);
		});

		test('light mode, Arabic', async ({ page }) => {
			await page.emulateMedia({ colorScheme: 'light' });
			await open(page, screen.path);
			await page.getByTestId('language-toggle').click();
			await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
			await expectNoViolations(page);
		});

		test('collapsed sidebar', async ({ page }) => {
			test.skip(!screen.signedIn, 'the sign-in page has no sidebar');
			await open(page, screen.path);
			await page.getByTestId('sidebar-toggle').click();
			await expect(page.getByTestId('app-sidebar')).toHaveAttribute('data-state', 'collapsed');
			await expectNoViolations(page);
		});
	});
}
