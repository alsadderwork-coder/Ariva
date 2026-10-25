#!/usr/bin/env node
// Runs Stryker.NET for verify.mjs (ARV-069a): its output goes to this console and to a log file, and the run fails when
// Stryker went into safe mode or printed no mutation score, even when Stryker itself exits zero, and (ARV-164a) when it found
// fewer tests than the unit tests list.
//   node scripts/mutation-run.mjs --log <file> [--tests <dll>] -- <dotnet arguments>   run dotnet, then check the log
//   node scripts/mutation-run.mjs --check <file> [--tests <dll>]                        check an existing Stryker log only
//   node scripts/mutation-run.mjs --self-test                                           prove the check accepts and refuses what it should
// Safe mode: when a mutant does not compile and Stryker cannot tell which one it was (CS0165 on an out or pattern variable
// declared inside a condition, CS0411, CS1620), it removes every mutant of the method and counts them as compile errors,
// so the method is not measured at all and the score does not show it. With --tests, the unit test dll Stryker ran is listed
// (dotnet <dll> --list-tests, scripts/test-counts.mjs) and Stryker's "Number of tests found" (summed over its test projects)
// must be at least that many:
// Stryker discovers the tests through its MTP runner, whose results come over the platform's protocol rather than standard
// output, so it cannot drop a line as dotnet test can, but a run that found fewer tests than exist measured mutants against
// part of the suite. No dependencies; the command is always dotnet.

import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { listedCases, listing } from './test-counts.mjs';

const SAFE_MODE = /Safe Mode! Stryker will remove all mutations in (\S+)/g;
const SCORE = /The final mutation score is (\d+(?:\.\d+)?) %/;
const FOUND = /Number of tests found: (\d+)/g;

/**
 * The reasons a Stryker log does not prove a complete run: methods in safe mode, no final score, or (when the unit tests'
 * listed count is given) no number of tests found, or fewer than are listed.
 */
export function strykerProblems(text, listed) {
  const lines = String(text ?? '').replace(/\r/g, '\n');
  const problems = [...new Set([...lines.matchAll(SAFE_MODE)].map(m => m[1]))]
    .map(method => `safe mode: every mutant of ${method} was removed as a compile error, so it is not measured`);
  if (!SCORE.test(lines)) problems.push('no final mutation score: Stryker did not finish or tested no mutant');
  if (listed !== undefined) {
    // matchAll copies the regex, so FOUND's own lastIndex is never moved (a global regex's test() would carry it over).
    const reported = [...lines.matchAll(FOUND)];
    const found = reported.reduce((sum, m) => sum + Number(m[1]), 0);
    // An empty listing counts as none: a listing whose format changed would otherwise let any number pass.
    if (!listed) problems.push('the unit tests could not be listed, so the tests Stryker found cannot be checked');
    else if (reported.length === 0) problems.push('no number of tests found: Stryker did not report its test discovery');
    else if (found < listed) problems.push(`tests left out: Stryker found ${found} tests, the unit tests list ${listed}`);
  }
  return problems;
}

function report(problems, listed) {
  if (problems.length === 0) {
    console.log(`\nmutation-run: no method in safe mode, final score present${listed === undefined ? '' : `, every one of the ${listed} listed tests found`}`);
    return 0;
  }
  console.error('\nmutation-run: the mutation run does not count:');
  for (const p of problems) console.error(`  ${p}`);
  if (problems.some(p => p.startsWith('safe mode')))
    console.error('  Fix: declare out and pattern variables in their own statements, never inside a condition (testing-strategy skill).');
  return 1;
}

