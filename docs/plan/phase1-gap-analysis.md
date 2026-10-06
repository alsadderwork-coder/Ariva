# Phase 1 (pilot MVP) gap analysis

Dated 2026-10-06. Draft for the product owner. Inputs: `docs/product/roadmap.md` (MVP scope, pilot acceptance criteria, MVP work breakdown), `docs/plan/implementation-plan.md`, `backlog/prd-phase0.json` (77 of 79 pass), `backlog/prd-asvs-gaps.json`, `backlog/phase1-candidates.md`, `docs/product/phase0-exit-review.md`, `docs/product/pilot-to-confirm.md`, `docs/domain/formulas.md` (F8, F10, F11, F18), the wiki and the code under `Platform/`. Companion backlog: `prd-phase1.json` (40 stories, ARV-104a to ARV-104i, ARV-111a, ARV-113 to ARV-139b).

Phase 0 already delivered much of what the roadmap planned for Phase 1. Each "exists" claim below was checked in the code, not only in the PRD notes.

## 1. MVP scope items

Paths are relative to `Platform/Backplane` unless they start with `Platform/`.

| # | MVP scope item | What exists (stories, code) | What is missing | Closing stories |
|---|---|---|---|---|
| 1 | One sensor family for the pilot hall's ceiling | ARV-021 to ARV-026: device registry and calibration (`Ariva.Core/Domain/Entities/Device.cs`), device auth, Xovis dialect over HTTPS push, MQTT transport with the declarative `ouster-detect-v1` mapping, heartbeats, raw archive (`0017_sensing_event.sql`) | Certification on a real capture (Xovis formats are inferred, "verify" labels in `docs/architecture/sensor-adapters.md`); 5 of 7 transports not built (REST pull, WebSocket, TCP or UDP, file drop, ONVIF), needed only if the chosen family uses them | ARV-132, ARV-133 (blocked on the survey) |
| 2 | Simulator | ARV-027 to ARV-029 (`Platform/Simulation/Ariva.Simulation.Api`), seed 9303, golden replay ARV-036 | Ground truth for a validation rehearsal; staff and service zone readings | ARV-104i, ARV-116 |
| 3 | Zone and line editor with versioned profiles | ARV-016, ARV-017, ARV-018, ARV-053 (`ZoneProfile.cs`, `FloorPlan.cs` with scale and origin, `/zones`); device orientation transform exists | Zone physical capacity (for the occupancy check); moving a configured site between environments | ARV-114a, ARV-122 |
| 4 | Realised wait and nowcast | ARV-030 to ARV-032, ARV-034 (`Ariva.Core/Queueing`, `0018_stream_output.sql`), desk term (ARV-064) | Nothing for the scope item; the ground-truth proof needs a stored sensor-only nowcast because the desk term is live only and never replayed (F8) | ARV-117 |
| 5 | Overflow detection | The engine sums overflow bands into the queue length (`Ariva.Core/Queueing/QueueInputs.cs`); topic constant `FlowOverflowDetected` | No stored band occupancy, no producer for the topic, `OverflowOccupied` returns nothing (`Ariva.Infra/Alerting/AlertInputs.cs`), so seeded R-002 shows "Not evaluated yet". Built in ARV-115 (`overflow_minute`, `OverflowDetected` through the outbox, R-002 evaluated); screens remain for ARV-120 | ARV-115, ARV-120 |
| 6 | Data-quality flags | F11 in the stream: `DeviceLiveness`, `zone_outage`, `queue_bin.quality`, degraded bands on displays | None | (done) |
| 7 | Conservation and track-completion checks | Bin counters exist (`queue_bin`: entries, exits, abandoned, fragmented, censored) | No conservation residual, track completion rate or occupancy sanity anywhere in the code; no alarms | ARV-114a, ARV-114b, ARV-120 |
| 8 | Bin revisions | `queue_bin` keeps revisions; degraded final bins ask for recomputation (ARV-031, ARV-036) | A user-triggered recomputation under a corrected profile (wiki/07 section 6) is not built; it belongs to disputes (v1), not the MVP | (v1) |
| 9 | Desk state from AMAN aggregate contracts | ARV-033, ARV-048 to ARV-050, ARV-068 Pact (`Ariva.Core/Desks`, `0030_border_feed.sql`, `0031_desk_feed.sql`, `0032_aman_pull.sql`) | A real AMAN feed (AMAN-side outbox change, not in this repository); site and lane code mapping when AMAN codes differ | ARV-130, ARV-131 (blocked) |
| 10 | Desk state from sensor zones | `ZoneKind.Staff` and `ZoneKind.Service` exist in profiles; F10 ranks defined | The stream drops non-queue zones, so staff and service readings never reach the desk engine (F10 implementation note) | ARV-116 |
| 11 | Basic e-gate utilisation and reject rates | ARV-049 `egate_minute`, ARV-057 immigration screen | None | (done) |
| 12 | Supervisor dashboard | ARV-055 live operations, ARV-057 immigration | Overflow state and a data-quality panel | ARV-120 |
| 13 | Alert rules, in-app push, email, acknowledgement | ARV-037 to ARV-040, ARV-056 (`AlertRule.cs`, `0020` to `0023`, live hub alert groups, MailKit outbox) | `DesksBelowPlan` has no data (`OverflowOccupied` has since ARV-115); health-check metrics | ARV-115, ARV-114b, ARV-129 (conditional) |
| 14 | Passenger display page in the site's languages | ARV-058 (`/display`, four languages, hysteresis, stale fallback) | Site values (staleness TC-09) and a native review of texts (TC-41, human) | ARV-136 |
| 15 | Daily and weekly reports, CSV | ARV-060, ARV-061 daily report (`ReportTemplate.DailyPeaks` is the only template) | Weekly report | ARV-119, ARV-120 |
| 16 | Validation tooling: tracer and manual-count form, comparison report | Only `CalibrationMethod.ManualCountTally` on device calibration (ARV-021) | Everything: campaigns, manual counts, tracers, desk observer log, tablet screens, F18 comparison, report | ARV-104a to ARV-104i (candidate ARV-104, accuracy assurance) |
| 17 | Single-tenant deployment in the border authority's environment | ARV-062 Helm, ARV-073 signed images, ARV-097, ARV-098, ARV-080, ARV-081 | First administrator in production (today only the sealed break-glass account, ARV-010c; `DevelopmentUserSeed` is vm-local only); backup and restore; system monitoring; basic offline bundle; smoke tests against a deployment; go-live security (ARV-082, ARV-083, ARV-085, ARV-087); dev cluster release (ARV-002); basic licensing; site identity provider if required | ARV-121, ARV-126, ARV-125, ARV-127, ARV-128, ARV-137, ARV-135 and ARV-134a, ARV-134b (conditional), ASVS stories in `prd-asvs-gaps.json` |
| 18 | Authentication, roles, audit (work package 10) | ARV-010a to ARV-012, ARV-059 | Covered; production bootstrap is row 17 | ARV-121 |
| 19 | Flight data: SSIM import; arrival-wave alert only with an AODB | ARV-046, ARV-047, ARV-038 predicted breach | SSIM date rules to confirm (TC-14, TC-46) | ARV-136 (values only) |
| 20 | Privacy and retention for the pilot DPIA (implied by TC-62, TC-70, TC-75) | Hypertable retention fixed in scripts (90 days raw) | Data inventory; retention from contract answers; relational retention job | ARV-111a |
| 21 | Hardening: failure modes, load test, security fixes (work package 12) | ARV-071 load harness (18,000 positions a second in a lab burst), ARV-072 fault tests (not yet run, see section 3) | Pilot-profile load and a 24-hour soak for bounded memory | ARV-124 |
| 22 | Demo data on a real airport (owner request 2026-10-06) | DMO seed only (ARV-019, `DemoTopologySeed.cs`, guard in `Ariva.Di/Extensions/DemoSeedExtensions.cs`) | A second, illustrative seed site for AUH Terminal A from cited public facts, with an "Illustrative, not surveyed" banner, and a simulator scenario of its own | ARV-139a, ARV-139b |

