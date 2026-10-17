# Validation rehearsal record: the reference evening through the live pipeline (ARV-104j)

Status: reference run of 2026-10-11. The rehearsal ran end to end against Ariva's real pipeline and every criterion of F18 meets its target from Ariva's own measurements, judged against ground truth Ariva never sees. A second campaign with a 6 percent count error on one line fails count accuracy on that line only. This record is the evidence a client can be shown; it replaces the record of the first run of 2026-10-10, whose four findings (F1 to F4) were decided by the owner and fixed (history at the end).

## What a rehearsal against the live pipeline is

The simulator plays the reference evening of the fictional Demo International Airport (site `DMO`, seed 9303) as sensor traffic in real time. Ariva receives it as it would receive a site's sensors: Ariva.Api.Ingest takes the pushes, Ariva.Api.Stream computes the queues, line counts, waits, nowcasts, desk states and outages, Ariva.Api.Cronz keeps the availability ledger. The simulator also knows the evening's ground truth (every person's entry and exit, every desk's state), independent of Ariva. Once the window has played, two Ariva accounts with the Validation observer role, signed in by the simulator through Ariva's normal sign-in (one with an authenticator), report that truth through the same capture API the observer tablet uses: one manual count per line and 15-minute bin, tracer runs, desk states minute by minute. A border shift supervisor plans and starts the campaigns, closes them with a second factor, and Ariva freezes their results with a SHA-256 content hash. Every figure below is Ariva's own measurement compared with the observers' truth; the test writes nothing to Ariva's database. The procedure is wiki 07 section 8; the automated run is `node scripts/verify.mjs rehearsal` (`Platform/Testing/Ariva.E2E/tests/rehearsal/live-validation.spec.ts`).

## The run

