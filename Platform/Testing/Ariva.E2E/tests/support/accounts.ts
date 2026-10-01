import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import { hosts } from './hosts';

// ARV-010a: the accounts the E2E run signs in with. Ariva.Api.Main creates them at startup from Auth:DevelopmentUsers
// (vm-local only; playwright.config.ts passes them as environment variables). The passwords are random per run:
// playwright.config.ts sets ARIVA_E2E_ACCOUNT_SEED once in the runner process and the workers inherit it, so no
// password is ever committed and global-teardown.ts can look for every one of them in the host logs.

const here = path.dirname(fileURLToPath(import.meta.url));

/** True when the run has a database (CI and `npm run dev:up`); sign-in needs one. */
export const databaseAvailable = process.env.ARIVA_E2E_SCHEMA_UPDATE === 'true';

/** Folder of the development token key the hosts share in this run, so tests can sign their own tokens. */
export const keyDirectory = process.env.ARIVA_E2E_KEY_DIR || path.resolve(here, '..', '..', '.e2e-keys');

/** Seconds an account stays locked in the E2E run (Auth:Lockout:DurationSeconds). */
export const lockoutSeconds = 5;

/** Failed sign-ins that lock an account (Auth:Lockout:Threshold, ADR-0026). */
export const lockoutThreshold = 10;

export type RoleCode = 'BorderShiftSupervisor' | 'TerminalDutyManager' | 'HandlerStationManager' | 'SystemAdministrator';

export interface Account {
	userName: string;
	password: string;
	roles: RoleCode[];
	temporary: boolean;
}

function seed(): string {
	const value = process.env.ARIVA_E2E_ACCOUNT_SEED;
	if (!value) throw new Error('ARIVA_E2E_ACCOUNT_SEED is not set; playwright.config.ts sets it before the workers start');
	return value;
}

function account(userName: string, roles: RoleCode[], temporary = false): Account {
	// Long, random and free of the username and the product name, so the password policy would accept it too.
	const password = crypto.createHash('sha256').update(`${seed()}|${userName}`).digest('base64url').slice(0, 24);
	return { userName, password, roles, temporary };
}

/** Every account, keyed by purpose. */
export function accounts() {
	return {
		BorderShiftSupervisor: account('e2e.border', ['BorderShiftSupervisor']),
		TerminalDutyManager: account('e2e.terminal', ['TerminalDutyManager']),
		HandlerStationManager: account('e2e.handler', ['HandlerStationManager']),
		SystemAdministrator: account('e2e.admin', ['SystemAdministrator']),
		pending: account('e2e.pending', [], true),
		changer: account('e2e.changer', [], true),
		lockout: account('e2e.lockout', []),
		unlock: account('e2e.unlock', [])
	} as const;
}

/** The password the change-password test sets on e2e.changer before it changes it back. */
export function changedPassword(): string {
	return crypto.createHash('sha256').update(`${seed()}|e2e.changer|next`).digest('base64url').slice(0, 24);
}

/** Auth__DevelopmentUsers__* variables for Ariva.Api.Main. */
export function developmentUserEnvironment(): Record<string, string> {
	const environment: Record<string, string> = {};
	Object.values(accounts()).forEach((entry, index) => {
		const prefix = `Auth__DevelopmentUsers__${index}__`;
		environment[`${prefix}UserName`] = entry.userName;
		environment[`${prefix}Password`] = entry.password;
		environment[`${prefix}Temporary`] = String(entry.temporary);
		entry.roles.forEach((role, roleIndex) => (environment[`${prefix}Roles__${roleIndex}`] = role));
	});
	return environment;
}

/**
 * A client address for X-Forwarded-For. The E2E hosts trust loopback as a proxy, so each test can have its own
 * address and the per-address sign-in limit (10 a minute) only applies where a test means it to.
 */
export function clientAddress(): string {
	const bytes = crypto.randomBytes(3);
	return `10.${bytes[0]}.${bytes[1]}.${Math.max(bytes[2], 1)}`;
}

export const loginUrl = `${hosts.main}/api/auth/login`;
export const changePasswordUrl = `${hosts.main}/api/auth/change-password`;
export const logoutUrl = `${hosts.main}/api/auth/logout`;

/** POST /api/auth/login from the given client address. */
export function login(request: APIRequestContext, userName: string, password: string, address = clientAddress()): Promise<APIResponse> {
	return request.post(loginUrl, { data: { userName, password }, headers: { 'X-Forwarded-For': address } });
}

export interface TokenResponse {
	accessToken: string;
	tokenType: string;
	expiresIn: number;
	scope: string | null;
}

/** Signs in and returns the token response; fails the test when sign-in fails. */
export async function signIn(request: APIRequestContext, entry: Account): Promise<TokenResponse> {
	const response = await login(request, entry.userName, entry.password);
	expect(response.status(), `sign-in of ${entry.userName}`).toBe(200);
	return (await response.json()) as TokenResponse;
}

/** The JSON payload of a compact JWS, without verifying it. */
export function claimsOf(token: string): Record<string, unknown> {
	return JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString('utf8'));
}

/** The JOSE header of a compact JWS. */
export function headerOf(token: string): Record<string, unknown> {
	return JSON.parse(Buffer.from(token.split('.')[0], 'base64url').toString('utf8'));
}

/** The development signing key of this run, or undefined when the hosts were started elsewhere (reused servers). */
export function developmentSigningKey(): crypto.KeyObject | undefined {
	const file = path.join(keyDirectory, 'token-signing-dev.key');
	return fs.existsSync(file) ? crypto.createPrivateKey(fs.readFileSync(file, 'utf8')) : undefined;
}

/** The kid Ariva derives from a public key: base64url of the first 16 bytes of SHA-256 over the SubjectPublicKeyInfo. */
export function keyIdOf(key: crypto.KeyObject): string {
	const spki = crypto.createPublicKey(key).export({ type: 'spki', format: 'der' });
	return crypto.createHash('sha256').update(spki).digest().subarray(0, 16).toString('base64url');
}

/** Builds a compact JWS signed with ES256 (or unsigned for alg none, or HMAC with the given secret). */
export function signToken(header: Record<string, unknown>, claims: Record<string, unknown>, key?: crypto.KeyObject, hmacSecret?: Buffer): string {
	const encode = (value: unknown) => Buffer.from(JSON.stringify(value)).toString('base64url');
	const input = `${encode(header)}.${encode(claims)}`;
	if (header.alg === 'none') return `${input}.`;
	if (hmacSecret) return `${input}.${crypto.createHmac('sha256', hmacSecret).update(input).digest('base64url')}`;
	if (!key) throw new Error('signToken needs a key for ES256');
	const signature = crypto.sign('sha256', Buffer.from(input), { key, dsaEncoding: 'ieee-p1363' });
	return `${input}.${signature.toString('base64url')}`;
}
