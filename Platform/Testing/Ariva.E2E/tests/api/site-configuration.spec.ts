import { createHash } from 'node:crypto';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, login, signIn, unusedTotpCode, type Account } from '../support/accounts';
import { createAirport } from '../support/airports';
import { hosts } from '../support/hosts';
import { expectStatus } from '../support/expect-status';

// ARV-122a: a site's configuration exported as one bundle and imported into an empty site, through the API. Export and
// import are critical actions (SiteConfiguration.Transfer, system administrators, a second factor in the last 15
// minutes), site-scoped (404 outside the caller's sites, nothing echoed) and audited. The bundle's manifest holds each
// part's SHA-256; a tampered part, hash, part list or version is refused with a fixed text, an oversized body answers
// 413 and a deeply nested one 400. The round trip between two sites gives the same portable geometry hash once the
// imported draft is published (F22 v1 differs: it names levels and desks by their ids). Names made of markup or SQL
// travel as data and are never echoed in an error.
// ARV-122b: the bundle also carries the devices (without any credential, calibration or access setting), the alert rules
// (without escalation contacts) and the displays (without pairing secrets). An import registers each device through the
// registration path, in Commissioning with a new credential returned once (asserted by its format, never printed);
// rules keep their codes and the answer lists, by code, those whose escalation contact must be typed again; displays
// arrive unpaired; the devices whose access settings (source networks, certificate pin) were withheld are listed by code.
// A display code a live display holds answers 409 and writes nothing, so the source's display is deleted before the
// round trip.
// ARV-181b2: a rule on open desks below recommendation travels when a border role owns it (it imports into the spare site
// with its owner) and is refused with an airport-side owner or escalation role, without an owner, or on an overflow band.
// ARV-187: a predicted nowcast rule is refused the same way (roles only), and a stored one an older release left with an
// airport-side owner refuses the export with a fixed text (409) that names no rule.
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
/** The source's and the target's airport codes, drawn when the airports are created (another code when one is taken). */
let sourceIata = '';
let targetIata = '';
const configuration = (site: string) => `${admin}/sites/${site}/configuration`;
const hostileTerminal = '<script>alert(1)</script>';
const hostileDesk = "' OR '1'='1";
const hostileZone = '<img src=x onerror=alert(1)>';
const hostileRule = '<script>alert(1)</script> rule';
const echoes = ['<script', 'onerror', "'1'='1", 'alert(1)', '../../etc'];
const displayCode = `CFD${tail}`;
const contact = 'Duty officer, extension 4411';
const ingestDevice = `${hosts.ingest}/api/v1/ingest/device`;
/** A device credential: the marker and 43 base64url characters. Tested inside an expect, never printed. */
const deviceCredentialFormat = /^ardk_[A-Za-z0-9_-]{43}$/;
const secretPattern = /ard[kp]_/;

let stepped: string;
let passwordOnly: string;
let scoped: string;
let duty: string;
let exported: any;
let sourceCredential: string;
const airports: string[] = [];

const sha256 = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');

/** The run's database as the migration login (the hosts' own settings), for values only an older release could have stored. */
async function withDatabase<T>(work: (db: pg.Client) => Promise<T>): Promise<T> {
	const db = new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
	await db.connect();
	try {
		return await work(db);
	} finally {
		await db.end();
	}
}
const decoded = (bundle: any, part: string) => JSON.parse(Buffer.from(bundle.parts[part], 'base64').toString('utf8'));

/** A copy of the bundle with one part replaced and its manifest entry recomputed: anyone can, so the manifest proves no author. */
function rehashed(bundle: any, part: string, bytes: Buffer) {
	return { ...bundle, manifest: { ...bundle.manifest, [part]: sha256(bytes) }, parts: { ...bundle.parts, [part]: bytes.toString('base64') } };
}

