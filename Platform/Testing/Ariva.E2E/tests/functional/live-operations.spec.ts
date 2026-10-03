import { expect, test } from '@playwright/test';
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

/** People queuing at the demo airport's zones whose snapshot in Redis is fresh (published within 150 seconds). */
async function freshQueueLengths(redis: ReturnType<typeof createClient>): Promise<number> {
	let total = 0;
	for (const key of await redis.keys(`${instance}live:zone:DMO/*`)) {
		const value = await redis.get(key);
		if (!value) continue;
		const snapshot = JSON.parse(value) as { queueLength: number; publishedUtc: string };
		if (Date.now() - Date.parse(snapshot.publishedUtc) <= 150_000) total += snapshot.queueLength;
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
		await page.getByRole('region', { name: 'Floor plan by nowcast' }).getByLabel('Level').selectOption({ label: 'ARR, Arrivals' });
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
