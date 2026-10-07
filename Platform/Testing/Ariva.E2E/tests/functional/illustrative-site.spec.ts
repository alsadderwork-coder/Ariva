import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { accounts, call, login, signIn, totpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-139a: the illustrative AUH Terminal A arrivals site (AUH-TA). Every screen that works on a site shows the
// persistent "Illustrative, not surveyed" banner while AUH-TA is chosen, in English and in Arabic, and none while the
// fictional DMO is; a passenger display of AUH-TA shows the note in both languages, one of DMO does not. The banner's
// text comes from the dictionaries, never from markup.

test.skip(!databaseAvailable, 'the seeds need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');

const english = {
	title: 'Illustrative, not surveyed.',
	detail: 'AUH-TA is modelled on a real airport from public information only'
};
const arabic = { title: 'توضيحي، غير مُمسوح ميدانيًا.', detail: 'AUH-TA نموذج لمطار حقيقي' };

/** Every screen with a site choice, by path and heading. */
const screens = [
	{ path: '/', heading: 'Live operations' },
	{ path: '/immigration', heading: 'Immigration' },
	{ path: '/zones', heading: 'Zones' },
	{ path: '/devices', heading: 'Devices' },
	{ path: '/topology', heading: 'Topology' },
	{ path: '/alert-rules', heading: 'Alert rules' },
	{ path: '/displays', heading: 'Passenger displays' },
	{ path: '/reports', heading: 'Reports' }
];

/** The demo seeds run in the background after Api.Main starts; AUH-TA follows DMO. */
test.beforeAll(async () => {
	const admin = await signIn(accounts().SystemAdministrator);
	await expect
		.poll(async () => (await call('GET', `${hosts.main}/api/v1/sites/AUH-TA`, { token: admin.accessToken })).status(), { timeout: 90_000, intervals: [1000] })
		.toBe(200);
});

async function chooseSite(page: Page, code: string): Promise<void> {
	const select = page.locator('select:has(option[value="AUH-TA"])').first();
	await expect(select).toBeVisible();
	await select.selectOption(code);
}

test('every site screen shows the banner for AUH-TA in English and Arabic, and none for DMO', async ({ page }) => {
	test.setTimeout(240_000);
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);

	for (const screen of screens) {
		await page.goto(screen.path);
		await expect(page.getByRole('heading', { level: 1 }), screen.path).toContainText(screen.heading);
		const banner = page.getByTestId('illustrative-banner');

		await chooseSite(page, 'DMO');
		await expect(banner, `${screen.path} DMO`).toHaveCount(0);

		await chooseSite(page, 'AUH-TA');
		await expect(banner, `${screen.path} AUH-TA`).toBeVisible();
		await expect(banner).toHaveAttribute('role', 'note');
		await expect(page.getByTestId('illustrative-banner-title')).toHaveText(english.title);
		await expect(banner).toContainText(english.detail);

		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
		await expect(page.getByTestId('illustrative-banner-title'), `${screen.path} Arabic`).toHaveText(arabic.title);
		await expect(banner).toContainText(arabic.detail);
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');

		await chooseSite(page, 'DMO');
		await expect(banner, `${screen.path} DMO in Arabic`).toHaveCount(0);
		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('lang', 'en');
	}

	// Text only: the banner holds no element the dictionaries did not put there.
	await chooseSite(page, 'AUH-TA');
	await expect(page.getByTestId('illustrative-banner').locator('script, img, iframe, a')).toHaveCount(0);
	await guards.expectClean();
});

test('the passenger display of an AUH-TA board shows the note in English and Arabic; a DMO board does not', async ({ browser }) => {
	test.setTimeout(150_000);
	const { userName, password, totpSecret } = accounts().illustrativeWebAdmin;
	let signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	for (let attempt = 0; signedIn.status() === 401 && attempt < 3; attempt++) {
		await new Promise((resolve) => setTimeout(resolve, 31_000 - (Date.now() % 30_000)));
		signedIn = await login(userName, password, undefined, undefined, { code: totpCode(totpSecret!) });
	}
	expect(signedIn.status()).toBe(200);
	const token = (await signedIn.json()).accessToken as string;

	const created: string[] = [];
	try {
		for (const [siteCode, illustrative] of [['AUH-TA', true], ['DMO', false]] as const) {
			const code = `IL-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
			const response = await call('POST', `${hosts.main}/api/v1/admin/displays`, {
				token,
				data: {
					siteCode, code, name: 'Arrivals', location: null, orientation: 'Landscape', languages: ['en', 'ar'], bandMinutes: 5, hysteresisMinutes: 2,
					staleSeconds: 150, entries: [{ zone: 'A-VIS', labels: { en: 'Visitors', ar: 'الزوار' } }],
					fallback: { en: 'Please follow the signs', ar: 'يرجى اتباع اللوحات' }, enabled: true
				}
			});
			expect(response.status(), await response.text()).toBe(201);
			const issued = await response.json();
			created.push(issued.display.id);

			const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
			try {
				const player = await context.newPage();
				const guards = await guardPage(player);
				await player.goto(`/display?code=${code}#key=${encodeURIComponent(issued.credential)}`);
				await expect(player.getByTestId('board-entry')).toHaveCount(1);
				const note = player.getByTestId('board-illustrative-text');
				if (illustrative) {
					await expect(note).toHaveCount(2);
					await expect(note.nth(0)).toHaveText('Illustrative, not surveyed: example data');
					await expect(note.nth(0)).toHaveAttribute('lang', 'en');
					await expect(note.nth(1)).toHaveText('توضيحي، غير مُمسوح ميدانيًا: بيانات تجريبية');
					await expect(note.nth(1)).toHaveAttribute('dir', 'rtl');
				} else {
					await expect(note).toHaveCount(0);
				}
				await guards.expectClean();
			} finally {
				await context.close();
			}
		}
	} finally {
		for (const id of created) await call('DELETE', `${hosts.main}/api/v1/admin/displays/${id}`, { token });
	}
});
