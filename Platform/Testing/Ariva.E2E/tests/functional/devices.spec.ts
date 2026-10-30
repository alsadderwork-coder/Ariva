import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, totpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, earlyInStep, fillSecret, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';

// ARV-054: the devices screen on the registry, calibration and health API (ARV-021 to ARV-025). A terminal duty manager
// registers a sensor at the demo airport after a step-up; its credential is shown once with a copy action, works at the
// Ingest host, and is never shown again; a calibration takes it Online; a new credential replaces the old one at once.
// Handler station managers have no devices screen and the server refuses them. Text from the registry is text.
// ARV-259e4: a duty manager declares the lines the sensor counts (All, These lines, No lines); a line its footprint does
// not reach is a warning; a line name from the zone profile is text; the list names lines a publish dropped.

test.skip(!databaseAvailable, 'the devices screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const code = `W${crypto.randomBytes(2).toString('hex').toUpperCase()}`;
const deviceUrl = `${hosts.ingest}/api/v1/ingest/device`;

/** The demo airport's first arrivals queue zone and a point inside it (the device must reach its zone). */
async function demoZone(): Promise<{ name: string; x: number; y: number }> {
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const history = (await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles?siteCode=DMO`, { token })).json()) as { id: string; status: string }[];
	const live = history.find((p) => p.status === 'Published')!;
	const profile = await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles/${live.id}`, { token })).json();
	const levels = (await (await call('GET', `${hosts.main}/api/v1/admin/levels?siteCode=DMO&text=ARR`, { token })).json()).data as {
		id: string;
		code: string;
	}[];
	const arrivals = levels.find((l) => l.code === 'ARR')!;
	const zone = (profile.zones as { name: string; kind: string; levelId: string; polygon: string }[]).find(
		(z) => z.kind === 'Queue' && z.levelId === arrivals.id
	)!;
	const points = zone.polygon.split(',').map((p) => p.trim().split(' ').map(Number));
	const x = points.reduce((s, p) => s + p[0], 0) / points.length;
	const y = points.reduce((s, p) => s + p[1], 0) / points.length;
	return { name: zone.name, x: Math.round(x * 10) / 10, y: Math.round(y * 10) / 10 };
}

/** Signs in with the second factor 20 minutes old, so registering asks for a fresh code. */
async function signInAged(page: Page): Promise<void> {
	await ownAddress(page);
	await page.route('**/api/auth/login', async (route) => {
		const response = await route.fetch();
		if (response.status() !== 200) return route.fulfill({ response });
		const body = await response.json();
		const claims = claimsOf(body.accessToken);
		body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
		return route.fulfill({ response, json: body });
	});
	const account = accounts().webDevices;
	await page.goto('/login');
	await fillSignIn(page, account, totpCode(account.totpSecret!, -1));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
}

