import fs from 'node:fs';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, integrationSeedsFile, login, totpCode } from '../support/accounts';
import { expectNoLeak, expectProblemDetails } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-044: AIDX 22.1 inbound. An AODB client with flights:write for DMO pushes IATA_AIDX_FlightLegNotifRQ messages
// (the role the ARV-029 emulator will play); legs arriving at or leaving DMO are applied as the client's own feed and
// the answer is an IATA_AIDX_FlightLegRS acknowledgement with a Warning per refused or unchanged leg. A DOCTYPE (XXE,
// entity expansion, an external DTD), a message that breaks the profile, one nested too deep, one over 5 MB and
// anything that is not XML are refused as a whole, without echoing the message and without reaching the database.

test.skip(!databaseAvailable, 'AIDX needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const ns = 'http://www.iata.org/IATA/2007/00';
const url = (code = 'DMO') => `${hosts.integration}/api/v1/integration/sites/${code}/aodb/aidx`;
const inside = () => `10.${1 + Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250)}.${1 + Math.floor(Math.random() * 250)}`;
const run = 1000 + Math.floor(Math.random() * 8999);
const at = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString().replace(/\.\d{3}Z$/, 'Z');
const today = new Date().toISOString().slice(0, 10);
const day = today.replaceAll('-', '');

type Credentials = { client: { id: string; clientId: string }; clientSecret: string; totpSecret: string };
let admin: string;
let aodb: { credentials: Credentials; token: string };
let allocationsOnly: { credentials: Credentials; token: string };

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

async function client(name: string, scopes: string[]) {
	const created = await call('POST', `${hosts.main}/api/v1/admin/integration-clients`, {
		token: admin,
		data: { name, kind: 'Aodb', scopes, siteCodes: ['DMO'], allowedNetworks: ['10.0.0.0/8'] }
	});
	expect(created.status(), await created.text()).toBe(201);
	const credentials: Credentials = await created.json();
	fs.appendFileSync(integrationSeedsFile, credentials.totpSecret + '\n');
	const exchanged = await call('POST', `${hosts.integration}/api/v1/auth`, {
		data: { clientId: credentials.client.clientId, clientSecret: credentials.clientSecret, totpCode: totpCode(credentials.totpSecret) },
		address: inside()
	});
	expect(exchanged.status(), await exchanged.text()).toBe(200);
	return { credentials, token: (await exchanged.json()).accessToken as string };
}

const leg = (number: number, from: string, to: string, times = '') => `
  <FlightLeg>
    <LegIdentifier>
      <Airline CodeContext="3">RJ</Airline><FlightNumber>${number}</FlightNumber>
      <DepartureAirport CodeContext="3">${from}</DepartureAirport><ArrivalAirport CodeContext="3">${to}</ArrivalAirport>
      <OriginDate>${today}</OriginDate>
    </LegIdentifier>
    <LegData>
      <AirportResources Usage="Actual">
        <Resource DepartureOrArrival="${to === 'DMO' ? 'Arrival' : 'Departure'}"><AircraftParkingPosition>B${number % 50}</AircraftParkingPosition><PassengerGate>G${number % 30}</PassengerGate></Resource>
      </AirportResources>
      <OperationTime OperationQualifier="${to === 'DMO' ? 'ONB' : 'OFB'}" CodeContext="9750" TimeType="SCT">${at(120)}</OperationTime>
      <OperationTime OperationQualifier="${to === 'DMO' ? 'ONB' : 'OFB'}" CodeContext="9750" TimeType="EST">${at(130)}</OperationTime>
      ${times}
      <AircraftInfo><AircraftType>320</AircraftType></AircraftInfo>
    </LegData>
    <TPA_Extension><x:note xmlns:x="urn:aodb">kept unread</x:note></TPA_Extension>
  </FlightLeg>`;

const message = (legs: string, prolog = '', stamp = at(-1)) =>
	`<?xml version="1.0" encoding="UTF-8"?>${prolog}<IATA_AIDX_FlightLegNotifRQ xmlns="${ns}" Version="22.1" TimeStamp="${stamp}" TransactionIdentifier="E2E-${run}"><Originator CompanyShortName="E2E"/>${legs}</IATA_AIDX_FlightLegNotifRQ>`;

const push = (xml: string, options: { key?: string; token?: string; type?: string; code?: string } = {}) =>
	call('POST', url(options.code), {
		token: options.token ?? aodb.token,
		address: inside(),
		raw: xml,
		headers: { 'Content-Type': options.type ?? 'application/xml', ...(options.key ? { 'Idempotency-Key': options.key } : {}) }
	});

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().aidxAdmin;
	const signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	expect(signedIn.status(), await signedIn.text()).toBe(200);
	admin = (await signedIn.json()).accessToken;
	aodb = await client('E2E AODB AIDX', ['flights:write']);
	allocationsOnly = await client('E2E AODB allocations', ['allocations:write']);
});

