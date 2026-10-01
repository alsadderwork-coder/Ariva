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

## Permissions (ARV-009)

- Controllers authorize with `[Permission(nameof(Global.Defaults.Permissions.ViewDesk))]` (several names mean any one of them). The attribute is an `AuthorizeAttribute` whose policy `PermissionPolicyProvider` builds on demand; an unknown name fails closed. Controllers never name roles.
- `Global.Defaults.Permissions` (Ariva.Core) catalogues every permission as `Entity.Action` with View, Create, Edit, Search and Delete per entity, plus special actions such as `ZoneProfile.Publish`. Codes never change once shipped.
- `RolePermissions` (Ariva.Core/Security) is the role seed for BorderShiftSupervisor, TerminalDutyManager, HandlerStationManager and SystemAdministrator. Audit entries are read-only for every role; only SystemAdministrator manages users, roles and integration clients; product version (`/api/v1/system/info`) is for SystemAdministrator only.
- `permission-matrix.json` lists every endpoint of every host with the status each caller gets (anonymous and each role). `PermissionMatrixTests` fails when a host maps an endpoint that is not listed, when a permission row disagrees with the seed, or when a host answers a row differently; the E2E suite reads the same file with real tokens for each role. Add the rows in the story that adds the endpoint. Routes are templates: `{id:guid}` is called with `routeValues.guid`, an id that never exists. A row's optional `body` is sent as JSON and must fail the same way with and without a database (unknown user, wrong current password, unknown id), because the in-process hosts have none.
- Permissions come from the user's stored role grants (`user_role`), looked up by the token's `sub` and cached for one minute under the tag `user:{id}`; tokens carry no roles. ARV-011 adds the administration of grants with this seed as the default.

## Accounts and tokens (ARV-010a, ADR-0026)

- Local accounts only. Passwords: PBKDF2-SHA256, 600,000 iterations, 16-byte salt, never trimmed; 12 to 128 code points; refused when on the bundled breached list (46,146 entries of 12 or more characters from SecLists, MIT) or when they contain the username, `ariva`, the site code or an `Auth:ContextWords` entry. Weaker stored hashes are upgraded at the next sign-in.
- Every sign-in failure (wrong password, unknown, malformed, disabled or locked account) is the same 401 body after one full PBKDF2 verification. 10 consecutive failures lock the account for 15 minutes; failures are counted in one SQL statement so parallel attempts cannot lose counts. The `auth` rate limit allows 10 sign-ins a minute per client address.
- Access tokens: ES256 only, header `typ` `at+jwt` and `kid` (first 16 bytes of the SHA-256 of the public key), issuer `ariva`, audience `ariva-users`, 15 minutes, 30 seconds of skew, read from the `Authorization` header only. Only Ariva.Api.Main holds the signing key; every host holds the public keys (current and, during a rotation, previous). Key handling: [wiki/04-Deployment-Guide.md](../wiki/04-Deployment-Guide.md).
- An account with a temporary password (and, from ARV-010c, without TOTP) gets a token with `scope` `pending`, which `PendingScopeMiddleware` confines to endpoints marked `[AllowPendingScope]` (change password, logout, later TOTP enrolment) and anonymous ones.
- `Auth:DevelopmentUsers` creates accounts at startup in vm-local only (developers and the E2E run, with per-run random passwords); a host in any other environment refuses to start when it is set.

