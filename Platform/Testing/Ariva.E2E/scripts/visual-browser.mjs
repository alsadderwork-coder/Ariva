#!/usr/bin/env node
// ARV-075: the browser that renders the visual regression screenshots. Baselines are only comparable when every pixel
// comes from the same browser build, fonts and Linux image, so the visual project never uses a local browser: it
// connects to this Playwright server, running in the official Playwright image pinned by digest (the same in CI, in
// the baseline update workflow and on a developer machine). The app's own fonts (DM Sans, Noto Sans Arabic) are bundled
// in Ariva.Web through package-lock.json; the image only supplies the fallbacks.
//
//   node scripts/visual-browser.mjs start   starts the server on 127.0.0.1:3123 (a random path) and prints the ARIVA_E2E_VISUAL_WS value
//   node scripts/visual-browser.mjs stop
//
// The container uses the host network (it opens the web app on localhost) and the Playwright server of this checkout's
// own node_modules/playwright-core (mounted read-only), so its version always matches @playwright/test. Nothing in it
// is downloaded at run time.

import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const name = 'ariva-visual-browser';
const port = 3123;
const version = JSON.parse(fs.readFileSync(path.join(root, 'node_modules', 'playwright-core', 'package.json'), 'utf8')).version;

/** mcr.microsoft.com/playwright:v1.63.0-noble. Bump together with @playwright/test, then regenerate the baselines. */
export const image = 'mcr.microsoft.com/playwright@sha256:eff16c30e6f3f4af0a03fa4b706120d5e9b0891c344a27d64559aff5900a4a27';
export const imageVersion = '1.63.0';
// A random path per start: the server has no authentication, so only a process that was told the endpoint can drive it.
const secretPath = `/${crypto.randomBytes(16).toString('hex')}`;
export const endpoint = `ws://127.0.0.1:${port}${secretPath}`;

function docker(args, options = {}) {
	return execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], ...options });
}

function listening() {
	return new Promise((resolve) => {
		const socket = net.connect(port, '127.0.0.1');
		socket.once('connect', () => {
			socket.destroy();
			resolve(true);
		});
		socket.once('error', () => resolve(false));
	});
}

async function start() {
	if (version !== imageVersion) {
		throw new Error(`playwright-core ${version} does not match the pinned image (${imageVersion}): update the image digest and regenerate the baselines`);
	}
	try {
		docker(['rm', '-f', name]);
	} catch {
		// not running
	}
	docker([
		'run', '--detach', '--rm', '--name', name, '--network', 'host', '--init', '--ipc', 'host', '--user', 'pwuser',
		'--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
		'--volume', `${path.join(root, 'node_modules', 'playwright-core')}:/opt/playwright-core:ro`,
		image, 'node', '/opt/playwright-core/cli.js', 'run-server', '--port', String(port), '--host', '127.0.0.1', '--path', secretPath
	]);
	for (let attempt = 0; attempt < 60; attempt++) {
		if (await listening()) {
			console.log(endpoint);
			return;
		}
		await new Promise((resolve) => setTimeout(resolve, 500));
	}
	throw new Error(`the visual browser did not listen on ${endpoint}:\n${docker(['logs', name])}`);
}

function stop() {
	try {
		docker(['rm', '-f', name]);
	} catch {
		// not running
	}
}

const command = process.argv[2];
if (command === 'start') await start();
else if (command === 'stop') stop();
else if (command) {
	console.error('usage: node scripts/visual-browser.mjs start|stop');
	process.exit(2);
}
