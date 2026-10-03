import { expect, test } from '@playwright/test';
import pg from 'pg';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { expectNoLeak, expectProblemDetails } from '../support/api-assertions';
import { hosts } from '../support/hosts';

// ARV-046: SSIM schedule files. An administrator previews what a file holds for DMO (legs arriving at or leaving its
// airport, expanded per operating date within the horizon, with the lines Ariva could not read), then imports exactly
// what was previewed (the preview token binds the file, the site, the horizon and the user). Malformed, oversized and
// hostile files are refused or reported by line number without echoing them. A schedule creates legs and never changes
// one a live feed has reported.

test.skip(!databaseAvailable, 'SSIM imports need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = (site = 'DMO') => `${hosts.main}/api/v1/admin/sites/${site}/flight-schedules`;
const number = 1000 + Math.floor(Math.random() * 8000);
let admin: string;

const ddMMMyy = (offsetDays: number) => {
	const d = new Date(Date.now() + offsetDays * 86_400_000);
	return `${String(d.getUTCDate()).padStart(2, '0')}${d.toLocaleString('en-US', { month: 'short', timeZone: 'UTC' }).toUpperCase()}${String(d.getUTCFullYear()).slice(2)}`;
};

/** A type 3 record with its fields at their SSIM positions (1-based). */
function leg(flight: number, from: string, to: string, days = '1234567'): string {
	const line = Array(200).fill(' ');
	const put = (start: number, value: string) => value.split('').forEach((c, i) => (line[start - 1 + i] = c));
	put(1, '3');
	put(3, 'QR ');
	put(6, String(flight).padStart(4, '0'));
	put(10, '0101J');
	put(15, ddMMMyy(-5));
	put(22, ddMMMyy(60));
	put(29, days);
	put(37, from);
	put(40, '0800');
	put(44, '0800');
	put(48, '+0000');
	put(55, to);
	put(58, '1200');
	put(62, '1200');
	put(66, '+0000');
	put(71, 'T1');
	put(73, '359');
	return line.join('');
}

const file = (...lines: string[]) => ['1AIRLINE STANDARD SCHEDULE DATA SET'.padEnd(200), '2UQR '.padEnd(200), ...lines, '5QR'.padEnd(200)].join('\r\n') + '\r\n';

function upload(path: string, content: string | Uint8Array, fields: Record<string, string> = {}, site = 'DMO') {
	const form = new FormData();
	form.append('file', new Blob([content]), 'schedule.ssim');
	for (const [name, value] of Object.entries(fields)) form.append(name, value);
	return call('POST', `${api(site)}/${path}`, { token: admin, form });
}

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

test.beforeAll(async () => {
	admin = (await signIn(accounts().SystemAdministrator)).accessToken;
});

test('a file is previewed, then exactly that file is imported as the ssim feed', async () => {
	const content = file(leg(number, 'DOH', 'DMO'), leg(number + 1, 'DMO', 'DOH', '1 3 5'), leg(number + 2, 'DOH', 'KWI'));
	const preview = await upload('preview', content, { horizonDays: '7' });
	expect(preview.status(), await preview.text()).toBe(200);
	const summary = await preview.json();
	expect(summary).toMatchObject({ legRecords: 3, legRecordsOfSite: 2, arrivals: 9, errorCount: 0 });
	expect(summary.sha256).toMatch(/^[0-9a-f]{64}$/);
	expect(summary.sample.length).toBeGreaterThan(0);
	expect(await query(`SELECT 1 FROM flight_leg WHERE flight_key LIKE $1`, [`QR${number}-%`]), 'a preview changes nothing').toHaveLength(0);

	expect(summary.previewToken).toMatch(/^[A-Za-z0-9_-]{20,2048}$/);
	const token = { previewToken: summary.previewToken };
	await expectProblemDetails(await upload('import', content.replace('T1', 'T2'), { horizonDays: '7', ...token }), 409);
	await expectProblemDetails(await upload('import', content, { horizonDays: '200', ...token }), 409);
	const elsewhere = await upload('import', content, { horizonDays: '7', ...token }, 'ZZ9');
	expect(elsewhere.status(), 'a token of DMO at another site').toBe(404);
	expect(await query(`SELECT 1 FROM flight_leg WHERE flight_key LIKE $1`, [`QR${number}-%`]), 'a refused import changes nothing').toHaveLength(0);

	const imported = await upload('import', content, { horizonDays: '7', ...token });
	expect(imported.status(), await imported.text()).toBe(200);
	expect(await imported.json()).toMatchObject({ sha256: summary.sha256, refused: 0 });
	const rows = await query<{ n: string }>(
		`SELECT count(*) AS n FROM flight_leg WHERE site_code = 'DMO' AND feed = 'ssim' AND schedule_feed = 'ssim' AND schedule_fallback AND flight_key LIKE $1`,
		[`QR${number}-%`]
	);
	expect(Number(rows[0].n)).toBe(9);
});

test('malformed lines are reported by number without echoing them; hostile and oversized files are refused', async () => {
	const hostile = leg(number + 3, 'DOH', 'DMO').slice(0, 14) + "' OR 1=1 --" + leg(number + 3, 'DOH', 'DMO').slice(25);
	const badAirline = leg(number + 5, 'DOH', 'DMO').slice(0, 2) + '<s>' + leg(number + 5, 'DOH', 'DMO').slice(5);
	const report = await upload(
		'preview',
		file(leg(number + 4, 'DOH', 'DMO'), hostile, 'x'.repeat(300), '3 <script>alert(1)</script>' + String.fromCharCode(7), badAirline),
		{ horizonDays: '3' }
	);
	expect(report.status(), await report.text()).toBe(200);
	const text = await report.text();
	const body = JSON.parse(text);
	expect(body.errorCount).toBe(4);
	expect(body.errors.map((e: { line: number }) => e.line)).toEqual([4, 5, 6, 7]);
	expect(body.sample.every((l: { flightKey: string }) => l.flightKey.startsWith(`QR${number + 4}-`)), 'a refused record is never in the sample').toBe(true);
	for (const echoed of ['OR 1=1', '<script>', 'xxxx', '<s>', '<S>']) expect(text).not.toContain(echoed);

	for (const [why, content, status] of [
		['no leg records', '1HEADER\r\n2UQR\r\n5QR\r\n', 400],
		['binary', new Uint8Array(Array.from({ length: 4096 }, (_, i) => (i * 37) % 256)), 400],
		// One byte over 20 MB: the request is within Kestrel's limit (20 MB and 64 KB for the framing), so the API itself answers 413.
		['one byte over 20 MB', 'x'.repeat(20 * 1024 * 1024 + 1), 413]
	] as const) {
		const refused = await upload('preview', content as string | Uint8Array);
		expect(refused.status(), why).toBe(status);
		expectNoLeak(await refused.text(), why);
	}

	// Past the request limit Kestrel answers 413 from the Content-Length and stops reading, so the client may instead see
	// the connection closed while it is still sending (behind the ingress, its 21m limit answers 413 first). Never read.
	const oversized = await upload('preview', 'x'.repeat(21 * 1024 * 1024)).then(
		(r) => r.status(),
		(e: Error) => `closed: ${e.message}`
	);
	expect([413, 'closed: fetch failed'], 'over the request limit').toContain(oversized);

	expect((await upload('preview', file(leg(number, 'DOH', 'DMO')), { horizonDays: '500' })).status(), 'horizon 1 to 200').toBe(400);
	expect((await upload('import', file(leg(number, 'DOH', 'DMO')), { previewToken: 'not a token!' })).status()).toBe(400);
	expect((await upload('import', file(leg(number, 'DOH', 'DMO')))).status(), 'no token').toBe(400);
	await expectProblemDetails(await upload('import', file(leg(number, 'DOH', 'DMO')), { previewToken: 'CfDJ8forged' }), 409);
	expect((await call('POST', `${api()}/preview`, { token: admin, form: new FormData() })).status(), 'no file').toBe(400);
	expect((await upload('preview', file(leg(number, 'DOH', 'DMO')), {}, 'ZZ9')).status(), 'an unknown site').toBe(404);
});
