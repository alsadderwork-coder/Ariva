import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { provisionLoadSite, randomLetters, secondFactorSignIn } from '../support/load-site';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-143a: advertising playlists and schedules at sites of their own (fresh codes each run, so no other spec shares them).
// A playlist names advertisements of the site's library and active instruction slides of the site, each once, with a slot of
// 5 to 60 seconds and a weight of 1 to 10; a schedule plays an active playlist of the site on some days in a local window,
// for the whole site, some queue zones of its published profile or some of its displays. The timeline shows a display's week
// from the site's local midnight as the board will resolve it: a display's schedule before a zone's before the site's, and a
// retired playlist's schedules stop applying. Markup comes back as JSON text; another site's plan answers 404 under this
// site's route (IDOR), every endpoint answers 403 while the site's advertising policy is off and 404 outside the caller's
// sites, and roles without the permission get 403.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const week = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const unknownId = '0199a000-0000-7000-8000-00000000a1a1';

const plan = (at: string) => `${hosts.main}/api/v1/admin/sites/${at}/advertising`;
const playlistsUrl = (at: string, id?: string) => `${plan(at)}/playlists${id ? `/${id}` : ''}`;
const schedulesUrl = (at: string, id?: string) => `${plan(at)}/schedules${id ? `/${id}` : ''}`;
const timelineUrl = (at: string, display: string) => `${plan(at)}/timeline?display=${encodeURIComponent(display)}`;

let site: string;
let other: string;
let off: string;
let zone: string;
let admin: string;
let manager: string;
let elsewhere: string;
let display: string;
let image: string;
let otherImage: string;
let slide: string;
let morning: { id: string };
let peak: { id: string };
let theirs: { id: string };
let schedule: { id: string };

interface Item {
	kind: string;
	id: string;
	seconds?: number | null;
	weight?: number | null;
}

const playlist = (name: string, items: Item[]) => ({ name, items });

function scheduled(changes: Record<string, unknown> = {}) {
	return { playlistId: morning.id, scope: 'Site', targets: [], days: week, opens: '06:00', closes: '22:00', ...changes };
}

async function allow(code: string) {
	const policyUrl = `${hosts.main}/api/v1/admin/sites/${code}/advertising-policy`;
	const { publishedProfileId } = await (await call('GET', policyUrl, { token: admin })).json();
	const changed = await call('PUT', policyUrl, {
		token: admin,
		data: { allowed: true, excludedZones: [], reason: 'Airport operator letter AO-E2E-143a, commercial team', reviewedProfileId: publishedProfileId }
	});
	expect(changed.status(), await changed.text()).toBe(200);
}

async function uploadImage(at: string): Promise<string> {
	const body = new FormData();
	body.append('file', new Blob([png], { type: 'image/png' }), 'ad.png');
	body.append('advertiser', 'Gulf Air');
	body.append('campaign', 'Winter sun');
	// The site's local day (Asia/Dubai, provisionLoadSite) for 30 days.
	const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai' }).format(new Date());
	const until = new Date(`${today}T00:00:00Z`);
	until.setUTCDate(until.getUTCDate() + 30);
	body.append('startsOn', today);
	body.append('endsOn', until.toISOString().slice(0, 10));
	const response = await call('POST', `${plan(at)}/media`, { token: manager, form: body });
	expect(response.status(), await response.text()).toBe(201);
	return (await response.json()).id;
}

/** The answer's parsed text: an echo escaped by the JSON encoder (<) would pass a check of the raw text. */
async function noEcho(response: { json: () => Promise<unknown>; text: () => Promise<string> }, label: string) {
	const text = await response.text();
	let answer = text;
	try {
		answer = JSON.stringify(JSON.parse(text));
	} catch {
		// Not JSON: the raw text is what a reader sees.
	}
	for (const fragment of [...markupFragments, 'alert(1)']) expect(answer, `${label}: no echo of ${fragment}`).not.toContain(fragment);
}

