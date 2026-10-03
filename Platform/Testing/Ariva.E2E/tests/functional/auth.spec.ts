import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import {
	accounts,
	call,
	claimsOf,
	developmentSigningKey,
	headerOf,
	integrationSeedsFile,
	logoutUrl,
	refusedWithin,
	signToken,
	totpCode,
	webFirstPassword
} from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { domMarkers, xssPayloads } from '../support/payloads';
import { homeHeading } from '../support/shell';
import { allowStatuses, databaseAvailable, earlyInStep, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';

// ARV-051 (ADR-0026): sign-in on Ariva.Web. The access token lives in memory only; a reload or a new tab takes one from
// the refresh cookie; tabs share sign-out; the first sign-in changes the temporary password and enrols TOTP; a critical
// action asks for a fresh code in a dialog and is sent once more; an ended session returns to sign-in with the page
// remembered. The server stays the authority: the sidebar only hides what a role cannot use.

test.skip(!databaseAvailable, 'signing in needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

/** The access token of the next successful sign-in, as the browser received it. */
function nextAccessToken(page: Page): Promise<string> {
	return page
		.waitForResponse((response) => response.url().endsWith('/api/auth/login') && response.status() === 200)
		.then(async (response) => (await response.json()).accessToken as string);
}

test.describe('signing in', () => {
	test('a signed-out visit goes to sign-in and returns to the page afterwards; the token is never stored', async ({ page }) => {
		const guards = await guardPage(page);
		await ownAddress(page);

		await page.goto('/account');
		await expect(page).toHaveURL(`${webUrl}/login?next=%2Faccount`);
		await expect(page).toHaveTitle('Sign in · Ariva');

		const token = nextAccessToken(page);
		await fillSignIn(page, accounts().web);
		await expect(page).toHaveURL(`${webUrl}/account`);
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account security');

		// Memory only (CWE-384): neither web storage nor a readable cookie holds the access token.
		const accessToken = await token;
		const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) + document.cookie);
		expect(stored).not.toContain(accessToken);
		expect(stored).not.toContain(accessToken.split('.')[2]);
		const cookies = await page.context().cookies();
		const refreshCookie = cookies.find((cookie) => cookie.name === '__Secure-ariva_rt');
		expect(refreshCookie, 'the refresh cookie').toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict', path: '/api/auth' });
		await guards.expectClean();
	});

	test('a wrong password shows one message for every reason, as text', async ({ page }) => {
		const guards = await guardPage(page);
		await ownAddress(page);
		await page.goto('/login');

		await fillSignIn(page, { ...accounts().web, password: 'not-the-password-of-this-account' });

		await expect(page.getByTestId('login-error')).toHaveText('The username, password or code is not correct.');
		await expect(page).toHaveURL(`${webUrl}/login`);
		allowStatuses(guards, 401);
		await guards.expectClean();
	});

	test('a TOTP account is asked for its code, then signed in; Arabic mirrors the form', async ({ page }) => {
		const guards = await guardPage(page);
		await ownAddress(page);
		await page.goto('/login');
		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('تسجيل الدخول');

		const { userName, password, totpSecret } = accounts().webTotp;
		await page.getByLabel('اسم المستخدم').fill(userName);
		await page.getByLabel('كلمة المرور', { exact: true }).fill(password);
		await page.getByRole('button', { name: 'تسجيل الدخول' }).click();
		const code = page.getByLabel('رمز تطبيق المصادقة');
		await expect(code).toBeVisible();
		await expect(code).toBeFocused();
		await code.fill(totpCode(totpSecret!));
		await page.getByRole('button', { name: 'تسجيل الدخول' }).click();

		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.ar);
		allowStatuses(guards, 401);
		await guards.expectClean();
	});

	test('the page to return to stays on this site', async ({ page }) => {
		await ownAddress(page);
		const outside = ['//attacker.example/x', 'https://attacker.example/', '/\\attacker.example', 'javascript:alert(1)', '/login?next=//attacker.example'];
		// Signing in, and later visits with the session (the sign-in page then forwards at once).
		await page.goto(`/login?next=${encodeURIComponent(outside[0])}`);
		await fillSignIn(page, accounts().web);
		for (const next of outside) {
			if (next !== outside[0]) await page.goto(`/login?next=${encodeURIComponent(next)}`);
			await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
			expect(new URL(page.url()).origin, next).toBe(webUrl);
			expect(new URL(page.url()).pathname, next).toBe('/');
		}
		await page.goto(`/login?next=${encodeURIComponent('/account?tab=x')}`);
		await expect(page).toHaveURL(`${webUrl}/account?tab=x`);
	});

	test('script payloads typed into the form are neither executed nor injected', async ({ page }) => {
		const guards = await guardPage(page);
		await ownAddress(page);
		for (const payload of xssPayloads.slice(0, 3)) {
			await page.goto(`/login?next=${encodeURIComponent(payload)}`);
			await page.getByLabel('Username').fill(payload);
			await page.getByLabel('Password', { exact: true }).fill(payload);
			await page.getByRole('button', { name: 'Sign in' }).click();
			await expect(page.getByTestId('login-error')).toBeVisible();
			const html = await page.content();
			for (const marker of domMarkers) expect(html, marker).not.toContain(marker);
			await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);
		}
		allowStatuses(guards, 400, 401);
		await guards.expectClean();
	});
});

