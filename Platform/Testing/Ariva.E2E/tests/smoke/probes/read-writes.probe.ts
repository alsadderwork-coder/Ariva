import { send } from '../support/client';
import { expect, smoke, target } from '../support/smoke';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only, run against a non-local target that does not exist
// (*.invalid, never resolved). A test declared @read that tries to write is still stopped: the smoke client refuses
// anything but a read, the session calls and the target's own addresses before sending, and the guarded page aborts a
// write before it leaves the browser. Nothing reaches the network.

smoke('a @read test cannot write to a non-local target', { tag: '@read' }, async ({ page }, testInfo) => {
	testInfo.skip(target.local, 'the refusals apply to non-local targets only');
	await expect(send('POST', `${target.base}/api/v1/admin/sites`, { json: { code: 'XX', name: 'XX' } })).rejects.toThrow(/sends only reads to a non-local target; POST .* refused/);
	await expect(send('DELETE', `${target.base}/api/v1/admin/displays/0199a000-0000-7000-8000-00000000a1a1`)).rejects.toThrow(/refused/);
	await expect(send('PUT', `${target.ingest}/api/v1/ingest/zones/A/crossings`, { json: {} })).rejects.toThrow(/refused/);
	await expect(send('GET', 'https://elsewhere.invalid/api/v1/sites')).rejects.toThrow(/not one of the smoke target's addresses; refused/);

	// The browser: the probe page is answered here (never fetched). Its form posts to an admin route; the guarded page aborts
	// that write before it leaves the browser, while a read of the same origin is answered (here, by the route below).
	const form = '<!doctype html><title>probe</title><form method="post" action="/api/v1/admin/sites"><button>write</button></form>';
	await page.route(`${target.base}/probe`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: form }));
	await page.route(`${target.base}/api/v1/sites`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
	await page.goto(`${target.base}/probe`);
	const failed = page.waitForEvent('requestfailed', (request) => request.method() === 'POST');
	await page.getByRole('button', { name: 'write' }).click();
	const blocked = await failed;
	expect(new URL(blocked.url()).pathname).toBe('/api/v1/admin/sites');
	expect(blocked.failure()?.errorText).toMatch(/BLOCKED_BY_CLIENT/);
	// The aborted form navigation ends on the browser's error page.
	await page.waitForURL(/^chrome-error:/);
	expect((await page.goto(`${target.base}/api/v1/sites`))?.status(), 'a read goes on').toBe(200);
});
