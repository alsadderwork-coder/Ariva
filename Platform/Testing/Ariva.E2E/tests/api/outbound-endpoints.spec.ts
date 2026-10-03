import http from 'node:http';
import type { AddressInfo } from 'node:net';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { expectNoLeak, expectProblemDetails } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-045: outbound endpoints and the ACRIS connector. An administrator with a recent second factor registers the
// systems Ariva calls; the secret is never shown again. Registration refuses plain HTTP outside the lab, the metadata
// address, /0 networks, reserved headers and token paths on another origin. Ariva.Api.Integration pulls ACRIS flights
// from a stand-in AODB (the role the ARV-029 emulator will play) with the endpoint's API key, applies the site's
// flights as the endpoint's feed, and records a redirect or an address outside the endpoint's networks as a refused
// call without following or reaching it.

test.skip(!databaseAvailable, 'outbound endpoints need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/admin/outbound-endpoints`;
const run = Date.now().toString(36);
const code = (name: string) => `${name}-${run}`.slice(0, 24);
const day = new Date().toISOString().slice(0, 10);
const at = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString().replace(/\.\d{3}Z$/, 'Z');
const number = 500 + Math.floor(Math.random() * 400);

let admin: string;
let server: http.Server;
let port: number;
const seen: { path: string; key: string | undefined }[] = [];

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

const connection = (overrides: Record<string, unknown> = {}) => ({
	baseUrl: 'https://aodb.example.test/api',
	allowedNetworks: ['10.0.0.0/8'],
	authKind: 'ApiKeyHeader',
	headerName: 'X-Api-Key',
	retryCount: 0,
	...overrides
});

const register = (body: Record<string, unknown>, token = admin) => call('POST', api, { token, data: body });

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().outboundAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;

	// The stand-in AODB: ACRIS flights for DMO behind an API key, and a redirect towards the metadata service.
	server = http.createServer((request, response) => {
		seen.push({ path: request.url ?? '', key: request.headers['x-api-key'] as string | undefined });
		if (request.url === '/acris/moved') {
			response.writeHead(302, { Location: 'http://169.254.169.254/latest/meta-data/' }).end();
			return;
		}
		if (request.headers['x-api-key'] !== `key-${run}`) {
			response.writeHead(401).end();
			return;
		}
		response.writeHead(200, { 'Content-Type': 'application/json' }).end(
			JSON.stringify({
				flights: [
					{ flightNumber: { airlineCode: 'EK', trackNumber: `0${number}` }, originDate: day, departureAirport: 'DXB', arrivalAirport: 'DMO', arrival: { scheduled: at(150), gate: 'G2' } },
					{ flightNumber: { airlineCode: 'EK', trackNumber: `${number + 1}` }, originDate: day, departureAirport: 'DXB', arrivalAirport: 'KWI' }
				]
			})
		);
	});
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
	port = (server.address() as AddressInfo).port;
});

test.afterAll(async () => {
	await new Promise((resolve) => server?.close(resolve));
});

test('an administrator registers an endpoint with a second factor; the secret is never shown and unsafe connections are refused', async () => {
	const created = await register({ code: code('generic'), name: 'E2E generic', purpose: 'Generic', siteCodes: ['DMO'], connection: connection(), secret: { apiKey: `key-${run}` } });
	expect(created.status(), await created.text()).toBe(201);
	const text = await created.text();
	expect(text).not.toContain(`key-${run}`);
	expect(await created.json()).toMatchObject({ baseUrl: 'https://aodb.example.test/api/', authKind: 'ApiKeyHeader', headerName: 'X-Api-Key', status: 'Active' });
	const listed = await (await call('GET', api, { token: admin })).text();
	expect(listed).toContain(code('generic'));
	expect(listed).not.toContain(`key-${run}`);

	// Moving an endpoint without its secret would send the stored key to the new place: refused.
	const id = (await created.json()).id;
	const moved = connection({ baseUrl: 'https://attacker.example.test/', allowedNetworks: ['203.0.113.7/32'] });
	const withoutSecret = await call('PUT', `${api}/${id}`, { token: admin, data: { name: 'E2E moved', siteCodes: ['DMO'], connection: moved } });
	await expectProblemDetails(withoutSecret, 400);
	expect(await withoutSecret.text()).toContain('needs the secret again');
	const withSecret = await call('PUT', `${api}/${id}`, { token: admin, data: { name: 'E2E moved', siteCodes: ['DMO'], connection: moved, secret: { apiKey: `new-key-${run}` } } });
	expect(withSecret.status(), await withSecret.text()).toBe(200);

	const passwordOnly = (await signIn(accounts().SystemAdministrator)).accessToken;
	expect((await register({ code: code('nomfa'), name: 'x', purpose: 'Generic', siteCodes: ['DMO'], connection: connection(), secret: { apiKey: 'k-12345678' } }, passwordOnly)).status(),
		'a critical action').toBe(401);

	for (const [why, overrides] of [
		['plain HTTP outside the lab', { baseUrl: 'http://aodb.example.test/' }],
		['the metadata address', { baseUrl: 'https://169.254.169.254/latest/meta-data/', allowedNetworks: ['169.254.0.0/16'] }],
		['credentials in the URL', { baseUrl: 'https://user:pass@aodb.example.test/' }],
		['a /0 network', { allowedNetworks: ['0.0.0.0/0'] }],
		['no network', { allowedNetworks: [] }],
		['a reserved header', { headerName: 'Host' }],
		['a token path on another origin', { authKind: 'OAuth2ClientCredentials', headerName: undefined, tokenPath: 'https://evil.example/token', clientId: 'ariva' }],
		['an unknown kind', { authKind: 'Basic' }],
		['a file URL', { baseUrl: 'file:///etc/passwd' }]
	] as const) {
		const refused = await register({ code: code('bad'), name: 'E2E bad', purpose: 'Generic', siteCodes: ['DMO'], connection: connection(overrides), secret: { apiKey: 'k-12345678' } });
		await expectProblemDetails(refused, 400);
		expectNoLeak(await refused.text(), why);
	}

	const wrongSecret = await register({ code: code('badsecret'), name: 'x', purpose: 'Generic', siteCodes: ['DMO'], connection: connection(), secret: { hmacKey: 'AAAA' } });
	expect(wrongSecret.status(), 'a secret field the kind does not use').toBe(400);
});

