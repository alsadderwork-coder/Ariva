import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { accounts, call, claimsOf, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { expectProblemDetails } from '../support/api-assertions';
import { readBreakGlass } from '../support/global-setup';
import { hosts } from '../support/hosts';

// ARV-010c (ADR-0026): TOTP per RFC 6238 at sign-in with the replay guard, enrolment confirmed by a first code,
// single-use recovery codes, and the break-glass account the installer command creates. The tests that use one
// account run in order, because a TOTP code is accepted once per account and step.

test.skip(!databaseAvailable, 'TOTP needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const enrolUrl = `${hosts.main}/api/auth/totp/enroll`;
const confirmUrl = `${hosts.main}/api/auth/totp/confirm`;
const recoveryUrl = `${hosts.main}/api/auth/totp/recovery-codes`;

test('an enrolled account needs its code; the same code twice is refused and the next step is accepted', async () => {
	const { userName, password, totpSecret } = accounts().totp;

	const noCode = await login(userName, password);
	await expectProblemDetails(noCode, 401);
	expect((await noCode.json()).error).toBe('mfa_required');
	expect(noCode.refreshCookie(), 'no session before the second factor').toBeUndefined();

	const wrongPassword = await login(userName, 'not-the-password-0000');
	expect((await wrongPassword.json()).error, 'a wrong password never reveals that a code is needed').toBeUndefined();

	const code = totpCode(totpSecret!);
	const accepted = await login(userName, password, undefined, undefined, { code });
	expect(accepted.status()).toBe(200);
	expect(claimsOf((await accepted.json()).accessToken).amr).toEqual(['pwd', 'otp']);

	expect((await login(userName, password, undefined, undefined, { code })).status(), 'replayed code').toBe(401);
	expect((await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!, 1) })).status(), 'next step').toBe(200);
	expect((await login(userName, password, undefined, undefined, { code: '000000' })).status()).toBe(401);
});

test('enrolment: the secret is shown once, a wrong first code is refused, recovery codes are single use', async () => {
	const account = accounts().enrol;
	const session = await signIn(account);
	const authorization = { token: session.accessToken };

	const enrolled = await call('POST', enrolUrl, authorization);
	expect(enrolled.status()).toBe(200);
	const { secret, otpAuthUri } = await enrolled.json();
	expect(otpAuthUri).toBe(`otpauth://totp/Ariva:${account.userName}?secret=${secret}&issuer=Ariva&algorithm=SHA1&digits=6&period=30`);

	expect((await call('POST', confirmUrl, { ...authorization, data: { code: '000000' } })).status()).toBe(400);
	const confirmed = await call('POST', confirmUrl, { ...authorization, data: { code: totpCode(secret) } });
	expect(confirmed.status()).toBe(200);
	const { token, recoveryCodes } = await confirmed.json();
	expect(recoveryCodes).toHaveLength(10);
	expect(claimsOf(token.accessToken).amr).toEqual(['pwd', 'otp']);

	const again = await call('POST', enrolUrl, { token: token.accessToken });
	expect(again.status(), 'an enrolled account never gets its secret again').toBe(409);
	expect(await again.text()).not.toContain(secret);

	expect((await login(account.userName, account.password)).status()).toBe(401);
	const recovered = await login(account.userName, account.password, undefined, undefined, { recoveryCode: recoveryCodes[0] });
	expect(recovered.status()).toBe(200);
	expect((await recovered.json()).recoveryCodesRemaining).toBe(9);
	expect((await login(account.userName, account.password, undefined, undefined, { recoveryCode: recoveryCodes[0] })).status(), 'single use').toBe(401);

	const regenerated = await call('POST', recoveryUrl, { token: token.accessToken, data: { code: totpCode(secret, 1) } });
	expect(regenerated.status()).toBe(200);
	expect((await login(account.userName, account.password, undefined, undefined, { recoveryCode: recoveryCodes[1] })).status(), 'old codes retired').toBe(401);
});

test('break-glass: signs in with a recovery code, is never locked out, and every sign-in is a critical event', async () => {
	const credential = readBreakGlass();
	expect(credential.recoveryCodes).toHaveLength(10);

	expect((await login(credential.userName, credential.password)).status()).toBe(401);
	for (let attempt = 0; attempt < 12; attempt++) await login(credential.userName, 'not-the-password-0000');

	const signedIn = await login(credential.userName, credential.password, undefined, undefined, { recoveryCode: credential.recoveryCodes[0] });
	expect(signedIn.status(), 'never locked out').toBe(200);
	const userId = claimsOf((await signedIn.json()).accessToken).sub;

	// Enabling is a critical action (ARV-059), so the administrator has a fresh second factor: the answer is the account's absence.
	const { userName, password, totpSecret } = accounts().breakGlassAdmin;
	const admin = await (await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) })).json();
	expect((await call('POST', `${hosts.main}/api/v1/admin/users/${userId}/enable`, { token: admin.accessToken })).status(), 'no API re-enables it').toBe(404);

	const logDirectory = process.env.ARIVA_E2E_LOG_DIR;
	test.skip(!logDirectory || !fs.existsSync(path.join(logDirectory, 'Ariva.Api.Main.log')), 'host logs are written only by hosts this run started');
	await expect
		.poll(() => fs.readFileSync(path.join(logDirectory!, 'Ariva.Api.Main.log'), 'utf8').includes('BreakGlassSignIn'), { timeout: 10_000 })
		.toBe(true);
});
