namespace Ariva.Infra.Messaging.Outbox;

/// <summary>
/// The limits of an <c>outbox_message</c> row that every outbox writer checks before it inserts (script 0011). A row the
/// table refuses would fail its whole transaction with a database error (22001), which a writer that retries on database
/// errors would repeat forever, so each writer checks first: the NHibernate outbox fails the commit with a clear error,
/// the stream's checkpoint drops the event with a warning (ARV-115).
/// </summary>
public static class OutboxLimits
{
    /// <summary>The longest partition key, in characters (<c>outbox_message.message_key varchar(200)</c>).</summary>
    public const int MaxMessageKeyLength = 200;

    /// <summary>
    /// Whether <paramref name="key"/> fits <c>message_key</c>: 1 to <see cref="MaxMessageKeyLength"/> characters, counted as
    /// PostgreSQL counts them (Unicode code points, so a character outside the basic plane counts once).
    /// </summary>
    public static bool FitsMessageKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        // At most two UTF-16 units per code point: a key this short always fits, and a key twice the limit never does.
        if (key.Length <= MaxMessageKeyLength)
            return true;
        if (key.Length > 2 * MaxMessageKeyLength)
            return false;
        var points = 0;
        foreach (var _ in key.EnumerateRunes())
        {
            if (++points > MaxMessageKeyLength)
                return false;
        }

        return true;
    }
}
