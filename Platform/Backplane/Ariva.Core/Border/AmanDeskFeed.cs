using Ariva.Core.Desks;

namespace Ariva.Core.Border;

/// <summary>The kind of a stored AMAN record (ARV-048 tables).</summary>
public enum AmanRecordKind
{
    DeskSession,
    DeskInterval,
    EgateInterval
}

/// <summary>
/// One stored AMAN record as the desk feed reads it (ARV-049): its id and when Ariva received it (the read cursor), the
/// Ariva desk or gate it resolved to (null when unmapped, ARV-048), its time (the session change, or the interval's
/// start), and the counts of its kind. Counts and states only.
/// </summary>
public sealed record AmanFeedRecord(
    AmanRecordKind Kind,
    Guid Id,
    DateTime ReceivedUtc,
    string DeskKey,
    string Lane,
    DateTime TimeUtc,
    string SessionState = null,
    int Transactions = 0,
    int Attempts = 0,
    int Rejected = 0,
    double MeanCycleSeconds = 0)
{
    /// <summary>When the record shows the feed was alive: a session change when it happened, an interval when it closed.</summary>
    public DateTime FeedTimeUtc => Kind == AmanRecordKind.DeskSession ? TimeUtc : TimeUtc.AddSeconds(ImmigrationRules.IntervalSeconds);
}

/// <summary>One e-gate over one minute from AMAN's interval statistics (the <c>egate_minute</c> row, F12).</summary>
public sealed record EgateMinute(string GateKey, string Lane, DateTime MinuteUtc, int Processed, int Rejected, double? MeanCycleSeconds);

/// <summary>What one read of AMAN's records gives the engines: desk signals in order, e-gate minutes and the feed heartbeat.</summary>
public sealed record AmanDeskFeedStep(IReadOnlyList<DeskSignal> Signals, IReadOnlyList<EgateMinute> EgateMinutes, DateTime? HeartbeatUtc);

/// <summary>
/// AMAN's stored records as desk-state signals (ARV-049, formulas F10): a desk session change is a session signal (rank
/// 2: Opened, Closed or Paused, AMAN's "on break"); a desk's closed interval is its transactions completed (rank 1,
/// activity, never a transaction in progress); an e-gate interval is that gate's minute. AMAN publishes sessions only on
/// change and intervals only while a desk is in session, so the feed itself is the heartbeat (F10: for AMAN, T_stale is
/// judged on the feed): each whole minute the feed is seen alive up to its latest record, every AMAN desk of the site gets
/// a heartbeat for its session and transaction sources, so a desk closed for an hour stays Closed rather than Unknown,
/// and when the feed stops the desks turn Unknown after T_stale. Unmapped records (no Ariva desk) keep the feed alive and
/// give no signal. Pure; at most <see cref="MaxHeartbeatMinutes"/> heartbeat minutes per step.
/// </summary>
public static class AmanDeskFeed
{
    /// <summary>The longest run of heartbeat minutes one step gives (a gap longer than this is caught up from its end).</summary>
    public const int MaxHeartbeatMinutes = 15;

    /// <summary>Records further ahead of Ariva's clock than this do not show the feed alive (the desk engine refuses them too).</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The signals of <paramref name="records"/> and the heartbeats since <paramref name="lastHeartbeatUtc"/>. The feed is
    /// alive up to its latest record, but never past Ariva's clock: a record dated ahead (AMAN's clock skewed, or the
    /// intake's allowance for a simulator) cannot move the heartbeat into the future and silence it.
    /// </summary>
    public static AmanDeskFeedStep Step(IEnumerable<AmanFeedRecord> records, IReadOnlyCollection<string> desks, DateTime? lastHeartbeatUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(desks);
        var nowMinute = Floor(nowUtc);
        if (lastHeartbeatUtc > nowMinute)
            lastHeartbeatUtc = nowMinute;
        var signals = new List<DeskSignal>();
        var gates = new List<EgateMinute>();
        DateTime? fed = null;
        foreach (var r in records)
        {
            if (r is null)
                continue;
            var alive = r.FeedTimeUtc < nowUtc ? r.FeedTimeUtc : nowUtc;
            if (r.FeedTimeUtc <= nowUtc + MaxAhead && (fed is null || alive > fed))
                fed = alive;
            if (r.DeskKey is null)
                continue;
            switch (r.Kind)
            {
                case AmanRecordKind.DeskSession when Enum.TryParse<DeskSessionSignal>(r.SessionState, ignoreCase: false, out var state) && Enum.IsDefined(state):
                    signals.Add(new DeskSessionChangedSignal(r.DeskKey, r.TimeUtc, state));
                    break;
                case AmanRecordKind.DeskInterval:
                    signals.Add(new DeskTransactionsCompleted(r.DeskKey, r.FeedTimeUtc, Math.Max(0, r.Transactions)));
                    break;
                case AmanRecordKind.EgateInterval:
                    gates.Add(new EgateMinute(r.DeskKey, r.Lane, r.TimeUtc, Math.Max(0, r.Attempts), Math.Max(0, r.Rejected),
                        r.MeanCycleSeconds > 0 && double.IsFinite(r.MeanCycleSeconds) ? r.MeanCycleSeconds : null));
                    break;
            }
        }

        var heartbeat = lastHeartbeatUtc;
        if (fed is { } seen)
        {
            var last = Floor(seen);
            var first = heartbeat is { } h ? Floor(h).AddMinutes(1) : last;
            if (first < last.AddMinutes(-(MaxHeartbeatMinutes - 1)))
                first = last.AddMinutes(-(MaxHeartbeatMinutes - 1));
            for (var minute = first; minute <= last; minute = minute.AddMinutes(1))
            {
                foreach (var desk in desks)
                {
                    signals.Add(new DeskHeartbeat(desk, minute, DeskSource.Session));
                    signals.Add(new DeskHeartbeat(desk, minute, DeskSource.Transactions));
                }
            }

            if (heartbeat is null || last > heartbeat)
                heartbeat = last;
        }

        return new AmanDeskFeedStep(signals.OrderBy(s => s.TimeUtc).ToList(), gates, heartbeat);
    }

