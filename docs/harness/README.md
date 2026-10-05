# Agentic harness: how to run Ariva with Claude Code

## One-time setup on the developer machine (Windows)

1. Clone Ariva next to AMAN: `git clone https://github.com/alsadderwork-coder/Ariva.git D:\DevOps\Ariva`, with AMAN at `D:\DevOps\Aman` (agents read AMAN as reference through `additionalDirectories`; hooks block writes to it).
2. Install: .NET 10 SDK, Node 22 or later, Docker Desktop (or Rancher Desktop) for Testcontainers and Compose, Git, the GitHub CLI (`winget install GitHub.cli`, then `gh auth login`), Claude Code, ralph-tui (`npm i -g ralph-tui` or as you installed it for AMAN), and `uv` for the Python-based MCP servers (`winget install astral-sh.uv`).
3. `npx playwright install chromium` once.
4. Environment variables: `GITHUB_PERSONAL_ACCESS_TOKEN` for the github MCP server (fine-grained token for `alsadderwork-coder/Ariva` only: Contents, Issues, Pull requests and Actions read and write, Metadata read; no Administration). Optional: `ARIVA_DEV_DATABASE_URI` for the read-only dev database MCP (`node scripts/dev-up.mjs` prints it with the generated password), `CONTEXT7_API_KEY` if you have a Context7 key (add a header in `.mcp.json`), `SHADCN_MCP_URL` for the shadcn.io Pro MCP server (the full URL including `?token=`; it is a secret, so keep it in `.claude/settings.local.json` under `env` or your shell, never in `.mcp.json`).
5. Open the folder in Claude Code; approve the project MCP servers when asked (they are listed in `.claude/settings.json` under `enabledMcpjsonServers`).

## Dev container (Codespaces and cloud agent sessions, ARV-076)

`.devcontainer/` gives a Linux workspace where every gate runs: open the repository in a Codespace (or "Reopen in Container" with the Dev Containers extension), wait for the post-create step, then `node scripts/verify.mjs all --with-integration`.

- Image (`.devcontainer/Dockerfile`): built only from images pinned by version and digest, with nothing downloaded during its own build. The docker-outside-of-docker feature then adds the Docker CLI 29.4.3 from Docker's signed apt repository, pinned by version, not by digest.
  - The .NET 10.0.103 SDK, the one the host images build with.
  - Node 22.
  - The Playwright 1.63.0 image as the base. It brings Chromium for Ariva.E2E's @playwright/test and its libraries, and it is the same image the visual baselines render in (`scripts/visual-browser.mjs`).
  - uv, for the semgrep and postgres-dev MCP servers.
  - The Docker CLI with Compose.
  - Helm, at the version CI installs, for the chart gate.
- Checks on the image: `scripts/base-images.mjs --check` covers the Dockerfile's FROM lines, holds the Playwright tag to the @playwright/test version in the E2E lock file, requires `scripts/visual-browser.mjs` to use the same digest, and holds Helm to the version `ci.yml` installs. Dependabot updates the others; the Playwright image moves with the package, together with the visual baselines.
- User and Docker (CWE-269):
  - The user is the image's non-root `pwuser`, with no sudo.
  - Docker is the host's engine, reached through its socket by the `docker-outside-of-docker` feature, pinned by digest, with its CLI pinned to the image's version. That socket is root-equivalent on its host. In a Codespace the host is a disposable VM. With "Reopen in Container" on a workstation, the host is your own machine: code in the container (npm install scripts, agents) gets the same reach, so use it only with a checkout you trust. Docker-in-docker would need a privileged container, which is worse.
  - `scripts/base-images.mjs --check` (the security gate) fails if this posture slips: a root or missing last USER, sudo, root `remoteUser`, privileged `runArgs`, a feature not pinned by digest, docker-in-docker, or a feature CLI version that differs from the image's.
  - The container uses the host network, so the Compose services' 127.0.0.1 ports and Testcontainers' mapped ports are `localhost` as on a workstation.