/** ARV-181b2: the bundle with R-001 turned into an open desks below recommendation rule (2 desks short or more), with changes. */
function desksBelow(bundle: any, changes: Record<string, unknown>) {
	const rules = decoded(bundle, 'alertRules').map((r: any) =>
		r.code === 'R-001' ? { ...r, metric: 'DesksBelowPlan', comparator: 'GreaterOrEqual', threshold: 2, ...changes } : r
	);
	return rehashed(bundle, 'alertRules', Buffer.from(JSON.stringify(rules)));
}

/** ARV-187: R-001 of the bundle as a predicted nowcast rule (border data: a border owner, escalating to a border role or none). */
function predictedBreach(bundle: any, changes: Record<string, unknown>) {
	const rules = decoded(bundle, 'alertRules').map((r: any) =>
		r.code === 'R-001' ? { ...r, metric: 'PredictedNowcast', comparator: 'GreaterThan', threshold: 20, minQueueLength: null, leadMinutes: 30, ...changes } : r
	);
	return rehashed(bundle, 'alertRules', Buffer.from(JSON.stringify(rules)));
}

async function secondFactor(account: Account): Promise<string> {
	const signedIn = await login(account.userName, account.password, undefined, undefined, { code: await unusedTotpCode(account.totpSecret!) });
	expect(signedIn.status(), `sign-in of ${account.userName}`).toBe(200);
	return (await signedIn.json()).accessToken;
}

async function create(path: string, data: unknown): Promise<string> {
	const response = await call('POST', `${admin}/${path}`, { token: stepped, data });
	await expectStatus(response, 201, path);
	const json = await response.json();
	return (json.id ?? json.profile?.id) as string;
}

/** Creates one of the suite's airports under the code createAirport draws. */
const postAirport = (iata: string) =>
	call('POST', `${admin}/airports`, { token: stepped, data: { iataCode: iata, name: `E2E site configuration ${iata}`, timeZoneId: 'Asia/Dubai' } });

async function publish(draft: string): Promise<any> {
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token: stepped })).json();
	const published = await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token: stepped, data: { geometryHash } });
	await expectStatus(published, 200, 'publish');
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
		await expectStatus(created, 201, site);
	}
	sourceIata = (await createAirport(postAirport)).iataCode;
	airports.push(sourceIata);
	targetIata = (await createAirport(postAirport)).iataCode;
	airports.push(targetIata);

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
	const overflow = await create(`zone-profiles/${draft}/zones`, { name: 'Overflow A', kind: 'Overflow', levelId: arrivals, queueZoneId: queue, polygon: '10 24,34 24,34 28,10 28' });
	await create(`zone-profiles/${draft}/lines`, { name: 'Overflow entry A', role: 'OverflowEntry', levelId: arrivals, startX: 10, startY: 25, endX: 10, endY: 27, zoneId: overflow });
	await publish(draft);

	const form = new FormData();
	form.append('file', new Blob(['<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="10" height="10"/></svg>'], { type: 'image/svg+xml' }), 'arrivals.svg');
	form.append('metresPerPixel', '0.05');
	form.append('originX', '0');
	form.append('originY', '0');
	const plan = await call('POST', `${admin}/levels/${arrivals}/floor-plan`, { token: stepped, form });
	await expectStatus(plan, 201, 'floor plan');

	// ARV-122b: a device (its credential kept only to show it never travels), two alert rules (one with an escalation
	// contact) and a display.
	const device = await call('POST', `${admin}/devices`, {
		token: stepped,
		data: {
			code: 'S01', family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Xovis', clockSource: 'Ntp',
			placement: { levelId: arrivals, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: 'Snake A' }
		}
	});
	await expectStatus(device, 201, 'device');
	const registered = await device.json();
	sourceCredential = registered.credential;
	// Source networks: withheld from the bundle (only the fact that there were some travels).
	const access = await call('PUT', `${admin}/devices/${registered.device.id}/access`, { token: stepped, data: { allowedSources: ['10.20.0.0/24'], clientCertificateSha256: '' } });
	await expectStatus(access, 200, 'device access');
	const rule = {
		siteCode: source, name: hostileRule, zones: ['Snake A'], metric: 'Nowcast', comparator: 'GreaterThan', threshold: 20, minQueueLength: null, clearThreshold: null,
		sustainMinutes: 5, clearAfterMinutes: 5, severity: 'Warning', ownerRole: 'BorderShiftSupervisor', escalateAfterMinutes: 15, escalateToRole: null,
		escalationContact: contact, notifyByEmail: false, enabled: true
	};
	await create('alert-rules', rule);
	await create('alert-rules', {
		...rule, name: 'Overflow in use', zones: ['Overflow A'], metric: 'OverflowOccupied', comparator: 'IsTrue', threshold: null, severity: 'Info', ownerRole: null,
		escalateAfterMinutes: null, escalationContact: null
	});
	const display = await call('POST', `${admin}/displays`, {
		token: stepped,
		data: {
			siteCode: source, code: displayCode, name: hostileZone, location: 'Gate 3', orientation: 'Landscape', languages: ['en', 'ar'], bandMinutes: 5, hysteresisMinutes: 1,
			staleSeconds: 300, entries: [{ zone: 'Snake A', labels: { en: 'Citizens', ar: '\u0645\u0648\u0627\u0637\u0646\u0648\u0646' } }],
			fallback: { en: 'Ask a member of staff', ar: '\u0627\u0633\u0623\u0644' }, enabled: true
		}
	});
	await expectStatus(display, 201, 'display');
});

