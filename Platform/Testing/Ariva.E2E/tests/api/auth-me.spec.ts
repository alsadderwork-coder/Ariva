import { expect, test } from '@playwright/test';
import { accounts, call, claimsOf, databaseAvailable, login, refusedWithin, signIn } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-051: GET /api/auth/me tells the web app who is signed in: names, roles, the permissions they grant (none while
// pending), sites, and what the first sign-in still needs. It is never cached, answers 401 without a valid session,
// and never carries a secret (password hash, TOTP seed, recovery codes).

const meUrl = `${hosts.main}/api/auth/me`;

test.skip(!databaseAvailable, 'sign-in needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

test('a full account sees its roles, permissions and sites, never a secret', async () => {
	const token = await signIn(accounts().webHandler);

	const response = await call('GET', meUrl, { token: token.accessToken });

	expect(response.status()).toBe(200);
	expect(response.headers()['cache-control']).toBe('no-store');
	const me = await response.json();
	expect(me).toMatchObject({
		userName: 'e2e.webhandler',
		roles: ['HandlerStationManager'],
		allSites: false,
		sites: ['DMO'],
		mustChangePassword: false,
		totpEnrolled: false,
		pending: false
	});
	expect(me.permissions).toContain('LiveQueue.View');
	expect(me.permissions).toContain('ZoneProfile.View');
	expect(me.permissions).not.toContain('Device.View');
	expect(me.permissions).not.toContain('User.View');
	expect(Object.keys(me).sort()).toEqual([
		'allSites',
		'displayName',
		'mustChangePassword',
		'pending',
		'permissions',
		'roles',
		'sites',
		'totpEnrolled',
		'userName'
	]);
	const text = await response.text();
	expectNoLeak(text, 'GET /api/auth/me');
	expect(text).not.toContain(accounts().webHandler.password);
});

test('an administrator has every site and the user permissions', async () => {
	const admin = await call('GET', meUrl, { token: (await signIn(accounts().webAdmin)).accessToken });
	const me = await admin.json();
	expect(me).toMatchObject({ userName: 'e2e.webadmin', roles: ['SystemAdministrator'], allSites: true, pending: false });
	expect(me.permissions).toEqual(expect.arrayContaining(['User.View', 'Device.View', 'ArrivalWaveLanes.View']));
});

test('a pending account is told what its first sign-in needs, with no permissions', async () => {
	const signedIn = await login(accounts().pending.userName, accounts().pending.password);
	expect(signedIn.status()).toBe(200);
	const token = await signedIn.json();
	expect(token.scope).toBe('pending');

	const response = await call('GET', meUrl, { token: token.accessToken });

	expect(response.status()).toBe(200);
	expect(await response.json()).toMatchObject({ userName: 'e2e.pending', mustChangePassword: true, pending: true, permissions: [], roles: [] });
});

test('no token, a tampered token or an ended session is refused', async () => {
	expect((await call('GET', meUrl)).status()).toBe(401);
	expect((await call('GET', meUrl, { token: 'not-a-token' })).status()).toBe(401);

	const token = await signIn(accounts().webHandler);
	const [header, , signature] = token.accessToken.split('.');
	const claims = { ...claimsOf(token.accessToken), name: 'e2e.webadmin' };
	const tampered = `${header}.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.${signature}`;
	expect((await call('GET', meUrl, { token: tampered })).status()).toBe(401);

	expect((await call('POST', `${hosts.main}/api/auth/logout`, { token: token.accessToken })).status()).toBe(204);
	expect(await refusedWithin(token.accessToken, meUrl), 'the ended session is refused within 6 seconds').toBeGreaterThanOrEqual(0);
	const ended = await call('GET', meUrl, { token: token.accessToken });
	expect((await ended.json()).error).toBe('session_expired');
});