## 2. Pilot acceptance criteria

| Criterion (target) | What exists | What is missing | Closing stories |
|---|---|---|---|
| Count accuracy per 15-minute bin, each line (at least 95%) | Zone-level entries and exits (`queue_minute`); device calibration accuracy | Per-line counts; manual count capture; comparison | ARV-113, ARV-104a, ARV-104c, ARV-104e, ARV-104g |
| Realised-wait absolute error (max of 1 min or 10%) | Realised waits per entry minute (F6, F7) | Tracer capture; tracer matching rule (product-owner decision, Proposed in ARV-104e) | ARV-104b, ARV-104c, ARV-104e |
| Realised-wait bias (plus or minus 5%) | As above | As above | ARV-104e |
| Track completion (at least 90%) | Bin counters (exits, abandoned, censored, fragmented) | The rate itself and its alarm | ARV-114a, ARV-114b, ARV-104e |
| Desk-state agreement with observer log (at least 95% of observed minutes) | `desk_minute` from AMAN | Sensor-zone desk input; observer log capture; comparison; the real AMAN feed | ARV-116, ARV-104b, ARV-104d, ARV-104f, ARV-131 |
| Nowcast error against later realised wait (median within 2 min under 20 min) | Nowcast and final realised waits in `queue_minute` | Comparison | ARV-104f |
| Availability (99% of operating hours) | Health probes, stale indicators | A definition of operating hours (new question for the KPI annex) and an availability ledger; system monitoring | ARV-118, ARV-125, ARV-124 |
| Ground-truth proof (nowcast error with and without AMAN, side by side) | Live desk term from AMAN | A stored sensor-only shadow nowcast (cannot be rebuilt later) and the comparison | ARV-117, ARV-104f, ARV-131 |

