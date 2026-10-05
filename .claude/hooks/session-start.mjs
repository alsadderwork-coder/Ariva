// SessionStart: orient the agent. Shows the next eligible backlog stories and the standing rules.
import fs from 'node:fs';
import path from 'node:path';
import { context, readJson, PROJECT_DIR } from './lib.mjs';

const prdDir = path.join(PROJECT_DIR, 'backlog');
const files = fs.existsSync(prdDir) ? fs.readdirSync(prdDir).filter(f => /^prd-.*\.json$/.test(f)) : [];
const lines = [];
// A story may depend on one in another PRD (the ASVS gaps depend on ARV-074 in phase 0), so passing ids are collected first.
const prds = files.map(f => [f, readJson(path.join(prdDir, f), { userStories: [] })]);
const passed = new Set(prds.flatMap(([, prd]) => (prd.userStories || []).filter(s => s.passes).map(s => s.id)));
for (const [f, prd] of prds) {
  const stories = prd.userStories || [];
  const done = new Set(stories.filter(s => s.passes).map(s => s.id));
  const next = stories.filter(s => !s.passes && (s.dependsOn || []).every(d => passed.has(d))).sort((a, b) => a.priority - b.priority).slice(0, 5);
  lines.push(`${f}: ${done.size}/${stories.length} stories pass. Next eligible: ${next.map(s => `${s.id} ${s.title}`).join('; ') || 'none'}`);
}
lines.push('Rules: read CLAUDE.md; one story per session; security gate and tests are part of done; AMAN (../Aman) is read-only reference; never push; docs without em dashes.');
context(lines.join('\n'), 'SessionStart');
