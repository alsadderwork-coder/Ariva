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
| Visual regression | `Ariva.E2E/tests/visual` (Playwright `toHaveScreenshot`, baselines in `tests/visual/__screenshots__`) | Sign-in, live operations, immigration (English and Arabic, light and dark) and a passenger display: an unintended change of layout or design tokens fails | ARV-075; rendered only in the pinned Playwright image |
| Contract tests (AMAN feed) | `Ariva.UnitTests/Contracts` (PactNet 5) | Ariva's message pact for the four AMAN V1 contracts (examples read by Ariva's strict reader and rules), its matchers, no identifier member, and the provider harness of wiki 08 run against the simulator's AMAN, with broken messages that must fail it | `AmanFeedPactTests` (ARV-068); the pact is the `aman-pact` CI artifact |
| Event catalogue | `Ariva.UnitTests/Messaging/AsyncApi` | `docs/architecture/asyncapi.yaml` and the wiki 08 topic table equal the code; references resolve; consumer groups exist in code | `AsyncApiTests` (ARV-067); the AsyncAPI CLI validates the document in CI |
| Property-based tests | `Ariva.UnitTests/Properties` (CsCheck) | Formula invariants over generated inputs (docs/domain/formulas.md, "Property tests"); every parser at Ariva's edge given random, truncated, mutated and retyped payloads answers with a result or Ariva's reason, never an exception (CWE-120) | ARV-070; `ARIVA_FUZZ_ITERATIONS` for a deeper run |
| Mutation tests | Stryker.NET (`Ariva.UnitTests/stryker-config.json`, `.github/workflows/mutation.yml`) | The unit tests catch real faults in the pure engines: formulas, the queue state engine and bins, realised wait attribution, the nowcast, e-gate coupling, the arrival wave | Weekly and on demand (ARV-069); break below 70 percent, target 80 |
| Replay and golden scenario tests | Ariva.Simulation.Api reference scenario | The seeded day (seed 9303) produces the same outputs every run: alert at 18:05, degraded zone 18:20 to 18:30, SLA breach at 19:10 | `ScenarioParityTests` (bit for bit against `sim.js`), `ReferenceScenarioTests` (ARV-027) |
| Conformance tests per sensor family | `Emulators/Sensors/<Family>/samples/` | Each adapter maps recorded payloads to the expected canonical events, including malformed and oversized payloads | Planned per family |
| AODB replay | Replay harness with recorded feeds | Adapters handle real feeds: out-of-order messages, identity changes, stale feeds | Planned; recorded feeds from each pilot airport join the suite |
| Load test | `Platform/Testing/Ariva.LoadTests` (a console harness: HTTP through Apizr, the hub over a plain WebSocket, StackExchange.Redis), run by `Ariva.E2E/tests/api/load.spec.ts`; `functional/live-under-load.spec.ts` | 40 sensors at their push rate and 3x, 200 live hub and display connections, 413 and 429 rather than failures; p50, p95, p99, throughput, CPU and memory per host (wiki 05, sizing notes); a live screen keeps up while 40 others watch | Smoke in every E2E run; the full run (`ARIVA_LOAD_MODE=full`) on demand, recorded as lab measurements. The site-scale 15,000 messages a second stays with Phase 1 Hardening |
| Failure-mode tests | `Ariva.IntegrationTests/Faults` (Testcontainers.Toxiproxy; PostgreSQL, Redis and Kafka reached only through Toxiproxy) | Broker cut off: the outbox holds, readiness reports it, a direct publish fails (Ingest answers 503), the relay resumes in order per key. Redis cut off and stalled: publishes fail within the client timeout with what Stream's checkpoint catches, reads fail as outages (503), the subscription comes back without a restart. Database stalled and cut off: a cold credential lookup fails within 5 seconds (503), the sensing archive waits the outage out and stores each batch once | Partly done (ARV-072). Pod crash, failover and clock drift stay with Phase 1 Hardening |
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

### Mutation testing (Stryker.NET)

