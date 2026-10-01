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
- Gates: backend PASS, integration PASS (Testcontainers.PostgreSql 4.15.0, TimescaleDB image), security PASS.
- Security review: CWE-89 (constant SQL enforced at compile time, parameters bound), CWE-532 (no SQL parameter logging).
- Learnings: passing an IType instance to the by-code mapper breaks enum mapping (use the generic EnumStringType); NHibernate needs System.Configuration.ConfigurationManager at runtime; a plain NpgsqlDriver with Npgsql 10 handles UTC timestamptz without the legacy switch.

## 2026-10-01 ARV-006 Versioned SQL script runner and database roles (PR #19)
- Summary: embedded scripts applied in order under an advisory lock, one transaction per script with its schema_version row; drift and pending checks in the runner and at host startup; checksums.lock; 0001_roles.sql with ariva_migration and ariva_runtime and a safe login function; Ariva.Api.Main --migrate run by a Helm hook Job.
- Gates: backend PASS, integration PASS, security PASS. Not yet run: the Helm Job on a cluster.
- Security review: CWE-269 (runtime login DML only, cannot change schema or schema_version), CWE-89 (login function quotes with format %I and %L).
- Learnings: CA2100 accepts a switch over string literals but not a tuple switch; checksums must normalise CRLF for Windows checkouts.
