namespace Ariva.Core.Queueing;

/// <summary>The counts and waits of a bin or of one of its minutes, in a snapshot.</summary>
public sealed record BinTallyState(
    long Entries,
    long Exits,
    long Abandoned,
    long Fragmented,
    long Censored,
    long Reanchored,
    long Rejected,
    long Resolved,
    long LateEvents,
    long Waits,
    bool Degraded,
    IReadOnlyList<double> Exact,
    IReadOnlyList<HistogramBucketState> Histogram,
    double Sum,
    long WithinTarget);

/// <summary>One bucket of a wait histogram.</summary>
public sealed record HistogramBucketState(int Bucket, long Count);

/// <summary>A minute of a bin.</summary>
public sealed record BinMinuteState(DateTime MinuteUtc, BinTallyState Tally, bool Changed);

/// <summary>An open bin.</summary>
public sealed record BinState(DateTime StartUtc, BinTallyState Total, IReadOnlyList<BinMinuteState> Minutes, BinQuality Marked, bool Changed, bool ExactOverflowed)
{
    /// <summary>The bin's health tallies (ARV-114a); a snapshot written before has none, and the bin's health starts empty.</summary>
    public BinHealthState Health { get; init; }
}

/// <summary>A minute of a bin with occupancy readings: checked against a physical capacity, and outside 0 to it (ARV-114a).</summary>
public sealed record HealthMinuteState(DateTime MinuteUtc, bool Checked, bool Outside);

/// <summary>The health tallies of an open bin (F18, ARV-114a).</summary>
public sealed record BinHealthState(
    long TracksEntered,
    long TracksExited,
    long TracksAbandoned,
    long TracksFragmented,
    long TracksCensored,
    long TracksRejected,
    int? OccupancyStart,
    int? OccupancyEnd,
    IReadOnlyList<HealthMinuteState> Minutes);

/// <summary>A quality mark still in force.</summary>
public sealed record BinMarkState(DateTime FromUtc, DateTime ToUtc, BinQuality Quality);

/// <summary>Everything a <see cref="BinAccumulator"/> holds (ARV-034 snapshots); plain data for System.Text.Json.</summary>
public sealed record BinAccumulatorState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string QueueZone { get; init; }
    public int ProfileVersion { get; init; }
    public IReadOnlyList<BinState> Open { get; init; } = [];
    public IReadOnlyList<BinMarkState> Marks { get; init; } = [];
    public DateTime WatermarkUtc { get; init; }
    public DateTime? NextBinUtc { get; init; }
    public DateTime? FinalHighUtc { get; init; }
}

public sealed partial class BinAccumulator
{
    /// <summary>The accumulator's whole state.</summary>
    public BinAccumulatorState Capture() => new()
    {
        QueueZone = _zone,
        ProfileVersion = _profileVersion,
        Open = [.. _open.Select(b => new BinState(b.Key, Of(b.Value.Total),
            [.. b.Value.Minutes.Select(m => new BinMinuteState(m.Key, Of(m.Value), b.Value.ChangedMinutes.Contains(m.Key)))],
            b.Value.Marked, b.Value.Changed, b.Value.ExactOverflowed) { Health = Of(b.Value.Health) })],
        Marks = [.. _marks.Select(m => new BinMarkState(m.From, m.To, m.Quality))],
        WatermarkUtc = _watermark,
        NextBinUtc = _nextBin,
        FinalHighUtc = _finalHigh
    };

    private static BinHealthState Of(BinHealth h) => new(h.TracksEntered, h.TracksExited, h.TracksAbandoned, h.TracksFragmented, h.TracksCensored,
        h.TracksRejected, h.OccupancyStart, h.OccupancyEnd, [.. h.Minutes.Select(m => new HealthMinuteState(m.Key, m.Value.Checked, m.Value.Outside))]);

    private static BinTallyState Of(Tally t) => new(t.Entries, t.Exits, t.Abandoned, t.Fragmented, t.Censored, t.Reanchored, t.Rejected, t.Resolved,
        t.LateEvents, t.Waits, t.Degraded, [.. t.Exact], [.. t.Histogram.Select(h => new HistogramBucketState(h.Key, h.Value))], t.Sum, t.WithinTarget);

    /// <summary>
    /// An accumulator in the captured state, checked against the bounds of <paramref name="settings"/>; <paramref name="geometry"/>
    /// gives the physical capacities for the health checks, as for a new one.
    /// </summary>
    public static BinAccumulator Restore(BinSettings settings, BinAccumulatorState state, QueueZoneGeometry geometry = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != BinAccumulatorState.CurrentVersion)
            throw new InvalidDataException($"Bin snapshot version {state.Version} is not {BinAccumulatorState.CurrentVersion}.");
        var accumulator = new BinAccumulator(state.QueueZone, state.ProfileVersion, settings, geometry);
        var s = accumulator._settings;
        var open = state.Open ?? [];
        if (open.Count > s.MaxOpenBins || (state.Marks?.Count ?? 0) > s.MaxMarks ||
            open.Any(b => b is null || b.Total is null || (b.Minutes?.Count ?? 0) > s.BinLength.TotalMinutes))
            throw new InvalidDataException("The bin snapshot exceeds the accumulator's bounds.");
        long exact = 0;
        // Without geometry the queue has no known overflow bands: one zone at the canonical bound.
        var maxOccupancy = geometry?.MaxQueueOccupancy ?? Sensing.CanonicalEventRules.MaxOccupancy;
        foreach (var b in open)
        {
            var start = QueueInputState.Utc(b.StartUtc);
            if (accumulator.BinOf(start) != start || (b.Minutes ?? []).Any(m => m is null || m.MinuteUtc < start || m.MinuteUtc >= start + s.BinLength))
                throw new InvalidDataException("The bin snapshot has a bin or minute outside the bin calendar.");
            var bin = new Bin { Marked = b.Marked, Changed = b.Changed, ExactOverflowed = b.ExactOverflowed };
            Fill(bin.Total, b.Total, ref exact);
            foreach (var m in b.Minutes ?? [])
            {
                if (m?.Tally is null)
                    throw new InvalidDataException("The bin snapshot has an empty minute.");
                var minute = QueueInputState.Utc(m.MinuteUtc);
                var tally = new Tally();
                Fill(tally, m.Tally, ref exact);
                bin.Minutes[minute] = tally;
                if (m.Changed)
                    bin.ChangedMinutes.Add(minute);
            }

            if (b.Health is { } h)
                Fill(bin.Health, h, start, s.BinLength, maxOccupancy);

            if (exact > 2L * s.MaxExactWaitsPerBin + 2)
                throw new InvalidDataException("The bin snapshot holds more exact waits than a bin keeps.");
            exact = 0;
            accumulator._open[QueueInputState.Utc(b.StartUtc)] = bin;
        }

