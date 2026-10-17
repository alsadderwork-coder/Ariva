import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// ARV-128: the smoke suite runs against a deployed environment, so it must never write, attack or load one. Three layers:
//   1. Declaration: every smoke test names exactly one kind, @read, @write, @attack or @load. A test that names none, or
//      more than one, is refused everywhere; against a non-local target only @read is allowed. The refusal is thrown while
//      Playwright collects the tests, so the run stops before any test starts (and an auto fixture checks again at run time).
//   2. Structure: a smoke spec imports only from ./support/ (the guarded test, its client and hub), so no spec can reach
//      Playwright's own test, the E2E suites' helpers (which write) or a raw HTTP client; playwright.smoke.config.ts refuses
//      to load otherwise.
//   3. Requests: against a non-local target the smoke client and the browser send GET (and HEAD) only, plus the sign-in,
//      the web app's session refresh and sign-out.

export const smokeTags = ['@read', '@write', '@attack', '@load'] as const;
export type SmokeTag = (typeof smokeTags)[number];

/** Why a test may not be declared or run here, or undefined when it may. */
export function refusal(title: string, tags: readonly string[], local: boolean): string | undefined {
	// Distinct: Playwright also takes a tag from a word of the title ("a @read test ...").
	const kinds = [...new Set(tags)].filter((tag): tag is SmokeTag => (smokeTags as readonly string[]).includes(tag));
	if (kinds.length !== 1) {
		return `ARV-128: smoke test "${title}" must name exactly one of ${smokeTags.join(', ')} (it names ${kinds.length === 0 ? 'none' : kinds.join(' and ')}); refused`;
	}
	if (!local && kinds[0] !== '@read') {
		return `ARV-128: smoke test "${title}" is ${kinds[0]}; the smoke suite runs only @read tests against a non-local target; refused`;
	}
	return undefined;
}

/** The only writes a read-only run makes: sign in, the web app's session refresh at start, and sign out. */
export const sessionPaths = ['/api/auth/login', '/api/auth/refresh', '/api/auth/logout'] as const;

/** Whether a request may be sent; against a local target everything may. */
export function requestAllowed(method: string, url: string, local: boolean): boolean {
	if (local) return true;
	const verb = method.toUpperCase();
	if (verb === 'GET' || verb === 'HEAD') return true;
	if (verb !== 'POST') return false;
	let pathname: string;
	try {
		pathname = new URL(url).pathname;
	} catch {
		return false;
	}
	return (sessionPaths as readonly string[]).some((allowed) => pathname === allowed || pathname.endsWith(allowed));
}

const importPattern = /\b(?:import|export)\s+(?:type\s+)?(?:[^'";]*?\s+from\s+)?['"]([^'"]+)['"]|\bimport\s*\(\s*['"]([^'"]+)['"]\s*\)|\brequire\s*\(\s*['"]([^'"]+)['"]\s*\)/g;

/** The import specifiers of a source text (static, dynamic and require). */
export function importsOf(source: string): string[] {
	return [...source.matchAll(importPattern)].map((match) => match[1] ?? match[2] ?? match[3]);
}

/**
 * What a smoke spec may not write (security review recommendation 2): a raw HTTP or WebSocket client, or a new browser context or
 * page, would reach the target past the smoke client's and the guarded page's request checks. Matched in the source as written.
 */
const forbiddenCalls: { what: string; re: RegExp }[] = [
	{ what: 'fetch(', re: /\bfetch\s*\(/ },
	{ what: 'WebSocket(', re: /\bWebSocket\s*\(/ },
	{ what: 'XMLHttpRequest', re: /\bXMLHttpRequest\b/ },
	{ what: 'newContext', re: /\bnewContext\b/ },
	{ what: 'newPage', re: /\bnewPage\b/ },
	{ what: '.request.', re: /\.\s*request\s*\./ }
];

/** Structure problems of one smoke spec or probe source: its imports (see structureProblems) and the calls it may not make. */
export function sourceProblems(file: string, source: string, support = supportFolder): string[] {
	const problems: string[] = [];
	const name = path.basename(file);
	for (const specifier of importsOf(source)) {
		const resolved = specifier.startsWith('.') ? path.resolve(path.dirname(file), specifier) : undefined;
		const inSupport = resolved !== undefined && path.dirname(resolved) === support && /^[a-z-]+$/.test(path.basename(resolved));
		if (!inSupport) problems.push(`${name} imports ${specifier.slice(0, 80)}`);
	}
	if (/\bimport\s*\(\s*[^'"\s]/.test(source) || /\brequire\s*\(\s*[^'"\s]/.test(source)) problems.push(`${name} imports a computed module`);
	for (const { what, re } of forbiddenCalls) if (re.test(source)) problems.push(`${name} uses ${what}`);
	return problems;
}

/** The folder of the smoke suite's own support modules (this file's folder). */
export const supportFolder = path.dirname(fileURLToPath(import.meta.url));

/**
 * Structure problems of the given smoke spec or probe files: an import that does not resolve to a module of
 * tests/smoke/support (so never Playwright itself, the E2E suites' helpers or an HTTP client), a dynamic import or require
 * with a computed specifier, and a raw request or a new context or page (sourceProblems).
 */
export function structureProblems(files: string[], support = supportFolder): string[] {
	return files.flatMap((file) => sourceProblems(file, fs.readFileSync(file, 'utf8'), support));
}
