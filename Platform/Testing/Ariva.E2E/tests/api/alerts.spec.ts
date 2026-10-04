import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { createClient } from 'redis';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-039: the alert lifecycle through the API and the live hub. The demo airport's rules R-001 (owner border shift
// supervisor, escalation to a named contact), R-003 (no owner) and R-004 (owner handler station manager, escalation
// to the terminal duty manager) have alerts as the Stream host's evaluation writes them (planted here in the database,
// as the live hub suite plants Redis snapshots). Each transition is taken by the role responsible and refused (404) to
// the others; a transition out of order answers 409; resolving needs a note; every change reaches the live hub's
// alert groups of the responsible roles only.

test.skip(!databaseAvailable, 'alerts need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/alerts`;
const redisUrl = process.env.ARIVA_E2E_REDIS_URL;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

const ids: Record<string, string> = {};

// One open alert per rule and target: earlier runs' open alerts on the targets used here (and only those: live-operations.spec
// plants its own on A-RES) are resolved first.
async function plant(): Promise<void> {
	const db = database();
	await db.connect();
	try {
		await db.query(`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = now(), resolved_by = 'e2e-cleanup', resolution_note = 'cleanup'
		                WHERE state <> 'Resolved' AND site_code = 'DMO' AND ((rule_code = 'R-001' AND zone_name = 'A-VIS') OR (rule_code = 'R-003' AND device_code = 'S-17')
		                      OR (rule_code = 'R-004' AND zone_name = 'CI-C'))`);
		for (const [key, code, zone, device, metric, value] of [
			['r001', 'R-001', 'A-VIS', null, 'Nowcast', 16.3],
			['r003', 'R-003', 'A-VIS', 'S-17', 'SensorOffline', 1],
			['r004', 'R-004', 'CI-C', null, 'BinP90', 15.1]
		] as const) {
			const result = await db.query(
				`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
				                    escalate_to_role, escalation_contact, raised_utc, raised_value, state)
				 SELECT gen_random_uuid(), site_code, id, code, name, $2, $3, metric, severity, owner_role, escalate_after_minutes, escalate_to_role, escalation_contact,
				        date_trunc('minute', now()) - interval '2 minutes', $4, 'Raised'
				 FROM alert_rule WHERE site_code = 'DMO' AND code = $1 AND metric = $5 RETURNING id`,
				[code, zone, device, value, metric]
			);
			expect(result.rowCount, `${code} is seeded on DMO`).toBe(1);
			ids[key] = result.rows[0].id;
		}
	} finally {
		await db.end();
	}
}

function hub(token: string): HubConnection {
	return new HubConnectionBuilder()
		.withUrl(`${hosts.main}/hubs/live`, { transport: HttpTransportType.WebSockets, skipNegotiation: true, accessTokenFactory: () => token })
		.configureLogging(LogLevel.None)
		.build();
}

let border: string, terminal: string, handler: string, elsewhere: string;

test.beforeAll(async () => {
	await plant();
	[border, terminal, handler, elsewhere] = await Promise.all(
		[accounts().dmoBorder, accounts().dmoTerminal, accounts().dmoHandler, accounts().BorderShiftSupervisor].map(async (a) => (await signIn(a)).accessToken)
	);
});

test('each role sees only the alerts it is responsible for, in its own sites', async () => {
	const listed = async (token: string) => ((await (await call('GET', `${api}?siteCode=DMO&open=true&pageSize=500`, { token })).json()).data as any[]).map((a) => a.id);
	expect(await listed(border)).toEqual(expect.arrayContaining([ids.r001, ids.r003]));
	expect(await listed(border)).not.toContain(ids.r004);
	expect(await listed(handler)).toEqual(expect.arrayContaining([ids.r003, ids.r004]));
	expect(await listed(handler)).not.toContain(ids.r001);
	expect(await listed(terminal)).toContain(ids.r003);
	expect(await listed(terminal), 'not before it is escalated to the terminal duty manager').not.toContain(ids.r004);

	for (const [token, id, why] of [
		[terminal, ids.r001, 'border alert'],
		[handler, ids.r001, 'border alert'],
		[elsewhere, ids.r003, 'another site'],
		[terminal, ids.r004, 'not escalated yet']
	] as const) {
		const read = await call('GET', `${api}/${id}`, { token });
		expect(read.status(), why).toBe(404);
		expect((await call('POST', `${api}/${id}/acknowledge`, { token, data: { note: null } })).status(), why).toBe(404);
	}
	expect((await call('GET', api)).status()).toBe(401);
	expect((await call('GET', `${api}?state=Raised,Resolved`, { token: border })).status()).toBe(400);
});

test('the owner acknowledges, escalates and resolves with a note; out-of-order actions answer 409', async () => {
	const ack = await call('POST', `${api}/${ids.r001}/acknowledge`, { token: border, data: { note: 'Opening two desks' } });
	expect(ack.status(), await ack.text()).toBe(200);
	expect(await ack.json()).toMatchObject({ state: 'Acknowledged', acknowledgedBy: 'e2e.dmoborder', acknowledgedNote: 'Opening two desks', escalationDueUtc: null });
	expect((await call('POST', `${api}/${ids.r001}/acknowledge`, { token: border, data: {} })).status(), 'acknowledged once').toBe(409);

	const escalated = await call('POST', `${api}/${ids.r001}/escalate`, { token: border, data: { note: 'Need the duty officer' } });
	expect(escalated.status(), await escalated.text()).toBe(200);
	expect(await escalated.json()).toMatchObject({ state: 'Escalated', escalationContact: 'Border operations duty officer' });

	for (const [why, note] of [
		['no note', ''],
		['blank note', '   '],
		['control character', 'Bell\u0007'],
		['bidirectional override', 'Done \u202Eevil'],
		['too long', 'n'.repeat(501)]
	] as const) {
		const refused = await call('POST', `${api}/${ids.r001}/resolve`, { token: border, data: { note } });
		expect(refused.status(), why).toBe(400);
		expectNoLeak(await refused.text(), why);
	}
	const resolved = await call('POST', `${api}/${ids.r001}/resolve`, { token: border, data: { note: '<script>alert(1)</script> desks 9 to 12 open' } });
	expect(resolved.status(), await resolved.text()).toBe(200);
	expect(await resolved.json()).toMatchObject({ state: 'Resolved', resolution: 'Manual', resolutionNote: '<script>alert(1)</script> desks 9 to 12 open' });
	expect(resolved.headers()['content-type']).toContain('application/json');
	for (const action of ['acknowledge', 'escalate', 'resolve'])
		expect((await call('POST', `${api}/${ids.r001}/${action}`, { token: border, data: { note: 'again' } })).status(), `${action} after resolve`).toBe(409);
});

test('an escalation hands the alert to the escalation role, who takes it on', async () => {
	expect((await call('POST', `${api}/${ids.r004}/escalate`, { token: handler, data: {} })).status()).toBe(200);
	const seen = await call('GET', `${api}/${ids.r004}`, { token: terminal });
	expect(seen.status(), 'the terminal duty manager sees it once escalated').toBe(200);
	const ack = await call('POST', `${api}/${ids.r004}/acknowledge`, { token: terminal, data: { note: 'Taking over' } });
	expect(ack.status(), await ack.text()).toBe(200);
	expect((await call('POST', `${api}/${ids.r004}/resolve`, { token: terminal, data: { note: 'Counters reopened' } })).status()).toBe(200);
});

test('alert changes reach the live hub groups of the responsible roles only', async () => {
	test.skip(!redisUrl, 'the live hub needs the run\'s Redis (ARIVA_E2E_REDIS_URL)');
	const borderHub = hub(border);
	const handlerHub = hub(handler);
	const elsewhereHub = hub(elsewhere);
	const received: Record<string, any[]> = { border: [], handler: [] };
	borderHub.on('alert', (n) => received.border.push(n));
	handlerHub.on('alert', (n) => received.handler.push(n));
	await Promise.all([borderHub.start(), handlerHub.start(), elsewhereHub.start()]);
	try {
		expect(await borderHub.invoke('JoinAlerts', 'DMO')).toBe(1);
		expect(await handlerHub.invoke('JoinAlerts', 'DMO')).toBe(1);
		await expect(elsewhereHub.invoke('JoinAlerts', 'DMO')).rejects.toThrow(/forbidden/);
		await expect(borderHub.invoke('JoinAlerts', 'dmo')).rejects.toThrow(/invalid_site/);

		// An action through the API is announced after commit, to the owner's group (and every role's for an alert without owner).
		expect((await call('POST', `${api}/${ids.r003}/acknowledge`, { token: handler, data: {} })).status()).toBe(200);
		// A notice the Stream host would publish, for a border-owned alert: the handler's screen is not told.
		const redis = createClient({ url: redisUrl });
		await redis.connect();
		try {
			const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
			const notice = {
				alertId: crypto.randomUUID(), siteCode: 'DMO', ruleCode: 'R-001', ruleName: 'Nowcast above 15 min', zoneName: 'A-VIS', deviceCode: null,
				metric: 'Nowcast', severity: 'Critical', state: 'Raised', raisedUtc: new Date().toISOString(), raisedValue: 16.3,
				ownerRole: 'BorderShiftSupervisor', escalateToRole: null, escalated: false, resolvedUtc: null, publishedUtc: new Date().toISOString()
			};
			await redis.publish(`${instance}live:alerts`, JSON.stringify(notice));
			await redis.publish(`${instance}live:alerts`, JSON.stringify({ ...notice, alertId: crypto.randomUUID(), ownerRole: 'Root' }));

			await expect.poll(() => received.border.map((n) => n.alertId), { timeout: 10_000 }).toEqual(expect.arrayContaining([ids.r003, notice.alertId]));
			await new Promise((resolve) => setTimeout(resolve, 1000));
			expect(received.handler.map((n) => n.alertId)).toContain(ids.r003);
			expect(received.handler.map((n) => n.alertId), 'a border alert is not sent to the handler').not.toContain(notice.alertId);
			expect([...received.border, ...received.handler].every((n) => n.ownerRole !== 'Root'), 'an implausible notice is dropped').toBe(true);
			expect(received.border.find((n) => n.alertId === ids.r003)).toMatchObject({ state: 'Acknowledged', siteCode: 'DMO' });
			expect(JSON.stringify(received.border), 'notices carry no people').not.toContain('e2e.dmohandler');
		} finally {
			await redis.quit();
		}
	} finally {
		await Promise.all([borderHub.stop(), handlerHub.stop(), elsewhereHub.stop()]);
		await call('POST', `${api}/${ids.r003}/resolve`, { token: handler, data: { note: 'e2e done' } });
	}
});
