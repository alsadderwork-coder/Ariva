using Ariva.Core.Alerting;
using Ariva.Core.Border;
using Ariva.Core.Flights;

namespace Ariva.Infra.Flights;

/// <summary>
/// A site's arriving legs as the arrival wave needs them (ARV-047): arrivals neither cancelled nor diverted whose
/// in-block time (as <see cref="ArrivalWave.InBlock"/> takes it) can still put passengers in the hall at
/// <paramref name="now"/> or falls within the window, joined to AMAN's lane demand for the same site and flight key.
/// Parameterised SQL; the schedule bounds follow the flight rules' limits on estimates (a day before and 3 days after the
/// schedule), so the schedule index narrows the read; at most <see cref="MaxLegs"/>.
/// </summary>
public static class ArrivingLegs
{
    /// <summary>At most this many legs are projected (far above any airport's arrivals in two and a half hours).</summary>
    public const int MaxLegs = 2_000;

    public static async Task<IReadOnlyList<ArrivingFlight>> ReadAsync(IStorageProvider storage, string siteCode, DateTime now, int windowMinutes,
        ArrivalWaveSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(settings);
        // Every in-block time that can still put passengers in the hall: from before the earliest hall minute to the window's end.
        var from = now.AddMinutes(-(settings.DelayMinutes + ArrivalWave.Spread.Count + 1));
        var to = now.AddMinutes(windowMinutes);
        // The in-block time in the query is ArrivalWave.InBlock's (GREATEST ignores a null): the on-block; else the later of
        // landing plus taxi-in and the estimate; else the schedule.
        var rows = await storage.ExecuteSqlAsync<LegRow>("""
            SELECT f.flight_key AS "FlightKey", f.carrier AS "Carrier", f.number AS "Number", f.suffix AS "Suffix", f.origin AS "Origin",
                   f.terminal AS "Terminal", f.stand AS "Stand", f.scheduled_utc AS "ScheduledUtc", f.estimated_utc AS "EstimatedUtc",
                   f.actual_utc AS "LandedUtc", f.on_block_utc AS "OnBlockUtc", f.seats AS "Seats", f.pax_estimate AS "PaxEstimate",
                   d.boarded_total AS "Boarded", d.cit AS "Cit", d.res AS "Res", d.vis AS "Vis", d.crw AS "Crw", d.egate_eligible AS "EGate",
                   d.computed_utc AS "ComputedUtc"
              FROM flight_leg f
              LEFT JOIN inbound_lane_demand d ON d.site_code = f.site_code AND d.flight_key = f.flight_key
             WHERE f.site_code = :site AND f.direction = 'Arrival' AND NOT f.cancelled AND NOT f.diverted
               AND f.scheduled_utc BETWEEN :scheduledFrom AND :scheduledTo
               AND COALESCE(f.on_block_utc, GREATEST(f.actual_utc + CAST(:taxi AS integer) * interval '1 minute', f.estimated_utc), f.scheduled_utc) BETWEEN :from AND :to
             ORDER BY f.scheduled_utc, f.flight_key
             LIMIT :limit
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode, ["scheduledFrom"] = from.AddDays(-3), ["scheduledTo"] = to.AddDays(1), ["taxi"] = settings.TaxiInMinutes, ["from"] = from,
            ["to"] = to, ["limit"] = MaxLegs
        }, ct);
        return rows.Select(r => r.Flight()).ToList();
    }

    private sealed class LegRow
    {
        public string FlightKey { get; set; }
        public string Carrier { get; set; }
        public string Number { get; set; }
        public string Suffix { get; set; }
        public string Origin { get; set; }
        public string Terminal { get; set; }
        public string Stand { get; set; }
        public DateTime ScheduledUtc { get; set; }
        public DateTime? EstimatedUtc { get; set; }
        public DateTime? LandedUtc { get; set; }
        public DateTime? OnBlockUtc { get; set; }
        public int? Seats { get; set; }
        public int? PaxEstimate { get; set; }
        public int? Boarded { get; set; }
        public int? Cit { get; set; }
        public int? Res { get; set; }
        public int? Vis { get; set; }
        public int? Crw { get; set; }
        public int? EGate { get; set; }
        public DateTime? ComputedUtc { get; set; }

        public ArrivingFlight Flight() =>
            new(FlightKey, Carrier, Number, Suffix, Origin, Terminal, Stand, Utc(ScheduledUtc), UtcOrNull(EstimatedUtc), UtcOrNull(LandedUtc), UtcOrNull(OnBlockUtc),
                Seats, PaxEstimate,
                Boarded is { } boarded
                    ? new AmanLaneDemand(boarded, Cit ?? 0, Res ?? 0, Vis ?? 0, Crw ?? 0, EGate ?? 0, Utc(ComputedUtc ?? default))
                    : null);

        private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        private static DateTime? UtcOrNull(DateTime? value) => value is { } v ? Utc(v) : null;
    }
}

/// <summary>
/// The arrival wave for predicted-breach rules (ARV-038 <see cref="IArrivalWaveSource"/>, ARV-047): a queue zone's
/// projected arrivals per minute are the hall arrivals (F14) of the categories its lane takes, shared equally among the
/// arrivals hall's queue zones of that lane. One rule with the staffing recommendation (ARV-185,
/// <see cref="Staffing.ArrivalsHallLanes"/>): the lane is the queue zone's own lane category, as the immigration screen and
/// the nowcast desk term read it; the hall is the queue zones of the site's published profile on a level with an Immigration
/// checkpoint, but a queue whose service zones stand only at desks of Emigration checkpoints (ARV-191: a departures queue
/// neither shares a lane nor receives the arrivals wave); the categories are <see cref="LaneDemand.Categories"/> (the shared
/// lane ALL takes the manual categories without a lane of their own). An overflow zone takes its queue zone's arrivals and
/// does not split them further. A zone outside the hall, or whose lane takes no category, has no projection (null), so its
/// predicted rules have nothing to judge. Minutes are projected from the current minute
/// with the longest window (<see cref="ArrivalWave.MaxWindow"/>); earlier minutes have no projection, so a predicted
/// value needs every minute after it to be from now on: a backtest finds none, except at most for its last minute when it
/// ends at the current minute. The e-gate rejects join their manual lane (ARV-049, F12, <see cref="EgateCoupling"/>):
/// r measured from AMAN's e-gate intervals over the window (<see cref="EgateRejects"/>), times the e-gate eligible
/// arrivals a lag earlier. One read per site and lifetime (a scope: one evaluation tick). The result is a lane queue's
/// share of the arrival wave, which is border data (ArrivalWaveLanes.View): it is read only by the predicted nowcast's
/// evaluation and backtest (<c>AlertInputs</c>), whose rules and alerts are border-only since ARV-187.
/// </summary>
public sealed class ProjectedArrivalWave(IUnitOfWork unitOfWork, TimeProvider timeProvider, ArrivalWaveSettings settings, EgateCouplingSettings coupling)
    : IArrivalWaveSource
{
    private readonly Dictionary<string, IReadOnlyList<MinuteDemand>> _projections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, (bool Queue, string Lane)>> _lanes = new(StringComparer.Ordinal);

    // The arrivals hall's queue zones (ArrivalsHallLanes, ARV-185) and the overflow zones of each, with the queue zone's lane.
    private const string HallZonesSql = "WITH hall AS (" + Staffing.ArrivalsHallLanes.QueueZones + """
        )
        SELECT h.zone AS "Zone", 'Queue' AS "Kind", h.lane AS "Lane" FROM hall h
        UNION
        SELECT o.name, 'Overflow', h.lane
          FROM hall h JOIN zone o ON o.profile_id = h.profile_id AND o.queue_zone_id = h.id AND o.kind = 'Overflow'
         ORDER BY 1, 2, 3
        """;

    public async Task<IReadOnlyDictionary<DateTime, double>> ArrivalsAsync(string siteCode, string zoneName, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default)
    {
        if (siteCode is null || zoneName is null)
            return null;
        var zones = await LanesAsync(siteCode, ct);
        if (!zones.TryGetValue(zoneName, out var target))
            return null;
        var hallLanes = zones.Values.Where(z => z.Queue).Select(z => z.Lane).Distinct(StringComparer.Ordinal).ToList();
        var categories = LaneDemand.Categories(target.Lane, hallLanes);
        if (categories.Count == 0)
            return null;
        // Shared among the hall's queue zones of the lane only: an overflow zone takes its queue zone's arrivals, it does not
        // split them further.
        var sharing = Math.Max(1, zones.Values.Count(z => z.Queue && string.Equals(z.Lane, target.Lane, StringComparison.Ordinal)));

        if (!_projections.TryGetValue(siteCode, out var minutes))
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var flights = await ArrivingLegs.ReadAsync(unitOfWork.StorageProvider, siteCode, now, ArrivalWave.MaxWindow, settings, ct);
            var projection = ArrivalWave.Project(flights, now, ArrivalWave.MaxWindow, settings);
            var gates = await EgateRejects.ReadAsync(unitOfWork.StorageProvider, siteCode, now, coupling, ct);
            _projections[siteCode] = minutes = EgateCoupling.Couple(projection.Minutes, gates.Rate, coupling.LagMinutes, coupling.RejectLane, gates.LiveRejects);
        }

        // A minute that is not plausible (LaneDemand.Of: NaN) is no projection for that minute, as a minute the wave lacks.
        return minutes
            .Where(m => m.MinuteUtc > fromUtc && m.MinuteUtc <= toUtc)
            .ToDictionary(m => m.MinuteUtc, m => LaneDemand.Of(m.Lanes, categories) / sharing);
    }

    private async Task<Dictionary<string, (bool Queue, string Lane)>> LanesAsync(string siteCode, CancellationToken ct)
    {
        if (_lanes.TryGetValue(siteCode, out var known))
            return known;
        var rows = await unitOfWork.StorageProvider.ExecuteSqlAsync<ZoneLaneRow>(HallZonesSql, new Dictionary<string, object> { ["site"] = siteCode }, ct);
        // One row per zone: a site has one published profile, whose zone names are unique (script 0012).
        var lanes = rows
            .Where(r => r.Zone is not null && r.Lane is not null)
            .GroupBy(r => r.Zone, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.First().Kind == "Queue", g.First().Lane), StringComparer.Ordinal);
        _lanes[siteCode] = lanes;
        return lanes;
    }

    private sealed class ZoneLaneRow
    {
        public string Zone { get; set; }
        public string Kind { get; set; }
        public string Lane { get; set; }
    }
}

/// <summary>
/// AMAN's e-gate rejects at a site (ARV-049, F12), read from the stored e-gate intervals of mapped gates (ARV-048; unmapped
/// codes are kept apart): the reject rate r over
/// the coupling window (or the reference when too few attempts), and the rejects per minute of the minutes just before
/// now, which reach the manual lane within the lag.
/// </summary>
public static class EgateRejects
{
    public sealed record Reading(double Rate, bool Measured, IReadOnlyDictionary<DateTime, double> LiveRejects);

    public static async Task<Reading> ReadAsync(IStorageProvider storage, string siteCode, DateTime now, EgateCouplingSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(settings);
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var rows = await storage.ExecuteSqlAsync<MinuteRow>("""
            SELECT interval_start_utc AS "MinuteUtc", SUM(attempts) AS "Attempts", SUM(rejected) AS "Rejected"
              FROM border_egate_interval
             WHERE site_code = :site AND desk_id IS NOT NULL AND interval_start_utc >= :from AND interval_start_utc < :to
             GROUP BY interval_start_utc
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode, ["from"] = minute.AddMinutes(-settings.RateWindowMinutes), ["to"] = minute
        }, ct);
        var (rate, measured) = EgateCoupling.RejectRate(rows.Sum(r => r.Attempts), rows.Sum(r => r.Rejected), settings);
        var live = rows.Where(r => r.MinuteUtc >= minute.AddMinutes(-settings.LagMinutes))
            .ToDictionary(r => DateTime.SpecifyKind(r.MinuteUtc, DateTimeKind.Utc), r => (double)r.Rejected);
        return new Reading(rate, measured, live);
    }

    private sealed class MinuteRow
    {
        public DateTime MinuteUtc { get; set; }
        public long Attempts { get; set; }
        public long Rejected { get; set; }
    }
}
