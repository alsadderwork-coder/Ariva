import { expect } from '@playwright/test';
import { type Account, call, login, unusedTotpCode } from './accounts';
import { hosts } from './hosts';

// ARV-071: a site of its own for a load run (api/load.spec.ts) or for the screen watched under load
// (functional/live-under-load.spec.ts): an airport, a terminal, a level and a published zone profile with one queue zone
// and its entry and exit lines. Each run gets a fresh site code, so suites and reruns never share a zone.

const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
export const randomLetters = (n: number) => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');

export interface LoadSite {
	site: string;
	zone: string;
	levelId: string;
	/** The administrator's access token (TOTP signed in). */
	token: string;
	/** POST to the admin API, expecting 201; returns the body. */
	create: (resource: string, data: unknown) => Promise<any>;
}

export async function provisionLoadSite(admin: Account, prefix: string, zone = 'Load Zone'): Promise<LoadSite> {
	const base = `${hosts.main}/api/v1/admin`;
	const signIn = await login(admin.userName, admin.password, undefined, undefined, { code: await unusedTotpCode(admin.totpSecret!) });
	expect(signIn.status(), 'administrator sign-in').toBe(200);
	const token = (await signIn.json()).accessToken as string;
	const create = async (resource: string, data: unknown) => {
		const response = await call('POST', `${base}/${resource}`, { token, data });
		expect(response.status(), `${resource}: ${await response.text()}`).toBe(201);
		return response.json();
	};
	const site = `${prefix}${randomLetters(4)}`;
	expect((await call('POST', `${base}/sites`, { token, data: { code: site, name: site } })).status()).toBe(201);
	const airport = (await create('airports', { iataCode: randomLetters(3), name: `E2E load ${site}`, timeZoneId: 'Asia/Dubai' })).id;
	const terminal = (await create('terminals', { airportId: airport, code: 'T1', name: 'T1', siteCode: site })).id;
	const levelId = (await create('levels', { terminalId: terminal, code: 'L0', name: 'L0', floorNumber: 0, widthMetres: 100, depthMetres: 50 })).id;
	const created = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals' });
	const draft = created.id ?? created.profile?.id;
	const zoneCreated = await create(`zone-profiles/${draft}/zones`, { name: zone, kind: 'Queue', levelId, polygon: '10 10,34 10,34 22,10 22' });
	const zoneId = zoneCreated.id ?? zoneCreated.profile?.id;
	await create(`zone-profiles/${draft}/lines`, { name: 'Entry L', role: 'Entry', levelId, startX: 10, startY: 12, endX: 10, endY: 16, zoneId });
	await create(`zone-profiles/${draft}/lines`, { name: 'Exit L', role: 'Exit', levelId, startX: 30, startY: 22, endX: 34, endY: 22, zoneId });
	const { geometryHash } = await (await call('GET', `${base}/zone-profiles/${draft}/validation`, { token })).json();
	expect((await call('POST', `${base}/zone-profiles/${draft}/publish`, { token, data: { geometryHash } })).status()).toBe(200);
	return { site, zone, levelId, token, create };
}

/** Access tokens for screens: one sign-in per few screens (the hub holds at most 8 connections per session), each from its own address. */
export async function screenSessions(account: Account, screens: number, perSession = 5): Promise<string[]> {
	const tokens: string[] = [];
	for (let i = 0; i < Math.ceil(screens / perSession); i++) {
		const response = await login(account.userName, account.password);
		expect(response.status(), 'screen sign-in').toBe(200);
		tokens.push((await response.json()).accessToken);
	}
	return tokens;
}
