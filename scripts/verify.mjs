#!/usr/bin/env node
// Ariva quality gates. Cross-platform (Windows, Linux, CI). No dependencies.
//   node scripts/verify.mjs <scope> [--no-restore]
// Scopes:
//   quick        security scan of changed files + root search guard self-test + docs text checks (used by the Stop hook)
//   build        dotnet build Ariva.slnx (security analyzers run as errors)
//   unit         build + Ariva.UnitTests (architecture, security and domain tests)
//   integration  Ariva.IntegrationTests (needs Docker for Testcontainers)
//   web          Ariva.Web: npm ci, svelte-check, lint, build
//   e2e          Platform/Testing/Ariva.E2E: API end-to-end and Playwright functional tests; --specs <file,...> runs only those.
//                After every Playwright run (e2e, visual, demo, rehearsal, pilot-load, zap, and those of story and checkpoint) the report,
//                the test output and their zips are checked for secrets (ARV-152, Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs)
//   artifacts    that artifact check alone, on the report and the test output a run left behind
//   visual       visual regression baselines (ARV-075) in the pinned Playwright image (Docker); --update regenerates them;
//                run it before e2e on a fresh database (the demo seed)
//   demo         the scripted demo (ARV-064): the reference evening in real time (about two hours) through the whole
//                pipeline, Stream included (Docker services and the E2E database environment); ARIVA_DEMO_START and
//                ARIVA_DEMO_END (demo minutes) shorten it
//   rehearsal    the validation rehearsal against the live pipeline (ARV-104j): the reference evening through Ingest, Stream
//                and Cronz in real time, the simulator's observers capturing through the capture API, campaigns closed and
//                their results frozen (about two hours; fresh services and the E2E database environment); ARIVA_DEMO_START and
//                ARIVA_DEMO_END (quarter hours, default 1080 and 1170) set the rehearsed window. Runs at phase ends and before
//                go-live, never in a story gate (docs/harness/test-cadence.md)
//   pilot-load   the pilot hall load run (ARV-124a): the hall's 30 sensors, AMAN's feed, 50 screens and 10 boards, then the D5 burst
//                (100 sensors, 15,000 positions a second) and the recovery, through Ingest, Kafka and Stream in real time (about
//                20 minutes; fresh services and the E2E database environment); ARIVA_LOAD_STEADY_SECONDS, ARIVA_LOAD_BURST_SECONDS
//                and ARIVA_LOAD_RECOVERY_SECONDS reshape it. Report in .verify/load/pilot. On demand and at phase ends, never in a
//                story gate (docs/harness/test-cadence.md)
//   zap          dynamic security scan (ARV-063): OWASP ZAP in its pinned image against the E2E stack (Docker and the E2E
//                database environment; about 10 minutes); reports in Platform/Testing/Ariva.E2E/zap-reports
//   security     scanner self-test + full scan + ops tooling self-tests (ARV-126, the TLS check of ARV-084) + dependency audits
//                + the E2E expect message check (no answer body as a message, ARV-152) and the artifact check's self-test
//   docs         text rules on Markdown (no em dashes, no double hyphens in prose)
//   backend      build + unit + security
//   mutation     Stryker.NET on the pure engines (ARV-069; one to three hours, mostly the queue engine; report in .verify/stryker);
//                fails when a method is in Stryker's safe mode or no score is printed (ARV-069a, scripts/mutation-run.mjs). Runs in
//                its own worktree of HEAD under its own lock, so builds and story gates may run in this checkout meanwhile
//   all          everything except integration (add --with-integration)
//   story        the per-story gate (test cadence, docs/harness/test-cadence.md): backend and docs always; web when the
//                web app changed; integration and E2E scoped to the story, or in full when the change is wide.
//                --specs <file,...> the story's E2E specs (tests/api/x.spec.ts); --integration "FullyQualifiedName~A|..."
//                (methods whose fully qualified name holds A, or another term; ARV-164b: only this shape, run as --filter-method);
//                --base <ref> compare with this commit (default HEAD: the uncommitted story)
//   checkpoint   the full suite on a committed tree every few stories and at each phase end: backend, docs, web, integration,
//                visual, e2e and mutation on the engine code changed since the last checkpoint; records backlog/checkpoint.json
//                --mutation-base <commit>: only while no checkpoint is recorded, the commit of an earlier checkpoint run whose
//                mutation step passed (the run failed elsewhere); mutation then runs only on engine code changed since it
// --plan prints what story or checkpoint would run without running it.
// Writes .verify/last.json with per-step results.

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { checkArtifacts, derivedVariables } from '../Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs';
import { methodFilter } from './test-counts.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WEB = path.join(ROOT, 'Platform', 'Frontplane', 'Ariva.Web');
const E2E = path.join(ROOT, 'Platform', 'Testing', 'Ariva.E2E');
const IS_WIN = process.platform === 'win32';
const args = process.argv.slice(2);
const VALUE_FLAGS = new Set(['--specs', '--integration', '--base', '--mutation-base']);
const scope = args.find((a, i) => !a.startsWith('--') && !VALUE_FLAGS.has(args[i - 1])) || 'backend';
const PLAN = args.includes('--plan');
const results = [];
// Set by the solution build: the integration step builds its own project only when no solution build ran before it.
let solutionBuilt = false;

// Telemetry off for every child process (2026-10-09): the Microsoft Testing Platform reports usage from every test host it
// starts, the network proxy refuses it, and Stryker, which starts hosts all the time, took 80 minutes for its initial test
// run instead of about 15; with this set it took 16. Values already set by the caller win.
for (const [name, value] of Object.entries({ DOTNET_CLI_TELEMETRY_OPTOUT: '1', TESTINGPLATFORM_TELEMETRY_OPTOUT: '1', PACT_DO_NOT_TRACK: 'true' }))
  process.env[name] ??= value;

function flag(name) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined;
}

function git(gitArgs) {
  return execFileSync('git', gitArgs, { cwd: ROOT, encoding: 'utf8' });
}

