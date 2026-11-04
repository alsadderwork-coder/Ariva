// ARV-272 (CWE-400, the gate's own disk): every Playwright run writes its hosts' logs to Platform/Testing/Ariva.E2E/logs/<run start>/
// (playwright.config.ts), and nothing removed an old run's folder: 33 runs held 2.9 GB, and a story gate's integration suite then
// failed on a full disk. Before verify.mjs starts a run, pruneLogFolders keeps the folders of the latest runs, the one starting
// included (ARIVA_E2E_LOG_RUNS, 5 by default, 2 to 1,000, so the newest earlier run's folder always stays, whatever started it), by
// the run start each folder's name holds, and removes the older ones. It never removes a folder whose name is not a run start, a
// link (nor what it points to), or the folder of a run that started at or after this run's own start (one in progress). A folder it
// cannot remove (held open, refused) is named and left; the run goes on. Self-test: node scripts/e2e-logs.mjs --self-test
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const DEFAULT_RUNS = 5;

// The folder name playwright.config.ts writes: new Date().toISOString() with ':' and '.' replaced by '-'.
const RUN_NAME = /^(\d{4})-(\d{2})-(\d{2})T(\d{2})-(\d{2})-(\d{2})-(\d{3})Z$/;

const nameOf = ms => new Date(ms).toISOString().replace(/[:.]/g, '-');

/** The run start a folder's name holds, in milliseconds since the epoch, or null for any other name. */
export function runStart(name) {
  const m = RUN_NAME.exec(name);
  if (!m) return null;
  const ms = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6], +m[7]);
  // A name that is not a real instant (month 13, second 61) would be read as another one: it is no run's.
  return nameOf(ms) === name ? ms : null;
}

/**
 * The runs to keep from ARIVA_E2E_LOG_RUNS: the default when it is not set, a whole number from 2 to 1,000 (the run starting and at
 * least the newest earlier one, which may be a run started outside verify.mjs and still going), or null when not valid.
 */
export function runsToKeep(value) {
  if (value === undefined || value === '') return DEFAULT_RUNS;
  if (!/^\d{1,4}$/.test(value)) return null;
  const runs = Number(value);
  return runs >= 2 && runs <= 1000 ? runs : null;
}

const removeFolder = folder => fs.rmSync(folder, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 });

/**
 * Removes the run folders directly under logsDir beyond the latest `keep` (0 or more) of the runs that started before startedMs,
 * oldest first. Folders of runs at or after startedMs stay, and are not counted. Returns the names removed and those it could not
 * remove (each tried with a few retries, then left), so a folder held open never stops the run that prunes.
 */
export function pruneLogFolders(logsDir, keep, startedMs, remove = removeFolder) {
  if (!fs.existsSync(logsDir)) return { removed: [], left: [] };
  const runs = fs.readdirSync(logsDir, { withFileTypes: true })
    .filter(entry => entry.isDirectory() && !entry.isSymbolicLink())
    .map(entry => ({ name: entry.name, start: runStart(entry.name) }))
    .filter(run => run.start !== null && run.start < startedMs)
    .sort((a, b) => b.start - a.start);
  const removed = [];
  const left = [];
  for (const run of runs.slice(keep).reverse()) {
    try {
      remove(path.join(logsDir, run.name));
      removed.push(run.name);
    } catch {
      left.push(run.name);
    }
  }
  return { removed, left };
}

