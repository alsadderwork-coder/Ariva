using Ariva.Core.Queueing;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// Nowcast error against the later realised wait, and the ground-truth proof (formulas F18, ARV-104f). The nowcast stored for
/// minute m is computed at the minute's end for someone joining then (F8), so it is compared with Ariva's final mean realised
/// wait of the people who entered in minute m + 1 (F5 to F7, <c>queue_minute</c>), as F8's reference measurements pair them
/// (Proposed, docs/product/decisions.md). error = nowcast - realised wait (positive when the nowcast overstated). The criterion
/// is the median absolute error over the judged minutes: Good (both minutes and the entrants' wait in stored bins of the
/// campaign's version, final and Good, with no quality interval over them), with a number, and a realised wait under 20
/// minutes. Minutes without service (no number, per reason), Degraded and Unknown minutes and those whose realised wait is 20
/// minutes or more are reported apart with their counts. A nowcast's own F11 flag (the exit term alone, an Unknown desk) does
/// not set a minute apart: it is part of the published number, and is reported beside. The ground-truth proof gives the same
/// error for the shadow nowcast without AMAN inputs (ARV-117, stored since ARV-117a in its own table), with the minutes each
/// covers and both on the minutes both judged; no target. Pure: no I/O, no clock.
/// </summary>
public static class NowcastErrors
{
    #region Formulas

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    /// <summary>The reason a stored name gives, compared exactly (case and spelling); null for anything else, a number or a blank included.</summary>
    public static NoServiceReason? ReasonOf(string name) =>
        name switch
        {
            nameof(NoServiceReason.NothingOpen) => NoServiceReason.NothingOpen,
            nameof(NoServiceReason.ThroughputTooLow) => NoServiceReason.ThroughputTooLow,
            nameof(NoServiceReason.NoThroughputData) => NoServiceReason.NoThroughputData,
            nameof(NoServiceReason.NoQueueLength) => NoServiceReason.NoQueueLength,
            nameof(NoServiceReason.Implausible) => NoServiceReason.Implausible,
            _ => null
        };

    /// <summary>
    /// The largest size an error takes, in minutes (a billion minutes, about 1,900 years; security review of ARV-104f, CWE-501).
    /// A stored nowcast has no upper bound, because a published nowcast of hours is a real error that must count; but a stored
    /// value near <see cref="double.MaxValue"/> (the script checks only that it is at least 0) made the median and the means
    /// Infinity, which no JSON writer serialises. Capped, such an error still counts, and fails any target.
    /// </summary>
    public const double MaxErrorMinutes = 1e9;

    /// <summary>
    /// nowcast - realised wait, in minutes, its size capped at <see cref="MaxErrorMinutes"/>; null when either is not a finite
    /// number at or above 0.
    /// </summary>
    public static double? Error(double nowcastMinutes, double realisedMinutes) =>
        IsWait(nowcastMinutes) && IsWait(realisedMinutes) ? Capped(nowcastMinutes - realisedMinutes) : null;

    /// <summary>Whether a realised wait is under the cut (F18: 20 minutes), compared to 1e-9 minutes; null when it is not a wait.</summary>
    public static bool? IsUnderCut(double realisedMinutes, double cutMinutes) =>
        IsWait(realisedMinutes) && double.IsFinite(cutMinutes) ? Math.Round(realisedMinutes - cutMinutes, 9) < 0 : null;