/** The live displays of a site (the display search answers a list). */
async function displaysOf(site: string): Promise<any[]> {
	const response = await call('GET', `${admin}/displays?siteCode=${site}`, { token: stepped });
	return response.status() === 200 ? await response.json() : [];
}

test.afterAll(async () => {
	if (!stepped) return;
	for (const site of [source, target, spare]) {
		for (const display of await displaysOf(site)) await call('DELETE', `${admin}/displays/${display.id}`, { token: stepped });
		for (const rule of await search('alert-rules', site)) await call('DELETE', `${admin}/alert-rules/${rule.id}`, { token: stepped });
		for (const device of await search('devices', site)) await call('DELETE', `${admin}/devices/${device.id}`, { token: stepped });
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
	await expectStatus(response, 200, 'export');
	expect(response.headers()['cache-control']).toContain('no-store');
	const text = await response.text();
	for (const prefix of ['ardk_', 'ardp_']) expect(text.includes(prefix), `no ${prefix.slice(0, 4)} credential in an export`).toBe(false);
	for (const fragment of echoes) expect(text, 'markup and SQL travel only inside base64 parts').not.toContain(fragment);
	exported = JSON.parse(text);

	expect(exported.bundleSha256).toMatch(/^[0-9a-f]{64}$/);
	expect(exported.zoneProfileIncluded).toBe(true);
	expect(exported.geometryHash).toMatch(/^[0-9a-f]{64}$/);
	expect(exported.portableGeometryHash).toMatch(/^[0-9a-f]{64}$/);
	expect(exported.portableGeometryHash).not.toBe(exported.geometryHash);
	expect(exported.counts).toEqual({
		terminals: 1, levels: 2, checkpoints: 2, desks: 3, deskCodeMappings: 1, zones: 3, lines: 4, floorPlans: 1, devices: 1, alertRules: 2, displays: 1
	});
	const bundle = exported.bundle;
	expect(bundle.formatVersion).toBe(1);
	expect(bundle.sourceSite).toBe(source);
	expect(Object.keys(bundle.manifest).sort()).toEqual(
		['alertRules', 'deskCodeMappings', 'devices', 'displays', 'floorPlanFile.1', 'floorPlans', 'topology', 'zoneProfile'].sort()
	);
	expect(Object.keys(bundle.parts).sort()).toEqual(Object.keys(bundle.manifest).sort());
	for (const [part, hash] of Object.entries(bundle.manifest)) expect(sha256(Buffer.from(bundle.parts[part], 'base64')), part).toBe(hash);
	// Hostile names are data inside the parts; no id travels.
	expect(decoded(bundle, 'topology').terminals[0]).toEqual({ airport: sourceIata, code: 'T1', name: hostileTerminal });
	expect(decoded(bundle, 'zoneProfile')[0].portableGeometryHash).toBe(exported.portableGeometryHash);
	expect(Buffer.from(bundle.parts.topology, 'base64').toString('utf8')).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/);

	// ARV-122b (CWE-532): no credential, pairing secret, escalation contact or access setting in any decoded part. Each
	// check is a boolean, so a failure prints no value.
	for (const part of Object.keys(bundle.parts)) {
		const text = Buffer.from(bundle.parts[part], 'base64').toString('utf8');
		expect(secretPattern.test(text), `no device credential or pairing secret in ${part}`).toBe(false);
		expect(text.includes(sourceCredential.slice(5, 13)) || text.includes(sourceCredential.slice(13)), `no fragment of the device's credential in ${part}`).toBe(false);
		expect(text.includes(contact), `no escalation contact in ${part}`).toBe(false);
	}
	expect(Object.keys(decoded(bundle, 'devices')[0]).sort()).toEqual(
		['code', 'family', 'model', 'transport', 'dialect', 'clockSource', 'mappingName', 'terminal', 'level', 'x', 'y', 'mountingHeightMetres', 'orientationDegrees',
			'queueZone', 'footprintLengthMetres', 'footprintWidthMetres', 'footprintRadiusMetres', 'accessSettingsWithheld'].sort()
	);
	expect(decoded(bundle, 'devices')[0]).toMatchObject({ code: 'S01', terminal: 'T1', level: 'L0', queueZone: 'Snake A', accessSettingsWithheld: true });
	expect(Object.keys(bundle.parts).some((part) => Buffer.from(bundle.parts[part], 'base64').toString('utf8').includes('10.20.0.0')), 'no source network travels').toBe(false);
	const rules = decoded(bundle, 'alertRules');
	expect(rules.map((r: any) => [r.code, r.escalationContactWithheld])).toEqual([['R-001', true], ['R-002', false]]);
	expect(rules.every((r: any) => !('escalationContact' in r))).toBe(true);
	expect(Object.keys(decoded(bundle, 'displays')[0]).sort()).toEqual(
		// The wait at which advertising gives way (ARV-143b) is a setting; no pairing secret, prefix or hash.
		['code', 'name', 'location', 'orientation', 'languages', 'bandMinutes', 'hysteresisMinutes', 'staleSeconds', 'entries', 'fallback', 'enabled', 'layout', 'queueShare', 'secondRegion', 'advertisingPauseMinutes'].sort()
	);
});

