# ADR-0005: Stream processing in .NET consumer services with keyed state

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 5

## Context

The volume is modest (see ADR-0003 sizing) and the team is two .NET developers. JVM stream engines would add a runtime and operational skills the team does not have.

## Decision

Stream processing runs as plain .NET consumer services in Ariva.Api.Stream: keyed state per partition held in memory and checkpointed to PostgreSQL, event-time windows that close on a watermark with a lateness allowance, late events reopening bins as new revisions, at-least-once delivery with idempotent upserts by (zone, bin start, revision).

## Consequences

- No built-in windowing or exactly-once semantics. Ariva implements watermarks, revisions, checkpoints and idempotent writes itself, and tests them (replay tests, rebalance tests).
- One language and runtime across the backend.

## Alternatives considered

- Kafka Streams (JVM only).
- Apache Flink (JVM and a heavy operational footprint).
- ksqlDB (restrictive licence and another runtime).
All would work; none fits a .NET team of two.
