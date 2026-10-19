import fs from 'node:fs';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, integrationSeedsFile, login, signIn, totpCode, unusedTotpCode, type CallResponse } from '../support/accounts';
import { createAirport } from '../support/airports';
import { expectNoLeak, expectProblemDetails } from '../support/api-assertions';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-089 (ASVS 5.0 V2.2.1, V15.3.5, V15.3.7): request input is read strictly. A JSON body with a member its request model
// does not have, a member sent twice (also in another case) or a number sent as a string is refused with the controller's
// 400, and a query parameter sent twice with a fixed 400, unless the action binds a list under that name (none does yet).
// Neither answer repeats a member, a key or a value sent. Authentication and authorization come first: anonymous callers
// get 401 and roles without the permission 403 whatever the input; another site's caller gets the same 400 for input that
// is not strict, so the answer says nothing about the site. Ariva.Api.Ingest and Ariva.Api.Integration refuse a repeated
// query parameter on their GET endpoints too; Ariva.Api.Cronz has no controller, so nothing there binds a query. The
// login freeze (owner, since 2026-10-14) keeps the sign-in and the integration token exchange on the lenient parsing until
// ARV-089a: an extra member, a member twice or a repeated query parameter does not by itself refuse them.

test.skip(!databaseAvailable, 'strict input needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const random = (n: number) => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');
const site = `SI${random(4)}`;
const inside = () => `10.${1 + Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250)}.${1 + Math.floor(Math.random() * 250)}`;
const json = { 'Content-Type': 'application/json' };
const level = { name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 };

let stepped: string;
let levelId: string;
let deviceKey: string;
let integrationToken: string;
let exchangedWithExtraMember: number;
let client: { client: { clientId: string }; clientSecret: string; totpSecret: string };

/** The body without its random traceId, which may hold any run of digits. */
async function answerOf(response: CallResponse): Promise<string> {
	return (await response.text()).replace(/"traceId":"[^"]*"/g, '"traceId":""');
}

/** A refused body: the controller's 400 naming the body only, with the fixed text and nothing sent. */
async function expectRefusedBody(response: CallResponse, why: string, sent: string[]) {
	const problem = await expectProblemDetails(response, 400);
	expect(Object.keys(problem.errors as Record<string, string[]>), why).toEqual(['body']);
	expect((problem.errors as Record<string, string[]>).body, why).toEqual(['The input was not valid.']);
	const text = await answerOf(response);
	for (const fragment of [...sent, ...markupFragments, 'Path:', 'LineNumber']) expect(text, `${why}: no echo of ${fragment}`).not.toContain(fragment);
}

