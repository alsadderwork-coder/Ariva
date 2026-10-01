---
name: kafka-streaming
description: Kafka usage in Ariva (Confluent.Kafka behind ISvcMessageBus, topics, keys, at-least-once consumers, outbox, dead letters, replay, live snapshots). Load for Ingest, Stream, Integration consumers and any event publishing.
---
# Kafka in Ariva

## Library and abstraction
Confluent.Kafka behind `ISvcMessageBus` (ADR-0018, Proposed: MassTransit 8 open-source patches wind down through 2026 and v9 is commercial). Keep the abstraction AMAN-shaped so a MassTransit implementation could be swapped in.

## Topics
`ariva.<context>.<event>.v1` from `KafkaTopics` constants only. Examples: `ariva.sensing.events.v1` (key zone id), `ariva.queue.minute.v1`, `ariva.alerting.alert-raised.v1`, `ariva.topology.zone-profile-published.v1`. AMAN produces `aman.feed.<contract>.v1`. Provision topics at startup from the constant list with configured partitions and retention (values "to confirm" in docs).

## Producers
`Acks.All`, `EnableIdempotence = true`, bounded `MessageMaxBytes` (1 MB), compression, key always set. Domain events go through the outbox table and a relay, never directly from a transaction.

## Consumers (at-least-once)
- `EnableAutoCommit = true`, `EnableAutoOffsetStore = false`; call `StoreOffset(result)` only after the effect is durably written (Confluent.Kafka docs: StoreOffset stores offset + 1 for the next commit). Alternatively commit manually after the batch.
- Idempotent handlers keyed by event id or `SourceEventId`; dedupe table or unique constraints.
- Partition revocation: flush state and store offsets in the revoked handler; rebuild per-zone state on assignment from the latest snapshot plus replay.
- Poison messages: after N attempts publish to `<topic>.dlq.v1` with the error, then store the offset.
- Bound memory per partition and per zone (CWE-120); validate every message against its contract before use (CWE-501).

## Replay and determinism
Raw canonical events are archived in `sensing_events`; replay re-runs the pure engine for a time range and profile version and must produce the same output hash. Golden test: seed 9303.

## Live
Stream workers write snapshots to Redis; Ariva.Api.Main pushes to SignalR groups per zone and checkpoint (Redis backplane, MessagePack). Hub group joins are authorised per permission and site.
