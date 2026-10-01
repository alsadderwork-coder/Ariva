# ADR-0019: Kafka topic naming and keys

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 (replaces D5's topic names)

## Context

D5 used unprefixed names such as `tracks.samples.v1` and keyed track samples by zone group. In a border deployment Ariva may share AMAN's Kafka cluster, which needs a dedicated prefix and ACLs.

## Decision

- Ariva topics are `ariva.<context>.<event>.v1`, lowercase, with kebab-case event names. Context slugs: `device`, `topology`, `flow`, `desk`, `flight`, `forecast`, `alert`, `sla`, `border`, `feed`.
- Sensor events are keyed by zone id: the id of the queue zone that owns the process, so overflow, service and staff zones attached to it share the key and its partition.
- AMAN feed topics are `aman.feed.<contract>.v1` and are produced by AMAN.
- A breaking schema change gets a new version suffix; the old and new versions are published in parallel during migration (Proposed).
The full topic list is in [../overview.md](../overview.md#5-messaging-and-kafka-topics).

## Consequences

- Prefix ACLs (`ariva.`) make sharing AMAN's cluster safe.
- Ordering holds per zone, per desk and per flight where it matters.

## Alternatives considered

- D5's unprefixed names. Rejected: collide in a shared cluster.
- Keying by zone group (D5). Replaced by the owning queue zone id, which serves the same purpose.
