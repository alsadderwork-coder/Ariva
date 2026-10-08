using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// How a compared item (a line's bin and direction, a tracer run, a zone's bin, a nowcast minute, an observed desk minute)
/// stands against the stored outputs (ARV-104e, ARV-104f, F18, F11). Only <see cref="Good"/> items enter a criterion's value,
/// except in the desk-state agreement, whose Degraded and Unknown minutes count (Unknown as disagreement, F18); every other
/// one is reported apart with its count, never dropped. The order is the precedence: when several apply, the later one wins.
/// </summary>
public enum ComparisonStanding
{
    /// <summary>Final, Good quality, the campaign's profile version, covered by stored results: compared.</summary>
    Good,

    /// <summary>
    /// A tracer whose entry minute is final and Good but holds no realised wait: the system has no wait to compare. For a
    /// nowcast minute (ARV-104f): the next minute, whose entrants' realised wait the nowcast is compared with, holds none
    /// (nobody entered then, or no row was stored for it).
    /// </summary>
    NoSystemWait,

    /// <summary>A stored result or a quality interval over the item's time is Degraded (a device outage, a corrected clock).</summary>
    Degraded,

    /// <summary>
    /// A stored result over the item's time is Unknown, missing or unusable (its latest revision refused or conflicting,
    /// <see cref="UnusableKey"/>), or an Unknown quality interval overlaps it.
    /// </summary>
    Unknown,

    /// <summary>A stored result the item needs is not final yet; the comparison waits for it.</summary>
    Provisional,

    /// <summary>A stored result the item needs was computed with another profile version: not comparable.</summary>
    OtherVersion
}

/// <summary>A criterion's verdict for one scope (a line or a zone) against its target.</summary>
public enum CriterionVerdict
{
    /// <summary>Nothing to judge: no Good item with a value (never a division by zero).</summary>
    NoData,
    Pass,
    Fail
}

/// <summary>Why the engine compared nothing.</summary>
public enum ComparisonProblem
{
    /// <summary>The scope is missing or inconsistent (zones, lines or windows).</summary>
    InvalidScope,

    /// <summary>An input list or the windows exceed the engine's bounds (CWE-120).</summary>
    InputTooLarge
}

/// <summary>A criterion's value for one scope, its target and the verdict; <see cref="Value"/> is null with <see cref="CriterionVerdict.NoData"/>.</summary>
public sealed record CriterionCheck(double? Value, double Target, CriterionVerdict Verdict);

/// <summary>How many items stood where (<see cref="ComparisonStanding"/>): the Degraded and Unknown ones are counted apart, never dropped.</summary>
public sealed record StandingTally(int Good, int NoSystemWait, int Degraded, int Unknown, int Provisional, int OtherVersion)
{
    public static readonly StandingTally None = new(0, 0, 0, 0, 0, 0);

    public int Total => Good + NoSystemWait + Degraded + Unknown + Provisional + OtherVersion;

    public static StandingTally Of(IEnumerable<ComparisonStanding> standings)
    {
        ArgumentNullException.ThrowIfNull(standings);
        var counts = new int[6];
        foreach (var standing in standings)
            counts[(int)standing]++;
        return new StandingTally(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]);
    }
}

/// <summary>
/// Count accuracy of one line, 15-minute bin and direction (F18): N_manual (the mean of the observers' latest counts, with
/// their largest difference when two or more counted), N_system (Ariva's crossings of the campaign's version; null when no
/// stored result covers the bin, or the line's row of the campaign's version is unusable), the absolute error, and the
/// accuracy 1 - |N_system - N_manual| / N_manual floored at 0 (null when N_manual is 0: the absolute error stands instead).
/// </summary>
public sealed record LineBinAccuracy(
    int ProfileVersion,
    string QueueZone,
    Guid LineId,
    string LineName,
    LineRole Role,
    DateTime BinStartUtc,
    CrossingDirection Direction,
    ComparisonStanding Standing,
    int Observers,
    double ManualCount,
    double? ObserverDifference,
    long? SystemCount,
    double? AbsoluteError,
    double? Accuracy,
    bool? MeetsTarget);

/// <summary>
/// Count accuracy of one line over the campaign (F18 "per 15-minute bin, each line"): the Good items judged (N_manual above
/// 0), how many met the target, the bins judged (Good, with at least one judged item) and excluded (not Good, or not judged
/// because an observer's count of them is unusable, <see cref="UnusableKey"/>), the lowest and the pooled accuracy (1 - sum
/// |error| / sum N_manual), the Good items with N_manual 0 and their summed absolute error, the bins by standing, and the
/// verdict: every judged item at or above the target. The verdict judges only the items the line holds, so it is not an
/// acceptance verdict on its own: the campaign's verdict (ARV-104g) also needs the campaign's target of judged bins per line
/// and shows the excluded share.
/// </summary>
public sealed record LineAccuracy(
    int ProfileVersion,
    string QueueZone,
    Guid LineId,
    string LineName,
    LineRole Role,
    int Judged,
    int Passing,
    int JudgedBins,
    int ExcludedBins,
    double? LowestAccuracy,
    double? PooledAccuracy,
    int ManualZero,
    double ManualZeroAbsoluteError,
    StandingTally Bins,
    CriterionCheck Check);

