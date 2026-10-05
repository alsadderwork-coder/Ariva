import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { accounts, signIn } from '../support/accounts';
import { hosts } from '../support/hosts';
import { loadTriage, readAlerts, summary, untriagedHigh, type ZapAlert } from '../../scripts/zap-findings.mjs';

/**
 * ARV-063: the dynamic security scan. OWASP ZAP, in its image pinned by digest, runs
 * - an API scan (passive and active) of each host's OpenAPI document, signed in as a system administrator without a
 *   second factor (so critical actions answer 401 and are never carried out), with the sign-in and hub endpoints left
 *   out (zap-hooks.py);
 * - a baseline scan (spider and passive rules) of each API host and of the web app served by the production nginx
 *   image's configuration.
 * Every report goes to zap-reports/ (HTML and JSON per scan, summary.md). The run fails on any High risk alert that is
 * not triaged in security/zap-triage.json; lower risks are listed for triage in backlog/progress.md.
 */

/** ghcr.io/zaproxy/zaproxy:2.17.0. Bump with the release notes; the image is large, so it is pulled once per run. */
export const zapImage = 'ghcr.io/zaproxy/zaproxy@sha256:781a2bdaea47324e7bab583e2263f21d257b0aee61ed51521a5be45f5f5081ef';
/** The web image's own nginx base (Platform/Frontplane/Ariva.Web/Dockerfile), serving the run's production build. */
export const nginxImage = 'nginx:1.30.5-alpine3.24@sha256:0985e772fb9f729e6fa0980da05fca5d9c468e870eed43071545afa9d2e27d94';

const here = path.dirname(fileURLToPath(import.meta.url));
const e2eRoot = path.resolve(here, '..', '..');
const repositoryRoot = path.resolve(e2eRoot, '..', '..', '..');
const reports = path.join(e2eRoot, 'zap-reports');
const webBuild = path.join(repositoryRoot, 'Platform', 'Frontplane', 'Ariva.Web', 'build');
const webPort = 3000;
const nginxName = 'ariva-zap-web';
/** An active scan stops after this many minutes per host; the access token lives 15. */
const activeScanMinutes = 10;

/** Operations the scan never calls (zap-hooks.py keeps the active scan and the spider off them too). */
const excludedRoutes = ['/api/auth', '/hubs'];

const documented = { main: hosts.main, ingest: hosts.ingest, integration: hosts.integration } as const;
const baseline = { main: hosts.main, ingest: hosts.ingest, integration: hosts.integration, cronz: hosts.cronz, web: `http://localhost:${webPort}` } as const;

test.describe.configure({ mode: 'serial' });

let work: string;

test.beforeAll(() => {
	fs.rmSync(reports, { recursive: true, force: true });
	fs.mkdirSync(reports, { recursive: true });
	// ZAP runs as its image's own user (uid 1000) and writes its reports into this folder.
	work = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-zap-'));
	fs.chmodSync(work, 0o777);
	fs.copyFileSync(path.join(here, 'zap-hooks.py'), path.join(work, 'hooks.py'));
	execFileSync('docker', ['pull', '--quiet', zapImage], { stdio: 'ignore', timeout: 20 * 60_000 });
});

test.afterAll(() => {
	spawnSync('docker', ['rm', '-f', nginxName], { stdio: 'ignore' });
	if (work) fs.rmSync(work, { recursive: true, force: true });
});

/**
 * Runs one packaged scan. The access token, when given, reaches ZAP through an env file only the owner can read, in a
 * folder of its own outside the shared work folder (the docker client reads it; it is never mounted).
 */
function runZap(script: string, args: string[], token?: string): number {
	const secrets = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-zap-env-'));
	const envFile = path.join(secrets, 'zap.env');
	fs.writeFileSync(envFile, token ? `ZAP_AUTH_HEADER_VALUE=Bearer ${token}\n` : '', { mode: 0o600, flag: 'wx' });
	try {
		const result = spawnSync(
			'docker',
			['run', '--rm', '--network', 'host', '--env-file', envFile, '-v', `${work}:/zap/wrk:rw`, zapImage, script, ...args, '--hook', '/zap/wrk/hooks.py', '-I'],
			{ encoding: 'utf8', timeout: (activeScanMinutes + 15) * 60_000, maxBuffer: 64 * 1024 * 1024 }
		);
		fs.writeFileSync(path.join(reports, `${args[args.indexOf('-J') + 1].replace('.json', '')}.log`), `${result.stdout}\n${result.stderr}`);
		return result.status ?? -1;
	} finally {
		fs.rmSync(secrets, { recursive: true, force: true });
	}
}

