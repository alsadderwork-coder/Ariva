// Test counts (ARV-164a): what a test executable lists against what its run's TRX holds. A library for
// scripts/test-count-guard.mjs and scripts/mutation-run.mjs, with no command line of its own.
// Under dotnet test the xUnit v3 VSTest adapter reads the runner's messages from its standard output and drops a line it
// cannot read without a failure (ARV-164), so a case, passing or failing, could be missing from a run whose totals look
// plausible. The listing is Microsoft Testing Platform's own (`--list-tests`, one case per line, two spaces in). A theory whose
// rows are not enumerated at discovery is listed once and runs as many rows, so the two totals differ; method by method, though,
// a run has at least as many results as the listing has cases, unless it lost one. A method is the name before its first
// parenthesis, since the listing and the TRX format the arguments differently. Not caught: a lost row of a theory listed once
// while another of its rows ran (ARV-251), and a loss hidden by another method's extra rows when two custom display names share
// the text before their parenthesis (the unit tests set none today). No dependencies; the command is always dotnet.

import { spawnSync } from 'node:child_process';

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
 * results than cases, or results for a method the listing does not name.
 */
export function guardProblems(listing, trx) {
  const listed = countBy(listedCases(listing));
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
  return problems;
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
    process.stderr.write(`${r.stdout ?? ''}${r.stderr ?? ''}`.slice(-4000));
    return null;
  }
  return r.stdout;
}
