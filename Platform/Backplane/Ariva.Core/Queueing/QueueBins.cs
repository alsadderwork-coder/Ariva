namespace Ariva.Core.Queueing;

/// <summary>Data quality of a result (F11): the worst input wins.</summary>
public enum BinQuality
{
    Good,
    Degraded,
    Unknown
}

/// <summary>Provisional until everyone who entered is resolved and the watermark has passed the bin (F6); then final.</summary>
public enum BinStatus
{
    Provisional,
    Final
}

/// <summary>
/// Wait statistics of one bin or minute (F7), in minutes: realised waits counted, mean, median, P90, P95, the share
/// within the target, and the sparse 30-second histogram that lets longer windows merge exactly (never averaged).
/// Percentiles are null when fewer passengers than the minimum waited. Final results take percentiles from the exact
/// waits; provisional ones from the histogram (exact to 30 seconds), which keeps a live update cheap.
/// </summary>
public sealed record WaitSummary(
    long Waits,
    double? MeanMinutes,
    double? P50Minutes,
    double? P90Minutes,
    double? P95Minutes,
    double? ShareWithinTarget,
    IReadOnlyList<(int Bucket, long Count)> Histogram);

/// <summary>
/// One bin of a queue zone (ADR-0007, F6): the people who entered in [Start, Start + delta), their realised waits and
/// what became of those without one, the exits counted in the bin, the status, quality and revision. Revision 1 is
/// the live result (provisional numbers change in place until final); a recomputation of a final bin produces revision
/// r + 1 with its reason (<see cref="BinRevisions"/>).
/// </summary>
public sealed record BinResult(
    string QueueZone,
    DateTime StartUtc,
    TimeSpan Length,
    int Revision,
    BinStatus Status,
    BinQuality Quality,
    long Entries,
    long Exits,
    WaitSummary Waits,
    long Abandoned,
    long Fragmented,
    long Censored,
    long Reanchored,
    long Rejected,
    long Open,
    long LateEvents,
    int ZoneProfileVersion,
    string RevisionReason)
{
    /// <summary>The values that matter for a revision: everything but the revision, its reason and the late-event count.</summary>
    public bool SameValues(BinResult other) =>
        other is not null && QueueZone == other.QueueZone && StartUtc == other.StartUtc && Length == other.Length && Status == other.Status &&
        Quality == other.Quality && Entries == other.Entries && Exits == other.Exits && Abandoned == other.Abandoned &&
        Fragmented == other.Fragmented && Censored == other.Censored && Reanchored == other.Reanchored && Rejected == other.Rejected &&
        Open == other.Open && ZoneProfileVersion == other.ZoneProfileVersion && Waits.Waits == other.Waits.Waits &&
        Waits.MeanMinutes == other.Waits.MeanMinutes && Waits.P50Minutes == other.Waits.P50Minutes && Waits.P90Minutes == other.Waits.P90Minutes &&
        Waits.P95Minutes == other.Waits.P95Minutes && Waits.ShareWithinTarget == other.Waits.ShareWithinTarget &&
        Waits.Histogram.SequenceEqual(other.Waits.Histogram);
}

/// <summary>One minute of a queue zone: entries and exits in the minute, and the waits of the people who entered in it.</summary>
public sealed record MinuteResult(string QueueZone, DateTime StartUtc, BinStatus Status, long Entries, long Exits, WaitSummary Waits);

/// <summary>
/// Bins that late events or a late outage touched after they were final: they change only through a recomputation over
/// the archived events (ADR-0007), from <see cref="FromUtc"/> to <see cref="ToUtc"/> (contiguous bins only).
/// </summary>
public sealed record RecomputationRequest(string QueueZone, DateTime FromUtc, DateTime ToUtc, long LateEvents, string Reason);

/// <summary>
/// What one <see cref="BinAccumulator.Accept"/> changed: minutes and bins with new numbers, recomputations due, and the
/// ranges the watermark jumped over without bins (more than the open-bin bound at once), which have no results.
/// </summary>
public sealed record BinUpdate(
    IReadOnlyList<MinuteResult> Minutes,
    IReadOnlyList<BinResult> Bins,
    IReadOnlyList<RecomputationRequest> Recomputations,
    IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> Gaps);

