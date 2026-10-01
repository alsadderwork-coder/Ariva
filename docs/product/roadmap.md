# Ariva roadmap and delivery estimate

Condensed from D6 (Roadmap and Delivery Estimate, 2026-09-28), with Ariva names and the decisions of 2026-10-01 applied. All estimates are judgement, not measurement: replace them with measured velocity after three sprints.

## Summary

- The MVP is the Border module at one AMAN arrivals hall, validated against manual counts: 23 to 38 developer-weeks with Claude Code. It fits only if the developer goes full-time at the contract gate; at half-time throughout, the pilot would take well over a year.
- Realistic scenario, assuming a pilot contract in April 2027: demo core by spring 2027, pilot acceptance between September and December 2027, v1 (Airport Operations module, penalties, forecasting) by mid-2028 with two developers.
- The calendar is set by three things Claude Code does not touch: sensor procurement and installation, the client's change control for the AMAN changes, and validation fieldwork. Software is on the critical path only because of the half-time constraint.
- First hire is a field engineer at contract signature; the second developer comes at pilot acceptance. The biggest single lever on the pilot date is funding the developer full-time before the contract (open decision).

## Phases and gates

Four phases separated by three gates. Dates are a scenario; every later date moves with the contract gate.

| Phase or gate | Dates (scenario) | Team | Scope headline |
|---|---|---|---|
| Phase 0: demo core | Oct 2026 to Apr 2027 | Developer half-time; architect and PO 20 to 30% | Queue engine, zones, simulator and lab sensors, dashboard, display page. Nothing that depends on a site |
| Gate 1: pilot contract signed | Apr 2027 | | Contract with a border authority for one AMAN arrivals hall |
| Phase 1: pilot MVP | Apr to Sep or Dec 2027 | Developer full-time plus a field engineer; architect 40% or more | One AMAN arrivals hall, AMAN desk and gate data, validation campaign |
| Gate 2: pilot accepted | Sep to Dec 2027 | | Acceptance criteria met (below) |
| v1: first sale | Oct 2027 to Jun 2028 | Two developers | Airport Operations module, SLA and penalty engine, forecasts and staffing |
| Gate 3: first commercial sale | | | |
| v2: scale | From H2 2028 | Team per sales | What-if simulation, national views, queue balancing |

Inconsistency in D6 to note: the roadmap figure says dates assume a pilot contract by March 2027, while the text and calendar scenario assume April 2027. This document uses April 2027.

Phase 0 calendar: 9 to 13.5 developer-weeks at half-time is 22 to 32 calendar weeks with contingency; starting early October 2026 that ends between early March and mid-May 2027. At full-time it would be 11 to 16 calendar weeks.

## Capability by phase

| Capability | MVP (pilot) | v1 | v2 |
|---|---|---|---|
| Sensor families | One, matched to the pilot hall | Second family (LiDAR perception) | More as sales require |
| Zones, lines, versioned profiles | Yes | Yes | Yes |
| Realised wait, nowcast, overflow | Yes | Yes | Yes |
| Desk state from AMAN and sensors | Yes | Plus common-use check-in logins | Yes |
| E-gate analytics | Basic: utilisation and rejects | Joined to lane forecasts | Yes |
| Alerts | Rules, in-app push, email, acknowledgement | Escalation workflows, SMS, operations-centre webhook | Yes |
| Supervisor dashboard and passenger display page | Yes | Yes | Yes |
| Reports | Daily and weekly basics, CSV | Full set, PDF, evidence packs | Yes |
| Flight data | Schedule-file (SSIM) import | AIDX plus one vendor AODB on a real feed | More vendors |
| Arrival-wave alert | Only if an AODB feed is already available | Yes | Yes |
| Lane-mix forecast from API | No | Yes | Yes |
| Show-up forecasts and staffing recommendation | No | Yes | Yes |
| Airport Operations module (check-in, security, handler tenancy) | No | Yes | Yes |
| SLA and penalty engine with disputes | No | Yes | Yes |
| Border-to-airport aggregate feed | No | Yes | Yes |
| Offline install bundle and module licensing | Basic | Yes | Yes |
| What-if simulation, national views, queue balancing | No | No | Yes |

