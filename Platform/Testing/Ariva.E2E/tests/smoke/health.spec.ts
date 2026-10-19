import { smokeSites } from './support/checks';
import { send, type SmokeResponse } from './support/client';
import { expect, smoke, target } from './support/smoke';

// ARV-128: the hosts are up, and the web app serves its sign-in page with the production security headers and without a
// console error or CSP violation. Reads only, and no credential: every request below is anonymous.
//
// Against a local target (the hosts' own ports, no ingress) each host answers its readiness probe Healthy (readiness
// includes the database), and the anonymous read below with its own 401.
//
// Against a deployed target (through the ingress) the probes are private (ARV-085): every Ingress host answers /health,
// /healthz and anything under them 404 at its server level, before any location, and the kubelet probes the pods' own
// ports, so readiness cannot be read from here (wiki 04 section 8, check 2, reads it inside the cluster). Each API host and
// the web host must answer every probe path 404 (or 403), and that answer must be the ingress's own: none of the pods'
// marks (X-Frame-Options, Referrer-Policy, a ProblemDetails body), and the chart's headers (ARV-084: one
// Strict-Transport-Security of at least a year with includeSubDomains, nosniff, frame-ancestors 'none').
// Each API host then proves it is up behind the ingress with a read it answers itself, that changes nothing and needs no
// credential: an endpoint of its own that challenges an anonymous call, before any action runs (Main: GET /api/auth/me;
// Ingest: the device check; Integration: the flight feed's connectivity check at the smoke site, where a call without a
// token is not even recorded). How that answer is told from the ingress's: the ingress's own answers (its 308, 403, 404,
// 413, 421 and 5xx) are nginx's HTML pages, and the chart sets no authentication annotation, so it never answers 401
// itself. The host's 401 is an RFC 9457 ProblemDetails (application/problem+json, status 401, a title and a traceId) from
// its status code pages, with its scheme's challenge (WWW-Authenticate: Bearer, with realm="ariva-devices" for Ingest's
// device scheme) and its own security headers, which neither the chart nor the controller sets: X-Frame-Options DENY,
// Referrer-Policy no-referrer and Content-Security-Policy default-src 'none' (SecurityHeadersMiddleware). The local run
// makes the same read, so a change of that answer fails in every E2E run, before a release.

interface Host {
	name: string;
	url: string | undefined;
	/** The anonymous read the host answers itself with 401. */
	proof: () => string;
	/** The host's WWW-Authenticate challenge on that read. */
	challenge: RegExp;
}

const hosts: Host[] = [
	{ name: 'Ariva.Api.Main', url: target.main, proof: () => '/api/auth/me', challenge: /^Bearer$/ },
	{ name: 'Ariva.Api.Ingest', url: target.ingest, proof: () => '/api/v1/ingest/device', challenge: /^Bearer realm="ariva-devices"$/ },
	{
		name: 'Ariva.Api.Integration',
		url: target.integration,
		// The site only names the route: an anonymous call is refused before the site is looked at.
		proof: () => `/api/v1/integration/sites/${encodeURIComponent(smokeSites()[0])}/flights/check`,
		challenge: /^Bearer$/
	}
];

/** Every probe path the pods serve: the .NET hosts' three, the web pod's /healthz. */
const probePaths = ['/health/startup', '/health/readiness', '/health/liveness', '/healthz'];

const yearSeconds = 31_536_000;
const frameAncestorsNone = /(^|;)\s*frame-ancestors\s+'none'\s*(;|$)/i;

/** The values of a header; Node's fetch joins repeated headers with ", " (none of the values checked here holds a comma). */
function values(response: SmokeResponse, name: string): string[] {
	const value = response.headers[name];
	return value === undefined ? [] : value.split(',').map((part) => part.trim());
}

/** What the ingress adds to every answer of a host (ARV-084), or what is missing of it. */
function ingressHeaderProblems(response: SmokeResponse): string[] {
	const problems: string[] = [];
	const hsts = values(response, 'strict-transport-security');
	if (hsts.length !== 1) problems.push(`${hsts.length} Strict-Transport-Security headers, not one`);
	else {
		const parts = hsts[0].split(';').map((part) => part.trim()).filter(Boolean);
		const maxAge = parts.map((part) => /^max-age\s*=\s*"?(\d+)"?$/i.exec(part)).find(Boolean);
		if (!maxAge || Number(maxAge[1]) < yearSeconds) problems.push(`Strict-Transport-Security max-age ${maxAge?.[1] ?? 'unset'}, under a year (${yearSeconds})`);
		if (!parts.some((part) => /^includesubdomains$/i.test(part))) problems.push('Strict-Transport-Security without includeSubDomains');
	}
	const nosniff = values(response, 'x-content-type-options');
	if (nosniff.length === 0 || nosniff.some((value) => value.toLowerCase() !== 'nosniff')) problems.push('no X-Content-Type-Options: nosniff');
	if (!values(response, 'content-security-policy').some((policy) => frameAncestorsNone.test(policy))) problems.push("no Content-Security-Policy with frame-ancestors 'none'");
	return problems;
}

/** What only a pod writes, never the ingress: the .NET hosts' and the web pod's own headers, a host's ProblemDetails. */
function podMarks(response: SmokeResponse): string[] {
	const marks: string[] = [];
	if (response.headers['x-frame-options'] !== undefined) marks.push('X-Frame-Options');
	if (response.headers['referrer-policy'] !== undefined) marks.push('Referrer-Policy');
	if ((response.headers['content-type'] ?? '').startsWith('application/problem+json')) marks.push('a ProblemDetails body');
	return marks;
}

