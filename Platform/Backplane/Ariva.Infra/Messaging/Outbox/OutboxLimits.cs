using Ariva.Core.Messaging;

namespace Ariva.Infra.Messaging.Outbox;

/// <summary>
/// The limits of an <c>outbox_message</c> row that every outbox writer checks before it inserts (script 0011). A row the
/// table refuses would fail its whole transaction with a database error (22001), which a writer that retries on database
/// errors would repeat forever, so each writer checks first: the NHibernate outbox fails the commit with a clear error,
/// the stream's checkpoint drops the event with a warning (ARV-115). The key limit itself lives in Core
/// (<see cref="MessageKeys"/>, ARV-114c), so the zone profile refuses a queue zone name whose zone key would not fit.
/// </summary>
public static class OutboxLimits
{
    /// <summary>The longest partition key, in characters (<c>outbox_message.message_key varchar(200)</c>).</summary>
    public const int MaxMessageKeyLength = MessageKeys.MaxLength;

    /// <summary>
    /// Whether <paramref name="key"/> fits <c>message_key</c>: 1 to <see cref="MaxMessageKeyLength"/> characters, counted as
    /// PostgreSQL counts them (Unicode code points, so a character outside the basic plane counts once).
    /// </summary>
    public static bool FitsMessageKey(string key) => MessageKeys.Fits(key);
}
