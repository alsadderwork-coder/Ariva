# Ariva decision log

Product and architecture decisions in date order. Architecture decisions with consequences and alternatives are in [../architecture/adr/README.md](../architecture/adr/README.md). Numbers such as "decision 8" refer to the numbered scope decisions recorded in D1 to D3 and cited by D4 to D6. A later entry overrides an earlier one where they conflict; superseded entries are marked.

## September 2026 (D1 to D3 and the product owner; exact dates not recorded in the sources)

- Build the airport queue management system as a Dalil Tech product, with no current client. AUH is a likely first target; AMAN is not the immigration system at AUH.
- Separate product and codebase from AMAN, integrating through new aggregate-only contracts; hostable in AMAN's cluster or a separate one (decision 15). ADR-0001.
- Two separately sellable modules: the Border module for border authorities and the Airport Operations module for airport operators (decision 1). ADR-0002.
- Penalties are in scope: a handler SLA and penalty engine in v1.
- Officer-level data for border supervisors only (decision 8), and aggregate-only contracts from AMAN (decision 9). The conflict is resolved in D5: officer-level analytics stay in AMAN and never cross into Ariva. ADR-0010.
- Multi-sensor support is treated like e-gates: vendor-agnostic adapters, all ceiling heights (decisions 13 and 14). Hardware is bought on Claude's recommendation; a local integration partner installs and maintains.
- On-prem first. Separate border and airport deployments, with a one-way aggregate feed from border to airport. ADR-0012.
- AODB feeds (SITA, Amadeus and others) are mocked during development rather than waiting for real feed access (decision 11). ADR-0014.
- The developer works roughly half-time on the product until a pilot contract is signed (decision 17).
- Delivery team assumption: one developer using Claude Code, with the architect and product owner part-time.
- The first pilot is the Border module at one AMAN arrivals hall.
- AUH is approached as a component inside SITA's platform.

## 2026-09-28: D4 How It Works (approved)

- Sensors: overhead 3D stereo vision for ceilings from 2 to about 20 m; LiDAR above that, outdoors, in poor light, or where cameras need legal authorisation. Certify one product family of each before the pilot.
- Wi-Fi or BLE probing and CCTV analytics are not used for queue KPIs (CCTV only as a coarse overflow fallback).
- Continuity by overlapping coverage, never by re-identification. Track ids rotate at zone exit and never outlive the operating day. ADR-0011.
- Two wait numbers: realised wait (attributed to the entry interval, final only once the queue clears, used for reports and penalties) and nowcast (queue length and staffed-desk throughput, used for screens and alerts). ADR-0007, ADR-0008.
- Queue model: backlog recursion with Monte Carlo, not Erlang formulas. ADR-0009.
- Desk state is a state machine (Closed, Idle, Serving, Paused, plus Unknown) fed by signals in strict precedence; a passenger walking up is not a signal.
- Passenger screens show the nowcast in 5-minute bands with hysteresis, never a realised number.
- Validation protocol per site (manual counts, timed tracers, observer logs, at least five operating days including two peaks) and proposed acceptance targets, to agree contractually.

## 2026-09-28: D5 Technical Architecture (approved)

- .NET event-driven system on Kafka and PostgreSQL with TimescaleDB, deployed on-prem per authority per airport.
- Ariva ingests tracks and owns versioned zone geometry; vendor crossings are a fallback. ADR-0003.
- Nine bounded contexts; Border Integration and Flight Demand are anti-corruption layers.
- Stream processing in plain .NET consumer services with keyed state. ADR-0005.
- PostgreSQL with TimescaleDB Community Edition (licence checked for self-managed use); plain partitioning as fallback. ADR-0006.
- Four aggregate-only AMAN contracts: DeskSessionChanged, DeskIntervalStats, EGateIntervalStats, InboundFlightLaneDemand, with timestamps coarsened to the interval. ADR-0010.
- Python worker for model training and batch forecasts (v1); staffing optimiser in .NET. ADR-0013.
- Canonical flight model with AIDX and ACRIS adapters, mocks and a replay harness. ADR-0014.
- Signed, offline-verifiable licence file per deployment for module licensing.
- Hosted SaaS by Dalil is out of scope for now.
- Rebus for commands and sagas. Superseded on 2026-10-01 (Rebus not used).
- EF Core for relational schemas. Superseded on 2026-10-01 (NHibernate).
- Observability on AMAN's Prometheus, Grafana and Loki. Superseded on 2026-10-01 (Serilog and OpenTelemetry to SigNoz or Loki).
- OpenSearch optional for event and audit search. Narrowed on 2026-10-01 (not used in Phase 0 or the MVP).
- Unprefixed topic names (for example `tracks.samples.v1`). Superseded on 2026-10-01 (`ariva.<context>.<event>.v1`).

## 2026-09-28: D6 Roadmap and Delivery Estimate (approved)

- MVP: the Border module at one AMAN arrivals hall, validated against manual counts (23 to 38 developer-weeks with Claude Code).
- Four phases and three gates: Phase 0 demo core, gate pilot contract signed, Phase 1 pilot MVP, gate pilot accepted, v1 first sale, gate first commercial sale, v2 scale.
- Phase 0 builds only what needs no site, on simulated and lab data.
- First hire at the contract gate is a field engineer, before a second developer; the second developer joins at pilot acceptance.
- Pilot acceptance criteria as proposed in D6 (see the roadmap).
- Keep ADRs, domain definitions and a project guide for Claude Code in the repository from the first sprint.

## 2026-10-01: repository setup

