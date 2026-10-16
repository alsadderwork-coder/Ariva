import { expect, test } from '@playwright/test';
import { accounts, call, databaseAvailable, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// ARV-141: instruction slides through the API. An administrator of site E2E3 only (no other spec writes slides there, so
// its order is this spec's) creates, changes, orders, retires and restores slides; every text field carries markup that
// comes back unchanged as JSON text (the player and the screen show it as text, functional/display-slides.spec.ts); the
// checks refuse control characters, long words, unknown languages, icons and scopes; a border supervisor of DMO makes
// the slides another site's administrator cannot reach (404), and roles without the permission get 403.

test.skip(!databaseAvailable, 'needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/admin/display-slides`;
let e2e3: string;
let dmo: string;
const created: { id: string; token: () => string }[] = [];

function slide(changes: Record<string, unknown> = {}, site = 'E2E3') {
	return {
		siteCode: site,
		scope: 'Site',
		targets: [],
		icon: 'Information',
		durationSeconds: 10,
		pinned: false,
		texts: { en: { title: 'Follow the signs', body: null }, ar: { title: 'اتبع اللافتات', body: 'إلى الكاونتر' } },
		...changes
	};
}

test.beforeAll(async () => {
	e2e3 = (await signIn(accounts().calendarAdmin)).accessToken;
	dmo = (await signIn(accounts().webBorder)).accessToken;
});

test.afterAll(async () => {
	// Slides are retired, never deleted: what this spec made stops showing anywhere.
	for (const { id, token } of created) await call('POST', `${api}/${id}/retire`, { token: token() });
});

test('markup in every text field comes back unchanged as JSON text, never interpreted', async () => {
	const texts = {
		en: { title: xssPayloads[0], body: xssPayloads[1] },
		ar: { title: xssPayloads[2], body: xssPayloads[3] },
		pt: { title: xssPayloads[4], body: xssPayloads[5] },
		sw: { title: 'Pasipoti', body: '<b>bold</b> & "quoted"' }
	};
	const response = await call('POST', api, { token: e2e3, data: slide({ texts, icon: 'Passport', durationSeconds: 12, pinned: true }) });
	expect(response.status(), await response.text()).toBe(201);
	expect(response.headers()['content-type']).toContain('application/json');
	const body = await response.json();
	created.push({ id: body.id, token: () => e2e3 });
	expect(body).toMatchObject({ siteCode: 'E2E3', scope: 'Site', icon: 'Passport', durationSeconds: 12, pinned: true, status: 'Active' });
	expect(body.texts).toEqual(texts);

	const read = await call('GET', `${api}/${body.id}`, { token: e2e3 });
	expect(await read.json()).toMatchObject({ texts });
	const listed = await call('GET', `${api}?siteCode=E2E3`, { token: e2e3 });
	expect((await listed.json()).find((s: { id: string }) => s.id === body.id).texts).toEqual(texts);
});

test('words, icons, scopes and times outside the rules are refused', async () => {
	const refused: [string, Record<string, unknown>][] = [
		['a control character in a title', { texts: { en: { title: 'Bell\u0007', body: null } } }],
		['a line break in a body', { texts: { en: { title: 'Title', body: 'Two\nlines' } } }],
		['a bidirectional override', { texts: { ar: { title: '\u202Eجواز', body: null } } }],
		['a zero width space', { texts: { en: { title: 'Pass\u200Bport', body: null } } }],
		['a title of 81 characters', { texts: { en: { title: 'x'.repeat(81), body: null } } }],
		['a body of 241 characters', { texts: { en: { title: 'Title', body: 'x'.repeat(241) } } }],
		['a blank title', { texts: { en: { title: '   ', body: 'Body' } } }],
		['no words', { texts: {} }],
		['an unknown language', { texts: { fr: { title: 'Passeport', body: null } } }],
		['five languages', { texts: { en: { title: 'a', body: null }, ar: { title: 'b', body: null }, pt: { title: 'c', body: null }, sw: { title: 'd', body: null }, fr: { title: 'e', body: null } } }],
		['an unknown icon', { icon: 'Skull' }],
		['an icon as a number', { icon: '1' }],
		['an icon as markup', { icon: xssPayloads[0] }],
		['a scope in lower case', { scope: 'site' }],
		['an unknown scope', { scope: 'Airport' }],
		['a site slide naming a zone', { targets: ['A-CIT'] }],
		['a zone slide naming no zone', { scope: 'Zone', targets: [] }],
		['a zone that is not published', { scope: 'Zone', targets: ['NOT-A-ZONE'] }],
		['a display that does not exist', { scope: 'Display', targets: ['NOPE-1'] }],
		['21 displays', { scope: 'Display', targets: Array.from({ length: 21 }, (_, i) => `D-${i}`) }],
		['4 seconds', { durationSeconds: 4 }],
		['61 seconds', { durationSeconds: 61 }],
		['markup as a site code', { siteCode: xssPayloads[1] }],
		['another site\'s queue zone (DMO)', { scope: 'Zone', targets: ['A-CIT'] }],
		['markup as a language', { texts: { [xssPayloads[0]]: { title: 'Bell\u0007', body: null }, en: { title: 'Fine', body: null } } }],
		['markup as a language with a missing title', { texts: { [xssPayloads[1]]: { title: null, body: 'x' } } }]
	];
	for (const [label, changes] of refused) {
		const response = await call('POST', api, { token: e2e3, data: slide(changes) });
		expect(response.status(), label).toBe(400);
		// The parsed answer: an echo escaped by the JSON encoder (\u003C) would pass a check of the raw text.
		const answer = JSON.stringify(await response.json());
		for (const fragment of [...markupFragments, 'alert(1)']) expect(answer, `${label}: no echo of ${fragment}`).not.toContain(fragment);
	}
});

test('attack payloads as a site code answer without an echo and never reach the database as SQL', async () => {
	for (const payload of [...sqlInjectionPayloads, ...xssPayloads, '../DMO', 'D'.repeat(300)]) {
		for (const [method, url, data] of [
			['GET', `${api}?siteCode=${encodeURIComponent(payload)}`, undefined],
			['PUT', `${api}/order?siteCode=${encodeURIComponent(payload)}`, { ids: [] }]
		] as const) {
			const response = await call(method, url, { token: e2e3, data });
			expect(response.status(), `${method} ${payload.slice(0, 30)}`).toBeLessThan(500);
			expect([400, 404], `${method} ${payload.slice(0, 30)}`).toContain(response.status());
			const text = await response.text();
			for (const fragment of markupFragments) expect(text, fragment).not.toContain(fragment);
			expect(text).not.toContain('UNION');
		}
	}
	expect((await call('GET', `${api}?siteCode=E2E3`, { token: e2e3 })).status(), 'the table is still there').toBe(200);
});

test('a slide is changed, ordered, retired and restored; the order lists every slide of the site once', async () => {
	const first = await (await call('POST', api, { token: e2e3, data: slide({ texts: { en: { title: 'Order one', body: null } } }) })).json();
	const second = await (await call('POST', api, { token: e2e3, data: slide({ texts: { en: { title: 'Order two', body: null } } }) })).json();
	created.push({ id: first.id, token: () => e2e3 }, { id: second.id, token: () => e2e3 });
	expect(second.position).toBe(first.position + 1);

	const changed = await call('PUT', `${api}/${second.id}`, { token: e2e3, data: slide({ icon: 'Baggage', texts: { pt: { title: 'Bagagem', body: null } } }) });
	expect(changed.status(), await changed.text()).toBe(200);
	expect(await changed.json()).toMatchObject({ icon: 'Baggage', texts: { pt: { title: 'Bagagem', body: null } }, position: second.position });
	expect((await call('PUT', `${api}/${second.id}`, { token: e2e3, data: slide({}, 'DMO') })).status(), 'a slide stays in its site').toBe(400);

	const all: string[] = (await (await call('GET', `${api}?siteCode=E2E3`, { token: e2e3 })).json()).map((s: { id: string }) => s.id);
	const reversed = [...all].reverse();
	const ordered = await call('PUT', `${api}/order?siteCode=E2E3`, { token: e2e3, data: { ids: reversed } });
	expect(ordered.status(), await ordered.text()).toBe(200);
	expect((await ordered.json()).map((s: { id: string; position: number }) => [s.id, s.position])).toEqual(reversed.map((id, i) => [id, i + 1]));
	for (const ids of [reversed.slice(1), [...reversed.slice(1), reversed[1]], [...reversed.slice(1), '0199a000-0000-7000-8000-00000000a1a1'], ['not-an-id']]) {
		expect((await call('PUT', `${api}/order?siteCode=E2E3`, { token: e2e3, data: { ids } })).status(), JSON.stringify(ids)).toBe(400);
	}
	// A site code outside the caller's sites answers like a site that does not exist ([SiteScoped]).
	expect((await call('PUT', `${api}/order?siteCode=DMO`, { token: e2e3, data: { ids: reversed } })).status(), 'another site').toBe(404);
	await call('PUT', `${api}/order?siteCode=E2E3`, { token: e2e3, data: { ids: all } });

	expect((await (await call('POST', `${api}/${second.id}/retire`, { token: e2e3 })).json()).status).toBe('Retired');
	expect((await (await call('POST', `${api}/${second.id}/retire`, { token: e2e3 })).json()).status, 'retiring twice is harmless').toBe('Retired');
	expect((await (await call('POST', `${api}/${second.id}/restore`, { token: e2e3 })).json()).status).toBe('Active');
});

test("another site's slide answers like one that does not exist; roles without the permission get 403", async () => {
	const own = await call('POST', api, { token: dmo, data: slide({ scope: 'Zone', targets: ['A-CIT', 'A-RES'], icon: 'EGate', texts: { en: { title: 'Scoped to DMO', body: null } } }, 'DMO') });
	expect(own.status(), await own.text()).toBe(201);
	const id = (await own.json()).id;
	created.push({ id, token: () => dmo });

	for (const [method, path, data] of [
		['GET', `${api}/${id}`, undefined],
		['PUT', `${api}/${id}`, slide({ pinned: true, texts: { en: { title: 'Taken over', body: null } } }, 'DMO')],
		['POST', `${api}/${id}/retire`, undefined],
		['POST', `${api}/${id}/restore`, undefined]
	] as const) {
		const response = await call(method, path, { token: e2e3, data });
		expect(response.status(), `${method} ${path} from another site`).toBe(404);
		expect(await response.text()).not.toContain('Scoped to DMO');
	}
	const listing = await call('GET', `${api}?siteCode=DMO`, { token: e2e3 });
	expect(listing.status(), 'another site is a site that does not exist ([SiteScoped])').toBe(404);
	expect(await listing.text()).not.toContain('Scoped to DMO');
	expect((await (await call('GET', api, { token: e2e3 })).json()).map((s: { siteCode: string }) => s.siteCode), 'only its own site without a filter')
		.not.toContain('DMO');
	expect((await call('POST', api, { token: e2e3, data: slide({}, 'DMO') })).status(), 'creating in another site').toBe(400);
	const kept = await (await call('GET', `${api}/${id}`, { token: dmo })).json();
	expect(kept).toMatchObject({ status: 'Active', pinned: false, texts: { en: { title: 'Scoped to DMO' } } });

	const handler = (await signIn(accounts().webHandler)).accessToken;
	const observer = (await signIn(accounts().ValidationObserver)).accessToken;
	for (const token of [handler, observer]) {
		expect((await call('GET', `${api}?siteCode=DMO`, { token })).status()).toBe(403);
		expect((await call('PUT', `${api}/${id}`, { token, data: slide({}, 'DMO') })).status()).toBe(403);
		expect((await call('POST', `${api}/${id}/retire`, { token })).status()).toBe(403);
	}
	expect((await call('GET', `${api}?siteCode=DMO`)).status(), 'anonymous').toBe(401);
});
