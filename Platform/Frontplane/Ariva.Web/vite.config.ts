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
			server.middlewares.use((_request, response, next) => {
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