/** Why an answer is not the host's own 401 to an anonymous call, or nothing when it is. */
function hostAnswerProblems(response: SmokeResponse, challenge: RegExp): string[] {
	const problems: string[] = [];
	if (response.status !== 401) problems.push(`answered ${response.status}, not 401`);
	const contentType = response.headers['content-type'] ?? 'none';
	if (!contentType.startsWith('application/problem+json')) problems.push(`Content-Type ${contentType}, not application/problem+json`);
	else {
		let body: { status?: unknown; title?: unknown; traceId?: unknown } = {};
		try {
			body = JSON.parse(response.text) as typeof body;
		} catch {
			problems.push('a ProblemDetails that is not JSON');
		}
		if (body.status !== 401) problems.push(`ProblemDetails status ${String(body.status)}, not 401`);
		if (typeof body.title !== 'string' || body.title.length === 0) problems.push('ProblemDetails without a title');
		if (typeof body.traceId !== 'string' || body.traceId.length === 0) problems.push('ProblemDetails without a traceId');
	}
	const challenged = values(response, 'www-authenticate');
	if (challenged.length !== 1 || !challenge.test(challenged[0])) problems.push(`WWW-Authenticate ${response.headers['www-authenticate'] ?? 'none'}, not ${challenge.source}`);
	if (response.headers['x-frame-options'] !== 'DENY') problems.push('no X-Frame-Options: DENY');
	if (response.headers['referrer-policy'] !== 'no-referrer') problems.push('no Referrer-Policy: no-referrer');
	if (!values(response, 'content-security-policy').some((policy) => policy.startsWith("default-src 'none'"))) problems.push("no Content-Security-Policy default-src 'none'");
	return problems;
}

/** Every probe path of a host, through the ingress: not routed, answered by the ingress itself, with its headers. */
async function expectProbesNotRouted(name: string, url: string): Promise<void> {
	// The ingress sends Strict-Transport-Security over HTTPS only (RFC 6797 section 7.2), and a deployed host is reached over HTTPS.
	expect(new URL(url).protocol, `${name} is reached over HTTPS`).toBe('https:');
	for (const path of probePaths) {
		const response = await send('GET', `${url}${path}`);
		expect([403, 404], `${name} GET ${path} through the ingress is not routed (the probes stay on the pods' own ports)`).toContain(response.status);
		expect(podMarks(response), `${name} GET ${path}: the ingress's own answer, no pod's`).toEqual([]);
		expect(ingressHeaderProblems(response), `${name} GET ${path}: the ingress's headers`).toEqual([]);
	}
}

for (const host of hosts) {
	const url = host.url;
	if (url === undefined) continue;
	if (target.local) {
		smoke(`${host.name} answers its readiness probe Healthy and an anonymous read with its own 401`, { tag: '@read' }, async () => {
			const response = await send('GET', `${url}/health/readiness`);
			expect(response.status, `${host.name} readiness`).toBe(200);
			expect(response.json<{ status: string }>().status, `${host.name} readiness`).toBe('Healthy');
			const path = host.proof();
			expect(hostAnswerProblems(await send('GET', `${url}${path}`), host.challenge), `${host.name} GET ${path} without a credential`).toEqual([]);
		});
	} else {
		smoke(`${host.name} keeps its health probes off the ingress and answers an anonymous read itself behind it`, { tag: '@read' }, async () => {
			await expectProbesNotRouted(host.name, url);
			const path = host.proof();
			expect(hostAnswerProblems(await send('GET', `${url}${path}`), host.challenge), `${host.name} answers GET ${path} itself, behind the ingress`).toEqual([]);
		});
	}
}

const webTitle = 'the web app serves its sign-in page with the security headers, no console error and no CSP violation';

smoke(target.local ? webTitle : `${webTitle}, and keeps its health probes off the ingress`, { tag: '@read' }, async ({ page, guards }) => {
	const response = await page.goto('/login');
	expect(response?.status(), 'the sign-in page').toBe(200);
	const headers = response!.headers();
	expect(headers['content-security-policy'], 'Content-Security-Policy').toContain("frame-ancestors 'none'");
	expect(headers['content-security-policy']).not.toContain("'unsafe-inline'");
	expect(headers['x-content-type-options']).toBe('nosniff');
	// ARV-092 (ASVS V4.1.1): the web image's nginx names the page's charset. The local stack serves the web app with
	// `vite preview`, which sends text/html alone, so only a deployed run (the image behind the ingress) checks it.
	if (!target.local) expect(headers['content-type'], 'Content-Type').toMatch(/^text\/html;\s*charset=utf-8$/i);
	await expect(page.getByTestId('login-form')).toBeVisible();
	await guards.expectClean();
	if (target.local) return;
	// The sign-in page above is the web pod's own answer behind the ingress.
	await expectProbesNotRouted('the web host', target.base);
	// ARV-092 (ASVS V13.4.1): the image keeps no copy of its nginx configuration in the web root, so the path gets the app's
	// page. Deployed runs only: `vite preview` serves the whole build folder, the configuration included.
	const config = await send('GET', `${target.base}/nginx/default.conf`, { headers: { Accept: 'text/html' } });
	expect(config.headers['content-type'] ?? '', 'GET /nginx/default.conf').toMatch(/^text\/html/i);
	expect(config.text, 'GET /nginx/default.conf').not.toMatch(/server_tokens|listen\.d/);
});
