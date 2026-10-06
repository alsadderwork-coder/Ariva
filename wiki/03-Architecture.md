# Architecture

A summary for engineers and DevOps. The full, reviewed architecture is `../docs/architecture/overview.md`; decisions with their reasons are in the ADR index `../docs/architecture/adr/README.md`. This page lists what runs, where it listens, what it stores and what it publishes.

## Shape in one paragraph

Ariva is an event-driven .NET 10 system on Kafka and PostgreSQL 17 with TimescaleDB, deployed on premises per authority per airport. A device gateway (Ingest) normalises vendor payloads into canonical events on Kafka. A stream service (Stream) applies Ariva's versioned zone geometry, runs the queue engine and desk state machine, and writes time series to TimescaleDB. Main serves the REST API, the SignalR live hub and the read-only display endpoint. Cronz runs scheduled and delayed jobs with TickerQ. Integration holds the anti-corruption layers for AODBs, AMAN, notifications and outbound feeds. The web front end is a SvelteKit single page app served by nginx. The repository mirrors AMAN's layout and conventions (ADR-0016).

## Hosts

| Host | Path | Local port | Container port | Kubernetes service | Role |
|---|---|---|---|---|---|
| Ariva.Api.Main | `Platform/Backplane/Ariva.Api.Main` | 51001 | 8080 | `api-main-service` (80) | REST API for configuration, contracts, alerts, reports and users; SignalR hub; read-only display endpoint |
| Ariva.Api.Ingest | `Platform/Backplane/Ariva.Api.Ingest` | 51002 | 8080 | `api-ingest-service` (80) | Device gateway: sensor adapters, MQTT over TLS, clock checks, disk-backed buffer; one instance per terminal on the sensor VLAN (target placement) |
| Ariva.Api.Stream | `Platform/Backplane/Ariva.Api.Stream` | 51003 (health only) | 8080 | `api-stream-service` (80) | Zone geometry and crossings, queue engine, desk state, alert rule evaluation, arrival-wave alert; writes hypertables |
| Ariva.Api.Cronz | `Platform/Backplane/Ariva.Api.Cronz` | 51004 | 8080 | `api-cronz-service` (80) | TickerQ jobs: escalation timers, SLA evaluation, reports and evidence packs, staffing optimiser (v1), retention and health sweeps; TickerQ dashboard |
| Ariva.Api.Integration | `Platform/Backplane/Ariva.Api.Integration` | 51005 | 8080 | `api-integration-service` (80) | Integration API v1, AODB adapters (AIDX, ACRIS, SSIM, vendor), AMAN feed translator (border only), notification channels, outbound wait-times API and webhooks, border-to-airport feed |
| Ariva.Web | `Platform/Frontplane/Ariva.Web` | 51010 (vite dev), 51011 (vite preview) | 3000 (nginx) | `web-service` (80) | Dashboards, live floor plan, zone editor, kiosk display pages |
| Ariva.Simulation.Api | `Platform/Simulation/Ariva.Simulation.Api` | 51020 | 8080 | `simulation-service` (80) | Sensor, AODB and AMAN emulators; reference scenario seed 9303 at site DMO. Disabled in production values |
| Forecasting worker (Python) | To confirm | | | | v1: show-up and load-factor models, Monte Carlo waits, backtesting; talks to the rest of Ariva only through Kafka |

Every .NET host answers `/health/startup`, `/health/readiness` and `/health/liveness`; the web image answers `/healthz`. Ports 510xx are distinct from AMAN's 500xx so both can run side by side on a developer machine.

Supporting projects: Ariva.Utilities, Ariva.Core (domain, service interfaces, formulas as pure functions, persisted workflow state machines), Ariva.Resources (Arabic, English, Portuguese, Swahili strings), Ariva.Infra (NHibernate, Timescale script runner, Kafka behind `ISvcMessageBus`, outbox relay, FusionCache, adapters), Ariva.Di, Ariva.Api.Common (layered appsettings, middleware, security, health endpoints), Ariva.Business.Contracts (AMAN feed contracts V1), Ariva.UnitTests, Ariva.IntegrationTests, Ariva.ServiceDefaults (OpenTelemetry, the health check service and HttpClient resilience, shared by every host, ARV-066), Ariva.K8s (Helm and Helmfile), Ariva.Cicd (Azure DevOps YAML), Ariva.AppHost (.NET Aspire local orchestration, development and test only).

