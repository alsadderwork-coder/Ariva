namespace Ariva.Core.Services;

/// <summary>
/// Publishes events to Kafka (AMAN's abstraction, ADR-0018), keyed by <see cref="IEvent.GetPartitionKey"/> on the
/// topic named by the event's <see cref="Messaging.KafkaTopicAttribute"/>.
/// <para>
/// Domain events raised by aggregates do not come here: the unit of work writes them to the outbox in the entity's
/// transaction and the relay produces them. Use this for facts that are not the result of a database change (a
/// stream worker's nowcast, an ingest sample), where losing the process before the produce loses nothing that was
/// stored.
/// </para>
/// </summary>
public interface ISvcMessageBus
{
    Task PublishAsync<TEvent>(TEvent message, CancellationToken ct = default) where TEvent : class, IEvent;
}
