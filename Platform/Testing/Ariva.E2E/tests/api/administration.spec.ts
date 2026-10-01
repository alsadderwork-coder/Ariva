import { expect, test } from '@playwright/test';
import { accounts, call, claimsOf, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-011: user, role and audit administration. Creating users, resets and role changes are critical actions, so the
// administrator signs in with its TOTP code (e2e.secadmin). Escalation attempts (self-grant, a non-administrator
// granting, step-up hints for callers without the permission) return 403; every change leaves an audit entry, and no
// API changes one.

test.skip(!databaseAvailable, 'administration needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const usersUrl = `${hosts.main}/api/v1/admin/users`;
const auditUrl = `${hosts.main}/api/v1/admin/audit-entries`;
const userName = `e2e.created.${Date.now().toString(36)}`;

let admin: string;
let userId: string;
let temporaryPassword: string;

test.beforeAll(async () => {
	const { userName: name, password, totpSecret } = accounts().securityAdmin;
	const response = await login(name, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(response.status(), 'administrator sign-in with TOTP').toBe(200);
	admin = (await response.json()).accessToken;
	expect(claimsOf(admin).amr).toEqual(['pwd', 'otp']);
});

test('an administrator creates a user with a one-time temporary password', async () => {
	const response = await call('POST', usersUrl, { token: admin, data: { userName, displayName: 'Created Officer', roles: ['BorderShiftSupervisor'] } });
	expect(response.status()).toBe(201);
	expect(response.headers()['cache-control']).toContain('no-store');
	const body = await response.json();
	userId = body.user.id;
	temporaryPassword = body.temporaryPassword;
	expect(body.user.roles).toEqual(['BorderShiftSupervisor']);
	expect(body.user.mustChangePassword).toBe(true);
	expect(temporaryPassword).toMatch(/^[A-Za-z2-9]{5}(-[A-Za-z2-9]{5}){3}$/);

	expect((await call('POST', usersUrl, { token: admin, data: { userName } })).status(), 'duplicate').toBe(409);
	expect((await call('POST', usersUrl, { token: admin, data: { userName: "<script>alert(1)</script>" } })).status(), 'markup as a username').toBe(400);
	expect((await call('POST', usersUrl, { token: admin, data: { userName: "x' OR '1'='1" } })).status(), 'SQL in a username').toBe(400);

	const signedIn = await login(userName, temporaryPassword);
	expect((await signedIn.json()).scope).toBe('pending');
	const view = await call('GET', `${usersUrl}/${userId}`, { token: admin });
	const text = await view.text();
	expect(text).not.toContain(temporaryPassword);
	expect(text).not.toMatch(/passwordHash|salt|totpSecret/i);
});

test('privilege escalation attempts return 403', async () => {
	const border = await signIn(accounts().BorderShiftSupervisor);
	const self = claimsOf(admin).sub as string;

	expect((await call('PUT', `${usersUrl}/${self}/roles/BorderShiftSupervisor`, { token: admin })).status(), 'self-grant').toBe(403);
	expect((await call('DELETE', `${usersUrl}/${self}/roles/SystemAdministrator`, { token: admin })).status(), 'self-revoke').toBe(403);
	expect((await call('PUT', `${usersUrl}/${userId}/roles/SystemAdministrator`, { token: border.accessToken })).status(), 'grant by a supervisor').toBe(403);
	expect((await call('POST', usersUrl, { token: border.accessToken, data: { userName: 'e2e.sneaky' } })).status(), 'create by a supervisor, no step-up hint').toBe(403);
	expect((await call('POST', `${usersUrl}/${self}/reset-totp`, { token: admin })).status(), 'own TOTP reset through the admin path').toBe(403);
	expect((await call('PUT', `${usersUrl}/${userId}/roles/Root`, { token: admin })).status(), 'unknown role').toBe(400);
});

test('grants, revokes and resets work for a stepped-up administrator and are audited', async () => {
	const granted = await call('PUT', `${usersUrl}/${userId}/roles/TerminalDutyManager`, { token: admin });
	expect(granted.status()).toBe(200);
	expect((await granted.json()).roles).toEqual(['BorderShiftSupervisor', 'TerminalDutyManager']);

	expect((await call('DELETE', `${usersUrl}/${userId}/roles/TerminalDutyManager`, { token: admin })).status()).toBe(200);
	const reset = await call('POST', `${usersUrl}/${userId}/reset-password`, { token: admin });
	expect(reset.status()).toBe(200);
	const next = (await reset.json()).temporaryPassword;
	expect((await login(userName, temporaryPassword)).status(), 'old temporary password').toBe(401);
	expect((await login(userName, next)).status()).toBe(200);
	expect((await call('POST', `${usersUrl}/${userId}/reset-totp`, { token: admin })).status()).toBe(204);

	const audit = await call('GET', `${auditUrl}?targetId=${userId}`, { token: admin });
	expect(audit.status()).toBe(200);
	const entries = (await audit.json()).data as { id: string; action: string; actorName: string }[];
	expect(entries.map((entry) => entry.action)).toEqual(['User.TotpReset', 'User.PasswordReset', 'User.RoleRevoked', 'User.RoleGranted', 'User.Created']);
	expect(entries.every((entry) => entry.actorName === accounts().securityAdmin.userName)).toBe(true);
	expect(JSON.stringify(entries)).not.toContain(next);

	const entry = `${auditUrl}/${entries[0].id}`;
	expect((await call('GET', entry, { token: admin })).status()).toBe(200);
	for (const method of ['PUT', 'PATCH', 'DELETE', 'POST']) {
		const status = (await call(method, method === 'POST' ? auditUrl : entry, { token: admin, data: { action: 'x' } })).status();
		expect(status, `${method} on the audit trail`).toBeGreaterThanOrEqual(400);
	}
});

test('search filters by text and role and refuses sort fields outside the allowlist', async () => {
	const found = await call('GET', `${usersUrl}?text=${userName}&role=BorderShiftSupervisor`, { token: admin });
	expect((await found.json()).data.map((user: { userName: string }) => user.userName)).toEqual([userName]);
	expect((await call('GET', `${usersUrl}?sortBy=password_hash`, { token: admin })).status()).toBe(400);
	expect((await call('GET', `${usersUrl}?pageSize=100000`, { token: admin })).status()).toBe(400);
	const roles = await call('GET', `${hosts.main}/api/v1/admin/roles`, { token: admin });
	expect((await roles.json()).map((role: { code: string }) => role.code)).toEqual(['BorderShiftSupervisor', 'TerminalDutyManager', 'HandlerStationManager', 'SystemAdministrator']);
});
