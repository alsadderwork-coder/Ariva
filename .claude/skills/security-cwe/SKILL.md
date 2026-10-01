---
name: security-cwe
description: The 14-CWE checklist every Ariva change is tested against, with the concrete controls, banned APIs, required tests and how the gates work. Load for every story.
---
# Security checklist (CWE)

Normative: docs/security/cwe-controls.md. Gate: `node scripts/security/scan.mjs` (40 rules, self-tested) plus .NET security analyzers as errors, architecture tests, E2E attack tests.

| CWE | Never | Always | Test |
|---|---|---|---|
| 78, 77 command injection | Process.Start, shells, exec with shell | No process execution in Ariva | ForbiddenDependencyTests |
| 94 code injection | scripting, dynamic LINQ, TypeNameHandling, eval, new Function | Typed rule data, embedded templates, strict CSP | Hostile-string rule tests |
| 918 SSRF | new HttpClient, URLs from requests | OutboundEndpoint registry, named clients, resolved-IP CIDR check, no redirects | Metadata IP and rebinding tests |
| 862 missing authorization | Endpoints without metadata | Default deny, [Permission]/[IntegrationScope]/[DeviceAuthenticated], hubs [Authorize] | EndpointInventoryTests |
| 863 incorrect authorization | Role strings, unscoped site queries | Permission policies, ISiteScope, per-role projections, scheme-pinned policies | Permission matrix and IDOR tests |
| 306 missing authentication | Anonymous critical functions | Step-up MFA for critical functions, allowlist with human approval | CriticalFunctionTests |
| 287 improper authentication | Plain secrets, == on secrets, TOTP without replay guard, disabled validation | PBKDF2, FixedTimeEquals, replay guard, strict JWT validation, lockout | Replay and confusion tests |
| 501 trust boundary | Binding entities, untrusted data into session or claims | Request models, validation before mapping, validated adapters | EntityBindingTests |
| 269 privilege | Roles from requests, self-grant, root containers | SvcRoleAssignment rules, non-root pods, DDL-less runtime role | PrivilegeEscalationTests |
| 384 session fixation | Tokens in URLs or web storage, reused refresh tokens | New sid per login, refresh rotation with family revocation, HttpOnly SameSite=Strict cookie | Rotation tests |
| 89 SQL injection | Interpolated SQL or HQL, string-built CommandText | Parameters, LINQ, typed COPY, sort allowlists | Payload tests on search endpoints |
| 120 buffer overflow | unsafe, variable stackalloc, unbounded bodies | Size limits, MaxDepth 32, bounded parsers | 413 and deep JSON tests |
| 79 XSS | {@html}, innerHTML, Html.Raw | Text interpolation, strict CSP, nosniff, CSV formula neutralisation | Playwright XSS probes |

Logging: never log secrets, tokens, TOTP codes or `access_token` query strings (SignalR browser clients send the token in the query string; set the `Microsoft.AspNetCore.Hosting` logger to Warning or redact it, per Microsoft's SignalR security guidance).
Forwarded headers: trusted proxies only; never `ASPNETCORE_FORWARDEDHEADERS_ENABLED` (it clears KnownProxies and KnownIPNetworks).
Exceptions: propose in security/allowlist.json with `"approvedBy": "PENDING: Ahmad"`; only a human approves.
