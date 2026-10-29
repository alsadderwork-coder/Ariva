namespace Ariva.Core.Queueing;

/// <summary>An entry or exit line of a queue zone without a live covering device, in a snapshot (ARV-259e1).</summary>
public sealed record LineCoverageState(string LineName, DateTime UncoveredSinceUtc, DateTime? MarkedThroughUtc);

/// <summary>A spell a line had no live covering device whose live minutes may not all have been published yet, in a snapshot.</summary>
public sealed record RecentUncoveredState(string LineName, DateTime FromUtc, DateTime ToUtc);

/// <summary>
/// Which entry and exit lines of a queue zone have no live covering device (ARV-259e1, formulas F11: such a line, or no
/// measurement at all, makes the zone Unknown). Until devices declare the lines they cover (ARV-259e4, ARV-259e5) every
/// device the zone tracks covers every entry and exit line, so a line is uncovered exactly when every device the zone tracks
/// is out, or it tracks none. A spell starts where the last covering device's outage started (the latest one, back-dated as
/// outages are) and ends where the first covering device is live again (where its outage ends, or the minute the push of a
/// device heard for the first time covers); a spell of no length is dropped. Pure, and judged at the same moments as device
/// liveness (<see cref="Recompute"/> after every change of it), so a replay of the same records gives the same spells.
/// </summary>
internal sealed class LineCoverage
{
    private sealed class Spell
    {
        public DateTime Since;
        public DateTime? MarkedThrough;
    }

    private readonly string[] _lines;
    private readonly SortedDictionary<string, Spell> _open = new(StringComparer.Ordinal);
    private readonly List<RecentUncoveredState> _recent = [];
    private readonly int _maxRecent;

