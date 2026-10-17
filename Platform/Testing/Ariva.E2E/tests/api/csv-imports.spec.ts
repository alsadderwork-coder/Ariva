import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, signIn, unusedTotpCode, type Account } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-123a: CSV imports of devices and desk code mappings through the API, each a preview then a commit. The device
// import needs CreateDevice and, both steps, a second factor in the last 15 minutes (registering devices is critical);
// the mapping import needs CreateDeskCodeMapping and no second factor, like the single mapping endpoint. Both are
// site-scoped (404 outside the caller's sites, nothing echoed). A malformed, oversized or formula-prefixed file is
// refused with fixed texts naming rows and columns, never a cell. A preview token works only for the file, site and
// user of its preview. A commit registers the devices in Commissioning and returns each new credential once; a
// credential is checked by its format inside an expect and never printed (ARV-152).
// The sites are new each run (codes CIA and CIB with a random tail; no other suite uses them). Everything the suite
// creates is removed at the end except the sites and the published zone profile versions.

test.skip(!databaseAvailable, 'the CSV imports need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const random = (n: number) => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');
const tail = random(4);
const site = `CIA${tail}`;
const otherSite = `CIB${tail}`;
const devices = (code: string) => `${admin}/sites/${code}/device-imports`;
const mappings = (code: string) => `${admin}/sites/${code}/desk-code-mapping-imports`;
const deviceHeader = 'code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone';
const hostileModel = '<script>alert(1)</script>';
const formula = '=HYPERLINK("http://example.invalid","x")';
const echoes = ['<script', 'alert(1)', 'HYPERLINK', 'example.invalid', 'calc'];
/** A device credential: the marker and 43 base64url characters. Tested inside an expect, never printed. */
const credentialFormat = /^ardk_[A-Za-z0-9_-]{43}$/;

let stepped: string;
let other: string;
let scoped: string;
let passwordOnly: string;
let handler: string;
let duty: string;
const airports: string[] = [];

const row = (code: string, model = 'PC2SE', x = 20) => `${code},StereoVision,"${model.replaceAll('"', '""')}",HttpsPush,Xovis,Ntp,T1,L0,${x},16,5,0,Snake A`;
const csv = (...rows: string[]) => `${deviceHeader}\r\n${rows.join('\r\n')}\r\n`;

/** A multipart body: the file, and the preview's token when committing. */
function form(text: string | Uint8Array, token?: string): FormData {
	const body = new FormData();
	body.append('file', new Blob([text], { type: 'text/csv' }), 'import.csv');
	if (token !== undefined) body.append('previewToken', token);
	return body;
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

/** Every record of a kind at a site (searches page at most 500). */
async function search(entity: string, code: string): Promise<any[]> {
	const response = await call('GET', `${admin}/${entity}?siteCode=${code}&pageSize=500`, { token: stepped });
	return response.status() === 200 ? (await response.json()).data : [];
}

/** A site with one level, a published queue zone Snake A, an immigration checkpoint (desk D01, e-gate EG01) and a check-in counter. */
async function siteWithTopology(code: string): Promise<void> {
	const created = await call('POST', `${admin}/sites`, { token: stepped, data: { code, name: `E2E CSV imports ${code}` } });
	expect(created.status(), created.status() === 201 ? code : await created.text()).toBe(201);
	let iata = random(3);
	while (airports.includes(iata)) iata = random(3);
	const airportId = await create('airports', { iataCode: iata, name: `E2E CSV imports ${iata}`, timeZoneId: 'Asia/Dubai' });
	airports.push(iata);
	const terminal = await create('terminals', { airportId, code: 'T1', name: 'Terminal 1', siteCode: code });
	const level = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	const immigration = await create('checkpoints', { levelId: level, code: 'IMM', name: 'Immigration', kind: 'Immigration' });
	await create('desks', { checkpointId: immigration, code: 'D01', kind: 'Desk', laneCategories: ['CIT'] });
	await create('desks', { checkpointId: immigration, code: 'EG01', kind: 'EGate', laneCategories: ['EG'] });
	const checkIn = await create('checkpoints', { levelId: level, code: 'CHK', name: 'Check-in', kind: 'CheckIn' });
	await create('desks', { checkpointId: checkIn, code: 'C01', kind: 'Counter' });
	const draft = await create('zone-profiles/drafts', { siteCode: code, name: 'Arrivals' });
	const queue = await create(`zone-profiles/${draft}/zones`, { name: 'Snake A', kind: 'Queue', levelId: level, polygon: '10 10,34 10,34 22,10 22' });
	await create(`zone-profiles/${draft}/lines`, { name: 'Entry A', role: 'Entry', levelId: level, startX: 10, startY: 12, endX: 10, endY: 16, zoneId: queue });
	await create(`zone-profiles/${draft}/lines`, { name: 'Exit A', role: 'Exit', levelId: level, startX: 30, startY: 22, endX: 34, endY: 22, zoneId: queue });
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token: stepped })).json();
	const published = await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token: stepped, data: { geometryHash } });
	expect(published.status(), published.status() === 200 ? 'publish' : await published.text()).toBe(200);
}

