using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Staffing;
using Ariva.Infra.Flights;

namespace Ariva.Infra.Staffing;

/// <summary>One arrivals-hall lane of a site's published profile: its queue zone and lane category.</summary>
public sealed record StaffingLane(string Zone, string Lane);

/// <summary>
/// What <see cref="StaffingInputs.ReadAsync"/> read for a site, or why it refused to read (a fixed text naming the bound),
/// and how many of the read lanes the engine refuses on their own (<see cref="SiteStaffing.Takes"/>): beyond the desk bound
/// (<see cref="RefusedLanes"/>, <see cref="StaffingInputs.TooManyLaneDesks"/>), or because the zone's key does not fit
/// (<see cref="UnkeyedLanes"/>, <see cref="StaffingInputs.UnkeyedZone"/>; ARV-185: such a zone still counts in its lane's sharing).
/// </summary>
public sealed record StaffingRead(SiteStaffingInput Input, string Refused, int RefusedLanes = 0, int UnkeyedLanes = 0);

/// <summary>
/// The inputs of a site's staffing recommendation (ARV-181a), read through the unit of work with constant, parameterised
/// SQL (CWE-89): the arrivals-hall lanes of the published profile (queue zones with a lane category on a level with an
/// Immigration checkpoint) and their desks (the nowcast desk term's match, <c>DeskTermSource</c>: desks of kind Desk at the
/// level's immigration and emigration checkpoints whose lane categories include the zone's), the lanes' queue minutes and
/// their desks' closed minutes over the throughput window, the arriving legs to the plan's end (<see cref="ArrivingLegs"/>)
/// and AMAN's e-gate rejects (<see cref="EgateRejects"/>). Every read is bounded: lanes at most
/// <see cref="Core.Services.Border.ISvcStaffingRecommendations.MaxLanes"/>, lane and desk pairs at most
/// <see cref="StaffingSettings.MaxLaneDeskRows"/>, queue minutes at most <see cref="StaffingSettings.MaxQueueRows"/> (the
/// lanes times the minutes read), desk minutes at most <see cref="StaffingSettings.MaxDeskMinuteRows"/>; a site beyond a
/// bound is refused, never cut short (CWE-400, CWE-120). A lane with more than
/// <see cref="StaffingRecommendation.MaxDesks"/> desks is refused on its own and the rest of the site is computed. Values
/// read back are checked again by the pure engines (CWE-501).
/// </summary>
public sealed class StaffingInputs(IUnitOfWork unitOfWork, ArrivalWaveSettings wave, EgateCouplingSettings coupling)
{
    public const string TooManyLanes = "more arrivals-hall lanes than one run takes";
    public const string TooManyLaneDesks = "more desks in one lane than one run takes";
    public const string TooManyDeskRows = "more lane desks than one run reads";
    public const string TooManyQueueRows = "more queue minutes than one run reads";
    public const string TooManyDeskMinutes = "more desk minutes than one run reads";
    public const string UnkeyedZone = "a queue zone whose key is longer than a message key";

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    /// <summary>
    /// The arrivals-hall lanes of a site's published profile, in zone name order, at most <paramref name="limit"/> (the caller
    /// asks for one more than its bound to refuse a profile beyond it).
    /// </summary>
    public static async Task<IReadOnlyList<StaffingLane>> LanesAsync(IStorageProvider storage, string siteCode, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var rows = await storage.ExecuteSqlAsync<LaneRow>(LanesSql, new Dictionary<string, object> { ["site"] = siteCode, ["limit"] = limit }, ct);
        return [.. rows.Select(r => new StaffingLane(r.Zone, r.Lane))];
    }

    // The arrivals hall's lanes (ArrivalsHallLanes, ARV-185), in zone name order.
    private const string LanesSql = "WITH hall AS (" + ArrivalsHallLanes.QueueZones + """
        )
        SELECT DISTINCT zone AS "Zone", lane AS "Lane" FROM hall
         ORDER BY 1
         LIMIT :limit
        """;