- On creation: `.devcontainer/post-create.sh` runs `npm ci` for Ariva.Web, Ariva.E2E and the chart tests, `dotnet restore`, and both tool manifests.
- On every start: `node scripts/dev-up.mjs` brings up TimescaleDB, Kafka, Redis and smtp4dev from `docker-compose.dev.yml` (first run: `.env` with random passwords).
- Everything Ariva runs in the container binds 127.0.0.1, except the ports Testcontainers publishes during integration tests. Keep the Codespace's forwarded ports Private.
- With the host's engine, bind mounts resolve on the host. Compose's `deploy/local/postgres-init` and the visual browser's `playwright-core` mount may then come up empty in a Codespace (both fail closed). If they do, run those from a workstation.
- Size: 4 cores and 16 GB of memory (`hostRequirements`). The full E2E run starts five hosts, the simulator and the web preview.
- The E2E suites need the run's database, Kafka and Redis in their environment, as in CI (`ARIVA_E2E_SCHEMA_UPDATE`, `Database__*`, `ARIVA_E2E_REDIS_URL`, `ARIVA_E2E_KAFKA_BOOTSTRAP`; see the `e2e` job in `.github/workflows/ci.yml`). Without them, the suites that need a database are skipped. The visual baselines need nothing else: `node scripts/verify.mjs visual` starts the pinned image beside the dev container.
- Checked on 2026-10-05: the image built from the pinned inputs and the post-create step passed. Inside the container, `node scripts/verify.mjs all --with-integration` passed unit, web, security, docs, E2E with the database environment, and integration with Testcontainers through the mounted socket; only the chart gate failed, because Helm was not yet in the image. After Helm was added, `node scripts/verify.mjs security` (chart gate included) passed in the rebuilt image.

## MCP servers (`.mcp.json`)

| Server | Transport | Purpose | Notes |
|---|---|---|---|
| microsoft-learn | HTTP, no auth | Official .NET and ASP.NET Core docs | https://learn.microsoft.com/api/mcp |
| context7 | HTTP | Library docs (NHibernate, MassTransit, Confluent.Kafka, TickerQ, FusionCache, Otp.NET, Npgsql, Testcontainers, Playwright, ECharts) | Works without a key at lower limits |
| svelte | HTTP | Svelte 5 and SvelteKit docs, svelte-autofixer | Official, https://mcp.svelte.dev/mcp |
| shadcn-svelte | HTTP | shadcn-svelte components, Bits UI API, Lucide icons | Community server (Michael-Obele/shadcn-svelte-mcp); the official `shadcn-svelte mcp` command was still a pull request when this was set up |
| shadcn | HTTP, token in URL | shadcn.io Pro components, blocks and registry items | `https://www.shadcn.io/api/mcp?token=...` from `SHADCN_MCP_URL` (placeholder `https://shadcn.invalid/mcp` when unset); optional, the server simply fails to connect without them. Pro items are React-first: port them to shadcn-svelte and Bits UI, never add React to Ariva.Web |
| playwright | stdio, npx | Drive the running app while writing functional tests | Microsoft |
| nuget | stdio, dnx (.NET 10 SDK) | Real package versions, vulnerability fixes | Microsoft NuGet MCP server |
| github | HTTP | Issues, pull requests, Actions runs, Dependabot alerts | GitHub's remote MCP server (https://api.githubcopilot.com/mcp/) with `GITHUB_PERSONAL_ACCESS_TOKEN`; tools require approval (`ask`) |
| semgrep | stdio, uvx | `security_check` on changed files | The standalone repo is archived and moving into the semgrep CLI; switch to `semgrep mcp` when your CLI version supports it |
| postgres-dev | stdio, uvx | Read-only inspection of the dev database | Postgres MCP Pro in `--access-mode=restricted` |
| aspire | stdio, `dotnet tool run aspire` (local tool, `.config/dotnet-tools.json`) | Resources, health, console and structured logs, traces and commands of a running Ariva.AppHost (ARV-066) | Aspire CLI 13.6 `aspire agent mcp`; finds an AppHost started with `dotnet run`; shows variable names but never their values. Run `dotnet tool restore` once |