test("the site's legs of a message are applied and acknowledged; a leg that does not touch the site is refused", async () => {
	const xml = message(leg(run, 'AMM', 'DMO') + leg(run + 1, 'AMM', 'CAI') + leg(run + 2, 'DMO', 'CAI'));
	const answer = await push(xml, { key: `E2E-AIDX-${run}-1` });
	const text = await answer.text();
	expect(answer.status(), text).toBe(200);
	expect(answer.headers()['content-type']).toContain('application/xml');
	expect(text).toContain('IATA_AIDX_FlightLegRS');
	expect(text).toContain('<Success');
	expect(text).toContain(`TransactionIdentifier="E2E-${run}"`);
	expect(text).toMatch(/RecordID="1" Status="Refused"/);
	expect(text).not.toMatch(/RecordID="0"|RecordID="2"/);

	const feed = 'api-' + aodb.credentials.client.clientId.slice(3);
	const rows = await query<{ flight_key: string; direction: string; stand: string; feed: string }>(
		`SELECT flight_key, direction, stand, feed FROM flight_leg WHERE site_code = 'DMO' AND flight_key IN ($1, $2, $3) ORDER BY flight_key`,
		[`RJ${run}-${day}-A`, `RJ${run + 1}-${day}-A`, `RJ${run + 2}-${day}-D`]
	);
	expect(rows.map((r) => `${r.flight_key} ${r.direction} ${r.stand} ${r.feed}`)).toEqual([
		`RJ${run}-${day}-A Arrival B${run % 50} ${feed}`,
		`RJ${run + 2}-${day}-D Departure B${(run + 2) % 50} ${feed}`
	]);

	const replay = await push(xml, { key: `E2E-AIDX-${run}-1` });
	expect(replay.headers()['idempotent-replayed']).toBe('true');
	expect(await replay.text()).toBe(text);
	await expectProblemDetails(await push(message(leg(run, 'AMM', 'DMO')), { key: `E2E-AIDX-${run}-1` }), 422);

	const again = await push(xml);
	expect(again.status(), 'without a key the message is simply applied again').toBe(200);
	const againText = await again.text();
	expect(againText).toMatch(/RecordID="0" Status="Unchanged"/);
	expect(againText).toMatch(/RecordID="2" Status="Unchanged"/);
});

test('a later message moves a leg on: actual touchdown and on-block, and a cancellation', async () => {
	const update = message(
		leg(
			run,
			'AMM',
			'DMO',
			`<OperationTime OperationQualifier="TDN" CodeContext="9750" TimeType="ACT">${at(115)}</OperationTime><OperationTime OperationQualifier="ONB" CodeContext="9750" TimeType="ACT">${at(121)}</OperationTime>`
		) + leg(run + 2, 'DMO', 'CAI').replace('<LegData>', '<LegData><OperationalStatus CodeContext="2005">DX</OperationalStatus>')
	);
	const answer = await push(update);
	expect(answer.status(), await answer.text()).toBe(200);
	const rows = await query<{ flight_key: string; status: string; on_block: boolean }>(
		`SELECT flight_key, status, on_block_utc IS NOT NULL AS on_block FROM flight_leg WHERE flight_key IN ($1, $2) ORDER BY flight_key`,
		[`RJ${run}-${day}-A`, `RJ${run + 2}-${day}-D`]
	);
	expect(rows).toEqual([
		{ flight_key: `RJ${run}-${day}-A`, status: 'OnBlock', on_block: true },
		{ flight_key: `RJ${run + 2}-${day}-D`, status: 'Cancelled', on_block: false }
	]);
});

