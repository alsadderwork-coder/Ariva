// Self-test of root-search.mjs and its use in guard-bash.mjs: node .claude/hooks/root-search.test.mjs (verify.mjs quick).
// Each case is [command, cwd, expected]; the paths come from this repository's location, so it runs on any machine.
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { rootSearch } from './root-search.mjs';

const HOOKS = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HOOKS, '..', '..');
const R = ROOT.replace(/\\/g, '/'); // forward slashes: a backslash in a shell command is an escape
const UP = path.dirname(ROOT).replace(/\\/g, '/');
const TMP = os.tmpdir().replace(/\\/g, '/');
const DOCS = path.join(ROOT, 'docs');
const PLATFORM = path.join(ROOT, 'Platform');

const cases = [
  // refused: a recursive walk that starts at the root or above it
  ['grep -rn foo .', ROOT, 'deny'],
  ['grep -rn foo', ROOT, 'deny'],
  ['grep -R foo ./', ROOT, 'deny'],
  [`grep --recursive foo ${R}`, ROOT, 'deny'],
  ['grep -rl -e foo .', ROOT, 'deny'],
  [`grep -rn foo ${UP}`, ROOT, 'deny'],
  ['grep -rn foo ..', DOCS, 'deny'],
  [`cd ${R} && grep -rn foo .`, DOCS, 'deny'],
  ['find . -name x', ROOT, 'deny'],
  ['find -name x', ROOT, 'deny'],
  [`find ${R} -type f`, TMP, 'deny'],
  ['rg --no-ignore foo', ROOT, 'deny'],
  ['rg -uu foo .', ROOT, 'deny'],
  ['ls -la | grep -rn foo .', ROOT, 'deny'],
  ['grep -rn "a\\|b" .', ROOT, 'deny'],
  ['echo "a;b" && grep -r x .', ROOT, 'deny'],
  ['grep -rE "foo|bar" | head', ROOT, 'deny'],
  // shells, eval, substitutions and wrappers run the search all the same
  ["bash -c 'grep -r x .'", ROOT, 'deny'],
  [`sh -lc 'cd ${R} && find . -name x'`, TMP, 'deny'],
  ['eval "grep -r x ."', ROOT, 'deny'],
  ['echo "$(grep -r x .)"', ROOT, 'deny'],
  ['echo `find . -name y`', ROOT, 'deny'],
  ['setsid nohup grep -r x . > out.log 2>&1 &', ROOT, 'deny'],
  ['timeout 30s find . -type f', ROOT, 'deny'],
  ['env LC_ALL=C grep -rn x .', ROOT, 'deny'],
  ['/usr/bin/env grep -rn foo .', ROOT, 'deny'],
  ['bash -Ec "grep -r x ."', ROOT, 'deny'],
  ['bash <<< "grep -r x ."', ROOT, 'deny'],
  // heredocs: a shell's body is checked, and the lines after any body are too
  ["bash <<'EOF'\ngrep -r x .\nEOF", ROOT, 'deny'],
  ["python3 - <<'EOF'\nx=1\nEOF\ngrep -r x .", ROOT, 'deny'],
  ["cat <<'EOF' | bash\ngrep -r x .\nEOF", ROOT, 'deny'],
  [`cat <<'EOF' > ${TMP}/a.sh && bash ${TMP}/a.sh\nfind . -name x\nEOF`, ROOT, 'deny'],
  ['python3 - <<END-PY\nprint(1)\nEND-PY\nfind . -name x', ROOT, 'deny'],
  ["bash <<'EOF'\ncd Platform\nEOF\nfind . -name x", ROOT, 'deny'],
  // comments, escapes and quoted values do not hide what follows
  ["# Check what's there\nfind . -name '*.cs'", ROOT, 'deny'],
  ["ls docs # what's here\nfind . -name '*.md'", ROOT, 'deny'],
  ['# a 5" screen\ngrep -rn foo . && echo "done"', ROOT, 'deny'],
  ["echo it\\'s; find . -name '*.cs'", ROOT, 'deny'],
  ['grep -rn --include="*.cs" foo', ROOT, 'deny'],
  ['grep -rn --exclude-dir="node_modules" foo', ROOT, 'deny'],
  ['grep -r -e"foo"', ROOT, 'deny'],
  // a cd inside a substitution, a sh -c or a subshell stays there
  ['DIR=$(cd Platform && pwd); find . -name x', ROOT, 'deny'],
  [`sh -c 'cd ${TMP}'; find . -name x`, ROOT, 'deny'],
  // separators, keywords, variables, flag values and other recursive forms
  ['npm run dev & find . -name x', ROOT, 'deny'],
  ['(find . -name x)', ROOT, 'deny'],
  ['(grep -rn foo .)', ROOT, 'deny'],
  ['if [ -d docs ]; then find . -name x; fi', ROOT, 'deny'],
  ['for d in a b; do grep -rn foo .; done', ROOT, 'deny'],
  ['find "$PWD" -name x', ROOT, 'deny'],
  ['find $(pwd) -name x', ROOT, 'deny'],
  ['grep -rn foo "$CLAUDE_PROJECT_DIR"', ROOT, 'deny'],
  ['cd "$CLAUDE_PROJECT_DIR" && find . -name x', TMP, 'deny'],
  [`cd -- ${R} && find . -name x`, TMP, 'deny'],
  ['grep -rn -A 3 foo', ROOT, 'deny'],
  ['grep -rn -C 2 TODO', ROOT, 'deny'],
  ['grep -rn -e foo -e bar', ROOT, 'deny'],
  ["grep -r --include '*.cs' foo", ROOT, 'deny'],
  ["rg -uu -g '*.cs' foo", ROOT, 'deny'],
  ['rgrep foo', ROOT, 'deny'],
  ['grep -d recurse foo .', ROOT, 'deny'],
  ['git grep --no-index foo', ROOT, 'deny'],
  ['ls -laR', ROOT, 'deny'],
  ['grep -rn foo .*', ROOT, 'deny'],
  // assignments with a path in their value are still wrappers; a << with no terminator line is not a heredoc
  ['PATH=/usr/bin:/bin grep -rn foo .', ROOT, 'deny'],
  [`TMPDIR=${TMP} find . -name x`, ROOT, 'deny'],
  ['echo $((1<<3))\nfind . -name x', ROOT, 'deny'],
  ['python3 -c "print(1 << 3)"\nfind . -name x', ROOT, 'deny'],
  ['echo "use <<EOF in docs"\nfind . -name x', ROOT, 'deny'],
  // a redirection's target is not a search path
  [`grep -rn foo > ${TMP}/x`, ROOT, 'deny'],
  ['find . -name x 2>/dev/null', ROOT, 'deny'],
  [`find . -name x > ${TMP}/list 2>&1`, ROOT, 'deny'],

  // allowed: inside a project folder, explicit project folders, or not recursive
  ['grep -rn foo Platform docs', ROOT, 'allow'],
  ['grep -rn foo .', PLATFORM, 'allow'],
  ['grep -n foo README.md', ROOT, 'allow'],
  ['find Platform -name x', ROOT, 'allow'],
  ['find . -name x', DOCS, 'allow'],
  ['rg foo', ROOT, 'allow'],
  ['git grep foo', ROOT, 'allow'],
  ['ps -eo args | grep -E "[v]erify"', ROOT, 'allow'],
  ['git status --short', ROOT, 'allow'],
  ['node scripts/verify.mjs quick', ROOT, 'allow'],
  [`grep -c x ${TMP}/a.log`, ROOT, 'allow'],
  [`find ${TMP} -name x`, ROOT, 'allow'],
  ['grep -rn -i "servic\\|seconds\\|duration" Platform/Business --include=*.cs', ROOT, 'allow'],
  ["grep -rn 'x|y' Platform", ROOT, 'allow'],
  ['grep -rE "foo|bar" docs wiki | head', ROOT, 'allow'],
  ["bash -c 'grep -r x Platform'", ROOT, 'allow'],
  ["sh -c 'cd Platform && grep -rn foo .'", ROOT, 'allow'],
  [`setsid nohup bash ${TMP}/x.sh > ${TMP}/x.log 2>&1 < /dev/null &`, ROOT, 'allow'],
  ['timeout 30s find Platform -type f', ROOT, 'allow'],
  ['echo "$(git rev-parse HEAD)"', ROOT, 'allow'],
  ['cd docs && echo $(find . -name x)', ROOT, 'allow'],
  ['grep -rn foo ./"Platform"', ROOT, 'allow'],
  ['grep -rn -A 3 foo Platform docs', ROOT, 'allow'],
  ["rg -uu -g '*.cs' foo Platform", ROOT, 'allow'],
  ['grep -r --include "*.md" TODO docs wiki', ROOT, 'allow'],
  ['ls -R Platform/Cloud | head', ROOT, 'allow'],
  ['(cd Platform/Frontplane/Ariva.Web && find . -name "*.svelte")', ROOT, 'allow'],
  ['find . -maxdepth 1 -type f', ROOT, 'allow'],
  [`grep -rn foo Platform > ${TMP}/x`, ROOT, 'allow'],
  ['find Platform -name x 2>/dev/null', ROOT, 'allow'],
  [`TMPDIR=${TMP} find Platform -name x`, ROOT, 'allow'],
  ["python3 - <<'EOF'\nprint(1 << 3)\nEOF\necho done", ROOT, 'allow'],
  ['dotnet test Ariva.slnx --logger "trx;LogFileName=x.trx" | tail -5', ROOT, 'allow'],
  // literal text: single quotes, escapes, and heredoc bodies no shell reads
  ["git commit -m 'Harness: refuse `grep -rn` and `find .` from the root'", ROOT, 'allow'],
  ['git commit -m "Harness: refuse \\`find . -name\\` from the root"', ROOT, 'allow'],
  ["echo 'run $(find . -name x) later'", ROOT, 'allow'],
  ["python3 - <<'EOF'\nprint(\"grep -r x .\")\nprint('$(find . -name z)')\nEOF\necho done", ROOT, 'allow'],
  [`cat > ${TMP}/s.sh <<'EOF'\nfind . -name q\nEOF`, ROOT, 'allow'],
  ["git commit -m \"$(cat <<'EOF'\nARV-1: x\n\nfind . -name y; grep -r z .\nEOF\n)\"", ROOT, 'allow'],
  ["docker exec -i ariva-postgres psql -U ariva <<'SQL'\nselect 1;\nSQL", ROOT, 'allow'],
  [`tee ${TMP}/run.sh <<'EOF'\nfind . -name x\nEOF`, ROOT, 'allow'],
  ["cat <<'EOF' | node --input-type=module\nconsole.log('find . -name x')\nEOF", ROOT, 'allow']
];

