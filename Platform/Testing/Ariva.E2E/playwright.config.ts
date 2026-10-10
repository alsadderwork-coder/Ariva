import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, devices } from '@playwright/test';
import { databaseAvailable, developmentUserEnvironment, keyDirectory, lockoutSeconds, refreshGraceSeconds } from './tests/support/accounts';
import { hosts, smtp4dev, streamUrl, webUrl } from './tests/support/hosts';

// Ariva API end-to-end and functional tests. See README.md.
//   api          Playwright's request fixture against the running .NET hosts, no browser
//   functional   Chromium against the production build of Ariva.Web served by `vite preview`
// The web servers below start smtp4dev, build the backend once (first .NET entry), start Ariva.Api.Main, Ariva.Api.Integration,
// Ariva.Api.Ingest and Ariva.Simulation.Api with `dotnet run --no-build` on their launchSettings ports, then build
// and preview the web app. Playwright starts them one after another and waits for each health URL.

const here = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(here, '..', '..', '..');
const configuration = process.env.ARIVA_E2E_CONFIGURATION || 'Debug';
const isCi = !!process.env.CI;

// Every .NET host writes its JSON log to this run's folder; global-teardown.ts fails the run if any line carries a
// bearer token, a JWT or an unredacted access_token (ARV-007, CWE-532). The folder is per run, so a previous run's
// files never mask or cause a finding.
// Set once in the runner; workers re-read this file and must see the same folder.
process.env.ARIVA_E2E_LOG_DIR ||= path.join(here, 'logs', new Date().toISOString().replace(/[:.]/g, '-'));
const logDirectory = process.env.ARIVA_E2E_LOG_DIR;

// CI installs the Chromium build that matches this Playwright version (npx playwright install chromium).
// Elsewhere an already installed Chromium can be used instead: ARIVA_E2E_CHROMIUM=/path/to/chrome.
const chromiumExecutable = process.env.ARIVA_E2E_CHROMIUM || undefined;
// ARV-075: the visual regression project renders only in the pinned Playwright image (scripts/visual-browser.mjs), so
// it exists only when that browser is up. Run it on its own and first (`--project=visual`, on the fresh demo seed),
// then the rest without ARIVA_E2E_VISUAL_WS: no other suite's alerts, snapshots or records are then on screen, and a
// changed screenshot never stops the other suites (a project dependency would skip them).
const visualEndpoint = process.env.ARIVA_E2E_VISUAL_WS || '';
const visual = visualEndpoint.length > 0;
// ARV-063: the dynamic security scan (OWASP ZAP in its pinned image, tests/zap). Run it on its own with ARIVA_E2E_ZAP=1
// and --project=zap: it attacks the hosts and leaves junk records behind. The hosts then serve their OpenAPI documents.
const zap = process.env.ARIVA_E2E_ZAP === '1';
// ARV-064: the scripted demo (tests/demo) plays the reference day through the whole pipeline: the sensor emulator pushes
// to Ingest, Ariva.Api.Stream (started only in this mode) computes the queues, snapshots and alerts, and Ariva.Api.Main
// takes device heartbeats from Kafka. It runs in real time and alone: ARIVA_E2E_DEMO=1 --project=demo.
const demo = process.env.ARIVA_E2E_DEMO === '1';

const project = (relative: string) => path.join(repositoryRoot, 'Platform', relative);

// Sign-in (ARV-010a) needs the database; CI always provides one (ci.yml starts PostgreSQL), so a CI run without it is
// a configuration error rather than a reason to skip the account tests.
if (isCi && !databaseAvailable) {
	throw new Error('CI runs need ARIVA_E2E_SCHEMA_UPDATE=true and the Database__* variables (see README.md)');
}

// Sensor pushes are accepted only once they are in Kafka; CI starts one (ci.yml), so its absence there is an error.
if (isCi && !process.env.ARIVA_E2E_KAFKA_BOOTSTRAP) {
	throw new Error('CI runs need ARIVA_E2E_KAFKA_BOOTSTRAP (see README.md)');
}

