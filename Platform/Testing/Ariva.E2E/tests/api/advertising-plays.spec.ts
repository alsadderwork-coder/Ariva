import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { provisionLoadSite, secondFactorSignIn } from '../support/load-site';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';
import { expectStatus } from '../support/expect-status';

// ARV-144: proof of play through the API, at sites of this spec's own (fresh codes each run). A display reports its plays in
// batches of 1 to 100 with its own credential only; the server takes a play only when it names an advertisement of the
// playlist that applied to that display when the play started, started within the last day, lasted 1 second to a slot's
// ceiling, has ended and overlaps no other play of the display; a batch sent twice counts once. Forged plays (another site's
// or another display's advertisement, one never scheduled, a time out of range, one inside another, a display set to
// instructions), oversized batches and bodies, and other credentials are refused;
// a display sends at most 6 batches a minute, refused ones included, so each test reports from displays of its own.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const week = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const admin = () => `${hosts.main}/api/v1/admin`;
const plan = (at: string) => `${admin()}/sites/${at}/advertising`;

interface Player {
	code: string;
	credential: string;
}

let site: string;
let other: string;
let adminToken: string;
let manager: string;
let approver: string;
let neighbour: Player;
let elsewhere: Player;
const media: Record<'scheduled' | 'unscheduled' | 'neighbours' | 'theirs', string> = {} as never;

function day(offset: number): string {
	const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai' }).format(new Date());
	const date = new Date(`${today}T00:00:00Z`);
	date.setUTCDate(date.getUTCDate() + offset);
	return date.toISOString().slice(0, 10);
}

async function allow(code: string) {
	const policyUrl = `${admin()}/sites/${code}/advertising-policy`;
	const { publishedProfileId } = await (await call('GET', policyUrl, { token: adminToken })).json();
	const changed = await call('PUT', policyUrl, {
		token: adminToken,
		data: { allowed: true, excludedZones: [], reason: 'Airport operator letter AO-E2E-144, commercial team', reviewedProfileId: publishedProfileId }
	});
	await expectStatus(changed, 200);
}

async function approved(at: string): Promise<string> {
	const body = new FormData();
	body.append('file', new Blob([png], { type: 'image/png' }), 'ad.png');
	body.append('advertiser', 'Gulf Air');
	body.append('campaign', 'Winter sun');
	body.append('startsOn', day(-1));
	body.append('endsOn', day(30));
	const created = await call('POST', `${plan(at)}/media`, { token: manager, form: body });
	await expectStatus(created, 201);
	const id = (await created.json()).id;
	const { reviewToken } = await (await call('GET', `${plan(at)}/media/${id}`, { token: approver })).json();
	expect((await call('POST', `${plan(at)}/media/${id}/approve`, { token: approver, data: { reviewed: reviewToken } })).status()).toBe(200);
	return id;
}

