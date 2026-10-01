# Testing strategy

How Ariva is tested, from pure formula tests to field validation, and how to run each layer locally and in CI. One entry point runs the gates: `node scripts/verify.mjs <scope>`, which writes per-step results to `.verify/last.json`.

## 1. Test layers

| Layer | Where | What it proves | Status (1 October 2026) |
|---|---|---|---|
| Unit tests | `Platform/Backplane/Ariva.UnitTests` (xUnit v3, Moq, FluentAssertions, Bogus) | Formulas as pure functions with the test cases in `docs/domain/formulas.md`; state machines (desk state, alert escalation) with every transition; validators; mappers | Project in place; domain tests arrive with the domain stories |
| Architecture tests | `Ariva.UnitTests/Architecture`, `Ariva.UnitTests/Security` | Layering (Ariva.Core depends only on Ariva.Utilities; no reference to AMAN assemblies), data boundary (no contract property that looks like a person or officer identifier), forbidden dependencies, endpoint inventory, entity binding, unsafe code | Layering and data-boundary tests exist; security reflection tests arrive with the controls |
| Integration tests | `Platform/Backplane/Ariva.IntegrationTests` with Testcontainers | Persistence (NHibernate mappings, Timescale scripts, binary COPY, upserts), Kafka consumers (rebalance, replay from committed offsets, idempotency), Redis | Placeholder test skipped until story ARV-007 adds Testcontainers for PostgreSQL with TimescaleDB, Kafka and Redis |
| API end-to-end tests | `Platform/Testing/Ariva.E2E` (Playwright test runner) | Integration API token exchange, TOTP replay rejection, scope and site enforcement, idempotency, every endpoint; security attack payloads | Planned; the project is not yet in the repository |
| Functional UI tests | `Platform/Testing/Ariva.E2E` (Playwright) | Screens, role visibility, create flows, XSS payloads rendered as text with no CSP violation | Planned |
| Replay and golden scenario tests | Ariva.Simulation.Api reference scenario | The seeded day (seed 9303) produces the same outputs every run: alert at 18:05, degraded zone 18:20 to 18:30, SLA breach at 19:10 | Planned with the simulator |
| Conformance tests per sensor family | `Emulators/Sensors/<Family>/samples/` | Each adapter maps recorded payloads to the expected canonical events, including malformed and oversized payloads | Planned per family |
| AODB replay | Replay harness with recorded feeds | Adapters handle real feeds: out-of-order messages, identity changes, stale feeds | Planned; recorded feeds from each pilot airport join the suite |
| Load test | Phase 1 epic Hardening | 15,000 messages per second sustained | Planned |
| Failure-mode tests | Phase 1 epic Hardening | The failure table in the architecture overview: sensor offline, Kafka outage, database failover, stream pod crash, stale feeds, clock drift | Planned |
| Accuracy validation | On site | Field accuracy against manual counts, tracers and observer logs | Pilot |

## 2. Unit tests

- Every formula in `docs/domain/formulas.md` lives as a pure function in Ariva.Core (or the Python worker for v1 forecasting) with its listed test cases, for example F8: 29 queuing, 6 desks, cycle 1.5 minutes gives a nowcast of 7.5 minutes; F17: 10 breaches, 2 excluded, 1 held, allowance 6, rate 350 gives a penalty of 350.
- Floating-point rule: never call a ceiling on a floating ratio directly (`(24 - 0.3) / (4.25 - 0.3)` evaluates to 5.999999999999999); use `Math.Ceiling(Math.Round(x, 9))` or `decimal`, and compare in tests with a tolerance of 1e-9.
- Geometry gets property tests (line crossing, F4): touch-and-return is not a crossing; a sample pair outside the segment is not a crossing.
- Naming, as in the existing tests: `Method_Should_Result_When_Condition`.

Run:

```bash
node scripts/verify.mjs unit
# or
dotnet test Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj
```

## 3. Integration tests (Testcontainers)

Containers: `timescale/timescaledb-ha` (PostgreSQL 17 with TimescaleDB), Kafka, Redis. Shared fixtures (container lifetime, connection strings, script runner) live in `Ariva.IntegrationTests/Setup`. Typical tests: the Timescale script runner applies all scripts and records them; a checksum change stops startup; COPY writers and upserts are idempotent by (zone, bin start, revision); a consumer replays after a rebalance without double counting.

Run (needs Docker):

```bash
node scripts/verify.mjs integration
```

## 4. API end-to-end and functional tests