function selfTest() {
  const problems = [];
  const expect = (what, ok) => { if (!ok) problems.push(what); };

  expect('a run start is read from its folder name', runStart('2026-10-28T09-15-02-123Z') === Date.UTC(2026, 9, 28, 9, 15, 2, 123));
  for (const name of ['2026-13-01T00-00-00-000Z', '2026-02-30T00-00-00-000Z', '2026-10-28T09-15-61-000Z', '2026-10-28T09:15:02.123Z', 'notes', '2026-10-28', '.2026-10-28T09-15-02-123Z'])
    expect(`${name} is no run's folder`, runStart(name) === null);
  for (const [value, runs] of [[undefined, 5], ['', 5], ['2', 2], ['3', 3], ['1000', 1000]])
    expect(`ARIVA_E2E_LOG_RUNS=${value} keeps ${runs}`, runsToKeep(value) === runs);
  for (const value of ['0', '1', '1001', '-1', '5x', ' 5', '5.0', '1e3', '00000'])
    expect(`ARIVA_E2E_LOG_RUNS=${value} is refused`, runsToKeep(value) === null);

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-e2e-logs-'));
  try {
    const logs = path.join(root, 'logs');
    fs.mkdirSync(logs);
    const started = Date.UTC(2026, 10, 4, 12, 0, 0, 0);
    // Eight earlier runs an hour apart, each with a log; a run that started after this one (in progress); other names.
    const earlier = Array.from({ length: 8 }, (_, i) => nameOf(started - (i + 1) * 3_600_000));
    for (const name of earlier) {
      fs.mkdirSync(path.join(logs, name));
      fs.writeFileSync(path.join(logs, name, 'api-main.log'), 'line\n');
    }
    const inProgress = nameOf(started + 1_000);
    const sameStart = nameOf(started);
    for (const name of [inProgress, sameStart, 'notes', '2026-13-01T00-00-00-000Z']) fs.mkdirSync(path.join(logs, name));
    const asFile = nameOf(started - 100 * 3_600_000);
    fs.writeFileSync(path.join(logs, asFile), 'a file with a run name\n');
    // A link with a run's name to a folder outside: neither the link nor what it points to is touched.
    const outside = path.join(root, 'outside');
    fs.mkdirSync(outside);
    fs.writeFileSync(path.join(outside, 'keep.txt'), 'keep\n');
    const linked = nameOf(started - 200 * 3_600_000);
    // A junction on Windows, which needs no privilege (a directory link may need Developer Mode); both read as links.
    fs.symlinkSync(outside, path.join(logs, linked), process.platform === 'win32' ? 'junction' : 'dir');

    const { removed, left } = pruneLogFolders(logs, 4, started);

    expect('the runs beyond the latest four earlier ones are removed, oldest first', JSON.stringify(removed) === JSON.stringify(earlier.slice(4).reverse()) && left.length === 0);
    for (const name of earlier.slice(0, 4)) expect(`${name} (one of the latest) stays`, fs.existsSync(path.join(logs, name, 'api-main.log')));
    for (const name of earlier.slice(4)) expect(`${name} is gone`, !fs.existsSync(path.join(logs, name)));
    for (const name of [inProgress, sameStart, 'notes', '2026-13-01T00-00-00-000Z', asFile]) expect(`${name} stays`, fs.existsSync(path.join(logs, name)));
    expect('the link stays', fs.lstatSync(path.join(logs, linked)).isSymbolicLink());
    expect('what the link points to stays', fs.existsSync(path.join(outside, 'keep.txt')));
    // A folder that cannot be removed is named and left, and the others still go.
    const held = earlier[0];
    const refused = pruneLogFolders(logs, 0, started, folder => {
      if (path.basename(folder) === held) throw Object.assign(new Error('busy'), { code: 'EBUSY' });
      removeFolder(folder);
    });
    expect('a folder that cannot be removed is left and named, the others removed',
      JSON.stringify(refused.left) === JSON.stringify([held]) && refused.removed.length === 3 && fs.existsSync(path.join(logs, held)));
    expect('keeping none removes every earlier run, still not the run in progress',
      pruneLogFolders(logs, 0, started).removed.length === 1 && fs.existsSync(path.join(logs, inProgress)));
    expect('a missing folder removes nothing', pruneLogFolders(path.join(root, 'none'), 0, started).removed.length === 0);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }

  if (problems.length) {
    console.log(`e2e-logs self-test failed:\n  ${problems.join('\n  ')}`);
    return 1;
  }
  console.log('e2e-logs self-test: runs kept and removed as intended');
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.includes('--self-test')) process.exit(selfTest());
  console.log('usage: node scripts/e2e-logs.mjs --self-test');
  process.exit(2);
}