test('a duty manager registers a sensor after a step-up; the credential is shown once, works, and is replaced', async ({ page, context }) => {
	test.skip(!developmentSigningKey(), 'needs the run key to age the token');
	test.setTimeout(150_000);
	await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: webUrl });
	const guards = await guardPage(page);
	const zone = await demoZone();
	await earlyInStep();
	await signInAged(page);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Devices' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');

	await page.getByTestId('register-device').click();
	const form = page.getByTestId('register-form');
	await form.getByLabel('Code').fill(code);
	await form.getByLabel('Model').fill('E2E stereo');
	await form.getByLabel('Level').selectOption({ label: 'ARR, Arrivals' });
	await form.getByLabel('Queue zone').selectOption(zone.name);
	await form.getByLabel('x (m)').fill(String(zone.x));
	await form.getByLabel('y (m)').fill(String(zone.y));
	await form.getByRole('button', { name: 'Register a device' }).click();

	// Registering is critical: the second factor is 20 minutes old, so the dialog asks for a fresh code.
	const dialog = page.getByTestId('step-up-dialog');
	await expect(dialog).toBeVisible();
	await fillSecret(page.locator('#step-up-code'), totpCode(accounts().webDevices.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();

	const reveal = page.getByTestId('credential-reveal');
	await expect(reveal).toBeVisible();
	const credential = (await page.getByTestId('credential-value').textContent())!.trim();
	expect(credential.length).toBeGreaterThan(30);
	await page.getByTestId('copy-credential').click();
	await expect.poll(async () => (await page.evaluate(() => navigator.clipboard.readText())) === credential, { message: 'the clipboard holds the credential' }).toBe(true);
	expect((await call('GET', deviceUrl, { token: credential })).status(), 'the shown credential works at Ingest').toBe(200);

	await page.getByTestId('credential-done').click();
	await expect(reveal).toHaveCount(0);
	expect((await page.content()).includes(credential), 'gone from the page once done').toBe(false);
	const panel = page.getByTestId('device-panel');
	await expect(panel.getByTestId('device-state')).toHaveText('Commissioning');
	const prefix = (await panel.getByTestId('credential-prefix').textContent()) ?? '';
	expect(prefix.includes(credential), 'the prefix is not the credential').toBe(false);

	// Never shown again: not after closing, not after a reload, not in the registry's answers.
	await page.reload();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');
	expect((await page.content()).includes(credential), 'not shown after a reload').toBe(false);
	const admin = (await signIn(accounts().webAdmin)).accessToken;
	const registry = await call('GET', `${hosts.main}/api/v1/admin/devices?siteCode=DMO&text=${code}`, { token: admin });
	expect((await registry.text()).includes(credential), "not in the registry's answers").toBe(false);

	// A calibration at 97 percent takes it Online; a model with a payload is shown as text.
	await page.getByTestId('devices-table').getByRole('button', { name: code }).click();
	await panel.getByLabel('Model').fill(xssPayloads[1]);
	await panel.getByTestId('device-details').getByRole('button', { name: 'Save' }).click();
	await expect(panel.getByRole('heading', { level: 2 })).toContainText(xssPayloads[1]);
	await panel.getByLabel('Notes').fill(xssPayloads[0]);
	await panel.getByRole('button', { name: 'Record the calibration' }).click();
	await expect(page.getByText('Calibration recorded: passed.')).toBeVisible();
	await expect(page.getByTestId('devices-table').getByRole('row', { name: new RegExp(code) })).toContainText('Online');
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);

	// A new credential (the step-up is fresh now): shown once, the old one stops at once.
	await page.getByTestId('devices-table').getByRole('button', { name: code }).click();
	await page.getByTestId('rotate').click();
	await page.getByTestId('confirm-rotate').click();
	await expect(reveal).toBeVisible();
	const next = (await page.getByTestId('credential-value').textContent())!.trim();
	expect(next === credential, 'a rotation shows a new credential').toBe(false);
	await page.getByTestId('credential-done').click();
	expect((await page.content()).includes(next), 'gone once done').toBe(false);
	// Ingest keeps a device's credential record cached for up to a minute (as device-auth.spec.ts allows).
	await expect
		.poll(async () => (await call('GET', deviceUrl, { token: credential })).status(), {
			timeout: 75_000,
			intervals: [1_000, 2_000, 5_000],
			message: 'the old credential'
		})
		.toBe(401);
	expect((await call('GET', deviceUrl, { token: next })).status(), 'the new credential').toBe(200);
	allowStatuses(guards, 401, 404);
	await guards.expectClean();
});

test('the coverage plan draws the demo devices at their zones; Arabic mirrors the screen', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().web);
	await page.goto('/devices');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');
	await page.getByRole('region', { name: 'Coverage on the plan' }).getByLabel('Level').selectOption({ label: 'ARR, Arrivals' });
	await expect(page.getByTestId('coverage-plan')).toBeVisible();
	await expect(page.getByTestId('device-marker').filter({ has: page.locator(`text=${code}`) })).toHaveCount(1);
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('الأجهزة');
	allowStatuses(guards, 404);
	await guards.expectClean();
});

/** True when the segment a-b touches the axis-aligned rectangle (Liang and Barsky's clip). */
function touches(rect: { x0: number; y0: number; x1: number; y1: number }, a: [number, number], b: [number, number]): boolean {
	const [dx, dy] = [b[0] - a[0], b[1] - a[1]];
	let [lo, hi] = [0, 1];
	for (const [p, q] of [
		[-dx, a[0] - rect.x0],
		[dx, rect.x1 - a[0]],
		[-dy, a[1] - rect.y0],
		[dy, rect.y1 - a[1]]
	]) {
		if (p === 0) {
			if (q < 0) return false;
			continue;
		}
		const t = q / p;
		if (p < 0) lo = Math.max(lo, t);
		else hi = Math.min(hi, t);
		if (lo > hi) return false;
	}
	return true;
}

