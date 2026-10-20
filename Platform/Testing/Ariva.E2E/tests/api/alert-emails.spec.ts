import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { expectNoLeak } from '../support/api-assertions';
import { hosts, smtp4dev } from '../support/hosts';
import { expectStatus } from '../support/expect-status';

// ARV-040: alert emails through the API, sent by Ariva.Api.Integration to the run's smtp4dev. An administrator gives the DMO
// border shift supervisor and terminal duty manager their addresses (an address that is not one plain address is
// refused) and creates a rule that notifies by email, owned by the supervisors and escalating to the duty managers.
// Its alert is planted in the database, as the Stream host's evaluation writes it (Stream is not part of the E2E run);
// when the supervisor escalates it, the duty manager, and nobody else, gets one email: from Ariva's address, to one
// recipient, with a one-line subject naming the rule, and with no header a value could have added. ARV-181b2: an open desks
// below recommendation alert whose rule was changed in the database to escalate to the duty managers writes them no email.
// ARV-187: an email to the duty manager about a predicted nowcast alert, pending from before the upgrade, is never sent.

test.skip(!databaseAvailable, 'alert emails need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const usersApi = `${hosts.main}/api/v1/admin/users`;
const rulesApi = `${hosts.main}/api/v1/admin/alert-rules`;
const alertsApi = `${hosts.main}/api/v1/alerts`;
const runId = Date.now().toString(36);
const borderAddress = `dmoborder.${runId}@ariva.e2e`;
const terminalAddress = `dmoterminal.${runId}@ariva.e2e`;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

type Smtp4devMessage = { id: string; from: string; to: string[]; subject: string };

async function messagesTo(address: string): Promise<Smtp4devMessage[]> {
	const response = await fetch(`${smtp4dev.url}/api/messages?pageSize=1000&sortColumn=receivedDate&sortIsDescending=true`);
	expect(response.status, 'smtp4dev answers').toBe(200);
	const page = (await response.json()) as { results: Smtp4devMessage[] };
	return page.results.filter((m) => m.to.some((to) => to.toLowerCase() === address.toLowerCase()));
}

let admin: string, border: string, terminal: string;
let ruleCode: string, ruleId: string, alertId: string;

async function userId(userName: string): Promise<string> {
	const found = await call('GET', `${usersApi}?text=${userName}&pageSize=10`, { token: admin });
	await expectStatus(found, 200);
	const user = ((await found.json()).data as any[]).find((u) => u.userName === userName);
	expect(user, `${userName} exists`).toBeTruthy();
	return user.id;
}

test.beforeAll(async () => {
	[admin, border, terminal] = await Promise.all([accounts().emailAdmin, accounts().dmoBorder, accounts().dmoTerminal].map(async (a) => (await signIn(a)).accessToken));
});

let desksRuleId: string, desksAlertId: string;
let predictedRuleId: string, predictedAlertId: string;

test.afterAll(async () => {
	const db = database();
	await db.connect();
	try {
		for (const [rule, alert] of [
			[ruleId, alertId],
			[desksRuleId, desksAlertId],
			[predictedRuleId, predictedAlertId]
		]) {
			if (!rule || !alert) continue;
			await db.query(
				`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = now(), resolved_by = 'e2e-cleanup', resolution_note = 'cleanup'
				 WHERE id = $1 AND state <> 'Resolved'`,
				[alert]
			);
			await db.query(`UPDATE alert_rule SET enabled = false WHERE id = $1`, [rule]);
		}
	} finally {
		await db.end();
	}
});

test('an address that is not one plain address is refused; a plain one is kept', async () => {
	const terminalId = await userId(accounts().dmoTerminal.userName);
	for (const [why, email] of [
		['line break and a second header', 'terminal@ariva.e2e\r\nBcc: attacker@example.com'],
		['display name', 'Terminal <terminal@ariva.e2e>'],
		['two addresses', 'terminal@ariva.e2e, attacker@example.com'],
		['no domain dot', 'terminal@localhost']
	] as const) {
		const refused = await call('PUT', `${usersApi}/${terminalId}`, { token: admin, data: { displayName: 'DMO duty manager', email } });
		expect(refused.status(), why).toBe(400);
		expectNoLeak(await refused.text(), why);
	}
	for (const [userName, email] of [
		[accounts().dmoTerminal.userName, terminalAddress],
		[accounts().dmoBorder.userName, borderAddress]
	]) {
		const kept = await call('PUT', `${usersApi}/${await userId(userName)}`, { token: admin, data: { displayName: userName, email: `  ${email} ` } });
		await expectStatus(kept, 200);
		expect(await kept.json()).toMatchObject({ email });
	}
});

test('escalating an alert of a rule that notifies by email sends the escalation role one plain email', async () => {
	const created = await call('POST', rulesApi, {
		token: admin,
		data: {
			siteCode: 'DMO',
			name: `E2E mail Bcc: attacker@example.com ${runId}`,
			zones: ['D-EG'],
			metric: 'QueueLength',
			comparator: 'GreaterThan',
			threshold: 40,
			minQueueLength: null,
			clearThreshold: null,
			sustainMinutes: 2,
			clearAfterMinutes: 2,
			severity: 'Warning',
			ownerRole: 'BorderShiftSupervisor',
			escalateAfterMinutes: 10,
			escalateToRole: 'TerminalDutyManager',
			escalationContact: null,
			notifyByEmail: true,
			enabled: true
		}
	});
	await expectStatus(created, 201);
	({ id: ruleId, code: ruleCode } = await created.json());

	const db = database();
	await db.connect();
	try {
		const planted = await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
			                    escalate_to_role, escalation_contact, raised_utc, raised_value, state)
			 SELECT gen_random_uuid(), site_code, id, code, name, 'D-EG', NULL, metric, severity, owner_role, escalate_after_minutes, escalate_to_role, escalation_contact,
			        date_trunc('minute', now()) - interval '2 minutes', 47, 'Raised'
			 FROM alert_rule WHERE id = $1 RETURNING id`,
			[ruleId]
		);
		alertId = planted.rows[0].id;
	} finally {
		await db.end();
	}

	const escalated = await call('POST', `${alertsApi}/${alertId}/escalate`, { token: border, data: { note: 'Need the duty manager' } });
	await expectStatus(escalated, 200);

	// Integration's sender runs every second in this run.
	await expect
		.poll(async () => (await messagesTo(terminalAddress)).filter((m) => m.subject.includes(ruleCode)).length, { timeout: 20_000, message: 'the duty manager is told' })
		.toBe(1);
	const [message] = (await messagesTo(terminalAddress)).filter((m) => m.subject.includes(ruleCode));
	expect(message.from).toBe('no-reply@ariva.e2e');
	expect(message.to).toEqual([terminalAddress]);
	expect(message.subject).toBe(`[Ariva] Escalated: Warning ${ruleCode} E2E mail Bcc: attacker@example.com ${runId} at D-EG (DMO)`);
	expect((await messagesTo(borderAddress)).filter((m) => m.subject.includes(ruleCode)), 'the owner who escalated is not emailed about it').toHaveLength(0);
	expect((await messagesTo('attacker@example.com')).filter((m) => m.subject.includes(ruleCode)), 'a value never adds a recipient').toHaveLength(0);

	const raw = await (await fetch(`${smtp4dev.url}/api/messages/${message.id}/raw`)).text();
	const head = raw.split(/\r?\n\r?\n/)[0].replace(/\r?\n[ \t]+/g, ' ');
	const headers = head.split(/\r?\n/);
	expect(headers.filter((h) => /^(bcc|cc|reply-to):/i.test(h)), 'no value adds a header (CWE-93)').toEqual([]);
	expect(headers.filter((h) => /^to:/i.test(h))).toEqual([`To: ${terminalAddress}`]);
	expect(headers).toContain('Auto-Submitted: auto-generated');
	const body = raw.slice(raw.search(/\r?\n\r?\n/));
	expect(body).toContain(`Alert ${ruleCode}`);
	expect(body).toContain('Zone:      D-EG');
	expect(body, 'no officer names or notes in the email').not.toContain('Need the duty manager');

	// Sent once: the row says so, and a second escalation cannot happen.
	const check = database();
	await check.connect();
	try {
		// The sender marks the row in the transaction that claimed it, which commits just after the message went out.
		await expect
			.poll(async () => (await check.query(`SELECT kind, recipient, status, attempts FROM email_message WHERE alert_id = $1`, [alertId])).rows, { timeout: 10_000 })
			.toEqual([{ kind: 'AlertEscalated', recipient: terminalAddress, status: 'Sent', attempts: 1 }]);
	} finally {
		await check.end();
	}
	expect((await call('POST', `${alertsApi}/${alertId}/escalate`, { token: border, data: {} })).status()).toBe(409);
	expect((await call('POST', `${alertsApi}/${alertId}/acknowledge`, { token: terminal, data: { note: 'On it' } })).status()).toBe(200);
});

test('an open desks below recommendation alert escalated to an airport-side role writes no email to it (ARV-181b2)', async () => {
	// A border rule as created, then changed in the database to escalate to the duty managers (as a rule from before ARV-181b2
	// could), with its alert planted as the evaluation writes it. The supervisor escalates it: the escalation role is not a
	// border role, so no email is written and the duty manager, who never sees the alert, is never told.
	const created = await call('POST', rulesApi, {
		token: admin,
		data: {
			siteCode: 'DMO',
			name: `E2E mail desks short ${runId}`,
			zones: ['A-RES'],
			metric: 'DesksBelowPlan',
			comparator: 'GreaterOrEqual',
			threshold: 2,
			minQueueLength: null,
			clearThreshold: null,
			sustainMinutes: 3,
			clearAfterMinutes: 2,
			severity: 'Warning',
			ownerRole: 'BorderShiftSupervisor',
			escalateAfterMinutes: null,
			escalateToRole: null,
			escalationContact: null,
			notifyByEmail: true,
			enabled: true
		}
	});
	await expectStatus(created, 201);
	const { id, code } = await created.json();
	desksRuleId = id;

	const db = database();
	await db.connect();
	try {
		await db.query(`UPDATE alert_rule SET escalate_after_minutes = 10, escalate_to_role = 'TerminalDutyManager' WHERE id = $1`, [id]);
		const planted = await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
			                    escalate_to_role, escalation_contact, raised_utc, raised_value, state)
			 SELECT gen_random_uuid(), site_code, id, code, name, 'A-RES', NULL, metric, severity, owner_role, escalate_after_minutes, escalate_to_role, escalation_contact,
			        date_trunc('minute', now()) - interval '2 minutes', 3, 'Raised'
			 FROM alert_rule WHERE id = $1 RETURNING id`,
			[id]
		);
		desksAlertId = planted.rows[0].id;

		expect((await call('POST', `${alertsApi}/${desksAlertId}/escalate`, { token: terminal, data: {} })).status(), 'the duty manager never finds it').toBe(404);
		const escalated = await call('POST', `${alertsApi}/${desksAlertId}/escalate`, { token: border, data: { note: 'Need more desks' } });
		await expectStatus(escalated, 200);
		expect(await escalated.json()).toMatchObject({ state: 'Escalated', escalateToRole: 'TerminalDutyManager' });
		// The emails of a change are written in its transaction: none for this one.
		expect((await db.query(`SELECT count(*)::int AS n FROM email_message WHERE alert_id = $1`, [desksAlertId])).rows[0].n).toBe(0);
	} finally {
		await db.end();
	}
	// Integration's sender runs every second in this run: still nothing for the duty manager after a few rounds.
	await new Promise((resolve) => setTimeout(resolve, 3_000));
	expect((await messagesTo(terminalAddress)).filter((m) => m.subject.includes(code)), 'no email to an airport-side role').toHaveLength(0);
	expect((await call('GET', `${alertsApi}/${desksAlertId}`, { token: terminal })).status()).toBe(404);
});