Agents write many tests; mutation testing checks that they would catch a real fault. Stryker.NET changes the engine code one small fault at a time (a `<` for a `<=`, a `+` for a `-`, a removed statement) and runs the unit tests that exercise it; a mutant the tests do not notice "survives".

- Scope (`Platform/Backplane/Ariva.UnitTests/stryker-config.json`): in Ariva.Core, `Queueing/QueueStateEngine.cs`, `QueueBins.cs`, `ZoneProcessor.cs`, `WaitStatistics.cs` (realised wait attribution), `Nowcast.cs`, `Border/EgateCoupling.cs`, `Flights/ArrivalWave.cs` and, since ARV-104e, the F18 comparison engine `Validation/Comparison/*.cs`; Stryker records which unit tests cover each mutant and runs only those against it. Snapshot persistence and guard clauses are left out. A story that adds an engine can run Stryker on its own files only: from `Platform/Backplane/Ariva.UnitTests`, `dotnet tool run dotnet-stryker -- --mutate "**/Validation/Comparison/*.cs" --output <folder>` (the command line's `--mutate` replaces the configuration's list; everything else, thresholds included, comes from `stryker-config.json`).
- Thresholds: the run fails below 70 percent of mutants killed; 80 is the target. Surviving mutants are triaged in `backlog/progress.md`: a missing test is added, an equivalent mutant (a change no input can observe) is recorded as such.
- When: weekly (Saturday night) and on demand (Actions, mutation, Run workflow); the HTML and JSON reports are the `mutation-report` artifact. Locally: `node scripts/verify.mjs mutation` (one to three hours, mostly the queue engine; the report goes to `.verify/stryker`). It runs in its own worktree of the current commit (uncommitted changes are not mutated) under its own lock, because Stryker replaces Ariva.Core in the unit tests' output while it runs; builds and story gates may use the main checkout meanwhile. `verify.mjs` turns the test platform's telemetry off: refused by a network proxy, it made Stryker's initial test run take 80 minutes instead of about 15 (2026-10-09).
- Runner: Stryker's Microsoft Testing Platform runner (`test-runner: mtp`; the unit test executable has `UseMicrosoftTestingPlatformRunner`, while `dotnet test` still runs through VSTest). Under VSTest Stryker could neither activate mutants nor capture coverage in the xUnit v3 test process, so every mutant "survived". The AMAN pact tests skip themselves in a Stryker run: the Pact FFI aborts Stryker's test server, and they test no engine.
- Tool: `dotnet-stryker` is pinned in its own manifest, `Platform/Backplane/Ariva.UnitTests/.config/dotnet-tools.json` (restore it from that folder). With SDK 10.0.4xx, restoring it from the same manifest as `aspire.cli` fails ("not contained in the package"), which failed every job that restores the root tools, the E2E run's smtp4dev included.
- Compile errors and safe mode: when a mutant does not compile and Stryker cannot tell which one it was, it removes every mutant of the method and counts them as compile errors (its safe mode, "Safe Mode! Stryker will remove all mutations in ..."), so the method is not measured and the score does not show the gap. The first checkpoint (2026-10-08) found 14 methods in safe mode; ARV-069a restructured them. The rule for the engines: **no `out` variable or pattern variable declared inside a condition.** `if (code is null || !_desks.TryGetValue(code, out var desk))`, `if (x is not { } v || ...)` and `while (list.First is { } first && ...)` become a declaration in its own statement (`Desk desk = null;` before the condition, `var v = x; if (v is null ...)`, `HasValue` and `Value`, or a `for` loop over a local), because a mutant that short-circuits the condition leaves the variable unassigned (CS0165). Two other shapes did the same: a concatenation of interpolated strings passed to `string.Create` (CS1620; pass one interpolated string) and a nested `HashCode.Combine` (CS0411; use typed locals).
- Gate: `verify.mjs mutation` (and the checkpoint's mutation step) runs Stryker through `scripts/mutation-run.mjs`, which prints Stryker's output, keeps it in `.verify/mutation.log` and fails the step when any method is in safe mode or no final score was printed, even when Stryker exits zero. `node scripts/mutation-run.mjs --check <log>` checks an earlier log; `--self-test` proves the check.

## 3. Integration tests (Testcontainers)

Containers: `timescale/timescaledb-ha` (PostgreSQL 17 with TimescaleDB), Kafka, Redis. Shared fixtures (container lifetime, connection strings, script runner) live in `Ariva.IntegrationTests/Setup`. Typical tests: the Timescale script runner applies all scripts and records them; a checksum change stops startup; COPY writers and upserts are idempotent by (zone, bin start, revision); a consumer replays after a rebalance without double counting.

Failure injection (ARV-072): `Faults/FaultsFixture` starts PostgreSQL, Redis and Kafka on their own Docker network behind one Toxiproxy (`ghcr.io/shopify/toxiproxy:2.12.0`), driven through its HTTP API with an Apizr client; Kafka advertises the proxy's address, so broker connections after bootstrap go through it too. Tests cut a dependency off (proxy disabled), stall it (Toxiproxy's `timeout` toxic, nothing comes back) and restore it while the code under test keeps running. The checks are the indicators operators and screens rely on: readiness, 503 with Retry-After (`DependencyOutage`), the publication time that turns screens and boards stale, and the stored rows (outbox, sensing archive). About four minutes; they run with the rest of the integration suite.

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

