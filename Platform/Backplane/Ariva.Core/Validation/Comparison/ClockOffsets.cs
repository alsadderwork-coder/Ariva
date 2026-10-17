namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// The capturing devices' clock offsets (ARV-104b security review, ARV-104e): each tracer batch's measured offset (device
/// minus server, F19's sign) set against the other batches of the same observer. Per observer the spread (minimum, median,
/// maximum, one value per batch whatever its runs); a batch whose offset differs from its observer's median by more than the
/// bound (Proposed 5 seconds) is flagged: a stale clock reading, or a device whose time was changed during the campaign. Its
/// runs stay in the comparison, flagged (runs are never corrected). Observers are pseudonymous Ariva user ids. Pure.
/// </summary>
public static class ClockOffsets
{
    /// <summary>The middle value, or the mean of the two middle values for an even count; null with none.</summary>
    public static double? Median(IEnumerable<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return null;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + (double)sorted[middle]) / 2;
    }

    /// <summary>Whether a batch's offset lies further than <paramref name="bound"/> from its observer's median (exactly the bound is not).</summary>
    public static bool IsOutlier(int offsetMs, double medianMs, TimeSpan bound) => Math.Abs(offsetMs - medianMs) > bound.TotalMilliseconds;

    /// <summary>Each observer's spread, ordered by observer id, and each batch, ordered by observer and batch id.</summary>
    internal static (IReadOnlyList<ObserverOffsetSpread> Observers, IReadOnlyList<BatchOffset> Batches) Compare(ComparisonData data)
    {
        var bound = data.Settings.OffsetOutlierBound;
        var observers = new List<ObserverOffsetSpread>();
        var batches = new List<BatchOffset>();
        var byObserver = data.Runs.GroupBy(r => r.BatchId)
            .Select(g => (BatchId: g.Key, g.First().ObserverId, g.First().ClockOffsetMs, Runs: g.Count()))
            .GroupBy(b => b.ObserverId)
            .OrderBy(g => g.Key);
        foreach (var observer in byObserver)
        {
            var median = Median(observer.Select(b => b.ClockOffsetMs)).GetValueOrDefault();
            var own = observer.OrderBy(b => b.BatchId)
                .Select(b => new BatchOffset(b.BatchId, observer.Key, b.ClockOffsetMs, b.Runs, b.ClockOffsetMs - median, IsOutlier(b.ClockOffsetMs, median, bound)))
                .ToList();
            batches.AddRange(own);
            observers.Add(new ObserverOffsetSpread(observer.Key, own.Count, own.Sum(b => b.Runs), own.Min(b => b.ClockOffsetMs), median,
                own.Max(b => b.ClockOffsetMs), own.Count(b => b.Outlier)));
        }

        return (observers.AsReadOnly(), batches.AsReadOnly());
    }
}
