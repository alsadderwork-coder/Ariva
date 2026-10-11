import { createHash } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, signIn, unusedTotpCode, type Account } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-122a: a site's configuration exported as one bundle and imported into an empty site, through the API. Export and
// import are critical actions (SiteConfiguration.Transfer, system administrators, a second factor in the last 15
// minutes), site-scoped (404 outside the caller's sites, nothing echoed) and audited. The bundle's manifest holds each
// part's SHA-256; a tampered part, hash, part list or version is refused with a fixed text, an oversized body answers
// 413 and a deeply nested one 400. The round trip between two sites gives the same portable geometry hash once the
// imported draft is published (F22 v1 differs: it names levels and desks by their ids). Names made of markup or SQL
// travel as data and are never echoed in an error.
// The sites are new each run (codes CFA, CFB and CFC with a random tail; no other suite uses them). Everything the
// suite creates is removed at the end except what Ariva never deletes: the sites themselves and the published zone
// profile versions (immutable by design, script 0012).

test.skip(!databaseAvailable, 'site configuration needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const random = (n: number) => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');
const tail = random(4);
const source = `CFA${tail}`;
const target = `CFB${tail}`;
const spare = `CFC${tail}`;
const sourceIata = random(3);
let targetIata = random(3);
while (targetIata === sourceIata) targetIata = random(3);
const configuration = (site: string) => `${admin}/sites/${site}/configuration`;
const hostileTerminal = '<script>alert(1)</script>';
const hostileDesk = "' OR '1'='1";
const hostileZone = '<img src=x onerror=alert(1)>';
const echoes = ['<script', 'onerror', "'1'='1", 'alert(1)', '../../etc'];

let stepped: string;
let passwordOnly: string;
let scoped: string;
let duty: string;
let exported: any;
const airports: string[] = [];

const sha256 = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');
const decoded = (bundle: any, part: string) => JSON.parse(Buffer.from(bundle.parts[part], 'base64').toString('utf8'));

/** A copy of the bundle with one part replaced and its manifest entry recomputed: anyone can, so the manifest proves no author. */
function rehashed(bundle: any, part: string, bytes: Buffer) {
	return { ...bundle, manifest: { ...bundle.manifest, [part]: sha256(bytes) }, parts: { ...bundle.parts, [part]: bytes.toString('base64') } };
}

async function secondFactor(account: Account): Promise<string> {
	const signedIn = await login(account.userName, account.password, undefined, undefined, { code: await unusedTotpCode(account.totpSecret!) });
	expect(signedIn.status(), `sign-in of ${account.userName}`).toBe(200);
	return (await signedIn.json()).accessToken;
}

async function create(path: string, data: unknown): Promise<string> {
	const response = await call('POST', `${admin}/${path}`, { token: stepped, data });
	expect(response.status(), response.status() === 201 ? path : await response.text()).toBe(201);
	const json = await response.json();
	return (json.id ?? json.profile?.id) as string;
}

async function publish(draft: string): Promise<any> {
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token: stepped })).json();
	const published = await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token: stepped, data: { geometryHash } });
	expect(published.status(), published.status() === 200 ? 'publish' : await published.text()).toBe(200);
	return published.json();
}

/** Every record of a kind at a site (topology searches page at most 500). */
async function search(entity: string, site: string): Promise<any[]> {
	const response = await call('GET', `${admin}/${entity}?siteCode=${site}&pageSize=500`, { token: stepped });
	return response.status() === 200 ? (await response.json()).data : [];
}