        foreach (var m in state.Marks ?? [])
        {
            if (m is not null)
                accumulator._marks.Add((QueueInputState.Utc(m.FromUtc), QueueInputState.Utc(m.ToUtc), m.Quality));
        }

        accumulator._watermark = QueueInputState.Utc(state.WatermarkUtc);
        accumulator._nextBin = state.NextBinUtc is { } next ? QueueInputState.Utc(next) : null;
        accumulator._finalHigh = state.FinalHighUtc is { } high ? QueueInputState.Utc(high) : null;
        return accumulator;
    }

    /// <summary>
    /// A bin's health tallies, checked (CWE-501, CWE-120): counts not negative, occupancy from zero to the queue's bound
    /// (<paramref name="maxOccupancy"/>), each minute once and inside the bin.
    /// </summary>
    private static void Fill(BinHealth health, BinHealthState state, DateTime start, TimeSpan length, long maxOccupancy)
    {
        long[] counts = [state.TracksEntered, state.TracksExited, state.TracksAbandoned, state.TracksFragmented, state.TracksCensored, state.TracksRejected];
        if (counts.Any(n => n is < 0 or > SnapshotChecks.MaxCount) || state.OccupancyStart < 0 || state.OccupancyEnd < 0 ||
            state.OccupancyStart > maxOccupancy || state.OccupancyEnd > maxOccupancy ||
            (state.Minutes?.Count ?? 0) > length.TotalMinutes)
            throw new InvalidDataException("The bin snapshot has health tallies out of bounds.");
        (health.TracksEntered, health.TracksExited, health.TracksAbandoned) = (state.TracksEntered, state.TracksExited, state.TracksAbandoned);
        (health.TracksFragmented, health.TracksCensored, health.TracksRejected) = (state.TracksFragmented, state.TracksCensored, state.TracksRejected);
        (health.OccupancyStart, health.OccupancyEnd) = (state.OccupancyStart, state.OccupancyEnd);
        foreach (var m in state.Minutes ?? [])
        {
            var minute = m is null ? default : QueueInputState.Utc(m.MinuteUtc);
            if (m is null || !LineCounts.IsMinute(minute) || minute < start || minute >= start + length || !health.Minutes.TryAdd(minute, (m.Checked, m.Outside)))
                throw new InvalidDataException("The bin snapshot has a health minute outside its bin or twice.");
        }
    }

    private static void Fill(Tally t, BinTallyState s, ref long exact)
    {
        (t.Entries, t.Exits, t.Abandoned, t.Fragmented, t.Censored) = (s.Entries, s.Exits, s.Abandoned, s.Fragmented, s.Censored);
        (t.Reanchored, t.Rejected, t.Resolved, t.LateEvents, t.Waits) = (s.Reanchored, s.Rejected, s.Resolved, s.LateEvents, s.Waits);
        (t.Degraded, t.Sum, t.WithinTarget) = (s.Degraded, s.Sum, s.WithinTarget);
        foreach (var w in s.Exact ?? [])
        {
            if (!double.IsFinite(w))
                throw new InvalidDataException("The bin snapshot has a wait that is not a number.");
            t.Exact.Add(w);
        }

        exact += t.Exact.Count;
        foreach (var h in s.Histogram ?? [])
        {
            if (h is not null)
                t.Histogram[h.Bucket] = h.Count;
        }
    }
}

/// <summary>One minute of exits in a snapshot.</summary>
public sealed record ExitMinuteState(DateTime MinuteUtc, long Exits, long Degraded);

public sealed partial class ExitRate
{
    /// <summary>The minutes held (at most an hour).</summary>
    public IReadOnlyList<ExitMinuteState> Capture() => [.. _minutes.Select(m => new ExitMinuteState(m.Key, m.Value.Exits, m.Value.Degraded))];

    /// <summary>An exit rate holding the captured minutes (the latest hour of them).</summary>
    public static ExitRate Restore(IEnumerable<ExitMinuteState> minutes)
    {
        var rate = new ExitRate();
        foreach (var m in (minutes ?? []).Where(m => m is not null).OrderByDescending(m => m.MinuteUtc).Take(KeptMinutes))
            rate._minutes[QueueInputState.Utc(m.MinuteUtc)] = (Math.Max(0, m.Exits), Math.Max(0, m.Degraded));
        return rate;
    }
}
