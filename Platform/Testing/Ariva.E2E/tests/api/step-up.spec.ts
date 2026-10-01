import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { accounts, call, claimsOf, databaseAvailable, developmentSigningKey, headerOf, login, signIn, signToken, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-010d (ADR-0026, RFC 9470): every critical action in security/critical-actions.json answers 401
// insufficient_user_authentication with max_age=900 when the second factor is older than 15 minutes or missing, and
// is reached after POST /api/auth/step-up. A non-critical endpoint never asks. The old token is the real one re-signed
// with this run's development key and auth_time moved back, so it belongs to a live session.

const here = path.dirname(fileURLToPath(import.meta.url));
const critical: { routes: { host: string; method: string; route: string }[] } = JSON.parse(
	fs.readFileSync(path.resolve(here, '..', '..', '..', '..', '..', 'security', 'critical-actions.json'), 'utf8')
);
const stepUpUrl = `${hosts.main}/api/auth/step-up`;
const routes = critical.routes.filter((route) => route.host === 'main').map((route) => ({ ...route, url: `${hosts.main}${route.route}` }));

test.skip(!databaseAvailable, 'step-up needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

/** The token with auth_time moved back by the given minutes, signed with the run's key. */
function aged(token: string, minutes: number): string {
	const key = developmentSigningKey()!;
	const claims = claimsOf(token);
	return signToken(headerOf(token), { ...claims, auth_time: (claims.auth_time as number) - minutes * 60 }, key);
}

function expectMfaRequired(status: number, wwwAuthenticate: string | undefined, body: any, what: string) {
	expect(status, what).toBe(401);
	expect(wwwAuthenticate, what).toContain('error="insufficient_user_authentication"');
	expect(wwwAuthenticate, what).toContain('max_age=900');
	expect(body.error, what).toBe('mfa_required');
	expect(body.max_age, what).toBe(900);
}

test('a password-only session is asked to step up on every critical action', async () => {
	const token = await signIn(accounts().BorderShiftSupervisor);
	expect(routes.length).toBeGreaterThan(0);

	for (const route of routes) {
		const response = await call(route.method, route.url, { token: token.accessToken, data: {} });
		expectMfaRequired(response.status(), response.headers()['www-authenticate'], await response.json(), `${route.method} ${route.route}`);
	}

	const notEnrolled = await call('POST', stepUpUrl, { token: token.accessToken, data: { code: '123456' } });
	expect(notEnrolled.status(), 'step-up needs an enrolled authenticator').toBe(400);
});

test('an old second factor is refused, step-up renews it in the same session, a non-critical endpoint never asks', async () => {
	test.skip(!developmentSigningKey(), 'needs the run key to age a token');
	const { userName, password, totpSecret } = accounts().stepUp;

	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status()).toBe(200);
	const fresh = (await signedIn.json()).accessToken as string;
	const old = aged(fresh, 16);

	for (const route of routes) {
		const recent = await call(route.method, route.url, { token: aged(fresh, 14), data: {} });
		expect(recent.status(), `${route.route} within 15 minutes`).not.toBe(401);

		const refused = await call(route.method, route.url, { token: old, data: {} });
		expectMfaRequired(refused.status(), refused.headers()['www-authenticate'], await refused.json(), `${route.method} ${route.route}`);
	}

	const enrol = await call('POST', `${hosts.main}/api/auth/totp/enroll`, { token: old });
	expect(enrol.status(), 'non-critical: the enrolled account gets 409, never a step-up').toBe(409);

	expect((await call('POST', stepUpUrl, { token: old, data: { code: '000000' } })).status()).toBe(400);
	const stepped = await call('POST', stepUpUrl, { token: old, data: { code: totpCode(totpSecret!, 1) } });
	expect(stepped.status()).toBe(200);
	expect(stepped.headers()['cache-control']).toContain('no-store');
	const renewed = (await stepped.json()).accessToken as string;
	const claims = claimsOf(renewed);
	expect(claims.amr).toEqual(['pwd', 'otp']);
	expect(claims.sid).toBe(claimsOf(fresh).sid);
	expect(Date.now() / 1000 - (claims.auth_time as number)).toBeLessThan(60);

	for (const route of routes) {
		const reached = await call(route.method, route.url, { token: renewed, data: {} });
		expect(reached.status(), `${route.route} after step-up`).not.toBe(401);
	}

	expect((await call('POST', stepUpUrl, { token: renewed, data: { code: totpCode(totpSecret!, 1) } })).status(), 'the same code twice').toBe(400);
});
