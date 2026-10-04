import { expect, test } from '@playwright/test';
import { guardPage } from '../support/browser-guards';
import { hosts } from '../support/hosts';
import { allowStatuses } from '../support/web-auth';

// ARV-060: the TickerQ dashboard of Ariva.Api.Cronz loads under its own content security policy (its two inline scripts by
// hash, nothing else inline or evaluated, nothing framed), asks for its key before it shows any job, and with the run's key
// shows the report delivery job and keeps its live connection.

const dashboard = `${hosts.cronz}/tickerq`;

test('the jobs dashboard loads under its own policy, asks for the key and works with it', async ({ page }) => {
	const guards = await guardPage(page);
	await page.goto(`${dashboard}/`);
	// TickerQ's host-mode sign-in: one access key field, kept by the browser app and sent as the Authorization header.
	await expect(page.getByRole('heading', { name: 'Host Authentication' })).toBeVisible({ timeout: 15_000 });
	await expect(page.getByText('ReportDeliveries')).toHaveCount(0);

	await page.getByRole('textbox', { name: /Access Key/ }).fill(process.env.ARIVA_E2E_CRONZ_KEY!);
	await page.getByRole('button', { name: 'Set Access Key' }).click();
	await expect(page.getByRole('heading', { name: 'Host Authentication' })).toHaveCount(0, { timeout: 15_000 });
	await page.screenshot({ path: test.info().outputPath('dashboard-signed-in.png') });
	// The hub's WebSockets: every one opened and every one closed, so a connection TickerQ drops after the handshake shows.
	const sockets: { url: string; closed: boolean }[] = [];
	page.on('websocket', (socket) => {
		if (!socket.url().includes('/ticker-notification-hub')) return;
		const entry = { url: socket.url(), closed: false };
		sockets.push(entry);
		socket.on('close', () => (entry.closed = true));
	});
	await page.goto(`${dashboard}/cron-tickers`);
	await expect(page.getByText('ReportDeliveries').first()).toBeVisible({ timeout: 15_000 });
	await expect(page.getByText('WebSocket Connected')).toBeVisible({ timeout: 15_000 });
	// Still the one connection ten seconds on: the hub kept it, and nothing secret is in its address.
	await page.waitForTimeout(10_000);
	await expect(page.getByText('WebSocket Connected')).toBeVisible();
	expect(sockets.length, 'one hub connection').toBe(1);
	expect(sockets[0].closed, 'the hub keeps the connection').toBe(false);
	expect(sockets[0].url).not.toContain('access_token');
	await page.screenshot({ path: test.info().outputPath('dashboard-cron-tickers.png') });

	// Before the key its API and its hub answer 401, which the page and SignalR log as errors; those, and only those, are
	// expected. Policy violations are checked as for every screen.
	allowStatuses(guards, 401);
	const expected = /SignalR|negotiation with the server|Failed to start the connection/;
	guards.consoleErrors.splice(0, guards.consoleErrors.length, ...guards.consoleErrors.filter((m) => !expected.test(m)));
	// One refusal is expected and wanted: the bundled vue-echarts probes for custom elements with new Function inside a
	// try and falls back when the policy refuses it (no 'unsafe-eval'). Anything else refused fails the test.
	const probe = /^script-src blocked eval \(http:\/\/[^/]+\/tickerq\/assets\/index-[A-Za-z0-9_-]+\.js:\d+\)$/;
	expect(
		(await guards.cspViolations()).filter((v) => !probe.test(v)),
		'no script or style refused by the page policy'
	).toEqual([]);
	// expectClean would count the probe again; the rest of its checks, as for every screen.
	expect(guards.dialogs, 'dialogs opened by the page').toEqual([]);
	expect(guards.pageErrors, 'uncaught page errors').toEqual([]);
	expect(guards.consoleErrors, 'console errors and CSP messages').toEqual([]);
});
