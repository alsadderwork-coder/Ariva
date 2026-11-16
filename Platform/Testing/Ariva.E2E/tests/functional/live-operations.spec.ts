import { expect, test, type Locator, type WebSocketRoute } from '@playwright/test';
import pg from 'pg';
import { createClient } from 'redis';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { homeHeading } from '../support/shell';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-055: the live operations screen over the live hub. Ariva.Api.Stream is not part of the E2E run (as in live-hub.spec
// and alerts.spec), so this suite plants what Stream writes for the reference day's 18:05 minute, when the arrival wave
// pushes the demo airport's residents' nowcast above 15 minutes: each queue zone's snapshot in Redis (kept and announced),
// rule R-001's alert in the database and its notice on the alert channel. The screen joins the zones, shows the minute,
// updates live, marks a stale zone, and the border shift supervisor acknowledges the alert.

test.skip(!databaseAvailable, 'the live screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

function snapshot(zone: string, queueLength: number, nowcastMinutes: number | null, minute: Date, publishedAgoSeconds = 0) {
	return {
		zoneKey: `DMO/${zone}`,
		minuteUtc: minute.toISOString(),
		queueLength,
		lengthFromSensors: true,
		lengthDegraded: false,
		nowcastMinutes,
		throughputPerMinute: 4,
		noService: null,
		nowcastDegraded: false,
		publishedUtc: new Date(Date.now() - publishedAgoSeconds * 1000).toISOString()
	};
}

/**
 * People queuing at the demo airport's zones whose snapshot in Redis is fresh (published within 150 seconds) and measured: a
 * zone without measurement (ARV-259e2) is in no total.
 */
async function freshQueueLengths(redis: ReturnType<typeof createClient>): Promise<number> {
	let total = 0;
	for (const key of await redis.keys(`${instance}live:zone:DMO/*`)) {
		const value = await redis.get(key);
		if (!value) continue;
		const snapshot = JSON.parse(value) as { queueLength: number; publishedUtc: string; unknown?: boolean };
		if (Date.now() - Date.parse(snapshot.publishedUtc) <= 150_000 && !snapshot.unknown) total += snapshot.queueLength;
	}
	return total;
}

test('the 18:05 minute: zones by nowcast, a live update, a stale zone, and the R-001 alert acknowledged', async ({ page }) => {
	test.skip(!redisUrl, "the live hub needs the run's Redis (ARIVA_E2E_REDIS_URL)");
	const guards = await guardPage(page);
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	const db = database();
	await db.connect();
	try {
		const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000);
		// What Stream keeps after the 18:05 checkpoint: residents over 15 minutes, citizens quiet, the crew lane last heard
		// from ten minutes ago (stale).
		for (const s of [snapshot('A-RES', 61, 16.3, minute), snapshot('A-CIT', 12, 4.2, minute), snapshot('A-CRW', 3, 1.1, minute, 600)]) {
			await redis.set(`${instance}live:zone:${s.zoneKey}`, JSON.stringify(s), { EX: 3600 });
		}
		await db.query(`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = now(), resolved_by = 'e2e-cleanup', resolution_note = 'cleanup'
		                WHERE state <> 'Resolved' AND site_code = 'DMO' AND rule_code = 'R-001' AND zone_name = 'A-RES'`);

		await signInThroughUi(page, accounts().webBorder);
		await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
		const residents = page.locator('[data-testid="zone-row"][data-zone="A-RES"]');
		await expect(residents).toContainText('16.3');
		await expect(residents).toContainText('Over target');
		await expect(page.locator('[data-testid="zone-row"][data-zone="A-CIT"]')).toContainText('Within target');
		await expect(page.locator('[data-testid="zone-row"][data-zone="A-CRW"]')).toContainText('Stale');
		await page.getByRole('region', { name: 'Floor plan by nowcast' }).getByLabel('Level', { exact: true }).selectOption({ label: 'ARR, Arrivals' });
		await expect(page.locator('[data-testid="plan-zone"][data-zone="A-RES"]')).toHaveAttribute('data-status', 'overTarget');
		// 61 + 12 people at the fresh zones planted here (the crew lane is stale), plus any other fresh DMO zone another
		// suite has planted meanwhile (the immigration suite's e-gate lane): the tile adds up every fresh queue zone.
		await expect
			.poll(async () => {
				const fresh = await freshQueueLengths(redis);
				const shown = Number(((await page.getByTestId('metric-waiting').textContent()) ?? '').match(/\d+/g)?.pop());
				return shown === fresh && fresh >= 73;
			})
			.toBe(true);

		// A minute later Stream announces the next snapshot: the row and the chart follow without a reload.
		await page.locator('[data-testid="zone-row"][data-zone="A-RES"]').getByRole('button').click();
		const next = snapshot('A-RES', 66, 17.8, new Date(minute.getTime() + 60_000));
		await redis.set(`${instance}live:zone:DMO/A-RES`, JSON.stringify(next), { EX: 3600 });
		await expect
			.poll(
				async () => {
					await redis.publish(`${instance}live:zones`, JSON.stringify(next));
					return page.locator('[data-testid="zone-row"][data-zone="A-RES"]').textContent();
				},
				{ timeout: 20_000, intervals: [500, 1000, 2000] }
			)
			.toContain('17.8');
		await expect(page.getByTestId('wait-chart').locator('path')).toHaveCount(1);

		// R-001 fires on the residents' lane, as Stream's evaluation writes it, and its notice reaches the screen.
		const planted = await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
			                    escalate_to_role, escalation_contact, raised_utc, raised_value, state)
			 SELECT gen_random_uuid(), site_code, id, code, name, 'A-RES', null, metric, severity, owner_role, escalate_after_minutes, escalate_to_role, escalation_contact,
			        date_trunc('minute', now()), 16.3, 'Raised'
			 FROM alert_rule WHERE site_code = 'DMO' AND code = 'R-001' AND metric = 'Nowcast' RETURNING id`
		);
		const alertId = planted.rows[0].id as string;
		const notice = {
			alertId,
			siteCode: 'DMO',
			ruleCode: 'R-001',
			ruleName: 'Nowcast above 15 min',
			zoneName: 'A-RES',
			deviceCode: null,
			metric: 'Nowcast',
			severity: 'Critical',
			state: 'Raised',
			raisedUtc: new Date().toISOString(),
			raisedValue: 16.3,
			ownerRole: 'BorderShiftSupervisor',
			escalateToRole: null,
			escalated: false,
			resolvedUtc: null,
			publishedUtc: new Date().toISOString()
		};
		const alert = page.locator(`[data-testid="alert"][data-rule="R-001"]`).filter({ hasText: 'A-RES' });
		await expect
			.poll(
				async () => {
					await redis.publish(`${instance}live:alerts`, JSON.stringify(notice));
					return alert.count();
				},
				{ timeout: 20_000, intervals: [500, 1000, 2000] }
			)
			.toBe(1);
		await expect(alert).toContainText('Nowcast above 15 min');
		await expect(alert).toContainText('Critical');

		await alert.getByLabel('Note (optional)').fill('Opening two more visitor desks');
		await alert.getByRole('button', { name: 'Acknowledge' }).click();
		await expect(page.getByText('Alert acknowledged.')).toBeVisible();
		await expect(alert).toHaveAttribute('data-state', 'Acknowledged');
		const token = (await signIn(accounts().webBorder)).accessToken;
		const stored = await (await call('GET', `${hosts.main}/api/v1/alerts/${alertId}`, { token })).json();
		expect(stored).toMatchObject({ state: 'Acknowledged', acknowledgedNote: 'Opening two more visitor desks', acknowledgedBy: accounts().webBorder.userName });
		await call('POST', `${hosts.main}/api/v1/alerts/${alertId}/resolve`, { token, data: { note: 'e2e done' } });
		allowStatuses(guards, 404);
		await guards.expectClean();
	} finally {
		await db.end();
		await redis.quit();
	}
});

test('alert text from the server is shown as text: rule and zone names carrying markup run nothing (CWE-79)', async ({ page }) => {
	const guards = await guardPage(page);
	const db = database();
	await db.connect();
	const [ruleName, zoneName] = [xssPayloads[1], xssPayloads[5]];
	try {
		// A rule name is free text an administrator writes; a zone name comes from the profile. Both reach the alert list.
		await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
			                    escalate_to_role, escalation_contact, raised_utc, raised_value, state)
			 SELECT gen_random_uuid(), site_code, id, code, $1, $2, null, metric, severity, owner_role, escalate_after_minutes, escalate_to_role, escalation_contact,
			        date_trunc('minute', now()), 16.3, 'Raised'
			 FROM alert_rule WHERE site_code = 'DMO' AND code = 'R-001' AND metric = 'Nowcast'`,
			[ruleName, zoneName]
		);
		await signInThroughUi(page, accounts().webBorder);
		const alert = page.locator('[data-testid="alert"][data-rule="R-001"]').filter({ hasText: zoneName });
		await expect(alert).toHaveCount(1);
		await expect(alert).toContainText(ruleName);
		await expect(alert.locator('img, svg[onload], iframe, script')).toHaveCount(0);
		allowStatuses(guards, 404);
		await guards.expectClean();
	} finally {
		await db.query(
			`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = now(), resolved_by = 'e2e-cleanup', resolution_note = 'cleanup'
			 WHERE state <> 'Resolved' AND site_code = 'DMO' AND rule_code = 'R-001' AND zone_name = $1`,
			[zoneName]
		);
		await db.end();
	}
});

