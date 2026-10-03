#!/usr/bin/env node
// Ariva security gate: static checks mapped to the CWE checklist in docs/security/cwe-controls.md.
// No dependencies. Usage:
//   node scripts/security/scan.mjs                 scan the repository, exit 1 on any error finding
//   node scripts/security/scan.mjs --changed       scan only files changed against git HEAD
//   node scripts/security/scan.mjs --self-test     prove every rule fires on fixtures/bad and stays quiet on fixtures/good
//   node scripts/security/scan.mjs --json          also print the JSON report to stdout
//   node scripts/security/scan.mjs <files...>      scan only the given files (used by the Claude Code hooks)
// Exceptions live in security/allowlist.json and need a reason, an approver and a date.

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.svelte-kit', 'build', 'dist', 'test-results', 'playwright-report', '.verify', 'prototype', 'fixtures']);
const EXT_LANG = {
  '.cs': 'cs', '.csproj': 'msbuild', '.props': 'msbuild', '.targets': 'msbuild',
  '.ts': 'js', '.js': 'js', '.mjs': 'js', '.cjs': 'js', '.svelte': 'svelte',
  '.json': 'json', '.conf': 'nginx', '.yaml': 'yaml', '.yml': 'yaml', '.env': 'env'
};

// The checklist the product owner asked for, with the correct CWE ids.
export const CHECKLIST = [
  { cwe: 'CWE-78', name: 'OS command injection' },
  { cwe: 'CWE-94', name: 'Code injection (listed as CWE-78 in the request; CWE-94 is the correct id)' },
  { cwe: 'CWE-918', name: 'Server-side request forgery' },
  { cwe: 'CWE-77', name: 'Command injection (listed as CWE-918 in the request; CWE-77 is the correct id)' },
  { cwe: 'CWE-862', name: 'Missing authorization' },
  { cwe: 'CWE-863', name: 'Incorrect authorization' },
  { cwe: 'CWE-306', name: 'Missing authentication for critical function' },
  { cwe: 'CWE-287', name: 'Improper authentication' },
  { cwe: 'CWE-501', name: 'Trust boundary violation' },
  { cwe: 'CWE-269', name: 'Improper privilege management' },
  { cwe: 'CWE-384', name: 'Session fixation' },
  { cwe: 'CWE-89', name: 'SQL injection' },
  { cwe: 'CWE-120', name: 'Buffer overflow (classic buffer copy)' },
  { cwe: 'CWE-79', name: 'Cross-site scripting' }
];