/// <summary>Settings of bin attribution (F6, F7).</summary>
public sealed record BinSettings
{
    /// <summary>delta: the bin length (15 minutes unless a contract says otherwise); a whole number of minutes dividing a day.</summary>
    public TimeSpan BinLength { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Offset of the operating day's start from UTC midnight, in whole minutes; bins align to it.</summary>
    public TimeSpan DayStartOffset { get; init; } = TimeSpan.Zero;

    /// <summary>The wait target for the share within target (F7, D4), in minutes.</summary>
    public double TargetMinutes { get; init; } = 15;

    /// <summary>Fewer realised waits than this give no percentiles (F7: the contract's minimum, default 1).</summary>
    public int MinimumPassengers { get; init; } = 1;

    /// <summary>Censored above this share of a bin's entries makes it Degraded (F6, Proposed 5 percent).</summary>
    public double CensoredDegradedShare { get; init; } = 0.05;

    /// <summary>Exact waits kept per bin, its minutes included; beyond it final percentiles come from the histogram (Degraded).</summary>
    public int MaxExactWaitsPerBin { get; init; } = 50_000;

    /// <summary>Bins held open at most; beyond it the earliest is published final with its open people (and Degraded).</summary>
    public int MaxOpenBins { get; init; } = 2_000;

    /// <summary>Quality marks held at most.</summary>
    public int MaxMarks { get; init; } = 1_000;

    public IEnumerable<string> Problems()
    {
        if (BinLength < TimeSpan.FromMinutes(1) || BinLength > TimeSpan.FromDays(1) || TimeSpan.FromDays(1).Ticks % BinLength.Ticks != 0 ||
            BinLength.Ticks % TimeSpan.TicksPerMinute != 0)
            yield return "BinLength is a whole number of minutes that divides a day.";
        if (DayStartOffset <= TimeSpan.FromHours(-24) || DayStartOffset >= TimeSpan.FromHours(24) || DayStartOffset.Ticks % TimeSpan.TicksPerMinute != 0)
            yield return "DayStartOffset is a whole number of minutes within a day.";
        if (!double.IsFinite(TargetMinutes) || TargetMinutes <= 0 || TargetMinutes > 24 * 60)
            yield return "TargetMinutes is above 0 and at most a day.";
        if (MinimumPassengers is < 1 or > 10_000)
            yield return "MinimumPassengers is from 1 to 10,000.";
        if (!double.IsFinite(CensoredDegradedShare) || CensoredDegradedShare is < 0 or > 1)
            yield return "CensoredDegradedShare is from 0 to 1.";
        if (MaxExactWaitsPerBin is < 0 or > 1_000_000 || MaxOpenBins is < 1 or > 10_000 || MaxMarks is < 1 or > 10_000)
            yield return "The bin bounds are at most 1,000,000 exact waits, 10,000 open bins and 10,000 marks.";
    }
}

/// <summary>
/// Attribution of a queue zone's results to entry bins (ARV-031, ADR-0007, formulas F6 and F7). It consumes the queue
/// engine's steps in order: each realised wait and each resolution goes to the bin and minute of its entry, entries and
/// exits to the minute they happened. A bin is final when the watermark has passed its end (the engine's watermark
/// already allows for lateness) and every person who entered in it is resolved; its quality is the worst of its inputs
/// (degraded events, a residual re-anchor or rejected pairs, censored above the share, and outages marked by the
/// caller as Degraded or Unknown, F11). Final results never change here: late events or late outages for a final bin
/// produce a <see cref="RecomputationRequest"/> instead. Pure: no I/O, no clock. Bounded: open bins, exact waits per
/// bin and marks; counters are 64-bit.
/// </summary>
public sealed partial class BinAccumulator
{
    private sealed class Tally
    {
        public long Entries, Exits, Abandoned, Fragmented, Censored, Reanchored, Rejected, Resolved, LateEvents, Waits;
        public bool Degraded;
        public readonly List<double> Exact = [];
        public readonly SortedDictionary<int, long> Histogram = [];
        public double Sum;
        public long WithinTarget;
    }

    private sealed class Bin
    {
        public Tally Total { get; } = new();
        public SortedDictionary<DateTime, Tally> Minutes { get; } = [];
        public BinQuality Marked { get; set; }
        public bool Changed { get; set; }
        public bool ExactOverflowed { get; set; }
        public HashSet<DateTime> ChangedMinutes { get; } = [];
    }

    private readonly string _zone;
    private readonly int _profileVersion;
    private readonly BinSettings _settings;
    private readonly SortedDictionary<DateTime, Bin> _open = [];
    private readonly List<(DateTime From, DateTime To, BinQuality Quality)> _marks = [];
    private DateTime _watermark = DateTime.MinValue;
    private DateTime? _nextBin;
    private DateTime? _finalHigh;

