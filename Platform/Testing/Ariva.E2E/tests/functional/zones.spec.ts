import { expect, test, type Page } from '@playwright/test';
import { accounts, call, claimsOf, developmentSigningKey, headerOf, signIn, signToken, totpCode } from '../support/accounts';
import { createAirport } from '../support/airports';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { xssPayloads } from '../support/payloads';
import { allowStatuses, databaseAvailable, earlyInStep, fillSecret, fillSignIn, ownAddress, signInThroughUi } from '../support/web-auth';
import { expectStatus } from '../support/expect-status';

// ARV-053: the zones screen on the zone profile API (ARV-017) and floor plans (ARV-018). A terminal duty manager at a
// site of its own starts a draft, draws a queue zone by dragging its corners and through the vertex table, adds its
// entry and exit lines, moves a line end with the keyboard, checks the draft and publishes it after a step-up. A handler
// station manager sees the published zones of the demo airport read only. Zone names are text, never markup.

test.skip(!databaseAvailable, 'the zones screen needs the E2E database (ARIVA_E2E_SCHEMA_UPDATE=true)');
test.describe.configure({ mode: 'serial' });

const api = `${hosts.main}/api/v1/admin`;
/** The duty manager's site: one of this run alone (ARV-279), so its first draft is the site's first, on a reused database too. */
const site = (): string => accounts().webZones.sites[0];
/** The plan of floor-plans.spec.ts: script, handlers, foreign content, an outside image and a javascript: link. */
const hostileSvg = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="100" onload="alert(1)">
<script>alert(2)</script>
<rect width="50" height="50" fill="#123456" onclick="alert(3)"/>
<foreignObject width="10" height="10"><div xmlns="http://www.w3.org/1999/xhtml">x</div></foreignObject>
<image href="https://attacker.example/p.png" width="1" height="1"/>
<text x="5" y="80">Gate</text>
<a xlink:href="javascript:alert(4)"><text x="5" y="90">click</text></a>
</svg>`;

test.beforeAll(async () => {
	// The run's site gets an airport, a terminal and one 60 by 40 m level, unless a retry of this run made them already.
	const admin = (await signIn(accounts().webAdmin)).accessToken;
	const levels = await call('GET', `${api}/levels?siteCode=${site()}`, { token: admin });
	if (((await levels.json()).data as unknown[]).length > 0) return;
	const airport = await createAirport((iata) =>
		call('POST', `${api}/airports`, { token: admin, data: { iataCode: iata, name: `E2E zones ${iata}`, timeZoneId: 'Asia/Amman' } })
	);
	const terminal = await call('POST', `${api}/terminals`, {
		token: admin,
		data: { airportId: airport.id, code: 'TZ', name: 'Zones terminal', siteCode: site() }
	});
	await expectStatus(terminal, 201);
	const level = await call('POST', `${api}/levels`, {
		token: admin,
		data: { terminalId: (await terminal.json()).id, code: 'LZ', name: 'Zones hall', floorNumber: 0, widthMetres: 60, depthMetres: 40 }
	});
	await expectStatus(level, 201);
});

/** Signs in with the second factor 20 minutes old, as after a long shift, so publishing asks for a fresh code. */
async function signInAged(page: Page): Promise<void> {
	await ownAddress(page);
	await page.route('**/api/auth/login', async (route) => {
		const response = await route.fetch();
		if (response.status() !== 200) return route.fulfill({ response });
		const body = await response.json();
		const claims = claimsOf(body.accessToken);
		body.accessToken = signToken(headerOf(body.accessToken), { ...claims, auth_time: (claims.auth_time as number) - 20 * 60 }, developmentSigningKey()!);
		return route.fulfill({ response, json: body });
	});
	const account = accounts().webZones;
	await page.goto('/login');
	await fillSignIn(page, account, totpCode(account.totpSecret!, -1));
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Live operations');
}

test('a duty manager draws a queue zone and its lines, checks the draft and publishes it after a step-up', async ({ page }) => {
	test.skip(!developmentSigningKey(), 'needs the run key to age the token');
	const guards = await guardPage(page);
	await earlyInStep();
	await signInAged(page);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Zones' }).click();
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Zones');
	await expect(page.getByLabel('Site', { exact: true })).toHaveValue(site());

	await page.getByTestId('new-draft').click();
	await expect(page.getByLabel('Version')).toContainText('Draft');

	// A queue zone starts as a 4 m square in the middle of the 60 by 40 m level.
	await page.getByTestId('add-zone').click();
	await page.getByTestId('add-zone-form').getByLabel('Name').fill('Snake Z');
	await page.getByTestId('add-zone-form').getByRole('button', { name: 'Create' }).click();
	const table = page.getByTestId('vertex-table');
	await expect(table.getByRole('row')).toHaveCount(5);

	// Keyboard alternative: type a corner, then save.
	await page.getByLabel('Corner 2, x in metres').fill('36');
	await page.getByLabel('Corner 2, x in metres').press('Tab');
	// ARV-057: the queue of the visitors' lane, for the immigration screen's waits per lane.
	await page.getByTestId('zone-details').getByLabel('Lane').selectOption('VIS');
	// ARV-114a: the snake's physical capacity, which the stream checks occupancy against.
	const capacity = page.getByTestId('zone-details').getByLabel('Physical capacity (people)');
	await capacity.fill('150');
	await page.getByTestId('save-zone').click();
	await expect(page.getByText('Snake Z saved.')).toBeVisible();

	// Drag the third corner on the plan; letting go saves it.
	const handle = page.getByTestId('vertex-handle').nth(2);
	// Saving scrolled the details panel's button into view; a mouse drag needs the handle itself on screen, clear of the
	// sticky header.
	await handle.evaluate((element) => element.scrollIntoView({ block: 'center' }));
	const box = (await handle.boundingBox())!;
	await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
	await page.mouse.down();
	await page.mouse.move(box.x + box.width / 2 + 40, box.y + box.height / 2 + 30, { steps: 5 });
	await page.mouse.up();
	await expect(page.getByText('Snake Z saved.').first()).toBeVisible();
	const cornerThree = Number(await page.getByLabel('Corner 3, x in metres').inputValue());
	expect(cornerThree, 'the dragged corner moved right').toBeGreaterThan(32);

	// Above 5,000 people is refused before it is sent (the server refuses it too); its toast is closed so that it
	// covers nothing on the plan.
	await capacity.fill('6000');
	await page.getByTestId('save-zone').click();
	const refused = page.getByRole('listitem').filter({ hasText: 'The physical capacity is a whole number from 1 to 5,000, or empty.' });
	await expect(refused).toBeVisible();
	await refused.getByRole('button', { name: 'Close toast' }).click();
	await capacity.fill('150');

	// The check names what is missing.
	await page.getByTestId('validate').click();
	await expect(page.getByTestId('validation-problems')).toContainText('needs at least one entry line');
	await expect(page.getByTestId('publish')).toHaveCount(0);

	for (const [name, role] of [
		['In Z', 'Entry'],
		['Out Z', 'Exit']
	] as const) {
		await page.getByTestId('add-line').click();
		const form = page.getByTestId('add-line-form');
		await form.getByLabel('Name').fill(name);
		await form.getByLabel('Role').selectOption(role);
		await form.getByLabel('Queue zone').selectOption({ label: 'Snake Z' });
		await form.getByRole('button', { name: 'Create' }).click();
		await expect(page.getByTestId('line-details')).toContainText(name);
	}

	// Move the entry line's end along the zone's top edge with the keyboard: arrows, then Enter saves (the line is
	// replaced in place). The entry starts on the middle half of that edge, 30 to 34 m.
	await page.getByTestId('lines-list').getByRole('button', { name: /In Z/ }).click();
	const end = page.getByTestId('line-handle').nth(1);
	await expect(end).toHaveAttribute('aria-label', /End of In Z at 34, 18 m/);
	await end.focus();
	await end.press('Shift+ArrowRight');
	await expect(end).toHaveAttribute('aria-label', /at 35, 18 m/);
	await end.press('Enter');
	await expect(page.getByText('In Z saved.')).toBeVisible();

	// A move off the zone's edge is refused by the server and the line stays where it was.
	const moved = page.getByTestId('line-handle').nth(1);
	await moved.focus();
	await moved.press('Shift+ArrowDown');
	await moved.press('Enter');
	await expect(page.getByText(/must lie on an edge of zone Snake Z/)).toBeVisible();
	await expect(page.getByTestId('line-handle').nth(1)).toHaveAttribute('aria-label', /End of In Z at 35, 18 m/);

	await page.getByTestId('validate').click();
	await expect(page.getByTestId('validation-ok')).toHaveText('Ready to publish.');
	await page.getByTestId('publish').click();

	// The second factor is 20 minutes old: the step-up dialog asks for a fresh code, then the publish goes through.
	const dialog = page.getByTestId('step-up-dialog');
	await expect(dialog).toBeVisible();
	await fillSecret(page.locator('#step-up-code'), totpCode(accounts().webZones.totpSecret!));
	await dialog.getByRole('button', { name: 'Confirm' }).click();
	await expect(page.getByText(/Version \d+ is live\./)).toBeVisible();
	await expect(page.getByLabel('Version')).toContainText('Published');
	await expect(page.getByTestId('zones-read-only')).toBeVisible();
	await expect(page.getByTestId('add-zone')).toHaveCount(0);

	// The server has the shapes the screen showed.
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const history = (await (await call('GET', `${api}/zone-profiles?siteCode=${site()}`, { token })).json()) as { id: string; status: string }[];
	const live = history.find((p) => p.status === 'Published')!;
	const profile = await (await call('GET', `${api}/zone-profiles/${live.id}`, { token })).json();
	const snake = (profile.zones as { name: string; polygon: string; laneCategory: string | null; physicalCapacity: number | null }[]).find(
		(z) => z.name === 'Snake Z'
	)!;
	expect(snake.polygon.split(',')[1].split(' ').map(Number)).toEqual([36, 18]);
	expect(snake.laneCategory, 'the lane went with the published version').toBe('VIS');
	expect(snake.physicalCapacity, 'the capacity went with the published version').toBe(150);
	const entry = (profile.lines as { name: string; endX: number }[]).find((l) => l.name === 'In Z')!;
	expect(entry.endX).toBe(35);
	allowStatuses(guards, 400, 401, 404);
	await guards.expectClean();
});

test('zone names are shown as text: script payloads are neither executed nor injected', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webZones, totpCode(accounts().webZones.totpSecret!, 1));
	await page.goto('/zones');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Zones');
	await page.getByTestId('new-draft').click();
	for (const payload of xssPayloads.slice(0, 3)) {
		await page.getByTestId('add-zone').click();
		await page.getByTestId('add-zone-form').getByLabel('Name').fill(payload);
		await page.getByTestId('add-zone-form').getByRole('button', { name: 'Create' }).click();
		await expect(page.getByTestId('zone-details').getByRole('heading', { level: 2 })).toHaveText(payload);
		await expect(page.getByTestId('zones-list')).toContainText(payload);
	}
	// A line named with a payload too (a count line needs no zone).
	await page.getByTestId('add-line').click();
	await page.getByTestId('add-line-form').getByLabel('Name').fill(xssPayloads[1]);
	await page.getByTestId('add-line-form').getByLabel('Role').selectOption('Count');
	await page.getByTestId('add-line-form').getByRole('button', { name: 'Create' }).click();
	await expect(page.getByTestId('line-details').getByRole('heading', { level: 2 })).toHaveText(xssPayloads[1]);
	await expect(page.getByTestId('lines-list')).toContainText(xssPayloads[1]);
	await expect(page.locator('img[src="x"], svg[onload], iframe, [onerror], [onload]')).toHaveCount(0);
	allowStatuses(guards, 401, 404);
	await guards.expectClean();
});

/** Floor coordinates (metres) to the page's coordinates, through the canvas's own transform. */
async function screenOf(page: Page, x: number, y: number): Promise<{ x: number; y: number }> {
	return page.getByTestId('zone-canvas').evaluate(
		(svg, p) => {
			const q = new DOMPoint(p.x, p.y).matrixTransform((svg as SVGSVGElement).getScreenCTM()!);
			return { x: q.x, y: q.y };
		},
		{ x, y }
	);
}

/** The page's coordinates to floor coordinates (metres). */
async function floorOf(page: Page, x: number, y: number): Promise<{ x: number; y: number }> {
	return page.getByTestId('zone-canvas').evaluate(
		(svg, p) => {
			const q = new DOMPoint(p.x, p.y).matrixTransform((svg as SVGSVGElement).getScreenCTM()!.inverse());
			return { x: q.x, y: q.y };
		},
		{ x, y }
	);
}

/** Brings the whole plan on screen, clear of the sticky header, before a pointer gesture. */
async function showCanvas(page: Page): Promise<void> {
	await page.getByTestId('zone-editor').evaluate((element) => element.scrollIntoView({ block: 'center' }));
}

async function viewBox(page: Page): Promise<number[]> {
	return ((await page.getByTestId('zone-canvas').getAttribute('viewBox')) ?? '').split(' ').map(Number);
}

async function drag(page: Page, from: { x: number; y: number }, by: { x: number; y: number }): Promise<void> {
	await page.mouse.move(from.x, from.y);
	await page.mouse.down();
	await page.mouse.move(from.x + by.x, from.y + by.y, { steps: 6 });
	await page.mouse.up();
}

async function corners(page: Page): Promise<number[][]> {
	const rows = await page.getByTestId('vertex-table').getByRole('row').count();
	const points: number[][] = [];
	for (let n = 1; n < rows; n++)
		points.push([
			Number(await page.getByLabel(`Corner ${n}, x in metres`).inputValue()),
			Number(await page.getByLabel(`Corner ${n}, y in metres`).inputValue())
		]);
	return points;
}

test('the plan zooms, pans and fits, and a whole zone moves with its lines, by mouse, keys and touch (ARV-298)', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	await page.goto('/zones');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Zones');
	await page.getByLabel('Site', { exact: true }).selectOption(site());
	await expect(page.getByLabel('Level', { exact: true })).toContainText('LZ');
	await expect(page.getByLabel('Version')).toContainText(/Draft|Published/);
	if ((await page.getByTestId('new-draft').count()) > 0) await page.getByTestId('new-draft').click();
	await expect(page.getByLabel('Version')).toContainText('Draft');

	// Zoom: the buttons, the keys and the wheel. The wheel keeps the floor point under the pointer where it is.
	const level = page.getByTestId('zoom-level');
	await expect(level).toHaveText('100%');
	const fitted = await viewBox(page);
	await page.getByTestId('zoom-in').click();
	await expect(level).toHaveText('125%');
	expect((await viewBox(page))[2]).toBeCloseTo(fitted[2] / 1.25, 3);
	await page.getByTestId('zoom-out').click();
	await expect(level).toHaveText('100%');
	await page.getByTestId('zoom-fit').focus();
	await page.keyboard.press('+');
	await expect(level).toHaveText('125%');
	await page.keyboard.press('0');
	await expect(level).toHaveText('100%');
	await showCanvas(page);
	const corner = await screenOf(page, 6, 6);
	await page.mouse.move(corner.x, corner.y);
	await page.mouse.wheel(0, -400);
	await expect.poll(async () => Number(await page.getByTestId('zone-canvas').getAttribute('data-zoom'))).toBeGreaterThan(1.5);
	const under = await floorOf(page, corner.x, corner.y);
	expect(under.x, 'the point under the pointer stays').toBeCloseTo(6, 1);
	expect(under.y).toBeCloseTo(6, 1);

	// Pan: dragging the empty floor moves the view, not a shape; Fit brings the whole level back.
	const before = await viewBox(page);
	await drag(page, corner, { x: 120, y: 80 });
	const after = await viewBox(page);
	expect(after[0], 'the view moved left as the floor moved right').toBeLessThan(before[0]);
	expect(after[2], 'panning keeps the zoom').toBeCloseTo(before[2], 3);
	await page.getByTestId('zoom-fit').click();
	expect(await viewBox(page)).toEqual(fitted);

	// A queue zone with an entry line. A new zone starts in the middle, under the others; selected, it is drawn on top.
	await page.getByTestId('add-zone').click();
	await page.getByTestId('add-zone-form').getByLabel('Name').fill('Move Z');
	await page.getByTestId('add-zone-form').getByRole('button', { name: 'Create' }).click();
	await expect(page.getByTestId('zone-details').getByRole('heading', { level: 2 })).toHaveText('Move Z');
	await page.getByTestId('add-line').click();
	const form = page.getByTestId('add-line-form');
	await form.getByLabel('Name').fill('Move in');
	await form.getByLabel('Role').selectOption('Entry');
	await form.getByLabel('Queue zone').selectOption({ label: 'Move Z' });
	await form.getByRole('button', { name: 'Create' }).click();
	await expect(page.getByTestId('line-details')).toContainText('Move in');
	await page.getByTestId('zones-list').getByRole('button', { name: /Move Z/ }).click();
	const start = await corners(page);
	const token = (await signIn(accounts().webAdmin)).accessToken;
	const stored = async () => {
		const versions = (await (await call('GET', `${api}/zone-profiles?siteCode=${site()}`, { token })).json()) as { id: string; status: string }[];
		const draft = versions.find((p) => p.status === 'Draft')!;
		return (await (await call('GET', `${api}/zone-profiles/${draft.id}`, { token })).json()) as {
			zones: { id: string; name: string; polygon: string }[];
			lines: { name: string; zoneId: string; startX: number; startY: number; endX: number; endY: number }[];
		};
	};
	const entryBefore = (await stored()).lines.find((l) => l.name === 'Move in')!;

	// Drag the whole zone by its middle: every corner and the entry line move by the same offset, saved in one call.
	await showCanvas(page);
	const middle = await screenOf(page, (start[0][0] + start[2][0]) / 2, (start[0][1] + start[2][1]) / 2);
	await drag(page, middle, { x: 90, y: -45 });
	await expect(page.getByText('Move Z saved.')).toBeVisible();
	const moved = await corners(page);
	const dx = moved[0][0] - start[0][0];
	const dy = moved[0][1] - start[0][1];
	expect(dx, 'the zone moved right').toBeGreaterThan(2);
	expect(dy, 'and up').toBeLessThan(-1);
	moved.forEach(([x, y], i) => {
		expect(x).toBeCloseTo(start[i][0] + dx, 3);
		expect(y).toBeCloseTo(start[i][1] + dy, 3);
	});
	const saved = await stored();
	const zone = saved.zones.find((z) => z.name === 'Move Z')!;
	expect(zone.polygon.split(',').map((p) => p.split(' ').map(Number))).toEqual(moved);
	const entry = saved.lines.find((l) => l.name === 'Move in')!;
	expect(entry.zoneId).toBe(zone.id);
	expect([entry.startX, entry.startY, entry.endX, entry.endY], 'the entry line moved with its zone, by the same offset').toEqual(
		[entryBefore.startX + dx, entryBefore.startY + dy, entryBefore.endX + dx, entryBefore.endY + dy].map((v) => Math.round(v * 1000) / 1000)
	);

	// The keyboard: the selected zone takes the focus; Shift and an arrow move it 1 m, Enter saves.
	const shape = page.getByRole('button', { name: /^Move Z\. Arrow keys move the whole zone/ });
	await shape.focus();
	await shape.press('Shift+ArrowLeft');
	await shape.press('Enter');
	await expect(page.getByText('Move Z saved.').first()).toBeVisible();
	await expect.poll(async () => (await corners(page))[0][0]).toBeCloseTo(moved[0][0] - 1, 3);

	// A corner typed and not saved: moving the whole zone waits until it is saved or undone.
	await page.getByLabel('Corner 1, x in metres').fill(String(moved[0][0] - 1.5));
	await page.getByLabel('Corner 1, x in metres').press('Tab');
	await shape.focus();
	await shape.press('ArrowRight');
	await expect(page.getByText(/before moving the whole zone/)).toBeVisible();
	await page.getByRole('button', { name: 'Undo changes' }).click();

	// Two fingers: a pinch apart zooms in around their middle.
	const fit = await page.getByTestId('zone-canvas').getAttribute('data-zoom');
	await showCanvas(page);
	const a = await screenOf(page, 8, 30);
	const b = await screenOf(page, 14, 30);
	await page.getByTestId('zone-canvas').evaluate(
		(svg, [p, q]) => {
			const send = (type: string, id: number, x: number, y: number) =>
				svg.dispatchEvent(new PointerEvent(type, { pointerId: id, pointerType: 'touch', clientX: x, clientY: y, bubbles: true, isPrimary: id === 1 }));
			send('pointerdown', 1, p.x, p.y);
			send('pointerdown', 2, q.x, q.y);
			for (let i = 1; i <= 5; i++) send('pointermove', 2, q.x + i * 20, q.y);
			send('pointerup', 2, q.x + 100, q.y);
			send('pointerup', 1, p.x, p.y);
		},
		[a, b]
	);
	expect(Number(await page.getByTestId('zone-canvas').getAttribute('data-zoom')), 'the pinch zoomed in').toBeGreaterThan(Number(fit) * 1.5);
	allowStatuses(guards, 401, 404);
	await guards.expectClean();
});

test('an uploaded SVG plan with script is shown as an image from a blob URL: nothing runs, the CSP holds', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webAdmin);
	await page.goto('/zones');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('Zones');
	await page.getByLabel('Site', { exact: true }).selectOption(site());
	await expect(page.getByLabel('Level', { exact: true })).toContainText('LZ');

	await page.getByTestId('upload-plan').click();
	await page.getByLabel(/Plan file/).setInputFiles({ name: 'hall.svg', mimeType: 'image/svg+xml', buffer: Buffer.from(hostileSvg) });
	await page.getByLabel('Metres per pixel').fill('0.3');
	await page.getByRole('button', { name: 'Upload', exact: true }).click();
	await expect(page.getByText('Floor plan uploaded.')).toBeVisible();

	const image = page.getByTestId('zone-canvas').locator('image');
	await expect(image).toHaveAttribute('href', /^blob:/);
	// 200 by 100 pixels at 0.3 m a pixel: 60 by 30 m from the origin.
	await expect(image).toHaveAttribute('width', '60');
	await expect(image).toHaveAttribute('height', '30');
	await page.waitForTimeout(500);
	expect(guards.dialogs, 'no script of the plan ran').toEqual([]);
	await expect(page.locator('foreignObject')).toHaveCount(0);
	allowStatuses(guards, 404);
	await guards.expectClean();
});

test('a handler station manager sees the demo airport zones read only', async ({ page }) => {
	await signInThroughUi(page, accounts().webHandler);
	await page.getByTestId('app-sidebar').getByRole('link', { name: 'Zones' }).click();
	await expect(page.getByLabel('Version')).toContainText('Published');
	await expect(page.getByTestId('zones-read-only')).toContainText('You can view the zones');
	for (const action of ['new-draft', 'add-zone', 'add-line', 'upload-plan', 'discard']) await expect(page.getByTestId(action)).toHaveCount(0);
	await page.getByTestId('zones-list').getByRole('button').first().click();
	await expect(page.getByTestId('zone-details')).toBeVisible();
	await expect(page.getByTestId('vertex-handle')).toHaveCount(0);
	await expect(page.getByTestId('save-zone')).toHaveCount(0);
	await expect(page.getByLabel('Corner 1, x in metres')).toHaveAttribute('readonly', '');

	// ARV-298: viewing needs the tools too; without edit rights a drag on a zone pans the view and moves nothing.
	const cornerBefore = await page.getByLabel('Corner 1, x in metres').inputValue();
	await showCanvas(page);
	const box = (await page.getByTestId('zone-shape').last().locator('polygon').boundingBox())!;
	const viewBefore = await viewBox(page);
	await drag(page, { x: box.x + box.width / 2, y: box.y + box.height / 2 }, { x: 60, y: 0 });
	expect((await viewBox(page))[0], 'the view moved').toBeLessThan(viewBefore[0]);
	await expect(page.getByLabel('Corner 1, x in metres')).toHaveValue(cornerBefore);
	// The keys move nothing either, even on a zone given the focus: no move is sent.
	const moves: string[] = [];
	page.on('request', (request) => {
		if (request.url().includes('/zone-profiles/') && request.method() !== 'GET') moves.push(`${request.method()} ${request.url()}`);
	});
	const shape = page.getByTestId('zone-shape').last().locator('polygon');
	await shape.evaluate((polygon) => (polygon as SVGElement).focus());
	await expect(shape, 'the keys go to the zone').toBeFocused();
	await page.keyboard.press('ArrowRight');
	await page.keyboard.press('Shift+ArrowDown');
	await page.keyboard.press('Enter');
	await page.waitForTimeout(500);
	expect(moves, 'no change was sent').toEqual([]);
	await expect(page.getByLabel('Corner 1, x in metres')).toHaveValue(cornerBefore);
	await page.getByTestId('zoom-in').click();
	await expect(page.getByTestId('zoom-level')).toHaveText('125%');

	// The server refuses what the screen hides.
	const token = (await signIn(accounts().webHandler)).accessToken;
	expect((await call('POST', `${api}/zone-profiles/drafts`, { token, data: { siteCode: 'DMO' } })).status()).toBe(403);
});

test('in Arabic the editor mirrors its controls and keeps the plan in floor coordinates', async ({ page }) => {
	const guards = await guardPage(page);
	await signInThroughUi(page, accounts().webHandler);
	await page.goto('/zones');
	await page.getByTestId('language-toggle').click();
	await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
	await expect(page.getByRole('heading', { level: 1 })).toHaveText('المناطق');
	await expect(page.getByTestId('zone-canvas')).toBeVisible();
	await expect(page.getByTestId('zone-shape').first()).toBeVisible();
	allowStatuses(guards, 404);
	await guards.expectClean();
});