/** A refused query: the fixed 400 that names no parameter. */
async function expectRefusedQuery(response: CallResponse, why: string, sent: string[]) {
	const problem = await expectProblemDetails(response, 400);
	expect(problem.detail, why).toBe('A query parameter appears more than once.');
	const text = await answerOf(response);
	for (const fragment of [...sent, ...markupFragments]) expect(text, `${why}: no echo of ${fragment}`).not.toContain(fragment);
}

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().strictInputAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: await unusedTotpCode(totpSecret!) });
	expect(signedIn.status(), 'strict input administrator sign-in').toBe(200);
	stepped = (await signedIn.json()).accessToken;
	const create = async (path: string, data: unknown) => {
		const response = await call('POST', `${admin}/${path}`, { token: stepped, data });
		expect(response.status(), `${path}: ${await response.text()}`).toBe(201);
		const body = await response.json();
		return (body.id ?? body.profile?.id) as string;
	};

	// A site of its own: a level, a published zone profile with one queue and a device in it.
	expect((await call('POST', `${admin}/sites`, { token: stepped, data: { code: site, name: `E2E strict input ${site}` } })).status()).toBe(201);
	const { id: airport } = await createAirport((iataCode) =>
		call('POST', `${admin}/airports`, { token: stepped, data: { iataCode, name: `E2E strict input ${site}`, timeZoneId: 'Asia/Dubai' } })
	);
	const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'T1', siteCode: site });
	levelId = await create('levels', { terminalId: terminal, code: 'L0', ...level });
	const draft = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals' });
	const zoneId = await create(`zone-profiles/${draft}/zones`, { name: 'Snake A', kind: 'Queue', levelId, polygon: '10 10,34 10,34 22,10 22' });
	await create(`zone-profiles/${draft}/lines`, { name: 'Snake A entry', role: 'Entry', levelId, startX: 10, startY: 12, endX: 10, endY: 16, zoneId });
	await create(`zone-profiles/${draft}/lines`, { name: 'Snake A exit', role: 'Exit', levelId, startX: 30, startY: 22, endX: 34, endY: 22, zoneId });
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token: stepped })).json();
	expect((await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token: stepped, data: { geometryHash } })).status()).toBe(200);
	const registered = await call('POST', `${admin}/devices`, {
		token: stepped,
		data: {
			code: 'S-1', family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Xovis', clockSource: 'Ntp',
			placement: { levelId, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: 'Snake A' }
		}
	});
	// The answer carries the device credential: it goes into the message only on failure.
	expect(registered.status(), registered.status() === 201 ? 'device registered' : await registered.text()).toBe(201);
	deviceKey = (await registered.json()).credential;

	// An integration client of the site. Its token exchange carries a member the request does not have (the login freeze:
	// the exchange keeps the lenient parsing until ARV-089a).
	const created = await call('POST', `${admin}/integration-clients`, {
		token: stepped,
		data: { name: `E2E strict input ${site}`, kind: 'Aodb', scopes: ['flights:write'], siteCodes: [site], allowedNetworks: ['10.0.0.0/8'] }
	});
	expect(created.status(), created.status() === 201 ? 'client registered' : await created.text()).toBe(201);
	client = await created.json();
	fs.appendFileSync(integrationSeedsFile, client.totpSecret + '\n');
	const exchanged = await call('POST', `${hosts.integration}/api/v1/auth`, {
		data: { clientId: client.client.clientId, clientSecret: client.clientSecret, totpCode: totpCode(client.totpSecret), grant_type: 'client_credentials' },
		address: inside()
	});
	exchangedWithExtraMember = exchanged.status();
	if (exchangedWithExtraMember === 200) integrationToken = (await exchanged.json()).accessToken;
});

test.describe('Ariva.Api.Main JSON bodies', () => {
	const levelUrl = () => `${admin}/levels/${levelId}`;

	test('a member the request model does not have is refused without an echo and changes nothing', async () => {
		for (const [why, data, sent] of [
			['an unknown member', { ...level, elevationProbe: 3 }, ['elevationProbe']],
			['an unknown member named with markup', { ...level, [xssPayloads[0]]: true }, ['alert(1)']],
			['an unknown member named with SQL', { ...level, [sqlInjectionPayloads[0]]: 1 }, ["OR '1'"]],
			['an unknown nested member', { ...level, extra: { widthMetres: 999 } }, ['extra', '999']],
			['a privileged member', { ...level, siteCode: 'DMO', isDeleted: true }, ['isDeleted', 'DMO']]
		] as [string, Record<string, unknown>, string[]][]) {
			const response = await call('PUT', levelUrl(), { token: stepped, data });
			await expectRefusedBody(response, why, sent);
		}
		const kept = await (await call('GET', levelUrl(), { token: stepped })).json();
		expect(kept, 'the refused bodies changed nothing').toMatchObject({ name: level.name, widthMetres: 100, depthMetres: 50 });
	});

	test('a member sent twice is refused, also in another case', async () => {
		for (const [why, raw, sent] of [
			['the same member twice', '{"name":"Arrivals","floorNumber":0,"widthMetres":100,"widthMetres":321.5,"depthMetres":50}', ['321.5']],
			['a member twice in another case', '{"name":"Arrivals","floorNumber":0,"widthMetres":100,"WidthMetres":321.5,"depthMetres":50}', ['WidthMetres', '321.5']],
			['a name twice', '{"name":"Arrivals","name":"Twin probe","floorNumber":0,"widthMetres":100,"depthMetres":50}', ['Twin probe']]
		] as const) {
			const response = await call('PUT', levelUrl(), { token: stepped, raw, headers: json });
			await expectRefusedBody(response, why, [...sent]);
		}
		expect((await (await call('GET', levelUrl(), { token: stepped })).json()).name).toBe(level.name);
	});

	test('a number sent as a string is refused', async () => {
		for (const [why, data, sent] of [
			['a width as a string', { ...level, widthMetres: '123.25' }, ['123.25']],
			['a floor as a string', { ...level, floorNumber: '7' }, []],
			['a number as markup', { ...level, depthMetres: xssPayloads[1] }, ['onerror']]
		] as [string, Record<string, unknown>, string[]][]) {
			const response = await call('PUT', levelUrl(), { token: stepped, data });
			await expectRefusedBody(response, why, sent);
		}
		expect((await (await call('GET', levelUrl(), { token: stepped })).json()).widthMetres).toBe(100);
	});

	test('a strict body is taken, and the answer writes numbers as numbers', async () => {
		const response = await call('PUT', levelUrl(), { token: stepped, data: { ...level, widthMetres: 120.5 } });
		expect(response.status(), await response.text()).toBe(200);
		const saved = await response.json();
		expect(saved).toMatchObject({ name: level.name, floorNumber: 0, widthMetres: 120.5, depthMetres: 50 });
		expect(typeof saved.widthMetres).toBe('number');
		expect((await call('PUT', levelUrl(), { token: stepped, data: level })).status()).toBe(200);
	});
});

