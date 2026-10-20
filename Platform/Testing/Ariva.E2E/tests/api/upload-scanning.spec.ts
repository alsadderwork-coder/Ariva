import { createHash, randomInt } from 'node:crypto';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, claimsOf, databaseAvailable, type CallResponse } from '../support/accounts';
import { createAirport } from '../support/airports';
import { expectProblemDetails } from '../support/api-assertions';
import { eicar, eicarSha256, eicarSignature } from '../support/eicar';
import { expectStatus } from '../support/expect-status';
import { hosts } from '../support/hosts';
import { provisionLoadSite, randomLetters, secondFactorSignIn } from '../support/load-site';

// ARV-092b: every upload Ariva.Api.Main takes is scanned by clamd before anything parses, stores or imports it. The EICAR
// test file sent to each upload endpoint (a floor plan; an SSIM preview and import; a device and a desk code mapping CSV
// preview and commit; a site configuration bundle with the file as its floor plan file part or as a JSON part; an
// advertisement image) answers 400 with the scan's fixed text, echoing neither the file nor the signature; nothing is
// stored or imported; and the audit trail holds one Upload.MalwareRefused entry per refused file, with the upload's kind
// as its target type, the signature as its target name and the file's size and SHA-256 in its summary (never the file or
// its name). An import or a commit sends its file again and is scanned again before its preview token is checked: the
// token sent here is a clean file's, so only the scan refuses it (a token of another file answers 409). The imports also
// record their own refusal, with the scan's text as its reason. The run's clamd is ARIVA_E2E_CLAMD (else the clamd of
// `npm run dev:up`). The suite's own account (a TOTP code counts once per account) signs in with its second factor once
// per worker: the device import, the site configuration export and import and the advertising policy are critical. The
// sites are new each run (codes UV and UW with a random tail; no other suite uses them); everything the suite creates is
// removed at the end except what Ariva never deletes (the sites, the published zone profile versions, the advertising
// policy).