## Data stores

| Store | Version | Holds | System of record | Notes |
|---|---|---|---|---|
| PostgreSQL with TimescaleDB Community Edition | PostgreSQL 17 (16 or later is the floor); TimescaleDB 2.x (2.30.1 was current in September 2026) | Relational tables (configuration, contracts, alerts, flights, users, audit, outbox, checkpoints) and hypertables (track samples, zone events, queue intervals, desk intervals, forecast values, border lane KPIs, device health) | Yes | Relational schema through NHibernate with reviewed SQL in production; TimescaleDB objects only from versioned scripts `Ariva.Infra/Timescale/Scripts/NNNN_*.sql` recorded in `schema_version`. The runtime role cannot update or delete raw hypertables |
| Kafka (KRaft mode) | Version To confirm (align with AMAN's chart when shared) | Facts: device events, crossings, intervals, nowcasts, desk states, flights, forecasts, alerts, SLA events, AMAN feed | No (short retention; raw samples for recomputation live in TimescaleDB) | Three brokers, replication factor 3, minimum two in-sync replicas, `acks=all` for a standard site; one broker at a small site |
| Redis | Version To confirm | FusionCache second level, SignalR backplane, idempotency keys, TOTP replay guard (with a database fallback) | No | Key prefix `ariva:` so it can share AMAN's Redis |

Sizing assumption (D5): a busy terminal tracks about 3,000 people at peak; samples stored at 1 Hz at a third of peak are about 1,000 rows per second, 86 million rows and 9 GB a day uncompressed, 1 to 2 GB a day compressed; a 90-day window needs 80 to 160 GB; provision 200 GB. Ingest for 100 sensors at 30 people each and 5 Hz is about 15,000 messages and 1.5 MB per second. See [Deployment guide](04-Deployment-Guide.md) for sizing per site.


Stream outputs (ARV-034): per-minute queue rows (`queue_minute`), bin revisions (`queue_bin`), per-desk and per-gate minutes (`desk_minute`, `egate_minute`), the 15-minute continuous aggregate `queue_minute_15m`, and since ARV-113 the per-line minute counts `line_minute` (crossings in and out on each line of the zone, with the line's role and the source, `Ariva` for the queue engine's own counts) with their 15-minute aggregate `line_minute_15m`, and since ARV-114a the continuous health checks per bin `zone_health_bin` (conservation residual, track completion, occupancy against the zone's physical capacity; formulas F18) are TimescaleDB hypertables written by Ariva.Api.Stream once per checkpoint, together with the zones' snapshots and the worker's positions, so a restart rewrites the same rows.

Alert evaluation (ARV-038): Ariva.Api.Stream evaluates every enabled alert rule once a minute on the stored minutes (`queue_minute`, `queue_bin`, `zone_outage` and the device registry), raising and auto-resolving alerts (`alert`, one open per rule and target) and keeping each target's evaluator state (`alert_rule_state`); one evaluator at a time across replicas. The rule backtest (`POST api/v1/admin/alert-rules/backtest`) is the same evaluation over the same minutes. The tick also escalates alerts left unacknowledged (ARV-039). Flights (ARV-041): every flight feed enters through `ISvcFlightIntake` in Ariva.Api.Integration (legs, milestones and check-in counter allocations, one item at a time, applied by message time under a lock per leg; tables `flight_leg`, `flight_event`, `counter_allocation`), and each change is published as `FlightChanged` (`ariva.flight.flight-changed.v1`) through the outbox. The stale-feed alarm sweeps `feed_freshness` every minute. Alert emails (ARV-040) are written with the alert change (`email_message`, an outbox) and sent by Ariva.Api.Integration, the host with egress to the mail relay. People act on alerts through `api/v1/alerts`; every change, from either host, is announced on the Redis channel `live:alerts` and the live hub forwards it to the site's alert groups of the roles responsible for the alert.

Golden replay (ARV-036): the stream follows each zone's devices from the zone's own records (device health is keyed by zone and merged with the sensing topics), records device outages in `zone_outage`, and the Stream host archives health reports in `device_health_event`. `Ariva.Api.Stream --replay` replays the archive for a site, zones, range and profile version with hash-chained inputs and outputs, records the run in `replay_run` and writes a tamper-evident export; `--verify-replay` checks one.

Live push (ARV-035): Ariva.Api.Stream writes each zone's latest live row to Redis and announces it; Ariva.Api.Main's hub `/hubs/live` pushes it to the browsers that joined the zone. Connecting needs `LiveQueue.View` (Border shift supervisor, Terminal duty manager, Handler station manager, System administrator); joining a zone needs its site among the caller's sites. One Main replica relays at a time (a Redis lease) and the SignalR Redis backplane reaches the others.
## Kafka topics

Naming is `ariva.<context>.<event>.v1` (ADR-0019); AMAN feed topics are `aman.feed.<contract>.v1` and are produced by AMAN. Zone-keyed topics use the id of the owning queue zone, so a queue's overflow, service and staff zones share one partition. The full table with producers, consumers and retention is in `../docs/architecture/overview.md` section 5.

| Context | Topics | Key | Proposed retention |
|---|---|---|---|
| device | `track-sample`, `vendor-line-crossing`, `health`, `registry-changed` (compacted) | Zone id; device id for health and registry | 3 days |
| topology | `zone-profile-activated`, `desk-changed` (both compacted) | Site id, desk id | Compacted |
| flow | `zone-crossing` (internal), `queue-interval`, `nowcast` (compacted), `overflow-detected` | Zone id | 3, 30, compacted, 14 days |
| desk | `signal`, `state-changed` (compacted), `interval-closed` | Desk id | 3 days, compacted, 30 days |
| border | `service-rate-updated`, `egate-outcome-rate-updated`, `lane-demand-updated` | Lane id; flight id for lane demand | 14 days |
| flight | `flight-changed` | Canonical flight id | 14 days |
| forecast | `published`, `arrival-wave`, `staffing-recommendation-issued` | Run id, zone id, area id | 30 days |
| alert | `state-changed` | Alert id | 30 days |
| sla | `breach-detected`, `evaluation-finalised`, `dispute-changed` | Contract id | 30 days |
| feed | `border-lane-kpi` (border to airport) | Lane id | 14 days |
| AMAN feed | `aman.feed.desk-session-changed.v1`, `aman.feed.desk-interval-stats.v1`, `aman.feed.egate-interval-stats.v1`, `aman.feed.inbound-flight-lane-demand.v1` | AMAN desk, gate or flight key | AMAN owns |

Partition counts are To confirm; zone-keyed topics need at least as many partitions as Stream replicas (the production values allow up to 4 Stream replicas). Topics are provisioned from a fixed list in code, never from input (CWE-77 control).

Processing model: event time, not arrival time; bins close on a watermark with a lateness allowance; keyed state per partition held in memory and checkpointed to PostgreSQL; at-least-once delivery with idempotent upserts by (zone, bin start, revision) and event-id deduplication; a transactional outbox for anything written to PostgreSQL that must be published. Long-running workflows (alert escalation, notifications, report and evidence-pack generation) are persisted state machines driven by Kafka events with TickerQ for time-based steps; there is no second message broker (ADR-0004).

## Bounded contexts

Nine contexts, each a module with its own database schema inside the shared projects: Site Topology, Device Management, Flow Measurement, Desk Operations, Flight Demand, Forecasting and Planning, Service Levels and Contracts, Alerting, Border Integration; plus two supporting modules, Passenger Information and Tenancy and Access. Border Integration and Flight Demand are anti-corruption layers: the only code that knows AMAN's or an AODB's vocabulary. Context table with aggregates, events and first phase: `../docs/architecture/overview.md` section 4.

Invariants:

- Every measured number carries its zone profile version and a data-quality flag.
- Interval results are provisional until the queue that produced them has cleared; only final results feed SLA evaluation.
- No event with a track id, officer id or document-level field is published to a topic that leaves the border deployment.
- Zone profiles and contracts are immutable once published.

## Cross-cutting choices

| Concern | Choice | ADR |
|---|---|---|
| Persistence | NHibernate through `IStorageProvider` and `IUnitOfWork`; `SchemaUpdate` in development only; Npgsql binary COPY on the hot path | ADR-0017 |
| Messaging client | MassTransit 8.5 Kafka Rider behind `ISvcMessageBus` (AMAN parity) with Ariva outbox, inbox and dead-letter filters; raw Confluent consumer for the stateful Stream engine (Accepted) | ADR-0018 |
| Background jobs | TickerQ in Ariva.Api.Cronz | ADR-0020 |
| Live push | SignalR with Redis backplane and MessagePack | ADR-0021 |
| Observability | Serilog plus OpenTelemetry to SigNoz or Loki | ADR-0022 |
| Search | No OpenSearch in Phase 0 or the MVP | ADR-0023 |
| Front end | SvelteKit 2, Svelte 5, Tailwind 4, bits-ui, ECharts, svelte-i18n | ADR-0024 |
| Simulation | Ariva.Simulation.Api with the seeded reference day (seed 9303) | ADR-0025 |
| Authorisation | AMAN's `Permission` attribute, default-deny fallback policy, integration scopes, device authentication | `../docs/security/cwe-controls.md` |

## ADR index

| ADR | Decision | Status |
|---|---|---|
| 0001 | Ariva is a separate product and codebase from AMAN | Accepted |
| 0002 | One codebase with separately licensed, separately deployable modules | Accepted |
| 0003 | Ingest tracks and apply Ariva's own versioned zone geometry centrally | Accepted |
| 0004 | Kafka for facts; workflows as persisted state machines (Rebus dropped) | Accepted, amended 2026-10-01 |
| 0005 | Stream processing in .NET consumer services with keyed state | Accepted |
| 0006 | PostgreSQL with TimescaleDB Community Edition | Accepted |
| 0007 | Realised wait attributed to the entry interval, provisional until final | Accepted |
| 0008 | Nowcast from queue length and staffed-desk throughput | Accepted |
| 0009 | Backlog recursion with Monte Carlo for waits and staffing | Accepted |
| 0010 | Aggregate-only AMAN contracts; officer analytics stay in AMAN | Accepted |
| 0011 | Continuity by overlapping coverage; no re-identification | Accepted |
| 0012 | One deployment per authority per airport, one-way border-to-airport feed | Accepted |
| 0013 | Python worker for model training and batch forecasts | Accepted |
| 0014 | Canonical flight model with AIDX and ACRIS adapters, mocks and a replay harness | Accepted |
| 0015 | Product and code name Ariva | Accepted |
| 0016 | Repository layout mirrors AMAN and inherits its conventions | Accepted |
| 0017 | NHibernate, dev-only SchemaUpdate, versioned SQL for TimescaleDB, binary COPY | Accepted |
| 0018 | MassTransit 8 Kafka Rider behind ISvcMessageBus | Accepted |
| 0019 | Kafka topic naming and keys | Accepted |
| 0020 | Background jobs with TickerQ | Accepted |
| 0021 | Live push with SignalR, Redis backplane and MessagePack | Accepted |
| 0022 | Observability with Serilog and OpenTelemetry to SigNoz or Loki | Accepted |
| 0023 | No OpenSearch in Phase 0 or the MVP | Accepted |
| 0024 | Web front end stack | Accepted |
| 0025 | Simulation host and reference scenario | Accepted |

Open architecture questions (from `../docs/product/decisions.md`): MassTransit v8 end of maintenance: v9 licence or Confluent swap before go-live (ADR-0018), Kafka deployment (Strimzi or AMAN's chart), partition counts and retention values, border-to-airport feed transport and fields, queue engine parameters, the forecasting worker's repository location.
