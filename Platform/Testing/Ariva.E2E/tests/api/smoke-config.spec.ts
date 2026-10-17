import { expect, test } from '@playwright/test';
import { criticalPermissionCodes, smokeRole, smokeRolePermissions } from '../smoke/support/role';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts, webUrl } from '../support/hosts';
import { smokeSite } from '../support/smoke-site';
import { playwrightChild } from '../support/smoke-runs';

// ARV-128 (TC-56): the smoke config an operator runs against a deployed environment (playwright.smoke.config.ts), run
// here in a child Playwright process against this stack as that operator would: every value in ARIVA_SMOKE_* variables,
// a smoke account of its own (e2e.smokecfg: a TOTP code counts once per account, and the smoke project signs in as
// e2e.smoke meanwhile), the test device and the display global setup provisioned. It must pass every check, skip none,
// and print no credential; its own artifact check fails it if a credential reached its report or output.

test.skip(!databaseAvailable, 'the smoke suite signs in, which needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

test('the deployed smoke config runs green against this stack and prints no credential', async () => {
	test.setTimeout(300_000);
	const account = accounts().smokeConfig;
	const provisioned = {
		ARIVA_SMOKE_ZONE: process.env.ARIVA_SMOKE_ZONE,
		ARIVA_SMOKE_DEVICE_KEY: process.env.ARIVA_SMOKE_DEVICE_KEY,
		ARIVA_SMOKE_DISPLAY_CODE: process.env.ARIVA_SMOKE_DISPLAY_CODE,
		ARIVA_SMOKE_DISPLAY_KEY: process.env.ARIVA_SMOKE_DISPLAY_KEY
	};
	for (const [name, value] of Object.entries(provisioned)) expect(value, `${name} from global setup (tests/support/smoke-site.ts)`).toBeTruthy();

	const secrets = [account.password, account.totpSecret!, provisioned.ARIVA_SMOKE_DEVICE_KEY!, provisioned.ARIVA_SMOKE_DISPLAY_KEY!];
	const outcome = playwrightChild(
		'playwright.smoke.config.ts',
		{
			ARIVA_SMOKE_BASE_URL: webUrl,
			ARIVA_SMOKE_MAIN_URL: hosts.main,
			ARIVA_SMOKE_INGEST_URL: hosts.ingest,
			ARIVA_SMOKE_INTEGRATION_URL: hosts.integration,
			ARIVA_SMOKE_USERNAME: account.userName,
			ARIVA_SMOKE_PASSWORD: account.password,
			ARIVA_SMOKE_TOTP_SECRET: account.totpSecret!,
			ARIVA_SMOKE_SITE: smokeSite,
			...(provisioned as Record<string, string>)
		},
		[],
		240_000
	);
	// The output is shown on failure with every secret masked, so even a failing run prints none.
	const shown = secrets.reduce((text, secret) => text.split(secret).join('[secret]'), outcome.output);
	for (const secret of secrets) expect(outcome.output.includes(secret), 'a smoke credential in the run output').toBe(false);
	expect(outcome.status, shown).toBe(0);
	expect(outcome.failed, shown).toBe(0);
	expect(outcome.skipped, 'no check skipped').toBe(0);
	// Health of three hosts and the web page, the device check and the display, and the five signed-in checks.
	expect(outcome.passed, shown).toBe(11);
	expect(outcome.output).toContain('smoke artifacts: no credential in');
});

test("the smoke role's permission list is the Handler station manager's as the API reports it, and holds no critical permission", async () => {
	// The smoke suite compares the smoke account's permissions with this list at every run (tests/smoke/support/role.ts); the list
	// must follow the role (RolePermissions.HandlerStationManager), and no permission a critical action needs may be in it.
	const admin = (await signIn(accounts().SystemAdministrator)).accessToken;
	const roles = await call('GET', `${hosts.main}/api/v1/admin/roles`, { token: admin });
	expect(roles.status()).toBe(200);
	const role = ((await roles.json()) as { code: string; permissions: string[] }[]).find((r) => r.code === smokeRole);
	expect(role, `${smokeRole} is listed`).toBeDefined();
	expect([...role!.permissions].sort()).toEqual([...smokeRolePermissions].sort());
	const critical = criticalPermissionCodes();
	expect(critical.length).toBeGreaterThan(0);
	expect(smokeRolePermissions.filter((code) => critical.includes(code))).toEqual([]);
});
