// ARV-198: a throwaway container test of the web pod's certificate reload (40-ariva-tls-reload.sh). It runs the web image's
// nginx base (the Dockerfile's pin, by digest) as the pod does (user 10001, a read-only root, /tmp, /run and /var/cache/nginx
// writable), with the watcher and a TLS server like the chart's listener, and the certificate directory laid out as the
// kubelet lays out a secret volume (tls.crt and tls.key links through ..data to a dated folder). It checks that nginx serves
// the first certificate, starts a slow download, swaps ..data to a second certificate as the kubelet does on a renewal, and
// then that a new connection is given the second certificate within the stated bound while the download started before the
// swap completes in full (a graceful reload drops no request in progress; an idle keep-alive connection is closed by the old
// workers and its client opens another, as HTTP clients do). Then it swaps in a renewal nginx must refuse (a key that does not
// match its certificate) and checks that the second certificate stays served and the refusal is logged once over several
// intervals. The container is removed afterwards, also on failure. Kept with the chart tests, not in the web app, whose code
// may not start processes (the security scan's SEC-004) and whose formatting rules are the web app's.
//   node Platform/Cloud/Ariva.K8s/tests/web-tls-reload.mjs      needs docker and openssl; exits 0 when it passes
import { execFileSync, spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import tls from 'node:tls';
import { fileURLToPath } from 'node:url';

const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'Frontplane', 'Ariva.Web');
const dockerfile = fs.readFileSync(path.join(web, 'Dockerfile'), 'utf8');
const pin = dockerfile.match(/^FROM\s+nginx:(\S+)@(sha256:[0-9a-f]{64})\s*$/m);
if (!pin) throw new Error('the Dockerfile has no nginx base pinned by digest');
const INTERVAL = 5;
// The bound the watcher promises after the files change: one interval, plus a few seconds for the reload itself.
const BOUND_MS = (INTERVAL + 10) * 1000;

const run = (cmd, args, options = {}) => execFileSync(cmd, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], ...options });

function certificate(folder, name) {
	fs.mkdirSync(folder, { recursive: true });
	run('openssl', ['req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes', '-days', '2', '-subj', `/CN=${name}`,
		'-keyout', path.join(folder, 'tls.key'), '-out', path.join(folder, 'tls.crt')]);
	// Readable by the container's user, as the chart's fsGroup makes the pod's 0440 key readable to it.
	for (const file of ['tls.key', 'tls.crt']) fs.chmodSync(path.join(folder, file), 0o644);
	return new crypto.X509Certificate(fs.readFileSync(path.join(folder, 'tls.crt'))).fingerprint256;
}

/** Points ..data at a dated folder in one rename, as the kubelet's atomic writer does. */
function swapData(dir, target) {
	const temporary = path.join(dir, `..data_tmp_${process.pid}`);
	fs.symlinkSync(target, temporary);
	fs.renameSync(temporary, path.join(dir, '..data'));
}

function served(port) {
	return new Promise((resolve, reject) => {
		const socket = tls.connect({ host: '127.0.0.1', port, rejectUnauthorized: false, servername: 'web-service' }, () => {
			const fingerprint = socket.getPeerX509Certificate()?.fingerprint256;
			socket.end();
			resolve(fingerprint);
		});
		socket.setTimeout(5000, () => socket.destroy(new Error('timed out')));
		socket.on('error', reject);
	});
}

const BIG = 160 * 1024;
const RATE = '16k';