async function display(at: string, secondRegion: 'Advertising' | 'Instructions' = 'Advertising'): Promise<Player> {
	const code = `PY-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
	const made = await call('POST', `${admin()}/displays`, {
		token: adminToken,
		data: {
			siteCode: at,
			code,
			name: 'Arrivals hall',
			location: null,
			orientation: 'Landscape',
			languages: ['en'],
			bandMinutes: 5,
			hysteresisMinutes: 1,
			staleSeconds: 150,
			entries: [{ zone: 'Load Zone', labels: { en: 'Arrivals' } }],
			fallback: { en: 'Please follow the signs' },
			enabled: true,
			layout: 'Split',
			queueShare: 60,
			secondRegion
		}
	});
	await expectStatus(made, 201);
	return { code, credential: (await made.json()).credential };
}

async function schedule(at: string, name: string, item: string, target: string) {
	const playlist = await call('POST', `${plan(at)}/playlists`, { token: manager, data: { name, items: [{ kind: 'Media', id: item, seconds: 10 }] } });
	await expectStatus(playlist, 201);
	const made = await call('POST', `${plan(at)}/schedules`, {
		token: manager,
		data: { playlistId: (await playlist.json()).id, scope: 'Display', targets: [target], days: week, opens: '00:00', closes: '24:00' }
	});
	await expectStatus(made, 201);
}

/** A display of the site with the scheduled advertisement on its own playlist, all day every day. */
async function scheduledPlayer(name: string): Promise<Player> {
	const fresh = await display(site);
	await schedule(site, name, media.scheduled, fresh.code);
	return fresh;
}

const playsUrl = (of: Player) => `${hosts.main}/api/v1/display/plays?code=${of.code}`;

function play(mediaId: string, secondsAgo = 60, duration = 10_000, completed = true) {
	return { mediaId, startedUtc: new Date(Date.now() - secondsAgo * 1000).toISOString(), durationMilliseconds: duration, completed };
}

async function send(of: Player, body: unknown, key = of.credential) {
	return call('POST', playsUrl(of), { headers: { 'X-Ariva-Display-Key': key }, data: body });
}

test.beforeAll(async () => {
	adminToken = await secondFactorSignIn(accounts().playsAdmin);
	site = (await provisionLoadSite(accounts().playsAdmin, 'PY', 'Load Zone', adminToken)).site;
	other = (await provisionLoadSite(accounts().playsAdmin, 'PZ', 'Load Zone', adminToken)).site;
	await allow(site);
	await allow(other);
	manager = (await signIn(accounts().playsManager)).accessToken;
	approver = await secondFactorSignIn(accounts().playsApprover);
	media.scheduled = await approved(site);
	media.unscheduled = await approved(site);
	media.neighbours = await approved(site);
	media.theirs = await approved(other);
	neighbour = await display(site);
	elsewhere = await display(other);
	await schedule(site, 'Neighbour bank', media.neighbours, neighbour.code);
	await schedule(other, 'Their bank', media.theirs, elsewhere.code);
});

test('a display reports the plays of its scheduled advertisement; a batch sent again counts once', async () => {
	const player = await scheduledPlayer('Played bank');
	const plays = [play(media.scheduled, 90), play(media.scheduled, 75, 4_000, false), play(media.scheduled, 60)];
	const first = await send(player, { plays });
	await expectStatus(first, 200);
	expect(first.headers()['cache-control']).toContain('no-store');
	expect(await first.json()).toEqual({ accepted: 3, duplicates: 0, refused: 0 });

	const again = await send(player, { plays });
	expect(await again.json(), 'the player retried a batch the server already had').toEqual({ accepted: 0, duplicates: 3, refused: 0 });
});

test('forged plays are refused, the honest ones in the same batch taken', async () => {
	const player = await scheduledPlayer('Forged bank');
	const now = Date.now();
	const response = await send(player, {
		plays: [
			play(media.scheduled, 45),
			play(media.unscheduled, 45),
			play(media.neighbours, 45),
			play(media.theirs, 45),
			{ ...play(media.scheduled), startedUtc: new Date(now + 10 * 60_000).toISOString() },
			{ ...play(media.scheduled), startedUtc: new Date(now - 25 * 3600_000).toISOString() },
			play(media.scheduled, 30, 125_001),
			play(media.scheduled, 5, 60_000),
			{ ...play(media.scheduled), startedUtc: new Date(now - 40_000).toISOString().replace('Z', '') },
			play('0199a000-0000-7000-8000-00000000a1a1', 45),
			{ ...play(media.scheduled), startedUtc: '9999-12-31T23:59:59.9999999Z' },
			play(media.scheduled, 43, 1_000, false),
			play(media.scheduled, 20, 999, false)
		]
	});
	await expectStatus(response, 200);
	expect(
		await response.json(),
		'one honest play; a never scheduled, a neighbour display, another site, a future, an old, a too long, an unfinished, a local time, an unknown advertisement, the last instant, one inside the honest play and one under a second refused'
	).toEqual({
		accepted: 1,
		duplicates: 0,
		refused: 12
	});

	const theirs = await send(elsewhere, { plays: [play(media.scheduled, 30)] });
	expect(await theirs.json(), "another site's display cannot claim this site's advertisement").toEqual({ accepted: 0, duplicates: 0, refused: 1 });

	// As its board would: a display set to instructions played no advertising, though a schedule names it (ARV-145).
	const instructions = await display(site, 'Instructions');
	await schedule(site, 'Instructions bank', media.scheduled, instructions.code);
	const shown = await send(instructions, { plays: [play(media.scheduled, 30)] });
	await expectStatus(shown, 200);
	expect(await shown.json(), 'a display set to instructions').toEqual({ accepted: 0, duplicates: 0, refused: 1 });
});

test('batches outside 1 to 100 plays, bodies over 64 KB and malformed bodies are refused without an echo', async () => {
	const many = Array.from({ length: 101 }, (_, i) => play(media.scheduled, 200 + i));
	// At most 6 requests a minute per display, refused ones included: two displays share the probes.
	const first = await scheduledPlayer('Refused bank');
	const second = await scheduledPlayer('Refused bank 2');
	const probes = [
		['101 plays', { plays: many }],
		['no plays', { plays: [] }],
		['no body fields', {}],
		['plays as a string', { plays: xssPayloads[0] }],
		['a media id that is not a uuid', { plays: [{ ...play(media.scheduled), mediaId: xssPayloads[1] }] }],
		['a start that is not a date', { plays: [{ ...play(media.scheduled), startedUtc: sqlInjectionPayloads[0] }] }],
		['a duration as markup', { plays: [{ ...play(media.scheduled), durationMilliseconds: xssPayloads[2] }] }]
	] as const;
	for (const [index, [label, body]] of probes.entries()) {
		const response = await send(index < 4 ? first : second, body);
		expect(response.status(), label).toBe(400);
		const answer = JSON.stringify(await response.json());
		for (const fragment of [...markupFragments, 'alert(1)', 'UNION']) expect(answer, `${label}: no echo of ${fragment}`).not.toContain(fragment);
	}

	const padded = { plays: [play(media.scheduled, 20)], padding: 'x'.repeat(70 * 1024) };
	expect((await send(second, padded)).status(), 'over 64 KB').toBe(413);
	const broken = await call('POST', playsUrl(first), {
		headers: { 'X-Ariva-Display-Key': first.credential, 'Content-Type': 'application/json' },
		data: '{"plays": [{"mediaId": '
	});
	expect(broken.status()).toBe(400);
});

test('only the display credential reports plays: none, a user token, or another display answers 401', async () => {
	const player = await scheduledPlayer('Credential bank');
	const body = { plays: [play(media.scheduled, 15)] };
	expect((await call('POST', playsUrl(player), { data: body })).status(), 'no credential').toBe(401);
	const token = (await signIn(accounts().playsManager)).accessToken;
	expect((await call('POST', playsUrl(player), { token, data: body })).status(), 'a user token').toBe(401);
	expect((await send(player, body, neighbour.credential)).status(), "another display's credential with this code").toBe(401);
	expect((await send({ code: 'NO-SUCH', credential: player.credential }, body)).status(), 'a code that is not the display').toBe(401);
});

test('a display sends at most 6 batches a minute', async () => {
	const fresh = await scheduledPlayer('Rate bank');
	const another = await scheduledPlayer('Rate bank 2');
	const statuses: number[] = [];
	for (let i = 0; i < 7; i++) statuses.push((await send(fresh, { plays: [play(media.scheduled, 30 + i)] })).status());
	expect(statuses.slice(0, 6)).toEqual([200, 200, 200, 200, 200, 200]);
	expect(statuses[6], 'the seventh in the minute').toBe(429);
	expect((await send(another, { plays: [play(media.scheduled, 10)] })).status(), 'another display keeps its own allowance').toBe(200);
});