test('a pending email to the duty manager about a predicted nowcast alert is never sent (ARV-187)', async () => {
	// A predicted nowcast rule as a release before ARV-187 could have left it (owned by the duty managers; made here by a
	// change in the database), its alert as the evaluation wrote it then, and the duty manager's email pending at the upgrade.
	// Integration's sender checks the border rule again before each send: the email is held back, and the duty manager, who
	// never finds the alert or the rule, is never told.
	const created = await call('POST', rulesApi, {
		token: admin,
		data: {
			siteCode: 'DMO',
			name: `E2E mail wave ahead ${runId}`,
			zones: ['A-CIT'],
			metric: 'PredictedNowcast',
			comparator: 'GreaterThan',
			threshold: 20,
			minQueueLength: null,
			clearThreshold: null,
			sustainMinutes: 2,
			clearAfterMinutes: 2,
			severity: 'Warning',
			ownerRole: 'BorderShiftSupervisor',
			escalateAfterMinutes: null,
			escalateToRole: null,
			escalationContact: null,
			notifyByEmail: true,
			enabled: true,
			leadMinutes: 30
		}
	});
	await expectStatus(created, 201);
	const { id, code } = await created.json();
	predictedRuleId = id;

	const db = database();
	await db.connect();
	try {
		await db.query(`UPDATE alert_rule SET owner_role = 'TerminalDutyManager', enabled = false WHERE id = $1`, [id]);
		const planted = await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
			                    escalate_to_role, raised_utc, raised_value, predicted_for_utc, state)
			 SELECT gen_random_uuid(), site_code, id, code, name, 'A-CIT', NULL, metric, severity, owner_role, NULL, NULL,
			        date_trunc('minute', now()) - interval '2 minutes', 48.2, date_trunc('minute', now()) + interval '28 minutes', 'Raised'
			 FROM alert_rule WHERE id = $1 RETURNING id`,
			[id]
		);
		predictedAlertId = planted.rows[0].id;
		await db.query(
			`INSERT INTO email_message (id, kind, alert_id, site_code, recipient, subject, body, status, attempts, created_utc, next_attempt_utc)
			 SELECT gen_random_uuid(), 'AlertRaised', id, site_code, $2, '[Ariva] ' || rule_code || ' planted before the upgrade', 'planted', 'Pending', 0, now(), now()
			 FROM alert WHERE id = $1`,
			[predictedAlertId, terminalAddress]
		);

		expect((await call('GET', `${alertsApi}/${predictedAlertId}`, { token: terminal })).status(), 'the duty manager never finds the alert').toBe(404);
		expect((await call('GET', `${rulesApi}/${id}`, { token: terminal })).status(), 'nor its own rule from before the story').toBe(404);
		// Integration's sender runs every second in this run and marks the row in the transaction that claimed it.
		await expect
			.poll(async () => (await db.query(`SELECT recipient, status FROM email_message WHERE alert_id = $1`, [predictedAlertId])).rows, { timeout: 15_000 })
			.toEqual([{ recipient: terminalAddress, status: 'Suppressed' }]);
	} finally {
		// Back to a border owner, so the demo airport holds no rule an export or the evaluation would refuse.
		await db.query(`UPDATE alert_rule SET owner_role = 'BorderShiftSupervisor' WHERE id = $1`, [id]);
		await db.end();
	}
	expect((await messagesTo(terminalAddress)).filter((m) => m.subject.includes(code)), 'no email to an airport-side role').toHaveLength(0);
});