test('a terminal name an older release stored with an override refuses the export with a fixed text (ARV-162)', async () => {
	// Since ARV-159 no endpoint stores such a name, so it is planted past them, in this suite's own source site, and put back.
	const planted = 'Terminal \u202E1 lanimret';
	const fixed = 'A terminal, level, checkpoint or desk of the site has a name with a control, invisible format or broken character, which an import refuses; rename it first.';
	const rename = (name: string) =>
		withDatabase((db) => db.query("UPDATE terminal SET name = $1::varchar WHERE site_code = $2::varchar AND code = 'T1' AND deleted_on IS NULL", [name, source]));
	expect((await rename(planted)).rowCount, 'the source terminal').toBe(1);
	try {
		const refused = await call('GET', configuration(source), { token: stepped });
		await expectStatus(refused, 409, 'export refused');
		const body = await refused.text();
		expect(body.includes(fixed), 'the fixed text').toBe(true);
		expect(body.includes(planted) || body.includes('\u202E'), 'the name is not repeated').toBe(false);
	} finally {
		await rename(hostileTerminal);
	}
	const again = await call('GET', configuration(source), { token: stepped });
	await expectStatus(again, 200, 'export again');
});

test('a predicted nowcast rule an older release left with an airport-side owner refuses the export with a fixed text (ARV-187)', async () => {
	// A rule from before ARV-187 that the import would refuse: the export is refused instead (409), so every export imports.
	// Planted past the endpoints in this suite's own source site (R-001 becomes such a rule) and put back.
	const fixed =
		'An alert rule of the site fails the checks an import applies (for example a rule on open desks below recommendation or predicted nowcast that does not belong to a border role), which an import refuses; change or delete it first.';
	const legacy = () =>
		withDatabase((db) =>
			db.query(
				"UPDATE alert_rule SET metric = 'PredictedNowcast', lead_minutes = 30, owner_role = 'TerminalDutyManager' WHERE site_code = $1::varchar AND code = 'R-001' AND deleted_on IS NULL",
				[source]
			)
		);
	const restore = () =>
		withDatabase((db) =>
			db.query(
				"UPDATE alert_rule SET metric = 'Nowcast', lead_minutes = NULL, owner_role = 'BorderShiftSupervisor' WHERE site_code = $1::varchar AND code = 'R-001' AND deleted_on IS NULL",
				[source]
			)
		);
	expect((await legacy()).rowCount, 'the source rule').toBe(1);
	try {
		const refused = await call('GET', configuration(source), { token: stepped });
		await expectStatus(refused, 409, 'export refused');
		const body = await refused.text();
		expect(body.includes(fixed), 'the fixed text').toBe(true);
		for (const fragment of [...echoes, 'R-001']) expect(body, 'no rule is named').not.toContain(fragment);
	} finally {
		expect((await restore()).rowCount).toBe(1);
	}
	const again = await call('GET', configuration(source), { token: stepped });
	await expectStatus(again, 200, 'export again');
});