// One random seed per run for the E2E account passwords (tests/support/accounts.ts). Set in the runner process before
// the workers start, so every worker derives the same passwords; never written to disk.
process.env.ARIVA_E2E_ACCOUNT_SEED ||= crypto.randomBytes(24).toString('base64url');
process.env.ARIVA_E2E_KEY_DIR ||= keyDirectory;
// One simulator operator key per run (ARV-027, ARV-028): the simulator gets only its SHA-256, the tests the key.
process.env.ARIVA_E2E_SIMULATION_KEY ||= 'sim-e2e-' + crypto.randomBytes(24).toString('base64url');
const simulationKeyDigest = crypto.createHash('sha256').update(process.env.ARIVA_E2E_SIMULATION_KEY).digest('hex');
// ARV-104i: a second operator key with the read scope only, so the control endpoints' 403 is proven end to end.
process.env.ARIVA_E2E_SIMULATION_READ_KEY ||= 'sim-e2e-read-' + crypto.randomBytes(24).toString('base64url');
const simulationReadKeyDigest = crypto.createHash('sha256').update(process.env.ARIVA_E2E_SIMULATION_READ_KEY).digest('hex');
// ARV-029: one client of the mock AMAN Integration API per run; the simulator gets the secret's SHA-256 and the seed.
process.env.ARIVA_E2E_MOCK_AMAN_SECRET ||= 'aman-e2e-' + crypto.randomBytes(24).toString('base64url');
process.env.ARIVA_E2E_MOCK_AMAN_SEED ||= base32Of(crypto.randomBytes(20));
// ARV-050: a second mock AMAN client for Ariva's own AMAN pull (one exchange per TOTP step and client, so not shared).
process.env.ARIVA_E2E_MOCK_AMAN_PULL_SECRET ||= 'aman-pull-' + crypto.randomBytes(24).toString('base64url');
process.env.ARIVA_E2E_MOCK_AMAN_PULL_SEED ||= base32Of(crypto.randomBytes(20));
// ARV-029: the API key Ariva's ACRIS pull would present to the emulated AODB.
process.env.ARIVA_E2E_ACRIS_KEY ||= 'acris-e2e-' + crypto.randomBytes(24).toString('base64url');
// ARV-060: the TickerQ dashboard of Ariva.Api.Cronz takes this key (the host keeps only its SHA-256).
process.env.ARIVA_E2E_CRONZ_KEY ||= 'cronz-e2e-' + crypto.randomBytes(24).toString('base64url');
// ARV-104g1: the validation reader login's password for this run (Ariva.Api.Main only; see validationReaderEnvironment).
process.env.ARIVA_E2E_VALIDATION_READER_PASSWORD ||= 'reader-e2e-' + crypto.randomBytes(24).toString('base64url');

function base32Of(bytes: Buffer): string {
	const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
	let bits = 0,
		value = 0,
		out = '';
	for (const byte of bytes) {
		value = (value << 8) | byte;
		bits += 8;
		while (bits >= 5) {
			out += alphabet[(value >>> (bits - 5)) & 31];
			bits -= 5;
		}
	}
	return bits > 0 ? out + alphabet[(value << (5 - bits)) & 31] : out;
}

/**
 * ARV-045: the ACRIS stand-in of outbound-endpoints.spec.ts listens on 127.0.0.1 over plain HTTP, which only a lab
 * deployment may call (Integration:Outbound, allowed in vm-local only). Main checks registrations with it, Integration calls.
 */
function outboundLabEnvironment(): Record<string, string> {
	return { Integration__Outbound__AllowLoopback: 'true', Integration__Outbound__LabHosts__0: '127.0.0.1' };
}

/**
 * ARV-104g1: the validation reader login, the only login that reads the shadow nowcast, given to Ariva.Api.Main alone (it
 * hosts the validation service), as the chart does in the clusters. Main migrates the run's database at startup (vm-local)
 * and so creates or updates the login (script 0049); the other hosts never get it, and every host checks at startup that its
 * runtime login (Database__Username, never a superuser or the owner) can read no value of the shadow nowcast.
 */