test.beforeAll(async () => {
	admin = await secondFactorSignIn(accounts().planAdmin);
	const provisioned = await provisionLoadSite(accounts().planAdmin, 'PL', 'Load Zone', admin);
	site = provisioned.site;
	zone = provisioned.zone;
	other = (await provisionLoadSite(accounts().planAdmin, 'PM', 'Load Zone', admin)).site;
	off = `PO${randomLetters(4)}`;
	expect((await call('POST', `${hosts.main}/api/v1/admin/sites`, { token: admin, data: { code: off, name: off } })).status(), 'a site that never had an advertising policy').toBe(201);
	await allow(site);
	await allow(other);
	manager = (await signIn(accounts().planManager)).accessToken;
	elsewhere = (await signIn(accounts().planElsewhere)).accessToken;

	display = `PL-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
	const made = await call('POST', `${hosts.main}/api/v1/admin/displays`, {
		token: admin,
		data: {
			siteCode: site,
			code: display,
			name: 'Arrivals hall',
			location: null,
			orientation: 'Landscape',
			languages: ['en'],
			bandMinutes: 5,
			hysteresisMinutes: 1,
			staleSeconds: 150,
			entries: [{ zone, labels: { en: 'Arrivals' } }],
			fallback: { en: 'Please follow the signs' },
			enabled: true,
			layout: 'Split',
			queueShare: 60,
			secondRegion: 'Advertising'
		}
	});
	expect(made.status(), await made.text()).toBe(201);

	const slideMade = await call('POST', `${hosts.main}/api/v1/admin/display-slides`, {
		token: admin,
		data: { siteCode: site, scope: 'Site', targets: [], icon: 'Passport', durationSeconds: 8, pinned: false, texts: { en: { title: 'Have your passport ready', body: null } } }
	});
	expect(slideMade.status(), await slideMade.text()).toBe(201);
	slide = (await slideMade.json()).id;
	image = await uploadImage(site);
	otherImage = await uploadImage(other);
});

test('a playlist keeps its items in order with their slots, and markup in its name comes back as JSON text', async () => {
	const created = await call('POST', playlistsUrl(site), {
		token: manager,
		data: playlist(xssPayloads[1], [
			{ kind: 'Media', id: image, seconds: 12, weight: 3 },
			{ kind: 'Slide', id: slide, seconds: 8 }
		])
	});
	expect(created.status(), await created.text()).toBe(201);
	expect(created.headers()['content-type']).toContain('application/json');
	const body = await created.json();
	expect(body).toMatchObject({ siteCode: site, name: xssPayloads[1], status: 'Active', totalSeconds: 20 });
	expect(body.items).toEqual([
		expect.objectContaining({ kind: 'Media', id: image, seconds: 12, weight: 3, label: 'Gulf Air: Winter sun', playable: false, mediaStatus: 'Draft', contentType: 'image/png' }),
		expect.objectContaining({ kind: 'Slide', id: slide, seconds: 8, weight: 1, label: 'Have your passport ready', playable: true, mediaStatus: null })
	]);
	expect(created.headers()['location']).toContain(`/advertising/playlists/${body.id}`);
	morning = body;

	const renamed = await call('PUT', playlistsUrl(site, body.id), { token: manager, data: playlist('Morning bank', [{ kind: 'Slide', id: slide, seconds: 10 }, { kind: 'Media', id: image, seconds: 15 }]) });
	expect(renamed.status(), await renamed.text()).toBe(200);
	expect(await renamed.json()).toMatchObject({ name: 'Morning bank', totalSeconds: 25, items: [{ kind: 'Slide' }, { kind: 'Media', weight: 1 }] });
	expect((await (await call('GET', playlistsUrl(site), { token: manager })).json()).map((p: { id: string }) => p.id)).toContain(body.id);

	const second = await call('POST', playlistsUrl(site), { token: manager, data: playlist('Peak bank', [{ kind: 'Media', id: image, seconds: 20 }]) });
	expect(second.status(), await second.text()).toBe(201);
	peak = await second.json();
	const elsewhereList = await call('POST', playlistsUrl(other), { token: manager, data: playlist('Theirs', [{ kind: 'Media', id: otherImage, seconds: 10 }]) });
	expect(elsewhereList.status(), await elsewhereList.text()).toBe(201);
	theirs = await elsewhereList.json();
});

test('items outside the rules, and attack payloads in the body, are refused without an echo', async () => {
	const refused: [string, unknown][] = [
		['a blank name', playlist('   ', [{ kind: 'Media', id: image, seconds: 10 }])],
		['a name of 121 characters', playlist('x'.repeat(121), [{ kind: 'Media', id: image, seconds: 10 }])],
		['a line break in the name', playlist('Two\nlines', [{ kind: 'Media', id: image, seconds: 10 }])],
		['a bidirectional override in the name', playlist('\u202EBank', [{ kind: 'Media', id: image, seconds: 10 }])],
		['no items', playlist('Empty', [])],
		['41 items', playlist('Long', Array.from({ length: 41 }, () => ({ kind: 'Media', id: image, seconds: 10 })))],
		["another site's advertisement", playlist('Taken', [{ kind: 'Media', id: otherImage, seconds: 10 }])],
		['an advertisement that does not exist', playlist('Ghost', [{ kind: 'Media', id: unknownId, seconds: 10 }])],
		['a slide named as an advertisement', playlist('Mixed', [{ kind: 'Media', id: slide, seconds: 10 }])],
		['an advertisement named as a slide', playlist('Mixed', [{ kind: 'Slide', id: image, seconds: 10 }])],
		['the same item twice', playlist('Twice', [{ kind: 'Media', id: image, seconds: 10 }, { kind: 'Media', id: image, seconds: 12 }])],
		['a kind in lower case', playlist('Case', [{ kind: 'media', id: image, seconds: 10 }])],
		['a kind as a number', playlist('Number', [{ kind: '0', id: image, seconds: 10 }])],
		['an unknown kind', playlist('Video', [{ kind: 'Video', id: image, seconds: 10 }])],
		['markup as a kind', playlist('Markup', [{ kind: xssPayloads[0], id: image, seconds: 10 }])],
		['an empty id', playlist('Empty id', [{ kind: 'Media', id: '00000000-0000-0000-0000-000000000000', seconds: 10 }])],
		['an id that is not a uuid', playlist('Bad id', [{ kind: 'Media', id: xssPayloads[2], seconds: 10 }])],
		['4 seconds', playlist('Short', [{ kind: 'Media', id: image, seconds: 4 }])],
		['61 seconds', playlist('Long slot', [{ kind: 'Slide', id: slide, seconds: 61 }])],
		['no seconds for an image', playlist('No slot', [{ kind: 'Media', id: image, seconds: null }])],
		['weight 0', playlist('Light', [{ kind: 'Media', id: image, seconds: 10, weight: 0 }])],
		['weight 11', playlist('Heavy', [{ kind: 'Media', id: image, seconds: 10, weight: 11 }])],
		['items as a string', { name: 'Text', items: xssPayloads[0] }],
		['no body fields', {}]
	];
	for (const [label, data] of refused) {
		const response = await call('POST', playlistsUrl(site), { token: manager, data });
		expect(response.status(), label).toBe(400);
		await noEcho(response, label);
	}
	const names: string[] = (await (await call('GET', playlistsUrl(site), { token: manager })).json()).map((p: { name: string }) => p.name);
	expect(names.sort(), 'nothing refused was stored').toEqual(['Morning bank', 'Peak bank']);
});

test('a schedule keeps its days in the week order and its local window; one outside the rules is refused', async () => {
	const created = await call('POST', schedulesUrl(site), { token: manager, data: scheduled({ scope: 'Zone', targets: [zone], days: ['Friday', 'Monday'], opens: '22:00', closes: '02:00' }) });
	expect(created.status(), await created.text()).toBe(201);
	const body = await created.json();
	expect(body).toMatchObject({ siteCode: site, playlistId: morning.id, playlistName: 'Morning bank', playlistActive: true, scope: 'Zone', targets: [zone], days: ['Monday', 'Friday'], opens: '22:00', closes: '02:00', status: 'Active' });
	expect(created.headers()['location']).toContain(`/advertising/schedules/${body.id}`);
	schedule = body;

	const changed = await call('PUT', schedulesUrl(site, body.id), { token: manager, data: scheduled() });
	expect(changed.status(), await changed.text()).toBe(200);
	expect(await changed.json()).toMatchObject({ scope: 'Site', targets: [], days: week, opens: '06:00', closes: '22:00' });
	expect((await (await call('POST', `${schedulesUrl(site, body.id)}/retire`, { token: manager })).json()).status).toBe('Retired');
	expect((await (await call('POST', `${schedulesUrl(site, body.id)}/retire`, { token: manager })).json()).status, 'retiring twice is harmless').toBe('Retired');
	expect((await (await call('POST', `${schedulesUrl(site, body.id)}/restore`, { token: manager })).json()).status).toBe('Active');
	expect((await (await call('GET', schedulesUrl(site, body.id), { token: manager })).json())).toMatchObject({ status: 'Active', scope: 'Site' });

	const retired = await (await call('POST', playlistsUrl(site), { token: manager, data: playlist('Retired bank', [{ kind: 'Media', id: image, seconds: 10 }]) })).json();
	expect((await call('POST', `${playlistsUrl(site, retired.id)}/retire`, { token: manager })).status()).toBe(200);
	const refused: [string, Record<string, unknown>][] = [
		['a day in lower case', { days: ['monday'] }],
		['an unknown day', { days: ['Funday'] }],
		['a day twice', { days: ['Monday', 'Monday'] }],
		['eight days', { days: [...week, 'Monday'] }],
		['no days', { days: [] }],
		['markup as a day', { days: [xssPayloads[0]] }],
		['an opening time of 25:00', { opens: '25:00' }],
		['an opening time in words', { opens: 'nine' }],
		['markup as a closing time', { closes: xssPayloads[1] }],
		['no opening time', { opens: null }],
		['a scope in lower case', { scope: 'site' }],
		['an unknown scope', { scope: 'Airport' }],
		['markup as a scope', { scope: xssPayloads[2] }],
		['a site schedule naming a zone', { targets: [zone] }],
		['a zone schedule naming no zone', { scope: 'Zone', targets: [] }],
		['a zone that is not published', { scope: 'Zone', targets: ['NOT-A-ZONE'] }],
		['markup as a zone', { scope: 'Zone', targets: [xssPayloads[3]] }],
		['a display that does not exist', { scope: 'Display', targets: ['NOPE-1'] }],
		['21 displays', { scope: 'Display', targets: Array.from({ length: 21 }, (_, i) => `D-${i}`) }],
		["another site's playlist", { playlistId: theirs.id }],
		['a playlist that does not exist', { playlistId: unknownId }],
		['a retired playlist', { playlistId: retired.id }],
		['an empty playlist id', { playlistId: '00000000-0000-0000-0000-000000000000' }],
		['a playlist id that is not a uuid', { playlistId: xssPayloads[4] }]
	];
	for (const [label, changes] of refused) {
		const response = await call('POST', schedulesUrl(site), { token: manager, data: scheduled(changes) });
		expect(response.status(), label).toBe(400);
		await noEcho(response, label);
	}
	const listed = await (await call('GET', schedulesUrl(site), { token: manager })).json();
	expect(listed.map((s: { id: string }) => s.id), 'nothing refused was stored').toEqual([schedule.id]);
});

test("the timeline resolves the display's week in the site's local time; a retired playlist stops its schedules applying", async () => {
	const own = await call('POST', schedulesUrl(site), { token: manager, data: scheduled({ playlistId: peak.id, scope: 'Display', targets: [display], opens: '09:00', closes: '10:00' }) });
	expect(own.status(), await own.text()).toBe(201);

	const response = await call('GET', timelineUrl(site, display), { token: manager });
	expect(response.status(), await response.text()).toBe(200);
	const timeline = await response.json();
	expect(timeline).toMatchObject({ siteCode: site, displayCode: display, timeZone: 'Asia/Dubai', secondRegion: 'Advertising' });
	expect(new Date(timeline.toUtc).getTime() - new Date(timeline.fromUtc).getTime(), 'one week').toBe(7 * 24 * 3600 * 1000);
	const segments: { fromLocal: string; toLocal: string; playlistName: string | null; fromUtc: string; toUtc: string }[] = timeline.segments;
	const first = segments[0].fromLocal.slice(0, 10);
	expect(segments[0].fromLocal, "the site's local midnight").toBe(`${first}T00:00`);
	// Dubai keeps UTC+4 all year: local midnight is 20:00 UTC the day before.
	expect(new Date(segments[0].fromUtc).toISOString()).toBe(new Date(`${first}T00:00:00+04:00`).toISOString());
	expect(segments.slice(0, 5).map((s) => [s.fromLocal.slice(11), s.toLocal.slice(11), s.playlistName])).toEqual([
		['00:00', '06:00', null],
		['06:00', '09:00', 'Morning bank'],
		['09:00', '10:00', 'Peak bank'],
		['10:00', '22:00', 'Morning bank'],
		['22:00', '06:00', null]
	]);
	expect(segments, 'five stretches the first day, four each following day').toHaveLength(29);
	expect(segments.at(-1)).toMatchObject({ toLocal: `${segments.at(-1)!.toLocal.slice(0, 10)}T00:00`, playlistName: null });
	for (let i = 1; i < segments.length; i++) expect(segments[i].fromUtc, 'stretches follow each other').toBe(segments[i - 1].toUtc);

	expect((await call('POST', `${playlistsUrl(site, morning.id)}/retire`, { token: manager })).status()).toBe(200);
	const listed = await (await call('GET', schedulesUrl(site), { token: manager })).json();
	expect(listed.find((s: { id: string }) => s.id === schedule.id)).toMatchObject({ playlistActive: false, status: 'Active' });
	const after = await (await call('GET', timelineUrl(site, display), { token: manager })).json();
	expect(after.segments.map((s: { playlistName: string | null }) => s.playlistName)).toEqual([null, ...Array.from({ length: 7 }, () => ['Peak bank', null]).flat()]);
	expect((await call('POST', `${playlistsUrl(site, morning.id)}/restore`, { token: manager })).status()).toBe(200);
	expect((await call('POST', schedulesUrl(site), { token: manager, data: scheduled() })).status(), 'a restored playlist plays again').toBe(201);

	for (const [label, code] of [
		['a display that does not exist', 'NO-SUCH'],
		['no display', ''],
		...[...sqlInjectionPayloads, ...xssPayloads, 'D'.repeat(41)].map((p) => [p.slice(0, 30), p])
	] as const) {
		const answer = await call('GET', timelineUrl(site, code), { token: manager });
		expect([400, 404], label).toContain(answer.status());
		await noEcho(answer, label);
		expect(await answer.text()).not.toContain('UNION');
	}
	expect((await call('GET', timelineUrl(other, display), { token: manager })).status(), "a display of another site under that site's route").toBe(404);
});

test("another site's plan answers like one that does not exist; outside the caller's sites everything is 404", async () => {
	for (const [method, url, data] of [
		['GET', playlistsUrl(site, theirs.id), undefined],
		['PUT', playlistsUrl(site, theirs.id), playlist('Taken over', [{ kind: 'Media', id: image, seconds: 10 }])],
		['POST', `${playlistsUrl(site, theirs.id)}/retire`, undefined],
		['POST', `${playlistsUrl(site, theirs.id)}/restore`, undefined],
		['GET', schedulesUrl(other, schedule.id), undefined],
		['PUT', schedulesUrl(other, schedule.id), scheduled({ playlistId: theirs.id })],
		['POST', `${schedulesUrl(other, schedule.id)}/retire`, undefined],
		['POST', `${schedulesUrl(other, schedule.id)}/restore`, undefined]
	] as const) {
		const response = await call(method, url, { token: manager, data });
		expect(response.status(), `${method} ${url}`).toBe(404);
		expect(await response.text()).not.toContain('Theirs');
	}
	expect((await (await call('GET', playlistsUrl(other, theirs.id), { token: manager })).json())).toMatchObject({ name: 'Theirs', status: 'Active' });
	expect((await (await call('GET', schedulesUrl(site, schedule.id), { token: manager })).json())).toMatchObject({ status: 'Active', playlistId: morning.id });

	for (const [method, url, data] of [
		['GET', playlistsUrl(site), undefined],
		['GET', playlistsUrl(site, morning.id), undefined],
		['POST', playlistsUrl(site), playlist('Outsider', [{ kind: 'Media', id: image, seconds: 10 }])],
		['POST', `${playlistsUrl(site, morning.id)}/retire`, undefined],
		['GET', schedulesUrl(site), undefined],
		['POST', schedulesUrl(site), scheduled()],
		['POST', `${schedulesUrl(site, schedule.id)}/retire`, undefined],
		['GET', timelineUrl(site, display), undefined],
		['GET', playlistsUrl('NOSUCHSITE'), undefined]
	] as const) {
		const response = await call(method, url, { token: elsewhere, data });
		expect(response.status(), `${method} ${url} outside the caller's sites`).toBe(404);
		expect(await response.text()).not.toContain('Morning bank');
	}
	expect((await (await call('GET', playlistsUrl(site, morning.id), { token: manager })).json()).status).toBe('Active');
});

