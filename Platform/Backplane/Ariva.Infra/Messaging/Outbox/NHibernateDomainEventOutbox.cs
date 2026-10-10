using System.Text.Json;

namespace Ariva.Infra.Messaging.Outbox;

/// <summary>
/// The transactional outbox (ADR-0018): the unit of work hands over the domain events of a commit, and each becomes an
/// <c>outbox_message</c> row in the same transaction as the entity change. Either both commit or neither does; the
/// relay produces the rows afterwards. An event without a topic fails the commit, because dropping it would lose a
/// fact other services depend on.
/// </summary>
internal sealed class NHibernateDomainEventOutbox(EventCatalog catalog, TimeProvider timeProvider) : IDomainEventOutbox
{
    public async Task WriteAsync(IStorageProvider storage, IReadOnlyList<IEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (events is not { Count: > 0 })
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var message in events)
        {
            var type = message.GetType();
            var key = message.GetPartitionKey();
            if (string.IsNullOrEmpty(key))
                throw new InvalidOperationException($"{type.Name} {message.Id} has no partition key; every event is keyed (ADR-0018).");

            var payload = JsonSerializer.Serialize(message, type, EventCatalog.Json);
            // Kafka refuses values over 1 MB; such a row would hold its key back forever, so the commit fails now instead.
            if (System.Text.Encoding.UTF8.GetByteCount(payload) > KafkaSettings.MaxMessageBytes - MessageHeadroom)
                throw new InvalidOperationException($"{type.Name} {message.Id} is larger than a Kafka message may be; split it or send a reference.");
            // outbox_message.message_key holds 200 characters: a longer key would fail the commit with 22001, so it fails here
            // with the reason instead (OutboxLimits). The key is not logged: it is made of names from configuration.
            if (!OutboxLimits.FitsMessageKey(key))
                throw new InvalidOperationException(
                    $"{type.Name} {message.Id} has a partition key longer than {OutboxLimits.MaxMessageKeyLength} characters; the outbox cannot hold it.");

            await storage.ExecuteSqlAsync<InsertedRow>(
                """
                INSERT INTO outbox_message (id, topic, message_key, message_type, payload, headers, created_on)
                VALUES (:id, :topic, :key, :type, CAST(:payload AS jsonb), CAST(:headers AS jsonb), :created)
                RETURNING 1 AS "Value"
                """,
                new Dictionary<string, object>
                {
                    ["id"] = message.Id,
                    ["topic"] = catalog.TopicOf(type),
                    ["key"] = key,
                    ["type"] = type.Name,
                    ["payload"] = payload,
                    ["headers"] = JsonSerializer.Serialize(OutboxHeaders.For(message), EventCatalog.Json),
                    ["created"] = now
                },
                ct);
        }
    }

    /// <summary>Room for the key and headers within the broker's limit.</summary>
    private const int MessageHeadroom = 16 * 1024;

    private sealed class InsertedRow
    {
        public int Value { get; set; }
    }
}

/// <summary>The headers that travel with an event: tracing ids and the event's name and version.</summary>
public static class OutboxHeaders
{
    public const string EventType = "ariva-event-type";
    public const string EventVersion = "ariva-event-version";
    public const string CorrelationId = "ariva-correlation-id";
    public const string CausationId = "ariva-causation-id";

    public static Dictionary<string, string> For(IEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventType] = message.GetType().Name,
            [EventVersion] = message.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrEmpty(message.CorrelationId))
            headers[CorrelationId] = message.CorrelationId;
        if (!string.IsNullOrEmpty(message.CausationId))
            headers[CausationId] = message.CausationId;
        return headers;
    }
}
