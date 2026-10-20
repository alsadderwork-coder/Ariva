import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, signIn, unusedTotpCode, type Account } from '../support/accounts';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';
import { expectStatus } from '../support/expect-status';

// ARV-145: a site's advertising policy through the API, at the illustrative site AUH-TA (no other spec sets advertising
// there or relies on it being off; functional/advertising-policy.spec.ts uses DMO's screen, not its policy). Advertising is
// off until a system administrator with a second factor allows it, with a reason the audit trail keeps with who and when;
// a display is set to advertising only where the policy allows every zone it shows, and its board falls back to
// instructions on the next refresh once it does not; another site's caller gets 404, roles without the permission 403.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const site = 'AUH-TA';
const policyUrl = (code = site) => `${hosts.main}/api/v1/admin/sites/${code}/advertising-policy`;
const displaysApi = `${hosts.main}/api/v1/admin/displays`;
const reason = 'Airport operator letter AO-E2E-145, terminal manager';
let stepped: string;
let passwordOnly: string;
let manager: string;
const created: string[] = [];
let display: { id: string; code: string; credential: string; request: Record<string, unknown> };

async function secondFactor(account: Account): Promise<string> {
	const signedIn = await login(account.userName, account.password, undefined, undefined, { code: await unusedTotpCode(account.totpSecret!) });
	expect(signedIn.status(), `sign-in of ${account.userName}`).toBe(200);
	return (await signedIn.json()).accessToken;
}

function displayRequest(zone: string, secondRegion: string, code: string) {
	return {
		siteCode: site, code, name: 'Arrivals hall', location: null, orientation: 'Landscape', languages: ['en', 'ar'], bandMinutes: 5, hysteresisMinutes: 1,
		staleSeconds: 150, entries: [{ zone, labels: { en: 'Visitors', ar: 'الزوار' } }], fallback: { en: 'Please follow the signs', ar: 'يرجى اتباع اللوحات' },
		enabled: true, layout: 'Split', queueShare: 60, secondRegion
	};
}

