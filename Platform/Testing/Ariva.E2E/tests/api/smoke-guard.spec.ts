import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import { sourceProblems, supportFolder } from '../smoke/support/guard';
import { isLocalUrl, nonLocalTargets } from '../support/local-target';
import { playwrightChild as run } from '../support/smoke-runs';

// ARV-128 (TC-56, CWE-306): the release smoke suite never writes, attacks or loads a deployed environment. These tests run
// the smoke config (playwright.smoke.config.ts) and the E2E config in child Playwright processes against made-up targets
// that never resolve (*.invalid): a non-local target with a @write, @attack, @load or untagged test refuses to start, a
// local one runs them; a @read test that tries to write is stopped before anything is sent; a credential that reaches the
// report fails the run without being printed; a spec past the guard and the E2E suites against a non-local target refuse
// to load. No child reaches the network.

const nonLocal = { ARIVA_SMOKE_BASE_URL: 'https://ariva-smoke.invalid', ARIVA_SMOKE_MAIN_URL: 'https://api-main.ariva-smoke.invalid', ARIVA_SMOKE_INGEST_URL: 'https://api-ingest.ariva-smoke.invalid' };
// Port 9 (discard) on loopback: nothing listens, and these probes send nothing anyway.
const local = { ARIVA_SMOKE_BASE_URL: 'http://localhost:9', ARIVA_SMOKE_MAIN_URL: 'http://127.0.0.1:9', ARIVA_SMOKE_INGEST_URL: 'http://[::1]:9' };

const smokeRun = (probes: string, target: Record<string, string>, extra: Record<string, string> = {}) =>
	run('playwright.smoke.config.ts', { ARIVA_SMOKE_PROBES: probes, ...target, ...extra });

test.describe.configure({ mode: 'parallel' });

test('which addresses count as local', () => {
	const cases: [string, boolean][] = [
		['http://localhost:51001', true],
		['https://localhost', true],
		['http://127.0.0.1:51011', true],
		['http://127.10.20.30', true],
		['http://[::1]:51002', true],
		// WHATWG parsing turns these into 127.0.0.1.
		['http://2130706433', true],
		['http://0x7f000001', true],
		['redis://:password@localhost:6380', true],
		['http://localhost.', false],
		['http://api.localhost', false],
		['http://localhost.ariva.example', false],
		['http://localhost@ariva.example', false],
		['http://ariva.example#@localhost', false],
		['http://0.0.0.0:51001', false],
		['http://10.0.0.5', false],
		['http://192.168.1.10', false],
		['http://[::ffff:127.0.0.1]', false],
		['https://ariva.dalil.example', false],
		['ftp://localhost', false],
		['file:///etc/hosts', false],
		['localhost:51001', false],
		['not an address', false],
		['', false]
	];
	for (const [value, expected] of cases) expect(isLocalUrl(value), value).toBe(expected);
	expect(nonLocalTargets({ a: 'http://localhost:1', b: 'https://ariva.example', c: undefined, d: '' })).toEqual(['b']);
	expect(nonLocalTargets({ kafka: 'localhost:19092,127.0.0.1:19093' }, { kafka: 'bootstrap' })).toEqual([]);
	expect(nonLocalTargets({ kafka: 'localhost:19092,kafka.ariva.example:9092' }, { kafka: 'bootstrap' })).toEqual(['kafka']);
	expect(nonLocalTargets({ database: '::1', other: 'db.ariva.example' }, { database: 'host', other: 'host' })).toEqual(['other']);
});

for (const kind of ['write', 'attack', 'load'] as const) {
	test(`a @${kind} test refuses to start against a non-local target and runs against a local one`, async () => {
		test.setTimeout(240_000);
		const refused = smokeRun(kind, nonLocal);
		expect(refused.status, refused.output).not.toBe(0);
		expect(refused.output).toContain(`is @${kind}; the smoke suite runs only @read tests against a non-local target; refused`);
		expect(refused.passed + refused.failed, 'no test started').toBe(0);

		const ran = smokeRun(kind, local);
		expect(ran.status, ran.output).toBe(0);
		expect(ran.passed, `the @${kind} probe ran locally`).toBe(1);
	});
}

