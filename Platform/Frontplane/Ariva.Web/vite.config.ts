import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig, type Plugin } from 'vite';
import { cspDirectives, securityHeaders, serializeCsp } from './csp.config.js';

/**
 * Vite applies preview.headers only to the files its own static middleware serves, but SvelteKit's preview
 * middleware answers every request. This plugin runs first in the preview server and sets preview.headers on
 * every response; for HTML pages SvelteKit then replaces Content-Security-Policy with the same policy plus the
 * hash of its inline bootstrap script (kit.csp in svelte.config.js). It also switches off the permissive CORS
 * (Access-Control-Allow-Origin: *) that SvelteKit enables for the preview server, because nginx sends none.
 *
 * Page requests are never answered with 304 Not Modified: SvelteKit's preview renders its own fallback page, and a
 * 304 would carry the base policy without the hash of that page's bootstrap script, so the browser would block it
 * on reload. nginx sends the hash on every status (add_header ... always), so production needs no such rule.
 */
function previewSecurityHeaders(): Plugin {
	return {
		name: 'ariva-preview-security-headers',
		enforce: 'post',
		config() {
			return { preview: { cors: false } };
		},
		configurePreviewServer(server) {
			const headers = server.config.preview.headers ?? {};
			server.middlewares.use((request, response, next) => {
				const pathname = (request.url ?? '/').split('?')[0];
				if (!/\.[a-z0-9]+$/i.test(pathname)) {
					delete request.headers['if-none-match'];
					delete request.headers['if-modified-since'];
				}
				for (const [name, value] of Object.entries(headers)) {
					if (value !== undefined) {
						response.setHeader(name, value);
					}
				}
				next();
			});
		}
	};
}

// The preview server sends the production security headers, the same values nginx sends (csp.config.js and
// static/nginx/default.conf), so the Playwright functional tests exercise the real policy.
export default defineConfig({
	plugins: [tailwindcss(), sveltekit(), previewSecurityHeaders()],
	server: { port: 51010 },
	preview: {
		port: 51011,
		strictPort: true,
		cors: false,
		headers: {
			...securityHeaders,
			'Content-Security-Policy': serializeCsp(cspDirectives())
		}
	}
});
