#!/usr/bin/env node
// Proves a test run left out no case it can check (ARV-164a): every case the test executable lists has its results in the
// run's TRX, method by method, and the TRX is a whole run's. The comparison and its limits: scripts/test-counts.mjs.
//   node scripts/test-count-guard.mjs --dll <test dll> --trx <file>    list the dll's cases (dotnet <dll> --list-tests), compare
//   node scripts/test-count-guard.mjs --list <file> --trx <file>       compare a saved listing
//   node scripts/test-count-guard.mjs --self-test                      prove the comparison accepts and refuses what it should
// A command line only: nothing here runs on import, and there is no condition that could skip it (a main check that compares
// paths can be fooled by a link and exit 0 having done nothing).

import fs from 'node:fs';
import path from 'node:path';
import { guardProblems, listedCases, listedMethods, listing, resultNames, trxProblems } from './test-counts.mjs';

function report(text, trx) {
  const problems = guardProblems(text, trx);
  const cases = listedCases(text).length;
  const results = resultNames(trx).length;
  if (problems.length === 0) {
    console.log(`test-count-guard: ${cases} cases listed in ${listedMethods(text)} methods, ${results} results: none left out`);
    return 0;
  }
  console.error(`test-count-guard: the run does not account for every listed case (${cases} listed, ${results} results):`);
  for (const p of problems.slice(0, 40)) console.error(`  ${p}`);
  if (problems.length > 40) console.error(`  and ${problems.length - 40} more`);
  return 1;
}

function trxOf(results, total = results.length) {
  return `<TestRun><ResultSummary outcome="Completed"><Counters total="${total}" /></ResultSummary><Results>` +
    `${results.map(n => `<UnitTestResult testName="${n}" outcome="Passed" />`).join('')}</Results></TestRun>\n`;
}

function selfTest() {
  const listing = ['', '  A.Tests.Plain', '  A.Tests.Rows(x: 1)', '  A.Tests.Rows(x: 2)', '  A.Tests.Expanded',
    '', 'Test discovery summary: found 4 test(s)', '  duration: 1s 002ms'].join('\n');
  const all = ['A.Tests.Plain', 'A.Tests.Rows(x: 1)', 'A.Tests.Rows(x: 2)', 'A.Tests.Expanded(y: &quot;a(b&quot;)', 'A.Tests.Expanded(y: &quot;c&quot;)'];
  const whole = trxOf(all);
  const cases = [
    { name: 'every case ran, a theory listed once ran twice', listing, trx: whole, problems: 0 },
    { name: 'CRLF listing', listing: listing.replace(/\n/g, '\r\n'), trx: whole, problems: 0 },
    { name: 'a pre-enumerated row lost', listing, trx: trxOf(all.filter(n => n !== 'A.Tests.Rows(x: 2)')), problems: 1 },
    { name: 'a whole method lost', listing, trx: trxOf(all.filter(n => n !== 'A.Tests.Plain')), problems: 1 },
    { name: 'a theory listed once lost every row', listing, trx: trxOf(all.filter(n => !n.startsWith('A.Tests.Expanded'))), problems: 1 },
    { name: 'a result for a method not listed', listing, trx: trxOf([...all, 'A.Tests.New']), problems: 1 },
    { name: 'empty listing', listing: '', trx: whole, problems: 4 },
    { name: 'no results', listing, trx: trxOf([]), problems: 4 },
    { name: 'the duration line is no case', listing: '  duration: 2s', trx: trxOf(['A.Tests.Plain']), problems: 2 },
    { name: 'a case with a line separator in its arguments', listing: '  A.Tests.Text(s: "a\u2028b")', trx: trxOf(['A.Tests.Text(s: "a\\u2028b")']), problems: 0 },
    // Cut before its last row: every method still has a result, so only the TRX's own completeness (no end, a total of 5
    // against 4 results) can tell.
    { name: 'a TRX cut short inside a theory listed once', listing, trx: whole.slice(0, whole.indexOf('<UnitTestResult testName="A.Tests.Expanded(y: &quot;c')), problems: 2 },
    { name: 'a TRX whose total is not its results', listing, trx: trxOf(all, 6), problems: 1 },
    { name: 'a TRX with no total', listing, trx: whole.replace(/<Counters [^>]*\/>/, ''), problems: 1 }
  ];
  let failed = 0;
  for (const c of cases) {
    const got = guardProblems(c.listing, c.trx).length;
    const ok = got === c.problems;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${c.name} (problems ${got}, expected ${c.problems})`);
  }
  const checks = [
    ['character references decoded', resultNames('<UnitTestResult testName="M(s: &quot;&lt;&amp;&#x41;&#66;&quot;)" />')[0], 'M(s: "<&AB")'],
    ['an unknown entity or code point left as it is', resultNames('<UnitTestResult testName="M(&constructor;&#x110000;)" />')[0], 'M(&constructor;&#x110000;)'],
    ['a whole TRX has no problem', trxProblems(whole).length, 0]
  ];
  for (const [name, got, expected] of checks) {
    const ok = got === expected;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} (${got})`);
  }
  return failed ? 1 : 0;
}

function read(file) {
  if (!fs.existsSync(file)) {
    console.error(`test-count-guard: no file at ${file}`);
    return null;
  }
  return fs.readFileSync(file, 'utf8');
}

const argv = process.argv.slice(2);
const option = name => (argv.indexOf(name) >= 0 ? argv[argv.indexOf(name) + 1] : undefined);
if (argv[0] === '--self-test' && argv.length === 1) process.exit(selfTest());
else if (argv.length === 4 && option('--trx') && (option('--dll') || option('--list'))) {
  const text = option('--dll') ? listing(path.resolve(option('--dll'))) : read(path.resolve(option('--list')));
  const trx = read(path.resolve(option('--trx')));
  process.exit(text === null || trx === null ? 1 : report(text, trx));
} else {
  console.error('Usage: test-count-guard.mjs --dll <test dll> --trx <file> | --list <file> --trx <file> | --self-test');
  process.exit(2);
}
