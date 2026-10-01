// Content security policy and security headers for Ariva.Web (CWE-79), defined once and used by:
//   svelte.config.js              kit.csp in hash mode: SvelteKit hashes its inline bootstrap script, sends the
//                                 policy as a header from `vite preview` and as a meta tag in build/index.html
//   vite.config.ts                preview.headers, so the functional tests run against the production headers
//   scripts/postbuild-nginx.mjs   writes the same policy, with the script hashes, into build/nginx/default.conf
// connect-src lists the API origins the app calls, taken from the same VITE_ARIVA_* variables as
// src/lib/core/Endpoints.ts, read at build time (environment variables, then .env files in the working
// directory, which is the project folder for every npm script).
import { loadEnv } from 'vite';

/** @typedef {NonNullable<NonNullable<import('@sveltejs/kit').KitConfig['csp']>['directives']>} CspDirectives */

/** API base URLs and their local defaults; keep in step with src/lib/core/Endpoints.ts. */
const API_DEFAULTS = {
	VITE_ARIVA_API_MAIN_URL: 'http://localhost:51001',
	VITE_ARIVA_API_INGEST_URL: 'http://localhost:51002',
	VITE_ARIVA_API_CRONZ_URL: 'http://localhost:51004',
	VITE_ARIVA_API_INTEGRATION_URL: 'http://localhost:51005',
	VITE_ARIVA_SIMULATION_URL: 'http://localhost:51020'
};

/** Host whose SignalR hub the app opens over WebSockets. */
const WEBSOCKET_API = 'VITE_ARIVA_API_MAIN_URL';

/**
 * SvelteKit's route announcer, an accessibility live region in the generated root component, is visually hidden
 * with this static style attribute. style-src-attr allows exactly this attribute through 'unsafe-hashes' and its
 * sha256 hash, and no other inline style; scripts/postbuild-nginx.mjs fails the build if SvelteKit changes it.
 */
export const SVELTEKIT_ANNOUNCER_STYLE =
	'position: absolute; left: 0; top: 0; clip: rect(0 0 0 0); clip-path: inset(50%); overflow: hidden; white-space: nowrap; width: 1px; height: 1px';

/** sha256 of SVELTEKIT_ANNOUNCER_STYLE, base64. */
export const SVELTEKIT_ANNOUNCER_STYLE_HASH = 'sha256-S8qMpvofolR8Mpjy4kQvEm7m1q8clzU4dfDH0AmvZjo=';

/** Keywords that CSP expects in single quotes. */
const KEYWORDS = new Set([
	'self',
	'none',
	'unsafe-inline',
	'unsafe-eval',
	'unsafe-hashes',
	'strict-dynamic',
	'report-sample',
	'wasm-unsafe-eval'
]);

/**
 * Security headers sent with every web response, by nginx and by `vite preview`.
 * @type {Record<string, string>}
 */
export const securityHeaders = {
	'X-Content-Type-Options': 'nosniff',
	'X-Frame-Options': 'DENY',
	'Referrer-Policy': 'no-referrer',
	'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
	'Cross-Origin-Opener-Policy': 'same-origin'
};

/**
 * Reads the VITE_ARIVA_* build variables (process environment first, then .env files).
 * @param {string} [mode]
 * @returns {Record<string, string>}
 */
export function loadArivaEnv(mode = 'production') {
	return { ...API_DEFAULTS, ...loadEnv(mode, '.', 'VITE_ARIVA_') };
}

/**
 * Origins the app may connect to: every API host, plus the WebSocket origin of the main API.
 * @param {Record<string, string>} env
 * @returns {string[]}
 */
export function apiOrigins(env) {
	const origins = new Set();
	for (const key of Object.keys(API_DEFAULTS)) {
		const url = new URL(env[key] || API_DEFAULTS[key]);
		origins.add(url.origin);
		if (key === WEBSOCKET_API) {
			origins.add(`${url.protocol === 'https:' ? 'wss:' : 'ws:'}//${url.host}`);
		}
	}
	return [...origins];
}

/**
 * The policy in SvelteKit's kit.csp format (keywords without quotes).
 * style-src needs no 'unsafe-inline': Tailwind and the Svelte component styles are emitted as external CSS files,
 * and Svelte sets dynamic styles through the CSSOM (element.style), which CSP does not restrict. The one static
 * inline style attribute, SvelteKit's route announcer, is allowed by hash in style-src-attr.
 * @param {Record<string, string>} [env]
 * @returns {CspDirectives}
 */
export function cspDirectives(env = loadArivaEnv()) {
	return {
		'default-src': ['self'],
		'script-src': ['self'],
		'style-src': ['self'],
		'style-src-attr': ['unsafe-hashes', SVELTEKIT_ANNOUNCER_STYLE_HASH],
		'img-src': ['self', 'data:'],
		'font-src': ['self'],
		'connect-src': ['self', .../** @type {any[]} */ (apiOrigins(env))],
		'object-src': ['none'],
		'base-uri': ['self'],
		'form-action': ['self'],
		'frame-ancestors': ['none']
	};
}

/**
 * Serialises directives into a header value, adding extra script sources such as 'sha256-...' hashes.
 * @param {CspDirectives} directives
 * @param {string[]} [scriptSources]
 * @returns {string}
 */
export function serializeCsp(directives, scriptSources = []) {
	/** @param {string} source */
	const quote = (source) =>
		KEYWORDS.has(source) || /^(sha256|sha384|sha512|nonce)-/.test(source) ? `'${source}'` : source;

	return Object.entries(directives)
		.map(([name, sources]) => {
			const all = name === 'script-src' ? [...(sources ?? []), ...scriptSources] : (sources ?? []);
			return [name, ...all.map((source) => quote(String(source)))].join(' ');
		})
		.join('; ');
}
