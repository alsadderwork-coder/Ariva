import { expect, test } from '@playwright/test';
import { accounts, call, claimsOf, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-012 (CWE-863): every site-bound read is limited to the caller's sites. The seed binds e2e.border to E2E1,
// e2e.terminal to E2E2, e2e.handler to both and the administrators to every site. Another site's code answers 404
// without data, exactly like a site that does not exist (IDOR); granting site access is a critical action and never
// goes beyond the granter's own sites.

test.skip(!databaseAvailable, 'site scoping needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

const sitesUrl = `${hosts.main}/api/v1/sites`;

async function codes(token: string): Promise<string[]> {
	const response = await call('GET', sitesUrl, { token });
	expect(response.status()).toBe(200);
	return ((await response.json()) as { code: string }[]).map((site) => site.code);
}

test('each caller lists only its own sites', async () => {
	const border = await signIn(accounts().BorderShiftSupervisor);
	const terminal = await signIn(accounts().TerminalDutyManager);
	const handler = await signIn(accounts().HandlerStationManager);
	const admin = await signIn(accounts().SystemAdministrator);

	expect(await codes(border.accessToken)).toEqual(['E2E1']);
	expect(await codes(terminal.accessToken)).toEqual(['E2E2']);
	expect(await codes(handler.accessToken)).toEqual(['E2E1', 'E2E2']);
	expect(await codes(admin.accessToken)).toEqual(expect.arrayContaining(['E2E1', 'E2E2']));
});

test("another site's code answers 404 without data, like an unknown one", async () => {
	const border = await signIn(accounts().BorderShiftSupervisor);
	const terminal = await signIn(accounts().TerminalDutyManager);

	expect((await call('GET', `${sitesUrl}/E2E1`, { token: border.accessToken })).status()).toBe(200);
	for (const [token, code] of [
		[border.accessToken, 'E2E2'],
		[terminal.accessToken, 'E2E1'],
		[border.accessToken, 'NOPE'],
		[border.accessToken, "E2E1'%20OR%20'1'='1"],
		[border.accessToken, 'e2e1']
	]) {
		const response = await call('GET', `${sitesUrl}/${code}`, { token });
		expect(response.status(), code).toBe(404);
		const text = await response.text();
		expect(text, code).not.toContain('"name"');
	}
	expect((await call('PUT', `${hosts.main}/api/v1/admin/sites/E2E1`, { token: terminal.accessToken, data: { name: 'Taken' } })).status()).toBe(403);
});

test('site access is granted only by a stepped-up administrator within its own sites', async () => {
	// Own accounts: narrowing access ends the user's sessions, which must not hit accounts other tests use in parallel.
	const { userName, password, totpSecret } = accounts().siteAdmin;
	const admin = (await (await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) })).json()).accessToken as string;
	const plainAdmin = await signIn(accounts().SystemAdministrator);
	const border = await signIn(accounts().siteUser);
	const borderId = claimsOf(border.accessToken).sub as string;
	const url = `${hosts.main}/api/v1/admin/users/${borderId}/sites`;

	expect((await call('PUT', url, { token: plainAdmin.accessToken, data: { siteCodes: ['E2E1', 'E2E2'] } })).status(), 'no recent second factor').toBe(401);
	expect((await call('PUT', url, { token: border.accessToken, data: { allSites: true } })).status(), 'a supervisor widening itself').toBe(403);
	expect((await call('PUT', url, { token: admin, data: { siteCodes: ['NOPE'] } })).status(), 'unknown site').toBe(400);

	const widened = await call('PUT', url, { token: admin, data: { siteCodes: ['E2E1', 'E2E2'] } });
	expect(widened.status()).toBe(200);
	expect((await widened.json()).sites).toEqual(['E2E1', 'E2E2']);
	expect(await codes(border.accessToken), 'widening keeps the session and applies at once').toEqual(['E2E1', 'E2E2']);

	expect((await call('PUT', url, { token: admin, data: { siteCodes: ['E2E1'] } })).status()).toBe(200);
	await expect.poll(async () => (await call('GET', sitesUrl, { token: border.accessToken })).status(), { timeout: 6000, message: 'narrowing ends the session' }).toBe(401);
	const audit = await call('GET', `${hosts.main}/api/v1/admin/audit-entries?targetId=${borderId}&action=User.SitesChanged`, { token: admin });
	expect(((await audit.json()).data as unknown[]).length).toBeGreaterThanOrEqual(2);
});
