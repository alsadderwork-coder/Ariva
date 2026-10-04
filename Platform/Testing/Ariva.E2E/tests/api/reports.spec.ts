import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts, smtp4dev } from '../support/hosts';

// ARV-060: the daily report and its scheduled delivery. The suite plants yesterday's minutes of the demo airport's crew
// lane (A-CRW) with their histograms and one alert whose rule name is a spreadsheet formula, as the Stream host and the
// alert evaluation write them (neither runs in the E2E run). The report answers per role and site, its CSV neutralises
// the formula, and a schedule sends each recipient its report with the CSV files through Ariva.Api.Cronz, whose TickerQ
// dashboard runs the job on demand with the run's key and refuses everything without it.

test.skip(!databaseAvailable, 'reports need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const reportsApi = `${hosts.main}/api/v1/sites/DMO/reports`;
const schedulesApi = `${hosts.main}/api/v1/report-schedules`;
const dashboard = `${hosts.cronz}/tickerq`;
const runId = Date.now().toString(36);
const borderAddress = `reportborder.${runId}@ariva.e2e`;
const terminalAddress = `reportterminal.${runId}@ariva.e2e`;
const formula = '=HYPERLINK("http://example.invalid","open")';

// DMO is in Asia/Dubai (UTC+4, no daylight saving): yesterday's local date and its 03:00 local in UTC.
const dubaiNow = new Date(Date.now() + 4 * 3_600_000);
const yesterday = new Date(Date.UTC(dubaiNow.getUTCFullYear(), dubaiNow.getUTCMonth(), dubaiNow.getUTCDate() - 1));
const date = yesterday.toISOString().slice(0, 10);
const threeLocal = new Date(yesterday.getTime() + 3 * 3_600_000 - 4 * 3_600_000);

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

type Smtp4devMessage = { id: string; to: string[]; subject: string };

async function messagesTo(address: string): Promise<Smtp4devMessage[]> {
	const response = await fetch(`${smtp4dev.url}/api/messages?pageSize=1000&sortColumn=receivedDate&sortIsDescending=true`);
	expect(response.status).toBe(200);
	return ((await response.json()).results as Smtp4devMessage[]).filter((m) => m.to.some((to) => to.toLowerCase() === address));
}

const key = () => ({ Authorization: `Bearer ${process.env.ARIVA_E2E_CRONZ_KEY}` });
let border: string, terminal: string, handler: string, elsewhere: string, admin: string;
let scheduleId: string;

test.beforeAll(async () => {
	[border, terminal, handler, elsewhere, admin] = await Promise.all(
		[accounts().reportBorder, accounts().reportTerminal, accounts().dmoHandler, accounts().BorderShiftSupervisor, accounts().webAdmin].map(
			async (a) => (await signIn(a)).accessToken
		)
	);
	const db = database();
	await db.connect();
	try {
		await db.query('DELETE FROM queue_minute WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc < $3', [
			'DMO/A-CRW',
			threeLocal,
			new Date(threeLocal.getTime() + 3_600_000)
		]);
		// 25 waits of 4 minutes and 5 of 31: the hour's P90 (rank 27 of 30) is in the long bucket.
		await db.query(
			`INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
			 VALUES ('DMO/A-CRW', $1, 1, 'Final', 25, 25, 25, 41, ARRAY[8], ARRAY[25], now()),
			        ('DMO/A-CRW', $2, 1, 'Final', 5, 5, 5, 44, ARRAY[62], ARRAY[5], now())`,
			[threeLocal, new Date(threeLocal.getTime() + 60_000)]
		);
		await db.query(
			`INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, metric, severity, owner_role, raised_utc, raised_value, state,
			                    resolved_utc, resolution, resolved_by)
			 SELECT gen_random_uuid(), site_code, id, code, $1, 'A-CRW', metric, severity, NULL, $2, 1, 'Resolved', $3, 'Manual', 'e2e'
			 FROM alert_rule WHERE code = 'R-002' AND site_code = 'DMO'`,
			[formula, new Date(threeLocal.getTime() + 5 * 60_000), new Date(threeLocal.getTime() + 15 * 60_000)]
		);
		for (const [account, address] of [
			[accounts().reportBorder, borderAddress],
			[accounts().reportTerminal, terminalAddress]
		] as const)
			await db.query('UPDATE "user" SET email = $1 WHERE user_name = $2', [address, account.userName]);
	} finally {
		await db.end();
	}
});

test.afterAll(async () => {
	if (scheduleId) await call('DELETE', `${schedulesApi}/${scheduleId}`, { token: border });
	const db = database();
	await db.connect();
	try {
		await db.query('DELETE FROM alert WHERE rule_name = $1', [formula]);
		await db.query('DELETE FROM queue_minute WHERE zone_key = $1 AND minute_utc >= $2 AND minute_utc < $3', [
			'DMO/A-CRW',
			threeLocal,
			new Date(threeLocal.getTime() + 3_600_000)
		]);
	} finally {
		await db.end();
	}
});

test('the daily report merges the lane hour and answers per role and site', async () => {
	const answer = await call('GET', `${reportsApi}/daily?date=${date}`, { token: border });
	expect(answer.status(), await answer.text()).toBe(200);
	const report = await answer.json();
	expect(report.timeZoneId).toBe('Asia/Dubai');
	const crew = report.lanes.find((l: { zone: string }) => l.zone === 'A-CRW');
	expect(crew.laneCategory).toBe('CRW');
	const hour = crew.hours.find((h: { start: string }) => h.start === '03:00');
	expect(hour).toMatchObject({ passengers: 30, waits: 30, p50Minutes: 4.5, p90Minutes: 31.5, maxQueueLength: 44, provisional: false });
	expect(crew.peak).toMatchObject({ start: '03:00', p90Minutes: 31.5 });
	expect(
		report.alerts.some((a: { ruleName: string }) => a.ruleName === formula),
		'the alert, as data'
	).toBe(true);

	expect((await call('GET', `${reportsApi}/daily?date=${date}`, { token: terminal })).status(), 'the duty manager reads it too').toBe(200);
	expect((await call('GET', `${reportsApi}/daily?date=${date}`, { token: handler })).status(), 'the handler has no report yet').toBe(403);
	expect((await call('GET', `${reportsApi}/daily?date=${date}`, { token: elsewhere })).status(), 'another site').toBe(404);
	expect((await call('GET', `${reportsApi}/daily?date=${date}`)).status()).toBe(401);
	for (const bad of ['', '2026-13-01', '30-09-2026', "2026-09-30' OR '1'='1", '2999-01-01'])
		expect((await call('GET', `${reportsApi}/daily?date=${encodeURIComponent(bad)}`, { token: border })).status(), bad).toBe(400);
});

test('the CSV files are text a spreadsheet cannot run', async () => {
	const hours = await call('GET', `${reportsApi}/daily.csv?date=${date}`, { token: border });
	expect(hours.status()).toBe(200);
	expect(hours.headers()['content-type']).toContain('text/csv');
	expect(hours.headers()['content-disposition']).toContain(`ariva-DMO-${date}-hours.csv`);
	expect(hours.headers()['cache-control']).toContain('no-store');
	expect(await hours.text()).toContain(`DMO,${date},03:00,04:00,A-CRW,CRW,30,30,4.5,31.5,44,Final,Complete`);

	const alerts = await (await call('GET', `${reportsApi}/daily.csv?date=${date}&section=alerts`, { token: border })).text();
	expect(alerts, 'the formula starts with an apostrophe and is quoted').toContain(`"'=HYPERLINK(""http://example.invalid"",""open"")"`);
	expect(
		alerts.split('\r\n').some((line) => /(^|,)[=+\-@]/.test(line)),
		'no cell starts with a formula character'
	).toBe(false);
	expect((await call('GET', `${reportsApi}/daily.csv?date=${date}&section=passwords`, { token: border })).status()).toBe(400);
	expect((await call('GET', `${reportsApi}/daily.csv?date=${date}&section=1`, { token: border })).status()).toBe(400);
});

test('a schedule sends each recipient its report once, through TickerQ in Cronz', async () => {
	test.setTimeout(90_000);
	const recipients = await call('GET', `${schedulesApi}/recipients?siteCode=DMO`, { token: border });
	expect(recipients.status()).toBe(200);
	const people = (await recipients.json()) as { id: string; userName: string }[];
	const ids = [accounts().reportBorder, accounts().reportTerminal].map((a) => people.find((p) => p.userName === a.userName)!.id);
	expect(
		people.some((p) => p.userName === accounts().dmoHandler.userName),
		'a handler cannot receive it'
	).toBe(false);
	expect(
		(
			await call('POST', schedulesApi, {
				token: border,
				data: { siteCode: 'DMO', name: 'Night peaks', sendAt: '00:00', recipientIds: [...ids, crypto.randomUUID()] }
			})
		).status(),
		'an id that is not an allowed recipient'
	).toBe(400);
	const created = await call('POST', schedulesApi, { token: border, data: { siteCode: 'DMO', name: 'Night peaks', sendAt: '00:00', recipientIds: ids } });
	expect(created.status(), await created.text()).toBe(201);
	scheduleId = (await created.json()).id;
	expect((await call('POST', schedulesApi, { token: handler, data: { siteCode: 'DMO', name: 'x', sendAt: '00:00', recipientIds: ids } })).status()).toBe(403);
	expect((await call('GET', `${schedulesApi}/${scheduleId}`, { token: elsewhere })).status(), "another site's schedule").toBe(404);

	// The dashboard's API needs the key; with it, the delivery job runs now instead of at the next five minutes.
	expect((await fetch(`${dashboard}/api/cron-tickers`)).status).toBe(401);
	expect(
		(await fetch(`${dashboard}/api/cron-tickers`, { headers: { Authorization: `Bearer ${admin}` } })).status,
		"an Ariva token is not the dashboard's key"
	).toBe(401);
	const tickers = await fetch(`${dashboard}/api/cron-tickers`, { headers: key() });
	expect(tickers.status).toBe(200);
	const job = ((await tickers.json()) as { id: string; function: string }[]).find((t) => t.function === 'ReportDeliveries');
	expect(job, 'the delivery job is scheduled').toBeTruthy();
	const run = await fetch(`${dashboard}/api/cron-ticker/run?id=${job!.id}`, { method: 'POST', headers: key() });
	expect(run.status).toBe(200);

	await expect.poll(async () => (await messagesTo(borderAddress)).length, { timeout: 30_000 }).toBe(1);
	await expect.poll(async () => (await messagesTo(terminalAddress)).length, { timeout: 30_000 }).toBe(1);
	const message = (await messagesTo(borderAddress))[0];
	expect(message.subject).toBe(`Ariva daily report DMO ${date}`);
	const raw = await (await fetch(`${smtp4dev.url}/api/messages/${message.id}/raw`)).text();
	expect(raw).toContain(`filename=ariva-DMO-${date}-hours.csv`);
	expect(raw).toContain(`filename=ariva-DMO-${date}-alerts.csv`);
	expect(raw).toContain('Worst peak hour:');

	// Again: every delivery of the day is done, so nobody gets a second email.
	expect((await fetch(`${dashboard}/api/cron-ticker/run?id=${job!.id}`, { method: 'POST', headers: key() })).status).toBe(200);
	await new Promise((resolve) => setTimeout(resolve, 5_000));
	expect((await messagesTo(borderAddress)).length).toBe(1);
	expect((await messagesTo(terminalAddress)).length).toBe(1);
	const listed = await (await call('GET', `${schedulesApi}/${scheduleId}`, { token: border })).json();
	expect(listed.lastSentUtc, 'the schedule shows its last delivery').toBeTruthy();
});

test('the dashboard page is served under a policy of its own and its API only with the key', async () => {
	const response = await fetch(`${dashboard}/`);
	expect(response.status).toBe(200);
	const policy = response.headers.get('content-security-policy') ?? '';
	expect(policy).toMatch(/^default-src 'none'; script-src 'self' 'sha256-/);
	expect(policy).toContain("frame-ancestors 'none'");
	expect(policy).not.toContain('unsafe-eval');
	for (const path of ['/api/options', '/api/ticker-functions', '/api/ticker-host/status']) expect((await fetch(`${dashboard}${path}`)).status, path).toBe(401);
	expect((await fetch(`${dashboard}/api/ticker-host/stop`, { method: 'POST' })).status, 'nobody stops the scheduler without the key').toBe(401);
});