// Pattern rules: one regex per line of source. "langs" limits where a rule runs.
const RULES = [
  { id: 'SEC-001', cwe: ['CWE-78', 'CWE-77'], langs: ['cs'], severity: 'error',
    re: /\bProcess\.Start\s*\(|new\s+ProcessStartInfo\b|\bProcessStartInfo\s*\{/,
    msg: 'Process execution. Ariva has no feature that needs to start processes; if one ever does, route it through a reviewed wrapper with a fixed executable and an argument list, never a shell.' },
  { id: 'SEC-002', cwe: ['CWE-78', 'CWE-77'], langs: ['cs', 'js', 'svelte'], severity: 'error',
    re: /["'`](cmd(\.exe)?|\/bin\/(ba)?sh|powershell(\.exe)?|pwsh)["'`]\s*[,)]/i,
    msg: 'Shell interpreter named in code. Never build commands for a shell.' },
  { id: 'SEC-003', cwe: ['CWE-78', 'CWE-77'], langs: ['js', 'svelte'], severity: 'error',
    re: /\bshell\s*:\s*true\b|\bexecSync\s*\(|(^|[^.\w])exec\s*\(\s*[`'"]/,
    msg: 'Shell execution from Node. Use execFile or spawn with an argument array and shell false.' },
  { id: 'SEC-004', cwe: ['CWE-78', 'CWE-77', 'CWE-94'], langs: ['js', 'svelte'], severity: 'error', paths: [/Platform[\\/]Frontplane[\\/]/],
    re: /\bchild_process\b/,
    msg: 'child_process in front-end code.' },

  { id: 'SEC-010', cwe: ['CWE-94'], langs: ['cs'], severity: 'error',
    re: /\bCSharpScript\.|(using\s+|global::)Microsoft\.CodeAnalysis\.(CSharp\.)?Scripting|\bDynamicExpressionParser\b|(using\s+|global::)System\.Linq\.Dynamic\.Core|\.Where\s*\(\s*\$?"[^"]*=>|Assembly\.Load\s*\(\s*(new\s+byte|\w*[Bb]ytes\b)|AppDomain\.CurrentDomain\.Load\s*\(/,
    msg: 'Runtime code generation or dynamic LINQ from strings. Express queries as typed expressions.' },
  { id: 'SEC-011', cwe: ['CWE-94'], langs: ['cs'], severity: 'error',
    re: /Type\.GetType\s*\(\s*(request|input|dto|model|body|query|payload)\b|Activator\.CreateInstance\s*\(\s*Type\.GetType\s*\(\s*(request|input|dto|model|body|query|payload)\b/i,
    msg: 'Type resolved from request data.' },
  { id: 'SEC-012', cwe: ['CWE-94'], langs: ['cs'], severity: 'error',
    re: /TypeNameHandling\s*\.\s*(All|Auto|Objects|Arrays)|\bBinaryFormatter\b|\bNetDataContractSerializer\b|\bLosFormatter\b|\bObjectStateFormatter\b|new\s+SimpleTypeResolver/,
    msg: 'Polymorphic or binary deserialization can execute attacker-chosen types (CWE-502).' },
  { id: 'SEC-013', cwe: ['CWE-94'], langs: ['js', 'svelte'], severity: 'error',
    re: /(^|[^.\w])eval\s*\(|new\s+Function\s*\(|\bset(Timeout|Interval)\s*\(\s*['"`]/,
    msg: 'Dynamic code evaluation in JavaScript.' },
  { id: 'SEC-014', cwe: ['CWE-94'], langs: ['cs'], severity: 'warning',
    re: /Handlebars\.Compile\s*\(\s*(?!Templates\.|Resources\.|EmbeddedTemplates\.)/,
    msg: 'Template compiled from a non-embedded source. Templates edited at runtime must be sandboxed and admin-only.' },

  { id: 'SEC-020', cwe: ['CWE-918'], langs: ['cs'], severity: 'error',
    re: /new\s+HttpClient\s*\(|WebRequest\.Create\s*\(|\bHttpWebRequest\b|new\s+WebClient\s*\(/,
    msg: 'Outbound HTTP outside IHttpClientFactory. Use a named client bound to an admin-registered OutboundEndpoint.' },
  { id: 'SEC-021', cwe: ['CWE-918'], langs: ['cs'], severity: 'error',
    re: /\$@?"https?:\/\/\{|new\s+Uri\s*\(\s*(request|input|dto|model|body|query|payload)\.|BaseAddress\s*=\s*new\s+Uri\s*\(\s*(request|input|dto|model|body|query|payload)\./i,
    msg: 'Outbound URL host built from variable or request data. Hosts come only from the OutboundEndpoint registry.' },
  { id: 'SEC-022', cwe: ['CWE-918'], langs: ['js', 'svelte'], severity: 'error', paths: [/Platform[\\/]Frontplane[\\/]/],
    re: /fetch\s*\(\s*(url|target|link|href|location|params\.\w+|\$page\.url)\b/,
    msg: 'Front end fetching a caller-controlled URL. Use the Api wrapper with relative API paths.' },

  { id: 'SEC-030', cwe: ['CWE-89'], langs: ['cs'], severity: 'error',
    re: /\b(ExecuteSqlAsync|ExecuteSql|CreateSQLQuery|CreateSqlQuery|CreateQuery|FromSqlRaw|ExecuteSqlRaw|ExecuteSqlRawAsync|SqlQueryRaw)\s*(<[^>]*>)?\s*\(\s*(\$@?"|@\$"|[^,()"]*"\s*\+|\w+\s*\+)/,
    msg: 'SQL or HQL built by interpolation or concatenation. Use parameters (ExecuteSqlAsync(sql, parameters) or LINQ).' },
  { id: 'SEC-031', cwe: ['CWE-89'], langs: ['cs'], severity: 'error',
    re: /CommandText\s*=\s*(\$|@\$|[^;]*"\s*\+\s*\w)|new\s+(Npgsql|Sql)Command\s*\(\s*(\$|@\$|[^,)]*"\s*\+)|string\.Format\s*\(\s*@?"\s*(SELECT|INSERT|UPDATE|DELETE|WITH|COPY)\b/i,
    msg: 'ADO.NET command text built from strings. Use NpgsqlParameter or binary COPY with typed writers.' },

  { id: 'SEC-040', cwe: ['CWE-287'], langs: ['cs'], severity: 'error',
    re: /Validate(Lifetime|Issuer|Audience|IssuerSigningKey)\s*=\s*false|RequireSignedTokens\s*=\s*false|RequireExpirationTime\s*=\s*false|SecurityAlgorithms\.None|ServerCertificateCustomValidationCallback\s*=.*=>\s*true|DangerousAcceptAnyServerCertificateValidator/,
    msg: 'Token or certificate validation switched off.' },
  { id: 'SEC-041', cwe: ['CWE-287'], langs: ['cs'], severity: 'error',
    re: /\b\w*(Secret|Password|ApiKey|TotpCode|SecretHash|PasswordHash|KeyHash)\b\s*(==|!=)\s*(?!null\b|default\b|string\.Empty\b|"")[\w"]/i,
    msg: 'Secret compared with == or !=. Use CryptographicOperations.FixedTimeEquals on hashes.' },
  { id: 'SEC-042', cwe: ['CWE-287'], langs: ['cs'], severity: 'error', fileCheck: 'totpReplay',
    msg: 'TOTP verified without a replay guard. Record the matched time step per principal and reject reuse.' },
  { id: 'SEC-043', cwe: ['CWE-287'], langs: ['cs'], severity: 'error', paths: [/Domain[\\/]Entities[\\/]/],
    re: /public\s+(virtual\s+)?string\s+(ClientSecret|Password|ApiKey|TotpSecret|TotpSecretKey|RefreshToken)\s*\{/,
    msg: 'Credential stored in a plain string property. Store a hash (secrets, passwords, refresh tokens) or an encrypted value (TOTP seed).' },

  { id: 'SEC-050', cwe: ['CWE-862', 'CWE-306'], langs: ['cs'], severity: 'error', fileCheck: 'controllerAuth',
    msg: 'Controller action without [Authorize], [Permission] or [IntegrationScope] at class or action level.' },
  { id: 'SEC-051', cwe: ['CWE-862', 'CWE-306'], langs: ['cs'], severity: 'error', fileCheck: 'minimalApiAuth',
    msg: 'Minimal API endpoint without RequireAuthorization().' },
  { id: 'SEC-052', cwe: ['CWE-306'], langs: ['cs'], severity: 'error',
    re: /\[AllowAnonymous\]|\.AllowAnonymous\s*\(\s*\)/,
    msg: 'Anonymous access. Every anonymous endpoint needs an entry in security/allowlist.json with a reason.' },
  { id: 'SEC-053', cwe: ['CWE-862', 'CWE-306'], langs: ['cs'], severity: 'error', fileCheck: 'hubAuth',
    msg: 'SignalR hub without [Authorize] or [Permission].' },

  { id: 'SEC-060', cwe: ['CWE-863'], langs: ['cs'], severity: 'error',
    re: /\[Authorize\s*\(\s*Roles\s*=|\bUser\.IsInRole\s*\(/,
    msg: 'Role-string authorization. Use Permission policies so role changes do not silently widen access.' },
  { id: 'SEC-061', cwe: ['CWE-863'], langs: ['cs'], severity: 'error', fileCheck: 'siteScope',
    msg: 'Action takes a site or airport id from the request without an ISiteScope check in the same file.' },

  { id: 'SEC-070', cwe: ['CWE-269'], langs: ['cs'], severity: 'error',
    re: /new\s+Claim\s*\(\s*ClaimTypes\.Role\s*,\s*(request|input|dto|model|body|payload)\.|\.Roles\s*=\s*(request|input|dto|model|body|payload)\.Roles\b/i,
    msg: 'Role taken from request data. Role grants go through SvcRoleAssignment, which checks the granter outranks the role.' },

  { id: 'SEC-080', cwe: ['CWE-501'], langs: ['cs'], severity: 'error',
    re: /HttpContext\.Session\.Set|\bSession\.SetString\s*\(|HttpContext\.Items\s*\[[^\]]+\]\s*=\s*(request|input|dto|model|body|payload)\b/i,
    msg: 'Untrusted data written into a trusted store (session or HttpContext.Items).' },
  { id: 'SEC-081', cwe: ['CWE-501'], langs: ['cs'], severity: 'error', fileCheck: 'entityBinding',
    msg: 'Domain entity bound directly from the request. Bind a Create*/Update* request model and map after validation.' },

  { id: 'SEC-090', cwe: ['CWE-384'], langs: ['cs'], severity: 'error',
    re: /Request\.(Query|Form)\s*\[\s*"(session|sid|sessionid|token|access_token|refresh_token)"/i,
    msg: 'Session or token accepted from query or form.' },
  { id: 'SEC-091', cwe: ['CWE-384'], langs: ['cs'], severity: 'error',
    re: /SecurePolicy\s*=\s*CookieSecurePolicy\.(None|SameAsRequest)|HttpOnly\s*=\s*false|SameSite\s*=\s*SameSiteMode\.None/,
    msg: 'Weak cookie settings.' },
  { id: 'SEC-092', cwe: ['CWE-384'], langs: ['js', 'svelte'], severity: 'error', paths: [/Platform[\\/]Frontplane[\\/]/],
    re: /(localStorage|sessionStorage)\.setItem\s*\(\s*['"`][^'"`]*(token|session|refresh)/i,
    msg: 'Token in web storage. Keep access tokens in memory; refresh tokens in an HttpOnly, Secure, SameSite=Strict cookie.' },

  { id: 'SEC-100', cwe: ['CWE-120'], langs: ['cs'], severity: 'error',
    re: /\bunsafe\s+(\{|static|void|public|private|internal|protected|struct|class|[A-Za-z_]\w*\s*\*)|\bstackalloc\s+\w+\s*\[\s*(?!\d+\s*\])/,
    msg: 'Unsafe code or stackalloc with a non-constant size.' },
  { id: 'SEC-101', cwe: ['CWE-120'], langs: ['msbuild'], severity: 'error',
    re: /<AllowUnsafeBlocks>\s*true\s*</i,
    msg: 'AllowUnsafeBlocks enabled.' },
  { id: 'SEC-102', cwe: ['CWE-120'], langs: ['cs'], severity: 'warning',
    re: /Marshal\.(Copy|AllocHGlobal|PtrToStructure)|\[DllImport|\[LibraryImport|\bfixed\s*\(/,
    msg: 'Native interop. Needs a reviewed bounds check and a test with oversized input.' },
  { id: 'SEC-103', cwe: ['CWE-120'], langs: ['cs'], severity: 'error',
    re: /MaxRequestBodySize\s*=\s*null|MultipartBodyLengthLimit\s*=\s*(long|int)\.MaxValue|MaxReceiveMessageSize\s*=\s*null/,
    msg: 'Unbounded input size.' },

  { id: 'SEC-110', cwe: ['CWE-79'], langs: ['svelte'], severity: 'error',
    re: /\{@html\s/,
    msg: 'Raw HTML in Svelte. Render text; if HTML is unavoidable, sanitize with the approved sanitizer and allowlist the line.' },
  { id: 'SEC-111', cwe: ['CWE-79'], langs: ['js', 'svelte'], severity: 'error',
    re: /\.(innerHTML|outerHTML)\s*=|insertAdjacentHTML\s*\(|document\.write\s*\(/,
    msg: 'DOM HTML sink.' },
  { id: 'SEC-112', cwe: ['CWE-79'], langs: ['cs'], severity: 'error',
    re: /Html\.Raw\s*\(|new\s+HtmlString\s*\(|\bMarkupString\b|ContentType\s*=\s*"text\/html"/,
    msg: 'Server-side HTML output from code.' },
  { id: 'SEC-113', cwe: ['CWE-79'], langs: ['nginx'], severity: 'error', fileCheck: 'nginxHeaders',
    msg: 'Web server config without Content-Security-Policy and X-Content-Type-Options headers.' },

  { id: 'SEC-121', cwe: ['CWE-863', 'CWE-306'], langs: ['yaml', 'docker', 'json', 'env'], severity: 'error',
    re: /(ASPNETCORE|DOTNET)_FORWARDEDHEADERS_ENABLED\s*[=:]\s*["']?true|"ForwardedHeaders_Enabled"\s*:\s*(true|"true")/i,
    msg: 'Forwarded headers enabled through the environment clears KnownProxies and KnownIPNetworks, so any client can spoof its IP and defeat per-client CIDR allowlists and rate limits. Configure trusted proxies in Security:ForwardedHeaders instead.' },
  { id: 'SEC-120', cwe: ['CWE-287', 'CWE-306'], langs: ['json'], severity: 'error', fileCheck: 'configSecrets',
    msg: 'Secret value committed in configuration outside vm-local files. Use ${placeholders} resolved from Kubernetes secrets.' }
];

// ---------------------------------------------------------------------------------------------
// structural checks (need the whole file)

const AUTH_ATTR = /\[(Authorize|Permission|IntegrationScope|DeviceAuthenticated)\b/;

function controllerAuth(text, lines) {
  if (!/\[ApiController\]|:\s*(ControllerBase|Controller)\b/.test(text)) return [];
  const out = [];
  const classIdx = lines.findIndex(l => /\bclass\s+\w+/.test(l));
  const head = lines.slice(Math.max(0, classIdx - 8), classIdx + 1).join('\n');
  if (AUTH_ATTR.test(head)) return [];
  for (let i = 0; i < lines.length; i++) {
    if (/\[Http(Get|Post|Put|Delete|Patch)\b|\[Route\(/.test(lines[i]) && i > classIdx) {
      let j = i, attrs = '';
      while (j < lines.length && !/\b(public|private|internal|protected)\b[^=]*\(/.test(lines[j])) { attrs += lines[j] + '\n'; j++; }
      let k = i - 1;
      while (k > classIdx && /^\s*\[/.test(lines[k])) { attrs += lines[k] + '\n'; k--; }
      if (!AUTH_ATTR.test(attrs) && !/\[AllowAnonymous\]/.test(attrs)) out.push(i + 1);
      i = j;
    }
  }
  return out;
}

function minimalApiAuth(text, lines) {
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    if (/\.Map(Get|Post|Put|Delete|Patch|Methods)\s*\(/.test(lines[i]) || /\.MapHub\s*</.test(lines[i])) {
      let stmt = '', j = i;
      while (j < lines.length) { stmt += lines[j] + '\n'; if (/;\s*$/.test(lines[j])) break; j++; }
      if (!/\.RequireAuthorization\s*\(|\.AllowAnonymous\s*\(/.test(stmt)) out.push(i + 1);
    }
  }
  return out;
}

function hubAuth(text, lines) {
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    if (/\bclass\s+\w+[^:]*:\s*Hub(<|\b)/.test(lines[i])) {
      const head = lines.slice(Math.max(0, i - 6), i + 1).join('\n');
      // [Permission(...)] derives from AuthorizeAttribute (Ariva.Api.Common), as SEC-050 accepts on controllers.
      if (!/\[(Authorize|Permission)\b/.test(head)) out.push(i + 1);
    }
  }
  return out;
}

function totpReplay(text, lines) {
  if (!/VerifyTotp\s*\(|\.VerifyTotp\b/.test(text)) return [];
  if (/(IsTimeStepUsed|MarkTimeStepUsed|LastUsedTimeStep|TryConsumeTimeStep)/.test(text)) return [];
  return [lines.findIndex(l => /VerifyTotp/.test(l)) + 1];
}

function siteScope(text, lines) {
  if (!/\[ApiController\]|:\s*ControllerBase\b/.test(text)) return [];
  if (/ISiteScope|SiteScope\.|\[SiteScoped\]/.test(text)) return [];
  // Integration API actions: the scope handler checks the {siteCode} route token against the client's sites, and
  // SiteScopeTests (architecture test) fails any other site reference in an [IntegrationScope] action (ARV-042).
  if (/\[IntegrationScope\(/.test(text)) return [];
  const out = [];
  lines.forEach((l, i) => { if (/\b(Guid|string|int)\s+(siteId|airportId|terminalId|siteCode)\b/.test(l) && /\(/.test(l)) out.push(i + 1); });
  return out;
}

let ENTITY_NAMES = null;
function loadEntityNames() {
  if (ENTITY_NAMES) return ENTITY_NAMES;
  ENTITY_NAMES = new Set();
  const dir = path.join(ROOT, 'Platform', 'Backplane', 'Ariva.Core', 'Domain', 'Entities');
  const walk = d => { if (!fs.existsSync(d)) return; for (const f of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, f.name);
    if (f.isDirectory()) walk(p); else if (f.name.endsWith('.cs')) {
      const m = fs.readFileSync(p, 'utf8').match(/\bclass\s+(\w+)/g) || [];
      m.forEach(x => ENTITY_NAMES.add(x.replace(/class\s+/, '')));
    } } };
  walk(dir);
  return ENTITY_NAMES;
}
function entityBinding(text, lines, file) {
  if (!/\[ApiController\]|:\s*ControllerBase\b|Map(Post|Put|Patch)\s*\(/.test(text)) return [];
  const names = file.includes('fixtures') ? new Set(['Device', 'Zone']) : loadEntityNames();
  const out = [];
  lines.forEach((l, i) => {
    const m = l.match(/\[From(Body|Form|Query)\]\s+(\w+)\s+\w+/);
    if (m && names.has(m[2])) out.push(i + 1);
  });
  return out;
}

function nginxHeaders(text) {
  if (!/\bserver\s*\{/.test(text)) return [];
  const ok = /add_header\s+Content-Security-Policy/i.test(text) && /add_header\s+X-Content-Type-Options/i.test(text);
  return ok ? [] : [1];
}

function configSecrets(text, lines, file) {
  const base = path.basename(file);
  if (!/^appsettings.*\.json$/.test(base) || /vm-local/.test(base)) return [];
  const out = [];
  lines.forEach((l, i) => {
    const m = l.match(/"(Password|SecretKey|ClientSecret|ApiKey|SigningKey|TotpSecret|ConnectionString)"\s*:\s*"([^"]*)"/i);
    if (!m) return;
    const v = m[2];
    if (!v || v.includes('${')) return;
    if (/^Password$/i.test(m[1]) || /^(SecretKey|ClientSecret|ApiKey|SigningKey|TotpSecret)$/i.test(m[1])) out.push(i + 1);
    else if (/Password=(?!\$\{)[^;]+/i.test(v)) out.push(i + 1);
  });
  return out;
}

const FILE_CHECKS = { controllerAuth, minimalApiAuth, hubAuth, totpReplay, siteScope, entityBinding, nginxHeaders, configSecrets };

// ---------------------------------------------------------------------------------------------

function langOf(file) {
  const base = path.basename(file);
  if (base === 'default.conf' || base.endsWith('.conf')) return 'nginx';
  if (base === 'Dockerfile' || base.startsWith('Dockerfile.')) return 'docker';
  return EXT_LANG[path.extname(file).toLowerCase()] || null;
}

function stripComment(line, lang) {
  if (lang === 'cs' || lang === 'js' || lang === 'svelte') {
    const t = line.trimStart();
    if (t.startsWith('//') || t.startsWith('*') || t.startsWith('/*')) return '';
  }
  if (lang === 'nginx' && line.trimStart().startsWith('#')) return '';
  return line;
}

function listFiles(dir, acc = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) { if (!SKIP_DIRS.has(e.name)) listFiles(path.join(dir, e.name), acc); }
    else if (langOf(e.name)) acc.push(path.join(dir, e.name));
  }
  return acc;
}

function changedFiles() {
  try {
    const out = execFileSync('git', ['status', '--porcelain'], { cwd: ROOT, encoding: 'utf8' });
    return out.split(/\r?\n/).filter(Boolean).map(l => l.slice(3).trim().replace(/^"|"$/g, ''))
      .map(f => path.join(ROOT, f)).filter(f => fs.existsSync(f) && fs.statSync(f).isFile() && langOf(f));
  } catch { return listFiles(ROOT); }
}

export function loadAllowlist() {
  const p = path.join(ROOT, 'security', 'allowlist.json');
  if (!fs.existsSync(p)) return [];
  const list = JSON.parse(fs.readFileSync(p, 'utf8').replace(/^﻿/, ''));
  for (const a of list) {
    if (!a.rule || !a.path || !a.reason || !a.approvedBy || !a.date) throw new Error(`Allowlist entry incomplete: ${JSON.stringify(a)}`);
  }
  return list;
}

// Returns null when not allowlisted, 'approved' when an approved entry matches, 'pending' when the entry awaits approval.
function allowed(allow, rule, rel, lineText) {
  const hit = allow.find(a => a.rule === rule && rel.replace(/\\/g, '/') === a.path && (!a.contains || lineText.includes(a.contains)));
  if (!hit) return null;
  return /^PENDING/i.test(hit.approvedBy) ? 'pending' : 'approved';
}

function record(findings, allow, r, rel, ln, raw) {
  const state = allowed(allow, r.id, rel, raw);
  if (state === 'approved') return;
  const pending = state === 'pending';
  findings.push({ rule: r.id, cwe: r.cwe, severity: pending ? 'warning' : r.severity, file: rel, line: ln, text: raw.trim().slice(0, 160),
    msg: pending ? `${r.msg} Allowlisted, awaiting approval in security/allowlist.json.` : r.msg });
}

export function scanFiles(files, { allow = [], root = ROOT } = {}) {
  const findings = [];
  for (const file of files) {
    const lang = langOf(file);
    if (!lang) continue;
    const text = fs.readFileSync(file, 'utf8');
    const lines = text.split(/\r?\n/);
    const rel = path.relative(root, file);
    for (const r of RULES) {
      if (!r.langs.includes(lang)) continue;
      if (r.paths && !r.paths.some(p => p.test(file))) continue;
      if (r.fileCheck) {
        for (const ln of FILE_CHECKS[r.fileCheck](text, lines, file)) {
          record(findings, allow, r, rel, ln, lines[ln - 1] || '');
        }
        continue;
      }
      lines.forEach((raw, i) => {
        const l = stripComment(raw, lang);
        if (l && r.re.test(l)) record(findings, allow, r, rel, i + 1, raw);
      });
    }
  }
  return findings;
}

function checklist(findings) {
  return CHECKLIST.map(c => {
    const rules = RULES.filter(r => r.cwe.includes(c.cwe)).map(r => r.id);
    const hits = findings.filter(f => f.cwe.includes(c.cwe));
    const errors = hits.filter(f => f.severity === 'error').length;
    return { ...c, rules, errors, warnings: hits.length - errors, status: errors ? 'FAIL' : 'PASS' };
  });
}

function writeReport(findings, files) {
  const dir = path.join(ROOT, '.verify');
  fs.mkdirSync(dir, { recursive: true });
  const list = checklist(findings);
  const report = { generatedAt: new Date().toISOString(), filesScanned: files.length, checklist: list, findings };
  fs.writeFileSync(path.join(dir, 'security-report.json'), JSON.stringify(report, null, 2));
  let md = `# Security scan\n\nFiles scanned: ${files.length}. Generated ${report.generatedAt}.\n\n| CWE | Check | Rules | Errors | Warnings | Status |\n|---|---|---|---|---|---|\n`;
  for (const c of list) md += `| ${c.cwe} | ${c.name} | ${c.rules.join(', ')} | ${c.errors} | ${c.warnings} | ${c.status} |\n`;
  if (findings.length) {
    md += `\n## Findings\n\n| Severity | Rule | CWE | Location | Detail |\n|---|---|---|---|---|\n`;
    for (const f of findings) md += `| ${f.severity} | ${f.rule} | ${f.cwe.join(' ')} | ${f.file}:${f.line} | ${f.msg} |\n`;
  }
  fs.writeFileSync(path.join(dir, 'security-report.md'), md);
  return report;
}

function selfTest() {
  const fx = path.join(HERE, 'fixtures');
  const bad = listFiles(path.join(fx, 'bad'));
  const good = listFiles(path.join(fx, 'good'));
  const badFindings = scanFiles(bad, { root: fx });
  const goodFindings = scanFiles(good, { root: fx });
  const fired = new Set(badFindings.map(f => f.rule));
  const missing = RULES.map(r => r.id).filter(id => !fired.has(id));
  let ok = true;
  if (missing.length) { ok = false; console.error(`Rules that did not fire on fixtures/bad: ${missing.join(', ')}`); }
  if (goodFindings.length) { ok = false; console.error('False positives on fixtures/good:'); goodFindings.forEach(f => console.error(`  ${f.rule} ${f.file}:${f.line} ${f.text}`)); }
  const cweCovered = CHECKLIST.filter(c => !RULES.some(r => r.cwe.includes(c.cwe)));
  if (cweCovered.length) { ok = false; console.error(`Checklist items without a rule: ${cweCovered.map(c => c.cwe).join(', ')}`); }
  console.log(ok ? `Self-test passed: ${RULES.length} rules fired on bad fixtures, 0 false positives on good fixtures, all ${CHECKLIST.length} checklist items covered.` : 'Self-test FAILED');
  return ok;
}

function main() {
  const args = process.argv.slice(2);
  if (args.includes('--self-test')) process.exit(selfTest() ? 0 : 1);
  const explicit = args.filter(a => !a.startsWith('--')).map(a => path.resolve(a)).filter(f => fs.existsSync(f));
  const files = explicit.length ? explicit : args.includes('--changed') ? changedFiles() : listFiles(ROOT);
  const allow = loadAllowlist();
  const findings = scanFiles(files.filter(f => !f.includes(`${path.sep}fixtures${path.sep}`)), { allow });
  const report = writeReport(findings, files);
  const errors = findings.filter(f => f.severity === 'error');
  for (const f of findings) console.log(`${f.severity.toUpperCase()} ${f.rule} [${f.cwe.join(' ')}] ${f.file}:${f.line}\n  ${f.msg}\n  > ${f.text}`);
  for (const c of report.checklist) console.log(`${c.status}  ${c.cwe.padEnd(8)} ${c.name}`);
  console.log(`\n${files.length} files scanned, ${errors.length} errors, ${findings.length - errors.length} warnings. Report: .verify/security-report.md`);
  if (args.includes('--json')) console.log(JSON.stringify(report));
  process.exit(errors.length ? 1 : 0);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