## MVP scope

The smallest thing a border authority would pay for: one arrivals hall measured end to end, with desk state from AMAN, live waits on screens, alerts to the supervisor, and a validation report proving the numbers. Without the validation report nobody pays; without AMAN desk state Ariva is a worse version of a sensor vendor's own software.

In the MVP:

- One sensor family chosen for the pilot hall's ceiling, plus the simulator (Ariva.Simulation.Api).
- Zone and line editor with versioned profiles.
- Realised wait, nowcast, overflow detection, data-quality flags, conservation and track-completion checks.
- Desk state from AMAN aggregate contracts and sensor zones; basic e-gate utilisation and reject rates.
- Supervisor dashboard, alert rules with in-app push and email plus acknowledgement, a passenger display page in the site's languages.
- Daily and weekly reports, CSV export.
- Validation tooling: tracer and manual-count capture form, comparison report.
- Single-tenant deployment in the border authority's environment.

Phase 0 (demo core) is the subset that needs no site: skeleton and pipelines, zones from a config file, simulator plus two to four lab sensors, queue engine core, minimal dashboard and display page. It runs end to end on recorded lab data and synthetic flight waves (the reference scenario, seed 9303), which is what wins the pilot.

### Pilot acceptance criteria (proposed)

The first four come from D4; the rest are D6 proposals. Formulas in [../domain/formulas.md](../domain/formulas.md) F18.

| Criterion | Target |
|---|---|
| Count accuracy per 15-minute bin, each line | At least 95% |
| Realised-wait absolute error | Within the larger of 1 minute or 10% |
| Realised-wait bias | Within plus or minus 5% |
| Track completion | At least 90% |
| Desk-state agreement with observer log | At least 95% of observed minutes |
| Nowcast error against later realised wait | Median within 2 minutes for waits under 20 minutes |
| Availability during the pilot | 99% of operating hours |
| Ground-truth proof | Nowcast error with and without AMAN inputs, side by side; no target, the result is the asset |

### Choosing the pilot hall

AMAN live; ceiling under 6 m so one wide-footprint stereo sensor covers about 100 m2; a hall small enough to cover completely (roughly up to 20 desks plus e-gates); a supervisor who wants it; a local partner able to install. API availability matters for v1, not the MVP. If the hall is in Angola, check whether the API/PNR feed request to MININT has been granted before promising the lane-mix forecast there.

## Estimate

### Assumptions

- One experienced .NET developer who knows AMAN's patterns and uses Claude Code daily. A developer-week is five focused days on Ariva; half-time is two and a half.
- Estimates include automated tests and exclude field work, AMAN's release cycle at the client, and everything in the "does not accelerate" list below.
- Without Claude Code, multiply by roughly 1.5 to 2 (judgement).
- Calendar figures add 20% for AMAN support interruptions, leave and public holidays.

### MVP work breakdown (developer-weeks, low to high)

| # | Work package | Phase 0 | Phase 1 |
|---|---|---|---|
| 1 | Skeleton, pipelines, Helm, Kafka, PostgreSQL with TimescaleDB, observability, reusing AMAN templates (for Ariva: the AMAN-mirrored repository, NHibernate, Timescale script runner, TickerQ, Serilog and OpenTelemetry) | 1.5 to 2.5 | |
| 2 | Zones: from a config file in Phase 0; editor with plan calibration and versioned profiles in Phase 1 | 0.5 | 2 to 3.5 |
| 3 | Device gateway (Ariva.Api.Ingest), simulator and lab adapter; then the production adapter for the pilot's sensor family | 2 to 3 | 1 to 1.5 |
| 4 | Queue engine core; then quality checks, overflow and bin revisions | 3 to 4 | 1 to 2 |
| 5 | Desk state and AMAN aggregate contracts, including AMAN-side outbox changes | | 3 to 5 |
| 6 | Supervisor dashboard: minimal, then complete | 1.5 to 2.5 | 1 to 1.5 |
| 7 | Alerting: rules, push, email, acknowledgement | | 1.5 to 2.5 |
| 8 | Passenger display page; then languages and stale-data handling | 0.5 to 1 | 0.5 |
| 9 | Reports and export | | 1 to 2 |
| 10 | Authentication, roles, audit | | 1 to 1.5 |
| 11 | Validation tooling | | 1 to 1.5 |
| 12 | Hardening: failure-mode tests, load test at 15,000 messages per second, security fixes | | 1.5 to 3 |
| | Total | 9 to 13.5 | 14.5 to 24.5 |

