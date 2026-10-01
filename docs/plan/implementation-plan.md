# Ariva implementation plan

As of 1 October 2026. Owner: Ahmad (architect and product owner). Delivery model: one developer half-time with Claude Code until the pilot contract, Ahmad part-time.

## The answer

Ariva is a private GitHub repository (`github.com/alsadderwork-coder/Ariva`, local clone `D:\DevOps\Ariva`) that mirrors AMAN's layout, conventions and look, with a Claude Code harness that lets agents build it story by story under hard gates. Phase 0 (demo core, October 2026 to April 2027) is 65 stories in 11 epics, plus 11 engineering-quality stories (E11) that can be interleaved; each is sized for one agent session, ordered by dependency, and run through ralph-tui exactly as AMAN's `.ralph-tui` is configured. Every story must pass the build with security analyzers, unit tests, a 14-CWE security gate, API end-to-end, Playwright functional and accessibility tests, plus a security-reviewer verdict, before it can be marked done. Agents push story branches and open pull requests; GitHub Actions runs the same gates; a human merges and releases.

What already exists and passes in the build environment: the full solution skeleton (16 projects build with 0 warnings), a security baseline (default deny, headers, limits, rate limiting, safe errors, trusted proxies, CORS allowlist, CSP, non-root containers), the web shell in Aman.Web's design system, 70 end-to-end, functional and accessibility tests passing, GitHub Actions (CI gate, Trivy, Semgrep, SBOM, CodeQL switch, Claude review, images) with Dependabot, unit tests compiled against stand-ins (they run for real in story ARV-001), the security gate at 0 errors, architecture and domain docs with 25 ADRs, and a 21-page wiki.

## Decisions taken in this step

| Decision | Choice | Status |
|---|---|---|
| Product and code name | Ariva (folder, solution and namespaces) | Taken by Ahmad. Trademark pre-check 2026-10-01: medium to high risk. PASSUR Aerospace sells ARiVA, an airport and airline operations analytics SaaS (same buyers); a US ARIVA filing in classes 9 and 42 is pending (July 2026); ARRIVA is phonetically identical in the EU. Registers for UAE, WIPO and EUIPO were not reachable and need manual searches. Recommendation: rename before branding spend, or keep Ariva only for the Middle East and Africa after clean UAE and WIPO searches |
| Repository structure | Mirror AMAN: Platform/Backplane, Business, Frontplane, Simulation, Cloud; Core, Infra, Di, Api.* | Taken |
| Hosting and CI | GitHub, private; GitHub Actions for CI and images (GHCR); Dependabot; Azure DevOps pipelines kept for Dalil Container Registry deployments | Taken 2026-10-01 (ADR-0016 amendment) |
| Visual design | Aman.Web's design system (tokens, DM Sans, sidebar and header, page pattern), with Arabic type and WCAG AA contrast fixes | Taken 2026-10-01 |
| ORM | NHibernate through AMAN's IStorageProvider pattern; SchemaUpdate only in vm-local; production schema and TimescaleDB objects from versioned SQL scripts | Taken by Ahmad (Claude recommended EF Core; NHibernate keeps parity) |
| Messaging | MassTransit 8.5.11 Kafka Rider behind AMAN's ISvcMessageBus, with Ariva-owned NHibernate outbox, inbox and dead-letter filters; raw Confluent consumer for the stateful Stream engine | Accepted 2026-10-01 (ADR-0018). Risk: v8 maintenance ends after 2026; decide v9 licence or Confluent swap before go-live |
| AMAN contracts | Four aggregate-only V1 records, reconciled with D5 (cycle time, documents, lane category, boarded total, coarse reject categories with small-cell suppression) | Taken |
| Integration with AODBs and immigration | AMAN-compatible client id, secret and TOTP, hardened; generic Integration API v1 plus AIDX, ACRIS, SSIM and the AMAN feed | Taken |
| Sensor support | Transports plus dialect mappers and capability tiers T1 to T4; Xovis PC2, PC3 and PF series through one adapter family; LiDAR through perception platforms | Taken |
| Security | 14-CWE control matrix, enforced at edit time, build time, test time and in the pipeline | Taken |
| Agent tooling | Claude Code with project MCP servers (Microsoft Learn, Context7, Svelte, shadcn-svelte, Playwright, NuGet, GitHub, Semgrep, read-only Postgres) | Taken |

