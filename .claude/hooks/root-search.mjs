// Recursive searches that start at the repository root (or above it) walk the git-ignored client material folder without
// naming it (owner decision 2026-10-10, after two such searches): grep -r, rgrep, find, ls -R, git grep --no-index, and rg
// told to ignore .gitignore. Searches that start inside a project folder, plain git grep and plain rg (which honours
// .gitignore) are unaffected. This guards against accidents, not an adversary: it reads the command as a shell would split
// it (quotes, escapes, comments, subshells, substitutions, sh -c, eval, heredocs a shell reads) and never runs anything.
// Returns the reason to refuse, or null. Self-test: node .claude/hooks/root-search.test.mjs (run by verify.mjs quick).
import os from 'node:os';
import path from 'node:path';

export const ROOT_SEARCH_REASON = 'Recursive searches from the repository root walk the client material folder. Name the project folders to search (Platform, docs, wiki, scripts, backlog, ...) or use the Grep tool, which skips git-ignored folders.';

class Refused extends Error {}

export function rootSearch(cmd, startCwd, projectDir) {
  const ROOT = path.resolve(projectDir);
  let cwd = path.resolve(String(startCwd || ROOT));
  const expand = t => t.replace(/^~(?=\/|$)/, os.homedir()).replace(/\$\{?PWD\}?|\$\(pwd\)|`pwd`/g, cwd).replace(/\$\{?CLAUDE_PROJECT_DIR\}?/g, ROOT);
  const atOrAboveRoot = target => {
    let t = expand(target);
    if (/(^|\/)\.[*?[]/.test(t)) t = path.dirname(t); // .* and .[!.]* name the hidden folders of their parent
    t = path.resolve(cwd, t);
    return t === ROOT || ROOT.startsWith(t.endsWith(path.sep) ? t : t + path.sep);
  };
  const walksRoot = targets => (!targets.length ? atOrAboveRoot('.') : targets.some(atOrAboveRoot));
  const refuse = () => { throw new Refused(); };

  // One shell word is a run of escaped characters, quoted strings, substitutions and plain characters.
  const PIECE = String.raw`\\[\s\S]|"(?:\\[\s\S]|[^"\\])*"|'[^']*'|\$\((?:[^()]|\([^()]*\))*\)|` + '`[^`]*`' + String.raw`|[^\s"'` + '`' + String.raw`;|&<>()\\]`;
  const REDIR = String.raw`\d*(?:<<<|<<-?|>>|>\||<>|[<>])&?-?\d*|&>>?`;
  const TOKEN = new RegExp(String.raw`(?<comment>#[^\n]*)|(?<redir>${REDIR})|(?<sep>\|\||&&|;;|[;|&\n()])|(?<word>(?:${PIECE})+)|\s+`, 'g');
  const IS_REDIR = new RegExp(String.raw`^(?:${REDIR})$`);
  const unquote = w => w.replace(/\\([\s\S])|"((?:\\[\s\S]|[^"\\])*)"|'([^']*)'/g, (m, e, d, s) => e ?? (d !== undefined ? d.replace(/\\([$`"\\\n])/g, '$1') : s));
  // Substitutions run wherever they appear unquoted or in double quotes; single quotes and escapes keep them literal.
  const substitutions = w => [...w.replace(/\\[\s\S]|"(?:\\[\s\S]|[^"\\])*"|'[^']*'/g, m => (m[0] === '"' ? m.replace(/\\`/g, '') : ''))
    .matchAll(/\$\(((?:[^()]|\([^()]*\))*)\)|`([^`]*)`/g)].map(m => m[1] ?? m[2]);
  const scoped = fn => { const saved = cwd; fn(); cwd = saved; };

  // Flags whose value is the next word when not attached, so the value is not taken for the pattern or a path.
  const VALUE_FLAGS = { grep: /^[efmABCdD]$/, rg: /^[efgtTmABCjMrE]$/ };
  const VALUE_LONG = /^--(include|exclude|exclude-dir|exclude-from|regexp|file|max-count|context|after-context|before-context|directories|devices|label|binary-files|glob|iglob|type|type-not|type-add|replace|max-depth|encoding|ignore-file|sort|sortr|colors|pre|pre-glob|max-filesize|threads|path-separator)$/;
  const parse = (tool, args) => {
    const flags = [], operands = [];
    let pattern = false, recurse = false, endOfOptions = false;
    for (let i = 0; i < args.length; i++) {
      const w = args[i];
      if (endOfOptions || !w.startsWith('-') || w === '-') { operands.push(w); continue; }
      if (w === '--') { endOfOptions = true; continue; }
      flags.push(w);
      if (w.startsWith('--')) {
        const [name, attached] = w.split('=', 2);
        const value = attached ?? (VALUE_LONG.test(name) ? args[++i] : undefined);
        if (/^--(regexp|file)$/.test(name)) pattern = true;
        if (name === '--directories' && value === 'recurse') recurse = true;
        continue;
      }
      for (let k = 1; k < w.length; k++) {
        if (!VALUE_FLAGS[tool].test(w[k])) continue;
        const value = k === w.length - 1 ? args[++i] : w.slice(k + 1);
        if (w[k] === 'e' || w[k] === 'f') pattern = true;
        if (tool === 'grep' && w[k] === 'd' && value === 'recurse') recurse = true;
        break;
      }
    }
    return { flags, operands, pattern, recurse };
  };

  const command = (raw, depth) => {
    for (const s of raw.flatMap(substitutions)) scoped(() => check(s, depth + 1));
    // Wrappers and keywords that run the next word as the command, with their own flags and arguments; raw and words
    // stay aligned so a word's quoting is known.
    const WRAPPER = /^(sudo|doas|nice|ionice|time|xargs|setsid|nohup|env|exec|command|timeout|stdbuf|then|do|else|elif|if|while|until|!|\{|\}|-\S*|\d+[smhd]?|[A-Za-z_][A-Za-z0-9_]*=\S*)$/;
    let start = 0;
    const wrapper = w => WRAPPER.test(w) || WRAPPER.test(path.basename(w)); // PATH=/usr/bin and /usr/bin/env alike
    while (start < raw.length && wrapper(unquote(raw[start]))) start++;
    raw = raw.slice(start);
    const words = raw.map(unquote);
    const tool = path.basename(words[0] || '');
    if (tool === 'cd' || tool === 'pushd') {
      const dir = words.slice(1).find(w => !w.startsWith('-'));
      cwd = path.resolve(cwd, expand(dir || ROOT));
      return;
    }
    if (/^(ba|z|da|k)?sh$/.test(tool)) {
      const c = words.findIndex(w => /^-[a-zA-Z]*c[a-zA-Z]*$/.test(w));
      if (c > 0 && words[c + 1]) scoped(() => check(words[c + 1], depth + 1));
      const h = raw.indexOf('<<<');
      if (h > 0 && words[h + 1]) scoped(() => check(words[h + 1], depth + 1));
      return;
    }
    if (tool === 'eval') return check(words.slice(1).join(' '), depth + 1);
    if (tool === 'watch') return scoped(() => check(words.slice(1).filter(w => !/^-|^\d+$/.test(w)).join(' '), depth + 1));
    // A redirection and its target are not arguments: `grep -rn foo > out.txt` still searches the current folder.
    const args = [];
    for (let i = 1; i < words.length; i++) {
      if (!IS_REDIR.test(raw[i])) { args.push(words[i]); continue; }
      if (!/&-?\d*$/.test(raw[i])) i++;
    }
    if (/^[ef]?grep$|^rgrep$/.test(tool)) {
      const p = parse('grep', args);
      const recursive = tool === 'rgrep' || p.recurse || p.flags.some(f => /^-[a-zA-Z]*[rR]/.test(f) || /^--(dereference-)?recursive$/.test(f));
      if (recursive && walksRoot(p.pattern ? p.operands : p.operands.slice(1))) refuse();
    }
    if (tool === 'git' && args[0] === 'grep' && args.some(a => /^--(no-index|no-exclude-standard)$/.test(a))) {
      const dd = args.indexOf('--');
      if (walksRoot(dd < 0 ? [] : args.slice(dd + 1))) refuse();
    }
    if (tool === 'find') {
      const starts = [];
      for (const w of args) {
        if (/^-(H|L|P|D\S*|O\d)$/.test(w) && !starts.length) continue;
        if (/^(-|\(|!|\\\()/.test(w)) break;
        starts.push(w);
      }
      const shallow = args.some((w, i) => w === '-maxdepth' && /^[01]$/.test(args[i + 1]));
      if (!shallow && walksRoot(starts)) refuse();
    }
    if (tool === 'rg') {
      const p = parse('rg', args);
      if (p.flags.some(f => /^-[a-zA-Z]*u/.test(f) || f.startsWith('--no-ignore')) && walksRoot(p.pattern ? p.operands : p.operands.slice(1))) refuse();
    }
    if (tool === 'ls' && args.some(a => /^-[a-zA-Z]*R/.test(a) || a === '--recursive')) {
      if (walksRoot(args.filter(a => !a.startsWith('-')))) refuse();
    }
  };

  const check = (text, depth) => {
    if (depth > 4) refuse(); // fail closed on nesting no ordinary command needs
    const stack = [];
    let current = [];
    for (const m of text.matchAll(TOKEN)) {
      const g = m.groups;
      if (g.word || g.redir) { current.push(m[0]); continue; }
      if (!g.sep) continue;
      if (current.length) command(current, depth);
      current = [];
      if (g.sep === '(') stack.push(cwd);
      if (g.sep === ')' && stack.length) cwd = stack.pop();
    }
    if (current.length) command(current, depth);
  };

  try {
    // A heredoc's body is input to its command, not shell, unless a shell on that line reads it.
    const lines = cmd.split('\n');
    const kept = [];
    for (let i = 0; i < lines.length; i++) {
      kept.push(lines[i]);
      const m = lines[i].match(/(?<!<)<<(?!<)-?\s*(?:'([^'\n]+)'|"([^"\n]+)"|\\?([^\s;&|<>()'"]+))/);
      if (!m) continue;
      const delimiter = m[1] ?? m[2] ?? m[3];
      const body = [];
      let j = i + 1;
      while (j < lines.length && lines[j].replace(/^\t+/, '') !== delimiter) body.push(lines[j++]);
      if (j >= lines.length) continue; // no terminator line: a shift or quoted text such as 1<<3, not a heredoc
      if (/(^|[\s;&|(])(\S*\/)?(ba|z|da|k)?sh(\s|$)/.test(lines[i])) scoped(() => check(body.join('\n'), 1));
      i = j;
    }
    check(kept.join('\n'), 0);
    return null;
  } catch (e) {
    if (e instanceof Refused) return ROOT_SEARCH_REASON;
    throw e;
  }
}
