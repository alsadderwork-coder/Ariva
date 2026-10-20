---
name: security-cwe
description: The 14-CWE checklist every Ariva change is tested against, with the concrete controls, banned APIs, required tests and how the gates work. Load for every story.
---
# Security checklist (CWE)

Normative: docs/security/cwe-controls.md. Gate: `node scripts/security/scan.mjs` (44 rules, self-tested) plus .NET security analyzers as errors, architecture tests, E2E attack tests.

| CWE | Never | Always | Test |
|---|---|---|---|
| 78, 77 command injection | Process.Start, shells, exec with shell | No process execution in Ariva | ForbiddenDependencyTests |
| 94 code injection | scripting, dynamic LINQ, TypeNameHandling, eval, new Function; in Ariva.Web `obj[key] = value` with a key from data, `__proto__`, `Object.assign` (prototype pollution, SEC-131, SEC-132); a literal invisible, bidirectional, control or line-separating character in any text file git tracks or in its path, comments included, and UTF-16, UTF-32, non-UTF-8 or non-UTF-8-declaring files (Trojan Source, SEC-130, every file of any type or folder since ARV-157) | Typed rule data, embedded templates, strict CSP; Map, SvelteMap or Object.fromEntries for objects keyed by data; such characters written as escapes (`\u202E` in code, `U&'\202E'` or `E'\u202E'` in SQL) | Hostile-string rule tests, scanner self-test (SEC-130 proven on every mapped type, unmapped and skipped files, UTF-16 and UTF-32, and each refused character) |
| 918 SSRF | new HttpClient, URLs from requests | OutboundEndpoint registry, named clients, resolved-IP CIDR check, no redirects | Metadata IP and rebinding tests |
| 862 missing authorization | Endpoints without metadata | Default deny, [Permission]/[IntegrationScope]/[DeviceAuthenticated], hubs [Authorize] | EndpointInventoryTests |
| 863 incorrect authorization | Role strings, unscoped site queries | Permission policies, ISiteScope, per-role projections, scheme-pinned policies | Permission matrix and IDOR tests |
| 306 missing authentication | Anonymous critical functions | Step-up MFA for critical functions, allowlist with human approval | CriticalFunctionTests |
| 287 improper authentication | Plain secrets, == on secrets, TOTP without replay guard, disabled validation | PBKDF2, FixedTimeEquals, replay guard, strict JWT validation, lockout | Replay and confusion tests |
| 501 trust boundary | Binding entities, untrusted data into session or claims, relaxing the strict JSON for a new endpoint | Request models, validation before mapping, validated adapters, strict JSON and no repeated query parameter (ARV-089; the auth controllers exempt until ARV-089a only) | EntityBindingTests, StrictInputTests |
| 269 privilege | Roles from requests, self-grant, root containers | SvcRoleAssignment rules, non-root pods, DDL-less runtime role | PrivilegeEscalationTests |
| 384 session fixation | Tokens in URLs or web storage, reused refresh tokens | New sid per login, refresh rotation with family revocation, HttpOnly SameSite=Strict cookie | Rotation tests |
| 89 SQL injection | Interpolated SQL or HQL, string-built CommandText | Parameters, LINQ, typed COPY, sort allowlists | Payload tests on search endpoints |
| 120 buffer overflow | unsafe, variable stackalloc, unbounded bodies | Size limits, MaxDepth 32, bounded parsers | 413 and deep JSON tests |
| 79 XSS | {@html}, innerHTML, Html.Raw | Text interpolation, strict CSP, nosniff, CSV formula neutralisation | Playwright XSS probes |

Logging: never log secrets, tokens, TOTP codes or `access_token` query strings (SignalR browser clients send the token in the query string; set the `Microsoft.AspNetCore.Hosting` logger to Warning or redact it, per Microsoft's SignalR security guidance).
Forwarded headers: trusted proxies only; never `ASPNETCORE_FORWARDEDHEADERS_ENABLED` (it clears KnownProxies and KnownIPNetworks).
Exceptions: propose in security/allowlist.json with `"approvedBy": "PENDING: Ahmad"`; only a human approves.
