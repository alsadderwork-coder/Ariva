#!/usr/bin/env node
// Ariva quality gates. Cross-platform (Windows, Linux, CI). No dependencies.
//   node scripts/verify.mjs <scope> [--no-restore]
// Scopes:
//   quick        security scan of changed files + docs text checks (used by the Stop hook)
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
//   zap          dynamic security scan (ARV-063): OWASP ZAP in its pinned image against the E2E stack (Docker and the E2E
//                database environment; about 10 minutes); reports in Platform/Testing/Ariva.E2E/zap-reports
//   security     scanner self-test + full scan + dependency audits
//   docs         text rules on Markdown (no em dashes, no double hyphens in prose)
//   backend      build + unit + security
//   mutation     Stryker.NET on the pure engines (ARV-069; about an hour on two cores, report in .verify/stryker)
//   all          everything except integration (add --with-integration)
// Writes .verify/last.json with per-step results.

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WEB = path.join(ROOT, 'Platform', 'Frontplane', 'Ariva.Web');
const E2E = path.join(ROOT, 'Platform', 'Testing', 'Ariva.E2E');
const IS_WIN = process.platform === 'win32';
const args = process.argv.slice(2);
const scope = args.find(a => !a.startsWith('--')) || 'backend';
const results = [];

function run(name, cmd, cmdArgs, cwd = ROOT, { optional = false } = {}) {
  const started = Date.now();
  process.stdout.write(`\n=== ${name}: ${cmd} ${cmdArgs.join(' ')}\n`);
  // shell is only used on Windows to resolve npm/npx .cmd shims; arguments are fixed strings from this file.
  const r = spawnSync(cmd, cmdArgs, { cwd, stdio: 'inherit', shell: IS_WIN && /^(npm|npx)$/.test(cmd) });
  const ok = r.status === 0;
  const skipped = r.error && r.error.code === 'ENOENT';
  results.push({ name, ok: ok || (optional && skipped), skipped: !!skipped, seconds: Math.round((Date.now() - started) / 1000), exit: r.status });
  if (skipped) process.stdout.write(`(${cmd} not found${optional ? ', skipped' : ''})\n`);
  return ok;
}

function docsCheck() {
  const bad = [];
  const walk = d => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (['node_modules', '.git', 'bin', 'obj', '.svelte-kit', 'build', 'prototype'].includes(e.name)) continue;
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

const steps = {
  quick: () => { run('security scan (changed files)', 'node', ['scripts/security/scan.mjs', '--changed']); docsCheck(); },
  build: () => run('dotnet build', 'dotnet', ['build', 'Ariva.slnx', '-c', 'Debug', ...(args.includes('--no-restore') ? ['--no-restore'] : [])]),
  unit: () => {
    steps.build();
    // The scenario engine's golden fingerprints must come from the current sim.js (ARV-027); the unit tests check the port against them.
    run('reference scenario golden up to date', 'node', ['scripts/simulation/reference-golden.mjs', '--check']);
    run('unit tests', 'dotnet', ['test', 'Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj', '--no-build']);
  },
  integration: () => run('integration tests', 'dotnet', ['test', 'Platform/Backplane/Ariva.IntegrationTests/Ariva.IntegrationTests.csproj']),
  web: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    run('web svelte-check', 'npm', ['run', 'check'], WEB);
    run('web lint', 'npm', ['run', 'lint'], WEB);
    run('web build', 'npm', ['run', 'build'], WEB);
  },
  e2e: () => {
    if (!fs.existsSync(E2E)) { results.push({ name: 'e2e', ok: false, note: 'Platform/Testing/Ariva.E2E missing' }); return; }
    // The functional suite builds and previews Ariva.Web, so its dependencies are needed too.
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
    run('e2e tests (API and functional)', 'npx', ['playwright', 'test'], E2E);
  },
  // ARV-075: renders only in the Playwright image pinned in scripts/visual-browser.mjs (needs Docker), never a local browser.
  visual: () => {
    if (!fs.existsSync(path.join(WEB, 'node_modules'))) run('web npm ci', 'npm', ['ci'], WEB);
    if (!fs.existsSync(path.join(E2E, 'node_modules'))) run('e2e npm ci', 'npm', ['ci'], E2E);
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
    run('base images pinned', 'node', ['scripts/base-images.mjs', '--check']);
    const CHART_TESTS = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'tests');
    if (!fs.existsSync(path.join(CHART_TESTS, 'node_modules'))) run('chart tests npm ci', 'npm', ['ci'], CHART_TESTS);
    run('chart security (pod security context, read-only root, TLS, release guards)', 'node', ['chart-security.mjs'], CHART_TESTS);
    run('nuget vulnerability audit', 'dotnet', ['list', 'Ariva.slnx', 'package', '--vulnerable', '--include-transitive'], ROOT, { optional: true });
    if (fs.existsSync(path.join(WEB, 'package-lock.json'))) run('npm audit (web)', 'npm', ['audit', '--audit-level=high'], WEB, { optional: true });
    if (fs.existsSync(path.join(E2E, 'package-lock.json'))) run('npm audit (e2e)', 'npm', ['audit', '--audit-level=high'], E2E, { optional: true });
  },
  docs: () => docsCheck(),
  // Scope and thresholds in Platform/Backplane/Ariva.UnitTests/stryker-config.json; exits non-zero below the break threshold.
  // Stryker replaces Ariva.Core.dll in the unit tests' output while it runs: do not build or test in this checkout meanwhile.
  mutation: () => {
    process.env.PACT_DO_NOT_TRACK = 'true'; // the Pact FFI's usage reporting stays off (the pacts skip under Stryker anyway)
    // dotnet-stryker has its own manifest in Ariva.UnitTests/.config (kept apart from aspire.cli for SDK 10.0.4xx).
    run('dotnet tool restore (dotnet-stryker)', 'dotnet', ['tool', 'restore'], path.join(ROOT, 'Platform', 'Backplane', 'Ariva.UnitTests'));
    run('mutation tests (Stryker.NET)', 'dotnet', ['dotnet-stryker', '--output', path.join(ROOT, '.verify', 'stryker')],
      path.join(ROOT, 'Platform', 'Backplane', 'Ariva.UnitTests'));
  },
  backend: () => { steps.unit(); steps.security(); },
  all: () => { steps.unit(); steps.web(); steps.security(); steps.docs(); steps.e2e(); if (args.includes('--with-integration')) steps.integration(); }
};

if (!steps[scope]) { console.error(`Unknown scope ${scope}. Use one of: ${Object.keys(steps).join(', ')}`); process.exit(2); }
steps[scope]();

fs.mkdirSync(path.join(ROOT, '.verify'), { recursive: true });
fs.writeFileSync(path.join(ROOT, '.verify', 'last.json'), JSON.stringify({ scope, at: new Date().toISOString(), results }, null, 2));
console.log('\n=== Summary');
for (const r of results) console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.skipped ? ' (skipped)' : ''}`);
const failed = results.filter(r => !r.ok);
process.exit(failed.length ? 1 : 0);
