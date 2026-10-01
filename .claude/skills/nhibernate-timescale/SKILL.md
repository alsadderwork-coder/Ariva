---
name: nhibernate-timescale
description: How Ariva persists data: NHibernate through IStorageProvider for configuration aggregates, versioned SQL scripts for schema and TimescaleDB objects, Npgsql binary COPY for time series. Load before touching persistence.
---
# Persistence in Ariva

## NHibernate (configuration and workflow aggregates)
- Port AMAN's `IStorageProvider`, `IUnitOfWork`, `NHibernateStorageProvider`, convention mapper and rules (../Aman/Platform/Backplane/Aman.Infra/NHibernate, read-only). NHibernate only; Ariva does not carry AMAN's EF Core switch.
- Mapping by code conventions; explicit rules for value objects (polygons stored as JSON), enums as strings, `Guid` comb ids.
- Queries: LINQ through `Query<T>()` / `QueryAsNoTracking<T>()`; batch count and page with `ToFutureValue` and `ToFuture`; never `CreateQuery` or `CreateSQLQuery` with interpolated strings.
- Raw SQL through `ExecuteSqlAsync(sql, parameters)` only.
- `SchemaUpdate`: only when `Database:AllowSchemaUpdate` is true (vm-local). Production uses scripts.

## Scripts (production schema and TimescaleDB)
- `Ariva.Infra/Timescale/Scripts/NNNN_description.sql`, applied in order by the script runner, recorded in `schema_version` with a checksum; a changed checksum of an applied script fails startup.
- Hypertables: `sensing_events` (raw canonical events for replay), `queue_minute`, `desk_minute`, `egate_minute`, `wait_samples`; continuous aggregates for 15-minute bins and hourly reports; retention and compression policies per table (values in docs/architecture/overview.md; mark unknown values "to confirm").
- Separate database roles: migration role (DDL) used by the runner job; runtime role without DDL (CWE-269).
- Fallback if TimescaleDB is unavailable: native declarative partitioning by day (ADR on storage); keep queries portable where cheap.

## Hot path writes
- Npgsql binary COPY (`BeginBinaryImport`) with typed `Write` calls per column; batch by size and time; never build COPY text from strings.
- Idempotency: unique keys on (zone_id, minute, profile_version) or event id; upserts with `ON CONFLICT` in parameterised SQL.

## Checks
Integration tests run the real script runner against a Testcontainers TimescaleDB image and verify hypertables, policies and a COPY round-trip.
