# Agentic harness: how to run Ariva with Claude Code

## One-time setup on the developer machine (Windows)

1. Clone Ariva next to AMAN: `git clone https://github.com/alsadderwork-coder/Ariva.git D:\DevOps\Ariva`, with AMAN at `D:\DevOps\Aman` (agents read AMAN as reference through `additionalDirectories`; hooks block writes to it).
2. Install: .NET 10 SDK, Node 22 or later, Docker Desktop (or Rancher Desktop) for Testcontainers and Compose, Git, the GitHub CLI (`winget install GitHub.cli`, then `gh auth login`), Claude Code, ralph-tui (`npm i -g ralph-tui` or as you installed it for AMAN), and `uv` for the Python-based MCP servers (`winget install astral-sh.uv`).
3. `npx playwright install chromium` once.
4. Environment variables: `GITHUB_PERSONAL_ACCESS_TOKEN` for the github MCP server (fine-grained token for `alsadderwork-coder/Ariva` only: Contents, Issues, Pull requests and Actions read and write, Metadata read; no Administration). Optional: `ARIVA_DEV_DATABASE_URI` for the read-only dev database MCP (`node scripts/dev-up.mjs` prints it with the generated password), `CONTEXT7_API_KEY` if you have a Context7 key (add a header in `.mcp.json`).
5. Open the folder in Claude Code; approve the project MCP servers when asked (they are listed in `.claude/settings.json` under `enabledMcpjsonServers`).

## MCP servers (`.mcp.json`)

| Server | Transport | Purpose | Notes |
|---|---|---|---|
| microsoft-learn | HTTP, no auth | Official .NET and ASP.NET Core docs | https://learn.microsoft.com/api/mcp |
| context7 | HTTP | Library docs (NHibernate, MassTransit, Confluent.Kafka, TickerQ, FusionCache, Otp.NET, Npgsql, Testcontainers, Playwright, ECharts) | Works without a key at lower limits |
| svelte | HTTP | Svelte 5 and SvelteKit docs, svelte-autofixer | Official, https://mcp.svelte.dev/mcp |
| shadcn-svelte | HTTP | shadcn-svelte components, Bits UI API, Lucide icons | Community server (Michael-Obele/shadcn-svelte-mcp); the official `shadcn-svelte mcp` command was still a pull request when this was set up |
| playwright | stdio, npx | Drive the running app while writing functional tests | Microsoft |
| nuget | stdio, dnx (.NET 10 SDK) | Real package versions, vulnerability fixes | Microsoft NuGet MCP server |
| github | HTTP | Issues, pull requests, Actions runs, Dependabot alerts | GitHub's remote MCP server (https://api.githubcopilot.com/mcp/) with `GITHUB_PERSONAL_ACCESS_TOKEN`; tools require approval (`ask`) |
| semgrep | stdio, uvx | `security_check` on changed files | The standalone repo is archived and moving into the semgrep CLI; switch to `semgrep mcp` when your CLI version supports it |
| postgres-dev | stdio, uvx | Read-only inspection of the dev database | Postgres MCP Pro in `--access-mode=restricted` |

If a stdio server fails to start on native Windows, wrap the command: `"command": "cmd", "args": ["/c", "npx", ...]`.

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
