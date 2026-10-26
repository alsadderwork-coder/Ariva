namespace Ariva.Infra.Messaging.Outbox;

/// <summary>
/// How long each outbox row has failed with the broker unavailable without a break (ARV-245). Since ARV-165 such a failure
/// counts no attempt, so a row whose partition has no leader (a partial outage: <c>Local_MsgTimedOut</c> for that partition
/// alone, while the brokers answer) is tried every <c>Kafka:Outbox:BrokerRetrySeconds</c> for ever and never reaches the
/// "stuck after N attempts" Error. The relay notes each such failure here and reports the row once, when its streak passes
/// <see cref="OutboxRelaySettings.BrokerStreakMinutes"/>. A send ends the streak, and so does a failure of the row's own (the
/// broker answered it).
/// <para>
/// Kept in the relay's memory, not in the row: a relay that takes over the lead (another pod, or this one after a restart)
/// starts the streaks it sees afresh, so a row is reported once per relay that leads through its streak. Bounded: a streak
/// not seen for <see cref="ForgetAfter"/> is dropped (another relay sent the row, or this one stopped leading), and at most
/// <see cref="MaxRows"/> are kept; past that a new row is not followed until one is dropped. Single threaded, like the
/// relay's passes.
/// </para>
/// </summary>
internal sealed class OutboxBrokerStreaks(TimeSpan reportAfter)
{
    /// <summary>A streak not noted again for this long is dropped.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

    /// <summary>The most rows followed at once.</summary>
    public const int MaxRows = 10_000;

    #region Fields

    private readonly Dictionary<long, Streak> _streaks = [];

    #endregion

    private sealed class Streak(DateTime since)
    {
        public DateTime Since { get; } = since;
        public DateTime LastSeen { get; set; } = since;
        public bool Reported { get; set; }
    }

    #region Properties

    /// <summary>The rows followed now (for the tests).</summary>
    internal int Count => _streaks.Count;

    #endregion

    #region Methods

    /// <summary>
    /// Notes that row <paramref name="seq"/> failed with the broker unavailable at <paramref name="now"/>. Returns the length of
    /// its streak the first time it reaches <see cref="OutboxRelaySettings.BrokerStreakMinutes"/>, and null otherwise (before,
    /// after it was reported, or when the row is not followed).
    /// </summary>
    public TimeSpan? BrokerUnavailable(long seq, DateTime now)
    {
        Prune(now);
        if (!_streaks.TryGetValue(seq, out var streak))
        {
            if (_streaks.Count >= MaxRows)
                return null;
            streak = new Streak(now);
            _streaks.Add(seq, streak);
        }

        streak.LastSeen = now;
        var length = now - streak.Since;
        if (streak.Reported || length < reportAfter)
            return null;
        streak.Reported = true;
        return length;
    }

    /// <summary>Row <paramref name="seq"/> was sent, or failed for a reason of its own: its streak, if any, ends.</summary>
    public void Ended(long seq) => _streaks.Remove(seq);

    private void Prune(DateTime now)
    {
        if (_streaks.Count == 0)
            return;
        List<long> stale = null;
        foreach (var (seq, streak) in _streaks)
        {
            if (now - streak.LastSeen > ForgetAfter)
                (stale ??= []).Add(seq);
        }

        foreach (var seq in stale ?? [])
            _streaks.Remove(seq);
    }

    #endregion
}