If a stdio server fails to start on native Windows, wrap the command: `"command": "cmd", "args": ["/c", "npx", ...]`.

## Running the system for agents (ARV-066)

`dotnet run --project Platform/Cloud/Ariva.AppHost` starts every dependency and host (see wiki 04, "One F5"). With it running, agents read resource state, logs and traces through the aspire MCP server instead of guessing from process output.

- E2E against the AppHost: `node scripts/e2e-apphost.mjs [playwright arguments]` draws the run's secrets, has the Playwright config write each host's variables (`ARIVA_E2E_WRITE_HOST_ENV`), starts the AppHost with them (no Stream, no dev server, throwaway containers without a volume, ports apart from the development AppHost), runs Playwright against those hosts and stops everything afterwards. CI keeps compose and Playwright's own host start.
- Integration tests against an existing server: set `ARIVA_IT_POSTGRES` to a superuser connection string (for example the AppHost's TimescaleDB: `Host=localhost;Port=5433;Username=ariva;Password=<database-owner-password>`); the tests drop and recreate only their own `ariva_it` and `it_*` databases there. Without it they start their own container.

## Ways to work

**Interactive (architecture, refinement, reviews):** open Claude Code in the repo. Useful commands: `/next-story`, `/grill-pbi ARV-0nn`, `/story ARV-0nn`, `/security-check`, `/verify backend`, `/adr <title>`, `/new-entity <Name>`, `/new-adapter <family>`, `/new-integration-endpoint ...`, `/replay`, `/sync-issues` (only when you want GitHub issues created or updated).

**Autonomous loop (implementation):**
```
git switch -c ralph/phase0
ralph-tui run --prd backlog/prd-phase0.json
```
Each iteration starts a fresh Claude Code session on the highest-priority story whose dependencies pass, follows CLAUDE.md ("How to work a story"), runs the gates, gets the security-reviewer verdict, logs to `backlog/progress.md`, sets `passes: true` and commits. Agents may push the `ralph/` or `story/` branch and open a pull request (`gh pr create`); the `ci` workflow runs the same gates, the `claude` workflow posts a security review, and you merge. Pushing `main`, force pushes and repository settings stay with you.

**Guard rails (always on):** default-deny permissions in `.claude/settings.json`; hooks block pushes other than story branches, force pushes, cluster changes, destructive git and SQL, writes to AMAN, secrets in the repo, self-approved security exceptions and dashes in docs; every edited file is security-scanned immediately; the Stop hook runs the quick gate.

## When a loop gets stuck
- A story fails its gates three iterations in a row: stop the loop, run `/grill-pbi` on it, split it in `backlog/generate.py`, regenerate (`python backlog/generate.py`, which keeps recorded `passes` and `notes`).
- A gate is wrong: fix the gate in a separate, human-reviewed change; never weaken it inside a story.

## GitHub repository settings (one time, by the owner)

- Visibility: private (set 2026-10-01). It was public for about an hour after creation; anyone who cloned it then keeps a copy, so treat anything in that first commit as disclosed.
- Actions secrets: `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`) or `ANTHROPIC_API_KEY` for the `claude` workflow; install the Claude GitHub App on the repository so the workflow can comment.
- Optional variable `ENABLE_CODEQL=true` once GitHub Code Security (Advanced Security) is licensed; CodeQL, secret scanning and dependency review need it on private repositories.
- Branch protection or a ruleset on `main` (require the `ci` checks and one review, block force pushes and deletions) needs GitHub Pro or a Team organisation for a private repository; on GitHub Free the harness hooks are the only guard, so move the repository to a Dalil organisation on Team when the team grows.
- Dependabot alerts and security updates: on (free on private repositories).
