import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, devices } from '@playwright/test';
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

function dotnetHost(projectPath: string, healthUrl: string, buildFirst = false) {
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
			Database__AllowSchemaUpdate: process.env.ARIVA_E2E_SCHEMA_UPDATE === 'true' ? 'true' : 'false',
			LogFile__Path: logFile
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
		dotnetHost(project('Backplane/Ariva.Api.Main'), `${hosts.main}/health/readiness`, true),
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