test('a duty manager declares the lines a sensor counts; a line out of reach warns; names are text; Arabic mirrors the form (ARV-259e4)', async ({ page }) => {
	const guards = await guardPage(page);
	const zone = await demoZone();
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const found = await (await call('GET', `${hosts.main}/api/v1/admin/devices?siteCode=DMO&text=${code}`, { token })).json();
	const device = (found.data as { id: string; code: string; x: number; y: number; orientationDegrees: number; footprint: { lengthMetres: number; widthMetres: number } }[])
		.find((d) => d.code === code)!;
	expect(device.orientationDegrees, 'registered facing along x').toBe(0);
	const history = (await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles?siteCode=DMO`, { token })).json()) as { id: string; status: string }[];
	const profile = await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles/${history.find((h) => h.status === 'Published')!.id}`, { token })).json();
	const queue = (profile.zones as { id: string; name: string; levelId: string }[]).find((z) => z.name === zone.name)!;
	const lines = (profile.lines as { name: string; role: string; zoneId: string; startX: number; startY: number; endX: number; endY: number }[]).filter(
		(l) => l.zoneId === queue.id && (l.role === 'Entry' || l.role === 'Exit')
	);
	const [hl, hw] = [device.footprint.lengthMetres / 2, device.footprint.widthMetres / 2];
	const rect = { x0: device.x - hl, y0: device.y - hw, x1: device.x + hl, y1: device.y + hw };
	const reached = lines.filter((l) => touches(rect, [l.startX, l.startY], [l.endX, l.endY]));
	const beyond = lines.filter((l) => !reached.includes(l));
	expect(beyond.length, 'the demo zone has a line the footprint at its centre does not reach').toBeGreaterThan(0);

	// The line names reach the screen from the zone profile: one more, named with markup, is added to that answer here, so
	// the screen shows it as text (the server would refuse to save it: it is not a line of the zone).
	await page.route(/\/api\/v1\/admin\/zone-profiles\/[0-9a-f-]{36}$/, async (route) => {
		const response = await route.fetch();
		const body = await response.json();
		body.lines = [...body.lines, { ...lines[0], id: crypto.randomUUID(), name: xssPayloads[1] }];
		await route.fulfill({ response, json: body });
	});
	// The uncovered lines come from the health answer and the warnings from the declaration's answer: markup is added to
	// both here, so the list, the warnings and the toast are shown to be text.
	await page.route(/\/api\/v1\/admin\/devices\/health\?siteCode=DMO/, async (route) => {
		const response = await route.fetch();
		const body = await response.json();
		body.uncoveredLines = [{ siteCode: 'DMO', queueZoneName: xssPayloads[0], lines: [xssPayloads[1], 'Exit Z'] }];
		await route.fulfill({ response, json: body });
	});
	await page.route(new RegExp(`/api/v1/admin/devices/${device.id}/coverage$`), async (route) => {
		const response = await route.fetch();
		if (response.status() !== 200) return route.fulfill({ response });
		const body = await response.json();
		if (body.warnings.length) body.warnings = [xssPayloads[4], ...body.warnings];
		await route.fulfill({ response, json: body });
	});
	await signInThroughUi(page, accounts().web);
	await page.goto('/devices');
	await expect(page.getByTestId('uncovered-lines')).toContainText(`${xssPayloads[0]}: ${xssPayloads[1]}, Exit Z`);
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);
	await page.getByTestId('devices-table').getByRole('button', { name: code }).click();
	const form = page.getByTestId('device-coverage');
	await expect(form.getByRole('heading', { name: 'Lines it counts' })).toBeVisible();
	await expect(form.getByLabel('All lines')).toBeChecked();
	await form.getByLabel('These lines').check();
	await expect(form.getByTestId('coverage-lines').getByRole('checkbox')).toHaveCount(lines.length + 1);
	await expect(form.getByTestId('coverage-lines')).toContainText(xssPayloads[1]);
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);

	// A line the footprint does not reach: saved, with a warning.
	await form.getByLabel(beyond[0].name, { exact: true }).check();
	await form.getByRole('button', { name: 'Save the lines' }).click();
	await expect(form.getByTestId('coverage-warnings')).toContainText(`does not reach line ${beyond[0].name}`);
	await expect(form.getByTestId('coverage-warnings')).toContainText(xssPayloads[4]);
	await expect(page.locator('[data-sonner-toast]').filter({ hasText: xssPayloads[4] })).toBeVisible();
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);
	const saved = await (await call('GET', `${hosts.main}/api/v1/admin/devices/${device.id}`, { token })).json();
	expect([saved.lineCoverage, saved.coveredLines]).toEqual(['Listed', [beyond[0].name]]);
	expect(saved.state, 'a declaration never recommissions').toBe('Online');

	// The markup line is not a line of the zone: refused, and the refusal does not repeat it.
	await form.getByLabel(xssPayloads[1], { exact: true }).check();
	await form.getByRole('button', { name: 'Save the lines' }).click();
	await expect(page.getByText(/entry or exit line of the device's queue zone/)).toBeVisible();
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);
	await form.getByLabel(xssPayloads[1], { exact: true }).uncheck();

	// No lines: saved without a warning.
	await form.getByLabel('No lines').check();
	await form.getByRole('button', { name: 'Save the lines' }).click();
	await expect(page.getByText('Lines saved')).toBeVisible();
	await expect(form.getByTestId('coverage-warnings')).toHaveCount(0);

	// The list names lines a publish dropped (the answer says so here; the integration and API tests publish one).
	await page.route(/\/api\/v1\/admin\/devices\?siteCode=DMO&/, async (route) => {
		const response = await route.fetch();
		const body = await response.json();
		for (const d of body.data) if (d.code === code) d.linesNotInZone = ['Exit Z', xssPayloads[2]];
		await route.fulfill({ response, json: body });
	});
	await page.reload();
	const row = page.getByTestId('devices-table').getByRole('row', { name: new RegExp(code) });
	await expect(row.getByTestId('lines-not-in-zone')).toContainText(`Exit Z, ${xssPayloads[2]}`);

	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await page.getByTestId('devices-table').getByRole('button', { name: code }).click();
	await expect(page.getByTestId('device-coverage').getByRole('heading', { name: 'الخطوط التي يعدّها' })).toBeVisible();
	await expect(page.getByTestId('device-coverage').getByLabel('كل الخطوط')).toBeVisible();
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);

	// Back to every line, for the suites after this one.
	await page.getByTestId('language-toggle').click();
	expect((await call('PUT', `${hosts.main}/api/v1/admin/devices/${device.id}/coverage`, { token, data: { mode: 'All' } })).status()).toBe(200);
	allowStatuses(guards, 400, 404);
	await guards.expectClean();
});

