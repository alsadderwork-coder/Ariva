# Architecture decision records

ADR-0001 to ADR-0014 are the key architecture decisions of D5 (Technical Architecture, 2026-09-28), in D5's order. ADR-0015 onward record decisions made on 2026-10-01 that D5 does not cover. Where a later decision changed a D5 decision, the ADR says so in its status and source.

Format per ADR: title, Status, Date, Source, Context, Decision, Consequences, Alternatives considered. A new ADR takes the next free number; an accepted ADR is not rewritten, it is amended or superseded by a new one.

| ADR | Title | Status | Date | Source |
|---|---|---|---|---|
| [ADR-0001](ADR-0001-separate-product-and-codebase.md) | Ariva is a separate product and codebase from AMAN | Accepted | 2026-09-28 | D5 key decision 1 (restates a product owner decision) |
| [ADR-0002](ADR-0002-one-codebase-separately-licensed-modules.md) | One codebase with separately licensed, separately deployable modules | Accepted | 2026-09-28 | D5 key decision 2; product decision 1 (modules sold separately) |
| [ADR-0003](ADR-0003-ingest-tracks-central-zone-geometry.md) | Ingest tracks and apply Ariva's own versioned zone geometry centrally | Accepted | 2026-09-28 | D5 key decision 3 |
| [ADR-0004](ADR-0004-kafka-for-facts-workflows-without-second-broker.md) | Kafka for facts; workflows as persisted state machines (Rebus dropped) | Accepted (amended 2026-10-01) | 2026-09-28, amended 2026-10-01 | D5 key decision 4, amended by the 2026-10-01 decision that Rebus is not used |
| [ADR-0005](ADR-0005-dotnet-stream-processing.md) | Stream processing in .NET consumer services with keyed state | Accepted | 2026-09-28 | D5 key decision 5 |
| [ADR-0006](ADR-0006-postgresql-timescaledb-community.md) | PostgreSQL with TimescaleDB Community Edition | Accepted | 2026-09-28 | D5 key decision 6 and the time-series storage section |
| [ADR-0007](ADR-0007-realised-wait-entry-interval-attribution.md) | Realised wait attributed to the entry interval, provisional until final | Accepted | 2026-09-28 | D5 key decision 7; D4 snake-queue wait time |
| [ADR-0008](ADR-0008-nowcast-from-queue-length-and-throughput.md) | Nowcast from queue length and staffed-desk throughput | Accepted | 2026-09-28 | D5 key decision 8; D4 nowcast |
| [ADR-0009](ADR-0009-backlog-recursion-monte-carlo.md) | Backlog recursion with Monte Carlo for waits and staffing | Accepted | 2026-09-28 | D5 key decision 9; D4 forecasting |
| [ADR-0010](ADR-0010-aggregate-only-aman-contracts.md) | Aggregate-only AMAN contracts; officer analytics stay in AMAN | Accepted | 2026-09-28 | D5 key decision 10; product decisions 8 and 9 |
| [ADR-0011](ADR-0011-no-re-identification.md) | Continuity by overlapping coverage; no re-identification | Accepted | 2026-09-28 | D5 key decision 11; D4 tracking |
| [ADR-0012](ADR-0012-deployment-per-authority-one-way-feed.md) | One deployment per authority per airport, with a one-way border-to-airport feed | Accepted | 2026-09-28 | D5 key decision 12; product decision 15 |
| [ADR-0013](ADR-0013-python-forecasting-worker.md) | Python worker for model training and batch forecasts | Accepted | 2026-09-28 | D5 key decision 13; D4 model quality |
| [ADR-0014](ADR-0014-canonical-flight-model-and-mocks.md) | Canonical flight model with AIDX and ACRIS adapters, mocks and a replay harness | Accepted | 2026-09-28 | D5 key decision 14; product decision 11 (mock AODB feeds during development) |
| [ADR-0015](ADR-0015-product-name-ariva.md) | Product and code name Ariva | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 |
| [ADR-0016](ADR-0016-repository-mirrors-aman.md) | Repository layout mirrors AMAN and inherits AMAN's code conventions | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 |
| [ADR-0017](ADR-0017-nhibernate-and-timescale-sql-scripts.md) | NHibernate via IStorageProvider; dev-only SchemaUpdate; versioned SQL for TimescaleDB; binary COPY on the hot path | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 (overrides D5's EF Core statement) |
| [ADR-0018](ADR-0018-masstransit-kafka-rider-behind-isvcmessagebus.md) | MassTransit 8 Kafka Rider behind ISvcMessageBus; raw Confluent consumer for stateful streams | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 (status Proposed) |
| [ADR-0019](ADR-0019-kafka-topic-naming.md) | Kafka topic naming and keys | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 (replaces D5's topic names) |
| [ADR-0020](ADR-0020-tickerq-background-jobs.md) | Background jobs with TickerQ in Ariva.Api.Cronz | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 (AMAN parity) |
| [ADR-0021](ADR-0021-signalr-live-push.md) | Live push with SignalR, Redis backplane and MessagePack | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 (AMAN parity); D5 read API and SignalR hub |
| [ADR-0022](ADR-0022-observability-serilog-opentelemetry.md) | Observability with Serilog and OpenTelemetry to SigNoz or Loki | Accepted | 2026-10-01 | Product owner decision, 2026-10-01 (AMAN parity; replaces D5's Prometheus, Grafana and Loki reuse) |
| [ADR-0023](ADR-0023-no-opensearch-in-phase0-and-mvp.md) | No OpenSearch in Phase 0 or the MVP | Accepted | 2026-10-01 | Product owner decision, 2026-10-01; D5 listed OpenSearch as optional for event and audit search |
| [ADR-0024](ADR-0024-web-frontend-stack.md) | Web front end: SvelteKit 2, Svelte 5, Tailwind 4, bits-ui, ECharts, svelte-i18n | Accepted | 2026-10-01 | Product owner decision, 2026-10-01; D5 asked to use the same framework as Aman.Web |
| [ADR-0025](ADR-0025-simulation-host-and-reference-scenario.md) | Simulation host and reference scenario | Accepted | 2026-10-01 | Product owner decision, 2026-10-01; D6 Phase 0; product decision 11 |
