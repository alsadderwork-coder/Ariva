# CLAUDE.md

Ariva is Dalil Tech's airport queue management system: sensor-based queue measurement, prediction and action for border authorities (Border module) and airport operators (Airport Operations module). It is a separate product from AMAN, built with AMAN's structure and conventions, and integrates with AMAN and any AODB through aggregate-only contracts.

Read this file first in every session. Depth lives in the skills (`.claude/skills/`) and docs (`docs/`); load them when the task touches their area.

## Repository map

```
Ariva.slnx, Directory.Build.props, Directory.Packages.props   .NET 10, C# 14, central package versions
Platform/Backplane/
  Ariva.Utilities        lowest layer, no Ariva references
  Ariva.Core             domain (Domain/{Common,Components,Constants,Contracts,Criteria,Entities,Enums,Events,InputModels,Lookups,ViewModels}), service interfaces, Global.cs permissions
  Ariva.Resources        .resx localisation (English, Arabic)
  Ariva.Infra            service implementations, NHibernate, Timescale scripts and COPY writers, Kafka, adapters' transports
  Ariva.Di               composition root extensions
  Ariva.Api.Common       security baseline, health, config layering (appsettings.base*.json)
  Ariva.Api.Main         51001  config and operations REST, SignalR live hub
  Ariva.Api.Ingest       51002  sensor adapters to Kafka
  Ariva.Api.Stream       51003  queue state engine (Kafka consumers)
  Ariva.Api.Cronz        51004  TickerQ jobs (forecast, SLA evaluation, reports, retention)
  Ariva.Api.Integration  51005  AODB and immigration integration (TOTP clients), AMAN feed
  Ariva.UnitTests, Ariva.IntegrationTests
Platform/Business/Ariva.Business.Contracts   AMAN feed contracts V1 (aggregate-only, packable)
Platform/Frontplane/Ariva.Web                SvelteKit 2, Svelte 5, Tailwind 4, bits-ui (shadcn-svelte), ECharts  51010
Platform/Simulation/Ariva.Simulation.Api     sensor, AODB and AMAN emulators; reference scenario seed 9303   51020
Platform/Testing/Ariva.E2E                   Playwright: API end-to-end and browser functional tests
Platform/Cloud/Ariva.K8s, Ariva.Cicd         Helm and Helmfile, Azure DevOps YAML (optional CD to Dalil Container Registry)
.github/                                     GitHub Actions (ci, security-scan, codeql, claude, images), Dependabot, templates
docs/        architecture, ADRs, domain formulas, data boundary, security controls, product roadmap
wiki/        project wiki in Markdown, rendered on GitHub (deployment, business flow, integration, operations, user and admin guides)
backlog/     ralph-tui PRDs (prd-phase0.json), progress log
security/    allowlist.json (exceptions, human-approved only)
scripts/     verify.mjs (all gates), security/scan.mjs (CWE gate)
../Aman      AMAN repository: READ-ONLY reference for patterns
```

## Commands

```
node scripts/verify.mjs quick         security scan of changed files + docs rules (Stop hook runs this)
node scripts/verify.mjs backend       build + unit tests + security gate
node scripts/verify.mjs web           Ariva.Web check, lint, build
node scripts/verify.mjs e2e           Playwright API end-to-end and functional tests (starts the hosts)
node scripts/verify.mjs integration   Testcontainers tests (Docker required)
node scripts/verify.mjs all           everything except integration
node scripts/security/scan.mjs        CWE gate; --self-test proves every rule fires
dotnet build Ariva.slnx
dotnet run --project Platform/Backplane/Ariva.Api.Main
cd Platform/Frontplane/Ariva.Web && npm run dev
```

## Non-negotiables

