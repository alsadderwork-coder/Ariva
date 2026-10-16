using Ariva.Core.Domain.Entities;
using Ariva.Core.Flights;
using Ariva.Core.Security;
using Ariva.Core.Services.Border;
using Ariva.Core.Services.Topology;
using Ariva.Core.Staffing;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Staffing;
using StaffingRow = Ariva.Infra.Staffing.StaffingReadBack.StaffingRow;

namespace Ariva.Infra.Services.Border;

/// <summary>
/// The indicative staffing recommendation of a site (ARV-181a, <see cref="ISvcStaffingRecommendations"/>). The site is
/// checked first (outside the caller's sites, unknown, or a caller without <c>ArrivalWaveLanes.View</c>: not found,
/// CWE-863, CWE-204; the endpoint checks the permission too), then every arrivals-hall lane of the published profile is
/// read (one more than <see cref="ISvcStaffingRecommendations.MaxLanes"/>, so a profile beyond the bound is refused, never cut
/// short, CWE-120) with the rows of its latest computed minute in <c>lane_staffing</c> (script 0053, written by
/// Ariva.Api.Cronz) within the last <see cref="StaffingSettings.StaleMinutes"/> minutes; a lane without one is answered
/// <see cref="StaffingErrors.Stale"/> with no numbers. Stored rows are checked again as read (CWE-501, <see cref="View"/>,
/// <see cref="StaffingReadBack.Plausible"/>, the check the DesksBelowPlan alert's inputs apply too, ARV-181b2): a row outside
/// the script's bounds and pairings, or a minute without its 13 intervals of one minute in the plan's order, is not shown
/// (the lane reads Stale). Constant, parameterised SQL only (CWE-89). No query string is read.
/// </summary>
internal sealed class SvcStaffingRecommendations(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, CallerRoles callerRoles,
    StaffingSettings settings) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcStaffingRecommendations
{
    public async Task<Result<StaffingRecommendationViewModel>> GetAsync(string siteCode, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) ||
            !RolePermissions.For(await callerRoles.GetAsync(ct)).Contains(Ariva.Core.Global.Defaults.Permissions.ViewArrivalWaveLanes) ||
            (await ExecuteSqlAsync<CodeRow>("""SELECT code AS "Code" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count == 0)
            return Result.Error<StaffingRecommendationViewModel>(TopologyErrors.NotFound);

        var lanes = await StaffingInputs.LanesAsync(UnitOfWork.StorageProvider, siteCode, ISvcStaffingRecommendations.MaxLanes + 1, ct);
        if (lanes.Count > ISvcStaffingRecommendations.MaxLanes)
            return Result.Error<StaffingRecommendationViewModel>(StaffingErrors.TooManyLanes);
        var now = UtcNow;
        var keyed = lanes.Where(l => Ariva.Core.Sensing.ZoneKeys.Fits(siteCode, l.Zone)).ToList();
        var rows = keyed.Count == 0
            ? []
            : await ExecuteSqlAsync<StaffingRow>("""
                SELECT s.zone_key AS "ZoneKey", s.minute_utc AS "MinuteUtc", CAST(s.interval_index AS integer) AS "IntervalIndex", s.lane AS "Lane",
                       s.start_utc AS "StartUtc", CAST(s.minutes AS integer) AS "Minutes", s.arrivals AS "Arrivals", CAST(s.recommended AS integer) AS "Recommended",
                       s.bound AS "Bound", s.at_limit AS "AtLimit", s.shortfall AS "Shortfall", s.wait_recommended AS "WaitRecommended", s.wait_open AS "WaitOpen",
                       s.no_service_open AS "NoServiceOpen", s.gap AS "Gap", s.lane_gap AS "LaneGap", s.queue_now AS "QueueNow", CAST(s.open_now AS integer) AS "OpenNow",
                       s.open_now_degraded AS "OpenNowDegraded", CAST(s.desks AS integer) AS "Desks", s.target_minutes AS "TargetMinutes", s.busy_cap AS "BusyCap",
                       s.throughput_per_desk AS "ThroughputPerDesk", s.throughput_source AS "ThroughputSource"
                  FROM lane_staffing s
                  JOIN (SELECT zone_key, max(minute_utc) AS minute_utc FROM lane_staffing
                         WHERE zone_key IN (:keys) AND minute_utc >= :from AND minute_utc <= :to GROUP BY zone_key) latest
                    ON latest.zone_key = s.zone_key AND latest.minute_utc = s.minute_utc
                 ORDER BY s.zone_key, s.interval_index
                 LIMIT :limit
                """, new Dictionary<string, object>
            {
                ["keys"] = keyed.Select(l => Ariva.Core.Sensing.ZoneKeys.For(siteCode, l.Zone)).ToList(),
                ["from"] = now.AddMinutes(-settings.StaleMinutes),
                ["to"] = now,
                ["limit"] = (ISvcStaffingRecommendations.MaxLanes + 1) * StaffingRecommendation.Intervals
            }, ct);

        var byZone = rows.GroupBy(r => r.ZoneKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<StaffingRow>)[.. g], StringComparer.Ordinal);
        var views = lanes.Select(l =>
        {
            var key = Ariva.Core.Sensing.ZoneKeys.For(siteCode, l.Zone);
            return Answer(l, key, byZone.GetValueOrDefault(key));
        }).ToList();
        return new Result<StaffingRecommendationViewModel>(new StaffingRecommendationViewModel(siteCode, now, StaffingRecommendation.IntervalMinutes, true, views));
    }

    /// <summary>A lane as answered: its stored minute when it holds together (<see cref="View"/>), otherwise Stale with no numbers.</summary>
    internal static LaneStaffingViewModel Answer(StaffingLane lane, string key, IReadOnlyList<StaffingRow> rows) =>
        View(lane, key, rows) ?? StaleLane(lane, key);

    private static LaneStaffingViewModel StaleLane(StaffingLane lane, string key) =>
        new(lane?.Zone, key, lane?.Lane, null, null, null, null, null, false, null, null, null, StaffingErrors.Stale, []);

    /// <summary>
    /// A lane's stored minute as answered, or null when it does not hold together (CWE-501: the table is input like any
    /// other): exactly <see cref="StaffingRecommendation.Intervals"/> rows of the lane's key, indexed 0 to 12 in order, each
    /// within script 0053's bounds and pairings (<see cref="StaffingReadBack.Plausible"/>), all of one minute and the lane's
    /// category with the lane's values repeated alike, starting and lasting as the plan of that minute does. A lane answered
    /// null reads Stale with no numbers.
    /// </summary>
    internal static LaneStaffingViewModel View(StaffingLane lane, string key, IReadOnlyList<StaffingRow> rows)
    {
        if (lane is null || key is null || rows is null || rows.Count != StaffingRecommendation.Intervals || rows.Any(r => r is null))
            return null;
        if (rows.Select((r, i) => r.IntervalIndex != i || r.ZoneKey != key).Any(wrong => wrong) || !rows.All(StaffingReadBack.Plausible))
            return null;
        var first = rows[0];
        if (first.Lane != lane.Lane || rows.Any(r => r.MinuteUtc != first.MinuteUtc || r.Lane != first.Lane || r.LaneGap != first.LaneGap || r.Desks != first.Desks ||
                r.OpenNow != first.OpenNow || r.OpenNowDegraded != first.OpenNowDegraded || r.QueueNow != first.QueueNow || r.TargetMinutes != first.TargetMinutes ||
                r.BusyCap != first.BusyCap || r.ThroughputPerDesk != first.ThroughputPerDesk || r.ThroughputSource != first.ThroughputSource))
            return null;
        // A minute this far on cannot be now (the read takes the last few minutes); the plan of it is not computed.
        if (first.MinuteUtc > DateTime.MaxValue.AddDays(-1))
            return null;
        var plan = StaffingRecommendation.Plan(Utc(first.MinuteUtc));
        if (rows.Select((r, i) => Utc(r.StartUtc) != plan[i].StartUtc || r.Minutes != plan[i].Minutes).Any(wrong => wrong))
            return null;
        return new LaneStaffingViewModel(lane.Zone, key, first.Lane, Utc(first.MinuteUtc), first.TargetMinutes, first.BusyCap, first.Desks, first.OpenNow, first.OpenNowDegraded,
            first.QueueNow, first.ThroughputPerDesk is { } rate ? Math.Round(1 / rate, 3) : null, first.ThroughputSource, first.LaneGap,
            [.. rows.Select(r => new StaffingIntervalViewModel(Utc(r.StartUtc), r.Minutes, r.Arrivals is { } a ? Math.Round(a, 1) : null, r.Recommended, r.Bound, r.AtLimit,
                r.Shortfall, r.WaitRecommended is { } w ? Math.Round(w, 1) : null, r.WaitOpen is { } o ? Math.Round(o, 1) : null, r.NoServiceOpen, r.Gap))]);
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private sealed class CodeRow
    {
        public string Code { get; set; }
    }
}
