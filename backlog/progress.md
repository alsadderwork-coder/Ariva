# Progress log

One entry per story, newest last. Format:

```
## YYYY-MM-DD ARV-nnn <title>
- Summary:
- Files:
- Gates: backend PASS, e2e PASS, ...
- Security review: PASS (CWEs checked: ...)
- Learnings for later stories:
```

## 2026-10-01 Scaffold (before ARV-001)
- Summary: Repository created by mirroring AMAN's layout; security baseline (default deny, headers, limits, rate limiting, ProblemDetails, trusted proxies, CORS allowlist); security gate with 40 rules mapped to 14 CWEs; unit, API end-to-end and Playwright functional tests for the skeleton; wiki; harness (CLAUDE.md files, subagents, commands, skills, hooks, MCP servers); Phase 0 backlog.
- Gates run in the build environment (no NuGet access): every non-test project builds with 0 warnings; security gate 0 errors (6 warnings: health probe allowlist entries awaiting approval); e2e 58 of 58 pass; unit tests compiled and run against stand-ins only, so ARV-001 must run them for real.
- Security review: baseline controls in place for CWE-862, CWE-306, CWE-120, CWE-79 (CSP), CWE-269 (non-root images and pod security context); the rest arrive with their stories.
- Learnings: Playwright on the build machine needed an explicit Chromium path; default deny answers 401 even for unknown routes (by design: the fallback policy runs before routing).

## 2026-10-01 ARV-001 Prove the build on GitHub
- Summary: The repository moved to GitHub (private) with ci, security-scan, images, codeql (off until enabled), base-images and dev-environment workflows; Dependabot; workflows post failures as PR comments because job logs are not readable with the token scope in use.
- Gates: backend PASS, unit PASS, e2e PASS, security PASS (GitHub Actions).
- Learnings: the build container has no NuGet access; CI is the compile gate. Playwright had one flaky failure on PR #15 that passed on rerun.

## 2026-10-01 ARV-002 Chart hardening, image pins and CVE fixes (PR #15)
- Summary: read-only root filesystems with emptyDirs, TLS on every ingress, prd render refuses floating tags, chart render tests in CI; base images pinned by digest; Microsoft packages 10.0.1 to 10.0.12 and MessagePack 3.1.4 to 3.1.7 (CVE-2026-48109, -48502, -48506, -45591); nginx 1.28 (Debian, 123 high or critical) to 1.30.5 on Alpine; OS upgrades in image builds (libssl3t64 CVE-2026-84782).
- Gates: chart tests PASS, Trivy fs and image PASS, backend PASS, e2e PASS. Not passing yet: start under the chart security context on the dev cluster (human release).
- Security review: CWE-269 (non-root, read-only root, dropped capabilities), supply chain pins.
- Learnings: AMAN pins the same vulnerable MessagePack and SignalR MessagePack versions; nginx 1.28 images are no longer rebuilt.

## 2026-10-01 ARV-003 Local development environment (PR #16)
- Summary: docker-compose.dev.yml (TimescaleDB HA image, Kafka KRaft, Redis with password, smtp4dev) on 127.0.0.1 ports away from AMAN's; dev-up writes random passwords and appsettings.local.json; dev-check proves each dependency.
- Gates: dev-environment workflow PASS.

## 2026-10-01 ARV-004 Domain foundations (PR #17)
- Summary: EntityBase, auditable and soft-deletable bases, IDomain, IEvent with partition key, EventBase, ISvc markers, trimmed ICurrentUser (no access token), BaseCriteria with UTC range rules; Result, PagedData, Search and Fx.Specification from Fluentx 6.1.9.
- Gates: backend PASS (unit tests for ids, equality, hash stability, events, audit, soft delete, hash keys, specifications).
- Learnings: transient entities must not compare equal and the hash code must stay stable across save; AMAN got both wrong.

## 2026-10-01 ARV-005 NHibernate storage provider and unit of work (PR #18)
- Summary: async IStorageProvider and IUnitOfWork; convention mapper with snake_case singular names, quoted reserved words, version 7 ids, UTC timestamptz, enums by name; audit interceptor and soft delete listener; domain events handed to IDomainEventOutbox inside the transaction (fails closed until ARV-020); ExecuteSqlAsync takes [ConstantExpected] SQL with CA1857 as an error.
- Gates: backend PASS, integration PASS (Testcontainers.PostgreSql 4.15.0, TimescaleDB image), security PASS. Correction: the first CI runs of this PR reported green while the build and unit tests failed, because "dotnet ... | tee" ran under bash -e without pipefail; the workflows now run with pipefail and the unit tests pass for real.
- Security review: CWE-89 (constant SQL enforced at compile time, parameters bound), CWE-532 (no SQL parameter logging).
- Learnings: passing an IType instance to the by-code mapper breaks enum mapping (use the generic EnumStringType); NHibernate needs System.Configuration.ConfigurationManager at runtime; a plain NpgsqlDriver with Npgsql 10 handles UTC timestamptz without the legacy switch.

