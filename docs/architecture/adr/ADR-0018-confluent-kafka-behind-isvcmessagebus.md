# ADR-0018: Confluent.Kafka behind ISvcMessageBus, with outbox and idempotency ported from AMAN

- Status: Proposed
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 (status Proposed)

## Context

Kafka stays as the event backbone (D5); AMAN now runs Kafka too. AMAN uses MassTransit 8.4 with the Kafka Rider: producers are registered for every event type carrying a message-owner attribute, consumers are discovered by subscribed queue, and an idempotency filter records processed event ids in FusionCache backed by Redis for 24 hours. MassTransit v9 is commercial, and v8 (Apache 2.0) patches wind down through 2026. Ariva's stream services also need partition-aware consumption: keyed state per partition, checkpoints, replay from committed offsets and rebalance handling (D5).

## Decision

Proposed: use the Confluent.Kafka client behind AMAN's `ISvcMessageBus` abstraction for publishing and consuming integration events, and port AMAN's transactional outbox and idempotency patterns. Partition-aware stream consumers in Ariva.Api.Stream use the Confluent consumer through an Ariva.Infra abstraction that exposes partitions, offsets and rebalance callbacks.

## Consequences

- No licence exposure from a commercial bus.
- Ariva owns plumbing a bus would provide: serialisation and schema versioning, retries, dead-letter handling, health checks and trace propagation.
- Diverges from AMAN's current bus implementation; the abstraction (`ISvcMessageBus`) and the outbox and idempotency patterns stay the same.

## Alternatives considered

- MassTransit 8.4 with the Kafka Rider (AMAN parity). Recorded option. Risks: v8 patches wind down through 2026 and v9 is commercial. Still viable if AMAN decides to stay on MassTransit and accepts the licence path.
- Rebus. Not used ([ADR-0004](ADR-0004-kafka-for-facts-workflows-without-second-broker.md)).

Open: confirm this ADR with the product owner (and revisit if AMAN changes its bus).
