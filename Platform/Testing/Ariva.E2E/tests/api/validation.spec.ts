import { expect, test } from '@playwright/test';
import { accounts, call, type CallResponse, claimsOf, databaseAvailable, login, signIn, unusedTotpCode } from '../support/accounts';
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
//
// ARV-104b (API only: the tracer and desk screens are ARV-104c and ARV-104d): tracer runs under .../tracer-runs (a batch
// with the device's clock reading, offset measured and applied, refused beyond 5 minutes) and desk observer logs under
// .../desk-observations (15-minute batches of per-minute states, corrections as revisions), both with a required
// Idempotency-Key per observer, the same permissions, role, site scope and separation of duties as manual counts. Desk
// states are border data: a terminal duty manager (the lead) neither puts desks in scope nor reads their states. The suite
// adds an immigration checkpoint VIMM with desks VD01 to VD04 and an e-gate, and a check-in checkpoint VCHK with a counter.
// After the first security review: e2e.valdual (a duty manager who also holds the observer role) sees no desk and gets 403
// on desk batches, corrections and its own desk reads; absurd device times (year 1, year 9999) are 400, never 500.

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

interface Desks {
	vd01: string;
	vd02: string;
	vd03: string;
	vd04: string;
	eGate: string;
	counter: string;
}

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
let desks: Desks;

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

