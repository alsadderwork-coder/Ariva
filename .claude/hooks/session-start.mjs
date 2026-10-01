// SessionStart: orient the agent. Shows the next eligible backlog stories and the standing rules.
import fs from 'node:fs';
import path from 'node:path';
import { context, readJson, PROJECT_DIR } from './lib.mjs';

const prdDir = path.join(PROJECT_DIR, 'backlog');
const files = fs.existsSync(prdDir) ? fs.readdirSync(prdDir).filter(f => /^prd-.*\.json$/.test(f)) : [];
const lines = [];
for (const f of files) {
  const prd = readJson(path.join(prdDir, f), { userStories: [] });
  const stories = prd.userStories || [];
  const done = new Set(stories.filter(s => s.passes).map(s => s.id));
  const next = stories.filter(s => !s.passes && (s.dependsOn || []).every(d => done.has(d))).sort((a, b) => a.priority - b.priority).slice(0, 5);
  lines.push(`${f}: ${done.size}/${stories.length} stories pass. Next eligible: ${next.map(s => `${s.id} ${s.title}`).join('; ') || 'none'}`);
}
lines.push('Rules: read CLAUDE.md; one story per session; security gate and tests are part of done; AMAN (../Aman) is read-only reference; never push; docs without em dashes.');
context(lines.join('\n'), 'SessionStart');
