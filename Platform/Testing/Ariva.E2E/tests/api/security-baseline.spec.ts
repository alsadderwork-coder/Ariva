import { expect, test } from '@playwright/test';
import { expectApiSecurityHeaders, expectNoLeak, expectProblemDetails } from '../support/api-assertions';
import { apiHosts, foreignOrigin, hosts, probes, systemInfoPath, webOrigin } from '../support/hosts';
import { markupFragments, sqlInjectionPayloads, xssPayloads } from '../support/payloads';

// Security baseline of every API host (docs/security/cwe-controls.md). Until the authentication story lands the
// only authentication scheme is the Ariva.Deny placeholder, so every request that is not a health probe is
// anonymous and must be refused. Default deny runs before routing can answer 404 or 405: an anonymous caller gets
// 401 for unknown routes and wrong methods too, which also stops route enumeration. The genuine 404 and 405
// ProblemDetails bodies are covered by DefaultDenyTests in Ariva.UnitTests with an authenticated test scheme.

const systemInfo = `${hosts.main}${systemInfoPath}`;

test.describe('default deny (CWE-862, CWE-306)', () => {
	test('system info returns 401 without a token', async ({ request }) => {
		const response = await request.get(systemInfo);

		await expectProblemDetails(response, 401);
		expect(response.headers()['www-authenticate']).toBe('Bearer');
	});

	test('system info returns 401 with a malformed bearer token', async ({ request }) => {
		const response = await request.get(systemInfo, { headers: { Authorization: 'Bearer not-a-jwt' } });

		await expectProblemDetails(response, 401);
		expect(response.headers()['cache-control']).toBe('no-store');
	});

	test('system info returns 401 with an unsigned token', async ({ request }) => {
		const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
		const claims = Buffer.from(JSON.stringify({ sub: 'admin', role: 'SystemAdministrator' })).toString('base64url');

		const response = await request.get(systemInfo, { headers: { Authorization: `Bearer ${header}.${claims}.` } });

		await expectProblemDetails(response, 401);
	});

	test('system info returns 401 with a token in the access_token query string', async ({ request }) => {
		const response = await request.get(`${systemInfo}?access_token=eyJhbGciOiJIUzI1NiJ9.e30.c2lnbmF0dXJl`);

		await expectProblemDetails(response, 401);
		expect(response.headers()['cache-control']).toBe('no-store');
	});

	for (const host of apiHosts) {
		test(`${host.name} refuses an anonymous request to a route that is not allowlisted`, async ({ request }) => {
			const response = await request.get(`${host.url}${systemInfoPath}`);

			await expectProblemDetails(response, 401);
		});
	}
});

test.describe('security headers', () => {
	for (const host of apiHosts) {
		test(`${host.name} sends the security headers and no Server header`, async ({ request }) => {
			const allowed = await request.get(`${host.url}${probes[2]}`);
			const refused = await request.get(`${host.url}${systemInfoPath}`);

			expectApiSecurityHeaders(allowed);
			expectApiSecurityHeaders(refused);
			expect(allowed.headers()['cache-control']).toBeUndefined();
		});
	}
});

test.describe('error bodies', () => {
	for (const host of apiHosts) {
		test(`${host.name} answers an unknown route with ProblemDetails and no internals (401 under default deny)`, async ({
			request
		}) => {
			const response = await request.get(`${host.url}/api/v1/does-not-exist/42`);

			await expectProblemDetails(response, 401);
		});

		test(`${host.name} answers a wrong method on a probe with ProblemDetails and no internals (401 under default deny)`, async ({
			request
		}) => {
			const response = await request.delete(`${host.url}${probes[2]}`);

			await expectProblemDetails(response, 401);
			expect(response.headers()['allow']).toBeUndefined();
		});

		test(`${host.name} rejects TRACE without echoing the request`, async ({ request }) => {
			const marker = `trace-probe-${Date.now()}`;

			const response = await request.fetch(`${host.url}${probes[2]}`, {
				method: 'TRACE',
				headers: { 'X-Ariva-Trace-Probe': marker }
			});

			await expectProblemDetails(response, 401);
			expect(await response.text()).not.toContain(marker);
		});
	}
});

test.describe('CORS', () => {
	test('a disallowed origin gets no Access-Control-Allow-Origin', async ({ request }) => {
		const simple = await request.get(`${hosts.main}${probes[2]}`, { headers: { Origin: foreignOrigin } });
		const preflight = await request.fetch(`${hosts.main}${systemInfoPath}`, {
			method: 'OPTIONS',
			headers: {
				Origin: foreignOrigin,
				'Access-Control-Request-Method': 'GET',
				'Access-Control-Request-Headers': 'authorization'
			}
		});

		expect(simple.status()).toBe(200);
		expect(simple.headers()['access-control-allow-origin']).toBeUndefined();
		expect(simple.headers()['access-control-allow-credentials']).toBeUndefined();
		expect(preflight.headers()['access-control-allow-origin']).toBeUndefined();
		expect(preflight.headers()['access-control-allow-methods']).toBeUndefined();
	});

	test('the web origin is allowed exactly, never as a wildcard', async ({ request }) => {
		const response = await request.get(`${hosts.main}${probes[2]}`, { headers: { Origin: webOrigin } });

		expect(response.headers()['access-control-allow-origin']).toBe(webOrigin);
		expect(response.headers()['access-control-allow-credentials']).toBe('true');
	});
});

test.describe('request limits (CWE-120)', () => {
	test('a 2 MB body is refused with 401 before the server reads it', async ({ request }) => {
		// Authorization runs before model binding, so the protected route answers 401 and Kestrel never buffers
		// the body; Kestrel's 1 MB MaxRequestBodySize (413) applies once an endpoint reads a body.
		const response = await request.post(systemInfo, {
			headers: { 'Content-Type': 'application/json' },
			data: Buffer.alloc(2 * 1024 * 1024, 'a')
		});

		await expectProblemDetails(response, 401);
	});

	test('a request line over 8 KB is rejected with 414', async ({ request }) => {
		const response = await request.get(`${hosts.main}${probes[2]}?q=${'a'.repeat(9_000)}`);

		expect(response.status()).toBe(414);
		expect(response.headers()['server']).toBeUndefined();
	});

	test('request headers over 32 KB are rejected with 431', async ({ request }) => {
		const headers = Object.fromEntries(
			Array.from({ length: 5 }, (_, index) => [`X-Ariva-Filler-${index}`, 'a'.repeat(7_000)])
		);

		const response = await request.get(`${hosts.main}${probes[2]}`, { headers });

		expect(response.status()).toBe(431);
		expect(response.headers()['server']).toBeUndefined();
	});
});

test.describe('injection payloads in query strings never cause a server error (CWE-89, CWE-79)', () => {
	const routes = [...probes, systemInfoPath];
	const payloads = [...sqlInjectionPayloads, ...xssPayloads];

	for (const host of apiHosts) {
		test(`${host.name} answers every payload below 500 and never reflects markup`, async ({ request }) => {
			for (const route of routes) {
				for (const payload of payloads) {
					const value = encodeURIComponent(payload);
					const url = `${host.url}${route}?q=${value}&id=${value}&sort=${value}&siteCode=${value}`;

					const response = await request.get(url);
					const body = await response.text();

					expect(response.status(), `${route} with ${payload}`).toBeLessThan(500);
					expectApiSecurityHeaders(response);
					expectNoLeak(body, `${route} with ${payload}`);
					for (const fragment of markupFragments) {
						expect(body, `${route} with ${payload}`).not.toContain(fragment);
					}
				}
			}
		});
	}
});