## Repository map (AMAN to Ariva)

| AMAN | Ariva | Purpose |
|---|---|---|
| Aman.Utilities, Aman.Core, Aman.Resources | Ariva.Utilities, Ariva.Core, Ariva.Resources | Domain, service interfaces, localisation |
| Aman.Infra, Aman.Di | Ariva.Infra, Ariva.Di | Implementations, NHibernate, Kafka, Timescale, composition |
| Aman.Api.Common | Ariva.Api.Common | Security baseline, health, config layering |
| Aman.Api.Main (50001) | Ariva.Api.Main (51001) | Configuration and operations API, SignalR live hub |
| none | Ariva.Api.Ingest (51002) | Sensor adapters to Kafka |
| none | Ariva.Api.Stream (51003) | Queue state engine |
| Aman.Api.Cronz | Ariva.Api.Cronz (51004) | TickerQ jobs |
| Aman.Api.Integration | Ariva.Api.Integration (51005) | AODB and immigration integration, TOTP clients |
| Aman.Business.Contracts | Ariva.Business.Contracts | AMAN feed contracts (packable) |
| Aman.Web (50010) | Ariva.Web (51010) | SvelteKit 2, Svelte 5, Tailwind 4, bits-ui, ECharts |
| Aman.Simulation.* | Ariva.Simulation.Api (51020) | Sensor, AODB and AMAN emulators, seed 9303 scenario |
| Aman.UnitTests | Ariva.UnitTests, Ariva.IntegrationTests, Platform/Testing/Ariva.E2E | Unit, Testcontainers, Playwright API and functional |
| Aman.K8s, Aman.Cicd | Ariva.K8s, Ariva.Cicd | Helm and Helmfile, Azure DevOps YAML |

Ports start at 51000 so both products run side by side on one machine.

## How the harness works

| Layer | Files | What it does |
|---|---|---|
| Project memory | `CLAUDE.md` plus `Platform/Backplane`, `Platform/Frontplane/Ariva.Web`, `Platform/Cloud` CLAUDE.md files | Map, commands, eight non-negotiables, story protocol, MCP usage |
| Skills | `.claude/skills/*` (9) | Loaded on demand: AMAN conventions, domain, persistence, Kafka, integration auth, sensors, security, Svelte UI, testing |
| Subagents | `.claude/agents/*` (11) | Architect, domain modeler, backend, stream, integration, sensor adapters, web, test, security reviewer (read-only), DevOps, docs |
| Commands | `.claude/commands/*` (11) | `/story`, `/next-story`, `/grill-pbi`, `/security-check`, `/verify`, `/new-entity`, `/new-adapter`, `/new-integration-endpoint`, `/adr`, `/sync-issues`, `/replay` |
| Hooks | `.claude/hooks/*` | Allow pushes of story branches only (never main, never forced); block cluster changes, destructive git and SQL, AMAN writes, secrets, self-approved exceptions and dashes in docs; scan every edited file for CWE findings; run the quick gate before stopping; show the next stories at session start |
| MCP servers | `.mcp.json`, approved in `.claude/settings.json` | Ground every framework and library call in current docs; real package versions; GitHub issues, pull requests and runs on request |
| Gates | `scripts/verify.mjs`, `scripts/security/scan.mjs` | One command per scope: quick, backend, integration, web, e2e, security, docs, all |
| Backlog | `backlog/prd-phase0.json` (ralph-tui), `backlog/PRD-phase0.md`, `backlog/generate.py`, `backlog/progress.md` | Stories with acceptance criteria, gates, CWEs and dependencies; one source generates both files |

Story flow: ralph-tui picks the highest-priority story whose dependencies pass, opens a fresh Claude Code session, the agent plans, delegates by area, writes tests and code, runs the gates, gets a security-reviewer verdict, updates docs, logs progress, marks the story and commits on `ralph/phase0`, then pushes the branch and opens or updates the pull request. The `ci` workflow runs the same gates, the `claude` workflow posts a security review, and Ahmad merges.