test('a tampered bundle is refused with a fixed text and writes nothing', async () => {
	const bundle = exported.bundle;
	const devices = Buffer.from('[]');
	const withMember = (part: string, member: string) =>
		rehashed(bundle, part, Buffer.from(JSON.stringify(decoded(bundle, part).map((row: any) => ({ [member]: 'x', ...row })))));
	const without122b = {
		...bundle,
		manifest: Object.fromEntries(Object.entries(bundle.manifest).filter(([name]) => !['devices', 'alertRules', 'displays'].includes(name))),
		parts: Object.fromEntries(Object.entries(bundle.parts).filter(([name]) => !['devices', 'alertRules', 'displays'].includes(name)))
	};
	const cases: [string, any, number][] = [
		['a part changed', { ...bundle, parts: { ...bundle.parts, topology: Buffer.from('{"terminals":[],"levels":[],"checkpoints":[],"desks":[]}').toString('base64') } }, 400],
		['a hash changed', { ...bundle, manifest: { ...bundle.manifest, zoneProfile: '0'.repeat(64) } }, 400],
		['a part missing', { ...bundle, parts: Object.fromEntries(Object.entries(bundle.parts).filter(([name]) => name !== 'floorPlans')) }, 400],
		['an entry without its part', { ...bundle, manifest: { ...bundle.manifest, 'floorPlanFile.2': '0'.repeat(64) } }, 400],
		['a part this version does not know', { ...bundle, manifest: { ...bundle.manifest, users: sha256(devices) }, parts: { ...bundle.parts, users: devices.toString('base64') } }, 400],
		['a bundle written before the devices, alert rules and displays parts', without122b, 400],
		['a device row carrying a credential hash', withMember('devices', 'credentialHash'), 400],
		['a device row carrying source networks', withMember('devices', 'allowedSources'), 400],
		['an alert rule carrying an escalation contact', withMember('alertRules', 'escalationContact'), 400],
		['a display row carrying a pairing secret', withMember('displays', 'pairingSecret'), 400],
		['an alert rule on a zone the profile lacks', rehashed(bundle, 'alertRules', Buffer.from(JSON.stringify(decoded(bundle, 'alertRules').map((r: any) => ({ ...r, zones: [hostileZone] }))))), 400],
		['a display on a zone that is no queue zone', rehashed(bundle, 'displays', Buffer.from(JSON.stringify(decoded(bundle, 'displays').map((d: any) => ({ ...d, entries: [{ ...d.entries[0], zone: 'Overflow A' }] }))))), 400],
		['a rule code above R-100000', rehashed(bundle, 'alertRules', Buffer.from(JSON.stringify(decoded(bundle, 'alertRules').map((r: any, i: number) => ({ ...r, code: i === 0 ? 'R-100001' : r.code }))))), 400],
		// ARV-181b2: open desks below recommendation belongs to a border role and watches queue zones only.
		['open desks below recommendation owned by the duty manager', desksBelow(bundle, { ownerRole: 'TerminalDutyManager' }), 400],
		['open desks below recommendation without an owner', desksBelow(bundle, { ownerRole: null }), 400],
		['open desks below recommendation escalated to the handler', desksBelow(bundle, { escalateAfterMinutes: 10, escalateToRole: 'HandlerStationManager' }), 400],
		['open desks below recommendation on an overflow band', desksBelow(bundle, { zones: ['Overflow A'] }), 400],
		// ARV-187: so does a predicted nowcast rule (with any comparator and on an overflow band).
		['a predicted nowcast owned by the duty manager', predictedBreach(bundle, { ownerRole: 'TerminalDutyManager' }), 400],
		['a predicted nowcast without an owner', predictedBreach(bundle, { ownerRole: null }), 400],
		['a predicted nowcast escalated to the duty manager', predictedBreach(bundle, { escalateAfterMinutes: 10, escalateToRole: 'TerminalDutyManager' }), 400],
		['a predicted nowcast below its threshold without an owner', predictedBreach(bundle, { ownerRole: null, comparator: 'LessThan', zones: ['Overflow A'] }), 400],
		['a device row without its access settings flag', rehashed(bundle, 'devices', Buffer.from(JSON.stringify(decoded(bundle, 'devices').map(({ accessSettingsWithheld, ...row }: any) => row)))), 400],
		['a device on a level outside the bundle', rehashed(bundle, 'devices', Buffer.from(JSON.stringify(decoded(bundle, 'devices').map((d: any) => ({ ...d, level: 'L9' }))))), 400],
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
		await expectStatus(refused, status, what);
		for (const fragment of [...echoes, target]) expect(text, `${what}: nothing echoed`).not.toContain(fragment);
	}

	const empty = await (await call('GET', configuration(target), { token: stepped })).json();
	expect(empty.counts, 'the target is still empty').toEqual({
		terminals: 0, levels: 0, checkpoints: 0, desks: 0, deskCodeMappings: 0, zones: 0, lines: 0, floorPlans: 0, devices: 0, alertRules: 0, displays: 0
	});
	expect(empty.zoneProfileIncluded).toBe(false);
});