- Product and code name: Ariva. ADR-0015.
- Repository on GitHub (`github.com/alsadderwork-coder/Ariva`, private; local clone `D:\DevOps\Ariva`), decided 2026-10-01, mirroring AMAN's layout: Backplane hosts Ariva.Api.Main (51001), Ariva.Api.Ingest (51002), Ariva.Api.Stream (51003), Ariva.Api.Cronz (51004), Ariva.Api.Integration (51005); Ariva.Business.Contracts; Ariva.Web (51010); Ariva.Simulation.Api (51020); Ariva.K8s (Helm and Helmfile); Ariva.Cicd (Azure DevOps YAML). ADR-0016.
- AMAN code conventions are inherited (Onion, `IStorageProvider`, `IUnitOfWork`, `SvcBase`, `Result<T>` and `Fx.Specification` from FluentX, AdminArea controllers, `Permission` attribute, FusionCache with Redis, Serilog, xUnit v3, Moq, FluentAssertions, Bogus). ADR-0016.
- ORM: NHibernate via `IStorageProvider`. `SchemaUpdate` in development only; production schema from reviewed SQL. TimescaleDB objects only from versioned scripts in `Ariva.Infra/Timescale/Scripts/NNNN_*.sql` with a `schema_version` table. Hot-path time-series writes use Npgsql binary COPY. ADR-0017.
- Kafka stays. Bus: MassTransit 8.5 with the Kafka Rider behind `ISvcMessageBus` (AMAN parity), decided 2026-10-01. Ariva adds the NHibernate outbox, inbox and dead-letter filters that AMAN's code lacks; the stateful Stream engine uses the raw Confluent consumer. ADR-0018.
- Rebus is not used. Long-running workflows are persisted state machines in Ariva.Core driven by Kafka events, with TickerQ jobs for time-based steps. ADR-0004 (amended).
- Topic naming `ariva.<context>.<event>.v1`, sensor events keyed by zone id; AMAN feed topics `aman.feed.<contract>.v1` produced by AMAN. ADR-0019.
- Background jobs: TickerQ in Ariva.Api.Cronz. ADR-0020.
- Live push: SignalR with Redis backplane and MessagePack. ADR-0021.
- Observability: Serilog plus OpenTelemetry to SigNoz or Loki. ADR-0022.
- OpenSearch is not used in Phase 0 or the MVP. ADR-0023.
- Front end: SvelteKit 2, Svelte 5, Tailwind 4, bits-ui, ECharts, svelte-i18n. ADR-0024.
- Ariva.Simulation.Api with sensor, AODB and AMAN emulators; reference scenario is the prototype's seeded day (seed 9303; scripted events at 18:05, 18:20 to 18:30 and 19:10). AODB mocks cover AIDX 22.1, ACRIS and SSIM import. ADR-0025.
- AMAN feed contracts V1 live in `Ariva.Business.Contracts`: DeskSessionChanged, DeskIntervalStats, EGateIntervalStats, InboundFlightLaneDemand.

- Alert email (ARV-040), decided 2026-10-03: a transactional outbox (`email_message`) written by the host that changes the alert and sent by Ariva.Api.Integration, the host with egress to the site's mail relay (wiki 05, flow 15), through MailKit; at least once; plain text from templates in Ariva.Resources; one recipient per message; limits per address per hour and per minute over all replicas. smtp4dev in development (compose) and in E2E as a pinned local .NET tool, so CI pulls no image for it.

- Flights (ARV-041), decided 2026-10-03: one flight model for every feed, keyed by site and the feed's flight key, applied by message time per field (schedule and each milestone), so ordering never matters; the stale-feed alarm is a system alarm (state in `feed_freshness`, a warning in the log, `Ariva.Flights` metrics for the monitoring stack, runbook section 3), judged per site against the flights due, not a user alert rule metric: alert rules watch zones, and a feed is not a zone.

## Open decisions

| Topic | Question | Where |
|---|---|---|
| Funding | Fund the developer full-time before the pilot contract (the biggest lever on the pilot date) | D6; roadmap |
| MassTransit v8 end of maintenance | Before go-live: buy a v9 licence (rider outbox, error topics) or swap the `ISvcMessageBus` implementation to raw Confluent.Kafka. v8 official maintenance ends after 2026 | ADR-0018 |
| AMAN contracts | Reconcile skeleton records with D5 content (cycle time, documents, lane category on sessions, boarded total, interval length, reject categories) | data-boundary.md |
| SLA module | Which module licenses the SLA and penalty engine | overview.md |
| Forecasting worker | Repository location of the Python worker | ADR-0013 |
| Kafka deployment | Strimzi (D5) or AMAN's Kafka chart when sharing AMAN's cluster; partition counts; retention values | overview.md |
| Border-to-airport feed | Transport and exact field list | overview.md, data-boundary.md |
| Queue engine parameters | T_censor, T_stale, nowcast blend weight and window, debounce window, staleness thresholds | formulas.md |
| Privacy | UAE PDPL, Angola, Tanzania and GDPR applicability; Angola authorisation for stereo sensors | data-boundary.md |
| Name | Trademark and domain availability for "Ariva" | ADR-0015 |
| Disabling alert rules | Deleting a rule needs step-up MFA (the PRD's critical action, ARV-037); disabling one with `PUT` (or raising its threshold until it never fires) has the same effect and needs none, only the audit trail. Confirm, or make disabling and edits that weaken a rule critical too | wiki 11 section 8 |
| Profile email changes | Changing a user's email redirects that user's alert emails; it is audited but needs no second factor (it is not one of the PRD's critical actions). Decide whether it should be | ARV-040, critical-actions.json |
| Staff addresses in sent emails | `email_message` keeps the recipient address with each alert email; set a retention period | wiki 14 |
