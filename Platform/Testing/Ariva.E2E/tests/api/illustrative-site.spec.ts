import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, login, signIn, totpCode } from '../support/accounts';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-139a: the illustrative AUH Terminal A arrivals site (AUH-TA), seeded beside the fictional DMO in vm-local. The
// sites API says which site is "Illustrative, not surveyed"; the flag is set by the seed alone (CWE-269): no body of the
// admin API can set it on a new site or set or clear it on an existing one, whatever the member is called. Another
// site's caller gets 404 without data, an anonymous one 401, a role without the permission 403; a display of AUH-TA tells
// its player to show the note, a display of DMO does not. The seed's published geometry and plan are readable as usual.

test.skip(!databaseAvailable, 'the seeds need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

const sites = `${hosts.main}/api/v1/sites`;
const adminSites = `${hosts.main}/api/v1/admin/sites`;

interface SiteView {
	code: string;
	name: string;
	illustrative: boolean;
}

/** The demo seeds run in the background after Api.Main starts; AUH-TA follows DMO. */
async function seeded(token: string): Promise<SiteView> {
	let found: SiteView | undefined;
	await expect
		.poll(
			async () => {
				const response = await call('GET', `${sites}/AUH-TA`, { token });
				if (response.status() !== 200) return response.status();
				found = (await response.json()) as SiteView;
				return 200;
			},
			{ timeout: 90_000, intervals: [1000], message: 'the illustrative seed has created AUH-TA' }
		)
		.toBe(200);
	return found!;
}

/** Every spelling of the flag a client might try to bind. */
const flagMembers = ['illustrative', 'Illustrative', 'isIllustrative', 'IsIllustrative', 'is_illustrative'];

test('the sites API flags AUH-TA illustrative and DMO not', async () => {
	const admin = await signIn(accounts().SystemAdministrator);
	const auh = await seeded(admin.accessToken);

	expect(auh).toMatchObject({ code: 'AUH-TA', illustrative: true });
	expect(auh.name).toContain('illustrative');
	const listed = (await (await call('GET', sites, { token: admin.accessToken })).json()) as SiteView[];
	expect(listed.find((s) => s.code === 'AUH-TA')?.illustrative).toBe(true);
	expect(listed.find((s) => s.code === 'DMO')?.illustrative).toBe(false);
	expect(listed.filter((s) => s.code.startsWith('E2E')).every((s) => s.illustrative === false)).toBe(true);
});

test('the admin API cannot set the flag on a new site', async () => {
	const admin = await signIn(accounts().SystemAdministrator);
	const code = `IL-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
	const body: Record<string, unknown> = { code, name: `Illustrative probe ${xssPayloads[1]}` };
	for (const member of flagMembers) body[member] = true;

	const created = await call('POST', adminSites, { token: admin.accessToken, data: body });

	expect(created.status(), await created.text()).toBe(201);
	expect(((await created.json()) as SiteView).illustrative).toBe(false);
	expect(((await (await call('GET', `${sites}/${code}`, { token: admin.accessToken })).json()) as SiteView).illustrative).toBe(false);

	// A rename (the columns the runtime role may update) with the flag beside it changes the name only.
	const renamed = await call('PUT', `${adminSites}/${code}`, { token: admin.accessToken, data: { name: 'Renamed probe', ...Object.fromEntries(flagMembers.map((m) => [m, true])) } });
	expect(renamed.status(), await renamed.text()).toBe(200);
	expect((await renamed.json()) as SiteView).toMatchObject({ name: 'Renamed probe', illustrative: false });
	// The name is text: the probe comes back as the characters sent, never as markup the API rendered.
	expect(((await created.json()) as SiteView).name).toBe(`Illustrative probe ${xssPayloads[1]}`);
});

test('the admin API cannot clear the flag of AUH-TA', async () => {
	const admin = await signIn(accounts().SystemAdministrator);
	const auh = await seeded(admin.accessToken);

	for (const value of [false, null, 0, 'false', '']) {
		const body: Record<string, unknown> = { name: auh.name };
		for (const member of flagMembers) body[member] = value;
		const response = await call('PUT', `${adminSites}/AUH-TA`, { token: admin.accessToken, data: body });
		expect(response.status(), JSON.stringify(value)).toBe(200);
		expect(((await response.json()) as SiteView).illustrative, JSON.stringify(value)).toBe(true);
	}
	expect((await seeded(admin.accessToken)).illustrative).toBe(true);
});

test('AUH-TA answers 401 without a token, 403 to a role that cannot edit sites and 404 to another site', async () => {
	const border = await signIn(accounts().BorderShiftSupervisor);
	const admin = await signIn(accounts().SystemAdministrator);
	await seeded(admin.accessToken);

	expect((await call('GET', `${sites}/AUH-TA`)).status()).toBe(401);
	expect((await call('GET', sites)).status()).toBe(401);
	expect((await call('PUT', `${adminSites}/AUH-TA`, { data: { name: 'x', illustrative: false } })).status()).toBe(401);
	expect((await call('PUT', `${adminSites}/AUH-TA`, { token: border.accessToken, data: { name: 'x', illustrative: false } })).status()).toBe(403);

	// e2e.border holds E2E1 only: AUH-TA is not its site, so it answers exactly like a site that does not exist.
	for (const code of ['AUH-TA', 'auh-ta', 'NOPE-TA']) {
		const response = await call('GET', `${sites}/${code}`, { token: border.accessToken });
		expect(response.status(), code).toBe(404);
		const text = await response.text();
		expect(text, code).not.toContain('illustrative');
		expect(text, code).not.toContain('"name"');
	}
	expect(((await (await call('GET', sites, { token: border.accessToken })).json()) as SiteView[]).map((s) => s.code)).not.toContain('AUH-TA');
	expect((await seeded(admin.accessToken)).illustrative).toBe(true);
});

test('attack payloads in the site code answer 4xx without markup or data', async () => {
	const admin = await signIn(accounts().SystemAdministrator);
	for (const payload of [...sqlInjectionPayloads, ...xssPayloads, "AUH-TA' OR 1=1", 'AUH-TA%00', 'A'.repeat(5000)]) {
		const response = await call('GET', `${sites}/${encodeURIComponent(payload)}`, { token: admin.accessToken });
		expect(response.status(), payload).toBeGreaterThanOrEqual(400);
		expect(response.status(), payload).toBeLessThan(500);
		const text = await response.text();
		for (const fragment of markupFragments) expect(text, payload).not.toContain(fragment);
		expect(text, payload).not.toContain('illustrative');
	}
	// A flag of the wrong type in a body is ignored or refused, never a 5xx and never applied.
	for (const value of [{ nested: true }, [true], 'true'.repeat(1000), 1e308]) {
		const response = await call('PUT', `${adminSites}/AUH-TA`, { token: admin.accessToken, data: { name: (await seeded(admin.accessToken)).name, illustrative: value } });
		expect(response.status(), JSON.stringify(value).slice(0, 40)).toBeLessThan(500);
	}
	expect((await seeded(admin.accessToken)).illustrative).toBe(true);
});

test("the seeded geometry and plan are AUH-TA's own and readable through the API", async () => {
	const admin = await signIn(accounts().SystemAdministrator);
	await seeded(admin.accessToken);

	let profiles: { status: string; version: number; zoneCount: number; lineCount: number; publishedBy: string }[] = [];
	await expect
		.poll(async () => {
			profiles = (await (await call('GET', `${hosts.main}/api/v1/admin/zone-profiles?siteCode=AUH-TA`, { token: admin.accessToken })).json()) as typeof profiles;
			return profiles.length;
		}, { timeout: 60_000 })
		.toBeGreaterThan(0);
	expect(profiles[0]).toMatchObject({ status: 'Published', version: 1, zoneCount: 86, lineCount: 18, publishedBy: 'demo-seed' });

	const plans = (await (await call('GET', `${hosts.main}/api/v1/admin/floor-plans?siteCode=AUH-TA`, { token: admin.accessToken })).json()) as {
		contentType: string;
		widthPixels: number;
		heightPixels: number;
		metresPerPixel: number;
		originalFileName: string;
	}[];
	expect(plans).toHaveLength(1);
	expect(plans[0]).toMatchObject({ contentType: 'image/svg+xml', widthPixels: 2000, heightPixels: 1500, metresPerPixel: 0.1 });

	const border = await signIn(accounts().BorderShiftSupervisor);
	for (const url of [`${hosts.main}/api/v1/admin/zone-profiles?siteCode=AUH-TA`, `${hosts.main}/api/v1/admin/floor-plans?siteCode=AUH-TA`]) {
		const response = await call('GET', url, { token: border.accessToken });
		expect([403, 404], url).toContain(response.status());
		expect(await response.text(), url).not.toContain('AUH Terminal A');
	}
});

test("an AUH-TA display tells its player to show the note; a DMO display does not", async () => {
	test.setTimeout(120_000);
	const { userName, password, totpSecret } = accounts().illustrativeAdmin;
	let signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	for (let attempt = 0; signedIn.status() === 401 && attempt < 3; attempt++) {
		await new Promise((resolve) => setTimeout(resolve, 31_000 - (Date.now() % 30_000)));
		signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	}
	expect(signedIn.status()).toBe(200);
	const token = (await signedIn.json()).accessToken as string;
	await seeded(token);

	const created: { id: string; code: string; credential: string }[] = [];
	try {
		for (const [siteCode, zone] of [['AUH-TA', 'A-VIS'], ['DMO', 'A-VIS']]) {
			const code = `IL-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
			const response = await call('POST', `${hosts.main}/api/v1/admin/displays`, {
				token,
				data: {
					siteCode, code, name: 'Arrivals', location: null, orientation: 'Landscape', languages: ['en', 'ar'], bandMinutes: 5, hysteresisMinutes: 2,
					staleSeconds: 150, entries: [{ zone, labels: { en: 'Visitors', ar: 'الزوار' } }], fallback: { en: 'Please follow the signs', ar: 'يرجى اتباع اللوحات' }, enabled: true
				}
			});
			expect(response.status(), await response.text()).toBe(201);
			const issued = await response.json();
			created.push({ id: issued.display.id, code, credential: issued.credential });
		}

		const [auh, dmo] = await Promise.all(
			created.map(async ({ code, credential }) => {
				const response = await call('GET', `${hosts.main}/api/v1/display/board?code=${code}`, { headers: { 'X-Ariva-Display-Key': credential } });
				expect(response.status()).toBe(200);
				return (await response.json()) as { illustrative: boolean };
			})
		);
		expect(auh.illustrative).toBe(true);
		expect(dmo.illustrative).toBe(false);
		// The board itself stays device-authenticated: no credential, or another display's, is 401.
		expect((await call('GET', `${hosts.main}/api/v1/display/board?code=${created[0].code}`)).status()).toBe(401);
		expect((await call('GET', `${hosts.main}/api/v1/display/board?code=${created[0].code}`, { headers: { 'X-Ariva-Display-Key': created[1].credential } })).status()).toBe(401);
		expect((await call('GET', `${hosts.main}/api/v1/display/board?code=${created[0].code}`, { token })).status()).toBe(401);
	} finally {
		for (const { id } of created) await call('DELETE', `${hosts.main}/api/v1/admin/displays/${id}`, { token });
	}
});