/** E2EV's validation desks (ARV-104b), created by the administrator the first time: VIMM with VD01 to VD04 and VEG1, VCHK with VC01. */
async function ensureDesks(): Promise<Desks> {
	const list = async (entity: string, query: string) => {
		const response = await call('GET', `${admin}/${entity}?${query}`, { token: administrator });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(200);
		const body = await response.json();
		return (body.data ?? body) as any[];
	};
	const create = async (entity: string, data: unknown) => {
		const response = await call('POST', `${admin}/${entity}`, { token: administrator, data });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
		return response.json();
	};
	const checkpoints = await list('checkpoints', `siteCode=${site}&pageSize=100`);
	const level = (await list('levels', `siteCode=${site}&pageSize=10`))[0];
	const border = checkpoints.find((c) => c.code === 'VIMM') ?? (await create('checkpoints', { levelId: level.id, code: 'VIMM', name: 'Validation immigration', kind: 'Immigration' }));
	const airportSide = checkpoints.find((c) => c.code === 'VCHK') ?? (await create('checkpoints', { levelId: level.id, code: 'VCHK', name: 'Validation check-in', kind: 'CheckIn' }));
	const existing = [...(await list('desks', `parentId=${border.id}&pageSize=100`)), ...(await list('desks', `parentId=${airportSide.id}&pageSize=100`))];
	const desk = async (checkpointId: string, code: string, kind: string, laneCategories: string[]) =>
		(existing.find((d) => d.code === code) ?? (await create('desks', { checkpointId, code, kind, laneCategories }))).id as string;
	return {
		vd01: await desk(border.id, 'VD01', 'Desk', ['CIT']),
		vd02: await desk(border.id, 'VD02', 'Desk', ['CIT']),
		vd03: await desk(border.id, 'VD03', 'Desk', ['VIS']),
		vd04: await desk(border.id, 'VD04', 'Desk', ['VIS']),
		eGate: await desk(border.id, 'VEG1', 'EGate', ['EG']),
		counter: await desk(airportSide.id, 'VC01', 'Counter', [])
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
	desks = await ensureDesks();
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

// ARV-104b: tracer runs and desk observer logs.

const iso = (at: number) => new Date(at).toISOString();
const minute = 60_000;

/** A campaign request with VD01 to VD04 in scope (only a border role may plan it). */
function deskCampaignRequest(name: string): Record<string, unknown> {
	return { ...campaignRequest(name), deskIds: [desks.vd01, desks.vd02, desks.vd03, desks.vd04] };
}

/** A running campaign with desks, planned by the border manager and started by the lead. */
async function runningWithDesks(name: string): Promise<string> {
	const created = await call('POST', campaigns(), { token: manager, data: deskCampaignRequest(name) });
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id as string;
	expect((await call('POST', `${campaigns()}/${id}/start`, { token: lead })).status()).toBe(200);
	return id;
}

/** A tracer batch from a device `aheadMs` ahead of the real clock: runs joined and exited at the device's times. */
function tracerBatch(aheadMs: number, runs: { code: string; joinedAgo: number; exitedAgo: number; abandoned?: boolean; zoneId?: string }[]) {
	const now = Date.now();
	return {
		deviceClockUtc: iso(now + aheadMs),
		runs: runs.map((r) => ({
			zoneId: r.zoneId ?? profile.queueA,
			tracerCode: r.code,
			joinedUtc: iso(now + aheadMs - r.joinedAgo),
			exitedUtc: iso(now + aheadMs - r.exitedAgo),
			abandoned: r.abandoned ?? false
		}))
	};
}

/** A desk batch for the bin `back` bins ago (1 is the last that ended): per desk, its states by minute index. */
function deskBatch(back: number, perDesk: [string, [number, string][]][]) {
	return {
		binStartUtc: binStart(back),
		desks: perDesk.map(([deskId, states]) => {
			const minutes: (string | null)[] = Array.from({ length: 15 }, () => null);
			for (const [index, state] of states) minutes[index] = state;
			return { deskId, states: minutes };
		})
	};
}

/** One run T-10 with raw device times as given, from a device `aheadMs` ahead of the real clock. */
const farRun = (aheadMs: number, joinedUtc: string, exitedUtc: string) => ({
	deviceClockUtc: iso(Date.now() + aheadMs),
	runs: [{ zoneId: profile.queueA, tracerCode: 'T-10', joinedUtc, exitedUtc, abandoned: false }]
});

const sendRuns = (id: string, token: string, data: unknown, key?: string, code = site) =>
	call('POST', `${capture(code)}/${id}/tracer-runs`, { token, data, headers: key ? { 'Idempotency-Key': key } : undefined });
const sendDesks = (id: string, token: string, data: unknown, key?: string, code = site) =>
	call('POST', `${capture(code)}/${id}/desk-observations`, { token, data, headers: key ? { 'Idempotency-Key': key } : undefined });

test('tracer runs are corrected by the device clock offset, resent batches return the stored one, and bad times are refused', async () => {
	const id = await runningWithDesks(`Tracers ${Date.now()}`);
	const listed = (await (await call('GET', capture(), { token: observer })).json()).find((c: any) => c.id === id);
	expect(listed.zones.map((z: any) => z.name)).toEqual(['Q-A']);
	expect(listed.desks.map((d: any) => d.code)).toEqual(['VD01', 'VD02', 'VD03', 'VD04']);
	expect(listed.maxClockOffsetSeconds).toBe(300);

	// The device runs 90 s ahead: its times come back corrected by the measured offset (the network delay included).
	const key = `e2e-tracers-${Date.now()}`;
	const batch = tracerBatch(90_000, [
		{ code: 'T-07', joinedAgo: 20 * minute, exitedAgo: 5 * minute },
		{ code: 'T-08', joinedAgo: 15 * minute, exitedAgo: 9 * minute, abandoned: true }
	]);
	const sent = await sendRuns(id, observer, batch, key);
	expect(sent.status(), await sent.text()).toBe(201);
	const stored = await sent.json();
	expect(Math.abs(stored.clockOffsetMs - 90_000), 'offset within a few seconds of 90 s').toBeLessThan(5_000);
	const t07 = stored.runs.find((r: any) => r.tracerCode === 'T-07');
	expect(t07).toMatchObject({ zoneName: 'Q-A', abandoned: false, waitSeconds: 900, clockOffsetMs: stored.clockOffsetMs });
	expect(Date.parse(t07.joinedRawUtc) - Date.parse(t07.joinedUtc)).toBe(stored.clockOffsetMs);
	expect(stored.runs.find((r: any) => r.tracerCode === 'T-08').abandoned).toBe(true);

	// The same key: the stored batch (200), also with the clock read again; with other runs, 409; without a key, 400.
	const resent = await sendRuns(id, observer, { ...batch, deviceClockUtc: iso(Date.now() + 90_000) }, key);
	expect(resent.status()).toBe(200);
	expect(await resent.json()).toMatchObject({ id: stored.id, clockOffsetMs: stored.clockOffsetMs });
	expect((await sendRuns(id, observer, tracerBatch(0, [{ code: 'T-09', joinedAgo: 20 * minute, exitedAgo: 10 * minute }]), key)).status()).toBe(409);
	expect((await sendRuns(id, observer, batch)).status(), 'a batch needs a key').toBe(400);
	expect((await sendRuns(id, observer, batch, `${key}-again`)).status(), 'the same runs in another batch').toBe(409);
	// A key belongs to the observer who sent it: another observer's identical key is its own batch.
	const second = await sendRuns(id, observer2, batch, key);
	expect(second.status(), await second.text()).toBe(201);
	expect((await second.json()).observerId).toBe(claimsOf(observer2).sub);

	// Out of range (400, the value never repeated): a device more than 5 minutes off, a join on a day not planned, an exit
	// in the future, an exit before the join, a run over 3 hours, a zone out of scope.
	const refused: [string, unknown][] = [
		['device 6 minutes ahead', tracerBatch(6 * minute, [{ code: 'T-10', joinedAgo: 20 * minute, exitedAgo: 10 * minute }])],
		['device an hour behind', tracerBatch(-60 * minute, [{ code: 'T-10', joinedAgo: 20 * minute, exitedAgo: 10 * minute }])],
		['joined three days ago', tracerBatch(0, [{ code: 'T-10', joinedAgo: 3 * 86_400_000, exitedAgo: 3 * 86_400_000 - 10 * minute }])],
		['exited in the future', tracerBatch(0, [{ code: 'T-10', joinedAgo: 10 * minute, exitedAgo: -5 * minute }])],
		['exited before joined', tracerBatch(0, [{ code: 'T-10', joinedAgo: 10 * minute, exitedAgo: 11 * minute }])],
		['over three hours', tracerBatch(0, [{ code: 'T-10', joinedAgo: 200 * minute, exitedAgo: 10 * minute }])],
		['a zone out of scope', tracerBatch(0, [{ code: 'T-10', joinedAgo: 20 * minute, exitedAgo: 10 * minute, zoneId: profile.queueB }])],
		// First security review: well-formed but absurd device times overflowed the clock correction (500); refused before it.
		['a join in year 1 from a device ahead', farRun(2_000, '0001-01-01T00:00:00.000Z', '0001-01-01T01:00:00.000Z')],
		['an exit in year 9999 from a device behind', farRun(-2_000, '9999-12-31T23:00:00.000Z', '9999-12-31T23:59:59.999Z')]
	];
	for (const [what, data] of refused) {
		const answer = await sendRuns(id, observer, data, `e2e-refused-${Date.now()}`);
		expect(answer.status(), what).toBe(400);
		const body = await answer.text();
		expect(body, what).not.toContain('T-10');
		expect(body, what).not.toMatch(/\d{4}-\d{2}-\d{2}T/);
		expect(body, what).not.toContain('9999');
	}

	// The manager reads both observers' runs; each observer only its own; the campaign shows the runs per zone.
	const all = await (await call('GET', `${campaigns()}/${id}/tracer-runs?sortBy=joinedUtc`, { token: lead })).json();
	expect(all.totalCount).toBe(4);
	const own = await (await call('GET', `${capture()}/${id}/tracer-runs?observerId=${String(claimsOf(observer).sub)}`, { token: observer2 })).json();
	expect(own.data.every((r: any) => r.observerId === claimsOf(observer2).sub)).toBe(true);
	expect((await (await call('GET', `${campaigns()}/${id}`, { token: lead })).json()).zones[0].tracerRuns).toBe(4);
});

test('desk states are logged in 15-minute batches, corrected as revisions, and read by border roles only', async () => {
	// A duty manager may not put desks in scope (border data, 403); an airport counter or an e-gate is never in scope (400).
	const refusedPlan = await call('POST', campaigns(), { token: lead, data: deskCampaignRequest('Desks by the lead') });
	expect(refusedPlan.status()).toBe(403);
	for (const deskId of [desks.counter, desks.eGate]) {
		expect((await call('POST', campaigns(), { token: manager, data: { ...campaignRequest('Wrong desk'), deskIds: [desks.vd01, deskId] } })).status()).toBe(400);
	}

	const id = await runningWithDesks(`Desks ${Date.now()}`);
	const asManager = await (await call('GET', `${campaigns()}/${id}`, { token: manager })).json();
	expect(asManager.desksIncluded).toBe(true);
	expect(asManager.desks.map((d: any) => [d.checkpoint, d.code])).toEqual([['VIMM', 'VD01'], ['VIMM', 'VD02'], ['VIMM', 'VD03'], ['VIMM', 'VD04']]);
	const asLead = await (await call('GET', `${campaigns()}/${id}`, { token: lead })).json();
	expect([asLead.desksIncluded, asLead.desks]).toEqual([false, []]);

	// Four desks for the last 15 minutes, then the same batch again (200, the stored one) and the key with another body (409).
	const key = `e2e-desks-${Date.now()}`;
	const states = ['Serving', 'Idle', 'Paused', 'Closed'];
	const batch = deskBatch(1, [desks.vd01, desks.vd02, desks.vd03, desks.vd04].map((deskId, d) => [deskId, Array.from({ length: 15 }, (_, m) => [m, states[(d + m) % 4]] as [number, string])]));
	const sent = await sendDesks(id, observer, batch, key);
	expect(sent.status(), await sent.text()).toBe(201);
	const stored = await sent.json();
	expect(stored.observations).toHaveLength(60);
	expect(stored.observations[0]).toMatchObject({ deskCode: 'VD01', checkpoint: 'VIMM', minuteUtc: binStart(1), state: 'Serving', revision: 1, current: true });
	const resent = await sendDesks(id, observer, batch, key);
	expect(resent.status()).toBe(200);
	expect((await resent.json()).id).toBe(stored.id);
	expect((await sendDesks(id, observer, deskBatch(1, [[desks.vd01, [[0, 'Idle']]]]), key)).status(), 'the key with another batch').toBe(409);
	expect((await sendDesks(id, observer, deskBatch(1, [[desks.vd01, [[0, 'Idle']]]]), `${key}-again`)).status(), 'a minute already observed').toBe(409);
	expect((await sendDesks(id, observer, deskBatch(2, [[desks.vd01, [[0, 'Idle']]]]))).status(), 'a batch needs a key').toBe(400);
	expect((await sendDesks(id, observer2, batch, key)).status(), "another observer's identical key is its own batch").toBe(201);

	// Out of range (400): a day not planned, a minute in the future, a desk out of scope, a state that is not one of four.
	for (const [what, data] of [
		['three days ago', { ...deskBatch(1, [[desks.vd01, [[0, 'Idle']]]]), binStartUtc: binStart(3 * 96) }],
		['a minute in the future', deskBatch(-1, [[desks.vd01, [[0, 'Idle']]]])],
		['an e-gate', deskBatch(2, [[desks.eGate, [[0, 'Idle']]]])],
		['Unknown', deskBatch(2, [[desks.vd01, [[0, 'Unknown']]]])]
	] as [string, unknown][]) {
		const answer = await sendDesks(id, observer, data, `e2e-out-${Date.now()}`);
		expect(answer.status(), what).toBe(400);
		expect(await answer.text(), what).not.toMatch(/\d{4}-\d{2}-\d{2}T/);
	}

	// A correction is the next revision with a reason; only one's own latest; audited.
	const first = stored.observations[0];
	const correct = (observationId: string, token: string, data: unknown, correctionKey?: string) =>
		call('POST', `${capture()}/${id}/desk-observations/${observationId}/corrections`, { token, data, headers: correctionKey ? { 'Idempotency-Key': correctionKey } : undefined });
	const corrected = await correct(first.id, observer, { state: 'Idle', reason: 'Tapped the wrong row' }, `${key}-fix`);
	expect(corrected.status(), await corrected.text()).toBe(201);
	const revision = await corrected.json();
	expect(revision).toMatchObject({ revision: 2, state: 'Idle', correctsId: first.id, batchId: null, current: true });
	expect((await correct(first.id, observer, { state: 'Idle', reason: 'Tapped the wrong row' }, `${key}-fix`)).status(), 'resent').toBe(200);
	expect((await correct(first.id, observer, { state: 'Closed', reason: 'Again' })).status(), 'not the latest').toBe(409);
	expect((await correct(revision.id, observer2, { state: 'Closed', reason: 'Not mine' })).status(), "another observer's state").toBe(404);
	expect((await correct(revision.id, observer, { state: 'Closed' })).status(), 'a correction needs a reason').toBe(400);
	const audit = await (await call('GET', `${admin}/audit-entries?targetId=${revision.id}`, { token: administrator })).json();
	expect(audit.data.map((e: any) => e.action)).toEqual(['DeskObservation.Corrected']);

	// Border roles read every observer's states; the duty manager gets none (desk-level data is border data).
	const border = await (await call('GET', `${campaigns()}/${id}/desk-observations?deskId=${desks.vd01}&observerId=${String(claimsOf(observer).sub)}`, { token: manager })).json();
	expect([border.desksIncluded, border.totalCount]).toEqual([true, 15]);
	const history = await (await call('GET', `${campaigns()}/${id}/desk-observations?deskId=${desks.vd01}&currentOnly=false`, { token: administrator })).json();
	expect([history.desksIncluded, history.totalCount]).toEqual([true, 31]);
	const airportSide = await call('GET', `${campaigns()}/${id}/desk-observations`, { token: lead });
	expect(airportSide.status()).toBe(200);
	expect(await airportSide.json()).toMatchObject({ desksIncluded: false, data: [], totalCount: 0 });
	const own = await (await call('GET', `${capture()}/${id}/desk-observations?currentOnly=false`, { token: observer2 })).json();
	expect(own.totalCount).toBe(60);

	// Closed: nothing is captured or corrected any more (409).
	expect((await call('POST', `${campaigns()}/${id}/close`, { token: manager })).status()).toBe(200);
	expect((await sendDesks(id, observer, deskBatch(2, [[desks.vd01, [[0, 'Idle']]]]), `e2e-late-${Date.now()}`)).status()).toBe(409);
	expect((await correct(revision.id, observer, { state: 'Closed', reason: 'Late' })).status()).toBe(409);
	expect((await sendRuns(id, observer, tracerBatch(0, [{ code: 'T-01', joinedAgo: 20 * minute, exitedAgo: 10 * minute }]), `e2e-late-run-${Date.now()}`)).status()).toBe(409);
});

test('each role reaches only its tracer and desk endpoints, the campaign creator or starter never captures, and other sites answer 404', async () => {
	const id = await runningWithDesks(`Ground truth roles ${Date.now()}`);
	const runBody = tracerBatch(0, [{ code: 'T-01', joinedAgo: 20 * minute, exitedAgo: 10 * minute }]);
	const deskBody = deskBatch(1, [[desks.vd01, [[0, 'Idle']]]]);
	const rows = (key: string): [string, string, unknown?, string?][] => [
		['GET', `${campaigns()}/${id}/tracer-runs`],
		['GET', `${campaigns()}/${id}/desk-observations`],
		['GET', `${capture()}/${id}/tracer-runs`],
		['POST', `${capture()}/${id}/tracer-runs`, runBody, `${key}-run`],
		['GET', `${capture()}/${id}/desk-observations`],
		['POST', `${capture()}/${id}/desk-observations`, deskBody, `${key}-desk`],
		['POST', `${capture()}/${id}/desk-observations/${id}/corrections`, { state: 'Idle', reason: 'x' }]
	];
	const statuses = async (token: string, key: string) => {
		const answers: number[] = [];
		for (const [method, url, data, rowKey] of rows(key)) answers.push((await call(method, url, { token, data, headers: rowKey ? { 'Idempotency-Key': rowKey } : undefined })).status());
		return answers;
	};

	// The handler holds no validation permission; the observer only Capture; the duty manager, the administrator and the
	// border manager read (the duty manager's desk states are an empty page) and never capture.
	const stamp = Date.now();
	expect(await statuses(handler, `h-${stamp}`)).toEqual([403, 403, 403, 403, 403, 403, 403]);
	expect(await statuses(observer, `o-${stamp}`)).toEqual([403, 403, 200, 201, 200, 201, 404]);
	expect(await statuses(lead, `l-${stamp}`)).toEqual([200, 200, 403, 403, 403, 403, 403]);
	expect(await statuses(administrator, `a-${stamp}`)).toEqual([200, 200, 403, 403, 403, 403, 403]);
	expect(await statuses(manager, `m-${stamp}`)).toEqual([200, 200, 403, 403, 403, 403, 403]);

	// Separation of duties: e2e.valdual (duty manager and observer) starts a border campaign and may not capture for it.
	const created = await call('POST', campaigns(), { token: manager, data: deskCampaignRequest(`Dual ${stamp}`) });
	const dualStarted = (await created.json()).id as string;
	expect((await call('POST', `${campaigns()}/${dualStarted}/start`, { token: dual })).status()).toBe(200);
	const ownRuns = await sendRuns(dualStarted, dual, runBody, `dual-run-${stamp}`);
	expect(ownRuns.status(), 'its starter').toBe(403);
	expect((await ownRuns.json()).detail).toContain('created or started');
	expect((await sendDesks(dualStarted, dual, deskBody, `dual-desk-${stamp}`)).status(), 'its starter').toBe(403);
	expect((await sendRuns(id, dual, runBody, `dual-other-${stamp}`)).status(), "another manager's campaign").toBe(201);

	// Another site: through its own route and the campaign's site, every new endpoint answers 404.
	for (const code of [site, 'E2E1']) {
		for (const path of ['tracer-runs', 'desk-observations'])
			expect((await call('GET', `${campaigns(code)}/${id}/${path}`, { token: elsewhere })).status(), `${code} ${path}`).toBe(404);
		expect((await sendRuns(id, observerElsewhere, runBody, `x-run-${stamp}`, code)).status()).toBe(404);
		expect((await sendDesks(id, observerElsewhere, deskBody, `x-desk-${stamp}`, code)).status()).toBe(404);
		expect((await call('GET', `${capture(code)}/${id}/tracer-runs`, { token: observerElsewhere })).status()).toBe(404);
		expect((await call('GET', `${capture(code)}/${id}/desk-observations`, { token: observerElsewhere })).status()).toBe(404);
	}
});

test('an account with an airport role and the observer role sees no desk and gets 403 on every desk path, while a pure observer keeps them', async () => {
	// First security review of ARV-104b (CWE-863, data boundary): desk codes beside minutes are border data. e2e.valdual
	// holds Terminal duty manager and Validation observer: the capture list shows it no desk, and a desk batch, a correction
	// and its own desk read answer 403 (decided before anything is read, so an attack payload changes nothing). The pure
	// observer, the border's own, sees the desks and logs, corrects and reads back.
	const id = await runningWithDesks(`Desk boundary ${Date.now()}`);
	const listedFor = async (token: string) => (await (await call('GET', capture(), { token })).json()).find((c: any) => c.id === id);
	const asDual = await listedFor(dual);
	expect(asDual).toMatchObject({ desksIncluded: false, desks: [] });
	expect(asDual.zones.map((z: any) => z.name), 'the zones stay: tracer runs are zone data').toEqual(['Q-A']);
	const asObserver = await listedFor(observer);
	expect(asObserver.desksIncluded).toBe(true);
	expect(asObserver.desks.map((d: any) => d.code)).toEqual(['VD01', 'VD02', 'VD03', 'VD04']);

	const stamp = Date.now();
	const logged = await sendDesks(id, observer, deskBatch(1, [[desks.vd01, [[0, 'Serving']]]]), `e2e-pure-${stamp}`);
	expect(logged.status(), await logged.text()).toBe(201);
	const correction = `${capture()}/${id}/desk-observations/${(await logged.json()).observations[0].id}/corrections`;
	const corrected = await call('POST', correction, { token: observer, data: { state: 'Idle', reason: 'Tapped the wrong row' } });
	expect(corrected.status(), await corrected.text()).toBe(201);
	const ownRead = await call('GET', `${capture()}/${id}/desk-observations?currentOnly=false`, { token: observer });
	expect(ownRead.status()).toBe(200);
	expect((await ownRead.json()).totalCount).toBe(2);

	const refused: [string, CallResponse][] = [
		['a desk batch', await sendDesks(id, dual, deskBatch(1, [[desks.vd02, [[1, 'Idle']]]]), `e2e-dual-desk-${stamp}`)],
		['a desk batch with markup', await sendDesks(id, dual, deskBatch(1, [[desks.vd02, [[2, xssPayloads[0]]]]]), `e2e-dual-xss-${stamp}`)],
		['a correction', await call('POST', correction, { token: dual, data: { state: 'Closed', reason: sqlInjectionPayloads[0] } })],
		['its own desk read', await call('GET', `${capture()}/${id}/desk-observations?currentOnly=false`, { token: dual })],
		['its own desk read of another campaign', await call('GET', `${capture()}/0199a000-0000-7000-8000-00000000dead/desk-observations`, { token: dual })]
	];
	for (const [what, answer] of refused) {
		expect(answer.status(), what).toBe(403);
		const body = await answer.text();
		expect(JSON.parse(body).detail, what).toContain('border data');
		for (const fragment of [...markupFragments, "OR '1'='1", 'VD0', 'Serving']) expect(body, what).not.toContain(fragment);
	}
	expect((await sendRuns(id, dual, tracerBatch(0, [{ code: 'T-31', joinedAgo: 20 * minute, exitedAgo: 10 * minute }]), `e2e-dual-run-${stamp}`)).status(), 'tracer runs are zone data').toBe(201);
	expect(await (await call('GET', `${campaigns()}/${id}/desk-observations`, { token: dual })).json(), 'as a duty manager').toMatchObject({ desksIncluded: false, data: [], totalCount: 0 });

	// Only the pure observer's states are stored.
	const stored = await (await call('GET', `${campaigns()}/${id}/desk-observations?currentOnly=false`, { token: manager })).json();
	expect(stored.totalCount).toBe(2);
	expect(stored.data.every((o: any) => o.observerId === claimsOf(observer).sub)).toBe(true);

	// Another site: the dual account answers 404 there before the desk rule (the site comes first).
	expect((await call('GET', `${capture('E2E1')}/${id}/desk-observations`, { token: dual })).status()).toBe(404);
});

test('attack payloads in tracer and desk batches are refused with 400 or kept as inert text, and oversized batches get 413', async () => {
	const id = await runningWithDesks(`Ground truth payloads ${Date.now()}`);
	const run = { zoneId: profile.queueA, tracerCode: 'T-01', joinedUtc: iso(Date.now() - 20 * minute), exitedUtc: iso(Date.now() - 10 * minute), abandoned: false };
	const refusedRuns: [string, unknown][] = [
		['SQL as the tracer code', { deviceClockUtc: iso(Date.now()), runs: [{ ...run, tracerCode: sqlInjectionPayloads[0] }] }],
		['markup as the tracer code', { deviceClockUtc: iso(Date.now()), runs: [{ ...run, tracerCode: xssPayloads[0] }] }],
		['a name as the tracer code', { deviceClockUtc: iso(Date.now()), runs: [{ ...run, tracerCode: 'Ahmad' }] }],
		['SQL as the device clock', { deviceClockUtc: sqlInjectionPayloads[2], runs: [run] }],
		['markup as a time', { deviceClockUtc: iso(Date.now()), runs: [{ ...run, joinedUtc: xssPayloads[1] }] }],
		['a zone id that is SQL', { deviceClockUtc: iso(Date.now()), runs: [{ ...run, zoneId: sqlInjectionPayloads[0] }] }],
		['an abandoned flag that is markup', { deviceClockUtc: iso(Date.now()), runs: [{ ...run, abandoned: xssPayloads[0] }] }],
		['21 runs', { deviceClockUtc: iso(Date.now()), runs: Array.from({ length: 21 }, (_, i) => ({ ...run, tracerCode: `T-${String(i + 10)}` })) }]
	];
	for (const [what, data] of refusedRuns) {
		const answer = await sendRuns(id, observer, data, `e2e-attack-${Date.now()}`);
		expect(answer.status(), what).toBe(400);
		const body = await answer.text();
		for (const fragment of [...markupFragments, "OR '1'='1", 'DROP TABLE', 'Ahmad']) expect(body, what).not.toContain(fragment);
	}

	const deskRefusals: [string, unknown][] = [
		['SQL as a state', deskBatch(1, [[desks.vd01, [[0, sqlInjectionPayloads[0]]]]])],
		['markup as a state', deskBatch(1, [[desks.vd01, [[0, xssPayloads[2]]]]])],
		['SQL as the bin', { ...deskBatch(1, [[desks.vd01, [[0, 'Idle']]]]), binStartUtc: sqlInjectionPayloads[1] }],
		['a desk id that is markup', { binStartUtc: binStart(1), desks: [{ deskId: xssPayloads[0], states: Array(15).fill('Idle') }] }],
		['16 states', { binStartUtc: binStart(1), desks: [{ deskId: desks.vd01, states: Array(16).fill('Idle') }] }],
		['21 desks', { binStartUtc: binStart(1), desks: Array.from({ length: 21 }, () => ({ deskId: desks.vd01, states: Array(15).fill('Idle') })) }]
	];
	for (const [what, data] of deskRefusals) {
		const answer = await sendDesks(id, observer, data, `e2e-attack-${Date.now()}`);
		expect(answer.status(), what).toBe(400);
		const body = await answer.text();
		for (const fragment of [...markupFragments, "OR '1'='1", 'DROP TABLE']) expect(body, what).not.toContain(fragment);
	}

	// Keys that are not keys; query values that are not allowed; markup in a correction's reason stays inert text.
	for (const key of [sqlInjectionPayloads[0], xssPayloads[2], 'short', 'k'.repeat(65)]) {
		const answer = await sendRuns(id, observer, { deviceClockUtc: iso(Date.now()), runs: [run] }, key);
		expect(answer.status(), key).toBe(400);
		expectNoLeak(await answer.text(), key);
	}
	for (const query of [`tracerCode=${encodeURIComponent(sqlInjectionPayloads[0])}`, `sortBy=${encodeURIComponent('joinedUtc; DROP TABLE tracer_run')}`, `zoneId=${encodeURIComponent(xssPayloads[0])}`]) {
		const answer = await call('GET', `${campaigns()}/${id}/tracer-runs?${query}`, { token: manager });
		expect(answer.status(), query).toBe(400);
		for (const fragment of markupFragments) expect(await answer.text(), query).not.toContain(fragment);
	}
	for (const query of [`sortBy=${encodeURIComponent(xssPayloads[0])}`, `deskId=${encodeURIComponent(sqlInjectionPayloads[0])}`, `fromDate=${encodeURIComponent(sqlInjectionPayloads[4])}`]) {
		expect((await call('GET', `${campaigns()}/${id}/desk-observations?${query}`, { token: manager })).status(), query).toBe(400);
	}
	const logged = await sendDesks(id, observer, deskBatch(1, [[desks.vd02, [[0, 'Idle']]]]), `e2e-inert-${Date.now()}`);
	expect(logged.status(), await logged.text()).toBe(201);
	const reason = `${xssPayloads[1]} ${sqlInjectionPayloads[3]}`;
	const corrected = await call('POST', `${capture()}/${id}/desk-observations/${(await logged.json()).observations[0].id}/corrections`, {
		token: observer,
		data: { state: 'Serving', reason }
	});
	expect(corrected.status(), await corrected.text()).toBe(201);
	expect(corrected.headers()['content-type']).toContain('application/json');
	expectApiSecurityHeaders(corrected);
	expect((await corrected.json()).reason).toBe(reason);

	// Bodies over the limit: a tracer or desk batch over 16 KB, a correction over 4 KB.
	const json = { 'Content-Type': 'application/json', 'Idempotency-Key': `e2e-big-${Date.now()}` };
	const bigRuns = await call('POST', `${capture()}/${id}/tracer-runs`, { token: observer, raw: ' '.repeat(17 * 1024) + JSON.stringify({ deviceClockUtc: iso(Date.now()), runs: [run] }), headers: json });
	expect(bigRuns.status()).toBe(413);
	const bigDesks = await call('POST', `${capture()}/${id}/desk-observations`, { token: observer, raw: ' '.repeat(17 * 1024) + JSON.stringify(deskBatch(2, [[desks.vd01, [[0, 'Idle']]]])), headers: json });
	expect(bigDesks.status()).toBe(413);
	const bigCorrection = await call('POST', `${capture()}/${id}/desk-observations/${id}/corrections`, {
		token: observer,
		raw: JSON.stringify({ state: 'Idle', reason: 'r'.repeat(5 * 1024) }),
		headers: { 'Content-Type': 'application/json' }
	});
	expect(bigCorrection.status()).toBe(413);
});

// ARV-104g: a campaign's results as JSON under .../campaigns/{id}/results (Validation.View at the campaign's site; the answer is
// projected per caller from its stored roles: desk-state results to border roles only, observer-level results to View or
// Manage holders, the shadow's figures to View holders), frozen at the close as revision 1 of a stored document with a SHA-256
// content hash (script 0050), read again by ?revision=n, and recomputed as the next revision with a reason under
// .../results/revisions (Validation.Manage with a second factor, audited). The E2E site has no sensors, so the verdicts are
// no data; the shapes, the serving rules, the freeze and the hash are what these tests prove.

const results = (id: string, code = site, suffix = '') => `${campaigns(code)}/${id}/results${suffix}`;
const criteriaNames = ['CountAccuracy', 'WaitError', 'WaitBias', 'TrackCompletion', 'NowcastError', 'Availability'];

/** The validation manager signed in again with a second factor now (recomputing is a critical action, 15 minutes). */
async function managerWithSecondFactor(): Promise<string> {
	const { userName, password, totpSecret } = accounts().validationManager;
	const signedIn = await login(userName, password, undefined, undefined, { code: await unusedTotpCode(totpSecret!) });
	expect(signedIn.status()).toBe(200);
	return (await signedIn.json()).accessToken;
}

/** A running campaign with desks and ground truth of two observers: a count, a tracer run each and 15 minutes of VD01. */
async function campaignWithGroundTruth(name: string): Promise<string> {
	const id = await runningWithDesks(name);
	const count = await call('POST', `${capture()}/${id}/counts`, { token: observer, data: { lineId: profile.entryA, binStartUtc: binStart(1), crossingsIn: 12, crossingsOut: 1 } });
	expect(count.status(), await count.text()).toBe(201);
	for (const [token, code, joinedAgo] of [[observer, 'T-21', 20], [observer2, 'T-22', 18]] as [string, string, number][]) {
		const runs = await sendRuns(id, token, tracerBatch(0, [{ code, joinedAgo: joinedAgo * minute, exitedAgo: 6 * minute }]), `e2e-results-${code}-${Date.now()}`);
		expect(runs.status(), await runs.text()).toBe(201);
	}
	const states = Array.from({ length: 15 }, (_, m) => [m, 'Serving'] as [number, string]);
	const logged = await sendDesks(id, observer, deskBatch(1, [[desks.vd01, states]]), `e2e-results-desks-${Date.now()}`);
	expect(logged.status(), await logged.text()).toBe(201);
	return id;
}

test('campaign results are served per role while running, frozen at the close as revision 1 with a SHA-256 hash, and recomputed as revision 2 with a reason', async () => {
	const id = await campaignWithGroundTruth(`Results ${Date.now()}`);

	// Running: computed when asked, not frozen; the duty manager (airport side) gets no desk-state result and no desk key.
	const live = await call('GET', results(id), { token: lead });
	expect(live.status(), await live.text()).toBe(200);
	expect(live.headers()['content-type']).toContain('application/json');
	expectApiSecurityHeaders(live);
	const running = await live.json();
	expect(running).toMatchObject({ campaignId: id, siteCode: site, status: 'Running', revision: null, desks: null, audience: { desksIncluded: false, observers: 'All', proofIncluded: true } });
	expect(running.criteria.map((c: any) => c.criterion)).toEqual(criteriaNames);
	expect(running.review).toContain('CampaignNotClosed');
	expect(running.observers.runs.map((r: any) => r.tracerCode).sort()).toEqual(['T-21', 'T-22']);
	expect(running.nowcast).not.toHaveProperty('minutes');
	for (const desk of ['VD01', desks.vd01]) expect(await live.text(), 'no desk key for an airport role').not.toContain(desk);

	// Closed: frozen as revision 1, its hash a SHA-256 in lowercase hexadecimal; the border manager sees the desk section.
	expect((await call('POST', `${campaigns()}/${id}/close`, { token: manager })).status()).toBe(200);
	const first = await call('GET', results(id), { token: manager });
	expect(first.status(), await first.text()).toBe(200);
	const frozen = await first.json();
	expect(frozen).toMatchObject({ status: 'Closed', revision: { number: 1, revisions: 1, reason: null }, audience: { desksIncluded: true, observers: 'All', proofIncluded: true } });
	expect(frozen.revision.contentSha256).toMatch(/^[0-9a-f]{64}$/);
	expect(frozen.desks.verdict.criterion).toBe('DeskStateAgreement');
	expect(frozen.desks.overall.minutes).toBe(15);
	expect(frozen.desks).not.toHaveProperty('minutes');
	expect(frozen.review).not.toContain('CampaignNotClosed');

	// Served from the stored document: the same bytes again, and by revision number; one hash for every reader.
	expect(await (await call('GET', results(id), { token: manager })).text()).toBe(await first.text());
	expect(await (await call('GET', results(id, site, '?revision=1'), { token: manager })).text()).toBe(await first.text());
	const asLead = await call('GET', results(id), { token: lead });
	expect(asLead.status()).toBe(200);
	const leadView = await asLead.json();
	expect([leadView.revision.contentSha256, leadView.desks, leadView.audience.desksIncluded]).toEqual([frozen.revision.contentSha256, null, false]);
	for (const desk of ['VD01', desks.vd01]) expect(await asLead.text()).not.toContain(desk);
	const asAdministrator = await (await call('GET', results(id), { token: administrator })).json();
	expect(asAdministrator.audience.desksIncluded).toBe(true);
	const asDual = await call('GET', results(id), { token: dual });
	expect(asDual.status()).toBe(200);
	expect((await asDual.json()).desks, 'an airport role with the observer role sees no desk').toBeNull();

	// Roles without Validation.View get 403 before anything is read.
	for (const token of [observer, observer2, handler]) expect((await call('GET', results(id), { token })).status()).toBe(403);
	expect((await call('GET', results(id))).status()).toBe(401);

	// Revisions: one that does not exist is 404; a number out of range or not a number is 400, never echoed.
	expect((await call('GET', results(id, site, '?revision=2'), { token: manager })).status()).toBe(404);
	for (const query of ['?revision=0', '?revision=1001', `?revision=${encodeURIComponent(sqlInjectionPayloads[0])}`, `?revision=${encodeURIComponent(xssPayloads[0])}`]) {
		const refused = await call('GET', results(id, site, query), { token: manager });
		expect(refused.status(), query).toBe(400);
		const body = await refused.text();
		for (const fragment of [...markupFragments, "OR '1'='1"]) expect(body, query).not.toContain(fragment);
	}

	// Recomputing is critical: a password-only session is asked for its second factor; roles without Manage get 403.
	const recompute = (token: string, data: unknown) => call('POST', results(id, site, '/revisions'), { token, data });
	const withoutSecondFactor = await recompute(lead, { reason: 'Late desk logs' });
	expect(withoutSecondFactor.status()).toBe(401);
	expect(withoutSecondFactor.headers()['www-authenticate']).toContain('insufficient_user_authentication');
	for (const token of [observer, handler]) expect((await recompute(token, { reason: 'Not mine' })).status()).toBe(403);
	const strong = await managerWithSecondFactor();
	for (const reason of [' ', '', 'r'.repeat(501), 'bidi \u202e override', null]) expect((await recompute(strong, { reason })).status(), String(reason)).toBe(400);
	const tooBig = await call('POST', results(id, site, '/revisions'), { token: strong, raw: ' '.repeat(5 * 1024) + JSON.stringify({ reason: 'Big' }), headers: { 'Content-Type': 'application/json' } });
	expect(tooBig.status()).toBe(413);

	const added = await recompute(strong, { reason: '  Desk logs re-entered from paper  ' });
	expect(added.status(), await added.text()).toBe(201);
	expectApiSecurityHeaders(added);
	const second = await added.json();
	expect(second.revision).toMatchObject({ number: 2, revisions: 2, reason: 'Desk logs re-entered from paper' });
	expect((await (await call('GET', results(id), { token: lead })).json()).revision.number).toBe(2);
	const kept = await (await call('GET', results(id, site, '?revision=1'), { token: manager })).json();
	expect([kept.revision.number, kept.revision.revisions, kept.revision.contentSha256]).toEqual([1, 2, frozen.revision.contentSha256]);

	// Audited: the freeze as the system, the recomputation as the manager, each against its revision.
	const audit = async (action: string) =>
		(await (await call('GET', `${admin}/audit-entries?action=${action}&pageSize=500`, { token: administrator })).json()).data.filter((e: any) =>
			String(e.afterSummary).includes(`campaign=${id}`));
	const frozenAudit = await audit('ValidationResults.Frozen');
	expect(frozenAudit.map((e: any) => [e.actorName, e.targetType])).toEqual([['validation-results-freeze', 'ValidationResultRevision']]);
	expect(frozenAudit[0].afterSummary).toContain(`sha256=${frozen.revision.contentSha256}`);
	const recomputedAudit = await audit('ValidationResults.Recomputed');
	expect(recomputedAudit.map((e: any) => [e.actorId, e.afterSummary.includes('reason="Desk logs re-entered from paper"')])).toEqual([[claimsOf(manager).sub, true]]);

	// A campaign still running has nothing frozen to recompute (409).
	const open = await plan(`Results open ${Date.now()}`);
	expect((await call('POST', `${campaigns()}/${open.id}/start`, { token: lead })).status()).toBe(200);
	expect((await call('POST', results(open.id, site, '/revisions'), { token: strong, data: { reason: 'Too early' } })).status()).toBe(409);
});

test('campaign results answer 404 across sites, refuse attack payloads and keep markup inert', async () => {
	const name = `${xssPayloads[0]} Results ${Date.now()}`;
	const created = await call('POST', campaigns(), { token: lead, data: campaignRequest(name) });
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id as string;
	expect((await call('POST', `${campaigns()}/${id}/start`, { token: lead })).status()).toBe(200);
	expect((await call('POST', `${campaigns()}/${id}/close`, { token: manager })).status()).toBe(200);

	// Markup in the campaign's name is a JSON string value: escaped in the body, the same text once parsed.
	const served = await call('GET', results(id), { token: lead });
	expect(served.status(), await served.text()).toBe(200);
	expect(served.headers()['content-type']).toContain('application/json');
	expectApiSecurityHeaders(served);
	const body = await served.text();
	for (const fragment of markupFragments) expect(body).not.toContain(fragment);
	expect(JSON.parse(body).campaignName).toBe(name);

	// Another site, or a campaign of another site: 404, never 403, whichever site the route names.
	for (const code of [site, 'E2E1']) expect((await call('GET', results(id, code), { token: elsewhere })).status(), code).toBe(404);
	expect((await call('GET', results(id, 'E2E1'), { token: administrator })).status(), 'a campaign of another site').toBe(404);
	const strong = await managerWithSecondFactor();
	expect((await call('POST', results(id, 'E2E1', '/revisions'), { token: strong, data: { reason: 'Other site' } })).status()).toBe(404);
	expect((await call('GET', results('0199a000-0000-7000-8000-00000000a1a1'), { token: lead })).status(), 'an unknown campaign').toBe(404);
	expect((await call('GET', `${campaigns()}/${encodeURIComponent(sqlInjectionPayloads[0])}/results`, { token: lead })).status(), 'an id that is not a GUID').toBe(404);

	// Site codes that are SQL or markup: refused (4xx) without echo, never a 5xx.
	for (const payload of [...sqlInjectionPayloads, ...xssPayloads]) {
		const answer = await call('GET', `${hosts.main}/api/v1/sites/${encodeURIComponent(payload)}/validation/campaigns/${id}/results`, { token: lead });
		expect(answer.status(), payload).toBeGreaterThanOrEqual(400);
		expect(answer.status(), payload).toBeLessThan(500);
		const text = await answer.text();
		for (const fragment of [...markupFragments, "OR '1'='1"]) expect(text, payload).not.toContain(fragment);
		expectNoLeak(text, payload);
	}

	// A recomputation's reason with markup and SQL is kept as inert text; JSON too deep, or not JSON, is refused.
	const reason = `${xssPayloads[1]} ${sqlInjectionPayloads[2]}`;
	const recomputed = await call('POST', results(id, site, '/revisions'), { token: strong, data: { reason } });
	expect(recomputed.status(), await recomputed.text()).toBe(201);
	expect(recomputed.headers()['content-type']).toContain('application/json');
	expectApiSecurityHeaders(recomputed);
	for (const fragment of markupFragments) expect(await recomputed.text()).not.toContain(fragment);
	expect((await recomputed.json()).revision.reason).toBe(reason);
	const deep = '{"reason":' + '['.repeat(40) + '"x"' + ']'.repeat(40) + '}';
	expect((await call('POST', results(id, site, '/revisions'), { token: strong, raw: deep, headers: { 'Content-Type': 'application/json' } })).status()).toBe(400);
	expect((await call('POST', results(id, site, '/revisions'), { token: strong, raw: 'reason=x', headers: { 'Content-Type': 'application/x-www-form-urlencoded' } })).status()).toBe(415);
	expect((await (await call('GET', results(id), { token: lead })).json()).revision.revisions, 'only the valid recomputation added a revision').toBe(2);
});
