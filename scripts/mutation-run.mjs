#!/usr/bin/env node
// Runs Stryker.NET for verify.mjs (ARV-069a): its output goes to this console and to a log file, and the run fails when
// Stryker went into safe mode or printed no mutation score, even when Stryker itself exits zero.
//   node scripts/mutation-run.mjs --log <file> -- <dotnet arguments>   run dotnet with the arguments, then check the log
//   node scripts/mutation-run.mjs --check <file>                        check an existing Stryker log only
//   node scripts/mutation-run.mjs --self-test                           prove the check accepts and refuses what it should
// Safe mode: when a mutant does not compile and Stryker cannot tell which one it was (CS0165 on an out or pattern variable
// declared inside a condition, CS0411, CS1620), it removes every mutant of the method and counts them as compile errors,
// so the method is not measured at all and the score does not show it. No dependencies; the command is always dotnet.

import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';

const SAFE_MODE = /Safe Mode! Stryker will remove all mutations in (\S+)/g;
const SCORE = /The final mutation score is (\d+(?:\.\d+)?) %/;

/** The reasons a Stryker log does not prove a complete run: methods in safe mode, or no final score. */
export function strykerProblems(text) {
  const lines = String(text ?? '').replace(/\r/g, '\n');
  const problems = [...new Set([...lines.matchAll(SAFE_MODE)].map(m => m[1]))]
    .map(method => `safe mode: every mutant of ${method} was removed as a compile error, so it is not measured`);
  if (!SCORE.test(lines)) problems.push('no final mutation score: Stryker did not finish or tested no mutant');
  return problems;
}

function report(problems) {
  if (problems.length === 0) {
    console.log('\nmutation-run: no method in safe mode, final score present');
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
    { name: 'empty log', text: '', problems: 1 }
  ];
  let failed = 0;
  for (const c of cases) {
    const got = strykerProblems(c.text).length;
    const ok = got === c.problems;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${c.name} (problems ${got}, expected ${c.problems})`);
  }
  return failed ? 1 : 0;
}

function check(file) {
  if (!fs.existsSync(file)) {
    console.error(`mutation-run: no log at ${file}`);
    return 1;
  }
  return report(strykerProblems(fs.readFileSync(file, 'utf8')));
}

// dotnet only, with an argument array and no shell: the arguments come from verify.mjs.
function runDotnet(file, dotnetArgs) {
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
      const problems = check(file);
      process.exit(code !== 0 ? code ?? 1 : problems);
    });
  });
}

const argv = process.argv.slice(2);
if (argv[0] === '--self-test') process.exit(selfTest());
else if (argv[0] === '--check' && argv.length === 2) process.exit(check(path.resolve(argv[1])));
else if (argv[0] === '--log' && argv.length >= 3 && argv[2] === '--') runDotnet(path.resolve(argv[1]), argv.slice(3));
else {
  console.error('Usage: mutation-run.mjs --log <file> -- <dotnet arguments> | --check <file> | --self-test');
  process.exit(2);
}