Calendar: Phase 0 at half-time 22 to 32 calendar weeks; Phase 1 at full-time 17 to 29 calendar weeks, in parallel with procurement and installation.

### v1 work breakdown (developer-weeks, two developers)

| Work package | Estimate |
|---|---|
| Second sensor family (LiDAR perception adapter) | 1.5 to 3 |
| AODB: AIDX adapter, one vendor AODB on a real feed, canonical flight id map | 3 to 5 |
| Arrival-wave alert | 1 to 1.5 |
| Lane-mix forecast from API, including the AMAN-side contract | 2 to 3 |
| Python forecasting worker: show-up models, Monte Carlo waits, backtesting | 4 to 6 |
| Staffing optimiser | 2 to 3 |
| Airport Operations module: check-in and security zones, common-use login adapter, handler tenancy with row-level security | 3 to 5 |
| SLA and penalty engine: contracts, evaluation, exclusions, evidence packs, disputes | 5 to 8 |
| Recomputation and revision tooling for disputes | 1.5 to 2.5 |
| Border-to-airport aggregate feed | 1 to 2 |
| Alerting: escalation workflows (persisted state machines and TickerQ, replacing D6's Rebus sagas), SMS, webhooks | 1.5 to 2.5 |
| Wait-times API and signage integrations | 1.5 to 2.5 |
| Full reports and PDF | 2 to 3 |
| Offline install bundle, licensing, high-availability hardening | 2 to 3 |
| Total | 31 to 50 |

With two developers at about 1.8 effective (coordination costs something), plus contingency, v1 is 21 to 34 calendar weeks.

Architect and product owner time: roughly 20 to 30% through Phase 0 (backlog, reviews, ADRs, vendor calls), rising to 40% or more around the contract gate and the pilot, because the pilot is also a sales campaign (assumption).

## What Claude Code accelerates, and what it does not

Claude Code compresses the software, which is not the long pole. The developer will be waiting on site and vendor work unless that work starts during Phase 0, not after the contract.

| Work | Effect of Claude Code | Owner |
|---|---|---|
| Configuration screens, zone editor scaffolding, dashboards, display pages | Strong | Developer |
| Adapters against documented formats: sensor MQTT payloads, AIDX schema, ACRIS APIs | Strong, once documentation is in hand | Developer |
| Simulator, synthetic crowds, replay harness | Strong | Developer |
| Test suites: queue-engine property tests, contract tests, load scripts | Strong | Developer |
| Helm charts, pipelines, report templates, API specs, docs | Strong | Developer |
| AMAN-side outbox events | Strong; the codebase is known | Developer |
| Python forecasting code and backtesting | Strong | Developer, with the architect reviewing the modelling |
| Queue-engine edge cases and definitions | Partial: it writes the code, the product owner decides what "exit" and "final" mean | Product owner, then developer |
| Performance tuning, security hardening | Partial | Developer |
| Sensor procurement and import, including customs | None | Procurement, partner |
| Site survey, installation, cabling, mounting, calibration | None | Local partner, field engineer |
| Vendor SDK access, partner and licence agreements (stereo vendor, LiDAR perception) | None | Product owner |
| AODB access and a recorded feed from the airport | None | Product owner, with airport IT |
| AMAN production release through the client's change control | None | Product owner, with the client |
| Validation fieldwork: tracers, manual counts, observer logs | None | Field engineer, client staff |
| Security accreditation and client-approved penetration test | None | Product owner, with the client |
| DPIA per market; Angola video-surveillance authorisation if stereo is used | None | Product owner, with local counsel |
| KPI and penalty definitions agreed with the client | None | Product owner |

## Critical path

After the contract, three chains run in parallel and meet at commissioning. On these estimates the software chain is the longest, and only because the developer is half-time before the contract: every developer-week moved before the gate takes a week off the pilot.

| Chain | Steps and durations (weeks, assumptions) |
|---|---|
| Software | Phase 1 software: 17 to 29 |
| Hardware | Site survey 1 to 3, then sensor order, delivery and customs 4 to 10, then installation 1 to 3 (6 to 16 in total) |
| AMAN | AMAN change build, then client release: 5 to 13 |
| Security | Security review and penetration test: 2 to 8 |
| Joint | All chains, then calibration and commissioning 1 to 2, then validation campaign and report 2 to 3, then pilot accepted |

Durations are assumptions to replace with real quotes: sensor lead times from the vendor, customs from the local partner, change-control windows from the client.

Calendar scenario (contract signed April 2027):

| Period | What happens |
|---|---|
| Oct 2026 to Mar or May 2027 | Phase 0 demo core; vendor agreements; lab sensors; informal walk of candidate halls; pilot proposal |
| Apr to May 2027 | Contract. Site survey, sensor order, AMAN contract changes built, security review opened, AODB access request filed for v1 |
| Jun to Jul 2027 | Sensors delivered and installed; AMAN change released through the client's change control; Phase 1 software continues |
| Aug 2027 | Calibration, commissioning, two weeks of burn-in |
| Sep 2027 | Validation campaign and report; pilot accepted at the low end of the ranges |
| Oct to Dec 2027 | Where the high end of the ranges lands the same acceptance |

## Where a second person becomes necessary

| When | Role | Why one developer plus the architect cannot cover it |
|---|---|---|
| Pilot contract signed | Field or deployment engineer, possibly from the AMAN deployment team | Site survey, partner coordination, installation supervision, calibration, commissioning and validation are weeks on site. If the developer does it, software stops; if the architect does it, sales and product work stop |
| Pilot accepted (start of v1) | Second developer, ideally .NET with data and Python skills for the forecasting worker | v1 is 31 to 50 developer-weeks while the first developer also supports the pilot and AMAN. Start recruiting during Phase 1; hiring takes months |
| First airport-operator tender | Pre-sales or solution consultant (optional) | Tenders, BOQs and demos for airport operators on top of AMAN's commercial load |

Signs the second developer is needed earlier:

- The pilot client insists on lane-mix forecasting or staffing recommendations inside the pilot.
- AMAN incidents consume more than half of the developer's week for two sprints running.
- Phase 0 and Phase 1 should overlap to move the pilot earlier.

Structural risk: until the second developer arrives, the Ariva codebase has a bus factor of one. Keep ADRs, the domain definitions and a project guide for Claude Code in the repository from the first sprint (this `docs` folder is that material), so a second person, or Claude Code working for them, can pick it up cold.

## Start-now actions from D6

Listed in D6 on 2026-09-28 as shortening the critical path without needing a contract. Status of each is unknown to this document.

- Decide whether to fund the developer full-time before the contract.
- Open partner talks with one stereo vendor and one LiDAR perception vendor; ask the stereo vendor whether it will sell sensors with data access to a competing software vendor.
- Buy two to four sensors of one family for a lab (an office entrance or corridor) to record real tracks for Phase 0.
- Shortlist two or three AMAN arrivals halls against the pilot criteria; walk them informally; photograph ceilings and snake layouts.
- Ask the likely pilot client for its change-control calendar and security review process.
- Name the field engineer who will own Phase 1 on site.
- Set up the repository (GitHub, private) and the backlog mirrored to GitHub issues with an epic label per work package; put the ADRs and domain definitions into a project guide for Claude Code.
- Draft a one-page annex of KPI definitions and acceptance criteria for the pilot proposal.
- Ask local counsel in Angola whether stereo counters fall under the video-surveillance law; prepare the DPIA template.
