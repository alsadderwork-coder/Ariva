import { send } from './support/client';
import { expect, smoke, target } from './support/smoke';
import { credential, credentialVariables } from './support/target';

// ARV-128: what a sensor and a passenger display see, with the test credentials an administrator issued for the smoke
// (wiki 04 section 8): the Ingest device check, the display board through the API and in the player. No credential
// answers 401 on both (CWE-306). Reads only; the keys travel in headers and the player's session storage, never in an address.

smoke('the test device passes the Ingest device check with its own credential, and nothing passes without one', { tag: '@read' }, async () => {
	const url = `${target.ingest}/api/v1/ingest/device`;
	expect((await send('GET', url)).status, 'no device credential').toBe(401);

	const response = await send('GET', url, { headers: { 'X-Ariva-Device-Key': credential(credentialVariables.deviceKey) } });
	expect(response.status, 'the test device credential').toBe(200);
	expect(response.headers['cache-control']).toContain('no-store');
	const device = response.json<{ code: string; siteCode: string; queueZoneName: string; state: string; serverTimeUtc: string }>();
	expect(device.code.length, 'the device code').toBeGreaterThan(0);
	expect(device.siteCode.length, 'the device site').toBeGreaterThan(0);
	// Devices take their clock offset from this answer; a server clock far from this machine's is a finding of its own.
	const skew = Math.abs(Date.parse(device.serverTimeUtc) - Date.now());
	expect(skew, 'the Ingest clock against this machine, in milliseconds').toBeLessThan(5 * 60_000);
});

smoke('the display board answers the display credential only, and the player shows it live', { tag: '@read' }, async ({ page, guards }) => {
	const code = credential(credentialVariables.displayCode);
	const key = credential(credentialVariables.displayKey);
	const board = `${target.base}/api/v1/display/board?code=${encodeURIComponent(code)}`;
	expect((await send('GET', board)).status, 'no display credential').toBe(401);
	const answer = await send('GET', board, { headers: { 'X-Ariva-Display-Key': key } });
	expect(answer.status, 'the display credential').toBe(200);
	expect(answer.headers['cache-control']).toContain('no-store');
	expect(answer.json<{ entries: unknown[] }>().entries.length, 'the board has entries').toBeGreaterThan(0);

	// The player reads its key from the tab's session storage when the address has none: put it there before the page runs.
	await page.addInitScript(([displayCode, displayKey]) => {
		try {
			sessionStorage.setItem(`ariva.display.${displayCode}`, displayKey);
		} catch {
			// No storage: the player then shows its set-up message and the assertion below fails.
		}
	}, [code, key]);
	await page.goto(`/display?code=${encodeURIComponent(code)}`);
	await expect(page.getByTestId('board'), 'the board is live').toHaveAttribute('data-state', 'live', { timeout: 20_000 });
	await expect(page.getByTestId('board-entry').first()).toBeVisible();
	await guards.expectClean();
});
