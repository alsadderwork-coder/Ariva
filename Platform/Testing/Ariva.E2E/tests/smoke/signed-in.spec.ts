import { accountProblems, enter, smokeRole, smokeRolePermissions, smokeSites, smokeZone, yesterdayUtc, type CurrentUser } from './support/checks';
import { hub, scrubbed, send, signIn, signOut, unusedCode, type SmokeSession } from './support/client';
import { expect, smoke, target } from './support/smoke';
import { credential, credentialVariables, variable } from './support/target';

// ARV-128: the dedicated smoke account (a Handler station manager at the smoke site or sites only, owner decision
// 2026-10-15) signs in with its authenticator where it has one, and is checked before anything else uses it: the role
// alone, exactly that role's permissions, none critical, exactly the configured sites. Then the live hub, the daily report's
// route (which this role may not read: 403), a sign-in through the web app to the live screen, and the sign-outs. In order,
// in one worker: a TOTP code counts once per account (replay guard, CWE-287), so the sign-ins share the step they last used.
// Reads only, apart from the sign-ins and sign-outs.

smoke.configure({ mode: 'serial' });

let session: SmokeSession | undefined;
const site = () => smokeSites()[0];

async function currentUser(token: string): Promise<CurrentUser> {
	const me = await send('GET', `${target.base}/api/auth/me`, { token });
	if (me.status !== 200) throw new Error(`ARV-128: GET /api/auth/me answered ${me.status}`);
	return me.json<CurrentUser>();
}

smoke.beforeAll(async () => {
	session = await signIn();
	// Before any use of the account: the run refuses an account with another role, more permissions, every site or another site.
	const problems = accountProblems(await currentUser(session.accessToken), smokeSites());
	if (problems.length > 0) {
		await signOut(session).catch(() => undefined);
		session = undefined;
		throw new Error(`ARV-128: the smoke account may not be used: ${problems.join('; ')}`);
	}
});

smoke.afterAll(async () => {
	// A run stopped early still ends its session.
	if (session) await signOut(session).catch(() => undefined);
});

smoke('the smoke account is a Handler station manager with that role\'s permissions only, none critical, at the smoke sites only', { tag: '@read' }, async () => {
	const me = await send('GET', `${target.base}/api/auth/me`, { token: session!.accessToken });
	expect(me.status, 'GET /api/auth/me').toBe(200);
	expect(me.headers['cache-control']).toContain('no-store');
	const now = me.json<CurrentUser>();
	expect(now.roles).toEqual([smokeRole]);
	expect([...now.permissions].sort()).toEqual([...smokeRolePermissions].sort());
	expect(now.allSites, 'never every site').toBe(false);
	expect([...now.sites].sort(), 'exactly the configured smoke sites').toEqual([...smokeSites()].sort());
	expect(accountProblems(now, smokeSites())).toEqual([]);
});

smoke('the live hub refuses a connection without a token and lets the smoke account join a queue zone of its site only', { tag: '@read' }, async () => {
	await expect(scrubbed(hub().start()), 'a connection without a token').rejects.toThrow();

	const token = session!.accessToken;
	const zone = await smokeZone(token);
	const connection = hub(token);
	await scrubbed(connection.start(), token);
	try {
		// The zone's latest snapshot, or null when Stream has published none yet.
		const snapshot = await scrubbed(connection.invoke<{ zoneKey: string } | null>('JoinZone', `${site()}/${zone}`), token);
		if (snapshot !== null) expect(snapshot.zoneKey).toBe(`${site()}/${zone}`);
		await expect(scrubbed(connection.invoke('JoinZone', 'ZZ9/NO-SUCH-ZONE'), token), 'a zone of a site it does not hold').rejects.toThrow(/forbidden/);
	} finally {
		await connection.stop();
	}
});

smoke("the daily report's route answers 403 to the smoke account, whose role does not read reports", { tag: '@read' }, async () => {
	// The route and its authorization (Report.View, which a Handler station manager does not hold); what the report holds is
	// covered by the E2E reports specs. Without a token it is 401 (CWE-306).
	const url = `${target.base}/api/v1/sites/${encodeURIComponent(site())}/reports/daily?date=${yesterdayUtc()}`;
	expect((await send('GET', url, { token: session!.accessToken })).status, 'the smoke account').toBe(403);
	expect((await send('GET', url)).status, 'no token').toBe(401);
});

smoke('the smoke account signs in through the web app, sees the live screen connected and signs out', { tag: '@read' }, async ({ page, guards }) => {
	await page.goto('/login');
	await enter(page.locator('#login-user'), credential(credentialVariables.userName));
	await enter(page.locator('#login-password'), credential(credentialVariables.password));
	await page.getByRole('button', { name: 'Sign in' }).click();
	const secret = variable(credentialVariables.totpSecret);
	if (secret) {
		// The first answer asks for the second factor (401 mfa_required); the browser logs that status.
		await enter(page.locator('#login-code'), await unusedCode(secret));
		await page.getByRole('button', { name: 'Sign in' }).click();
	}
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
	await expect(page.getByTestId('live-state'), 'the live hub through the web origin').toHaveAttribute('data-state', 'connected', { timeout: 20_000 });
	const offered = await page.locator('#live-site option').evaluateAll((options) => options.map((option) => (option as HTMLOptionElement).value));
	expect(offered, 'the live screen offers the smoke site').toContain(site());

	const loggedOut = page.waitForResponse((response) => new URL(response.url()).pathname.endsWith('/api/auth/logout'));
	await page.getByTestId('sign-out').click();
	expect((await loggedOut).status(), 'sign-out').toBe(204);
	await expect(page).toHaveURL(/\/login/);

	// The 401 asking for the second factor is the only error the browser may log.
	const expected = secret ? /status of 401/ : /^$/;
	guards.consoleErrors.splice(0, guards.consoleErrors.length, ...guards.consoleErrors.filter((message) => !expected.test(message)));
	await guards.expectClean();
});

smoke('the API session signs out', { tag: '@read' }, async () => {
	const ended = session!;
	session = undefined;
	expect(await signOut(ended), 'POST /api/auth/logout').toBe(204);
	// Every replica refuses an ended session within 5 seconds (ADR-0026).
	await expect
		.poll(async () => (await send('GET', `${target.base}/api/auth/me`, { token: ended.accessToken })).status, { message: 'the token after sign-out', timeout: 8_000 })
		.toBe(401);
});