function run(name, cmd, cmdArgs, cwd = ROOT, { optional = false } = {}) {
  const started = Date.now();
  process.stdout.write(`\n=== ${name}: ${cmd} ${cmdArgs.join(' ')}\n`);
  if (PLAN) { results.push({ name, ok: true, planned: true }); return true; }
  // shell is only used on Windows to resolve npm/npx .cmd shims; arguments are fixed strings from this file.
  const r = spawnSync(cmd, cmdArgs, { cwd, stdio: 'inherit', shell: IS_WIN && /^(npm|npx)$/.test(cmd) });
  const ok = r.status === 0;
  const skipped = r.error && r.error.code === 'ENOENT';
  results.push({ name, ok: ok || (optional && skipped), skipped: !!skipped, seconds: Math.round((Date.now() - started) / 1000), exit: r.status });
  if (skipped) process.stdout.write(`(${cmd} not found${optional ? ', skipped' : ''})\n`);
  return ok;
}

// The NuGet audit fails on a High or Critical advisory, as CI's "Vulnerable packages" step does: dotnet list exits 0 whatever
// it finds, so its table is read (ARV-184 review). Moderate and Low advisories are printed and do not fail the gate.
function nugetAudit() {
  const name = 'nuget vulnerability audit (fails on high or critical)';
  const cmdArgs = ['list', 'Ariva.slnx', 'package', '--vulnerable', '--include-transitive'];
  const started = Date.now();
  process.stdout.write(`\n=== ${name}: dotnet ${cmdArgs.join(' ')}\n`);
  if (PLAN) { results.push({ name, ok: true, planned: true }); return; }
  // English output whatever the machine's locale, since the severity words are what is read.
  const r = spawnSync('dotnet', cmdArgs, { cwd: ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' } });
  process.stdout.write(`${r.stdout ?? ''}${r.stderr ?? ''}`);
  const skipped = r.error?.code === 'ENOENT';
  const severe = /\b(High|Critical)\b/.test(r.stdout ?? '');
  if (severe) process.stdout.write('High or critical vulnerable package (CI fails on it too)\n');
  if (skipped) process.stdout.write('(dotnet not found, skipped)\n');
  results.push({ name, ok: skipped || (r.status === 0 && !severe), skipped, seconds: Math.round((Date.now() - started) / 1000), exit: r.status });
}

// ARV-104j review and ARV-152 (CWE-532): no credential in the Playwright report, the test output or a trace. Every Playwright
// run this script starts goes through playwright(): it gets an account seed of its own unless the caller set one (so the
// run's E2E account passwords can be looked for afterwards), and once it has ended (the HTML reporter writes after global
// teardown) its artifacts are scanned by Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs: device and display
// credentials, integration client secrets, JWTs, TOTP seeds and codes, recovery codes, temporary passwords, the run's account
// passwords and secret variables, in plain files, the report's embedded zip and trace zips. Only files and kinds are printed.
const CALLER_SEED = process.env.ARIVA_E2E_ACCOUNT_SEED;
// The run's other secrets (simulator, mock AMAN, ACRIS and Cronz keys, the validation reader's password) are derived from the
// run's seed and given to Playwright, which keeps a value it is given, so the check can look for them too; a caller's own
// value wins.
const CALLER_VARIABLES = new Set(Object.keys(derivedVariables('x')).filter(name => process.env[name] !== undefined));
function artifactCheck(folders, started) {
  // The smoke configuration's own report and output, when a spec of this run (smoke-config.spec.ts) wrote them.
  const smoke = ['playwright-report-smoke', 'test-results-smoke', 'test-results-smoke-runs'].map(f => path.join(E2E, f))
    .filter(f => fs.existsSync(f) && fs.statSync(f).mtimeMs >= started);
  const paths = [...folders.map(f => path.join(E2E, f)), ...smoke];
  process.stdout.write(`\n=== no secret in the test artifacts: ${paths.map(p => path.relative(ROOT, p)).join(', ')}\n`);
  if (PLAN) { results.push({ name: 'no secret in the test artifacts', ok: true, planned: true }); return true; }
  const ok = checkArtifacts(paths, { requireSeed: true });
  results.push({ name: 'no secret in the test artifacts (report, output, traces)', ok });
  return ok;
}
// The hosts of a Playwright run listen on fixed ports (Platform/Testing/Ariva.E2E/tests/support/hosts.ts), and a host of the run
// before can still be stopping when the next one starts: on 2026-10-20 the checkpoint's E2E run met the visual run's Ingest on
// 51002 ("address already in use") five seconds after it ended. So a run first waits, at most a minute, until each port can be
// bound again, and fails at once with the ports named when one stays taken (a host left running, or another program).
const E2E_HOST_PORTS = [51001, 51002, 51003, 51004, 51005, 51011, 51020];

function hostPortsFree(name, seconds = 60) {
  if (PLAN) return true;
  const probe = `const net = require('node:net');
const ports = ${JSON.stringify(E2E_HOST_PORTS)};
const until = Date.now() + ${seconds * 1000};
const taken = port => new Promise(done => {
  const server = net.createServer();
  server.once('error', () => done(true));
  server.listen(port, '127.0.0.1', () => server.close(() => done(false)));
});
(async () => {
  for (;;) {
    const busy = [];
    for (const port of ports) if (await taken(port)) busy.push(port);
    if (busy.length === 0) process.exit(0);
    if (Date.now() > until) { process.stdout.write(busy.join(', ')); process.exit(1); }
    await new Promise(next => setTimeout(next, 1000));
  }
})();`;
  const r = spawnSync(process.execPath, ['-e', probe], { encoding: 'utf8' });
  if (r.status === 0) return true;
  console.log(`${name}: the E2E host ports ${(r.stdout || '').trim()} are still taken after ${seconds} s (a host of an earlier run, or another program)`);
  results.push({ name, ok: false, seconds, exit: r.status });
  return false;
}

function playwright(name, playwrightArgs, folders = ['playwright-report', 'test-results']) {
  if (!hostPortsFree(name)) return;
  const started = Date.now();
  process.env.ARIVA_E2E_ACCOUNT_SEED = CALLER_SEED || crypto.randomBytes(24).toString('base64url');
  const derived = Object.entries(derivedVariables(process.env.ARIVA_E2E_ACCOUNT_SEED)).filter(([variable]) => !CALLER_VARIABLES.has(variable));
  for (const [variable, value] of derived) process.env[variable] = value;
  try {
    run(name, 'npx', ['playwright', 'test', ...playwrightArgs], E2E);
  } finally {
    artifactCheck(folders, started);
    if (CALLER_SEED === undefined) delete process.env.ARIVA_E2E_ACCOUNT_SEED;
    for (const [variable] of derived) delete process.env[variable];
  }
}

function docsCheck() {
  const bad = [];
  const walk = d => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      // The client material folder is never read, not even by a gate (owner decision 2026-10-09, ARV-139c).
      if (['node_modules', '.git', 'bin', 'obj', '.svelte-kit', 'build', 'prototype', '.private'].includes(e.name)) continue;
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (p.endsWith('.md')) {
        let fenced = false; // shell commands in fenced blocks legitimately use " -- " to end options
        fs.readFileSync(p, 'utf8').split(/\r?\n/).forEach((l, i) => {
          if (l.includes('—') || l.includes('–')) bad.push(`${path.relative(ROOT, p)}:${i + 1} dash character`);
          if (/^\s*```/.test(l)) { fenced = !fenced; return; }
          if (fenced) return;
          const prose = l.replace(/`[^`]*`/g, '');
          if (/\s--\s/.test(prose)) bad.push(`${path.relative(ROOT, p)}:${i + 1} double hyphen in prose`);
        });
      }
    }
  };
  walk(ROOT);
  bad.forEach(b => console.log(`DOCS ${printablePath(b)}`));
  results.push({ name: 'docs text rules', ok: bad.length === 0, findings: bad.length });
  return bad.length === 0;
}

// Names Git for Windows refuses to check out (the owner works on Windows; ARV-157's nul.cs fixture broke a checkout there):
// a reserved device name, with or without an extension, a character Windows does not allow in a name, a name ending in a dot
// or a space, and two paths that differ only in case.
const WINDOWS_RESERVED = /^(con|prn|aux|nul|conin\$|conout\$|com[0-9¹²³]|lpt[0-9¹²³])(?=$|[. :])/i;

function windowsPathProblems(paths) {
  const problems = [];
  const seen = new Map();
  for (const p of paths) {
    for (const name of p.split('/')) {
      if (WINDOWS_RESERVED.test(name)) problems.push(`${p}: ${name} is a reserved device name on Windows`);
      else if (/[<>:"\\|?*\x00-\x1f]/.test(name)) problems.push(`${p}: ${name} holds a character Windows does not allow`);
      else if (/[. ]$/.test(name)) problems.push(`${p}: ${name} ends in a dot or a space`);
    }
    const key = p.toLowerCase();
    if (seen.has(key)) problems.push(`${p}: differs only in case from ${seen.get(key)}`);
    else seen.set(key, p);
  }
  return problems;
}

function windowsPathsCheck() {
  const name = 'every tracked path checks out on Windows';
  // The check proves itself first: each bad name is refused alone, the case clash once, and the good names pass.
  const bad = ['a/nul.cs', 'NUL', 'b/Com1.txt', 'lpt9', 'x/con.tar.gz', 'conin$', 'a:b', 'q?', 'tab\tname', 'dot.', 'space '];
  const missed = bad.filter(p => windowsPathProblems([p]).length !== 1);
  const good = ['nul-script', 'console.md', 'auxiliary/x.cs', 'com10', 'a/.gitignore', 'Dockerfile'];
  const refused = good.filter(p => windowsPathProblems([p]).length !== 0);
  if (missed.length || refused.length || windowsPathProblems(['Docs/a.md', 'docs/a.md']).length !== 1) {
    console.log(`WINDOWS PATHS self-test failed: missed ${JSON.stringify(missed)}, refused ${JSON.stringify(refused)}`);
    results.push({ name, ok: false });
    return false;
  }
  // The index only (what a clone checks out, staged changes included): never the working tree's untracked files.
  const problems = windowsPathProblems(git(['ls-files', '-z']).split('\0').filter(Boolean));
  problems.forEach(p => console.log(`WINDOWS PATH ${printablePath(p)}`));
  results.push({ name, ok: problems.length === 0, findings: problems.length });
  return problems.length === 0;
}

// Test cadence (owner decision 2026-10-08, docs/harness/test-cadence.md). A story runs the scoped gate; the full suite runs
// at a checkpoint every CHECKPOINT_EVERY stories, at each phase end and before go-live.
const CHECKPOINT_FILE = path.join(ROOT, 'backlog', 'checkpoint.json');
const CHECKPOINT_EVERY = 5;
// A change here reaches every host, every screen or the whole pipeline: the story runs the full integration and E2E suites.
const WIDE = [
  /^Platform\/Backplane\/Ariva\.(Api\.Common|ServiceDefaults|Di|Api\.Stream)\//,
  /^Platform\/Backplane\/Ariva\.Core\/(Security|Messaging|Queueing)\//,
  /^Platform\/Backplane\/Ariva\.Core\/(Global|RoleCodes)\.cs$/,
  // ARV-160: what every service and host shares rather than one feature: the entity bases (Domain/Common), the value types
  // and the event base (Domain/Components: DisplayText, Geometry, EventBase), the contracts the interceptors, the outbox and
  // the site scope read (Domain/Contracts: IAuditable, ISiteBound, IEvent), the shared constants (Domain/Constants:
  // ValidationErrors, SearchCounts, AuditActions), the criteria base every search binds (BaseCriteria.cs), the service
  // contracts under every service (Services/Core: IUnitOfWork, ICurrentUser, which the site scope reads, IStorageProvider,
  // ISvcMessageBus) and the service bases and unit of work (Infra Services/Foundation: SvcBase, SvcDb with the paged search,
  // UnitOfWork with the commit and outbox order), with the paged result every search returns (PageViewModel in
  // AdministrationViewModels.cs). What every host runs at its start: the settings (Infra Settings: the database, hosting,
  // token and session settings, every Postgres connection, the certificate downloads), the schema checks and migrations
  // (the Timescale classes; a new script is NEW_SCRIPT below), the health metrics and the dependency outage answer
  // (Monitoring, Resilience), and the packages of Ariva.Core and Ariva.Infra. The other entities, enums, criteria, inputs
  // and view models are one file per feature and stay scoped.
  /^Platform\/Backplane\/Ariva\.Core\/Domain\/(Common|Components|Contracts|Constants)\//,
  /^Platform\/Backplane\/Ariva\.Core\/Domain\/(Criteria\/BaseCriteria|ViewModels\/AdministrationViewModels)\.cs$/,
  /^Platform\/Backplane\/Ariva\.Core\/Services\/Core\//,
  /^Platform\/Backplane\/Ariva\.Infra\/Services\/Foundation\//,
  /^Platform\/Backplane\/Ariva\.Infra\/Timescale\/[^/]+\.cs$/,
  /^Platform\/Backplane\/Ariva\.(Core|Infra)\/(Ariva\.(Core|Infra)\.csproj|packages\.lock\.json)$/,
  /^Platform\/Backplane\/Ariva\.Infra\/(NHibernate|Messaging|Streaming|Security|DataProtection|Caching|Settings|Monitoring|Resilience)\//,
  /^Platform\/Simulation\//,
  // The web: the shared client code and its build, and the shell every screen sits in (the root layout and error page,
  // the (modules) layout with the sign-in gate, the shell and the step-up dialog of every signed-in screen, the (public)
  // layout and the frame of the public screens and the error page, app.html and app.css, the layout and shared
  // components, the theme, the navigation, the i18n loader; the message files and the page components are per feature).
  /^Platform\/Frontplane\/Ariva\.Web\/(src\/lib\/core\/|src\/hooks|package(-lock)?\.json$|svelte\.config|vite\.config)/,
  /^Platform\/Frontplane\/Ariva\.Web\/src\/(app\.(html|css)|routes\/(\((modules|public)\)\/)?\+(layout\.(svelte|ts)|error\.svelte)|lib\/(navigation|utils)\.ts|lib\/i18n\/index\.ts)$/,
  /^Platform\/Frontplane\/Ariva\.Web\/src\/lib\/(components\/(layout|shared)|theme)\//,
  /^Platform\/Frontplane\/Ariva\.Web\/src\/lib\/components\/auth\/(StepUpDialog|PublicFrame)\.svelte$/,
  /^Platform\/Testing\/Ariva\.E2E\/(playwright\.config\.ts$|package(-lock)?\.json$|tests\/support\/)/,
  /^(Directory\.(Build|Packages)\.props|Ariva\.slnx|global\.json|nuget\.config)$/i
];
// Persistence: the integration tests for the story's areas (--integration), and the whole suite for a new script.
const PERSISTENCE = /^Platform\/Backplane\/(Ariva\.Infra|Ariva\.IntegrationTests)\//;
const NEW_SCRIPT = /^Platform\/Backplane\/Ariva\.Infra\/Timescale\/(Scripts\/|checksums\.lock$)/;
const E2E_RELEVANT = /^(Platform\/(Backplane|Business|Simulation|Frontplane|Testing\/Ariva\.E2E)\/|Directory\.)/;
const WEB_CHANGED = /^Platform\/Frontplane\/Ariva\.Web\//;
// Always run with a story's own specs: sign-in, the security baseline and the permission matrix (authorization is where
// cross-site and missing-permission defects show), and the shell screen.
const E2E_SMOKE = [
  'tests/api/health.spec.ts', 'tests/api/auth.spec.ts', 'tests/api/security-baseline.spec.ts',
  'tests/api/permission-matrix.spec.ts', 'tests/functional/shell.spec.ts'
];
// Values from the command line reach git, dotnet and (on Windows, through the npx shim) a shell: fixed shapes only.
// ARV-128: the release smoke suite's specs (tests/smoke) too; its probes (tests/smoke/probes) run only through tests/api/smoke-guard.spec.ts.
const SPEC = /^tests\/(api|functional|smoke)\/[a-z0-9-]+\.spec\.ts$/;
// ARV-160: never a leading hyphen, which git would read as an option (--cached, -S<text>) rather than a revision.
const GIT_REF = /^(?!-)[A-Za-z0-9_./^~-]{1,100}$/;
const SHA = /^[0-9a-f]{7,40}$/;

// The e2e scope's own --specs (the story gate passes its specs itself): fixed shapes of files that exist, or null after a refusal.
function scopeSpecs() {
  const specs = (flag('--specs') ?? '').split(',').map(s => s.trim()).filter(Boolean);
  const refused = specs.filter(s => !SPEC.test(s) || !fs.existsSync(path.join(E2E, s)));
  if (refused.length) {
    results.push({ name: `e2e --specs refused (${refused.map(s => s.slice(0, 80)).join('; ')}); specs are tests/api|functional|smoke/<name>.spec.ts files that exist`, ok: false });
    return null;
  }
  return specs;
}

// ARV-160: a rename counts at both its paths (--no-renames), so a file moved out of a wide folder still makes the story wide,
// and every path comes through as written (-z), never quoted, so a path with characters outside ASCII, a quote or a
// backslash still meets the patterns.
function changedFiles(base) {
  const tracked = git(['diff', '--name-only', '--no-renames', '-z', base]).split('\0');
  const untracked = git(['ls-files', '--others', '--exclude-standard', '-z']).split('\0');
  return [...new Set([...tracked, ...untracked].filter(Boolean))];
}

// A path as printable ASCII for the console: any other character as \u{...}, so a bidirectional override or a control
// character in a file name cannot change what the line shows.
const printablePath = f => f.replace(/[^\x20-\x7E]/gu, c => `\\u{${c.codePointAt(0).toString(16)}}`);

function readCheckpoint() {
  if (!fs.existsSync(CHECKPOINT_FILE)) return undefined;
  const c = JSON.parse(fs.readFileSync(CHECKPOINT_FILE, 'utf8'));
  return c && typeof c.commit === 'string' && SHA.test(c.commit) ? c : undefined;
}

// Stories committed since the last checkpoint: distinct ids in commit subjects of the form "ARV-nnn: title".
function storiesSince(commit) {
  const ids = git(['log', `${commit}..HEAD`, '--format=%s']).split('\n').map(s => /^(ARV-\d+(?:[a-z]\d*)?):/.exec(s)?.[1]).filter(Boolean);
  return new Set(ids).size;
}

function checkpointNote() {
  const last = readCheckpoint();
  if (!last) { console.log('\nNOTE no checkpoint recorded: run node scripts/verify.mjs checkpoint on a committed tree'); return; }
  let n;
  try { n = storiesSince(last.commit); } catch { console.log(`\nNOTE the last checkpoint ${last.commit.slice(0, 10)} is not in this history`); return; }
  const due = n >= CHECKPOINT_EVERY;
  console.log(`\nNOTE ${n} stories since the last checkpoint (${last.commit.slice(0, 10)}, ${last.at})${due ? ': a checkpoint is due before the next story' : ''}`);
}

function storyGate() {
  const base = flag('--base') ?? 'HEAD';
  const specs = (flag('--specs') ?? '').split(',').map(s => s.trim()).filter(Boolean);
  const filter = flag('--integration');
  const refused = [
    ...(GIT_REF.test(base) ? [] : ['--base']),
    ...specs.filter(s => !SPEC.test(s) || !fs.existsSync(path.join(E2E, s))).map(s => `--specs ${s.slice(0, 80)}`),
    ...(filter === undefined || methodFilter(filter) ? [] : ['--integration'])
  ];
  if (refused.length) {
    results.push({ name: `story arguments refused (${refused.join('; ')}); specs are tests/api|functional|smoke/<name>.spec.ts files that exist, the integration filter FullyQualifiedName~<name> terms joined by |`, ok: false });
    return;
  }
  const files = changedFiles(base);
  const wide = files.filter(f => WIDE.some(w => w.test(f)));
  const persistence = files.some(f => PERSISTENCE.test(f));
  const newScript = files.some(f => NEW_SCRIPT.test(f));
  const e2eRelevant = files.some(f => E2E_RELEVANT.test(f));
  console.log(`story: ${files.length} changed files against ${base}; wide: ${wide.length ? wide.slice(0, 8).map(printablePath).join(', ') : 'none'}`);

  steps.unit();
  steps.security();
  steps.docs();
  if (files.some(f => WEB_CHANGED.test(f))) steps.web();

  if (wide.length || newScript || (persistence && filter === undefined)) steps.integration();
  else if (persistence) steps.integration(filter);
  else results.push({ name: 'integration tests (no persistence change)', ok: true, skipped: true });

  if (wide.length) steps.e2e();
  else if (e2eRelevant) {
    // The story's own spec files, the ones it changed, and the smoke set.
    const changedSpecs = files.map(f => f.replace(/^Platform\/Testing\/Ariva\.E2E\//, '')).filter(f => SPEC.test(f) && fs.existsSync(path.join(E2E, f)));
    steps.e2e([...new Set([...specs, ...changedSpecs, ...E2E_SMOKE])]);
  } else results.push({ name: 'e2e tests (no code change)', ok: true, skipped: true });
  checkpointNote();
}

// Engine files Stryker mutates (stryker-config.json "mutate" globs, relative to Ariva.Core) changed since a commit and still
// present, as paths relative to Ariva.Core; null when the commit is not in this history.
const CORE = 'Platform/Backplane/Ariva.Core/';
function changedEngineFiles(commit, to = 'HEAD') {
  const config = JSON.parse(fs.readFileSync(path.join(ROOT, 'Platform', 'Backplane', 'Ariva.UnitTests', 'stryker-config.json'), 'utf8'));
  const globs = (config['stryker-config']?.mutate ?? []).map(g => new RegExp('^' +
    g.replace(/[.+?^${}()|[\]\\]/g, '\\$&').replace(/\*\*\//g, '\u0000').replace(/\*/g, '[^/]*').replace(/\u0000/g, '(.*/)?') + '$'));
  try {
    return git(['diff', '--name-only', '--diff-filter=d', commit, to]).split('\n').map(f => f.trim())
      .filter(f => f.startsWith(CORE)).map(f => f.slice(CORE.length)).filter(f => globs.some(g => g.test(f)));
  } catch {
    return null;
  }
}

function engineChangedSince(commit) {
  const files = changedEngineFiles(commit);
  return files === null || files.length > 0; // a commit outside this history: treat every engine file as changed
}

function isAncestor(commit) {
  return spawnSync('git', ['merge-base', '--is-ancestor', commit, 'HEAD'], { cwd: ROOT }).status === 0;
}

function checkpointGate() {
  const dirty = git(['status', '--porcelain', '--untracked-files=no']).trim();
  if (dirty && !PLAN) { results.push({ name: 'checkpoint needs a committed tree (commit or stash first)', ok: false }); return; }
  const last = readCheckpoint();
  const head = git(['rev-parse', 'HEAD']).trim();
  const mutationBase = flag('--mutation-base');
  if (mutationBase !== undefined && (last || !SHA.test(mutationBase) || !isAncestor(mutationBase))) {
    results.push({ name: '--mutation-base needs a commit of this history and no recorded checkpoint', ok: false });
    return;
  }
  const since = last?.commit ?? mutationBase;
  steps.unit();
  steps.security();
  steps.docs();
  steps.web();
  steps.integration();
  steps.visual(); // before e2e: the visual baselines expect the fresh demo seed
  steps.e2e();
  // Mutation runs in its own worktree of this checkpoint's commit under its own lock, so the machine lock is released first:
  // story gates may use this checkout and the services while Stryker works (owner decision 2026-10-09).
  releaseLock(LOCK);
  if (!since) steps.mutation(undefined, head);
  else if (engineChangedSince(since)) steps.mutation(isAncestor(since) && changedEngineFiles(since, head) ? since : undefined, head);
  else results.push({ name: `mutation tests (no engine change since ${since.slice(0, 10)})`, ok: true, skipped: true });
  if (PLAN || results.some(r => !r.ok)) return;
  const record = { commit: head, at: new Date().toISOString().slice(0, 10), stories: last ? storiesSince(last.commit) : undefined, mutationBase,
    results: results.map(r => ({ name: r.name, ok: r.ok, skipped: r.skipped || undefined, seconds: r.seconds })) };
  fs.writeFileSync(CHECKPOINT_FILE, JSON.stringify(record, null, 2) + '\n');
  console.log(`\ncheckpoint: recorded ${head.slice(0, 10)} in backlog/checkpoint.json; commit it ("Checkpoint: full suite green")`);
}

const steps = {
  quick: () => { run('security scan (changed files)', 'node', ['scripts/security/scan.mjs', '--changed']); run('root search guard self-test', 'node', ['.claude/hooks/root-search.test.mjs']); docsCheck(); windowsPathsCheck(); },
  build: () => (solutionBuilt = run('dotnet build', 'dotnet', ['build', 'Ariva.slnx', '-c', 'Debug', ...(args.includes('--no-restore') ? ['--no-restore'] : [])])),
  unit: () => {
    steps.build();
    // The scenario engine's golden fingerprints must come from the current sim.js (ARV-027); the unit tests check the port against them.
    run('reference scenario golden up to date', 'node', ['scripts/simulation/reference-golden.mjs', '--check']);
    // ARV-164: the test executable runs the suite itself (Microsoft Testing Platform, in process), not dotnet test. There the
    // VSTest adapter reads the runner's messages as JSON lines from its standard output, which also carries the Pact verifier's
    // native report and display names with raw control characters; a line it cannot read is dropped without a failure, so a
    // case could go missing from a run (7,811 against 7,812 on one tree). The TRX names every case that ran, so a total that
    // differs between two runs can be traced to the case. PACT_DO_NOT_TRACK, which the runsettings set for dotnet test, is set
    // for every child process above.
    const unitDll = path.join('Platform', 'Backplane', 'Ariva.UnitTests', 'bin', 'Debug', 'net10.0', 'Ariva.UnitTests.dll');
    // A run that writes no TRX must not be judged by the last run's (a step that fails does not stop the ones after it).
    if (!PLAN) fs.rmSync(path.join(ROOT, '.verify', 'unit-tests.trx'), { force: true });
    run('unit tests', 'dotnet', [unitDll, '--results-directory', path.join(ROOT, '.verify'), '--report-xunit-trx', '--report-xunit-trx-filename', 'unit-tests.trx']);
    // ARV-164a: every case the executable lists has its results in the TRX, method by method (a theory listed once runs as many
    // rows), so a case left out of the run, passing or failing, fails the gate instead of shrinking a total nobody compares.
    run('test count guard self-test', 'node', ['scripts/test-count-guard.mjs', '--self-test']);
    run('unit test count guard (every listed case has its results)', 'node', ['scripts/test-count-guard.mjs', '--dll', unitDll,
      '--trx', path.join(ROOT, '.verify', 'unit-tests.trx')]);
  },
  // ARV-164b: as the unit suite, the test executable runs the integration suite itself (Microsoft Testing Platform, in process),
  // not dotnet test, whose VSTest adapter reads the runner's messages from standard output, which the hosts' console logs share,
  // and drops a line it cannot read; then the count guard, over the same filter. With a story filter (methodFilter, checked by
  // storyGate), only the methods it names.
  integration: (filter) => {
    const dll = path.join('Platform', 'Backplane', 'Ariva.IntegrationTests', 'bin', 'Debug', 'net10.0', 'Ariva.IntegrationTests.dll');
    const trx = path.join(ROOT, '.verify', 'integration-tests.trx');
    const options = filter === undefined ? [] : methodFilter(filter) ?? [];
    if (filter !== undefined && options.length === 0) {
      results.push({ name: 'integration tests (the filter is not FullyQualifiedName~<name> terms joined by |)', ok: false });
      return;
    }
    if (!solutionBuilt) run('dotnet build (integration tests)', 'dotnet', ['build', 'Platform/Backplane/Ariva.IntegrationTests/Ariva.IntegrationTests.csproj', '-c', 'Debug']);
    // A run that writes no TRX must not be judged by the last run's.
    if (!PLAN) fs.rmSync(trx, { force: true });
    run(filter === undefined ? 'integration tests' : 'integration tests (story filter)', 'dotnet',
      [dll, '--results-directory', path.join(ROOT, '.verify'), '--report-xunit-trx', '--report-xunit-trx-filename', 'integration-tests.trx', ...options]);
    run('integration test count guard (every listed case has its results)', 'node',
      ['scripts/test-count-guard.mjs', '--dll', dll, '--trx', trx, ...(options.length ? ['--', ...options] : [])]);
  },
  web: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    run('web svelte-check', 'npm', ['run', 'check'], WEB);
    run('web lint', 'npm', ['run', 'lint'], WEB);
    run('web build', 'npm', ['run', 'build'], WEB);
  },
  // `node scripts/verify.mjs e2e --specs <file,...>` runs only those specs (tests/api|functional|smoke/<name>.spec.ts).
  e2e: (specs = scope === 'e2e' ? scopeSpecs() : []) => {
    if (!fs.existsSync(E2E)) { results.push({ name: 'e2e', ok: false, note: 'Platform/Testing/Ariva.E2E missing' }); return; }
    if (specs === null) return;
    // The functional suite builds and previews Ariva.Web, so its dependencies are needed too.
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    if (specs.length) playwright(`e2e tests (${specs.length} spec files)`, specs);
    else playwright('e2e tests (API and functional)', []);
  },
  // ARV-075: renders only in the Playwright image pinned in scripts/visual-browser.mjs (needs Docker), never a local browser.
  visual: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    if (PLAN) { playwright('visual regression (pinned image)', ['--project=visual'], ['playwright-report', 'test-results-visual']); return; }
    const endpoint = execFileSync('node', ['scripts/visual-browser.mjs', 'start'], { cwd: E2E, encoding: 'utf8' }).trim();
    process.env.ARIVA_E2E_VISUAL_WS = endpoint;
    try {
      playwright('visual regression (pinned image)', ['--project=visual', '--output=test-results-visual', ...(args.includes('--update') ? ['--update-snapshots'] : [])],
        ['playwright-report', 'test-results-visual']);
    } finally {
      execFileSync('node', ['scripts/visual-browser.mjs', 'stop'], { cwd: E2E });
      delete process.env.ARIVA_E2E_VISUAL_WS;
    }
  },
  // ARV-064: the reference evening through the real pipeline, alone and in real time.
  demo: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    process.env.ARIVA_E2E_DEMO = '1';
    try {
      playwright('scripted demo (reference evening)', ['--project=demo']);
    } finally {
      delete process.env.ARIVA_E2E_DEMO;
    }
  },
  // ARV-104j: the validation rehearsal against the live pipeline, alone and in real time; its record is rehearsal-record.json in
  // the test's output folder (test-results), attached to the report.
  rehearsal: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    process.env.ARIVA_E2E_REHEARSAL = '1';
    try {
      playwright('validation rehearsal (live pipeline)', ['--project=rehearsal']);
    } finally {
      delete process.env.ARIVA_E2E_REHEARSAL;
    }
  },
  // ARV-124a: the pilot hall load run through the live pipeline (Stream included), alone and in real time, on fresh services;
  // its record is pilot-report.md and pilot-measurements.json in .verify/load/pilot, attached to the report.
  'pilot-load': () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    run('load harness build', 'dotnet', ['build', 'Platform/Testing/Ariva.LoadTests/Ariva.LoadTests.csproj', '-c', 'Debug']);
    process.env.ARIVA_E2E_PILOT_LOAD = '1';
    try {
      playwright('pilot hall load run (live pipeline)', ['--project=pilot-load']);
    } finally {
      delete process.env.ARIVA_E2E_PILOT_LOAD;
    }
  },
  // ARV-063: ZAP API and baseline scans; the run fails on an untriaged High risk finding (security/zap-triage.json).
  zap: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    process.env.ARIVA_E2E_ZAP = '1';
    try {
      playwright('dynamic security scan (OWASP ZAP)', ['--project=zap'], ['playwright-report', 'test-results', 'zap-reports']);
    } finally {
      delete process.env.ARIVA_E2E_ZAP;
    }
  },
  security: () => {
    run('security scanner self-test', 'node', ['scripts/security/scan.mjs', '--self-test']);
    run('security scan', 'node', ['scripts/security/scan.mjs']);
    // ARV-152 (CWE-532): no spec passes an answer's body as an expect message (Playwright keeps it as the step title even when
    // the assertion passes), and the artifact check every Playwright run ends with finds every kind of secret it looks for.
    run('E2E expect message check self-test', 'node', ['Platform/Testing/Ariva.E2E/scripts/expect-messages.mjs', '--self-test']);
    run('E2E expect messages hold no answer body', 'node', ['Platform/Testing/Ariva.E2E/scripts/expect-messages.mjs']);
    run('E2E artifact secret check self-test', 'node', ['Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs', '--self-test']);
    // ARV-164b: the client four security specs send their oversized requests with, refusing what is not a whole answer.
    run('E2E raw HTTP client self-test', 'node', ['Platform/Testing/Ariva.E2E/scripts/raw-http.mjs', '--self-test']);
    // ARV-126: the backup and restore tooling. The wrapper's validation and argument arrays; the database script's syntax
    // (its behaviour is proven by BackupRestoreTests in the integration suite). Without bash on the machine the second is skipped.
    run('ops tooling self-test (backup and restore wrapper)', 'node', ['scripts/ops/ariva-ops.mjs', '--self-test']);
    run('ops database script syntax', 'bash', ['-n', 'scripts/ops/ariva-db.sh'], ROOT, { optional: true });
    // ARV-084: the release gate's TLS check (protocols, suites, certificate, HSTS, the ingress's own responses, plain HTTP),
    // proven against throwaway local servers.
    run('TLS check self-test (release gate)', 'node', ['scripts/ops/tls-check.mjs', '--self-test']);
    run('base images pinned', 'node', ['scripts/base-images.mjs', '--check']);
    // ARV-184: the check every host image build runs on its publish output (runtime packs at the runtime-deps version, Pkcs and
    // SignalR assemblies at or above the fixed file version); base-images --check holds each Dockerfile to it.
    run('published runtime check self-test', 'node', ['scripts/published-runtime.mjs', '--self-test']);
    // ARV-127: the offline bundle's verifier and builder (every kind of tampering fails; the builder's bundles need Helm, and
    // are skipped without it outside GitHub Actions).
    run('offline bundle verifier self-test', 'node', ['scripts/offline/verify.mjs', '--self-test']);
    run('offline bundle builder self-test', 'node', ['scripts/offline/bundle.mjs', '--self-test']);
    const CHART_TESTS = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'tests');
    if (!fs.existsSync(path.join(CHART_TESTS, 'node_modules'))) run('chart tests npm ci', 'npm', ['ci'], CHART_TESTS);
    run('chart security (pod security context, read-only root, TLS, release guards)', 'node', ['chart-security.mjs'], CHART_TESTS);
    nugetAudit();
    // ARV-096: the three npm trees CI installs from a lock file, dev dependencies included, failing on high or critical as ci.yml does.
    if (fs.existsSync(path.join(WEB, 'package-lock.json'))) run('npm audit (web)', 'npm', ['audit', '--audit-level=high'], WEB, { optional: true });
    if (fs.existsSync(path.join(E2E, 'package-lock.json'))) run('npm audit (e2e)', 'npm', ['audit', '--audit-level=high'], E2E, { optional: true });
    if (fs.existsSync(path.join(CHART_TESTS, 'package-lock.json'))) run('npm audit (chart tests)', 'npm', ['audit', '--audit-level=high'], CHART_TESTS, { optional: true });
  },
  docs: () => { docsCheck(); windowsPathsCheck(); },
  // ARV-152: the artifact check on its own, over the report and the test output a run left (no seed: shapes and variables only).
  artifacts: () => {
    process.stdout.write(`\n=== no secret in the test artifacts\n`);
    results.push({ name: 'no secret in the test artifacts (report, output, traces)', ok: checkArtifacts(['playwright-report', 'test-results'].map(f => path.join(E2E, f))) });
  },
  // Scope and thresholds in Platform/Backplane/Ariva.UnitTests/stryker-config.json; exits non-zero below the break threshold.
  // Stryker replaces Ariva.Core.dll in the unit tests' output while it runs, so it runs in its own worktree of the commit
  // (default HEAD; uncommitted changes are not mutated), never in this checkout, under its own lock (one mutation run per
  // machine); the worktree is removed afterwards. The report and log stay in this checkout's .verify folder.
  // With a commit, only the engine files changed since it are mutated (--mutate per file, which replaces the configured
  // list): the checkpoint's scoped run. Stryker's own --since mode ended silently after its coverage capture with the MTP
  // runner (2026-10-08), so it is not used. One test session fewer than the machine's cores (at least two), so a story gate
  // still has a core.
  // ARV-069a: Stryker runs through scripts/mutation-run.mjs, which prints its output, keeps it in .verify/mutation.log and
  // fails the step when any method went into safe mode (its mutants removed as compile errors, so it is not measured) or no
  // final score was printed, even when Stryker exits zero.
  mutation: (since, commit) => {
    run('mutation log check self-test', 'node', ['scripts/mutation-run.mjs', '--self-test']);
    if (!PLAN && !takeLock(MUTATION_LOCK, 'mutation')) {
      results.push({ name: 'mutation tests (another mutation run holds its lock)', ok: false });
      return;
    }
    const rev = commit ?? git(['rev-parse', 'HEAD']).trim();
    const files = since ? changedEngineFiles(since, rev) : [];
    const tests = path.join(MUTATION_TREE, 'Platform', 'Backplane', 'Ariva.UnitTests');
    const sessions = String(Math.max(2, os.cpus().length - 1));
    try {
      if (!PLAN) addMutationTree(rev);
      // dotnet-stryker has its own manifest in Ariva.UnitTests/.config (kept apart from aspire.cli for SDK 10.0.4xx).
      run('dotnet tool restore (dotnet-stryker)', 'dotnet', ['tool', 'restore'], tests);
      // At a lower CPU priority outside Windows: Stryker uses the idle machine fully but yields to a story gate running beside
      // it (2026-10-10: an integration test timed out at 5 minutes with the load at about 20 while Stryker ran at normal priority).
      // --tests: Stryker must have found at least the tests the worktree's unit test build lists (ARV-164a).
      const mutationRun = [path.join(ROOT, 'scripts', 'mutation-run.mjs'), '--log', path.join(ROOT, '.verify', 'mutation.log'),
        '--tests', path.join(tests, 'bin', 'Debug', 'net10.0', 'Ariva.UnitTests.dll'), '--',
        'dotnet-stryker', '--concurrency', sessions, '--output', path.join(ROOT, '.verify', 'stryker'), ...files.flatMap(f => ['--mutate', `**/${f}`])];
      run(`mutation tests (Stryker.NET in a worktree of ${rev.slice(0, 10)}${since ? `, ${files.length} engine files changed since ${since.slice(0, 10)}` : ''}; fails on safe mode, no score or tests left out)`,
        IS_WIN ? 'node' : 'nice', IS_WIN ? mutationRun : ['-n', '10', 'node', ...mutationRun], tests);
    } finally {
      if (!PLAN) removeMutationTree();
      releaseLock(MUTATION_LOCK);
    }
  },
  backend: () => { steps.unit(); steps.security(); },
  all: () => { steps.unit(); steps.web(); steps.security(); steps.docs(); steps.e2e(); if (args.includes('--with-integration')) steps.integration(); },
  story: () => storyGate(),
  checkpoint: () => checkpointGate()
};

if (!steps[scope]) { console.error(`Unknown scope ${scope}. Use one of: ${Object.keys(steps).join(', ')}`); process.exit(2); }

// One heavy run per machine (2026-10-08: two checkpoints in one worktree shared the E2E ports and collided in Stryker; two
// integration suites crashed PostgreSQL and filled the disk). Scopes that start the hosts or Testcontainers take a lock in
// the machine's temporary folder; a second one refuses to start while the holder's process lives. Stryker has its own lock
// and its own worktree (2026-10-09), so a mutation run and one heavy run may share the machine.
const HEAVY = new Set(['integration', 'e2e', 'visual', 'demo', 'rehearsal', 'pilot-load', 'zap', 'all', 'story', 'checkpoint']);
const LOCK = path.join(os.tmpdir(), 'ariva-verify.lock');
const MUTATION_LOCK = path.join(os.tmpdir(), 'ariva-mutation.lock');
const MUTATION_TREE = path.join(os.tmpdir(), 'ariva-mutation-tree');
function alive(pid) {
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}
function releaseLock(file) {
  try { if (JSON.parse(fs.readFileSync(file, 'utf8')).pid === process.pid) fs.unlinkSync(file); } catch { /* gone */ }
}
function takeLock(file = LOCK, holderScope = scope) {
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      fs.writeFileSync(file, JSON.stringify({ pid: process.pid, scope: holderScope, root: ROOT, at: new Date().toISOString() }), { flag: 'wx' });
      process.on('exit', () => releaseLock(file));
      return true;
    } catch (e) {
      if (e.code !== 'EEXIST') throw e;
      let holder;
      try { holder = JSON.parse(fs.readFileSync(file, 'utf8')); } catch { holder = undefined; }
      if (holder && Number.isInteger(holder.pid) && alive(holder.pid)) {
        console.error(`Another run holds ${file}: pid ${holder.pid}, scope ${holder.scope}, ${holder.root}, since ${holder.at}. Wait for it to end.`);
        return false;
      }
      try { fs.unlinkSync(file); } catch { /* raced */ } // a stale lock: its process is gone
    }
  }
  return false;
}
// The mutation worktree: a detached checkout of one commit, replaced if an earlier run left one behind (only the holder of
// the mutation lock gets here), and removed with its build output afterwards (it needs about 2 GB while it lives).
function addMutationTree(rev) {
  removeMutationTree();
  execFileSync('git', ['worktree', 'add', '--detach', MUTATION_TREE, rev], { cwd: ROOT, stdio: 'inherit' });
}
function removeMutationTree() {
  spawnSync('git', ['worktree', 'remove', '--force', MUTATION_TREE], { cwd: ROOT, stdio: 'ignore' });
  fs.rmSync(MUTATION_TREE, { recursive: true, force: true });
  spawnSync('git', ['worktree', 'prune'], { cwd: ROOT, stdio: 'ignore' });
}
if (HEAVY.has(scope) && !PLAN && !takeLock()) process.exit(3);
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => process.exit(130));
steps[scope]();

if (!PLAN) {
  fs.mkdirSync(path.join(ROOT, '.verify'), { recursive: true });
  fs.writeFileSync(path.join(ROOT, '.verify', 'last.json'), JSON.stringify({ scope, at: new Date().toISOString(), results }, null, 2));
}
console.log('\n=== Summary');
for (const r of results) console.log(`${r.planned ? 'PLAN' : r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.skipped ? ' (skipped)' : ''}`);
const failed = results.filter(r => !r.ok);
process.exit(failed.length ? 1 : 0);
