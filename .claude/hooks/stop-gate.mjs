// Stop: before an agent finishes, run the fast gates (security scan of changed files and docs text rules).
// The full gates (build, tests, e2e) are part of each story's acceptance criteria and run via scripts/verify.mjs.
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { readInput, block, PROJECT_DIR } from './lib.mjs';

const input = await readInput();
if (input?.stop_hook_active) process.exit(0); // never loop

const r = spawnSync(process.execPath, [path.join(PROJECT_DIR, 'scripts', 'verify.mjs'), 'quick'], { cwd: PROJECT_DIR, encoding: 'utf8', timeout: 120000 });
if (r.status === 0) process.exit(0);
const tail = `${r.stdout || ''}\n${r.stderr || ''}`.split(/\r?\n/).filter(l => /ERROR|FAIL|DOCS/.test(l)).slice(-25).join('\n');
block(`The quick gate failed (node scripts/verify.mjs quick). Fix these before finishing:\n${tail}`, 'Stop');
