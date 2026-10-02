namespace Ariva.Core.Queueing;

/// <summary>
/// Wait statistics of formula F7, arrival-weighted: every passenger counts once, whichever minute they entered in.
/// Weighted nearest rank: sort by wait, the smallest wait whose cumulative weight reaches p times the total; for
/// unweighted waits this is the ceil(p * n)-th smallest. Never averages percentiles.
/// </summary>
public static class WaitStatistics
{
    /// <summary>Histogram bucket width (F7, Proposed: 30-second buckets).</summary>
    public static readonly TimeSpan BucketWidth = TimeSpan.FromSeconds(30);

    /// <summary>Buckets up to 24 hours plus one for anything longer; percentiles merged from histograms are exact to a bucket.</summary>
    public const int Buckets = 24 * 60 * 2 + 1;

    /// <summary>The weighted nearest-rank percentile (p in (0, 1]); null when there is no weight.</summary>
    public static double? Percentile(IReadOnlyList<(double Wait, double Weight)> samples, double p)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (p is <= 0 or > 1 || double.IsNaN(p))
            throw new ArgumentOutOfRangeException(nameof(p), "A percentile is in (0, 1].");
        var sorted = samples.Where(s => s.Weight > 0 && double.IsFinite(s.Wait)).OrderBy(s => s.Wait).ToList();
        var total = sorted.Sum(s => s.Weight);
        if (total <= 0)
            return null;
        double cumulative = 0;
        foreach (var (wait, weight) in sorted)
        {
            cumulative += weight;
            // A relative tolerance keeps an exact share (0.9 x 12 = 10.8) from missing its rank by one ulp (formulas,
            // rounding) without skipping a rank when the weights are tiny.
            if (cumulative >= p * total * (1 - 1e-12))
                return wait;
        }

        return sorted[^1].Wait;
    }

    /// <summary>The nearest-rank percentile of unweighted waits, in minutes: the ceil(p * n)-th smallest.</summary>
    public static double? Percentile(IReadOnlyList<double> waits, double p) =>
        Percentile(waits?.Select(w => (w, 1.0)).ToList() ?? throw new ArgumentNullException(nameof(waits)), p);

    /// <summary>The weighted mean; null when there is no weight.</summary>
    public static double? Mean(IReadOnlyList<(double Wait, double Weight)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var valid = Valid(samples);
        var total = valid.Sum(s => s.Weight);
        return total > 0 ? valid.Sum(s => s.Weight * s.Wait) / total : null;
    }

    private static List<(double Wait, double Weight)> Valid(IReadOnlyList<(double Wait, double Weight)> samples) =>
        [.. samples.Where(s => s.Weight > 0 && double.IsFinite(s.Weight) && double.IsFinite(s.Wait))];

    /// <summary>
    /// Nearest-rank percentiles of unweighted waits (the ceil(p * n)-th smallest), all from one sort; nulls when empty.
    /// </summary>
    public static double?[] Percentiles(IEnumerable<double> waits, params double[] ps)
    {
        ArgumentNullException.ThrowIfNull(waits);
        ArgumentNullException.ThrowIfNull(ps);
        var sorted = waits.Where(double.IsFinite).ToArray();
        Array.Sort(sorted);
        var o = new double?[ps.Length];
        for (var k = 0; k < ps.Length; k++)
        {
            if (ps[k] is <= 0 or > 1 || double.IsNaN(ps[k]))
                throw new ArgumentOutOfRangeException(nameof(ps), "A percentile is in (0, 1].");
            if (sorted.Length > 0)
                o[k] = sorted[(int)Math.Max(0, Math.Ceiling(Math.Round(ps[k] * sorted.Length, 9)) - 1)];
        }

        return o;
    }

    /// <summary>The share of weight with a wait of at most <paramref name="target"/>; null when there is no weight.</summary>
    public static double? Share(IReadOnlyList<(double Wait, double Weight)> samples, double target)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var valid = Valid(samples);
        var total = valid.Sum(s => s.Weight);
        return total > 0 ? valid.Where(s => s.Wait <= target).Sum(s => s.Weight) / total : null;
    }

    /// <summary>The histogram bucket of a wait: 30-second buckets, the last one open-ended.</summary>
    public static int BucketOf(TimeSpan wait) =>
        wait <= TimeSpan.Zero ? 0 : (int)Math.Min(Buckets - 1, wait.Ticks / BucketWidth.Ticks);

    /// <summary>
    /// The nearest-rank percentile from a sparse histogram (bucket, count) in bucket order, in minutes, read as the upper
    /// edge of the bucket that reaches the rank (exact to the bucket width; the open-ended bucket reads as its lower
    /// edge). Null for an empty histogram.
    /// </summary>
    public static double? Percentile(IReadOnlyList<(int Bucket, long Count)> histogram, double p)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        if (p is <= 0 or > 1 || double.IsNaN(p))
            throw new ArgumentOutOfRangeException(nameof(p), "A percentile is in (0, 1].");
        long total = 0;
        var previous = -1;
        foreach (var (bucket, count) in histogram)
        {
            if (bucket <= previous || bucket >= Buckets || count < 0)
                throw new ArgumentException("A histogram lists buckets in ascending order, each once, with counts that are not negative.", nameof(histogram));
            previous = bucket;
            total = checked(total + count);
        }

        if (total == 0)
            return null;
        var rank = (long)Math.Ceiling(Math.Round(p * total, 9));
        long cumulative = 0;
        foreach (var (bucket, count) in histogram)
        {
            cumulative += count;
            if (cumulative >= rank)
                return (bucket == Buckets - 1 ? bucket : bucket + 1) * BucketWidth.TotalMinutes;
        }

        return null;
    }

    /// <summary>Adds sparse histograms (merging bins into hours or days, F7): bucket order, counts summed.</summary>
    public static IReadOnlyList<(int Bucket, long Count)> Merge(params IReadOnlyList<(int Bucket, long Count)>[] histograms)
    {
        ArgumentNullException.ThrowIfNull(histograms);
        var merged = new SortedDictionary<int, long>();
        // Counts are 64-bit: merging a year of busy bins cannot overflow.
        foreach (var histogram in histograms)
        {
            foreach (var (bucket, count) in histogram ?? [])
            {
                if (bucket is < 0 or >= Buckets || count < 0)
                    throw new ArgumentException("A histogram bucket is from 0 to the last bucket and its count is not negative.", nameof(histograms));
                merged[bucket] = checked(merged.GetValueOrDefault(bucket) + count);
            }
        }

        return [.. merged.Select(kv => (kv.Key, kv.Value))];
    }
}
