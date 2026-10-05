# Handoff: ARV-064 Scripted demo and runbook (WIP)

Branch: `story/ARV-064-demo`, based on main `26dd0f5` (ARV-002 GitHub dev release, landed). Not merged to main. `backlog/prd-phase0.json` still has ARV-064 `"passes": false`.

## Goal

A repeatable demo of the reference evening at DMO (seed 9303) from 17:40. Three scripted events must show on the live screens, played through the whole pipeline with nothing planted. Owner decisions (2026-10-05, "go with your recommendation"):

- **1a:** the full real-time rehearsal runs as its own Playwright project. **1b:** a short live demo, documented; the runbook now says 17:50 to 18:35, not 17:55, because of sensor warm-up.
- **2a:** event 3 is R-004 (check-in P90) plus the CI-C nowcast. No SLA engine.
- **3:** the AMAN desk term is wired into the nowcast. "Degraded" is shown only for sensor or length problems.

## Acceptance criteria

| Criterion | State |
|---|---|
| Simulator starts at 17:40 seed 9303; the three events show on the live screens | Partial. Event 1 (18:05, R-001 on A-VIS for the border supervisor) passed in a real-time trial (range 17:40 to 18:15, 43 min). Events 2 (18:20, S-17 offline, R-003) and 3 (19:10, R-004 on CI-C) have not been run since the fixes. The PRD says "on the dev cluster", but no cluster is reachable from here, so it was verified with the local E2E stack only. |
| A Playwright demo test walks the three events | Done (`Platform/Testing/Ariva.E2E/tests/demo/reference-day.spec.ts`, project `demo`). Only event 1 has run green. |
| wiki runbook page updated | Done (wiki/10 section 4.11, plus the 4.10 table row). |
| node scripts/verify.mjs e2e passes, incl. new tests | Partial. A full e2e run passed (503 passed, 1 skipped) on commit `6b76c26`, before the security fixes in the last commit. Not re-run since. |
| security-reviewer verdict PASS recorded in backlog/progress.md | Partial. The independent review is PASS after one round of fixes (Medium: TOTP bypass and dev users not enforced at runtime, fixed). It is not yet written into progress.md. |
| docs and wiki updated (verify docs passes) | Done. `verify docs` passes. |

## Files changed and why

**Core**
- `Platform/Backplane/Ariva.Core/Queueing/DeskTerm.cs` (new): a pure `DeskTerms.Compute`.
  - n_open: desks Idle or Serving for at least 30 s of the latest closed minute.
  - c: AMAN's lane cycle time (`LaneCycle`), with open-time-per-transaction as fallback.
  - Degraded when a desk is Unknown, degraded or behind the others.
  - No term when none is open and any is Unknown.
- `Ariva.Core/Queueing/ZoneProcessor.cs`:
  - `UseDesks`/`Desks`, and the setting `DeskTermFreshMinutes` (5).
  - `Live` passes OpenServers and CycleMinutes into `Nowcast.Compute`.
  - The desk term is not in the snapshot, and replays never set it, so the golden replay is unchanged (verified).
  - Also the idle-tick fix: a zone with no clock yet no longer logs "Idle tick failed" every 15 s.

**Infra and DI**
- `Ariva.Infra/Streaming/DeskTermSource.cs` (new): parameterised SQL.
  - Matches queue zone to desks by level and lane, like `SvcImmigrationView`.
  - Reads `desk_minute` and `border_desk_interval`.
  - Each union half has its own `ORDER BY ... DESC LIMIT 30000`; a site at the cap is skipped with a warning.
  - Timeout 5 s, CommandTimeout 10 s.
- `Ariva.Infra/Streaming/QueueStreamWorker.cs`: `RefreshDeskTermsAsync` runs every `Stream:DeskTermSeconds` (15; 0 turns it off). Failures are caught.
- `Ariva.Di/Extensions/StreamExtensions.cs`: registers `DeskTermSource`.
- `Ariva.Di/Extensions/AccountExtensions.cs`: `LocalOnlyProblem`.
  - The host refuses to start with `Auth:DevelopmentUsers` or `Auth:TotpRequired=false` unless `Application:Environment` is vm-local and the host environment (configuration key `environment`) is vm-local or absent.

