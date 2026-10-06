namespace Ariva.Core.Queueing;

/// <summary>
/// Where a line's minute counts come from (ARV-113, script 0037). <see cref="Ariva"/> is what Ariva's queue engine
/// counted on the line: the crossings and interval counts it applied, in event-time order behind its watermark.
/// <see cref="Vendor"/> is reserved for crossings a vendor computed on its own lines, kept apart as a cross-check once
/// Ariva computes crossings from tracks itself (ADR-0003); the two are never added together.
/// </summary>
public enum LineCountSource
{
    Ariva,
    Vendor
}

/// <summary>
/// One line of a queue zone in one closed minute (ARV-113): the crossings in and out counted on it, with the line's
/// role in the zone profile. Closed means the engine's watermark has passed the minute's end, so the counts never
/// change afterwards (events behind the watermark are late and not applied, F6); a recomputation over the archive
/// is the only route to other numbers. F18 compares these per line and 15-minute bin with manual counts.
/// <see cref="Additive"/> marks a part of a minute released before the watermark passed it (the open bound was reached,
/// see <see cref="LineCounts"/>): its counts add to what was written for the same line and minute, so no count is lost.
/// </summary>
public sealed record LineMinute(string LineName, QueueLineRole Role, DateTime MinuteUtc, long In, long Out, LineCountSource Source = LineCountSource.Ariva)
{
    /// <summary>
    /// The counts are a part of the line minute, added to any written before (an early release); otherwise they are the
    /// whole minute and replace what a replay wrote. Hashed by the replay only when set, so ordinary runs hash as before.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Additive { get; init; }
}

/// <summary>A line's counts in a minute still open (the watermark has not passed its end), as a snapshot holds them.</summary>
public sealed record LineMinuteState(string LineName, QueueLineRole Role, DateTime MinuteUtc, long In, long Out);

/// <summary>
/// The per-line minute counts of one queue zone (ARV-113): the engine's line movements are added to the minute they
/// happened in, and a minute is released as a <see cref="LineMinute"/> once the watermark has passed its end. Events
/// that arrive late but inside the lateness allowance are still ahead of the watermark, so they revise the open minute;
/// events behind it are not applied, so a released minute never changes. Pure and bounded: the engine applies events
/// only up to its watermark, so the open minutes are the lines of the watermark's own minute; at most
/// <see cref="MaxOpenLineMinutes"/> are held (CWE-120). Beyond the bound the earliest is released early, before the
/// watermark has passed it, and later steps may still add to it: every minute up to the latest one released early
/// (<see cref="ReleasedEarlyThroughUtc"/>, kept in the snapshot) is then released as <see cref="LineMinute.Additive"/>
/// parts that the store adds together, until the watermark passes it. No count is lost and the bound holds.
/// </summary>
public sealed class LineCounts
{
    /// <summary>Line minutes held open at most (a profile's lines times the minutes one step can leave open).</summary>
    public const int MaxOpenLineMinutes = 20_000;

    private readonly SortedDictionary<(DateTime Minute, string Line), (QueueLineRole Role, long In, long Out)> _open = new(KeyOrder.Instance);

    /// <summary>Line minutes still open.</summary>
    public int Open => _open.Count;

    /// <summary>The latest minute released before the watermark passed it, while the watermark has not passed it yet.</summary>
    public DateTime? ReleasedEarlyThroughUtc { get; private set; }

    /// <summary>Line minutes released early since this instance started (for health; not in the snapshot).</summary>
    public long ReleasedEarly { get; private set; }

