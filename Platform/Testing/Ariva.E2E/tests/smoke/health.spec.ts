import { send } from './support/client';
import { expect, smoke, target } from './support/smoke';

// ARV-128: the hosts are up and ready (their readiness includes the database), and the web app serves its sign-in page
// with the production security headers and without a console error or CSP violation. Reads only.

const hosts: [string, string | undefined][] = [
	['Ariva.Api.Main', target.main],
	['Ariva.Api.Ingest', target.ingest],
	['Ariva.Api.Integration', target.integration]
];

for (const [name, url] of hosts.filter((entry): entry is [string, string] => entry[1] !== undefined)) {
	smoke(`${name} answers its readiness probe Healthy`, { tag: '@read' }, async () => {
		const response = await send('GET', `${url}/health/readiness`);
		expect(response.status, `${name} readiness`).toBe(200);
		expect(response.json<{ status: string }>().status, `${name} readiness`).toBe('Healthy');
	});
}

smoke('the web app serves its sign-in page with the security headers, no console error and no CSP violation', { tag: '@read' }, async ({ page, guards }) => {
	const response = await page.goto('/login');
	expect(response?.status(), 'the sign-in page').toBe(200);
	const headers = response!.headers();
	expect(headers['content-security-policy'], 'Content-Security-Policy').toContain("frame-ancestors 'none'");
	expect(headers['content-security-policy']).not.toContain("'unsafe-inline'");
	expect(headers['x-content-type-options']).toBe('nosniff');
	await expect(page.getByTestId('login-form')).toBeVisible();
	await guards.expectClean();
});