function validationReaderEnvironment(): Record<string, string> {
	return {
		Database__ValidationReader__Username: 'ariva_validation',
		Database__ValidationReader__Password: process.env.ARIVA_E2E_VALIDATION_READER_PASSWORD!
	};
}

function dotnetHost(projectPath: string, healthUrl: string, buildFirst = false, extraEnvironment: Record<string, string> = {}) {
	const run = `dotnet run --no-build --configuration ${configuration} --project "${projectPath}"`;
	const logFile = path.join(logDirectory, `${path.basename(projectPath)}.log`);
	return {
		command: buildFirst ? `node scripts/build-backend.mjs && ${run}` : run,
		cwd: here,
		url: healthUrl,
		reuseExistingServer: !isCi,
		timeout: buildFirst ? 300_000 : 120_000,
		stdout: 'ignore' as const,
		stderr: 'pipe' as const,
		// The E2E run has no database unless ARIVA_E2E_SCHEMA_UPDATE=true says one is up (npm run dev:up); vm-local
		// otherwise runs the development schema update at startup and the hosts would stop on the refused connection.
		env: {
			Database__AllowSchemaUpdate: databaseAvailable ? 'true' : 'false',
			LogFile__Path: logFile,
			// Every host validates tokens with the run's development key; tests read it to sign tampered tokens.
			Auth__Tokens__DevelopmentKeyDirectory: process.env.ARIVA_E2E_KEY_DIR!,
			Auth__Lockout__DurationSeconds: String(lockoutSeconds),
			Auth__Sessions__RefreshGraceSeconds: String(refreshGraceSeconds),
			// Most E2E accounts sign in many times a minute in parallel, which the TOTP replay guard would refuse;
			// TOTP has its own accounts in totp.spec.ts. Every committed environment keeps it on (AuthSettingsTests).
			Auth__TotpRequired: 'false',
			// The suite talks to the hosts over loopback and sets X-Forwarded-For per test, so the per-address sign-in
			// limit applies only where a test means it to (production trusts only the ingress network).
			Security__ForwardedHeaders__KnownProxies__0: '127.0.0.1',
			Security__ForwardedHeaders__KnownProxies__1: '::1',
			// ARV-063: the scan's documents, and a global limit high enough that the scan reaches the endpoints instead of
			// 429 (the limiter itself is tested in RateLimitingAndCorsTests).
			...(zap ? { OpenApi__Enabled: 'true', Security__RateLimiting__Global__PermitLimit: '100000' } : {}),
			...emailEnvironment(),
			...extraEnvironment
		}
	};
}

// The live hub (ARV-035) reads Stream's snapshots from Redis: ARIVA_E2E_REDIS_URL (redis://[:password@]host:port) points
// Ariva.Api.Main and live-hub.spec.ts at the run's Redis. Without it the hub runs without Redis and the update test skips.
function redisEnvironment(): Record<string, string> {
	const url = process.env.ARIVA_E2E_REDIS_URL;
	if (!url) return {};
	const parsed = new URL(url);
	const password = parsed.password ? `,password=${decodeURIComponent(parsed.password)}` : '';
	return {
		Redis__Enabled: 'true',
		Redis__ConnectionString: `${parsed.hostname}:${parsed.port || '6379'}${password}`,
		Redis__InstanceName: process.env.ARIVA_E2E_REDIS_INSTANCE || 'ariva:'
	};
}

// Sensor pushes (ARV-023) are answered 202 only once their events are in Kafka: ARIVA_E2E_KAFKA_BOOTSTRAP (host:port of
// a single-broker Kafka) lets Ariva.Api.Ingest provision its topics there and publish. Without it Ingest answers 503 and
// the push suites skip their accepted-push steps.
function kafkaEnvironment(): Record<string, string> {
	const bootstrap = process.env.ARIVA_E2E_KAFKA_BOOTSTRAP;
	if (!bootstrap) return {};
	return {
		Kafka__Enabled: 'true',
		Kafka__BootstrapServers: bootstrap,
		Kafka__ProvisionTopics: 'true',
		Kafka__Topics__Partitions: '1',
		Kafka__Topics__ReplicationFactor: '1',
		Kafka__Topics__MinInSyncReplicas: '1'
	};
}

