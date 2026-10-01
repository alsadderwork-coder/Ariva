---
name: kafka-streaming
description: Kafka usage in Ariva (MassTransit 8 Kafka Rider behind ISvcMessageBus, raw Confluent consumer for the Stream engine, topics, keys, outbox, inbox, dead letters, replay, live snapshots). Load for Ingest, Stream, Integration, Cronz consumers and any event publishing.
---
# Kafka in Ariva

## Which client where (ADR-0018)
- **Integration and domain events** (queue intervals, nowcasts, alerts, topology changes, AMAN feed, AODB and immigration events): MassTransit 8.5.11 with the Kafka Rider, hosted on an in-memory bus, behind `ISvcMessageBus`. Same shape as AMAN's `Aman.Di/Extensions/MassTransitServiceExtensions.cs`, with the gaps below closed.
- **Stateful stream engine** (Ariva.Api.Stream: per-zone track state, snapshots, replay): raw Confluent.Kafka consumer through the Ariva.Infra partition-aware abstraction. The v8 rider has no partition assigned or revoked callbacks.
- MassTransit and Confluent types live only in Ariva.Infra and Ariva.Di. Ariva.Core sees `ISvcMessageBus` and event records; an architecture test enforces this.
- Pinned: MassTransit 8.5.11, MassTransit.Kafka 8.5.11, Confluent.Kafka 2.15.1. Never 9.x without a licence decision (v9 is commercial; v8 maintenance ends after 2026).

## Do not copy from AMAN
AMAN's `IdempotencyFilter` is never registered, its outbox is a fallback table whose replay job is commented out, faulted messages are discarded (no retry, no DLQ), producers have no keys, topics have one partition and replication 1, and every service shares `aman-group`. Ariva fixes all of these.

## Topics
`ariva.<context>.<event>.v1` from `KafkaTopics` constants only. Examples: `ariva.device.track-sample.v1` (key zone id), `ariva.flow.queue-interval.v1`, `ariva.alerting.alert-raised.v1`, `ariva.topology.zone-profile-activated.v1`. AMAN produces `aman.feed.<contract>.v1`. Provision topics at startup from the constant list with configured partitions, replication and retention; do not rely on `CreateIfMissing`. Dead letters: `<topic>.dlq.v1`.

## Producers
Keyed always: `r.AddProducer<string, T>(topic, (ctx, p) => { p.EnableIdempotence = true; p.CompressionType = CompressionType.Lz4; })`, `Acks.All`, `MessageMaxBytes` 1 MB. Domain events never go to Kafka from inside a transaction: write `outbox_message` in the aggregate's NHibernate transaction; the relay (single leader by PostgreSQL advisory lock, `FOR UPDATE SKIP LOCKED`, id order, stop per key on failure) produces them.

## Rider consumers (at-least-once)
```csharp
k.TopicEndpoint<string, QueueInterval>(KafkaTopics.QueueInterval, "ariva-cronz.queue-interval", e =>
{
    e.AutoOffsetReset = AutoOffsetReset.Earliest;
    e.ConcurrentMessageLimit = 8;      // parallel across keys, ordered per key
    e.ConcurrentDeliveryLimit = 1;     // never raise: breaks ordering
    e.CheckpointInterval = TimeSpan.FromSeconds(5);
    e.CheckpointMessageCount = 500;    // bounds replay after a crash
    e.UseConsumeFilter(typeof(DeadLetterFilter<>), ctx);   // outermost
    e.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500)));
    e.UseConsumeFilter(typeof(InboxFilter<>), ctx);        // idempotency in the consumer's transaction
    e.ConfigureConsumer<QueueIntervalConsumer>(ctx);
});
```
- One consumer group per service and endpoint: `ariva-<service>.<purpose>`.
- `InboxFilter`: `INSERT INTO processed_event(consumer, event_id) ON CONFLICT DO NOTHING` in the same NHibernate transaction as the handler; zero rows means duplicate, skip.
- `DeadLetterFilter`: after retries, produce key, body, headers, topic, partition, offset and error to `<topic>.dlq.v1`, then return so the checkpoint advances. If that produce fails, keep retrying; v8 discards rethrown faults.
- Keep retries short: a retrying message blocks its key lane.

## Raw consumer (Stream engine only)
- `EnableAutoCommit = true`, `EnableAutoOffsetStore = false`; `StoreOffset(result)` only after the effect is durably written (stores offset + 1).
- Revoked handler: flush state, store offsets. Assigned handler: rebuild per-zone state from the latest snapshot plus replay.
- Poison messages to `<topic>.dlq.v1` after N attempts, then store the offset.

## Always
Bound memory per partition and per zone (CWE-120); validate every message against its contract before use (CWE-501); topics never built from input (CWE-77). Telemetry: `AddSource("MassTransit")` and `AddMeter("MassTransit")`.

## Replay and determinism
Raw canonical events are archived in `sensing_events`; replay re-runs the pure engine for a time range and profile version and must produce the same output hash. Golden test: seed 9303.

## Live
Stream workers write snapshots to Redis; Ariva.Api.Main pushes to SignalR groups per zone and checkpoint (Redis backplane, MessagePack). Hub group joins are authorised per permission and site.
