using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// Count accuracy (formulas F18, ARV-104e): per line, 15-minute bin and direction, Ariva's crossings (N_system, line_minute of
/// the campaign's version) against the observers' manual counts (N_manual). Rules Proposed for the owner
/// (docs/product/decisions.md): N_manual is the mean of the observers' latest counts, with their largest difference reported;
/// an entry or overflow entry line is compared on its crossings in, an exit line on its crossings out, a count line on both
/// (two items); the accuracy is floored at 0 (a system count above twice the manual one), the absolute error always reported;
/// a line meets the target when every judged bin does. A line and bin whose counts cannot all be used is not judged, and a
/// line's row of the campaign's version that cannot be used leaves N_system unknown (security review, CWE-501). Pure: no I/O,
/// no clock.
/// </summary>
public static class CountAccuracy
{
    #region Formulas

    /// <summary>|N_system - N_manual|; null when either is not a finite number at or above 0.</summary>
    public static double? AbsoluteError(double system, double manual) =>
        IsCount(system) && IsCount(manual) ? Math.Abs(system - manual) : null;

    /// <summary>
    /// 1 - |N_system - N_manual| / N_manual, floored at 0 and so never outside 0 to 1; null when N_manual is 0 (F18: report the
    /// absolute error instead) or either is not a finite number at or above 0.
    /// </summary>
    public static double? Accuracy(double system, double manual) =>
        IsCount(system) && IsCount(manual) && manual > 0 ? Math.Max(0, 1 - (Math.Abs(system - manual) / manual)) : null;

    /// <summary>The directions compared for a line's role: In for entry and overflow entry lines, Out for exit lines, both for count lines.</summary>
    public static IReadOnlyList<CrossingDirection> Directions(LineRole role) =>
        role switch
        {
            LineRole.Exit => [CrossingDirection.Out],
            LineRole.Count => [CrossingDirection.In, CrossingDirection.Out],
            _ => [CrossingDirection.In]
        };

    /// <summary>Whether a value reaches a target, compared to 1e-9 (formulas, conventions).</summary>
    internal static bool AtLeast(double value, double target) => Math.Round(value - target, 9) >= 0;

    private static bool IsCount(double value) => double.IsFinite(value) && value >= 0;

    #endregion

    #region Comparison

    /// <summary>Every line, bin and direction with a manual count, and each line's summary (every line in scope, with or without counts).</summary>
    internal static (IReadOnlyList<LineBinAccuracy> Bins, IReadOnlyList<LineAccuracy> Lines) Compare(ComparisonData data)
    {
        var items = new List<LineBinAccuracy>();
        var binStandings = new Dictionary<Guid, List<ComparisonStanding>>();
        var groups = data.Counts.GroupBy(c => (c.LineId, c.BinStartUtc))
            .Select(g => (Line: data.LineById[g.Key.LineId], Bin: g.Key.BinStartUtc, Observers: g.ToList()))
            .OrderBy(g => g.Line.QueueZone, StringComparer.Ordinal).ThenBy(g => g.Line.Name, StringComparer.Ordinal).ThenBy(g => g.Bin);
        foreach (var (line, bin, observers) in groups)
        {
            var stored = data.LineBinAt(line.QueueZone, line.Name, bin);
            var standing = data.Over(line.QueueZone, bin, bin + ComparisonData.BinLength, requireFinal: true);
            if (stored.OtherVersion)
                standing = ComparisonData.Worst(standing, ComparisonStanding.OtherVersion);
            // The line's row of the campaign's version is refused or conflicting: its crossings are not known, never 0.
            if (stored.Unusable)
                standing = ComparisonData.Worst(standing, ComparisonStanding.Unknown);
            // N_system exists when a stored bin of the campaign's version covers the bin; a line without rows there crossed 0 times.
            var measured = standing != ComparisonStanding.OtherVersion && !stored.Unusable && data.BinAt(line.QueueZone, bin) is not null;
            if (!binStandings.TryGetValue(line.LineId, out var standings))
                binStandings[line.LineId] = standings = [];
            standings.Add(standing);

            foreach (var direction in Directions(line.Role))
            {
                // Whole counts: their sum, largest and smallest do not depend on the observers' order.
                var counts = observers.Select(o => direction == CrossingDirection.In ? o.CrossingsIn : o.CrossingsOut).ToList();
                var manual = (double)counts.Sum(c => (long)c) / counts.Count;
                double? difference = counts.Count > 1 ? counts.Max() - counts.Min() : null;
                long? system = measured ? (direction == CrossingDirection.In ? stored.In : stored.Out) : null;
                var error = system is { } s ? AbsoluteError(s, manual) : null;
                var accuracy = system is { } t ? Accuracy(t, manual) : null;
                bool? meets = accuracy is { } a ? AtLeast(a, data.Settings.CountAccuracyTarget) : null;
                items.Add(new LineBinAccuracy(data.Version, line.QueueZone, line.LineId, line.Name, line.Role, bin, direction, standing, counts.Count, manual,
                    difference, system, error, accuracy, meets));
            }
        }

        var byLine = items.GroupBy(i => i.LineId).ToDictionary(g => g.Key, g => g.ToList());
        var lines = data.Lines.Select(line => Summarise(data, line, byLine.GetValueOrDefault(line.LineId) ?? [],
            binStandings.GetValueOrDefault(line.LineId) ?? [], data.UnusableCountBins.GetValueOrDefault(line.LineId))).ToList();
        return (items.AsReadOnly(), lines.AsReadOnly());
    }

    private static LineAccuracy Summarise(ComparisonData data, ScopeLine line, List<LineBinAccuracy> items, List<ComparisonStanding> bins, int leftOutBins)
    {
        var target = data.Settings.CountAccuracyTarget;
        var good = items.Where(i => i.Standing == ComparisonStanding.Good).ToList();
        var judged = good.Where(i => i.Accuracy is not null).ToList();
        var zero = good.Where(i => i.ManualCount == 0).ToList();
        var passing = judged.Count(i => i.MeetsTarget == true);
        double? lowest = judged.Count > 0 ? judged.Min(i => i.Accuracy.GetValueOrDefault()) : null;
        double? pooled = judged.Count > 0
            ? Math.Max(0, 1 - (judged.Sum(i => i.AbsoluteError.GetValueOrDefault()) / judged.Sum(i => i.ManualCount)))
            : null;
        var verdict = judged.Count == 0 ? CriterionVerdict.NoData
            : passing == judged.Count ? CriterionVerdict.Pass
            : CriterionVerdict.Fail;
        var tally = StandingTally.Of(bins);
        // Bins, not items: a count line's two directions are one bin, as the campaign's target of bins per line counts them.
        var judgedBins = judged.Select(i => i.BinStartUtc).Distinct().Count();
        return new LineAccuracy(data.Version, line.QueueZone, line.LineId, line.Name, line.Role, judged.Count, passing, judgedBins,
            tally.Total - tally.Good + leftOutBins, lowest, pooled, zero.Count, zero.Sum(i => i.AbsoluteError.GetValueOrDefault()), tally,
            new CriterionCheck(lowest, target, verdict));
    }

    #endregion
}