    // Each arrivals-hall lane with its desks, the nowcast desk term's match: kind Desk at the level's immigration and
    // emigration checkpoints whose lane categories include the zone's.
    private const string LaneDesksSql = "WITH lanes AS (" + ArrivalsHallLanes.QueueZones + """
        )
        SELECT DISTINCT l.zone AS "Zone", l.lane AS "Lane", d.site_code || '/' || c.code || '/' || d.code AS "DeskKey", d.in_service AS "InService"
          FROM lanes l
          LEFT JOIN (checkpoint c JOIN desk d ON d.checkpoint_id = c.id AND d.deleted_on IS NULL AND d.kind = 'Desk')
                 ON c.level_id = l.level_id AND c.site_code = :site AND c.deleted_on IS NULL AND c.kind IN ('Immigration', 'Emigration')
                    AND d.site_code = :site AND l.lane = ANY (string_to_array(COALESCE(d.lane_category_codes, ''), ','))
         ORDER BY 1, 3
         LIMIT :limit
        """;

    // The closed minutes of every desk of the arrivals hall's lanes over the window.
    private const string DeskMinutesSql = "WITH lanes AS (" + ArrivalsHallLanes.QueueZones + """
        ), desks AS (
            SELECT DISTINCT d.site_code || '/' || c.code || '/' || d.code AS desk_key
              FROM lanes l
              JOIN checkpoint c ON c.level_id = l.level_id AND c.site_code = :site AND c.deleted_on IS NULL AND c.kind IN ('Immigration', 'Emigration')
              JOIN desk d ON d.checkpoint_id = c.id AND d.site_code = :site AND d.deleted_on IS NULL AND d.kind = 'Desk'
             WHERE l.lane = ANY (string_to_array(COALESCE(d.lane_category_codes, ''), ','))
        )
        SELECT m.desk_code AS "DeskKey", m.minute_utc AS "MinuteUtc", CAST(m.idle_seconds AS double precision) AS "Idle",
               CAST(m.serving_seconds AS double precision) AS "Serving", CAST(m.unknown_seconds AS double precision) AS "Unknown",
               m.degraded AS "Degraded"
          FROM desk_minute m JOIN desks k ON k.desk_key = m.desk_code
         WHERE m.minute_utc > :from AND m.minute_utc <= :to
         LIMIT :limit
        """;

    /// <summary>The inputs of <paramref name="siteCode"/> at <paramref name="nowUtc"/>, or a refusal naming the bound reached.</summary>
    public async Task<StaffingRead> ReadAsync(string siteCode, DateTime nowUtc, StaffingSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteCode);
        ArgumentNullException.ThrowIfNull(settings);
        var minute = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var site = new Dictionary<string, object> { ["site"] = siteCode, ["limit"] = settings.MaxLaneDeskRows + 1 };

        var deskRows = await Storage.ExecuteSqlAsync<LaneDeskRow>(LaneDesksSql, site, ct);
        if (deskRows.Count > settings.MaxLaneDeskRows)
            return new StaffingRead(null, TooManyDeskRows);
        // A zone whose key does not fit (ZoneKeys.Fits) has no queue minutes and is not computed, but it stays one of the hall's
        // lanes: it counts in its lane's sharing and in what the shared lane takes (ARV-185).
        var lanes = deskRows.GroupBy(r => r.Zone, StringComparer.Ordinal)
            .Select(g => (Zone: g.Key, g.First().Lane, Desks: g.Where(r => r.DeskKey is not null).GroupBy(r => r.DeskKey, StringComparer.Ordinal)
                .Select(d => (Key: d.Key, InService: d.Any(r => r.InService == true))).ToList(), Fits: Ariva.Core.Sensing.ZoneKeys.Fits(siteCode, g.Key)))
            .OrderBy(l => l.Zone, StringComparer.Ordinal)
            .ToList();
        if (lanes.Count > Core.Services.Border.ISvcStaffingRecommendations.MaxLanes)
            return new StaffingRead(null, TooManyLanes);
        if (lanes.Count == 0)
            return new StaffingRead(new SiteStaffingInput(siteCode, nowUtc, [], [], [], false, 0, new Dictionary<DateTime, double>()), null);

