# ADR-0004: Kafka for facts; workflows as persisted state machines (Rebus dropped)

- Status: Accepted (amended 2026-10-01)
- Date: 2026-09-28, amended 2026-10-01
- Source: D5 key decision 4, amended by the 2026-10-01 decision that Rebus is not used

## Context

D5 chose Kafka for facts (what sensors, flights and desks did) and Rebus for intentions (escalate this alert, send that notification, generate that report), with RabbitMQ as transport or Rebus's PostgreSQL transport at small sites. On 2026-10-01 it was decided that Rebus is not used.

## Decision

- Kafka carries facts. There is no second message broker.
- Long-running workflows (alert escalation, notification sending with retries, report and evidence-pack generation) are modelled as persisted state machines in Ariva.Core, with their state stored in PostgreSQL through `IStorageProvider`, and driven by Kafka events.
- Time-based steps (escalate after N minutes, retry a failed notification, close an evaluation window) are TickerQ jobs in Ariva.Api.Cronz ([ADR-0020](ADR-0020-tickerq-background-jobs.md)).
- A request for another service to act is published as an event through the transactional outbox.

## Consequences

- One messaging system to operate; smaller footprint at small sites.
- Workflow state is hand-built. This is the cost D5 named for "Kafka only". Contained by keeping workflows few, each an explicit state machine with idempotent transitions and unit tests for every transition.
- Kafka's replay remains available for recomputation and recovery.

## Alternatives considered

- Rebus sagas over RabbitMQ or Rebus's PostgreSQL transport (D5's original choice). Dropped 2026-10-01.
- RabbitMQ only. Rejected in D5: no replay for recomputation.
- MassTransit sagas. Depends on the client library choice ([ADR-0018](ADR-0018-confluent-kafka-behind-isvcmessagebus.md)); not chosen while that ADR is Proposed.