test('an oversized body answers 413 and a deeply nested one 400', async () => {
	const headers = { 'Content-Type': 'application/json' };
	const oversized = `{"formatVersion":1,"sourceSite":"${source}","parts":{"topology":"${'A'.repeat(21 * 1024 * 1024)}"}}`;
	const tooLarge = await call('POST', configuration(spare), { token: stepped, raw: oversized, headers });
	await expectStatus(tooLarge, 413, 'body over 20 MB');

	const bigPart = rehashed(exported.bundle, 'deskCodeMappings', Buffer.alloc(9 * 1024 * 1024, 0x20));
	const partTooLarge = await call('POST', configuration(spare), { token: stepped, data: bigPart });
	await expectStatus(partTooLarge, 413, 'a configuration part over 8 MB');

	const deep = `{"formatVersion":1,"manifest":${'{"a":'.repeat(40)}1${'}'.repeat(40)}}`;
	const nested = await call('POST', configuration(spare), { token: stepped, raw: deep, headers });
	await expectStatus(nested, 400, 'body 41 levels deep');

	const deepPart = rehashed(exported.bundle, 'deskCodeMappings', Buffer.from(`${'['.repeat(40)}${']'.repeat(40)}`));
	const nestedPart = await call('POST', configuration(spare), { token: stepped, data: deepPart });
	await expectStatus(nestedPart, 400, 'a part 40 levels deep');
});

