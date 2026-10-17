#!/usr/bin/env node
// Ariva quality gates. Cross-platform (Windows, Linux, CI). No dependencies.
//   node scripts/verify.mjs <scope> [--no-restore]
// Scopes:
//   quick        security scan of changed files + root search guard self-test + docs text checks (used by the Stop hook)
//   build        dotnet build Ariva.slnx (security analyzers run as errors)
//   unit         build + Ariva.UnitTests (architecture, security and domain tests)
//   integration  Ariva.IntegrationTests (needs Docker for Testcontainers)
//   web          Ariva.Web: npm ci, svelte-check, lint, build
//   e2e          Platform/Testing/Ariva.E2E: API end-to-end and Playwright functional tests
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
//   zap          dynamic security scan (ARV-063): OWASP ZAP in its pinned image against the E2E stack (Docker and the E2E
//                database environment; about 10 minutes); reports in Platform/Testing/Ariva.E2E/zap-reports
//   security     scanner self-test + full scan + ops tooling self-test (ARV-126) + dependency audits
//   docs         text rules on Markdown (no em dashes, no double hyphens in prose)
//   backend      build + unit + security
//   mutation     Stryker.NET on the pure engines (ARV-069; one to three hours, mostly the queue engine; report in .verify/stryker);
//                fails when a method is in Stryker's safe mode or no score is printed (ARV-069a, scripts/mutation-run.mjs). Runs in
//                its own worktree of HEAD under its own lock, so builds and story gates may run in this checkout meanwhile
//   all          everything except integration (add --with-integration)
//   story        the per-story gate (test cadence, docs/harness/test-cadence.md): backend and docs always; web when the
//                web app changed; integration and E2E scoped to the story, or in full when the change is wide.
//                --specs <file,...> the story's E2E specs (tests/api/x.spec.ts); --integration "<dotnet test filter>";
//                --base <ref> compare with this commit (default HEAD: the uncommitted story)
//   checkpoint   the full suite on a committed tree every few stories and at each phase end: backend, docs, web, integration,
//                visual, e2e and mutation on the engine code changed since the last checkpoint; records backlog/checkpoint.json
//                --mutation-base <commit>: only while no checkpoint is recorded, the commit of an earlier checkpoint run whose
//                mutation step passed (the run failed elsewhere); mutation then runs only on engine code changed since it
// --plan prints what story or checkpoint would run without running it.
// Writes .verify/last.json with per-step results.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WEB = path.join(ROOT, 'Platform', 'Frontplane', 'Ariva.Web');
const E2E = path.join(ROOT, 'Platform', 'Testing', 'Ariva.E2E');
const IS_WIN = process.platform === 'win32';
const args = process.argv.slice(2);
const VALUE_FLAGS = new Set(['--specs', '--integration', '--base', '--mutation-base']);
const scope = args.find((a, i) => !a.startsWith('--') && !VALUE_FLAGS.has(args[i - 1])) || 'backend';
const PLAN = args.includes('--plan');
const results = [];

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