test.describe('the session', () => {
	test('a new tab is signed in from the refresh cookie; signing out in one tab signs out the other', async ({ page, context }) => {
		await signInThroughUi(page);
		const second = await context.newPage();
		await ownAddress(second);

		await second.goto('/');
		await expect(second.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await expect(second.getByTestId('user-name')).toHaveText(accounts().web.userName);

		await second.getByTestId('sign-out').click();
		await expect(second).toHaveURL(`${webUrl}/login`);
		// The first tab hears it over the BroadcastChannel and leaves the shell without a reload.
		await expect(page).toHaveURL(`${webUrl}/login`);
		await expect(page.getByTestId('app-sidebar')).toHaveCount(0);

		// The cookie is gone too: a reload stays signed out.
		await page.goto('/');
		await expect(page).toHaveURL(`${webUrl}/login`);
	});

	test('signing out with an expired access token still ends the session on the server', async ({ page, context }) => {
		test.skip(!developmentSigningKey(), 'needs the run key to sign an expired token');
		await signInThroughUi(page);
		// The tab slept past its token's expiry: the first logout carries an expired token and is refused (401).
		let expiredSent = false;
		await page.route('**/api/auth/logout', async (route) => {
			if (expiredSent) return route.continue();
			expiredSent = true;
			const token = (route.request().headers()['authorization'] ?? '').replace(/^Bearer /, '');
			const claims = claimsOf(token);
			const expired = signToken(headerOf(token), { ...claims, exp: Math.floor(Date.now() / 1000) - 120 }, developmentSigningKey()!);
			return route.continue({ headers: { ...route.request().headers(), authorization: `Bearer ${expired}` } });
		});
		const logouts: number[] = [];
		page.on('response', (response) => {
			if (response.url().endsWith('/api/auth/logout')) logouts.push(response.status());
		});

		await page.getByTestId('sign-out').click();

		await expect(page).toHaveURL(`${webUrl}/login`);
		await expect.poll(() => logouts).toEqual([401, 204]);
		// The refresh cookie no longer gives a session.
		const refresh = await context.request.post(`${webUrl}/api/auth/refresh`, { headers: { 'X-Ariva-Csrf': '1', Origin: webUrl } });
		expect(refresh.status()).toBe(401);
	});

	test('a sign-out the server could not complete says so', async ({ page }) => {
		test.skip(!developmentSigningKey(), 'needs the run key to sign an expired token');
		await signInThroughUi(page);
		// Every logout carries an expired token (401) and the refresh in between answers 503 (Main restarting): the
		// session may live on, so the user is told rather than shown a clean sign-out.
		await page.route('**/api/auth/logout', async (route) => {
			const token = (route.request().headers()['authorization'] ?? '').replace(/^Bearer /, '');
			const claims = claimsOf(token);
			const expired = signToken(headerOf(token), { ...claims, exp: Math.floor(Date.now() / 1000) - 120 }, developmentSigningKey()!);
			return route.continue({ headers: { ...route.request().headers(), authorization: `Bearer ${expired}` } });
		});
		await page.route('**/api/auth/refresh', (route) => route.fulfill({ status: 503, contentType: 'application/problem+json', body: '{"status":503}' }));

		await page.getByTestId('sign-out').click();

		await expect(page.getByText('Ariva could not end your session on the server.', { exact: false })).toBeVisible();
		await expect(page).toHaveURL(`${webUrl}/login`);
	});

	test('two tabs opened at once both get a session', async ({ page, context }) => {
		await signInThroughUi(page);
		const tabs = [await context.newPage(), await context.newPage()];
		for (const tab of tabs) await ownAddress(tab);

		await Promise.all(tabs.map((tab) => tab.goto('/')));

		for (const tab of [...tabs, page]) {
			await expect(tab.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		}
		await page.reload();
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
	});

	test('a session ended on the server returns to sign-in with the page remembered', async ({ page }) => {
		const guards = await guardPage(page);
		await ownAddress(page);
		await earlyInStep();
		const { totpSecret } = accounts().webExpiry;
		await page.goto('/login');
		const token = nextAccessToken(page);
		await fillSignIn(page, accounts().webExpiry, totpCode(totpSecret!, -1));
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await page.getByRole('link', { name: 'Account security' }).click();
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account security');

		// Signed out elsewhere: the server revokes the session behind the browser's access token.
		const accessToken = await token;
		expect((await call('POST', logoutUrl, { token: accessToken })).status()).toBe(204);
		expect(await refusedWithin(accessToken, `${hosts.main}/api/auth/me`)).toBeGreaterThanOrEqual(0);

		await page.locator('#account-code').fill(totpCode(totpSecret!));
		await page.getByRole('button', { name: 'Generate new recovery codes' }).click();

		await expect(page).toHaveURL(`${webUrl}/login?next=%2Faccount`);
		await fillSignIn(page, accounts().webExpiry, totpCode(totpSecret!, 1));
		await expect(page).toHaveURL(`${webUrl}/account`);
		allowStatuses(guards, 401);
		await guards.expectClean();
	});
});

test.describe('first sign-in', () => {
	test('changes the temporary password, enrols TOTP from the QR code and shows the recovery codes once', async ({ page, context }) => {
		await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: webUrl });
		const guards = await guardPage(page);
		await ownAddress(page);
		const account = accounts().webFirst;
		await page.goto('/login');
		await fillSignIn(page, account);

		await expect(page).toHaveURL(`${webUrl}/setup`);
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('Set up your account');
		const steps = page.getByRole('list', { name: 'Setup steps' });
		await expect(steps.locator('[aria-current="step"]')).toContainText('New password');

		// A different repeat is refused before anything is sent; then the server's own rule (no product name).
		await page.getByLabel('Temporary password').fill(account.password);
		await page.getByLabel('New password', { exact: true }).fill(webFirstPassword());
		await page.getByLabel('Repeat the new password').fill(`${webFirstPassword()}x`);
		await page.getByRole('button', { name: 'Change the password' }).click();
		await expect(page.getByTestId('setup-error')).toHaveText('The two new passwords differ.');
		await page.getByLabel('New password', { exact: true }).fill('ariva-station-2026');
		await page.getByLabel('Repeat the new password').fill('ariva-station-2026');
		await page.getByRole('button', { name: 'Change the password' }).click();
		await expect(page.getByTestId('setup-error')).toContainText('product name');

		await page.getByLabel('New password', { exact: true }).fill(webFirstPassword());
		await page.getByLabel('Repeat the new password').fill(webFirstPassword());
		await page.getByRole('button', { name: 'Change the password' }).click();

		await expect(steps.locator('[aria-current="step"]')).toContainText('Authenticator');
		const qr = page.getByTestId('totp-qr');
		await expect(qr).toBeVisible();
		await expect(qr).toHaveAttribute('aria-label', 'QR code to add Ariva to your authenticator app');
		expect((await qr.locator('path').getAttribute('d'))?.length ?? 0).toBeGreaterThan(500);
		const secret = (await page.getByTestId('totp-secret').textContent())!.replace(/\s+/g, '');
		expect(secret).toMatch(/^[A-Z2-7]{16,}$/);
		fs.appendFileSync(integrationSeedsFile, secret + '\n');

		await page.getByLabel('Code from the app').fill(totpCode(secret));
		await page.getByRole('button', { name: 'Confirm the code' }).click();

		await expect(steps.locator('[aria-current="step"]')).toContainText('Recovery codes');
		const codes = page.getByTestId('recovery-codes').getByRole('listitem');
		await expect(codes).toHaveCount(10);
		const shown = (await codes.allTextContents()).map((code) => code.trim());
		await page.getByTestId('copy-recovery-codes').click();
		await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(shown.join('\n'));

		await expect(page.getByTestId('setup-finish')).toBeDisabled();
		await page.getByLabel('I have saved the codes').check();
		await page.getByTestId('setup-finish').click();
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);

		// Shown once: back on the setup page there is nothing left to set up, so it goes home.
		await page.goto('/setup');
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await expect(page.getByTestId('recovery-codes')).toHaveCount(0);
		allowStatuses(guards, 400);
		await guards.expectClean();
	});

	test('a pending account cannot open the signed-in screens', async ({ page }) => {
		const account = accounts().pending;
		await ownAddress(page);
		await page.goto('/login');
		await fillSignIn(page, account);
		await expect(page).toHaveURL(`${webUrl}/setup`);

		await page.goto('/account');
		await expect(page).toHaveURL(`${webUrl}/setup`);
		await expect(page.getByTestId('app-sidebar')).toHaveCount(0);
	});
});

