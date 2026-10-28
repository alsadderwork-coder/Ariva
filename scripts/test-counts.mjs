// Test counts (ARV-164a): what a test executable lists against what its run's TRX holds. A library for
// scripts/test-count-guard.mjs and scripts/mutation-run.mjs, with no command line of its own.
// Under dotnet test the xUnit v3 VSTest adapter reads the runner's messages from its standard output and drops a line it
// cannot read without a failure (ARV-164), so a case, passing or failing, could be missing from a run whose totals look
// plausible. The listing is Microsoft Testing Platform's own (`--list-tests`, one case per line, two spaces in). A theory whose
// rows are not enumerated at discovery is listed once and runs as many rows, so the two totals differ; method by method, though,
// a run has at least as many results as the listing has cases, unless it lost one. A method is the name before its first
// parenthesis, since the listing and the TRX format the arguments differently. A theory listed once is held to its rows by the
// test project's row file (ARV-251, theory-rows.json beside the test executable: each such theory with its number of rows,
// kept current from the theories' own data by the project's TheoryRowsTests), so a lost row of it, while another ran, is
// caught too, and so is any method that ran more rows than it was listed with and is not in the file (a theory with a single
// row needs no entry: losing it leaves its method without a result); on a whole run an entry for a method not listed (renamed
// or removed) is reported as well. The method is the name before the first parenthesis, so a test under a display name of
// its own could be counted with another; TheoryRowsTests refuses such names, so none is set. Not caught: a theory listed once
// and left out of the file whose every run, from its first, lost all but one of its rows. No package dependencies (the
// secret shapes are a local module); the command is always dotnet.

import { spawnSync } from 'node:child_process';
import { redactSecrets } from '../Platform/Testing/Ariva.E2E/scripts/secret-shapes.mjs';

// The s flag: a case's arguments may hold U+2028 or U+2029, which . does not match without it (a display text test does).
const CASE = /^ {2}(?!duration: )(\S.*?)\s*$/s;
const RESULT = /<UnitTestResult\b[^>]*?\btestName="([^"]*)"/g;
const TOTAL = /<Counters\b[^>]*?\btotal="(\d+)"/;
const ENTITIES = new Map([['quot', '"'], ['amp', '&'], ['lt', '<'], ['gt', '>'], ['apos', "'"]]);

/** The cases of a `--list-tests` listing, as named there. */
export function listedCases(text) {
  return String(text ?? '').replace(/\r/g, '').split('\n').map(line => CASE.exec(line)?.[1]).filter(Boolean);
}

function decode(whole, ref) {
  if (ref[0] !== '#') return ENTITIES.get(ref) ?? whole;
  const code = ref[1].toLowerCase() === 'x' ? parseInt(ref.slice(2), 16) : parseInt(ref.slice(1), 10);
  return Number.isInteger(code) && code >= 0 && code <= 0x10ffff ? String.fromCodePoint(code) : whole;
}

/** The test names of a TRX's results, with XML character references decoded. */
export function resultNames(trx) {
  return [...String(trx ?? '').matchAll(RESULT)].map(m => m[1].replace(/&(#x[0-9a-f]+|#\d+|\w+);/gi, decode));
}

const methodOf = name => name.split('(', 1)[0];

function countBy(names) {
  const counts = new Map();
  for (const name of names) counts.set(methodOf(name), (counts.get(methodOf(name)) ?? 0) + 1);
  return counts;
}

/** Why a TRX is not a whole run's: cut short (no summary or no end), or a total that is not its number of results. */
export function trxProblems(trx) {
  const text = String(trx ?? '');
  const problems = [];
  if (!text.includes('<ResultSummary') || !text.trimEnd().endsWith('</TestRun>')) problems.push('the TRX is cut short (no result summary or no end)');
  const total = TOTAL.exec(text);
  const results = resultNames(text).length;
  if (!total) problems.push('the TRX states no total');
  else if (Number(total[1]) !== results) problems.push(`the TRX states ${total[1]} tests and holds ${results} results`);
  return problems;
}

/**
 * Why a run does not account for every listed case: an incomplete TRX, nothing listed, no results, a method with fewer
 * results than cases, results for a method the listing does not name, and against the row file (`rows`, from parseRows): a
 * theory listed once whose results are not its rows, a method that ran more rows than it was listed with and is not in the
 * file, an entry for a method listed by its rows (enumerated at discovery) and, when the listing is the whole executable's
 * (`complete`), an entry for a method it does not name. A filtered run's listing names only what it selected, so there those
 * entries are not looked at.
 */
export function guardProblems(listing, trx, rows, { complete = false } = {}) {
  if (!(rows instanceof Map)) throw new TypeError('guardProblems needs the row file\'s rows');
  const cases = listedCases(listing);
  const listed = countBy(cases);
  const bare = new Set(cases.filter(name => !name.includes('(')));
  const ran = countBy(resultNames(trx));
  const problems = trxProblems(trx);
  if (listed.size === 0) problems.push('the listing names no case');
  if (ran.size === 0) problems.push('the TRX holds no result');
  for (const [method, cases] of listed) {
    const results = ran.get(method) ?? 0;
    if (results < cases) problems.push(`${method}: listed with ${cases} case(s), ${results} result(s)`);
  }
  for (const method of ran.keys())
    if (!listed.has(method)) problems.push(`${method}: results but not listed (the listing is not this build's)`);
  for (const [method, cases] of listed) {
    const once = cases === 1 && bare.has(method);
    const results = ran.get(method) ?? 0;
    if (rows.has(method) && !once) problems.push(`${method}: in the row file but listed by its rows (enumerated at discovery): remove it from the file`);
    else if (rows.has(method) && results < rows.get(method)) problems.push(`${method}: a theory listed once with ${rows.get(method)} rows in the row file, ${results} result(s)`);
    else if (rows.has(method) && results > rows.get(method)) problems.push(`${method}: ${results} results, ${rows.get(method)} rows in the row file (update the file)`);
    else if (!rows.has(method) && results > cases) problems.push(`${method}: ran ${results} rows, listed with ${cases} case(s), and is not in the row file`);
  }
  if (complete)
    for (const method of rows.keys())
      if (!listed.has(method)) problems.push(`${method}: in the row file but not listed (renamed or removed): remove it from the file`);
  return problems;
}

const ROW_METHOD = /^[A-Za-z_][A-Za-z0-9_.]{0,400}$/;
// A JSON object's member names, read again to find one named twice (JSON.parse keeps the last).
const ROW_KEY = /"((?:[^"\\]|\\.)*)"\s*:/g;