// ARV-104j review (CWE-532): the live-pipeline scopes register DMO's sensors, whose answers carry device credentials; none
// may reach the Playwright report or the test output (an expect's message is a step title even when it passes). Runs after
// the run, since the HTML reporter writes after global teardown; counts the files, never prints a value.
function artifactCredentialCheck() {
  const credential = /(?<![A-Za-z0-9_-])ard[kp]_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])/;
  // The HTML report embeds its data as a base64 zip, and traces are zips: read their entries (central directory, stored
  // or deflated) so a credential inside them is found too.
  const zipEntries = buf => {
    const texts = [];
    let end = buf.length - 22;
    while (end >= 0 && buf.readUInt32LE(end) !== 0x06054b50) end--;
    if (end < 0) return texts;
    let at = buf.readUInt32LE(end + 16);
    for (let i = buf.readUInt16LE(end + 10); i > 0 && buf.readUInt32LE(at) === 0x02014b50; i--) {
      const method = buf.readUInt16LE(at + 10), size = buf.readUInt32LE(at + 20), local = buf.readUInt32LE(at + 42);
      const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
      const data = buf.subarray(start, start + size);
      try { texts.push((method === 8 ? zlib.inflateRawSync(data) : data).toString('latin1')); } catch { texts.push(''); }
      at += 46 + buf.readUInt16LE(at + 28) + buf.readUInt16LE(at + 30) + buf.readUInt16LE(at + 32);
    }
    return texts;
  };
  const holds = buf => {
    const text = buf.toString('latin1');
    if (credential.test(text)) return true;
    const embedded = [...text.matchAll(/data:application\/zip;base64,([A-Za-z0-9+/=]+)/g)].map(m => Buffer.from(m[1], 'base64'));
    if (buf.readUInt32LE(0) === 0x04034b50) embedded.push(buf);
    // A zip that cannot be read is a finding too: what it holds cannot be shown to be clean.
    return embedded.some(zip => { try { return zipEntries(zip).some(entry => credential.test(entry)); } catch { return true; } });
  };
  const holding = [];
  const walk = d => {
    if (!fs.existsSync(d)) return;
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (e.isFile() && fs.statSync(p).size >= 4 && holds(fs.readFileSync(p))) holding.push(path.relative(ROOT, p));
    }
  };
  for (const folder of ['playwright-report', 'test-results']) walk(path.join(E2E, folder));
  holding.forEach(f => console.log(`CREDENTIAL in ${f}`));
  results.push({ name: 'no device credential in the test artifacts', ok: holding.length === 0, findings: holding.length });
  return holding.length === 0;
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
  bad.forEach(b => console.log(`DOCS ${b}`));
  results.push({ name: 'docs text rules', ok: bad.length === 0, findings: bad.length });
  return bad.length === 0;
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
  /^Platform\/Backplane\/Ariva\.Infra\/(NHibernate|Messaging|Streaming|Security|DataProtection|Caching)\//,
  /^Platform\/Simulation\//,
  /^Platform\/Frontplane\/Ariva\.Web\/(src\/lib\/core\/|src\/hooks|package(-lock)?\.json$|svelte\.config|vite\.config)/,
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
const TEST_FILTER = /^[A-Za-z0-9_.~=!|&]{1,400}$/;
const GIT_REF = /^[A-Za-z0-9_./^~-]{1,100}$/;
const SHA = /^[0-9a-f]{7,40}$/;

