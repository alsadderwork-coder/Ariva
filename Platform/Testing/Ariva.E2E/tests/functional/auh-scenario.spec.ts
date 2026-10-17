import { expect, test } from '@playwright/test';
import { createClient } from 'redis';
import { accounts, call, signIn } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-139b: the AUH-TA live screen at the scripted breach of the AUH Terminal A arrivals evening (seed 9304). The
// simulator's AUH-TA day gives the minute 18:12, when the visitors' nowcast passes 15 minutes during the evening shift
// handover; Ariva.Api.Stream is not part of the E2E run (as in live-operations.spec), so this suite plants what Stream
// keeps for that minute in Redis, from the simulator's own figures: the visitors' queue over target, the citizens' queue
// within it. The live screen of AUH-TA shows the breach, and the illustrative banner stays on above it, in English and
// in Arabic.

test.skip(!databaseAvailable, 'the live screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true) and the seeds');

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
const operator = { Authorization: `Bearer ${process.env.ARIVA_E2E_SIMULATION_KEY ?? ''}` };
const breachMinute = 1092; // 18:12

type QueueState = { queue: string; length: number; throughput: number; nowcastMinutes: number | null };

test('the AUH-TA live screen shows the scripted 18:12 breach under the illustrative banner', async ({ page }) => {
	test.skip(!redisUrl, "the live hub needs the run's Redis (ARIVA_E2E_REDIS_URL)");
	test.setTimeout(120_000);
	const guards = await guardPage(page);

	// The seeds run in the background after Api.Main starts; AUH-TA follows DMO.
	const admin = await signIn(accounts().SystemAdministrator);
	await expect
		.poll(async () => (await call('GET', `${hosts.main}/api/v1/sites/AUH-TA`, { token: admin.accessToken })).status(), { timeout: 90_000, intervals: [1000] })
		.toBe(200);

	const states = (await (await call('GET', `${hosts.simulation}/api/v1/simulation/scenario/queues?minute=${breachMinute}&site=AUH-TA`, { headers: operator })).json()) as QueueState[];
	const visitors = states.find((s) => s.queue === 'A-VIS')!;
	const citizens = states.find((s) => s.queue === 'A-CIT')!;
	expect(visitors.nowcastMinutes, 'the scenario breaches at 18:12').toBeGreaterThan(15);
	expect(citizens.nowcastMinutes ?? 0).toBeLessThan(10);
	const before = (await (await call('GET', `${hosts.simulation}/api/v1/simulation/scenario/queues/A-VIS?minute=${breachMinute - 1}&site=AUH-TA`, { headers: operator })).json()) as QueueState;
	expect(before.nowcastMinutes ?? 0, 'and not a minute earlier').toBeLessThanOrEqual(15);

	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		const minute = new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000);
		const snapshot = (zone: string, s: QueueState) => ({
			zoneKey: `AUH-TA/${zone}`,
			minuteUtc: minute.toISOString(),
			queueLength: Math.round(s.length),
			lengthFromSensors: true,
			lengthDegraded: false,
			nowcastMinutes: s.nowcastMinutes === null ? null : Math.round(s.nowcastMinutes * 10) / 10,
			throughputPerMinute: Math.round(s.throughput * 10) / 10,
			noService: null,
			nowcastDegraded: false,
			publishedUtc: new Date().toISOString()
		});
		const planted = [snapshot('A-VIS', visitors), snapshot('A-CIT', citizens)];
		for (const s of planted) await redis.set(`${instance}live:zone:${s.zoneKey}`, JSON.stringify(s), { EX: 3600 });
		const shown = String(planted[0].nowcastMinutes);

		await signInThroughUi(page, accounts().webAdmin);
		await expect(page.getByRole('heading', { level: 1 })).toContainText('Live operations');
		const select = page.locator('select:has(option[value="AUH-TA"])').first();
		await expect(select).toBeVisible();
		await select.selectOption('AUH-TA');

		const banner = page.getByTestId('illustrative-banner');
		await expect(banner).toBeVisible();
		await expect(page.getByTestId('illustrative-banner-title')).toHaveText('Illustrative, not surveyed.');
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');

		const row = page.locator('[data-testid="zone-row"][data-zone="A-VIS"]');
		await expect
			.poll(
				async () => {
					for (const s of planted) await redis.publish(`${instance}live:zones`, JSON.stringify({ ...s, publishedUtc: new Date().toISOString() }));
					return (await row.textContent()) ?? '';
				},
				{ timeout: 30_000, intervals: [500, 1000, 2000] }
			)
			.toContain(shown);
		await expect(row).toContainText('Over target');
		await expect(page.locator('[data-testid="zone-row"][data-zone="A-CIT"]')).toContainText('Within target');
		await expect(page.locator('[data-testid="zone-row"][data-zone="CI-C"]'), 'no DMO zone on the AUH-TA screen').toHaveCount(0);

		// The banner stays above the breach in Arabic too.
		await page.getByTestId('language-toggle').click();
		await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
		await expect(page.getByTestId('illustrative-banner-title')).toHaveText('توضيحي، غير مُمسوح ميدانيًا.');
		await expect(row).toBeVisible();
		await page.getByTestId('language-toggle').click();
		await guards.expectClean();
	} finally {
		await redis.del(['A-VIS', 'A-CIT'].map((zone) => `${instance}live:zone:AUH-TA/${zone}`));
		await redis.quit();
	}
});
