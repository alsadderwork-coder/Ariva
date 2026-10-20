import { expect, type Locator, type Page } from '@playwright/test';
import { accounts, clientAddress, databaseAvailable, totpCode, type Account } from './accounts';
import type { PageGuards } from './browser-guards';
import { homeHeading } from './shell';

// ARV-051: signing in through Ariva.Web's own form. Each page gets its own client address (X-Forwarded-For passes the
// preview server's proxy and Main trusts loopback in the E2E run), so the per-address sign-in limit of 10 a minute
// applies per test rather than to the whole suite.

export { databaseAvailable };

/** The account the shell suites sign in with: a terminal duty manager at the demo airport. */
export const shellAccount = (): Account => accounts().web;

/** Gives the page a client address of its own; call before the first request. */
export async function ownAddress(page: Page): Promise<string> {
	const address = clientAddress();
	await page.setExtraHTTPHeaders({ 'X-Forwarded-For': address });
	return address;
}

/**
 * ARV-152 (CWE-532): types a password, a TOTP code, a recovery code or a key into a field without putting it in the report.
 * A `fill` step's title is `Fill "<value>"` and the HTML report keeps every step title, passed or not; here the value is set
 * in the page instead (the input's own value setter, then the `input` and `change` events Svelte 5's bindings listen to),
 * once the field is visible and editable as `fill` would wait for, so the step's title is "Evaluate". A trace still records
 * the value as the call's argument: traces are kept only for a failed test and never in CI (playwright.config.ts).
 */
export async function fillSecret(field: Locator, value: string): Promise<void> {
	await expect(field).toBeVisible();
	await expect(field).toBeEditable();
	await field.evaluate((element, text) => {
		const input = element as HTMLInputElement;
		input.focus();
		const setValue = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(input), 'value')?.set;
		if (setValue) setValue.call(input, text);
		else input.value = text;
		input.dispatchEvent(new Event('input', { bubbles: true }));
		input.dispatchEvent(new Event('change', { bubbles: true }));
	}, value);
}

/** Fills the sign-in form; with a TOTP account the code field appears after the first answer (401 mfa_required). */
export async function fillSignIn(page: Page, account: Account, code?: string): Promise<void> {
	await page.getByLabel('Username').fill(account.userName);
	await fillSecret(page.getByLabel('Password', { exact: true }), account.password);
	await page.getByRole('button', { name: 'Sign in' }).click();
	if (account.totpSecret) {
		const field = page.getByLabel('Authenticator code');
		await expect(field).toBeVisible();
		await fillSecret(field, code ?? totpCode(account.totpSecret));
		await page.getByRole('button', { name: 'Sign in' }).click();
	}
}

/** Signs in through /login and waits for the home screen (the live operations heading). */
export async function signInThroughUi(page: Page, account: Account = shellAccount(), code?: string): Promise<void> {
	await ownAddress(page);
	await page.goto('/login');
	await fillSignIn(page, account, code);
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
}

/**
 * Drops the console errors a flow causes on purpose: Chromium logs every 4xx answer of fetch ("Failed to load
 * resource"), and a sign-in that asks for the second factor, a step-up and an ended session answer 401.
 */
export function allowStatuses(guards: PageGuards, ...statuses: number[]): void {
	const expected = new RegExp(`status of (${statuses.join('|')})`);
	const unexpected = guards.consoleErrors.filter((message) => !expected.test(message));
	guards.consoleErrors.splice(0, guards.consoleErrors.length, ...unexpected);
}

/** Waits until the current TOTP step has at least 12 seconds left, so a test's codes stay in order (replay guard). */
export async function earlyInStep(): Promise<void> {
	const into = Date.now() % 30_000;
	if (into > 18_000) await new Promise((resolve) => setTimeout(resolve, 30_000 - into + 200));
}
