using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Queueing;

namespace Ariva.Core.Staffing;

/// <summary>
/// One lane of a site's arrivals hall as the recommendation reads it (ARV-181a; the hall's queue zones as ARV-185 defines
/// them for the recommendation and the predicted-breach wave alike, <c>ArrivalsHallLanes</c>): a queue zone of the published
/// profile with a lane category on a level with an Immigration checkpoint, its desks (the nowcast desk term's match: kind Desk
/// at the level's immigration and emigration checkpoints whose lane categories include the zone's; keys
/// site/checkpoint/desk), how many of them are in service (N), its recent queue minutes, and whether its zone key fits
/// (<see cref="Ariva.Core.Sensing.ZoneKeys.Fits"/>): a zone whose key does not fit has no queue minutes and is not computed,
/// but it is still one of the hall's queue zones and counts in its lane's sharing (ARV-185).
/// </summary>
public sealed record StaffingLaneInput(string Zone, string Lane, IReadOnlyList<string> DeskKeys, int DesksInService, IReadOnlyList<QueueMinuteSample> QueueMinutes,
    bool KeyFits = true);

/// <summary>
/// What a site's recommendation reads at <see cref="NowUtc"/>: its arrivals-hall lanes, the closed desk minutes of their
/// desks over the throughput window, the arriving legs (F14) and whether more were left unread, and AMAN's e-gate reject
/// rate and recent rejects (F12). Aggregates only: no officer, traveller or document identity.
/// </summary>
public sealed record SiteStaffingInput(
    string SiteCode,
    DateTime NowUtc,
    IReadOnlyList<StaffingLaneInput> Lanes,
    IReadOnlyList<StaffingDeskMinute> DeskMinutes,
    IReadOnlyList<ArrivingFlight> Flights,
    bool FlightsTruncated,
    double RejectRate,
    IReadOnlyDictionary<DateTime, double> LiveRejects);

/// <summary>
/// One lane's computed minute (ARV-181a, stored in <c>lane_staffing</c>): the queue now and its minute, the desks open now
/// (the desk-state count, F8 n_open, with its F11 flag), the desks in service, the target and the busy cap used, the
/// throughput per open desk and where it came from, and the recommendation per interval.
/// </summary>
public sealed record LaneStaffingRow(
    string Zone,
    string Lane,
    DateTime MinuteUtc,
    DateTime? QueueMinuteUtc,
    double? QueueNow,
    int? OpenNow,
    bool OpenNowDegraded,
    int Desks,
    double TargetMinutes,
    double BusyCap,
    LaneThroughputResult Throughput,
    LaneStaffing Staffing);

/// <summary>
/// A site's recommendation for one minute (formulas F16 pilot version, ARV-181a): the arrival wave projected to the plan's
/// end (F14, rejects coupled, F12), and for each lane its queue now, its desks open now (F8's desk term over the last 15
/// minutes, fresh for <see cref="StaffingSettings.OpenFreshMinutes"/>), its measured or configured throughput per open desk
/// (<see cref="LaneThroughput"/>) and its 13 intervals (<see cref="StaffingRecommendation"/>). Pure: no I/O, the time passed in.
/// </summary>
public static class SiteStaffing
{
    /// <summary>The desk minutes F8's desk term reads for the desks open now (as the stream's desk term source).</summary>
    public const int OpenLookbackMinutes = 15;

    /// <summary>A queue length above this is not plausible (<see cref="StaffingRecommendation.MaxPassengers"/>): the lane has no queue now.</summary>
    public const int MaxQueueLength = 1_000_000;

    /// <summary>
    /// Whether the engine takes a lane: its zone key fits, and at most <see cref="StaffingRecommendation.MaxDesks"/> distinct
    /// desks and 0 to that many in service. A lane beyond it is refused on its own (no row; the read counts it for its
    /// warning), never cut short, and still counts in its category's sharing: it is still one of the hall's queue zones.
    /// </summary>
    public static bool Takes(StaffingLaneInput lane) =>
        lane is not null && lane.KeyFits && lane.DesksInService is >= 0 and <= StaffingRecommendation.MaxDesks &&
        (lane.DeskKeys ?? []).Where(k => k is not null).Distinct(StringComparer.Ordinal).Count() <= StaffingRecommendation.MaxDesks;

    /// <summary>The window the projection takes: from now to the plan's end, at most <see cref="ArrivalWave.MaxProjectionWindow"/>.</summary>
    public static int ProjectionWindow(DateTime nowUtc, IReadOnlyList<StaffingSlot> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var minutes = (int)Math.Ceiling((plan[^1].EndUtc - nowUtc).TotalMinutes);
        return Math.Clamp(minutes, ArrivalWave.MinWindow, ArrivalWave.MaxProjectionWindow);
    }

