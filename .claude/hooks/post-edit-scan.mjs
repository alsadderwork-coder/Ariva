// PostToolUse: scan the file the agent just wrote with the Ariva security gate and
// feed any CWE findings straight back, so they are fixed in the same step.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { readInput, block, context, resolveInProject, isInside, PROJECT_DIR } from './lib.mjs';

const input = await readInput();
const ti = input?.tool_input || {};
const target = resolveInProject(ti.file_path || ti.notebook_path);
if (!target || !isInside(target, PROJECT_DIR) || !fs.existsSync(target)) process.exit(0);

const scanner = path.join(PROJECT_DIR, 'scripts', 'security', 'scan.mjs');
if (!fs.existsSync(scanner)) process.exit(0);
const { scanFiles, loadAllowlist } = await import(pathToFileURL(scanner).href);

let findings = [];
try { findings = scanFiles([target], { allow: loadAllowlist() }); } catch { process.exit(0); }
if (!findings.length) process.exit(0);

const lines = findings.map(f => `${f.severity.toUpperCase()} ${f.rule} [${f.cwe.join(' ')}] ${f.file}:${f.line} ${f.msg}`);
const errors = findings.filter(f => f.severity === 'error');
const text = `Security gate findings in the file you just wrote (see docs/security/cwe-controls.md):\n${lines.join('\n')}`;
if (errors.length) block(`${text}\nFix these before continuing. If a finding is a genuine exception, propose an entry in security/allowlist.json with "approvedBy": "PENDING: Ahmad" and explain why.`);
context(text, 'PostToolUse');