## Security approach (the 14 CWEs)

Two ids in the request were mislabelled: code injection is CWE-94 and command injection is CWE-77; both are covered alongside the 12 others. Controls sit in eight layers: edit-time scan, .NET security analyzers as errors, the repository scanner (40 rules, self-tested), architecture tests (endpoint inventory, forbidden dependencies, entity binding, unsafe code), behaviour tests with attack payloads (API and browser), dependency audits, OWASP ZAP against the dev deployment (ARV-063), and an external penetration test before the pilot. Exceptions need a human approval in `security/allowlist.json`; the only entries are the six Kubernetes probe routes, approved by Ahmad on 1 October 2026. Full matrix: `docs/security/cwe-controls.md`.

AMAN fixes, done on the local branch `feature/integration-auth-hardening` in `D:\DevOps\Aman` (two commits, not pushed, not compiled here because NuGet is unreachable from the agent environment): constant-time secret check, one generic error, TOTP replay guard, lockout and per-IP rate limit, UTC expiry, TOTP secret cache expiry, and removal of `[Log]` from the login method, which was writing the client secret and TOTP code to the logs (CWE-532); then PBKDF2 hashing of client secrets with lazy migration and a one-time credentials dialog in Aman.Web. Still open in AMAN: the idempotency filter is never registered, the outbox replay job is commented out, Kafka consumer faults are discarded without retry or dead letters, producers are keyless on single-partition topics, `GetAll` on the integration client controller has no permission, and the client details view (with secrets) is cached in Redis.

## Phase 0 backlog

| Epic | Stories | Highlights |
|---|---|---|
| E0 Foundation and security baseline | ARV-001 to ARV-012 | Restore and run everything for real, container hardening, Compose, port AMAN foundations and NHibernate, script runner, logging with redaction, Data Protection, permission matrix, user auth with TOTP and refresh rotation, role administration, site scope |
| E1 Site topology and zone profiles | ARV-013 to ARV-019 | Topology, desk code mapping, versioned zone profiles with invariants, floor plans with SVG sanitising, DMO seed |
| E2 Messaging, sensing and ingest | ARV-020 to ARV-026 | Kafka with outbox and dead letters, devices and calibrations, device auth, Xovis push ingest, declarative mapper and MQTT, heartbeats, raw event archive |
| E3 Simulation | ARV-027 to ARV-029 | Port the prototype scenario (seed 9303), sensor emulator, AODB, AMAN and immigration emulators |
| E4 Queue state engine and stream | ARV-030 to ARV-036 | Pure engine, wait attribution and bins, nowcast, desk state, stream host, SignalR live hub, golden replay |
| E5 Alerting | ARV-037 to ARV-040 | Typed rules, evaluation with backtest, lifecycle, email |
| E6 Flights and AODB integration | ARV-041 to ARV-047 | Flight model, integration clients with TOTP, Integration API, AIDX, outbound endpoints and ACRIS, SSIM, arrival wave |
| E7 AMAN and immigration integration | ARV-048 to ARV-050 | Feed consumer and REST endpoints, desk sessions and e-gate coupling, outbound AMAN connector |
| E8 Web application | ARV-051 to ARV-059 | Shell and login, admin screens, zones editor, devices, live operations, alert rules, immigration, passenger display, users and audit |
| E9 Reports | ARV-060 to ARV-061 | Daily report, CSV, scheduled delivery, reports screen |
| E10 Deployment and demo | ARV-062 to ARV-065 | Complete Helm deployment, ZAP pipeline, scripted demo, exit review |
| E11 Engineering quality | ARV-066 to ARV-076 | .NET Aspire AppHost and Aspire MCP, AsyncAPI for topics, Pact for the AMAN feed, Stryker.NET mutation testing, property-based tests, NBomber load tests, Toxiproxy failure tests, signed images with SBOM, OWASP ASVS L2 mapping, visual regression, dev container |

Critical path (longest dependency chain, 18 stories): ARV-001, ARV-003, ARV-005, ARV-008, ARV-010, ARV-011, ARV-012, ARV-014, ARV-021, ARV-022, ARV-023, ARV-026, ARV-034, ARV-038, ARV-039, ARV-040, ARV-060, ARV-061. Authentication and site scope (ARV-010 to ARV-012) sit on it early, so they get the most review; the live demo path (ARV-034, ARV-035, ARV-055, ARV-064) runs one or two stories behind it.

