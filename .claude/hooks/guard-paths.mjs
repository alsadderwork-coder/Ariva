// PreToolUse guard for Edit, Write and MultiEdit.
// Keeps agents inside the repository, protects AMAN and the harness, blocks secrets,
// stops agents from approving their own security exceptions, and enforces the no-dash writing rule.
import fs from 'node:fs';
import path from 'node:path';
import { readInput, deny, ask, resolveInProject, isInside, isTempOrClaudeHome, PROJECT_DIR, AMAN_DIR } from './lib.mjs';

const input = await readInput();
const ti = input?.tool_input || {};
const target = resolveInProject(ti.file_path || ti.notebook_path);
if (!target) process.exit(0);

if (isInside(target, AMAN_DIR)) deny('The AMAN repository is read-only reference material. Port the pattern into Ariva instead.');
if (!isInside(target, PROJECT_DIR) && !isTempOrClaudeHome(target)) deny(`Writes outside the Ariva repository are not allowed: ${target}`);

const rel = path.relative(PROJECT_DIR, target).replace(/\\/g, '/');
const base = path.basename(target).toLowerCase();

if (/(^|\/)\.private(\/|$)/i.test(rel)) deny('The .private folder holds client material (owner decision 2026-10-09, ARV-139c): agents never read or write it.');

if (/^\.env(\.|$)/.test(base) || /\.(pfx|p12|pem|key|jks|kdbx)$/.test(base) || base === 'id_rsa' || base === 'kubeconfig') {
  deny('Secret material never goes into the repository. Use Kubernetes secrets or user secrets.');
}

// Gather the text being written.
const pieces = [];
if (typeof ti.content === 'string') pieces.push(ti.content);
if (typeof ti.new_string === 'string') pieces.push(ti.new_string);
if (Array.isArray(ti.edits)) for (const e of ti.edits) if (typeof e?.new_string === 'string') pieces.push(e.new_string);
const text = pieces.join('\n');

if (rel === 'security/allowlist.json') {
  // Simulate the write, then compare approved entries before and after. Agents may add or edit
  // PENDING entries, but every approved entry in the result must exist unchanged in the current file.
  const current = fs.existsSync(target) ? fs.readFileSync(target, 'utf8') : '[]';
  let next = current;
  if (typeof ti.content === 'string') next = ti.content;
  const edits = Array.isArray(ti.edits) ? ti.edits : (typeof ti.old_string === 'string' ? [ti] : []);
  for (const e of edits) {
    if (typeof e?.old_string !== 'string' || typeof e?.new_string !== 'string') continue;
    next = e.replace_all ? next.split(e.old_string).join(e.new_string) : next.replace(e.old_string, e.new_string);
  }
  const approvedKeys = json => {
    let list;
    try { list = JSON.parse(json); } catch { deny('security/allowlist.json must stay valid JSON.'); }
    return new Set((Array.isArray(list) ? list : [])
      .filter(x => x && !/^PENDING/i.test(String(x.approvedBy ?? '')))
      .map(x => JSON.stringify([x.rule, x.path, x.contains, x.routes, x.reason, x.approvedBy])));
  };
  const before = approvedKeys(current);
  for (const key of approvedKeys(next)) {
    if (!before.has(key)) deny('Agents may propose security exceptions but never approve or change approved ones. Set "approvedBy": "PENDING: Ahmad" on new or changed entries.');
  }
}

if (rel.startsWith('.claude/settings') || rel.startsWith('.claude/hooks/') || rel === '.mcp.json' || rel.startsWith('scripts/security/')) {
  ask(`This changes the harness or the security gate (${rel}). A human must approve.`);
}

// Writing rule from the product owner: no em dashes, no en dashes as dashes, no double hyphens in prose.
if (/\.(md|txt)$/i.test(base) || rel.startsWith('wiki/') || rel.startsWith('docs/')) {
  if (/[—–]/.test(text)) deny('Docs must not contain em or en dashes. Use commas, colons, semicolons, parentheses or periods.');
  const prose = text.replace(/```[\s\S]*?```/g, '').replace(/`[^`]*`/g, '');
  if (/\s--\s/.test(prose)) deny('Docs must not use double hyphens as dashes.');
}
if (/\.(cs|ts|js|mjs|svelte|json|resx)$/i.test(base) && /—/.test(text)) {
  deny('No em dash characters in code, strings or resources. Use a comma, colon or parentheses.');
}
process.exit(0);
