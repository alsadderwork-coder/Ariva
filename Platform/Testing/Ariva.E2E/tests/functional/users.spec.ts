import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, login, signIn, signToken, totpCode, type Account } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, earlyInStep, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';

// ARV-059: Users and access. An administrator creates an account through the screen after a step-up (the temporary
// password is shown once), gives it the demo airport, grants and revokes roles, resets its password and authenticator,
// disables and enables it, and reads every change in the audit log, payloads shown as text. Escalation attempts are
// blocked in the screen (own account read only, roles above one's rank and sites beyond one's own not offered, no
// screen without the permission) and refused by the API whatever the screen offers.

test.skip(!databaseAvailable, 'users and access need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const usersUrl = `${hosts.main}/api/v1/admin/users`;
const auditUrl = `${hosts.main}/api/v1/admin/audit-entries`;
const run = crypto.randomBytes(3).toString('hex');
const created = `e2e.ui.${run}`;
const name = `Officer ${xssPayloads[1]}`;
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
const temporaryPattern = /^[A-Za-z2-9]{5}(-[A-Za-z2-9]{5}){3}$/;

/** Signs in with the second factor 20 minutes old, so the first critical action asks for a fresh code. */
async function signInAged(page: Page, account: Account): Promise<void> {
	await ownAddress(page);
	await page.route('**/api/auth/login', async (route) => {
		const response = await route.fetch();
		if (response.status() !== 200) return route.fulfill({ response });
		const body = await response.json();
		const claims = claimsOf(body.accessToken);
		body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
		return route.fulfill({ response, json: body });
	});
	await page.goto('/login');
	await fillSignIn(page, account, totpCode(account.totpSecret!, -1));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
}

async function idOf(token: string, userName: string): Promise<string> {
	const found = await call('GET', `${usersUrl}?text=${encodeURIComponent(userName)}`, { token });
	expect(found.status()).toBe(200);
	const match = ((await found.json()).data as { id: string; userName: string }[]).find((u) => u.userName === userName);
	expect(match, `${userName} is listed`).toBeTruthy();
	return match!.id;
}

function row(page: Page, userName: string) {
	return page.locator(`[data-testid="user-row"][data-user="${userName}"]`);
}

async function openUser(page: Page, userName: string): Promise<void> {
	await page.getByTestId('user-search').fill(userName);
	await page.getByTestId('search-users').click();
	await row(page, userName).getByTestId('open-user').click();
	await expect(page.locator(`[data-testid="user-panel"][data-user="${userName}"]`)).toBeVisible();
}

test('an administrator creates an account after a step-up, gives it a site and roles, resets and disables it, and reads the audit', async ({ page }) => {
	test.setTimeout(120_000);
	const guards = await guardPage(page);
	await earlyInStep();
	await signInAged(page, accounts().webUsers);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Users and access' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Users and access');

	await page.getByTestId('add-user').click();
	const form = page.getByTestId('user-form');
	await form.getByLabel('Username', { exact: true }).fill(`bad name ${run}`);
	await form.getByTestId('save-user').click();
	await expect(form.getByTestId('user-problems'), 'the username rule is checked before any request').toContainText('3 to 64 letters');
	await form.getByLabel('Username', { exact: true }).fill(created);
	await form.getByLabel('Name', { exact: true }).fill(name);
	await form.getByLabel('Email (optional)').fill(`${run}@example.org`);
	await expect(form.getByRole('checkbox', { name: 'System administrator' }), 'an administrator may grant every role').toBeEnabled();
	await form.getByRole('checkbox', { name: 'Border shift supervisor' }).check();
	// The sites come with the account, in the same request: never a window without sites (ARV-059 review).
	await expect(form.getByTestId('new-user-sites')).toContainText('An account without sites reaches no site');
	await form.getByTestId('new-user-sites').locator('[data-site="DMO"]').check();
	await form.getByTestId('save-user').click();

	// Creating an account is a critical action: the second factor is 20 minutes old, so a fresh code is asked.
	const dialog = page.getByTestId('step-up-dialog');
	await expect(dialog).toBeVisible();
	await page.locator('#step-up-code').fill(totpCode(accounts().webUsers.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();
	const reveal = page.getByTestId('credential-reveal');
	await expect(reveal).toContainText('Temporary password');
	const first = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();
	expect(first).toMatch(temporaryPattern);
	await page.getByTestId('credential-done').click();
	await expect(reveal).toHaveCount(0);
	expect(await page.content(), 'gone once done').not.toContain(first);

	const panel = page.locator(`[data-testid="user-panel"][data-user="${created}"]`);
	await expect(panel).toBeVisible();
	await expect(panel.getByTestId('user-status')).toContainText('Temporary password');
	await expect(panel.getByTestId('user-status')).toContainText('No authenticator');
	await expect(row(page, created)).toContainText(name);
	await expect(page.locator(injected)).toHaveCount(0);

	// Created with the demo airport; a second site from the panel.
	await expect(row(page, created)).toContainText('DMO');
	await expect(panel.locator('[data-site="DMO"]')).toBeChecked();
	await panel.locator('[data-site="E2E1"]').check();
	await panel.getByTestId('save-sites').click();
	await expect(row(page, created)).toContainText('DMO, E2E1');

	// Roles change as their boxes change; a role taken away is gone from the row.
	await panel.locator('[data-role="TerminalDutyManager"]').check();
	await expect(row(page, created)).toContainText('Border shift supervisor, Terminal duty manager');
	await panel.locator('[data-role="BorderShiftSupervisor"]').uncheck();
	await expect(row(page, created)).not.toContainText('Border shift supervisor');

	// A new temporary password, shown once; the first one stops working.
	await panel.getByTestId('reset-password').click();
	await panel.getByTestId('confirm-reset-password').click();
	await expect(reveal).toBeVisible();
	const second = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();
	expect(second).toMatch(temporaryPattern);
	expect(second).not.toBe(first);
	expect((await login(created, first)).status(), 'the first temporary password').toBe(401);
	expect((await (await login(created, second)).json()).scope, 'the new one, for the first sign-in only').toBe('pending');
	await page.getByTestId('credential-done').click();

	await panel.getByTestId('reset-totp').click();
	await panel.getByTestId('confirm-reset-totp').click();
	await panel.getByTestId('disable-user').click();
	await panel.getByTestId('confirm-disable-user').click();
	await expect(row(page, created).getByTestId('user-status')).toContainText('Disabled');
	expect((await login(created, second)).status(), 'a disabled account cannot sign in').toBe(401);
	await panel.getByTestId('enable-user').click();
	await expect(row(page, created).getByTestId('user-status')).toContainText('Active');

	// The audit of the account, newest first, everything as text.
	await panel.getByTestId('audit-about').click();
	await expect(page.getByTestId('audit-filter')).toContainText(`About ${created}`);
	const actions = page.getByTestId('audit-row');
	await expect(actions.first()).toHaveAttribute('data-action', 'User.Enabled');
	await expect
		.poll(async () => actions.evaluateAll((rows) => rows.map((r) => r.getAttribute('data-action'))))
		.toEqual([
			'User.Enabled',
			'User.Disabled',
			'User.TotpReset',
			'User.PasswordReset',
			'User.RoleRevoked',
			'User.RoleGranted',
			'User.SitesChanged',
			'User.Created'
		]);
	await expect(actions.first()).toContainText(accounts().webUsers.userName);
	const createdEntry = actions.last();
	await createdEntry.getByText('Show').click();
	await expect(createdEntry.getByTestId('audit-after')).toContainText(`displayName=${JSON.stringify(name)}`);
	await expect(page.locator(injected)).toHaveCount(0);
	const audit = await page.getByTestId('audit-log').textContent();
	expect(audit).not.toContain(first);
	expect(audit).not.toContain(second);

	// Action filter, and back to every account.
	await page.getByLabel('Action').fill('User.RoleGranted');
	await page.getByTestId('audit-search').click();
	await expect(actions).toHaveCount(1);
	await page.getByRole('button', { name: 'Show every account' }).click();
	await expect(page.getByTestId('audit-filter')).toHaveCount(0);

	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('an administrator cannot change its own account on the screen, and the API refuses it however it is asked', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	await page.goto('/users');
	await openUser(page, accounts().webAdmin.userName);
	const panel = page.locator(`[data-testid="user-panel"][data-user="${accounts().webAdmin.userName}"]`);
	await expect(panel.getByTestId('own-account')).toContainText('This is your own account');
	await expect(panel.getByLabel('Name', { exact: true })).toHaveAttribute('readonly', '');
	await expect(panel.getByTestId('save-profile')).toHaveCount(0);
	for (const role of ['BorderShiftSupervisor', 'TerminalDutyManager', 'HandlerStationManager', 'SystemAdministrator'])
		await expect(panel.locator(`[data-role="${role}"]`), role).toBeDisabled();
	await expect(panel.getByRole('radio', { name: 'Every site' })).toBeDisabled();
	await expect(panel.getByTestId('save-sites')).toHaveCount(0);
	for (const action of ['reset-password', 'reset-totp', 'disable-user']) await expect(panel.getByTestId(action), action).toBeDisabled();
	await guards.expectClean();

	// The same changes sent straight to the API, with the screen out of the way (the critical ones, which need a second
	// factor first, are sent by the site administrator below).
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const self = claimsOf(token).sub as string;
	expect((await call('POST', `${usersUrl}/${self}/disable`, { token })).status(), 'own account disabled').toBe(400);
	// Fields the profile does not have are ignored, not applied (mass assignment).
	const before = await (await call('GET', `${usersUrl}/${self}`, { token })).json();
	const updated = await call('PUT', `${usersUrl}/${self}`, {
		token,
		data: {
			displayName: before.displayName,
			email: before.email,
			roles: ['SystemAdministrator', 'BorderShiftSupervisor'],
			allSites: false,
			isDisabled: true,
			sites: ['E2E1']
		}
	});
	expect(updated.status()).toBe(200);
	const after = await updated.json();
	expect([after.roles, after.allSites, after.isDisabled, after.sites]).toEqual([before.roles, before.allSites, before.isDisabled, before.sites]);
	// A name that would read as more audit fields is one quoted value; control characters are refused.
	expect((await call('PUT', `${usersUrl}/${self}`, { token, data: { displayName: 'Admin\nroles=x' } })).status(), 'a line break in a name').toBe(400);
});

test('a site administrator sees and grants only its own site; the API refuses wider access', async ({ page }) => {
	const account = accounts().webSiteAdmin;
	await earlyInStep();
	// The API session first (with the previous step's code), then the screen with this step's: a code counts once.
	const signedIn = await login(account.userName, account.password, undefined, undefined, { code: totpCode(account.totpSecret!, -1) });
	expect(signedIn.status()).toBe(200);
	const token = (await signedIn.json()).accessToken as string;

	// An account comes with its sites: one of its own, never none, another site's or every site.
	const create = (userName: string, extra: Record<string, unknown>) =>
		call('POST', usersUrl, { token, data: { userName, displayName: 'Site officer', ...extra } });
	expect((await create(`e2e.site.none.${run}`, { roles: ['HandlerStationManager'] })).status(), 'no sites').toBe(400);
	expect((await create(`e2e.site.far.${run}`, { siteCodes: ['E2E1'] })).status(), 'another site').toBe(403);
	expect((await create(`e2e.site.every.${run}`, { allSites: true })).status(), 'every site').toBe(403);
	const createdBySite = await create(`e2e.site.${run}`, { roles: ['HandlerStationManager'], siteCodes: ['DMO'] });
	expect(createdBySite.status()).toBe(201);
	const siteUserId = (await createdBySite.json()).user.id as string;
	const adminBySite = await create(`e2e.site.admin.${run}`, { roles: ['SystemAdministrator'], siteCodes: ['DMO'] });
	expect(adminBySite.status(), 'an administrator for its own site').toBe(201);
	expect((await call('GET', `${usersUrl}/${(await adminBySite.json()).user.id}`, { token })).status(), 'and it stays its to administer').toBe(200);
	expect((await call('PUT', `${usersUrl}/${siteUserId}/sites`, { token, data: { allSites: false, siteCodes: ['DMO'] } })).status()).toBe(200);
	// Its own account, with a fresh second factor: still not its to change.
	const self = claimsOf(token).sub as string;
	expect((await call('PUT', `${usersUrl}/${self}/roles/BorderShiftSupervisor`, { token })).status(), 'own role').toBe(403);
	expect((await call('DELETE', `${usersUrl}/${self}/roles/SystemAdministrator`, { token })).status(), 'own role taken').toBe(403);
	expect((await call('PUT', `${usersUrl}/${self}/sites`, { token, data: { allSites: true } })).status(), 'own sites widened').toBe(403);
	expect((await call('POST', `${usersUrl}/${self}/reset-password`, { token })).status(), 'own password through the admin path').toBe(403);
	expect((await call('POST', `${usersUrl}/${self}/reset-totp`, { token })).status(), 'own authenticator through the admin path').toBe(403);
	expect((await call('PUT', `${usersUrl}/${siteUserId}/sites`, { token, data: { allSites: true } })).status(), 'every site').toBe(403);
	expect(
		(await call('PUT', `${usersUrl}/${siteUserId}/sites`, { token, data: { allSites: false, siteCodes: ['DMO', 'E2E1'] } })).status(),
		'a site of its own and one beyond'
	).toBe(403);
	expect(
		(await call('PUT', `${usersUrl}/${siteUserId}/sites`, { token, data: { allSites: false, siteCodes: ['NOPE'] } })).status(),
		'an unknown site reads the same as a foreign one'
	).toBe(403);
	// Accounts beyond its site are not there at all: not listed, not readable, not changeable, not in the audit.
	const admin = (await signIn(accounts().webAdmin)).accessToken;
	const allSitesId = await idOf(admin, accounts().webAdmin.userName);
	const otherSiteId = await idOf(admin, accounts().BorderShiftSupervisor.userName);
	// An account without sites may be about to get any site: it belongs to the every-site administrators.
	const unsitedId = await idOf(admin, accounts().unlock.userName);
	expect((await call('GET', `${usersUrl}/${unsitedId}`, { token })).status(), 'an account without sites').toBe(404);
	expect((await (await call('GET', `${auditUrl}?targetId=${unsitedId}`, { token })).json()).totalCount).toBe(0);
	for (const id of [allSitesId, otherSiteId]) {
		expect((await call('GET', `${usersUrl}/${id}`, { token })).status()).toBe(404);
		expect((await call('PUT', `${usersUrl}/${id}/roles/TerminalDutyManager`, { token })).status()).toBe(404);
		expect((await call('POST', `${usersUrl}/${id}/disable`, { token })).status()).toBe(404);
		expect((await (await call('GET', `${auditUrl}?targetId=${id}`, { token })).json()).totalCount).toBe(0);
	}
	expect((await (await call('GET', `${usersUrl}?text=${accounts().webAdmin.userName}`, { token })).json()).totalCount).toBe(0);

	const guards = await guardPage(page);
	await signInThroughUi(page, account);
	// The site list arrives after the form opens (a slow network): its one site is still given by default.
	let held = false;
	await page.route(
		(url) => url.pathname === '/api/v1/sites',
		async (route) => {
			if (!held) {
				held = true;
				await new Promise((resolve) => setTimeout(resolve, 1_500));
			}
			await route.continue();
		}
	);
	await page.goto('/users');
	await page.getByTestId('add-user').click();
	const sites = page.getByTestId('new-user-sites');
	await expect(sites.getByRole('radio', { name: 'Every site' }), 'every site is not its to give').toBeDisabled();
	await expect(sites.locator('[data-site]')).toHaveCount(1);
	await expect(sites.locator('[data-site="DMO"]'), 'its one site by default').toBeChecked();
	await sites.locator('[data-site="DMO"]').uncheck();
	await page.getByTestId('user-form').getByLabel('Username', { exact: true }).fill(`e2e.site.ui.${run}`);
	await page.getByTestId('save-user').click();
	await expect(page.getByTestId('user-problems'), 'refused before any request').toContainText('at least one of your sites');
	await page.getByTestId('user-form').getByRole('button', { name: 'Cancel' }).click();
	await openUser(page, `e2e.site.${run}`);
	const panel = page.locator(`[data-testid="user-panel"][data-user="e2e.site.${run}"]`);
	await expect(panel.getByRole('radio', { name: 'Every site' }), 'every site is not its to give').toBeDisabled();
	await expect(panel.locator('[data-site]')).toHaveCount(1);
	await expect(panel.locator('[data-site="DMO"]')).toBeChecked();
	await expect(panel.getByText('You can grant only the sites you can reach yourself.')).toBeVisible();
	await page.getByTestId('user-search').fill('e2e.');
	await page.getByTestId('search-users').click();
	await expect(row(page, accounts().webBorder.userName), 'a demo airport account').toBeVisible();
	await expect(row(page, accounts().webAdmin.userName), 'an every-site account').toHaveCount(0);
	await expect(row(page, accounts().BorderShiftSupervisor.userName), 'another site').toHaveCount(0);

	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('المستخدمون والصلاحيات');
	await expect(panel.getByTestId('user-status')).toContainText('نشط');
	// Signing in with a second factor answers 401 mfa_required before the code.
	allowStatuses(guards, 401);
	await guards.expectClean();
});

test('without the permission there is no screen and every user API answers 403', async ({ page }) => {
	const border = accounts().webBorder;
	const token = (await signIn(border)).accessToken;
	const self = claimsOf(token).sub as string;
	expect((await call('GET', usersUrl, { token })).status()).toBe(403);
	expect((await call('PUT', `${usersUrl}/${self}/roles/SystemAdministrator`, { token })).status(), 'self-grant').toBe(403);
	expect((await call('PUT', `${usersUrl}/${self}/sites`, { token, data: { allSites: true } })).status(), 'own sites').toBe(403);
	expect((await call('POST', usersUrl, { token, data: { userName: `e2e.sneaky.${run}`, roles: ['SystemAdministrator'] } })).status()).toBe(403);
	expect((await call('GET', auditUrl, { token })).status()).toBe(403);
	expect((await call('GET', `${hosts.main}/api/v1/admin/roles`, { token })).status()).toBe(403);

	const guards = await guardPage(page);
	const requests: string[] = [];
	page.on('request', (request) => {
		if (/\/api\/v1\/admin\/(users|roles|audit-entries)/.test(request.url())) requests.push(request.url());
	});
	await signInThroughUi(page, border);
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Users and access' })).toHaveCount(0);
	await page.goto('/users');
	await expect(page.getByTestId('no-access')).toContainText('No access');
	await expect(page.getByTestId('users-table')).toHaveCount(0);
	await expect(page.getByTestId('add-user')).toHaveCount(0);
	expect(requests, 'the screen asks the API for nothing it cannot have').toEqual([]);
	await guards.expectClean();
});
