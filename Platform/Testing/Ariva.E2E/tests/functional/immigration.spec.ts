import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import pg from 'pg';
import { createClient } from 'redis';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-057: the immigration screen on AMAN's interval aggregates as the immigration intake stores them (ARV-048). This
// suite plants what the intake writes for the demo airport's arrival hall (desk sessions, desk intervals, e-gate
// intervals; AMAN's codes IN02, IN08 and EGIN1 mapped to AR-02, AR-08 and AG-1) and a live snapshot of the e-gate lane's
// queue (Stream is not part of the E2E run). A border shift supervisor sees lanes, desks and gates; a terminal duty
// manager sees the same lane and e-gate totals and no desk or gate code anywhere; a handler station manager has no
// immigration screen and the server refuses it.

test.skip(!databaseAvailable, 'the immigration screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
const run = crypto.randomBytes(3).toString('hex');
const api = (site = 'DMO') => `${hosts.main}/api/v1/sites/${site}/immigration`;
// Codes that must never reach a terminal duty manager: Ariva's desk and gate codes and AMAN's.
const deskMarkers = ['AR-0', 'AR-1', 'AR-2', 'AG-', 'DP-', 'DG-', 'IN0', 'EGIN', 'OUT0', 'EGOUT'];

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

/** The mapped desk id of an Ariva desk code at the arrival hall. */
const deskId = `(SELECT d.id FROM desk d JOIN checkpoint c ON c.id = d.checkpoint_id
                 WHERE d.site_code = 'DMO' AND c.code = 'IMM' AND d.code = $1 AND d.deleted_on IS NULL)`;

test.beforeAll(async () => {
	const db = database();
	await db.connect();
	try {
		const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 3 * 60_000);
		const occurred = new Date(Date.now() - 60_000);
		for (const [desk, aman, lane] of [
			['AR-02', 'IN02', 'CIT'],
			['AR-08', 'IN08', 'VIS']
		] as const) {
			await db.query(
				`INSERT INTO border_desk_session (id, site_code, desk_code, desk_id, state, lane_category, occurred_utc, feed, source_event_id, received_utc)
				 VALUES (gen_random_uuid(), 'DMO', $2, ${deskId}, 'Opened', $3, $4, 'aman-e2e', $5, now())`,
				[desk, aman, lane, occurred, `e2e-imm-${run}-s-${aman}`]
			);
			await db.query(
				`INSERT INTO border_desk_interval (id, site_code, desk_code, desk_id, interval_start_utc, transactions_processed, documents_processed,
				                                   mean_service_seconds, p90_service_seconds, mean_cycle_seconds, lane_category, feed, source_event_id, received_utc)
				 VALUES (gen_random_uuid(), 'DMO', $2, ${deskId}, $3, 4, 4, 48, 75, 60, $4, 'aman-e2e', $5, now())`,
				[desk, aman, minute, lane, `e2e-imm-${run}-i-${aman}`]
			);
		}
		await db.query(
			`INSERT INTO border_egate_interval (id, site_code, gate_code, desk_id, interval_start_utc, attempts, accepted, rejected, rejects_other,
			                                    rejects_document_read, rejects_biometric_capture, rejects_eligibility, rejects_referred_to_officer,
			                                    rejects_technical, mean_cycle_seconds, feed, source_event_id, received_utc)
			 VALUES (gen_random_uuid(), 'DMO', 'EGIN1', ${deskId}, $2, 20, 14, 6, 0, 3, 0, 0, 0, 3, 18, 'aman-e2e', $3, now())`,
			['AG-1', minute, `e2e-imm-${run}-g-EGIN1`]
		);
	} finally {
		await db.end();
	}
});

test.afterAll(async () => {
	const db = database();
	await db.connect();
	try {
		for (const table of ['border_desk_session', 'border_desk_interval', 'border_egate_interval'])
			await db.query(`DELETE FROM ${table} WHERE site_code = 'DMO' AND source_event_id LIKE $1`, [`e2e-imm-${run}-%`]);
	} finally {
		await db.end();
	}
});

