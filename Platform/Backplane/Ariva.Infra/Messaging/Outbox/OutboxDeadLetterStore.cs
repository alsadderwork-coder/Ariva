using System.Text;
using System.Text.Json;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Messaging.Outbox;

/// <summary>
/// Keeps a dead letter that Kafka did not take before its endpoint stopped as an <c>outbox_message</c> row for the
/// dead-letter topic (ARV-171), which the relay sends later with the same per-key order, backoff and pass bound as every
/// row. Only then does the dead-letter filter let the record complete, so MassTransit's checkpointer can no longer commit
/// past a record that was neither processed nor dead-lettered.
/// <para>
/// The row is written in a short transaction of its own (the consumer's unit of work was rolled back with its failure),
/// bounded by the caller's token. Its topic comes from <see cref="Ariva.Core.Messaging.KafkaTopics.DeadLetter"/>, which
/// refuses a source topic that is not a constant; its id from <see cref="DeadLetters.IdOf"/>, so a record that is delivered
/// again and kept again adds no second row (<c>ON CONFLICT (id) DO NOTHING</c>); its key from
/// <see cref="DeadLetters.OutboxKeyOf"/>, which fits <c>message_key</c>; its payload is the letter's JSON, within
/// <see cref="DeadLetters.MaxLetterBytes"/> by construction and checked again here. Nothing of the letter is logged.
/// </para>
/// </summary>
internal sealed class OutboxDeadLetterStore(DatabaseSettings database, TimeProvider timeProvider) : IDeadLetterStore
{
    public async Task KeepAsync(DeadLetter letter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(letter);
        var topic = DeadLetters.TopicFor(letter);
        var payload = JsonSerializer.Serialize(letter, EventCatalog.Json);
        if (Encoding.UTF8.GetByteCount(payload) > DeadLetters.MaxLetterBytes)
            throw new InvalidOperationException($"The dead letter for {letter.Topic} is larger than a Kafka message may be.");

        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO outbox_message (id, topic, message_key, message_type, payload, headers, created_on)
            VALUES (@id, @topic, @key, @type, CAST(@payload AS jsonb), '{}'::jsonb, @created)
            ON CONFLICT (id) DO NOTHING
            """,
            connection);
        command.Parameters.AddWithValue("id", DeadLetters.IdOf(letter));
        command.Parameters.AddWithValue("topic", topic);
        command.Parameters.AddWithValue("key", DeadLetters.OutboxKeyOf(letter));
        command.Parameters.AddWithValue("type", DeadLetters.OutboxMessageType);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("created", timeProvider.GetUtcNow().UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }
}