test('the ACRIS pull applies the site\'s flights with the endpoint\'s key, and refuses a redirect and an address outside the networks', async () => {
	const lab = { baseUrl: `http://127.0.0.1:${port}/`, allowedNetworks: ['127.0.0.0/8'], pollSeconds: 30 };
	const good = await register({
		code: code('acris'), name: 'E2E ACRIS', purpose: 'AcrisFlights', siteCodes: ['DMO'],
		connection: connection({ ...lab, pullPath: '/acris/flights' }), secret: { apiKey: `key-${run}` }
	});
	expect(good.status(), await good.text()).toBe(201);
	const moved = await register({
		code: code('moved'), name: 'E2E moved', purpose: 'AcrisFlights', siteCodes: ['DMO'],
		connection: connection({ ...lab, pullPath: '/acris/moved' }), secret: { apiKey: `key-${run}` }
	});
	expect(moved.status(), await moved.text()).toBe(201);
	// A host name that resolves to loopback, for an endpoint whose networks do not include it: refused at connection time.
	const outside = await register({
		code: code('outside'), name: 'E2E outside', purpose: 'AcrisFlights', siteCodes: ['DMO'],
		connection: connection({ baseUrl: `https://localhost:${port}/`, allowedNetworks: ['10.0.0.0/8'], pullPath: '/acris/flights', pollSeconds: 30 }),
		secret: { apiKey: `key-${run}` }
	});
	expect(outside.status(), await outside.text()).toBe(201);

	type Row = { code: string; last_status: string | null; consecutive_failures: number };
	let rows: Row[] = [];
	await expect
		.poll(async () => {
			rows = await query<Row>(`SELECT code, last_status, consecutive_failures FROM outbound_endpoint WHERE code = ANY($1)`, [[code('acris'), code('moved'), code('outside')]]);
			return rows.filter((r) => r.last_status !== null).length;
		}, { timeout: 60_000, intervals: [2_000] })
		.toBe(3);
	const status = Object.fromEntries(rows.map((r) => [r.code, r]));
	expect(status[code('acris')].last_status).toBe('Pulled 2 flights: 1 applied, 1 refused.');
	expect(status[code('moved')]).toMatchObject({ consecutive_failures: 1 });
	expect(status[code('moved')].last_status).toContain('redirect');
	expect(status[code('outside')].last_status).toContain('outside the endpoint');

	const legs = await query<{ direction: string; gate: string; feed: string }>(`SELECT direction, gate, feed FROM flight_leg WHERE site_code = 'DMO' AND flight_key = $1`, [
		`EK${number}-${day.replaceAll('-', '')}-A`
	]);
	expect(legs).toEqual([{ direction: 'Arrival', gate: 'G2', feed: `acris-${code('acris')}` }]);
	expect(seen.filter((s) => s.path === '/acris/flights').every((s) => s.key === `key-${run}`), 'the API key on every call').toBe(true);
	expect(seen.some((s) => s.path.includes('meta-data')), 'the redirect was not followed').toBe(false);
});