test.beforeAll(async () => {
	stepped = await secondFactor(accounts().siteConfigAdmin);
	scoped = await secondFactor(accounts().siteConfigScopedAdmin);
	passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;
	duty = (await signIn(accounts().TerminalDutyManager)).accessToken;

	for (const site of [source, target, spare]) {
		const created = await call('POST', `${admin}/sites`, { token: stepped, data: { code: site, name: `E2E site configuration ${site}` } });
		expect(created.status(), created.status() === 201 ? site : await created.text()).toBe(201);
	}
	for (const iata of [sourceIata, targetIata]) {
		await create('airports', { iataCode: iata, name: `E2E site configuration ${iata}`, timeZoneId: 'Asia/Dubai' });
		airports.push(iata);
	}

	const airportId = (await (await call('GET', `${admin}/airports?text=${sourceIata}&pageSize=500`, { token: stepped })).json()).data.find((a: any) => a.iataCode === sourceIata).id;
	const terminal = await create('terminals', { airportId, code: 'T1', name: hostileTerminal, siteCode: source });
	const arrivals = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	const departures = await create('levels', { terminalId: terminal, code: 'L1', name: 'Departures', floorNumber: 1, widthMetres: 80, depthMetres: 40 });
	const immigration = await create('checkpoints', { levelId: arrivals, code: 'IMM', name: 'Immigration', kind: 'Immigration' });
	const desk = await create('desks', { checkpointId: immigration, code: 'D01', name: hostileDesk, kind: 'Desk', laneCategories: ['CIT'] });
	await create('desks', { checkpointId: immigration, code: 'EG01', kind: 'EGate', laneCategories: ['EG'] });
	const checkIn = await create('checkpoints', { levelId: departures, code: 'CHK', name: 'Check-in', kind: 'CheckIn' });
	await create('desks', { checkpointId: checkIn, code: 'C01', kind: 'Counter' });
	await create('desk-code-mappings', { system: 'Aman', externalCode: 'IMM-01', deskId: desk });

	const draft = await create('zone-profiles/drafts', { siteCode: source, name: 'Arrivals' });
	const queue = await create(`zone-profiles/${draft}/zones`, { name: 'Snake A', kind: 'Queue', levelId: arrivals, polygon: '10 10,34 10,34 22,10 22', laneCategory: 'CIT' });
	await create(`zone-profiles/${draft}/zones`, { name: hostileZone, kind: 'Service', levelId: arrivals, queueZoneId: queue, deskId: desk, polygon: '40 10,44 10,44 14,40 14' });
	await create(`zone-profiles/${draft}/lines`, { name: 'Entry A', role: 'Entry', levelId: arrivals, startX: 10, startY: 12, endX: 10, endY: 16, zoneId: queue });
	await create(`zone-profiles/${draft}/lines`, { name: 'Exit A', role: 'Exit', levelId: arrivals, startX: 30, startY: 22, endX: 34, endY: 22, zoneId: queue });
	await create(`zone-profiles/${draft}/lines`, { name: 'Count hall', role: 'Count', levelId: departures, startX: 1, startY: 1, endX: 1, endY: 9 });
	await publish(draft);

	const form = new FormData();
	form.append('file', new Blob(['<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="10" height="10"/></svg>'], { type: 'image/svg+xml' }), 'arrivals.svg');
	form.append('metresPerPixel', '0.05');
	form.append('originX', '0');
	form.append('originY', '0');
	const plan = await call('POST', `${admin}/levels/${arrivals}/floor-plan`, { token: stepped, form });
	expect(plan.status(), plan.status() === 201 ? 'floor plan' : await plan.text()).toBe(201);
});

test.afterAll(async () => {
	if (!stepped) return;
	for (const site of [source, target, spare]) {
		for (const plan of await (async () => {
			const response = await call('GET', `${admin}/floor-plans?siteCode=${site}`, { token: stepped });
			return response.status() === 200 ? await response.json() : [];
		})())
			await call('DELETE', `${admin}/levels/${plan.levelId}/floor-plan`, { token: stepped });
		for (const mapping of await search('desk-code-mappings', site)) await call('DELETE', `${admin}/desk-code-mappings/${mapping.id}`, { token: stepped });
		const history = await call('GET', `${admin}/zone-profiles?siteCode=${site}`, { token: stepped });
		for (const draft of history.status() === 200 ? (await history.json()).filter((p: any) => p.status === 'Draft') : [])
			await call('DELETE', `${admin}/zone-profiles/${draft.id}`, { token: stepped });
		for (const entity of ['desks', 'checkpoints', 'levels', 'terminals'])
			for (const record of await search(entity, site)) await call('DELETE', `${admin}/${entity}/${record.id}`, { token: stepped });
	}
	const list = await call('GET', `${admin}/airports?pageSize=500`, { token: stepped });
	for (const airport of list.status() === 200 ? (await list.json()).data.filter((a: any) => airports.includes(a.iataCode)) : [])
		await call('DELETE', `${admin}/airports/${airport.id}`, { token: stepped });
});