test('a test that names no smoke kind is refused everywhere', async () => {
	test.setTimeout(240_000);
	for (const target of [nonLocal, local]) {
		const refused = smokeRun('untagged', target);
		expect(refused.status, refused.output).not.toBe(0);
		expect(refused.output).toContain('must name exactly one of @read, @write, @attack, @load (it names none); refused');
		expect(refused.passed + refused.failed, 'no test started').toBe(0);
	}
});

test('a test that names two kinds, one of them in its title, is refused everywhere', async () => {
	test.setTimeout(240_000);
	for (const target of [nonLocal, local]) {
		const refused = smokeRun('title-tag', target);
		expect(refused.status, refused.output).not.toBe(0);
		expect(refused.output).toContain('(it names @read and @write); refused');
		expect(refused.passed + refused.failed, 'no test started').toBe(0);
	}
});

test('a @read test that tries to write to a non-local target is stopped before anything is sent', async () => {
	test.setTimeout(180_000);
	const outcome = smokeRun('read-writes', nonLocal);
	expect(outcome.status, outcome.output).toBe(0);
	expect(outcome.passed, 'the probe asserts each refusal').toBe(1);
});

test('a smoke credential that reaches the report fails the run, which names the variable and never the value', async () => {
	test.setTimeout(180_000);
	const canary = `smoke-canary-${crypto.randomBytes(12).toString('hex')}`;
	const outcome = smokeRun('leak', local, { ARIVA_SMOKE_PASSWORD: canary });
	expect(outcome.status, outcome.output).not.toBe(0);
	expect(outcome.output).toContain('holds the value of ARIVA_SMOKE_PASSWORD');
	expect(outcome.output).not.toContain(canary);
});

test('the structure rule refuses raw requests, new contexts and pages, and imports past tests/smoke/support', () => {
	// Security review recommendation 2: each of these reaches the target past the smoke client's and the guarded page's checks.
	const spec = path.join(supportFolder, '..', 'example.spec.ts');
	const clean = "import { send } from './support/client';\nimport { expect, smoke } from './support/smoke';\nsmoke('x', { tag: '@read' }, async ({ page }) => { await page.goto('/'); });\n";
	expect(sourceProblems(spec, clean)).toEqual([]);
	const cases: [string, string][] = [
		["await fetch('/api/v1/sites');", 'uses fetch('],
		['const ws = new WebSocket(url);', 'uses WebSocket('],
		['const x = new XMLHttpRequest();', 'uses XMLHttpRequest'],
		['const c = await page.context().browser()!.newContext();', 'uses newContext'],
		['const other = await page.context().newPage();', 'uses newPage'],
		["await page.request.post('/api/v1/admin/sites');", 'uses .request.'],
		["await page.context().request.delete('/api/v1/admin/sites/X');", 'uses .request.'],
		["import { test } from '@playwright/test';", 'imports @playwright/test'],
		["import { call } from '../support/accounts';", 'imports ../support/accounts'],
		["const m = await import(name);", 'imports a computed module']
	];
	for (const [line, problem] of cases) expect(sourceProblems(spec, clean + line).join('; '), line).toContain(problem);
});

test('a raw request in a smoke spec stops the smoke config from loading', async () => {
	test.setTimeout(180_000);
	const outcome = smokeRun('raw-request', local);
	expect(outcome.status, outcome.output).not.toBe(0);
	expect(outcome.output).toContain('raw-request.probe.ts uses fetch(');
	expect(outcome.passed + outcome.failed, 'no test started').toBe(0);
});

test('a smoke test gets the page and its guards only, by type and at run time', async () => {
	test.setTimeout(180_000);
	// The probe's @ts-expect-error lines make the project's typecheck fail if a smoke test is ever offered request, browser,
	// context or playwright again.
	const e2e = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
	const tsc = spawnSync(process.execPath, [path.join(e2e, 'node_modules', 'typescript', 'bin', 'tsc'), '--noEmit', '-p', 'tsconfig.json'], {
		cwd: e2e,
		encoding: 'utf8',
		timeout: 150_000
	});
	expect(tsc.status, `${tsc.stdout}${tsc.stderr}`).toBe(0);
	const outcome = smokeRun('fixtures', local);
	expect(outcome.status, outcome.output).toBe(0);
	expect(outcome.passed, 'the probe found none of them at run time').toBe(1);
});

