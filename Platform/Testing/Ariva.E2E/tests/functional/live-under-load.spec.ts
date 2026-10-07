import { expect, test } from '@playwright/test';
import { createClient } from 'redis';
import { accounts } from '../support/accounts';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { provisionLoadSite, screenSessions } from '../support/load-site';
import { allowStatuses, databaseAvailable, signInThroughUi } from '../support/web-auth';

// ARV-071: the live operations screen while the hub carries forty other screens on the same zone and the zone's
// snapshot changes five times a second (Stream announces once a minute; this is the burst a replay or a restart
// would send). The screen ends on the last snapshot and stays connected, and every other screen received the
// snapshots. A site of its own, so no other suite's zones move. Ariva.Api.Stream is not in the E2E run: the tests
// write and announce the snapshots in Redis as Stream does.
// The screen opens on the demo airport and the tests then choose the load site. When that choice landed while the
// demo airport was still loading, the screen used to watch nothing on the load site (2026-10-07): the first load keyed
// the demo airport's zones with the load site's code, and a watch made while the hub connection was still opening was
// overtaken by the earlier one. The last two tests hold each of those moments open and prove the screen still ends on
// the site chosen.

test.skip(!databaseAvailable, 'the live screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
// One load site for the file, provisioned once (its administrator's TOTP code is accepted once per step).
test.describe.configure({ mode: 'serial' });

const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';
const separator = '\u001e';
const others = 40;
const announcements = 50;

let loadSite: { site: string; zone: string } | undefined;

test.beforeAll(async () => {
	if (!redisUrl) return;
	const { site, zone } = await provisionLoadSite(accounts().loadWebAdmin, 'LW');
	loadSite = { site, zone };
});

/** A snapshot as Stream writes it, for the minute before this one. */
function snapshotOf(zoneKey: string, queueLength: number, nowcastMinutes: number) {
	return {
		zoneKey,
		minuteUtc: new Date(Math.floor(Date.now() / 60_000) * 60_000 - 60_000).toISOString(),
		queueLength,
		lengthFromSensors: true,
		lengthDegraded: false,
		nowcastMinutes,
		throughputPerMinute: 6,
		noService: null,
		nowcastDegraded: false,
		publishedUtc: new Date().toISOString()
	};
}

/** Keeps the zone's snapshot and announces it, as Stream does after a checkpoint. */
async function publish(redis: ReturnType<typeof createClient>, snapshot: ReturnType<typeof snapshotOf>): Promise<void> {
	await redis.set(`${instance}live:zone:${snapshot.zoneKey}`, JSON.stringify(snapshot), { EX: 3600 });
	await redis.publish(`${instance}live:zones`, JSON.stringify(snapshot));
}

interface Screen {
	socket: WebSocket;
	received: number;
	last: number | null;
}

/** A dashboard on the live hub, as the web app speaks it (SignalR JSON protocol over a WebSocket), joined to the zone. */
function screen(token: string, zoneKey: string): Promise<Screen> {
	const url = `${hosts.main.replace(/^http/, 'ws')}/hubs/live?access_token=${encodeURIComponent(token)}`;
	return new Promise((resolve, reject) => {
		const socket = new WebSocket(url);
		const state: Screen = { socket, received: 0, last: null };
		let joined = false;
		const timer = setTimeout(() => reject(new Error('join timed out')), 15_000);
		socket.onopen = () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + separator);
		socket.onerror = () => reject(new Error('socket error'));
		socket.onmessage = (event) => {
			for (const frame of String(event.data).split(separator).filter(Boolean)) {
				const message = JSON.parse(frame);
				if (!joined && Object.keys(message).length === 0) {
					socket.send(JSON.stringify({ type: 1, invocationId: '1', target: 'JoinZone', arguments: [zoneKey] }) + separator);
				} else if (message.type === 3 && message.invocationId === '1') {
					clearTimeout(timer);
					if (message.error) reject(new Error(message.error));
					joined = true;
					resolve(state);
				} else if (message.type === 1 && message.target === 'zone') {
					state.received++;
					state.last = message.arguments[0].queueLength;
				}
			}
		};
	});
}

