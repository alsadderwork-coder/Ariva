import crypto from 'node:crypto';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { provisionLoadSite, randomLetters, secondFactorSignIn } from '../support/load-site';
import { sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-142a: the advertisement library of sites of its own (fresh codes each run, so no other spec shares them). Images are
// typed by their bytes and written anew (metadata, comments and anything after the image's end are gone), stored under
// generated keys and recorded as drafts; only an approved item within its dates plays, and the approver is a second person
// with a second factor, naming the item as it was shown (its review token: 409 once it changed). Each person may attempt
// 30 uploads in 10 minutes, so the attacks and the probes have accounts of their own. Every endpoint answers 403 while the
// site's advertising policy is off and 404 outside the caller's sites; another site's item answers 404 under this site's
// route (IDOR). A display fetches an item with its own credential only: an approved item of its own site within its dates
// while it is set to advertising and the policy allows it.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAgAAAAEAQMAAACJA+yzAAAABlBMVEX/AAAAAP9sof2OAAAADElEQVQI12PgZwBDAAD4AD3ab+xIAAAAAElFTkSuQmCC', 'base64');
const jpeg = Buffer.from(
	'/9j/4AAQSkZJRgABAQAAAAAAAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAEAAgDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACP/EABgQAAIDAAAAAAAAAAAAAAAAAAAHRYPC/8QAFQEBAQAAAAAAAAAAAAAAAAAABwj/xAAYEQACAwAAAAAAAAAAAAAAAAAACEWDw//aAAwDAQACEQMRAD8AGzVi7cDgskxRsUaw8Vdkf//Z',
	'base64'
);
const webp = Buffer.from('UklGRh4AAABXRUJQVlA4TBIAAAAvB8AAAA8Q87//8x8O+hDR/wA=', 'base64');
const markup = '<html><body><script>alert(document.cookie)</script></body></html>';

/** A JPEG comment segment (FF FE) right after the start-of-image marker. */
function withComment(image: Buffer, text: string): Buffer {
	const payload = Buffer.from(text, 'latin1');
	const length = Buffer.alloc(2);
	length.writeUInt16BE(payload.length + 2);
	return Buffer.concat([image.subarray(0, 2), Buffer.from([0xff, 0xfe]), length, payload, image.subarray(2)]);
}

/** The site's local day (Asia/Dubai, provisionLoadSite) moved by some days, "yyyy-MM-dd". */
function day(offset: number): string {
	const today = new Intl.DateTimeFormat('en-CA', {
		timeZone: 'Asia/Dubai'
	}).format(new Date());
	const date = new Date(`${today}T00:00:00Z`);
	date.setUTCDate(date.getUTCDate() + offset);
	return date.toISOString().slice(0, 10);
}

interface Details {
	advertiser?: string;
	campaign?: string;
	startsOn?: string;
	endsOn?: string;
}

function form(content: Buffer | string, fileName: string, type: string, details: Details = {}): FormData {
	const body = new FormData();
	body.append('file', new Blob([content], { type }), fileName);
	body.append('advertiser', details.advertiser ?? 'Gulf Air');
	body.append('campaign', details.campaign ?? 'Winter sun');
	body.append('startsOn', details.startsOn ?? day(-1));
	body.append('endsOn', details.endsOn ?? day(30));
	return body;
}

const mediaUrl = (site: string, id?: string) => `${hosts.main}/api/v1/admin/sites/${site}/advertising/media${id ? `/${id}` : ''}`;
const displayFetchUrl = (id: string, code: string) => `${hosts.main}/api/v1/display/advertising/${id}?code=${code}`;

let site: string;
let other: string;
let off: string;
let admin: string;
let manager: string;
let approver: string;
let elsewhere: string;
let attacker: string;
let prober: string;
const displays: Record<'advertising' | 'instructions', { id: string; code: string; credential: string }> = {} as never;

async function upload(token: string, content: Buffer | string, fileName: string, type: string, details: Details = {}, at = site) {
	return call('POST', mediaUrl(at), {
		token,
		form: form(content, fileName, type, details)
	});
}

async function uploaded(token: string, content: Buffer = png, details: Details = {}, at = site): Promise<{ id: string; status: string }> {
	const response = await upload(token, content, 'ad.png', 'image/png', details, at);
	expect(response.status(), await response.text()).toBe(201);
	return response.json();
}

/** Approves an item as the approver is shown it now: its review token read first, as the screen sends it. */
async function approve(at: string, id: string, token = approver) {
	const shown = await call('GET', mediaUrl(at, id), { token });
	const reviewed = shown.status() === 200 ? (await shown.json()).reviewToken : '0'.repeat(64);
	return call('POST', `${mediaUrl(at, id)}/approve`, {
		token,
		data: { reviewed }
	});
}

async function allow(code: string) {
	const policyUrl = `${hosts.main}/api/v1/admin/sites/${code}/advertising-policy`;
	const { publishedProfileId } = await (await call('GET', policyUrl, { token: admin })).json();
	const changed = await call('PUT', policyUrl, {
		token: admin,
		data: {
			allowed: true,
			excludedZones: [],
			reason: 'Airport operator letter AO-E2E-142a, commercial team',
			reviewedProfileId: publishedProfileId
		}
	});
	expect(changed.status(), await changed.text()).toBe(200);
}

async function fetchAsDisplay(id: string, display: { code: string; credential: string }, key = display.credential) {
	return fetch(displayFetchUrl(id, display.code), {
		headers: { 'X-Ariva-Display-Key': key }
	});
}

test.beforeAll(async () => {
	admin = await secondFactorSignIn(accounts().mediaAdmin);
	site = (await provisionLoadSite(accounts().mediaAdmin, 'AM', 'Load Zone', admin)).site;
	other = (await provisionLoadSite(accounts().mediaAdmin, 'AN', 'Load Zone', admin)).site;
	off = `AO${randomLetters(4)}`;
	const offSite = await call('POST', `${hosts.main}/api/v1/admin/sites`, { token: admin, data: { code: off, name: off } });
	expect(offSite.status(), 'a site that never had an advertising policy').toBe(201);
	await allow(site);
	await allow(other);
	manager = (await signIn(accounts().mediaManager)).accessToken;
	approver = await secondFactorSignIn(accounts().mediaApprover);
	elsewhere = (await signIn(accounts().mediaElsewhere)).accessToken;
	attacker = (await signIn(accounts().mediaAttacker)).accessToken;
	prober = (await signIn(accounts().mediaProber)).accessToken;
	for (const secondRegion of ['Advertising', 'Instructions'] as const) {
		const code = `AD-${crypto.randomBytes(3).toString('hex').toUpperCase()}`;
		const made = await call('POST', `${hosts.main}/api/v1/admin/displays`, {
			token: admin,
			data: {
				siteCode: site,
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
		expect(made.status(), await made.text()).toBe(201);
		const issued = await made.json();
		displays[secondRegion === 'Advertising' ? 'advertising' : 'instructions'] = { id: issued.display.id, code, credential: issued.credential };
	}
});

test('an image is typed by its bytes, written anew without what it carried, and kept as a draft under a generated name', async () => {
	// A PNG with a page after its end, declared as HTML: stored as the PNG it is, without the page.
	const created = await upload(manager, Buffer.concat([png, Buffer.from(markup)]), 'summer.png', 'text/html');
	expect(created.status(), await created.text()).toBe(201);
	const item = await created.json();
	expect(item).toMatchObject({
		siteCode: site,
		contentType: 'image/png',
		widthPixels: 8,
		heightPixels: 4,
		originalFileName: 'summer.png',
		status: 'Draft'
	});
	expect(item).toMatchObject({
		playable: false,
		canApprove: false,
		uploadedBy: accounts().mediaManager.userName,
		approvedBy: null
	});
	expect(created.headers()['location']).toContain(`/advertising/media/${item.id}`);

	const content = await fetch(`${mediaUrl(site, item.id)}/content`, {
		headers: { Authorization: `Bearer ${manager}` }
	});
	expect(content.status).toBe(200);
	const bytes = Buffer.from(await content.arrayBuffer());
	expect(bytes.subarray(0, 8).equals(png.subarray(0, 8)), 'a PNG').toBe(true);
	expect(bytes.toString('latin1')).not.toContain('<script');
	expect(crypto.createHash('sha256').update(bytes).digest('hex')).toBe(item.sha256);
	expect(content.headers.get('content-type')).toBe('image/png');
	expect(content.headers.get('content-security-policy')).toContain('sandbox');
	expect(content.headers.get('content-security-policy')).toContain("default-src 'none'");
	expect(content.headers.get('x-content-type-options')).toBe('nosniff');
	expect(content.headers.get('cross-origin-resource-policy')).toBe('same-origin');
	expect(content.headers.get('cache-control')).toContain('no-store');

	// A JPEG with markup in a comment, and a WebP: each kept in its own type, the comment gone.
	const fromJpeg = await upload(manager, withComment(jpeg, markup), 'winter.jpg', 'image/jpeg');
	expect(fromJpeg.status(), await fromJpeg.text()).toBe(201);
	const stored = await fetch(`${mediaUrl(site, (await fromJpeg.json()).id)}/content`, { headers: { Authorization: `Bearer ${manager}` } });
	expect(stored.headers.get('content-type')).toBe('image/jpeg');
	expect(Buffer.from(await stored.arrayBuffer()).toString('latin1')).not.toContain('<script');
	const fromWebp = await upload(manager, webp, 'spring.webp', 'image/webp');
	expect(fromWebp.status(), await fromWebp.text()).toBe(201);
	expect((await fromWebp.json()).contentType).toBe('image/webp');
});

test('upload attacks are refused without an echo: wrong types, polyglots, oversized files and path tricks in the name', async () => {
	const refusedTypes: [string, Buffer | string, string, string][] = [
		['SVG with script', '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>', 'ad.png', 'image/png'],
		['HTML', markup, 'ad.jpg', 'image/jpeg'],
		['PDF', '%PDF-1.7\n1 0 obj << /Type /Catalog /OpenAction << /JS (alert(1)) >> >> endobj', 'ad.png', 'image/png'],
		['GIF', Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64'), 'ad.gif', 'image/gif'],
		['MP4 (videos come with ARV-142b)', Buffer.concat([Buffer.from([0, 0, 0, 0x18]), Buffer.from('ftypisom'), Buffer.alloc(12)]), 'ad.mp4', 'video/mp4'],
		['a PNG signature over a page', Buffer.concat([png.subarray(0, 8), Buffer.from(markup)]), 'ad.png', 'image/png'],
		['a JPEG signature over a script', Buffer.concat([Buffer.from([0xff, 0xd8, 0xff, 0xe0]), Buffer.from(markup)]), 'ad.jpg', 'image/jpeg'],
		['a truncated PNG', png.subarray(0, 40), 'ad.png', 'image/png'],
		['an empty file', Buffer.alloc(0), 'ad.png', 'image/png']
	];
	for (const [name, content, fileName, type] of refusedTypes) {
		const response = await upload(attacker, content, fileName, type);
		expect(response.status(), name).toBe(400);
		const text = await response.text();
		expect(text, name).toContain('is not a PNG, JPEG or WebP image');
		expect(text, `${name}: nothing sent is echoed`).not.toContain('<script');
	}

	const oversized = await upload(attacker, Buffer.concat([png, Buffer.alloc(10 * 1024 * 1024)]), 'ad.png', 'image/png');
	expect(oversized.status(), 'over 10 MB').toBe(413);

	for (const fileName of [
		'../../etc/passwd.png',
		'..\\..\\boot.png',
		'C:\\ads\\a.png',
		'/var/ariva/a.png',
		'ads/a.png',
		'ad\u202Egnp.png',
		'a'.repeat(201) + '.png'
	]) {
		const response = await upload(attacker, png, fileName, 'image/png');
		expect(response.status(), fileName).toBe(400);
		const text = await response.text();
		expect(text).toContain('The file name holds a path');
		expect(text, 'the name is not echoed').not.toContain('passwd');
	}

	const list = await (await call('GET', mediaUrl(site), { token: attacker })).json();
	expect(
		list.every((item: { originalFileName: string | null }) => !item.originalFileName?.includes('/')),
		'no stored name carries a path'
	).toBe(true);
});

test('details are bounded plain text: markup is kept as text, control and invisible characters and bad dates are refused', async () => {
	for (const payload of [...xssPayloads, ...sqlInjectionPayloads.slice(0, 3)]) {
		const response = await upload(prober, png, 'ad.png', 'image/png', {
			advertiser: payload.slice(0, 120),
			campaign: payload.slice(0, 120)
		});
		if (response.status() === 400) {
			// Only for a payload with a control character, never for its markup.
			expect(await response.text()).toContain('on one line');
			continue;
		}
		expect(response.status(), payload).toBe(201);
		expect(response.headers()['content-type']).toContain('application/json');
		expect((await response.json()).advertiser, 'kept and returned as JSON text').toBe(payload.slice(0, 120).trim());
	}
	// A file name made of markup is kept as text (it holds no path) and returned as JSON text.
	const named = await upload(prober, png, '<img src=x onerror=alert(1)>.png', 'image/png');
	expect(named.status(), await named.text()).toBe(201);
	expect(named.headers()['content-type']).toContain('application/json');
	expect((await named.json()).originalFileName).toBe('<img src=x onerror=alert(1)>.png');

	const refused: [string, Details][] = [
		['the last day before the first', { startsOn: day(5), endsOn: day(1) }],
		['a span of more than two years', { startsOn: day(0), endsOn: day(731) }],
		['a blank advertiser', { advertiser: '   ' }],
		['a bidirectional override', { advertiser: 'Gulf\u202EriA' }],
		['a line break', { campaign: 'Winter\nsun' }],
		['an advertiser of 121 characters', { advertiser: 'a'.repeat(121) }]
	];
	for (const [name, details] of refused) expect((await upload(prober, png, 'ad.png', 'image/png', details)).status(), name).toBe(400);
});

test('approval needs a second person with a second factor; a change sends an approved item back to draft', async () => {
	const item = await uploaded(manager);

	const passwordOnly = await approve(site, item.id, manager);
	expect(passwordOnly.status(), 'a password alone is not enough').toBe(401);
	expect((await passwordOnly.json()).error).toBe('mfa_required');

	// The approver's own upload: refused to the approver, even with a second factor.
	const own = await uploaded(approver);
	const self = await approve(site, own.id, approver);
	expect(self.status(), 'the uploader never approves').toBe(403);
	expect(await self.text()).toContain('cannot approve it');
	expect((await (await call('GET', mediaUrl(site, own.id), { token: approver })).json()).status).toBe('Draft');

	// Shown to the approver, then changed by the uploader: approving what was shown is refused, and nothing is approved.
	const shown = (await (await call('GET', mediaUrl(site, item.id), { token: approver })).json()).reviewToken;
	const widened = await call('PUT', mediaUrl(site, item.id), {
		token: manager,
		data: {
			advertiser: 'Gulf Air',
			campaign: 'Winter sun',
			startsOn: day(-1),
			endsOn: day(700)
		}
	});
	expect(widened.status(), await widened.text()).toBe(200);
	const stale = await call('POST', `${mediaUrl(site, item.id)}/approve`, {
		token: approver,
		data: { reviewed: shown }
	});
	expect(stale.status(), 'approving what is no longer the item').toBe(409);
	expect(await stale.text()).toContain('has changed since you reviewed it');
	expect(
		(
			await call('POST', `${mediaUrl(site, item.id)}/approve`, {
				token: approver,
				data: {}
			})
		).status(),
		'no review token'
	).toBe(400);

	const approved = await approve(site, item.id, approver);
	expect(approved.status(), await approved.text()).toBe(200);
	expect(await approved.json()).toMatchObject({
		status: 'Approved',
		approvedBy: accounts().mediaApprover.userName,
		playable: true,
		canApprove: false
	});
	expect((await approve(site, item.id, approver)).status(), 'approved once').toBe(409);

	// The approver changes the dates: the item is a draft again, and the approver, now its last changer, cannot approve it.
	const changed = await call('PUT', mediaUrl(site, item.id), {
		token: approver,
		data: {
			advertiser: 'Gulf Air',
			campaign: 'Winter sun',
			startsOn: day(-1),
			endsOn: day(60)
		}
	});
	expect(changed.status(), await changed.text()).toBe(200);
	expect(await changed.json()).toMatchObject({
		status: 'Draft',
		approvedBy: null,
		playable: false
	});
	expect((await approve(site, item.id, approver)).status(), 'the last changer never approves').toBe(403);

	const audit = await call('GET', `${hosts.main}/api/v1/admin/audit-entries?pageSize=200`, { token: admin });
	expect(audit.status()).toBe(200);
	const entries = (
		(await audit.json()).data as {
			action: string;
			actorName: string;
			targetId: string;
		}[]
	).filter((e) => e.targetId === item.id);
	expect(entries.map((e) => e.action)).toEqual(expect.arrayContaining(['AdvertisingMedia.Uploaded', 'AdvertisingMedia.Approved', 'AdvertisingMedia.Changed']));
	expect(entries.find((e) => e.action === 'AdvertisingMedia.Approved')!.actorName).toBe(accounts().mediaApprover.userName);
});

test('another site, its caller and its items answer 404; roles without the permissions 403; a site with advertising off 403', async () => {
	const mine = await uploaded(manager);
	const theirs = await uploaded(manager, png, {}, other);

	for (const [method, url] of [
		['GET', mediaUrl(site)],
		['GET', mediaUrl(site, mine.id)],
		['GET', `${mediaUrl(site, mine.id)}/content`],
		['POST', `${mediaUrl(site, mine.id)}/retire`]
	] as const) {
		expect((await call(method, url, { token: elsewhere })).status(), `${method} ${url} for a manager of E2E2 only`).toBe(404);
	}
	expect((await upload(elsewhere, png, 'ad.png', 'image/png')).status()).toBe(404);

	// Another site's item named under this site's route (IDOR): not found, whatever the action.
	expect((await call('GET', mediaUrl(site, theirs.id), { token: manager })).status()).toBe(404);
	expect(
		(
			await call('GET', `${mediaUrl(site, theirs.id)}/content`, {
				token: manager
			})
		).status()
	).toBe(404);
	expect(
		(
			await call('POST', `${mediaUrl(site, theirs.id)}/retire`, {
				token: manager
			})
		).status()
	).toBe(404);
	expect((await approve(site, theirs.id, approver)).status()).toBe(404);
	expect(
		(
			await call('PUT', mediaUrl(site, theirs.id), {
				token: manager,
				data: {
					advertiser: 'X',
					campaign: 'Y',
					startsOn: day(0),
					endsOn: day(1)
				}
			})
		).status()
	).toBe(404);
	expect((await call('GET', mediaUrl(other, theirs.id), { token: manager })).status(), 'under its own site it is found').toBe(200);

	const border = (await signIn(accounts().BorderShiftSupervisor)).accessToken;
	expect((await call('GET', mediaUrl(site), { token: border })).status(), 'the border shift supervisor holds neither permission').toBe(403);

	const offList = await call('GET', mediaUrl(off), { token: manager });
	expect(offList.status(), 'a site that never allowed advertising').toBe(403);
	expect(await offList.text()).toContain('Advertising is off for this site.');
	expect((await upload(manager, png, 'ad.png', 'image/png', {}, off)).status()).toBe(403);
});

test('a display fetches with its own credential only an approved item of its own site within its dates, while it is set to advertising', async () => {
	const playing = await uploaded(manager);
	expect((await approve(site, playing.id, approver)).status()).toBe(200);
	const draft = await uploaded(manager);
	const future = await uploaded(manager, png, {
		startsOn: day(10),
		endsOn: day(20)
	});
	expect((await approve(site, future.id, approver)).status()).toBe(200);
	const theirs = await uploaded(manager, png, {}, other);
	expect((await approve(other, theirs.id, approver)).status()).toBe(200);

	const shown = await fetchAsDisplay(playing.id, displays.advertising);
	expect(shown.status).toBe(200);
	expect(shown.headers.get('content-type')).toBe('image/png');
	expect(shown.headers.get('content-security-policy')).toContain('sandbox');
	expect(shown.headers.get('cache-control')).toContain('no-store');

	// No credential, a user's token, a wrong key: 401 before anything is looked up.
	expect((await fetch(displayFetchUrl(playing.id, displays.advertising.code))).status, 'no credential').toBe(401);
	expect(
		(
			await fetch(displayFetchUrl(playing.id, displays.advertising.code), {
				headers: { Authorization: `Bearer ${admin}` }
			})
		).status,
		'a user token'
	).toBe(401);
	expect((await fetchAsDisplay(playing.id, displays.advertising, displays.instructions.credential)).status, "another display's key").toBe(401);

	for (const [name, id, display] of [
		['a draft', draft.id, displays.advertising],
		['before its first day', future.id, displays.advertising],
		["another site's approved item", theirs.id, displays.advertising],
		['an item that does not exist', crypto.randomUUID(), displays.advertising],
		['a display set to instructions', playing.id, displays.instructions]
	] as const) {
		expect((await fetchAsDisplay(id, display)).status, name).toBe(404);
	}
});

test('a retired item never plays again and is not changed; turning advertising off stops the library and every fetch', async () => {
	const item = await uploaded(manager);
	expect((await approve(site, item.id, approver)).status()).toBe(200);
	expect((await fetchAsDisplay(item.id, displays.advertising)).status).toBe(200);

	const retired = await call('POST', `${mediaUrl(site, item.id)}/retire`, {
		token: manager
	});
	expect(retired.status(), await retired.text()).toBe(200);
	expect(await retired.json()).toMatchObject({
		status: 'Retired',
		playable: false
	});
	expect((await fetchAsDisplay(item.id, displays.advertising)).status, 'a retired item').toBe(404);
	expect(
		(
			await call('POST', `${mediaUrl(site, item.id)}/retire`, {
				token: manager
			})
		).status()
	).toBe(409);
	expect((await approve(site, item.id, approver)).status()).toBe(409);
	expect(
		(
			await call('PUT', mediaUrl(site, item.id), {
				token: manager,
				data: {
					advertiser: 'X',
					campaign: 'Y',
					startsOn: day(0),
					endsOn: day(1)
				}
			})
		).status()
	).toBe(409);

	const playing = await uploaded(manager);
	expect((await approve(site, playing.id, approver)).status()).toBe(200);
	const policyUrl = `${hosts.main}/api/v1/admin/sites/${site}/advertising-policy`;
	const turnedOff = await call('PUT', policyUrl, {
		token: admin,
		data: {
			allowed: false,
			excludedZones: [],
			reason: 'E2E: the operator withdrew advertising',
			reviewedProfileId: null
		}
	});
	expect(turnedOff.status(), await turnedOff.text()).toBe(200);
	expect((await call('GET', mediaUrl(site), { token: manager })).status(), 'the library while advertising is off').toBe(403);
	expect((await fetchAsDisplay(playing.id, displays.advertising)).status, 'an approved item while advertising is off').toBe(404);
});