Against Ariva.AppHost (ARV-066): `node scripts/e2e-apphost.mjs [playwright arguments]` runs the same suite against hosts the AppHost starts, configured exactly as Playwright would start them (the config writes each host's variables when `ARIVA_E2E_WRITE_HOST_ENV` names a file; the AppHost applies them with `AppHost:HostEnvironmentFile`), on throwaway containers. It is for local runs and agents; CI keeps compose. The integration tests use an existing PostgreSQL instead of their own container when `ARIVA_IT_POSTGRES` holds a superuser connection string (they recreate only their `ariva_it` and `it_*` databases there).

Load run (ARV-071): `tests/api/load.spec.ts` provisions a site of its own (a published queue zone, 41 canonical sensors, the displays, one screen sign-in per five screens, since the hub holds at most 8 connections per session) and runs `Platform/Testing/Ariva.LoadTests` (built with the backend) with the credentials in its environment. Every E2E run does the smoke size (40 screens, 10 displays, 15 seconds at 0.2 pushes a second per sensor, the device limit lowered to 120 a minute for the flood); `ARIVA_LOAD_MODE=full npx playwright test tests/api/load.spec.ts` does the lab measurement (150 screens, 50 displays, 120 seconds at 1 push a second, the production device limit). The harness writes `.verify/load/<mode>/load-report.json` and `.md` (p50, p95, p99, throughput, CPU and memory per host from /proc) and exits 1 on any failed request, a limit that did not answer 413 or 429 with Retry-After, a screen that could not join or was dropped, under 95 percent of snapshots delivered, or a display board that did not answer 200. The harness is our own rather than NBomber, whose licence is paid for organisational use (decision log). `functional/live-under-load.spec.ts` watches the live screen in a browser while 40 other hub connections share the zone and snapshots arrive five times a second.

Visual regression (ARV-075): `tests/visual/screens.spec.ts` compares 14 screenshots (sign-in, live operations and immigration in English and Arabic, light and dark; the passenger display, bilingual, light and dark) with the baselines in `tests/visual/__screenshots__`. Only one browser renders them: the official Playwright image pinned by digest in `scripts/visual-browser.mjs` (the same in CI and on a developer machine), reached as a Playwright server on 127.0.0.1:3123, while the tests and the hosts run as usual; Ariva.Web bundles its fonts (DM Sans, Noto Sans Arabic), so the image only supplies fallbacks. Run it on its own and before the other suites, on a fresh database (the demo seed): `node scripts/verify.mjs visual` (Docker needed), or `node scripts/visual-browser.mjs start` and `ARIVA_E2E_VISUAL_WS=ws://127.0.0.1:3123/ npx playwright test --project=visual`. The live values are planted (fixed snapshots for every DMO queue zone, as Ariva.Api.Stream writes them), the publication times in the zones table are replaced by one fixed text (their digits change the column widths), and what follows the clock or AMAN's feed (times, the arrival wave, the wait chart, alerts, desk states, desks and e-gates) is masked. A pixel counts as changed at a colour distance of 0.01, and a screen fails above 0.05 percent of its pixels: a five-unit change of the primary colour fails. The run removes what it planted. In CI the visual step runs first in the E2E job; a failure uploads `visual-diffs` (expected, actual and diff images) and the rest of the suite still runs. To accept a deliberate change, run the `ci` workflow by hand with `update-visual-baselines` ticked, download the `visual-baselines` artifact, review it and commit it, or run `node scripts/verify.mjs visual --update` locally (the same image gives the same pixels). When @playwright/test is upgraded, pin the matching image digest and regenerate.

Alert emails (ARV-040): the run starts smtp4dev from the pinned local tool (`.config/dotnet-tools.json`, `dotnet tool restore`) on 127.0.0.1 (SMTP 25251, web API 5081, `ARIVA_E2E_SMTP_PORT` and `ARIVA_E2E_SMTP4DEV_URL` to change them), so neither a developer machine nor CI pulls an image; `alert-emails.spec.ts` reads what arrived through its API. The integration tests use a small SMTP server in the test process (`Setup/SmtpSink.cs`) that can refuse a recipient or drop the connection.

## 5. Replay and golden scenario tests

The reference scenario is the prototype's seeded day at the fictional DMO airport, whose topology and zone profile v12 the demo seed creates in dev and demo (ARV-019; layout in the [Administration guide](11-Administration-Guide.md)): seed 9303, simulated from 17:40 in the prototype, with scripted events:

| Time | Event | Expected behaviour |
|---|---|---|
| 18:05 | A visitor-heavy arrival wave | The arrivals Visitors nowcast passes 15 minutes and rule R-001 fires |
| 18:20 to 18:30 | Sensor S-17 over the arrivals hall offline | The Visitors zone is `Degraded`, the wait shows a band, a device alarm appears |
| 19:10 | Handler B's check-in island C after a shift change leaves 5 of 12 counters open | SLA breach for consecutive 15-minute bins, provisional first, final once the queue clears |

Same seed, same output: golden-file tests lock the behaviour so that changes to the engine show up as reviewed diffs. Created objects use their own sub-seeded random streams so they do not disturb the scripted events.

The engine is ported to C# in `Ariva.Simulation.Api/Scenarios/Engine` (ARV-027) and must reproduce the prototype bit for bit. `node scripts/simulation/reference-golden.mjs` runs `sim.js` for six cases and writes SHA-256 fingerprints of every output family (schedule, roster, the run, servers, expected and day-ahead runs, alerts, bins, states, the Monte Carlo forecast, recommendations, waits, servers at a minute, demand and hall curves) to `Ariva.UnitTests/Simulation/reference-golden.json`. `ScenarioParityTests` checks the port against every fingerprint, `ReferenceScenarioTests` checks the three scripted events above, and `ScenarioEndpointTests` checks the simulator's operator keys and endpoints. The sensor emulator (ARV-028) is checked by `SensorTrafficTests` (every push of every sensor maps through Ingest's own canonical and Xovis mappers, names only published lines and zones, and conserves passengers), `SensorEmulatorTests` (the demo clock, pause, speed, jump, failures) and the E2E `sensor-emulator.spec.ts` (a 10-minute run at speed 60 against the running Ingest, accepted equal to expected). `node scripts/verify.mjs unit` also fails when the golden file no longer matches `sim.js`.

The golden replay (ARV-036) locks the stream's behaviour on the same evening: `GoldenReplayTests` runs the sensor emulator's pushes of S-15, S-17 and S-50 from 17:00 to 20:30 through Ingest's own ingest, puts them in the archive's form and replays them through `ZoneReplay`. The replay must show the Visitors nowcast passing 15 minutes at 18:05, S-17 out from 18:20 to 18:31 with the Visitors zone degraded and its 18:15 and 18:30 bins marked, and island C's 19:00, 19:15 and 19:30 bins above 15 minutes; its output hash must equal `Ariva.UnitTests/Replay/replay-golden.json`, run after run and after a fresh ingest; and its export must fail verification when any line is changed, dropped, added or moved. `GoldenReplayArchiveTests` writes the same evening to the archive in TimescaleDB (pseudonymous tracks, health reports), replays it with the command's runner under the demo seed's profile version 12 and must reach the same output hash. A change to the engine that changes the hash is reviewed as a diff of the outputs, and the golden value is updated only with the human's approval (`/replay`).

Alert evaluation (ARV-038) is tested on the same evening: `AlertEvaluatorTests` judges the replayed Visitors minutes with R-001 and must raise at 18:05, the minute the prototype raises it, both as one fold (backtest) and one minute at a time with the state stored as JSON between minutes (live), with the same transitions; on random series the two must also agree for every sustain and clear setting. `AlertEvaluationTests` writes the evening's stream outputs to TimescaleDB, ticks the live evaluation once a minute from 17:00 to 20:35 with the demo airport's R-001 to R-005, and requires every rule's backtest to list exactly the alerts the live evaluation stored.

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
| OWASP ZAP API and baseline scans (ARV-063) | `node scripts/verify.mjs zap` (Docker and the E2E database environment), and `.github/workflows/security-zap.yml` weekly and on demand; fails on an untriaged High risk alert |
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
| `zap` | The dynamic security scan (ARV-063): `npx playwright test --project=zap` with `ARIVA_E2E_ZAP=1`; reports in `Platform/Testing/Ariva.E2E/zap-reports` |
| `docs` | Markdown text rules: no em or en dash characters, no double hyphens in prose |
| `backend` | `unit` plus `security` (the default) |
| `all` | Everything except integration (add `--with-integration`) |
| `story` | The per-story gate: `backend` and `docs` always, `web` when the web app changed, integration filtered by `--integration` and E2E for `--specs` plus a smoke set (health, auth, security baseline, permission matrix, shell); both in full when the change is wide or adds a Timescale script ([test cadence](../docs/harness/test-cadence.md)) |
| `checkpoint` | The full suite on a committed tree every 5 stories, at each phase end and before go-live: backend, docs, web, integration, visual, E2E, and Stryker on the engine code changed since the previous checkpoint; records `backlog/checkpoint.json` |

The script is cross-platform (Windows, Linux, CI) and has no dependencies beyond Node.

## 9. In CI

| Pipeline | File | Runs |
|---|---|---|
| CI on every push and pull request (GitHub Actions) | `.github/workflows/ci.yml` | Security gate and docs rules; AsyncAPI validation (its own job); Release build, unit tests (publishing the AMAN pact as `aman-pact`) and integration tests with Testcontainers; web check, lint and build; the e2e suite with docker compose |
| Security scans | `.github/workflows/security-scan.yml` | Trivy filesystem scan, Semgrep, SBOM, on pull requests and weekly |
| Mutation tests | `.github/workflows/mutation.yml` | Stryker.NET on the engines, weekly and on demand; report as the `mutation-report` artifact |
| Azure DevOps PR validation (deployment mirror) | `Platform/Cloud/Ariva.Cicd/AzureDevOps/Common/Analyze-solution.yaml` | .NET 10 restore and build (Release), unit and integration test projects with published results, web `npm ci`, `npm run check`, `npm run build` |
| Image builds | `K8s/Build-k8s-<service>.yaml`, fan-out `Build-k8s-all.yaml` | Docker build and push per service on trunk changes to its paths |
| Release to dev | `K8s/Release-ariva-k8s-dev.yaml` | Secrets and `helmfile apply` |

Planned additions: the repository scan and dependency audits in PR validation, the e2e suite against a deployed stack, a nightly dependency audit, and the ZAP pipeline.

## 10. Definition of done for a story

A story is done when its acceptance criteria pass, `node scripts/verify.mjs story` passes (which includes `security`), the tests for every control it touches pass, and the security reviewer has signed off its checklist (see `../docs/security/cwe-controls.md`). The full suite runs at the next checkpoint ([test cadence](../docs/harness/test-cadence.md)); a checkpoint failure is fixed before the next story starts.
