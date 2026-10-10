using Ariva.Core.Queueing;

namespace Ariva.Core.Validation.Comparison;

/// <summary>
/// One nowcast of a minute against the realised wait (ARV-104f, F18): its standing (that of the minute pair it was compared on,
/// and Unknown for a stored row that cannot be used), the nowcast in minutes or why there was none, its own F11 flag (true for
/// a nowcast from the exit term alone, an Unknown desk or a degraded queue length; null when not known), the sensor cycle time
/// the shadow took (ARV-117b; null for the published nowcast and when the shadow fell back), and the error nowcast minus
/// realised wait (positive when the nowcast overstated the wait; null without both).
/// </summary>
public sealed record NowcastReading(
    ComparisonStanding Standing,
    double? NowcastMinutes,
    NoServiceReason? NoService,
    bool? Flagged,
    double? SensorCycleMinutes,
    double? ErrorMinutes);

/// <summary>
/// One minute of a queue zone in the planned days with a stored nowcast (ARV-104f, F18): the nowcast stored for the minute is
/// computed at its end, for someone joining then, so it is compared with Ariva's final mean realised wait of the people who
/// entered in the next minute (the matching rule, <see cref="NowcastErrors"/>). The standing covers both minutes and the
/// entrants' wait (their 15-minute bins stored, of the campaign's version, final and Good, and no quality interval over them;
/// the next minute final with a realised wait), the realised wait and how many waits its mean holds, whether it is under the
/// cut (F18: 20 minutes; null without one), and the published and the shadow nowcast (null when the minute has none of that
/// kind). A queue-level aggregate with no desk and no person; the shadow reading is validation data (ARV-104g restricts it).
/// </summary>
public sealed record NowcastMinuteError(
    int ProfileVersion,
    string QueueZone,
    DateTime MinuteUtc,
    ComparisonStanding Standing,
    double? RealisedWaitMinutes,
    long RealisedWaits,
    bool? UnderCut,
    NowcastReading Published,
    NowcastReading Shadow);

/// <summary>The errors of a set of nowcast minutes: how many, the median and mean absolute error and the mean error (signed: positive when the nowcasts overstated), in minutes; null with none.</summary>
public sealed record NowcastErrorStats(int Minutes, double? MedianAbsoluteErrorMinutes, double? MeanAbsoluteErrorMinutes, double? MeanErrorMinutes)
{
    public static readonly NowcastErrorStats None = new(0, null, null, null);
}

/// <summary>How many Good minutes had no nowcast for one reason (F8 no service).</summary>
public sealed record NoServiceCount(NoServiceReason Reason, int Minutes);

/// <summary>
/// The error of one nowcast (published or shadow) over a zone's minutes, or every zone's (ARV-104f, F18): the minutes with a
/// reading of it, by standing, the Good ones without a number (no service, per reason) and of them those whose next minute's
/// entrants waited under the cut (<see cref="NoServiceUnderCut"/>: minutes the criterion would have judged), the judged
/// minutes (Good, a number, the realised wait under the cut: the criterion's), the Good minutes with a number at or above the
/// cut (reported apart, no target), of the judged those whose nowcast carried its own F11 flag (judged all the same), the
/// Degraded minutes with a number under the cut (their errors shown, not judged), for the shadow the judged minutes that took
/// the sensor cycle time (null for the published nowcast), and the minutes excluded (neither judged nor at or above the cut).
/// Medians are pooled over the minutes, never a median of zones' medians; every error's size is capped at
/// <see cref="NowcastErrors.MaxErrorMinutes"/>.
/// <para>
/// The no-service lever (security review of ARV-104f, Medium): a minute without a number is not judged, so a nowcast that said
/// "no service" while people joined and waited lowers no median: 30 such minutes beside one judged minute pass on that one.
/// The verdict here judges only the minutes it holds; the campaign's verdict (ARV-104g) must count
/// <see cref="NoServiceUnderCut"/> against the nowcast (as errors beyond the target) or cap its share of the minutes under the
/// cut (<see cref="NoServiceUnderCut"/> plus <see cref="Judged"/>'s minutes).
/// </para>
/// </summary>
public sealed record NowcastErrorSummary(
    int Minutes,
    StandingTally Standings,
    int NoService,
    int NoServiceUnderCut,
    IReadOnlyList<NoServiceCount> NoServiceReasons,
    NowcastErrorStats Judged,
    NowcastErrorStats AtOrAboveCut,
    NowcastErrorStats Flagged,
    NowcastErrorStats Degraded,
    NowcastErrorStats WithSensorCycle,
    int Excluded);

/// <summary>
/// The published and the shadow nowcast on the same minutes (ARV-104f, the ground-truth proof): the minutes both judged, and
/// each one's errors over exactly those minutes, so the side-by-side does not mix different minutes. No target.
/// </summary>
public sealed record NowcastPairedErrors(int Minutes, NowcastErrorStats Published, NowcastErrorStats Shadow);

/// <summary>
/// Nowcast error of one queue zone, or of every zone (<see cref="QueueZone"/> null), F18: the published nowcast's error and its
/// verdict (the median absolute error over the judged minutes within the target, F18: 2 minutes; no data with none judged),
/// and the ground-truth proof beside it: the shadow nowcast without AMAN inputs (ARV-117) with the minutes it covers, and both
/// on the minutes both judged; no target for the proof. The verdict judges only the minutes it holds, so it is not an
/// acceptance verdict on its own (ARV-104g: the campaign's verdict shows the excluded share). The shadow's figures are
/// validation data: ARV-104g serves them only in the validation results (data boundary).
/// </summary>
public sealed record NowcastZoneErrors(
    int ProfileVersion,
    string QueueZone,
    NowcastErrorSummary Published,
    NowcastErrorSummary Shadow,
    NowcastPairedErrors Both,
    CriterionCheck Check);