`Platform/Testing/Ariva.E2E` will hold Playwright tests for both the APIs and the web app, against a running stack with the simulator. Coverage required by the design: the integration token exchange, TOTP replay rejection, scope and site enforcement, idempotency, every Integration API endpoint, and the security behaviour tests in `tests/security` (oversized body returns 413, deep JSON returns 400, injection payloads never return 500, script payloads never execute, CSV formula injection neutralised, refresh-token reuse revokes the family, the session id changes at sign-in, critical endpoints return 401 without a token and 403 without recent MFA).

Run:

```bash
node scripts/verify.mjs e2e
```

The scope reports a failure until the project exists. How to point it at a deployed environment is To confirm.

## 5. Replay and golden scenario tests

The reference scenario is the prototype's seeded day at the fictional DMO airport: seed 9303, simulated from 17:40 in the prototype, with scripted events:

| Time | Event | Expected behaviour |
|---|---|---|
| 18:05 | A visitor-heavy arrival wave | The arrivals Visitors nowcast passes 15 minutes and rule R-001 fires |
| 18:20 to 18:30 | Sensor S-17 over the arrivals hall offline | The Visitors zone is `Degraded`, the wait shows a band, a device alarm appears |
| 19:10 | Handler B's check-in island C after a shift change leaves 5 of 12 counters open | SLA breach for consecutive 15-minute bins, provisional first, final once the queue clears |

Same seed, same output: golden-file tests lock the behaviour so that changes to the engine show up as reviewed diffs. Created objects use their own sub-seeded random streams so they do not disturb the scripted events.

## 6. Accuracy validation

Field validation is not a software test but uses the same metrics (F18): count accuracy per 15-minute bin per line at least 95 percent, realised-wait error within the larger of 1 minute or 10 percent, bias within 5 percent, track completion at least 90 percent, desk-state agreement at least 95 percent, nowcast median error within 2 minutes for waits under 20 minutes. Protocol in [Commissioning and calibration](07-Commissioning-and-Calibration.md).

## 7. Security testing

| Layer | Run |
|---|---|
| Edit-time scan (agent hook) | Automatic on each agent edit |
| .NET security analyzers as errors | Every build |
| Repository scan (39 rules, 14 CWEs), with self-test | `node scripts/verify.mjs security` |
| Architecture and security reflection tests | `node scripts/verify.mjs unit` |
| Behaviour tests with attack payloads | `node scripts/verify.mjs e2e` |
| Dependency audits (NuGet, npm) | `node scripts/verify.mjs security` (needs registry access) |
| OWASP ZAP baseline and OpenAPI scan | Pipeline `Security-zap.yaml` (Phase 1), weekly and before each release |
| External penetration test | Before the pilot goes live; once per major release |

Details in [Security guide](13-Security-Guide.md).

## 8. verify.mjs scopes

| Scope | Runs |
|---|---|
| `quick` | Security scan of changed files and the docs text rules (used by the Stop hook) |
| `build` | `dotnet build Ariva.slnx` with analyzers as errors (`--no-restore` supported) |
| `unit` | Build, then `Ariva.UnitTests` |
| `integration` | `Ariva.IntegrationTests` (needs Docker) |
| `web` | Ariva.Web: `npm ci` if needed, `svelte-check`, Prettier lint, build |
| `e2e` | `Platform/Testing/Ariva.E2E`: `npm ci` if needed, then `npx playwright test` |
| `security` | Scanner self-test, full scan, NuGet and npm audits |
| `docs` | Markdown text rules: no em or en dash characters, no double hyphens in prose |
| `backend` | `unit` plus `security` (the default) |
| `all` | Everything except integration (add `--with-integration`) |

The script is cross-platform (Windows, Linux, CI) and has no dependencies beyond Node.

## 9. In CI

| Pipeline | File | Runs |
|---|---|---|
| PR validation (branch policy on trunk) | `Platform/Cloud/Ariva.Cicd/AzureDevOps/Common/Analyze-solution.yaml` | .NET 10 restore and build (Release), unit and integration test projects with published results, web `npm ci`, `npm run check`, `npm run build` |
| Image builds | `K8s/Build-k8s-<service>.yaml`, fan-out `Build-k8s-all.yaml` | Docker build and push per service on trunk changes to its paths |
| Release to dev | `K8s/Release-ariva-k8s-dev.yaml` | Secrets and `helmfile apply` |

Planned additions: the repository scan and dependency audits in PR validation, the e2e suite against a deployed stack, a nightly dependency audit, and the ZAP pipeline.

## 10. Definition of done for a story

A story is done when its acceptance criteria pass, the tests for every control it touches pass, `node scripts/verify.mjs security` passes, and the security reviewer has signed off its checklist (see `../docs/security/cwe-controls.md`).
