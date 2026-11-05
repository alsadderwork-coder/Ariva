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
// add, whatever its folder or type; the other rules read the mapped types outside SKIP_DIRS that git tracks or would add
// (ARV-225: no ignored output). A tracked symbolic link and a listed name that is not valid UTF-8 fail SEC-130 too.

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
    re: /\b(ExecuteSqlAsync|ExecuteSql|ReadSqlAsync|CreateSQLQuery|CreateSqlQuery|CreateQuery|FromSqlRaw|ExecuteSqlRaw|ExecuteSqlRawAsync|SqlQueryRaw)\s*(<[^>]*>)?\s*\(\s*(\$@?"|@\$"|[^,()"]*"\s*\+|\w+\s*\+)/,
    msg: 'SQL or HQL built by interpolation or concatenation. Use parameters (ExecuteSqlAsync(sql, parameters), ReadSqlAsync(sql, parameters, map) or LINQ).' },
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
  // declares another encoding. ARV-225: a byte order mark anywhere but a file's first character (an invisible zero-width
  // no-break space), the rarer format characters (the combining grapheme joiner, the Khmer inherent vowels, the Mongolian
  // variation selectors, the shorthand format controls, the musical symbol format controls), the variation selectors (emoji
  // text writes U+FE0F as an escape), the Egyptian hieroglyph format controls, and the noncharacters (U+FDD0 to U+FDEF, and
  // the last two code points of every plane). Also refused (scanFiles): a tracked symbolic
  // link, a listed name that is not valid UTF-8, a file git converts at checkout (a filter or a working-tree-encoding other
  // than UTF-8, read by git from the index, and the same written in a .gitattributes file), and a PowerShell file that is
  // neither ASCII nor marked, or a batch file that is not ASCII. ARV-225b: the further declared encodings (an .editorconfig
  // charset, an Emacs or Vim coding line, a SQL client encoding, a batch console code page), code pages set in any project
  // file, outside one (an environment variable, a build command line) or with character references, escapes or evaluated
  // names (and an evaluated check in Directory.Build.props), a leading byte order mark in a type whose tool keeps it, a
  // gitlink, an untracked repository, and a link checked out as a file.
  { id: 'SEC-130', cwe: ['CWE-94'], anyText: true, severity: 'error', raw: true,
    re: /[\x00-\x08\x0B-\x1F\x7F-\x9F\u00AD\u034F\u061C\u115F\u1160\u17B4\u17B5\u180B-\u180F\u200B-\u200F\u2028-\u202E\u2060-\u206F\u3164\uFE00-\uFE0F\uFEFF\uFFA0\uFDD0-\uFDEF\uFFF9-\uFFFB\uFFFE\uFFFF\u{1BCA0}-\u{1BCA3}\u{1D173}-\u{1D17A}\u{13430}-\u{1343F}\u{E0000}-\u{E007F}\u{1FFFE}\u{1FFFF}\u{2FFFE}\u{2FFFF}\u{3FFFE}\u{3FFFF}\u{4FFFE}\u{4FFFF}\u{5FFFE}\u{5FFFF}\u{6FFFE}\u{6FFFF}\u{7FFFE}\u{7FFFF}\u{8FFFE}\u{8FFFF}\u{9FFFE}\u{9FFFF}\u{AFFFE}\u{AFFFF}\u{BFFFE}\u{BFFFF}\u{CFFFE}\u{CFFFF}\u{DFFFE}\u{DFFFF}\u{EFFFE}\u{EFFFF}\u{FFFFE}\u{FFFFF}\u{10FFFE}\u{10FFFF}\u{E0100}-\u{E01EF}]/u,
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
export function porcelainPaths(out, invalid = []) {
  // ARV-225: read as bytes, so a name that is not valid UTF-8 is reported (invalid), not dropped, and a rename's old path is
  // still skipped after its new one whatever either name holds (the two status letters are ASCII).
  const records = nulRecords(Buffer.isBuffer(out) ? out : Buffer.from(out, 'utf8'));
  const paths = [];
  for (let i = 0; i < records.length; i++) {
    if (records[i].length < 4) continue;
    const name = decodeName(records[i].subarray(3), invalid);
    if (name !== null) paths.push(name);
    if (/[RC]/.test(records[i].subarray(0, 2).toString('latin1'))) i++;
  }
  return paths;
}

/** A path that exists as a file or a link (a link's target need not exist: fs.existsSync follows it). */
const present = f => { try { fs.lstatSync(f); return true; } catch { return false; } };

/** A name's bytes with each byte that is not printable ASCII (or a backslash) shown as \x{HH}, for a name that is not valid UTF-8. */
export const escapedName = bytes => [...bytes].map(b => (b >= 0x20 && b < 0x7F && b !== 0x5C ? String.fromCharCode(b) : `\\x{${b.toString(16).toUpperCase().padStart(2, '0')}}`)).join('');

/** git's -z output split into its records, as bytes. */
const nulRecords = buf => {
  const records = [];
  let start = 0;
  for (let i = 0; i <= buf.length; i++) {
    if (i < buf.length && buf[i] !== 0) continue;
    if (i > start) records.push(buf.subarray(start, i));
    start = i + 1;
  }
  return records;
};

/**
 * A name from git's -z output (ARV-225), decoded as UTF-8 with any byte order mark kept, or null when it is not valid UTF-8:
 * then it is added to invalid, shown with escapes (git keeps a name's bytes, and a decoder with replacements would turn it
 * into a path that does not exist, dropped without a word).
 */
const decodeName = (bytes, invalid) => {
  try { return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes); }
  catch { invalid.push(escapedName(bytes)); return null; }
};

function changedFiles(root = ROOT, env = process.env, invalid = []) {
  let out;
  try {
    // Every untracked file, not just its new folder (a folder entry is not a file and would be dropped). Any type: SEC-130
    // reads every text file (ARV-157).
    out = execFileSync('git', ['status', '--porcelain', '-z', '--untracked-files=all'], { cwd: root, env, maxBuffer: 256 * 1024 * 1024 });
  } catch { return listFiles(root); }
  return porcelainPaths(out, invalid).map(f => path.join(root, f))
    .filter(f => present(f) && (fs.lstatSync(f).isFile() || fs.lstatSync(f).isSymbolicLink()));
}

/** Every file git tracks or would add (untracked and not ignored), as git lists it; a name that is not valid UTF-8 goes to invalid. */
function gitFiles(root = ROOT, env = process.env, invalid = []) {
  const out = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'], { cwd: root, env, maxBuffer: 256 * 1024 * 1024 });
  return [...new Set(nulRecords(out).map(r => decodeName(r, invalid)).filter(r => r !== null))].map(f => path.join(root, f)).filter(present);
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
 * whatever its folder or type, for SEC-130 alone (textOnly). Not the security fixtures (bad on purpose) or .private. ARV-225:
 * only files git tracks or would add, so ignored output (a worktree, test results) is not read; a tracked symbolic link is
 * listed, for SEC-130 to report (git keeps its target path, not a file); a name that is not valid UTF-8 is in invalid.
 */
function fullScan(root = ROOT, env = process.env) {
  const invalid = [];
  const keep = f => !f.startsWith(SECURITY_FIXTURES) && !path.relative(root, f).split(path.sep).includes('.private');
  const tracked = gitFiles(root, env, invalid).filter(f => keep(f) && (fs.lstatSync(f).isFile() || fs.lstatSync(f).isSymbolicLink()));
  // ARV-225b: the mapped set from git's own list (a mapped type, in no folder the other rules skip), not from a second walk of
  // the disk matched by exact path, so a name a case-insensitive or normalising file system spells otherwise is still mapped.
  const files = tracked.filter(f => langOf(f) && !path.relative(root, path.dirname(f)).split(path.sep).some(d => SKIP_DIRS.has(d)));
  const listed = new Set(files);
  const textOnly = new Set(tracked.filter(f => !listed.has(f)));
  const all = [...files, ...textOnly];
  const { gitlinks, links, nested } = gitSpecials(root, env);
  return { files: all, textOnly, invalid, converted: checkoutConversions(all, root, env), gitlinks: gitlinks.filter(keep), nested: nested.filter(keep), links };
}

/**
 * ARV-225b: what git's lists hold that is not a file here: a gitlink (mode 160000, a submodule's commit), a link by its mode
 * (120000; a checkout with core.symlinks=false writes it as a file holding its target, which lstat calls a file), and an
 * untracked repository (git lists its folder, with a trailing slash, not its files). Read from the index and the untracked
 * list. The scan reads the working tree, as it is built and run here; CI checks out the commit, so what it reads there is the
 * blob reviewed (a file deleted or changed after staging is read as it is on disk, which is what this checkout runs).
 */
function gitSpecials(root = ROOT, env = process.env) {
  const staged = execFileSync('git', ['ls-files', '-z', '-s'], { cwd: root, env, maxBuffer: 256 * 1024 * 1024 });
  const gitlinks = [];
  const links = new Set();
  for (const r of nulRecords(staged)) {
    const tab = r.indexOf(0x09);
    const name = tab < 0 ? null : decodeName(r.subarray(tab + 1), []);
    if (name === null) continue;
    const mode = r.subarray(0, 6).toString('latin1');
    if (mode === '160000') gitlinks.push(path.join(root, name));
    else if (mode === '120000') links.add(path.join(root, name));
  }
  const others = execFileSync('git', ['ls-files', '-z', '--others', '--exclude-standard'], { cwd: root, env, maxBuffer: 256 * 1024 * 1024 });
  const nested = nulRecords(others).map(r => decodeName(r, [])).filter(n => n !== null && n.endsWith('/')).map(n => path.join(root, n));
  return { gitlinks: [...new Set(gitlinks)], links, nested };
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
 * scanner never sees: in UTF-7, +AAo- is a line feed, so a comment can end and code run. ARV-225b: also an .editorconfig
 * charset, an Emacs coding line (in any comment of the first two lines, or a Local Variables block at the end) or a Vim
 * modeline's fileencoding, a SQL client encoding (SET client_encoding, SET NAMES, psql's \encoding), a batch file's console
 * code page (chcp, mode con cp select), and a C# code page set in any project file, by an environment variable or on a build
 * command line, or written with character references, escapes or evaluated names.
 */
export function declaredEncoding(text, file = '') {
  const ext = path.extname(file).toLowerCase();
  const base = path.basename(file);
  const labels = [];
  const head = text.split(/\r?\n/, 2).join('\n');
  const python = head.match(/^[ \t\f]*#.*?coding[:=][ \t]*([-\w.]+)/m);
  const xml = text.match(/^\uFEFF?<\?xml[^>]*\bencoding\s*=\s*["']([^"']+)/);
  const css = text.match(/^\uFEFF?@charset\s+["']([^"']+)/i);
  // ARV-225b: Emacs reads a coding: in a -*- line (the first, or the second after a shebang) whatever comment holds it, and a
  // PEP 263 style coding line in a // or /* */ comment is read as one; its Local Variables block in the last 3,000 characters;
  // Vim a modeline's fileencoding in the first or last five lines.
  const emacsLine = head.match(/-\*-[^\n]*?\bcoding:[ \t]*([-\w.]+)/i);
  const slashCoding = head.match(/^[ \t]*(?:\/\/|\/\*).*?coding[:=][ \t]*([-\w.]+)/m);
  const tail = text.slice(-3000);
  const local = /Local Variables:/i.test(tail) ? tail.slice(tail.search(/Local Variables:/i)).match(/^.*?\bcoding:[ \t]*([-\w.]+)/im) : null;
  const edges = text.split(/\r?\n/);
  const modeline = [...edges.slice(0, 5), ...edges.slice(-5)].join('\n').match(/\b(?:vim?|ex):.*?\b(?:fenc|fileencoding)=([-\w.]+)/i);
  for (const m of [python, xml, css, emacsLine, slashCoding, local, modeline]) if (m) labels.push(m[1].replace(/-+$/, ''));
  // HTML, fail closed: every charset= from the first meta tag to the end of the file, whatever the quoting, a commented-out
  // tag included. A browser trims the label and finds charset anywhere in content, its tokenizer takes a quote as a quote
  // only at a value's start, and Chromium reads the head past 1,024 bytes: no pattern for a tag follows all of that.
  // ARV-225b: character references decoded first (a browser decodes them in an attribute's value), and one this scanner does
  // not decode refused inside a meta tag.
  const meta = text.search(/<meta[\s/>]/i);
  if (meta >= 0) {
    const region = decodeHtmlReferences(text.slice(meta));
    for (const m of region.matchAll(/charset\s*=\s*["']?\s*([-\w.:]*)/gi)) labels.push(m[1]);
    // A reference left after a charset, or in a meta tag up to the next < (a > is text inside a quoted value), is refused.
    if (/charset\s*=?\s*["']?\s*&/i.test(region) || /<meta\b[^<]*&[#A-Za-z]/i.test(region)) return 'a character reference this scanner does not decode in a meta tag';
  }
  // An empty label declares nothing (a browser ignores it; bundled scripts in a report write charset= before an expression).
  const other = labels.find(label => label !== '' && !/^utf[-_]?8(-sig|-unix|-dos|-mac|-emacs(-unix|-dos|-mac)?|-with-signature(-unix|-dos|-mac)?)?$/i.test(label));
  if (other !== undefined) return other;
  // ARV-225b: an .editorconfig charset (its keys are read without case): utf-8 or utf-8-bom only.
  if (base === '.editorconfig') {
    const charset = [...text.matchAll(/^[ \t]*charset[ \t]*[=:][ \t]*(.*?)[ \t]*$/gim)].map(m => m[1].toLowerCase()).find(v => v !== 'utf-8' && v !== 'utf-8-bom');
    if (charset !== undefined) return `an .editorconfig charset of ${charset}`;
  }
  // ARV-225b: SQL, fail closed: a client encoding named only as SET client_encoding to UTF8 or psql's \encoding UTF8, on a line
  // of its own; any other mention (SET NAMES, set_config, PGCLIENTENCODING, a value of another encoding) is refused.
  if (ext === '.sql') {
    // psql's meta-commands end at their line; the rest is read with its comments out and its whitespace folded, newlines
    // included, so a comment or a line break between SET and NAMES hides nothing. set_config and a U&" identifier (which can
    // spell any name) are refused outright: no script needs them.
    const lines = text.split(/\r?\n/);
    if (lines.some(l => /\\encoding\b/i.test(l) && !/^\s*\\encoding\s+'?utf-?8'?\s*$/i.test(l))) return 'a client encoding other than UTF8';
    const sql = lines.filter(l => !/^\s*\\/.test(l)).join('\n').replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/--[^\n]*/g, ' ').replace(/\s+/g, ' ');
    if (/\bset_config\b|\bU&"/i.test(sql)) return 'a client encoding set by set_config or a U&" identifier';
    for (const statement of sql.split(';'))
      if (/client_encoding|pgclientencoding|\bset (?:session |local )?names\b/i.test(statement) && !/^\s*set (?:session |local )?client_encoding ?(?:=|to) ?'?utf-?8'?\s*$/i.test(statement))
        return 'a client encoding other than UTF8';
  }
  // ARV-225b: batch, fail closed: chcp 65001 alone on its line (its output sent to nul or not); any other chcp, and mode's code
  // page selection, are refused. The batch rule's ASCII-only text assumes the console's code page is ASCII-compatible, which
  // every code page chcp can select is (cmd.exe refuses UTF-16 and EBCDIC ones as a console code page).
  if (BATCH.has(ext)) {
    const unescaped = text.replace(/\^/g, '');
    if (unescaped.split(/\r?\n/).some(l => /\bchcp\b/i.test(l) && !/^\s*@?chcp(?:\.com)?\s+65001\s*(?:>\s*nul(?:\s+2>&1)?\s*)?$/i.test(l)) || /\bmode\b[^\n]*\bcp\b[^\n]*\bselect\b/i.test(unescaped))
      return 'a console code page other than 65001';
  }
  // C#, fail closed: the compiler decodes every source file without a byte order mark in the code page MSBuild gives it. A
  // project file may name a code page only as <CodePage>65001</CodePage>, and a response file only as CodePage=65001 or
  // codepage:65001 once its quotes are dropped (MSBuild and the compiler drop them); any other mention (a condition, a task
  // output, a Csc attribute, a second value, a quoted name) is refused, since its value cannot be judged from the text.
  // ARV-225b: every project type MSBuild imports (.proj, .projitems, .csproj.user and the like), read as MSBuild reads it (XML
  // character references and %XX escapes decoded; a document type or an entity refused, since it can spell any name); a
  // compiler response file named in a project, an import of a file of another type, and a property named by evaluation (a
  // PropertyName that is not a plain name, Properties or AdditionalProperties given to a project) are refused too. The one
  // evaluated check of Directory.Build.props is the exception (its exact conditions).
  if (MSBUILD_TYPES.has(ext) || isMsbuildProject(text)) {
    const decoded = decodeMsbuild(text);
    if (/<!DOCTYPE|<!ENTITY/i.test(decoded)) return 'a document type or an entity in a project file';
    const rest = EVALUATED_CHECKS.reduce((t, check) => t.split(check).join(''), decoded).replace(/<CodePage>\s*65001\s*<\/CodePage>/g, '');
    if (CODEPAGE_WORD.test(rest)) return 'a code page other than <CodePage>65001</CodePage>';
    if (/compilerresponsefile|responsefiles/i.test(rest)) return 'a compiler response file named in a project file';
    // An Import read with XML's quoting (a > inside a quoted Condition is text), every part of a ; list judged.
    for (const m of decoded.matchAll(/<Import\b((?:[^>"']|"[^"]*"|'[^']*')*)\/?>/gi)) {
      const project = /\bProject\s*=\s*(?:"([^"]*)"|'([^']*)')/i.exec(m[1]);
      if (!project) return 'an import whose project this scanner cannot read';
      for (const part of (project[1] ?? project[2]).split(';').map(p => p.trim()).filter(Boolean))
        if (!MSBUILD_TYPES.has(path.extname(part.replace(/\$\([^)]*\)$/, 'x.unknown')).toLowerCase())) return `an import of ${part}, a file of a type this scanner does not read as a project`;
    }
    for (const m of decoded.matchAll(/\bPropertyName\s*=\s*(["'])(.*?)\1/gi)) if (!/^[A-Za-z_][\w.-]*$/.test(m[2])) return 'a property named by evaluation (PropertyName)';
    if (/\b(?:Additional)?Properties\s*=\s*["']/i.test(decoded)) return 'properties given to another project (Properties, AdditionalProperties)';
  }
  if (ext === '.rsp' && /codepage/i.test(text.replace(/"/g, '').replace(/codepage\s*[:=]\s*65001(?![^;,\s])/gi, '')))
    return 'a code page other than 65001';
  // ARV-225b: a code page set for the compiler outside a project file (MSBuild takes every environment variable as a property):
  // an environment variable in a Dockerfile, a workflow, a script or a settings file, or a build command line (-p:, /p:,
  // -property:), fail closed: every mention of the word in any other text file is followed by 65001 (after a quote, :, = or a
  // space). Not the documentation (Markdown, docs, wiki, backlog) or this scanner, whose rules name it.
  const rel = path.relative(ROOT, file).split(path.sep).join('/');
  const documentation = ext === '.md' || /^(?:docs|wiki|backlog)\//.test(rel) || SELF.has(rel);
  // ARV-225b: MSBuild's extension points (a file imported before or after the SDK's own, Directory.Build.props and its
  // imports switched off or moved, files imported by wildcard) and the checks' own names (a target of the same name replaces
  // a check) are refused in every text file but the documentation, Directory.Build.props and the scanner with its build probe.
  if (!documentation && rel !== 'Directory.Build.props') {
    const point = text.match(EXTENSION_POINTS);
    if (point) return `MSBuild's ${point[0]}, which can import a project file this scanner does not read or switch off Directory.Build.props`;
  }
  if (!MSBUILD_TYPES.has(ext) && ext !== '.rsp' && !documentation) {
    for (const m of text.matchAll(new RegExp(CODEPAGE_WORD.source, 'gi')))
      if (!/^["']?\s*(?:[:=]|\s)\s*["']?65001(?!\d)/.test(text.slice(m.index + m[0].length))) return 'a code page set outside a project file (an environment variable or a build command line) other than 65001';
  }
  return null;
}

/** The scanner and its build probe, which name what they refuse. */
const SELF = new Set(['scripts/security/scan.mjs', 'scripts/security/source-encoding-check.mjs']);

/** ARV-225b: MSBuild's extension points and the source encoding checks' own names (see declaredEncoding). */
const EXTENSION_POINTS = /\b(?:Custom(?:Before|After)Microsoft\w*|DirectoryBuild(?:Props|Targets)Path|ImportDirectoryBuild(?:Props|Targets)|ImportDirectoryPackagesProps|DirectoryPackagesPropsPath|ImportByWildcard\w*|ImportUserLocationsByWildcard\w*|ImportProjectExtension(?:Props|Targets)|MSBuildProjectExtensionsPath|ArivaSourceEncodingCheck|ArivaCompilerArgumentsCheck|ArivaRefusedCompilerArgument|ProvideCommandLineArgs)\b/i;

/** True when a text file is an MSBuild project by its content: its first element is Project, whatever its name (ARV-225b). */
const isMsbuildProject = (text) => /^\uFEFF?\s*(?:<\?xml\b[^>]*\?>\s*)?(?:<!--[\s\S]*?-->\s*)*<Project[\s>/]/.test(text);

/** The word a code page property, variable or switch is named by, not part of another word (StatusCodePages). */
const CODEPAGE_WORD = /(?<![A-Za-z0-9_])codepage(?![A-Za-z0-9_])/i;

/** The project types MSBuild imports or evaluates. */
const MSBUILD_TYPES = new Set(['.csproj', '.vbproj', '.fsproj', '.props', '.targets', '.proj', '.projitems', '.shproj', '.user']);

/**
 * ARV-225b: the evaluated check in Directory.Build.props, which names the code page and the compiler response file to refuse
 * them: these exact conditions are the only mentions a project file may hold besides <CodePage>65001</CodePage>.
 */
const EVALUATED_CHECKS = [`Condition="'$(CodePage)' != '' And '$(CodePage)' != '65001'"`, `Condition="'$(CompilerResponseFile)' != ''"`,
  `Condition="$([System.String]::Copy('%(Identity)').ToLowerInvariant().StartsWith('/codepage:')) And '%(Identity)' != '/codepage:65001'"`,
  `Condition="$([System.String]::Copy('%(Identity)').ToLowerInvariant().StartsWith('-codepage:')) And '%(Identity)' != '-codepage:65001'"`];

/** XML character references and MSBuild %XX escapes decoded, as MSBuild reads a project's names and values (ARV-225b). */
function decodeMsbuild(text) {
  const named = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };
  return text.replace(/&#x([0-9a-f]+);/gi, (_, h) => safeCodePoint(parseInt(h, 16))).replace(/&#(\d+);/g, (_, d) => safeCodePoint(Number(d)))
    .replace(/&(lt|gt|amp|quot|apos);/g, (_, n) => named[n]).replace(/%([0-9a-f]{2})/gi, (_, h) => String.fromCharCode(parseInt(h, 16)));
}

/** HTML character references decoded (numeric ones, with or without their semicolon, and the named ones that spell a setting). */
function decodeHtmlReferences(text) {
  const named = { quot: '"', QUOT: '"', apos: "'", amp: '&', AMP: '&', lt: '<', LT: '<', gt: '>', GT: '>', equals: '=', colon: ':', semi: ';', sol: '/', Tab: '\t', NewLine: '\n', nbsp: ' ', hyphen: '\u2010', dash: '\u2010', lowbar: '_', period: '.' };
  // A browser also takes quot, amp, lt, gt and nbsp (and their upper case forms) without their semicolon.
  const bare = { quot: '"', QUOT: '"', amp: '&', AMP: '&', lt: '<', LT: '<', gt: '>', GT: '>', nbsp: ' ' };
  return text.replace(/&#x([0-9a-f]+);?/gi, (_, h) => safeCodePoint(parseInt(h, 16))).replace(/&#(\d+);?/g, (_, d) => safeCodePoint(Number(d)))
    .replace(/&([A-Za-z]+);/g, (whole, n) => named[n] ?? whole).replace(/&(quot|QUOT|amp|AMP|lt|LT|gt|GT|nbsp)(?![A-Za-z0-9;])/g, (_, n) => bare[n]);
}

const safeCodePoint = (cp) => (cp >= 0 && cp <= 0x10FFFF ? String.fromCodePoint(cp) : '\uFFFD');

const WIDE = 'Text file in UTF-16 or UTF-32 (a byte order mark at its start): git shows it as binary, so a reviewer sees none of it, and a false mark hides UTF-8 text from this scanner. Save it as UTF-8.';
const NOT_UTF8 = 'File that is not valid UTF-8 and not of a binary type: a tool that reads it in another encoding sees characters this scanner does not, and git may show it as binary. Save it as UTF-8.';
const DECLARED = 'File that declares an encoding other than UTF-8 (a Python, Emacs or Vim coding line, an XML prolog, a CSS @charset, an HTML meta charset, an .editorconfig charset, a SQL client encoding, a batch console code page, or a C# code page in any project file, a response file, an environment variable or a build command line, or written with references, escapes or evaluated names): its tool decodes the bytes into characters this scanner does not see (in UTF-7, +AAo- is a line feed). Declare UTF-8.';
const IN_PATH = 'Invisible, bidirectional or control character in a file path (Trojan Source): a name can read as another file or type than it is. Rename it.';
const NAME_NOT_UTF8 = 'File name that is not valid UTF-8 (shown with \\x{HH} escapes): a tool that reads names in another encoding sees another name than this scanner, and git keeps the bytes as they are. Rename it.';
const LINK = 'Symbolic link git tracks or would add (its target follows, quoted): git keeps the target path, not a file, so what a tool reads through it is not what a reviewer sees here, and a link can point outside the repository. Replace it with the file, or allowlist the link with its exact quoted target as "line".';
const GITATTRIBUTES = 'A .gitattributes filter, or a working-tree-encoding other than UTF-8: git converts the file at checkout, so the text built and run differs from the blob reviewed and scanned. Remove it.';
const CONVERTED = 'File git converts at checkout (git check-attr from the index: a filter, or a working-tree-encoding other than UTF-8, follows): the text this scanner reads, built and run is the converted one, not the blob reviewed, and a working-tree-encoding of UTF-7 turns a hidden character into plain ASCII here. Remove the attribute.';
const LEADING_MARK = 'A byte order mark at the start of a file whose tool keeps it (any type but the .NET and PowerShell ones, whose tools drop it): a shell reading a shebang, git reading CODEOWNERS, a dotenv or EditorConfig reader outside .NET take it as part of the first line, so that line means something else to them than it shows. Remove it.';
const GITLINK = 'A gitlink (a submodule) git tracks: its files are another repository at a commit, which this scanner does not read, and a checkout of it brings them in unread. Vendor the files, or remove the gitlink.';
const NESTED = 'An untracked repository inside this one: git lists its folder, not its files, so this scanner does not read them. Remove it, or ignore its folder.';
const WINDOWS_SCRIPT = 'A PowerShell file with text beyond ASCII and no UTF-8 byte order mark, or a batch file with text beyond ASCII or a mark: Windows PowerShell 5.1 reads a file without a mark in the system code page, and cmd.exe reads every batch file in the console code page (a mark becomes part of its first command), so the text run differs from the text reviewed. Keep it ASCII (or, for PowerShell, save it with a byte order mark).';

/** PowerShell types, which a UTF-8 byte order mark makes safe; batch types, which only ASCII does. */
const POWERSHELL = new Set(['.ps1', '.psm1', '.psd1', '.pssc', '.psrc', '.ps1xml', '.cdxml']);
/** ARV-225b: the types whose tools drop a leading byte order mark (.NET's and PowerShell's); a leading mark in any other is refused. */
const MARK_TYPES = new Set(['.cs', '.csproj', '.vbproj', '.fsproj', '.props', '.targets', '.proj', '.projitems', '.user', '.sln', '.slnx', '.resx', '.config', '.xml', '.xaml', '.razor', '.cshtml', ...POWERSHELL]);
const BATCH = new Set(['.cmd', '.bat']);

/**
 * ARV-225: the lines of a .gitattributes file that set a filter (its smudge command changes the file at checkout) or a
 * working-tree-encoding other than UTF-8 (git re-encodes the file at checkout), a macro definition included. An attribute
 * unset (-filter) or unspecified (!filter) is allowed. Read as git reads it: a comment only after spaces, tabs or carriage
 * returns; a pattern that opens with a quote ends at its closing quote no backslash escapes, and its attributes may follow
 * it at once (an unclosed one is read whole, failing closed); tokens split on spaces, tabs and carriage returns; the name
 * matched whatever its case. The full and --changed scans also ask git (checkoutConversions), which reads the index.
 */
export function gitAttributeLines(lines) {
  const found = [];
  lines.forEach((raw, i) => {
    // Git skips one byte order mark at the file's start (review round 2), so a pattern glued to its attribute after it applies.
    let rest = (i === 0 ? raw.replace(/^\u{FEFF}/u, '') : raw).replace(/^[ \t\r]+/, '');
    if (!rest || rest.startsWith('#')) return;
    if (rest.startsWith('"')) {
      let j = 1;
      while (j < rest.length && rest[j] !== '"') j += rest[j] === '\\' ? 2 : 1;
      if (j < rest.length) rest = rest.slice(j + 1);
    } else rest = rest.replace(/^[^ \t\r]+/, '');
    for (const token of rest.split(/[ \t\r]+/)) {
      const m = token.match(/^([-!]?)(filter|working-tree-encoding)(=(.*))?$/i);
      if (!m || m[1]) continue;
      if (m[2].toLowerCase() === 'working-tree-encoding' && /^utf-?8$/i.test(m[4] ?? '')) continue;
      found.push(i + 1);
      return;
    }
  });
  return found;
}

/** The SEC-130 findings for names a git list held that are not valid UTF-8 (ARV-225); never allowlisted: rename the file. */
function invalidNameFindings(invalid) {
  const sec130 = RULES.find(r => r.id === 'SEC-130');
  return invalid.map(name => ({ rule: sec130.id, cwe: sec130.cwe, severity: sec130.severity, file: name, line: 0, text: '', msg: NAME_NOT_UTF8 }));
}

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
export function scanFiles(files, { allow = [], root = ROOT, textOnly = new Set(), stats = {}, links = new Set() } = {}) {
  const findings = [];
  const sec130 = RULES.find(r => r.id === 'SEC-130');
  stats.read ??= 0;
  for (const file of files) {
    const rel = path.relative(root, file);
    if (sec130.re.test(rel)) record(findings, allow, { ...sec130, msg: IN_PATH }, rel, 0, '');
    const lang = langOf(file);
    const kind = fs.lstatSync(file);
    // ARV-225: a link is reported with its target, which an allowlist entry names as its exact line.
    // Quoted, so the trimming an allowlist entry's line match does cannot make a target with a trailing space or tab match.
    if (kind.isSymbolicLink()) { record(findings, allow, { ...sec130, msg: LINK }, rel, 0, `-> ${JSON.stringify(fs.readlinkSync(file))}`); continue; }
    // ARV-225b: a link git tracks (mode 120000) that this checkout holds as a file (core.symlinks=false: the file holds the target).
    if (links.has(file) && kind.isFile()) { record(findings, allow, { ...sec130, msg: LINK }, rel, 0, `-> ${JSON.stringify(fs.readFileSync(file, 'utf8'))}`); continue; }
    if (!kind.isFile()) continue;
    const bytes = fs.readFileSync(file);
    const decoded = decodeText(bytes, binaryFile(file, bytes));
    if (decoded === null) { stats.binary = (stats.binary ?? 0) + 1; continue; }
    stats.read++;
    if (decoded.wide) record(findings, allow, { ...sec130, msg: WIDE }, rel, 1, '');
    else if (!decoded.utf8) record(findings, allow, { ...sec130, msg: NOT_UTF8 }, rel, 1, '');
    const { text } = decoded;
    if (declaredEncoding(text, file)) record(findings, allow, { ...sec130, msg: DECLARED }, rel, 1, '');
    const lines = text.split(/\r?\n/);
    // ARV-225: a byte order mark is allowed as a file's first character only (SEC-130 refuses U+FEFF anywhere else).
    const leadingMark = decoded.utf8 && text.startsWith('\uFEFF');
    if (leadingMark && !MARK_TYPES.has(path.extname(file).toLowerCase())) record(findings, allow, { ...sec130, msg: LEADING_MARK }, rel, 1, lines[0]);
    if (path.basename(file) === '.gitattributes') for (const ln of gitAttributeLines(lines)) record(findings, allow, { ...sec130, msg: GITATTRIBUTES }, rel, ln, lines[ln - 1]);
    const ext = path.extname(file).toLowerCase();
    if ((POWERSHELL.has(ext) && !leadingMark) || BATCH.has(ext)) {
      const beyond = lines.findIndex((l, i) => /[^\x00-\x7F]/.test(i === 0 && leadingMark ? l.slice(1) : l) || (i === 0 && leadingMark && BATCH.has(ext)));
      if (beyond >= 0) record(findings, allow, { ...sec130, msg: WINDOWS_SCRIPT }, rel, beyond + 1, lines[beyond]);
    }
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
        const l = r.raw ? (i === 0 && leadingMark && r === sec130 ? raw.slice(1) : raw) : stripComment(raw, lang);
        if (l && r.re.test(l)) record(findings, allow, r, rel, i + 1, raw);
      });
    }
  }
  return findings;
}

/**
 * ARV-225 (review): the files git converts at checkout, as git's own parser reads the attributes from the index (git
 * check-attr --cached): a filter set or valued, or a working-tree-encoding set to anything but UTF-8. Quoting, macros and a
 * .gitattributes file that converts itself cannot hide one, and the working tree this scanner reads is the converted text
 * (a UTF-7 checkout turns a hidden character into ASCII), so the attribute itself is refused. Throws when git cannot answer.
 */
export function checkoutConversions(files, root = ROOT, env = process.env) {
  const rels = files.map(f => path.relative(root, f).split(path.sep).join('/'));
  if (!rels.length) return [];
  const out = execFileSync('git', ['check-attr', '--cached', '-z', '--stdin', 'filter', 'working-tree-encoding'],
    { cwd: root, env, input: `${rels.join('\0')}\0`, maxBuffer: 256 * 1024 * 1024 }).toString('utf8').split('\0');
  const found = [];
  // Records of three (path, attribute, value), empty values kept: "filter=" is a value.
  for (let i = 0; i + 2 < out.length; i += 3) {
    const [rel, attribute, value] = out.slice(i, i + 3);
    if (value === 'unspecified' || value === 'unset') continue;
    if (attribute === 'working-tree-encoding' && /^utf-?8$/i.test(value)) continue;
    found.push({ file: path.join(root, rel), text: `${attribute}: ${value}` });
  }
  return found;
}

/** Every finding of a scan's selection (ARV-225): the names that are not valid UTF-8, the files git converts, then the files. */
export function scanSelection({ files, textOnly, invalid = [], converted = [], gitlinks = [], nested = [], links = new Set() }, options = {}) {
  const root = options.root ?? ROOT;
  const sec130 = RULES.find(r => r.id === 'SEC-130');
  const findings = invalidNameFindings(invalid);
  for (const c of converted) record(findings, options.allow ?? [], { ...sec130, msg: CONVERTED }, path.relative(root, c.file), 0, c.text);
  // ARV-225b: a gitlink and an untracked repository, whose files this scanner does not read.
  for (const g of gitlinks) record(findings, options.allow ?? [], { ...sec130, msg: GITLINK }, path.relative(root, g), 0, '');
  for (const n of nested) record(findings, options.allow ?? [], { ...sec130, msg: NESTED }, `${path.relative(root, n)}/`, 0, '');
  return [...findings, ...scanFiles(files, { ...options, textOnly, links })];
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
const HIDDEN_CHARS = [[0x00, 0x08], [0x0B, 0x1F], [0x7F, 0x9F], [0x00AD], [0x034F], [0x061C], [0x115F, 0x1160], [0x17B4, 0x17B5],
  [0x180B, 0x180F], [0x200B, 0x200F], [0x2028, 0x202E], [0x2060, 0x206F], [0x3164], [0xFE00, 0xFE0F], [0xFEFF], [0xFFA0], [0xFFF9, 0xFFFB],
  [0xFFFE, 0xFFFF], [0xFDD0, 0xFDEF], [0x1BCA0, 0x1BCA3], [0x1D173, 0x1D17A], [0x13430, 0x1343F], [0xE0000, 0xE007F], [0xE0100, 0xE01EF],
  ...Array.from({ length: 16 }, (_, n) => [(n + 1) * 0x10000 + 0xFFFE, (n + 1) * 0x10000 + 0xFFFF])];
// ARV-225: the neighbours of the newly refused characters stay visible (combining marks, Khmer vowel signs, Mongolian
// punctuation and digits, vertical forms, the replacement character, Duployan and musical symbols).
const VISIBLE_CHARS = [[0x09], [0x20, 0x7E], [0x00A0, 0x00AC], [0x034E], [0x0350], [0x0600, 0x061B], [0x061D, 0x06FF], [0x17B3], [0x17B6],
  [0x180A], [0x1810, 0x1819], [0x2010, 0x2027], [0xFB50, 0xFDCF], [0xFDF0, 0xFDFF], [0xFE10, 0xFE19], [0xFE70, 0xFEFC], [0xFFFD], [0x1BC9F], [0x1D172], [0x1D17B],
  [0x1342F], [0x13440], [0x1FFFD], [0x10FFFD]];

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

    // ARV-225: a tracked link, a name that is not valid UTF-8, a name that starts with a byte order mark (a decoder that drops
    // it lists a file that does not exist), and ignored output outside the folders the other rules skip, holding a hidden
    // character: the names listed or reported, the link reported, the output never read.
    put('.gitignore', 'ignored.txt\nout/\n');
    put('out/hidden.cs', 'x\u{202E}\n');
    const marked = '\u{FEFF}bom.md';
    put(marked, 'g\n');
    let latin = false;
    if (process.platform !== 'win32') {
      fs.symlinkSync('kept.md', path.join(repo, 'link.md'));
      fs.symlinkSync('missing.md', path.join(repo, 'dangling.md'));
      try { fs.writeFileSync(Buffer.concat([Buffer.from(path.join(repo, 'caf')), Buffer.from([0xE9]), Buffer.from('.md')]), 'f\n'); latin = true; }
      catch (e) { if (process.platform === 'linux') fail(`A file named with Latin-1 bytes could not be written: ${e.message}`); }
    }
    git('add', '-A');
    const latinName = latin ? 'caf\\x{E9}.md' : '';
    const has = (list, name) => list.map(f => path.relative(repo, f).split(path.sep).join('/')).includes(name);
    const links = process.platform !== 'win32';
    const listedInvalid = [];
    const listedNow = gitFiles(repo, env, listedInvalid);
    if (!has(listedNow, marked) || (links && !has(listedNow, 'link.md')) || has(listedNow, 'out/hidden.cs') || listedInvalid.join('|') !== latinName)
      fail(`The full scan's git list does not hold the marked name and the link, leaves the output in, or drops a name that is not UTF-8 (${listedInvalid.join(', ')})`);
    const changedInvalid = [];
    const changedNow = changedFiles(repo, env, changedInvalid);
    if (!has(changedNow, marked) || (links && !has(changedNow, 'link.md')) || changedInvalid.join('|') !== latinName)
      fail(`--changed does not hold the marked name and the link, or drops a name that is not UTF-8 (${changedInvalid.join(', ')})`);
    const full = fullScan(repo, env);
    if (has(full.files, 'out/hidden.cs') || !has(full.files, marked) || (links && !has(full.files, 'link.md')) || full.invalid.join('|') !== latinName)
      fail('The full scan reads ignored output, leaves out the marked name or the link, or drops a name that is not UTF-8.');
    const found = scanSelection(full, { root: repo });
    if ((links && !['link.md', 'dangling.md'].every(name => found.some(f => f.msg === LINK && f.file === name))) || (latin && !found.some(f => f.msg === NAME_NOT_UTF8 && f.file === latinName))
      || !found.some(f => f.msg === IN_PATH && f.file === shown(marked)) || found.some(f => f.file.startsWith('out')))
      fail('The full scan of a scratch repository does not report the links (one dangling), the name that is not UTF-8 and the marked name, or reads ignored output.');

    // ARV-225b: a gitlink (a submodule at a commit), an untracked repository inside this one, and a link git tracks that this
    // checkout holds as a file (core.symlinks=false writes the target into it) are reported; the mapped set is git's own list.
    git('commit', '-q', '-m', 'self-test 2');
    const head = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo, env, encoding: 'utf8' }).trim();
    git('update-index', '--add', '--cacheinfo', `160000,${head},sub`);
    const blob = execFileSync('git', ['hash-object', '-w', '--stdin'], { cwd: repo, env, input: 'kept.md', encoding: 'utf8' }).trim();
    git('update-index', '--add', '--cacheinfo', `120000,${blob},textlink.md`);
    put('textlink.md', 'kept.md');
    put('nested/inner.md', 'h\u{202E}\n');
    execFileSync('git', ['init', '-q', 'nested'], { cwd: repo, env, stdio: 'pipe' });
    const special = fullScan(repo, env);
    const specialFound = scanSelection(special, { root: repo });
    if (!specialFound.some(f => f.msg === GITLINK && f.file === 'sub') || !specialFound.some(f => f.msg === NESTED && f.file === 'nested/')
      || !specialFound.some(f => f.msg === LINK && f.file === 'textlink.md' && f.text === '-> "kept.md"'))
      fail('The full scan does not report a gitlink, an untracked repository, or a tracked link this checkout holds as a file.');
    const mapped = special.files.filter(f => !special.textOnly.has(f)).map(f => path.relative(repo, f)).sort().join('|');
    const expectMapped = gitFiles(repo, env).filter(f => langOf(f) && fs.lstatSync(f).isFile() || langOf(f) && fs.lstatSync(f).isSymbolicLink()).map(f => path.relative(repo, f)).sort().join('|');
    if (mapped !== expectMapped) fail(`The full scan's mapped set is not git's own list by type (${mapped} against ${expectMapped}).`);
  } catch (e) { fail(`The git lists could not be checked in a scratch repository: ${e.message}`); }
  finally { fs.rmSync(repo, { recursive: true, force: true }); }
  return problems;
}

/**
 * ARV-225 (review round 1): a .gitattributes file that gives itself and a PowerShell file a working-tree-encoding of UTF-7,
 * in a fresh clone (as CI scans it): the script's blob holds U+202E, which the UTF-7 checkout turns into ASCII, and the
 * attribute file's own = becomes +AD0-, so neither the characters nor the text check can see it; only git's reading of the
 * attributes from the index refuses both files. Returns the problems found.
 */
function scratchCloneConversion(baseEnv) {
  const problems = [];
  const base = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-sec130-clone-'));
  const source = path.join(base, 'source');
  const clone = path.join(base, 'clone');
  try {
    fs.mkdirSync(source);
    const git = (cwd, ...a) => execFileSync('git', ['-c', 'user.name=Self-test', '-c', 'user.email=self-test@example.invalid', '-c', 'commit.gpgsign=false', ...a],
      { cwd, env: isolatedGitEnv(baseEnv, cwd), stdio: 'pipe' });
    git(source, 'init', '-q');
    const gitDir = execFileSync('git', ['rev-parse', '--absolute-git-dir'], { cwd: source, env: isolatedGitEnv(baseEnv, source), encoding: 'utf8' }).trim();
    if (fs.realpathSync(gitDir) !== fs.realpathSync(path.join(source, '.git'))) throw new Error(`git would write to ${gitDir}, not the scratch repository`);
    fs.writeFileSync(path.join(source, 'run.ps1'), "Write-Output 'a\u{202E}b'\n");
    fs.writeFileSync(path.join(source, 'notes.txt'), 'plain\n');
    fs.writeFileSync(path.join(source, 'app.cfg'), 'plain\n');
    git(source, 'add', '.');
    git(source, 'commit', '-q', '-m', 'script');
    // A filter with an empty value too (git reports it as an empty field, which the records of three must keep), and one set
    // without a value (git reports "set").
    fs.writeFileSync(path.join(source, '.gitattributes'), '.gitattributes working-tree-encoding=UTF-7\n"*.ps1"working-tree-encoding=UTF-7\n*.txt filter=\n*.cfg filter\n');
    // The attributes alone: the script's blob stays as committed (its UTF-8 text is not UTF-7, so git could not add it again).
    git(source, 'add', '.gitattributes');
    git(source, 'commit', '-q', '-m', 'attributes');
    execFileSync('git', ['clone', '-q', source, clone], { cwd: base, env: isolatedGitEnv(baseEnv, clone), stdio: 'pipe' });
    // The control: the checkout did convert, so the script holds no hidden character and the attribute file no plain =.
    if (fs.readFileSync(path.join(clone, 'run.ps1'), 'utf8').includes('\u{202E}')) throw new Error('the clone was not converted to UTF-7, so the check proves nothing here');
    const env = isolatedGitEnv(baseEnv, clone);
    const found = scanSelection(fullScan(clone, env), { root: clone });
    for (const [name, text] of [['run.ps1', 'working-tree-encoding: UTF-7'], ['.gitattributes', 'working-tree-encoding: UTF-7'], ['notes.txt', 'filter:'], ['app.cfg', 'filter: set']])
      if (!found.some(f => f.msg === CONVERTED && f.file === name && f.severity === 'error' && f.text === text))
        problems.push(`A file git converts at checkout is not refused in a fresh clone: ${name} (${text})`);
    if (found.filter(f => f.msg === CONVERTED).length !== 4) problems.push('A file git does not convert is refused as converted in a fresh clone.');
  } catch (e) { problems.push(`The checkout conversion check could not run in a scratch clone: ${e.message}`); }
  finally { fs.rmSync(base, { recursive: true, force: true }); }
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

  // ARV-228b: SEC-030 fires at the bad fixture's line of each storage provider SQL entry point, not merely somewhere, so a name
  // dropped from its pattern fails here.
  const badCode = path.join(fx, 'bad', 'BadCode.cs');
  const badLines = fs.readFileSync(badCode, 'utf8').split('\n');
  for (const entry of ['ExecuteSqlAsync', 'ReadSqlAsync']) {
    const line = badLines.findIndex(l => l.includes(`storage.${entry}`)) + 1;
    if (line === 0) fail(`fixtures/bad/BadCode.cs has no ${entry} line for SEC-030`);
    else if (!badFindings.some(f => f.rule === 'SEC-030' && f.line === line && path.join(fx, f.file) === badCode)) fail(`SEC-030 does not fire on ${entry} (fixtures/bad/BadCode.cs line ${line})`);
  }

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

  // ARV-225b: the evaluated check in Directory.Build.props is there, before the compiler runs, with its exact conditions (the
  // only mentions of the code page and the response file a project may hold besides <CodePage>65001</CodePage>).
  const props = fs.readFileSync(path.join(ROOT, 'Directory.Build.props'), 'utf8');
  if (!/<Target Name="ArivaSourceEncodingCheck" BeforeTargets="CoreCompile">/.test(props) || !EVALUATED_CHECKS.slice(0, 2).every(check => props.includes(`<Error ${check}`))
    || !/<Target Name="ArivaCompilerArgumentsCheck" AfterTargets="CoreCompile" Condition="'@\(CscCommandLineArgs\)' != ''">/.test(props) || !EVALUATED_CHECKS.slice(2).every(check => props.includes(` ${check} />`))
    || !props.includes('<ProvideCommandLineArgs>true</ProvideCommandLineArgs>'))
    fail('Directory.Build.props no longer holds the two source encoding checks (the evaluated properties before the compiler, its arguments after it; ARV-225b).');

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
      'rsp-prefix.rsp',
      // ARV-225b: the further declared encodings, the other project types, a response file named, an import of another type,
      // references, escapes, a document type and evaluated names in a project, code pages outside a project file, and
      // character references in a meta tag.
      'editorconfig/.editorconfig', 'emacs-line.cs', 'emacs-slash.js', 'emacs-local.ts', 'vim-modeline.cs', 'client-encoding.sql',
      'psql-encoding.sql', 'set-names.sql', 'chcp.cmd', 'mode-cp.bat', 'codepage.proj', 'codepage.projitems', 'codepage.csproj.user',
      'response.targets', 'response-csc.targets', 'import-xml.targets', 'import-evaluated.targets', 'codepage-reference.targets',
      'codepage-escape.targets', 'doctype.targets', 'property-evaluated.targets', 'properties.targets', 'additional-properties.targets',
      'Dockerfile.codepage', 'codepage-workflow.yml', 'codepage-cli.sh', 'codepage-env.ps1', 'codepage-launch.json',
      'meta-reference.html', 'meta-named-reference.html', 'meta-unknown-reference.html',
      // Review round 1: an extension point, a Markdown file that is a project by its content, an import with a > in its
      // Condition and one of a list, a check replaced by name, Directory.Build.props switched off, a publish profile, the SQL
      // forms (SESSION NAMES, a comment or a line break inside SET NAMES, set_config, a U&" identifier), a > before a
      // reference in a meta tag and an upper case one, and a caret inside chcp.
      'custom-after.csproj', 'notes-project.md', 'import-condition.targets', 'import-list.targets', 'replaced-check.targets', 'import-off.yml',
      'profile.pubxml', 'set-session-names.sql', 'set-comment-names.sql', 'set-names-lines.sql', 'set-config.sql', 'unicode-identifier.sql',
      'meta-gt-quot.html', 'meta-upper-quot.html', 'chcp-caret.cmd']],
    // ARV-225b: a leading byte order mark in a type whose tool keeps it.
    [LEADING_MARK, ['bom-shebang', 'bom.env', 'bom.yml', 'bom.sh', 'bom-codeowners/CODEOWNERS']]];
  for (const [msg, names] of wholeFile) {
    const missed = names.filter(name => !badFindings.some(x => x.rule === 'SEC-130' && x.msg === msg && x.line === 1 && path.join(fx, x.file) === path.join(trojanDir, name)));
    if (missed.length) fail(`Not refused as a whole for their own reason (${msg.slice(0, 40)}...): ${missed.join(', ')}`);
  }

  // ARV-225: the Windows script rule fires for its own reason on fixtures that hold nothing else (Arabic text without a mark in
  // PowerShell, a mark in a batch file); a second byte order mark at a file's start, one in a line and a variation selector are
  // refused on their line, while a first mark is not (fixtures/good/trojan/bom-lead.cs, arabic-bom.ps1).
  for (const [msg, names] of [[WINDOWS_SCRIPT, ['arabic.ps1', 'bom.cmd', 'arabic.ps1xml']], [RULES.find(r => r.id === 'SEC-130').msg, ['bom-twice.txt', 'bom-mid.ts', 'selector.md']]]) {
    const missed = names.filter(name => !badFindings.some(x => x.rule === 'SEC-130' && x.msg === msg && x.line === 1 && path.join(fx, x.file) === path.join(trojanDir, name)));
    if (missed.length) fail(`Not refused on line 1 for their own reason (${msg.slice(0, 40)}...): ${missed.join(', ')}`);
  }

  // ARV-225: a .gitattributes filter, or a working-tree-encoding other than UTF-8, is refused on its line, in any folder; an
  // unset or unspecified one, a UTF-8 encoding and a comment are not. Read as git reads it (review round 1): a quoted pattern
  // glued to its attribute, an escaped quote inside one, an unclosed one, and a line that opens with a no-break space before
  // its #, which git reads as a pattern, not a comment.
  const attributes = ['# filter=x in a comment', '*.sh text eol=lf', '*.cs filter=lfs', '*.cs -filter', '*.cs !filter',
    '*.md working-tree-encoding=UTF-8', '*.txt working-tree-encoding=UTF-16LE-BOM', '[attr]evil filter=run', '"a b.txt" FILTER=y', '*.x filter',
    '*.y Working-Tree-Encoding=utf8', '*.z working-tree-encoding', '"*.cs"filter=lfs', '"*.ps1"working-tree-encoding=UTF-16LE-BOM',
    '"a\\"b"filter=x', '\u{A0}# filter=evil', ' \t# filter=x', '"unclosed filter=x', '"*.cs" -filter', '*.w filter='];
  const attributeLines = [3, 7, 8, 9, 10, 12, 13, 14, 15, 16, 18, 20];
  if (gitAttributeLines(attributes).join(',') !== attributeLines.join(','))
    fail(`.gitattributes lines refused: ${gitAttributeLines(attributes).join(',')}, not ${attributeLines.join(',')}`);
  // A byte order mark at the file's start is skipped as git skips it: a quoted pattern glued to its attribute after it is
  // refused, and a comment after it is not.
  if (gitAttributeLines(['\u{FEFF}"*.cs"filter=lfs']).join(',') !== '1' || gitAttributeLines(['\u{FEFF}# filter=x']).length)
    fail('A .gitattributes file that starts with a byte order mark is not read as git reads it.');
  if (escapedName(Buffer.from([0x61, 0x5C, 0x0A, 0xE9])) !== 'a\\x{5C}\\x{0A}\\x{E9}') fail('A name that is not valid UTF-8 is not shown with its bytes escaped.');
  const scratchLinks = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-sec130-links-'));
  try {
    fs.writeFileSync(path.join(scratchLinks, '.gitattributes'), `${attributes.join('\n')}\n`);
    fs.mkdirSync(path.join(scratchLinks, 'nested'));
    fs.writeFileSync(path.join(scratchLinks, 'nested', '.gitattributes'), '*.txt filter=x\n');
    const found = scanFiles([path.join(scratchLinks, '.gitattributes'), path.join(scratchLinks, 'nested', '.gitattributes')], { root: scratchLinks });
    const linesOf = file => found.filter(f => f.file === file && f.msg === GITATTRIBUTES).map(f => f.line).join(',');
    if (linesOf('.gitattributes') !== attributeLines.join(',') || linesOf(path.join('nested', '.gitattributes')) !== '1')
      fail('A .gitattributes filter or working-tree-encoding is not refused on its line in every folder.');
    // A link, reported with its target; an approved entry names that exact target, so a link moved elsewhere fires again.
    if (process.platform !== 'win32') {
      fs.writeFileSync(path.join(scratchLinks, 'target.md'), 'plain\n');
      const link = path.join(scratchLinks, 'link.md');
      fs.symlinkSync('target.md', link);
      if (!scanFiles([link], { root: scratchLinks }).some(f => f.file === 'link.md' && f.msg === LINK && f.line === 0 && f.text === '-> "target.md"'))
        fail('A symbolic link is not reported with its quoted target.');
      const approved = [{ rule: 'SEC-130', path: 'link.md', line: '-> "target.md"', contains: '-> "target.md"', reason: 'r', approvedBy: 'Owner', date: '2026-11-01' }];
      if (scanFiles([link], { root: scratchLinks, allow: approved }).length) fail('An approved entry naming a link\'s exact target does not hide it.');
      // Moved elsewhere, or to the same name with a trailing space or tab (another file, which trimming would match): fires again.
      for (const moved of ['other.md', 'target.md ', 'target.md\t']) {
        fs.rmSync(link);
        fs.symlinkSync(moved, link);
        if (!scanFiles([link], { root: scratchLinks, allow: approved }).some(f => f.msg === LINK)) fail(`A link moved to ${JSON.stringify(moved)} is hidden by the entry for its old target.`);
      }
    }
  } catch (e) { fail(`The .gitattributes and link checks could not run: ${e.message}`); }
  finally { fs.rmSync(scratchLinks, { recursive: true, force: true }); }

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

  for (const problem of scratchCloneConversion(process.env)) fail(problem);

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
  // ARV-225: read as bytes, a name that is not valid UTF-8 is reported, and a rename's old path is skipped whatever it holds.
  const porcelainInvalid = [];
  const fromBytes = porcelainPaths(Buffer.concat([Buffer.from(' M caf'), Buffer.from([0xE9]), Buffer.from('.md\0R  new.cs\0old'), Buffer.from([0xFF]),
    Buffer.from('.cs\0?? a.md\0')]), porcelainInvalid);
  if (fromBytes.join('|') !== 'new.cs|a.md' || porcelainInvalid.join('|') !== 'caf\\x{E9}.md')
    fail(`git status -z bytes read wrongly: ${fromBytes.join(', ')}; not UTF-8: ${porcelainInvalid.join(', ')}`);
  const partial = partialScan([path.join(SECURITY_FIXTURES, 'bad', 'BadCode.cs'), path.join(ROOT, MUST_LIST[3]), path.join(ROOT, 'scripts', 'verify.mjs')]);
  if (partial.files.length !== 2 || partial.textOnly.size !== 1 || !partial.textOnly.has(path.join(ROOT, MUST_LIST[3])))
    fail('An explicit or --changed scan does not leave out the security fixtures or read other fixtures with SEC-130 alone.');

  // ARV-157: the full scan lists every file git tracks or would add (SEC-130 reads each one that is text), whatever its
  // folder or type, but for the security fixtures; checked against git's own list and named files. ARV-225: symbolic links
  // are listed (and reported), and nothing git ignores is (git check-ignore, which never reports a tracked file).
  try {
    const listed = new Set(fullScan().files.map(f => path.relative(ROOT, f).split(path.sep).join('/')));
    const git = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'], { cwd: ROOT, maxBuffer: 256 * 1024 * 1024 })
      .toString('utf8').split('\0').filter(Boolean);
    const unlisted = git.filter(p => !listed.has(p) && !p.startsWith('scripts/security/fixtures/') && present(path.join(ROOT, p)));
    if (unlisted.length) fail(`The full scan leaves out files git tracks: ${unlisted.slice(0, 10).join(', ')}`);
    let ignored = '';
    try { ignored = execFileSync('git', ['check-ignore', '-z', '--stdin'], { cwd: ROOT, input: [...listed].join('\0'), maxBuffer: 256 * 1024 * 1024 }).toString('utf8'); }
    catch (e) { if (e.status !== 1) throw e; }
    if (ignored) fail(`The full scan reads files git ignores: ${ignored.split('\0').filter(Boolean).slice(0, 10).join(', ')}`);
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
  let selection;
  if (explicit.length || args.includes('--changed')) {
    const invalid = [];
    selection = { ...partialScan(explicit.length ? explicit : changedFiles(ROOT, process.env, invalid)), invalid };
    // The files git converts at checkout, when git can answer (an edit-time scan outside a repository reads text only).
    try { selection.converted = checkoutConversions(selection.files.filter(f => !path.relative(ROOT, f).startsWith('..'))); } catch { selection.converted = []; }
  } else {
    // Fails closed without git: SEC-130 must see every tracked file.
    selection = fullScan();
  }
  const allow = loadAllowlist();
  const stats = {};
  const findings = scanSelection(selection, { allow, stats });
  const report = writeReport(findings, stats.read);
  if (stats.binary) console.log(`${stats.binary} files of binary types with their signatures (${Object.keys(BINARY_SIGNATURES).join(' ')}) skipped as binary.`);
  const errors = findings.filter(f => f.severity === 'error');
  for (const f of findings) console.log(`${f.severity.toUpperCase()} ${f.rule} [${f.cwe.join(' ')}] ${f.file}:${f.line}\n  ${f.msg}\n  > ${f.text}`);
  for (const c of report.checklist) console.log(`${c.status}  ${c.cwe.padEnd(8)} ${c.name}`);
  console.log(`\n${stats.read} files scanned, ${errors.length} errors, ${findings.length - errors.length} warnings. Report: .verify/security-report.md`);
  if (args.includes('--json')) console.log(JSON.stringify(report));
  process.exit(errors.length ? 1 : 0);
}

// Run as a command, through a link too (ARV-164a review): the loader resolves import.meta.url through links unless
// --preserve-symlinks-main keeps the link's path, so argv[1] counts as this module when either its own path or its real
// path is; otherwise a linked run would exit 0 having scanned nothing. Imported (the post-edit hook), main does not run.
const self = fileURLToPath(import.meta.url);
if (process.argv[1] && fs.existsSync(process.argv[1]) && [path.resolve(process.argv[1]), fs.realpathSync(process.argv[1])].includes(self)) main();