test.describe('repeated query parameters', () => {
	test('Ariva.Api.Main refuses a query parameter sent twice without an echo; once is fine', async () => {
		for (const [why, query, sent] of [
			['a site twice', `siteCode=${site}&siteCode=DMO`, ['DMO', site]],
			['a key twice in another case', `text=first-probe&TEXT=second-probe`, ['first-probe', 'second-probe', 'text']],
			['markup twice', `text=${encodeURIComponent(xssPayloads[0])}&text=${encodeURIComponent(xssPayloads[2])}`, ['alert(1)']],
			['SQL twice', `sortBy=${encodeURIComponent(sqlInjectionPayloads[0])}&sortBy=code`, ["OR '1'", 'sortBy']],
			['a key the endpoint does not read, twice', `unusedProbe=1&unusedProbe=2`, ['unusedProbe']],
			['a page size twice', `pageSize=1&pageSize=500`, ['pageSize']]
		] as const) {
			const response = await call('GET', `${admin}/levels?${query}`, { token: stepped });
			await expectRefusedQuery(response, why, [...sent]);
		}
		const once = await call('GET', `${admin}/levels?siteCode=${site}&text=Arrivals&pageSize=50`, { token: stepped });
		expect(once.status(), await once.text()).toBe(200);
		expect((await once.json()).data.map((l: { id: string }) => l.id)).toContain(levelId);
	});

	test('Ariva.Api.Ingest refuses a query parameter sent twice on the device endpoint', async () => {
		const asDevice = (query: string, credential = deviceKey) =>
			call('GET', `${hosts.ingest}/api/v1/ingest/device${query}`, { headers: credential ? { 'X-Ariva-Device-Key': credential } : {} });
		await expectRefusedQuery(await asDevice('?zone=Snake%20A&zone=Snake%20B'), 'a zone twice', ['Snake']);
		await expectRefusedQuery(await asDevice(`?probe=${encodeURIComponent(xssPayloads[1])}&PROBE=1`), 'markup twice', ['onerror']);
		const once = await asDevice('?zone=Snake%20A');
		expect(once.status()).toBe(200);
		expect((await once.json()).queueZoneName).toBe('Snake A');
		expect((await asDevice('?zone=a&zone=b', '')).status(), 'the device credential is checked first').toBe(401);
	});

	test('Ariva.Api.Integration refuses a query parameter sent twice on the connectivity check', async () => {
		expect(integrationToken, 'the integration token of the run').toBeTruthy();
		const check = (query: string, token = integrationToken) =>
			call('GET', `${hosts.integration}/api/v1/integration/sites/${site}/flights/check${query}`, { token, address: inside() });
		await expectRefusedQuery(await check('?site=DMO&site=DMO'), 'a site twice', ['DMO']);
		await expectRefusedQuery(await check(`?probe=${encodeURIComponent(xssPayloads[0])}&probe=2`), 'markup twice', ['alert(1)']);
		expect((await check('')).status(), 'the token of the run works').toBe(200);
		expect((await check('?probe=1')).status(), 'once is fine').toBe(200);
		expect((await check('?a=1&a=2', '')).status(), 'the token is checked first').toBe(401);
	});
});

