// Writes the production Content-Security-Policy into build/nginx/default.conf after `vite build`.
// SvelteKit's SPA fallback page (build/index.html) boots with one inline script whose content, and therefore
// hash, changes with every build. nginx must send that hash in script-src, so this step hashes every inline
// script in build/index.html, checks that SvelteKit's own meta policy carries the same hashes, and replaces the
// fail-closed Content-Security-Policy line of the copied nginx config with the policy from csp.config.js.
// It also checks that the route announcer style allowed by hash in csp.config.js is still the one SvelteKit renders.
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
	cspDirectives,
	serializeCsp,
	SVELTEKIT_ANNOUNCER_STYLE,
	SVELTEKIT_ANNOUNCER_STYLE_HASH
} from '../csp.config.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const indexPath = path.join(root, 'build', 'index.html');
const nginxPath = path.join(root, 'build', 'nginx', 'default.conf');
const cspLine = /add_header\s+Content-Security-Policy\s+"[^"]*"\s+always;/;
const kitRootTemplate = path.join(
	root,
	'node_modules',
	'@sveltejs',
	'kit',
	'src',
	'core',
	'sync',
	'write_root.js'
);

const sha256 = (text) => `sha256-${createHash('sha256').update(text, 'utf8').digest('base64')}`;

function fail(message) {
	console.error(`postbuild-nginx: ${message}`);
	process.exit(1);
}

if (!fs.existsSync(indexPath)) fail(`${indexPath} not found; run vite build first.`);
if (!fs.existsSync(nginxPath))
	fail(`${nginxPath} not found; static/nginx/default.conf was not copied.`);

const html = fs.readFileSync(indexPath, 'utf8');
const inlineScripts = [...html.matchAll(/<script(?![^>]*\ssrc=)[^>]*>([\s\S]*?)<\/script>/g)].map(
	(m) => m[1]
);
if (inlineScripts.length === 0) fail('no inline script found in build/index.html.');

const hashes = inlineScripts.map(sha256);

const announcer = fs
	.readFileSync(kitRootTemplate, 'utf8')
	.match(/id="svelte-announcer"[^>]*style="([^"]*)"/);
if (!announcer)
	fail(`route announcer not found in ${kitRootTemplate}; review style-src-attr in csp.config.js.`);
if (
	announcer[1] !== SVELTEKIT_ANNOUNCER_STYLE ||
	sha256(announcer[1]) !== SVELTEKIT_ANNOUNCER_STYLE_HASH
) {
	fail(
		`SvelteKit's route announcer style changed to "${announcer[1]}"; update SVELTEKIT_ANNOUNCER_STYLE and its hash in csp.config.js.`
	);
}

const meta = html.match(/<meta http-equiv="content-security-policy" content="([^"]*)"/i);
if (!meta)
	fail(
		'SvelteKit did not write a content-security-policy meta tag; check kit.csp in svelte.config.js.'
	);
for (const hash of hashes) {
	if (!meta[1].includes(`'${hash}'`))
		fail(`inline script hash ${hash} is missing from SvelteKit's meta policy.`);
}

const policy = serializeCsp(cspDirectives(), hashes);
const nginx = fs.readFileSync(nginxPath, 'utf8');
if (!cspLine.test(nginx))
	fail('no "add_header Content-Security-Policy ... always;" line in build/nginx/default.conf.');
fs.writeFileSync(
	nginxPath,
	nginx.replace(cspLine, `add_header Content-Security-Policy "${policy}" always;`)
);

console.log(
	`postbuild-nginx: wrote Content-Security-Policy with ${hashes.length} script hash(es) to build/nginx/default.conf`
);
