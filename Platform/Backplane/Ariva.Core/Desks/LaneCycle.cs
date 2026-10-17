using Ariva.Core.Border;

namespace Ariva.Core.Desks;

/// <summary>
/// How the lane cycle time leaves out a desk's idle time (ARV-117d, F10, Proposed; MeanService accepted by the owner on
/// 2026-10-07, CycleCappedAtP90 kept as the switch if pilot data show a long walk-up gap, docs/product/decisions.md).
/// </summary>
public enum LaneCycleMethod
{
    /// <summary>
    /// Each interval's mean service time (transaction start to end, contract V1): no idle time by definition. Leaves out the
    /// walk-up gap as well, so where the gap is long against the service time the desk term is optimistic (D4); built.
    /// </summary>
    MeanService = 0,

    /// <summary>
    /// The alternative, not used: each interval's mean cycle (start to next start while open) bounded by its service times,
    /// max(mean service, min(mean cycle, max(P90 service, mean service))): a gap after a lull longer than a slow transaction is
    /// left out while a walk-up gap under that bound is kept. Without the floor at the mean service the bound is biased
    /// low: with one transaction an interval its P90 is its own service time, so the cap takes the smaller of two
    /// consecutive transactions' times (ARV-117d, docs/product/decisions.md).
    /// </summary>
    CycleCappedAtP90 = 1
}

/// <summary>Why the lane has no cycle time from AMAN's interval statistics.</summary>
public enum LaneCycleFallback
{
    /// <summary>No interval statistics were read for the lane: the desk term keeps its own fallback (F8).</summary>
    NoIntervals,

    /// <summary>Intervals were read but no document was processed in them (desks idle, or all left out).</summary>
    NoDocuments
}

/// <summary>
/// One desk's one-minute interval statistics as the lane cycle time reads them (AMAN contract V1 <c>DeskIntervalStats</c>):
/// counts and timings only, no desk key or identity.
/// </summary>
public sealed record DeskIntervalSample(int Transactions, int Documents, double MeanServiceSeconds, double P90ServiceSeconds, double MeanCycleSeconds)
{
    /// <summary>
    /// Whether the values are within contract V1's bounds (<see cref="ImmigrationRules"/>, CWE-501): counts from 0 to
    /// 10,000, transactions at most documents and both 0 or neither, every time a finite number of seconds from 0 to 3,600.
    /// </summary>
    public bool WithinContract =>
        Transactions is >= 0 and <= ImmigrationRules.MaxCount && Documents is >= 0 and <= ImmigrationRules.MaxCount &&
        Transactions <= Documents && (Documents == 0) == (Transactions == 0) &&
        Seconds(MeanServiceSeconds) && Seconds(P90ServiceSeconds) && Seconds(MeanCycleSeconds);

    private static bool Seconds(double value) => double.IsFinite(value) && value is >= 0 and <= ImmigrationRules.MaxSeconds;
}

/// <summary>
/// The lane cycle time per person (F10, ARV-117d) in minutes, or null with <see cref="Missing"/> saying why;
/// <see cref="Refused"/> intervals failed contract V1's bounds or carried transactions with no time, were left out and flag
/// the desk term (<see cref="Flagged"/>).
/// </summary>
public sealed record LaneCycleResult(double? Minutes, int Intervals, int Refused, long Documents, LaneCycleFallback? Missing)
{
    /// <summary>The result is flagged when any interval was left out (the lane's time is partly unknown).</summary>
    public bool Flagged => Refused > 0;
}

/// <summary>
/// The lane cycle time of F8's desk term from AMAN's per-desk interval statistics (F10, ARV-117d, Proposed): per person,
/// in working time.
/// <code>
/// c_lane = sum over intervals of (t_i x T_i) / sum over intervals of D_i / 60      (minutes per person)
/// t_i    = MeanService_i                                                           (MeanService, built)
///        = max(MeanService_i, min(MeanCycle_i, max(P90Service_i, MeanService_i)))  (CycleCappedAtP90, the alternative)
/// </code>
/// T_i is the interval's transactions (approaches: a family processed together is one) and D_i its documents (people):
/// F8's c is per person because Q counts people. t_i x T_i is the interval's working seconds; dividing by the documents
/// gives seconds per person. Every interval is checked against contract V1's bounds before use (CWE-501); one that fails,
/// or has transactions with a working time of 0, is left out and counted, and flags the result. Intervals with no
/// transactions carry no weight. Pure.
/// </summary>
public static class LaneCycle
{
    /// <summary>The lane cycle time per person in working time over the given intervals.</summary>
    public static LaneCycleResult PerPerson(IEnumerable<DeskIntervalSample> intervals, LaneCycleMethod method = LaneCycleMethod.MeanService)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (!Enum.IsDefined(method))
            throw new ArgumentOutOfRangeException(nameof(method), "Unknown lane cycle method.");
        double working = 0;
        long documents = 0;
        var count = 0;
        var refused = 0;
        foreach (var i in intervals)
        {
            count++;
            if (i is null || !i.WithinContract)
            {
                refused++;
                continue;
            }

            if (i.Transactions == 0)
                continue;
            var seconds = method == LaneCycleMethod.MeanService
                ? i.MeanServiceSeconds
                : Math.Max(i.MeanServiceSeconds, Math.Min(i.MeanCycleSeconds, Math.Max(i.P90ServiceSeconds, i.MeanServiceSeconds)));
            if (seconds <= 0)
            {
                refused++;
                continue;
            }

            working += seconds * i.Transactions;
            documents += i.Documents;
        }

        if (count == 0)
            return new LaneCycleResult(null, 0, 0, 0, LaneCycleFallback.NoIntervals);
        return documents > 0
            ? new LaneCycleResult(working / documents / 60, count, refused, documents, null)
            : new LaneCycleResult(null, count, refused, 0, LaneCycleFallback.NoDocuments);
    }

    /// <summary>
    /// The transaction-weighted mean of per-interval cycle seconds, in minutes: c_lane as ARV-064 read it (per transaction,
    /// idle time included), no longer the published reading since ARV-117d. Kept for the comparisons that measure what the
    /// change does; intervals with no weight or a cycle that is not finite, not positive or above an hour are left out.
    /// </summary>
    public static double? PerTransaction(IEnumerable<(double MeanCycleSeconds, int Transactions)> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        double weighted = 0;
        long transactions = 0;
        foreach (var (cycle, n) in intervals)
        {
            if (n <= 0 || !double.IsFinite(cycle) || cycle <= 0 || cycle > ImmigrationRules.MaxSeconds)
                continue;
            weighted += cycle * n;
            transactions += n;
        }

        return transactions > 0 ? weighted / transactions / 60 : null;
    }
}
