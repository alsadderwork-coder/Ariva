import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-017: the zone profile workflow through the API. A duty manager of E2E2 drafts, edits, validates and publishes
// (publishing is critical: without a second factor in the last 15 minutes it answers 401
// insufficient_user_authentication, RFC 9470); a published version never changes (409); the next draft copies it and
// publishing it retires the first; the history names who published each version; other sites see nothing.
// The PRD wrote 403 for "publish without recent MFA"; ADR-0026 and ARV-010d answer 401 with the RFC 9470 challenge,
// which is what clients must react to, so this spec expects 401.

test.skip(!databaseAvailable, 'zone profiles need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const api = `${admin}/zone-profiles`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const iata = Array.from({ length: 3 }, () => letters[Math.floor(Math.random() * 26)]).join('');
let passwordOnly: string;
let withSecondFactor: string;
let levelId: string;
let firstId: string;
let queueId: string;

test.beforeAll(async () => {
	const administrator = (await signIn(accounts().SystemAdministrator)).accessToken;
	const create = async (entity: string, data: unknown) => {
		const response = await call('POST', `${admin}/${entity}`, { token: administrator, data });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
		return (await response.json()).id as string;
	};
	const airport = await create('airports', { iataCode: iata, name: `E2E zones ${iata}`, timeZoneId: 'Asia/Dubai' });
	const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'Terminal 1', siteCode: 'E2E2' });
	levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });

	const { userName, password, totpSecret } = accounts().zoneManager;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status()).toBe(200);
	withSecondFactor = (await signedIn.json()).accessToken;
	passwordOnly = (await signIn(accounts().TerminalDutyManager)).accessToken;
});

test('a draft is edited, validated and published as version 1 only with a recent second factor', async () => {
	// Earlier runs may have left a draft for E2E2: discard it first (administrators may).
	const administrator = (await signIn(accounts().SystemAdministrator)).accessToken;
	const history = await (await call('GET', `${api}?siteCode=E2E2`, { token: administrator })).json();
	for (const old of history.filter((p: any) => p.status === 'Draft')) await call('DELETE', `${api}/${old.id}`, { token: administrator });

	const created = await call('POST', `${api}/drafts`, { token: withSecondFactor, data: { siteCode: 'E2E2', name: 'Arrivals immigration' } });
	expect(created.status(), await created.text()).toBe(201);
	firstId = (await created.json()).profile.id;
	expect((await call('POST', `${api}/drafts`, { token: withSecondFactor, data: { siteCode: 'E2E2' } })).status(), 'one draft per site').toBe(409);

	const zone = await call('POST', `${api}/${firstId}/zones`, { token: withSecondFactor, data: { name: 'Snake A', kind: 'Queue', levelId, polygon: '10 10,34 10,34 22,10 22' } });
	expect(zone.status(), await zone.text()).toBe(201);
	queueId = (await zone.json()).id;
	const crossing = await call('POST', `${api}/${firstId}/zones`, { token: withSecondFactor, data: { name: 'Bow', kind: 'Queue', levelId, polygon: '0 0,10 10,10 0,0 10' } });
	expect(crossing.status(), 'self-crossing polygon').toBe(400);
	const markup = await call('POST', `${api}/${firstId}/zones`, { token: withSecondFactor, data: { name: '<script>alert(1)</script>', kind: 'Queue', levelId, polygon: '50 10,60 10,60 20' } });
	expect([201, 400], 'markup is refused or stored as inert text').toContain(markup.status());
	if (markup.status() === 201) await call('DELETE', `${api}/${firstId}/zones/${(await markup.json()).id}`, { token: withSecondFactor });

	const problems = await (await call('GET', `${api}/${firstId}/validation`, { token: withSecondFactor })).json();
	expect(problems.publishable).toBe(false);
	for (const line of [
		{ name: 'Entry A', role: 'Entry', startX: 10, startY: 12, endX: 10, endY: 16 },
		{ name: 'Exit A', role: 'Exit', startX: 30, startY: 22, endX: 34, endY: 22 }
	]) {
		const added = await call('POST', `${api}/${firstId}/lines`, { token: withSecondFactor, data: { ...line, levelId, zoneId: queueId } });
		expect(added.status(), await added.text()).toBe(201);
	}
	const reviewed = await (await call('GET', `${api}/${firstId}/validation`, { token: withSecondFactor })).json();
	expect(reviewed.publishable).toBe(true);
	const geometryHash = reviewed.geometryHash as string;

	const refused = await call('POST', `${api}/${firstId}/publish`, { token: passwordOnly, data: { geometryHash } });
	expect(refused.status(), 'publish without a recent second factor').toBe(401);
	expect(refused.headers()['www-authenticate']).toContain('insufficient_user_authentication');

	const stale = await call('POST', `${api}/${firstId}/publish`, { token: withSecondFactor, data: { geometryHash: '0'.repeat(64) } });
	expect(stale.status(), 'a hash other than the reviewed geometry').toBe(409);
	const published = await call('POST', `${api}/${firstId}/publish`, { token: withSecondFactor, data: { geometryHash } });
	expect(published.status(), await published.text()).toBe(200);
	const v1 = await published.json();
	expect(v1.version).toBeGreaterThanOrEqual(1);
	expect(v1.status).toBe('Published');
	expect(v1.geometryHash).toMatch(/^[0-9a-f]{64}$/);
});