    public BinAccumulator(string queueZone, int zoneProfileVersion, BinSettings settings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueZone);
        _settings = settings ?? new BinSettings();
        var problems = _settings.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(settings));
        _zone = queueZone;
        _profileVersion = zoneProfileVersion;
    }

    /// <summary>Bins still open (provisional).</summary>
    public int OpenBins => _open.Count;

    /// <summary>The start of the bin holding <paramref name="time"/>.</summary>
    public DateTime BinOf(DateTime time)
    {
        var length = _settings.BinLength.Ticks;
        var origin = DateTime.UnixEpoch.Ticks + _settings.DayStartOffset.Ticks;
        var offset = time.Ticks - origin;
        var floored = origin + offset - (((offset % length) + length) % length);
        // Near the very start of the calendar the bin would begin before it: clamp (unreachable for real event times).
        return new DateTime(Math.Max(floored, DateTime.MinValue.Ticks), DateTimeKind.Utc);
    }

    private static DateTime MinuteOf(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private bool IsFinal(DateTime start) => !_open.ContainsKey(start) && _finalHigh is { } high && start <= high;

    /// <summary>
    /// Marks the data quality of [from, to) for this zone (F11): Degraded for a degraded sensor, an Unknown desk or a
    /// stale feed; Unknown for an entry or exit line without coverage. Open bins take the mark; final bins it covers
    /// are returned as recomputations (a final result never changes in place). Ranges longer than 7 days are refused.
    /// </summary>
    public IReadOnlyList<RecomputationRequest> Mark(DateTime fromUtc, DateTime toUtc, BinQuality quality)
    {
        if (toUtc <= fromUtc || quality == BinQuality.Good || fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc ||
            toUtc - fromUtc > TimeSpan.FromDays(7))
            return [];
        var recomputations = new List<RecomputationRequest>();
        for (var start = BinOf(fromUtc); start < toUtc && start <= DateTime.MaxValue - _settings.BinLength; start += _settings.BinLength)
        {
            if (_open.TryGetValue(start, out var bin))
            {
                if (quality > bin.Marked)
                {
                    bin.Marked = quality;
                    bin.Changed = true;
                }
            }
            else if (IsFinal(start))
            {
                recomputations.Add(new RecomputationRequest(_zone, start, start + _settings.BinLength, 0, $"the zone was {quality} after the bin was final"));
            }
        }

        // Bins created later take the mark from the list; marks behind every open bin no longer matter; the rest are bounded.
        _marks.Add((fromUtc, toUtc, quality));
        var behind = _open.Count > 0 ? _open.First().Key : _watermark;
        _marks.RemoveAll(m => m.To <= behind);
        if (_marks.Count > _settings.MaxMarks)
            _marks.RemoveRange(0, _marks.Count - _settings.MaxMarks);
        return Runs(recomputations);
    }

    /// <summary>Takes one engine step, in order, and returns what changed.</summary>
    public BinUpdate Accept(QueueStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!string.Equals(step.QueueZone, _zone, StringComparison.Ordinal))
            throw new ArgumentException($"The step is for {step.QueueZone}, not {_zone}.", nameof(step));

        var recomputations = new List<RecomputationRequest>();
        foreach (var (minute, count) in step.Rejections.LateByMinute)
        {
            var start = BinOf(minute);
            if (_open.TryGetValue(start, out var lateBin))
                lateBin.Total.LateEvents += count; // still provisional: recomputed once it is final
            else
                recomputations.Add(new RecomputationRequest(_zone, start, start + _settings.BinLength, count, "late events arrived after the bin was final"));
        }

        foreach (var movement in step.Movements)
        {
            var (bin, minute) = Slot(movement.MinuteUtc);
            if (bin is null)
                continue;
            bin.Total.Entries += movement.Entries;
            minute.Entries += movement.Entries;
            bin.Total.Exits += movement.Exits;
            minute.Exits += movement.Exits;
            if (movement.DegradedEntries + movement.DegradedExits > 0)
                bin.Total.Degraded = true;
            Touch(bin, MinuteOf(movement.MinuteUtc));
        }

        foreach (var wait in step.Waits)
        {
            var (bin, minute) = Slot(wait.EntryUtc);
            if (bin is null)
                continue;
            AddWait(bin, minute, wait.Wait);
            bin.Total.Resolved++;
            if (wait.Degraded)
                bin.Total.Degraded = true;
            Touch(bin, MinuteOf(wait.EntryUtc));
        }

        foreach (var resolution in step.Resolutions)
        {
            var (bin, _) = Slot(resolution.EntryUtc);
            if (bin is null)
                continue;
            switch (resolution.Outcome)
            {
                case EntrantOutcome.Abandoned:
                    bin.Total.Abandoned++;
                    break;
                case EntrantOutcome.Fragmented:
                    bin.Total.Fragmented++;
                    break;
                case EntrantOutcome.Censored:
                    bin.Total.Censored++;
                    break;
                case EntrantOutcome.Reanchored:
                    bin.Total.Reanchored++;
                    bin.Total.Degraded = true;
                    break;
                case EntrantOutcome.Rejected:
                    bin.Total.Rejected++;
                    bin.Total.Degraded = true;
                    break;
            }

            bin.Total.Resolved++;
            Touch(bin, MinuteOf(resolution.EntryUtc));
        }

        if (step.WatermarkUtc > _watermark)
            _watermark = step.WatermarkUtc;

        // Every bin the watermark has reached exists even without a person, so that an empty bin is published final (F6);
        // a jump beyond the open-bin bound is reported as a gap instead of thousands of empty bins.
        var gaps = new List<(DateTime, DateTime)>();
        if (_watermark - DateTime.MinValue > _settings.BinLength * (_settings.MaxOpenBins + 1))
        {
            var latest = BinOf(_watermark);
            var earliestKept = latest - _settings.BinLength * (_settings.MaxOpenBins - 1);
            var start = _nextBin ?? (_open.Count > 0 ? _open.First().Key : latest);
            if (start < earliestKept)
            {
                gaps.Add((start, earliestKept));
                start = earliestKept;
            }

            for (; start <= latest; start += _settings.BinLength)
                Slot(start);
            _nextBin = latest + _settings.BinLength;
        }

        var bins = new List<BinResult>();
        var minutes = new List<MinuteResult>();
        foreach (var (start, bin) in _open.ToList())
        {
            var open = bin.Total.Entries - bin.Total.Resolved;
            var ended = _watermark >= start + _settings.BinLength;
            var forced = _open.Count > _settings.MaxOpenBins && start == _open.First().Key;
            var final = (ended && open <= 0) || forced;
            if (!bin.Changed && !final)
                continue;
            var status = final ? BinStatus.Final : BinStatus.Provisional;
            var result = Result(start, bin, status, Math.Max(0, open), forced && open > 0);
            bins.Add(result);
            var minuteKeys = final ? bin.Minutes.Keys.ToList() : bin.ChangedMinutes.Order().ToList();
            foreach (var minute in minuteKeys)
            {
                var tally = bin.Minutes[minute];
                minutes.Add(new MinuteResult(_zone, minute, status, tally.Entries, tally.Exits, Summary(tally, final && !bin.ExactOverflowed)));
            }

            bin.ChangedMinutes.Clear();
            bin.Changed = false;
            if (final)
            {
                _open.Remove(start);
                if (_finalHigh is not { } high || start > high)
                    _finalHigh = start;
                if (bin.Total.LateEvents > 0)
                    recomputations.Add(new RecomputationRequest(_zone, start, start + _settings.BinLength, bin.Total.LateEvents,
                        "late events arrived while the bin was provisional"));
            }
        }

        return new BinUpdate(minutes, bins, Runs(recomputations), gaps);
    }

    /// <summary>One request per run of contiguous bins with the same reason, never one span over unrelated bins.</summary>
    private List<RecomputationRequest> Runs(List<RecomputationRequest> requests)
    {
        var runs = new List<RecomputationRequest>();
        foreach (var r in requests.OrderBy(r => r.Reason, StringComparer.Ordinal).ThenBy(r => r.FromUtc))
        {
            if (runs.Count > 0 && runs[^1].Reason == r.Reason && r.FromUtc <= runs[^1].ToUtc)
            {
                var last = runs[^1];
                runs[^1] = last with { ToUtc = r.ToUtc > last.ToUtc ? r.ToUtc : last.ToUtc, LateEvents = last.LateEvents + r.LateEvents };
            }
            else
            {
                runs.Add(r);
            }
        }

        return runs;
    }

    /// <summary>The open bin and minute tally for a time, creating them; null for a bin that is already final.</summary>
    private (Bin Bin, Tally Minute) Slot(DateTime time)
    {
        var start = BinOf(time);
        if (!_open.TryGetValue(start, out var bin))
        {
            // A final bin is never reopened here: only a recomputation changes it (ADR-0007).
            if (_finalHigh is { } high && start <= high)
                return (null, null);
            bin = new Bin { Changed = true };
            foreach (var (from, to, quality) in _marks)
            {
                if (start < to && start + _settings.BinLength > from && quality > bin.Marked)
                    bin.Marked = quality;
            }

            _open[start] = bin;
        }

        var minute = MinuteOf(time);
        if (!bin.Minutes.TryGetValue(minute, out var tally))
            bin.Minutes[minute] = tally = new Tally();
        return (bin, tally);
    }

    private static void Touch(Bin bin, DateTime minute)
    {
        bin.Changed = true;
        bin.ChangedMinutes.Add(minute);
    }

    /// <summary>
    /// Adds a wait to its bin and minute. Exact waits are kept once, in the minute's list, within the bin's budget; the
    /// bin's exact percentiles are read from all its minutes when it is final.
    /// </summary>
    private void AddWait(Bin bin, Tally minute, TimeSpan wait)
    {
        var minutes = wait.TotalMinutes;
        var bucket = WaitStatistics.BucketOf(wait);
        foreach (var tally in new[] { bin.Total, minute })
        {
            tally.Waits++;
            tally.Sum += minutes;
            if (minutes <= _settings.TargetMinutes)
                tally.WithinTarget++;
            tally.Histogram[bucket] = tally.Histogram.GetValueOrDefault(bucket) + 1;
        }

        if (bin.ExactOverflowed)
            return;
        if (bin.Total.Waits <= _settings.MaxExactWaitsPerBin)
        {
            minute.Exact.Add(minutes);
        }
        else
        {
            bin.ExactOverflowed = true;
            foreach (var m in bin.Minutes.Values)
                m.Exact.Clear();
        }
    }

    private WaitSummary Summary(Tally tally, bool exact, IEnumerable<double> exactWaits = null)
    {
        var histogram = tally.Histogram.Select(kv => (kv.Key, kv.Value)).ToList();
        if (tally.Waits == 0)
            return new WaitSummary(0, null, null, null, null, null, histogram);
        double?[] percentiles;
        if (tally.Waits < _settings.MinimumPassengers)
            percentiles = [null, null, null];
        else if (exact)
            percentiles = WaitStatistics.Percentiles(exactWaits ?? tally.Exact, 0.5, 0.9, 0.95);
        else
            percentiles = [WaitStatistics.Percentile(histogram, 0.5), WaitStatistics.Percentile(histogram, 0.9), WaitStatistics.Percentile(histogram, 0.95)];
        return new WaitSummary(tally.Waits, tally.Sum / tally.Waits, percentiles[0], percentiles[1], percentiles[2], (double)tally.WithinTarget / tally.Waits, histogram);
    }

    private BinResult Result(DateTime start, Bin bin, BinStatus status, long open, bool forced)
    {
        var t = bin.Total;
        var quality = bin.Marked;
        if (quality < BinQuality.Degraded &&
            (t.Degraded || forced || (status == BinStatus.Final && bin.ExactOverflowed) || (t.Entries > 0 && t.Censored > _settings.CensoredDegradedShare * t.Entries)))
            quality = BinQuality.Degraded;
        var exact = status == BinStatus.Final && !bin.ExactOverflowed;
        var summary = Summary(t, exact, exact ? bin.Minutes.Values.SelectMany(m => m.Exact) : null);
        return new BinResult(_zone, start, _settings.BinLength, 1, status, quality, t.Entries, t.Exits, summary, t.Abandoned, t.Fragmented,
            t.Censored, t.Reanchored, t.Rejected, open, t.LateEvents, _profileVersion, null);
    }
}

/// <summary>
/// Revisions of final bins (ADR-0007, F6): a recomputation over the archived events gives new results for a range;
/// a final bin whose values changed gets revision r + 1 with the reason and the recomputation time, and the earlier
/// revision is kept by whoever stores them. Bins whose values did not change produce nothing.
/// </summary>
public static class BinRevisions
{
    public static IReadOnlyList<BinResult> Next(IReadOnlyList<BinResult> published, IReadOnlyList<BinResult> recomputed, string reason)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(recomputed);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var latest = published
            .Where(b => b.Status == BinStatus.Final)
            .GroupBy(b => (b.QueueZone, b.StartUtc))
            .ToDictionary(g => g.Key, g => Enumerable.MaxBy(g, b => b.Revision));
        var revisions = new List<BinResult>();
        foreach (var bin in recomputed.Where(b => b.Status == BinStatus.Final))
        {
            if (latest.TryGetValue((bin.QueueZone, bin.StartUtc), out var current) && current.SameValues(bin))
                continue;
            revisions.Add(bin with { Revision = (current?.Revision ?? 0) + 1, RevisionReason = reason, LateEvents = 0 });
        }

        return revisions;
    }
}