/// <summary>
/// One tracer run against the system (F18): the tracer's wait (corrected exit minus corrected join), the system's realised
/// wait at the corrected join time (the matching rule, <see cref="TracerWaits.SystemWaitAt"/>), the error w_system -
/// w_tracer, the tolerance max(1 min, 10 percent of w_tracer) and whether the error is within it, and the error again with
/// every offset lowered and raised by the sensitivity shift (null when the minutes read at the shifted join are not Good).
/// Abandoned runs carry no system wait and no error. The batch's offset is flagged when it lies too far from its observer's
/// median.
/// </summary>
public sealed record TracerComparison(
    int ProfileVersion,
    Guid RunId,
    Guid BatchId,
    Guid ObserverId,
    string TracerCode,
    string QueueZone,
    DateTime JoinedUtc,
    DateTime ExitedUtc,
    int ClockOffsetMs,
    bool OffsetOutlier,
    bool Abandoned,
    ComparisonStanding Standing,
    double TracerWaitMinutes,
    double? SystemWaitMinutes,
    double? ErrorMinutes,
    double ToleranceMinutes,
    bool? WithinTolerance,
    double? ErrorOffsetMinusMinutes,
    double? ErrorOffsetPlusMinutes);

/// <summary>
/// How much the clock correction matters (ARV-104b security review): over the Good compared runs whose system wait is known
/// with every offset lowered by <see cref="Shift"/>, as measured, and raised by it, the bias and the runs within tolerance at
/// each, and the largest change of one run's error.
/// </summary>
public sealed record WaitSensitivity(
    TimeSpan Shift,
    int Runs,
    double? BiasOffsetMinus,
    double? Bias,
    double? BiasOffsetPlus,
    int WithinOffsetMinus,
    int Within,
    int WithinOffsetPlus,
    double? LargestChangeMinutes);

/// <summary>
/// Tracer wait error and bias of one queue zone (or of every zone, <see cref="QueueZone"/> null), F18: the runs, the Good
/// compared ones (not abandoned, with a system wait: the judged runs), the excluded ones (not abandoned and not Good), those
/// within tolerance, the bias sum(w_system - w_tracer) / sum(w_tracer), the mean and largest error, the runs by standing, the
/// runs whose batch offset is an outlier, the abandoned runs set beside the system's abandoned entrants and entries over the
/// final bins their joins fall in (no target), the sensitivity to the clock offsets, and the two verdicts. The verdicts judge
/// only the runs compared, so they are not acceptance verdicts on their own: the campaign's verdict (ARV-104g) also needs the
/// campaign's target of compared tracer runs and shows the excluded share.
/// </summary>
public sealed record TracerZoneSummary(
    int ProfileVersion,
    string QueueZone,
    int Runs,
    int Compared,
    int Excluded,
    int WithinTolerance,
    double? Bias,
    double? MeanErrorMinutes,
    double? LargestAbsoluteErrorMinutes,
    StandingTally Standings,
    int OffsetOutliers,
    int Abandoned,
    int AbandonedBins,
    long SystemAbandoned,
    long SystemEntries,
    WaitSensitivity Sensitivity,
    CriterionCheck ErrorCheck,
    CriterionCheck BiasCheck);

