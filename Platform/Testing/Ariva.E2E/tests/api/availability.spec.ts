import { expect, test } from '@playwright/test';
import pg from 'pg';
import { createClient } from 'redis';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-118: the availability ledger per operating minute (formulas F18, Proposed). The site operating calendar is kept
// through api/v1/admin/sites/{siteCode}/calendar (read with the site's view permission, changed with its edit permission,
// audited; entries recorded before they take effect), and GET api/v1/sites/{siteCode}/availability (DataQuality.View)
// reads available operating minutes over operating minutes per local day, week and range. The suite plants ledger rows for
// past days at the demo airport (Dubai) and reads them back per role and site; it also plants a site of its own (E2EAV)
// with two queue zones, their queue minutes and fresh live snapshots in Redis, and waits for Ariva.Api.Cronz's job to
// record a minute as available, then one as stale once the snapshots stop.

test.skip(!databaseAvailable, 'availability needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
const availability = (site = 'DMO') => `${hosts.main}/api/v1/sites/${site}/availability`;
const calendar = (site = 'E2E3') => `${hosts.main}/api/v1/admin/sites/${site}/calendar`;
const range = (from: string, to: string) => `?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`;

function database(): pg.Client {
	return new pg.Client({
		host: process.env.Database__Host || 'localhost',
		port: Number(process.env.Database__Port || 5432),
		database: process.env.Database__Name || 'postgres',
		user: process.env.Database__Migration__Username || 'postgres',
		password: process.env.Database__Migration__Password
	});
}

async function withDatabase<T>(work: (db: pg.Client) => Promise<T>): Promise<T> {
	const db = database();
	await db.connect();
	try {
		return await work(db);
	} finally {
		await db.end();
	}
}

/** A local date (UTC calendar, E2E3 has no airport) some days ahead, different on every run so reruns never collide. */
const ahead = (days: number) => new Date(Date.UTC(new Date().getUTCFullYear(), new Date().getUTCMonth(), new Date().getUTCDate() + days)).toISOString().slice(0, 10);
const offset = 200 + Math.floor(Math.random() * 100);

let border: string, terminal: string, handler: string, elsewhere: string, admin: string, siteAdmin: string;

test.beforeAll(async () => {
	[border, terminal, handler, elsewhere, admin, siteAdmin] = await Promise.all(
		[accounts().reportBorder, accounts().reportTerminal, accounts().dmoHandler, accounts().BorderShiftSupervisor, accounts().webAdmin, accounts().calendarAdmin].map(
			async (a) => (await signIn(a)).accessToken
		)
	);
});

test.describe('site operating calendar', () => {
	const ids: { kind: 'weeks' | 'exceptions' | 'maintenance-windows'; id: string }[] = [];
	const removed: string[] = [];

	test.afterAll(async () => {
		// Safety net when an earlier test failed before the removal test: future entries can be removed, so E2E3's
		// calendar is left as the run found it. The removal test itself asserts each answer.
		for (const { kind, id } of ids.filter((entry) => !removed.includes(entry.id))) {
			const answer = await call('DELETE', `${calendar()}/${kind}/${id}`, { token: siteAdmin });
			expect([200, 404], `${kind} ${id}`).toContain(answer.status());
		}
	});

	test('an administrator of the site records hours, an exception and a maintenance window before they take effect', async () => {
		const read = await call('GET', calendar(), { token: siteAdmin });
		expect(read.status(), await read.text()).toBe(200);
		expect(await read.json()).toMatchObject({ siteCode: 'E2E3', timeZoneId: 'UTC' });

		const week = await call('PUT', `${calendar()}/weeks`, {
			token: siteAdmin,
			data: { effectiveFrom: ahead(offset), hours: [{ day: 'Friday', opens: '18:00', closes: '02:00' }, { day: 'Monday', opens: '06:00', closes: '22:00' }] }
		});
		expect(week.status(), await week.text()).toBe(200);
		const weeks = (await week.json()).weeks as { id: string; effectiveFrom: string; hours: unknown[] }[];
		const set = weeks.find((w) => w.effectiveFrom === ahead(offset));
		expect(set?.hours).toEqual([
			{ day: 'Monday', opens: '06:00', closes: '22:00' },
			{ day: 'Friday', opens: '18:00', closes: '02:00' }
		]);
		ids.push({ kind: 'weeks', id: set!.id });

		// Markup in a reason is text: stored as given and returned as a JSON string value with nosniff (as alert notes are),
		// never rendered by the API; no screen shows it in this story.
		const exception = await call('POST', `${calendar()}/exceptions`, {
			token: siteAdmin,
			data: { date: ahead(offset + 1), closed: true, hours: [], reason: xssPayloads[0] }
		});
		expect(exception.status(), await exception.text()).toBe(200);
		expect(exception.headers()['content-type']).toContain('application/json');
		expect(exception.headers()['x-content-type-options']).toBe('nosniff');
		const added = ((await exception.json()).exceptions as { id: string; date: string; reason: string; closed: boolean }[]).find((e) => e.date === ahead(offset + 1));
		expect(added).toMatchObject({ closed: true, reason: xssPayloads[0] });
		ids.push({ kind: 'exceptions', id: added!.id });

		const again = await call('POST', `${calendar()}/exceptions`, {
			token: siteAdmin,
			data: { date: ahead(offset + 1), closed: false, hours: [{ opens: '10:00', closes: '11:00' }], reason: 'Second' }
		});
		expect(again.status(), 'one exception per day').toBe(409);

		// SQL in a reason is text too: parameterised, stored verbatim.
		const window = await call('POST', `${calendar()}/maintenance-windows`, {
			token: siteAdmin,
			data: { startsUtc: `${ahead(offset)}T01:00:00Z`, endsUtc: `${ahead(offset)}T02:30:00Z`, reason: sqlInjectionPayloads[2] }
		});
		expect(window.status(), await window.text()).toBe(200);
		const planned = ((await window.json()).maintenanceWindows as { id: string; startsUtc: string; reason: string }[]).find(
			(w) => w.startsUtc === `${ahead(offset)}T01:00:00Z`
		);
		expect(planned?.reason).toBe(sqlInjectionPayloads[2]);
		ids.push({ kind: 'maintenance-windows', id: planned!.id });
		expect((await call('GET', calendar(), { token: siteAdmin })).status(), 'the tables are still there').toBe(200);
	});

	test('a window that has started, a past day or malformed hours are refused without echoing the request', async () => {
		const now = new Date(Math.floor(Date.now() / 60_000) * 60_000);
		const refused: [string, string, unknown][] = [
			['maintenance-windows', 'POST', { startsUtc: now.toISOString().replace('.000', ''), endsUtc: new Date(now.getTime() + 3_600_000).toISOString(), reason: 'Now' }],
			['maintenance-windows', 'POST', { startsUtc: `${ahead(offset)}T01:00:30Z`, endsUtc: `${ahead(offset)}T02:00:00Z`, reason: 'Seconds' }],
			['maintenance-windows', 'POST', { startsUtc: `${ahead(offset)}T01:00:00+04:00`, endsUtc: `${ahead(offset)}T02:00:00Z`, reason: 'Offset' }],
			['maintenance-windows', 'POST', { startsUtc: `${ahead(offset)}T01:00:00Z`, endsUtc: `${ahead(offset + 8)}T01:00:00Z`, reason: 'Too long' }],
			['maintenance-windows', 'POST', { startsUtc: sqlInjectionPayloads[0], endsUtc: xssPayloads[1], reason: 'x' }],
			['exceptions', 'POST', { date: ahead(0), closed: true, hours: [], reason: 'Today' }],
			['exceptions', 'POST', { date: ahead(-3), closed: true, hours: [], reason: 'Past' }],
			['exceptions', 'POST', { date: `${ahead(offset + 2)}' OR '1'='1`, closed: true, hours: [], reason: 'x' }],
			['exceptions', 'POST', { date: ahead(offset + 2), closed: true, hours: [], reason: 'bidi \u202e override' }],
			['exceptions', 'POST', { date: ahead(offset + 2), closed: false, hours: [], reason: 'Open without hours' }],
			['weeks', 'PUT', { effectiveFrom: ahead(1), hours: [{ day: '<script>alert(1)</script>', opens: '06:00', closes: '22:00' }] }],
			['weeks', 'PUT', { effectiveFrom: ahead(1), hours: [{ day: 'Monday', opens: "06:00' OR '1'='1", closes: '22:00' }] }],
			['weeks', 'PUT', { effectiveFrom: ahead(1), hours: [{ day: 'Monday', opens: '06:00', closes: '12:00' }, { day: 'Monday', opens: '11:00', closes: '13:00' }] }],
			['weeks', 'PUT', { effectiveFrom: ahead(1), hours: Array.from({ length: 29 }, () => ({ day: 'Monday', opens: '06:00', closes: '07:00' })) }],
			['weeks', 'PUT', { effectiveFrom: ahead(400), hours: [] }],
			['exceptions', 'POST', { date: ahead(offset + 2), closed: true, hours: [], reason: 'x'.repeat(201) }]
		];
		for (const [kind, method, data] of refused) {
			const answer = await call(method, `${calendar()}/${kind}`, { token: siteAdmin, data });
			expect(answer.status(), `${kind} ${JSON.stringify(data).slice(0, 80)}`).toBe(400);
			const text = await answer.text();
			for (const leak of ["OR '1'", '<script>', 'onerror', 'xxxxxxxxxx', 'override']) expect(text, leak).not.toContain(leak);
		}
	});

	test('the permission and the site decide who reads and changes it', async () => {
		const week = { effectiveFrom: ahead(offset + 3), hours: [] };
		expect((await call('GET', calendar())).status()).toBe(401);
		expect((await call('PUT', `${calendar()}/weeks`, { data: week })).status()).toBe(401);
		expect((await call('GET', calendar('E2E1'), { token: elsewhere })).status(), "a supervisor reads its own site's calendar").toBe(200);
		expect((await call('GET', calendar('DMO'), { token: handler })).status(), 'every role views its sites').toBe(200);
		expect((await call('PUT', `${calendar('E2E1')}/weeks`, { token: elsewhere, data: week })).status(), 'only the edit permission changes it').toBe(403);
		expect((await call('POST', `${calendar('DMO')}/exceptions`, { token: terminal, data: { date: ahead(offset), closed: true, reason: 'x' } })).status()).toBe(403);

		// Another site: 404 like a site that does not exist, before the body is looked at, with nothing of the site in the answer.
		for (const [method, url, data] of [
			['GET', calendar('DMO'), undefined],
			['PUT', `${calendar('DMO')}/weeks`, week],
			['POST', `${calendar('DMO')}/maintenance-windows`, { startsUtc: 'not a time', endsUtc: 'x', reason: 'x' }],
			['GET', calendar('ZZ9'), undefined],
			['GET', calendar('e2e3'), undefined]
		] as [string, string, unknown][]) {
			const answer = await call(method, url, { token: siteAdmin, data });
			expect(answer.status(), `${method} ${url}`).toBe(404);
			const text = await answer.text();
			for (const leak of ['Dubai', 'weeks', 'timeZoneId']) expect(text, leak).not.toContain(leak);
		}
		expect((await call('GET', calendar('E2E3'), { token: border })).status(), "a supervisor of another site").toBe(404);

		// An entry of one site through another site's route: 404, and it stays.
		const own = await call('GET', calendar(), { token: siteAdmin });
		const entry = ((await own.json()).maintenanceWindows as { id: string }[])[0];
		expect(entry, 'the first test planted a window').toBeTruthy();
		expect((await call('DELETE', `${calendar('DMO')}/maintenance-windows/${entry.id}`, { token: admin })).status()).toBe(404);
		expect(((await (await call('GET', calendar(), { token: siteAdmin })).json()).maintenanceWindows as { id: string }[]).map((w) => w.id)).toContain(entry.id);
		expect((await call('DELETE', `${calendar()}/maintenance-windows/00000000-0000-7000-8000-000000000000`, { token: siteAdmin })).status()).toBe(404);
	});

	test('an entry that has not taken effect is removed: 200, gone from the calendar, kept as deleted', async () => {
		expect(ids.map((entry) => entry.kind).sort(), 'the first test planted one of each').toEqual(['exceptions', 'maintenance-windows', 'weeks']);
		for (const { kind, id } of ids) {
			const answer = await call('DELETE', `${calendar()}/${kind}/${id}`, { token: siteAdmin });
			expect(answer.status(), `${kind} ${await answer.text()}`).toBe(200);
			const view = await answer.json();
			const listed = (view[kind === 'maintenance-windows' ? 'maintenanceWindows' : kind] as { id: string }[]).map((entry) => entry.id);
			expect(listed, kind).not.toContain(id);
			removed.push(id);
		}

		const view = await (await call('GET', calendar(), { token: siteAdmin })).json();
		const listed = [...view.weeks, ...view.exceptions, ...view.maintenanceWindows].map((entry: { id: string }) => entry.id);
		for (const { id } of ids) expect(listed).not.toContain(id);
		const deleted = await withDatabase(async (db) =>
			(
				await db.query(
					`SELECT count(*)::int AS n FROM (
					   SELECT id, deleted_on FROM operating_week UNION ALL SELECT id, deleted_on FROM operating_day_exception
					   UNION ALL SELECT id, deleted_on FROM maintenance_window) e
					 WHERE id = ANY ($1::uuid[]) AND deleted_on IS NOT NULL`,
					[ids.map((entry) => entry.id)]
				)
			).rows[0].n as number
		);
		expect(deleted, 'soft deleted, kept for the record').toBe(3);
		expect((await call('DELETE', `${calendar()}/weeks/${ids.find((entry) => entry.kind === 'weeks')!.id}`, { token: siteAdmin })).status(), 'removed once').toBe(404);
	});

	test('every change is in the audit trail', async () => {
		const actions = await withDatabase(async (db) =>
			(await db.query("SELECT action FROM audit_entry WHERE target_name = 'E2E3' AND action LIKE 'SiteCalendar.%'")).rows.map((r) => r.action as string)
		);
		expect(actions).toEqual(
			expect.arrayContaining([
				'SiteCalendar.WeekSet',
				'SiteCalendar.ExceptionAdded',
				'SiteCalendar.MaintenanceAdded',
				'SiteCalendar.WeekRemoved',
				'SiteCalendar.ExceptionRemoved',
				'SiteCalendar.MaintenanceCancelled'
			])
		);
		const removals = await withDatabase(async (db) =>
			(
				await db.query(
					`SELECT action, before_summary, after_summary FROM audit_entry
					  WHERE target_id = ANY ($1::uuid[]) AND action IN ('SiteCalendar.WeekRemoved', 'SiteCalendar.ExceptionRemoved', 'SiteCalendar.MaintenanceCancelled')`,
					[ids.map((entry) => entry.id)]
				)
			).rows as { action: string; before_summary: string; after_summary: string | null }[]
		);
		expect(removals.map((r) => r.action).sort()).toEqual(['SiteCalendar.ExceptionRemoved', 'SiteCalendar.MaintenanceCancelled', 'SiteCalendar.WeekRemoved']);
		for (const removal of removals) {
			expect(removal.after_summary, removal.action).toBeNull();
			expect(removal.before_summary, removal.action).toContain('site=E2E3');
		}
		expect(removals.find((r) => r.action === 'SiteCalendar.WeekRemoved')!.before_summary).toContain(`effectiveFrom=${ahead(offset)}`);
		expect(removals.find((r) => r.action === 'SiteCalendar.ExceptionRemoved')!.before_summary).toContain(`date=${ahead(offset + 1)}`);
		expect(removals.find((r) => r.action === 'SiteCalendar.MaintenanceCancelled')!.before_summary).toContain(`startsUtc=${ahead(offset)}T01:00Z`);
	});
});

test.describe('availability per day, week and range', () => {
	// Two local days at the demo airport (Dubai, UTC+4), planted as the job writes them. 30 December 2025 (a Tuesday): 10
	// operating minutes (8 available, 1 with a stale and a missing zone, 1 unobserved after downtime), 2 in maintenance and 3
	// closed; 5 January 2026 (the next Monday): 4 operating minutes, all available.
	const rows: [string, string, string, string, string[]][] = [
		...Array.from({ length: 8 }, (_, i) => ['2025-12-30', `2025-12-30T06:0${i}:00Z`, 'Operating', 'Available', []] as [string, string, string, string, string[]]),
		['2025-12-30', '2025-12-30T06:08:00Z', 'Operating', 'Unavailable', ['StaleZone', 'MissingMinute']],
		['2025-12-30', '2025-12-30T06:09:00Z', 'Operating', 'Unobserved', ['NotObservedLive']],
		['2025-12-30', '2025-12-30T06:10:00Z', 'Maintenance', 'Unavailable', ['StaleZone']],
		['2025-12-30', '2025-12-30T06:11:00Z', 'Maintenance', 'Available', []],
		['2025-12-30', '2025-12-30T19:00:00Z', 'Closed', 'Unavailable', ['NoPublishedZones']],
		['2025-12-30', '2025-12-30T19:01:00Z', 'Closed', 'Available', []],
		['2025-12-30', '2025-12-30T19:02:00Z', 'Closed', 'Available', []],
		...Array.from({ length: 4 }, (_, i) => ['2026-01-05', `2026-01-05T06:0${i}:00Z`, 'Operating', 'Available', []] as [string, string, string, string, string[]])
	];

	const clear = (db: pg.Client) =>
		db.query("DELETE FROM availability_minute WHERE site_code = 'DMO' AND minute_utc >= '2025-12-20T00:00:00Z' AND minute_utc < '2026-01-10T00:00:00Z'");

	test.beforeAll(async () => {
		await withDatabase(async (db) => {
			// The ledger is write-once for the runtime role; the migration login (this client) can clear its own rows.
			await clear(db);
			for (const [date, minute, calendarState, state, reasons] of rows)
				await db.query(
					`INSERT INTO availability_minute (site_code, minute_utc, local_date, calendar, state, reasons, zones_expected, zones_stale, zones_missing, zones_lagging,
					   profile_version, rule_version, decided_utc)
					 VALUES ('DMO', $1, $2, $3, $4, $5, 2, $6, $7, 0, 12, 1, now())`,
					[minute, date, calendarState, state, reasons, reasons.includes('StaleZone') ? 1 : 0, reasons.includes('MissingMinute') ? 1 : 0]
				);
		});
	});

	test.afterAll(async () => withDatabase(clear));

	const zero = {
		recordedMinutes: 0,
		operatingMinutes: 0,
		availableMinutes: 0,
		unavailableMinutes: 0,
		unobservedMinutes: 0,
		maintenanceMinutes: 0,
		closedMinutes: 0,
		staleZoneMinutes: 0,
		missingMinuteMinutes: 0,
		streamLagMinutes: 0,
		noPublishedZonesMinutes: 0,
		availability: null,
		availabilityMaintenanceAsUnavailable: null
	};
	const tuesday = {
		...zero,
		recordedMinutes: 15,
		operatingMinutes: 10,
		availableMinutes: 8,
		unavailableMinutes: 1,
		unobservedMinutes: 1,
		maintenanceMinutes: 2,
		closedMinutes: 3,
		staleZoneMinutes: 1,
		missingMinuteMinutes: 1,
		availability: 0.8,
		// The stricter figure: the 2 maintenance minutes count as operating and not available, 8 of 12.
		availabilityMaintenanceAsUnavailable: 8 / 12
	};
	const monday = { ...zero, recordedMinutes: 4, operatingMinutes: 4, availableMinutes: 4, availability: 1, availabilityMaintenanceAsUnavailable: 1 };

	test('available operating minutes over operating minutes, per local day, Monday-to-Sunday week and range', async () => {
		const answer = await call('GET', availability() + range('2025-12-30', '2026-01-05'), { token: border });
		expect(answer.status(), await answer.text()).toBe(200);
		const view = await answer.json();
		expect(view).toMatchObject({ siteCode: 'DMO', timeZoneId: 'Asia/Dubai', from: '2025-12-30', to: '2026-01-05', pilotTarget: 0.99 });
		expect(view.days).toHaveLength(7);
		expect(view.days[0]).toEqual({ date: '2025-12-30', counts: tuesday });
		expect(view.days[1]).toEqual({ date: '2025-12-31', counts: zero });
		expect(view.days[6]).toEqual({ date: '2026-01-05', counts: monday });
		expect(view.weeks).toEqual([
			{ weekStart: '2025-12-29', days: 6, counts: tuesday },
			{ weekStart: '2026-01-05', days: 1, counts: monday }
		]);
		expect(view.total.operatingMinutes).toBe(14);
		expect(view.total.availableMinutes).toBe(12);
		expect(view.total.availability).toBeCloseTo(12 / 14, 12);
		expect(view.total.availabilityMaintenanceAsUnavailable, 'maintenance cannot inflate the stricter figure').toBeCloseTo(12 / 16, 12);

		const one = await (await call('GET', availability() + range('2026-01-05', '2026-01-05'), { token: terminal })).json();
		expect(one.total, "the duty manager's read of one day").toEqual(monday);
		const empty = await (await call('GET', availability() + range('2025-11-01', '2025-11-30'), { token: admin })).json();
		expect(empty.total, 'no operating minute gives no ratio').toEqual(zero);
	});

	test('the permission and the site decide who reads it', async () => {
		const url = availability() + range('2025-12-30', '2026-01-05');
		expect((await call('GET', url, { token: terminal })).status(), 'the terminal duty manager').toBe(200);
		expect((await call('GET', url, { token: admin })).status(), 'an administrator').toBe(200);
		expect((await call('GET', url, { token: handler })).status(), 'a handler station manager holds no DataQuality.View').toBe(403);
		expect((await call('GET', url)).status()).toBe(401);

		const foreign = await call('GET', url, { token: elsewhere });
		expect(foreign.status(), "another site's supervisor").toBe(404);
		const text = await foreign.text();
		for (const leak of ['DMO', 'Dubai', 'operatingMinutes', '2025-12-30']) expect(text, leak).not.toContain(leak);
		expect((await call('GET', availability('E2E1') + range('2025-12-30', '2026-01-05'), { token: border })).status(), 'a site the caller does not reach').toBe(404);
		const foreignInvalid = await call('GET', availability('E2E1') + range('yesterday', '2026-01-05'), { token: border });
		expect(foreignInvalid.status(), 'an invalid range against a site the caller does not reach').toBe(404);
		expect(await foreignInvalid.text()).not.toContain('yesterday');
		expect((await call('GET', availability('ZZ9') + range('2025-12-30', '2026-01-05'), { token: admin })).status(), 'a site that does not exist').toBe(404);
		expect((await call('GET', availability('dmo') + range('2025-12-30', '2026-01-05'), { token: admin })).status(), 'a site code is exact').toBe(404);
	});

	test('a range over 92 days or not of local dates is refused, without echoing the query', async () => {
		const refused: [string, string][] = [
			['2025-01-01', '2025-04-03'],
			['2026-01-05', '2025-12-30'],
			['2025-12-30T00:00:00Z', '2026-01-05'],
			['30/12/2025', '2026-01-05'],
			['2025-02-30', '2025-03-01'],
			["2025-12-30' OR '1'='1", '2026-01-05'],
			[sqlInjectionPayloads[2], '2026-01-05'],
			[xssPayloads[0], '2026-01-05'],
			['', '2026-01-05'],
			['yesterday', 'today']
		];
		for (const [from, to] of refused) {
			const answer = await call('GET', availability() + range(from, to), { token: border });
			expect(answer.status(), `${from} ${to}`).toBe(400);
			const text = await answer.text();
			for (const leak of ["OR '1'", 'DROP', '<script>', 'yesterday', '30/12']) expect(text, leak).not.toContain(leak);
		}

		expect((await call('GET', availability(), { token: border })).status(), 'no range').toBe(400);
		expect((await call('GET', availability() + range('2025-01-01', '2025-04-02'), { token: border })).status(), 'exactly 92 days').toBe(200);
	});
});

test.describe("the Cronz job's ledger", () => {
	// A site of this suite's own: two queue zones of a published profile (planted as the zone profile service leaves them),
	// their queue minutes and fresh live snapshots. No other suite writes E2EAV's snapshots, so nothing here changes what
	// the live screens' suites see.
	const site = 'E2EAV';
	const zones = ['AV-1', 'AV-2'];

	test.beforeAll(async () => {
		await withDatabase(async (db) => {
			await db.query('BEGIN');
			await db.query("INSERT INTO site (id, code, name) SELECT gen_random_uuid(), $1::varchar, 'Availability E2E' WHERE NOT EXISTS (SELECT 1 FROM site WHERE code = $1::varchar)", [site]);
			const profiles = await db.query("SELECT 1 FROM zone_profile WHERE site_code = $1 AND status = 'Published'", [site]);
			if (profiles.rowCount === 0) {
				await db.query(`INSERT INTO airport (id, iata_code, name, time_zone_id) SELECT gen_random_uuid(), 'AVZ', 'Availability E2E', 'Asia/Dubai'
				                WHERE NOT EXISTS (SELECT 1 FROM airport WHERE iata_code = 'AVZ' AND deleted_on IS NULL)`);
				await db.query(`INSERT INTO terminal (id, airport_id, code, name, site_code)
				                SELECT gen_random_uuid(), a.id, 'AV', 'Availability', $1::varchar FROM airport a WHERE a.iata_code = 'AVZ' AND a.deleted_on IS NULL LIMIT 1`, [site]);
				await db.query(`INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
				                SELECT gen_random_uuid(), t.id, $1::varchar, 'L0', 'Ground', 0, 100, 100 FROM terminal t WHERE t.site_code = $1::varchar LIMIT 1`, [site]);
				const profile = await db.query("INSERT INTO zone_profile (id, site_code, name, status) VALUES (gen_random_uuid(), $1, 'Availability', 'Draft') RETURNING id", [site]);
				for (const zone of zones)
					await db.query(
						`INSERT INTO zone (id, profile_id, name, kind, level_id, polygon) SELECT gen_random_uuid(), $1::uuid, $2::varchar, 'Queue', l.id, '[]' FROM level l WHERE l.site_code = $3::varchar LIMIT 1`,
						[profile.rows[0].id, zone, site]
					);
				await db.query(
					"UPDATE zone_profile SET status = 'Published', version = 1, geometry_hash = repeat('a', 64), published_on = now() - INTERVAL '1 day', published_by = 'e2e' WHERE id = $1",
					[profile.rows[0].id]
				);
			}
			await db.query('COMMIT');
		});
	});

	test('records a live minute as available, then a minute as stale when the snapshots stop', async () => {
		test.skip(!redisUrl, "the ledger reads the run's Redis (ARIVA_E2E_REDIS_URL)");
		test.setTimeout(330_000);
		const redis = createClient({ url: redisUrl });
		await redis.connect();
		const started = new Date(Math.floor(Date.now() / 60_000) * 60_000);
		try {
			await withDatabase((db) =>
				db.query(
					`INSERT INTO queue_minute (zone_key, minute_utc, profile_version, updated_on)
					 SELECT $1::text || '/' || z, m, 1, now() FROM unnest($2::text[]) AS z, generate_series($3::timestamptz, $3::timestamptz + INTERVAL '8 minutes', INTERVAL '1 minute') AS m
					 ON CONFLICT DO NOTHING`,
					[site, zones, started.toISOString()]
				)
			);
			const publish = async () => {
				const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000).toISOString();
				for (const zone of zones) {
					const snapshot = {
						zoneKey: `${site}/${zone}`,
						minuteUtc: minute,
						queueLength: 3,
						lengthFromSensors: true,
						lengthDegraded: false,
						nowcastMinutes: 2,
						throughputPerMinute: 2,
						noService: null,
						nowcastDegraded: false,
						publishedUtc: new Date().toISOString()
					};
					await redis.set(`${instance}live:zone:${snapshot.zoneKey}`, JSON.stringify(snapshot), { EX: 600 });
				}
			};
			const ledger = async (state: string, after: Date) =>
				withDatabase(async (db) =>
					(
						await db.query("SELECT minute_utc, reasons FROM availability_minute WHERE site_code = $1 AND state = $2 AND minute_utc >= $3 ORDER BY minute_utc", [
							site,
							state,
							after.toISOString()
						])
					).rows as { minute_utc: Date; reasons: string[] }[]
				);

			// Fresh snapshots every 5 seconds until the job has decided a planted minute.
			let available: { minute_utc: Date }[] = [];
			for (let i = 0; i < 36 && available.length === 0; i++) {
				await publish();
				await new Promise((resolve) => setTimeout(resolve, 5_000));
				available = await ledger('Available', started);
			}
			expect(available.length, 'a live minute with both zones fresh and their queue minutes').toBeGreaterThan(0);

			// The snapshots stop: the next minutes the job decides find no snapshot, and the zones are stale.
			await redis.del(zones.map((zone) => `${instance}live:zone:${site}/${zone}`));
			const stopped = new Date(Math.floor(Date.now() / 60_000) * 60_000);
			let stale: { reasons: string[] }[] = [];
			for (let i = 0; i < 30 && stale.length === 0; i++) {
				await new Promise((resolve) => setTimeout(resolve, 5_000));
				stale = await ledger('Unavailable', stopped);
			}
			expect(stale.length).toBeGreaterThan(0);
			expect(stale[0].reasons).toContain('StaleZone');
		} finally {
			await redis.del(zones.map((zone) => `${instance}live:zone:${site}/${zone}`));
			await redis.quit();
		}
	});
});