test.beforeAll(async () => {
	stepped = await secondFactor(accounts().csvImportAdmin);
	other = await secondFactor(accounts().csvImportOtherAdmin);
	scoped = await secondFactor(accounts().csvImportScopedAdmin);
	passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;
	handler = (await signIn(accounts().HandlerStationManager)).accessToken;
	duty = (await signIn(accounts().TerminalDutyManager)).accessToken;
	await siteWithTopology(site);
	await siteWithTopology(otherSite);
});

test.afterAll(async () => {
	if (!stepped) return;
	for (const code of [site, otherSite]) {
		for (const device of await search('devices', code)) await call('DELETE', `${admin}/devices/${device.id}`, { token: stepped });
		for (const mapping of await search('desk-code-mappings', code)) await call('DELETE', `${admin}/desk-code-mappings/${mapping.id}`, { token: stepped });
		const history = await call('GET', `${admin}/zone-profiles?siteCode=${code}`, { token: stepped });
		for (const draft of history.status() === 200 ? (await history.json()).filter((p: any) => p.status === 'Draft') : [])
			await call('DELETE', `${admin}/zone-profiles/${draft.id}`, { token: stepped });
		for (const entity of ['desks', 'checkpoints', 'levels', 'terminals'])
			for (const record of await search(entity, code)) await call('DELETE', `${admin}/${entity}/${record.id}`, { token: stepped });
	}
	const list = await call('GET', `${admin}/airports?pageSize=500`, { token: stepped });
	for (const airport of list.status() === 200 ? (await list.json()).data.filter((a: any) => airports.includes(a.iataCode)) : [])
		await call('DELETE', `${admin}/airports/${airport.id}`, { token: stepped });
});

test('each step needs its permission, the device import a recent second factor, and the caller one of its sites', async () => {
	for (const step of ['preview', 'commit']) {
		for (const [url, refusedRole] of [
			[`${devices(site)}/${step}`, handler],
			[`${mappings(site)}/${step}`, duty]
		] as const) {
			const body = () => form(csv(row('S-01')), step === 'commit' ? 'token' : undefined);
			expect((await call('POST', url, { form: body() })).status(), `${url} anonymous`).toBe(401);
			expect((await call('POST', url, { token: refusedRole, form: body() })).status(), `${url} without the permission`).toBe(403);
			for (const code of [site, 'ZZ9']) {
				const away = await call('POST', url.replace(site, code), { token: scoped, form: body() });
				expect(away.status(), `${url} at a site out of reach`).toBe(404);
				expect(await away.text(), 'nothing echoed').not.toContain(code);
			}
		}

		const stale = await call('POST', `${devices(site)}/${step}`, { token: passwordOnly, form: form(csv(row('S-01')), step === 'commit' ? 'token' : undefined) });
		expect(stale.status(), `device ${step} without a recent second factor`).toBe(401);
		expect(stale.headers()['www-authenticate']).toContain('error="insufficient_user_authentication"');
	}

	// The mapping preview needs no second factor, like the single mapping endpoint.
	const mappingPreview = await call('POST', `${mappings(site)}/preview`, { token: passwordOnly, form: form('externalSystem,externalCode,checkpoint,desk\nAman,E2E-01,IMM,D01\n') });
	expect(mappingPreview.status(), mappingPreview.status() === 200 ? 'mapping preview' : await mappingPreview.text()).toBe(200);
});

