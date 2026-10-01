# Security

## Exceptions

`allowlist.json` holds the only permitted exceptions to the security gate (`node scripts/security/scan.mjs`).

Each entry needs: `rule` (SEC-nnn), `path` (repository relative, forward slashes), optional `contains` (text that must appear on the flagged line), `reason`, `proposedBy`, `approvedBy` and `date`. Entries for SEC-052 (anonymous endpoints) also list the route patterns they cover in `routes`, for example `["/health/startup"]`; `EndpointInventoryTests` boots every host and fails when an anonymous endpoint's route is not listed by an entry for the file that maps it. `AllowlistTests` fails CI when any field is missing or malformed.

An entry whose `approvedBy` starts with `PENDING` downgrades the finding to a warning instead of hiding it, so unapproved exceptions stay visible in every report. Only a human approver replaces `PENDING`; agents may propose entries but never approve them (enforced by the `guard-paths` hook, which simulates each agent edit and refuses it if any approved entry in the result is new or differs from the current file).

## Baseline

Every Backplane host calls `AddAppSecurityBaseline` and the matching middlewares from `Ariva.Api.Common` (see `Extensions/SecurityBaselineExtensions.cs` for the order). Settings live in the `Security` section of `appsettings.base*.json`.

| Control | Where | Settings |
|---|---|---|
| Default deny (CWE-862, CWE-306): the `Ariva.Deny` placeholder scheme never authenticates and the fallback policy requires an authenticated user, so everything except the allowlisted probes answers 401. The authentication story replaces the placeholder with the JWT bearer schemes. | `Security/DenyAuthenticationHandler.cs`, `Extensions/SecurityExtensions.cs` | none |
| API security headers: `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `Cache-Control: no-store` when the request carries credentials, no `Server` header | `Middlewares/SecurityHeadersMiddleware.cs` | none |
| Input limits (CWE-120): Kestrel body (1 MB), request line, headers and header timeout; form limits; JSON `MaxDepth` 32 | `Extensions/RequestLimitsExtensions.cs` | `Security:Limits` |
| Rate limiting: global fixed window per client IP and the stricter `auth` policy for login endpoints | `Extensions/RateLimitingExtensions.cs` | `Security:RateLimiting` |
| Errors as ProblemDetails; exception details only in vm-local | `Extensions/ErrorHandlingExtensions.cs` | environment |
| Forwarded headers trusted only from the listed proxies and networks (none by default; the k8s files list the private ranges and should be narrowed to the cluster's pod network) | `Extensions/ForwardedHeadersExtensions.cs` | `Security:ForwardedHeaders` |
| CORS allow-list of exact origins; a wildcard fails startup | `Extensions/CorsExtensions.cs` | `Security:Cors` |

Ariva.Simulation.Api keeps its own small copy (default deny, no `Server` header, body limit, ProblemDetails, headers) because it references Ariva.Business.Contracts only, and it refuses to start in k8s-prd. Ariva.Web sends its CSP and headers from nginx and from `vite preview`, both generated from `Platform/Frontplane/Ariva.Web/csp.config.js`.