test('XXE, entity expansion, external DTDs, profile breaks, deep nesting, oversized and non-XML messages are refused whole', async () => {
	const good = leg(run + 10, 'AMM', 'DMO');
	const cases: [string, () => ReturnType<typeof call>, number][] = [
		['XXE', () => push(message(good, '<!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]>').replace('<Originator', '<Originator Note="&x;"')), 400],
		['entity expansion', () => push(message(good, '<!DOCTYPE r [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>')), 400],
		['an external DTD', () => push(message(good, '<!DOCTYPE r SYSTEM "http://169.254.169.254/latest/meta-data/">')), 400],
		[
			'a flight number with SQL',
			() => push(message(good.replace(`<FlightNumber>${run + 10}</FlightNumber>`, "<FlightNumber>1' OR '1'='1</FlightNumber>"))),
			400
		],
		[
			'markup in a code',
			() => push(message(good.replace('<Airline CodeContext="3">RJ</Airline>', '<Airline CodeContext="3"><script>alert(1)</script></Airline>'))),
			400
		],
		['not well-formed', () => push(message(good).slice(0, -20)), 400],
		['nested too deep', () => push(message(good.replace('<TPA_Extension>', '<TPA_Extension>' + '<y>'.repeat(40) + '</y>'.repeat(40)))), 400],
		['a TimeStamp with an offset', () => push(message(good, '', '2026-10-03T12:00:00+03:00')), 400],
		['no TimeStamp', () => push(message(good).replace(/ TimeStamp="[^"]*"/, '')), 400],
		['JSON', () => push('{"items":[]}', { type: 'application/json' }), 415]
	];
	for (const [why, sending, status] of cases) {
		const answer = await sending();
		const text = await answer.text();
		expect(answer.status(), `${why}: ${text.slice(0, 300)}`).toBe(status);
		expect(answer.headers()['content-type'], why).toContain('application/problem+json');
		expectNoLeak(text, why);
		for (const echoed of ['passwd', '169.254', "OR '1'='1", '<script>', 'meta-data']) expect(text, why).not.toContain(echoed);
	}
	// Over the size limit the host answers 413 from the Content-Length and stops reading, so the client may instead see
	// the connection closed while it is still sending (as in flight-schedules.spec.ts). Either way nothing is read.
	const oversized = await push(message(good + '<!--' + 'x'.repeat(5_300_000) + '-->')).then(
		async (r) => {
			expect(r.headers()['content-type'], 'more than 5 MB').toContain('application/problem+json');
			expectNoLeak(await r.text(), 'more than 5 MB');
			return r.status();
		},
		(e: Error) => `closed: ${e.message}`
	);
	expect([413, 'closed: fetch failed'], 'more than 5 MB').toContain(oversized);
	const rows = await query(`SELECT 1 FROM flight_leg WHERE flight_key = $1`, [`RJ${run + 10}-${day}-A`]);
	expect(rows, 'nothing of a refused message was applied').toHaveLength(0);
});

test("AIDX needs flights:write and the client's own site", async () => {
	const xml = message(leg(run + 20, 'AMM', 'DMO'));
	expect((await push(xml, { token: allocationsOnly.token })).status(), 'another scope').toBe(403);
	expect((await push(xml, { code: 'E2E1' })).status(), 'another site').toBe(403);
	expect((await call('POST', url(), { raw: xml, headers: { 'Content-Type': 'application/xml' }, address: inside() })).status(), 'no token').toBe(401);
	const recorded = await query<{ status: number }>(`SELECT status FROM integration_call WHERE client_id = $1 AND route LIKE '%aodb/aidx'`, [
		aodb.credentials.client.clientId
	]);
	expect(recorded.map((r) => r.status)).toEqual(expect.arrayContaining([200, 400, 413, 415, 403]));
});