test('only an administrator with a recent second factor reaches either action, and only at its own sites', async () => {
	for (const [method, data] of [
		['GET', undefined],
		['POST', {}]
	] as const) {
		const what = `${method} configuration`;
		expect((await call(method, configuration(source), { data })).status(), `${what} anonymous`).toBe(401);
		expect((await call(method, configuration(source), { token: duty, data })).status(), `${what} without the permission`).toBe(403);

		const stale = await call(method, configuration(source), { token: passwordOnly, data });
		expect(stale.status(), `${what} without a recent second factor`).toBe(401);
		expect(stale.headers()['www-authenticate'], what).toContain('error="insufficient_user_authentication"');
		expect(stale.headers()['www-authenticate'], what).toContain('max_age=900');
		expect((await stale.json()).error, what).toBe('mfa_required');

		for (const site of [source, 'ZZ9']) {
			const away = await call(method, configuration(site), { token: scoped, data: method === 'POST' ? { formatVersion: 1 } : undefined });
			expect(away.status(), `${what} at ${site} out of reach`).toBe(404);
			expect(await away.text(), 'nothing echoed').not.toContain(site);
		}
	}
});

test('the export carries the published profile with both hashes, every part in the manifest and no secret', async () => {
	const response = await call('GET', configuration(source), { token: stepped });
	expect(response.status(), response.status() === 200 ? 'export' : await response.text()).toBe(200);
	expect(response.headers()['cache-control']).toContain('no-store');
	const text = await response.text();
	for (const secret of ['ardk_', 'ardp_']) expect(text.includes(secret), `no ${secret.slice(0, 4)} credential in an export`).toBe(false);
	for (const fragment of echoes) expect(text, 'markup and SQL travel only inside base64 parts').not.toContain(fragment);
	exported = JSON.parse(text);

	expect(exported.bundleSha256).toMatch(/^[0-9a-f]{64}$/);
	expect(exported.zoneProfileIncluded).toBe(true);
	expect(exported.geometryHash).toMatch(/^[0-9a-f]{64}$/);
	expect(exported.portableGeometryHash).toMatch(/^[0-9a-f]{64}$/);
	expect(exported.portableGeometryHash).not.toBe(exported.geometryHash);
	expect(exported.counts).toEqual({ terminals: 1, levels: 2, checkpoints: 2, desks: 3, deskCodeMappings: 1, zones: 2, lines: 3, floorPlans: 1 });
	const bundle = exported.bundle;
	expect(bundle.formatVersion).toBe(1);
	expect(bundle.sourceSite).toBe(source);
	expect(Object.keys(bundle.manifest).sort()).toEqual(['deskCodeMappings', 'floorPlanFile.1', 'floorPlans', 'topology', 'zoneProfile']);
	expect(Object.keys(bundle.parts).sort()).toEqual(Object.keys(bundle.manifest).sort());
	for (const [part, hash] of Object.entries(bundle.manifest)) expect(sha256(Buffer.from(bundle.parts[part], 'base64')), part).toBe(hash);
	// Hostile names are data inside the parts; no id travels.
	expect(decoded(bundle, 'topology').terminals[0]).toEqual({ airport: sourceIata, code: 'T1', name: hostileTerminal });
	expect(decoded(bundle, 'zoneProfile')[0].portableGeometryHash).toBe(exported.portableGeometryHash);
	expect(Buffer.from(bundle.parts.topology, 'base64').toString('utf8')).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/);
});

