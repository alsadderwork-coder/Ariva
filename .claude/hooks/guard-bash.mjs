// PreToolUse guard for Bash and PowerShell commands.
// Blocks destructive, shared-environment and supply-chain actions; humans run those.
import { readInput, deny, ask } from './lib.mjs';

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