The validation report (ARV-104g, screen ARV-104h) puts all eight together, frozen with a content hash at campaign close; ARV-104i rehearses the whole path on the simulator before any site work.

## 3. Phase 0 items carried into Phase 1

| Item | Status | Where it closes |
|---|---|---|
| ARV-002, last criterion: release on the dev Kubernetes cluster, verified by a human | **Owner decision 2026-10-06: deferred until every other MVP story is done.** No cluster work is scheduled earlier; stories that touch the chart (ARV-125, ARV-127, and the ASVS stories ARV-082, ARV-083, ARV-085) prove themselves with render tests only | ARV-137 (owner), then ARV-138 |
| ARV-065: the six Toxiproxy fault tests did not run (image host blocked in the cloud session) | Open | Gate criterion in ARV-138 |
| Five `approvedBy: PENDING` allowlist entries | Owner only | Gate criterion in ARV-138 |
| Go-live security stories ARV-082, ARV-083, ARV-085, ARV-087 | Open, in `backlog/prd-asvs-gaps.json` (not duplicated) | ARV-138 depends on them |
| Other ASVS gap stories (ARV-080a, ARV-084, ARV-086, ARV-088 to ARV-096) | Open, Phase 1 | Not gate dependencies; see risk 3 for ARV-084 and ARV-088 |

## 4. Non-software dependencies

