# ADR-0018: MassTransit 8 with the Kafka Rider behind ISvcMessageBus; raw Confluent consumer for stateful stream processing

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01 ("MassTransit 8"), replacing the earlier Proposed Confluent.Kafka option

## Context

Kafka is the event backbone (D5). AMAN runs MassTransit 8.4 with the Kafka Rider, and the product owner wants Ariva to stay on the same bus for team familiarity and shared patterns.

A read of AMAN's code on 2026-10-01 (HEAD b72d69c) found that the bus is wired but several protections described in AMAN's docs are not active:

| AMAN today | Effect |
|---|---|
| `IdempotencyFilter` and `MessageTrackingFilter` exist in `Aman.Infra/Messaging` but no `UseConsumeFilter` registers them | No duplicate suppression after a crash or rebalance |
| `OutboxMessage` rows are written only when a publish throws; the `OutboxProcessor` job's `[TickerFunction]` is commented out | Not a transactional outbox; failed publishes are never resent |
| No retry, no dead-letter handling (`Kafka:RetryCount` is bound but unused); v8 topic endpoints discard faulted messages | A consumer exception silently drops the message |
| Producers are registered without keys; topics created with one partition and replication factor 1 | No ordering per entity, no parallelism, no broker redundancy |
| One consumer group (`aman-group`) for every service | A deploy of one service rebalances all of them |

MassTransit facts that shape the design (v8 source and docs, checked 2026-10-01):

- v9 is commercial. v8 stays Apache 2.0; the official transition table ends v8 maintenance after 2026. Latest v8 is 8.5.11 (2026-09-30), which requires Confluent.Kafka 2.15.1 or later.
- The v8 bus outbox exists for EF Core and MongoDB only and does not cover rider producers (`ITopicProducer`); rider outbox support arrives in v9.1. There is no NHibernate outbox.
- v8 topic endpoints have no partition assigned or revoked callbacks, and no built-in error topic (`EnableErrorTopic` is v9).
- Offsets are checkpointed per partition after messages complete in order (at-least-once; a crash replays up to one checkpoint window). Messages are spread over `ConcurrentMessageLimit` lanes by key, so order holds per key; `ConcurrentDeliveryLimit` above 1 breaks ordering.

## Decision

1. **Integration and domain events** (queue intervals, nowcasts, alerts, topology changes, AMAN feed, AODB and immigration events) use **MassTransit 8.5.11 with the Kafka Rider** behind AMAN's `ISvcMessageBus` abstraction, hosted on an in-memory bus as in AMAN. Pinned: `MassTransit` 8.5.11, `MassTransit.Kafka` 8.5.11, `Confluent.Kafka` 2.15.1.
2. **Stateful stream processing** in Ariva.Api.Stream (per-zone track state, snapshots, replay) uses the **Confluent.Kafka consumer directly**, through an Ariva.Infra abstraction that exposes partitions, offsets and rebalance callbacks. The rider cannot tell a consumer when it loses a partition, and Stream keeps state per partition.
3. Ariva builds the protections AMAN lacks, as part of ARV-020:
   - **Transactional outbox on NHibernate:** domain events are written to `outbox_message` in the same transaction as the aggregate change. A relay (`BackgroundService`, single leader through a PostgreSQL advisory lock) claims rows with `FOR UPDATE SKIP LOCKED`, produces them in id order through keyed `ITopicProducer<string, T>`, and stops for a key on failure so order holds.
   - **Inbox (idempotency) filter:** `INSERT INTO processed_event(consumer, event_id) ON CONFLICT DO NOTHING` inside the consumer's NHibernate transaction; zero rows inserted means a duplicate, which is acknowledged and skipped. Unlike AMAN's Redis claim, a crash cannot mark an unprocessed event as done.
   - **Dead-letter filter:** outermost consume filter. After bounded in-process retries it produces the original key, body, headers, topic, partition, offset and error to `<topic>.dlq.v1`, then returns so the offset commits. Replay from the DLQ is an admin operation (`/replay` command, audited).
   - **Keys always set** (zone id, desk id, gate id, device id, site id); topics provisioned at startup from `KafkaTopics` with configured partitions and replication; **one consumer group per service and endpoint** (`ariva-<service>.<purpose>`).
   - Endpoint defaults: `AutoOffsetReset` earliest, `ConcurrentDeliveryLimit` 1, `ConcurrentMessageLimit` per endpoint (parallel across keys), `CheckpointInterval` 5 s, `CheckpointMessageCount` 500 (bounds replay), short exponential retry.
   - Telemetry: `AddSource("MassTransit")` and `AddMeter("MassTransit")`; MassTransit's bus health check is part of readiness.

## Consequences

- Parity with AMAN's bus, with the gaps above closed in Ariva and reported to AMAN (see `docs/security/cwe-controls.md`).
- **Licence and maintenance risk:** v8 maintenance ends after 2026. Mitigations: MassTransit stays behind `ISvcMessageBus` and inside Ariva.Infra (no MassTransit types in Ariva.Core, enforced by an architecture test); the outbox, inbox and DLQ are Ariva code, not MassTransit features, so a swap to the raw Confluent client or to a v9 licence is a single-project change. Decide before go-live (tracked in `docs/product/decisions.md`). Community forks (OpenTransit, PublicTransit) are watched but not relied on.
- Two Kafka consumption styles in one codebase; the kafka-streaming skill says which to use where.
- Confluent.Kafka moves from AMAN's 2.8.0 to 2.15.1; Testcontainers.Kafka integration tests cover the at-least-once, duplicate and DLQ paths.
- Not verified yet: filter order (dead-letter outside retry) on rider endpoints; ARV-020 proves it with a test harness.

## Alternatives considered

- Confluent.Kafka for everything (the earlier proposal). No licence exposure, but diverges from AMAN and reimplements serialisation, consumer hosting and telemetry. Remains the fallback if v8 support becomes a problem.
- MassTransit 9 with a commercial licence. Adds the rider outbox and error topics; cost and licence-key operations for every deployment. Revisit at go-live.
- Rebus. Not used ([ADR-0004](ADR-0004-kafka-for-facts-workflows-without-second-broker.md)).