test.describe('step-up', () => {
	test('a critical action with an old second factor opens the dialog, then goes through once', async ({ page }) => {
		test.skip(!developmentSigningKey(), 'needs the run key to age the token');
		const guards = await guardPage(page);
		await ownAddress(page);
		await earlyInStep();
		// The browser receives the real token with its second factor 20 minutes old, as after a long shift.
		await page.route('**/api/auth/login', async (route) => {
			const response = await route.fetch();
			if (response.status() !== 200) return route.fulfill({ response });
			const body = await response.json();
			const claims = claimsOf(body.accessToken);
			body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
			return route.fulfill({ response, json: body });
		});
		const account = accounts().webStepUp;
		await page.goto('/login');
		await fillSignIn(page, account, totpCode(account.totpSecret!, -1));
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await page.getByRole('link', { name: 'Account security' }).click();

		// The code in the form is the later step: the step-up code is used first (each step counts once).
		await page.locator('#account-code').fill(totpCode(account.totpSecret!, 1));
		await page.getByRole('button', { name: 'Generate new recovery codes' }).click();

		const dialog = page.getByTestId('step-up-dialog');
		await expect(dialog).toBeVisible();
		await expect(dialog.getByRole('heading', { name: 'Confirm it is you' })).toBeVisible();
		await expect(page.locator('#step-up-code')).toBeFocused();
		await page.locator('#step-up-code').fill('000000');
		await dialog.getByRole('button', { name: 'Confirm' }).click();
		await expect(dialog.getByRole('alert')).toHaveText('The code was not accepted.');

		await page.locator('#step-up-code').fill(totpCode(account.totpSecret!));
		await dialog.getByRole('button', { name: 'Confirm' }).click();

		await expect(dialog).toBeHidden();
		await expect(page.getByTestId('recovery-codes').getByRole('listitem')).toHaveCount(10);
		allowStatuses(guards, 400, 401);
		await guards.expectClean();
	});

	test('cancelling the dialog leaves the action undone', async ({ page }) => {
		test.skip(!developmentSigningKey(), 'needs the run key to age the token');
		await ownAddress(page);
		await page.route('**/api/auth/login', async (route) => {
			const response = await route.fetch();
			if (response.status() !== 200) return route.fulfill({ response });
			const body = await response.json();
			const claims = claimsOf(body.accessToken);
			body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
			return route.fulfill({ response, json: body });
		});
		const account = accounts().webCancel;
		await earlyInStep();
		await page.goto('/login');
		await fillSignIn(page, account, totpCode(account.totpSecret!, -1));
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await page.getByRole('link', { name: 'Account security' }).click();
		await page.locator('#account-code').fill('123456');
		await page.getByRole('button', { name: 'Generate new recovery codes' }).click();

		await expect(page.getByTestId('step-up-dialog')).toBeVisible();
		await page.keyboard.press('Escape');

		await expect(page.getByTestId('step-up-dialog')).toBeHidden();
		await expect(page.getByTestId('recovery-codes')).toHaveCount(0);
		await expect(page.getByRole('alert')).toContainText('That did not work');
	});
});