test('a handler station manager has no devices screen, and the server refuses the registry', async ({ page }) => {
	await signInThroughUi(page, accounts().webHandler);
	await expect(page.getByTestId('app-sidebar').getByRole('navigation').getByText('Devices', { exact: true })).toHaveCount(0);
	const token = (await signIn(accounts().webHandler)).accessToken;
	expect((await call('GET', `${hosts.main}/api/v1/admin/devices?siteCode=DMO`, { token })).status()).toBe(403);
	expect((await call('GET', `${hosts.main}/api/v1/admin/devices/health?siteCode=DMO`, { token })).status()).toBe(403);
});

test('a border supervisor sees the registry with its actions but cannot retire (no delete permission)', async ({ page }) => {
	await signInThroughUi(page, accounts().webBorder);
	await page.goto('/devices');
	await expect(page.getByTestId('register-device')).toBeVisible();
	await page.getByTestId('devices-table').getByRole('button', { name: code }).click();
	await expect(page.getByTestId('device-panel')).toBeVisible();
	await expect(page.getByTestId('retire')).toHaveCount(0);
	await expect(page.getByTestId('rotate')).toBeVisible();

	// The server refuses retiring even with a fresh second factor (the role has no delete permission).
	if (developmentSigningKey()) {
		const admin = (await signIn(accounts().webAdmin)).accessToken;
		const found = await call('GET', `${hosts.main}/api/v1/admin/devices?siteCode=DMO&text=${code}`, { token: admin });
		const id = ((await found.json()).data as { id: string; code: string }[]).find((d) => d.code === code)!.id;
		const border = (await signIn(accounts().webBorder)).accessToken;
		const fresh = signToken(headerOf(border), { ...claimsOf(border), amr: ['pwd', 'otp'], auth_time: Math.floor(Date.now() / 1000) }, developmentSigningKey()!);
		expect((await call('POST', `${hosts.main}/api/v1/admin/devices/${id}/retire`, { token: fresh })).status()).toBe(403);
	}
});