test('a sensor offline alert names its sensor as well as its zone', async ({ page }) => {
	// ARV-064: R-003 judges each commissioned device, so its alert carries the zone and the device; the panel showed the
	// zone only, and the supervisor could not tell which sensor to check.
	const guards = await guardPage(page);
	const db = database();
	await db.connect();
	try {
		await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, owner_role, escalate_after_minutes,
			                    escalate_to_role, escalation_contact, raised_utc, raised_value, state)
			 SELECT gen_random_uuid(), site_code, id, code, name, 'A-VIS', 'S-17', metric, severity, owner_role, escalate_after_minutes, escalate_to_role,
			        escalation_contact, date_trunc('minute', now()), 1, 'Raised'
			 FROM alert_rule WHERE site_code = 'DMO' AND code = 'R-003' AND metric = 'SensorOffline'`
		);
		await signInThroughUi(page, accounts().webBorder);
		const alert = page.locator('[data-testid="alert"][data-rule="R-003"]').filter({ hasText: 'S-17' });
		await expect(alert).toHaveCount(1);
		await expect(alert).toContainText('A-VIS · S-17');
		allowStatuses(guards, 404);
		await guards.expectClean();
	} finally {
		await db.query(
			`UPDATE alert SET state = 'Resolved', resolution = 'Manual', resolved_utc = now(), resolved_by = 'e2e-cleanup', resolution_note = 'cleanup'
			 WHERE state <> 'Resolved' AND site_code = 'DMO' AND rule_code = 'R-003' AND device_code = 'S-17'`
		);
		await db.end();
	}
});

test('desk states and the arrival wave follow the role: border desks for the border supervisor, counters for the duty manager', async ({ page }) => {
	// What Stream's desk engine writes for one immigration desk and one check-in counter (ARV-049).
	const db = database();
	await db.connect();
	try {
		const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 120_000);
		for (const [key, lane, serving, closed] of [
			['DMO/IMM/AR-05', 'RES', 50, 0],
			['DMO/CI/B07', '', 0, 60]
		] as const) {
			await db.query(
				`INSERT INTO desk_minute (desk_code, lane, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, transactions,
				                          sensor_derived_seconds, present_seconds, degraded, updated_on)
				 VALUES ($1, $2, $3, $4, 0, $5, 0, $6, 3, 0, 0, false, now()) ON CONFLICT (desk_code, minute_utc) DO NOTHING`,
				[key, lane, minute, closed, serving, 60 - serving - closed]
			);
		}
	} finally {
		await db.end();
	}

	await signInThroughUi(page, accounts().webBorder);
	await expect(page.locator('[data-testid="desk-state"][data-desk="AR-05"]')).toHaveAttribute('data-state', 'Serving');
	await expect(page.locator('[data-testid="desk-state"][data-desk="B07"]'), 'check-in counters are not border data').toHaveCount(0);
	await expect(page.getByTestId('arrival-strip')).toBeVisible();

	const duty = await page.context().browser()!.newContext();
	const other = await duty.newPage();
	try {
		await signInThroughUi(other, accounts().web);
		await expect(other.locator('[data-testid="desk-state"][data-desk="B07"]')).toHaveAttribute('data-state', 'Closed');
		await expect(other.locator('[data-testid="desk-state"][data-desk="AR-05"]'), 'immigration desks are border data').toHaveCount(0);
		await expect(other.getByTestId('border-desks-hidden')).toBeVisible();
	} finally {
		await duty.close();
	}

	// The server decides, not the screen: the duty manager's own call has no border desks either.
	const token = (await signIn(accounts().web)).accessToken;
	const states = await (await call('GET', `${hosts.main}/api/v1/sites/DMO/desk-states`, { token })).json();
	expect(states).toMatchObject({ airportIncluded: true, borderIncluded: false });
	expect((states.desks as { checkpointKind: string }[]).every((d) => d.checkpointKind === 'CheckIn' || d.checkpointKind === 'Security')).toBe(true);
	const border = await (await call('GET', `${hosts.main}/api/v1/sites/DMO/desk-states`, { token: (await signIn(accounts().webBorder)).accessToken })).json();
	expect(border).toMatchObject({ airportIncluded: false, borderIncluded: true });
	expect((border.desks as { checkpointKind: string }[]).every((d) => d.checkpointKind === 'Immigration' || d.checkpointKind === 'Emigration')).toBe(true);
});

test('a handler station manager has no arrival wave and no other desks; Arabic mirrors the screen', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webHandler);
	await expect(page.getByTestId('zones-table')).toBeVisible();
	await expect(page.getByTestId('arrival-strip')).toHaveCount(0);
	// A handler sees only its own counters (wiki 01), which needs handler tenancy: until then no desk states at all.
	await expect(page.getByTestId('desk-states')).toHaveCount(0);
	const states = await (await call('GET', `${hosts.main}/api/v1/sites/DMO/desk-states`, { token: (await signIn(accounts().webHandler)).accessToken })).json();
	expect(states).toMatchObject({ airportIncluded: false, borderIncluded: false, desks: [] });
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.ar);
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('a nowcast from the exit rate alone is marked as an estimate; only doubtful data is shown as degraded; a zone without measurement as Not measured (ARV-064, ARV-259e2)', async ({ page }) => {
	test.skip(!redisUrl, "the live hub needs the run's Redis (ARIVA_E2E_REDIS_URL)");
	const guards = await guardPage(page);
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	const keys = [`${instance}live:zone:DMO/D-RES`, `${instance}live:zone:DMO/D-CRW`];
	try {
		const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000);
		// D-RES: the queue is measured, the throughput is the exit rate only (no desk state): an estimate, still coloured.
		// D-CRW: the queue length comes from degraded readings (a sensor outage): the data itself is in doubt.
		const estimate = { ...snapshot('D-RES', 30, 12.5, minute), nowcastDegraded: true };
		const doubtful = { ...snapshot('D-CRW', 4, 2.0, minute), lengthDegraded: true, nowcastDegraded: true };
		for (const s of [estimate, doubtful]) await redis.set(`${instance}live:zone:${s.zoneKey}`, JSON.stringify(s), { EX: 3600 });

		await signInThroughUi(page, accounts().webBorder);
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
		const residents = page.locator('[data-testid="zone-row"][data-zone="D-RES"]');
		await expect(residents).toContainText('Near target');
		await expect(residents.getByTestId('estimate-marker')).toHaveAttribute('title', /exit rate only/);
		const crew = page.locator('[data-testid="zone-row"][data-zone="D-CRW"]');
		await expect(crew).toContainText('Data degraded');
		await expect(crew.getByTestId('estimate-marker')).toHaveCount(0);

		// Then the desks close: no nowcast, and the status says there is no estimate rather than degraded data.
		const closed = { ...snapshot('D-RES', 30, null, new Date(minute.getTime() + 60_000)), noService: 'NothingOpen' };
		await redis.set(`${instance}live:zone:DMO/D-RES`, JSON.stringify(closed), { EX: 3600 });
		await expect
			.poll(
				async () => {
					await redis.publish(`${instance}live:zones`, JSON.stringify(closed));
					return residents.textContent();
				},
				{ timeout: 20_000, intervals: [500, 1000, 2000] }
			)
			.toContain('No estimate');
		await expect(residents.getByTestId('estimate-marker')).toHaveCount(0);

		// ARV-259e2: then every sensor of D-CRW is out (formulas F11), as Stream announces it: nothing measured, both flags set.
		// The queue length is the last one the engine held; the screen never shows it, nor counts it in the waiting tile.
		const unknown = {
			...snapshot('D-CRW', 27, null, new Date(minute.getTime() + 60_000)),
			throughputPerMinute: null,
			lengthDegraded: true,
			nowcastDegraded: true,
			unknown: true
		};
		await redis.set(`${instance}live:zone:DMO/D-CRW`, JSON.stringify(unknown), { EX: 3600 });
		await expect
			.poll(
				async () => {
					await redis.publish(`${instance}live:zones`, JSON.stringify(unknown));
					return crew.getByTestId('not-measured').count();
				},
				{ timeout: 20_000, intervals: [500, 1000, 2000] }
			)
			.toBe(1);
		await expect(crew.getByTestId('not-measured')).toHaveText(/^Not measured/);
		await expect(crew.getByTestId('not-measured')).toHaveAttribute('title', /no measurement/);
		await expect(crew.locator('td').nth(0), 'no queue length').toHaveText('');
		await expect(crew.locator('td').nth(1), 'no wait').not.toContainText('27');
		await expect(crew).not.toContainText('Data degraded');
		await expect(crew).not.toContainText('Within target');
		await page.getByRole('region', { name: 'Floor plan by nowcast' }).getByLabel('Level', { exact: true }).selectOption({ label: 'DEP, Departures' });
		const plan = page.locator('[data-testid="plan-zone"][data-zone="D-CRW"]');
		await expect(plan).toHaveAttribute('data-status', 'notMeasured');
		await expect(plan.getByTestId('plan-not-measured')).toHaveText('Not measured');
		// The waiting tile adds up the measured fresh zones only: were D-CRW's 27 in it, it would not match.
		await expect
			.poll(async () => {
				const fresh = await freshQueueLengths(redis);
				const shown = Number(((await page.getByTestId('metric-waiting').textContent()) ?? '').match(/\d+/g)?.pop());
				return shown === fresh;
			})
			.toBe(true);
		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(crew.getByTestId('not-measured')).toHaveText(/^غير مقاس/);
		await expect(plan.getByTestId('plan-not-measured')).toHaveText('غير مقاس');
		await guards.expectClean();
	} finally {
		await redis.del(keys);
		await redis.quit();
	}
});

test('the screen refuses a zone frame whose unknown is not a boolean, or Unknown with a number or without its flags (ARV-259e2, CWE-501)', async ({ page }) => {
	// The server checks every snapshot too (LiveZones.Plausible), so these frames are written into the hub's WebSocket here, as a
	// tampering proxy would: only the screen's own check (asZoneSnapshot) stands between them and the rows. The server's frames
	// for the two zones used are held back, so the rows show only what this test sends, whatever other suites plant in Redis.
	const guards = await guardPage(page);
	const record = '\u001e';
	const ours = new Set(['DMO/D-VIS', 'DMO/D-CIT']);
	let toPage: WebSocketRoute | undefined;
	await page.routeWebSocket(/\/hubs\/live/, (ws) => {
		const server = ws.connectToServer();
		ws.onMessage((message) => server.send(message));
		server.onMessage((message) => {
			const kept = String(message)
				.split(record)
				.filter((r) => r.length > 0)
				.flatMap((r) => {
					const m = JSON.parse(r) as { type?: number; target?: string; arguments?: Array<{ zoneKey?: string }>; result?: { zoneKey?: string } | null };
					if (m.type === 1 && m.target === 'zone' && ours.has(m.arguments?.[0]?.zoneKey ?? '')) return [];
					if (m.type === 3 && ours.has(m.result?.zoneKey ?? '')) return [JSON.stringify({ ...m, result: null })];
					return [r];
				});
			if (kept.length > 0) ws.send(kept.map((r) => r + record).join(''));
		});
		toPage = ws;
	});
	await signInThroughUi(page, accounts().webBorder);
	await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
	await expect.poll(() => toPage !== undefined).toBe(true);
	const send = (snapshot: object) => toPage!.send(JSON.stringify({ type: 1, target: 'zone', arguments: [snapshot] }) + record);
	const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000);
	const nothing = { nowcastMinutes: null, throughputPerMinute: null, noService: null };
	const flagged = { lengthDegraded: true, nowcastDegraded: true };
	const visitors = page.locator('[data-testid="zone-row"][data-zone="D-VIS"]');
	const citizens = page.locator('[data-testid="zone-row"][data-zone="D-CIT"]');

	// A frame without the member (an Api.Main before ARV-259e2) reads as measured.
	send(snapshot('D-VIS', 20, 4.4, minute));
	await expect(visitors.locator('td').nth(1)).toHaveText('4.4');
	for (const forged of [
		{ ...snapshot('D-VIS', 20, 3.1, minute), unknown: 0 },
		{ ...snapshot('D-VIS', 20, 3.2, minute), unknown: 'false' },
		{ ...snapshot('D-VIS', 20, null, minute), ...nothing, ...flagged, unknown: 'true' },
		{ ...snapshot('D-VIS', 20, 3.3, minute), ...flagged, throughputPerMinute: null, unknown: true },
		{ ...snapshot('D-VIS', 20, null, minute), ...flagged, unknown: true },
		{ ...snapshot('D-VIS', 20, null, minute), ...nothing, noService: 'NothingOpen', ...flagged, unknown: true },
		{ ...snapshot('D-VIS', 20, null, minute), ...nothing, lengthDegraded: true, unknown: true },
		{ ...snapshot('D-VIS', 20, null, minute), ...nothing, lengthDegraded: false, nowcastDegraded: true, unknown: true }
	])
		send(forged);
	// The sentinel, on the same socket after them: once it shows, every frame before it has been judged.
	send({ ...snapshot('D-CIT', 20, 7.7, minute), unknown: false });
	await expect(citizens.locator('td').nth(1)).toHaveText('7.7');
	await expect(visitors.locator('td').nth(1), 'every forged frame refused').toHaveText('4.4');
	await expect(visitors.getByTestId('not-measured')).toHaveCount(0);

	// A well-formed Unknown frame is taken.
	send({ ...snapshot('D-VIS', 20, null, minute), ...nothing, ...flagged, unknown: true });
	await expect(visitors.getByTestId('not-measured')).toBeVisible();
	await expect(visitors.locator('td').nth(0), 'no queue length').toHaveText('');
	await guards.expectClean();
});

/** The floor in view, in metres: the plan's viewBox. */
async function viewBox(plan: Locator): Promise<number[]> {
	return ((await plan.getAttribute('viewBox')) ?? '').split(' ').map(Number);
}

/** The page's coordinates to floor coordinates (metres). */
async function floorOf(plan: Locator, at: { x: number; y: number }): Promise<{ x: number; y: number }> {
	return plan.evaluate((svg, p) => {
		const q = new DOMPoint(p.x, p.y).matrixTransform((svg as SVGSVGElement).getScreenCTM()!.inverse());
		return { x: q.x, y: q.y };
	}, at);
}

/** The middle of the plan on the page, once it is in view clear of the sticky header. */
async function middleOf(plan: Locator): Promise<{ x: number; y: number }> {
	await plan.evaluate((element) => element.scrollIntoView({ block: 'center' }));
	const box = (await plan.boundingBox())!;
	return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

test('the floor plan zooms, pans, fits and zooms to the selected zone; a plain wheel scrolls the page (ARV-299a)', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webBorder);
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
	const region = page.getByRole('region', { name: 'Floor plan by nowcast' });
	await region.getByLabel('Level', { exact: true }).selectOption({ label: 'ARR, Arrivals' });
	const plan = page.getByTestId('nowcast-plan');
	await expect(plan.locator('[data-testid="plan-zone"][data-zone="A-CIT"]')).toBeAttached();
	const tools = region.getByRole('toolbar', { name: 'Floor plan view' });
	const level = tools.getByTestId('zoom-level');
	await expect(level).toHaveText('100%');
	const fitted = await viewBox(plan);
	expect(await plan.evaluate((svg) => getComputedStyle(svg).touchAction), 'a touch on the fitted plan scrolls the page').toBe('pan-y');

	// The buttons and the keys.
	await tools.getByRole('button', { name: 'Zoom in' }).click();
	await expect(level).toHaveText('125%');
	expect((await viewBox(plan))[2]).toBeCloseTo(fitted[2] / 1.25, 3);
	expect(await plan.evaluate((svg) => getComputedStyle(svg).touchAction), 'zoomed in, one finger pans the plan').toBe('none');
	await tools.getByRole('button', { name: 'Zoom out' }).click();
	await expect(level).toHaveText('100%');
	await tools.getByRole('button', { name: 'Fit the level' }).focus();
	await page.keyboard.press('+');
	await expect(level).toHaveText('125%');
	await page.keyboard.press('0');
	await expect(level).toHaveText('100%');
	expect(await viewBox(plan)).toEqual(fitted);

	// A plain wheel is left to the page (not prevented) and says how to zoom; Ctrl and the wheel zoom around the pointer.
	await page.evaluate(() => {
		const w = window as unknown as { wheels: boolean[] };
		w.wheels = [];
		window.addEventListener('wheel', (event) => w.wheels.push(event.defaultPrevented), { passive: true });
	});
	let middle = await middleOf(plan);
	await page.mouse.move(middle.x, middle.y);
	await page.mouse.wheel(0, 120);
	await expect(page.getByTestId('plan-modifier-hint')).toBeVisible();
	await expect.poll(() => page.evaluate(() => (window as unknown as { wheels: boolean[] }).wheels)).toEqual([false]);
	expect(Number(await plan.getAttribute('data-zoom')), 'a plain wheel does not zoom').toBe(1);
	await page.waitForTimeout(500);
	middle = await middleOf(plan);
	const at = { x: middle.x - 80, y: middle.y - 30 };
	await page.mouse.move(at.x, at.y);
	const under = await floorOf(plan, at);
	await page.keyboard.down('Control');
	await page.mouse.wheel(0, -400);
	await page.keyboard.up('Control');
	await expect.poll(async () => Number(await plan.getAttribute('data-zoom'))).toBeGreaterThan(1.5);
	const still = await floorOf(plan, at);
	expect(still.x, 'the point under the pointer stays').toBeCloseTo(under.x, 1);
	expect(still.y).toBeCloseTo(under.y, 1);

	// A drag pans the view and never selects the zone it started on.
	const current = page.locator('[data-testid="zone-row"] [aria-current="true"]');
	const selectedBefore = await current.textContent();
	const before = await viewBox(plan);
	await page.mouse.move(at.x, at.y);
	await page.mouse.down();
	await page.mouse.move(at.x + 120, at.y + 60, { steps: 6 });
	await page.mouse.up();
	const after = await viewBox(plan);
	expect(after[0], 'the view moved left as the floor moved right').toBeLessThan(before[0]);
	expect(after[2], 'panning keeps the zoom').toBeCloseTo(before[2], 3);
	expect(await current.textContent(), 'a drag selects nothing').toBe(selectedBefore);

	// Fit, then a click on a zone selects it; the tool zooms to it, the zone in the middle of the view.
	await tools.getByRole('button', { name: 'Fit the level' }).click();
	expect(await viewBox(plan)).toEqual(fitted);
	await plan.locator('[data-testid="plan-zone"][data-zone="A-CIT"] polygon').click();
	await expect(page.locator('[data-testid="zone-row"][data-zone="A-CIT"] [aria-current="true"]')).toHaveCount(1);
	await expect(plan.locator('[data-testid="plan-zone"][data-zone="A-CIT"] polygon')).toHaveAttribute('aria-pressed', 'true');
	await tools.getByRole('button', { name: 'Zoom to the selection' }).click();
	await expect.poll(async () => Number(await plan.getAttribute('data-zoom'))).toBeGreaterThan(1);
	const [x, y, w, h] = await viewBox(plan);
	const zone = await plan.locator('[data-testid="plan-zone"][data-zone="A-CIT"] polygon').evaluate((polygon) => {
		const b = (polygon as SVGPolygonElement).getBBox();
		return { x: b.x + b.width / 2, y: b.y + b.height / 2, width: b.width, height: b.height };
	});
	expect(zone.x, 'the zone is in the middle of the view').toBeCloseTo(x + w / 2, 0);
	expect(zone.y).toBeCloseTo(y + h / 2, 0);
	expect(zone.width, 'and fills much of it').toBeGreaterThan(w * 0.3);

	// Two fingers: a pinch apart zooms in.
	await tools.getByRole('button', { name: 'Fit the level' }).click();
	middle = await middleOf(plan);
	await plan.evaluate((svg, p) => {
		const send = (type: string, id: number, x: number, y: number) =>
			svg.dispatchEvent(new PointerEvent(type, { pointerId: id, pointerType: 'touch', clientX: x, clientY: y, bubbles: true, isPrimary: id === 1 }));
		send('pointerdown', 1, p.x - 30, p.y);
		send('pointerdown', 2, p.x + 30, p.y);
		for (let i = 1; i <= 5; i++) send('pointermove', 2, p.x + 30 + i * 20, p.y);
		send('pointerup', 2, p.x + 130, p.y);
		send('pointerup', 1, p.x - 30, p.y);
	}, middle);
	expect(Number(await plan.getAttribute('data-zoom')), 'the pinch zoomed in').toBeGreaterThan(1.5);
	await expect(page.locator('[data-testid="zone-row"][data-zone="A-CIT"] [aria-current="true"]'), 'a pinch selects nothing new').toHaveCount(1);

	// Arabic: the tools in the dictionary's words (the region's English name is gone, so by test id).
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	const arabic = page.getByTestId('nowcast-tools');
	await expect(arabic).toHaveAttribute('aria-label', 'عرض مخطط الطابق');
	await expect(arabic.getByRole('button', { name: 'تكبير', exact: true })).toBeVisible();
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('zone names carrying markup are text on the floor plan and still select their zone: nothing runs (ARV-299a, CWE-79)', async ({ page }) => {
	const guards = await guardPage(page);
	const hostile = [xssPayloads[1], xssPayloads[2]];
	// A zone name is free text an administrator writes. On the way to this screen the published profile's two arrivals
	// queue zones are renamed: on the plan each name is a label, an accessible name, a title and part of the zone's key,
	// which a click reads back from the page to select the zone.
	await page.route(/\/api\/v1\/admin\/zone-profiles\/[0-9a-f-]{36}$/, async (route) => {
		if (route.request().method() !== 'GET') return route.continue();
		const response = await route.fetch();
		const profile = (await response.json()) as { zones: { name: string }[] };
		const rename = (name: string) => (name === 'A-RES' ? hostile[0] : name === 'A-CIT' ? hostile[1] : name);
		await route.fulfill({ response, json: { ...profile, zones: profile.zones.map((z) => ({ ...z, name: rename(z.name) })) } });
	});
	await signInThroughUi(page, accounts().webBorder);
	await expect(page.getByRole('heading', { level: 1 })).toHaveText(homeHeading.en);
	const region = page.getByRole('region', { name: 'Floor plan by nowcast' });
	await region.getByLabel('Level', { exact: true }).selectOption({ label: 'ARR, Arrivals' });
	const view = page.getByTestId('nowcast-view');
	const zones = view.locator('[data-testid="plan-zone"]');
	await expect.poll(() => zones.evaluateAll((groups) => groups.map((g) => g.getAttribute('data-zone')))).toEqual(expect.arrayContaining(hostile));
	const shown = await zones.evaluateAll((groups) =>
		groups.map((g) => {
			const polygon = g.querySelector('polygon');
			return {
				zone: g.getAttribute('data-zone'),
				label: polygon?.getAttribute('aria-label'),
				key: polygon?.getAttribute('data-zone-key'),
				title: polygon?.querySelector('title')?.textContent,
				text: g.querySelector('text')?.textContent
			};
		})
	);
	for (const name of hostile) {
		const zone = shown.find((s) => s.zone === name);
		expect(zone, `${name} is on the plan`).toBeDefined();
		expect(zone?.label, 'the accessible name is the text').toBe(name);
		expect(zone?.key, 'the key is the site and the text').toBe(`DMO/${name}`);
		expect(zone?.text, 'the label is the text').toBe(name);
		expect(zone?.title, 'the title is the text').toContain(name);
	}
	await expect(view.locator('img, iframe, script, [onerror], [onload]'), 'no markup of a name became an element').toHaveCount(0);

	// A click on a hostile zone selects it: the key read back from the page is exactly the zone's key.
	await view.getByRole('button', { name: hostile[0], exact: true }).click();
	const row = page.getByTestId('zone-row').filter({ has: page.getByRole('button', { name: hostile[0], exact: true }) });
	await expect(row.getByRole('button', { name: hostile[0], exact: true })).toHaveAttribute('aria-current', 'true');
	await expect(view.getByRole('button', { name: hostile[0], exact: true })).toHaveAttribute('aria-pressed', 'true');
	await region.getByRole('toolbar', { name: 'Floor plan view' }).getByRole('button', { name: 'Zoom to the selection' }).click();
	await expect.poll(async () => Number(await page.getByTestId('nowcast-plan').getAttribute('data-zoom'))).toBeGreaterThan(1);
	await expect(page.locator('img[src="x"], svg[onload], iframe')).toHaveCount(0);
	expect(guards.dialogs, 'no script of a name ran').toEqual([]);
	allowStatuses(guards, 404);
	await guards.expectClean();
});
