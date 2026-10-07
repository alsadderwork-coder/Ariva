import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect } from '@playwright/test';
import type { ResponseLike } from './api-assertions';
import { hosts, webOrigin } from './hosts';

// ARV-010a: the accounts the E2E run signs in with. Ariva.Api.Main creates them at startup from Auth:DevelopmentUsers
// (vm-local only; playwright.config.ts passes them as environment variables). The passwords are random per run:
// playwright.config.ts sets ARIVA_E2E_ACCOUNT_SEED once in the runner process and the workers inherit it, so no
// password is ever committed and global-teardown.ts can look for every one of them in the host logs.

const here = path.dirname(fileURLToPath(import.meta.url));

/** True when the run has a database (CI and `npm run dev:up`); sign-in needs one. */
export const databaseAvailable = process.env.ARIVA_E2E_SCHEMA_UPDATE === 'true';

/** Folder of the development token key the hosts share in this run, so tests can sign their own tokens. */
export const keyDirectory = process.env.ARIVA_E2E_KEY_DIR || path.resolve(here, '..', '..', '.e2e-keys');

/** Where the global setup writes the break-glass credential the installer command prints (ARV-010c). */
export const breakGlassFile = path.resolve(here, '..', '..', '.e2e-keys', 'break-glass.txt');
/** Integration client TOTP seeds received in the run (ARV-042), one per line: the log scan looks for each one. */
export const integrationSeedsFile = path.resolve(here, '..', '..', '.e2e-keys', 'integration-seeds.txt');

/** Seconds a used refresh token still returns its successor in the E2E run (Auth:Sessions:RefreshGraceSeconds). */
export const refreshGraceSeconds = 3;

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
	/** Base32 TOTP secret when the account is seeded with TOTP enrolled. */
	totpSecret?: string;
	/** Site codes the account may access ('*' for every site); the seed creates missing sites (ARV-012). */
	sites: string[];
}

function seed(): string {
	const value = process.env.ARIVA_E2E_ACCOUNT_SEED;
	if (!value) throw new Error('ARIVA_E2E_ACCOUNT_SEED is not set; playwright.config.ts sets it before the workers start');
	return value;
}

function account(userName: string, roles: RoleCode[], temporary = false, withTotp = false, sites: string[] = []): Account {
	// Long, random and free of the username and the product name, so the password policy would accept it too.
	const password = crypto.createHash('sha256').update(`${seed()}|${userName}`).digest('base64url').slice(0, 24);
	const totpSecret = withTotp ? base32(crypto.createHash('sha256').update(`${seed()}|${userName}|totp`).digest().subarray(0, 20)) : undefined;
	return { userName, password, roles, temporary, totpSecret, sites };
}

/** RFC 4648 base32 without padding. */
export function base32(bytes: Uint8Array): string {
	const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
	let bits = 0;
	let value = 0;
	let output = '';
	for (const byte of bytes) {
		value = (value << 8) | byte;
		bits += 8;
		while (bits >= 5) {
			output += alphabet[(value >>> (bits - 5)) & 31];
			bits -= 5;
		}
	}
	if (bits > 0) output += alphabet[(value << (5 - bits)) & 31];
	return output;
}

function fromBase32(text: string): Buffer {
	const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
	let bits = 0;
	let value = 0;
	const output: number[] = [];
	for (const char of text.replace(/=+$/, '').toUpperCase()) {
		value = (value << 5) | alphabet.indexOf(char);
		bits += 5;
		if (bits >= 8) {
			output.push((value >>> (bits - 8)) & 0xff);
			bits -= 8;
		}
	}
	return Buffer.from(output);
}

