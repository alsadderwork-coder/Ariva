---
name: kafka-streaming
description: Kafka usage in Ariva (Ariva's own Confluent producer behind ISvcMessageBus, MassTransit 8 Kafka Rider consumers, raw Confluent consumer for the Stream engine, topics, keys, outbox, inbox, dead letters, replay, live snapshots). Load for Ingest, Stream, Integration, Cronz consumers and any event publishing.
---
# Kafka in Ariva

## Which client where (ADR-0018)
- **Integration and domain events** (queue intervals, nowcasts, alerts, topology changes, AMAN feed, AODB and immigration events): consumed with MassTransit 8.5.11 and the Kafka Rider (topic endpoints on an in-memory bus, same shape as AMAN's `Aman.Di/Extensions/MassTransitServiceExtensions.cs` with the gaps below closed); produced through Ariva's own producer, one Confluent `IProducer<string, byte[]>` per host (`KafkaProduce`, ARV-167), which `ISvcMessageBus`, the outbox relay and `IDeadLetterSink` share; consumers are declared with `Consume<TMessage, TConsumer>`.
- **Stateful stream engine** (Ariva.Api.Stream: per-zone track state, snapshots, replay): raw Confluent.Kafka consumer through the Ariva.Infra partition-aware abstraction. The v8 rider has no partition assigned or revoked callbacks.
- MassTransit and Confluent types live only in Ariva.Infra and Ariva.Di. Ariva.Core sees `ISvcMessageBus` and event records; an architecture test enforces this.
- Pinned: MassTransit 8.5.11, MassTransit.Kafka 8.5.11, Confluent.Kafka 2.15.1. Never 9.x without a licence decision (v9 is commercial; v8 maintenance ends after 2026).

## Do not copy from AMAN
AMAN's `IdempotencyFilter` is never registered, its outbox is a fallback table whose replay job is commented out, faulted messages are discarded (no retry, no DLQ), producers have no keys, topics have one partition and replication 1, and every service shares `aman-group`. Ariva fixes all of these.

## Topics
`ariva.<context>.<event>.v1` from `KafkaTopics` constants only. Examples: `KafkaTopics.DeviceTrackSample` (`ariva.device.track-sample.v1`, key zone id), `KafkaTopics.FlowQueueInterval`, `KafkaTopics.AlertStateChanged`, `KafkaTopics.TopologyZoneProfileActivated`. An event class names its topic with `[KafkaTopic(KafkaTopics.X)]`; one event type per topic. AMAN produces `aman.feed.<contract>.v1`. `TopicProvisioner` creates missing topics from `TopicCatalog` (partitions, replication, retention or compaction) in the background when `Kafka:ProvisionTopics` is set (Api.Main); do not rely on `CreateIfMissing`. Dead letters: `KafkaTopics.DeadLetter(topic)`, `<topic>.dlq.v1` (AMAN topics get the `ariva.` prefix in front).

## Producing (ARV-167)
One producer per host, `KafkaProduce` (Ariva.Infra), registered by `AddArivaMessaging`, built at host start, flushed for at most 5 seconds at stop, disposed only with the host. No send outcome disposes or replaces it; only a fatal client error does, in place, at most once per `Kafka:Producer:RecreateSeconds` (30), sends failing at once in between; fatal errors and recreations (not the failed sends) are counted on the `Ariva.Kafka` meter. The outbox relay waits for the producer (`KafkaProduce.IsReady`), not for MassTransit's bus. Never register `r.AddProducer` on the rider and never build another producer: publish with `ISvcMessageBus` (keyed by the event's `GetPartitionKey()`), write domain events to `outbox_message` in the aggregate's NHibernate transaction (the relay, single leader by PostgreSQL advisory lock, `FOR UPDATE SKIP LOCKED`, `seq` order, stop per key on failure, sends them), and send dead letters with `IDeadLetterSink`. Settings come from `KafkaClientConfig.Producer`: `Acks.All`, idempotence, 1 MB, linger 0, 30 s message timeout, LZ4, delivery reports without the message, queue bounded (`QueueMaxKbytes` 32768, `QueueMaxMessages` 10,000; a full queue throws `Local_QueueFull` at once, which Ingest answers 503). A send takes no cancellation token into the client: the caller's token ends only its own wait, and every awaited send ends within `KafkaProduce.SendTimeout` (45 s). The wire format is the rider's (contract test in `KafkaPipelineTests`): UTF-8 key, the event's JSON, Ariva's four headers, `MT-Activity-Id` when traced, `MessageId`.

## Rider consumers (at-least-once)
Hosts declare consumers through the composition root; `AddArivaMessaging` registers each consumer with its group, the endpoint defaults and the Ariva consume pipe (`ConsumePipeline.Configure`), and `KafkaConsumerEndpoints` connects its topic endpoint after the bus has started, once the topic exists (a topic that appears later is connected at the next check, every `Kafka:Consumers:TopicWatchSeconds`, 10 s, without a restart; ARV-176):
```csharp
builder.Services.RegisterArivaServices(builder.Configuration, messaging => messaging
    .Consume<QueueInterval, QueueIntervalConsumer>(KafkaTopics.FlowQueueInterval, "queue-interval"));
// group ariva-<Kafka:ServiceName>.queue-interval; AutoOffsetReset Earliest; ConcurrentDeliveryLimit 1 (never raise: breaks
// ordering); ConcurrentMessageLimit 8 (parallel across keys); CheckpointInterval 5 s; CheckpointMessageCount 500.
```
- Pipe, from the outside in (proven by `ConsumePipelineTests` with MassTransit's probe): retry (MassTransit always puts it first), `DeadLetterFilter` (acts on the final attempt only), `InboxFilter` (claim + unit of work, fresh scope per attempt), consumer.
- `InboxFilter`: `INSERT INTO processed_event(consumer, event_id) ON CONFLICT DO NOTHING` in the consumer's transaction, then the consumer, then commit; a duplicate is acknowledged without running the consumer; a failure rolls the claim back. The consumer does not commit itself.
- `DeadLetterFilter`: sends key, body (base64), headers, topic, partition, offset, consumer and error to the dead-letter topic through `IDeadLetterSink`, then returns so the checkpoint advances. A refused dead letter is retried until accepted (v8 discards rethrown faults); if the endpoint stops first, the letter is kept as an `outbox_message` row (type `ariva.dead-letter`, `OutboxDeadLetterStore`, within 10 s) that the relay sends later, and only when that write fails too is the record left unacknowledged for redelivery (ARV-171). Each letter is counted once by outcome on `ariva.kafka.dead_letters` (sent, kept, failed; ARV-125a).
- Keep retries short (`Kafka:Consumers`): a retrying message blocks its key lane.

## Raw consumer (Stream engine only)
- `EnableAutoCommit = false`, `EnableAutoOffsetStore = false`; `StoreOffset(result)` after the handler; `Commit()` only right after `CheckpointAsync` made the state durable (every `Kafka:Consumers:CheckpointSeconds` and before a clean revoke). Never commit past state that was not persisted.
- Revoked: checkpoint, commit, release. Lost or after a handler failure: drop state, commit nothing (replay from the last checkpoint). Assigned: rebuild per-zone state from the latest snapshot plus replay. Declare the topic with `messaging.StreamFrom(topic)` (it must be a `KafkaTopics` constant); its unreadable records go to its dead-letter topic through the host's producer.
- `PartitionedConsumer<T>` (Ariva.Infra/Streaming) implements this with `IPartitionHandler<T>` (assigned, revoked, lost, handle); a record that is not valid JSON goes to the dead-letter topic and its offset is stored; a handler failure stops the loop so the record is replayed after restart.

## Always
Bound memory per partition and per zone (CWE-120); validate every message against its contract before use (CWE-501); topics never built from input (CWE-77). Telemetry: `AddSource("MassTransit")` (consume spans), `AddSource(KafkaProduce.ActivitySourceName)` (send spans, `Ariva.Kafka`, each ending at its message's delivery report), `AddMeter("MassTransit")` and `AddMeter(KafkaProducerMetrics.MeterName)` (`ariva.kafka.producer_fatal`).

## Replay and determinism
Raw canonical events are archived in `sensing_events`; replay re-runs the pure engine for a time range and profile version and must produce the same output hash. Golden test: seed 9303.

## Live
Stream workers write snapshots to Redis; Ariva.Api.Main pushes to SignalR groups per zone and checkpoint (Redis backplane, MessagePack). Hub group joins are authorised per permission and site.