    /// <summary>The desk or gate key in the stream's minute tables: site, checkpoint and desk code (unique while the desk lives).</summary>
    public static string Key(string siteCode, string checkpointCode, string deskCode) => DeskKeys.For(siteCode, checkpointCode, deskCode);

    private static DateTime Floor(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}

/// <summary>A record already taken, remembered while a read can still return it.</summary>
public sealed record AmanTakenRecord(Guid Id, DateTime ReceivedUtc);

/// <summary>
/// The read position in AMAN's stored records (ARV-049): records are read by the time Ariva received them, from
/// <see cref="Overlap"/> before the position, because an intake transaction that began earlier can commit after a later
/// one; the records already taken within the overlap are remembered and left out of the next read (so a read's limit
/// counts only records not yet taken, and a burst larger than the limit is read over several steps). At most
/// <c>maxTaken</c> are remembered: past it the oldest are forgotten and the read no longer reaches back before the oldest
/// kept (<see cref="Floor"/>), so nothing is read twice; a record committed that late is then missed (counted).
/// </summary>
public sealed record AmanFeedCursor(DateTime PositionUtc, IReadOnlyList<AmanTakenRecord> Taken, DateTime? Floor = null)
{
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    public static AmanFeedCursor Start(DateTime nowUtc) => new(nowUtc, []);

    /// <summary>Where the next read starts.</summary>
    public DateTime ReadFromUtc => Floor is { } floor && floor > PositionUtc - Overlap ? floor : PositionUtc - Overlap;

    /// <summary>
    /// The records not taken yet, in the order received, and the cursor after them; <paramref name="maxTaken"/> bounds
    /// what is remembered.
    /// </summary>
    public (IReadOnlyList<AmanFeedRecord> Fresh, AmanFeedCursor Next) Take(IReadOnlyList<AmanFeedRecord> read, int maxTaken = 50_000) =>
        Take(read, r => r.Id, r => r.ReceivedUtc, maxTaken);

    /// <summary>
    /// The same for any stored rows read by the time Ariva wrote them, given each row's id and that time (ARV-116: the
    /// staff and service zone readings the stream writes to <c>desk_zone_reading</c>).
    /// </summary>
    public (IReadOnlyList<T> Fresh, AmanFeedCursor Next) Take<T>(IReadOnlyList<T> read, Func<T, Guid> id, Func<T, DateTime> received, int maxTaken = 50_000)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(received);
        var taken = new HashSet<Guid>((Taken ?? []).Select(t => t.Id));
        var fresh = read.Where(r => r is not null && taken.Add(id(r))).OrderBy(received).ThenBy(id).ToList();
        var position = fresh.Count == 0 ? PositionUtc : new DateTime(Math.Max(PositionUtc.Ticks, fresh.Max(r => received(r)).Ticks), DateTimeKind.Utc);
        var keepFrom = position - Overlap;
        var kept = (Taken ?? []).Concat(fresh.Select(r => new AmanTakenRecord(id(r), received(r))))
            .Where(t => t.ReceivedUtc >= keepFrom && (Floor is null || t.ReceivedUtc >= Floor))
            .OrderBy(t => t.ReceivedUtc)
            .ToList();
        var floor = Floor is { } f && f > keepFrom ? f : (DateTime?)null;
        if (kept.Count > maxTaken)
        {
            // Keep the newest; records received at the same instant as the newest dropped one are kept too, so the floor
            // never cuts a set of records in two.
            var cut = kept[kept.Count - maxTaken].ReceivedUtc;
            kept = kept.Where(t => t.ReceivedUtc >= cut).ToList();
            floor = cut;
        }

        return (fresh, new AmanFeedCursor(position, kept, floor));
    }

    /// <summary>The ids to leave out of the next read.</summary>
    public Guid[] TakenIds => (Taken ?? []).Select(t => t.Id).ToArray();
}