/// <summary>Tracks of a set of bins (zone_health_bin): bins, tracks entered, exited, abandoned, fragmented and censored, and the rate exited / entered (null with none entered).</summary>
public sealed record TrackTotals(int Bins, long Entered, long Exited, long Abandoned, long Fragmented, long Censored, double? Rate)
{
    public static readonly TrackTotals None = new(0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// Track completion of one queue zone over the campaign's bins (F18): the bins by standing and the bins excluded (not Good;
/// the judged bins are <see cref="Good"/>'s), the tracks of the Good bins with their pooled rate (never an average of bin
/// rates), the Degraded and Unknown bins' tracks apart, and the verdict (not an acceptance verdict on its own: the campaign's
/// verdict, ARV-104g, shows the excluded share).
/// </summary>
public sealed record ZoneTrackCompletion(int ProfileVersion, string QueueZone, StandingTally Bins, int ExcludedBins, TrackTotals Good, TrackTotals Degraded,
    TrackTotals Unknown, CriterionCheck Check);

/// <summary>
/// The clock offsets one observer's batches measured (ARV-104b security review): the observer's pseudonymous id (an Ariva user
/// id, never a name), its batches and runs, and the spread of the batch offsets (minimum, median, maximum, milliseconds).
/// </summary>
public sealed record ObserverOffsetSpread(Guid ObserverId, int Batches, int Runs, int MinimumMs, double MedianMs, int MaximumMs, int OutlierBatches);

/// <summary>One tracer batch's measured offset, its distance from its observer's median and whether that exceeds the bound.</summary>
public sealed record BatchOffset(Guid BatchId, Guid ObserverId, int ClockOffsetMs, int Runs, double DeviationMs, bool Outlier);

/// <summary>The kinds of input whose keys the engine reports unusable (<see cref="UnusableKey"/>).</summary>
public enum UnusableKeyKind
{
    /// <summary>A manual count: line, bin and observer.</summary>
    ManualCount,

    /// <summary>A line's Ariva crossings in a bin under the campaign's version: zone, line and bin.</summary>
    LineBin,

    /// <summary>A queue minute: zone and minute.</summary>
    QueueMinute,

    /// <summary>A queue bin: zone and bin.</summary>
    QueueBin,

    /// <summary>A zone health bin: zone and bin.</summary>
    HealthBin,

    /// <summary>
    /// A quality interval of the zone that cannot be placed (a time not UTC, an end not after its start, an undefined quality):
    /// one key per zone, at <see cref="ComparisonData.EarliestUtc"/>, since the zone is Unknown over the whole comparison.
    /// </summary>
    QualityInterval,

    /// <summary>An observer's desk state (ARV-104f): desk, minute and observer. The desk minute is not judged.</summary>
    DeskObservation,

    /// <summary>A desk's stored minute (ARV-104f): desk and minute. Its observed minute counts as Unknown, so as disagreement.</summary>
    DeskMinute,

    /// <summary>A shadow nowcast (ARV-104f): zone and minute. The minute's shadow reading is Unknown.</summary>
    ShadowMinute
}

/// <summary>Why a key is unusable.</summary>
public enum UnusableKeyReason
{
    /// <summary>
    /// A row of its deciding revision holds a value that cannot be (a time not UTC, below 0, above its bound, not a number, more
    /// outcomes than entrants), or a quality interval of the zone cannot be placed.
    /// </summary>
    Refused,

    /// <summary>Its deciding revision is held by rows that disagree: which of them is true cannot be told.</summary>
    Conflicting
}

/// <summary>
/// A key whose deciding rows cannot be used (ARV-104e security review, CWE-501): the highest revision of a manual count, a
/// queue bin, a zone health bin or an observer's desk state, or the rows of a queue minute, a shadow nowcast, a desk minute or
/// a line's Ariva crossings in a bin under the campaign's version, are refused or conflicting. No earlier revision takes its
/// place: a queue bin or health bin counts as not stored (Unknown), a queue minute as not usable (a tracer who joined in it is
/// Unknown; as a neighbour it is absent; its nowcast minute is Unknown), a line's crossings as not known (the item Unknown,
/// without N_system), the line and bin of a manual count and the desk minute of a desk state are not judged, a desk minute's
/// stored state is Unknown (so disagreement) and a shadow reading is Unknown. A zone with a quality interval that cannot be
/// placed is Unknown throughout. The line name is null for a zone's key; the zone is null for a desk's key, which names the
/// desk by <see cref="DeskId"/>; the observer (an Ariva user id, never a name) is set for a manual count and a desk state
/// only; the time is always UTC. Keys of desk states and desk minutes are desk-level border data (ARV-104g: border roles of
/// the campaign's site only).
/// </summary>
public sealed record UnusableKey(
    UnusableKeyKind Kind,
    string QueueZone,
    string LineName,
    DateTime StartUtc,
    Guid? ObserverId,
    UnusableKeyReason Reason,
    Guid? DeskId = null);

/// <summary>
/// Input rows the engine left out per kind (not a number, negative, not UTC, inverted, out of scope or outside the planned days,
/// refused or conflicting at a key's deciding revision, or a manual count of a line and bin, or a desk state of a desk minute,
/// that is not judged), never used, always counted (rows as read, never a list's own count), and the keys made unusable, in
/// order of kind, zone, line, desk, time and observer. Equal when every count and every key is.
/// </summary>
public sealed record LeftOutInputs(int ManualCounts, int TracerRuns, int LineBins, int QueueMinutes, int QueueBins, int HealthBins, int QualityIntervals)
{
    public static readonly LeftOutInputs None = new(0, 0, 0, 0, 0, 0, 0);

    private readonly IReadOnlyList<UnusableKey> _unusableKeys = [];

    /// <summary>The keys whose deciding rows are refused or conflicting (a copy; never changes).</summary>
    public IReadOnlyList<UnusableKey> UnusableKeys
    {
        get => _unusableKeys;
        init => _unusableKeys = value is null ? [] : [.. value];
    }

    /// <summary>Desk states left out (ARV-104f).</summary>
    public int DeskObservations { get; init; }

    /// <summary>Desk minutes left out (ARV-104f).</summary>
    public int DeskMinutes { get; init; }

    /// <summary>Shadow nowcasts left out (ARV-104f).</summary>
    public int ShadowMinutes { get; init; }

    public int Total => ManualCounts + TracerRuns + LineBins + QueueMinutes + QueueBins + HealthBins + QualityIntervals + DeskObservations + DeskMinutes + ShadowMinutes;

    public bool Equals(LeftOutInputs other) =>
        other is not null && ManualCounts == other.ManualCounts && TracerRuns == other.TracerRuns && LineBins == other.LineBins &&
        QueueMinutes == other.QueueMinutes && QueueBins == other.QueueBins && HealthBins == other.HealthBins && QualityIntervals == other.QualityIntervals &&
        DeskObservations == other.DeskObservations && DeskMinutes == other.DeskMinutes && ShadowMinutes == other.ShadowMinutes &&
        UnusableKeys.SequenceEqual(other.UnusableKeys);

    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(ManualCounts, TracerRuns, LineBins, QueueMinutes, QueueBins, HealthBins, QualityIntervals),
            DeskObservations, DeskMinutes, ShadowMinutes, UnusableKeys.Count);
}

/// <summary>
/// The comparison of a campaign (ARV-104e, ARV-104f, F18): count accuracy per line and bin and per line, tracer runs, tracer
/// summaries per zone and over all zones, track completion per zone, the observers' clock offsets, the desk-state agreement
/// per observed desk minute, per desk and over every desk, the nowcast error per minute, per zone and over every zone with the
/// published and the shadow nowcast side by side (the ground-truth proof), and the inputs left out. Every result carries the
/// profile version, and every list is read-only. With a <see cref="Problem"/> nothing was compared: every list is empty and
/// <see cref="TracerOverall"/>, <see cref="DeskOverall"/> and <see cref="NowcastOverall"/> are null.
/// <para>
/// Data boundary (what ARV-104g must restrict when it serves these): nothing here names a person; observers appear only as
/// pseudonymous Ariva user ids (tracer runs, batches, offsets and unusable keys of counts and desk states). The desk-state
/// results (<see cref="DeskMinutes"/>, <see cref="Desks"/>, <see cref="DeskOverall"/>) and the unusable keys of kinds
/// <see cref="UnusableKeyKind.DeskObservation"/> and <see cref="UnusableKeyKind.DeskMinute"/> are border per-desk data (a desk
/// code and a minute can be joined with AMAN's records to find an officer): only callers who see border desks of the
/// campaign's site (<c>BorderDesks.View</c>, the rule of <c>BorderDeskAccess</c>), never airport roles, never the
/// border-to-airport feed, AMAN or any export to an airport deployment. The shadow nowcast's figures (every
/// <see cref="NowcastReading"/> of the shadow, <see cref="NowcastZoneErrors.Shadow"/> and <see cref="NowcastZoneErrors.Both"/>)
/// are validation data read through <c>ariva_validation_reader</c>: only in the validation results, to <c>Validation.View</c>
/// holders of the site, never on a screen, display, alert, report, the live snapshot, the feed or AMAN.
/// </para>
/// </summary>
public sealed record ComparisonResult(
    int ProfileVersion,
    ComparisonProblem? Problem,
    IReadOnlyList<LineBinAccuracy> CountBins,
    IReadOnlyList<LineAccuracy> Lines,
    IReadOnlyList<TracerComparison> Tracers,
    IReadOnlyList<TracerZoneSummary> TracerZones,
    TracerZoneSummary TracerOverall,
    IReadOnlyList<ZoneTrackCompletion> TrackCompletion,
    IReadOnlyList<ObserverOffsetSpread> Observers,
    IReadOnlyList<BatchOffset> Batches,
    IReadOnlyList<DeskMinuteAgreement> DeskMinutes,
    IReadOnlyList<DeskAgreementSummary> Desks,
    DeskAgreementSummary DeskOverall,
    IReadOnlyList<NowcastMinuteError> NowcastMinutes,
    IReadOnlyList<NowcastZoneErrors> NowcastZones,
    NowcastZoneErrors NowcastOverall,
    LeftOutInputs LeftOut);