// Alert emails (ARV-040): every host writes them; Ariva.Api.Integration, the only host given the relay, sends them every
// second to the run's smtp4dev (the pinned local tool, .config/dotnet-tools.json, started below on loopback in clear
// text, which only vm-local and k8s-dev accept). The hourly limit per address is raised: the suites send many alert
// emails to the same people.
function emailEnvironment(): Record<string, string> {
	return {
		Email__Enabled: 'true',
		Email__FromAddress: 'no-reply@ariva.e2e',
		Email__PollSeconds: '1',
		Email__MaxPerRecipientPerHour: '1000'
	};
}

function smtpEnvironment(): Record<string, string> {
	return {
		Email__Smtp__Host: '127.0.0.1',
		Email__Smtp__Port: String(smtp4dev.smtpPort),
		Email__Smtp__Security: 'None',
		Email__Smtp__AllowInsecure: 'true'
	};
}

// The .NET hosts, by the resource names Ariva.AppHost uses (ARV-066). Playwright starts them in this order.
const hostServers = {
	'api-main': dotnetHost(project('Backplane/Ariva.Api.Main'), `${hosts.main}/health/readiness`, true, {
		...(databaseAvailable ? developmentUserEnvironment() : {}),
		...(databaseAvailable ? validationReaderEnvironment() : {}),
		...redisEnvironment(),
		...outboundLabEnvironment(),
		// ARV-064: Main records device heartbeats from ariva.device.health.v1, so the demo's devices stay Online.
		...(demo ? kafkaEnvironment() : {}),
		// ARV-139c (CWE-200): this run's api-main never seeds the development-only NBJ-BC1 site, even where the owner's shell
		// or appsettings.local.json turns it on (NbjSiteScopeTests). The pin cannot remove a site an earlier run seeded into
		// the same database: global setup stops the run on such a database (tests/support/global-setup.ts).
		Seed__NbjSite: 'false'
	}),
	'api-integration': dotnetHost(project('Backplane/Ariva.Api.Integration'), `${hosts.integration}/health/readiness`, false, {
		...smtpEnvironment(),
		// ARV-048: Integration consumes AMAN's feed topics (aman.feed.*.v1), which the simulator's AMAN publishes.
		...kafkaEnvironment(),
		// The lockout test fails one client ten times in a few seconds (ARV-042); production keeps 5 a minute.
		Auth__IntegrationAttemptsPerMinute: '30',
		// ARV-043: small enough for flights-api.spec.ts to reach both batch limits; production keeps 8 at once and 16
		// waiting per host, and 120 batches a minute per client.
		Security__RateLimiting__IntegrationBatch__PermitLimit: '2',
		Security__RateLimiting__IntegrationBatch__QueueLimit: '2',
		Security__RateLimiting__IntegrationClient__PermitLimit: '60',
		...outboundLabEnvironment()
	}),
	'api-ingest': dotnetHost(project('Backplane/Ariva.Api.Ingest'), `${hosts.ingest}/health/readiness`, false, {
		...kafkaEnvironment(),
		// A device's own limit (600 a minute by default) lowered so the load smoke's flood reaches it with 200 requests,
		// well inside the per-address budget the parallel suites share; every device here pushes about once a minute.
		// The full load run (ARIVA_LOAD_MODE=full) keeps the production 600; Ingest's per-address limit (20,000 a minute, for
		// sensors behind one gateway) covers 40 sensors from this one address either way.
		Security__RateLimiting__Device__PermitLimit: process.env.ARIVA_LOAD_MODE === 'full' ? '600' : '120'
	}),
	// ARV-060: TickerQ runs the scheduled reports and mails them through the run's smtp4dev; the dashboard is on with
	// the run's key so reports.spec.ts can run the delivery job on demand.
	// ARV-118: the availability ledger reads the run's Redis snapshots; a 30-second grace lets availability.spec.ts see a
	// minute decided within two runs of the job.
	'api-cronz': dotnetHost(project('Backplane/Ariva.Api.Cronz'), `${hosts.cronz}/health/readiness`, false, {
		...smtpEnvironment(),
		...redisEnvironment(),
		Availability__GraceSeconds: '30',
		Cronz__Dashboard__Enabled: 'true',
		Cronz__Dashboard__KeySha256: crypto.createHash('sha256').update(process.env.ARIVA_E2E_CRONZ_KEY).digest('hex')
	}),
	...(demo
		? {
				'api-stream': dotnetHost(project('Backplane/Ariva.Api.Stream'), `${streamUrl}/health/readiness`, false, {
					...kafkaEnvironment(),
					...redisEnvironment()
				})
			}
		: {}),
	simulation: dotnetHost(project('Simulation/Ariva.Simulation.Api'), `${hosts.simulation}/health/readiness`, false, {
		Simulation__Control__Keys__0__Name: 'e2e',
		Simulation__Control__Keys__0__Sha256: simulationKeyDigest,
		Simulation__Control__Keys__0__Scopes__0: 'read',
		Simulation__Control__Keys__0__Scopes__1: 'control',
		Simulation__Control__Keys__1__Name: 'e2e-reader',
		Simulation__Control__Keys__1__Sha256: simulationReadKeyDigest,
		Simulation__Control__Keys__1__Scopes__0: 'read',
		// The sensor emulator pushes to the Ingest under test; plain HTTP only over loopback.
		Simulation__Sensors__IngestUrl: hosts.ingest,
		Simulation__Sensors__AllowInsecureTransport: /^http:\/\/(localhost|127\.0\.0\.1|\[::1\])(:|\/|$)/.test(hosts.ingest) ? 'true' : 'false',
		// ARV-029: the AODB, AMAN and immigration emulators call the Integration host under test.
		Simulation__Ariva__IntegrationUrl: hosts.integration,
		// ARV-104i: the validation observers sign in to the Ariva.Api.Main under test and use its capture API (loopback too).
		Simulation__Ariva__MainUrl: hosts.main,
		// Five rehearsals a run of validation-rehearsal.spec.ts (more with a retry); the limit itself (10 a minute per key by
		// default) is tested in ValidationEmulatorEndpointTests.
		Simulation__Validation__RehearsalsPerMinute: '30',
		// The control test reads the truth twice per hostile payload with the read key; the limit itself (60 a minute per key by
		// default) is tested in ValidationEmulatorEndpointTests.
		Simulation__Validation__TruthReadsPerMinute: '300',
		Simulation__Ariva__AllowInsecureTransport:
			[hosts.integration, hosts.main].some((url) => /^http:\/\/(localhost|127\.0\.0\.1|\[::1\])(:|\/|$)/.test(url)) ? 'true' : 'false',
		Simulation__Aman__Kafka__BootstrapServers: process.env.ARIVA_E2E_KAFKA_BOOTSTRAP ?? '',
		Simulation__Aman__Mock__Clients__0__ClientId: 'e2e-aman-connector',
		Simulation__Aman__Mock__Clients__0__SecretSha256: crypto.createHash('sha256').update(process.env.ARIVA_E2E_MOCK_AMAN_SECRET).digest('hex'),
		Simulation__Aman__Mock__Clients__0__TotpSecret: process.env.ARIVA_E2E_MOCK_AMAN_SEED,
		Simulation__Aman__Mock__Clients__1__ClientId: 'e2e-aman-pull',
		Simulation__Aman__Mock__Clients__1__SecretSha256: crypto.createHash('sha256').update(process.env.ARIVA_E2E_MOCK_AMAN_PULL_SECRET).digest('hex'),
		Simulation__Aman__Mock__Clients__1__TotpSecret: process.env.ARIVA_E2E_MOCK_AMAN_PULL_SEED,
		Simulation__Aodb__AcrisKeySha256: crypto.createHash('sha256').update(process.env.ARIVA_E2E_ACRIS_KEY).digest('hex')
	})
};

