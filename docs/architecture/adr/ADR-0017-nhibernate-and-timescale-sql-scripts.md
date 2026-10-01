# ADR-0017: NHibernate via IStorageProvider; dev-only SchemaUpdate; versioned SQL for TimescaleDB; binary COPY on the hot path

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 (overrides D5's EF Core statement)

## Context

D5 proposed EF Core for relational schemas and Npgsql with hand-written SQL for hypertables. AMAN uses NHibernate behind `IStorageProvider`, which also exposes `UpdateDatabaseSchemaAndExecute()` and `UpdateDatabaseSchema()`. TimescaleDB objects (hypertables, continuous aggregates, compression and retention policies) cannot be expressed in NHibernate mappings. Track samples arrive at about 1,000 rows per second per busy terminal.

## Decision

- Relational data uses NHibernate through `IStorageProvider` and `IUnitOfWork`, with services on `SvcBase` (AMAN pattern).
- NHibernate `SchemaUpdate` is allowed only in development. Production schema changes come from reviewed SQL: generated in development with `UpdateDatabaseSchema()` (script only), reviewed, committed, and applied by the script runner. Folder for relational scripts: Proposed `Ariva.Infra/Database/Scripts`, To confirm.
- TimescaleDB objects are created only by versioned SQL scripts in `Ariva.Infra/Timescale/Scripts/NNNN_*.sql`, applied in numeric order by a script runner that records version, file name, checksum and applied time in a `schema_version` table. An applied script is never edited; a checksum mismatch stops startup. Some TimescaleDB statements cannot run inside a transaction block (for example creating a continuous aggregate with data), so the runner supports a per-script no-transaction marker (Proposed).
- Hypertables are excluded from NHibernate schema handling. Hot-path time-series writes (track_samples, zone_events, device_health, forecast_values) bypass NHibernate and use Npgsql binary COPY. Interval results use Npgsql upserts keyed by (zone, bin start, revision). Time-bucket reads use hand-written SQL.
- Organisation-level tenancy uses NHibernate filters (replacing D5's EF Core global filters), with PostgreSQL row-level security on contract, SLA and alert tables as a second line.

## Consequences

- Parity with AMAN's persistence code and skills.
- Two write paths to the same database, each with its own tests.
- Schema discipline is manual: every production change is a reviewed script.

## Alternatives considered

- EF Core with migrations (D5's proposal). Rejected by the product owner for AMAN parity.
- `SchemaUpdate` in production. Rejected: unreviewed DDL on a system of record.
- Writing track samples through NHibernate. Rejected: too slow for the hot path.