## 2026-10-01 ARV-006 Versioned SQL script runner and database roles (PR #19)
- Summary: embedded scripts applied in order under an advisory lock, one transaction per script with its schema_version row; drift and pending checks in the runner and at host startup; checksums.lock; 0001_roles.sql with ariva_migration and ariva_runtime and a safe login function; Ariva.Api.Main --migrate run by a Helm hook Job.
- Gates: backend PASS, integration PASS, security PASS. Not yet run: the Helm Job on a cluster.
- Security review: CWE-269 (runtime login DML only, cannot change schema or schema_version), CWE-89 (login function quotes with format %I and %L).
- Learnings: CA2100 accepts a switch over string literals but not a tuple switch; checksums must normalise CRLF for Windows checkouts.

## 2026-10-01 ARV-007 Logging and telemetry with secret redaction (PR #20)
- Summary: Serilog in every API host (JSON console, Loki and OTLP by configuration); a redaction enricher added in code removes sensitive properties, header dictionaries, bearer and basic credentials, JWTs and token query values; Microsoft.AspNetCore.Hosting fixed at Warning; traces and metrics over OTLP when an endpoint is set.
- Gates: backend PASS (268 unit, 15 integration), e2e PASS including the log scan (canary credentials sent by the suite never reach a host log).
- Security review: CWE-532.
- Learnings: CI now posts test counts and fails when a suite runs no tests; Application:Name placeholders are not resolved yet, so ArivaLogging resolves the one used for service names.

## 2026-10-01 ARV-008 Caching and Data Protection key ring (PR #21)
- Summary: FusionCache (AMAN entry options) with tags, Redis distributed level and backplane on one connection, memory-only without Redis; Data Protection keys in PostgreSQL (script 0002, append-only for the runtime role) encrypted with a certificate from the ariva-dataprotection secret, previous certificates for rotation, development certificate on vm-local only.
- Gates: backend PASS (275 unit, 18 integration including two-host key ring and Redis backplane), e2e PASS.
- Security review: CWE-287 (shared key ring protected at rest), CWE-611 (XML read without DTDs).
- Learnings: JSON booleans read back as "True" through IConfiguration; compare with GetValue<bool>.

## 2026-10-01 ARV-009 Permission model and permission matrix (PR #22)
- Summary: Global.Defaults.Permissions (Entity.Action, five actions per entity), [Permission] as an AuthorizeAttribute with on-demand policies that fail closed, role seed from the wiki 01 role descriptions, security/permission-matrix.json checked in-process for every host and by E2E for anonymous callers.
- Gates: backend PASS (306 unit), e2e PASS.
- Security review: CWE-862, CWE-863 (no role strings in controllers; audit read-only for every role); CWE-200 (product version for administrators only).

## 2026-10-01 ARV-010a Password login, lockout and token issuance (PR #23)
- Summary: User and UserRole (script 0003), PBKDF2-SHA256 600k with rehash, password policy with a bundled breached list and context words, NFKC usernames; one 401 for every sign-in failure; lockout 10 for 15 minutes counted in one SQL statement; ES256 at+jwt tokens with kid signed only by Ariva.Api.Main and validated by every host; permissions from stored grants; pending scope for temporary passwords; administrator unlock; token key secrets in the chart with a rule that keeps the signing key in api-main.
- Gates: backend PASS (393 unit, 29 integration with 1 skipped), e2e PASS against PostgreSQL with per-run random accounts and a DML-only runtime login, security PASS with one warning (SEC-052 for /api/auth/login, PENDING approval).
- Security review: CWE-287, CWE-307 (lockout and rate limit), CWE-204 and CWE-208 (uniform failure after a full hash check), CWE-916 (hash cost), CWE-347 (ES256 only, typ, kid), CWE-598 (no query string tokens), CWE-532 (passwords and tokens covered by the E2E log scan).
- Learnings: positional records need validation attributes on the constructor parameters, not with property:, or MVC throws on every request; a harness that compiles several projects as one assembly hides missing usings, so build per project; a real Kestrel host against PostgreSQL with the runtime login found what in-process tests without a database cannot.

