using Ariva.Core.Queueing;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// Tracer wait error and bias (formulas F18, ARV-104e). A tracer's wait is its corrected exit minus its corrected join; the
/// system's wait it is compared against is the matching rule <see cref="SystemWaitAt"/> (Proposed for the owner,
/// docs/product/decisions.md): Ariva's final realised wait of the people who entered the same queue zone at the tracer's
/// corrected join time, read from the stored minutes. error = w_system - w_tracer, within tolerance when |error| &lt;=
/// max(1 min, 10 percent of w_tracer); bias = sum(w_system - w_tracer) / sum(w_tracer). Abandoned runs have no realised wait
/// (F5): left out of the error and the bias and counted apart (owner decision 2026-10-08). Runs are never corrected. Pure: no
/// I/O, no clock.
/// </summary>
public static class TracerWaits
{
    #region Formulas

    /// <summary>The floor of the tolerance (F18): 1 minute.</summary>
    public const double ToleranceFloorMinutes = 1;

    /// <summary>The tolerance's share of the tracer's wait (F18): 10 percent.</summary>
    public const double ToleranceShare = 0.10;

    private static readonly TimeSpan HalfMinute = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    /// <summary>max(1 min, 0.10 x w_tracer); null when the wait is not a finite number at or above 0.</summary>
    public static double? Tolerance(double tracerMinutes) =>
        IsWait(tracerMinutes) ? Math.Max(ToleranceFloorMinutes, ToleranceShare * tracerMinutes) : null;

    /// <summary>|error| &lt;= max(1 min, 0.10 x w_tracer), compared to 1e-9 minutes; null when either is not a finite number (or the wait is below 0).</summary>
    public static bool? IsWithin(double errorMinutes, double tracerMinutes) =>
        double.IsFinite(errorMinutes) && Tolerance(tracerMinutes) is { } tolerance ? Math.Round(Math.Abs(errorMinutes) - tolerance, 9) <= 0 : null;

    /// <summary>
    /// bias = sum(w_system - w_tracer) / sum(w_tracer): positive when the system overstates the waits, negative when it
    /// understates them. Null with no pair, a pair that is not two finite waits at or above 0, or no tracer time at all.
    /// </summary>
    public static double? Bias(IEnumerable<(double SystemMinutes, double TracerMinutes)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var list = pairs.ToList();
        return list.All(p => IsWait(p.SystemMinutes) && IsWait(p.TracerMinutes)) ? BiasOf(list.Select(p => (p.SystemMinutes - p.TracerMinutes, p.TracerMinutes))) : null;
    }