1. **Security on every change.** Every story is checked against the 14 CWEs in `docs/security/cwe-controls.md` (OS and command injection, code injection, SSRF, missing and incorrect authorization, missing authentication, improper authentication, trust boundary, privilege management, session fixation, SQL injection, buffer overflow, XSS). The post-edit hook scans each file you write; fix findings immediately. Default deny: every endpoint needs `[Permission]`, `[IntegrationScope]`, `[DeviceAuthenticated]` or a human-approved allowlist entry. Never approve an allowlist entry yourself.
2. **Data boundary.** Ariva never receives officer, traveller or document identities. AMAN contracts are aggregate-only (`docs/domain/data-boundary.md`). Border per-desk data is visible to border roles only.
3. **Tests are part of done.** Unit tests for domain and services, integration tests for persistence and Kafka, API end-to-end tests for every endpoint (including its authorization and attack payloads), Playwright functional tests for every screen. A story with failing or missing tests does not pass.
4. **AMAN conventions** (load the `aman-conventions` skill): `Svc` prefix, `Result<T>` from FluentX, `Fx.Specification` validation, rich entity methods, `Controllers/AdminArea/<Entity>/Controller.cs`, `#region` blocks, primary constructors, `CancellationToken` everywhere, UTC only. AMAN's own CLAUDE.md is stale on messaging (it says Rebus; AMAN uses MassTransit with Kafka). Ariva uses MassTransit 8.5 with the Kafka Rider behind `ISvcMessageBus`, plus its own outbox, inbox and dead-letter filters; only the stateful Stream engine uses the raw Confluent consumer (ADR-0018). Never reference MassTransit types from Ariva.Core.
5. **Schema.** NHibernate maps configuration aggregates; `SchemaUpdate` is allowed only in `vm-local`. Production schema and every TimescaleDB object come from versioned scripts in `Ariva.Infra/Timescale/Scripts/NNNN_*.sql`. Hot time-series writes use Npgsql binary COPY.
6. **Reference AMAN, never change it.** Read `../Aman` to port patterns (integration auth, storage provider, messaging, controllers). Hooks block writes there.
7. **Writing rule.** No em dashes, en dashes used as dashes, or double hyphens in docs, comments or strings. Use commas, colons, semicolons, parentheses or periods.
8. **Story branches only, no cluster changes, no destructive git.** The repository is `github.com/alsadderwork-coder/Ariva` (private). Commit on a story branch (`story/ARV-nnn-<slug>`, or `ralph/<prd>` under ralph-tui), push it with `git push -u origin <branch>` and open a pull request with `gh pr create`; never push `main`, never force push. The `ci` workflow and the `security-reviewer` must pass; a human merges and releases.

## MCP servers (use them before guessing)

| Server | Use it for |
|---|---|
| `microsoft-learn` | ASP.NET Core, .NET, Kestrel, SignalR, Data Protection, authentication and authorization, rate limiting. Check the current API before writing framework code. |
| `context7` | Library docs: NHibernate, MassTransit, Confluent.Kafka, FusionCache, TickerQ, Mapster, Otp.NET, Npgsql, Testcontainers, Playwright, ECharts. |
| `svelte` | Svelte 5 and SvelteKit docs; run `svelte-autofixer` on every `.svelte` file you write. |
| `shadcn-svelte` | shadcn-svelte components, Bits UI API, Lucide icons (community server). |
| `nuget` | Real package versions and vulnerability fixes. Never invent a version. |
| `playwright` | Drive the running app while writing functional tests. |
| `semgrep` | `security_check` on changed files, in addition to `scripts/security/scan.mjs`. |
| `github` | Issues, pull requests, Actions runs and Dependabot alerts for `alsadderwork-coder/Ariva` (needs `GITHUB_PERSONAL_ACCESS_TOKEN`). Create or change issues only when the human asks (`/sync-issues`). |
| `postgres-dev` | Read-only inspection of the local dev database (restricted mode). |

Setup and prerequisites: `docs/harness/README.md`.

## How to work a story

1. Pick the story (`/story <id>` or the id ralph-tui gives you). Read its acceptance criteria and the docs and skills it names.
2. Plan briefly: files to touch, CWEs involved, tests to write. For domain or contract changes, consult `docs/domain/*` first.
3. Write tests and code together. Follow the layering; run `dotnet build` early.
4. Run the gates in the story's acceptance criteria (`node scripts/verify.mjs backend` at minimum; `web` and `e2e` for UI or API stories).
5. Ask the `security-reviewer` subagent to review the diff against `docs/security/cwe-controls.md`; fix what it finds.
6. Update docs or wiki pages the change affects. Append what you learned to `backlog/progress.md` (one dated entry: story, what changed, gotchas).
7. Set `"passes": true` for the story in `backlog/prd-phase0.json` only when every criterion is met. Commit with message `ARV-nnn: <title>`, push the story branch and open a pull request that follows `.github/pull_request_template.md`. Under ralph-tui, end with `<promise>COMPLETE</promise>`.

If a story is too big for one session, stop, split it in the PRD (new ids with a suffix, for example ARV-031a), and record why in `backlog/progress.md`.

## Subagents (`.claude/agents/`)

architect, domain-modeler, backend-engineer, stream-engineer, integration-engineer, sensor-adapter-engineer, web-engineer, test-engineer, security-reviewer (read-only), devops-engineer, docs-writer. Delegate by area; the security-reviewer reviews every story before it passes.

## Key docs

`docs/architecture/overview.md`, `docs/architecture/adr/README.md`, `docs/architecture/integration.md`, `docs/architecture/sensor-adapters.md`, `docs/domain/formulas.md`, `docs/domain/glossary.md`, `docs/domain/data-boundary.md`, `docs/security/cwe-controls.md`, `docs/product/roadmap.md`, `docs/plan/implementation-plan.md`, `docs/design/prototype/` (clickable prototype and its spec: the UI and behaviour reference).