test.describe('authentication, authorization and other sites come first', () => {
	const notStrict = { ...level, widthMetres: '100', elevationProbe: 1 };

	test('anonymous callers get 401 and roles without the permission 403, whatever the input', async () => {
		const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
		await expectProblemDetails(await call('PUT', `${admin}/levels/${levelId}`, { data: notStrict }), 401);
		await expectProblemDetails(await call('GET', `${admin}/levels?siteCode=${site}&siteCode=${site}`), 401);
		expect((await call('GET', `${hosts.ingest}/api/v1/ingest/device?zone=a&zone=b`)).status(), 'Ingest without a device credential').toBe(401);
		expect((await call('GET', `${hosts.integration}/api/v1/integration/sites/${site}/flights/check?a=1&a=2`, { address: inside() })).status(), 'Integration without a token')
			.toBe(401);
		expect((await call('PUT', `${admin}/levels/${levelId}`, { token: border, data: notStrict })).status(), 'a level edit by a border role').toBe(403);
		expect((await call('GET', `${admin}/users?text=a&text=b`, { token: border })).status(), 'the user search by a border role').toBe(403);
	});

	test("another site's administrator gets the same 400 for input that is not strict, and 404 for strict input", async () => {
		// e2e.calendaradmin administers E2E3 only: this suite's site is not one of its sites.
		const elsewhere = (await signIn(accounts().calendarAdmin)).accessToken;
		await expectRefusedBody(await call('PUT', `${admin}/levels/${levelId}`, { token: elsewhere, data: notStrict }), 'another site, a body that is not strict', ['elevationProbe']);
		const strict = await call('PUT', `${admin}/levels/${levelId}`, { token: elsewhere, data: { ...level, name: 'Taken over' } });
		expect(strict.status(), 'a level of a site out of reach').toBe(404);
		expect(await strict.text()).not.toContain('Arrivals');
		expect((await (await call('GET', `${admin}/levels/${levelId}`, { token: stepped })).json()).name).toBe(level.name);
	});
});

test.describe('the login freeze (TEMPORARY until ARV-089a)', () => {
	test('sign-in keeps the lenient parsing: an extra member, a member twice or a repeated query parameter does not refuse it', async () => {
		const { userName, password } = accounts().SystemAdministrator;
		const extra = await call('POST', `${hosts.main}/api/auth/login`, { data: { userName, password, rememberMe: true } });
		expect(extra.status(), 'an extra member is skipped as before').toBe(200);
		const twice = await call('POST', `${hosts.main}/api/auth/login`, {
			raw: JSON.stringify({ userName, password }).replace('{', `{"userName":${JSON.stringify(userName)},`),
			headers: json
		});
		expect(twice.status(), 'a member twice is read as before').toBe(200);
		const repeated = await call('POST', `${hosts.main}/api/auth/login?client=web&client=web`, { data: { userName, password } });
		expect(repeated.status(), 'a repeated query parameter is not refused here').toBe(200);
		for (const response of [extra, twice, repeated]) {
			const { accessToken } = await response.json();
			expect(accessToken, 'a token').toBeTruthy();
			await call('POST', `${hosts.main}/api/auth/logout`, { token: accessToken, cookie: response.refreshCookie() });
		}
		// A body that is not JSON is still the fixed 400, as before.
		const broken = await call('POST', `${hosts.main}/api/auth/login`, { raw: '{"userName": tru}', headers: json });
		const problem = await expectProblemDetails(broken, 400);
		expect((problem.errors as Record<string, string[]>).body).toEqual(['The input was not valid.']);
		expectNoLeak(await broken.text(), 'a sign-in body that is not JSON');
	});

	test('the integration token exchange keeps the lenient parsing: an extra member or a repeated query parameter does not refuse it', async () => {
		expect(exchangedWithExtraMember, 'the exchange with an extra member gave a token').toBe(200);
		// The next step's code: each code counts once per client (replay guard).
		const repeated = await call('POST', `${hosts.integration}/api/v1/auth?scope=a&scope=b`, {
			data: { clientId: client.client.clientId, clientSecret: client.clientSecret, totpCode: totpCode(client.totpSecret, 1) },
			address: inside()
		});
		expect(repeated.status(), await repeated.text()).toBe(200);
		expect((await repeated.json()).accessToken, 'a token').toBeTruthy();
	});
});