/** The RFC 6238 code (HMAC-SHA1, 6 digits, 30 seconds) for a base32 secret, offset in steps from now. */
export function totpCode(secretBase32: string, offsetSteps = 0, at = Date.now()): string {
	const counter = Buffer.alloc(8);
	counter.writeBigInt64BE(BigInt(Math.floor(at / 1000 / 30) + offsetSteps));
	const hash = crypto.createHmac('sha1', fromBase32(secretBase32)).update(counter).digest();
	const offset = hash[hash.length - 1] & 0x0f;
	const binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
	return String(binary % 1_000_000).padStart(6, '0');
}

/**
 * A code the server has not yet accepted in this run. The server takes a step only once and only after the last one it
 * took (CWE-287 replay guard), allowing one step of skew; a sign-in repeated within a step (`--repeat-each`) therefore
 * uses the next step, and waits for the step boundary only when that would be two steps ahead. Playwright starts a
 * new worker process per test here, so the last step is kept in a file named by a hash of the run seed and the secret
 * (the secret itself is never written).
 */
export async function unusedTotpCode(secretBase32: string): Promise<string> {
	const name = crypto.createHash('sha256').update(`${process.env.ARIVA_E2E_ACCOUNT_SEED ?? ''}:${secretBase32}`).digest('hex');
	// Kept in the suite's own output folder (git-ignored), owner-only, never following a planted link.
	const dir = path.join(here, '..', '..', 'test-results', '.totp');
	fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
	const file = path.join(dir, name.slice(0, 32));
	const stored = fs.existsSync(file) && !fs.lstatSync(file).isSymbolicLink() ? Number(fs.readFileSync(file, 'utf8')) : Number.NaN;
	const now = Math.floor(Date.now() / 1000 / 30);
	// A stored step more than two steps ahead of the clock is not ours to wait for: ignore it.
	const last = Number.isInteger(stored) && stored <= now + 2 ? stored : Number.NaN;
	const step = Number.isFinite(last) ? Math.max(now, last + 1) : now;
	if (step > now + 1) await new Promise((resolve) => setTimeout(resolve, (step - 1) * 30_000 - Date.now() + 100));
	if (fs.existsSync(file)) fs.rmSync(file);
	fs.writeFileSync(file, String(step), { mode: 0o600, flag: 'wx' });
	return totpCode(secretBase32, 0, step * 30_000);
}

