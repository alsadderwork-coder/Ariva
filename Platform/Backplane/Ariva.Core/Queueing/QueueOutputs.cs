namespace Ariva.Core.Queueing;

/// <summary>How a realised wait was measured (F5).</summary>
public enum WaitMethod
{
    /// <summary>One track from its first entry crossing to its exit crossing (T3, the primary method).</summary>
    Track,

    /// <summary>The n-th entrant paired with the n-th exit, from individual crossings (FIFO).</summary>
    Fifo,

    /// <summary>Cumulative curves from interval counts (T1): entries and exits spread evenly over their interval.</summary>
    Cumulative
}

/// <summary>What became of a person who entered and is not counted with a realised wait (F6).</summary>
public enum EntrantOutcome
{
    /// <summary>Left the queue without passing the exit (back over an entry line).</summary>
    Abandoned,

    /// <summary>A track lost inside the zone and not seen again within the hand-over window.</summary>
    Fragmented,

    /// <summary>Still unresolved T_censor after entry, or pushed out when the zone's state reached its bound.</summary>
    Censored,

    /// <summary>Dropped when the queue was observed empty while the FIFO sequence still held them (a counting residual).</summary>
    Reanchored,

    /// <summary>Paired with an exit earlier than the entry (F5: a clock or geometry error); no wait is kept.</summary>
    Rejected
}

/// <summary>A realised wait (F5), attributed later to the bin of <see cref="EntryUtc"/> (F6).</summary>
public sealed record RealisedWait(DateTime EntryUtc, DateTime ExitUtc, WaitMethod Method, bool Degraded, string TrackKey)
{
    public TimeSpan Wait => ExitUtc - EntryUtc;
}

/// <summary>A person who entered at <see cref="EntryUtc"/> and was resolved at <see cref="ResolvedUtc"/> without a wait.</summary>
public sealed record EntrantResolution(DateTime EntryUtc, DateTime ResolvedUtc, EntrantOutcome Outcome, string TrackKey)
{
    /// <summary>The person entered as a track of their own (T3), for the track completion rate of F18 (ARV-114a).</summary>
    public bool Tracked { get; init; }
}

/// <summary>Entries and exits counted in one minute (UTC, aligned), with how many of each came from a degraded input.</summary>
public sealed record MovementCount(DateTime MinuteUtc, long Entries, long Exits, long DegradedEntries, long DegradedExits)
{
    /// <summary>The entries that were tracks of their own (T3), for the track completion rate of F18 (ARV-114a).</summary>
    public long TrackedEntries { get; init; }
}

/// <summary>
/// The occupancy the sensors report for the whole queue at a minute boundary (ARV-114a, F18 conservation): the latest
/// reading of the queue zone plus the latest readings of the overflow bands heard so far, each at or before
/// <see cref="AtUtc"/> and fresh there. Only boundaries where every one of them is fresh have a sample; a band never heard
/// counts as empty, so people standing in a band without a sensor show in the conservation residual.
/// </summary>
public sealed record OccupancySample(DateTime AtUtc, int Count);

/// <summary>The lowest and highest occupancy reading of one zone of the queue in one minute (ARV-114a, F18 occupancy sanity).</summary>
public sealed record ZoneReadingMinute(string ZoneName, DateTime MinuteUtc, int Min, int Max);

/// <summary>
/// Crossings of one line in one minute (UTC, aligned) that the engine applied in a step (ARV-113): every crossing of a
/// line of the zone in each direction, and the in and out counts of interval readings spread as the engine spreads
/// them, whatever the crossing did to the queue. A delta: later steps may add to the same line and minute.
/// </summary>
public sealed record LineMovement(string LineName, QueueLineRole Role, DateTime MinuteUtc, long In, long Out);

/// <summary>
/// The queue length at the watermark: the sum of the latest occupancy readings of the queue zone and its overflow
/// bands when all are fresh, otherwise the people the engine holds (entries minus exits since the last anchor).
/// </summary>
public sealed record QueueLength(int Count, bool FromSensors, bool Degraded, DateTime AtUtc);

/// <summary>Why events were not used, counted per step.</summary>
public sealed record QueueRejections
{
    /// <summary>Older than the watermark when they arrived: their minutes were already processed (a recomputation decides).</summary>
    public long Late { get; init; }

    /// <summary>The earliest time among the late events, or null.</summary>
    public DateTime? EarliestLateUtc { get; init; }

    /// <summary>Late events older than the recomputation horizon: counted, too old for the archive to recompute.</summary>
    public long BeyondHorizon { get; init; }

    /// <summary>Late events per minute of their event time (within the horizon), so that only their bins are recomputed.</summary>
    public IReadOnlyList<(DateTime MinuteUtc, long Count)> LateByMinute { get; init; } = [];

    /// <summary>Further ahead of the reference clock than the allowed skew.</summary>
    public long Future { get; init; }

    /// <summary>Times without a UTC kind, negative or implausible counts, intervals that are empty, inverted or longer than a day.</summary>
    public long Invalid { get; init; }

    /// <summary>Lines or zones that are not the queue zone's.</summary>
    public long UnknownGeometry { get; init; }

    /// <summary>A track's second exit, or a repeated entry of an open track.</summary>
    public long Duplicates { get; init; }

    /// <summary>Inward crossings of an exit line.</summary>
    public long Reverse { get; init; }

    /// <summary>Exits with no one left to pair (people who entered before the engine started, or count errors).</summary>
    public long UnmatchedExits { get; init; }

    /// <summary>Pairs whose exit precedes the entry (F5: a clock or geometry error).</summary>
    public long NegativeWaits { get; init; }

    /// <summary>Events refused because the reorder buffer (or its share for events ahead of the clock) was full.</summary>
    public long BufferFull { get; init; }

    /// <summary>Times a full buffer made the engine process events that were already due before the step.</summary>
    public long ForcedAdvances { get; init; }

    /// <summary>Tracked crossings counted as anonymous because the zone already has the most devices it tracks separately.</summary>
    public long TooManyDevices { get; init; }

    public bool Any => Late + Future + Invalid + UnknownGeometry + Duplicates + Reverse + UnmatchedExits + NegativeWaits + BufferFull +
        ForcedAdvances + TooManyDevices > 0;
}

/// <summary>
/// What one <see cref="QueueStateEngine.Advance"/> produced: every event up to the watermark processed in time order,
/// the realised waits, the resolutions without a wait, the movements per minute, the queue length and the rejections.
/// <see cref="More"/> is true when the step reached its size bound with due events left: advance again to get them.
/// </summary>
public sealed record QueueStep(
    string QueueZone,
    DateTime WatermarkUtc,
    IReadOnlyList<MovementCount> Movements,
    IReadOnlyList<RealisedWait> Waits,
    IReadOnlyList<EntrantResolution> Resolutions,
    QueueLength Length,
    QueueRejections Rejections,
    int Reanchors,
    int OpenEntrants,
    int BufferedEvents,
    bool More)
{
    /// <summary>The line crossings applied in this step, per line and minute (ARV-113), in minute and then line name order.</summary>
    public IReadOnlyList<LineMovement> Lines { get; init; } = [];

    /// <summary>The queue's sensor occupancy at the minute boundaries this step passed (ARV-114a), in time order.</summary>
    public IReadOnlyList<OccupancySample> Occupancy { get; init; } = [];

    /// <summary>The occupancy readings applied in this step, per zone and minute (ARV-114a), in minute and then zone order.</summary>
    public IReadOnlyList<ZoneReadingMinute> Readings { get; init; } = [];
}
