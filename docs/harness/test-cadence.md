# Test cadence

Owner decision 2026-10-08. Every story still passes its own gates before it is marked passing; the full suite runs at checkpoints instead of twice in every story. Measured on ARV-104b before the change: unit tests about 2 minutes plus the build, integration 8 minutes, full E2E about 11 minutes plus host start-up, Stryker about 35 minutes (an hour or more when the queue engine changed: about 12 mutants a minute on two cores, 2.5 hours for its 1,733 mutants at the 2026-10-09 checkpoint), and the full gates ran twice per story (before and after the security review's fixes).

## Per story: `node scripts/verify.mjs story`

```
node scripts/verify.mjs story --specs tests/api/validation.spec.ts,tests/functional/validation-observer.spec.ts --integration "FullyQualifiedName~Validation"
node scripts/verify.mjs story --plan     prints what it would run, runs nothing
```

The gate compares the working tree with `--base` (default `HEAD`, the uncommitted story) and decides:

| Step | When |
|---|---|
| Build, reference golden check, all unit tests (architecture, security, domain, golden replays) | Always |
| Security: scanner self-test, full scan, base images, chart security, dependency audits | Always |
| Docs text rules | Always |
| Web: svelte-check, lint, build | When `Platform/Frontplane/Ariva.Web` changed |
| Integration, filtered by `--integration` | When `Ariva.Infra` or `Ariva.IntegrationTests` changed |
| Integration, full | When the change is wide, adds a Timescale script, or touches persistence without a filter |
| E2E: the story's `--specs`, the spec files it changed, and the smoke set | When backend, web, simulator or E2E code changed |
| E2E, full | When the change is wide |

The smoke set is `health`, `auth`, `security-baseline` and `permission-matrix` (API) and `shell` (functional). The permission matrix is always in because authorization is where cross-site and missing-permission defects show.

A change is wide when it reaches every host, every screen or the whole pipeline (the `WIDE` list in `scripts/verify.mjs`): `Ariva.Api.Common`, `Ariva.ServiceDefaults`, `Ariva.Di`, `Ariva.Api.Stream`; `Ariva.Core` `Security`, `Messaging`, `Queueing`, `Global.cs`, `RoleCodes.cs`; `Ariva.Infra` `NHibernate`, `Messaging`, `Streaming`, `Security`, `DataProtection`, `Caching`; the simulator; the web app's `src/lib/core`, hooks and build configuration; the E2E configuration and `tests/support`; the solution and central package files.

The security-reviewer still reviews every story. After its fixes, only the tests the fixes touch run again. A FAIL verdict is closed by a focused re-check of the fixes (the story's criteria need a PASS); a full second review runs only for a High finding or a fix that changes authorization.

A full E2E run (a wide story or a checkpoint) needs fresh services: the suite assumes an empty database, and data left by earlier runs fails tests that create fixed names (seen 2026-10-08: seven failures, all "already exists" and duplicate rows, gone on fresh services). With `CI=true` set, a missing `helm` turns the chart security step into a failure; without it the step self-tests its rules and skips the render.

One heavy run at a time on a machine: `verify.mjs` scopes that start the hosts or Testcontainers (integration, e2e, visual, demo, zap, all, story, checkpoint) take a lock in the machine's temporary folder (`ariva-verify.lock`) and a second one refuses to start while the holder lives (a stale lock is taken over). Stryker has its own lock (`ariva-mutation.lock`, one mutation run per machine) and runs in its own worktree (see the checkpoint's step 4), so one mutation run and one heavy run may share the machine. It was added after two checkpoints started in one worktree shared the E2E ports and collided in Stryker. One integration suite at a time on a machine: the Testcontainers of two runs share the Docker host's memory and disk (seen 2026-10-08 on a 16 GB host: PostgreSQL in crash recovery, 39 failures with 57P03, and later a full disk, "No space left on device"). Before a checkpoint, check the free space; build output of old worktrees and unused images are the first to go.

## Checkpoint: `node scripts/verify.mjs checkpoint`

Runs on a committed tree (it refuses uncommitted changes) every 5 stories, at the end of each phase and before go-live (ARV-138):

1. Build, unit tests and security, docs, web.
2. Integration, full.
3. Visual regression, then E2E in full, on fresh services (the visual baselines expect the demo seed).
4. Stryker, in a worktree of the checkpoint's commit in the machine's temporary folder (`ariva-mutation-tree`, removed afterwards; about 2 GB while it lives), after the checkpoint has released the machine lock: Stryker replaces `Ariva.Core.dll` in the unit tests' output while it runs, so in its own worktree it no longer blocks this checkout, and story gates may run meanwhile (owner decision 2026-10-09; before, a checkpoint with the queue engine changed held every story for over two hours). It uses one test session fewer than the machine's cores (at least two), so a story gate keeps a core. Stryker mutates the engine files changed since the previous checkpoint (one `--mutate` per changed file, which replaces the configured list); in full when no checkpoint is recorded; skipped when no engine file changed. Stryker's own `--since` mode ended silently after its coverage capture with the MTP runner on 2026-10-08, so it is not used. The step fails when Stryker puts any method in safe mode or prints no final score, even if Stryker exits zero (ARV-069a, `scripts/mutation-run.mjs`; the rule that avoids safe mode is in wiki 16).

While no checkpoint is recorded, `--mutation-base <commit>` names an earlier checkpoint run whose mutation step passed although another step failed: the rerun tests mutation only on engine code changed since that commit, and `checkpoint.json` records the base, so a fix to an unrelated step does not repeat a run of an hour or more.

When everything passes it writes `backlog/checkpoint.json` (commit, date, stories since the previous checkpoint, per-step results), which is committed as "Checkpoint: full suite green". The story gate prints how many stories have passed since the last checkpoint and says when one is due; the next story does not start until it has run.

A checkpoint failure is fixed before the next story starts. Each story is its own commit, so the failing change is one of at most five.

## What this trades

A regression outside a story's own area is found at the next checkpoint, up to four stories later, instead of at once. The wide list sends the changes most likely to cause that through the full suites at the story itself.
