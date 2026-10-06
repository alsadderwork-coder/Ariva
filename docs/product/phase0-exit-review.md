# Phase 0 exit review (ARV-065)

The gate before the pilot (roadmap, Gate 1). Dated 2026-10-06. It records the state of the demo core when Phase 0 closes: the gates, the coverage, the full-repository security review, what is still open, and the questions the pilot contract must answer.

## 1. Scope delivered

- `backlog/prd-phase0.json`: 77 of 79 stories pass; this story stays open until the six fault tests have run. ARV-002 (container and chart hardening) is the exception: every automated criterion passes, and its last criterion is a release on the dev cluster, which needs a reachable cluster and GitHub Actions minutes (neither available on 2026-10-06).
- The scripted demo (ARV-064) plays the reference evening at DMO end to end through every host; the runbook is wiki/10, section 4.11.

## 2. Gates

Run on 2026-10-06 in the cloud session on `local/exit-review-candidate`: main at `5138e2c` plus the three open pull requests (#56 shadcn setup, #57 backlog follow-ups, #58 dropdown fixes) and this story's documents, which is main once they merge. GitHub Actions does not start on this account, so these are local runs of the CI commands.

| Gate | Result |
|---|---|
| `node scripts/verify.mjs backend` | PASS: build with analyzers, 2,324 unit tests, golden replay unchanged, security gate, base images, chart security, NuGet and npm audits |
| `node scripts/verify.mjs integration` | 316 passed, 1 skipped; the 6 Toxiproxy fault tests (`Faults/*`) could not run, because their image (`ghcr.io/shopify/toxiproxy:2.12.0`) is downloaded from `pkg-containers.githubusercontent.com`, which the cloud session's network policy blocks. **Open: run them where that host is reachable.** |
| `node scripts/verify.mjs web` | PASS: svelte-check, lint, build |
| `node scripts/verify.mjs e2e` | PASS: 506 passed, 1 skipped (API end-to-end and Playwright functional, with TimescaleDB, Kafka and Redis) |
| `node scripts/security/scan.mjs` | PASS: 982 files, 0 errors, 6 warnings (all allowlisted); `--self-test` 42 rules fire, 0 false positives, all 14 CWEs covered |
| `node scripts/verify.mjs docs` | PASS |
| Visual baselines | 14 of 14 (ARV-064, 2026-10-05) |
| Demo rehearsal | All three scripted events (ARV-064, 2026-10-05) |

## 3. Coverage

Line coverage of the .NET code from the unit and integration suites (coverlet, Cobertura, merged by line), on 2026-10-06 at commit `5138e2c` (main):

| Assembly | Lines | Covered | Line coverage |
|---|---|---|---|
| Ariva.Api.Common | 2,451 | 1,917 | 78.2% |
| Ariva.Api.Cronz | 1,204 | 567 | 47.1% |
| Ariva.Api.Ingest | 949 | 537 | 56.6% |
| Ariva.Api.Integration | 701 | 209 | 29.8% |
| Ariva.Api.Main | 1,123 | 485 | 43.2% |
| Ariva.Api.Stream | 421 | 42 | 10.0% |
| Ariva.AppHost | 192 | 173 | 90.1% |
| Ariva.Business.Contracts | 39 | 39 | 100.0% |
| Ariva.Core | 10,202 | 9,284 | 91.0% |
| Ariva.Di | 974 | 783 | 80.4% |
| Ariva.Infra | 13,606 | 11,911 | 87.5% |
| Ariva.ServiceDefaults | 93 | 85 | 91.4% |
| Ariva.Simulation.Api | 4,469 | 4,138 | 92.6% |
| **Total** | 36,424 | 30,170 | **82.8%** |

How to read it:

- The engines and services are well covered: Ariva.Core 91.0%, Ariva.Infra 87.5%, the simulator 92.6%.
- The hosts look low (Ariva.Api.Stream 10.0%, Ariva.Api.Integration 29.8%, Ariva.Api.Main 43.2%) because their controllers, hubs and workers are exercised by the E2E suite, which starts each host as its own process; the collector measures only the test processes. Every endpoint has API end-to-end tests with its authorization cases (CLAUDE.md, non-negotiable 3), and the hosts' wiring is checked by `HostServicesTests`.
- The 6 Toxiproxy fault-injection tests did not run in the cloud session (their ghcr.io image is blocked there), so the fault paths they cover are not in these numbers.
- The web app has no line coverage; its screens are covered by the Playwright functional suite and the visual baselines.

To reproduce: `dotnet test Platform/Backplane/Ariva.UnitTests --collect:"XPlat Code Coverage"` and the same for `Ariva.IntegrationTests` (Docker required), then merge the two `coverage.cobertura.xml` files.

## 4. Security review

Independent security reviewer, full repository (not a diff), all 14 CWEs: **PASS with conditions.** No CWE has an exploitable gap in code; no secrets are committed (`.mcp.json` holds only `${...}` placeholders); no officer, traveller or document identifier appears in code, SQL or the web app.

| CWE | Status |
|---|---|
| 78, 77 Command injection | No process execution in production code (`ForbiddenDependencyTests`) |
| 94 Code injection | No eval or dynamic scripting; hash-mode CSP |
| 918 SSRF | `OutboundTransport` checks every resolved address before dialling, no redirects or proxy (`OutboundTests`, `outbound-endpoints.spec.ts`) |
| 862 Missing authorization | Default deny; every endpoint listed (`EndpointInventoryTests`, `PermissionMatrixTests`) |
| 863 Incorrect authorization | `[SiteScoped]` and `ISiteScope` on site-bound data; hub joins re-check site and role (`SiteScopeTests`) |
| 306 Missing authentication | Anonymous routes allowlisted; step-up MFA for critical actions (`step-up.spec.ts`) |
| 287 Improper authentication | PBKDF2, constant-time comparisons, TOTP replay guard for users; the integration API's per-request TOTP can be replayed (ARV-088, Phase 1) |
| 501 Trust boundary | `EntityBindingTests`; device transports and alert metrics the system cannot serve are refused (#58) |
| 269 Privilege management | Role assignment rules (`AdministrationTests`); one shared database role (ARV-083, Phase 1) |
| 384 Session fixation | Refresh rotation with family revocation, HttpOnly SameSite=Strict cookie (`sessions.spec.ts`) |
| 89 SQL injection | Constant SQL with bound parameters; sorts through allowlists |
| 120 Buffer overflow | No unsafe code; body, depth and size limits |
| 79 XSS | No `{@html}`, `innerHTML` or `eval`; strict CSP; XSS probes in 10 functional specs |

Conditions (see section 5): the five `PENDING` allowlist approvals before the pilot contract (ARV-097, the other condition, is done); ARV-085 with ARV-087, and ARV-082 before go-live (ARV-098 is done).

## 5. Open items carried into Phase 1

Before the pilot contract:

- **ARV-097** (done, 2026-10-06): MassTransit sent usage telemetry (versions, the server's time zone, Kafka topic names) to an external endpoint from four hosts. It is now off in code, a host refuses to start if `MASSTRANSIT_USAGE_TELEMETRY` turns it back on, and the E2E log scan checks for it.
- **Five `approvedBy: PENDING` entries in `security/allowlist.json`**: the anonymous sign-in, refresh and auth routes, the mock AMAN sign-in, and the two reviewed SSRF exceptions in the outbound connector. Only the owner may approve them (CLAUDE.md, non-negotiable 1).
- **The six fault-injection tests** must run green where the Toxiproxy image is reachable (a developer machine, or the cloud environment with `pkg-containers.githubusercontent.com` allowed).

Before go-live:

- **ARV-098** (done, 2026-10-06): an image started without DOTNET_ENVIRONMENT fell back to vm-local (development keys, sign-in without TOTP, SchemaUpdate, the demo seed). Images now ship no `environment.json`, a host without a known environment refuses to start, and the chart test checks every .NET workload sets it.
- **ARV-085 and ARV-087**: the TickerQ dashboard and the health probes are reachable through the public ingress; no NetworkPolicies.
- **ARV-082**: no TLS to PostgreSQL, Redis or between pods in k8s-prd.
- **ARV-002**: the release on the dev cluster (its last criterion), once a cluster and Actions minutes are available.

Phase 1: the other ASVS gap stories (ARV-083, 084, 086, 088 to 096; ARV-080 is done); ASVS 5.0 Level 2 stands at 9 Gap and 68 Partly rows after ARV-080 (`docs/security/asvs-l2.md`). Product: the Phase 1 candidates in `backlog/phase1-candidates.md`, including ARV-099 (the shadcn-svelte component layer). Known product limits: overflow occupancy and desks-below-plan are not evaluated yet (R-002 shows "Not evaluated yet"); five of seven sensor transports are not built.

## 6. Questions for the pilot contract

77 open questions, grouped by who answers them, are in [pilot-to-confirm.md](pilot-to-confirm.md): 9 block the contract, 13 the site survey and 30 go-live. The five most urgent for the contract are TC-04 (validation sample size and tracer count), TC-62 (dispute window, which sets raw sensing retention), TC-75 with TC-70 to TC-72 (whether anonymous tracks are personal data, and the passenger notice), TC-34 (recovery objectives and a standby site) and TC-42 (which module licenses the SLA engine).
