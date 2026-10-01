# Agentic harness: how to run Ariva with Claude Code

## One-time setup on the developer machine (Windows)

1. Clone Ariva next to AMAN: `D:\DevOps\Ariva` and `D:\DevOps\Aman` (agents read AMAN as reference through `additionalDirectories`; hooks block writes to it).
2. Install: .NET 10 SDK, Node 22 or later, Docker Desktop (or Rancher Desktop) for Testcontainers and Compose, Git, Claude Code, ralph-tui (`npm i -g ralph-tui` or as you installed it for AMAN), and `uv` for the Python-based MCP servers (`winget install astral-sh.uv`).
3. `npx playwright install chromium` once.
4. Optional environment variables: `ARIVA_DEV_DATABASE_URI` for the read-only dev database MCP (defaults to the local Compose database), `CONTEXT7_API_KEY` if you have a Context7 key (add a header in `.mcp.json`).
5. Open the folder in Claude Code; approve the project MCP servers when asked (they are listed in `.claude/settings.json` under `enabledMcpjsonServers`). The Azure DevOps server opens a browser sign-in for the DalilCloud organisation on first use.

## MCP servers (`.mcp.json`)

| Server | Transport | Purpose | Notes |
|---|---|---|---|
| microsoft-learn | HTTP, no auth | Official .NET, ASP.NET Core and Azure DevOps docs | https://learn.microsoft.com/api/mcp |
| context7 | HTTP | Library docs (NHibernate, Confluent.Kafka, TickerQ, FusionCache, Otp.NET, Npgsql, Testcontainers, Playwright, ECharts) | Works without a key at lower limits |
| svelte | HTTP | Svelte 5 and SvelteKit docs, svelte-autofixer | Official, https://mcp.svelte.dev/mcp |
| shadcn-svelte | HTTP | shadcn-svelte components, Bits UI API, Lucide icons | Community server (Michael-Obele/shadcn-svelte-mcp); the official `shadcn-svelte mcp` command was still a pull request when this was set up |
| playwright | stdio, npx | Drive the running app while writing functional tests | Microsoft |
| nuget | stdio, dnx (.NET 10 SDK) | Real package versions, vulnerability fixes | Microsoft NuGet MCP server |
| azure-devops | stdio, npx | Work items, repos, wiki, pipelines in DalilCloud | Microsoft; tools require approval (`ask`) |
| semgrep | stdio, uvx | `security_check` on changed files | The standalone repo is archived and moving into the semgrep CLI; switch to `semgrep mcp` when your CLI version supports it |
| postgres-dev | stdio, uvx | Read-only inspection of the dev database | Postgres MCP Pro in `--access-mode=restricted` |

If a stdio server fails to start on native Windows, wrap the command: `"command": "cmd", "args": ["/c", "npx", ...]`.

## Ways to work

**Interactive (architecture, refinement, reviews):** open Claude Code in the repo. Useful commands: `/next-story`, `/grill-pbi ARV-0nn`, `/story ARV-0nn`, `/security-check`, `/verify backend`, `/adr <title>`, `/new-entity <Name>`, `/new-adapter <family>`, `/new-integration-endpoint ...`, `/replay`, `/sync-ado` (only when you want PBIs created).

**Autonomous loop (implementation):**
```
git switch -c ralph/phase0
ralph-tui run --prd backlog/prd-phase0.json
```
Each iteration starts a fresh Claude Code session on the highest-priority story whose dependencies pass, follows CLAUDE.md ("How to work a story"), runs the gates, gets the security-reviewer verdict, logs to `backlog/progress.md`, sets `passes: true` and commits. Review the branch daily, push it yourself, and open the PR; the PR pipeline (`Analyze-solution.yaml`) runs the same gates.

**Guard rails (always on):** default-deny permissions in `.claude/settings.json`; hooks block pushes, cluster changes, destructive git and SQL, writes to AMAN, secrets in the repo, self-approved security exceptions and dashes in docs; every edited file is security-scanned immediately; the Stop hook runs the quick gate.

## When a loop gets stuck
- A story fails its gates three iterations in a row: stop the loop, run `/grill-pbi` on it, split it in `backlog/generate.py`, regenerate (`python backlog/generate.py`, which keeps recorded `passes` and `notes`).
- A gate is wrong: fix the gate in a separate, human-reviewed change; never weaken it inside a story.
