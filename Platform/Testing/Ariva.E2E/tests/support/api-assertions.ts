import { expect, type APIResponse } from '@playwright/test';

/** What the assertions read from a response; Playwright's APIResponse and the cookie-free calls in accounts.ts both fit. */
export type ResponseLike = Pick<APIResponse, 'status' | 'headers' | 'text' | 'url'>;

/** Text that must never reach a client: stack frames, source paths and framework or runtime names. */
export const leakPatterns: readonly RegExp[] = [
	/\bat [\w.<>`]+\(/, // .NET stack frame "at Namespace.Type.Method("
	/\.cs:line \d+/,
	/Microsoft\.AspNetCore/,
	/\bSystem\.[A-Z]\w+/,
	/Kestrel/i,
	/ASP\.NET/i,
	/\.NET \d/,
	/Exception\b/
];

/** The security headers Ariva.Api.Common adds to every API response. */
export const apiSecurityHeaders: Readonly<Record<string, string>> = {
	'x-content-type-options': 'nosniff',
	'x-frame-options': 'DENY',
	'referrer-policy': 'no-referrer',
	'content-security-policy': "default-src 'none'; frame-ancestors 'none'"
};

/** Asserts the API security headers are present and the Server header is absent. */
export function expectApiSecurityHeaders(response: ResponseLike): void {
	const headers = response.headers();
	for (const [name, value] of Object.entries(apiSecurityHeaders)) {
		expect(headers[name], `${name} on ${response.url()}`).toBe(value);
	}
	expect(headers['server'], `Server header on ${response.url()}`).toBeUndefined();
}

/** Asserts the body leaks no stack trace, source path or framework name. */
export function expectNoLeak(body: string, context: string): void {
	for (const pattern of leakPatterns) {
		expect(body, `${context} must not match ${pattern}`).not.toMatch(pattern);
	}
}

/**
 * Asserts an RFC 9457 ProblemDetails answer with the given status, the API security headers and no leaked
 * internals, and returns the parsed body.
 */
export async function expectProblemDetails(
	response: ResponseLike,
	status: number
): Promise<Record<string, unknown>> {
	expect(response.status(), `status of ${response.url()}`).toBe(status);
	expect(response.headers()['content-type']).toContain('application/problem+json');
	expectApiSecurityHeaders(response);

	const text = await response.text();
	expectNoLeak(text, `${status} body of ${response.url()}`);

	const problem = JSON.parse(text) as Record<string, unknown>;
	expect(problem.status).toBe(status);
	expect(typeof problem.title).toBe('string');
	expect(problem).not.toHaveProperty('exception');
	expect(problem).not.toHaveProperty('stackTrace');
	return problem;
}