test("attack payloads as a site code answer without an echo and never reach the database as SQL", async () => {
	for (const payload of [...sqlInjectionPayloads, ...xssPayloads, '../DMO', 'D'.repeat(300)]) {
		const at = encodeURIComponent(payload);
		for (const [method, url, data] of [
			['GET', playlistsUrl(at), undefined],
			['POST', playlistsUrl(at), playlist('Probe', [{ kind: 'Media', id: image, seconds: 10 }])],
			['GET', schedulesUrl(at), undefined],
			['GET', timelineUrl(at, display), undefined]
		] as const) {
			const response = await call(method, url, { token: manager, data });
			expect([400, 404], `${method} ${payload.slice(0, 30)}`).toContain(response.status());
			await noEcho(response, `${method} ${payload.slice(0, 30)}`);
			expect(await response.text()).not.toContain('UNION');
		}
	}
	expect((await call('GET', playlistsUrl(site), { token: manager })).status(), 'the table is still there').toBe(200);
});

test('every endpoint answers 403 while the advertising policy is off; roles without the permission get 403, anonymous 401', async () => {
	for (const [method, url, data] of [
		['GET', playlistsUrl(off), undefined],
		['POST', playlistsUrl(off), playlist('Off', [{ kind: 'Media', id: image, seconds: 10 }])],
		['PUT', playlistsUrl(off, morning.id), playlist('Off', [{ kind: 'Media', id: image, seconds: 10 }])],
		['GET', schedulesUrl(off), undefined],
		['POST', schedulesUrl(off), scheduled()],
		['POST', `${schedulesUrl(off, schedule.id)}/retire`, undefined],
		['GET', timelineUrl(off, display), undefined]
	] as const) {
		expect((await call(method, url, { token: manager, data })).status(), `${method} ${url} with advertising off`).toBe(403);
	}

	const handler = (await signIn(accounts().webHandler)).accessToken;
	const observer = (await signIn(accounts().ValidationObserver)).accessToken;
	for (const token of [handler, observer]) {
		expect((await call('GET', playlistsUrl(site), { token })).status()).toBe(403);
		expect((await call('POST', playlistsUrl(site), { token, data: playlist('No', [{ kind: 'Media', id: image, seconds: 10 }]) })).status()).toBe(403);
		expect((await call('PUT', schedulesUrl(site, schedule.id), { token, data: scheduled() })).status()).toBe(403);
		expect((await call('GET', timelineUrl(site, display), { token })).status()).toBe(403);
	}
	expect((await call('GET', playlistsUrl(site))).status(), 'anonymous').toBe(401);
	expect((await call('GET', timelineUrl(site, display))).status(), 'anonymous').toBe(401);
	expect((await (await call('GET', playlistsUrl(site, morning.id), { token: manager })).json()).name, 'nothing changed').toBe('Morning bank');
});