// ARV-066: with ARIVA_E2E_WRITE_HOST_ENV set, the exact variables each host gets here are written to that file (owner
// only) for Ariva.AppHost's AppHost:HostEnvironmentFile, so scripts/e2e-apphost.mjs can run the suite against hosts the
// AppHost started, configured exactly as Playwright would start them. The file holds this run's derived settings.
if (process.env.ARIVA_E2E_WRITE_HOST_ENV) {
	const variables = Object.fromEntries(Object.entries(hostServers).map(([name, server]) => [name, server.env]));
	fs.writeFileSync(process.env.ARIVA_E2E_WRITE_HOST_ENV, JSON.stringify(variables, null, 2), { mode: 0o600 });
}

export default defineConfig({
	testDir: './tests',
	globalSetup: './tests/support/global-setup.ts',
	globalTeardown: './tests/support/global-teardown.ts',
	outputDir: './test-results',
	fullyParallel: true,
	forbidOnly: isCi,
	retries: isCi ? 1 : 0,
	workers: isCi ? 2 : undefined,
	timeout: 30_000,
	reporter: [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }]],
	use: {
		trace: 'retain-on-failure'
	},
	// Visual baselines (ARV-075): one PNG per screen and variant, no platform suffix, since only the pinned image renders them.
	snapshotPathTemplate: '{testDir}/__screenshots__/{testFilePath}/{arg}{ext}',
	expect: {
		timeout: 5_000,
		toHaveScreenshot: { animations: 'disabled', caret: 'hide', scale: 'css', threshold: 0.01, maxDiffPixelRatio: 0.0005 }
	},
	projects: [
		...(visual
			? [
					{
						name: 'visual',
						testDir: './tests/visual',
						fullyParallel: false,
						use: {
							...devices['Desktop Chrome'],
							viewport: { width: 1440, height: 900 },
							deviceScaleFactor: 1,
							baseURL: webUrl,
							locale: 'en-US',
							timezoneId: 'Asia/Dubai',
							connectOptions: { wsEndpoint: visualEndpoint },
							// The PNGs (expected, actual, diff) are the evidence; a trace would carry the sign-in and the player's credential.
							trace: 'off' as const
						}
					}
				]
			: []),
		...(demo
			? [
					{
						name: 'demo',
						testDir: './tests/demo',
						fullyParallel: false,
						retries: 0,
						// The reference evening in real time, with the setup and a margin.
						timeout: 4 * 60 * 60_000,
						use: {
							...devices['Desktop Chrome'],
							viewport: { width: 1440, height: 900 },
							baseURL: webUrl,
							locale: 'en-US',
							launchOptions: { executablePath: chromiumExecutable },
							screenshot: 'on' as const,
							trace: 'off' as const
						}
					}
				]
			: []),
		...(zap
			? [
					{
						name: 'zap',
						testDir: './tests/zap',
						fullyParallel: false,
						retries: 0,
						timeout: 120 * 60_000,
						// The scan's own reports are the evidence; a trace would carry the scan account's sign-in.
						use: { trace: 'off' as const }
					}
				]
			: []),
		{
			name: 'api',
			testDir: './tests/api'
		},
		{
			name: 'functional',
			testDir: './tests/functional',
			use: {
				...devices['Desktop Chrome'],
				baseURL: webUrl,
				locale: 'en-US',
				launchOptions: { executablePath: chromiumExecutable }
			}
		}
	],
	webServer: [
		{
			command:
				`dotnet tool restore && dotnet tool run smtp4dev --urls=${smtp4dev.url} --smtpport=${smtp4dev.smtpPort} ` +
				'--imapport= --pop3port= --disableipv6 --bindaddress=127.0.0.1 --db= --messagestokeep=1000',
			cwd: repositoryRoot,
			url: `${smtp4dev.url}/api/messages`,
			reuseExistingServer: !isCi,
			timeout: 180_000,
			stdout: 'ignore' as const,
			stderr: 'pipe' as const
		},
		...Object.values(hostServers),
		{
			command: 'npm run build && npm run preview',
			cwd: project('Frontplane/Ariva.Web'),
			url: webUrl,
			reuseExistingServer: !isCi,
			timeout: 300_000,
			stdout: 'ignore',
			stderr: 'pipe'
		}
	]
});
