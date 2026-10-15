using Fluentx;

namespace Ariva.Core.Services.Border;

/// <summary>The answers of the staffing recommendation service that are not "not found" (ARV-181a).</summary>
public static class StaffingErrors
{
    /// <summary>
    /// The published profile has more arrivals-hall lanes than one answer holds (<see cref="ISvcStaffingRecommendations.MaxLanes"/>):
    /// refused, never cut short (CWE-120). A fixed text that echoes nothing of the request.
    /// </summary>
    public const string TooManyLanes = "The site's published zone profile has more arrivals-hall lanes than one answer holds (100).";

    /// <summary>A lane whose latest stored minute is older than the staleness bound, or that has none: no numbers.</summary>
    public const string Stale = "Stale";
}

/// <summary>
/// One interval of a lane's recommendation (formulas F16 pilot version): its start and length (the first runs from the
/// computed minute to the next quarter hour), the predicted arrivals, the recommended open desks and what set them
/// (<c>Wait</c>, <c>Busy</c>, <c>Both</c> or <c>None</c>), whether the lane is at its desk limit and by how many desks it
/// falls short (null at the limit when no count reaches the target), the predicted wait with the recommendation and with
/// the desks open now (minutes, 120 meaning 120 or more; null when not known; <see cref="NoServiceOpenNow"/> when nothing is
/// open while people wait), and why there is no recommendation (<see cref="Gap"/>, <c>None</c> otherwise).
/// </summary>
public sealed record StaffingIntervalViewModel(
    DateTime StartUtc,
    int Minutes,
    double? Arrivals,
    int? Recommended,
    string Bound,
    bool AtLimit,
    int? ShortfallDesks,
    double? WaitRecommendedMinutes,
    double? WaitOpenNowMinutes,
    bool NoServiceOpenNow,
    string Gap);

/// <summary>
/// One arrivals-hall lane (a queue zone with a lane category, <see cref="ZoneKey"/> as on the live hub): the minute its
/// recommendation was computed, the target wait and busy cap used, the desks in service, the desks open now (the desk-state
/// count F8 uses, Idle or Serving, with its flag; not AMAN's sessions), the queue now, the cycle time per passenger at a desk
/// and where it came from (<c>Measured</c> or <c>Fallback</c>), why the lane has no recommendation (<see cref="Gap"/>,
/// <c>Stale</c> when its stored minute is too old) and the 13 intervals. Lane aggregates only: no desk code, no identity.
/// </summary>
public sealed record LaneStaffingViewModel(
    string Zone,
    string ZoneKey,
    string Lane,
    DateTime? MinuteUtc,
    double? TargetMinutes,
    double? BusyCap,
    int? Desks,
    int? OpenNow,
    bool OpenNowDegraded,
    double? QueueNow,
    double? CycleMinutes,
    string ThroughputSource,
    string Gap,
    IReadOnlyList<StaffingIntervalViewModel> Intervals);

/// <summary>
/// The indicative staffing recommendation of a site (ARV-181a, formulas F16 pilot version) at <see cref="AsOfUtc"/>: every
/// arrivals-hall lane of the published profile with its latest stored minute (computed once a minute by Ariva.Api.Cronz).
/// <see cref="Indicative"/> is always true: from the flight schedule or AODB and measured desk throughput, with no roster,
/// shift or break rules (v1).
/// </summary>
public sealed record StaffingRecommendationViewModel(string SiteCode, DateTime AsOfUtc, int IntervalMinutes, bool Indicative, IReadOnlyList<LaneStaffingViewModel> Lanes);

/// <summary>
/// The indicative staffing recommendation (ARV-181a): border data, because it carries the lane split of the arrival wave,
/// so only for callers with <c>ArrivalWaveLanes.View</c> (also checked by the endpoint); a site the caller cannot see, or
/// that does not exist, answers not found (CWE-863); a profile with more than <see cref="MaxLanes"/> arrivals-hall lanes is
/// refused (<see cref="StaffingErrors.TooManyLanes"/>).
/// </summary>
public interface ISvcStaffingRecommendations : ISvcScoped
{
    /// <summary>Arrivals-hall lanes in one answer at most.</summary>
    const int MaxLanes = 100;

    Task<Result<StaffingRecommendationViewModel>> GetAsync(string siteCode, CancellationToken ct = default);
}