test.skip(!databaseAvailable, 'the uploads need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
// In order in one worker, so one second factor sign-in serves every test; not serial, so a failure skips no other upload.
// Each test builds its own site first, which takes a few seconds more than most API tests.
test.describe.configure({ mode: 'default', timeout: 60_000 });

const adminApi = `${hosts.main}/api/v1/admin`;
/** The scan's fixed answer to a detection (UploadScanErrors.Malware). */
const refusal = 'The file was refused: the malware scanner found a known threat in it.';
const scanAction = 'Upload.MalwareRefused';
const png = Buffer.from(
	'iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFklEQVR4nGP8z8DwnwEJMDGgAQYGBgCkKAIFD1jZ6wAAAABJRU5ErkJggg==',
	'base64'
);
const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="10" height="10"/></svg>';
const deviceHeader = 'code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone';
const mappingHeader = 'externalSystem,externalCode,checkpoint,desk';

let admin: string;
let self: string;
/** The suite's sites and the airports it made outside them, for the clean-up. */
const sites: string[] = [];
const airportIds: string[] = [];

interface AuditEntry {
	id: string;
	actorName: string;
	targetType: string;
	targetId: string | null;
	targetName: string | null;
	afterSummary: string | null;
}

test.beforeAll(async () => {
	// A second sign-in within one TOTP step (a new worker after a failure) waits for the next step.
	test.setTimeout(120_000);
	admin = await secondFactorSignIn(accounts().uploadScanAdmin);
	self = String(claimsOf(admin).sub);
});

test.afterAll(async () => {
	if (!admin) return;
	test.setTimeout(180_000);
	const lenient = async (path: string): Promise<any[]> => {
		const response = await call('GET', `${adminApi}/${path}`, { token: admin });
		if (response.status() !== 200) return [];
		const json = await response.json();
		return Array.isArray(json) ? json : json.data;
	};
	const airports = new Set(airportIds);
	for (const site of sites) {
		for (const plan of await lenient(`floor-plans?siteCode=${site}`)) await call('DELETE', `${adminApi}/levels/${plan.levelId}/floor-plan`, { token: admin });
		for (const mapping of await lenient(`desk-code-mappings?siteCode=${site}&pageSize=500`)) await call('DELETE', `${adminApi}/desk-code-mappings/${mapping.id}`, { token: admin });
		for (const device of await lenient(`devices?siteCode=${site}&pageSize=500`)) await call('DELETE', `${adminApi}/devices/${device.id}`, { token: admin });
		for (const draft of (await lenient(`zone-profiles?siteCode=${site}`)).filter((p) => p.status === 'Draft'))
			await call('DELETE', `${adminApi}/zone-profiles/${draft.id}`, { token: admin });
		for (const terminal of await lenient(`terminals?siteCode=${site}&pageSize=500`)) airports.add(terminal.airportId);
		for (const entity of ['desks', 'checkpoints', 'levels', 'terminals'])
			for (const record of await lenient(`${entity}?siteCode=${site}&pageSize=500`)) await call('DELETE', `${adminApi}/${entity}/${record.id}`, { token: admin });
	}
	for (const id of airports) await call('DELETE', `${adminApi}/airports/${id}`, { token: admin });
});

/** A new site of the suite (provisionLoadSite: an airport, terminal T1, level L0 and a published profile with zone Load Zone). */
async function freshSite() {
	const provisioned = await provisionLoadSite(accounts().uploadScanAdmin, 'UV', 'Load Zone', admin);
	sites.push(provisioned.site);
	return provisioned;
}

/** Every record of a kind at a site (searches page at most 500); the search itself must answer. */
async function records(entity: string, site: string): Promise<any[]> {
	const response = await call('GET', `${adminApi}/${entity}?siteCode=${site}&pageSize=500`, { token: admin });
	await expectStatus(response, 200, `${entity} of the site`);
	return (await response.json()).data;
}

/** The run's database as the migration login (the hosts' own settings), for the flight legs no endpoint lists by site. */
async function query<T = Record<string, unknown>>(sql: string, values: unknown[] = []): Promise<T[]> {
	const db = new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
	await db.connect();
	try {
		return (await db.query(sql, values)).rows as T[];
	} finally {
		await db.end();
	}
}

/** The newest entries of an action this suite's account wrote (at most 500, newest first). */
async function auditTrail(action: string): Promise<AuditEntry[]> {
	const response = await call('GET', `${adminApi}/audit-entries?action=${action}&actorId=${self}&pageSize=500`, { token: admin });
	await expectStatus(response, 200, `audit entries ${action}`);
	return (await response.json()).data as AuditEntry[];
}

/** The entries of an action this suite's account wrote since `before` was read. */
async function auditedSince(action: string, before: AuditEntry[]): Promise<AuditEntry[]> {
	const seen = new Set(before.map((entry) => entry.id));
	return (await auditTrail(action)).filter((entry) => !seen.has(entry.id));
}

/** A refusal by the scan: 400 ProblemDetails with the fixed text, echoing neither the file nor the signature. */
async function expectScanRefusal(response: CallResponse, what: string): Promise<void> {
	await expectStatus(response, 400, what);
	const problem = await expectProblemDetails(response, 400);
	expect(problem.detail, `${what}: the scan's fixed text`).toBe(refusal);
	expect(/eicar/i.test(await response.text()), `${what}: neither the file nor the signature is echoed`).toBe(false);
}

/**
 * One Upload.MalwareRefused entry per refused file, by the suite's account: the kind as the target type, the signature as
 * the target name, no target id, and a summary of the kind, the signature, the size and the SHA-256 only.
 */
function expectScanAudit(entries: AuditEntry[], kind: string, files: number): void {
	expect(
		entries.map((entry) => [entry.targetType, entry.targetName, entry.targetId, entry.actorName]),
		`${kind}: one entry per refused file`
	).toEqual(Array.from({ length: files }, () => [kind, eicarSignature, null, accounts().uploadScanAdmin.userName]));
	for (const entry of entries) {
		expect(JSON.parse(entry.afterSummary ?? 'null'), `${kind}: the summary`).toEqual({ kind, signature: eicarSignature, bytes: eicar().length, sha256: eicarSha256 });
	}
}

/** The site, outcome and reason of an import's own audit entries (the CSV imports and the site configuration import). */
function importOutcomes(entries: AuditEntry[]): unknown[] {
	return entries.map((entry) => {
		const summary = JSON.parse(entry.afterSummary ?? 'null');
		return [entry.targetType, entry.targetName, summary?.outcome, summary?.reason];
	});
}

function floorPlanForm(content: Buffer | string, fileName: string, type: string): FormData {
	const form = new FormData();
	form.append('file', new Blob([content], { type }), fileName);
	form.append('metresPerPixel', '0.05');
	form.append('originX', '0');
	form.append('originY', '0');
	return form;
}

/** A CSV import's form: the file, and the preview's token for a commit. */
function csvForm(content: Buffer | string, previewToken?: string): FormData {
	const form = new FormData();
	form.append('file', new Blob([content], { type: 'text/csv' }), 'import.csv');
	if (previewToken !== undefined) form.append('previewToken', previewToken);
	return form;
}

/** A type 3 record of an SSIM file with its fields at their SSIM positions (1-based), as flight-schedules.spec.ts builds it. */
function ssimLeg(flight: number, from: string, to: string): string {
	const day = (offset: number) => {
		const d = new Date(Date.now() + offset * 86_400_000);
		return `${String(d.getUTCDate()).padStart(2, '0')}${d.toLocaleString('en-US', { month: 'short', timeZone: 'UTC' }).toUpperCase()}${String(d.getUTCFullYear()).slice(2)}`;
	};
	const line = Array(200).fill(' ');
	const put = (start: number, value: string) => value.split('').forEach((c, i) => (line[start - 1 + i] = c));
	put(1, '3');
	put(3, 'QR ');
	put(6, String(flight).padStart(4, '0'));
	put(10, '0101J');
	put(15, day(-5));
	put(22, day(60));
	put(29, '1234567');
	put(37, from);
	put(40, '0800');
	put(44, '0800');
	put(48, '+0000');
	put(55, to);
	put(58, '1200');
	put(62, '1200');
	put(66, '+0000');
	put(71, 'T1');
	put(73, '359');
	return line.join('');
}

const ssimFile = (...legs: string[]) =>
	['1AIRLINE STANDARD SCHEDULE DATA SET'.padEnd(200), '2UQR '.padEnd(200), ...legs, '5QR'.padEnd(200)].join('\r\n') + '\r\n';

/** The site's local day (Asia/Dubai, provisionLoadSite) moved by some days, "yyyy-MM-dd". */
function day(offset: number): string {
	const date = new Date(`${new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai' }).format(new Date())}T00:00:00Z`);
	date.setUTCDate(date.getUTCDate() + offset);
	return date.toISOString().slice(0, 10);
}

/** A copy of the bundle with one part replaced and its manifest entry recomputed, as site-configuration.spec.ts builds one. */
function rehashed(bundle: any, part: string, bytes: Buffer) {
	return {
		...bundle,
		manifest: { ...bundle.manifest, [part]: createHash('sha256').update(bytes).digest('hex') },
		parts: { ...bundle.parts, [part]: bytes.toString('base64') }
	};
}

test('a floor plan holding the EICAR test file is refused and audited; the level keeps no plan, or its previous one', async () => {
	const { site, levelId, create } = await freshSite();
	const [terminal] = await records('terminals', site);
	const planned = (await create('levels', { terminalId: terminal.id, code: 'L1', name: 'L1', floorNumber: 1, widthMetres: 100, depthMetres: 50 })).id as string;
	const planUrl = (level: string) => `${adminApi}/levels/${level}/floor-plan`;
	const kept = await call('POST', planUrl(planned), { token: admin, form: floorPlanForm(png, 'hall.png', 'image/png') });
	await expectStatus(kept, 201, 'a clean plan');
	const previous = await kept.json();
	const before = await auditTrail(scanAction);

	for (const [what, level, fileName, type] of [
		['a level without a plan', levelId, 'plan.png', 'image/png'],
		['a level with a plan', planned, 'plan.svg', 'image/svg+xml']
	] as const) {
		await expectScanRefusal(await call('POST', planUrl(level), { token: admin, form: floorPlanForm(eicar(), fileName, type) }), what);
	}

	expect((await call('GET', planUrl(levelId), { token: admin })).status(), 'the level without a plan still has none').toBe(404);
	const current = await call('GET', planUrl(planned), { token: admin });
	await expectStatus(current, 200, 'the previous plan');
	expect(await current.json(), 'the previous plan is still the current one').toMatchObject({ id: previous.id, sha256: previous.sha256, contentType: 'image/png' });
	const content = await fetch(`${planUrl(planned)}/content`, { headers: { Authorization: `Bearer ${admin}` } });
	expect(content.status, 'its file is served').toBe(200);
	expect(createHash('sha256').update(Buffer.from(await content.arrayBuffer())).digest('hex'), 'its file is unchanged').toBe(previous.sha256);

	expectScanAudit(await auditedSince(scanAction, before), 'FloorPlan', 2);
});

test('an SSIM file holding the EICAR test file is refused at the preview and at the import, and no flight leg is added', async () => {
	const { site } = await freshSite();
	const [terminal] = await records('terminals', site);
	const schedules = `${adminApi}/sites/${site}/flight-schedules`;
	const send = (step: 'preview' | 'import', content: Buffer | string, fields: Record<string, string>) => {
		const form = new FormData();
		form.append('file', new Blob([content]), 'schedule.ssim');
		for (const [name, value] of Object.entries(fields)) form.append(name, value);
		return call('POST', `${schedules}/${step}`, { token: admin, form });
	};
	// A clean file with one leg arriving at the site's airport: its preview gives the token the import sends.
	const previewed = await send('preview', ssimFile(ssimLeg(randomInt(1000, 9000), 'DOH', terminal.airportIataCode)), { horizonDays: '7' });
	await expectStatus(previewed, 200, 'a clean file');
	const summary = await previewed.json();
	expect(summary.legRecordsOfSite, 'the clean file holds a leg of the site').toBe(1);
	expect(typeof summary.previewToken, 'the clean file can be imported').toBe('string');
	const before = await auditTrail(scanAction);

	await expectScanRefusal(await send('preview', eicar(), { horizonDays: '7' }), 'the preview');
	await expectScanRefusal(await send('import', eicar(), { horizonDays: '7', previewToken: summary.previewToken }), "the import with a clean file's token");

	const [legs] = await query<{ n: number }>('SELECT count(*)::int AS n FROM flight_leg WHERE site_code = $1', [site]);
	expect(legs.n, 'no flight leg at the site').toBe(0);
	expectScanAudit(await auditedSince(scanAction, before), 'FlightSchedule', 2);
});

test('a device CSV holding the EICAR test file is refused at the preview and at the commit, and no device is added', async () => {
	const { site } = await freshSite();
	const imports = `${adminApi}/sites/${site}/device-imports`;
	const previewed = await call('POST', `${imports}/preview`, {
		token: admin,
		form: csvForm(`${deviceHeader}\r\nS-01,StereoVision,PC2SE,HttpsPush,Xovis,Ntp,T1,L0,20,16,5,0,Load Zone\r\n`)
	});
	await expectStatus(previewed, 200, 'a clean file');
	const { previewToken } = await previewed.json();
	expect(typeof previewToken, 'the clean file can be committed').toBe('string');
	const [scans, previews, refusals] = await Promise.all([scanAction, 'DeviceImport.Previewed', 'DeviceImport.Refused'].map(auditTrail));

	await expectScanRefusal(await call('POST', `${imports}/preview`, { token: admin, form: csvForm(eicar()) }), 'the preview');
	await expectScanRefusal(await call('POST', `${imports}/commit`, { token: admin, form: csvForm(eicar(), previewToken) }), "the commit with a clean file's token");

	expect(await records('devices', site), 'no device at the site').toEqual([]);
	expectScanAudit(await auditedSince(scanAction, scans), 'DeviceImport', 2);
	expect(importOutcomes(await auditedSince('DeviceImport.Previewed', previews)), 'the preview recorded as refused for the scan').toEqual([['Site', site, 'refused', refusal]]);
	expect(importOutcomes(await auditedSince('DeviceImport.Refused', refusals)), 'the commit recorded as refused for the scan').toEqual([['Site', site, 'refused', refusal]]);
});

test('a desk code mapping CSV holding the EICAR test file is refused at the preview and at the commit, and no mapping is added', async () => {
	const { site, levelId, create } = await freshSite();
	const checkpoint = (await create('checkpoints', { levelId, code: 'IMM', name: 'Immigration', kind: 'Immigration' })).id as string;
	await create('desks', { checkpointId: checkpoint, code: 'D01', kind: 'Desk', laneCategories: ['CIT'] });
	const imports = `${adminApi}/sites/${site}/desk-code-mapping-imports`;
	const previewed = await call('POST', `${imports}/preview`, { token: admin, form: csvForm(`${mappingHeader}\r\nAman,UV-01,IMM,D01\r\n`) });
	await expectStatus(previewed, 200, 'a clean file');
	const { previewToken } = await previewed.json();
	expect(typeof previewToken, 'the clean file can be committed').toBe('string');
	const [scans, previews, refusals] = await Promise.all([scanAction, 'DeskCodeMappingImport.Previewed', 'DeskCodeMappingImport.Refused'].map(auditTrail));

	await expectScanRefusal(await call('POST', `${imports}/preview`, { token: admin, form: csvForm(eicar()) }), 'the preview');
	await expectScanRefusal(await call('POST', `${imports}/commit`, { token: admin, form: csvForm(eicar(), previewToken) }), "the commit with a clean file's token");

	expect(await records('desk-code-mappings', site), 'no mapping at the site').toEqual([]);
	expectScanAudit(await auditedSince(scanAction, scans), 'DeskCodeMappingImport', 2);
	expect(importOutcomes(await auditedSince('DeskCodeMappingImport.Previewed', previews)), 'the preview recorded as refused for the scan').toEqual([
		['Site', site, 'refused', refusal]
	]);
	expect(importOutcomes(await auditedSince('DeskCodeMappingImport.Refused', refusals)), 'the commit recorded as refused for the scan').toEqual([
		['Site', site, 'refused', refusal]
	]);
});

test('a site configuration bundle whose floor plan file or JSON part is the EICAR test file is refused and audited, and imports nothing', async () => {
	test.setTimeout(90_000);
	const configuration = (code: string) => `${adminApi}/sites/${code}/configuration`;
	// The source: a site with a floor plan, exported as a bundle.
	const { site: source, levelId } = await freshSite();
	const plan = await call('POST', `${adminApi}/levels/${levelId}/floor-plan`, { token: admin, form: floorPlanForm(svg, 'arrivals.svg', 'image/svg+xml') });
	await expectStatus(plan, 201, 'the floor plan of the source');
	const exported = await call('GET', configuration(source), { token: admin });
	await expectStatus(exported, 200, 'the export');
	const { bundle } = await exported.json();
	expect(Object.keys(bundle.parts), 'the bundle carries the floor plan file').toContain('floorPlanFile.1');

	// The target: an empty site and an airport of its own, so that only the parts decide.
	const target = `UW${randomLetters(4)}`;
	const created = await call('POST', `${adminApi}/sites`, { token: admin, data: { code: target, name: target } });
	await expectStatus(created, 201, 'the target site');
	sites.push(target);
	const airport = await createAirport((iataCode) =>
		call('POST', `${adminApi}/airports`, { token: admin, data: { iataCode, name: `E2E upload scanning ${iataCode}`, timeZoneId: 'Asia/Dubai' } })
	);
	airportIds.push(airport.id);
	const [scans, imports] = await Promise.all([scanAction, 'SiteConfiguration.ImportRefused'].map(auditTrail));

	// A part is base64 bytes under its manifest hash, so a JSON part carries any bytes as well as a file part: each is
	// decoded and scanned before anything reads it as JSON or as an image.
	for (const [what, part] of [
		['the floor plan file part', 'floorPlanFile.1'],
		['the topology part (JSON)', 'topology']
	] as const) {
		const refused = await call('POST', `${configuration(target)}?airportCode=${airport.iataCode}`, { token: admin, data: rehashed(bundle, part, eicar()) });
		await expectScanRefusal(refused, `the EICAR test file as ${what}`);
	}

	const after = await call('GET', configuration(target), { token: admin });
	await expectStatus(after, 200, 'the target');
	const { counts, zoneProfileIncluded } = await after.json();
	expect(Object.values(counts).every((count) => count === 0), 'the target is still empty').toBe(true);
	expect(counts.floorPlans, 'no floor plan imported').toBe(0);
	expect(zoneProfileIncluded, 'no zone profile imported').toBe(false);
	const plans = await call('GET', `${adminApi}/floor-plans?siteCode=${target}`, { token: admin });
	await expectStatus(plans, 200, 'the floor plans of the target');
	expect(await plans.json(), 'no floor plan stored at the target').toEqual([]);

	expectScanAudit(await auditedSince(scanAction, scans), 'SiteConfiguration', 2);
	expect(importOutcomes(await auditedSince('SiteConfiguration.ImportRefused', imports)), 'each import recorded as refused for the scan').toEqual([
		['Site', target, 'refused', refusal],
		['Site', target, 'refused', refusal]
	]);
});

test('an advertisement image holding the EICAR test file is refused and audited, and the library gets no item', async () => {
	const { site } = await freshSite();
	const policyUrl = `${adminApi}/sites/${site}/advertising-policy`;
	const policy = await call('GET', policyUrl, { token: admin });
	await expectStatus(policy, 200, 'the advertising policy');
	const allowed = await call('PUT', policyUrl, {
		token: admin,
		data: {
			allowed: true,
			excludedZones: [],
			reason: 'Airport operator letter AO-E2E-092b, commercial team',
			reviewedProfileId: (await policy.json()).publishedProfileId
		}
	});
	await expectStatus(allowed, 200, 'advertising allowed');
	const media = `${adminApi}/sites/${site}/advertising/media`;
	const before = await auditTrail(scanAction);

	const form = new FormData();
	form.append('file', new Blob([eicar()], { type: 'image/png' }), 'ad.png');
	form.append('advertiser', 'Gulf Air');
	form.append('campaign', 'Winter sun');
	form.append('startsOn', day(-1));
	form.append('endsOn', day(30));
	await expectScanRefusal(await call('POST', media, { token: admin, form }), 'an image');

	const library = await call('GET', media, { token: admin });
	await expectStatus(library, 200, 'the library');
	expect(await library.json(), 'no library item').toEqual([]);
	expectScanAudit(await auditedSince(scanAction, before), 'AdvertisingMedia', 1);
});
