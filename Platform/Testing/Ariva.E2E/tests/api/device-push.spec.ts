import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, totpCode } from '../support/accounts';
import { createAirport, createSite, randomLetters } from '../support/airports';
import { hosts, kafkaAvailable } from '../support/hosts';
import { expectStatus } from '../support/expect-status';

// ARV-023: sensor pushes on Ariva.Api.Ingest for the device's own zone and dialect: 202 with the counts; the unknown
// line refused; another zone 403; another dialect 400; not JSON 415; over 256 KB 413 with Connection: close (ARV-233), and the
// next push answered; a Xovis firmware 5 logics push
// mapped to an interval count and an occupancy. ARV-024: an Ouster Detect occupations message through the declarative
// mapping ouster-detect-v1, the mappings list, and an unknown mapping refused at registration. ARV-277: a device that declares
// the lines it counts (Listed) has its push on other entry and exit lines accepted whole, each such line named once with its
// count in the safe form (a line named with markup is never echoed), and a device that declared nothing has no such problem.

test.skip(!databaseAvailable, 'pushes need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const admin = `${hosts.main}/api/v1/admin`;
const ingest = `${hosts.ingest}/api/v1/ingest/zones/${encodeURIComponent('Snake A')}`;
// The site's code is drawn when it is created (ARV-196: drawn again when an earlier run took it).
let site = '';
const keys: Record<string, string> = {};
// A name the zone profile accepts (ARV-159's rule allows markup characters) that must never come back as markup.
const hostileLine = 'Entry <b onmouseover=alert(1)>';
let declaredKey = '';
let adminToken = '';
let level = '';

const push = (path: string, key: string, body: string, contentType = 'application/json') =>
	call('POST', `${ingest}/${path}`, { headers: { 'X-Ariva-Device-Key': key, 'Content-Type': contentType }, raw: body });

test.beforeAll(async () => {
	const { userName, password, totpSecret } = accounts().devicePushAdmin;
	const token = (await (await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) })).json()).accessToken;
	const create = async (path: string, data: unknown) => {
		const response = await call('POST', `${admin}/${path}`, { token, data });
		await expectStatus(response, 201, path);
		const json = await response.json();
		return (json.id ?? json.profile?.id) as string;
	};
	site = await createSite(() => `DP${randomLetters(4)}`, (code) => call('POST', `${admin}/sites`, { token, data: { code, name: code } }));
	const { id: airport } = await createAirport((iataCode) =>
		call('POST', `${admin}/airports`, { token, data: { iataCode, name: `E2E pushes ${site}`, timeZoneId: 'Asia/Dubai' } })
	);
	const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'T1', siteCode: site });
	const levelId = await create('levels', { terminalId: terminal, code: 'L0', name: 'L0', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	adminToken = token;
	level = levelId;
	const draft = await create('zone-profiles/drafts', { siteCode: site, name: 'Arrivals' });
	const zoneId = await create(`zone-profiles/${draft}/zones`, { name: 'Snake A', kind: 'Queue', levelId, polygon: '10 10,34 10,34 22,10 22' });
	await create(`zone-profiles/${draft}/lines`, { name: 'Entry A', role: 'Entry', levelId, startX: 10, startY: 12, endX: 10, endY: 16, zoneId });
	await create(`zone-profiles/${draft}/lines`, { name: 'Exit A', role: 'Exit', levelId, startX: 30, startY: 22, endX: 34, endY: 22, zoneId });
	await create(`zone-profiles/${draft}/lines`, { name: hostileLine, role: 'Entry', levelId, startX: 10, startY: 17, endX: 10, endY: 21, zoneId });
	const { geometryHash } = await (await call('GET', `${admin}/zone-profiles/${draft}/validation`, { token })).json();
	expect((await call('POST', `${admin}/zone-profiles/${draft}/publish`, { token, data: { geometryHash } })).status()).toBe(200);
	for (const [code, dialect, mappingName] of [['S-1', 'Canonical', null], ['S-2', 'Xovis', null], ['L-1', 'Declarative', 'ouster-detect-v1']] as const) {
		const registered = await call('POST', `${admin}/devices`, {
			token,
			data: {
				code, family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect, clockSource: 'Ntp', mappingName,
				placement: { levelId, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: 'Snake A' }
			}
		});
		await expectStatus(registered, 201);
		keys[dialect] = (await registered.json()).credential;
	}

	// ARV-277: a device that declares it counts the exit line only (ARV-259e4), before it ever pushes.
	const declared = await call('POST', `${admin}/devices`, {
		token,
		data: {
			code: 'S-3', family: 'StereoVision', model: 'PC2SE', transport: 'HttpsPush', dialect: 'Canonical', clockSource: 'Ntp', mappingName: null,
			placement: { levelId, x: 20, y: 16, mountingHeightMetres: 5, orientationDegrees: 0, queueZoneName: 'Snake A' }
		}
	});
	await expectStatus(declared, 201);
	const { device, credential } = await declared.json();
	declaredKey = credential;
	await expectStatus(await call('PUT', `${admin}/devices/${device.id}/coverage`, { token, data: { mode: 'Listed', lines: ['Exit A'] } }), 200);
});