    /// <summary>
    /// F8's desk term over a lane's desk minutes of the last <see cref="OpenLookbackMinutes"/> minutes up to
    /// <paramref name="nowUtc"/> (<see cref="DeskTerms"/>, its published part):
    /// n_open, its minute and its flag, or null when no desk minute is there or none is open while any is Unknown. Built with
    /// loops only, so no Staffing type holds a desk term sample (the shadow nowcast's sensor-only values stay with the desk
    /// term, ARV-117a).
    /// </summary>
    public static DeskTerm OpenNow(IReadOnlyList<StaffingDeskMinute> minutes, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        var minute = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var from = minute.AddMinutes(-OpenLookbackMinutes);
        var samples = new List<DeskMinuteSample>();
        foreach (var m in minutes)
        {
            if (m is not null && m.MinuteUtc > from && m.MinuteUtc <= nowUtc)
                samples.Add(new DeskMinuteSample(m.DeskKey, m.MinuteUtc, m.IdleSeconds, m.ServingSeconds, m.UnknownSeconds, 0, m.Degraded));
        }

        return samples.Count == 0 ? null : DeskTerms.Compute(samples, 5, nowUtc);
    }

    /// <summary>
    /// The rows of every lane the engine takes (<see cref="Takes"/>), in input order. A value out of its range stays with
    /// its lane or interval (a queue length gives <see cref="StaffingGap.NoQueueLength"/>, an interval's arrivals
    /// <see cref="StaffingGap.ArrivalsOutOfRange"/>, a lane beyond the desk bound no row): one bad lane never stops the site.
    /// </summary>
    public static IReadOnlyList<LaneStaffingRow> Compute(SiteStaffingInput input, StaffingSettings settings, ArrivalWaveSettings wave, EgateCouplingSettings coupling)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(coupling);
        ArgumentNullException.ThrowIfNull(input.Lanes);
        if (input.NowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The time is UTC.", nameof(input));
        var now = input.NowUtc;
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var plan = StaffingRecommendation.Plan(now);
        var projection = ArrivalWave.Project(input.Flights ?? [], now, ProjectionWindow(now, plan), wave);
        var minutes = EgateCoupling.Couple(projection.Minutes, input.RejectRate, coupling.LagMinutes, coupling.RejectLane, input.LiveRejects);
        // Every queue zone of the hall counts in its lane's sharing and in which categories the shared lane takes, the lanes the
        // engine refuses and those whose zone key does not fit included (ARV-185).
        var sharing = input.Lanes.Where(l => l?.Lane is not null).GroupBy(l => l.Lane, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var hallLanes = sharing.Keys.ToList();
        var deskMinutes = (input.DeskMinutes ?? []).Where(m => m?.DeskKey is not null).GroupBy(m => m.DeskKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var rows = new List<LaneStaffingRow>(input.Lanes.Count);
        foreach (var lane in input.Lanes)
        {
            if (lane?.Zone is null || lane.Lane is null)
                throw new ArgumentException("Every lane has a zone and a lane category.", nameof(input));
            // A lane beyond the desk bound (CWE-120), or whose zone key does not fit, is refused on its own: the others are still computed.
            if (!Takes(lane))
                continue;
            var keys = (lane.DeskKeys ?? []).Where(k => k is not null).Distinct(StringComparer.Ordinal).ToList();
            var laneMinutes = keys.SelectMany(k => deskMinutes.GetValueOrDefault(k) ?? []).ToList();
            var queueMinutes = lane.QueueMinutes ?? [];

            // Q_0: the lane queue's latest live minute (one with a length) at or before now, while fresh and not degraded.
            var latest = System.Linq.Enumerable.MaxBy(queueMinutes.Where(q => q is not null && q.QueueLength is not null && q.MinuteUtc <= minute), q => q.MinuteUtc);
            var fresh = latest is not null && latest.MinuteUtc >= minute.AddMinutes(-settings.QueueFreshMinutes);
            // A length out of range is not used (NoQueueLength for this lane only, CWE-501), never thrown at the whole site.
            double? queueNow = fresh && !latest.LengthDegraded && latest.QueueLength is >= 0 and <= MaxQueueLength ? latest.QueueLength : null;

            var throughput = LaneThroughput.PerOpenDesk(keys, laneMinutes, queueMinutes, latest?.MinuteUtc ?? minute, settings,
                settings.FallbackCycleSecondsFor(input.SiteCode, lane.Lane));

            // Open now: F8's n_open from the desks' latest closed minute, as the nowcast's desk term reads it.
            var term = OpenNow(laneMinutes, now);
            var openFresh = term is not null && term.AsOfMinuteUtc >= minute.AddMinutes(-settings.OpenFreshMinutes);
            int? openNow = openFresh ? Math.Min(term.OpenServers, StaffingRecommendation.MaxDesks) : null;

            var arrivals = StaffingRecommendation.LaneArrivals(plan, minutes, projection.Flights, LaneDemand.Categories(lane.Lane, hallLanes), sharing[lane.Lane],
                input.FlightsTruncated);
            var target = settings.TargetFor(input.SiteCode, lane.Lane);
            var busyCap = settings.BusyCapFor(input.SiteCode);
            var staffing = StaffingRecommendation.Recommend(new LaneStaffingInput(queueNow, arrivals, throughput.RatePerDeskMinute, openNow, lane.DesksInService, target, busyCap));
            rows.Add(new LaneStaffingRow(lane.Zone, lane.Lane, minute, latest?.MinuteUtc, queueNow, openNow, openFresh && term.Degraded, lane.DesksInService, target, busyCap,
                throughput, staffing));
        }

        return rows;
    }
}