    /// <summary>
    /// The matching rule (Proposed): the system's realised wait for someone who joined <paramref name="zone"/> at
    /// <paramref name="joinedUtc"/>. Each stored minute's mean realised wait (the people who entered in it, F5 to F7) stands at
    /// the minute's centre; the wait at the join time is the straight line between the centres around it. A neighbour minute
    /// that is not final, of another version or without waits, or whose stored results are not Good and final (its bin, a
    /// quality interval over it: <see cref="ComparisonData.Over"/>), is not used, and on that side the join minute's own mean
    /// holds (security review: a neighbour in an earlier bin, under an outage, must not weigh in while the run stays Good).
    /// The join minute itself must be final, of the campaign's version, with at least one realised wait; otherwise there is no
    /// wait and the standing says why (no row or no waits: <see cref="ComparisonStanding.NoSystemWait"/>; a row refused or
    /// conflicting: <see cref="ComparisonStanding.Unknown"/>). The standing also covers the join minute whole, from its start
    /// (its mean includes people who entered before the join): the minutes read count, not only the tracer's own wait.
    /// </summary>
    internal static (double? Minutes, ComparisonStanding Standing) SystemWaitAt(ComparisonData data, string zone, DateTime joinedUtc)
    {
        var minute = new DateTime(joinedUtc.Ticks - (joinedUtc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
        var read = data.Over(zone, minute, minute + OneMinute, requireFinal: false);
        var row = data.MinuteAt(zone, minute);
        if (row is null)
            return (null, ComparisonData.Worst(read, data.IsUnusableMinute(zone, minute) ? ComparisonStanding.Unknown : ComparisonStanding.NoSystemWait));
        if (row.ProfileVersion != data.Version)
            return (null, ComparisonData.Worst(read, ComparisonStanding.OtherVersion));
        if (row.Status != BinStatus.Final)
            return (null, ComparisonData.Worst(read, ComparisonStanding.Provisional));
        if (row.MeanWaitMinutes is not { } mean)
            return (null, ComparisonData.Worst(read, ComparisonStanding.NoSystemWait));

        var fromCentre = (double)(joinedUtc - (minute + HalfMinute)).Ticks / TimeSpan.TicksPerMinute;
        var neighbour = UsableMean(data, zone, fromCentre >= 0 ? minute + OneMinute : minute - OneMinute);
        return (neighbour is { } other ? mean + ((other - mean) * Math.Abs(fromCentre)) : mean, read);
    }

    internal static double? BiasOf(IEnumerable<(double Error, double Tracer)> runs)
    {
        double errors = 0, tracer = 0;
        foreach (var (error, wait) in runs)
        {
            errors += error;
            tracer += wait;
        }

        return tracer > 0 ? errors / tracer : null;
    }

    /// <summary>A neighbour minute's mean: final, of the campaign's version, with waits, and Good and final stored results over it; null otherwise.</summary>
    private static double? UsableMean(ComparisonData data, string zone, DateTime minute) =>
        data.MinuteAt(zone, minute) is { Status: BinStatus.Final, MeanWaitMinutes: { } mean } row && row.ProfileVersion == data.Version &&
        data.Over(zone, minute, minute + OneMinute, requireFinal: true) == ComparisonStanding.Good
            ? mean
            : null;

    /// <summary>The error at a shifted join: the same rule, so a read whose minutes are not Good gives no error (null).</summary>
    private static double? ShiftedError(ComparisonData data, string zone, DateTime joinedUtc, double tracerMinutes) =>
        SystemWaitAt(data, zone, joinedUtc) is ({ } wait, ComparisonStanding.Good) ? wait - tracerMinutes : null;

    private static bool IsWait(double minutes) => double.IsFinite(minutes) && minutes >= 0;

    private static double MinutesOf(TimeSpan value) => (double)value.Ticks / TimeSpan.TicksPerMinute;

    #endregion

    #region Comparison

    /// <summary>
    /// Every usable run against the system, ordered by zone, join time, tracer code and id. A run's standing covers its wait
    /// [joined, exited) and the minutes the matching rule read. A shift of every offset by +s moves the corrected join and exit
    /// by -s (t_server = t_device - offset) and leaves the tracer's wait unchanged, so the error at a shifted offset is the
    /// system's wait at the shifted join minus the same tracer wait, known only when the minutes read there are Good.
    /// </summary>
    internal static IReadOnlyList<TracerComparison> Compare(ComparisonData data, IReadOnlySet<Guid> outlierBatches)
    {
        var shift = data.Settings.SensitivityShift;
        var runs = new List<TracerComparison>();
        foreach (var run in data.Runs.OrderBy(r => data.ZoneById[r.ZoneId].Name, StringComparer.Ordinal).ThenBy(r => r.JoinedUtc)
                     .ThenBy(r => r.TracerCode, StringComparer.Ordinal).ThenBy(r => r.RunId))
        {
            var zone = data.ZoneById[run.ZoneId].Name;
            var tracer = MinutesOf(run.ExitedUtc - run.JoinedUtc);
            var standing = data.Over(zone, run.JoinedUtc, run.ExitedUtc, requireFinal: false);
            double? system = null, error = null, minus = null, plus = null;
            bool? within = null;
            if (!run.Abandoned)
            {
                var (wait, matched) = SystemWaitAt(data, zone, run.JoinedUtc);
                standing = ComparisonData.Worst(standing, matched);
                if (wait is { } w)
                {
                    system = w;
                    error = w - tracer;
                    within = IsWithin(w - tracer, tracer);
                    minus = ShiftedError(data, zone, run.JoinedUtc + shift, tracer);
                    plus = ShiftedError(data, zone, run.JoinedUtc - shift, tracer);
                }
            }

            runs.Add(new TracerComparison(data.Version, run.RunId, run.BatchId, run.ObserverId, run.TracerCode, zone, run.JoinedUtc, run.ExitedUtc,
                run.ClockOffsetMs, outlierBatches.Contains(run.BatchId), run.Abandoned, standing, tracer, system, error,
                Tolerance(tracer).GetValueOrDefault(), within, minus, plus));
        }

        return runs.AsReadOnly();
    }

    /// <summary>The summary of a zone's runs, or of every run with <paramref name="zone"/> null.</summary>
    internal static TracerZoneSummary Summarise(ComparisonData data, string zone, IReadOnlyList<TracerComparison> runs)
    {
        var settings = data.Settings;
        var waiting = runs.Where(r => !r.Abandoned).ToList();
        var compared = waiting.Where(r => r.Standing == ComparisonStanding.Good && r.ErrorMinutes is not null).ToList();
        var within = compared.Count(r => r.WithinTolerance == true);
        var bias = BiasOf(compared.Select(r => (r.ErrorMinutes.GetValueOrDefault(), r.TracerWaitMinutes)));
        double? mean = compared.Count > 0 ? compared.Sum(r => r.ErrorMinutes.GetValueOrDefault()) / compared.Count : null;
        double? largest = compared.Count > 0 ? compared.Max(r => Math.Abs(r.ErrorMinutes.GetValueOrDefault())) : null;

        // Abandoned runs beside the system's own abandonment (F6) in the final bins of the campaign's version their joins fall in.
        var abandoned = runs.Where(r => r.Abandoned).ToList();
        var bins = abandoned.Select(r => (r.QueueZone, Bin: ComparisonData.BinOf(r.JoinedUtc))).Distinct()
            .Select(k => data.BinAt(k.QueueZone, k.Bin))
            .Where(b => b is { Status: BinStatus.Final } && b.ProfileVersion == data.Version)
            .ToList();

        double? share = compared.Count > 0 ? (double)within / compared.Count : null;
        var errorCheck = new CriterionCheck(share, settings.WithinToleranceTarget, share is { } s
            ? CountAccuracy.AtLeast(s, settings.WithinToleranceTarget) ? CriterionVerdict.Pass : CriterionVerdict.Fail
            : CriterionVerdict.NoData);
        var biasCheck = new CriterionCheck(bias, settings.BiasTarget, bias is { } b
            ? Math.Round(Math.Abs(b) - settings.BiasTarget, 9) <= 0 ? CriterionVerdict.Pass : CriterionVerdict.Fail
            : CriterionVerdict.NoData);
        return new TracerZoneSummary(data.Version, zone, runs.Count, compared.Count, waiting.Count - compared.Count, within, bias, mean, largest,
            StandingTally.Of(waiting.Select(r => r.Standing)),
            runs.Count(r => r.OffsetOutlier), abandoned.Count, bins.Count, bins.Sum(b => b.Abandoned), bins.Sum(b => b.Entries),
            Sensitivity(settings.SensitivityShift, compared), errorCheck, biasCheck);
    }

    private static WaitSensitivity Sensitivity(TimeSpan shift, List<TracerComparison> compared)
    {
        var runs = compared.Where(r => r.ErrorOffsetMinusMinutes is not null && r.ErrorOffsetPlusMinutes is not null).ToList();
        double? largest = runs.Count > 0
            ? runs.Max(r => Math.Max(Math.Abs(r.ErrorOffsetMinusMinutes.GetValueOrDefault() - r.ErrorMinutes.GetValueOrDefault()),
                Math.Abs(r.ErrorOffsetPlusMinutes.GetValueOrDefault() - r.ErrorMinutes.GetValueOrDefault())))
            : null;
        return new WaitSensitivity(shift, runs.Count,
            BiasOf(runs.Select(r => (r.ErrorOffsetMinusMinutes.GetValueOrDefault(), r.TracerWaitMinutes))),
            BiasOf(runs.Select(r => (r.ErrorMinutes.GetValueOrDefault(), r.TracerWaitMinutes))),
            BiasOf(runs.Select(r => (r.ErrorOffsetPlusMinutes.GetValueOrDefault(), r.TracerWaitMinutes))),
            runs.Count(r => IsWithin(r.ErrorOffsetMinusMinutes.GetValueOrDefault(), r.TracerWaitMinutes) == true),
            runs.Count(r => r.WithinTolerance == true),
            runs.Count(r => IsWithin(r.ErrorOffsetPlusMinutes.GetValueOrDefault(), r.TracerWaitMinutes) == true),
            largest);
    }

    #endregion
}