test('the airport is checked as an IATA code that exists and is never created', async () => {
	for (const airportCode of ['zz', '<b>X</b>', 'ABCD']) {
		const refused = await call('POST', `${configuration(spare)}?airportCode=${encodeURIComponent(airportCode)}`, { token: stepped, data: exported.bundle });
		const text = await refused.text();
		await expectStatus(refused, 400, airportCode);
		expect(text).not.toContain('<b>');
	}
	let unknown = random(3);
	while (unknown === sourceIata || unknown === targetIata) unknown = random(3);
	const missing = await call('POST', `${configuration(spare)}?airportCode=${unknown}`, { token: stepped, data: exported.bundle });
	const text = await missing.text();
	await expectStatus(missing, 400, 'unknown airport');
	expect(text).not.toContain(unknown);
	const airportsNow = await (await call('GET', `${admin}/airports?text=${unknown}&pageSize=500`, { token: stepped })).json();
	expect(airportsNow.data.filter((a: any) => a.iataCode === unknown), 'no airport is created').toHaveLength(0);
});

test('a display code a live display holds answers 409 and writes nothing', async () => {
	const refused = await call('POST', `${configuration(target)}?airportCode=${targetIata}`, { token: stepped, data: exported.bundle });
	const text = await refused.text();
	await expectStatus(refused, 409, 'display code taken');
	expect(text, 'the fixed text names no code').not.toContain(displayCode);
	for (const fragment of echoes) expect(text).not.toContain(fragment);
	const still = await (await call('GET', configuration(target), { token: stepped })).json();
	expect(Object.values(still.counts).every((count) => count === 0), 'nothing was written').toBe(true);

	// Display codes are unique among the deployment's live displays: the source's display goes before its copy arrives.
	for (const display of await displaysOf(source)) {
		const deleted = await call('DELETE', `${admin}/displays/${display.id}`, { token: stepped });
		await expectStatus(deleted, 204, 'delete display');
	}
});

