import crypto from 'node:crypto';
import http from 'node:http';
import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { provisionLoadSite, randomLetters, secondFactorSignIn } from '../support/load-site';
import { box, lasting, live, renamed, videoFixture, withCodec } from '../support/media';
import { sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-142a: the advertisement library of sites of its own (fresh codes each run, so no other spec shares them). Images are
// typed by their bytes and written anew (metadata, comments and anything after the image's end are gone), stored under
// generated keys and recorded as drafts; only an approved item within its dates plays, and the approver is a second person
// with a second factor, naming the item as it was shown (its review token: 409 once it changed). Each person may attempt
// 30 uploads in 10 minutes, so the attacks and the probes have accounts of their own. Every endpoint answers 403 while the
// site's advertising policy is off and 404 outside the caller's sites; another site's item answers 404 under this site's
// route (IDOR). A display fetches an item with its own credential only: an approved item of its own site within its dates
// while it is set to advertising and the policy allows it.
// ARV-142b: MP4 (H.264) and WebM (VP8, VP9, AV1) videos are typed by their container and kept without their metadata, with
// their length; markup in free space is zeroed, and data after the end, polyglots, other codecs, fragmented, live, truncated,
// too long (over 30 seconds) and too large (over 20 MB, the installation's defaults) videos are refused. An approved video
// plays on a display like an image.
// ARV-092: every item goes out inline under a name the server makes from its id and stored type, never the uploaded name.

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
let videoMaker: string;
let videoAttacker: string;
let pathProber: string;
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

/**
 * Streams a video upload offering `offered` bytes of file, without a declared length (chunked) or, with `declared`, with
 * its whole length in Content-Length and `Expect: 100-continue`, writing the body only when the server asks for it. It
 * stops writing once the server answers: what was sent by then shows where the server stopped reading. A server that
 * answers before taking the whole body closes the connection, and a write can fail before the answer is read (a plain
 * fetch fails with EPIPE); the result then has status 0 and `closed`.
 */
function streamUpload(token: string, offered: number, declared = false): Promise<{ status: number; body: string; sent: number; closed: boolean }> {
	const boundary = `ariva${crypto.randomBytes(8).toString('hex')}`;
	const field = (name: string, value: string) => `--${boundary}\r\nContent-Disposition: form-data; name="${name}"\r\n\r\n${value}\r\n`;
	const head = Buffer.from(
		field('advertiser', 'Gulf Air') +
			field('campaign', 'Streamed') +
			field('startsOn', day(0)) +
			field('endsOn', day(30)) +
			`--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="spot.mp4"\r\nContent-Type: video/mp4\r\n\r\n`
	);
	const tail = Buffer.from(`\r\n--${boundary}--\r\n`);
	const headers: Record<string, string> = { Authorization: `Bearer ${token}`, 'Content-Type': `multipart/form-data; boundary=${boundary}` };
	if (declared) {
		headers['Content-Length'] = String(head.length + offered + tail.length);
		headers['Expect'] = '100-continue';
	}
	return new Promise((resolve) => {
		let sent = 0;
		let answered = false;
		let settled = false;
		const settle = (result: { status: number; body: string; sent: number; closed: boolean }) => {
			if (settled) return;
			settled = true;
			request.destroy();
			resolve(result);
		};
		const request = http.request(mediaUrl(site), { method: 'POST', headers }, (response) => {
			answered = true;
			const chunks: Buffer[] = [];
			response.on('data', (chunk: Buffer) => chunks.push(chunk));
			response.on('end', () => settle({ status: response.statusCode ?? 0, body: Buffer.concat(chunks).toString('utf8'), sent, closed: false }));
		});
		request.on('error', () => {
			if (!answered) settle({ status: 0, body: '', sent, closed: true });
		});
		const chunk = Buffer.alloc(256 * 1024);
		const pump = () => {
			while (!answered && sent < offered) {
				const part = chunk.subarray(0, Math.min(chunk.length, offered - sent));
				sent += part.length;
				if (!request.write(part)) {
					request.once('drain', pump);
					return;
				}
			}
			if (!answered) request.end(tail);
		};
		const send = () => {
			request.write(head);
			pump();
		};
		if (declared) {
			request.on('continue', send);
			request.flushHeaders();
		} else send();
	});
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
	videoMaker = (await signIn(accounts().mediaVideos)).accessToken;
	videoAttacker = (await signIn(accounts().mediaVideoAttacker)).accessToken;
	pathProber = (await signIn(accounts().mediaPaths)).accessToken;
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
	expect(content.headers.get('content-disposition'), 'a server name, never summer.png').toBe(`inline; filename="advertisement-${item.id}.png"`);
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
		const response = await upload(pathProber, png, fileName, 'image/png');
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

test('a video is typed by its container, kept without its metadata and with its length, and plays on a display once approved', async () => {
	for (const [name, type, length] of [
		['h264-aac.mp4', 'video/mp4', 2000],
		['vp9-opus.webm', 'video/webm', 2008]
	] as const) {
		const original = videoFixture(name);
		// Declared as something else: the container decides.
		const created = await upload(videoMaker, original, name, 'application/octet-stream');
		expect(created.status(), await created.text()).toBe(201);
		const item = await created.json();
		expect(item, name).toMatchObject({
			contentType: type,
			widthPixels: 160,
			heightPixels: 90,
			durationMilliseconds: length,
			sizeBytes: original.length,
			status: 'Draft'
		});
		const content = await fetch(`${mediaUrl(site, item.id)}/content`, { headers: { Authorization: `Bearer ${videoMaker}` } });
		expect(content.status).toBe(200);
		expect(content.headers.get('content-type')).toBe(type);
		expect(content.headers.get('content-disposition'), name).toBe(`inline; filename="advertisement-${item.id}.${type.split('/')[1]}"`);
		expect(content.headers.get('content-security-policy')).toContain('sandbox');
		expect(content.headers.get('x-content-type-options')).toBe('nosniff');
		const bytes = Buffer.from(await content.arrayBuffer());
		expect(bytes.length, 'the media is not moved').toBe(original.length);
		expect(bytes.toString('latin1'), 'the title in its metadata is gone').not.toContain('Fixture title');
		expect(crypto.createHash('sha256').update(bytes).digest('hex')).toBe(item.sha256);
	}

	// Markup hidden in free space at the end: taken, the free space zeroed.
	const hidden = await upload(
		videoMaker,
		Buffer.concat([videoFixture('h264-aac.mp4'), box('free', markup), box('uuid', Buffer.concat([Buffer.alloc(16), Buffer.from(markup)]))]),
		'spot.mp4',
		'video/mp4'
	);
	expect(hidden.status(), await hidden.text()).toBe(201);
	const zeroed = await fetch(`${mediaUrl(site, (await hidden.json()).id)}/content`, { headers: { Authorization: `Bearer ${videoMaker}` } });
	expect(Buffer.from(await zeroed.arrayBuffer()).toString('latin1')).not.toContain('<script');

	// Approved by a second person with a second factor, a video plays on the display like an image.
	const spot = await upload(videoMaker, videoFixture('av1.webm'), 'spot.webm', 'video/webm');
	expect(spot.status(), await spot.text()).toBe(201);
	const video = await spot.json();
	expect((await fetchAsDisplay(video.id, displays.advertising)).status, 'a draft').toBe(404);
	const approved = await approve(site, video.id, approver);
	expect(approved.status(), await approved.text()).toBe(200);
	expect((await approved.json()).status).toBe('Approved');
	const shown = await fetchAsDisplay(video.id, displays.advertising);
	expect(shown.status).toBe(200);
	expect(shown.headers.get('content-type')).toBe('video/webm');
	expect(shown.headers.get('content-disposition'), 'the display fetch too').toBe(`inline; filename="advertisement-${video.id}.webm"`);
	expect(shown.headers.get('content-security-policy')).toContain('sandbox');
	expect(shown.headers.get('cache-control')).toContain('no-store');
	expect((await fetchAsDisplay(video.id, displays.instructions)).status, 'a display set to instructions').toBe(404);
});

test('video upload attacks are refused without an echo: data after the end, polyglots, other codecs, fragmented, live, too long and too large', async () => {
	const mp4 = videoFixture('h264-aac.mp4');
	const webm = videoFixture('vp9-opus.webm');
	const refused: [string, Buffer][] = [
		['a page after the last box', Buffer.concat([mp4, Buffer.from(markup)])],
		['a page after the last element', Buffer.concat([webm, Buffer.from(markup)])],
		['an MP4 signature over a page', Buffer.concat([Buffer.from([0, 0, 0, 0x18]), Buffer.from('ftypisom'), Buffer.alloc(12), Buffer.from(markup)])],
		['an MP4 file type box and nothing else', Buffer.concat([Buffer.from([0, 0, 0, 0x18]), Buffer.from('ftypisom'), Buffer.alloc(12)])],
		['a WebM signature over a page', Buffer.concat([Buffer.from([0x1a, 0x45, 0xdf, 0xa3]), Buffer.from(markup)])],
		['HEVC', withCodec(mp4, 'hvc1')],
		['a movie fragment', renamed(mp4, 'free', 'moof')],
		['a live WebM of unknown size', live(webm)],
		['a truncated MP4', mp4.subarray(0, mp4.length - 10)],
		['a truncated WebM', webm.subarray(0, webm.length / 2)]
	];
	for (const [name, content] of refused) {
		const response = await upload(videoAttacker, content, 'spot.mp4', 'video/mp4');
		expect(response.status(), name).toBe(400);
		const text = await response.text();
		expect(text, name).toContain('The video is not an MP4 with H.264 video');
		expect(text, `${name}: nothing sent is echoed`).not.toContain('<script');
	}

	const tooLong = await upload(videoAttacker, lasting(mp4, 30_001), 'spot.mp4', 'video/mp4');
	expect(tooLong.status(), 'over 30 seconds').toBe(400);
	expect(await tooLong.text()).toContain('The video is longer than 30 seconds.');

	const tooLarge = await upload(videoAttacker, Buffer.concat([mp4, box('free', Buffer.alloc(20 * 1024 * 1024))]), 'spot.mp4', 'video/mp4');
	expect(tooLarge.status(), 'over 20 MB').toBe(413);
	expect(await tooLarge.text()).toContain('The file is larger than 20 MB.');
	// Sent without a length (chunked), a body is cut off where the installation's limit is, not at the endpoint's 100 MB.
	// Each try proves the cut-off (the server stops reading near 20 MB); a write can fail before the answer is read, so
	// up to three tries are made for the answer itself. A body cut off in the filter never reaches the service, so the
	// tries cost no upload attempts.
	const before = (await (await call('GET', mediaUrl(site), { token: videoAttacker })).json()).length;
	let streamed = await streamUpload(videoAttacker, 60 * 1024 * 1024);
	for (let attempt = 1; attempt < 3 && streamed.closed; attempt++) {
		expect(streamed.sent, 'the server closed the connection near 20 MB').toBeLessThan(40 * 1024 * 1024);
		streamed = await streamUpload(videoAttacker, 60 * 1024 * 1024);
	}
	expect(streamed.status, streamed.body).toBe(413);
	expect(streamed.body).toContain('The file is larger than 20 MB.');
	expect(streamed.sent, 'the server stopped reading near 20 MB, not at the 100 MB ceiling').toBeLessThan(40 * 1024 * 1024);
	expect((await (await call('GET', mediaUrl(site), { token: videoAttacker })).json()).length, 'nothing is stored').toBe(before);
	// Declared larger than the installation takes, a body is refused before its form is read (the endpoint's ceiling is 100 MB).
	const declared = await streamUpload(videoAttacker, 25 * 1024 * 1024, true);
	expect(declared.status, declared.body).toBe(413);
	expect(declared.body).toContain('The file is larger than 20 MB.');
	expect(declared.sent, 'answered before any of the body was asked for').toBe(0);
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
