import { expect, test } from '@playwright/test';
import {
	accounts,
	call,
	claimsOf,
	databaseAvailable,
	login,
	logoutUrl,
	refresh,
	refreshGraceSeconds,
	refreshUrl,
	refreshCookieName,
	refusedWithin,
	signIn
} from '../support/accounts';
import { expectProblemDetails } from '../support/api-assertions';
import { foreignOrigin, hosts, systemInfoPath, webOrigin } from '../support/hosts';

// ARV-010b (ADR-0026): server-side sessions. Every sign-in is a new session and refresh family; the refresh token
// lives only in the __Secure-ariva_rt cookie and rotates on every use; reuse within the grace window (3 seconds in
// this run) returns the same successor once, any other reuse revokes the family; logout and disable end access on
// every host within 5 seconds. Idle and absolute expiry need a clock the suite cannot move: Ariva.IntegrationTests
// (SessionTests) covers them with a manual clock.

test.skip(!databaseAvailable, 'sessions need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

const protectedUrls = [`${hosts.main}/api/v1/not-a-route`, `${hosts.ingest}${systemInfoPath}`, `${hosts.integration}${systemInfoPath}`];

test.describe('refresh cookie', () => {
	test('sign-in sets an HttpOnly, Secure, SameSite=Strict cookie on /api/auth that never appears in the body', async () => {
		const response = await login(accounts().session.userName, accounts().session.password);
		const body = await response.text();
		const setCookie = response.refreshSetCookie() ?? '';
		const attributes = setCookie.toLowerCase();

		expect(response.status()).toBe(200);
		expect(setCookie.startsWith(`${refreshCookieName}=`)).toBe(true);
		expect(attributes).toContain('httponly');
		expect(attributes).toContain('secure');
		expect(attributes).toContain('samesite=strict');
		expect(attributes).toContain('path=/api/auth');
		expect(attributes).toContain('max-age=43200');
		expect(attributes).not.toContain('domain=');
		expect(body).not.toContain(response.refreshCookie()!);
		expect(JSON.parse(body)).not.toHaveProperty('refreshToken');
	});

	test('refresh without the CSRF header or from a foreign origin is refused with 403', async () => {
		const { refreshToken } = await signIn(accounts().session);

		const noHeader = await call('POST', refreshUrl, { cookie: refreshToken, origin: webOrigin });
		const foreign = await call('POST', refreshUrl, { cookie: refreshToken, csrf: true, origin: foreignOrigin });
		const noOrigin = await call('POST', refreshUrl, { cookie: refreshToken, csrf: true });

		await expectProblemDetails(noHeader, 403);
		await expectProblemDetails(foreign, 403);
		await expectProblemDetails(noOrigin, 403);
		expect((await refresh(refreshToken)).status(), 'the refused attempts did not use the token').toBe(200);
	});

	test('refresh rotates the cookie and keeps the session', async () => {
		const first = await signIn(accounts().session);

		const response = await refresh(first.refreshToken);
		const body = await response.json();

		expect(response.status()).toBe(200);
		expect(response.refreshCookie()).toBeTruthy();
		expect(response.refreshCookie()).not.toBe(first.refreshToken);
		expect(claimsOf(body.accessToken).sid).toBe(claimsOf(first.accessToken).sid);
		expect((await refresh(response.refreshCookie()!)).status(), 'the successor is current').toBe(200);
	});

	test('reuse within the grace window returns the same successor once, then revokes the family', async () => {
		const first = await signIn(accounts().session);

		const rotated = await refresh(first.refreshToken);
		const sameAgain = await refresh(first.refreshToken);
		const theft = await refresh(first.refreshToken);

		expect(rotated.status()).toBe(200);
		expect(sameAgain.status()).toBe(200);
		expect(sameAgain.refreshCookie()).toBe(rotated.refreshCookie());
		await expectProblemDetails(theft, 401);
		expect((await theft.json()).error).toBe('session_expired');
		expect(theft.refreshCookie(), 'a refused refresh clears the cookie').toBe('');
		expect((await refresh(rotated.refreshCookie()!)).status(), 'the successor dies with its family').toBe(401);
		expect(await refusedWithin(first.accessToken, protectedUrls[0])).toBeGreaterThanOrEqual(0);
	});

	test('reuse after the grace window revokes the family', async () => {
		const first = await signIn(accounts().session);
		expect((await refresh(first.refreshToken)).status()).toBe(200);

		await new Promise((resolve) => setTimeout(resolve, (refreshGraceSeconds + 1) * 1000));
		const reused = await refresh(first.refreshToken);

		expect(reused.status()).toBe(401);
		expect(await refusedWithin(first.accessToken, protectedUrls[0])).toBeGreaterThanOrEqual(0);
	});

	test('two tabs refreshing together both get the same successor', async () => {
		const first = await signIn(accounts().session);

		const [a, b] = await Promise.all([refresh(first.refreshToken), refresh(first.refreshToken)]);

		expect(a.status()).toBe(200);
		expect(b.status()).toBe(200);
		expect(a.refreshCookie()).toBe(b.refreshCookie());
	});
});

test.describe('session lifecycle', () => {
	test('every sign-in is a new session, and a cookie sent with sign-in is revoked', async () => {
		const first = await signIn(accounts().session);

		const second = await login(accounts().session.userName, accounts().session.password, undefined, first.refreshToken);
		const secondToken = (await second.json()).accessToken as string;

		expect(claimsOf(secondToken).sid).not.toBe(claimsOf(first.accessToken).sid);
		expect((await refresh(first.refreshToken)).status(), 'CWE-384: the old session ends').toBe(401);
		expect(await refusedWithin(first.accessToken, protectedUrls[0])).toBeGreaterThanOrEqual(0);
		expect((await call('GET', protectedUrls[0], { token: secondToken })).status()).toBe(404);
	});

	test('after logout the old access token is refused on every host within 5 seconds', async () => {
		const session = await signIn(accounts().session);
		for (const url of protectedUrls) expect((await call('GET', url, { token: session.accessToken })).status(), url).toBe(404);

		const logout = await call('POST', logoutUrl, { token: session.accessToken, cookie: session.refreshToken });

		expect(logout.status()).toBe(204);
		expect(logout.refreshCookie(), 'logout clears the cookie').toBe('');
		for (const url of protectedUrls) {
			const elapsed = await refusedWithin(session.accessToken, url);
			expect(elapsed, `${url} refuses the token`).toBeGreaterThanOrEqual(0);
			expect(elapsed, `${url} within 5 seconds`).toBeLessThanOrEqual(5000);
		}
		const body = await (await call('GET', protectedUrls[0], { token: session.accessToken })).json();
		expect(body.error).toBe('session_expired');
		expect((await refresh(session.refreshToken)).status()).toBe(401);
	});

	test('a disabled user loses access on every host within 5 seconds and cannot sign in', async () => {
		const target = accounts().disabled;
		const session = await signIn(target);
		const userId = String(claimsOf(session.accessToken).sub);
		const admin = await signIn(accounts().SystemAdministrator);

		try {
			const disabled = await call('POST', `${hosts.main}/api/v1/admin/users/${userId}/disable`, { token: admin.accessToken });
			expect(disabled.status()).toBe(204);

			for (const url of protectedUrls) {
				const elapsed = await refusedWithin(session.accessToken, url);
				expect(elapsed, `${url} refuses the token`).toBeGreaterThanOrEqual(0);
				expect(elapsed, `${url} within 5 seconds`).toBeLessThanOrEqual(5000);
			}
			expect((await refresh(session.refreshToken)).status()).toBe(401);
			expect((await login(target.userName, target.password)).status()).toBe(401);
		} finally {
			// Re-enable so a retry of this test starts from the same state.
			await call('POST', `${hosts.main}/api/v1/admin/users/${userId}/enable`, { token: admin.accessToken });
		}

		expect((await login(target.userName, target.password)).status()).toBe(200);
	});

	test('an administrator cannot disable their own account', async () => {
		const admin = await signIn(accounts().SystemAdministrator);

		const response = await call('POST', `${hosts.main}/api/v1/admin/users/${claimsOf(admin.accessToken).sub}/disable`, { token: admin.accessToken });

		await expectProblemDetails(response, 400);
	});
});