test('a round trip between two sites gives the same portable geometry hash once the draft is published', async () => {
	const imported = await call('POST', `${configuration(target)}?airportCode=${targetIata}`, { token: stepped, data: exported.bundle });
	await expectStatus(imported, 201, 'import');
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
	expect(draft.zones.map((z: any) => z.name).sort()).toEqual([hostileZone, 'Overflow A', 'Snake A'].sort());

	// Devices: in Commissioning, each with a new credential that works at Ingest as the target's device; the source's
	// credential still names the source's device. Credentials are tested inside expects and never printed.
	expect(answer.deviceCredentials.length, 'one new credential').toBe(1);
	const issued = answer.deviceCredentials[0];
	expect(issued.code).toBe('S01');
	expect(deviceCredentialFormat.test(issued.credential), 'the credential has the device credential format').toBe(true);
	expect(issued.credential === sourceCredential, 'a new credential, not the source one').toBe(false);
	const asTarget = await call('GET', ingestDevice, { headers: { 'X-Ariva-Device-Key': issued.credential } });
	expect(asTarget.status(), 'the new credential authenticates').toBe(200);
	expect(await asTarget.json()).toMatchObject({ code: 'S01', siteCode: target, state: 'Commissioning' });
	const asSource = await call('GET', ingestDevice, { headers: { 'X-Ariva-Device-Key': sourceCredential }, address: '10.20.0.17' });
	expect(asSource.status(), 'the source device keeps its own credential').toBe(200);
	expect((await asSource.json()).siteCode, 'the old credential never reaches the copy').toBe(source);
	const [copied] = await search('devices', target);
	const [original] = await search('devices', source);
	expect(copied.state).toBe('Commissioning');
	expect(copied.credentialPrefix === original.credentialPrefix, 'a different credential prefix').toBe(false);
	expect(copied.allowedSources ?? [], 'no source networks').toEqual([]);
	expect(answer.devicesNeedingAccessSettings, 'listed by code for its access settings').toEqual(['S01']);

	// Alert rules: their codes, no escalation contact; the answer lists the rule that needs one, by code only.
	expect(answer.rulesNeedingEscalationContact).toEqual(['R-001']);
	const rules = await call('GET', `${admin}/alert-rules?siteCode=${target}&pageSize=500`, { token: stepped });
	expect(rules.headers()['content-type']).toContain('application/json');
	expect(rules.headers()['x-content-type-options']).toBe('nosniff');
	const copiedRules = (await rules.json()).data.sort((a: any, b: any) => a.code.localeCompare(b.code));
	expect(copiedRules.map((r: any) => [r.code, r.escalationContact, r.ownerRole])).toEqual([['R-001', null, 'BorderShiftSupervisor'], ['R-002', null, null]]);
	expect(copiedRules[0].name, 'a hostile name is data').toBe(hostileRule);

	// Displays: unpaired, with their entries.
	const [board] = await displaysOf(target);
	expect(board).toMatchObject({ code: displayCode, siteCode: target, name: hostileZone, credentialPrefix: null });
	expect(board.entries.map((e: any) => e.zone)).toEqual(['Snake A']);
	expect(answer.notes.length).toBeGreaterThanOrEqual(6);

	const published = await publish(answer.draftProfileId);
	const again = await call('GET', configuration(target), { token: stepped });
	await expectStatus(again, 200, 'export of the copy');
	const copy = await again.json();
	expect(copy.portableGeometryHash, 'the same portable geometry hash').toBe(exported.portableGeometryHash);
	expect(published.geometryHash, 'F22 v1 names levels and desks by their new ids').not.toBe(exported.geometryHash);
	expect(copy.counts).toEqual(exported.counts);
});

test('a rule on open desks below recommendation owned by the border imports with its owner (ARV-181b2)', async () => {
	// Into the spare site, at an airport of its own (the source's and the target's terminal codes are taken there), without the
	// display (its code is live at the target now).
	const { iataCode: iata } = await createAirport(postAirport);
	airports.push(iata);
	const bundle = desksBelow(rehashed(exported.bundle, 'displays', Buffer.from('[]')), {});
	const imported = await call('POST', `${configuration(spare)}?airportCode=${iata}`, { token: stepped, data: bundle });
	await expectStatus(imported, 201, 'import');
	expect((await imported.json()).counts.alertRules).toBe(2);
	const rules = await call('GET', `${admin}/alert-rules?siteCode=${spare}&pageSize=500`, { token: stepped });
	const desks = ((await rules.json()).data as any[]).find((r) => r.code === 'R-001');
	expect(desks).toMatchObject({ metric: 'DesksBelowPlan', comparator: 'GreaterOrEqual', threshold: 2, ownerRole: 'BorderShiftSupervisor' });
});

test('an import into a site that is not empty answers 409', async () => {
	for (const site of [source, target]) {
		const refused = await call('POST', `${configuration(site)}?airportCode=${targetIata}`, { token: stepped, data: exported.bundle });
		const text = await refused.text();
		await expectStatus(refused, 409, site);
		for (const fragment of echoes) expect(text).not.toContain(fragment);
	}
});
