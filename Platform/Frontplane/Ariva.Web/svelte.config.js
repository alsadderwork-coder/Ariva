import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import { cspDirectives } from './csp.config.js';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	// Consult https://svelte.dev/docs/kit/integrations for more information about preprocessors
	preprocess: vitePreprocess(),
	compilerOptions: {
		runes: true
	},
	kit: {
		// Static SPA build served by nginx (see Dockerfile and static/nginx/default.conf), as in Aman.Web.
		adapter: adapter({
			pages: 'build',
			assets: 'build',
			fallback: 'index.html' // for SPA routing
		}),
		alias: {
			'@': './src/lib'
		},
		// CWE-79: strict content security policy (see csp.config.js). Hash mode adds the hash of SvelteKit's inline
		// bootstrap script, so script-src needs no 'unsafe-inline'.
		csp: {
			mode: 'hash',
			directives: cspDirectives()
		}
	}
};

export default config;
