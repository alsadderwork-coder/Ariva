namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// Settings of the comparison engine (ARV-104e): the targets of F18 (proposals until the pilot's KPI annex fixes them per
/// campaign) and the Proposed values of the clock offset checks (docs/product/decisions.md).
/// </summary>
public sealed record ComparisonSettings
{
    /// <summary>Count accuracy every judged bin of a line must reach (F18: at least 95 percent).</summary>
    public double CountAccuracyTarget { get; init; } = 0.95;

    /// <summary>
    /// Share of a zone's compared tracer runs that must be within tolerance (Proposed 1.0: every run, the literal reading of
    /// F18's "within max(1 min, 10 percent of the true wait)").
    /// </summary>
    public double WithinToleranceTarget { get; init; } = 1.0;

    /// <summary>Largest magnitude of a zone's bias (F18: within plus or minus 5 percent).</summary>
    public double BiasTarget { get; init; } = 0.05;

    /// <summary>Track completion a zone must reach (F18: at least 90 percent).</summary>
    public double TrackCompletionTarget { get; init; } = 0.90;

    /// <summary>
    /// A batch whose offset differs from its observer's median by more than this is flagged (Proposed 5 seconds: a device set by
    /// network time agrees with itself to well under a second, so seconds of difference mean a stale reading or a clock changed
    /// during the campaign).
    /// </summary>
    public TimeSpan OffsetOutlierBound { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The shift applied to every offset, both ways, for the wait error's sensitivity (Proposed 2 seconds).</summary>
    public TimeSpan SensitivityShift { get; init; } = TimeSpan.FromSeconds(2);

    public IEnumerable<string> Problems()
    {
        if (!IsShare(CountAccuracyTarget))
            yield return "CountAccuracyTarget is above 0 and at most 1.";
        if (!IsShare(WithinToleranceTarget))
            yield return "WithinToleranceTarget is above 0 and at most 1.";
        if (!IsShare(BiasTarget))
            yield return "BiasTarget is above 0 and at most 1.";
        if (!IsShare(TrackCompletionTarget))
            yield return "TrackCompletionTarget is above 0 and at most 1.";
        if (OffsetOutlierBound < TimeSpan.FromMilliseconds(1) || OffsetOutlierBound > TimeSpan.FromMinutes(5))
            yield return "OffsetOutlierBound is from 1 ms to 5 minutes.";
        if (SensitivityShift < TimeSpan.FromMilliseconds(1) || SensitivityShift > TimeSpan.FromMinutes(1))
            yield return "SensitivityShift is from 1 ms to 1 minute.";
    }

    private static bool IsShare(double value) => double.IsFinite(value) && value > 0 && value <= 1;
}
