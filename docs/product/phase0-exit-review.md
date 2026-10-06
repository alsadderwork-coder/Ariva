# Phase 0 exit review (ARV-065)

The gate before the pilot (roadmap, Gate 1). Dated 2026-10-06. It records the state of the demo core when Phase 0 closes: the gates, the coverage, the full-repository security review, what is still open, and the questions the pilot contract must answer.

## 1. Scope delivered

- `backlog/prd-phase0.json`: 78 of 79 stories pass once this one does. ARV-002 (container and chart hardening) is the exception: every automated criterion passes, and its last criterion is a release on the dev cluster, which needs a reachable cluster and GitHub Actions minutes (neither available on 2026-10-06).
- The scripted demo (ARV-064) plays the reference evening at DMO end to end through every host; the runbook is wiki/10, section 4.11.

## 2. Gates

GATES_PLACEHOLDER

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

SECURITY_PLACEHOLDER

## 5. Open items carried into Phase 1

OPEN_PLACEHOLDER

## 6. Questions for the pilot contract

77 open questions, grouped by who answers them, are in [pilot-to-confirm.md](pilot-to-confirm.md): 9 block the contract, 13 the site survey and 30 go-live. The five most urgent for the contract are TC-04 (validation sample size and tracer count), TC-62 (dispute window, which sets raw sensing retention), TC-75 with TC-70 to TC-72 (whether anonymous tracks are personal data, and the passenger notice), TC-34 (recovery objectives and a standby site) and TC-42 (which module licenses the SLA engine).