test('a tampered bundle is refused with a fixed text and writes nothing', async () => {
	const bundle = exported.bundle;
	const devices = Buffer.from('[]');
	const cases: [string, any, number][] = [
		['a part changed', { ...bundle, parts: { ...bundle.parts, topology: Buffer.from('{"terminals":[],"levels":[],"checkpoints":[],"desks":[]}').toString('base64') } }, 400],
		['a hash changed', { ...bundle, manifest: { ...bundle.manifest, zoneProfile: '0'.repeat(64) } }, 400],
		['a part missing', { ...bundle, parts: Object.fromEntries(Object.entries(bundle.parts).filter(([name]) => name !== 'floorPlans')) }, 400],
		['an entry without its part', { ...bundle, manifest: { ...bundle.manifest, 'floorPlanFile.2': '0'.repeat(64) } }, 400],
		['a later story part', { ...bundle, manifest: { ...bundle.manifest, devices: sha256(devices) }, parts: { ...bundle.parts, devices: devices.toString('base64') } }, 400],
		['an unknown part named with markup', { ...bundle, manifest: { ...bundle.manifest, [hostileTerminal]: sha256(devices) }, parts: { ...bundle.parts, [hostileTerminal]: devices.toString('base64') } }, 400],
		['format version 2', { ...bundle, formatVersion: 2 }, 400],
		['a zone profile that names a level outside the bundle', rehashed(bundle, 'zoneProfile', Buffer.from(JSON.stringify(decoded(bundle, 'zoneProfile').map((p: any) => ({ ...p, zones: p.zones.map((z: any) => ({ ...z, level: hostileTerminal })) }))))), 400],
		['a portable hash the profile does not reproduce', rehashed(bundle, 'zoneProfile', Buffer.from(JSON.stringify(decoded(bundle, 'zoneProfile').map((p: any) => ({ ...p, portableGeometryHash: '1'.repeat(64) }))))), 400],
		['a floor plan that is not an image', rehashed(bundle, 'floorPlanFile.1', Buffer.from(`<html>${hostileTerminal}</html>`)), 400],
		['a floor plan named with a path', rehashed(bundle, 'floorPlans', Buffer.from(JSON.stringify(decoded(bundle, 'floorPlans').map((p: any) => ({ ...p, fileName: '../../etc/x.svg' }))))), 400]
	];
	for (const [what, body, status] of cases) {
		const refused = await call('POST', `${configuration(target)}?airportCode=${targetIata}`, { token: stepped, data: body });
		const text = await refused.text();
		expect(refused.status(), refused.status() === status ? what : text).toBe(status);
		for (const fragment of [...echoes, target]) expect(text, `${what}: nothing echoed`).not.toContain(fragment);
	}

	const empty = await (await call('GET', configuration(target), { token: stepped })).json();
	expect(empty.counts, 'the target is still empty').toEqual({ terminals: 0, levels: 0, checkpoints: 0, desks: 0, deskCodeMappings: 0, zones: 0, lines: 0, floorPlans: 0 });
	expect(empty.zoneProfileIncluded).toBe(false);
});

test('an oversized body answers 413 and a deeply nested one 400', async () => {
	const headers = { 'Content-Type': 'application/json' };
	const oversized = `{"formatVersion":1,"sourceSite":"${source}","parts":{"topology":"${'A'.repeat(21 * 1024 * 1024)}"}}`;
	const tooLarge = await call('POST', configuration(spare), { token: stepped, raw: oversized, headers });
	expect(tooLarge.status(), tooLarge.status() === 413 ? 'body over 20 MB' : await tooLarge.text()).toBe(413);

	const bigPart = rehashed(exported.bundle, 'deskCodeMappings', Buffer.alloc(9 * 1024 * 1024, 0x20));
	const partTooLarge = await call('POST', configuration(spare), { token: stepped, data: bigPart });
	expect(partTooLarge.status(), partTooLarge.status() === 413 ? 'a configuration part over 8 MB' : await partTooLarge.text()).toBe(413);

	const deep = `{"formatVersion":1,"manifest":${'{"a":'.repeat(40)}1${'}'.repeat(40)}}`;
	const nested = await call('POST', configuration(spare), { token: stepped, raw: deep, headers });
	expect(nested.status(), nested.status() === 400 ? 'body 41 levels deep' : await nested.text()).toBe(400);

	const deepPart = rehashed(exported.bundle, 'deskCodeMappings', Buffer.from(`${'['.repeat(40)}${']'.repeat(40)}`));
	const nestedPart = await call('POST', configuration(spare), { token: stepped, data: deepPart });
	expect(nestedPart.status(), nestedPart.status() === 400 ? 'a part 40 levels deep' : await nestedPart.text()).toBe(400);
});

