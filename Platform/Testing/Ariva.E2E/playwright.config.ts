import crypto from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, devices } from '@playwright/test';
import { databaseAvailable, developmentUserEnvironment, keyDirectory, lockoutSeconds, refreshGraceSeconds } from './tests/support/accounts';
import { hosts, webUrl } from './tests/support/hosts';

// Ariva API end-to-end and functional tests. See README.md.
//   api          Playwright's request fixture against the running .NET hosts, no browser
//   functional   Chromium against the production build of Ariva.Web served by `vite preview`
// The web servers below build the backend once (first entry), start Ariva.Api.Main, Ariva.Api.Integration,
// Ariva.Api.Ingest and Ariva.Simulation.Api with `dotnet run --no-build` on their launchSettings ports, then build
// and preview the web app. Playwright starts them one after another and waits for each health URL.

const here = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(here, '..', '..', '..');
const configuration = process.env.ARIVA_E2E_CONFIGURATION || 'Debug';
const isCi = !!process.env.CI;

// Every .NET host writes its JSON log to this run's folder; global-teardown.ts fails the run if any line carries a
// bearer token, a JWT or an unredacted access_token (ARV-007, CWE-532). The folder is per run, so a previous run's
// files never mask or cause a finding.
const logDirectory = path.join(here, 'logs', new Date().toISOString().replace(/[:.]/g, '-'));
process.env.ARIVA_E2E_LOG_DIR = logDirectory;

// CI installs the Chromium build that matches this Playwright version (npx playwright install chromium).
// Elsewhere an already installed Chromium can be used instead: ARIVA_E2E_CHROMIUM=/path/to/chrome.
const chromiumExecutable = process.env.ARIVA_E2E_CHROMIUM || undefined;

const project = (relative: string) => path.join(repositoryRoot, 'Platform', relative);

// Sign-in (ARV-010a) needs the database; CI always provides one (ci.yml starts PostgreSQL), so a CI run without it is
// a configuration error rather than a reason to skip the account tests.
if (isCi && !databaseAvailable) {
	throw new Error('CI runs need ARIVA_E2E_SCHEMA_UPDATE=true and the Database__* variables (see README.md)');
}

// One random seed per run for the E2E account passwords (tests/support/accounts.ts). Set in the runner process before
// the workers start, so every worker derives the same passwords; never written to disk.
process.env.ARIVA_E2E_ACCOUNT_SEED ||= crypto.randomBytes(24).toString('base64url');
process.env.ARIVA_E2E_KEY_DIR ||= keyDirectory;

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
			// The suite talks to the hosts over loopback and sets X-Forwarded-For per test, so the per-address sign-in
			// limit applies only where a test means it to (production trusts only the ingress network).
			Security__ForwardedHeaders__KnownProxies__0: '127.0.0.1',
			Security__ForwardedHeaders__KnownProxies__1: '::1',
			...extraEnvironment
		}
	};
}

export default defineConfig({
	testDir: './tests',
	globalTeardown: './tests/support/global-teardown.ts',
	outputDir: './test-results',
	fullyParallel: true,
	forbidOnly: isCi,
	retries: isCi ? 1 : 0,
	workers: isCi ? 2 : undefined,
	timeout: 30_000,
	expect: { timeout: 5_000 },
	reporter: [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }]],
	use: {
		trace: 'retain-on-failure'
	},
	projects: [
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
		dotnetHost(project('Backplane/Ariva.Api.Main'), `${hosts.main}/health/readiness`, true, databaseAvailable ? developmentUserEnvironment() : {}),
		dotnetHost(project('Backplane/Ariva.Api.Integration'), `${hosts.integration}/health/readiness`),
		dotnetHost(project('Backplane/Ariva.Api.Ingest'), `${hosts.ingest}/health/readiness`),
		dotnetHost(project('Simulation/Ariva.Simulation.Api'), `${hosts.simulation}/health/readiness`),
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
