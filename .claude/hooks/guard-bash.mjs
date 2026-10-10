// PreToolUse guard for Bash and PowerShell commands.
// Blocks destructive, shared-environment and supply-chain actions; humans run those.
import path from 'node:path';
import { readInput, deny, ask, PROJECT_DIR } from './lib.mjs';

const input = await readInput();
const cmd = String(input?.tool_input?.command || '');
if (!cmd) process.exit(0);
const c = cmd.replace(/\s+/g, ' ');

const rules = [
  [/\bgit\s+(reset\s+--hard|clean\s+-[a-z]*f|checkout\s+--\s+\.|restore\s+\.)/i, 'Destructive git command. Use git stash or ask the human.'],
  [/\bgit\s+(rebase|filter-branch|filter-repo)\b/i, 'History rewriting needs the human.'],
  [/\b(helm|helmfile)\s+(install|upgrade|uninstall|delete|rollback|apply|sync|destroy)\b/i, 'Cluster changes run through the release pipeline, not from an agent session.'],
  [/\bkubectl\s+(apply|create|delete|patch|replace|scale|rollout|edit|exec|cp|drain|cordon|taint|label|annotate)\b/i, 'Only read-only kubectl (get, describe, logs) is allowed for agents.'],
  [/\b(DROP\s+(DATABASE|SCHEMA|TABLE)|TRUNCATE\s+TABLE|dropdb\b)/i, 'Destructive SQL. Write a versioned script and let the human run it.'],
  [/\b(curl|wget|iwr|Invoke-WebRequest)\b[^|]*\|\s*(ba)?sh\b|\biex\s*\(/i, 'Piping downloads into a shell is not allowed.'],
  [/\b(npm\s+publish|dotnet\s+nuget\s+push|docker\s+push)\b/i, 'Publishing artifacts is done by the pipeline.'],
  [/\b(npm\s+install|npm\s+i)\s+(-g|--global)\b/i, 'Global installs change the developer machine; ask the human.'],
  [/(\.\.[\\/]Aman|DevOps[\\/]+Aman)[^\s]*.*(>|\btee\b|sed\s+-i|\bmv\b|\bcp\b|\brm\b|Set-Content|Out-File|Remove-Item)|(>|\btee\b|sed\s+-i|\bmv\b|\bcp\b|\brm\b|Set-Content|Out-File|Remove-Item).*(\.\.[\\/]Aman|DevOps[\\/]+Aman)/i, 'The AMAN repository is read-only reference material for Ariva agents.'],
  [/\bgit\s+-C\s+\S*Aman\S*\s+(commit|checkout|switch|reset|clean|stash|merge|pull|push)\b/i, 'The AMAN repository is read-only reference material for Ariva agents.'],
  [/(^|[\s"'=:;,(\/\\])\.private([\/\\\s"';|&)]|$)/i, 'The .private folder holds client material (owner decision 2026-10-09, ARV-139c): agents never read, list, copy or write it.']
];
for (const [re, why] of rules) if (re.test(c)) deny(why);

// Recursive searches that start at the repository root (or above it) walk the git-ignored client material folder without
// naming it (owner decision 2026-10-10, after two such searches): grep -r, find, and rg told to ignore .gitignore. Searches
// that start inside a project folder, git grep and plain rg (which honours .gitignore) are unaffected.
{
  let cwd = path.resolve(String(input?.cwd || PROJECT_DIR));
  const atOrAboveRoot = target => {
    const t = path.resolve(cwd, target.replace(/^["']|["']$/g, ''));
    return t === PROJECT_DIR || PROJECT_DIR.startsWith(t.endsWith(path.sep) ? t : t + path.sep);
  };
  const why = 'Recursive searches from the repository root walk the client material folder. Name the project folders to search (Platform, docs, wiki, scripts, backlog, ...) or use the Grep tool, which skips git-ignored folders.';
  for (const segment of cmd.split(/\|\||&&|[;|\n]/)) {
    const words = segment.trim().split(/\s+/).filter(Boolean);
    while (words.length && /^(sudo|nice|time|xargs|-n|\d+|[A-Z_][A-Z0-9_]*=\S*)$/.test(words[0])) words.shift();
    if (words[0] === 'cd') { cwd = path.resolve(cwd, (words[1] || PROJECT_DIR).replace(/^["']|["']$/g, '')); continue; }
    const tool = path.basename(words[0] || '');
    const flags = words.slice(1).filter(w => w.startsWith('-'));
    const operands = words.slice(1).filter(w => !w.startsWith('-'));
    if (/^[ef]?grep$/.test(tool) && flags.some(f => /^-[a-zA-Z]*[rR]/.test(f) || f === '--recursive' || f === '--dereference-recursive')) {
      const patternGiven = flags.some(f => /^-[a-zA-Z]*[ef]$/.test(f) || f.startsWith('--regexp') || f.startsWith('--file'));
      const targets = patternGiven ? operands : operands.slice(1);
      if (!targets.length ? atOrAboveRoot('.') : targets.some(atOrAboveRoot)) deny(why);
    }
    if (tool === 'find') {
      const starts = [];
      for (const w of words.slice(1)) { if (/^[-(!]/.test(w)) break; starts.push(w); }
      if (!starts.length ? atOrAboveRoot('.') : starts.some(atOrAboveRoot)) deny(why);
    }
    if (tool === 'rg' && flags.some(f => /^-u+$/.test(f) || f.startsWith('--no-ignore'))) {
      if (!operands.slice(1).length ? atOrAboveRoot('.') : operands.slice(1).some(atOrAboveRoot)) deny(why);
    }
  }
}

// git push: only story branches, only to origin, never forced, never main or trunk. Humans merge pull requests.
for (const push of c.matchAll(/\bgit\s+push\b([^;&|]*)/gi)) {
  const parts = push[1].trim().split(' ').filter(Boolean);
  const flags = parts.filter(p => p.startsWith('-'));
  const refs = parts.filter(p => !p.startsWith('-'));
  if (flags.some(f => /^(-f|--force.*|--mirror|--all|--tags|--delete|-d|--prune)$/.test(f))) deny('Forced, mirror, delete and bulk pushes are not allowed for agents.');
  if (refs.length < 2 || refs[0] !== 'origin') deny('Push with an explicit branch: git push -u origin <story branch>.');
  for (const ref of refs.slice(1)) {
    const target = ref.includes(':') ? ref.split(':').pop() : ref;
    if (ref.startsWith('+') || ref.startsWith(':')) deny('Forced and delete refspecs are not allowed for agents.');
    if (!/^(refs\/heads\/)?(ralph|story|feature|fix|claude)\/[A-Za-z0-9._\/-]+$/.test(target)) deny(`Agents push story branches only (ralph/, story/, feature/, fix/, claude/), never ${target}. Open a pull request; a human merges.`);
  }
}

// rm -rf is allowed only on build output folders.
const rm = c.match(/\b(rm\s+-[a-z]*r[a-z]*f?|rm\s+-[a-z]*f[a-z]*r|Remove-Item\b[^;|&]*-Recurse)\s+([^;|&]+)/i);
if (rm) {
  const targets = rm[2].split(' ').filter(t => t && !t.startsWith('-'));
  const ok = /(^|[\\/])(bin|obj|node_modules|\.svelte-kit|build|dist|test-results|playwright-report|TestResults|\.verify|coverage)[\\/]?$/;
  if (!targets.length || targets.some(t => !ok.test(t.replace(/["']/g, '')))) deny(`rm -rf is limited to build output folders. Targets: ${targets.join(' ')}`);
}

if (/\bdotnet\s+add\b.*\bpackage\b/i.test(c) || /\bnpm\s+(install|i|add)\s+[^-]/i.test(c)) {
  ask('Adding a dependency. Check it with the NuGet or npm audit first, pin the version in Directory.Packages.props or package.json, and confirm.');
}
process.exit(0);
