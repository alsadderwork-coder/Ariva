import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, devices } from '@playwright/test';
import { structureProblems } from './tests/smoke/support/guard';
import { missingVariables, smokeTarget } from './tests/smoke/support/target';

// ARV-128 (TC-56): the read-only smoke suite against a deployed environment, at go-live and after each release (wiki 04
// section 8). It starts nothing, provisions nothing and writes nothing:
//   npm run test:smoke        (npx playwright test --config=playwright.smoke.config.ts)
// Every address, the smoke account, its site, the test device's credential and the display's come from ARIVA_SMOKE_*
// environment variables (tests/smoke/support/target.ts), never from a file. The suite refuses, before any test starts,
// a test that is not @read against a non-local target (tests/smoke/support/guard.ts); traces, screenshots and videos are
// off and the run fails if a credential reached its report or output folder (tests/smoke/support/artifact-reporter.ts).
// The same specs run against the local E2E stack as the `smoke` project of playwright.config.ts, which provisions it.

const here = path.dirname(fileURLToPath(import.meta.url));
const testDir = path.join(here, 'tests', 'smoke');

// Probe files (tests/smoke/probes) exist only for the guard's own test (tests/api/smoke-guard.spec.ts): ARIVA_SMOKE_PROBES
// names the ones to load. Whatever a probe is, it passes the same guard as every spec.
const probes = (process.env.ARIVA_SMOKE_PROBES ?? '')
	.split(',')
	.map((name) => name.trim())
	.filter(Boolean);
if (probes.some((name) => !/^[a-z-]{1,40}$/.test(name) || !fs.existsSync(path.join(testDir, 'probes', `${name}.probe.ts`)))) {
	throw new Error('ARV-128: ARIVA_SMOKE_PROBES names probe files of tests/smoke/probes only');
}

// The local E2E stack's skip switch never applies to a deployed run (it would turn every test into a skip).
delete process.env.ARIVA_SMOKE_UNAVAILABLE;

// The folders the run empties (Playwright empties the output folder, the HTML reporter its report folder) and the JSON file
// it writes stay inside this project's own git-ignored smoke folders (security review recommendation 4), so a mistyped
// variable or option can never empty another folder. Lexical, after resolving against this folder; a folder that is a
// symbolic link is refused too.
const outputRoots = ['playwright-report-smoke', 'test-results-smoke', 'test-results-smoke-runs'];
function confined(name: string, value: string): string {
	const resolved = path.resolve(here, value);
	const relative = path.relative(here, resolved);
	const parts = relative.split(path.sep);
	const escapes = relative === '' || path.isAbsolute(relative) || parts[0] === '..' || !outputRoots.includes(parts[0]);
	const linked = parts.some((_, i) => {
		const at = path.join(here, ...parts.slice(0, i + 1));
		return fs.existsSync(at) && fs.lstatSync(at).isSymbolicLink();
	});
	if (escapes || linked) throw new Error(`ARV-128: ${name} must be inside ${outputRoots.join(', ')} of Platform/Testing/Ariva.E2E; refused`);
	return resolved;
}

// Command line options that would override what keeps the credentials out of the artifacts: --trace records every header
// and typed value, --reporter replaces the artifact check, and UI and debug modes record traces too; --output must stay in
// the smoke folders (above). Checked in the runner, whose arguments are Playwright's (a worker's are not).
if (!process.env.TEST_WORKER_INDEX) {
	const args = process.argv.slice(2);
	const refused = args.filter((arg, index) => {
		if (/^--(ui|ui-host|ui-port|debug)(=|$)/.test(arg) || /^--reporter(=|$)/.test(arg)) return true;
		if (arg === '--trace') return args[index + 1] !== 'off';
		return /^--trace=/.test(arg) && arg !== '--trace=off';
	});
	if (refused.length > 0) throw new Error(`ARV-128: the smoke suite keeps traces off and its own reporters; refused: ${refused.map((arg) => arg.split('=')[0]).join(', ')}`);
	args.forEach((arg, index) => {
		if (arg === '--output') confined('--output', args[index + 1] ?? '');
		else if (arg.startsWith('--output=')) confined('--output', arg.slice('--output='.length));
	});
}

// Before anything else: every variable, by name (the values are never printed). Probes need the addresses only.
const missing = missingVariables(probes.length > 0 ? 'urls' : 'all');
if (missing.length > 0) throw new Error(`ARV-128: the smoke suite needs ${missing.join(', ')} (wiki 04 section 8)`);
const target = smokeTarget();

// A spec or probe imports only from tests/smoke/support (guard.ts, layer 2).
const files = [
	...fs.readdirSync(testDir).filter((name) => name.endsWith('.spec.ts')).map((name) => path.join(testDir, name)),
	...probes.map((name) => path.join(testDir, 'probes', `${name}.probe.ts`))
];
const problems = structureProblems(files);
if (problems.length > 0) throw new Error(`ARV-128: a smoke spec may import from tests/smoke/support only; refused:\n${problems.join('\n')}`);

const outputDir = path.join(here, 'test-results-smoke');
const reportDir = confined('ARIVA_SMOKE_REPORT_DIR', process.env.ARIVA_SMOKE_REPORT_DIR || 'playwright-report-smoke');
const jsonFile = process.env.ARIVA_SMOKE_JSON_FILE ? confined('ARIVA_SMOKE_JSON_FILE', process.env.ARIVA_SMOKE_JSON_FILE) : undefined;

export default defineConfig({
	testDir,
	// The specs directly in tests/smoke, or the named probes only.
	testMatch: probes.length > 0 ? new RegExp(`[\\\\/]tests[\\\\/]smoke[\\\\/]probes[\\\\/](${probes.join('|')})\\.probe\\.ts$`) : /[\\/]tests[\\/]smoke[\\/][a-z0-9-]+\.spec\.ts$/,
	outputDir,
	fullyParallel: false,
	forbidOnly: true,
	// One worker: the signed-in checks share one account, whose TOTP codes count once each (replay guard).
	workers: 1,
	retries: 0,
	timeout: 60_000,
	expect: { timeout: 10_000 },
	reporter: [
		['list'],
		['html', { outputFolder: reportDir, open: 'never' }],
		...(jsonFile ? [['json', { outputFile: jsonFile }] as ['json', { outputFile: string }]] : []),
		// Last, so the HTML report is written before it is searched.
		['./tests/smoke/support/artifact-reporter.ts', { folders: [reportDir, ...(jsonFile ? [jsonFile] : [])], patterns: true }]
	],
	use: {
		// Never: a trace holds every request header and every value typed, a screenshot or video the screens.
		trace: 'off',
		screenshot: 'off',
		video: 'off'
	},
	projects: [
		{
			name: 'smoke',
			use: {
				...devices['Desktop Chrome'],
				baseURL: target.base,
				locale: 'en-US',
				launchOptions: { executablePath: process.env.ARIVA_E2E_CHROMIUM || undefined }
			}
		}
	]
});