| Dependency | Owner | Blocks |
|---|---|---|
| Site survey (TC-11, TC-13, TC-15, TC-16, TC-24, TC-25, TC-28, TC-30, TC-31, TC-33, TC-37, TC-40, TC-71) | Field engineer, local partner, client IT | ARV-132, ARV-133 (family and network), ARV-131 (AMAN Kafka), ARV-134a, ARV-134b (identity provider), node sizing after ARV-124 |
| Sensor family decision (survey; TC-71 stereo against LiDAR in Angola) and a real capture from the vendor (TC-38 Xovis or TC-39 Ouster) | Product owner, vendor | ARV-132, then ARV-133 |
| Sensor purchase, import, customs, installation (6 to 16 weeks, roadmap) | Procurement, partner | Commissioning and the validation campaign after ARV-138 |
| AMAN-side outbox changes, in the AMAN repository (not in this backlog): publish the four V1 aggregate contracts through AMAN's outbox; fix what the implementation plan lists as open in AMAN (idempotency filter never registered, outbox replay job commented out, Kafka consumer faults discarded without retry or dead letters, keyless producers on single-partition topics, `GetAll` on the integration client controller without permission, client details with secrets cached in Redis); merge the local `feature/integration-auth-hardening` branch; then the client's change control (5 to 13 weeks) | AMAN team, client | ARV-131; the desk-state agreement and ground-truth proof criteria (they need real AMAN data in the campaign) |
| AMAN answers TC-20, TC-21, TC-22, TC-23, TC-24, TC-25 | AMAN team | ARV-130, ARV-131 |
| Pilot contract answers: TC-04 (sample size and tracers), TC-62 (dispute window and raw retention), TC-70, TC-72, TC-75 (DPIA, passenger notice), TC-34 (recovery objectives), TC-42 (SLA engine module), TC-32 (support telemetry), TC-76 (name) | Product owner, client, counsel | TC-04: ARV-104a defaults and ARV-138; TC-62: ARV-111a; TC-70, TC-72, TC-75: the DPIA (signage is human work); TC-34: ARV-126; TC-42: ARV-135; TC-32: ARV-125 routing |
| Go-live answers: TC-01, TC-02, TC-03, TC-08, TC-09, TC-44, TC-47 | Border authority, Dalil | ARV-136 |
| Go-live answers handled elsewhere: TC-05 (identity proofing, ARV-095), TC-06 (MFA policy, ARV-134b), TC-26 and TC-50 (ARV-126), TC-27 and TC-48 (ARV-082, ARV-083), TC-29 (ingress certificates), TC-35 and TC-54 (signature policy, rulesets), TC-36 (ARV-125), TC-41 (native review), TC-43 (ARV-135), TC-49 (ARV-121, Dalil internal), TC-52 (ARV-127), TC-56 (ARV-128), TC-65 (ARV-111a, ARV-091) | Various | ARV-138 requires each answered or accepted as a known limit |
| New questions this analysis raises (to add to `pilot-to-confirm.md` through ARV-118 and ARV-104e) | Product owner with the client | Operating hours for availability; the tracer matching rule; whether Unknown desk minutes count as disagreement; whether `DesksBelowPlan` is in the MVP (ARV-129) |
| Client penetration test and accreditation (2 to 8 weeks) | Product owner, client | ARV-138 |
| Field engineer hired; client staff for counts, tracers and observer logs (at least five operating days with two peaks) | Product owner | The campaign after ARV-138 |
| AUH demo seed: owner confirmed AUH Terminal A and that no real plan exists | Owner (done 2026-10-06) | ARV-139a can start; use with care outside internal demos and AUH pitches |

## 5. Critical path

Three chains run in parallel after the contract (roadmap) and meet at commissioning.

| Chain | Steps | Weeks (roadmap assumptions) |
|---|---|---|
| Hardware | Survey 1 to 3, order and customs 4 to 10, installation 1 to 3 | 6 to 16 |
| AMAN | AMAN outbox change built, then the client's release | 5 to 13 |
| Security | ASVS go-live stories, then penetration test | 2 to 8 (test) |
| Software, site-bound | ARV-132 (needs a real capture), ARV-133, ARV-130, ARV-131, ARV-136, then ARV-137 and ARV-138 | 2 to 4 developer-weeks once unblocked |
| Joint | Commissioning 1 to 2, burn-in 2, validation campaign and report 2 to 3 | 5 to 7 |