        var from = minute.AddMinutes(-settings.QueueReadMinutes);
        var keys = lanes.Where(l => l.Fits).Select(l => Ariva.Core.Sensing.ZoneKeys.For(siteCode, l.Zone)).ToList();
        var maxQueueRows = settings.MaxQueueRows(keys.Count);
        // No zone key fits: nothing to read (an empty IN list is not SQL), and no lane is computed.
        List<QueueRow> queueRows = [];
        if (keys.Count > 0)
        {
            queueRows = await Storage.ExecuteSqlAsync<QueueRow>("""
                SELECT zone_key AS "ZoneKey", minute_utc AS "MinuteUtc", exits AS "Exits", queue_length AS "QueueLength", length_degraded AS "LengthDegraded"
                  FROM queue_minute
                 WHERE zone_key IN (:keys) AND minute_utc > :from AND minute_utc <= :to
                 ORDER BY zone_key, minute_utc
                 LIMIT :limit
                """, new Dictionary<string, object> { ["keys"] = keys, ["from"] = from, ["to"] = nowUtc, ["limit"] = maxQueueRows + 1 }, ct);
            if (queueRows.Count > maxQueueRows)
                return new StaffingRead(null, TooManyQueueRows);
        }

        var deskMinutes = new List<StaffingDeskMinute>();
        if (lanes.Any(l => l.Desks.Count > 0))
        {
            var minuteRows = await Storage.ExecuteSqlAsync<DeskMinuteRow>(DeskMinutesSql, new Dictionary<string, object> { ["site"] = siteCode, ["from"] = from, ["to"] = nowUtc, ["limit"] = settings.MaxDeskMinuteRows + 1 }, ct);
            if (minuteRows.Count > settings.MaxDeskMinuteRows)
                return new StaffingRead(null, TooManyDeskMinutes);
            deskMinutes.AddRange(minuteRows.Select(r => new StaffingDeskMinute(r.DeskKey, Utc(r.MinuteUtc), r.Idle, r.Serving, r.Unknown, r.Degraded)));
        }

        var plan = StaffingRecommendation.Plan(nowUtc);
        var window = SiteStaffing.ProjectionWindow(nowUtc, plan);
        var flights = await ArrivingLegs.ReadAsync(Storage, siteCode, nowUtc, window, wave, ct);
        var gates = await EgateRejects.ReadAsync(Storage, siteCode, nowUtc, coupling, ct);

        var byZone = queueRows.GroupBy(r => r.ZoneKey, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => (IReadOnlyList<QueueMinuteSample>)[.. g.Select(r => new QueueMinuteSample(Utc(r.MinuteUtc), r.Exits, r.QueueLength, r.LengthDegraded == true))],
            StringComparer.Ordinal);
        var inputs = lanes.Select(l => new StaffingLaneInput(l.Zone, l.Lane, [.. l.Desks.Select(d => d.Key)], l.Desks.Count(d => d.InService),
            l.Fits ? byZone.GetValueOrDefault(Ariva.Core.Sensing.ZoneKeys.For(siteCode, l.Zone)) ?? [] : [], l.Fits)).ToList();
        return new StaffingRead(new SiteStaffingInput(siteCode, nowUtc, inputs, deskMinutes, flights, flights.Count >= ArrivingLegs.MaxLegs, gates.Rate, gates.LiveRejects), null,
            inputs.Count(l => l.KeyFits && !SiteStaffing.Takes(l)), inputs.Count(l => !l.KeyFits));
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private sealed class LaneRow
    {
        public string Zone { get; set; }
        public string Lane { get; set; }
    }

    private sealed class LaneDeskRow
    {
        public string Zone { get; set; }
        public string Lane { get; set; }
        public string DeskKey { get; set; }
        public bool? InService { get; set; }
    }

    private sealed class QueueRow
    {
        public string ZoneKey { get; set; }
        public DateTime MinuteUtc { get; set; }
        public long? Exits { get; set; }
        public int? QueueLength { get; set; }
        public bool? LengthDegraded { get; set; }
    }

    private sealed class DeskMinuteRow
    {
        public string DeskKey { get; set; }
        public DateTime MinuteUtc { get; set; }
        public double Idle { get; set; }
        public double Serving { get; set; }
        public double Unknown { get; set; }
        public bool Degraded { get; set; }
    }
}