// Through the hook itself: the folder rule and the root search both deny; an ordinary command passes.
const hookCases = [
  ['cat .private/notes.md', ROOT, 'deny'],
  ['grep -rn foo .', ROOT, 'deny'],
  ['git status --short', ROOT, 'allow']
];

let wrong = 0;
const report = (got, want, cwd, command) => {
  if (got === want) return;
  wrong++;
  console.log(`root-search: expected ${want}, got ${got} (cwd ${cwd.replace(ROOT, '<root>')}): ${JSON.stringify(command)}`);
};
for (const [command, cwd, want] of cases) report(rootSearch(command, cwd, ROOT) ? 'deny' : 'allow', want, cwd, command);
for (const [command, cwd, want] of hookCases) {
  const r = spawnSync(process.execPath, [path.join(HOOKS, 'guard-bash.mjs')], {
    input: JSON.stringify({ cwd, tool_input: { command } }), encoding: 'utf8', env: { ...process.env, CLAUDE_PROJECT_DIR: ROOT }
  });
  report(r.status === 0 && !/"deny"/.test(r.stdout) ? 'allow' : 'deny', want, cwd, command);
}
console.log(wrong ? `root-search self-test: ${wrong} of ${cases.length + hookCases.length} wrong` : `root-search self-test: ${cases.length + hookCases.length} cases as intended`);
process.exit(wrong ? 1 : 0);
