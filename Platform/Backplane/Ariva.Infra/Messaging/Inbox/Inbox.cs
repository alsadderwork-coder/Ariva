using System.Data;

namespace Ariva.Infra.Messaging.Inbox;

/// <summary>Records that a consumer applied an event (the inbox of ADR-0018).</summary>
public interface IInbox
{
    /// <summary>
    /// Claims (consumer, event) inside the current unit of work's transaction. False when the event was applied before,
    /// so the delivery is a duplicate. The claim commits or rolls back with the consumer's own changes.
    /// </summary>
    Task<bool> TryClaimAsync(string consumer, Guid eventId, CancellationToken ct);
}

/// <summary>
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> on <c>processed_event</c> through the unit of work's session. Unlike AMAN's
/// Redis claim, a consumer that fails rolls its claim back, so an event that was not applied is never marked done; a
/// concurrent duplicate waits on the primary key until the first transaction ends, then sees the conflict.
/// </summary>
internal sealed class PostgresInbox(IStorageProvider storage, TimeProvider timeProvider) : IInbox
{
    public async Task<bool> TryClaimAsync(string consumer, Guid eventId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        if (!storage.IsTransactionActive())
            storage.BeginTransaction(IsolationLevel.ReadCommitted);

        var rows = await storage.ExecuteSqlAsync<ClaimRow>(
            """
            INSERT INTO processed_event (consumer, event_id, processed_on) VALUES (:consumer, :eventId, :now)
            ON CONFLICT DO NOTHING
            RETURNING 1 AS "Value"
            """,
            new Dictionary<string, object>
            {
                ["consumer"] = consumer.Length <= 200 ? consumer : consumer[..200],
                ["eventId"] = eventId,
                ["now"] = timeProvider.GetUtcNow().UtcDateTime
            },
            ct);
        return rows.Count == 1;
    }

    private sealed class ClaimRow
    {
        public int Value { get; set; }
    }
}
