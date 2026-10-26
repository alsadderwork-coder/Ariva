#!/usr/bin/env node
// Proves a test run left out no case it can check (ARV-164a): every case the test executable lists has its results in the
// run's TRX, method by method, each theory listed once has as many results as its project's row file says it has rows
// (ARV-251), and the TRX is a whole run's. The comparison and its limits: scripts/test-counts.mjs.
//   node scripts/test-count-guard.mjs --dll <test dll> --trx <file> [-- <filter options>]
//                                                                      list the dll's cases (dotnet <dll> --list-tests, with the
//                                                                      run's own --filter-method, -class or -namespace options) and
//                                                                      read the row file beside it (theory-rows.json), compare
//   node scripts/test-count-guard.mjs --list <file> --rows <file> --trx <file>
//                                                                      compare a saved listing and row file
//   node scripts/test-count-guard.mjs --self-test                      prove the comparison accepts and refuses what it should
// A command line only: nothing here runs on import, and there is no condition that could skip it (a main check that compares
// paths can be fooled by a link and exit 0 having done nothing).

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { filterProblem, guardProblems, listedCases, listedMethods, listing, methodFilter, parseRows, resultNames, trxProblems } from './test-counts.mjs';

/** The row file beside a test executable (copied there by its project). */
const ROW_FILE = 'theory-rows.json';