test.describe('role-aware sidebar', () => {
	test('a handler station manager sees only the screens of the role; the server refuses the rest', async ({ page }) => {
		await ownAddress(page);
		await page.goto('/login');
		const token = nextAccessToken(page);
		await fillSignIn(page, accounts().webHandler);
		const nav = page.getByTestId('app-sidebar').getByRole('navigation');
		await expect(nav).toBeVisible();

		for (const visible of ['Live operations', 'Alert rules', 'Topology', 'Zones']) await expect(nav.getByText(visible, { exact: true })).toBeVisible();
		for (const hidden of ['Immigration', 'Devices', 'Users and access']) await expect(nav.getByText(hidden, { exact: true })).toHaveCount(0);
		await expect(page.getByTestId('user-card')).toContainText('Handler station manager');

		// Hiding is convenience only: the same token is refused by the server.
		expect((await call('GET', `${hosts.main}/api/v1/admin/users`, { token: await token })).status()).toBe(403);
	});

	test('an administrator sees every screen, users included', async ({ page }) => {
		await signInThroughUi(page, accounts().webAdmin);
		const nav = page.getByTestId('app-sidebar').getByRole('navigation');
		for (const visible of ['Live operations', 'Immigration', 'Devices', 'Users and access'])
			await expect(nav.getByText(visible, { exact: true })).toBeVisible();
		await expect(page.getByTestId('user-card')).toContainText('All sites');
	});
});
