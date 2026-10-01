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
//   security     scanner self-test + full scan + dependency audits
//   docs         text rules on Markdown (no em dashes, no double hyphens in prose)
//   backend      build + unit + security
//   all          everything except integration (add --with-integration)
// Writes .verify/last.json with per-step results.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
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
        fs.readFileSync(p, 'utf8').split(/\r?\n/).forEach((l, i) => {
          if (l.includes('—') || l.includes('–')) bad.push(`${path.relative(ROOT, p)}:${i + 1} dash character`);
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
  unit: () => { steps.build(); run('unit tests', 'dotnet', ['test', 'Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj', '--no-build']); },
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
  security: () => {
    run('security scanner self-test', 'node', ['scripts/security/scan.mjs', '--self-test']);
    run('security scan', 'node', ['scripts/security/scan.mjs']);
    const CHART_TESTS = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'tests');
    if (!fs.existsSync(path.join(CHART_TESTS, 'node_modules'))) run('chart tests npm ci', 'npm', ['ci'], CHART_TESTS);
    run('chart security (pod security context, read-only root, TLS, release guards)', 'node', ['chart-security.mjs'], CHART_TESTS);
    run('nuget vulnerability audit', 'dotnet', ['list', 'Ariva.slnx', 'package', '--vulnerable', '--include-transitive'], ROOT, { optional: true });
    if (fs.existsSync(path.join(WEB, 'package-lock.json'))) run('npm audit (web)', 'npm', ['audit', '--audit-level=high'], WEB, { optional: true });
    if (fs.existsSync(path.join(E2E, 'package-lock.json'))) run('npm audit (e2e)', 'npm', ['audit', '--audit-level=high'], E2E, { optional: true });
  },
  docs: () => docsCheck(),
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
