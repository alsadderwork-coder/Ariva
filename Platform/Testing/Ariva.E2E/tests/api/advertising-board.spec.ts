import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { provisionLoadSite, secondFactorSignIn } from '../support/load-site';
import { sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-143b: the board carries the playlist a display plays, at sites of this spec's own (fresh codes each run). Only while
// the site's advertising policy allows it for the display (read on every board), only the display's own site's plan, only
// items that may play today (a draft never), each as often as its weight, until the plan next changes but no further ahead
// than the display's board may be shown (its stale threshold), with the display's wait rule. A display set to
// instructions, or a full board, carries none and fetches no advertisement. The wait rule is 1 to 240 minutes or none.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const week = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const admin = () => `${hosts.main}/api/v1/admin`;
const plan = (at: string) => `${admin()}/sites/${at}/advertising`;

interface Player {
	id: string;
	code: string;
	credential: string;
	request: Record<string, unknown>;
}

let site: string;
let other: string;
let adminToken: string;
let manager: string;
let approver: string;
let player: Player;
let instructions: Player;
let full: Player;
let elsewhere: Player;
const media: Record<'first' | 'second' | 'draft' | 'theirs', string> = {} as never;
let playlistId: string;

/** The site's local day (Asia/Dubai, provisionLoadSite), "yyyy-MM-dd", moved by some days. */
function day(offset: number): string {
	const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai' }).format(new Date());
	const date = new Date(`${today}T00:00:00Z`);
	date.setUTCDate(date.getUTCDate() + offset);
	return date.toISOString().slice(0, 10);
}

async function allow(code: string, allowed = true) {
	const policyUrl = `${admin()}/sites/${code}/advertising-policy`;
	const { publishedProfileId } = await (await call('GET', policyUrl, { token: adminToken })).json();
	const changed = await call('PUT', policyUrl, {
		token: adminToken,
		data: { allowed, excludedZones: [], reason: `Airport operator letter AO-E2E-143b, ${allowed ? 'on' : 'off'}`, reviewedProfileId: publishedProfileId }
	});
	expect(changed.status(), await changed.text()).toBe(200);
}

async function upload(at: string, approve: boolean): Promise<string> {
	const body = new FormData();
	body.append('file', new Blob([png], { type: 'image/png' }), 'ad.png');
	body.append('advertiser', 'Gulf Air');
	body.append('campaign', 'Winter sun');
	body.append('startsOn', day(-1));
	body.append('endsOn', day(30));
	const created = await call('POST', `${plan(at)}/media`, { token: manager, form: body });
	expect(created.status(), await created.text()).toBe(201);
	const id = (await created.json()).id;
	if (approve) {
		const { reviewToken } = await (await call('GET', `${plan(at)}/media/${id}`, { token: approver })).json();
		const approved = await call('POST', `${plan(at)}/media/${id}/approve`, { token: approver, data: { reviewed: reviewToken } });
		expect(approved.status(), await approved.text()).toBe(200);
	}
	return id;
}

async function display(at: string, options: { secondRegion?: string; layout?: string; pause?: number | null } = {}): Promise<Player> {
	const code = `BD-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
	const request = {
		siteCode: at,
		code,
		name: 'Arrivals hall',
		location: null,
		orientation: 'Landscape',
		languages: ['en', 'ar'],
		bandMinutes: 5,
		hysteresisMinutes: 1,
		staleSeconds: 150,
		entries: [{ zone: 'Load Zone', labels: { en: 'Arrivals', ar: 'القادمون' } }],
		fallback: { en: 'Please follow the signs', ar: 'يرجى اتباع اللافتات' },
		enabled: true,
		layout: options.layout ?? 'Split',
		queueShare: 60,
		secondRegion: options.secondRegion ?? 'Advertising',
		advertisingPauseMinutes: options.pause ?? null
	};
	const made = await call('POST', `${admin()}/displays`, { token: adminToken, data: request });
	expect(made.status(), await made.text()).toBe(201);
	const issued = await made.json();
	return { id: issued.display.id, code, credential: issued.credential, request };
}

async function board(of: Player) {
	const response = await call('GET', `${hosts.main}/api/v1/display/board?code=${of.code}`, { headers: { 'X-Ariva-Display-Key': of.credential } });
	expect(response.status(), await response.text()).toBe(200);
	return response.json();
}

async function fetchAsDisplay(of: Player, id: string) {
	return fetch(`${hosts.main}/api/v1/display/advertising/${id}?code=${of.code}`, { headers: { 'X-Ariva-Display-Key': of.credential } });
}

async function scheduleAll(at: string, playlist: string) {
	const made = await call('POST', `${plan(at)}/schedules`, {
		token: manager,
		data: { playlistId: playlist, scope: 'Site', targets: [], days: week, opens: '00:00', closes: '24:00' }
	});
	expect(made.status(), await made.text()).toBe(201);
}

test.beforeAll(async () => {
	adminToken = await secondFactorSignIn(accounts().boardAdmin);
	site = (await provisionLoadSite(accounts().boardAdmin, 'BD', 'Load Zone', adminToken)).site;
	other = (await provisionLoadSite(accounts().boardAdmin, 'BE', 'Load Zone', adminToken)).site;
	await allow(site);
	await allow(other);
	manager = (await signIn(accounts().boardManager)).accessToken;
	approver = await secondFactorSignIn(accounts().boardApprover);

	media.first = await upload(site, true);
	media.second = await upload(site, true);
	media.draft = await upload(site, false);
	media.theirs = await upload(other, true);
	const made = await call('POST', `${plan(site)}/playlists`, {
		token: manager,
		data: {
			name: 'Board bank',
			items: [
				{ kind: 'Media', id: media.first, seconds: 12, weight: 2 },
				{ kind: 'Media', id: media.draft, seconds: 10, weight: 3 },
				{ kind: 'Media', id: media.second, seconds: 6, weight: 1 }
			]
		}
	});
	expect(made.status(), await made.text()).toBe(201);
	playlistId = (await made.json()).id;
	await scheduleAll(site, playlistId);
	const theirs = await call('POST', `${plan(other)}/playlists`, { token: manager, data: { name: 'Theirs', items: [{ kind: 'Media', id: media.theirs, seconds: 9 }] } });
	expect(theirs.status(), await theirs.text()).toBe(201);
	await scheduleAll(other, (await theirs.json()).id);

	player = await display(site, { pause: 20 });
	instructions = await display(site, { secondRegion: 'Instructions' });
	full = await display(site, { layout: 'Full' });
	elsewhere = await display(other);
});

test('the board carries the playable playlist, each item as often as its weight, for as long as its board may be shown, with the wait rule', async () => {
	const read = await board(player);

	expect(read.secondRegion).toBe('Advertising');
	expect(read.advertising).toMatchObject({ playlistId, pauseMinutes: 20, order: [0, 1, 0] });
	expect(read.advertising.items).toEqual([
		{ kind: 'Media', mediaId: media.first, contentType: 'image/png', seconds: 12, slide: null },
		{ kind: 'Media', mediaId: media.second, contentType: 'image/png', seconds: 6, slide: null }
	]);
	// Nothing changes before the site's midnight, so the plan holds as long as the board may be shown: 150 seconds ahead.
	expect(Date.parse(read.advertising.untilUtc) - Date.parse(read.serverUtc), 'the display stale threshold ahead').toBe(150_000);
	expect(JSON.stringify(read)).not.toContain(media.draft);

	const shown = await fetchAsDisplay(player, media.first);
	expect(shown.status).toBe(200);
	expect(shown.headers.get('content-type')).toBe('image/png');
	expect((await fetchAsDisplay(player, media.draft)).status, 'a draft is not served').toBe(404);
	expect((await fetchAsDisplay(player, media.theirs)).status, "another site's item is not served").toBe(404);
});

test("a display plays its own site's plan only; one set to instructions, or a full board, carries none", async () => {
	const theirs = await board(elsewhere);
	expect(theirs.advertising.items.map((i: { mediaId: string }) => i.mediaId)).toEqual([media.theirs]);
	expect(JSON.stringify(theirs)).not.toContain(media.first);

	for (const [label, of] of [
		['set to instructions', instructions],
		['a full board', full]
	] as const) {
		const read = await board(of);
		expect(read.advertising, label).toBeNull();
	}
	expect((await board(instructions)).secondRegion).toBe('Instructions');
	expect((await fetchAsDisplay(instructions, media.first)).status, 'a display set to instructions fetches no advertisement').toBe(404);
	expect((await fetchAsDisplay(full, media.first)).status, 'nor does a full board, which has no second region').toBe(404);
});

test('turned off, the board carries instructions though the plan is kept; turned on again, it plays again', async () => {
	await allow(site, false);
	try {
		const off = await board(player);
		expect(off.secondRegion).toBe('Instructions');
		expect(off.advertising, 'a plan is never itself permission to advertise').toBeNull();
		expect((await fetchAsDisplay(player, media.first)).status).toBe(404);
		expect((await board(elsewhere)).advertising, "another site's policy is its own").not.toBeNull();
	} finally {
		await allow(site, true);
	}
	expect((await board(player)).advertising).toMatchObject({ playlistId, order: [0, 1, 0] });
});

test('a retired playlist stops the plan; an advertisement retired since leaves it', async () => {
	expect((await call('POST', `${plan(site)}/media/${media.second}/retire`, { token: manager })).status()).toBe(200);
	const read = await board(player);
	expect(read.advertising.items.map((i: { mediaId: string }) => i.mediaId)).toEqual([media.first]);
	expect(read.advertising.order).toEqual([0, 0]);

	expect((await call('POST', `${plan(site)}/playlists/${playlistId}/retire`, { token: manager })).status()).toBe(200);
	expect((await board(player)).advertising).toBeNull();
	expect((await call('POST', `${plan(site)}/playlists/${playlistId}/restore`, { token: manager })).status()).toBe(200);
	expect((await board(player)).advertising.playlistId).toBe(playlistId);
});

test('the wait rule is 1 to 240 minutes or none, and the board carries what was saved', async () => {
	const url = `${admin()}/displays/${player.id}`;
	for (const minutes of [0, 241, -5, 1.5, xssPayloads[0], sqlInjectionPayloads[0]]) {
		const response = await call('PUT', url, { token: manager, data: { ...player.request, advertisingPauseMinutes: minutes } });
		expect(response.status(), String(minutes)).toBe(400);
		expect(await response.text()).not.toContain('<script>');
	}
	for (const [minutes, expected] of [
		[240, 240],
		[null, null],
		[30, 30]
	] as const) {
		const saved = await call('PUT', url, { token: manager, data: { ...player.request, advertisingPauseMinutes: minutes } });
		expect(saved.status(), await saved.text()).toBe(200);
		expect((await saved.json()).advertisingPauseMinutes).toBe(expected);
		expect((await board(player)).advertising.pauseMinutes).toBe(expected);
	}
	const { advertisingPauseMinutes, ...withoutRule } = player.request;
	void advertisingPauseMinutes;
	const left = await call('PUT', url, { token: manager, data: withoutRule });
	expect((await left.json()).advertisingPauseMinutes, 'left out: no rule').toBeNull();
});

test('a board without the display credential is refused; a wrong code or credential reads nothing', async () => {
	expect((await call('GET', `${hosts.main}/api/v1/display/board?code=${player.code}`)).status()).toBe(401);
	expect((await call('GET', `${hosts.main}/api/v1/display/board?code=${elsewhere.code}`, { headers: { 'X-Ariva-Display-Key': player.credential } })).status()).toBe(401);
	expect((await fetch(`${hosts.main}/api/v1/display/advertising/${media.first}?code=${player.code}`)).status).toBe(401);
});
