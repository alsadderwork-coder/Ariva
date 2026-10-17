namespace Ariva.Core.Messaging;

/// <summary>
/// The one limit on a message's partition key (ARV-114c): a Kafka key and an <c>outbox_message.message_key</c> row
/// (<c>varchar(200)</c>, script 0011) hold the same key, so every key Ariva produces must fit both. Kept in Core so the
/// domain can refuse a name that would make an overlong key (a queue zone name in the zone key) before it is stored;
/// the outbox writers in Infra check the same rule (<c>OutboxLimits</c>) as a last line.
/// </summary>
public static class MessageKeys
{
    /// <summary>The longest partition key, in characters as PostgreSQL counts them (Unicode code points).</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Whether <paramref name="key"/> is 1 to <see cref="MaxLength"/> characters, counted as PostgreSQL counts them
    /// (Unicode code points, so a character outside the basic plane counts once).
    /// </summary>
    public static bool Fits(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        // At most two UTF-16 units per code point: a key this short always fits, and a key twice the limit never does.
        if (key.Length <= MaxLength)
            return true;
        if (key.Length > 2 * MaxLength)
            return false;
        var points = 0;
        foreach (var _ in key.EnumerateRunes())
        {
            if (++points > MaxLength)
                return false;
        }

        return true;
    }
}