test('a border shift supervisor sees each lane, the desks and the e-gates of the arrival hall', async ({ page }) => {
	test.skip(!redisUrl, 'the lane waits come from the live hub (ARIVA_E2E_REDIS_URL)');
	const guards = await guardPage(page);
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await signInThroughUi(page, accounts().webBorder);
		await page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
		await expect(page.getByRole('heading', { level: 1 })).toHaveText('Immigration');
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');

		// The e-gate lane's queue as Stream would announce it.
		const snapshot = {
			zoneKey: 'DMO/A-EG',
			minuteUtc: new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000).toISOString(),
			queueLength: 9,
			lengthFromSensors: true,
			lengthDegraded: false,
			nowcastMinutes: 6.4,
			throughputPerMinute: 2,
			noService: null,
			nowcastDegraded: false,
			publishedUtc: new Date().toISOString()
		};
		await redis.set(`${instance}live:zone:DMO/A-EG`, JSON.stringify(snapshot), { EX: 3600 });
		const egLane = page.locator('[data-testid="lane-row"][data-lane="EG"]');
		await expect
			.poll(
				async () => {
					await redis.publish(`${instance}live:zones`, JSON.stringify(snapshot));
					return egLane.getByTestId('lane-wait').textContent();
				},
				{ timeout: 20_000, intervals: [500, 1000, 2000] }
			)
			.toContain('6.4 min');
		await expect(egLane).toContainText('E-gate eligible');
		for (const lane of ['CIT', 'RES', 'VIS', 'CRW']) await expect(page.locator(`[data-testid="lane-row"][data-lane="${lane}"]`)).toBeVisible();

		// The server's figures, as the screen shows them.
		const token = (await signIn(accounts().webBorder)).accessToken;
		const view = await (await call('GET', api(), { token })).json();
		const arrivals = view.halls.find((h: { kind: string }) => h.kind === 'Immigration');
		expect(view.desksIncluded).toBe(true);
		expect(arrivals.desks.find((d: { desk: string }) => d.desk === 'AR-02')).toMatchObject({ lane: 'CIT' });
		expect(arrivals.desks.find((d: { desk: string }) => d.desk === 'AR-02').transactions).toBeGreaterThanOrEqual(4);
		expect(arrivals.eGates.attempts).toBeGreaterThanOrEqual(20);
		await expect(page.getByTestId('egate-attempts')).toHaveText(String(arrivals.eGates.attempts));
		await expect(page.locator('[data-testid="rejects"] [data-reject="documentRead"]')).toContainText(String(arrivals.eGates.rejects.documentRead));
		await expect(page.getByTestId('extra-load')).toContainText('desk-minutes');
		await expect(page.locator('[data-testid="desk"][data-desk="AR-02"]')).toBeVisible();
		await expect(page.locator('[data-testid="gate"][data-gate="AG-1"]')).toBeVisible();
		await expect(page.getByTestId('aman-link')).toHaveAttribute('aria-disabled', 'true');
		expect(await page.locator('main').innerText(), 'Ariva shows its own desk codes, not AMAN’s').not.toContain('IN02');

		// The departures hall has its own lanes.
		await page.getByTestId('hall-Emigration').click();
		await expect(page.getByTestId('hall-Emigration')).toHaveAttribute('aria-selected', 'true');
		await expect(page.locator('[data-testid="lane-row"][data-lane="VIS"]')).toBeVisible();
		await expect(page.locator('[data-testid="desk"][data-desk="AR-02"]')).toHaveCount(0);
		await guards.expectClean();
	} finally {
		// The live screen adds up every fresh zone of the site: leave no planted queue behind.
		await redis.del(`${instance}live:zone:DMO/A-EG`);
		await redis.quit();
	}
});

test('a terminal duty manager sees lane and e-gate totals only: no desk or gate code anywhere', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().web);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Immigration');
	await expect(page.locator('[data-testid="lane-row"][data-lane="CIT"]')).toBeVisible();
	await expect(page.getByTestId('desks-hidden')).toBeVisible();
	await expect(page.getByTestId('desk')).toHaveCount(0);
	await expect(page.getByTestId('gate')).toHaveCount(0);
	const token = (await signIn(accounts().web)).accessToken;
	const answer = await (await call('GET', api(), { token })).text();
	const view = JSON.parse(answer);
	expect(view.desksIncluded).toBe(false);
	const arrivals = view.halls.find((h: { kind: string }) => h.kind === 'Immigration');
	expect(arrivals.desks).toEqual([]);
	expect(arrivals.gates).toEqual([]);
	expect(arrivals.eGates.attempts).toBeGreaterThanOrEqual(20);
	await expect(page.getByTestId('egate-attempts')).toHaveText(String(arrivals.eGates.attempts));
	await page.getByTestId('hall-Emigration').click();
	// What the page shows and holds in its attributes (not the build's script names).
	const html = `${await page.locator('main').innerText()}\n${await page.locator('main').innerHTML()}`;
	for (const marker of deskMarkers) {
		expect(answer, `${marker} in the answer`).not.toContain(marker);
		expect(html, `${marker} on the page`).not.toContain(marker);
	}
	await guards.expectClean();
});

test('a handler station manager has no immigration screen and the server refuses it; Arabic mirrors the screen', async ({ page }) => {
	const handler = (await signIn(accounts().webHandler)).accessToken;
	expect((await call('GET', api(), { token: handler })).status()).toBe(403);
	expect((await call('GET', api('NOPE'), { token: (await signIn(accounts().webBorder)).accessToken })).status(), 'a site that does not exist').toBe(404);

	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webHandler);
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' })).toHaveCount(0);
	await guards.expectClean();

	const border = await page.context().browser()!.newContext();
	const other = await border.newPage();
	try {
		const otherGuards = await guardPage(other);
		await signInThroughUi(other, accounts().webBorder);
		await other.getByTestId('app-sidebar').getByRole('link', { name: 'Immigration' }).click();
		await other.getByTestId('language-toggle').click();
		await expect(other.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(other.getByRole('heading', { level: 1 })).toHaveText('الهجرة');
		await expect(other.getByTestId('hall-Immigration')).toHaveText('القدوم');
		allowStatuses(otherGuards, 404);
		await otherGuards.expectClean();
	} finally {
		await border.close();
	}
});