    /// <summary>Adds a step's line movements and returns the line minutes the step closed, in minute and then line name order.</summary>
    public IReadOnlyList<LineMinute> Accept(QueueStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        foreach (var m in step.Lines ?? [])
        {
            if (m is null || m.LineName is null || m.Role == QueueLineRole.Unknown)
                continue;
            var key = (m.MinuteUtc, m.LineName);
            _open.TryGetValue(key, out var t);
            _open[key] = (m.Role, checked(t.In + m.In), checked(t.Out + m.Out));
        }

        var closed = new List<LineMinute>();
        // A minute is closed when every event inside it is at or before the watermark.
        while (_open.Count > 0)
        {
            var (key, value) = _open.First();
            var early = !Passed(key.Minute, step.WatermarkUtc);
            if (early && _open.Count <= MaxOpenLineMinutes)
                break;
            _open.Remove(key);
            if (early)
            {
                ReleasedEarly++;
                if (ReleasedEarlyThroughUtc is not { } through || key.Minute > through)
                    ReleasedEarlyThroughUtc = key.Minute;
            }

            // A minute at or before the latest early release may have a part written already: this part adds to it.
            var additive = ReleasedEarlyThroughUtc is { } mark && key.Minute <= mark;
            closed.Add(new LineMinute(key.Line, value.Role, key.Minute, value.In, value.Out) { Additive = additive });
        }

        // Once the watermark has passed the latest early release, nothing can add to those minutes any more.
        if (ReleasedEarlyThroughUtc is { } last && Passed(last, step.WatermarkUtc))
            ReleasedEarlyThroughUtc = null;
        return closed;
    }

    /// <summary>The open line minutes, for the zone's snapshot.</summary>
    public IReadOnlyList<LineMinuteState> Capture() =>
        [.. _open.Select(o => new LineMinuteState(o.Key.Line, o.Value.Role, o.Key.Minute, o.Value.In, o.Value.Out))];

    /// <summary>
    /// The counts of a snapshot, checked (CWE-501, CWE-120): at most <see cref="MaxOpenLineMinutes"/> entries, each a line
    /// of <paramref name="geometry"/> with its role there, an aligned UTC minute and counts that are not negative; the
    /// latest early release (<paramref name="releasedEarlyThroughUtc"/>), when given, is an aligned plausible minute.
    /// </summary>
    public static LineCounts Restore(QueueZoneGeometry geometry, IReadOnlyList<LineMinuteState> state, DateTime? releasedEarlyThroughUtc = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var counts = new LineCounts();
        if ((state?.Count ?? 0) > MaxOpenLineMinutes)
            throw new InvalidDataException($"The zone snapshot holds more than {MaxOpenLineMinutes} open line minutes.");
        foreach (var l in state ?? [])
        {
            if (l is null || l.LineName is null || geometry.RoleOf(l.LineName) is var role && (role == QueueLineRole.Unknown || role != l.Role))
                throw new InvalidDataException("The zone snapshot counts a line that is not the zone's.");
            var minute = DateTime.SpecifyKind(l.MinuteUtc, DateTimeKind.Utc);
            if (l.In < 0 || l.Out < 0 || !IsMinute(minute))
                throw new InvalidDataException("The zone snapshot has a line minute with a negative count or an unaligned time.");
            if (!counts._open.TryAdd((minute, l.LineName), (l.Role, l.In, l.Out)))
                throw new InvalidDataException("The zone snapshot holds a line minute twice.");
        }

        if (releasedEarlyThroughUtc is { } through)
        {
            through = DateTime.SpecifyKind(through, DateTimeKind.Utc);
            if (!IsMinute(through))
                throw new InvalidDataException("The zone snapshot has an unaligned early release time.");
            counts.ReleasedEarlyThroughUtc = through;
        }

        return counts;
    }

    /// <summary>Whether a time is a whole UTC minute the stream can hold (aligned and plausible; CWE-501).</summary>
    public static bool IsMinute(DateTime minuteUtc) => minuteUtc.Ticks % TimeSpan.TicksPerMinute == 0 && ZoneProcessor.Plausible(minuteUtc);

    private static bool Passed(DateTime minute, DateTime watermarkUtc) => minute.AddMinutes(1).AddTicks(-1) <= watermarkUtc;

    private sealed class KeyOrder : IComparer<(DateTime Minute, string Line)>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare((DateTime Minute, string Line) x, (DateTime Minute, string Line) y)
        {
            var byMinute = x.Minute.CompareTo(y.Minute);
            return byMinute != 0 ? byMinute : string.CompareOrdinal(x.Line, y.Line);
        }
    }
}