function selfTest() {
  const score = '[14:37:51 INF] The final mutation score is 93.17 %';
  const cases = [
    { name: 'complete run', text: `[12:51:44 INF] 12735 mutants created\n${score}\n`, problems: 0 },
    { name: 'progress output with carriage returns', text: `\r[1/2] tested\r[2/2] tested\r\n${score}`, problems: 0 },
    { name: 'safe mode', text: `[12:51:14 INF] Safe Mode! Stryker will remove all mutations in GetHashCode and mark them as 'compile error'.\n${score}`, problems: 1 },
    { name: 'two methods in safe mode, one twice', text: 'Safe Mode! Stryker will remove all mutations in Exit and\rSafe Mode! Stryker will remove all mutations in Exit and\nSafe Mode! Stryker will remove all mutations in Offer and\n' + score, problems: 2 },
    { name: 'no score', text: '[12:51:44 INF] 12735 mutants created\n', problems: 1 },
    { name: 'no number', text: 'The final mutation score is NaN %', problems: 1 },
    { name: 'empty log', text: '', problems: 1 },
    { name: 'every listed test found', text: `[03:12:17 INF] Number of tests found: 7212 for project x.csproj. Initial test run started.\n${score}`, listed: 7212, problems: 0 },
    { name: 'more found than listed', text: `Number of tests found: 7900 for project x.csproj.\n${score}`, listed: 7212, problems: 0 },
    { name: 'fewer found than listed', text: `Number of tests found: 7211 for project x.csproj.\n${score}`, listed: 7212, problems: 1 },
    { name: 'no number of tests found', text: score, listed: 7212, problems: 1 },
    { name: 'the tests could not be listed', text: `Number of tests found: 7212 for project x.csproj.\n${score}`, listed: null, problems: 1 },
    { name: 'the listing named no test', text: `Number of tests found: 7212 for project x.csproj.\n${score}`, listed: 0, problems: 1 },
    { name: 'two test projects summed', text: `Number of tests found: 7000 for project x.csproj.\nNumber of tests found: 300 for project y.csproj.\n${score}`, listed: 7212, problems: 0 },
    { name: 'no listing asked for', text: score, problems: 0 }
  ];
  let failed = 0;
  for (const c of cases) {
    const got = strykerProblems(c.text, c.listed).length;
    const ok = got === c.problems;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${c.name} (problems ${got}, expected ${c.problems})`);
  }
  return failed ? 1 : 0;
}

/** The unit tests' listed count, null when they cannot be listed, undefined when no dll is given. */
function listedCount(dll) {
  if (!dll) return undefined;
  const text = listing(dll);
  return text === null ? null : listedCases(text).length;
}

function check(file, dll) {
  if (!fs.existsSync(file)) {
    console.error(`mutation-run: no log at ${file}`);
    return 1;
  }
  const listed = listedCount(dll);
  return report(strykerProblems(fs.readFileSync(file, 'utf8'), listed), listed);
}

// dotnet only, with an argument array and no shell: the arguments come from verify.mjs.
function runDotnet(file, dll, dotnetArgs) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const log = fs.createWriteStream(file);
  const child = spawn('dotnet', dotnetArgs, { stdio: ['inherit', 'pipe', 'pipe'] });
  child.stdout.on('data', d => { process.stdout.write(d); log.write(d); });
  child.stderr.on('data', d => { process.stderr.write(d); log.write(d); });
  // A spawn failure emits both 'error' and 'close': only the first one settles the exit code.
  let settled = false;
  child.on('error', e => {
    if (settled) return;
    settled = true;
    console.error(`mutation-run: ${e.message}`);
    log.end(() => process.exit(127));
  });
  child.on('close', code => {
    if (settled) return;
    settled = true;
    log.end(() => {
      const problems = check(file, dll);
      process.exit(code !== 0 ? code ?? 1 : problems);
    });
  });
}

const argv = process.argv.slice(2);
const tests = argv[2] === '--tests' && argv[3] ? path.resolve(argv[3]) : undefined;
const rest = tests ? argv.slice(4) : argv.slice(2);
if (argv[0] === '--self-test' && argv.length === 1) process.exit(selfTest());
else if (argv[0] === '--check' && argv[1] && rest.length === 0) process.exit(check(path.resolve(argv[1]), tests));
else if (argv[0] === '--log' && argv[1] && rest[0] === '--' && rest.length >= 2) runDotnet(path.resolve(argv[1]), tests, rest.slice(1));
else {
  console.error('Usage: mutation-run.mjs --log <file> [--tests <dll>] -- <dotnet arguments> | --check <file> [--tests <dll>] | --self-test');
  process.exit(2);
}