test('a malformed or oversized file is refused with a fixed text that echoes nothing', async () => {
	const cases: [string, FormData, number][] = [
		['no file', new FormData(), 400],
		['an empty file', form(''), 400],
		['a header that is not the schema', form(`${deviceHeader.replace('model', formula)}\n${row('S-01')}\n`), 400],
		['a header without rows', form(`${deviceHeader}\n`), 400],
		['UTF-16', form(new Uint8Array([0xff, 0xfe, ...Array.from(Buffer.from(csv(row('S-01')), 'utf16le'))])), 400],
		['one byte over 1 MB', form(new Uint8Array(1024 * 1024 + 1).fill(0x61)), 413],
		['501 device rows', form(csv(...Array.from({ length: 501 }, (_, i) => row(`S-${i + 1}`)))), 400]
	];
	for (const [what, body, status] of cases) {
		const refused = await call('POST', `${devices(site)}/preview`, { token: stepped, form: body });
		const text = await refused.text();
		expect(refused.status(), refused.status() === status ? what : text).toBe(status);
		for (const fragment of echoes) expect(text, `${what}: nothing echoed`).not.toContain(fragment);
	}

	// Past the request limit the form is never read: Kestrel answers 413 from the Content-Length, or the form reader stops
	// at its own limit and the request is refused as one without a readable form (400), or the client sees the connection
	// closed while it is still sending (as the SSIM import's spec notes).
	const tooLarge = await call('POST', `${devices(site)}/preview`, { token: stepped, form: form(new Uint8Array(3 * 1024 * 1024).fill(0x61)) }).then(
		(r) => r.status(),
		(e: Error) => `closed: ${e.message}`
	);
	expect([400, 413, 'closed: fetch failed'], 'a body over the request limit').toContain(tooLarge);
	expect((await search('devices', site)).length, 'nothing written').toBe(0);
});

test('formula-prefixed, malformed and hostile cells are answered by row and column, as fixed texts', async () => {
	const response = await call('POST', `${devices(site)}/preview`, {
		token: stepped,
		form: form(csv(row('S-01', hostileModel), row('S-02', formula), row('S-03').replace(',20,', ',+20,'), row('S-04').replace(',20,', ',-5,'), 'S-05,"bad"quote,x', row('S-06', '@SUM(A1)'), row('S-07', 'x', 90)))
	});
	const text = await response.text();
	expect(response.status(), response.status() === 200 ? 'preview' : text).toBe(200);
	expect(response.headers()['content-type']).toContain('application/json');
	expect(response.headers()['x-content-type-options']).toBe('nosniff');
	const preview = JSON.parse(text);
	expect(preview.previewToken, 'no token for a file with a refused row').toBeNull();
	expect(preview.devices.map((d: any) => d.code)).toEqual(['S-01']);
	expect(preview.devices[0].model, 'a hostile value travels as data').toBe(hostileModel);
	expect(preview.errors.map((e: any) => [e.row, e.column])).toEqual([
		[3, 3],
		[4, 9],
		[5, 9],
		[6, null],
		[7, 3],
		[8, 9]
	]);
	expect(preview.errorCount).toBe(6);
	const reasons = preview.errors.map((e: any) => e.reason).join('\n');
	for (const fragment of echoes) expect(reasons, 'no reason repeats a cell').not.toContain(fragment);
	expect((await search('devices', site)).length, 'a preview writes nothing').toBe(0);
});

test('a token works only for the file, site and user of its preview', async () => {
	const file = csv(row('S-11'), row('S-12', 'PC2SE', 22));
	const previewed = await call('POST', `${devices(site)}/preview`, { token: stepped, form: form(file) });
	expect(previewed.status(), previewed.status() === 200 ? 'preview' : await previewed.text()).toBe(200);
	const { previewToken } = await previewed.json();
	expect(typeof previewToken).toBe('string');

	for (const [what, url, token, body] of [
		['another user', `${devices(site)}/commit`, other, form(file, previewToken)],
		['another site', `${devices(otherSite)}/commit`, stepped, form(file, previewToken)],
		['another file', `${devices(site)}/commit`, stepped, form(file.replace('S-12', 'S-13'), previewToken)],
		['a mapping commit', `${mappings(site)}/commit`, stepped, form(file, previewToken)]
	] as const) {
		const refused = await call('POST', url, { token, form: body });
		expect(refused.status(), refused.status() === 409 ? what : await refused.text()).toBe(409);
	}
	const shaped = await call('POST', `${devices(site)}/commit`, { token: stepped, form: form(file, '<script>') });
	expect(shaped.status(), 'a token of another shape').toBe(400);
	expect(await shaped.text()).not.toContain('<script');
	expect((await search('devices', site)).length + (await search('devices', otherSite)).length, 'nothing written').toBe(0);
});

