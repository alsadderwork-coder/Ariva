import crypto from 'node:crypto';
import { expect, test, type APIResponse } from '@playwright/test';
import {
	accounts,
	changedPassword,
	changePasswordUrl,
	claimsOf,
	clientAddress,
	databaseAvailable,
	developmentSigningKey,
	headerOf,
	keyIdOf,
	lockoutSeconds,
	lockoutThreshold,
	login,
	logoutUrl,
	signIn,
	signToken
} from '../support/accounts';
import { expectProblemDetails } from '../support/api-assertions';
import { hosts, systemInfoPath } from '../support/hosts';

// ARV-010a (ADR-0026): username and password sign-in on Ariva.Api.Main, ES256 access tokens validated by every host,
// brute force lockout, the per-address limit and the pending scope. The accounts are created by Ariva.Api.Main from
// Auth:DevelopmentUsers with per-run random passwords (tests/support/accounts.ts).

const systemInfo = `${hosts.main}${systemInfoPath}`;

test.skip(!databaseAvailable, 'sign-in needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

/** The parts of a sign-in failure a client sees, without the per-request trace id. */
async function failureOf(response: APIResponse) {
	const body = await response.json();
	return { status: response.status(), type: body.type, title: body.title, detail: body.detail };
}

test.describe('sign-in', () => {
	test('a valid username and password return an ES256 access token that is never cached', async ({ request }) => {
		const response = await login(request, ` ${accounts().SystemAdministrator.userName.toUpperCase()} `, accounts().SystemAdministrator.password);

		expect(response.status()).toBe(200);
		expect(response.headers()['cache-control']).toBe('no-store');
		const token = await response.json();
		expect(token.tokenType).toBe('Bearer');
		expect(token.expiresIn).toBe(900);
		expect(token.scope ?? null).toBeNull();

		const header = headerOf(token.accessToken);
		expect(header.alg).toBe('ES256');
		expect(header.typ).toBe('at+jwt');
		expect(typeof header.kid).toBe('string');

		const claims = claimsOf(token.accessToken);
		expect(claims.iss).toBe('ariva');
		expect(claims.aud).toBe('ariva-users');
		expect(claims.name).toBe(accounts().SystemAdministrator.userName);
		expect(Number(claims.exp) - Number(claims.iat)).toBe(900);
		expect(claims).not.toHaveProperty('role');
	});

	test('the token opens a protected endpoint on Main and is accepted by the other hosts', async ({ request }) => {
		const token = await signIn(request, accounts().SystemAdministrator);
		const authorization = { Authorization: `Bearer ${token.accessToken}` };

		expect((await request.get(systemInfo, { headers: authorization })).status()).toBe(200);
		expect((await request.post(logoutUrl, { headers: authorization })).status()).toBe(204);

		// Ingest and Integration map no system info route: a valid token gets past default deny to the 404, an
		// anonymous call stops at 401. That proves both hosts validate Main's tokens with the shared public key.
		for (const host of [hosts.ingest, hosts.integration]) {
			expect((await request.get(`${host}${systemInfoPath}`, { headers: authorization })).status(), host).toBe(404);
			expect((await request.get(`${host}${systemInfoPath}`)).status(), host).toBe(401);
		}
	});

	test('a wrong password and an unknown user get the same answer', async ({ request }) => {
		const wrongPassword = await login(request, accounts().BorderShiftSupervisor.userName, 'not-the-password-0000');
		const unknownUser = await login(request, `nobody.${crypto.randomUUID()}`, 'not-the-password-0000');
		const malformedUser = await login(request, 'x', 'not-the-password-0000');

		await expectProblemDetails(wrongPassword, 401);
		expect(await failureOf(unknownUser)).toEqual(await failureOf(wrongPassword));
		expect(await failureOf(malformedUser)).toEqual(await failureOf(wrongPassword));
	});

	test('a token in the access_token query string is ignored', async ({ request }) => {
		const token = await signIn(request, accounts().SystemAdministrator);

		const response = await request.get(`${systemInfo}?access_token=${token.accessToken}`);

		await expectProblemDetails(response, 401);
	});

	test('the eleventh sign-in from one address within a minute gets 429 with Retry-After', async ({ request }) => {
		const address = clientAddress();
		for (let attempt = 0; attempt < 10; attempt++) {
			expect((await login(request, `nobody.${attempt}`, 'not-the-password-0000', address)).status()).toBe(401);
		}

		const limited = await login(request, accounts().SystemAdministrator.userName, accounts().SystemAdministrator.password, address);

		expect(limited.status()).toBe(429);
		expect(Number(limited.headers()['retry-after'])).toBeGreaterThan(0);
		expect((await login(request, accounts().SystemAdministrator.userName, accounts().SystemAdministrator.password)).status()).toBe(200);
	});
});

test.describe('lockout', () => {
	test('ten failures lock the account, the correct password then fails, and the lock expires on its own', async ({ request }) => {
		const { userName, password } = accounts().lockout;
		const first = await failureOf(await login(request, userName, 'not-the-password-0000'));
		for (let attempt = 1; attempt < lockoutThreshold; attempt++) {
			expect((await login(request, userName, 'not-the-password-0000')).status()).toBe(401);
		}

		const whileLocked = await login(request, userName, password);
		expect(await failureOf(whileLocked)).toEqual(first);

		await new Promise((resolve) => setTimeout(resolve, (lockoutSeconds + 1) * 1000));
		expect((await login(request, userName, password)).status()).toBe(200);
	});

	test('an administrator unlocks a locked account at once', async ({ request }) => {
		const target = accounts().unlock;
		const userId = String(claimsOf((await signIn(request, target)).accessToken).sub);
		for (let attempt = 0; attempt < lockoutThreshold; attempt++) {
			await login(request, target.userName, 'not-the-password-0000');
		}
		expect((await login(request, target.userName, target.password)).status()).toBe(401);

		const admin = await signIn(request, accounts().SystemAdministrator);
		const unlock = await request.post(`${hosts.main}/api/v1/admin/users/${userId}/unlock`, {
			headers: { Authorization: `Bearer ${admin.accessToken}` }
		});

		expect(unlock.status()).toBe(204);
		expect((await login(request, target.userName, target.password)).status()).toBe(200);
	});

	test('a user without EditUser cannot unlock', async ({ request }) => {
		const supervisor = await signIn(request, accounts().BorderShiftSupervisor);
		const target = String(claimsOf(supervisor.accessToken).sub);

		const response = await request.post(`${hosts.main}/api/v1/admin/users/${target}/unlock`, {
			headers: { Authorization: `Bearer ${supervisor.accessToken}` }
		});

		await expectProblemDetails(response, 403);
	});
});

test.describe('pending scope', () => {
	test('a temporary password signs in with the pending scope, which only reaches the pending endpoints', async ({ request }) => {
		const token = await signIn(request, accounts().pending);
		const authorization = { Authorization: `Bearer ${token.accessToken}` };

		expect(token.scope).toBe('pending');
		const blocked = await request.get(systemInfo, { headers: authorization });
		await expectProblemDetails(blocked, 403);
		expect((await blocked.json()).type).toBe('https://ariva/problems/account-pending');
		expect((await request.post(logoutUrl, { headers: authorization })).status()).toBe(204);
	});

	test('changing the temporary password gives a full token; a weak or reused password is refused', async ({ request }) => {
		const account = accounts().changer;
		const pending = await signIn(request, account);
		const authorization = { Authorization: `Bearer ${pending.accessToken}`, 'X-Forwarded-For': clientAddress() };

		const weak = await request.post(changePasswordUrl, { headers: authorization, data: { currentPassword: account.password, newPassword: 'password1234' } });
		expect(weak.status()).toBe(400);
		const same = await request.post(changePasswordUrl, { headers: authorization, data: { currentPassword: account.password, newPassword: account.password } });
		expect(same.status()).toBe(400);
		const wrongCurrent = await request.post(changePasswordUrl, { headers: authorization, data: { currentPassword: 'not-the-password-0000', newPassword: changedPassword() } });
		expect(wrongCurrent.status()).toBe(400);

		const changed = await request.post(changePasswordUrl, { headers: authorization, data: { currentPassword: account.password, newPassword: changedPassword() } });
		expect(changed.status()).toBe(200);
		expect(changed.headers()['cache-control']).toBe('no-store');
		const full = await changed.json();
		expect(full.scope ?? null).toBeNull();
		expect((await login(request, account.userName, account.password)).status()).toBe(401);

		// Back to the seeded password, so a retry of this test starts from the same state (no longer temporary).
		const restore = await request.post(changePasswordUrl, {
			headers: { Authorization: `Bearer ${full.accessToken}`, 'X-Forwarded-For': clientAddress() },
			data: { currentPassword: changedPassword(), newPassword: account.password }
		});
		expect(restore.status()).toBe(200);
	});
});

test.describe('token validation', () => {
	const key = developmentSigningKey();

	test('tokens without an ES256 signature are refused', async ({ request }) => {
		const real = claimsOf((await signIn(request, accounts().SystemAdministrator)).accessToken);
		const none = signToken({ alg: 'none', typ: 'at+jwt' }, real);
		// Key confusion: HS256 with the public key as the HMAC secret.
		const publicPem = key ? Buffer.from(crypto.createPublicKey(key).export({ type: 'spki', format: 'pem' }) as string) : Buffer.from('ariva');
		const hs256 = signToken({ alg: 'HS256', typ: 'at+jwt' }, real, undefined, publicPem);

		for (const token of [none, hs256]) {
			await expectProblemDetails(await request.get(systemInfo, { headers: { Authorization: `Bearer ${token}` } }), 401);
		}
	});

	test('tokens signed with the run key but with a wrong audience, issuer, type or lifetime are refused', async ({ request }) => {
		test.skip(!key, 'needs the development key of this run (hosts started by this Playwright run)');
		const real = claimsOf((await signIn(request, accounts().SystemAdministrator)).accessToken);
		const header = { alg: 'ES256', typ: 'at+jwt', kid: keyIdOf(key!) };
		const now = Math.floor(Date.now() / 1000);

		// Positive control: the same claims signed by the test are accepted, so the refusals below are about the change.
		const valid = signToken(header, real, key);
		expect((await request.get(systemInfo, { headers: { Authorization: `Bearer ${valid}` } })).status()).toBe(200);

		const tampered: Record<string, string> = {
			'other audience': signToken(header, { ...real, aud: 'another-api' }, key),
			'other issuer': signToken(header, { ...real, iss: 'https://attacker.example' }, key),
			'typ JWT': signToken({ ...header, typ: 'JWT' }, real, key),
			expired: signToken(header, { ...real, iat: now - 1200, nbf: now - 1200, exp: now - 120 }, key),
			'unknown key': (() => {
				const other = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' }).privateKey;
				return signToken({ ...header, kid: keyIdOf(other) }, real, other);
			})()
		};

		for (const [name, token] of Object.entries(tampered)) {
			const response = await request.get(systemInfo, { headers: { Authorization: `Bearer ${token}` } });
			expect(response.status(), name).toBe(401);
		}
	});
});