test('the live screen keeps up with a burst of snapshots while forty other screens watch the zone', async ({ page }) => {
	test.skip(!redisUrl, "the live hub needs the run's Redis (ARIVA_E2E_REDIS_URL)");
	test.setTimeout(180_000);
	const guards = await guardPage(page);
	const { site, zone } = loadSite!;
	const zoneKey = `${site}/${zone}`;
	const tokens = await screenSessions(accounts().loadScreen, others);
	const screens = await Promise.all(Array.from({ length: others }, (_, i) => screen(tokens[i % tokens.length], zoneKey)));
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await signInThroughUi(page, accounts().loadScreen);
		await page.locator('#live-site').selectOption(site);
		const row = page.locator(`[data-testid="zone-row"][data-zone="${zone}"]`);
		await expect(row).toBeVisible();
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');

		for (let i = 1; i <= announcements; i++) {
			const last = i === announcements;
			await publish(redis, snapshotOf(zoneKey, last ? 88 : 20 + i, last ? 12.4 : 5 + i / 10));
			await new Promise((r) => setTimeout(r, 200));
		}

		// The screen settles on the last snapshot, still connected; every other screen got it and nearly all before it.
		await expect(row).toContainText('12.4', { timeout: 15_000 });
		await expect(row).toContainText('88');
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
		await expect.poll(() => screens.filter((s) => s.last === 88).length, { timeout: 15_000 }).toBe(others);
		const received = screens.reduce((sum, s) => sum + s.received, 0);
		expect(received).toBeGreaterThanOrEqual(0.95 * announcements * others);
		expect(screens.every((s) => s.socket.readyState === WebSocket.OPEN)).toBe(true);
		allowStatuses(guards, 404);
		await guards.expectClean();
	} finally {
		for (const s of screens) s.socket.close();
		await redis.quit();
	}
});

test('a site chosen while the live connection is still opening is the site the screen watches', async ({ page }) => {
	test.skip(!redisUrl, "the live hub needs the run's Redis (ARIVA_E2E_REDIS_URL)");
	const guards = await guardPage(page);
	const { site, zone } = loadSite!;
	const zoneKey = `${site}/${zone}`;
	// The first negotiation (the demo airport's watch) waits until the load site's zones are on screen and its own
	// watch has been asked for.
	let open = () => {};
	const opening = new Promise<void>((resolve) => (open = resolve));
	let negotiations = 0;
	await page.route(
		(url) => url.pathname.endsWith('/hubs/live/negotiate'),
		async (route) => {
			if (negotiations++ === 0) await opening;
			await route.continue();
		}
	);
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await publish(redis, snapshotOf(zoneKey, 31, 6.1));
		await signInThroughUi(page, accounts().loadScreen);
		await expect.poll(() => negotiations).toBe(1);
		await page.locator('#live-site').selectOption(site);
		const row = page.locator(`[data-testid="zone-row"][data-zone="${zone}"]`);
		await expect(row).toBeVisible();
		open();

		// The connection opens on the zones asked for last: the kept snapshot on joining, then each new one.
		await expect(row).toContainText('6.1');
		await publish(redis, snapshotOf(zoneKey, 32, 6.2));
		await expect(row).toContainText('6.2');
		await expect(page.getByTestId('zone-row')).toHaveCount(1);
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
		allowStatuses(guards, 404);
		await guards.expectClean();
	} finally {
		open();
		await redis.quit();
	}
});

test("a site chosen while the first site's zones are still loading is the site the screen shows and watches", async ({ page }) => {
	test.skip(!redisUrl, "the live hub needs the run's Redis (ARIVA_E2E_REDIS_URL)");
	const guards = await guardPage(page);
	const { site, zone } = loadSite!;
	const zoneKey = `${site}/${zone}`;
	// The first zone profile read (the demo airport's published profile) answers only after the load site is shown.
	let answer = () => {};
	const answering = new Promise<void>((resolve) => (answer = resolve));
	let profileReads = 0;
	const isProfileRead = (url: URL) => /\/api\/v1\/admin\/zone-profiles\/[0-9a-f-]{36}$/i.test(url.pathname);
	await page.route(isProfileRead, async (route) => {
		if (profileReads++ === 0) await answering;
		await route.continue();
	});
	const redis = createClient({ url: redisUrl });
	await redis.connect();
	try {
		await publish(redis, snapshotOf(zoneKey, 41, 7.1));
		await signInThroughUi(page, accounts().loadScreen);
		await expect.poll(() => profileReads).toBe(1);
		await page.locator('#live-site').selectOption(site);
		const row = page.locator(`[data-testid="zone-row"][data-zone="${zone}"]`);
		await expect(row).toContainText('7.1');
		const late = page.waitForResponse((response) => isProfileRead(new URL(response.url())));
		answer();
		await (await late).finished();

		// The demo airport's late answer changes nothing: the load site's zone alone, still watched.
		await publish(redis, snapshotOf(zoneKey, 42, 7.2));
		await expect(row).toContainText('7.2');
		await expect(page.getByTestId('zone-row')).toHaveCount(1);
		await expect(page.getByTestId('live-state')).toHaveAttribute('data-state', 'connected');
		allowStatuses(guards, 404);
		await guards.expectClean();
	} finally {
		answer();
		await redis.quit();
	}
});