/** A download of the BIG body, slowed by nginx to RATE a second (about ten seconds), resolved with its status and bytes. */
function download(port) {
	return new Promise((resolve, reject) => {
		const chunks = [];
		const socket = tls.connect({ host: '127.0.0.1', port, rejectUnauthorized: false, servername: 'web-service' }, () =>
			socket.write('GET /big HTTP/1.1\r\nHost: web-service\r\nConnection: close\r\n\r\n'));
		socket.on('data', (chunk) => chunks.push(chunk));
		socket.on('end', () => {
			const answer = Buffer.concat(chunks);
			const head = answer.indexOf('\r\n\r\n');
			resolve({ status: answer.subarray(0, answer.indexOf('\r\n')).toString('latin1'), bytes: head < 0 ? 0 : answer.length - head - 4 });
		});
		socket.on('error', reject);
		setTimeout(() => socket.destroy(new Error('the download did not end in time')), 60_000).unref();
	});
}

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function main() {
	if (spawnSync('docker', ['info'], { stdio: 'ignore' }).status !== 0) {
		console.log('web TLS reload test: docker is not available; SKIPPED');
		return 0;
	}
	// The pin by digest from Docker Hub, or the same digest from Google's mirror of it when Docker Hub refuses (rate limits).
	let image = `nginx@${pin[2]}`;
	if (spawnSync('docker', ['image', 'inspect', image], { stdio: 'ignore' }).status !== 0 && spawnSync('docker', ['pull', image], { stdio: 'ignore' }).status !== 0) {
		image = `mirror.gcr.io/library/nginx@${pin[2]}`;
		if (spawnSync('docker', ['image', 'inspect', image], { stdio: 'ignore' }).status !== 0) run('docker', ['pull', image]);
	}
	const work = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-tls-reload-'));
	fs.chmodSync(work, 0o755);
	const dir = path.join(work, 'tls');
	fs.mkdirSync(dir);
	fs.chmodSync(dir, 0o755);
	const first = certificate(path.join(dir, '..2026_11_09_a'), 'web-service first');
	const second = certificate(path.join(dir, '..2026_11_09_b'), 'web-service second');
	// A renewal nginx refuses: a certificate with another certificate's key.
	certificate(path.join(dir, '..2026_11_09_c'), 'web-service refused');
	certificate(path.join(work, 'other'), 'another key');
	fs.copyFileSync(path.join(work, 'other', 'tls.key'), path.join(dir, '..2026_11_09_c', 'tls.key'));
	fs.chmodSync(path.join(dir, '..2026_11_09_c', 'tls.key'), 0o644);
	fs.symlinkSync('..2026_11_09_a', path.join(dir, '..data'));
	fs.symlinkSync('..data/tls.crt', path.join(dir, 'tls.crt'));
	fs.symlinkSync('..data/tls.key', path.join(dir, 'tls.key'));
	const conf = path.join(work, 'default.conf');
	fs.writeFileSync(conf, `server {\n    listen 3443 ssl;\n    ssl_certificate /etc/nginx/tls/tls.crt;\n    ssl_certificate_key /etc/nginx/tls/tls.key;\n    ssl_protocols TLSv1.2 TLSv1.3;\n    location = /big { alias /srv/big.bin; limit_rate ${RATE}; }\n}\n`);
	fs.chmodSync(conf, 0o644);
	const www = path.join(work, 'www');
	fs.mkdirSync(www);
	fs.chmodSync(www, 0o755);
	fs.writeFileSync(path.join(www, 'big.bin'), crypto.randomBytes(BIG));
	fs.chmodSync(path.join(www, 'big.bin'), 0o644);
	const name = `ariva-tls-reload-${process.pid}`;
	let failed = null;
	try {
		run('docker', ['run', '-d', '--name', name, '--user', '10001:10001', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
			// The chart's emptyDirs are the pod's through its fsGroup; here the tmpfs mounts are given to the user.
			'--tmpfs', '/tmp:uid=10001,gid=10001', '--tmpfs', '/run:uid=10001,gid=10001', '--tmpfs', '/var/cache/nginx:uid=10001,gid=10001', '-e', `ARIVA_TLS_RELOAD_SECONDS=${INTERVAL}`,
			'-v', `${dir}:/etc/nginx/tls:ro`, '-v', `${conf}:/etc/nginx/conf.d/default.conf:ro`, '-v', `${www}:/srv:ro`,
			'-v', `${path.join(web, 'docker', '40-ariva-tls-reload.sh')}:/docker-entrypoint.d/40-ariva-tls-reload.sh:ro`, '-p', '127.0.0.1::3443', image]);
		const port = Number(run('docker', ['port', name, '3443/tcp']).trim().split(':').pop());
		let before;
		for (let attempt = 0; attempt < 40 && !before; attempt++) {
			before = await served(port).catch(() => undefined);
			if (!before) await sleep(250);
		}
		if (before !== first) throw new Error(`before the swap nginx served ${before ?? 'nothing'}, not the first certificate`);
		const inFlight = download(port);
		await sleep(1000);
		swapData(dir, '..2026_11_09_b');
		const swapped = Date.now();
		let after;
		while (Date.now() - swapped < BOUND_MS) {
			after = await served(port).catch(() => undefined);
			if (after === second) break;
			await sleep(500);
		}
		if (after !== second) throw new Error(`${BOUND_MS / 1000} s after the swap nginx still served ${after === first ? 'the first certificate' : after ?? 'nothing'}`);
		const took = Math.round((Date.now() - swapped) / 1000);
		const transfer = await inFlight.catch((error) => ({ status: error.message, bytes: 0 }));
		if (!/^HTTP\/1\.1 200/.test(transfer.status) || transfer.bytes !== BIG) throw new Error(`the download started before the swap was cut (${transfer.status}, ${transfer.bytes} of ${BIG} bytes)`);
		const logs = spawnSync('docker', ['logs', name], { encoding: 'utf8' });
		if (!`${logs.stdout}${logs.stderr}`.includes('a renewed certificate in /etc/nginx/tls is served')) throw new Error('the watcher did not log the reload');
		// The refused renewal: four intervals later the second certificate is still served, and the refusal was logged once.
		swapData(dir, '..2026_11_09_c');
		await sleep(4 * INTERVAL * 1000 + 2000);
		const kept = await served(port).catch(() => undefined);
		if (kept !== second) throw new Error(`after a renewal nginx refuses it served ${kept === first ? 'the first certificate' : kept ?? 'nothing'}, not the certificate it had`);
		const refusals = (spawnSync('docker', ['logs', name], { encoding: 'utf8' }).stderr ?? '').split('\n').filter((line) => line.includes('changed but nginx refused it')).length;
		if (refusals !== 1) throw new Error(`the refused renewal was logged ${refusals} times over four intervals, not once`);
		console.log(`PASS web TLS reload: the renewed certificate is served ${took} s after the swap (bound ${BOUND_MS / 1000} s, watcher every ${INTERVAL} s), without a restart, a download in progress across the reload completed in full (${BIG} bytes), and a renewal nginx refuses leaves the previous certificate served with one log line`);
	} catch (error) {
		failed = error;
		const logs = spawnSync('docker', ['logs', name], { encoding: 'utf8' });
		console.error(`FAIL web TLS reload: ${error.message}\n${(logs.stdout ?? '') + (logs.stderr ?? '')}`.trimEnd());
	} finally {
		spawnSync('docker', ['rm', '-f', name], { stdio: 'ignore' });
		fs.rmSync(work, { recursive: true, force: true });
	}
	return failed ? 1 : 0;
}

process.exit(await main());