    /// <summary>
    /// The median: the middle value, or the mean of the two middle values for an even count (halved before they are added, so
    /// two values near <see cref="double.MaxValue"/> never overflow); null with none or a value that is not a finite number.
    /// </summary>
    public static double? Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.ToList();
        if (sorted.Count == 0 || !sorted.All(double.IsFinite))
            return null;
        sorted.Sort();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] / 2) + (sorted[middle] / 2);
    }

    /// <summary>Whether a median absolute error is within the target (F18: 2 minutes), compared to 1e-9 minutes.</summary>
    public static bool IsWithinTarget(double medianMinutes, double targetMinutes) => Math.Round(medianMinutes - targetMinutes, 9) <= 0;

    /// <summary>
    /// The errors' count, median and mean absolute error and mean error (in the order given, so sums are reproducible); each
    /// error's size is capped at <see cref="MaxErrorMinutes"/>, so the statistics stay finite whatever the errors given.
    /// </summary>
    public static NowcastErrorStats Stats(IEnumerable<double> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.Where(double.IsFinite).Select(Capped).ToList();
        if (list.Count == 0)
            return NowcastErrorStats.None;
        return new NowcastErrorStats(list.Count, Median(list.Select(Math.Abs)), list.Sum(Math.Abs) / list.Count, list.Sum() / list.Count);
    }

    private static bool IsWait(double minutes) => double.IsFinite(minutes) && minutes >= 0;

    private static double Capped(double errorMinutes) => Math.Clamp(errorMinutes, -MaxErrorMinutes, MaxErrorMinutes);

    #endregion

    #region Comparison

    /// <summary>
    /// Every nowcast minute of the planned days (<see cref="ComparisonData.NowcastKeys"/>, by zone and minute), each zone's
    /// errors (every zone in scope, by name) and the errors over every zone.
    /// </summary>
    internal static (IReadOnlyList<NowcastMinuteError> Minutes, IReadOnlyList<NowcastZoneErrors> Zones, NowcastZoneErrors Overall) Compare(ComparisonData data)
    {
        var cut = data.Settings.NowcastWaitCutMinutes;
        var items = new List<NowcastMinuteError>();
        foreach (var (zone, minute) in data.NowcastKeys())
        {
            var row = data.MinuteAt(zone, minute);
            var unusable = data.IsUnusableMinute(zone, minute);
            // The pair's standing: the nowcast's own minute (its version), the next minute's realised wait, and the stored results
            // over both minutes and the entrants' mean wait after them. A minute whose own row is unusable, or has none (a shadow
            // alone), cannot be placed under a version: Unknown.
            double? realised = null;
            long waits = 0;
            var standing = ComparisonStanding.Unknown;
            if (!unusable && row is not null)
            {
                (realised, waits, standing) = RealisedAfter(data, zone, minute);
                if (row.ProfileVersion != data.Version)
                    standing = ComparisonData.Worst(standing, ComparisonStanding.OtherVersion);
                standing = ComparisonData.Worst(standing,
                    data.Over(zone, minute, minute + OneMinute + OneMinute + TimeSpan.FromMinutes(realised.GetValueOrDefault()), requireFinal: true));
            }

            var published = unusable ? Unusable()
                : row is { NowcastDegraded: { } flag } ? Reading(standing, row.NowcastMinutes, row.NoService, flag, null, realised)
                : null;
            var shadowRow = data.ShadowAt(zone, minute);
            var shadow = data.IsUnusableShadow(zone, minute) ? Unusable()
                : shadowRow is not null ? Reading(standing, shadowRow.NowcastMinutes, shadowRow.NoService, shadowRow.NowcastDegraded, shadowRow.SensorCycleMinutes, realised)
                : null;
            items.Add(new NowcastMinuteError(data.Version, zone, minute, standing, realised, waits, realised is { } r ? IsUnderCut(r, cut) : null, published, shadow));
        }

        var byZone = items.GroupBy(i => i.QueueZone, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var zones = data.Zones.Select(z => Summarise(data, z.Name, byZone.GetValueOrDefault(z.Name) ?? [])).ToList();
        return (items.AsReadOnly(), zones.AsReadOnly(), Summarise(data, null, items));
    }

    /// <summary>
    /// The final mean realised wait of the people who entered in the minute after <paramref name="minute"/>, how many waits it
    /// holds, and how the minute stands: Unknown when its row is unusable, NoSystemWait without a row, without a minute result
    /// (status null: nobody entered or left) or without waits, OtherVersion, Provisional.
    /// </summary>
    private static (double? Minutes, long Waits, ComparisonStanding Standing) RealisedAfter(ComparisonData data, string zone, DateTime minute)
    {
        var next = minute + OneMinute;
        var row = data.MinuteAt(zone, next);
        if (row is null)
            return (null, 0, data.IsUnusableMinute(zone, next) ? ComparisonStanding.Unknown : ComparisonStanding.NoSystemWait);
        if (row.ProfileVersion != data.Version)
            return (null, 0, ComparisonStanding.OtherVersion);
        if (row.Status is null)
            return (null, 0, ComparisonStanding.NoSystemWait);
        if (row.Status != BinStatus.Final)
            return (null, 0, ComparisonStanding.Provisional);
        return row.MeanWaitMinutes is { } mean ? (mean, row.Waits, ComparisonStanding.Good) : (null, 0, ComparisonStanding.NoSystemWait);
    }

    private static NowcastReading Unusable() => new(ComparisonStanding.Unknown, null, null, null, null, null);

    private static NowcastReading Reading(ComparisonStanding standing, double? nowcast, string noService, bool flagged, double? cycle, double? realised) =>
        new(standing, nowcast, ReasonOf(noService), flagged, cycle, nowcast is { } n && realised is { } r ? Error(n, r) : null);

    private static NowcastZoneErrors Summarise(ComparisonData data, string zone, List<NowcastMinuteError> items)
    {
        var settings = data.Settings;
        var published = Summary(items, i => i.Published, shadow: false);
        var shadow = Summary(items, i => i.Shadow, shadow: true);
        var both = items.Where(i => IsJudged(i, i.Published) && IsJudged(i, i.Shadow)).ToList();
        var paired = new NowcastPairedErrors(both.Count, Stats(both.Select(i => i.Published.ErrorMinutes.GetValueOrDefault())),
            Stats(both.Select(i => i.Shadow.ErrorMinutes.GetValueOrDefault())));
        var median = published.Judged.MedianAbsoluteErrorMinutes;
        var verdict = median is { } m
            ? IsWithinTarget(m, settings.NowcastErrorTargetMinutes) ? CriterionVerdict.Pass : CriterionVerdict.Fail
            : CriterionVerdict.NoData;
        return new NowcastZoneErrors(data.Version, zone, published, shadow, paired, new CriterionCheck(median, settings.NowcastErrorTargetMinutes, verdict));
    }

    /// <summary>A reading the criterion judges: Good, with a number and an error, the realised wait under the cut.</summary>
    private static bool IsJudged(NowcastMinuteError item, NowcastReading reading) =>
        reading is { Standing: ComparisonStanding.Good, ErrorMinutes: not null } && item.UnderCut == true;

    private static NowcastErrorSummary Summary(List<NowcastMinuteError> items, Func<NowcastMinuteError, NowcastReading> of, bool shadow)
    {
        var read = items.Where(i => of(i) is not null).ToList();
        var good = read.Where(i => of(i).Standing == ComparisonStanding.Good).ToList();
        var noService = good.Where(i => of(i).NowcastMinutes is null).ToList();
        var reasons = noService.GroupBy(i => of(i).NoService.GetValueOrDefault()).ToDictionary(g => g.Key, g => g.Count());
        var judged = good.Where(i => IsJudged(i, of(i))).ToList();
        var above = good.Where(i => of(i).ErrorMinutes is not null && i.UnderCut == false).ToList();
        var degraded = read.Where(i => of(i) is { Standing: ComparisonStanding.Degraded, ErrorMinutes: not null } && i.UnderCut == true).ToList();
        double ErrorOf(NowcastMinuteError i) => of(i).ErrorMinutes.GetValueOrDefault();
        return new NowcastErrorSummary(read.Count, StandingTally.Of(read.Select(i => of(i).Standing)), noService.Count, noService.Count(i => i.UnderCut == true),
            Enum.GetValues<NoServiceReason>().Select(r => new NoServiceCount(r, reasons.GetValueOrDefault(r))).ToList().AsReadOnly(),
            Stats(judged.Select(ErrorOf)), Stats(above.Select(ErrorOf)), Stats(judged.Where(i => of(i).Flagged == true).Select(ErrorOf)), Stats(degraded.Select(ErrorOf)),
            shadow ? Stats(judged.Where(i => of(i).SensorCycleMinutes is not null).Select(ErrorOf)) : null,
            read.Count - judged.Count - above.Count);
    }

    #endregion
}