test('a commit registers the devices in Commissioning and returns each credential once', async () => {
	const file = csv(row('S-21', hostileModel), row('S-22', 'PC2SE', 22));
	const previewed = await call('POST', `${devices(site)}/preview`, { token: stepped, form: form(file) });
	expect(previewed.status(), previewed.status() === 200 ? 'preview' : await previewed.text()).toBe(200);
	const { previewToken } = await previewed.json();

	const committed = await call('POST', `${devices(site)}/commit`, { token: stepped, form: form(file, previewToken) });
	expect(committed.status(), committed.status() === 200 ? 'commit' : await committed.text()).toBe(200);
	expect(committed.headers()['cache-control']).toContain('no-store');
	const answer = await committed.json();
	expect(answer.devices).toBe(2);
	expect(answer.credentials.length, 'one credential per device').toBe(2);
	expect(answer.credentials.map((c: any) => c.code)).toEqual(['S-21', 'S-22']);
	expect(answer.credentials.every((c: any) => credentialFormat.test(c.credential)), 'each credential has the device credential format').toBe(true);

	const registered = await search('devices', site);
	expect(registered.map((d: any) => [d.code, d.state]).sort()).toEqual([
		['S-21', 'Commissioning'],
		['S-22', 'Commissioning']
	]);
	expect(registered.find((d: any) => d.code === 'S-21').model, 'stored as data').toBe(hostileModel);
	const listed = JSON.stringify(registered);
	expect(answer.credentials.some((c: any) => listed.includes(c.credential.slice(13))), 'the devices list never holds a credential').toBe(false);

	// The same token again: the codes are taken now, so nothing more is registered and no credential comes back.
	const again = await call('POST', `${devices(site)}/commit`, { token: stepped, form: form(file, previewToken) });
	const againText = await again.text();
	expect(again.status(), 'a second commit of the same preview').toBe(400);
	expect(/ard[kp]_/.test(againText), 'no credential in a refusal').toBe(false);
	expect((await search('devices', site)).length).toBe(2);
});

test('the mapping import creates every mapping of its preview, and none of a file with a refused row', async () => {
	const file = 'externalSystem,externalCode,checkpoint,desk\r\nAman,E2E-01,IMM,D01\r\nAman,E2E-EG1,IMM,EG01\r\nAodb,E2E/C01,CHK,C01\r\n';
	const previewed = await call('POST', `${mappings(site)}/preview`, { token: passwordOnly, form: form(file) });
	expect(previewed.status(), previewed.status() === 200 ? 'preview' : await previewed.text()).toBe(200);
	const preview = await previewed.json();
	expect(preview.errors).toEqual([]);

	const committed = await call('POST', `${mappings(site)}/commit`, { token: passwordOnly, form: form(file, preview.previewToken) });
	expect(committed.status(), committed.status() === 200 ? 'commit' : await committed.text()).toBe(200);
	expect((await committed.json()).mappings).toBe(3);
	expect((await search('desk-code-mappings', site)).map((m: any) => m.externalCode).sort()).toEqual(['E2E-01', 'E2E-EG1', 'E2E/C01']);

	const refused = await call('POST', `${mappings(site)}/preview`, {
		token: passwordOnly,
		form: form('externalSystem,externalCode,checkpoint,desk\nAman,E2E-01,IMM,D01\nAman,E2E-02,CHK,C01\nAman,"-1+1",IMM,D01\n')
	});
	const text = await refused.text();
	expect(refused.status(), refused.status() === 200 ? 'preview' : text).toBe(200);
	const answer = JSON.parse(text);
	expect(answer.previewToken).toBeNull();
	expect(answer.errors.map((e: any) => [e.row, e.column])).toEqual([
		[2, 2],
		[3, 4],
		[4, 2]
	]);
	expect(text).not.toContain('-1+1');
});
