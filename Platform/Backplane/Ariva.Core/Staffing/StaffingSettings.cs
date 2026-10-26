using Ariva.Core.Domain.Entities;

namespace Ariva.Core.Staffing;

/// <summary>
/// A site's own staffing values (<c>Staffing:Sites:&lt;code&gt;</c>, the ARV-148 pattern): any value left out takes the
/// deployment's. Lane keys are lane category codes (2 to 4 upper case letters).
/// </summary>
public sealed record StaffingSiteSettings
{
    /// <summary>The site's target wait for every lane without a value of its own, in minutes (1 to 120).</summary>
    public double? TargetMinutes { get; init; }

    /// <summary>The site's target wait per lane category, in minutes (1 to 120).</summary>
    public Dictionary<string, double> LaneTargetMinutes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The site's busy cap per desk (0.5 to 1).</summary>
    public double? BusyCap { get; init; }

    /// <summary>
    /// The cycle time per passenger at a desk of each lane category, in seconds (3 to 600), used when the lane's own
    /// throughput cannot be measured. Set at commissioning; none is shipped (decision of 2026-10-15).
    /// </summary>
    public Dictionary<string, double> FallbackCycleSeconds { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The settings of the indicative staffing recommendation (<c>Staffing</c>, formulas F16 pilot version, ARV-181a): the
/// target wait (default 10 minutes for immigration, F16), the busy cap per desk (0.85, owner decision of 2026-10-15), the
/// fallback cycle times (none shipped), the measured throughput's window and minimum sample, and the read bounds. Values
/// marked Proposed in formulas.md F16. Checked at start in every host that reads them.
/// </summary>
public sealed record StaffingSettings
{
    public const string SectionName = "Staffing";

    /// <summary>Sites with values of their own at most.</summary>
    public const int MaxSites = 500;

    /// <summary>Lane values of one kind per site or deployment at most.</summary>
    public const int MaxLaneValues = 50;

    /// <summary>Runs the Cronz job (the API reads what it stored either way).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>T of F16: the target wait of a lane without a value of its own, in minutes (1 to 120; F16 reference 10 for immigration).</summary>
    public double TargetMinutes { get; init; } = 10;

    /// <summary>The deployment's target wait per lane category, in minutes (1 to 120).</summary>
    public Dictionary<string, double> LaneTargetMinutes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// u: the share of an interval a desk may be busy with the interval's arrivals (0.5 to 1; 0.85, owner decision of
    /// 2026-10-15): a margin for the variability the deterministic recursion leaves out, until v1's P90 (F15).
    /// </summary>
    public double BusyCap { get; init; } = 0.85;

    /// <summary>The deployment's fallback cycle time per lane category, in seconds (3 to 600); empty: none is shipped.</summary>
    public Dictionary<string, double> FallbackCycleSeconds { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The measured throughput's window in whole minutes, ending at the lane's latest live queue minute (Proposed 60, 15 to 240).</summary>
    public int ThroughputWindowMinutes { get; init; } = 60;

    /// <summary>Below this many open desk-minutes in the window's busy minutes the throughput is not measured (Proposed 30).</summary>
    public double MinOpenDeskMinutes { get; init; } = 30;

    /// <summary>Below this many exits in the window's busy minutes the throughput is not measured (Proposed 20).</summary>
    public long MinExits { get; init; } = 20;

    /// <summary>Shorter cycle times per passenger are not plausible at a desk (Proposed 0.2 minutes, as F8's sensor cycle time).</summary>
    public double CycleMinMinutes { get; init; } = 0.2;

    /// <summary>Longer cycle times per passenger are not plausible at a desk (Proposed 10 minutes).</summary>
    public double CycleMaxMinutes { get; init; } = 10;

    /// <summary>A lane's latest live queue minute is used while at most this many minutes old (Proposed 3).</summary>
    public int QueueFreshMinutes { get; init; } = 3;

    /// <summary>The desks' latest closed minute counts for "open now" while at most this many minutes old (F8's desk term: 5).</summary>
    public int OpenFreshMinutes { get; init; } = 5;

    /// <summary>The API shows a lane's stored minute while at most this many minutes old (Proposed 3).</summary>
    public int StaleMinutes { get; init; } = 3;

    /// <summary>Desk minutes read for one site and run at most (CWE-400, CWE-120): a site above it is refused, never cut short.</summary>
    public int MaxDeskMinuteRows { get; init; } = 120_000;

    /// <summary>
    /// Lane and desk pairs read for one site and run at most (1 to 100,000; 100 lanes of 200 desks by default; CWE-400,
    /// CWE-120): a site above it is refused, never cut short. A single lane above <see cref="StaffingRecommendation.MaxDesks"/>
    /// desks is refused on its own (<see cref="SiteStaffing.Takes"/>).
    /// </summary>
    public int MaxLaneDeskRows { get; init; } = 20_000;

    /// <summary>Sites computed per run at most.</summary>
    public int MaxSitesPerRun { get; init; } = 500;

    /// <summary>Each site's own values, by site code.</summary>
    public Dictionary<string, StaffingSiteSettings> Sites { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The whole minutes of queue read per lane: the throughput window, the freshness of the latest live minute before it,
    /// and the current minute (the read takes minute_utc in (minute - this, now]). At most 240 + 15 + 1 = 256.
    /// </summary>
    public int QueueReadMinutes => ThroughputWindowMinutes + QueueFreshMinutes + 1;

    /// <summary>
    /// Queue minutes read for <paramref name="lanes"/> lanes at most: one row per zone and minute (queue_minute's key), so
    /// lanes times <see cref="QueueReadMinutes"/>; at most 100 x 256 = 25,600. The read refuses a site above it, which only a
    /// table without that key could reach, or a row whose minute_utc is not a whole minute (the key holds any timestamp, so
    /// such rows could add more than one row per zone and minute; the queue engine writes whole minutes only).
    /// </summary>
    public int MaxQueueRows(int lanes) => Math.Max(0, lanes) * QueueReadMinutes;

    /// <summary>The target wait of a lane at a site: the site's lane value, the site's value, the deployment's lane value, the deployment's value.</summary>
    public double TargetFor(string siteCode, string lane)
    {
        var site = SiteOf(siteCode);
        if (lane is not null && site?.LaneTargetMinutes is { } siteLanes && siteLanes.TryGetValue(lane, out var siteLane))
            return siteLane;
        if (site?.TargetMinutes is { } siteTarget)
            return siteTarget;
        if (lane is not null && LaneTargetMinutes is not null && LaneTargetMinutes.TryGetValue(lane, out var deploymentLane))
            return deploymentLane;
        return TargetMinutes;
    }

    /// <summary>The busy cap at a site: its own, or the deployment's.</summary>
    public double BusyCapFor(string siteCode) => SiteOf(siteCode)?.BusyCap ?? BusyCap;

    /// <summary>The fallback cycle time of a lane at a site in seconds (the site's, else the deployment's), or null when none is configured.</summary>
    public double? FallbackCycleSecondsFor(string siteCode, string lane)
    {
        if (lane is null)
            return null;
        if (SiteOf(siteCode)?.FallbackCycleSeconds is { } site && site.TryGetValue(lane, out var siteValue))
            return siteValue;
        return FallbackCycleSeconds is not null && FallbackCycleSeconds.TryGetValue(lane, out var value) ? value : null;
    }

    private StaffingSiteSettings SiteOf(string siteCode)
    {
        if (siteCode is null || Sites is null)
            return null;
        Sites.TryGetValue(siteCode, out var site);
        return site;
    }

    public IEnumerable<string> Problems()
    {
        foreach (var problem in Values(SectionName, TargetMinutes, LaneTargetMinutes, BusyCap, FallbackCycleSeconds))
            yield return problem;
        if (ThroughputWindowMinutes is < 15 or > 240)
            yield return $"{SectionName}:ThroughputWindowMinutes is 15 to 240.";
        if (!double.IsFinite(MinOpenDeskMinutes) || MinOpenDeskMinutes is < 1 or > 10_000)
            yield return $"{SectionName}:MinOpenDeskMinutes is 1 to 10,000.";
        if (MinExits is < 1 or > 100_000)
            yield return $"{SectionName}:MinExits is 1 to 100,000.";
        if (!double.IsFinite(CycleMinMinutes) || CycleMinMinutes is < 0.05 or > 10)
            yield return $"{SectionName}:CycleMinMinutes is 0.05 to 10.";
        if (!double.IsFinite(CycleMaxMinutes) || CycleMaxMinutes <= CycleMinMinutes || CycleMaxMinutes > 60)
            yield return $"{SectionName}:CycleMaxMinutes is above CycleMinMinutes and at most 60.";
        if (QueueFreshMinutes is < 1 or > 15)
            yield return $"{SectionName}:QueueFreshMinutes is 1 to 15.";
        if (OpenFreshMinutes is < 1 or > 30)
            yield return $"{SectionName}:OpenFreshMinutes is 1 to 30.";
        if (StaleMinutes is < 1 or > 60)
            yield return $"{SectionName}:StaleMinutes is 1 to 60.";
        if (MaxDeskMinuteRows is < 1_000 or > 1_000_000)
            yield return $"{SectionName}:MaxDeskMinuteRows is 1,000 to 1,000,000.";
        if (MaxLaneDeskRows is < 1 or > 100_000)
            yield return $"{SectionName}:MaxLaneDeskRows is 1 to 100,000.";
        if (MaxSitesPerRun is < 1 or > 10_000)
            yield return $"{SectionName}:MaxSitesPerRun is 1 to 10,000.";
        if ((Sites?.Count ?? 0) > MaxSites)
        {
            yield return $"{SectionName}:Sites names at most {MaxSites} sites.";
            yield break;
        }

        foreach (var (code, site) in Sites ?? [])
        {
            // A site code as the topology allows it (CWE-501: configuration is still input to the engine).
            if (!Site.IsValidCode(code))
            {
                yield return $"{SectionName}:Sites has a key that is not a site code.";
                continue;
            }

            if (site is null)
                continue;
            foreach (var problem in Values($"{SectionName}:Sites:{code}", site.TargetMinutes, site.LaneTargetMinutes, site.BusyCap, site.FallbackCycleSeconds))
                yield return problem;
        }
    }

    private static IEnumerable<string> Values(string section, double? target, Dictionary<string, double> laneTargets, double? busyCap, Dictionary<string, double> fallbacks)
    {
        if (target is { } t && !IsTarget(t))
            yield return $"{section}:TargetMinutes is 1 to 120.";
        if (busyCap is { } u && (!double.IsFinite(u) || u is < 0.5 or > 1))
            yield return $"{section}:BusyCap is 0.5 to 1.";
        if ((laneTargets?.Count ?? 0) > MaxLaneValues || (fallbacks?.Count ?? 0) > MaxLaneValues)
            yield return $"{section}: at most {MaxLaneValues} lane values of each kind.";
        foreach (var (lane, minutes) in laneTargets ?? [])
        {
            if (!LaneCategory.IsValid(lane))
                yield return $"{section}:LaneTargetMinutes has a key that is not a lane category code (2 to 4 upper case letters).";
            else if (!IsTarget(minutes))
                yield return $"{section}:LaneTargetMinutes:{lane} is 1 to 120.";
        }

        foreach (var (lane, seconds) in fallbacks ?? [])
        {
            if (!LaneCategory.IsValid(lane))
                yield return $"{section}:FallbackCycleSeconds has a key that is not a lane category code (2 to 4 upper case letters).";
            else if (!double.IsFinite(seconds) || seconds is < 3 or > 600)
                yield return $"{section}:FallbackCycleSeconds:{lane} is 3 to 600.";
        }
    }

    private static bool IsTarget(double minutes) => double.IsFinite(minutes) && minutes is >= 1 and <= StaffingRecommendation.MaxTargetMinutes;
}