const newCode = () => `ADV-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;

/** A change as the screen sends it: naming the published zone profile the policy was reviewed against, unless the test sets it. */
async function change(data: Record<string, unknown>, token = stepped) {
	const reviewedProfileId =
		'reviewedProfileId' in data ? data.reviewedProfileId : (await (await call('GET', policyUrl(), { token: stepped })).json()).publishedProfileId;
	return call('PUT', policyUrl(), { token, data: { ...data, reviewedProfileId } });
}

async function boardRegion(): Promise<string> {
	const response = await call('GET', `${hosts.main}/api/v1/display/board?code=${display.code}`, { headers: { 'X-Ariva-Display-Key': display.credential } });
	expect(response.status()).toBe(200);
	return (await response.json()).secondRegion;
}

test.beforeAll(async () => {
	stepped = await secondFactor(accounts().advertisingAdmin);
	passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;
	manager = (await signIn(accounts().advertisingManager)).accessToken;
	// The demo seeds run in the background after Api.Main starts; AUH-TA follows DMO.
	await expect
		.poll(async () => (await call('GET', `${hosts.main}/api/v1/sites/${site}`, { token: stepped })).status(), {
			timeout: 90_000, intervals: [1000], message: 'the illustrative seed has created AUH-TA'
		})
		.toBe(200);
	// A run before this one may have left it on: every run starts from off.
	expect((await change({ allowed: false, excludedZones: [], reason: 'E2E run starts with advertising off' })).status()).toBe(200);
});

test.afterAll(async () => {
	for (const id of created) await call('DELETE', `${displaysApi}/${id}`, { token: stepped });
	await change({ allowed: false, excludedZones: [], reason: 'E2E run ends with advertising off' });
});

test('advertising is off until allowed: a display cannot be set to advertising, and a duty manager can see why', async () => {
	const read = await call('GET', policyUrl(), { token: manager });
	await expectStatus(read, 200);
	expect(await read.json()).toMatchObject({ siteCode: site, allowed: false, excludedZones: [] });

	const refused = await call('POST', displaysApi, { token: stepped, data: displayRequest('A-VIS', 'Advertising', newCode()) });
	expect(refused.status()).toBe(400);
	expect(await refused.text()).toContain("Advertising is off for this display's site or for one of its zones");

	const code = newCode();
	const request = displayRequest('A-VIS', 'Instructions', code);
	const made = await call('POST', displaysApi, { token: stepped, data: request });
	await expectStatus(made, 201);
	const issued = await made.json();
	created.push(issued.display.id);
	display = { id: issued.display.id, code, credential: issued.credential, request };
	const edited = await call('PUT', `${displaysApi}/${display.id}`, { token: manager, data: { ...request, secondRegion: 'Advertising' } });
	expect(edited.status(), 'the duty manager cannot set it either').toBe(400);
	expect(await boardRegion()).toBe('Instructions');
});

test('only a system administrator with a recent second factor changes it, with a reason the audit trail keeps', async () => {
	const body = { allowed: true, excludedZones: ['A-CIT'], reason };
	const unstepped = await change(body, passwordOnly);
	expect(unstepped.status(), 'a password alone is not enough').toBe(401);
	expect((await unstepped.json()).error).toBe('mfa_required');
	expect((await change(body, manager)).status(), 'a duty manager sees it and never changes it').toBe(403);

	const allowed = await change(body);
	await expectStatus(allowed, 200);
	const saved = await allowed.json();
	expect(saved).toMatchObject({ siteCode: site, allowed: true, excludedZones: ['A-CIT'], reason, unpublishedZones: [], profileChanged: false });
	expect(saved.publishedProfileVersion, 'the seed published a profile').toBeGreaterThan(0);
	expect(saved.confirmedProfileVersion, 'saved against the profile it reviewed').toBe(saved.publishedProfileVersion);

	// An allowing change names the profile it was reviewed against: another (or none) answers 409 and changes nothing.
	for (const reviewedProfileId of ['0199a000-0000-7000-8000-00000000a1a1', null]) {
		const stale = await change({ allowed: true, excludedZones: [], reason, reviewedProfileId });
		expect(stale.status(), `reviewed ${reviewedProfileId}`).toBe(409);
		expect(await stale.text()).toContain('Another zone profile has been published since you reviewed this policy');
	}
	expect(await (await call('GET', policyUrl(), { token: stepped })).json(), 'nothing was kept').toMatchObject({ excludedZones: ['A-CIT'] });
	expect(await (await call('GET', policyUrl(), { token: manager })).json()).toMatchObject({ allowed: true, excludedZones: ['A-CIT'], reason });

	// Who (the actor), when (occurredOn) and why (the reason), before and after.
	const audit = await call('GET', `${hosts.main}/api/v1/admin/audit-entries?action=AdvertisingPolicy.Changed&pageSize=50`, { token: stepped });
	expect(audit.status()).toBe(200);
	const entry = ((await audit.json()).data as { actorName: string; targetName: string; occurredOn: string; beforeSummary: string; afterSummary: string }[])
		.find((e) => e.targetName === site && e.afterSummary?.includes('AO-E2E-145'));
	expect(entry, 'the change is audited').toBeDefined();
	expect(entry!).toMatchObject({ actorName: accounts().advertisingAdmin.userName });
	expect(entry!.beforeSummary).toContain('"allowed":false');
	expect(entry!.afterSummary).toContain('"allowed":true');
	expect(Date.now() - Date.parse(entry!.occurredOn)).toBeLessThan(10 * 60_000);
});

test('changes outside the rules are refused without an echo, and leave the policy as it was', async () => {
	const refused: [string, Record<string, unknown>][] = [
		['no reason', { allowed: true, excludedZones: [] }],
		['a reason of 9 characters', { allowed: true, excludedZones: [], reason: 'Approved.' }],
		['a reason of 501 characters', { allowed: true, excludedZones: [], reason: 'x'.repeat(501) }],
		['a line break in the reason', { allowed: true, excludedZones: [], reason: 'Approved by the\noperator' }],
		['a bidirectional override in the reason', { allowed: true, excludedZones: [], reason: 'Approved \u202Eby the operator' }],
		['a zone that is not published', { allowed: true, excludedZones: ['NOT-A-ZONE'], reason }],
		['an overflow band', { allowed: true, excludedZones: ['A-VIS-OV'], reason }],
		['a zone twice', { allowed: true, excludedZones: ['A-CIT', 'A-CIT'], reason }],
		['21 zones', { allowed: true, excludedZones: Array.from({ length: 21 }, (_, i) => `Z-${i}`), reason }],
		['markup as a zone', { allowed: true, excludedZones: [xssPayloads[0]], reason }],
		['SQL as a zone', { allowed: true, excludedZones: [sqlInjectionPayloads[0]], reason }],
		['a setting given as text', { allowed: 'yes', excludedZones: [], reason }]
	];
	for (const [label, data] of refused) {
		const response = await change(data);
		expect(response.status(), label).toBe(400);
		const answer = JSON.stringify(await response.json());
		for (const fragment of [...markupFragments, 'alert(1)', 'NOT-A-ZONE']) expect(answer, `${label}: no echo of ${fragment}`).not.toContain(fragment);
	}
	expect(await (await call('GET', policyUrl(), { token: stepped })).json(), 'nothing refused was kept').toMatchObject({ allowed: true, excludedZones: ['A-CIT'], reason });

	// Markup in a reason is text: kept and returned as JSON text, never interpreted (the screen shows it as text).
	const markup = await change({ allowed: true, excludedZones: ['A-CIT'], reason: `${xssPayloads[0]} approved` });
	expect(markup.status()).toBe(200);
	expect(markup.headers()['content-type']).toContain('application/json');
	expect((await markup.json()).reason).toBe(`${xssPayloads[0]} approved`);
	expect((await change({ allowed: true, excludedZones: ['A-CIT'], reason })).status()).toBe(200);
});

test('a display shows advertising only where every zone it shows is allowed, and its board falls back on the next refresh', async () => {
	test.skip(!display, 'needs the display of the first test');
	const advertising = await call('PUT', `${displaysApi}/${display.id}`, { token: manager, data: { ...display.request, secondRegion: 'Advertising' } });
	await expectStatus(advertising, 200);
	expect((await advertising.json()).secondRegion).toBe('Advertising');
	expect(await boardRegion()).toBe('Advertising');

	const excludedZone = await call('POST', displaysApi, { token: stepped, data: displayRequest('A-CIT', 'Advertising', newCode()) });
	expect(excludedZone.status(), 'a display showing an excluded zone').toBe(400);
	const movedIn = await call('PUT', `${displaysApi}/${display.id}`, {
		token: manager,
		data: { ...display.request, secondRegion: 'Advertising', entries: [{ zone: 'A-CIT', labels: { en: 'Citizens', ar: 'المواطنون' } }] }
	});
	expect(movedIn.status(), 'nor can an advertising display take an excluded zone').toBe(400);

	const excluded = await change({ allowed: true, excludedZones: ['A-CIT', 'A-VIS'], reason });
	expect((await excluded.json()).displaysFallingBack).toContain(display.code);
	expect(await boardRegion(), 'its zone excluded').toBe('Instructions');
	expect((await change({ allowed: true, excludedZones: ['A-CIT'], reason })).status()).toBe(200);
	expect(await boardRegion()).toBe('Advertising');

	const off = await change({ allowed: false, excludedZones: ['A-CIT'], reason: 'Border authority withdrew approval, ref BA-E2E-9' });
	expect(off.status()).toBe(200);
	expect((await off.json()).displaysFallingBack).toContain(display.code);
	expect(await boardRegion(), 'advertising off').toBe('Instructions');
	const kept = await call('GET', `${displaysApi}/${display.id}`, { token: manager });
	expect((await kept.json()).secondRegion, 'the display keeps its setting').toBe('Advertising');
	const renamed = await call('PUT', `${displaysApi}/${display.id}`, { token: manager, data: { ...display.request, name: 'Renamed', secondRegion: 'Advertising' } });
	expect(renamed.status(), 'saved only with instructions while off').toBe(400);
});

test("another site's caller gets 404 without data, roles without the permission 403, attack payloads never reach SQL", async () => {
	const elsewhere = (await signIn(accounts().dmoTerminal)).accessToken;
	const foreign = await call('GET', policyUrl(), { token: elsewhere });
	expect(foreign.status(), 'a site outside the caller\'s sites').toBe(404);
	expect(await foreign.text()).not.toContain('BA-E2E-9');
	const handler = (await signIn(accounts().webHandler)).accessToken;
	const observer = (await signIn(accounts().ValidationObserver)).accessToken;
	for (const token of [handler, observer]) expect((await call('GET', policyUrl('DMO'), { token })).status()).toBe(403);
	expect((await call('GET', policyUrl())).status(), 'anonymous').toBe(401);
	expect((await call('PUT', policyUrl(), { data: { allowed: true, excludedZones: [], reason } })).status(), 'anonymous').toBe(401);

	for (const payload of [...sqlInjectionPayloads, ...xssPayloads, 'D'.repeat(300), 'auh-ta']) {
		for (const method of ['GET', 'PUT'] as const) {
			const response = await call(method, policyUrl(encodeURIComponent(payload)), {
				token: stepped,
				data: method === 'PUT' ? { allowed: true, excludedZones: [], reason } : undefined
			});
			expect(response.status(), `${method} ${payload.slice(0, 30)}`).toBeLessThan(500);
			expect([400, 404], `${method} ${payload.slice(0, 30)}`).toContain(response.status());
			const text = await response.text();
			for (const fragment of markupFragments) expect(text, fragment).not.toContain(fragment);
			expect(text).not.toContain('UNION');
		}
	}
	expect((await call('GET', policyUrl(), { token: stepped })).status(), 'the table is still there').toBe(200);
});