**Ingest**
- `Ariva.Api.Ingest/Endpoints/ReceiptTime.cs` (new), `Program.cs`, `Controllers/PushController.cs`: the receipt time is taken by the first middleware.
- Diagnosis: the first push's credential check took over 500 ms, which F19 read as a clock offset. That made every device Unreliable, and every queue length degraded, for its first 10 pushes.
- Not yet confirmed in a live run. After the next trial, check `device_health_event.clock_state`.

**Simulator**
- `Platform/Simulation/.../Emulators/Sensors/SensorTraffic.cs`: a queue lead with an overflow band reports at most the cap (A-VIS 190, D-VIS 140, SEC-N 90), and the band reports the rest. Before this, Ariva's sum double counted once a queue exceeded the cap.

**Web**
- `Platform/Frontplane/Ariva.Web/src/lib/components/pages/live/waits.ts`: `waitStatus` returns 'degraded' only for stale or `lengthDegraded`. It adds 'noEstimate', and an `estimateOnly()` marker for exit-rate-only nowcasts.
- The "≈" marker with an i18n title is shown in `routes/(modules)/+page.svelte` and `immigration/+page.svelte`.
- `NowcastPlan.svelte` gets a fill for 'noEstimate'.
- `en.json`/`ar.json` get `status.noEstimate` and `estimateHint`.

**E2E**
- `tests/demo/reference-day.spec.ts` loads every counting sensor by default (20); `ARIVA_DEMO_SENSORS=events` loads 7.
  - S-25 (the A-OV overflow lead) was the cause of the first trial's failure: without it, A-VIS length was never a full sensor reading, and R-001 skips degraded lengths.
  - It now starts on the next whole minute with a pre-roll of up to 14 demo minutes, so bins align and no device goes Offline while waiting.
- `tests/functional/live-operations.spec.ts`: a new test for the estimate marker, "Data degraded" and "No estimate". It passed in the e2e run.
- `playwright.config.ts` demo mode (from the earlier WIP): Stream host, Kafka for Main, the `demo` project. `accounts.ts` adds `demoAdmin`; `hosts.ts` adds `streamUrl`; `scripts/build-backend.mjs` builds Stream.

**Unit and integration tests**
- `Ariva.UnitTests/Queueing/DeskTermTests.cs` (new)
- `Ariva.UnitTests/Sensing/ReceiptTimeTests.cs` (new)
- `Ariva.UnitTests/Security/AuthSettingsTests.cs`: the `LocalOnly` theory.
- `Ariva.UnitTests/Simulation/SensorTrafficTests.cs`: the conservation test now adds the overflow band.
- `Ariva.IntegrationTests/Border/ImmigrationIntakeTests.cs`: `DeskTerms_Should_GiveEachQueueTheDesksOfItsLaneOnItsLevel_When_TheStreamAsks`.

**Local demo**
- `scripts/demo-local.mjs` (new): `prepare | start | status | stop | accounts | code`.
  - Writes `.demo/` (git-ignored, chmod 700/600): demo accounts, and the AppHost host-environment file for api-main (dev users, `Auth__TotpRequired=false`) and the simulator (a second operator key).
  - Registers and calibrates sensors, loads them into the simulator and starts.
  - Loopback URLs only, unless `--remote` is given.
- `.gitignore` adds `.demo/`.

**Docs**
- wiki/10 4.11 (runbook), 4.10 row.
- `docs/domain/formulas.md` (F8 desk term, F10 note).
- `docs/product/decisions.md`: the decision entry; the open "Nowcast desk term" row removed.
- `security/README.md` and the wiki/04 settings table (the runtime guard).
- `scripts/verify.mjs`: a `demo` scope (from the earlier WIP).

## Gates run

