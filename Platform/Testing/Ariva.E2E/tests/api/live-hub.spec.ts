import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { createClient } from 'redis';
import { expect, test } from '@playwright/test';
import { accounts, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';

// ARV-035: the live hub on Ariva.Api.Main. Connecting needs an access token with LiveQueue.View (a browser sends it as
// access_token, which only /hubs accepts and every log redacts); joining a zone needs the zone's site among the
// caller's sites; a snapshot that Ariva.Api.Stream publishes to Redis reaches the zone's group.
// Sign-in needs the database; the update test also needs the run's Redis (ARIVA_E2E_REDIS_URL, set by CI and dev:up).

const hubUrl = `${hosts.main}/hubs/live`;
// A queue zone of the DMO demo airport's published profile (the demo seed, ARV-019): only published zones can be joined.
const zoneKey = 'DMO/A-VIS';
const redisUrl = process.env.ARIVA_E2E_REDIS_URL;
const instance = process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:';

function connection(token?: string, negotiate = false): HubConnection {
	return new HubConnectionBuilder()
		.withUrl(hubUrl, {
			// As the web app connects: WebSockets only and no negotiate request, so any Main replica can take the
			// connection without sticky sessions (the token travels as access_token on the WebSocket request).
			transport: HttpTransportType.WebSockets,
			skipNegotiation: !negotiate,
			...(token ? { accessTokenFactory: () => token } : {})
		})
		.configureLogging(LogLevel.None)
		.build();
}

test.describe('live hub', () => {
	test.skip(!databaseAvailable, 'needs the database for sign-in (ARIVA_E2E_SCHEMA_UPDATE=true)');

	// The negotiate request shows the 401 to the client; a WebSocket refused at the upgrade only shows as a failed
	// connection, so those starts are asserted to fail without a message check.
	test('refuses a connection without an access token', async () => {
		await expect(connection(undefined, true).start()).rejects.toThrow(/401|Unauthorized/i);
		await expect(connection().start()).rejects.toThrow();
	});

	test('refuses a connection with a forged token', async () => {
		const forged = 'eyJhbGciOiJFUzI1NiIsInR5cCI6ImF0K2p3dCJ9.eyJzdWIiOiJ4In0.c2ln';
		await expect(connection(forged, true).start()).rejects.toThrow(/401|Unauthorized/i);
		await expect(connection(forged).start()).rejects.toThrow();
	});

	test('refuses to join a zone of another site or one that does not exist', async () => {
		const border = await signIn(accounts().BorderShiftSupervisor); // sites: E2E1
		const hub = connection(border.accessToken);
		await hub.start();
		try {
			await expect(hub.invoke('JoinZone', zoneKey)).rejects.toThrow(/forbidden/);
			await expect(hub.invoke('JoinZone', 'E2E1/NO-SUCH-ZONE')).rejects.toThrow(/forbidden/);
			await expect(hub.invoke('JoinZone', 'not a zone')).rejects.toThrow(/invalid_zone/);
		} finally {
			await hub.stop();
		}
	});

	test('delivers a zone snapshot to an authorised client', async () => {
		test.skip(!redisUrl, 'needs the run\'s Redis (ARIVA_E2E_REDIS_URL)');
		const admin = await signIn(accounts().SystemAdministrator); // every site
		const hub = connection(admin.accessToken);
		const received: Array<Record<string, unknown>> = [];
		hub.on('zone', (snapshot: Record<string, unknown>) => received.push(snapshot));
		await hub.start();
		const redis = createClient({ url: redisUrl });
		await redis.connect();
		try {
			await redis.del(`${instance}live:zone:${zoneKey}`);
			expect(await hub.invoke('JoinZone', zoneKey)).toBeNull();
			const snapshot = {
				zoneKey,
				minuteUtc: '2026-09-28T18:05:00Z',
				queueLength: 61,
				lengthFromSensors: true,
				lengthDegraded: false,
				nowcastMinutes: 15.4,
				throughputPerMinute: 4,
				noService: null,
				nowcastDegraded: false,
				publishedUtc: new Date().toISOString()
			};
			// What Ariva.Api.Stream does after a checkpoint: keep the zone's snapshot and announce it.
			await redis.set(`${instance}live:zone:${zoneKey}`, JSON.stringify(snapshot), { EX: 3600 });
			await expect
				.poll(async () => {
					await redis.publish(`${instance}live:zones`, JSON.stringify(snapshot));
					return received.length;
				}, { timeout: 20_000, intervals: [500, 1000, 2000] })
				.toBeGreaterThan(0);
			expect(received[0]).toMatchObject({ zoneKey, queueLength: 61, nowcastMinutes: 15.4 });

			// A snapshot that fails the checks never reaches a screen; one of another zone never reaches this group.
			const before = received.length;
			await redis.publish(`${instance}live:zones`, JSON.stringify({ ...snapshot, queueLength: -5 }));
			await redis.publish(`${instance}live:zones`, JSON.stringify({ ...snapshot, zoneKey: 'DMO/OTHER' }));
			await new Promise((resolve) => setTimeout(resolve, 1500));
			expect(received.filter((s) => s.queueLength === -5 || s.zoneKey === 'DMO/OTHER')).toHaveLength(0);
			expect(received.length).toBeGreaterThanOrEqual(before);

			// Joining again returns the kept snapshot straight away.
			expect(await hub.invoke('JoinZone', zoneKey)).toMatchObject({ zoneKey, queueLength: 61 });
		} finally {
			await redis.quit();
			await hub.stop();
		}
	});
});