test('a published version never changes; the next draft copies it and retires it on publishing', async () => {
	expect((await call('PUT', `${api}/${firstId}`, { token: withSecondFactor, data: { name: 'Renamed' } })).status(), 'rename published').toBe(409);
	expect((await call('POST', `${api}/${firstId}/zones`, { token: withSecondFactor, data: { name: 'Late', kind: 'Queue', levelId, polygon: '50 10,60 10,60 20' } })).status(), 'add to published').toBe(409);
	expect((await call('DELETE', `${api}/${firstId}/zones/${queueId}`, { token: withSecondFactor })).status(), 'remove from published').toBe(409);
	expect((await call('POST', `${api}/${firstId}/publish`, { token: withSecondFactor, data: { geometryHash: '0'.repeat(64) } })).status(), 'publish twice').toBe(409);

	const next = await call('POST', `${api}/drafts`, { token: withSecondFactor, data: { siteCode: 'E2E2' } });
	expect(next.status(), await next.text()).toBe(201);
	const draft = await next.json();
	expect(draft.zones).toHaveLength(1);
	expect(draft.lines).toHaveLength(2);
	const snake = draft.zones[0];
	const moved = await call('PUT', `${api}/${draft.profile.id}/zones/${snake.id}`, { token: withSecondFactor, data: { name: 'Snake A', polygon: '10 10,34 10,36 16,34 22,10 22' } });
	expect(moved.status(), await moved.text()).toBe(200);
	const nextHash = (await (await call('GET', `${api}/${draft.profile.id}/validation`, { token: withSecondFactor })).json()).geometryHash;
	const v2 = await (await call('POST', `${api}/${draft.profile.id}/publish`, { token: withSecondFactor, data: { geometryHash: nextHash } })).json();
	expect(v2.version).toBe(draft.profile.basedOnVersion + 1);

	const history = await (await call('GET', `${api}?siteCode=E2E2`, { token: withSecondFactor })).json();
	expect(history[0]).toMatchObject({ id: draft.profile.id, status: 'Published', publishedBy: 'e2e.zonemanager' });
	expect(history.find((p: any) => p.id === firstId)).toMatchObject({ status: 'Retired' });
	expect(history.every((p: any) => p.createdOn)).toBe(true);
});

test("another site's caller sees nothing, and only administrators discard", async () => {
	const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
	expect((await call('GET', `${api}?siteCode=E2E2`, { token: border })).status()).toBe(404);
	expect((await call('GET', `${api}/${firstId}`, { token: border })).status()).toBe(404);
	expect((await call('POST', `${api}/drafts`, { token: border, data: { siteCode: 'E2E2' } })).status()).toBe(400);

	const draft = await (await call('POST', `${api}/drafts`, { token: withSecondFactor, data: { siteCode: 'E2E2' } })).json();
	expect((await call('DELETE', `${api}/${draft.profile.id}`, { token: withSecondFactor })).status(), 'duty managers cannot discard').toBe(403);
	const administrator = (await signIn(accounts().SystemAdministrator)).accessToken;
	expect((await call('DELETE', `${api}/${draft.profile.id}`, { token: administrator })).status()).toBe(204);
});