    public LineCoverage(IEnumerable<string> lines)
    {
        _lines = [.. (lines ?? []).Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        _maxRecent = 4 * Math.Max(1, _lines.Length);
    }

    /// <summary>The entry and exit lines of the zone, in ordinal order.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Whether a line has no live covering device now.</summary>
    public bool AnyUncovered => _open.Count > 0;

    private static DateTime MinuteOf(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    /// <summary>
    /// After a change of the zone's device liveness: lines that lost their last live covering device open a spell (where the
    /// latest of their covering devices' outages started, or <paramref name="openFallbackUtc"/> when the zone tracks none of
    /// them), and lines a device covers again close theirs at <paramref name="closeAtUtc"/>. Returns where the closed spells'
    /// bins still need marking (the part after the marks made while they were open).
    /// </summary>
    public IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> Recompute(DeviceLiveness liveness, DateTime openFallbackUtc, DateTime closeAtUtc)
    {
        // Every device the zone tracks covers every line until devices declare theirs (ARV-259e5), so one look at the devices
        // judges every line: the cost is the devices and the lines once, and only when the devices' live state changed.
        var (anyLive, latestOutSince) = liveness.Covering();
        List<(DateTime, DateTime)> closed = null;
        foreach (var line in _lines)
        {
            var covered = anyLive;
            var open = _open.TryGetValue(line, out var spell);
            if (!covered && !open)
                _open[line] = new Spell { Since = latestOutSince ?? MinuteOf(openFallbackUtc) };
            else if (covered && open)
            {
                _open.Remove(line);
                if (closeAtUtc <= spell.Since)
                    continue;
                Remember(new RecentUncoveredState(line, spell.Since, closeAtUtc));
                var from = spell.MarkedThrough is { } marked && marked > spell.Since ? marked : spell.Since;
                if (closeAtUtc > from)
                    (closed ??= []).Add((from, closeAtUtc));
            }
        }

        return closed ?? [];
    }

    /// <summary>
    /// The open spells whose bins should be marked through <paramref name="throughUtc"/> (as device outages are, so a bin is
    /// marked before it can become final and only a spell's first mark can ask for a recomputation), each from where its last
    /// mark ended.
    /// </summary>
    public IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> MarksDue(DateTime throughUtc)
    {
        List<(DateTime, DateTime)> due = null;
        foreach (var spell in _open.Values)
        {
            // Nullable values are read through HasValue and Value, never pattern variables of a condition (ARV-069a).
            if (throughUtc <= spell.Since)
                continue;
            var marked = spell.MarkedThrough;
            if (marked.HasValue && throughUtc <= marked.Value)
                continue;
            var start = marked.HasValue && marked.Value > spell.Since ? marked.Value : spell.Since;
            spell.MarkedThrough = throughUtc;
            (due ??= []).Add((start, throughUtc));
        }

        return due ?? [];
    }

    /// <summary>Whether a line had no live covering device during the minute starting at <paramref name="minuteUtc"/>.</summary>
    public bool UncoveredDuring(DateTime minuteUtc)
    {
        foreach (var spell in _open.Values)
        {
            if (spell.Since <= minuteUtc)
                return true;
        }

        foreach (var r in _recent)
        {
            if (r.FromUtc <= minuteUtc && minuteUtc < r.ToUtc)
                return true;
        }

        return false;
    }

    /// <summary>Forgets ended spells whose minutes were all published live (up to <paramref name="lastLiveUtc"/>).</summary>
    public void Published(DateTime lastLiveUtc) => _recent.RemoveAll(r => r.ToUtc <= lastLiveUtc.AddMinutes(1));

    /// <summary>
    /// Ends the spells still open at <paramref name="endUtc"/> (a replay's end): they count for the live minutes before the end
    /// only, and their bins are marked through it; returns those marks.
    /// </summary>
    public IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> EndAll(DateTime endUtc)
    {
        var end = MinuteOf(endUtc);
        var marks = new List<(DateTime, DateTime)>();
        foreach (var (line, spell) in _open)
        {
            if (spell.Since >= end)
                continue;
            Remember(new RecentUncoveredState(line, spell.Since, end));
            var marked = spell.MarkedThrough;
            var from = marked.HasValue && marked.Value > spell.Since ? marked.Value : spell.Since;
            if (end > from)
                marks.Add((from, end));
        }

        _open.Clear();
        return marks;
    }

    // Ended spells wait for their live minutes; a stuck watermark cannot grow the list beyond a bound.
    private void Remember(RecentUncoveredState spell)
    {
        _recent.Add(spell);
        if (_recent.Count > _maxRecent)
            _recent.RemoveAt(0);
    }

    public (IReadOnlyList<LineCoverageState> Open, IReadOnlyList<RecentUncoveredState> Recent) Capture() =>
        ([.. _open.Select(s => new LineCoverageState(s.Key, s.Value.Since, s.Value.MarkedThrough))], [.. _recent]);

    /// <summary>
    /// The coverage a snapshot says, checked against the zone's lines and its restored devices (CWE-501): each line an entry or
    /// exit line of the zone, once; while the zone tracks a device, a line is uncovered exactly when no device the zone tracks is
    /// live; the open lines are none or all, sharing one start and one mark; and <see cref="SnapshotChecks"/> bounds every time
    /// by the zone's clock. Within those bounds the start and the mark are taken as written, and so are the spells of a zone that
    /// tracks no device (it has either never heard one or forgotten them all after a day's silence, its spell opened when they
    /// went out). A snapshot written before the zone kept coverage (<paramref name="derive"/>) carries none: its spells are
    /// derived from its devices, from where the latest outage started, and marked from their start at the next step. Any
    /// problem is an <see cref="InvalidDataException"/>.
    /// </summary>
    public static LineCoverage Restore(IEnumerable<string> lines, IReadOnlyList<LineCoverageState> open, IReadOnlyList<RecentUncoveredState> recent,
        DeviceLiveness liveness, DateTime referenceUtc, bool derive)
    {
        var coverage = new LineCoverage(lines);
        var tracked = liveness.Count > 0;
        if (derive)
        {
            if (tracked)
                coverage.Recompute(liveness, referenceUtc, referenceUtc);
            return coverage;
        }

        var known = new HashSet<string>(coverage._lines, StringComparer.Ordinal);
        foreach (var state in open ?? [])
        {
            if (state is null || !known.Contains(state.LineName) || coverage._open.ContainsKey(state.LineName))
                throw new InvalidDataException("The zone snapshot names a line without coverage that is not one of the zone's entry or exit lines, or names one twice.");
            coverage._open[state.LineName] = new Spell
            {
                Since = DateTime.SpecifyKind(state.UncoveredSinceUtc, DateTimeKind.Utc),
                MarkedThrough = state.MarkedThroughUtc is { } m ? DateTime.SpecifyKind(m, DateTimeKind.Utc) : null
            };
        }

        var (anyLive, _) = liveness.Covering();
        foreach (var line in coverage._lines)
        {
            if (tracked && coverage._open.ContainsKey(line) == anyLive)
                throw new InvalidDataException("The zone snapshot's lines without coverage do not agree with its devices.");
        }

        // Every device covers every line until devices declare theirs (ARV-259e5 relaxes this), so the lines lose and regain their
        // coverage together: either none is open or all are, from one start and marked through one minute. The start is not bound
        // by the devices' outages: a device first heard reporting itself offline during a spell goes out after it started, and the
        // device whose outage started it may have been forgotten since.
        if (coverage._open.Count > 0)
        {
            var first = coverage._open.Values.First();
            if (coverage._open.Count != coverage._lines.Length || coverage._open.Values.Any(s => s.Since != first.Since || s.MarkedThrough != first.MarkedThrough))
                throw new InvalidDataException("The zone snapshot's lines without coverage do not share one spell, as lines covered by every device do.");
        }

        foreach (var r in (recent ?? []).Take(coverage._maxRecent))
        {
            if (r is null || !known.Contains(r.LineName))
                throw new InvalidDataException("The zone snapshot names a recent spell of a line that is not one of the zone's entry or exit lines.");
            coverage._recent.Add(r with { FromUtc = DateTime.SpecifyKind(r.FromUtc, DateTimeKind.Utc), ToUtc = DateTime.SpecifyKind(r.ToUtc, DateTimeKind.Utc) });
        }

        return coverage;
    }

    /// <summary>
    /// The union of marks (ranges of [from, to)), sorted and merged where they overlap or touch, so one range of bins is
    /// marked once whatever number of lines lost their coverage over it.
    /// </summary>
    public static IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> Union(IEnumerable<(DateTime FromUtc, DateTime ToUtc)> marks)
    {
        var merged = new List<(DateTime FromUtc, DateTime ToUtc)>();
        foreach (var (from, to) in marks.Where(m => m.ToUtc > m.FromUtc).OrderBy(m => m.FromUtc).ThenBy(m => m.ToUtc))
        {
            if (merged.Count > 0 && from <= merged[^1].ToUtc)
                merged[^1] = (merged[^1].FromUtc, to > merged[^1].ToUtc ? to : merged[^1].ToUtc);
            else
                merged.Add((from, to));
        }

        return merged;
    }
}
