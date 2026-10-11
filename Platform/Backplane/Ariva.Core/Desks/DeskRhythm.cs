namespace Ariva.Core.Desks;

/// <summary>
/// A queue zone's latest live minute as the transaction rhythm reads it (ARV-149): the people in the queue zone at the
/// minute's end and whether the stream marked the length degraded. A count only.
/// </summary>
public sealed record LaneQueueSample(string Zone, DateTime MinuteUtc, int Length, bool Degraded);

/// <summary>
/// One AMAN interval of a desk that serves a queue zone (ARV-149): the zone, the interval's row id (an interval read under
/// several zones counts once) and its statistics. Counts and timings only.
/// </summary>
public sealed record LaneIntervalSample(string Zone, string Id, DeskIntervalSample Interval);

/// <summary>The lane contexts of one read and what was left out of them (CWE-501), for the counted warnings.</summary>
public sealed record DeskRhythmContexts(IReadOnlyList<DeskLaneContext> Contexts, int RefusedIntervals, int RefusedQueues);

/// <summary>
/// The lane contexts of the desks without staff or service zones (F10 rows 5a to 5f, ARV-149, Proposed): for each desk,
/// over the queue zones it serves (the zones of its level whose lane is one of its lanes, as the nowcast's desk term
/// matches them, F8),
/// <list type="bullet">
/// <item>s, the lane's expected service time per person: the lane cycle time of ARV-117d (<see cref="LaneCycle.PerPerson"/>,
/// mean service time per document) over the AMAN intervals of every desk serving those zones, each interval once and checked
/// against contract V1's bounds there; none without documents;</item>
/// <item>Q, the people waiting: each zone's latest live minute of the last <see cref="QueueFreshMinutes"/> minutes; Q is the
/// sum when any zone has people (degraded only when every zone with people is), 0 when every zone is known and empty
/// (degraded when any of them is), and not known otherwise. A zone whose latest fresh minute is out of bounds is not
/// known (an earlier minute never stands in for it).</item>
/// </list>
/// A desk that serves no queue zone gets no context (its rows are 5 and 7 as before). Pure: no I/O and no clock.
/// </summary>
public static class DeskRhythm
{
    /// <summary>How old a queue zone's latest live minute may be and still say whether people wait (Proposed, as the desk term's freshness).</summary>
    public const int QueueFreshMinutes = 5;

    /// <summary>
    /// The contexts at <paramref name="atUtc"/> of the desks in <paramref name="served"/> (desk key, queue zone), in desk key
    /// order. <paramref name="atUtc"/> is when they apply and what the queue minutes' freshness is judged from; the desk feed
    /// passes its engine's watermark at the read, the earliest moment a context can apply without revising a closed minute.
    /// </summary>
    public static DeskRhythmContexts Contexts(IEnumerable<(string DeskKey, string Zone)> served, IEnumerable<LaneQueueSample> queues,
        IEnumerable<LaneIntervalSample> intervals, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(served);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(intervals);
        var zonesOf = served.Where(s => DeskKeys.Fits(s.DeskKey) && !string.IsNullOrEmpty(s.Zone))
            .GroupBy(s => s.DeskKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Desk: g.Key, Zones: g.Select(s => s.Zone).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();

        // Each zone's latest fresh minute decides (CWE-501): a length out of bounds is counted, and when it is the latest the
        // zone is not known (an earlier minute never stands in for it; of two of the same minute, the one out of bounds wins).
        var fresh = atUtc.AddMinutes(-QueueFreshMinutes);
        var refusedQueues = 0;
        var deciding = new Dictionary<string, LaneQueueSample>(StringComparer.Ordinal);
        foreach (var q in queues)
        {
            if (q is null || string.IsNullOrEmpty(q.Zone) || q.MinuteUtc < fresh || q.MinuteUtc > atUtc)
                continue;
            var valid = DeskLaneContext.WaitingWithinBounds(q.Length);
            refusedQueues += valid ? 0 : 1;
            LaneQueueSample known = null;
            deciding.TryGetValue(q.Zone, out known);
            if (known is null || q.MinuteUtc > known.MinuteUtc || (q.MinuteUtc == known.MinuteUtc && !valid))
                deciding[q.Zone] = q;
        }

        var latest = deciding.Where(d => DeskLaneContext.WaitingWithinBounds(d.Value.Length)).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

        var byZone = intervals.Where(i => i is not null && !string.IsNullOrEmpty(i.Zone) && i.Id is not null)
            .GroupBy(i => i.Zone, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var contexts = new List<DeskLaneContext>(zonesOf.Count);
        // Desks serving the same zones share s: computed once per set of zones (a lane), not once per desk.
        var serviceOf = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var (desk, zones) in zonesOf)
        {
            var set = string.Join('\n', zones.Order(StringComparer.Ordinal));
            double? service = null;
            if (!serviceOf.TryGetValue(set, out service))
            {
                var mine = zones.SelectMany(z => byZone.GetValueOrDefault(z) ?? []).DistinctBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Interval);
                var seconds = LaneCycle.PerPerson(mine).Minutes * 60;
                service = seconds.HasValue && DeskLaneContext.ServiceWithinBounds(seconds.Value) ? seconds : null;
                serviceOf[set] = service;
            }

            var (waiting, degraded) = Waiting(zones, latest);
            contexts.Add(new DeskLaneContext(desk, atUtc, service, waiting, degraded));
        }

        // Every interval of the read once (by row id), whichever desks and zones read it.
        var refusedIntervals = LaneCycle.PerPerson(byZone.Values.SelectMany(i => i).DistinctBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Interval)).Refused;
        return new DeskRhythmContexts(contexts, refusedIntervals, refusedQueues);
    }

    // Q over a desk's zones: the sum when any has people (degraded only when every zone with people is), 0 when all are known
    // and empty (degraded when any is), not known otherwise.
    private static (int? Waiting, bool Degraded) Waiting(IReadOnlyList<string> zones, IReadOnlyDictionary<string, LaneQueueSample> latest)
    {
        var known = new List<LaneQueueSample>(zones.Count);
        foreach (var zone in zones)
        {
            LaneQueueSample sample = null;
            latest.TryGetValue(zone, out sample);
            if (sample is not null)
                known.Add(sample);
        }

        var withPeople = known.Where(k => k.Length > 0).ToList();
        if (withPeople.Count > 0)
            return ((int)Math.Min(DeskLaneContext.MaxWaiting, known.Sum(k => (long)k.Length)), withPeople.All(k => k.Degraded));
        return known.Count == zones.Count ? (0, known.Any(k => k.Degraded)) : (null, false);
    }
}
