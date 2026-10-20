import crypto from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { accounts, call, login, signIn, unusedTotpCode } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts, webUrl } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-141: instruction slides. A border supervisor of DMO manages them on the Display instructions screen (every text
// field carries markup, shown as text in the list and the preview; Arabic previews right to left), orders, retires and
// restores them. A split display rotates the slides that apply to it in its second region, each for its own time, pins
// one under the waits in either layout, and falls back to the default instructions when none applies. The test display
// shows a departures queue, which the seeded arrivals slides do not concern, so only this spec's slides apply to it.

test.skip(!databaseAvailable, 'the slides need the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const slidesApi = `${hosts.main}/api/v1/admin/display-slides`;
const displaysApi = `${hosts.main}/api/v1/admin/displays`;
const code = `SLD-${crypto.randomBytes(2).toString('hex').toUpperCase()}`;
const injected = 'img[src="x"], [onerror], [onload], iframe, svg[onload]';
let border: string;
let display: { id: string; path: string } | null = null;
const made: string[] = [];

async function createDisplay(): Promise<{ id: string; path: string }> {
	// Creating a display issues its credential, a critical action: a token with a fresh second factor.
	const account = accounts().slidesDisplays;
	const signedIn = await login(account.userName, account.password, undefined, undefined, { code: await unusedTotpCode(account.totpSecret!) });
	expect(signedIn.status()).toBe(200);
	const token = (await signedIn.json()).accessToken;
	const created = await call('POST', displaysApi, {
		token,
		data: {
			siteCode: 'DMO', code, name: 'Departures slides', location: null, orientation: 'Landscape', languages: ['ar', 'en'],
			bandMinutes: 5, hysteresisMinutes: 1, staleSeconds: 600,
			entries: [{ zone: 'D-CIT', labels: { ar: 'المواطنون', en: 'Citizens' } }],
			fallback: { ar: 'يرجى اتباع اللافتات', en: 'Please follow the signs' },
			layout: 'Split', queueShare: 60, secondRegion: 'Instructions'
		}
	});
	await expectStatus(created, 201);
	const body = await created.json();
	// Absolute (ARV-152): a navigation's step keeps a relative address whole, fragment and key included; an absolute one
	// without its fragment.
	return { id: body.display.id, path: `${webUrl}/display?code=${code}#key=${encodeURIComponent(body.credential)}` };
}

async function makeSlide(data: Record<string, unknown>): Promise<{ id: string; position: number }> {
	const response = await call('POST', slidesApi, {
		token: border,
		data: { siteCode: 'DMO', scope: 'Display', targets: [code], durationSeconds: 5, pinned: false, ...data }
	});
	await expectStatus(response, 201);
	const slide = await response.json();
	made.push(slide.id);
	return slide;
}

async function changeDisplay(changes: Record<string, unknown>): Promise<void> {
	const current = await (await call('GET', `${displaysApi}/${display!.id}`, { token: border })).json();
	const request = {
		siteCode: 'DMO', code, name: current.name, location: current.location, orientation: current.orientation, languages: current.languages,
		bandMinutes: current.bandMinutes, hysteresisMinutes: current.hysteresisMinutes, staleSeconds: current.staleSeconds, entries: current.entries,
		fallback: current.fallback, enabled: current.enabled, layout: current.layout, queueShare: current.queueShare, secondRegion: current.secondRegion, ...changes
	};
	const response = await call('PUT', `${displaysApi}/${display!.id}`, { token: border, data: request });
	await expectStatus(response, 200);
}

async function openSlides(page: Page): Promise<void> {
	await signInThroughUi(page, accounts().webBorder);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Display instructions' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Display instructions');
}

test.beforeAll(async () => {
	border = (await signIn(accounts().webBorder)).accessToken;
	display = await createDisplay();
});

test.afterAll(async () => {
	for (const id of made) await call('POST', `${slidesApi}/${id}/retire`, { token: border });
	if (display) await call('DELETE', `${displaysApi}/${display.id}`, { token: border });
});

test('a border supervisor writes a slide with markup in every field: the preview and the list show it as text, Arabic right to left', async ({ page }) => {
	test.setTimeout(120_000);
	// Unique to this run and attempt, so the list row is this slide's whatever earlier attempts left (retired) in the list.
	const title = `${xssPayloads[0]} ${code}-${crypto.randomBytes(2).toString('hex')}`;
	const guards = await guardPage(page);
	await openSlides(page);
	await page.getByTestId('add-slide').click();
	const form = page.getByTestId('slide-form');
	await form.getByLabel('Shown on').selectOption('Display');
	await form.getByTestId('slide-targets').getByRole('checkbox', { name: code }).check();
	await form.getByLabel('Icon').selectOption('Passport');
	await form.getByLabel('Seconds on screen').fill('7');
	await form.getByLabel('Title (English)').fill(title);
	await form.getByLabel('Text (English, optional)').fill(xssPayloads[1]);
	await form.getByLabel('Title (Arabic)').fill('افتح جواز سفرك');
	await form.getByLabel('Text (Arabic, optional)').fill(xssPayloads[2]);
	await form.getByLabel('Title (Portuguese)').fill(xssPayloads[4]);

	// The preview: one panel per written language, as a display shows it.
	const english = form.locator('[data-testid="slide-preview"][data-language="en"]');
	const arabic = form.locator('[data-testid="slide-preview"][data-language="ar"]');
	await expect(form.getByTestId('slide-preview')).toHaveCount(3);
	await expect(english.getByTestId('slide-text')).toHaveAttribute('dir', 'ltr');
	await expect(english).toContainText(xssPayloads[0]);
	await expect(english).toContainText(xssPayloads[1]);
	await expect(arabic.getByTestId('slide-text')).toHaveAttribute('dir', 'rtl');
	await expect(arabic.getByTestId('slide-text')).toHaveAttribute('lang', 'ar');
	await expect(arabic).toContainText('افتح جواز سفرك');
	await expect(page.locator(injected)).toHaveCount(0);

	await form.getByTestId('save-slide').click();
	await expect(form).toHaveCount(0);
	const row = page.getByTestId('slide-row').filter({ hasText: title });
	await expect(row).toHaveCount(1);
	await expect(row.getByTestId('slide-scope')).toHaveText(`Displays ${code}`);
	await expect(row).toContainText('7 s on screen');
	const position = Number(await row.getAttribute('data-position'));
	const listed = await (await call('GET', `${slidesApi}?siteCode=DMO`, { token: border })).json();
	const saved = listed.find((s: { position: number }) => s.position === position);
	made.push(saved.id);
	expect(saved.texts).toEqual({
		en: { title, body: xssPayloads[1] },
		ar: { title: 'افتح جواز سفرك', body: xssPayloads[2] },
		pt: { title: xssPayloads[4], body: null }
	});

	// Changed, moved up one place, retired and restored.
	await row.getByTestId('edit-slide').click();
	await form.getByLabel('Title (Arabic)').fill('جهّز جواز سفرك');
	await form.getByTestId('save-slide').click();
	await expect(form).toHaveCount(0);
	await row.getByTestId('slide-up').click();
	await expect(row).toHaveAttribute('data-position', String(position - 1));
	await row.getByTestId('retire-slide').click();
	await expect(row).toHaveAttribute('data-status', 'Retired');
	await row.getByTestId('restore-slide').click();
	await expect(row).toHaveAttribute('data-status', 'Active');
	await expect(page.locator(injected)).toHaveCount(0);
	await guards.expectClean();
	// The slide stays out of the player tests' way.
	await call('POST', `${slidesApi}/${saved.id}/retire`, { token: border });

	// The screen in Arabic mirrors.
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('تعليمات الشاشات');
});

test('a split display rotates its slides in the second region, pins one under the waits, and falls back without them', async ({ browser }) => {
	test.setTimeout(150_000);
	const first = await makeSlide({ icon: 'Passport', texts: { en: { title: 'Rotation one', body: null }, ar: { title: 'الأولى', body: null } } });
	const second = await makeSlide({ icon: 'Baggage', texts: { en: { title: 'Rotation two', body: xssPayloads[1] }, ar: { title: 'الثانية', body: null } } });
	await makeSlide({
		icon: 'Accessibility', pinned: true,
		texts: { en: { title: xssPayloads[0], body: 'Ask staff for help' }, ar: { title: 'اطلب المساعدة', body: null }, sw: { title: 'Not on this display', body: null } }
	});

	const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
	const page = await context.newPage();
	const guards = await guardPage(page);
	try {
		await page.goto(display!.path);
		await expect(page.getByTestId('board')).toHaveAttribute('data-layout', 'Split');
		const region = page.getByTestId('board-second');
		await expect(region).toHaveAttribute('data-state', 'slides');
		await expect(region).toHaveAttribute('data-count', '2', { timeout: 20_000 });
		const shown = page.getByTestId('board-slide');
		// Each slide stays its 5 seconds, then the next one, in the site's order, then the first again.
		await expect(shown).toHaveAttribute('data-position', String(first.position));
		await expect(shown).toHaveAttribute('data-icon', 'Passport');
		await expect(shown).toHaveAttribute('data-position', String(second.position), { timeout: 8_000 });
		await expect(shown).toContainText('Rotation two');
		await expect(shown).toContainText(xssPayloads[1]);
		await expect(shown.locator('[lang="ar"]')).toHaveAttribute('dir', 'rtl');
		await expect(shown).toHaveAttribute('data-position', String(first.position), { timeout: 8_000 });

		// The pinned slide under the waits, inside the queue region, in the display's languages only, as text.
		const queue = page.getByTestId('board-queue');
		const pinned = queue.getByTestId('board-pinned');
		await expect(pinned).toContainText(xssPayloads[0]);
		await expect(pinned).toContainText('اطلب المساعدة');
		await expect(pinned).not.toContainText('Not on this display');
		await expect(pinned.locator('[data-icon="Accessibility"]')).toHaveCount(1);
		const queueBox = (await queue.boundingBox())!;
		expect(queueBox.width, 'the queue region keeps its share').toBeGreaterThanOrEqual(1920 * 0.6 - 1);
		const pinnedBox = (await pinned.boundingBox())!;
		expect(pinnedBox.y + pinnedBox.height, 'the pinned slide is on the screen').toBeLessThanOrEqual(1080);
		await expect(page.locator(injected)).toHaveCount(0);
		await page.screenshot({ path: test.info().outputPath('slides-split.png') });

		// The full layout keeps the pinned slide under the waits.
		await changeDisplay({ layout: 'Full' });
		await expect(page.getByTestId('board')).toHaveAttribute('data-layout', 'Full', { timeout: 25_000 });
		await expect(page.getByTestId('board-pinned')).toContainText(xssPayloads[0]);
		await expect(page.getByTestId('board-second-region')).toHaveCount(0);

		// Without slides the split display shows the default instructions again, at its next poll.
		await changeDisplay({ layout: 'Split' });
		for (const id of made) await call('POST', `${slidesApi}/${id}/retire`, { token: border });
		await expect(page.getByTestId('board-second')).toHaveAttribute('data-state', 'ready', { timeout: 25_000 });
		await expect(page.getByTestId('board-instructions').first()).toBeVisible();
		await expect(page.getByTestId('board-pinned')).toHaveCount(0);
		await guards.expectClean();
	} finally {
		await context.close();
	}
});

test('an icon name the player does not know draws the information icon, inherited names included', async ({ browser }) => {
	test.setTimeout(120_000);
	await makeSlide({ icon: 'Passport', pinned: true, texts: { en: { title: 'Pinned probe', body: null } } });
	await makeSlide({ icon: 'Baggage', texts: { en: { title: 'Rotating probe', body: null } } });
	await changeDisplay({ layout: 'Split' });
	const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
	const page = await context.newPage();
	const guards = await guardPage(page);
	try {
		// Each board answer names another icon the player does not have; the server never sends these (exact enum names).
		const names = ['constructor', '__proto__', 'hasOwnProperty', 'Skull'];
		let answers = 0;
		await page.route('**/api/v1/display/board?*', async (route) => {
			const response = await route.fetch();
			const body = await response.json();
			const name = names[Math.min(answers++, names.length - 1)];
			if (body.pinnedSlide) body.pinnedSlide.icon = name;
			for (const slide of body.slides ?? []) slide.icon = name;
			await route.fulfill({ response, json: body });
		});
		await page.goto(display!.path);
		for (const name of names) {
			await expect(page.locator(`[data-testid="board-pinned"] svg[data-icon="${name}"]`), name).toHaveClass(/lucide-info/, { timeout: 25_000 });
			await expect(page.locator(`[data-testid="board-slide"] svg[data-icon="${name}"]`), name).toHaveClass(/lucide-info/);
		}
		await expect(page.getByTestId('board-entry')).toHaveCount(1);
		await guards.expectClean();
	} finally {
		await context.close();
	}
});

test('a handler station manager has no slides screen', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webHandler);
	await expect(page.getByTestId('app-sidebar').getByRole('link', { name: 'Display instructions' })).toHaveCount(0);
	await page.goto('/display-slides');
	await expect(page.getByTestId('no-access')).toBeVisible();
	await guards.expectClean();
});
