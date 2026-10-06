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
/// </summary>
public sealed record LineMinute(string LineName, QueueLineRole Role, DateTime MinuteUtc, long In, long Out, LineCountSource Source = LineCountSource.Ariva);

/// <summary>A line's counts in a minute still open (the watermark has not passed its end), as a snapshot holds them.</summary>
public sealed record LineMinuteState(string LineName, QueueLineRole Role, DateTime MinuteUtc, long In, long Out);

/// <summary>
/// The per-line minute counts of one queue zone (ARV-113): the engine's line movements are added to the minute they
/// happened in, and a minute is released as a <see cref="LineMinute"/> once the watermark has passed its end. Events
/// that arrive late but inside the lateness allowance are still ahead of the watermark, so they revise the open minute;
/// events behind it are not applied, so a released minute never changes. Pure and bounded: the engine applies events
/// only from its watermark on, so the open minutes are the few the latest step touched; at most
/// <see cref="MaxOpenLineMinutes"/> are held, beyond it the earliest is released (CWE-120).
/// </summary>
public sealed class LineCounts
{
    /// <summary>Line minutes held open at most (a profile's lines times the minutes one step can leave open).</summary>
    public const int MaxOpenLineMinutes = 20_000;

    private readonly SortedDictionary<(DateTime Minute, string Line), (QueueLineRole Role, long In, long Out)> _open = new(KeyOrder.Instance);

    /// <summary>Line minutes still open.</summary>
    public int Open => _open.Count;

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
            if (key.Minute.AddMinutes(1).AddTicks(-1) > step.WatermarkUtc && _open.Count <= MaxOpenLineMinutes)
                break;
            _open.Remove(key);
            closed.Add(new LineMinute(key.Line, value.Role, key.Minute, value.In, value.Out));
        }

        return closed;
    }

    /// <summary>The open line minutes, for the zone's snapshot.</summary>
    public IReadOnlyList<LineMinuteState> Capture() =>
        [.. _open.Select(o => new LineMinuteState(o.Key.Line, o.Value.Role, o.Key.Minute, o.Value.In, o.Value.Out))];

    /// <summary>
    /// The counts of a snapshot, checked (CWE-501, CWE-120): at most <see cref="MaxOpenLineMinutes"/> entries, each a line
    /// of <paramref name="geometry"/> with its role there, an aligned UTC minute and counts that are not negative.
    /// </summary>
    public static LineCounts Restore(QueueZoneGeometry geometry, IReadOnlyList<LineMinuteState> state)
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
            if (l.In < 0 || l.Out < 0 || minute.Ticks % TimeSpan.TicksPerMinute != 0 || !ZoneProcessor.Plausible(minute))
                throw new InvalidDataException("The zone snapshot has a line minute with a negative count or an unaligned time.");
            if (!counts._open.TryAdd((minute, l.LineName), (l.Role, l.In, l.Out)))
                throw new InvalidDataException("The zone snapshot holds a line minute twice.");
        }

        return counts;
    }

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