| Command | Commit | Result |
|---|---|---|
| `node scripts/verify.mjs backend` | 6b76c26 | PASS |
| `cd Platform/Backplane && dotnet test Ariva.IntegrationTests --no-build` | 6b76c26 | PASS |
| `cd Platform/Testing/Ariva.E2E && CI=true ARIVA_E2E_SCHEMA_UPDATE=true ... npx playwright test` (all projects except demo) | 6b76c26 | PASS (503 passed, 1 skipped) |
| `dotnet test Ariva.UnitTests` (full) | 6b76c26 plus the uncommitted desk term work | PASS (2312) |
| `node scripts/verify.mjs web` | 282b6c6 | PASS (svelte-check, lint, build) |
| `node scripts/verify.mjs docs` | 6b76c26 | PASS |
| Targeted: `dotnet test Ariva.UnitTests --filter DeskTermTests\|ZoneProcessorTests\|AuthSettingsTests\|Hosting\|BreakGlass` | 282b6c6 | PASS |
| Targeted: `dotnet test Ariva.IntegrationTests --filter DeskTerms_Should\|AccountTests\|TotpTests\|DevelopmentUser` | 282b6c6 | PASS |
| `ARIVA_E2E_DEMO=1 ARIVA_DEMO_START=1060 ARIVA_DEMO_END=1095 npx playwright test --project=demo` | c342066 + S-25 fix (before the desk term) | PASS (event 1; 2 skipped by range) |
| `node scripts/verify.mjs quick` (handoff) | 282b6c6 | PASS. It scanned 0 files because everything was committed (`--changed` scans uncommitted files only). |

Not run on the final commit: the full backend, integration and e2e gates, the full demo (events 2 and 3), the AppHost local run, and ZAP.

## Broken or half done

- **Events 2 and 3 are not verified end to end.** Event 3 depends on the 15-minute bin alignment (the pre-roll start) and on R-004 judging the 19:00 to 19:15 bin after 19:15. Event 2 depends on S-17's heartbeat stopping at 18:20; the simulator sends nothing while it is offline.
- **The desk term has not been seen in a live run.** The trial that passed predates it. With it, the A-VIS nowcast blends AMAN's desks with the exit rate. Watch that R-001 still crosses 15 at about 18:05. In the earlier trial, AMAN VIS showed about 9.5 desks open with a cycle of about 1.6 min.
- **The receipt-time fix has not been seen in a live run.** Expected effect: no 10-push Unreliable warm-up after a start.
- **`demo-local.mjs` has only been tested offline:** argument handling, `prepare`, the permissions and the loopback guard. `start` against the AppHost has not been run. The AppHost builds.
- **The security review PASS is not yet written into backlog/progress.md, and there is no progress entry for ARV-064 yet.**

## Decisions and things that did not work

- The first trial failed on R-001. It was not the alert engine: A-VIS length was `length_from_sensors=false` and degraded, because the overflow band A-OV had no sensor. Fixed by loading S-25. Loading every counting sensor by default also makes all 16 zones live for the demo.
- The desk term is live-only, kept out of the snapshot, so the golden replay (`replay-golden.json`, unchanged) and the bins stay deterministic. A replayed minute after a restart may get a different nowcast. This is documented in `formulas.md`.
- c comes from AMAN `border_desk_interval.mean_cycle_seconds` (F10 `LaneCycle`) rather than open-time per transaction, which overestimates c when desks are idle. The proxy remains as a fallback.
- "All desks Unknown" gives no term, not `NothingOpen`. Early in a run AMAN reports every desk Unknown, and treating that as zero open desks would blank the nowcast.
- Starting immediately with a pre-roll replaced the "wait for the quarter hour" approach. Waiting idle left calibrated devices silent for over 180 s, which raised R-003 for every sensor at the start.
- Dependabot cannot be read from the cloud session (403). It was read from the user's device: 2 npm alerts in Ariva.Web, not fixed (see the questions below).

## Open questions for the product owner

