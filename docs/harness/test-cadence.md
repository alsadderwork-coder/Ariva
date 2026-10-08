# Test cadence

Owner decision 2026-10-08. Every story still passes its own gates before it is marked passing; the full suite runs at checkpoints instead of twice in every story. Measured on ARV-104b before the change: unit tests about 2 minutes plus the build, integration 8 minutes, full E2E about 11 minutes plus host start-up, Stryker about 35 minutes, and the full gates ran twice per story (before and after the security review's fixes).

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

## Checkpoint: `node scripts/verify.mjs checkpoint`

Runs on a committed tree (it refuses uncommitted changes) every 5 stories, at the end of each phase and before go-live (ARV-138):

1. Build, unit tests and security, docs, web.
2. Integration, full.
3. Visual regression, then E2E in full, on fresh services (the visual baselines expect the demo seed).
4. Stryker on the engine files changed since the previous checkpoint (`--since:<commit>`); in full when no checkpoint is recorded; skipped when no engine file changed.

While no checkpoint is recorded, `--mutation-base <commit>` names an earlier checkpoint run whose mutation step passed although another step failed: the rerun tests mutation only on engine code changed since that commit, and `checkpoint.json` records the base, so a fix to an unrelated step does not repeat a run of an hour or more.

When everything passes it writes `backlog/checkpoint.json` (commit, date, stories since the previous checkpoint, per-step results), which is committed as "Checkpoint: full suite green". The story gate prints how many stories have passed since the last checkpoint and says when one is due; the next story does not start until it has run.

A checkpoint failure is fixed before the next story starts. Each story is its own commit, so the failing change is one of at most five.

## What this trades

A regression outside a story's own area is found at the next checkpoint, up to four stories later, instead of at once. The wide list sends the changes most likely to cause that through the full suites at the story itself. Stryker's since feature with the Microsoft Testing Platform runner is first used at the second checkpoint; if it fails there, the checkpoint runs the full mutation instead and this page records it.
