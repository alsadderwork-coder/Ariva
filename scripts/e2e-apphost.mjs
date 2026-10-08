// Runs the E2E suite against hosts started by Ariva.AppHost (ARV-066), configured exactly as Playwright would start them:
//   1. draws this run's secrets (the ARIVA_E2E_* values playwright.config.ts would draw, and the AppHost's passwords);
//   2. loads the Playwright config once with ARIVA_E2E_WRITE_HOST_ENV, which writes each host's variables to a file;
//   3. starts the AppHost with that file, no Stream and no dev server (as the Playwright run), throwaway containers with
//      no volume, on ports apart from the development AppHost and compose; waits until every host is ready;
//   4. runs Playwright, which finds the hosts up and reuses them (it still starts its smtp4dev and the web preview);
//   5. stops the AppHost and deletes the file, whatever happened.
// Usage: node scripts/e2e-apphost.mjs [playwright test arguments]   (needs Docker and a Debug build: dotnet build Ariva.slnx)
// CI keeps docker compose and Playwright's own host start (ci.yml); this is for local runs and agents.
import { spawn, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const E2E = path.join(ROOT, 'Platform', 'Testing', 'Ariva.E2E');
const APPHOST = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.AppHost', 'bin', 'Debug', 'net10.0', 'Ariva.AppHost.dll');
const npx = process.platform === 'win32' ? 'npx.cmd' : 'npx';

if (process.env.CI) {
	console.error('e2e-apphost: CI runs the suite with docker compose and Playwright starting the hosts (ci.yml).');
	process.exit(2);
}
if (!fs.existsSync(APPHOST)) {
	console.error('e2e-apphost: build first (dotnet build Ariva.slnx -c Debug).');
	process.exit(2);
}

const port = (name, fallback) => Number(process.env[name] || fallback);
const ports = {
	database: port('ARIVA_APPHOST_E2E_DB_PORT', 25433),
	kafka: port('ARIVA_APPHOST_E2E_KAFKA_PORT', 29092),
	redis: port('ARIVA_APPHOST_E2E_REDIS_PORT', 26379),
	smtp: port('ARIVA_APPHOST_E2E_SMTP_PORT', 22525),
	smtpUi: port('ARIVA_APPHOST_E2E_SMTP_UI_PORT', 25080)
};
const secret = () => randomBytes(24).toString('base64url');
const base32 = (bytes) => {
	const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
	let bits = 0, value = 0, out = '';
	for (const byte of bytes) {
		value = (value << 8) | byte;
		bits += 8;
		while (bits >= 5) {
			out += alphabet[(value >>> (bits - 5)) & 31];
			bits -= 5;
		}
	}
	return bits > 0 ? out + alphabet[(value << (5 - bits)) & 31] : out;
};

const work = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-e2e-apphost-'));
fs.chmodSync(work, 0o700);
const hostFile = path.join(work, 'hosts.json');
const passwords = { owner: secret(), runtime: secret(), readonly: secret(), validationReader: secret(), redis: secret(), simulator: secret() };

// The run's values, as playwright.config.ts draws them when they are not set; both Playwright passes and the hosts see these.
const run = {
	ARIVA_E2E_LOG_DIR: path.join(E2E, 'logs', new Date().toISOString().replace(/[:.]/g, '-')),
	ARIVA_E2E_ACCOUNT_SEED: secret(),
	ARIVA_E2E_KEY_DIR: path.join(work, 'keys'),
	ARIVA_E2E_SIMULATION_KEY: 'sim-e2e-' + secret(),
	ARIVA_E2E_MOCK_AMAN_SECRET: 'aman-e2e-' + secret(),
	ARIVA_E2E_MOCK_AMAN_SEED: base32(randomBytes(20)),
	ARIVA_E2E_MOCK_AMAN_PULL_SECRET: 'aman-pull-' + secret(),
	ARIVA_E2E_MOCK_AMAN_PULL_SEED: base32(randomBytes(20)),
	ARIVA_E2E_ACRIS_KEY: 'acris-e2e-' + secret(),
	ARIVA_E2E_CRONZ_KEY: 'cronz-e2e-' + secret(),
	// ARV-104g1: the validation reader login api-main gets (the same value as the AppHost's parameter below).
	ARIVA_E2E_VALIDATION_READER_PASSWORD: passwords.validationReader,
	// The AppHost's containers, for the tests themselves (database checks, Kafka and Redis suites).
	ARIVA_E2E_SCHEMA_UPDATE: 'true',
	ARIVA_E2E_KAFKA_BOOTSTRAP: `localhost:${ports.kafka}`,
	ARIVA_E2E_REDIS_URL: `redis://:${passwords.redis}@localhost:${ports.redis}`,
	Database__Host: 'localhost',
	Database__Port: String(ports.database),
	Database__Name: 'ariva',
	Database__Username: 'ariva_app',
	Database__Password: passwords.runtime,
	Database__Migration__Username: 'ariva',
	Database__Migration__Password: passwords.owner
};
const environment = { ...process.env, ...run };

let appHost;
let exitCode = 1;
// An interrupted run still stops the AppHost, which stops its hosts and containers (step 5).
let interrupted = false;
for (const signal of ['SIGINT', 'SIGTERM']) {
	process.once(signal, () => {
		interrupted = true;
		if (appHost && appHost.exitCode === null) appHost.kill('SIGINT');
	});
}
try {
	// 2. The hosts' variables, from the Playwright config itself (listing loads the config, starts no server).
	const list = spawnSync(npx, ['playwright', 'test', '--list'], {
		cwd: E2E,
		env: { ...environment, ARIVA_E2E_WRITE_HOST_ENV: hostFile },
		stdio: 'ignore',
		shell: process.platform === 'win32'
	});
	if (list.status !== 0 || !fs.existsSync(hostFile)) throw new Error('could not load the Playwright config (npx playwright test --list)');

	// 3. The AppHost, with the generated passwords as its parameters and the hosts' variables.
	appHost = spawn(
		'dotnet',
		[
			APPHOST,
			`--AppHost:HostEnvironmentFile=${hostFile}`,
			'--AppHost:Stream=false',
			'--AppHost:Web=false',
			'--AppHost:Persistent=false',
			'--AppHost:DatabaseVolume=',
			`--AppHost:DatabasePort=${ports.database}`,
			`--AppHost:KafkaPort=${ports.kafka}`,
			`--AppHost:RedisPort=${ports.redis}`,
			`--AppHost:SmtpPort=${ports.smtp}`,
			`--AppHost:SmtpUiPort=${ports.smtpUi}`
		],
		{
			cwd: path.dirname(APPHOST),
			env: {
				...process.env,
				DOTNET_ENVIRONMENT: 'Development',
				ASPNETCORE_URLS: 'http://localhost:15890',
				ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL: 'http://localhost:19890',
				ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL: 'http://localhost:20890',
				ASPIRE_ALLOW_UNSECURED_TRANSPORT: 'true',
				'Parameters__database-owner-password': passwords.owner,
				'Parameters__database-runtime-password': passwords.runtime,
				'Parameters__database-readonly-password': passwords.readonly,
				'Parameters__database-validation-reader-password': passwords.validationReader,
				'Parameters__redis-password': passwords.redis,
				'Parameters__simulation-operator-key': passwords.simulator
			},
			stdio: ['ignore', fs.openSync(path.join(work, 'apphost.log'), 'a'), fs.openSync(path.join(work, 'apphost.log'), 'a')]
		}
	);
	const ready = ['51001', '51002', '51004', '51005', '51020'].map((p) => `http://localhost:${p}/health/readiness`);
	const deadline = Date.now() + 600_000;
	for (;;) {
		if (appHost.exitCode !== null) throw new Error(`the AppHost stopped (see ${path.join(work, 'apphost.log')})`);
		const states = await Promise.all(ready.map((url) => fetch(url).then((r) => r.ok, () => false)));
		if (states.every(Boolean)) break;
		if (Date.now() > deadline) throw new Error('the hosts were not ready within ten minutes');
		await new Promise((resolve) => setTimeout(resolve, 3_000));
	}
	console.log('e2e-apphost: every host is ready; running Playwright.');

	// 4. The suite, reusing the hosts.
	// Asynchronous, so an interrupt reaches the handlers above while the suite runs.
	const tests = spawn(npx, ['playwright', 'test', ...process.argv.slice(2)], {
		cwd: E2E,
		env: environment,
		stdio: 'inherit',
		shell: process.platform === 'win32'
	});
	const status = await new Promise((resolve) => tests.once('exit', (code) => resolve(code)));
	exitCode = interrupted ? 130 : (status ?? 1);
} catch (error) {
	console.error(`e2e-apphost: ${error.message}`);
} finally {
	// 5. The AppHost stops its hosts and containers on an interrupt.
	if (appHost && appHost.exitCode === null) {
		appHost.kill('SIGINT');
		await new Promise((resolve) => {
			const timer = setTimeout(() => {
				appHost.kill('SIGKILL');
				resolve();
			}, 60_000);
			appHost.once('exit', () => {
				clearTimeout(timer);
				resolve();
			});
		});
	}
	// The orchestrator stops the hosts and containers shortly after the AppHost exits: wait for the hosts' ports to close,
	// so a following run (or the gates) never finds this run's hosts still answering.
	const hostPorts = ['51001', '51002', '51004', '51005', '51020'];
	const gone = Date.now() + 120_000;
	while (Date.now() < gone) {
		const answering = await Promise.all(hostPorts.map((p) => fetch(`http://localhost:${p}/health/liveness`).then(() => true, () => false)));
		if (!answering.some(Boolean)) break;
		await new Promise((resolve) => setTimeout(resolve, 2_000));
	}
	// The variables file and the run's keys go; the AppHost log stays only when the run failed, for its reader.
	fs.rmSync(hostFile, { force: true });
	fs.rmSync(run.ARIVA_E2E_KEY_DIR, { recursive: true, force: true });
	if (exitCode === 0) fs.rmSync(work, { recursive: true, force: true });
	else console.error(`e2e-apphost: the AppHost log is in ${path.join(work, 'apphost.log')} (owner only; it holds this run's dashboard login link, delete it when done)`);
}
process.exit(exitCode);