test('the airport is checked as an IATA code that exists and is never created', async () => {
	for (const airportCode of ['zz', '<b>X</b>', 'ABCD']) {
		const refused = await call('POST', `${configuration(spare)}?airportCode=${encodeURIComponent(airportCode)}`, { token: stepped, data: exported.bundle });
		const text = await refused.text();
		expect(refused.status(), refused.status() === 400 ? airportCode : text).toBe(400);
		expect(text).not.toContain('<b>');
	}
	let unknown = random(3);
	while (unknown === sourceIata || unknown === targetIata) unknown = random(3);
	const missing = await call('POST', `${configuration(spare)}?airportCode=${unknown}`, { token: stepped, data: exported.bundle });
	const text = await missing.text();
	expect(missing.status(), missing.status() === 400 ? 'unknown airport' : text).toBe(400);
	expect(text).not.toContain(unknown);
	const airportsNow = await (await call('GET', `${admin}/airports?text=${unknown}&pageSize=500`, { token: stepped })).json();
	expect(airportsNow.data.filter((a: any) => a.iataCode === unknown), 'no airport is created').toHaveLength(0);
});

test('a round trip between two sites gives the same portable geometry hash once the draft is published', async () => {
	const imported = await call('POST', `${configuration(target)}?airportCode=${targetIata}`, { token: stepped, data: exported.bundle });
	expect(imported.status(), imported.status() === 201 ? 'import' : await imported.text()).toBe(201);
	expect(imported.headers()['cache-control']).toContain('no-store');
	const answer = await imported.json();
	expect(answer.bundleSha256, 'the operator compares the two hashes out of band').toBe(exported.bundleSha256);
	expect(answer.sourceSite).toBe(source);
	expect(answer.counts).toEqual(exported.counts);
	expect(answer.portableGeometryHash).toBe(exported.portableGeometryHash);
	expect(answer.draftProfileId).toMatch(/^[0-9a-f-]{36}$/);

	// Hostile names arrive as data and leave as a JSON string value (application/json with nosniff, the APIs' CWE-79 control).
	const terminals = await call('GET', `${admin}/terminals?siteCode=${target}`, { token: stepped });
	expect(terminals.headers()['content-type']).toContain('application/json');
	expect(terminals.headers()['x-content-type-options']).toBe('nosniff');
	expect((await terminals.json()).data[0]).toMatchObject({ code: 'T1', name: hostileTerminal, airportIataCode: targetIata });
	const draft = await (await call('GET', `${admin}/zone-profiles/${answer.draftProfileId}`, { token: stepped })).json();
	expect(draft.profile.status, 'the profile arrives as a draft').toBe('Draft');
	expect(draft.zones.map((z: any) => z.name).sort()).toEqual([hostileZone, 'Snake A'].sort());

	const published = await publish(answer.draftProfileId);
	const again = await call('GET', configuration(target), { token: stepped });
	expect(again.status(), again.status() === 200 ? 'export of the copy' : await again.text()).toBe(200);
	const copy = await again.json();
	expect(copy.portableGeometryHash, 'the same portable geometry hash').toBe(exported.portableGeometryHash);
	expect(published.geometryHash, 'F22 v1 names levels and desks by their new ids').not.toBe(exported.geometryHash);
	expect(copy.counts).toEqual(exported.counts);
});

test('an import into a site that is not empty answers 409', async () => {
	for (const site of [source, target]) {
		const refused = await call('POST', `${configuration(site)}?airportCode=${targetIata}`, { token: stepped, data: exported.bundle });
		const text = await refused.text();
		expect(refused.status(), refused.status() === 409 ? site : text).toBe(409);
		for (const fragment of echoes) expect(text).not.toContain(fragment);
	}
});
