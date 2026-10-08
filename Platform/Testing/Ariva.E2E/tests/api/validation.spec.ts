import { expect, test } from '@playwright/test';
import { accounts, call, claimsOf, databaseAvailable, login, signIn, unusedTotpCode } from '../support/accounts';
import { expectApiSecurityHeaders, expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-104a: validation campaigns and manual count capture (formulas F18, wiki 07 section 8). Campaigns of a site live under
// api/v1/sites/{siteCode}/validation/campaigns (Validation.View to read, Validation.Manage to plan and start; closing is a
// critical action with a second factor, audited), and the observer's side under .../validation/capture/campaigns
// (Validation.Capture: running campaigns, one's own counts, a count per line and 15-minute bin, corrections as new
// revisions; an Idempotency-Key belongs to the observer who sent it; the account that created or started a campaign never
// counts for it, owner decision 2026-10-08). Administrators view and manage campaigns but never capture. The suite works at
// its own site E2EV (Asia/Dubai) with a zone profile it publishes once: two queue zones, an overflow band and their lines. This story has no screen of its own (the observer tablet is ARV-104c and ARV-104d,
// the campaign screens ARV-104h); functional/validation-observer.spec.ts checks only that the observer role reaches no
// operational screen.

test.skip(!databaseAvailable, 'validation needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const site = 'E2EV';
const admin = `${hosts.main}/api/v1/admin`;
const campaigns = (code = site) => `${hosts.main}/api/v1/sites/${code}/validation/campaigns`;
const capture = (code = site) => `${hosts.main}/api/v1/sites/${code}/validation/capture/campaigns`;
const quarter = 15 * 60_000;

/** A local date (yyyy-MM-dd) in Dubai, the site's time zone. */
const dubaiDay = (at: number) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(at));
/** The start of the quarter-hour bin that ended `back` bins ago (1 is the last one that has ended). */
const binStart = (back: number) => new Date(Math.floor(Date.now() / quarter) * quarter - back * quarter).toISOString().replace('.000Z', 'Z');

interface Profile {
	version: number;
	queueA: string;
	queueB: string;
	entryA: string;
	exitA: string;
	bandEntryA: string;
	entryB: string;
}

let manager: string, lead: string, observer: string, observer2: string, observerElsewhere: string, elsewhere: string, handler: string, administrator: string, dual: string;
let profile: Profile;

/** E2EV's published profile, drafted and published by the validation manager the first time (an existing one is reused). */
async function ensureProfile(): Promise<Profile> {
	const api = `${admin}/zone-profiles`;
	const history = await (await call('GET', `${api}?siteCode=${site}`, { token: manager })).json();
	let published = history.find((p: any) => p.status === 'Published');
	if (!published) {
		for (const old of history.filter((p: any) => p.status === 'Draft')) await call('DELETE', `${api}/${old.id}`, { token: administrator });
		const create = async (entity: string, data: unknown) => {
			const response = await call('POST', `${admin}/${entity}`, { token: administrator, data });
			expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
			return (await response.json()).id as string;
		};
		const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
		const iata = Array.from({ length: 3 }, () => letters[Math.floor(Math.random() * 26)]).join('');
		const airport = await create('airports', { iataCode: iata, name: `E2E validation ${iata}`, timeZoneId: 'Asia/Dubai' });
		const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'Terminal 1', siteCode: site });
		const levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });

		const draft = await call('POST', `${api}/drafts`, { token: manager, data: { siteCode: site, name: 'Validation hall' } });
		expect(draft.status(), await draft.text()).toBe(201);
		const id = (await draft.json()).profile.id as string;
		const zone = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/zones`, { token: manager, data: { levelId, ...data } });
			expect(response.status(), await response.text()).toBe(201);
			return (await response.json()).id as string;
		};
		const line = async (data: Record<string, unknown>) => {
			const response = await call('POST', `${api}/${id}/lines`, { token: manager, data: { levelId, ...data } });
			expect(response.status(), await response.text()).toBe(201);
		};
		const queueA = await zone({ name: 'Q-A', kind: 'Queue', polygon: '10 10,34 10,34 22,10 22' });
		const band = await zone({ name: 'Q-A band', kind: 'Overflow', polygon: '10 4,34 4,34 10,10 10', queueZoneId: queueA });
		const queueB = await zone({ name: 'Q-B', kind: 'Queue', polygon: '50 10,74 10,74 22,50 22' });
		await line({ name: 'Entry A', role: 'Entry', startX: 10, startY: 12, endX: 10, endY: 16, zoneId: queueA });
		await line({ name: 'Exit A', role: 'Exit', startX: 30, startY: 22, endX: 34, endY: 22, zoneId: queueA });
		await line({ name: 'Band entry A', role: 'OverflowEntry', startX: 10, startY: 5, endX: 10, endY: 9, zoneId: band });
		await line({ name: 'Entry B', role: 'Entry', startX: 50, startY: 12, endX: 50, endY: 16, zoneId: queueB });
		await line({ name: 'Exit B', role: 'Exit', startX: 70, startY: 22, endX: 74, endY: 22, zoneId: queueB });
		const review = await (await call('GET', `${api}/${id}/validation`, { token: manager })).json();
		expect(review.publishable, JSON.stringify(review)).toBe(true);
		const publish = await call('POST', `${api}/${id}/publish`, { token: manager, data: { geometryHash: review.geometryHash } });
		expect(publish.status(), await publish.text()).toBe(200);
		published = await publish.json();
	}

	const view = await (await call('GET', `${api}/${published.id}`, { token: manager })).json();
	const zoneId = (name: string) => view.zones.find((z: any) => z.name === name).id as string;
	const lineId = (name: string) => view.lines.find((l: any) => l.name === name).id as string;
	return {
		version: published.version,
		queueA: zoneId('Q-A'),
		queueB: zoneId('Q-B'),
		entryA: lineId('Entry A'),
		exitA: lineId('Exit A'),
		bandEntryA: lineId('Band entry A'),
		entryB: lineId('Entry B')
	};
}

/** A campaign request over Q-A and its three lines for yesterday and today (Dubai). */
function campaignRequest(name: string): Record<string, unknown> {
	return {
		name,
		profileVersion: profile.version,
		zoneIds: [profile.queueA],
		lineIds: [profile.entryA, profile.exitA, profile.bandEntryA],
		days: [dubaiDay(Date.now() - 86_400_000), dubaiDay(Date.now())],
		targetTracerRuns: 12
	};
}

async function plan(name: string, token = lead): Promise<any> {
	const created = await call('POST', campaigns(), { token, data: campaignRequest(name) });
	expect(created.status(), await created.text()).toBe(201);
	return created.json();
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().validationManager;
	const signedIn = await login(userName, password, undefined, undefined, { code: await unusedTotpCode(totpSecret!) });
	expect(signedIn.status(), 'the validation manager signs in with a second factor').toBe(200);
	manager = (await signedIn.json()).accessToken;
	[lead, observer, observer2, observerElsewhere, elsewhere, handler, administrator, dual] = await Promise.all(
		[
			accounts().validationLead,
			accounts().ValidationObserver,
			accounts().validationObserver2,
			accounts().validationObserverElsewhere,
			accounts().BorderShiftSupervisor,
			accounts().HandlerStationManager,
			accounts().SystemAdministrator,
			accounts().validationDual
		].map(async (a) => (await signIn(a)).accessToken)
	);
	profile = await ensureProfile();
});

test('a campaign is planned, started, counted, corrected and closed with a second factor, and audited', async () => {
	const name = `Pilot ${Date.now()}`;
	const campaign = await plan(name);
	expect(campaign).toMatchObject({ siteCode: site, name, status: 'Planned', profileVersion: profile.version, profileStatus: 'Published', timeZoneId: 'Asia/Dubai' });
	expect(campaign.targets).toEqual({ binsPerLine: 20, tracerRuns: 12, placeholder: true });
	expect(campaign.lines.map((l: any) => [l.name, l.role, l.queueZone])).toEqual([
		['Band entry A', 'OverflowEntry', 'Q-A'],
		['Entry A', 'Entry', 'Q-A'],
		['Exit A', 'Exit', 'Q-A']
	]);
	const id = campaign.id as string;

	// Planned: no capture yet; the observer does not list it.
	const early = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 3, crossingsOut: 0 } });
	expect(early.status(), 'capture before the start').toBe(409);
	expect((await (await call('GET', capture(), { token: observer })).json()).map((c: any) => c.id)).not.toContain(id);

	const started = await call('POST', `${campaigns()}/${id}/start`, { token: lead });
	expect(started.status(), await started.text()).toBe(200);
	expect((await started.json()).status).toBe('Running');
	expect((await call('POST', `${campaigns()}/${id}/start`, { token: lead })).status(), 'start twice').toBe(409);

	const listed = (await (await call('GET', capture(), { token: observer })).json()).find((c: any) => c.id === id);
	expect(listed).toMatchObject({ siteCode: site, name, timeZoneId: 'Asia/Dubai', binMinutes: 15 });
	expect(listed.lines.map((l: any) => l.id).sort()).toEqual([profile.entryA, profile.exitA, profile.bandEntryA].sort());
	expect(listed).not.toHaveProperty('targets');

	// One count per line, bin and observer; a second observer may count the same line and bin.
	const count = { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 42, crossingsOut: 3 };
	const first = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: count });
	expect(first.status(), await first.text()).toBe(201);
	const recorded = await first.json();
	expect(recorded).toMatchObject({ campaignId: id, lineName: 'Entry A', revision: 1, current: true, crossingsIn: 42, crossingsOut: 3, reason: null });
	expect((await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { ...count, crossingsIn: 50 } })).status(), 'the same line and bin again').toBe(409);
	expect((await call('POST', `${capture()}/${id}/counts`, { token: observer2, data: { ...count, crossingsIn: 41 } })).status(), 'a second observer').toBe(201);
	expect((await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { ...count, lineId: profile.entryB } })).status(), 'a line out of scope').toBe(400);
	expect((await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { ...count, binStartUtc: binStart(-1) } })).status(), 'a bin that has not ended').toBe(400);

	// A resent request with the same Idempotency-Key gets the stored count; the key with another body is refused.
	const keyed = { lineId: profile.exitA, binStartUtc: binStart(2), crossingsIn: 7, crossingsOut: 1 };
	const key = `e2e-${Date.now()}`;
	const sent = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: keyed, headers: { 'Idempotency-Key': key } });
	expect(sent.status(), await sent.text()).toBe(201);
	const resent = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: keyed, headers: { 'Idempotency-Key': key } });
	expect(resent.status(), 'the stored answer').toBe(200);
	expect((await resent.json()).id).toBe((await sent.json()).id);
	expect((await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { ...keyed, crossingsIn: 8 }, headers: { 'Idempotency-Key': key } })).status()).toBe(409);

	// A correction is the next revision with a reason; only the latest, and only one's own.
	const correct = (countId: string, token: string, data: Record<string, unknown>) => call('POST', `${capture()}/${id}/counts/${countId}/corrections`, { token, data });
	const corrected = await correct(recorded.id, observer, { crossingsIn: 44, crossingsOut: 3, reason: 'Two missed at the start' });
	expect(corrected.status(), await corrected.text()).toBe(201);
	const revision = await corrected.json();
	expect(revision).toMatchObject({ revision: 2, crossingsIn: 44, reason: 'Two missed at the start', correctsId: recorded.id, current: true });
	expect((await correct(recorded.id, observer, { crossingsIn: 45, crossingsOut: 3, reason: 'Again' })).status(), 'not the latest').toBe(409);
	expect((await correct(revision.id, observer2, { crossingsIn: 1, crossingsOut: 1, reason: 'Not mine' })).status(), "another observer's count").toBe(404);
	expect((await correct(revision.id, observer, { crossingsIn: 45, crossingsOut: 3 })).status(), 'a correction needs a reason').toBe(400);

	// The manager reads every observer's counts, current or every revision; an observer reads its own.
	const current = await (await call('GET', `${campaigns()}/${id}/counts?lineId=${profile.entryA}`, { token: lead })).json();
	expect(current.data.map((c: any) => [c.revision, c.crossingsIn]).sort()).toEqual([[1, 41], [2, 44]]);
	const all = await (await call('GET', `${campaigns()}/${id}/counts?lineId=${profile.entryA}&currentOnly=false&sortBy=recordedUtc`, { token: lead })).json();
	expect(all.totalCount).toBe(3);
	expect(all.data.find((c: any) => c.id === recorded.id).current).toBe(false);
	const own = await (await call('GET', `${capture()}/${id}/counts?currentOnly=false`, { token: observer2 })).json();
	expect(own.data).toHaveLength(1);
	expect((await (await call('GET', `${campaigns()}/${id}`, { token: lead })).json()).lines.find((l: any) => l.name === 'Entry A').binsCaptured).toBe(1);

	// Closing is critical: a password-only session is asked for its second factor; an observer may not.
	const withoutSecondFactor = await call('POST', `${campaigns()}/${id}/close`, { token: lead });
	expect(withoutSecondFactor.status()).toBe(401);
	expect(withoutSecondFactor.headers()['www-authenticate']).toContain('insufficient_user_authentication');
	expect((await withoutSecondFactor.json()).error).toBe('mfa_required');
	expect((await call('POST', `${campaigns()}/${id}/close`, { token: observer })).status()).toBe(403);
	const closed = await call('POST', `${campaigns()}/${id}/close`, { token: manager });
	expect(closed.status(), await closed.text()).toBe(200);
	expect((await closed.json()).status).toBe('Closed');

	// Nothing is captured or corrected after the close (409), and it cannot be closed or started again.
	expect((await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { ...count, lineId: profile.bandEntryA } })).status(), 'capture after close').toBe(409);
	expect((await correct(revision.id, observer, { crossingsIn: 46, crossingsOut: 3, reason: 'Late' })).status(), 'correction after close').toBe(409);
	expect((await call('POST', `${campaigns()}/${id}/close`, { token: manager })).status()).toBe(409);
	expect((await call('POST', `${campaigns()}/${id}/start`, { token: lead })).status()).toBe(409);

	// Audited: created, started and closed by the lead and the manager, and the correction.
	const audit = await (await call('GET', `${admin}/audit-entries?targetId=${id}&pageSize=50`, { token: administrator })).json();
	expect(audit.data.map((e: any) => e.action).sort()).toEqual(['ValidationCampaign.Closed', 'ValidationCampaign.Created', 'ValidationCampaign.Started']);
	const correction = await (await call('GET', `${admin}/audit-entries?targetId=${revision.id}`, { token: administrator })).json();
	expect(correction.data.map((e: any) => e.action)).toEqual(['ManualCount.Corrected']);
});

test('each role reaches only its validation endpoints at the site', async () => {
	const campaign = await plan(`Roles ${Date.now()}`);
	const id = campaign.id as string;
	const bodyCount = { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 1, crossingsOut: 0 };
	const rows: [string, string, unknown?][] = [
		['GET', campaigns()],
		['POST', campaigns(), campaignRequest('Matrix')],
		['GET', `${campaigns()}/${id}`],
		['POST', `${campaigns()}/${id}/start`],
		['POST', `${campaigns()}/${id}/close`],
		['GET', `${campaigns()}/${id}/counts`],
		['GET', capture()],
		['GET', `${capture()}/${id}/counts`],
		['POST', `${capture()}/${id}/counts`, bodyCount],
		['POST', `${capture()}/${id}/counts/${id}/corrections`, { crossingsIn: 1, crossingsOut: 0, reason: 'x' }]
	];
	const statuses = async (token: string) => {
		const answers: number[] = [];
		for (const [method, url, data] of rows) answers.push((await call(method, url, { token, data })).status());
		return answers;
	};

	// The handler holds no validation permission; the observer only Capture, the lead (duty manager) View and Manage (its close
	// asks for a second factor). Every refused call is 403, before the campaign or the body is looked at.
	expect(await statuses(handler)).toEqual([403, 403, 403, 403, 403, 403, 403, 403, 403, 403]);
	expect(await statuses(observer)).toEqual([403, 403, 403, 403, 403, 403, 200, 200, 409, 404]);
	const leadStatuses = await statuses(lead);
	expect(leadStatuses.slice(0, 3)).toEqual([200, 201, 200]);
	expect(leadStatuses.slice(4)).toEqual([401, 200, 403, 403, 403, 403]);
	expect(leadStatuses[3], 'start').toBe(200);
	// The administrator views and manages (the campaign already runs: 409; its close asks for a second factor) and never
	// captures: the ground truth stays independent of whoever configures the system (owner decision 2026-10-08).
	expect(await statuses(administrator)).toEqual([200, 201, 200, 409, 401, 200, 403, 403, 403, 403]);
});

test('an Idempotency-Key belongs to the observer who sent it, for counts and corrections', async () => {
	// CWE-863: another observer sending the same key gets a count of its own (201), never the first observer's stored count
	// (200) nor a refusal (409); the replay looks among the caller's own counts only.
	const campaign = await plan(`Keys ${Date.now()}`);
	const id = campaign.id as string;
	expect((await call('POST', `${campaigns()}/${id}/start`, { token: lead })).status()).toBe(200);
	const [firstId, secondId] = [claimsOf(observer).sub, claimsOf(observer2).sub];
	const stamp = Date.now();
	const exitKey = `e2e-exit-${stamp}`;
	const entryKey = `e2e-entry-${stamp}`;
	const send = (token: string, data: Record<string, unknown>, key: string) =>
		call('POST', `${capture()}/${id}/counts`, { token, data, headers: { 'Idempotency-Key': key } });
	const exit = { lineId: profile.exitA, binStartUtc: binStart(1), crossingsIn: 7, crossingsOut: 1 };
	const entry = { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 5, crossingsOut: 0 };

	const firstExit = await send(observer, exit, exitKey);
	expect(firstExit.status(), await firstExit.text()).toBe(201);
	const firstExitCount = await firstExit.json();
	const firstEntry = await send(observer, entry, entryKey);
	expect(firstEntry.status(), await firstEntry.text()).toBe(201);
	const firstEntryCount = await firstEntry.json();

	const sameBody = await send(observer2, exit, exitKey);
	expect(sameBody.status(), 'the same key and body from another observer').toBe(201);
	const sameBodyCount = await sameBody.json();
	expect(sameBodyCount).toMatchObject({ observerId: secondId, revision: 1, crossingsIn: 7 });
	expect(sameBodyCount.id).not.toBe(firstExitCount.id);
	const otherBody = await send(observer2, { ...entry, crossingsIn: 9, crossingsOut: 2 }, entryKey);
	expect(otherBody.status(), 'the same key with another body from another observer').toBe(201);
	const otherBodyCount = await otherBody.json();
	expect(otherBodyCount).toMatchObject({ observerId: secondId, crossingsIn: 9, crossingsOut: 2 });

	// Each observer's resend still returns its own stored count.
	const firstResent = await send(observer, exit, exitKey);
	expect(firstResent.status()).toBe(200);
	expect(await firstResent.json()).toMatchObject({ id: firstExitCount.id, observerId: firstId });
	const secondResent = await send(observer2, exit, exitKey);
	expect(secondResent.status()).toBe(200);
	expect((await secondResent.json()).id).toBe(sameBodyCount.id);

	// The same two cases for a correction key.
	const correct = (token: string, countId: string, data: Record<string, unknown>, key: string) =>
		call('POST', `${capture()}/${id}/counts/${countId}/corrections`, { token, data, headers: { 'Idempotency-Key': key } });
	const fixKey = `e2e-fix-${stamp}`;
	const fixKey2 = `e2e-fix2-${stamp}`;
	const recount = { crossingsIn: 8, crossingsOut: 1, reason: 'Recount' };
	const firstFix = await correct(observer, firstExitCount.id, recount, fixKey);
	expect(firstFix.status(), await firstFix.text()).toBe(201);
	const firstFixCount = await firstFix.json();
	expect((await correct(observer, firstEntryCount.id, { crossingsIn: 6, crossingsOut: 0, reason: 'Missed one' }, fixKey2)).status()).toBe(201);

	const secondFix = await correct(observer2, sameBodyCount.id, recount, fixKey);
	expect(secondFix.status(), 'the same correction key and body from another observer').toBe(201);
	const secondFixCount = await secondFix.json();
	expect(secondFixCount).toMatchObject({ observerId: secondId, revision: 2, correctsId: sameBodyCount.id });
	expect(secondFixCount.id).not.toBe(firstFixCount.id);
	const secondFix2 = await correct(observer2, otherBodyCount.id, { crossingsIn: 10, crossingsOut: 2, reason: 'Two more' }, fixKey2);
	expect(secondFix2.status(), 'the same correction key with another body from another observer').toBe(201);
	expect(await secondFix2.json()).toMatchObject({ observerId: secondId, crossingsIn: 10, correctsId: otherBodyCount.id });
	const firstFixResent = await correct(observer, firstExitCount.id, recount, fixKey);
	expect(firstFixResent.status()).toBe(200);
	expect((await firstFixResent.json()).id).toBe(firstFixCount.id);
});

test('the account that created or started a campaign never counts for it, even with the observer role', async () => {
	// Owner decision 2026-10-08 (separation of duties): e2e.valdual holds Terminal duty manager and Validation observer.
	const count = { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 3, crossingsOut: 0 };
	const countIn = (id: string, token: string, key?: string) =>
		call('POST', `${capture()}/${id}/counts`, { token, data: count, headers: key ? { 'Idempotency-Key': key } : undefined });

	const created = (await plan(`Dual created ${Date.now()}`, dual)).id as string;
	expect((await call('POST', `${campaigns()}/${created}/start`, { token: lead })).status()).toBe(200);
	const refused = await countIn(created, dual);
	expect(refused.status(), 'its creator').toBe(403);
	expect((await refused.json()).detail).toContain('created or started');
	expect((await countIn(created, dual, `e2e-dual-${Date.now()}`)).status(), 'its creator, with a key').toBe(403);

	const started = (await plan(`Dual started ${Date.now()}`)).id as string;
	expect((await call('POST', `${campaigns()}/${started}/start`, { token: dual })).status()).toBe(200);
	expect((await countIn(started, dual)).status(), 'its starter').toBe(403);

	// Another manager's campaign is one it may count for; the observer counts for all three.
	const another = (await plan(`Dual other ${Date.now()}`)).id as string;
	expect((await call('POST', `${campaigns()}/${another}/start`, { token: lead })).status()).toBe(200);
	expect((await countIn(another, dual)).status(), "another manager's campaign").toBe(201);
	for (const id of [created, started, another]) expect((await countIn(id, observer)).status()).toBe(201);
	for (const id of [created, started]) {
		const stored = await (await call('GET', `${campaigns()}/${id}/counts?observerId=${String(claimsOf(dual).sub)}`, { token: lead })).json();
		expect(stored.totalCount, 'nothing of its own campaigns was stored').toBe(0);
	}
});

test('another site answers 404, through its own route and the campaign site', async () => {
	const campaign = await plan(`Cross-site ${Date.now()}`);
	const id = campaign.id as string;
	expect((await call('POST', `${campaigns()}/${id}/start`, { token: lead })).status()).toBe(200);
	const count = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 9, crossingsOut: 0 } });
	expect(count.status(), await count.text()).toBe(201);
	const countId = (await count.json()).id as string;

	for (const code of [site, 'E2E1']) {
		// A border shift supervisor of E2E1 holds View and Manage, an observer of E2E1 holds Capture: neither reaches E2EV.
		for (const [method, url] of [
			['GET', `${campaigns(code)}/${id}`],
			['GET', `${campaigns(code)}/${id}/counts`],
			['POST', `${campaigns(code)}/${id}/start`]
		])
			expect((await call(method, url, { token: elsewhere })).status(), `${method} ${url}`).toBe(404);
		expect((await call('POST', `${capture(code)}/${id}/counts`, { token: observerElsewhere, data: { lineId: profile.exitA, binStartUtc: binStart(1), crossingsIn: 1, crossingsOut: 0 } })).status()).toBe(404);
		expect((await call('POST', `${capture(code)}/${id}/counts/${countId}/corrections`, { token: observerElsewhere, data: { crossingsIn: 1, crossingsOut: 0, reason: 'x' } })).status()).toBe(404);
		expect((await call('GET', `${capture(code)}/${id}/counts`, { token: observerElsewhere })).status()).toBe(404);
	}

	expect((await call('GET', campaigns(), { token: elsewhere })).status(), "E2EV's list").toBe(404);
	expect((await call('GET', capture(), { token: observerElsewhere })).status()).toBe(404);
	expect((await call('POST', campaigns(), { token: elsewhere, data: campaignRequest('Elsewhere') })).status()).toBe(404);
	const theirs = await (await call('GET', campaigns('E2E1'), { token: elsewhere })).json();
	expect(theirs.data.map((c: any) => c.id)).not.toContain(id);
	const unchanged = await (await call('GET', `${campaigns()}/${id}/counts`, { token: lead })).json();
	expect(unchanged.totalCount, 'nothing was written from another site').toBe(1);
});

test('SQL injection and markup are refused with 400 or stored as inert text, never echoed or executed', async () => {
	// Markup and SQL in a name are text: stored as given and returned as a JSON string value with nosniff and the API's
	// default-src 'none' policy (as alert notes and calendar reasons are), never rendered by the API, found by a
	// parameterised search. The screens that show campaign names (ARV-104h) interpolate text only.
	for (const name of [xssPayloads[0], xssPayloads[1], sqlInjectionPayloads[0], sqlInjectionPayloads[2]]) {
		const created = await call('POST', campaigns(), { token: lead, data: campaignRequest(name) });
		expect(created.status(), name).toBe(201);
		expect(created.headers()['content-type']).toContain('application/json');
		expectApiSecurityHeaders(created);
		expect((await created.json()).name).toBe(name);
	}

	const search = await call('GET', `${campaigns()}?text=${encodeURIComponent(sqlInjectionPayloads[0])}`, { token: lead });
	expect(search.status()).toBe(200);
	expect((await search.json()).data.every((c: any) => c.name.includes(sqlInjectionPayloads[0]))).toBe(true);
	for (const query of [
		`sortBy=${encodeURIComponent(sqlInjectionPayloads[1])}`,
		`sortBy=${encodeURIComponent('name; DROP TABLE site')}`,
		`status=${encodeURIComponent("Running' OR '1'='1")}`,
		`status=${encodeURIComponent(xssPayloads[0])}`,
		`text=${encodeURIComponent('bidi \u202e override')}`,
		`fromDate=${encodeURIComponent(sqlInjectionPayloads[4])}`,
		`pageSize=${encodeURIComponent(sqlInjectionPayloads[0])}`
	]) {
		const refused = await call('GET', `${campaigns()}?${query}`, { token: lead });
		expect(refused.status(), query).toBe(400);
		const body = await refused.text();
		for (const fragment of markupFragments) expect(body, query).not.toContain(fragment);
		expect(body, query).not.toContain('DROP TABLE');
	}

	// Request bodies: refused with the rule, never the value.
	const campaign = await plan(`Payloads ${Date.now()}`);
	const id = campaign.id as string;
	expect((await call('POST', `${campaigns()}/${id}/start`, { token: lead })).status()).toBe(200);
	const base = { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 1, crossingsOut: 0 };
	const refusedBodies: [string, unknown][] = [
		['create: SQL in a day', { ...campaignRequest('x'), days: [`${dubaiDay(Date.now())}' OR '1'='1`] }],
		['create: markup in a day', { ...campaignRequest('x'), days: [xssPayloads[0]] }],
		['create: a zone id that is SQL', { ...campaignRequest('x'), zoneIds: [sqlInjectionPayloads[0]] }],
		['create: a bidi override in the name', campaignRequest('bidi \u202e override')],
		['create: a name over 200 characters', campaignRequest('N'.repeat(201))],
		['capture: SQL as the bin', { ...base, binStartUtc: sqlInjectionPayloads[0] }],
		['capture: markup as the bin', { ...base, binStartUtc: xssPayloads[1] }],
		['capture: a bin with an offset', { ...base, binStartUtc: binStart(1).replace('Z', '+04:00') }],
		['capture: a line id that is markup', { ...base, lineId: xssPayloads[0] }],
		['capture: SQL as a count', { ...base, crossingsIn: sqlInjectionPayloads[0] }],
		['capture: a negative count', { ...base, crossingsIn: -1 }],
		['capture: no counts', { lineId: profile.entryA, binStartUtc: binStart(1) }]
	];
	for (const [what, data] of refusedBodies) {
		const url = what.startsWith('create') ? campaigns() : `${capture()}/${id}/counts`;
		const refused = await call('POST', url, { token: what.startsWith('create') ? lead : observer, data });
		expect(refused.status(), what).toBe(400);
		const body = await refused.text();
		for (const fragment of [...markupFragments, "OR '1'='1", 'DROP TABLE']) expect(body, what).not.toContain(fragment);
	}

	// An Idempotency-Key that is not a key is refused; markup in a correction's reason is inert text.
	for (const key of [sqlInjectionPayloads[0], xssPayloads[2], 'short', 'k'.repeat(65)]) {
		const refused = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: base, headers: { 'Idempotency-Key': key } });
		expect(refused.status(), key).toBe(400);
		expectNoLeak(await refused.text(), key);
	}

	const first = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: base });
	expect(first.status(), await first.text()).toBe(201);
	const reason = `${xssPayloads[1]} ${sqlInjectionPayloads[3]}`;
	const corrected = await call('POST', `${capture()}/${id}/counts/${(await first.json()).id}/corrections`, { token: observer, data: { crossingsIn: 2, crossingsOut: 0, reason } });
	expect(corrected.status(), await corrected.text()).toBe(201);
	expect(corrected.headers()['content-type']).toContain('application/json');
	expectApiSecurityHeaders(corrected);
	expect((await corrected.json()).reason).toBe(reason);

	// Route values that are not a site or an id.
	expect((await call('GET', `${hosts.main}/api/v1/sites/${encodeURIComponent("E2EV' OR '1'='1")}/validation/campaigns`, { token: lead })).status()).toBe(404);
	expect((await call('GET', `${campaigns()}/${encodeURIComponent(sqlInjectionPayloads[0])}`, { token: lead })).status()).toBe(404);
	for (const query of [`lineId=${encodeURIComponent(sqlInjectionPayloads[0])}`, `sortBy=${encodeURIComponent(xssPayloads[0])}`, `toDate=${encodeURIComponent(xssPayloads[3])}`]) {
		const refused = await call('GET', `${campaigns()}/${id}/counts?${query}`, { token: lead });
		expect(refused.status(), query).toBe(400);
		for (const fragment of markupFragments) expect(await refused.text(), query).not.toContain(fragment);
	}
});

test('a body over the limit is refused with 413', async () => {
	const campaign = await plan(`Limits ${Date.now()}`);
	const id = campaign.id as string;
	const json = { 'Content-Type': 'application/json' };
	// A campaign is at most 32 KB and a count or correction 4 KB.
	const oversizedCampaign = await call('POST', campaigns(), { token: lead, raw: ' '.repeat(33 * 1024) + JSON.stringify(campaignRequest('Big')), headers: json });
	expect(oversizedCampaign.status()).toBe(413);
	const oversizedCount = await call('POST', `${capture()}/${id}/counts`, {
		token: observer,
		raw: ' '.repeat(5 * 1024) + JSON.stringify({ lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 1, crossingsOut: 0 }),
		headers: json
	});
	expect(oversizedCount.status()).toBe(413);
	const oversizedCorrection = await call('POST', `${capture()}/${id}/counts/${id}/corrections`, {
		token: observer,
		raw: JSON.stringify({ crossingsIn: 1, crossingsOut: 0, reason: 'r'.repeat(5 * 1024) }),
		headers: json
	});
	expect(oversizedCorrection.status()).toBe(413);
	// Under the limit, a long name is still refused by its own rule (200 characters).
	expect((await call('POST', campaigns(), { token: lead, data: campaignRequest('n'.repeat(500)) })).status()).toBe(400);
});