The software chain without site decisions (priorities 1 to 30) has its own longest path: ARV-113, ARV-114a, ARV-104e, ARV-104f (with ARV-116 and ARV-117 alongside), ARV-104g, ARV-104h, ARV-104i, then the gate. ARV-117 must be live from the first day of burn-in because the shadow nowcast cannot be recomputed later.

Conclusion: if priorities 1 to 30 are built before the contract (the Phase 0 window runs to April 2027 and Phase 0 finished early), software leaves the critical path. The pilot date is then set by the hardware chain (6 to 16 weeks) or AMAN's change control (5 to 13 weeks), plus 5 to 7 weeks of joint work: roughly 11 to 23 weeks from contract to the end of the campaign.

## 6. Effort estimate (an estimate, not a measurement)

Sizing basis: the implementation plan's pace of 3 to 5 stories a week at half-time (0.1 to 0.17 developer-weeks a story), raised for Phase 1 because the stories are less templated: S 0.15 to 0.3, M 0.3 to 0.6 developer-weeks, plus an allowance for rework when real payloads and AMAN data arrive.

| Group | Stories | Developer-weeks (low to high) |
|---|---|---|
| MVP core, no site decision | 8 S and 19 M (priorities 1 to 30 without ARV-129, ARV-139a and ARV-139b) | 6.9 to 13.8 |
| MVP core, site-bound | ARV-130, ARV-136 (S); ARV-131, ARV-132 (M); ARV-138 (S, agent part) | 1.1 to 2.1 |
| ASVS go-live stories (other file) | ARV-082, ARV-085, ARV-087 (M), ARV-083 (L) | 1.5 to 3.0 |
| Rework allowance for real sensor and AMAN data | | 1.0 to 3.0 |
| **MVP core total** | | **about 10 to 22** |
| Conditional, if the owner keeps them | ARV-129, ARV-133, ARV-134a, ARV-134b, ARV-135 (M) | 1.5 to 3.0 |
| Demo data (owner request) | ARV-139a, ARV-139b (M) | 0.6 to 1.2 |
| **Everything in prd-phase1.json plus the go-live ASVS stories** | | **about 12 to 26** |

ARV-137 (dev cluster release) is owner time and not counted. The roadmap's Phase 1 software estimate was 14.5 to 24.5 developer-weeks; the remaining work is smaller in most packages because Phase 0 delivered them, and larger in validation tooling (roadmap 1 to 1.5; here about 3 to 5.5), which the roadmap under-sized for eight criteria, three capture forms and a frozen report. Phase 0's 79 stories closed far faster than planned, so the low end is plausible for code; reviews, field support and the site-bound rework are what push toward the high end.

## 7. Risks and open decisions

1. **The AMAN chain decides two criteria.** Desk-state agreement and the ground-truth proof need real AMAN data during the campaign, which needs the AMAN-side outbox change through the client's change control (5 to 13 weeks) and AMAN's own reliability fixes (outbox replay job commented out, idempotency filter unregistered, consumer faults discarded). Start the AMAN change at contract signature at the latest.
2. **Validation definitions are undecided.** TC-04 (sample size, tracers), the tracer matching rule, operating hours for availability and the treatment of Unknown desk minutes are product-owner decisions for the KPI annex; the tooling can be built on Proposed rules now, but the report is only contractual once they are agreed. The sensor family (survey, TC-71) gates certification. Also found: production's only administrator today is the break-glass account, which should stay sealed; ARV-121 adds a proper first-administrator bootstrap.
3. **Security scope at go-live.** ARV-083 is sized L and should be split before scheduling. The gate depends on ARV-082, ARV-083, ARV-085 and ARV-087 as instructed, but ARV-088 (the integration API's per-request TOTP can be replayed, CWE-287) and ARV-084 (ingress TLS and HSTS) also matter for an accreditation and a penetration test; recommend adding them to the gate. The MassTransit v8 decision (maintenance ends after 2026, ADR-0018) must be taken before go-live.