test('a canonical push is accepted for the own zone and refused elsewhere', async () => {
	test.skip(!kafkaAvailable, 'an accepted push needs Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP)');
	const now = new Date();
	const minuteAgo = new Date(now.getTime() - 60_000);
	const body = JSON.stringify({
		sentUtc: now.toISOString(),
		packageId: 1,
		tracks: [{ trackId: '7', x: 12.5, y: 14, heightMetres: 1.7, timeUtc: now.toISOString() }],
		intervals: [
			{ lineName: 'Entry A', in: 3, out: 0, fromUtc: minuteAgo.toISOString(), toUtc: now.toISOString() },
			{ lineName: 'Nowhere', in: 3, out: 0, fromUtc: minuteAgo.toISOString(), toUtc: now.toISOString() }
		]
	});
	const accepted = await push('events', keys.Canonical, body);
	await expectStatus(accepted, 202);
	const outcome = await accepted.json();
	expect(outcome.accepted).toBe(2);
	expect(outcome.rejected).toBe(1);

	expect((await call('POST', `${hosts.ingest}/api/v1/ingest/zones/${encodeURIComponent('Snake B')}/events`, { headers: { 'X-Ariva-Device-Key': keys.Canonical }, data: JSON.parse(body) })).status(), 'another zone').toBe(403);
	expect((await push('xovis', keys.Canonical, body)).status(), 'another dialect').toBe(400);
	expect((await push('events', keys.Canonical, body, 'text/plain')).status(), 'not JSON').toBe(415);
	// ARV-233: the body is refused unread, so the answer closes the connection: a device never reuses one Ingest closes after it.
	const oversize = await push('events', keys.Canonical, ' '.repeat(300 * 1024) + '{}');
	expect(oversize.status(), 'over 256 KB').toBe(413);
	expect(oversize.headers()['connection'], 'an oversized push closes its connection').toBe('close');
	await expectStatus(await push('events', keys.Canonical, body), 202);
	expect((await call('POST', `${ingest}/events`, { data: JSON.parse(body) })).status(), 'no key').toBe(401);
});

test('a Xovis firmware 5 logics push becomes an interval count and an occupancy', async () => {
	test.skip(!kafkaAvailable, 'an accepted push needs Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP)');
	const ms = Date.now();
	const body = JSON.stringify({
		logics_data: {
			package_info: { version: '5.0', id: 77, agent_id: 1000 },
			sensor_info: { serial_number: '00:6E:02:08:92:64', type: 'SINGLE_SENSOR', time: ms },
			logics: [
				{ id: 1000, name: 'Entry A', records: [{ from: ms - 60_000, to: ms, samples: 1, samples_expected: 1, counts: [{ id: 1, name: 'fw', value: 5 }, { id: 2, name: 'bw', value: 1 }] }] },
				{ id: 1001, name: 'Snake A', records: [{ from: ms - 60_000, to: ms, samples: 1, samples_expected: 1, counts: [{ id: 3, name: 'balance', value: 12 }] }] }
			]
		}
	});
	const accepted = await push('xovis', keys.Xovis, body);
	await expectStatus(accepted, 202);
	expect((await accepted.json()).accepted).toBe(2);
});

