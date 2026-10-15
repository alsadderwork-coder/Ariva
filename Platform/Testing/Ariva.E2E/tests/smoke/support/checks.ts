import type { Locator } from '@playwright/test';
import { send, target } from './client';
import { criticalPermissionCodes, smokeRole, smokeRolePermissions } from './role';
import { credential, credentialVariables, variable } from './target';

// ARV-128: what the smoke specs check beyond a status code, kept here so a spec needs nothing but ./support/.

export { criticalPermissionCodes, smokeRole, smokeRolePermissions };

/** The signed-in user as GET /api/auth/me answers. */
export interface CurrentUser {
	roles: string[];
	permissions: string[];
	allSites: boolean;
	sites: string[];
	pending: boolean;
}

/** The smoke site or sites: ARIVA_SMOKE_SITE, one site code or several separated by commas. */
export function smokeSites(): string[] {
	const codes = credential(credentialVariables.site)
		.split(',')
		.map((code) => code.trim())
		.filter(Boolean);
	// Site.IsValidCode: 2 to 8 upper case letters or digits, optionally a hyphen and 1 to 8 more.
	if (codes.length === 0 || codes.some((code) => !/^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/.test(code))) {
		throw new Error(`ARV-128: ${credentialVariables.site} holds site codes separated by commas`);
	}
	return [...new Set(codes)];
}

/**
 * Why the smoke suite may not use this account, or none: it must hold the smoke role alone, exactly that role's
 * permissions and none of the critical ones, have finished its first sign-in, and hold exactly the configured smoke sites,
 * never every site and no other site. The server keeps no account of this role from every site, so this check (and the
 * administrator's care, wiki 04 section 8.1) is what keeps a smoke credential to the smoke sites.
 */
export function accountProblems(user: CurrentUser, sites: string[]): string[] {
	const problems: string[] = [];
	if (user.pending) problems.push('the account has not finished its first sign-in (new password or authenticator)');
	if (user.roles.length !== 1 || user.roles[0] !== smokeRole) problems.push(`the account must hold the ${smokeRole} role alone; it holds ${user.roles.join(', ') || 'none'}`);
	const expected = new Set<string>(smokeRolePermissions);
	const held = new Set(user.permissions);
	const extra = [...held].filter((code) => !expected.has(code));
	const missing = [...expected].filter((code) => !held.has(code));
	if (extra.length > 0) problems.push(`permissions beyond the ${smokeRole} role: ${extra.join(', ')}`);
	if (missing.length > 0) problems.push(`permissions of the ${smokeRole} role missing: ${missing.join(', ')}`);
	const critical = criticalPermissionCodes().filter((code) => held.has(code));
	if (critical.length > 0) problems.push(`critical permissions: ${critical.join(', ')}`);
	if (user.allSites) problems.push('the account has every site; give it the smoke site or sites only');
	const configured = new Set(sites);
	const others = user.sites.filter((code) => !configured.has(code));
	const absent = sites.filter((code) => !user.sites.includes(code));
	if (others.length > 0) problems.push(`the account holds other sites: ${others.join(', ')}`);
	if (absent.length > 0) problems.push(`the account does not hold the smoke site: ${absent.join(', ')}`);
	return problems;
}

/** The queue zone to join: ARIVA_SMOKE_ZONE, or the first queue zone of the first smoke site's published profile. */
export async function smokeZone(token: string): Promise<string> {
	const configured = variable(credentialVariables.zone);
	if (configured) return configured;
	const site = smokeSites()[0];
	const profiles = await send('GET', `${target.base}/api/v1/admin/zone-profiles?siteCode=${encodeURIComponent(site)}`, { token });
	if (profiles.status !== 200) throw new Error(`the site's zone profiles answered ${profiles.status}`);
	const published = profiles.json<{ id: string; status: string }[]>().find((p) => p.status === 'Published');
	if (!published) throw new Error(`the smoke site has no published zone profile; publish one or set ${credentialVariables.zone}`);
	const profile = await send('GET', `${target.base}/api/v1/admin/zone-profiles/${encodeURIComponent(published.id)}`, { token });
	if (profile.status !== 200) throw new Error(`the published zone profile answered ${profile.status}`);
	const zone = profile.json<{ zones: { name: string; kind: string }[] }>().zones.find((z) => z.kind === 'Queue');
	if (!zone) throw new Error(`the smoke site's published profile has no queue zone; set ${credentialVariables.zone}`);
	return zone.name;
}

/** Yesterday as a UTC date: a day the daily report accepts at a site in any time zone (not after the site's today). */
export function yesterdayUtc(): string {
	return new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
}

/**
 * Types a value into a field without it entering a step title. Playwright 1.63 titles fill and type steps with the text
 * ('Fill "{value}"') in the report; an evaluate step is titled "Evaluate" only. The input event is what Svelte's
 * bind:value listens to.
 */
export async function enter(field: Locator, value: string): Promise<void> {
	await field.waitFor({ state: 'visible' });
	await field.evaluate((element, text) => {
		const input = element as HTMLInputElement;
		input.focus();
		input.value = text;
		input.dispatchEvent(new Event('input', { bubbles: true }));
		input.dispatchEvent(new Event('change', { bubbles: true }));
	}, value);
}
