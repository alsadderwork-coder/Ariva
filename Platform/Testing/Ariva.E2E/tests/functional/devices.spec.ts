import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, totpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, earlyInStep, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';

// ARV-054: the devices screen on the registry, calibration and health API (ARV-021 to ARV-025). A terminal duty manager
// registers a sensor at the demo airport after a step-up; its credential is shown once with a copy action, works at the
// Ingest host, and is never shown again; a calibration takes it Online; a new credential replaces the old one at once.
// Handler station managers have no devices screen and the server refuses them. Text from the registry is text.

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
	await page.locator('#step-up-code').fill(totpCode(accounts().webDevices.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();

	const reveal = page.getByTestId('credential-reveal');
	await expect(reveal).toBeVisible();
	const credential = (await page.getByTestId('credential-value').textContent())!.trim();
	expect(credential.length).toBeGreaterThan(30);
	await page.getByTestId('copy-credential').click();
	await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(credential);
	expect((await call('GET', deviceUrl, { token: credential })).status(), 'the shown credential works at Ingest').toBe(200);

	await page.getByTestId('credential-done').click();
	await expect(reveal).toHaveCount(0);
	expect(await page.content(), 'gone from the page once done').not.toContain(credential);
	const panel = page.getByTestId('device-panel');
	await expect(panel.getByTestId('device-state')).toHaveText('Commissioning');
	const prefix = (await panel.getByTestId('credential-prefix').textContent()) ?? '';
	expect(prefix).not.toContain(credential);

	// Never shown again: not after closing, not after a reload, not in the registry's answers.
	await page.reload();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Devices');
	expect(await page.content()).not.toContain(credential);
	const admin = (await signIn(accounts().webAdmin)).accessToken;
	const registry = await call('GET', `${hosts.main}/api/v1/admin/devices?siteCode=DMO&text=${code}`, { token: admin });
	expect(await registry.text()).not.toContain(credential);

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
	expect(next).not.toBe(credential);
	await page.getByTestId('credential-done').click();
	expect(await page.content()).not.toContain(next);
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

test('the register form offers only the transports Ingest takes data from', async ({ page }) => {
	// The sensor support model names seven transports; Ingest has HTTPS push and MQTT, and the server refuses the others.
	await signInThroughUi(page, accounts().webBorder);
	await page.goto('/devices');
	await page.getByTestId('register-device').click();
	const transport = page.getByTestId('register-form').locator('#register-transport');
	await expect(transport).toBeVisible();
	expect(await transport.locator('option').evaluateAll((o) => o.map((x) => (x as HTMLOptionElement).value))).toEqual(['HttpsPush', 'Mqtt']);
});