## 2026-10-01 ARV-010b Server-side sessions, refresh rotation and revocation (PR #24)
- Summary: user_session and refresh_token (script 0004); a new session and refresh family at every sign-in, revoking a cookie sent along; a session check on every authenticated request in every host with a 4 second cache evicted on revocation; 12 hour absolute and role-based idle lifetimes; single-use refresh tokens with a grace successor for concurrent tabs and family revocation on reuse; the __Secure-ariva_rt cookie with an X-Ariva-Csrf and Origin check; administrator disable and enable; SignalR session filter and sweeper; /api and /hubs on the web host.
- Gates: backend PASS (410 unit, 43 integration with 1 skipped), e2e PASS, security PASS with two warnings (SEC-052 for login and refresh, PENDING approval).
- Security review: CWE-384 (new session at every sign-in, presented cookie revoked), CWE-613 (server-side expiry and revocation within 5 seconds), CWE-352 (SameSite=Strict, custom header and Origin allowlist on refresh), CWE-287 (refresh reuse detection).
- Learnings: PostgreSQL RETURNING returns the new row, so taking a value while clearing it needs the old row from a locked subquery; Playwright's request context sends Secure cookies to localhost, so E2E auth calls use fetch with explicit cookies; a 5 second cache measured about 5.15 seconds across hosts, so the cache is 4 seconds to keep the 5 second promise; login, refresh and password change share the per-address limit, which the in-process matrix run must lift.

## 2026-10-02 ARV-010c TOTP, recovery codes and break-glass access (PR #25)
- Summary: script 0005 (TOTP columns, break-glass flag with a unique partial index, recovery_code); RFC 6238 TOTP with a one-step window and a replay guard in one UPDATE on the user row; secrets Data Protection encrypted and shown once; enrolment confirmed by a first code; ten Crockford recovery codes, SHA-256 at rest, single use, regenerated with a code; wrong codes count towards the lockout; MFA at sign-in with amr pwd plus otp or rc; the break-glass account created and rotated only by the installer command, never locked out, a critical event at every sign-in.
- Gates: local harness PASS (every project built on its own, the TOTP and break-glass flows against PostgreSQL 16 through a real Kestrel host with the runtime login); GitHub CI not observed from this session (no push access after the context reset), so the PR checks are the outstanding gate.
- Security review: CWE-294 (replay guard in SQL), CWE-308 (TOTP for every human account), CWE-312 (secret encrypted, recovery codes hashed), CWE-307 (codes in the lockout), CWE-269 (break-glass outside the API).
- Learnings: the security scanner refuses stackalloc with a computed size, so TOTP uses fixed buffers; RFC 6238 vectors were checked against an independent Python implementation before the C# tests were written.

## 2026-10-02 ARV-010d Step-up MFA for critical actions
- Summary: [RequiresRecentMfa(15)] with on-demand RecentMfa policies reading amr and auth_time; the RFC 9470 401 (insufficient_user_authentication, max_age) with a mfa_required problem only when the second factor is the sole gap, 403 otherwise; POST /api/auth/step-up with a TOTP or recovery code that records the new auth_time on the session; security/critical-actions.json with an architecture test over every host; recovery code regeneration as the first critical action.
- Gates: local harness PASS (step-up flow through a real Kestrel host against PostgreSQL: stale token refused with the header, non-critical endpoint unaffected, step-up by recovery code in the same session, single use); GitHub CI not observed from this session.
- Security review: CWE-306 (critical functions need a recent second factor), CWE-287 (step-up shares the replay guard and lockout), CWE-204 (403 before step-up for callers without the permission, so the 401 never reveals an action).
- Learnings: AuthorizationHandlerContext.Fail empties FailedRequirements, so a handler that wants the result handler to see which requirement failed must leave it pending rather than fail it.

## 2026-10-02 ARV-011 User, role and audit administration
- Summary: api/v1/admin/users (search, view, create with a one-time temporary password, update, password and TOTP reset, grant and revoke), api/v1/admin/roles and the read-only api/v1/admin/audit-entries; ranked fixed roles with no self-change, nothing above the granter's rank and the last active administrator kept; create, resets, grants and revokes as critical actions; audit_entry written in the same transaction and append-only for the runtime login (script 0006); session revocation through one AccountSessions path.
- Gates: local harness PASS (34 checks through a real Kestrel host against PostgreSQL 16 with the runtime login, including UPDATE and DELETE on audit_entry refused with 42501, and the session and TOTP flows rerun after the refactor); GitHub CI not observed from this session.
- Security review: CWE-269 (rank check, no self-change, last administrator, step-up on every grant), CWE-863 (permission per action, break-glass invisible), CWE-306 (critical actions), CWE-89 (sort allowlist, parameterised filters), CWE-532 and CWE-312 (no credentials in audit summaries or logs), CWE-640 (temporary passwords random, shown once, forcing a change).
- Decisions: roles stay the four fixed codes (custom roles and a second approving administrator are Phase 1 candidates); accounts are disabled, never deleted; the last-administrator rule excludes the break-glass account.
- Learnings: fetching a collection together with Skip and Take pages the joined rows, so roles for a page of users come from a second query.