function changedFiles(base) {
  const tracked = git(['diff', '--name-only', base]).split('\n');
  const untracked = git(['ls-files', '--others', '--exclude-standard']).split('\n');
  return [...new Set([...tracked, ...untracked].map(f => f.trim()).filter(Boolean))];
}

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
    ...(filter === undefined || TEST_FILTER.test(filter) ? [] : ['--integration'])
  ];
  if (refused.length) {
    results.push({ name: `story arguments refused (${refused.join('; ')}); specs are tests/api|functional|smoke/<name>.spec.ts files that exist`, ok: false });
    return;
  }
  const files = changedFiles(base);
  const wide = files.filter(f => WIDE.some(w => w.test(f)));
  const persistence = files.some(f => PERSISTENCE.test(f));
  const newScript = files.some(f => NEW_SCRIPT.test(f));
  const e2eRelevant = files.some(f => E2E_RELEVANT.test(f));
  console.log(`story: ${files.length} changed files against ${base}; wide: ${wide.length ? wide.slice(0, 8).join(', ') : 'none'}`);

  steps.unit();
  steps.security();
  steps.docs();
  if (files.some(f => WEB_CHANGED.test(f))) steps.web();

  if (wide.length || newScript || (persistence && filter === undefined)) steps.integration();
  else if (persistence) run('integration tests (story filter)', 'dotnet', ['test', 'Platform/Backplane/Ariva.IntegrationTests/Ariva.IntegrationTests.csproj', '--filter', filter]);
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
  quick: () => { run('security scan (changed files)', 'node', ['scripts/security/scan.mjs', '--changed']); run('root search guard self-test', 'node', ['.claude/hooks/root-search.test.mjs']); docsCheck(); },
  build: () => run('dotnet build', 'dotnet', ['build', 'Ariva.slnx', '-c', 'Debug', ...(args.includes('--no-restore') ? ['--no-restore'] : [])]),
  unit: () => {
    steps.build();
    // The scenario engine's golden fingerprints must come from the current sim.js (ARV-027); the unit tests check the port against them.
    run('reference scenario golden up to date', 'node', ['scripts/simulation/reference-golden.mjs', '--check']);
    // The TRX names every case that ran, so a total that differs between two runs can be traced to the case.
    run('unit tests', 'dotnet', ['test', 'Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj', '--no-build',
      '--logger', `trx;LogFileName=${path.join(ROOT, '.verify', 'unit-tests.trx')}`]);
  },
  integration: () => run('integration tests', 'dotnet', ['test', 'Platform/Backplane/Ariva.IntegrationTests/Ariva.IntegrationTests.csproj']),
  web: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    run('web svelte-check', 'npm', ['run', 'check'], WEB);
    run('web lint', 'npm', ['run', 'lint'], WEB);
    run('web build', 'npm', ['run', 'build'], WEB);
  },
  e2e: (specs = []) => {
    if (!fs.existsSync(E2E)) { results.push({ name: 'e2e', ok: false, note: 'Platform/Testing/Ariva.E2E missing' }); return; }
    // The functional suite builds and previews Ariva.Web, so its dependencies are needed too.
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    if (specs.length) run(`e2e tests (${specs.length} spec files)`, 'npx', ['playwright', 'test', ...specs], E2E);
    else run('e2e tests (API and functional)', 'npx', ['playwright', 'test'], E2E);
  },
  // ARV-075: renders only in the Playwright image pinned in scripts/visual-browser.mjs (needs Docker), never a local browser.
  visual: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    if (PLAN) { run('visual regression (pinned image)', 'npx', ['playwright', 'test', '--project=visual']); return; }
    const endpoint = execFileSync('node', ['scripts/visual-browser.mjs', 'start'], { cwd: E2E, encoding: 'utf8' }).trim();
    process.env.ARIVA_E2E_VISUAL_WS = endpoint;
    try {
      run('visual regression (pinned image)', 'npx', ['playwright', 'test', '--project=visual', '--output=test-results-visual', ...(args.includes('--update') ? ['--update-snapshots'] : [])], E2E);
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
      run('scripted demo (reference evening)', 'npx', ['playwright', 'test', '--project=demo'], E2E);
    } finally {
      delete process.env.ARIVA_E2E_DEMO;
    }
    artifactCredentialCheck();
  },
  // ARV-104j: the validation rehearsal against the live pipeline, alone and in real time; its record is rehearsal-record.json in
  // the test's output folder (test-results), attached to the report.
  rehearsal: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    process.env.ARIVA_E2E_REHEARSAL = '1';
    try {
      run('validation rehearsal (live pipeline)', 'npx', ['playwright', 'test', '--project=rehearsal'], E2E);
    } finally {
      delete process.env.ARIVA_E2E_REHEARSAL;
    }
    artifactCredentialCheck();
  },
  // ARV-063: ZAP API and baseline scans; the run fails on an untriaged High risk finding (security/zap-triage.json).
  zap: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    process.env.ARIVA_E2E_ZAP = '1';
    try {
      run('dynamic security scan (OWASP ZAP)', 'npx', ['playwright', 'test', '--project=zap'], E2E);
    } finally {
      delete process.env.ARIVA_E2E_ZAP;
    }
  },
  security: () => {
    run('security scanner self-test', 'node', ['scripts/security/scan.mjs', '--self-test']);
    run('security scan', 'node', ['scripts/security/scan.mjs']);
    // ARV-126: the backup and restore tooling. The wrapper's validation and argument arrays; the database script's syntax
    // (its behaviour is proven by BackupRestoreTests in the integration suite). Without bash on the machine the second is skipped.
    run('ops tooling self-test (backup and restore wrapper)', 'node', ['scripts/ops/ariva-ops.mjs', '--self-test']);
    run('ops database script syntax', 'bash', ['-n', 'scripts/ops/ariva-db.sh'], ROOT, { optional: true });
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
    if (fs.existsSync(path.join(WEB, 'package-lock.json'))) run('npm audit (web)', 'npm', ['audit', '--audit-level=high'], WEB, { optional: true });
    if (fs.existsSync(path.join(E2E, 'package-lock.json'))) run('npm audit (e2e)', 'npm', ['audit', '--audit-level=high'], E2E, { optional: true });
  },
  docs: () => docsCheck(),
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
      const mutationRun = [path.join(ROOT, 'scripts', 'mutation-run.mjs'), '--log', path.join(ROOT, '.verify', 'mutation.log'), '--',
        'dotnet-stryker', '--concurrency', sessions, '--output', path.join(ROOT, '.verify', 'stryker'), ...files.flatMap(f => ['--mutate', `**/${f}`])];
      run(`mutation tests (Stryker.NET in a worktree of ${rev.slice(0, 10)}${since ? `, ${files.length} engine files changed since ${since.slice(0, 10)}` : ''}; fails on safe mode or no score)`,
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
const HEAVY = new Set(['integration', 'e2e', 'visual', 'demo', 'rehearsal', 'zap', 'all', 'story', 'checkpoint']);
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
