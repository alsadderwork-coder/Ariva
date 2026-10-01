# ADR-0006: PostgreSQL with TimescaleDB Community Edition

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 6 and the time-series storage section

## Context

Ariva needs time-bucketed queries, compression, rolling aggregates and retention on track samples and interval results, plus transactional joins to configuration and contracts. D5 checked the licence on 2026-09-28: the Apache-2 core is free to run and resell; Community Edition (Tiger Data licence) may be installed and run free on self-managed on-prem or cloud infrastructure but may not be offered as a database service. Compression, continuous aggregates and hyperfunctions are in Community Edition. TimescaleDB 2.30.1 was current (September 2026). PostgreSQL 15 support ended June 2026.

## Decision

PostgreSQL 17 with TimescaleDB Community Edition, PostgreSQL 16 or later as the floor. Plain PostgreSQL native partitioning with in-house rollups is the documented fallback and escape hatch if licensing changes. Schema handling is in [ADR-0017](ADR-0017-nhibernate-and-timescale-sql-scripts.md).

## Consequences

- One database engine the team already runs.
- A licence dependency to watch. If Dalil ever hosts Ariva as SaaS, confirm with counsel that a product whose customers never touch the database is outside the "database service" restriction (To confirm if SaaS is ever added).
- The extension must exist on every restore target and in the HA image.

## Alternatives considered

- PostgreSQL native partitioning and own rollups: fallback.
- ClickHouse: excellent analytics, but a second engine with weak transactional joins. Rejected for team fit.
- OpenSearch: not a system of record for exact, revisable bins. See [ADR-0023](ADR-0023-no-opensearch-in-phase0-and-mvp.md).
- InfluxDB or QuestDB: new engine and query model. Rejected.