1. **Dependabot alerts.** Both are dev-time only in our build; accept or override?
   - GHSA-67mh-4wv8-2f99: esbuild ≤0.24.2, pulled in by svelte-i18n 4.0.1 for its CLI only.
   - GHSA-pxg6-pf52-xh8x: cookie <0.7.0, pulled in by @sveltejs/kit 2.70.3.
   - An npm `overrides` entry would fix them, but cookie 0.7 is outside kit's declared range.
2. **Reviewer's Low residual.** The guard reads `configuration["environment"]`, which an un-prefixed `ENVIRONMENT` variable can override (it takes two deliberate variables). Should `IHostEnvironment.EnvironmentName` be threaded into `AddArivaAccounts`?
3. **"On the dev cluster" in the AC.** Accept local plus E2E verification for Phase 0 until GitHub Actions runs (billing) and a cluster is reachable?
4. **The GitHub token** used from the user's device for pushes should be revoked when this work is done.

## Next three steps

1. Run the full gates on this branch (`node scripts/verify.mjs backend`, integration, e2e). Then run the full demo rehearsal once:
   `ARIVA_E2E_DEMO=1 npx playwright test --project=demo` (default range 1060 to 1180, about 2 h 20 min including setup), with a fresh Kafka and database.
   Confirm all three events and the clock state after the receipt-time fix.
2. Validate the local path:
   - `node scripts/demo-local.mjs prepare`
   - `dotnet run --project Platform/Cloud/Ariva.AppHost -- "--AppHost:HostEnvironmentFile=$PWD/.demo/apphost-environment.json"`
   - `node scripts/demo-local.mjs start --at 17:50 --until 18:35`
   - Sign in as demo.border and see R-001.
   Fix whatever breaks, then send the user the local run steps.
3. Write the ARV-064 progress entry (including the reviewer PASS and residuals). Set `passes: true` only when every criterion is gated, squash the WIP commits to `ARV-064: Scripted demo and runbook`, and land it fast-forward on main. Then start ARV-065 (the Phase 0 exit review).

## Only on this machine (the next session will not have these)

- **Containers:**
  - `e2e-pg` (timescale/timescaledb-ha:pg17-ts2.30 on 5433, database `postgres`, holds the last e2e data)
  - `e2e-kafka` (apache/kafka:4.1.0 on 19092)
  - `e2e-redis` (6380)
  - `signreg` (registry on 5555, for ARV-073 tests)
- **Scratchpad scripts** (not in git), which wrap the E2E commands with these variables:
  - Scripts: `gates.sh`, `demo.sh`, `demo-nodb.sh`, `fn.sh`, `zap.sh`, `e2e-kafka.sh`.
  - Variables: `CI`, `ARIVA_E2E_DEMO`, `ARIVA_DEMO_START`, `ARIVA_DEMO_END`, `ARIVA_DEMO_SENSORS`, `ARIVA_E2E_KAFKA_BOOTSTRAP`, `ARIVA_E2E_CHROMIUM`, `ARIVA_E2E_SCHEMA_UPDATE`, `ARIVA_E2E_REDIS_URL`, `Database__Host`, `Database__Port`, `Database__Name`, `Database__Migration__Username`, `Database__Migration__Password`, `Database__Username`, `Database__Password`.
  - The equivalent commands are in `Platform/Testing/Ariva.E2E/README.md` and `.github/workflows/ci.yml`.
- **Tools** in the scratchpad: Helm 3.19.0 and 3.16.4, Helmfile 1.1.7, kubeconform, cosign 3.0.6, trivy 0.75.0 and oras 1.3.4.
- **Worktrees:**
  - `/home/claude/ariva-build/ariva-dev`: the branch used for this work. Its `Platform/Testing/Ariva.E2E/node_modules` is a symlink to the main clone's.
  - `/home/claude/ariva-build/ariva-rel`: the ARV-002 release branch, already landed.
- **Pushing:** the cloud session cannot push to GitHub. Pushes go through the user's device clone (`D:\DevOps\Ariva`) with a token the user holds (the variable is not stored anywhere). Do not write the token to disk or to git config.
- **GitHub Actions** jobs do not start on this account (billing or spending limit suspected), so CI has not validated any of the recent stories.
