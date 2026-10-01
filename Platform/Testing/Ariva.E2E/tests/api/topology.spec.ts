import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-014: topology CRUD per entity (airport, terminal, level, checkpoint, desk) through the admin API, limited to the
// caller's sites (another site's records answer 404 without data), with injection and markup payloads refused by
// validation or stored as inert text. The seed binds e2e.border to E2E1 and e2e.terminal to E2E2.

test.skip(!databaseAvailable, 'topology needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/admin`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const iata = Array.from({ length: 3 }, (_, i) => letters[Math.floor(Date.now() / 1000 / 26 ** i) % 26]).join('');
const ids: Record<string, string> = {};
let admin: string;

test.beforeAll(async () => {
	admin = (await signIn(accounts().SystemAdministrator)).accessToken;
});

async function create(entity: string, data: unknown): Promise<any> {
	const response = await call('POST', `${api}/${entity}`, { token: admin, data });
	expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
	expect(response.headers()['location']).toContain(`/api/v1/admin/${entity}/`);
	return response.json();
}

test('an administrator builds the tree: airport, terminals in two sites, level, checkpoint, desks', async () => {
	ids.airport = (await create('airports', { iataCode: iata, name: `E2E ${iata}`, timeZoneId: 'Asia/Amman' })).id;
	const t1 = await create('terminals', { airportId: ids.airport, code: 'T1', name: 'Terminal 1', siteCode: 'E2E1' });
	ids.t1 = t1.id;
	expect(t1.siteCode).toBe('E2E1');
	ids.t2 = (await create('terminals', { airportId: ids.airport, code: 'T2', name: 'Terminal 2', siteCode: 'E2E2' })).id;
	const level = await create('levels', { terminalId: ids.t1, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 300, depthMetres: 120 });
	ids.level = level.id;
	expect(level.siteCode, 'the site flows down from the terminal').toBe('E2E1');
	ids.checkpoint = (await create('checkpoints', { levelId: ids.level, code: 'IMM', name: 'Immigration', kind: 'Immigration' })).id;
	const desk = await create('desks', { checkpointId: ids.checkpoint, code: 'D01', kind: 'Desk', laneCategories: ['res', 'CIT'] });
	ids.desk = desk.id;
	expect(desk.laneCategories).toEqual(['CIT', 'RES']);
	expect((await create('desks', { checkpointId: ids.checkpoint, code: 'EG-01', kind: 'EGate', laneCategories: ['EG'] })).kind).toBe('EGate');

	expect((await call('POST', `${api}/airports`, { token: admin, data: { iataCode: iata, name: 'Again', timeZoneId: 'Asia/Amman' } })).status(), 'duplicate').toBe(409);
	expect((await call('POST', `${api}/desks`, { token: admin, data: { checkpointId: ids.checkpoint, code: 'C01', kind: 'Counter' } })).status(), 'counter at immigration').toBe(400);
	expect((await call('POST', `${api}/checkpoints`, { token: admin, data: { levelId: ids.level, code: 'X1', name: 'x', kind: '2' } })).status(), 'kind as a number').toBe(400);
	expect((await call('DELETE', `${api}/checkpoints/${ids.checkpoint}`, { token: admin })).status(), 'checkpoint with desks').toBe(409);
});

test('every entity reads, updates and soft deletes', async () => {
	for (const [entity, id, update] of [
		['airports', ids.airport, { name: `E2E ${iata} renamed`, timeZoneId: 'Asia/Dubai' }],
		['terminals', ids.t1, { name: 'Terminal 1 renamed' }],
		['levels', ids.level, { name: 'Arrivals renamed', floorNumber: 0, widthMetres: 310, depthMetres: 120 }],
		['checkpoints', ids.checkpoint, { name: 'Immigration renamed' }],
		['desks', ids.desk, { name: 'Desk one', laneCategories: ['VIS'], inService: false }]
	] as const) {
		expect((await call('GET', `${api}/${entity}/${id}`, { token: admin })).status(), `${entity} get`).toBe(200);
		const updated = await call('PUT', `${api}/${entity}/${id}`, { token: admin, data: update });
		expect(updated.status(), `${entity} update: ${await updated.text()}`).toBe(200);
		expect((await updated.json()).name).toBe(update.name);
		const search = await call('GET', `${api}/${entity}?text=${encodeURIComponent(update.name.split(' ')[0])}`, { token: admin });
		expect(search.status(), `${entity} search`).toBe(200);
	}

	expect((await call('DELETE', `${api}/desks/${ids.desk}`, { token: admin })).status()).toBe(204);
	expect((await call('GET', `${api}/desks/${ids.desk}`, { token: admin })).status(), 'deleted').toBe(404);
	expect((await call('POST', `${api}/desks`, { token: admin, data: { checkpointId: ids.checkpoint, code: 'D01', kind: 'Desk', laneCategories: ['CIT'] } })).status(), 'code free again').toBe(201);
	expect((await call('DELETE', `${api}/terminals/${ids.t2}`, { token: admin })).status()).toBe(204);

	const audit = await call('GET', `${hosts.main}/api/v1/admin/audit-entries?targetId=${ids.desk}`, { token: admin });
	expect(((await audit.json()).data as { action: string }[]).map((e) => e.action)).toEqual(['Desk.Deleted', 'Desk.Updated', 'Desk.Created']);
});

test("another site's records answer 404 without data, and only administrators write", async () => {
	const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
	const terminal = (await signIn(accounts().TerminalDutyManager)).accessToken;

	expect((await call('GET', `${api}/levels/${ids.level}`, { token: border })).status(), 'own site').toBe(200);
	for (const [entity, id] of [['terminals', ids.t1], ['levels', ids.level], ['checkpoints', ids.checkpoint]]) {
		const response = await call('GET', `${api}/${entity}/${id}`, { token: terminal });
		expect(response.status(), `${entity} of E2E1 for the E2E2 manager`).toBe(404);
		expect(await response.text()).not.toContain(ids.level);
	}
	const listed = await call('GET', `${api}/terminals?parentId=${ids.airport}`, { token: terminal });
	expect((await listed.json()).totalCount, 'E2E1 terminals are not listed for an E2E2 manager').toBe(0);
	expect((await call('POST', `${api}/levels`, { token: border, data: { terminalId: ids.t1, code: 'L5', name: 'x', floorNumber: 5, widthMetres: 10, depthMetres: 10 } })).status()).toBe(403);
	expect((await call('DELETE', `${api}/airports/${ids.airport}`, { token: border })).status()).toBe(403);
});

test('injection and markup payloads are refused or stored as inert text', async () => {
	expect((await call('POST', `${api}/desks`, { token: admin, data: { checkpointId: ids.checkpoint, code: "D9' OR '1'='1", kind: 'Desk', laneCategories: ['CIT'] } })).status()).toBe(400);
	expect((await call('POST', `${api}/desks`, { token: admin, data: { checkpointId: ids.checkpoint, code: 'D9<script>', kind: 'Desk', laneCategories: ['CIT'] } })).status()).toBe(400);
	const markup = await call('POST', `${api}/desks`, { token: admin, data: { checkpointId: ids.checkpoint, code: 'D10', name: '<img src=x onerror=alert(1)>', kind: 'Desk', laneCategories: ['CIT'] } });
	expect(markup.status()).toBe(201);
	expect((await markup.json()).name, 'stored and returned as JSON text, never rendered by the API').toBe('<img src=x onerror=alert(1)>');
	expect(markup.headers()['content-type']).toContain('application/json');
	const search = await call('GET', `${api}/desks?text=${encodeURIComponent("' OR 1=1 --")}`, { token: admin });
	expect(search.status()).toBe(200);
	expect((await search.json()).totalCount).toBe(0);
	expect((await call('GET', `${api}/desks?sortBy=${encodeURIComponent('code; DROP TABLE desk')}`, { token: admin })).status()).toBe(400);
	expect((await call('GET', `${api}/desks?pageSize=501`, { token: admin })).status()).toBe(400);
});