/** Every account, keyed by purpose. */
export function accounts() {
	return {
		BorderShiftSupervisor: account('e2e.border', ['BorderShiftSupervisor'], false, false, ['E2E1']),
		TerminalDutyManager: account('e2e.terminal', ['TerminalDutyManager'], false, false, ['E2E2']),
		HandlerStationManager: account('e2e.handler', ['HandlerStationManager'], false, false, ['E2E1', 'E2E2']),
		SystemAdministrator: account('e2e.admin', ['SystemAdministrator'], false, false, ['*']),
		pending: account('e2e.pending', [], true),
		changer: account('e2e.changer', [], true),
		lockout: account('e2e.lockout', []),
		unlock: account('e2e.unlock', []),
		session: account('e2e.session', ['TerminalDutyManager']),
		disabled: account('e2e.disabled', ['BorderShiftSupervisor']),
		totp: account('e2e.totp', ['TerminalDutyManager'], false, true),
		enrol: account('e2e.enrol', ['HandlerStationManager']),
		stepUp: account('e2e.stepup', ['BorderShiftSupervisor'], false, true),
		// Holds every critical permission, so each critical route answers 401 for its second factor and never 403.
		stepUpAdmin: account('e2e.stepupadmin', ['SystemAdministrator'], false, true, ['*']),
		// The device suites sign in with a fresh second factor in parallel; a TOTP code counts once per account (replay
		// guard), so each suite has its own administrator rather than sharing stepUpAdmin.
		devicesAdmin: account('e2e.devicesadmin', ['SystemAdministrator'], false, true, ['*']),
		deviceAuthAdmin: account('e2e.devauthadmin', ['SystemAdministrator'], false, true, ['*']),
		devicePushAdmin: account('e2e.devpushadmin', ['SystemAdministrator'], false, true, ['*']),
		deviceHealthAdmin: account('e2e.devhealthadmin', ['SystemAdministrator'], false, true, ['*']),
		emulatorAdmin: account('e2e.emuadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-017: a duty manager who drafts and publishes zone profiles for E2E2 (publishing needs a second factor).
		zoneManager: account('e2e.zonemanager', ['TerminalDutyManager'], false, true, ['E2E2']),
		securityAdmin: account('e2e.secadmin', ['SystemAdministrator'], false, true, ['*']),
		siteAdmin: account('e2e.siteadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-037: deleting an alert rule is a critical action, so the alert rule suite signs in with a second factor.
		alertAdmin: account('e2e.alertadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-039: one person per operational role at the demo airport, where the seeded rules raise their alerts.
		dmoBorder: account('e2e.dmoborder', ['BorderShiftSupervisor'], false, false, ['DMO']),
		dmoTerminal: account('e2e.dmoterminal', ['TerminalDutyManager'], false, false, ['DMO']),
		dmoHandler: account('e2e.dmohandler', ['HandlerStationManager'], false, false, ['DMO']),
		siteUser: account('e2e.siteuser', ['BorderShiftSupervisor'], false, false, ['E2E1']),
		// ARV-040: creates the email rule and sets the DMO people's addresses (neither is a critical action).
		emailAdmin: account('e2e.emailadmin', ['SystemAdministrator'], false, false, ['*']),
		// ARV-042: registering and changing integration clients are critical actions, so this suite signs in with a second factor.
		integrationAdmin: account('e2e.intadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-043: its own account, so its sign-in code never collides with integration-auth.spec.ts's in the same TOTP step (replay guard).
		feedAdmin: account('e2e.feedadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-044: the same, for aidx.spec.ts.
		aidxAdmin: account('e2e.aidxadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-045: outbound endpoints are critical actions too.
		outboundAdmin: account('e2e.outadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-029: registers the emulated AODB's and AMAN's integration clients (critical actions).
		emulatorFeedAdmin: account('e2e.emufeedadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-048: registers the immigration clients of aman-feed.spec.ts.
		amanFeedAdmin: account('e2e.amanfeedadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-047: registers the clients that feed arrival-wave.spec.ts.
		arrivalWaveAdmin: account('e2e.arrivalwaveadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-050: registers the AMAN pull endpoints of aman-pull.spec.ts (critical actions).
		amanPullAdmin: account('e2e.amanpulladmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-071: provisions the load run's site, devices and displays.
		loadAdmin: account('e2e.loadadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-071: the load run's screens, signed in once per few screens (a hub session holds at most 8 connections).
		loadScreen: account('e2e.loadscreen', ['TerminalDutyManager'], false, false, ['*']),
		// ARV-071: provisions the site of the functional screen-under-load test (apart from loadAdmin: a TOTP code is accepted once).
		loadWebAdmin: account('e2e.loadwebadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-075: the visual baselines' display player (created through the admin API).
		visualAdmin: account('e2e.visualadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-063: the dynamic scan's caller. Password only, so every critical action answers 401 for its second factor and
		// the scan cannot carry one out; the OpenAPI documents need SystemInfo.View.
		zapAdmin: account('e2e.zapadmin', ['SystemAdministrator'], false, false, ['*']),
		// ARV-064: registers, calibrates and loads the scripted demo's devices (registering needs a second factor).
		demoAdmin: account('e2e.demoadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-051: the web app's sign-in. The shell suites sign in as a duty manager in parallel (password only, so no
		// replay guard); each TOTP flow has its own account, because a code counts once per account.
		web: account('e2e.web', ['TerminalDutyManager'], false, false, ['DMO']),
		webHandler: account('e2e.webhandler', ['HandlerStationManager'], false, false, ['DMO']),
		webAdmin: account('e2e.webadmin', ['SystemAdministrator'], false, false, ['*']),
		webFirst: account('e2e.webfirst', [], true),
		webTotp: account('e2e.webtotp', ['BorderShiftSupervisor'], false, true, ['DMO']),
		webStepUp: account('e2e.webstepup', ['TerminalDutyManager'], false, true, ['DMO']),
		webExpiry: account('e2e.webexpiry', ['TerminalDutyManager'], false, true, ['DMO']),
		webCancel: account('e2e.webcancel', ['TerminalDutyManager'], false, true, ['DMO']),
		// ARV-052: a border supervisor at the demo airport, for the read-only screens.
		webBorder: account('e2e.webborder', ['BorderShiftSupervisor'], false, false, ['DMO']),
		// ARV-053: draws and publishes zones at a site of its own (publishing needs a second factor).
		webZones: account('e2e.webzones', ['TerminalDutyManager'], false, true, ['E2EZ']),
		// ARV-054: registers devices at the demo airport through the screen (registering needs a second factor).
		webDevices: account('e2e.webdevices', ['TerminalDutyManager'], false, true, ['DMO']),
		// ARV-056: writes alert rules at the demo airport through the screen (deleting needs a second factor).
		webRules: account('e2e.webrules', ['BorderShiftSupervisor'], false, true, ['DMO']),
		// ARV-058: sets up passenger displays at the demo airport through the screen (creating needs a second factor).
		webDisplays: account('e2e.webdisplays', ['TerminalDutyManager'], false, true, ['DMO']),
		// ARV-059: administers accounts through the Users and access screen (creating, resets and role grants need a second
		// factor); the second is limited to the demo airport, to show that the screen and the server keep it there.
		webUsers: account('e2e.webusers', ['SystemAdministrator'], false, true, ['*']),
		webSiteAdmin: account('e2e.websiteadmin', ['SystemAdministrator'], false, true, ['DMO']),
		// ARV-059: enabling an account is a critical action, so the session suite re-enables with a second factor.
		sessionAdmin: account('e2e.sessionadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-059: the break-glass test asks enable with a fresh second factor, so the answer is the account's absence.
		breakGlassAdmin: account('e2e.bgadmin', ['SystemAdministrator'], false, true, ['*']),
		// ARV-060: the scheduled report's recipients at the demo airport (their own addresses, so no other suite's mail mixes in).
		reportBorder: account('e2e.reportborder', ['BorderShiftSupervisor'], false, false, ['DMO']),
		reportTerminal: account('e2e.reportterminal', ['TerminalDutyManager'], false, false, ['DMO']),
		// ARV-118: an administrator of one site (E2E3, created by the seed, no airport: UTC) who keeps its operating calendar.
		calendarAdmin: account('e2e.calendaradmin', ['SystemAdministrator'], false, false, ['E2E3'])
	} as const;
}

/** The password the change-password test sets on e2e.changer before it changes it back. */
export function changedPassword(): string {
	return crypto.createHash('sha256').update(`${seed()}|e2e.changer|next`).digest('base64url').slice(0, 24);
}

/** The password the web first-login test chooses for e2e.webfirst (ARV-051). */
export function webFirstPassword(): string {
	return crypto.createHash('sha256').update(`${seed()}|e2e.webfirst|chosen`).digest('base64url').slice(0, 24);
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
		if (entry.totpSecret) environment[`${prefix}TotpSecret`] = entry.totpSecret;
		entry.sites.forEach((site, siteIndex) => (environment[`${prefix}Sites__${siteIndex}`] = site));
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
export const refreshUrl = `${hosts.main}/api/auth/refresh`;
export const changePasswordUrl = `${hosts.main}/api/auth/change-password`;
export const logoutUrl = `${hosts.main}/api/auth/logout`;

export const refreshCookieName = '__Secure-ariva_rt';

/**
 * A response from {@link call}. Auth calls go through Node's fetch, which has no cookie jar: Playwright's request
 * context keeps the refresh cookie (it sends Secure cookies to localhost) and would then send it with every later
 * sign-in and logout, which revokes the session it belongs to. Tests pass the cookie explicitly instead.
 */
export interface CallResponse extends ResponseLike {
	json(): Promise<any>;
	/** The value of the refresh cookie this response sets, '' when it clears it, undefined when it does not touch it. */
	refreshCookie(): string | undefined;
	/** The Set-Cookie header for the refresh cookie, as sent. */
	refreshSetCookie(): string | undefined;
}

export interface CallOptions {
	data?: unknown;
	/** A multipart body (file uploads); fetch sets the boundary. */
	form?: FormData;
	/** A body sent as it is, with the Content-Type given in headers (device pushes, ARV-023). */
	raw?: string;
	token?: string;
	cookie?: string;
	csrf?: boolean;
	origin?: string;
	address?: string;
	headers?: Record<string, string>;
}

/** One HTTP call without a cookie jar; every call has its own client address unless one is given. */
export async function call(method: string, url: string, options: CallOptions = {}): Promise<CallResponse> {
	const headers: Record<string, string> = { 'X-Forwarded-For': options.address ?? clientAddress(), ...options.headers };
	if (options.data !== undefined) headers['Content-Type'] = 'application/json';
	if (options.token) headers.Authorization = `Bearer ${options.token}`;
	if (options.cookie) headers.Cookie = `${refreshCookieName}=${options.cookie}`;
	if (options.csrf) headers['X-Ariva-Csrf'] = '1';
	if (options.origin) headers.Origin = options.origin;

	const response = await fetch(url, { method, headers, body: options.form ?? options.raw ?? (options.data === undefined ? undefined : JSON.stringify(options.data)) });
	const text = await response.text();
	const setCookie = response.headers.getSetCookie().find((value) => value.startsWith(`${refreshCookieName}=`));
	const lowered: Record<string, string> = {};
	response.headers.forEach((value, name) => (lowered[name.toLowerCase()] = value));
	return {
		status: () => response.status,
		headers: () => lowered,
		text: async () => text,
		json: async () => JSON.parse(text),
		url: () => url,
		refreshSetCookie: () => setCookie,
		refreshCookie: () => (setCookie === undefined ? undefined : setCookie.slice(refreshCookieName.length + 1).split(';')[0])
	};
}

/** POST /api/auth/login from the given client address, optionally with a refresh cookie the browser still holds. */
export function login(userName: string, password: string, address = clientAddress(), cookie?: string, second?: { code?: string; recoveryCode?: string }): Promise<CallResponse> {
	return call('POST', loginUrl, { data: { userName, password, ...second }, address, cookie });
}

/** POST /api/auth/refresh as the web app sends it: the cookie, X-Ariva-Csrf and the web origin. */
export function refresh(cookie: string): Promise<CallResponse> {
	return call('POST', refreshUrl, { cookie, csrf: true, origin: webOrigin });
}

export interface TokenResponse {
	accessToken: string;
	tokenType: string;
	expiresIn: number;
	scope: string | null;
	/** The refresh cookie value from Set-Cookie (never in the body). */
	refreshToken: string;
}

/** Signs in and returns the token response and the refresh cookie; fails the test when sign-in fails. */
export async function signIn(entry: Account): Promise<TokenResponse> {
	const response = await login(entry.userName, entry.password);
	expect(response.status(), `sign-in of ${entry.userName}`).toBe(200);
	return { ...(await response.json()), refreshToken: response.refreshCookie() } as TokenResponse;
}

/** Polls until the access token is refused (session revocation reaches every node within 5 seconds). */
export async function refusedWithin(token: string, url: string, milliseconds = 6000): Promise<number> {
	const started = Date.now();
	for (;;) {
		const status = (await call('GET', url, { token })).status();
		if (status === 401) return Date.now() - started;
		if (Date.now() - started > milliseconds) return -1;
		await new Promise((resolve) => setTimeout(resolve, 250));
	}
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