function collect(scan: string): ZapAlert[] {
	const file = path.join(work, `${scan}.json`);
	expect(fs.existsSync(file), `${scan}: ZAP wrote no report`).toBe(true);
	for (const extension of ['json', 'html']) {
		const source = path.join(work, `${scan}.${extension}`);
		if (fs.existsSync(source)) fs.copyFileSync(source, path.join(reports, `${scan}.${extension}`));
	}
	return readAlerts(file, scan);
}

const found: ZapAlert[] = [];

for (const [name, url] of Object.entries(documented)) {
	test(`API scan of ${name} (OpenAPI document, signed in)`, async ({ request }) => {
		const { accessToken } = await signIn(accounts().zapAdmin);
		const response = await request.get(`${url}/openapi/v1.json`, { headers: { Authorization: `Bearer ${accessToken}` } });
		expect(response.status(), `${name} serves its OpenAPI document to a system administrator`).toBe(200);
		// ZAP's OpenAPI import calls every operation once before any exclusion applies, so the sign-in, session and hub
		// operations leave the document here: a logout or password change would end the scan account's session.
		const document = await response.json();
		const excluded = Object.keys(document.paths ?? {}).filter((route) => excludedRoutes.some((prefix) => route.startsWith(prefix)));
		for (const route of excluded) delete document.paths[route];
		fs.writeFileSync(path.join(work, `${name}-openapi.json`), JSON.stringify(document));

		const status = runZap(
			'zap-api-scan.py',
			['-t', `/zap/wrk/${name}-openapi.json`, '-f', 'openapi', '-J', `api-${name}.json`, '-r', `api-${name}.html`, '-T', '15',
				'-z', `-config scanner.maxScanDurationInMins=${activeScanMinutes} -config scanner.threadPerHost=4`],
			accessToken
		);
		expect(status, `zap-api-scan of ${name} ran (exit 3 is a ZAP error; see zap-reports/api-${name}.log)`).not.toBe(3);
		found.push(...collect(`api-${name}`));
		// The scan only means something if it stayed signed in to the end.
		const after = await request.get(`${url}/openapi/v1.json`, { headers: { Authorization: `Bearer ${accessToken}` } });
		expect(after.status(), `the scan of ${name} kept its session`).toBe(200);
	});
}

test('baseline scans of every host and the web app', () => {
	// The web app as its image serves it: the run's production build under the image's nginx configuration.
	spawnSync('docker', ['rm', '-f', nginxName], { stdio: 'ignore' });
	execFileSync('docker', [
		'run', '-d', '--rm', '--name', nginxName, '--network', 'host', '--read-only', '--tmpfs', '/var/cache/nginx', '--tmpfs', '/var/run', '--tmpfs', '/tmp',
		'-v', `${webBuild}:/usr/share/nginx/html/web:ro`, '-v', `${path.join(webBuild, 'nginx', 'default.conf')}:/etc/nginx/conf.d/default.conf:ro`, nginxImage
	], { stdio: 'ignore' });

	for (const [name, url] of Object.entries(baseline)) {
		const status = runZap('zap-baseline.py', ['-t', `${url}/`, '-J', `baseline-${name}.json`, '-r', `baseline-${name}.html`, '-m', '2', '-T', '10']);
		expect(status, `zap-baseline of ${name} ran (see zap-reports/baseline-${name}.log)`).not.toBe(3);
		found.push(...collect(`baseline-${name}`));
	}
});

test('no untriaged High risk finding', () => {
	const triage = loadTriage();
	fs.writeFileSync(path.join(reports, 'summary.md'), summary(found, triage));
	expect(untriagedHigh(found, triage), 'High risk findings must be fixed or triaged in security/zap-triage.json').toEqual([]);
});