function report(text, trx, rows, complete) {
  const problems = guardProblems(text, trx, rows, { complete });
  const cases = listedCases(text).length;
  const results = resultNames(trx).length;
  if (problems.length === 0) {
    console.log(`test-count-guard: ${cases} cases listed in ${listedMethods(text)} methods, ${results} results, ${rows.size} theories listed once in the row file: none left out`);
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
  const rows = new Map([['A.Tests.Expanded', 2]]);
  const cases = [
    { name: 'every case ran, a theory listed once ran its two rows', listing, trx: whole, problems: 0 },
    { name: 'CRLF listing', listing: listing.replace(/\n/g, '\r\n'), trx: whole, problems: 0 },
    { name: 'a pre-enumerated row lost', listing, trx: trxOf(all.filter(n => n !== 'A.Tests.Rows(x: 2)')), problems: 1 },
    { name: 'a whole method lost', listing, trx: trxOf(all.filter(n => n !== 'A.Tests.Plain')), problems: 1 },
    { name: 'a theory listed once lost every row', listing, trx: trxOf(all.filter(n => !n.startsWith('A.Tests.Expanded'))), problems: 2 },
    // ARV-251: the case the method by method count could not see.
    { name: 'a theory listed once lost one row while another ran', listing, trx: trxOf(all.filter(n => !n.startsWith('A.Tests.Expanded(y: &quot;c'))), problems: 1 },
    { name: 'a theory listed once ran more rows than its row file says', listing, trx: trxOf([...all, 'A.Tests.Expanded(y: &quot;d&quot;)']), problems: 1 },
    { name: 'a theory listed once that ran two rows is not in the row file', listing, trx: whole, rows: new Map(), problems: 1 },
    { name: 'a row file entry for a theory listed by its rows', listing, trx: whole, rows: new Map([...rows, ['A.Tests.Rows', 2]]), problems: 1 },
    { name: 'a row file entry for a method listed once is not a theory listed once with arguments', listing: '  A.Tests.One(x: 1)', trx: trxOf(['A.Tests.One(x: 1)']), rows: new Map([['A.Tests.One', 2]]), problems: 1 },
    { name: 'a row file entry for a method the filtered listing leaves out', listing, trx: whole, rows: new Map([...rows, ['A.Tests.Other', 3]]), problems: 0 },
    { name: 'a row file entry for a method the whole listing does not name', listing, trx: whole, rows: new Map([...rows, ['A.Tests.Other', 3]]), complete: true, problems: 1 },
    { name: 'a whole listing whose row file names only listed methods', listing, trx: whole, complete: true, problems: 0 },
    { name: 'a method listed by its rows that ran one more', listing, trx: trxOf([...all, 'A.Tests.Rows(x: 3)']), problems: 1 },
    { name: 'a theory listed once with one row needs no entry', listing: '  A.Tests.Single', trx: trxOf(['A.Tests.Single(z: 1)']), rows: new Map(), problems: 0 },
    { name: 'a result for a method not listed', listing, trx: trxOf([...all, 'A.Tests.New']), problems: 1 },
    { name: 'empty listing', listing: '', trx: whole, problems: 4 },
    { name: 'no results', listing, trx: trxOf([]), problems: 5 },
    { name: 'the duration line is no case', listing: '  duration: 2s', trx: trxOf(['A.Tests.Plain']), problems: 2 },
    { name: 'a case with a line separator in its arguments', listing: '  A.Tests.Text(s: "a\u2028b")', trx: trxOf(['A.Tests.Text(s: "a\\u2028b")']), problems: 0 },
    // Cut before its last row: every method still has a result; the TRX's own completeness (no end, a total of 5 against 4
    // results) tells, and since ARV-251 so does the row file.
    { name: 'a TRX cut short inside a theory listed once', listing, trx: whole.slice(0, whole.indexOf('<UnitTestResult testName="A.Tests.Expanded(y: &quot;c')), problems: 3 },
    { name: 'a TRX whose total is not its results', listing, trx: trxOf(all, 6), problems: 1 },
    { name: 'a TRX with no total', listing, trx: whole.replace(/<Counters [^>]*\/>/, ''), problems: 1 }
  ];
  let failed = 0;
  for (const c of cases) {
    const got = guardProblems(c.listing, c.trx, c.rows ?? rows, { complete: c.complete === true }).length;
    const ok = got === c.problems;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${c.name} (problems ${got}, expected ${c.problems})`);
  }
  const checks = [
    ['character references decoded', resultNames('<UnitTestResult testName="M(s: &quot;&lt;&amp;&#x41;&#66;&quot;)" />')[0], 'M(s: "<&AB")'],
    ['an unknown entity or code point left as it is', resultNames('<UnitTestResult testName="M(&constructor;&#x110000;)" />')[0], 'M(&constructor;&#x110000;)'],
    ['a whole TRX has no problem', trxProblems(whole).length, 0],
    ['a row file read', JSON.stringify([...parseRows('{"A.B.C": 2, "A.B_d": 100000}').rows]), JSON.stringify([['A.B.C', 2], ['A.B_d', 100000]])],
    ['an empty row file read', parseRows('{}').rows?.size, 0],
    ...['', '[]', 'null', '{"A.B": 1}', '{"A.B": 2.5}', '{"A.B": "2"}', '{"A.B": 100001}', '{"A B": 2}', '{"A.B(x)": 2}', '{"A": {"B": 2}}']
      .map(text => [`a row file refused: ${text || '(empty)'}`, parseRows(text).problem === null, false]),
    ['the guard refuses to run without the row file', (() => { try { guardProblems(listing, whole); return true; } catch { return false; } })(), false],
    ['a row file naming a method twice refused', parseRows('{"A.B": 2, "A.B": 3}').problem?.includes('twice'), true],
    ['a row file with a nested object refused', parseRows('{"A.B": 2, "C": {"D": 3}}').problem?.includes('nested'), true],
    ['a refused name printed escaped', /[\u0000-\u001f]/.test(parseRows('{"A\\u0007B": 2}').problem ?? '\u0007'), false],
    ...commandLine(),
    ['the story filter as method filters', JSON.stringify(methodFilter('FullyQualifiedName~CsvImports|FullyQualifiedName~A.B_c')),
      JSON.stringify(['--filter-method', '*CsvImports*', '--filter-method', '*A.B_c*'])],
    ...['FullyQualifiedName!~X', 'Category=Slow', 'FullyQualifiedName~X&FullyQualifiedName~Y', 'FullyQualifiedName~', 'FullyQualifiedName~a b', '']
      .map(f => [`a filter of another shape refused: ${f || '(empty)'}`, methodFilter(f), null]),
    ['method filters accepted', filterProblem(['--filter-method', '*X*', '--filter-not-class', 'A.B']), null],
    ...[['--filter-method'], ['--filter-query', '/a'], ['--filter-method', '*X* --y'], ['--filter-method', 'A*B'], ['--results-directory', 'x']]
      .map(f => [`filter options refused: ${f.join(' ')}`, filterProblem(f) === null, false])
  ];
  for (const [name, got, expected] of checks) {
    const ok = got === expected;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} (${got})`);
  }
  return failed ? 1 : 0;
}

