#!/usr/bin/env node
// Ariva security gate: static checks mapped to the CWE checklist in docs/security/cwe-controls.md.
// No dependencies. Usage:
//   node scripts/security/scan.mjs                 scan the repository, exit 1 on any error finding
//   node scripts/security/scan.mjs --changed       scan only files changed against git HEAD
//   node scripts/security/scan.mjs --self-test     prove every rule fires on fixtures/bad and stays quiet on fixtures/good
//   node scripts/security/scan.mjs --json          also print the JSON report to stdout
//   node scripts/security/scan.mjs <files...>      scan only the given files (used by the Claude Code hooks)
// Exceptions live in security/allowlist.json and need a reason, an approver and a date.
// SEC-130 (hidden characters, ARV-157) reads every text file in the scope: in a full scan, every file git tracks or would
// add, whatever its folder or type; the other rules read the mapped types outside SKIP_DIRS.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
// .private holds client material that nothing reads, not even a gate (owner decision 2026-10-09, ARV-139c).
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.svelte-kit', 'build', 'dist', 'test-results', 'playwright-report', '.verify', 'prototype', 'fixtures', '.private']);
// Every type here is read as text even when it holds a NUL byte, and the self-test needs a bad SEC-130 fixture of each.
const EXT_LANG = {
  '.cs': 'cs', '.csproj': 'msbuild', '.props': 'msbuild', '.targets': 'msbuild',
  '.ts': 'js', '.js': 'js', '.mjs': 'js', '.cjs': 'js', '.svelte': 'svelte',
  '.json': 'json', '.conf': 'nginx', '.yaml': 'yaml', '.yml': 'yaml', '.env': 'env',
  // ARV-157: text types no rule but SEC-130 reads; SEC-130 also reads any other file that is text (decodeText).
  '.md': 'text', '.sql': 'text', '.resx': 'text', '.sh': 'text', '.ps1': 'text', '.psm1': 'text', '.html': 'text', '.css': 'text',
  '.txt': 'text', '.csv': 'text', '.xml': 'text', '.svg': 'text', '.http': 'text', '.tpl': 'text', '.gotmpl': 'text', '.mts': 'text',
  '.cts': 'text', '.slnx': 'text', '.toml': 'text', '.ini': 'text', '.py': 'text', '.cmd': 'text', '.bat': 'text'
};
const SECURITY_FIXTURES = path.join(HERE, 'fixtures') + path.sep;

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
    re: /new\s+HttpClient\s*\(|\bHttpClient\s+\w+\s*=\s*new\s*\(|WebRequest\.Create\s*\(|\bHttpWebRequest\b|new\s+WebClient\s*\(/,
    msg: 'Outbound HTTP outside IHttpClientFactory and the outbound endpoint registry (OutboundClients). Use a named client, or an OutboundEndpoint registered by an administrator.' },
  // ARV-045: a handler of Ariva's own decides where a connection goes; in Backplane only the outbound transport
  // (OutboundTransport, with its checked ConnectCallback) may build one, and it is allowlisted.
  { id: 'SEC-023', cwe: ['CWE-918'], langs: ['cs'], severity: 'error', paths: [/Platform[\\/]Backplane[\\/]/],
    re: /new\s+(SocketsHttpHandler|HttpClientHandler)\b/,
    msg: 'HTTP handler built in Backplane outside the outbound transport. Outbound connections go through OutboundTransport, which checks every address it dials.' },
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

  // ARV-157: every text file of any type or folder (anyText), comment lines included (a reordering character in a comment
  // is the Trojan Source attack itself), and every path. C0 and C1 control characters but the tab and the line feed (a
  // lone carriage return or U+0085 ends a line in C#, JavaScript or YAML where this scanner sees one line, so code after a
  // comment would pass unread; a NUL byte makes git show the file as binary); bidirectional controls and the Arabic letter
  // mark; zero-width, invisible and format characters; the line and paragraph separators (a line end to JavaScript, not to
  // an editor); the Hangul fillers (letters that render as nothing, so valid in a JavaScript name); tag characters and
  // supplementary variation selectors (text hidden from a reader, agents included). Refused as a whole (scanFiles): a file
  // in UTF-16 or UTF-32, a file that is not valid UTF-8 (but a binary type whose bytes carry its signature), and a file that
  // declares another encoding. Not refused yet: a byte order mark after a file's start and the rarer format characters
  // (ARV-225).
  { id: 'SEC-130', cwe: ['CWE-94'], anyText: true, severity: 'error', raw: true,
    re: /[\x00-\x08\x0B-\x1F\x7F-\x9F\u00AD\u061C\u115F\u1160\u180E\u200B-\u200F\u2028-\u202E\u2060-\u206F\u3164\uFFA0\uFFF9-\uFFFB\u{E0000}-\u{E007F}\u{E0100}-\u{E01EF}]/u,
    msg: 'Invisible, bidirectional, control or line-separating character (Trojan Source, hidden text): the file can read differently from how it runs or what a reader is shown. Write it as an escape: \\u202E in C#, JavaScript, JSON and YAML strings, `u{202E} in PowerShell, &#x202E; in HTML, XML, .resx, SVG and Markdown, \\202E in CSS, U&\'\\202E\' in SQL.' },
  // ARV-089 (ASVS V15.3.6): prototype pollution in the web app. A key that comes from data (a zone key, an id, a language)
  // written with obj[key] = value reaches the __proto__ setter when the key is "__proto__"; Map, Object.fromEntries,
  // object literals and spreads define own properties and never do. Allowed: a string or number literal key, or a choice
  // between two string literals. Object.assign copies with the same setter, so it is refused too. A write through a computed
  // read (obj[key].name = value, obj[key][0] = value) is refused as well: with the key "__proto__" it writes Object.prototype.
  // Not seen by the line pattern: spaces before the bracket or around ++ and -- (Prettier removes them), a key split over
  // lines, destructuring targets ([a[i], a[j]] = ..., ({ x: obj[k] } = src)), for (obj[k] of ...), and a bind whose first
  // key is a literal (bind:value={rows[0][k]}) or whose key holds brackets (bind:value={notes[a[b]]}). It also flags a
  // TypeScript indexed access type (let t: T[K] = x) and an array element write by index (list[i].done = true).
  { id: 'SEC-131', cwe: ['CWE-1321', 'CWE-94'], langs: ['js', 'svelte'], severity: 'error', paths: [/Platform[\\/]Frontplane[\\/]/],
    re: /__proto__|\bObject\.assign\s*\(|\bObject\.setPrototypeOf\s*\(|\bReflect\.set\s*\(/,
    msg: 'Prototype pollution: __proto__, Object.assign, Object.setPrototypeOf or Reflect.set in the web app. Build objects with literals, spreads or Object.fromEntries, or use a Map.' },
  { id: 'SEC-132', cwe: ['CWE-1321', 'CWE-94'], langs: ['js', 'svelte'], severity: 'error', paths: [/Platform[\\/]Frontplane[\\/]/],
    re: /[\w$)\]]\[(?!\s*(?:'[^'\n]*'|"[^"\n]*"|`[^`$\n]*`|\d+)\s*\])(?!\s*[\w$.!()]+\s*\?\s*(?:'[^'\n]*'|"[^"\n]*")\s*:\s*(?:'[^'\n]*'|"[^"\n]*")\s*\])(?:[^[\]\n]|\[[^[\]\n]*\])+\](?:\s*(?:=(?![=>])|\?\?=|\|\|=|&&=|\*\*=|<<=|>>>?=|[-+*/%&|^]=)|\+\+|--)|(?:\+\+|--)[\w$.]+\[(?!\s*(?:'[^'\n]*'|"[^"\n]*"|`[^`$\n]*`|\d+)\s*\])[^\]\n]+\]|[\w$)\]]\[(?!\s*(?:'[^'\n]*'|"[^"\n]*"|`[^`$\n]*`|\d+)\s*\])(?!\s*[\w$.!()]+\s*\?\s*(?:'[^'\n]*'|"[^"\n]*")\s*:\s*(?:'[^'\n]*'|"[^"\n]*")\s*\])(?:[^[\]\n]|\[[^[\]\n]*\])+\](?:\[[^\]\n]+\]|\.[\w$]+)+\s*(?:(?:=(?![=>])|\?\?=|\|\|=|&&=|\*\*=|<<=|>>>?=|[-+*/%&|^]=)|\+\+|--)|\bbind:[\w-]+=\{\s*[\w$.]+\[(?!\s*(?:'[^'\n]*'|"[^"\n]*"|`[^`$\n]*`|\d+)\s*\])[^\]\n]+\](?:(?:\[[^\]\n]+\]|\.[\w$]+)+)?\s*\}/,
    msg: 'Prototype pollution: a write with a computed key (obj[key] = value, obj[key]++, obj[key].name = value, obj[key][0] = value or bind:value={obj[key]} and bind:value={obj[key].name}) in the web app. Use a Map (SvelteMap for reactive state, with a function binding), Object.fromEntries or a spread with the key ({ ...obj, [key]: value }).' },

  { id: 'SEC-121', cwe: ['CWE-863', 'CWE-306'], langs: ['yaml', 'docker', 'json', 'env'], severity: 'error',
    re: /(ASPNETCORE|DOTNET)_FORWARDEDHEADERS_ENABLED\s*[=:]\s*["']?true|"ForwardedHeaders_Enabled"\s*:\s*(true|"true")/i,
    msg: 'Forwarded headers enabled through the environment clears KnownProxies and KnownIPNetworks, so any client can spoof its IP and defeat per-client CIDR allowlists and rate limits. Configure trusted proxies in Security:ForwardedHeaders instead.' },
  { id: 'SEC-120', cwe: ['CWE-287', 'CWE-306'], langs: ['json'], severity: 'error', fileCheck: 'configSecrets',
    msg: 'Secret value committed in configuration outside vm-local files. Use ${placeholders} resolved from Kubernetes secrets.' }
];

// ---------------------------------------------------------------------------------------------
// structural checks (need the whole file)

// Device (ARV-022) and display player (ARV-058) attributes derive from AuthorizeAttribute with a scheme of their own.
const AUTH_ATTR = /\[(Authorize|Permission|IntegrationScope|DeviceAuthenticated|DisplayAuthenticated)\b/;

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

/** Every file under dir, mapped or not, no folder skipped (the self-test's fixtures). */
function walkAll(dir, acc = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) walkAll(path.join(dir, e.name), acc); else acc.push(path.join(dir, e.name));
  }
  return acc;
}

/**
 * The paths in `git status --porcelain -z` output: names unquoted whatever they hold, a rename or copy under its new path
 * (its old path follows as a record of its own).
 */
export function porcelainPaths(out) {
  const records = out.split('\0');
  const paths = [];
  for (let i = 0; i < records.length; i++) {
    if (records[i].length < 4) continue;
    paths.push(records[i].slice(3));
    if (/[RC]/.test(records[i].slice(0, 2))) i++;
  }
  return paths;
}

function changedFiles(root = ROOT, env = process.env) {
  try {
    // Every untracked file, not just its new folder (a folder entry is not a file and would be dropped). Any type: SEC-130
    // reads every text file (ARV-157).
    const out = execFileSync('git', ['status', '--porcelain', '-z', '--untracked-files=all'], { cwd: root, env, encoding: 'utf8' });
    return porcelainPaths(out).map(f => path.join(root, f)).filter(f => fs.existsSync(f) && fs.lstatSync(f).isFile());
  } catch { return listFiles(root); }
}

/** Every file git tracks or would add (untracked and not ignored), as git lists it. */
function gitFiles(root = ROOT, env = process.env) {
  const out = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'], { cwd: root, env, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
  return [...new Set(out.split('\0').filter(Boolean))].map(f => path.join(root, f)).filter(f => fs.existsSync(f));
}

/**
 * git's environment for a scratch repository (ARV-157): without the caller's GIT_ variables (git exports GIT_DIR and
 * GIT_INDEX_FILE to its hooks, so a hook that runs this self-test would otherwise commit into the real repository) and
 * without global or system configuration.
 */
export function isolatedGitEnv(env, repo) {
  return { ...Object.fromEntries(Object.entries(env).filter(([key]) => !/^GIT_/i.test(key))),
    GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: os.devNull, GIT_CEILING_DIRECTORIES: path.dirname(repo) };
}

/**
 * The full scan (ARV-157): the mapped files outside SKIP_DIRS for every rule, and every other file git tracks or would add,
 * whatever its folder or type, for SEC-130 alone (textOnly). Not the security fixtures (bad on purpose), symbolic links
 * (git keeps their target path, not the file) or .private.
 */
function fullScan() {
  const files = listFiles(ROOT);
  const listed = new Set(files);
  const textOnly = new Set(gitFiles().filter(f => !listed.has(f) && !f.startsWith(SECURITY_FIXTURES)
    && !path.relative(ROOT, f).split(path.sep).includes('.private') && fs.lstatSync(f).isFile()));
  return { files: [...files, ...textOnly], textOnly };
}

/**
 * An explicit or --changed scan: the security fixtures are bad on purpose and left out; any other fixtures folder holds
 * samples for its own tests, read by SEC-130 alone.
 */
function partialScan(given) {
  const files = given.filter(f => !f.startsWith(SECURITY_FIXTURES));
  return { files, textOnly: new Set(files.filter(f => f.includes(`${path.sep}fixtures${path.sep}`))) };
}

export function loadAllowlist() {
  const p = path.join(ROOT, 'security', 'allowlist.json');
  if (!fs.existsSync(p)) return [];
  return validateAllowlist(JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, '')));
}

/** Throws on an incomplete entry; returns the list. */
export function validateAllowlist(list) {
  for (const a of list) {
    if (!a.rule || !a.path || !a.reason || !a.approvedBy || !a.date) throw new Error(`Allowlist entry incomplete: ${JSON.stringify(a)}`);
    if ('line' in a && (typeof a.line !== 'string' || !a.line.trim())) throw new Error(`Allowlist entry with an empty line: ${JSON.stringify(a)}`);
    // ARV-157: an entry with "line" repeats it in "contains": the approval guard (.claude/hooks/guard-paths.mjs) keys approved
    // entries on "contains" and not yet on "line" (ARV-225), so the line cannot change unseen. A SEC-130 exception names its
    // exact line, never a whole file.
    if ('line' in a && a.contains !== a.line) throw new Error(`An allowlist entry with "line" repeats it in "contains": ${JSON.stringify(a)}`);
    if (a.rule === 'SEC-130' && a.line === undefined) throw new Error(`A SEC-130 allowlist entry names its exact line ("line" and "contains"): ${JSON.stringify(a)}`);
  }
  return list;
}

/** A line without its leading and trailing spaces and tabs; nothing else is trimmed (String.trim also takes U+2028 and U+FEFF). */
const trimSpaces = text => text.replace(/^[ \t]+|[ \t]+$/g, '');

// Returns null when not allowlisted, 'approved' when an approved entry matches, 'pending' when the entry awaits approval.
// "contains" matches a line holding that text; "line" only the line that is exactly that text (leading and trailing spaces
// and tabs aside), so nothing added to it is hidden.
function allowed(allow, rule, rel, lineText) {
  const hit = allow.find(a => a.rule === rule && rel.replace(/\\/g, '/') === a.path && (!a.contains || lineText.includes(a.contains))
    && (a.line === undefined || trimSpaces(lineText) === a.line));
  if (!hit) return null;
  return /^PENDING/i.test(hit.approvedBy) ? 'pending' : 'approved';
}

// A finding's text shows a hidden character as its escape, so a terminal, a CI log or a report never reorders or hides it.
const HIDDEN = new RegExp(`${RULES.find(r => r.id === 'SEC-130').re.source}|\\uFEFF`, 'gu');
const shown = text => text.replace(HIDDEN, c => `\\u{${c.codePointAt(0).toString(16).toUpperCase()}}`);

function record(findings, allow, r, rel, ln, raw) {
  const state = allowed(allow, r.id, rel, raw);
  if (state === 'approved') return;
  const pending = state === 'pending';
  findings.push({ rule: r.id, cwe: r.cwe, severity: pending ? 'warning' : r.severity, file: shown(rel), line: ln, text: shown(trimSpaces(raw)).slice(0, 160),
    msg: pending ? `${r.msg} Allowlisted, awaiting approval in security/allowlist.json.` : r.msg });
}

/**
 * The only files SEC-130 may skip as binary (ARV-157): a binary type whose bytes start with that type's signature, and only
 * when they are not valid UTF-8. A script given one of these names is refused, not skipped; a binary type not listed here
 * is refused until its signature is added.
 */
const at = (b, offset, ...bytes) => bytes.every((x, i) => b[offset + i] === x);
const ascii = (b, offset, text) => at(b, offset, ...Buffer.from(text, 'latin1'));
const zip = b => at(b, 0, 0x50, 0x4B, 0x03, 0x04) || at(b, 0, 0x50, 0x4B, 0x05, 0x06);
const isoMedia = b => ascii(b, 4, 'ftyp');
const BINARY_SIGNATURES = {
  '.png': b => at(b, 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), '.jpg': b => at(b, 0, 0xFF, 0xD8, 0xFF), '.jpeg': b => at(b, 0, 0xFF, 0xD8, 0xFF),
  '.gif': b => ascii(b, 0, 'GIF8'), '.webp': b => ascii(b, 0, 'RIFF') && ascii(b, 8, 'WEBP'), '.ico': b => at(b, 0, 0x00, 0x00, 0x01, 0x00),
  '.bmp': b => ascii(b, 0, 'BM'), '.avif': isoMedia, '.mp4': isoMedia, '.mov': isoMedia, '.webm': b => at(b, 0, 0x1A, 0x45, 0xDF, 0xA3),
  '.mp3': b => ascii(b, 0, 'ID3') || (b[0] === 0xFF && (b[1] & 0xE0) === 0xE0), '.ogg': b => ascii(b, 0, 'OggS'),
  '.wav': b => ascii(b, 0, 'RIFF') && ascii(b, 8, 'WAVE'), '.gz': b => at(b, 0, 0x1F, 0x8B), '.tgz': b => at(b, 0, 0x1F, 0x8B), '.zip': zip,
  '.nupkg': zip, '.jar': zip, '.7z': b => at(b, 0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C), '.pdf': b => ascii(b, 0, '%PDF-'),
  '.woff': b => ascii(b, 0, 'wOFF'), '.woff2': b => ascii(b, 0, 'wOF2'), '.ttf': b => at(b, 0, 0x00, 0x01, 0x00, 0x00), '.otf': b => ascii(b, 0, 'OTTO'),
  '.dll': b => ascii(b, 0, 'MZ'), '.exe': b => ascii(b, 0, 'MZ'), '.so': b => at(b, 0, 0x7F, 0x45, 0x4C, 0x46)
};
const binaryFile = (file, buf) => BINARY_SIGNATURES[path.extname(file).toLowerCase()]?.(buf) === true;

/**
 * A file as text (ARV-157), or null when it is binary. A UTF-16 or UTF-32 byte order mark is honoured (wide): Roslyn,
 * PowerShell and the ResX reader accept those while git shows such a file as binary, so a reviewer sees none of it, and
 * SEC-130 refuses it as a whole. A valid UTF-8 file is text whatever its type, NUL bytes included (SEC-130 refuses them).
 * Any other file is binary only when it is of a binary type with that type's signature (BINARY_SIGNATURES); otherwise it is read as UTF-8 with
 * replacements and refused as a whole (utf8 false): a shell, Python or .editorconfig reader decodes the bytes this scanner
 * cannot, and git may show the file as binary.
 */
export function decodeText(buf, binaryType = false) {
  const b = buf;
  if (b.length >= 4 && b[0] === 0xFF && b[1] === 0xFE && b[2] === 0 && b[3] === 0) return { text: utf32(b.subarray(4), true), wide: true };
  if (b.length >= 4 && b[0] === 0 && b[1] === 0 && b[2] === 0xFE && b[3] === 0xFF) return { text: utf32(b.subarray(4), false), wide: true };
  if (b.length >= 2 && b[0] === 0xFF && b[1] === 0xFE) return { text: new TextDecoder('utf-16le').decode(b.subarray(2)), wide: true };
  if (b.length >= 2 && b[0] === 0xFE && b[1] === 0xFF) return { text: new TextDecoder('utf-16be').decode(b.subarray(2)), wide: true };
  try { return { text: new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(b), wide: false, utf8: true }; }
  catch { return binaryType ? null : { text: b.toString('utf8'), wide: false, utf8: false }; }
}

/**
 * The encoding a text file declares for its own tool, when it is not UTF-8 (ARV-157): a Python coding line (PEP 263, lines
 * 1 and 2), an XML prolog, a CSS @charset or an HTML meta charset. Such a tool decodes ASCII bytes into characters this
 * scanner never sees: in UTF-7, +AAo- is a line feed, so a comment can end and code run.
 */
export function declaredEncoding(text, file = '') {
  const ext = path.extname(file).toLowerCase();
  const labels = [];
  const python = text.split(/\r?\n/, 2).join('\n').match(/^[ \t\f]*#.*?coding[:=][ \t]*([-\w.]+)/m);
  const xml = text.match(/^\uFEFF?<\?xml[^>]*\bencoding\s*=\s*["']([^"']+)/);
  const css = text.match(/^\uFEFF?@charset\s+["']([^"']+)/i);
  for (const m of [python, xml, css]) if (m) labels.push(m[1]);
  // HTML, fail closed: every charset= from the first meta tag to the end of the file, whatever the quoting, a commented-out
  // tag included. A browser trims the label and finds charset anywhere in content, its tokenizer takes a quote as a quote
  // only at a value's start, and Chromium reads the head past 1,024 bytes: no pattern for a tag follows all of that.
  const meta = text.search(/<meta[\s/>]/i);
  if (meta >= 0) for (const m of text.slice(meta).matchAll(/charset\s*=\s*["']?\s*([-\w.:]*)/gi)) labels.push(m[1]);
  // An empty label declares nothing (a browser ignores it; bundled scripts in a report write charset= before an expression).
  const other = labels.find(label => label !== '' && !/^utf[-_]?8(-sig)?$/i.test(label));
  if (other !== undefined) return other;
  // C#, fail closed: the compiler decodes every source file without a byte order mark in the code page MSBuild gives it. A
  // project file may name a code page only as <CodePage>65001</CodePage>, and a response file only as CodePage=65001 or
  // codepage:65001 once its quotes are dropped (MSBuild and the compiler drop them); any other mention (a condition, a task
  // output, a Csc attribute, a second value, a quoted name) is refused, since its value cannot be judged from the text.
  if (['.csproj', '.props', '.targets'].includes(ext) && /codepage/i.test(text.replace(/<CodePage>\s*65001\s*<\/CodePage>/g, '')))
    return 'a code page other than <CodePage>65001</CodePage>';
  if (ext === '.rsp' && /codepage/i.test(text.replace(/"/g, '').replace(/codepage\s*[:=]\s*65001(?![^;,\s])/gi, '')))
    return 'a code page other than 65001';
  return null;
}

const WIDE = 'Text file in UTF-16 or UTF-32 (a byte order mark at its start): git shows it as binary, so a reviewer sees none of it, and a false mark hides UTF-8 text from this scanner. Save it as UTF-8.';
const NOT_UTF8 = 'File that is not valid UTF-8 and not of a binary type: a tool that reads it in another encoding sees characters this scanner does not, and git may show it as binary. Save it as UTF-8.';
const DECLARED = 'File that declares an encoding other than UTF-8 (a Python coding line, an XML prolog, a CSS @charset, an HTML meta charset, or a C# code page in a project or response file): its tool decodes the bytes into characters this scanner does not see (in UTF-7, +AAo- is a line feed). Declare UTF-8.';
const IN_PATH = 'Invisible, bidirectional or control character in a file path (Trojan Source): a name can read as another file or type than it is. Rename it.';

function utf32(b, littleEndian) {
  const out = [];
  for (let i = 0; i + 3 < b.length; i += 4) {
    const cp = littleEndian ? b.readUInt32LE(i) : b.readUInt32BE(i);
    out.push(cp <= 0x10FFFF ? String.fromCodePoint(cp) : '�');
  }
  return out.join('');
}

/**
 * Scans the files: SEC-130 (anyText) on every path and every file that is text, the other rules on the mapped types not in
 * textOnly. stats.read counts the files read as text.
 */
export function scanFiles(files, { allow = [], root = ROOT, textOnly = new Set(), stats = {} } = {}) {
  const findings = [];
  const sec130 = RULES.find(r => r.id === 'SEC-130');
  stats.read ??= 0;
  for (const file of files) {
    const rel = path.relative(root, file);
    if (sec130.re.test(rel)) record(findings, allow, { ...sec130, msg: IN_PATH }, rel, 0, '');
    const lang = langOf(file);
    if (!fs.lstatSync(file).isFile()) continue;
    const bytes = fs.readFileSync(file);
    const decoded = decodeText(bytes, binaryFile(file, bytes));
    if (decoded === null) { stats.binary = (stats.binary ?? 0) + 1; continue; }
    stats.read++;
    if (decoded.wide) record(findings, allow, { ...sec130, msg: WIDE }, rel, 1, '');
    else if (!decoded.utf8) record(findings, allow, { ...sec130, msg: NOT_UTF8 }, rel, 1, '');
    const { text } = decoded;
    if (declaredEncoding(text, file)) record(findings, allow, { ...sec130, msg: DECLARED }, rel, 1, '');
    const lines = text.split(/\r?\n/);
    for (const r of RULES) {
      if (!r.anyText && (!lang || textOnly.has(file) || !r.langs.includes(lang))) continue;
      if (r.paths && !r.paths.some(p => p.test(file))) continue;
      if (r.fileCheck) {
        for (const ln of FILE_CHECKS[r.fileCheck](text, lines, file)) {
          record(findings, allow, r, rel, ln, lines[ln - 1] || '');
        }
        continue;
      }
      lines.forEach((raw, i) => {
        const l = r.raw ? raw : stripComment(raw, lang);
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

function writeReport(findings, scanned) {
  const dir = path.join(ROOT, '.verify');
  fs.mkdirSync(dir, { recursive: true });
  const list = checklist(findings);
  const report = { generatedAt: new Date().toISOString(), filesScanned: scanned, checklist: list, findings };
  fs.writeFileSync(path.join(dir, 'security-report.json'), JSON.stringify(report, null, 2));
  let md = `# Security scan\n\nFiles scanned: ${scanned}. Generated ${report.generatedAt}.\n\n| CWE | Check | Rules | Errors | Warnings | Status |\n|---|---|---|---|---|---|\n`;
  for (const c of list) md += `| ${c.cwe} | ${c.name} | ${c.rules.join(', ')} | ${c.errors} | ${c.warnings} | ${c.status} |\n`;
  if (findings.length) {
    md += `\n## Findings\n\n| Severity | Rule | CWE | Location | Detail |\n|---|---|---|---|---|\n`;
    for (const f of findings) md += `| ${f.severity} | ${f.rule} | ${f.cwe.join(' ')} | ${f.file}:${f.line} | ${f.msg} |\n`;
  }
  fs.writeFileSync(path.join(dir, 'security-report.md'), md);
  return report;
}

// ARV-157: the characters SEC-130 refuses, listed apart from its pattern so that a narrowed pattern fails the self-test;
// and characters text uses (printable ASCII, Arabic letters and marks, punctuation) that it never refuses.
const HIDDEN_CHARS = [[0x00, 0x08], [0x0B, 0x1F], [0x7F, 0x9F], [0x00AD], [0x061C], [0x115F, 0x1160], [0x180E], [0x200B, 0x200F],
  [0x2028, 0x202E], [0x2060, 0x206F], [0x3164], [0xFFA0], [0xFFF9, 0xFFFB], [0xE0000, 0xE007F], [0xE0100, 0xE01EF]];
const VISIBLE_CHARS = [[0x09], [0x20, 0x7E], [0x00A0, 0x00AC], [0x0600, 0x061B], [0x061D, 0x06FF], [0x2010, 0x2027], [0xFB50, 0xFDFF],
  [0xFE70, 0xFEFC]];

// ARV-157: files the full scan must list (an oracle apart from gitFiles): types no rule but SEC-130 reads, and folders the
// other rules skip.
const MUST_LIST = ['.gitattributes', '.github/CODEOWNERS', 'docs/design/prototype/app/access.html', 'Platform/Cloud/Ariva.K8s/tests/fixtures/bad.yaml',
  'Platform/Cloud/Ariva.K8s/tests/node_modules/yaml/README.md'];

/**
 * --changed and the full scan's git lists in a scratch repository (ARV-157): an untracked file in a new folder, a rename
 * under its new path, a changed file, an Arabic name, and an ignored file left out. Every git call runs in isolatedGitEnv
 * from the environment given, and the repository git would write to is checked before anything is written. Returns the
 * problems found.
 */
function scratchGitLists(baseEnv) {
  const problems = [];
  const fail = message => problems.push(message);
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-sec130-git-'));
  const env = isolatedGitEnv(baseEnv, repo);
  try {
    const git = (...a) => execFileSync('git', ['-c', 'user.name=Self-test', '-c', 'user.email=self-test@example.invalid', '-c', 'commit.gpgsign=false', ...a],
      { cwd: repo, env, stdio: 'pipe' });
    const put = (rel, text) => { fs.mkdirSync(path.dirname(path.join(repo, rel)), { recursive: true }); fs.writeFileSync(path.join(repo, rel), text); };
    git('init', '-q');
    const gitDir = execFileSync('git', ['rev-parse', '--absolute-git-dir'], { cwd: repo, env, encoding: 'utf8' }).trim();
    if (fs.realpathSync(gitDir) !== fs.realpathSync(path.join(repo, '.git'))) throw new Error(`git would write to ${gitDir}, not the scratch repository`);
    put('old.md', 'a\n'); put('kept.md', 'b\n'); put('.gitignore', 'ignored.txt\n');
    git('add', '.'); git('commit', '-q', '-m', 'self-test');
    git('mv', 'old.md', 'new.md');
    put('kept.md', 'b2\n'); put('folder/deep.txt', 'c\n'); put('مرحبا.md', 'd\n'); put('ignored.txt', 'e\n');
    const rels = list => list.map(f => path.relative(repo, f).split(path.sep).join('/')).sort().join('|');
    const expectChanged = ['folder/deep.txt', 'kept.md', 'new.md', 'مرحبا.md'].sort().join('|');
    const expectListed = ['.gitignore', 'folder/deep.txt', 'kept.md', 'new.md', 'مرحبا.md'].sort().join('|');
    if (rels(changedFiles(repo, env)) !== expectChanged) fail(`--changed lists ${rels(changedFiles(repo, env))}, not ${expectChanged}`);
    if (rels(gitFiles(repo, env)) !== expectListed) fail(`The full scan's git list is ${rels(gitFiles(repo, env))}, not ${expectListed}`);
  } catch (e) { fail(`The git lists could not be checked in a scratch repository: ${e.message}`); }
  finally { fs.rmSync(repo, { recursive: true, force: true }); }
  return problems;
}

/** The self-test's view of a fixture folder, as the full scan would see it: folders in SKIP_DIRS read by SEC-130 alone. */
function fixtureScan(dir) {
  const listed = new Set(listFiles(dir));
  const files = walkAll(dir);
  return { files, textOnly: new Set(files.filter(f => !listed.has(f))) };
}

function selfTest() {
  const fx = path.join(HERE, 'fixtures');
  const bad = fixtureScan(path.join(fx, 'bad'));
  const good = fixtureScan(path.join(fx, 'good'));
  const badFindings = scanFiles(bad.files, { root: fx, textOnly: bad.textOnly });
  const goodFindings = scanFiles(good.files, { root: fx, textOnly: good.textOnly });
  const fired = new Set(badFindings.map(f => f.rule));
  const missing = RULES.map(r => r.id).filter(id => !fired.has(id));
  let ok = true;
  const fail = message => { ok = false; console.error(message); };
  if (missing.length) fail(`Rules that did not fire on fixtures/bad: ${missing.join(', ')}`);
  if (goodFindings.length) { fail('False positives on fixtures/good:'); goodFindings.forEach(f => console.error(`  ${f.rule} ${f.file}:${f.line} ${f.text}`)); }

  // ARV-157: SEC-130 fires on every bad/trojan fixture by itself (so no other file stands in for it), and they cover every
  // mapped type and Dockerfile; the rest are an unmapped type, a dotfile and a file without extension, NUL bytes in a
  // mapped type and in an unmapped one, a lone carriage return and U+0085 after a comment, UTF-16 and UTF-32 files and a
  // false byte order mark, a source file that is not UTF-8, and a folder the other rules skip.
  const trojanDir = path.join(fx, 'bad', 'trojan');
  const trojanFiles = walkAll(trojanDir);
  const firedOn = new Set(badFindings.filter(f => f.rule === 'SEC-130').map(f => path.join(fx, f.file)));
  const silent = trojanFiles.filter(f => !firedOn.has(f)).map(f => path.relative(trojanDir, f));
  if (silent.length) fail(`SEC-130 did not fire on fixtures/bad/trojan: ${silent.join(', ')}`);
  const kinds = new Set(trojanFiles.map(f => path.basename(f) === 'Dockerfile' ? 'Dockerfile' : path.extname(f).toLowerCase()));
  const uncovered = [...Object.keys(EXT_LANG), 'Dockerfile'].filter(t => !kinds.has(t));
  if (uncovered.length) fail(`No SEC-130 fixture in fixtures/bad/trojan for: ${uncovered.join(', ')}`);

  const sec130 = RULES.find(r => r.id === 'SEC-130').re;
  const hex = c => `U+${c.toString(16).toUpperCase().padStart(4, '0')}`;
  const each = (ranges, test) => ranges.flatMap(([a, b = a]) => Array.from({ length: b - a + 1 }, (_, i) => a + i)).filter(test);
  const passed = each(HIDDEN_CHARS, c => !sec130.test(String.fromCodePoint(c)));
  if (passed.length) fail(`SEC-130 does not refuse: ${passed.slice(0, 20).map(hex).join(', ')}${passed.length > 20 ? ` and ${passed.length - 20} more` : ''}`);
  const refused = each(VISIBLE_CHARS, c => sec130.test(String.fromCodePoint(c)));
  if (refused.length) fail(`SEC-130 refuses visible text: ${refused.slice(0, 20).map(hex).join(', ')}`);

  // ARV-157: a finding's text and path show hidden characters as escapes.
  const unescaped = [...badFindings, ...goodFindings].filter(f => sec130.test(f.text) || sec130.test(f.file));
  if (unescaped.length) fail(`Findings that print a hidden character: ${unescaped.slice(0, 5).map(f => `${f.rule} line ${f.line}`).join(', ')}`);
  if (shown('a\uFEFFb\u202Ec') !== 'a\\u{FEFF}b\\u{202E}c') fail('A finding does not show a byte order mark or a hidden character as its escape.');

  // ARV-157: each whole-file refusal fires for its own reason on fixtures that hold nothing else: a UTF-16, UTF-32 or false
  // byte order mark, a file that is not UTF-8 (a mapped type, a script, a dotfile, a binary name without its signature), and
  // a declared encoding (a Python coding line on line 1 or 2, an XML prolog with or without a byte order mark, a CSS
  // @charset, an HTML meta placed late).
  const wholeFile = [[WIDE, ['utf16le.cs', 'utf16be.json', 'utf32le.txt', 'utf32be.yaml', 'fakebom.txt', 'wide-clean.txt', 'utf16le.anything']],
    [NOT_UTF8, ['latin1.cs', 'latin1-only-script', '.latin1rc', 'nosig.gz']],
    [DECLARED, ['utf7.py', 'coding2.py', 'declared.xml', 'bom-prolog.xml', 'charset.css', 'meta.html', 'meta-space.html', 'meta-equiv-space.html',
      'meta-comment.html', 'meta-quoted.html', 'meta-xcharset.html', 'meta-name-quote.html', 'meta-value-quote.html', 'meta-late.html',
      'codepage.csproj', 'codepage-condition.targets', 'codepage-twice.props', 'codepage-gt.targets', 'codepage-output.targets',
      'codepage-csc.targets', 'build.rsp', 'property.rsp', 'csc.rsp', 'rsp-quoted.rsp', 'rsp-quoted-name.rsp', 'rsp-split.rsp', 'rsp-twice.rsp',
      'rsp-prefix.rsp']]];
  for (const [msg, names] of wholeFile) {
    const missed = names.filter(name => !badFindings.some(x => x.rule === 'SEC-130' && x.msg === msg && x.line === 1 && path.join(fx, x.file) === path.join(trojanDir, name)));
    if (missed.length) fail(`Not refused as a whole for their own reason (${msg.slice(0, 40)}...): ${missed.join(', ')}`);
  }

  // ARV-157: a hidden character in a path, in a file name or a folder name, fires SEC-130 (scratch files outside the repository).
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-sec130-'));
  try {
    const named = path.join(scratch, 'notes\u202Etxt.yml');
    const bare = path.join(scratch, 'run\u2067me');
    const folder = path.join(scratch, 'conf\u2066ig', 'plain.yml');
    fs.mkdirSync(path.dirname(folder));
    for (const f of [named, bare, folder]) fs.writeFileSync(f, 'plain: text\n');
    const pathFindings = scanFiles([named, bare, folder], { root: scratch }).filter(f => f.rule === 'SEC-130' && f.line === 0);
    if (pathFindings.length !== 3) fail('SEC-130 does not fire on a hidden character in a file name (mapped or not) or a folder name.');
  } finally { fs.rmSync(scratch, { recursive: true, force: true }); }

  // ARV-157: the git lists in a scratch repository, from this process's environment, then from one where a git hook's
  // GIT_DIR and GIT_INDEX_FILE name a decoy repository, which must be left as it was.
  for (const problem of scratchGitLists(process.env)) fail(problem);
  const decoy = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-sec130-decoy-'));
  try {
    const dgit = (...a) => execFileSync('git', ['-c', 'user.name=Self-test', '-c', 'user.email=self-test@example.invalid', '-c', 'commit.gpgsign=false', ...a],
      { cwd: decoy, env: isolatedGitEnv(process.env, decoy), encoding: 'utf8', stdio: 'pipe' });
    dgit('init', '-q');
    fs.writeFileSync(path.join(decoy, 'important.txt'), 'keep\n');
    dgit('add', '.');
    dgit('commit', '-q', '-m', 'decoy');
    const state = () => dgit('rev-parse', 'HEAD') + fs.readFileSync(path.join(decoy, '.git', 'index')).toString('base64') + dgit('status', '--porcelain');
    const before = state();
    // As a hook sees it (GIT_DIR and GIT_INDEX_FILE; git exports no GIT_WORK_TREE to a hook of a repository with a work tree),
    // then with GIT_WORK_TREE as well.
    const hook = { ...process.env, GIT_DIR: path.join(decoy, '.git'), GIT_INDEX_FILE: path.join(decoy, '.git', 'index') };
    for (const env of [hook, { ...hook, GIT_WORK_TREE: decoy }]) {
      for (const problem of scratchGitLists(env)) fail(`With a hook's GIT_DIR: ${problem}`);
      if (state() !== before) fail('The scratch repository check wrote into the repository a git hook\'s GIT_DIR names.');
    }
  } catch (e) { fail(`The scratch repository's isolation could not be checked: ${e.message}`); }
  finally { fs.rmSync(decoy, { recursive: true, force: true }); }

  // A user's global git configuration (a hooks path whose pre-commit hook writes a marker) never reaches the scratch repository.
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-sec130-home-'));
  try {
    const hooks = path.join(home, 'hooks');
    const marker = path.join(home, 'marker');
    fs.mkdirSync(hooks);
    fs.writeFileSync(path.join(hooks, 'pre-commit'), `#!/bin/sh\necho ran > '${marker}'\n`, { mode: 0o755 });
    fs.writeFileSync(path.join(home, '.gitconfig'), `[core]\n\thooksPath = ${hooks.split(path.sep).join('/')}\n`);
    // The control: the same configuration, given to git outright, runs the hook in a throwaway repository, so the check
    // below proves something wherever it runs.
    const control = path.join(home, 'control');
    fs.mkdirSync(control);
    const controlEnv = { ...isolatedGitEnv(process.env, control), GIT_CONFIG_GLOBAL: path.join(home, '.gitconfig') };
    const cgit = (...a) => execFileSync('git', ['-c', 'user.name=Self-test', '-c', 'user.email=self-test@example.invalid', '-c', 'commit.gpgsign=false', ...a],
      { cwd: control, env: controlEnv, stdio: 'pipe' });
    cgit('init', '-q');
    fs.writeFileSync(path.join(control, 'a.md'), 'a\n');
    cgit('add', '.');
    cgit('commit', '-q', '-m', 'control');
    if (!fs.existsSync(marker)) throw new Error('the control hook did not run, so the check cannot prove anything here');
    fs.rmSync(marker);
    for (const problem of scratchGitLists({ ...process.env, HOME: home, XDG_CONFIG_HOME: home, USERPROFILE: home })) fail(`With a global git configuration: ${problem}`);
    if (fs.existsSync(marker)) fail('A global git configuration reached the scratch repository (its pre-commit hook ran).');
  } catch (e) { fail(`The scratch repository's isolation from a global configuration could not be checked: ${e.message}`); }
  finally { fs.rmSync(home, { recursive: true, force: true }); }

  // ARV-157: a SEC-130 exception without its exact line, with an empty one, or with "line" apart from "contains" is refused.
  const entry = { rule: 'SEC-130', path: 'x.sql', reason: 'r', proposedBy: 'p', approvedBy: 'PENDING: Owner', date: '2026-10-20' };
  const other = { ...entry, rule: 'SEC-020' };
  const refusedEntries = [entry, { ...entry, line: ' ', contains: ' ' }, { ...entry, line: 'a', contains: 'b' }, { ...entry, line: 'a' },
    { ...other, line: 'a' }, { ...other, line: 'a', contains: 'b' }];
  const accepted = refusedEntries.filter(e => { try { validateAllowlist([e]); return true; } catch { return false; } });
  let exactAccepted = true;
  try { validateAllowlist([{ ...entry, line: 'a', contains: 'a' }, { ...other, line: 'a', contains: 'a' }, { ...other, contains: 'a' }]); } catch { exactAccepted = false; }
  if (accepted.length || !exactAccepted) fail('The allowlist check does not hold entries with "line" to the same text in "contains", or SEC-130 entries to their exact line.');

  // ARV-157: an entry's "line" hides exactly that line, spaces and tabs aside; anything else on it fires again.
  const exact = [{ rule: 'SEC-130', path: 'x.sql', line: "AND a !~ '[\u202E]'", approvedBy: 'Owner', reason: 'r', date: '2026-10-20' }];
  const exactCases = [["  \tAND a !~ '[\u202E]' ", 'approved'], ["AND a !~ '[\u202E]' -- \u2066", null], ["AND a !~ '[\u202E]'\u2029", null],
    ["\u2028AND a !~ '[\u202E]'", null]];
  if (exactCases.some(([line, expected]) => allowed(exact, 'SEC-130', 'x.sql', line) !== expected))
    fail('An allowlist entry with "line" does not match exactly that line.');

  // ARV-157: --changed reads git's -z output (names unquoted, a rename under its new path), and an explicit or --changed
  // scan leaves the security fixtures out and reads other fixtures with SEC-130 alone.
  const porcelain = porcelainPaths(' M a.md\0R  new.cs\0old.cs\0?? docs/\u0645\u0631\u062D\u0628\u0627.md\0?? .trojanrc\0');
  if (porcelain.join('|') !== 'a.md|new.cs|docs/\u0645\u0631\u062D\u0628\u0627.md|.trojanrc') fail(`git status -z paths read wrongly: ${porcelain.join(', ')}`);
  const partial = partialScan([path.join(SECURITY_FIXTURES, 'bad', 'BadCode.cs'), path.join(ROOT, MUST_LIST[3]), path.join(ROOT, 'scripts', 'verify.mjs')]);
  if (partial.files.length !== 2 || partial.textOnly.size !== 1 || !partial.textOnly.has(path.join(ROOT, MUST_LIST[3])))
    fail('An explicit or --changed scan does not leave out the security fixtures or read other fixtures with SEC-130 alone.');

  // ARV-157: the full scan lists every file git tracks or would add (SEC-130 reads each one that is text), whatever its
  // folder or type, but for the security fixtures and symbolic links; checked against git's own list and named files.
  try {
    const listed = new Set(fullScan().files.map(f => path.relative(ROOT, f).split(path.sep).join('/')));
    const git = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'], { cwd: ROOT, maxBuffer: 256 * 1024 * 1024 })
      .toString('utf8').split('\0').filter(Boolean);
    const unlisted = git.filter(p => !listed.has(p) && !p.startsWith('scripts/security/fixtures/') && fs.existsSync(path.join(ROOT, p))
      && !fs.lstatSync(path.join(ROOT, p)).isSymbolicLink());
    if (unlisted.length) fail(`The full scan leaves out files git tracks: ${unlisted.slice(0, 10).join(', ')}`);
    const absent = MUST_LIST.filter(p => !listed.has(p));
    if (absent.length) fail(`The full scan leaves out: ${absent.join(', ')}`);
  } catch (e) { fail(`The full scan's file list could not be checked (git ls-files): ${e.message}`); }

  const cweCovered = CHECKLIST.filter(c => !RULES.some(r => r.cwe.includes(c.cwe)));
  if (cweCovered.length) fail(`Checklist items without a rule: ${cweCovered.map(c => c.cwe).join(', ')}`);
  console.log(ok ? `Self-test passed: ${RULES.length} rules fired on bad fixtures (SEC-130 on each of ${trojanFiles.length} hidden-character fixtures, every mapped type among them), 0 false positives on good fixtures, all ${CHECKLIST.length} checklist items covered.` : 'Self-test FAILED');
  return ok;
}

function main() {
  const args = process.argv.slice(2);
  if (args.includes('--self-test')) process.exit(selfTest() ? 0 : 1);
  const explicit = args.filter(a => !a.startsWith('--')).map(a => path.resolve(a)).filter(f => fs.existsSync(f));
  let files, textOnly;
  if (explicit.length || args.includes('--changed')) {
    ({ files, textOnly } = partialScan(explicit.length ? explicit : changedFiles()));
  } else {
    // Fails closed without git: SEC-130 must see every tracked file.
    ({ files, textOnly } = fullScan());
  }
  const allow = loadAllowlist();
  const stats = {};
  const findings = scanFiles(files, { allow, textOnly, stats });
  const report = writeReport(findings, stats.read);
  if (stats.binary) console.log(`${stats.binary} files of binary types with their signatures (${Object.keys(BINARY_SIGNATURES).join(' ')}) skipped as binary.`);
  const errors = findings.filter(f => f.severity === 'error');
  for (const f of findings) console.log(`${f.severity.toUpperCase()} ${f.rule} [${f.cwe.join(' ')}] ${f.file}:${f.line}\n  ${f.msg}\n  > ${f.text}`);
  for (const c of report.checklist) console.log(`${c.status}  ${c.cwe.padEnd(8)} ${c.name}`);
  console.log(`\n${stats.read} files scanned, ${errors.length} errors, ${findings.length - errors.length} warnings. Report: .verify/security-report.md`);
  if (args.includes('--json')) console.log(JSON.stringify(report));
  process.exit(errors.length ? 1 : 0);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