test('a registration answered after another site was chosen shows its credential under no other site', async ({ page }) => {
	// ARV-241 (security review round 1, M1): the answer is held, then given as the server gives it for a device at DMO, while the
	// empty site E2SW is shown. The request never reaches the server, so no step-up is asked and no device is made.
	const guards = await guardPage(page);
	const zone = await demoZone();
	const deviceCode = `SW-${Date.now() % 100_000}`;
	await signInThroughUi(page, accounts().switchAdmin);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Devices' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');
	await page.getByTestId('register-device').click();
	const form = page.getByTestId('register-form');
	await form.getByLabel('Code').fill(deviceCode);
	await form.getByLabel('Model').fill('E2E stereo');
	await form.getByLabel('Level').selectOption({ label: 'ARR, Arrivals' });
	await form.getByLabel('Queue zone').selectOption(zone.name);
	await form.getByLabel('x (m)').fill(String(zone.x));
	await form.getByLabel('y (m)').fill(String(zone.y));
	let release = (): void => {};
	const held = new Promise<void>((resolve) => (release = resolve));
	let posted = 0;
	await page.route(/\/api\/v1\/admin\/devices$/, async (route) => {
		if (route.request().method() !== 'POST') return route.continue();
		posted++;
		await held;
		const sent = route.request().postDataJSON() as Record<string, unknown>;
		await route.fulfill({
			status: 201,
			json: {
				device: {
					...sent, id: crypto.randomUUID(), code: deviceCode, siteCode: 'DMO', dialect: 'Canonical', clockSource: 'Device', state: 'Commissioning',
					footprint: { shape: 'Rectangle', lengthMetres: 4, widthMetres: 3, radiusMetres: null, assumed: true },
					credentialPrefix: 'held', credentialIssuedOn: new Date().toISOString(), lastCalibratedOn: null, lastCalibrationPassed: null, retiredOn: null,
					createdOn: new Date().toISOString(), allowedSources: null, clientCertificateSha256: null, mappingName: null
				},
				credential: 'a-held-answer-not-a-credential'
			}
		});
	});
	await form.getByRole('button', { name: 'Register a device' }).click();
	await expect.poll(() => posted, { message: 'the registration was sent' }).toBe(1);
	const chosen = page.waitForResponse(/\/api\/v1\/admin\/devices\?siteCode=E2SW&/);
	await page.locator('#devices-site').selectOption('E2SW');
	await chosen;

	release();

	await expect(page.getByText(`The credential of ${deviceCode} was issued at DMO`)).toBeVisible();
	await expect(page.getByTestId('credential-reveal'), 'no credential under the site shown').toHaveCount(0);
	await expect(page.locator('#devices-site')).toHaveValue('E2SW');
	await expect(page.getByTestId('device-row')).toHaveCount(0);
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('the register form offers only the transports Ingest takes data from', async ({ page }) => {
	// The sensor support model names seven transports; Ingest has HTTPS push and MQTT, and the server refuses the others.
	await signInThroughUi(page, accounts().webBorder);
	await page.goto('/devices');
	await page.getByTestId('register-device').click();
	const transport = page.getByTestId('register-form').locator('#register-transport');
	await expect(transport).toBeVisible();
	expect(await transport.locator('option').evaluateAll((o) => o.map((x) => (x as HTMLOptionElement).value))).toEqual(['HttpsPush', 'Mqtt']);
});