/**
 * The command line's own answers (the paths the comparison's self-test does not reach), each run as a child process against
 * files in a temporary folder: a whole run passes; a missing, empty or doubled row file, a theory listed once that ran rows
 * the file does not give, a listing without its row file and filter options with a saved listing fail.
 */
function commandLine() {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-count-guard-'));
  try {
    const file = (name, text) => {
      const at = path.join(folder, name);
      fs.writeFileSync(at, text);
      return at;
    };
    const list = file('list.txt', '  A.Tests.Plain\n  A.Tests.Expanded\n');
    const trx = file('run.trx', trxOf(['A.Tests.Plain', 'A.Tests.Expanded(y: 1)', 'A.Tests.Expanded(y: 2)']));
    const rows = file('rows.json', '{"A.Tests.Expanded": 2}');
    const run = args => spawnSync(process.execPath, [fileURLToPath(import.meta.url), ...args], { encoding: 'utf8', timeout: 60_000 }).status;
    return [
      ['command line: a whole run against its row file passes', run(['--list', list, '--rows', rows, '--trx', trx]), 0],
      ['command line: a missing row file fails', run(['--list', list, '--rows', path.join(folder, 'none.json'), '--trx', trx]), 1],
      ['command line: an empty row file fails', run(['--list', list, '--rows', file('empty.json', ''), '--trx', trx]), 1],
      ['command line: a row file naming a method twice fails', run(['--list', list, '--rows', file('twice.json', '{"A.Tests.Expanded": 2, "A.Tests.Expanded": 2}'), '--trx', trx]), 1],
      ['command line: a theory listed once the row file leaves out fails', run(['--list', list, '--rows', file('none-listed.json', '{}'), '--trx', trx]), 1],
      ['command line: a saved listing without its row file is refused', run(['--list', list, '--trx', trx]), 2],
      ['command line: filter options with a saved listing are refused', run(['--list', list, '--rows', rows, '--trx', trx, '--', '--filter-method', '*A*']), 2]
    ];
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
}

function read(file) {
  if (!fs.existsSync(file)) {
    console.error(`test-count-guard: no file at ${file}`);
    return null;
  }
  return fs.readFileSync(file, 'utf8');
}

const all = process.argv.slice(2);
const cut = all.indexOf('--');
const argv = cut >= 0 ? all.slice(0, cut) : all;
const filter = cut >= 0 ? all.slice(cut + 1) : [];
const option = name => (argv.indexOf(name) >= 0 ? argv[argv.indexOf(name) + 1] : undefined);
const dllMode = argv.length === 4 && option('--dll') && option('--trx');
const listMode = argv.length === 6 && option('--list') && option('--rows') && option('--trx') && cut < 0;
if (argv[0] === '--self-test' && argv.length === 1 && cut < 0) process.exit(selfTest());
else if ((dllMode || listMode) && filterProblem(filter) === null) {
  const text = dllMode ? listing(path.resolve(option('--dll')), filter) : read(path.resolve(option('--list')));
  const rowText = read(dllMode ? path.join(path.dirname(path.resolve(option('--dll'))), ROW_FILE) : path.resolve(option('--rows')));
  const trx = read(path.resolve(option('--trx')));
  const parsed = rowText === null ? null : parseRows(rowText);
  if (parsed?.problem) console.error(`test-count-guard: ${parsed.problem}`);
  // The executable's whole listing (no filter) names every theory, so an entry it does not name is stale.
  process.exit(text === null || trx === null || !parsed?.rows ? 1 : report(text, trx, parsed.rows, Boolean(dllMode) && filter.length === 0));
} else {
  console.error('Usage: test-count-guard.mjs --dll <test dll> --trx <file> [-- <filter options>] | --list <file> --rows <file> --trx <file> | --self-test');
  process.exit(2);
}