Estimate (assumption, to recalibrate after the first 10 stories): a half-time developer reviewing agent output completes 3 to 5 stories a week once E0 is done, and 2 a week during E0 because porting AMAN foundations needs judgement. That gives about 16 to 24 weeks for Phase 0, inside the October to April window with 6 to 14 weeks of slack for vendor sample payloads, review cycles and the pilot negotiation. The estimate in D6 (developer half-time for 7 months) stands.

## First four weeks

| Week | Work | Who |
|---|---|---|
| 1 | ARV-001 on your machine (restore, all gates green); fix whatever the first CI runs report; review and merge the AMAN hardening branch; run the manual trademark searches and decide on the name; add the Claude secret for the review workflow | Ahmad with Claude Code interactive |
| 2 | ARV-002 to ARV-006 through ralph-tui; first daily branch reviews | Developer, loop |
| 3 | ARV-007 to ARV-012 (authentication and permissions are the riskiest E0 stories; run `/grill-pbi` on ARV-010 first) | Developer, loop with interactive reviews |
| 4 | E1 topology and zone profiles; request Xovis sample payloads and data push documentation through the reseller | Developer, Ahmad on vendor contact |

## What Claude Code will not do

Hardware procurement and installation, vendor SDK and documentation access (Xovis, perception platforms), AODB and AMAN feed access agreements, on-site calibration and validation, security accreditation and the penetration test, pushes and releases, and approval of security exceptions. These stay on the plan in D6 and on the human side of every gate.

## Risks specific to the harness

| Risk | Mitigation |
|---|---|
| Agents weaken a gate to pass a story | Gate files and hooks need human approval to change; the security reviewer checks for it; CI runs the same gates |
| Context drift across 76 stories | Fresh session per story, short CLAUDE.md with skills loaded on demand, `backlog/progress.md` learnings, ADRs |
| Stale guidance (AMAN's CLAUDE.md already drifted on messaging) | `/adr` for decisions, docs updated per story, MCP servers for current framework and library facts |
| Third-party MCP servers see queries | Only public library questions go to Context7, Svelte and shadcn-svelte; never paste code or customer data into queries; disable them in `.claude/settings.json` for sensitive sessions |
| Unverifiable vendor details | Sensor stories require vendor sources or explicit "unverified" labels before certification |
| Cost and pacing of autonomous loops | `maxIterations` 20 per run, one story per iteration, daily human review |
| MassTransit v8 maintenance ends after 2026 | Bus stays behind `ISvcMessageBus` in Ariva.Infra; outbox, inbox and dead letters are Ariva code; decide v9 licence or Confluent swap before go-live (ADR-0018) |
| No branch protection on a private repository under GitHub Free | Hooks deny pushes to main and force pushes; move the repository to a Dalil organisation on GitHub Team to enforce rulesets and required checks |
| The repository was public for about an hour after creation | Treat that content as disclosed; nothing secret was in it, but AMAN weakness notes were; AMAN fixes are on the hardening branch |

## Decisions taken on 1 October 2026

1. The six health-probe allowlist entries: approved.
2. ADR-0018: MassTransit 8.5 with the Kafka Rider (AMAN parity), with Ariva-owned outbox, inbox and dead letters.
3. AMAN integration-auth weaknesses: fixed on a local AMAN branch, ready for review.
4. Trademark: pre-check run; result above.
5. Repository: GitHub (private), Actions, Dependabot, GitHub issues through `/sync-issues`.
6. Design: Aman.Web's design system.
7. Commits: automatic; agents push story branches and open pull requests.

## Still open

1. The product name, after the manual UAE, WIPO, EUIPO and USPTO searches.
2. Rotate the GitHub token shared in chat, and install the Claude GitHub App (direct pushes from Claude, review workflow).
3. GitHub plan: Team organisation (branch rules, required checks) and Code Security (CodeQL, secret scanning) for the private repository.
4. Release target for images: GHCR from Actions, Dalil Container Registry through Azure DevOps, or both.