/**
 * A row file (ARV-251): a JSON object naming each theory listed once with its number of rows, at least 2 and at most 100,000.
 * The rows as a Map, or the problem with the file.
 */
export function parseRows(text) {
  let value;
  try {
    value = JSON.parse(String(text ?? ''));
  } catch {
    return { rows: null, problem: 'the row file is not JSON' };
  }
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return { rows: null, problem: 'the row file is not a JSON object' };
  const names = [...String(text).matchAll(ROW_KEY)].map(m => m[1]);
  const twice = names.find((name, i) => names.indexOf(name) !== i);
  if (twice !== undefined) return { rows: null, problem: `the row file names a method twice: ${JSON.stringify(twice.slice(0, 80))}` };
  if (names.length !== Object.keys(value).length) return { rows: null, problem: 'the row file holds members it does not name plainly (a nested object or an escaped name)' };
  const rows = new Map();
  for (const [method, count] of Object.entries(value)) {
    if (!ROW_METHOD.test(method)) return { rows: null, problem: `the row file names no method: ${JSON.stringify(method.slice(0, 80))}` };
    if (!Number.isInteger(count) || count < 2 || count > 100_000) return { rows: null, problem: `${method}: the row file's count is not a whole number from 2 to 100,000` };
    rows.set(method, count);
  }
  return { rows, problem: null };
}

/** The number of distinct methods in a listing. */
export const listedMethods = text => countBy(listedCases(text)).size;

const FILTER_OPTION = /^--filter-(not-)?(method|class|namespace)$/;
const FILTER_VALUE = /^\*?[A-Za-z0-9_.]{1,200}\*?$/;

/** Why the given test filter options are refused (pairs of a --filter-method, -class or -namespace option and a name), or null. */
export function filterProblem(options) {
  if (!Array.isArray(options) || options.length % 2 !== 0 || options.length > 80) return 'filter options come in pairs, at most 40';
  const bad = options.find((a, i) => !(i % 2 ? FILTER_VALUE : FILTER_OPTION).test(String(a)));
  return bad === undefined ? null : `not a filter option or name: ${String(bad).slice(0, 80)}`;
}

/**
 * The story gate's integration filter in dotnet test's syntax (`FullyQualifiedName~A|FullyQualifiedName~B`, ARV-164b) as the
 * test executable's options: `--filter-method *A*` for each term, which matches a method whose fully qualified name holds A,
 * as `~` does. Null for any other shape (a filter the translation would change).
 */
export function methodFilter(expression) {
  const terms = String(expression ?? '').split('|');
  if (terms.length > 40) return null;
  const options = [];
  for (const term of terms) {
    const m = /^FullyQualifiedName~([A-Za-z0-9_.]{1,120})$/.exec(term);
    if (!m) return null;
    options.push('--filter-method', `*${m[1]}*`);
  }
  return options;
}

/**
 * A test dll's own listing, or null when it cannot be had: dotnet only, with an argument array and no shell (the path comes
 * from verify.mjs, mutation-run.mjs or CI), without colour codes, and bounded in time. With filter options (those of the run,
 * checked by filterProblem), the cases the run selected.
 */
export function listing(dll, filter = []) {
  const problem = filterProblem(filter);
  if (problem) {
    console.error(`test-counts: ${problem}`);
    return null;
  }
  const r = spawnSync('dotnet', [dll, '--list-tests', '--no-ansi', ...filter], { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, timeout: 5 * 60 * 1000 });
  if (r.status !== 0) {
    console.error(`test-counts: dotnet ${dll} --list-tests exited ${r.status ?? r.signal ?? r.error?.code}`);
    // ARV-252: the listing's tail holds case names with their theory arguments, printed into CI's public job log before the
    // test log check runs, so through the shapes' redaction.
    process.stderr.write(redactSecrets(`${r.stdout ?? ''}${r.stderr ?? ''}`.slice(-4000)));
    return null;
  }
  return r.stdout;
}
