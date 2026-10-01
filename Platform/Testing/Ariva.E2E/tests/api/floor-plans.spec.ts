import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-018: floor plans per level. PNG, JPEG and SVG only, typed by their bytes; SVG is sanitised (scripts, event
// attributes, foreignObject and external references removed) and served under a sandboxing CSP; 20 MB limit (413);
// file names with a path are refused (CWE-22) and the stored name is always generated. The seed binds e2e.border to
// E2E1 and e2e.terminal to E2E2.

test.skip(!databaseAvailable, 'floor plans need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const adminApi = `${hosts.main}/api/v1/admin`;
const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const iata = Array.from({ length: 3 }, () => letters[Math.floor(Math.random() * 26)]).join('');
const png = Buffer.from(
	'iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFklEQVR4nGP8z8DwnwEJMDGgAQYGBgCkKAIFD1jZ6wAAAABJRU5ErkJggg==',
	'base64'
);
const hostileSvg = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="100" onload="alert(1)">
<script>alert(2)</script>
<rect width="50" height="50" fill="#123456" onclick="alert(3)"/>
<foreignObject width="10" height="10"><div xmlns="http://www.w3.org/1999/xhtml">x</div></foreignObject>
<image href="https://attacker.example/p.png" width="1" height="1"/>
<text x="5" y="80">Gate</text>
<a xlink:href="javascript:alert(4)"><text x="5" y="90">click</text></a>
</svg>`;

let admin: string;
let levelUrl: string;

function upload(content: Buffer | string, fileName: string, type: string, scale = '0.05'): FormData {
	const form = new FormData();
	form.append('file', new Blob([content], { type }), fileName);
	form.append('metresPerPixel', scale);
	form.append('originX', '0');
	form.append('originY', '0');
	return form;
}

test.beforeAll(async () => {
	admin = (await signIn(accounts().SystemAdministrator)).accessToken;
	const create = async (entity: string, data: unknown) => {
		const response = await call('POST', `${adminApi}/${entity}`, { token: admin, data });
		expect(response.status(), `${entity}: ${await response.text()}`).toBe(201);
		return (await response.json()).id as string;
	};
	const airport = await create('airports', { iataCode: iata, name: `E2E plans ${iata}`, timeZoneId: 'Asia/Amman' });
	const terminal = await create('terminals', { airportId: airport, code: 'T1', name: 'Terminal 1', siteCode: 'E2E1' });
	const level = await create('levels', { terminalId: terminal, code: 'L0', name: 'Arrivals', floorNumber: 0, widthMetres: 100, depthMetres: 50 });
	levelUrl = `${adminApi}/levels/${level}/floor-plan`;
});

test('a PNG is stored under a generated name and served with a sandboxing policy', async () => {
	const created = await call('POST', levelUrl, { token: admin, form: upload(png, 'hall.png', 'image/png') });
	expect(created.status(), await created.text()).toBe(201);
	const plan = await created.json();
	expect(plan.contentType).toBe('image/png');
	expect(plan.widthPixels).toBe(2);
	expect(plan.originalFileName).toBe('hall.png');

	const content = await call('GET', `${levelUrl}/content`, { token: admin });
	expect(content.status()).toBe(200);
	expect(content.headers()['content-type']).toBe('image/png');
	expect(content.headers()['content-security-policy']).toContain('sandbox');
	expect(content.headers()['content-security-policy']).toContain("default-src 'none'");
	expect(content.headers()['x-content-type-options']).toBe('nosniff');
});

test('an SVG with script is neutralised and replaces the previous plan', async () => {
	const created = await call('POST', levelUrl, { token: admin, form: upload(hostileSvg, 'hall.svg', 'image/svg+xml') });
	expect(created.status(), await created.text()).toBe(201);
	expect((await created.json()).contentType).toBe('image/svg+xml');

	const body = await (await call('GET', `${levelUrl}/content`, { token: admin })).text();
	for (const removed of ['<script', 'onload', 'onclick', 'foreignObject', 'attacker.example', 'javascript:', 'alert(']) {
		expect(body, `${removed} is removed`).not.toContain(removed);
	}
	expect(body, 'the drawing itself survives').toContain('#123456');
	expect(body).toContain('Gate');

	const audit = await call('GET', `${hosts.main}/api/v1/admin/audit-entries?action=FloorPlan.Uploaded`, { token: admin });
	expect(audit.status()).toBe(200);
});

test('HTML dressed as an image, unsafe names and oversized uploads are refused', async () => {
	const html = await call('POST', levelUrl, { token: admin, form: upload('<html><script>alert(1)</script></html>', 'plan.svg', 'image/svg+xml') });
	expect(html.status(), 'HTML is not an image').toBe(400);
	const text = await call('POST', levelUrl, { token: admin, form: upload('just text', 'plan.png', 'image/png') });
	expect(text.status(), 'the declared type is not trusted').toBe(400);

	for (const name of ['../../etc/passwd.png', '..\\..\\windows\\win.ini', 'C:plan.png', 'a/b.png']) {
		const response = await call('POST', levelUrl, { token: admin, form: upload(png, name, 'image/png') });
		expect(response.status(), `name ${JSON.stringify(name)}`).toBe(400);
	}

	const big = Buffer.concat([png, Buffer.alloc(20 * 1024 * 1024 + 1)]);
	// The multipart body has a known length, so Kestrel answers 413 before reading it.
	const oversized = await call('POST', levelUrl, { token: admin, form: upload(big, 'big.png', 'image/png') });
	expect(oversized.status(), 'oversized upload').toBe(413);
	expect((await (await call('GET', levelUrl, { token: admin })).json()).contentType, 'the SVG plan is still current').toBe('image/svg+xml');
});

test('calibration, site scoping and delete', async () => {
	const calibrated = await call('PUT', `${levelUrl}/calibration`, { token: admin, data: { metresPerPixel: 0.02, originX: 5, originY: 7 } });
	expect(calibrated.status(), await calibrated.text()).toBe(200);
	expect((await calibrated.json()).metresPerPixel).toBe(0.02);
	expect((await call('PUT', `${levelUrl}/calibration`, { token: admin, data: { metresPerPixel: 0, originX: 0, originY: 0 } })).status(), 'zero scale').toBe(400);

	const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
	const terminal = (await signIn(accounts().TerminalDutyManager)).accessToken;
	expect((await call('GET', `${levelUrl}/content`, { token: border })).status(), 'own site reads').toBe(200);
	expect((await call('POST', levelUrl, { token: border, form: upload(png, 'x.png', 'image/png') })).status(), 'supervisors do not upload').toBe(403);
	const other = await call('GET', levelUrl, { token: terminal });
	expect(other.status(), 'another site answers like an unknown level').toBe(404);
	expect(await other.text()).not.toContain('image/svg+xml');

	expect((await call('DELETE', levelUrl, { token: admin })).status()).toBe(204);
	expect((await call('GET', levelUrl, { token: admin })).status()).toBe(404);
});