test('a spec that does not go through the smoke guard refuses to load', async () => {
	test.setTimeout(180_000);
	const outcome = smokeRun('direct-import', local);
	expect(outcome.status, outcome.output).not.toBe(0);
	expect(outcome.output).toContain('a smoke spec may import from tests/smoke/support only; refused');
	expect(outcome.output).toContain('direct-import.probe.ts imports @playwright/test');
	expect(outcome.passed + outcome.failed, 'no test started').toBe(0);
});

test('the smoke suite refuses the options that would record traces or replace its artifact check', async () => {
	test.setTimeout(180_000);
	for (const args of [['--trace', 'on'], ['--trace=retain-on-failure'], ['--reporter=line'], ['--ui'], ['--debug']]) {
		const outcome = run('playwright.smoke.config.ts', { ARIVA_SMOKE_PROBES: 'write', ...local }, args);
		expect(outcome.status, `${args.join(' ')}: ${outcome.output}`).not.toBe(0);
		expect(outcome.output).toContain('the smoke suite keeps traces off and its own reporters; refused');
		expect(outcome.passed + outcome.failed, 'no test started').toBe(0);
	}
	const traceOff = run('playwright.smoke.config.ts', { ARIVA_SMOKE_PROBES: 'write', ...local }, ['--trace', 'off']);
	expect(traceOff.status, traceOff.output).toBe(0);
});

test('the smoke suite names the variables it misses, never their values', async () => {
	test.setTimeout(180_000);
	const secret = `smoke-secret-${crypto.randomBytes(12).toString('hex')}`;
	const outcome = run('playwright.smoke.config.ts', { ...nonLocal, ARIVA_SMOKE_PASSWORD: secret }, ['--list']);
	expect(outcome.status, outcome.output).not.toBe(0);
	expect(outcome.output).toContain('the smoke suite needs ARIVA_SMOKE_USERNAME, ARIVA_SMOKE_SITE, ARIVA_SMOKE_DEVICE_KEY, ARIVA_SMOKE_DISPLAY_CODE, ARIVA_SMOKE_DISPLAY_KEY');
	expect(outcome.output.includes(secret), 'the output never holds the value').toBe(false);
});

test('the E2E suites refuse to load against a non-local target, naming the target and never its address', async () => {
	test.setTimeout(180_000);
	const password = `redis-${crypto.randomBytes(12).toString('hex')}`;
	for (const [extra, name] of [
		[{ ARIVA_E2E_MAIN_URL: 'https://api-main.ariva-smoke.invalid' }, 'main'],
		[{ ARIVA_E2E_WEB_URL: 'http://10.20.30.40:51011' }, 'web'],
		[{ ARIVA_E2E_REDIS_URL: `redis://:${password}@redis.ariva-smoke.invalid:6379` }, 'redis'],
		[{ ARIVA_E2E_KAFKA_BOOTSTRAP: 'localhost:19092,kafka.ariva-smoke.invalid:9092' }, 'kafka'],
		// ARV-092b: the malware scanner api-main streams the run's uploads to.
		[{ ARIVA_E2E_CLAMD: 'clamd.ariva-smoke.invalid:3310' }, 'clamd']
	] as [Record<string, string>, string][]) {
		// --list loads the config without starting a host.
		const outcome = run('playwright.config.ts', extra, ['--list']);
		expect(outcome.status, outcome.output).not.toBe(0);
		expect(outcome.output).toContain(`they run only against this machine; refused for ${name}`);
		expect(outcome.output.includes(password), 'the output never holds the password').toBe(false);
	}
});

test('the E2E suites refuse to load in CI without the run\'s clamd, or with a clamd that is not host:port', async () => {
	// ARV-092b: every upload in the run goes through the malware scan, so a CI run never starts the hosts without it.
	test.setTimeout(180_000);
	const ci = { CI: 'true', ARIVA_E2E_SCHEMA_UPDATE: 'true', ARIVA_E2E_KAFKA_BOOTSTRAP: 'localhost:9' };
	for (const [extra, message] of [
		[{ ...ci, ARIVA_E2E_CLAMD: '' }, 'CI runs need ARIVA_E2E_CLAMD'],
		[{ ARIVA_E2E_CLAMD: 'localhost' }, 'ARIVA_E2E_CLAMD must be host:port']
	] as [Record<string, string>, string][]) {
		// --list loads the config without starting a host.
		const outcome = run('playwright.config.ts', extra, ['--list']);
		expect(outcome.status, outcome.output).not.toBe(0);
		expect(outcome.output).toContain(message);
	}
});