| Item | Value |
|---|---|
| Date and time | 2026-10-11, 02:33 to 04:46 UTC, about 2 hours 15 minutes |
| Code | commit 0668d02 with ARV-148, ARV-149, ARV-150, ARV-151, ARV-149a (to its third round) and ARV-104j in the tree, committed after the run as their story commits; ARV-149a's fourth round (a start-up warning and a test) came after the run and does not change what it measures |
| Environment | One machine: the E2E stack (fresh TimescaleDB, Kafka, Redis) with Ariva.Api.Main, Ingest, Stream, Cronz, Integration and the simulator, Debug builds |
| Scenario | DMO, seed 9303; the evening played from 17:37 to 20:30 of the scenario at speed 1 (17:37 at 02:37:01 UTC, so the scenario's 18:00 was 03:00:01 UTC) |
| Rehearsed window | 18:00 to 19:30 of the scenario, 03:00 to 04:30 UTC: six 15-minute bins (the visitors' wave, S-17's outage from 18:20 to 18:30, the empty hall, the start of the second wave) |
| Sensors | The 20 counting sensors of DMO, registered and calibrated through the admin API; 129 pushes each (S-17: 119, offline for 10 minutes); Ingest accepted every event of every push (accepted equal to expected, no failure) |
| Campaign scope | Queue zones A-VIS and A-RES; lines A-VIS entry, A-VIS exit, A-RES entry, A-RES exit; arrival immigration desks AR-05 to AR-12 (none has a staff or service zone in DMO's profile); planned day 2026-10-11 (Asia/Dubai) |
| Targets | 3 judged bins per line, 6 compared tracer runs; so 6 Good bins for track completion and 45 judged minutes for the desk states and the nowcast; exclusion rule Strict; F18 targets |
| Observers' capture | Each campaign: 24 counts, 7 tracer runs in 2 batches, 12 desk batches (720 desk minutes), every one recorded, no refusal; both observers Capturing; the campaign's manager is not among the observers |
| Host logs | 6 host logs, 19,880 lines: no credentials, no usage telemetry |

## Campaign 1: no injected error

Frozen as revision 1 at 04:46:11 UTC. Content SHA-256 `19f2653c0b61717689297c373b56f42141ac7519d4a5ee88c66da29583a45d62`.

| Criterion | Ariva's measurement | Target | Judged / needed | Verdict |
|---|---|---|---|---|
| Count accuracy per bin, each line | 1.000 on every judged bin of every line | at least 0.95 on every judged bin | 15 of 12 | Pass |
| Realised-wait error | 6 of 6 compared runs within tolerance (largest error 0.003 minutes) | every run | 6 of 6 | Pass |
| Realised-wait bias | +0.05 % (A-RES +0.02 %, 4 runs; A-VIS +0.05 %, 2 runs) | within 5 % | 6 of 6 | Pass |
| Track completion | A-VIS 1.000 (492 entered, 492 exited), A-RES 1.000 (137 and 137) | at least 0.90 per zone | 15 Good bins of 6 | Pass |
| Nowcast error (waits under 20 minutes) | median 0.29 minutes over 97 judged minutes (Strict: the 3 minutes without service counted as errors; none for NothingOpen) | median at most 2 minutes | 97 of 45 | Pass |
| Availability | 1.000: 128 of 128 operating minutes available | at least 0.99 | 128 minutes | Pass |
| Desk-state agreement | 0.994: 716 of 720 observed minutes agree, every desk judged at the throughput level (open or not open; no desk has a zone) | at least 0.95 | 720 of 45 | Pass |

Count accuracy per bin (observers' count / Ariva's count; A-VIS bins under S-17's outage are set apart as Degraded, bins nobody crossed have no accuracy):

| Bin (scenario) | A-VIS entry | A-VIS exit | A-RES entry | A-RES exit |
|---|---|---|---|---|
| 18:00 | 243 / 243 | 108 / 108 | 52 / 52 | 52 / 52 |
| 18:15 | Degraded (48 / 48) | Degraded (120 / 120) | 11 / 11 | 11 / 11 |
| 18:30 | 0 / 0 | 73 / 73 | 0 / 0 | 0 / 0 |
| 18:45 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| 19:00 | 51 / 51 | 51 / 51 | 26 / 26 | 26 / 26 |
| 19:15 | 74 / 74 | 41 / 41 | 15 / 15 | 12 / 12 |

Desk states: of the 720 observed minutes, 30 Closed, 25 Paused and 661 open agree with Ariva; 4 minutes an observer saw Serving, Ariva had Paused (AR-09 and AR-12, 2 each). Reported beside the criterion, with no target (owner decision of 2026-10-10, ARV-149a): Serving against Idle agrees in 589 of the 661 minutes both call open (0.891); 44 observed Idle were Serving in Ariva, 28 observed Serving were Idle. A desk with a staff or service zone is judged on its full state; a site that wants 0.95 on Serving against Idle needs such a zone at each desk.

S-17's outage, from the pipeline's own detection: Stream recorded S-17 out over A-VIS from 18:20 to 18:30 (03:20 to 03:30 UTC), the only device outage of the run. The 18:15 bins of both A-VIS lines are Degraded and set apart; the 18:30 bin, which the first push after the outage covers, is judged again (A-VIS exit 73 / 73), as the owner decided on F4; A-RES has none; the one A-VIS tracer whose wait spans the outage is set apart.

## Campaign 2: a 6 percent count error on `A-VIS entry`

Frozen as revision 1 at 04:46:30 UTC. Content SHA-256 `648065ffc90d9de0c1359ce199969aab800d62b020447d0d03bbe5f7ff807ccf`.

The observers reported 6 percent more people on `A-VIS entry` (18:00: 258 for 243; 19:00: 54 for 51; 19:15: 78 for 74). Count accuracy fails on that line only (0.942, its lowest judged bin), A-VIS exit and both A-RES lines stay at 1.000, and every other criterion has the verdict and value of campaign 1 (availability 129 of 129 minutes, the ledger one minute further).

## What this run proves, and what it does not

Proven against the live pipeline, with no planted data: Ariva's counts, realised waits, track completion, nowcast, availability and desk states meet the F18 targets against independent ground truth on the reference evening; an observer's count error shows on its line only; a sensor outage is detected by Stream and its bins are set apart; the campaign closes with a second factor and its results are frozen with their hash.

Not proven by this run: one evening of one scenario is not a pilot. The scenario's sensors are perfect apart from the scripted outage (no miscounts, no occlusion), so count accuracy of 1.000 shows the pipeline loses and duplicates nothing, not how a real sensor counts; that is the pilot's validation campaign (ARV-104, wiki 07). The desk states of a site whose desks have no zones are judged at the throughput level by the owner's decision; Serving against Idle (0.891 here) has no target there. The targets are F18's Proposed values until the KPI annex agrees them.

## History of the rehearsal

- First run, 2026-10-10 06:36 to 08:47 UTC: the rehearsal ran end to end, but Ariva's measurements fell short: count accuracy 0.510, track completion 0.706, desk-state agreement 0.129, availability 0.969; only the nowcast met its target. Four findings: F1, crossings taken as late because Stream moved a zone's clock by each batch's receive time; F2, desks without zones never Serving and Paused after 3 minutes without a transaction; F3, the availability ledger counting a site's minutes before its profile was published; F4, an outage ending where the device is heard again, so the bin after it was set apart too. The owner decided all four on 2026-10-10 (docs/product/decisions.md): event time (ARV-148), desk state from AMAN's transaction rhythm (ARV-149, with the simulator's truth corrected in ARV-151 and desks without zones judged at the throughput level in ARV-149a), the ledger from the first publication (ARV-150), and the outage ending at the start of the minute the first push after it covers (ARV-148).
- Second run, 2026-10-10 22:32 to 2026-10-11 00:46 UTC: every criterion met its target, desk-state agreement by one minute (0.951). 28 of its 35 disagreeing minutes were a regression of ARV-149a's first round (a queue minute still running, with its counts and no length yet, read as a queue not known, so desks silent for a while were Paused at each wave's start), fixed in ARV-149a's third round. The run also failed two harness expectations written before the F4 decision (the 18:30 bin expected Degraded), corrected to express the scenario's outage in whole minutes.
- Third run, this record.

## Reproducing it

On a machine with Docker and the E2E environment (`Platform/Testing/Ariva.E2E/README.md`), on fresh services: `node scripts/verify.mjs rehearsal`. The run writes `rehearsal-record.json` (both content hashes, every criterion with its value, verdict and units, the per-bin counts, the tracers, track completion, the nowcast, the desk confusion, the availability, the outages and stored bins Stream recorded, the ledger's unavailable minutes) to `Platform/Testing/Ariva.E2E/test-results` and attaches it to the Playwright report. The scenario is deterministic for seed 9303, so the observers' truth is the same in every run; Ariva's figures can move by a minute with the timing of the live pipeline, and the content hashes differ from run to run (campaign ids, times).