test('an Ouster occupations message is read through the declarative mapping', async () => {
	test.skip(!kafkaAvailable, 'an accepted push needs Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP)');
	const body = JSON.stringify({
		occupations: [
			{ id: 1658947733821, name: 'Snake A', num_objects: 41, num_points: 8800, objects: [1094, 1095] },
			{ id: 1658947733822, name: 'Nowhere', num_objects: 3, num_points: 610, objects: [] }
		]
	});
	const accepted = await push('declarative', keys.Declarative, body);
	await expectStatus(accepted, 202);
	const outcome = await accepted.json();
	expect(outcome.accepted).toBe(1);
	expect(outcome.rejected).toBe(1);
	expect((await push('declarative', keys.Canonical, body)).status()).toBe(400);

	const mappings = await call('GET', `${admin}/devices/mappings`, { token: adminToken });
	expect(mappings.status()).toBe(200);
	expect((await mappings.json()).map((m: { name: string }) => m.name)).toContain('ouster-detect-v1');
	const unknown = await call('POST', `${admin}/devices`, {
		token: adminToken,
		data: {
			code: 'L-9', family: 'Lidar', model: 'Gemini', transport: 'Mqtt', dialect: 'Declarative', clockSource: 'Ntp', mappingName: 'made-up-v1',
			placement: { levelId: level, x: 20, y: 16, mountingHeightMetres: 6, orientationDegrees: 0, queueZoneName: 'Snake A' }
		}
	});
	expect(unknown.status()).toBe(400);
});

test('a push on entry lines its device does not declare it counts is accepted, each line named once and safely (ARV-277)', async () => {
	test.skip(!kafkaAvailable, 'an accepted push needs Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP)');
	const now = new Date();
	const minuteAgo = new Date(now.getTime() - 60_000);
	const crossing = (lineName: string, trackId: string, direction = 'In') => ({ lineName, direction, trackId, timeUtc: now.toISOString() });
	const body = JSON.stringify({
		sentUtc: now.toISOString(),
		crossings: [crossing(hostileLine, '8'), crossing(hostileLine, '9'), crossing('Entry A', '10'), crossing('Exit A', '8', 'Out')],
		intervals: [{ lineName: 'Entry A', in: 2, out: 0, fromUtc: minuteAgo.toISOString(), toUtc: now.toISOString() }]
	});

	const answer = await push('events', declaredKey, body);
	await expectStatus(answer, 202);
	expect(answer.headers()['content-type']).toContain('application/json');
	const outcome = await answer.json();
	expect(outcome.accepted, 'a line the device does not declare is a warning, never a refusal').toBe(5);
	expect(outcome.rejected).toBe(0);
	const warning = 'events accepted on an entry or exit line this device does not declare it counts';
	// In ordinal order of the names ('<' sorts before 'A'); the declared exit line is not named.
	expect(outcome.problems).toEqual([`undeclared line 'Entry ?b onmouseover?alert?1??': 2 ${warning}`, `undeclared line 'Entry A': 2 ${warning}`]);
	for (const problem of outcome.problems as string[]) expect(problem, 'the name is in the safe form, never markup').not.toMatch(/[<>()="&]/);

	// The same push from a device that declared nothing (All) names no line.
	const all = await push('events', keys.Canonical, body);
	await expectStatus(all, 202);
	expect((await all.json()).problems).toEqual([]);
});
