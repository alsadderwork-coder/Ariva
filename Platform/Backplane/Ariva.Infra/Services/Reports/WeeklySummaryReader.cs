using System.Globalization;
using Ariva.Core;
using Ariva.Core.Queueing;
using Ariva.Core.Reports;
using Ariva.Core.Security;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Quality;
using static Ariva.Core.Global.Defaults;

namespace Ariva.Infra.Services.Reports;

/// <summary>
/// Reads one site's local week for the weekly summary (ARV-119) and builds it (<see cref="WeeklySummaries"/>). The site's
/// time zone is applied in .NET (<see cref="SiteTimeZones"/>): the week's local hour and day bounds go to the database as
/// parameters and every grouping uses them (<c>width_bucket</c>), so a week with a clock change has 167 or 169 hours and
/// PostgreSQL's own time zone data never decides a local day. Every query is constant and parameterised (CWE-89), names the
/// site (a zone key built from it, a desk joined to the site's own checkpoints, the site code of an alert, a device or a
/// ledger minute), and reads only what the given roles allow (CWE-863): the alerts they see, airport desks only with
/// <c>AirportDesks.View</c>, data quality and availability only with <c>DataQuality.View</c>. Every read is bounded by
/// <see cref="Limits"/> and a read that passes its bound is refused, never cut short (LIMIT bound + 1, CWE-120).
/// <para>
/// Sources: <c>queue_minute</c> and its wait histograms merged per lane, hour and bucket in the database (the continuous
/// aggregate <c>queue_minute_15m</c> has no histograms, so percentiles cannot come from it; F7, as the daily report);
/// the stored 15-minute results <c>queue_bin</c> and <c>zone_health_bin</c> (latest revision of each bin);
/// <c>overflow_minute</c> with the rule of <c>overflow_bin_15m</c> (TC-19: distinct minutes in which any band held anyone),
/// read by its own time column so the hypertable skips chunks; the availability ledger per local date
/// (<see cref="AvailabilityLedgerReader"/>); <c>desk_minute</c>; <c>alert</c>; <c>device</c> and <c>zone_outage</c>.
/// </para>
/// The caller decides who may read the site; this class only reads.
/// </summary>
internal sealed class WeeklySummaryReader(IUnitOfWork unitOfWork, AvailabilityLedgerReader ledger) : SvcDb(unitOfWork)
{
    /// <summary>The bounds of the reads; the defaults unless a test lowers them.</summary>
    public WeeklySummaryLimits Limits { get; init; } = WeeklySummaryLimits.Default;

    #region Site

    public async Task<bool> SiteExistsAsync(string siteCode, CancellationToken ct) =>
        (await ExecuteSqlAsync<TextRow>("""SELECT code AS "Value" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count > 0;

    /// <summary>
    /// The site's time zone: its airport's (the first by IATA code, as the daily report), UTC for a site without an airport,
    /// or none when the airport names a zone this host does not know (never decided in UTC then, as the availability ledger).
    /// </summary>
    public async Task<(bool Known, TimeZoneInfo Zone)> TimeZoneAsync(string siteCode, CancellationToken ct)
    {
        var zones = await ExecuteSqlAsync<TextRow>("""
            SELECT a.time_zone_id AS "Value" FROM terminal t JOIN airport a ON a.id = t.airport_id
            WHERE t.site_code = :site AND t.deleted_on IS NULL AND a.deleted_on IS NULL ORDER BY a.iata_code LIMIT 1
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct);
        if (zones.Count == 0)
            return (true, TimeZoneInfo.Utc);
        TimeZoneInfo zone = null;
        var found = SiteTimeZones.TryFind(zones[0].Value, out zone);
        return found ? (true, zone) : (false, null);
    }

    #endregion

    /// <summary>The summary of the site's local week from <paramref name="weekStart"/>, for <paramref name="roles"/>; refused (TooLarge) beyond a bound.</summary>
    public async Task<Result<WeeklySummary>> ReadAsync(string siteCode, DateOnly weekStart, TimeZoneInfo zone, IReadOnlyCollection<string> roles, DateTime nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zone);
        // Zone keys and outages are read by the site code as a key prefix (LIKE 'code/%'): a code outside the site pattern, which
        // holds no LIKE wildcard, is never read, whoever calls this reader (CWE-863; in SQL 'DM_/%' matches DMO's keys).
        if (!Site.IsValidCode(siteCode))
            return Result.Error<WeeklySummary>(WeeklySummaryErrors.NotFound);
        // The week's bound again, whoever calls (defence in depth beside the service's check, CWE-120).
        if (!WeeklySummaries.IsValidWeek(weekStart, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone))))
            return Result.Error<WeeklySummary>(WeeklySummaryErrors.InvalidWeek);
        roles ??= [];
        var permissions = RolePermissions.For(roles);
        var seesBorder = permissions.Contains(Permissions.ViewBorderDesks);
        var seesAirport = permissions.Contains(Permissions.ViewAirportDesks);
        var seesQuality = permissions.Contains(Permissions.ViewDataQuality);
        var calendar = WeeklySummaries.Calendar(weekStart, zone);
        var hours = WeeklySummaries.Instants(calendar.Hours.Select(h => h.StartUtc));
        var days = WeeklySummaries.Instants(calendar.Days.Select(d => d.FromUtc));
        var prefix = siteCode + "/%";

        // The lanes: every queue zone of the published profile and any other zone with minutes in the week (one since renamed
        // or retired); the lane category from the newest profile that names it.
        var published = await ExecuteSqlAsync<TextRow>("""
            SELECT DISTINCT z.name AS "Value" FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND z.kind = 'Queue' AND p.status = 'Published'
            ORDER BY 1 LIMIT :cap
            """, new Dictionary<string, object> { ["site"] = siteCode, ["cap"] = Limits.MaxLanes + 1 }, ct);
        var withData = await ExecuteSqlAsync<TextRow>("""
            SELECT DISTINCT zone_key AS "Value" FROM queue_minute
            WHERE zone_key LIKE :prefix AND minute_utc >= :from AND minute_utc < :to
            ORDER BY 1 LIMIT :cap
            """, new Dictionary<string, object> { ["prefix"] = prefix, ["from"] = calendar.FromUtc, ["to"] = calendar.ToUtc, ["cap"] = Limits.MaxLanes + 1 }, ct);
        var names = published.Select(r => r.Value)
            .Concat(withData.Select(r => r.Value).Where(k => k.StartsWith(siteCode + "/", StringComparison.Ordinal)).Select(k => k[(siteCode.Length + 1)..]))
            .Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > Limits.MaxLanes)
            return TooLarge();

        var zones = new List<ReportZone>();
        var hourRows = new List<WeeklyHourRow>();
        var binRows = new List<WeeklyBinRow>();
        var healthRows = new List<WeeklyHealthRow>();
        var overflowRows = new List<WeeklyOverflowRow>();
        if (names.Count > 0)
        {
            var lanes = (await ExecuteSqlAsync<ZoneRow>("""
                SELECT DISTINCT ON (z.name) z.name AS "Name", z.lane_category AS "Lane"
                FROM zone z JOIN zone_profile p ON p.id = z.profile_id
                WHERE p.site_code = :site AND z.kind = 'Queue' AND p.status IN ('Published', 'Retired') AND z.name IN (:names)
                ORDER BY z.name, p.version DESC
                """, new Dictionary<string, object> { ["site"] = siteCode, ["names"] = names }, ct)).ToDictionary(z => z.Name, z => z.Lane, StringComparer.Ordinal);
            zones = [.. names.Select(n => new ReportZone(Ariva.Core.Sensing.ZoneKeys.For(siteCode, n), n, lanes.GetValueOrDefault(n)))];
            var keys = zones.Select(z => z.ZoneKey).ToList();

            var hourCap = Limits.MaxLanes * calendar.Hours.Count;
            var hourRead = await ExecuteSqlAsync<HourRow>("""
                WITH m AS (
                    SELECT q.zone_key, width_bucket(q.minute_utc, CAST(string_to_array(:hours, ',') AS timestamptz[])) AS hour,
                           q.entries, q.waits, q.queue_length, q.status, q.wait_buckets, q.wait_counts,
                           (q.wait_buckets IS NOT NULL AND NOT EXISTS (
                                SELECT 1 FROM unnest(q.wait_buckets, q.wait_counts) AS x(bucket, n)
                                WHERE x.bucket IS NULL OR x.n IS NULL OR x.bucket < 0 OR x.bucket >= :buckets OR x.n < 0)) AS usable
                    FROM queue_minute q
                    WHERE q.zone_key IN (:keys) AND q.minute_utc >= :from AND q.minute_utc < :to
                ), stats AS (
                    SELECT zone_key, hour, CAST(sum(entries) AS bigint) AS entries, CAST(sum(waits) AS bigint) AS waits, max(queue_length) AS max_length,
                           bool_or(status IS DISTINCT FROM 'Final') AS provisional,
                           bool_or(waits > 0 AND (wait_buckets IS NULL OR cardinality(wait_buckets) = 0)) AS missing
                    FROM m GROUP BY zone_key, hour
                ), histogram AS (
                    SELECT m.zone_key, m.hour, b.bucket, CAST(sum(b.n) AS bigint) AS n
                    FROM m CROSS JOIN LATERAL unnest(m.wait_buckets, m.wait_counts) AS b(bucket, n)
                    WHERE m.usable
                    GROUP BY m.zone_key, m.hour, b.bucket
                ), merged AS (
                    SELECT zone_key, hour, array_to_string(array_agg(bucket ORDER BY bucket), ',') AS buckets, array_to_string(array_agg(n ORDER BY bucket), ',') AS counts
                    FROM histogram GROUP BY zone_key, hour
                )
                SELECT s.zone_key AS "ZoneKey", s.hour AS "Hour", s.entries AS "Entries", s.waits AS "Waits", s.max_length AS "MaxQueueLength",
                       s.provisional AS "Provisional", s.missing AS "Missing", h.buckets AS "Buckets", h.counts AS "Counts"
                FROM stats s LEFT JOIN merged h ON h.zone_key = s.zone_key AND h.hour = s.hour
                ORDER BY s.zone_key, s.hour
                LIMIT :cap
                """, new Dictionary<string, object>
            {
                ["keys"] = keys, ["from"] = calendar.FromUtc, ["to"] = calendar.ToUtc, ["hours"] = hours, ["buckets"] = WaitStatistics.Buckets, ["cap"] = hourCap + 1
            }, ct);
            if (hourRead.Count > hourCap)
                return TooLarge();
            hourRows = [.. hourRead.Select(r => new WeeklyHourRow(r.ZoneKey, r.Hour - 1, r.Entries ?? 0, r.Waits ?? 0, r.MaxQueueLength, r.Provisional ?? false,
                r.Missing ?? false, Numbers<int>(r.Buckets, int.Parse), Numbers<long>(r.Counts, long.Parse)))];

            var dayCap = Limits.MaxLanes * 7;
            var window = new Dictionary<string, object> { ["keys"] = keys, ["from"] = calendar.FromUtc, ["to"] = calendar.ToUtc, ["days"] = days, ["cap"] = dayCap + 1 };
            var overflowRead = await ExecuteSqlAsync<OverflowRow>("""
                SELECT zone_key AS "ZoneKey", width_bucket(minute_utc, CAST(string_to_array(:days, ',') AS timestamptz[])) AS "Day",
                       CAST(count(DISTINCT minute_utc) FILTER (WHERE max_occupancy > 0) AS integer) AS "OverflowMinutes",
                       CAST(count(DISTINCT minute_utc) AS integer) AS "ObservedMinutes",
                       max(max_occupancy) AS "PeakBandOccupancy"
                FROM overflow_minute
                WHERE zone_key IN (:keys) AND minute_utc >= :from AND minute_utc < :to
                GROUP BY 1, 2 ORDER BY 1, 2 LIMIT :cap
                """, window, ct);
            if (overflowRead.Count > dayCap)
                return TooLarge();
            overflowRows = [.. overflowRead.Select(r => new WeeklyOverflowRow(r.ZoneKey, r.Day - 1, r.OverflowMinutes, r.ObservedMinutes, r.PeakBandOccupancy))];

            if (seesQuality)
            {
                var binRead = await ExecuteSqlAsync<BinRow>("""
                    SELECT zone_key AS "ZoneKey", width_bucket(start_utc, CAST(string_to_array(:days, ',') AS timestamptz[])) AS "Day",
                           CAST(count(*) AS integer) AS "Bins",
                           CAST(count(*) FILTER (WHERE status = 'Final') AS integer) AS "FinalBins",
                           CAST(count(*) FILTER (WHERE quality = 'Good') AS integer) AS "GoodBins",
                           CAST(count(*) FILTER (WHERE quality = 'Degraded') AS integer) AS "DegradedBins",
                           CAST(count(*) FILTER (WHERE degraded_by_completion_only) AS integer) AS "CompletionOnlyBins",
                           CAST(count(*) FILTER (WHERE quality = 'Unknown') AS integer) AS "UnknownBins"
                    FROM (SELECT DISTINCT ON (zone_key, start_utc) zone_key, start_utc, status, quality, degraded_by_completion_only
                          FROM queue_bin
                          WHERE zone_key IN (:keys) AND start_utc >= :from AND start_utc < :to
                          ORDER BY zone_key, start_utc, revision DESC) b
                    GROUP BY 1, 2 ORDER BY 1, 2 LIMIT :cap
                    """, window, ct);
                if (binRead.Count > dayCap)
                    return TooLarge();
                binRows = [.. binRead.Select(r => new WeeklyBinRow(r.ZoneKey, r.Day - 1, r.Bins, r.FinalBins, r.GoodBins, r.DegradedBins, r.CompletionOnlyBins, r.UnknownBins))];

                // Track completion as F11 and ARV-114b judge a final bin: exits capped at entries, below the target with F18's 1e-9.
                var healthRead = await ExecuteSqlAsync<HealthRow>("""
                    SELECT zone_key AS "ZoneKey", width_bucket(start_utc, CAST(string_to_array(:days, ',') AS timestamptz[])) AS "Day",
                           CAST(count(*) AS integer) AS "Bins",
                           CAST(count(*) FILTER (WHERE status = 'Final') AS integer) AS "FinalBins",
                           CAST(COALESCE(sum(tracks_entered) FILTER (WHERE status = 'Final'), 0) AS bigint) AS "TracksEntered",
                           CAST(COALESCE(sum(least(tracks_exited, tracks_entered)) FILTER (WHERE status = 'Final'), 0) AS bigint) AS "TracksExited",
                           CAST(count(*) FILTER (WHERE status = 'Final' AND tracks_entered > 0
                                AND CAST(least(tracks_exited, tracks_entered) AS double precision) / tracks_entered < :below) AS integer) AS "BelowTarget",
                           CAST(count(conservation_residual) AS integer) AS "ResidualBins",
                           CAST(COALESCE(sum(abs(conservation_residual)), 0) AS bigint) AS "SumAbsResidual",
                           CAST(COALESCE(max(abs(conservation_residual)), 0) AS bigint) AS "MaxAbsResidual",
                           CAST(sum(occupancy_minutes) AS integer) AS "OccupancyMinutes",
                           CAST(sum(capacity_minutes) AS integer) AS "CapacityMinutes",
                           CAST(sum(minutes_outside_capacity) AS integer) AS "OutsideMinutes"
                    FROM (SELECT DISTINCT ON (zone_key, start_utc) *
                          FROM zone_health_bin
                          WHERE zone_key IN (:keys) AND start_utc >= :from AND start_utc < :to
                          ORDER BY zone_key, start_utc, revision DESC) h
                    GROUP BY 1, 2 ORDER BY 1, 2 LIMIT :cap
                    """, new Dictionary<string, object>(window) { ["below"] = HealthChecks.TrackCompletionTarget - 1e-9 }, ct);
                if (healthRead.Count > dayCap)
                    return TooLarge();
                healthRows = [.. healthRead.Select(r => new WeeklyHealthRow(r.ZoneKey, r.Day - 1, r.Bins, r.FinalBins, r.TracksEntered, r.TracksExited, r.BelowTarget,
                    r.ResidualBins, r.SumAbsResidual, r.MaxAbsResidual, r.OccupancyMinutes, r.CapacityMinutes, r.OutsideMinutes))];
            }
        }

        // The alerts the roles see, by the alert screens' rule (as the daily report): the owner role, the escalation role once
        // escalated, an alert without an owner, or an administrator; an alert on a border-only metric only for roles holding
        // ArrivalWaveLanes.View, whatever its owner and escalation roles (BorderAlerts, ARV-181b2).
        var admin = Enumerable.Contains(roles, RoleCodes.SystemAdministrator, StringComparer.Ordinal);
        var alerts = await ExecuteSqlAsync<AlertRow>("""
            SELECT rule_code AS "RuleCode", rule_name AS "RuleName", zone_name AS "ZoneName", severity AS "Severity", state AS "State",
                   raised_utc AS "RaisedUtc", acknowledged_utc AS "AcknowledgedUtc", escalated_utc AS "EscalatedUtc"
            FROM alert
            WHERE site_code = :site AND raised_utc >= :from AND raised_utc < :to
              AND (:admin OR owner_role IS NULL OR owner_role IN (:roles) OR (escalated_utc IS NOT NULL AND escalate_to_role IN (:roles)))
              AND (:border OR metric NOT IN (:borderMetrics))
            ORDER BY raised_utc LIMIT :cap
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode,
            ["from"] = calendar.FromUtc,
            ["to"] = calendar.ToUtc,
            ["admin"] = admin,
            // An empty IN list is not valid SQL; a role code that cannot exist stands in for "none".
            ["roles"] = roles.Count > 0 ? roles.ToList() : ["-"],
            ["border"] = BorderAlerts.Receives(roles),
            ["borderMetrics"] = BorderAlerts.MetricNames,
            ["cap"] = Limits.MaxAlerts + 1
        }, ct);
        if (alerts.Count > Limits.MaxAlerts)
            return TooLarge();

        var devices = await ExecuteSqlAsync<DeviceRow>("""
            SELECT d.code AS "Code", d.queue_zone_name AS "ZoneName", d.state AS "State", h.last_seen_on AS "LastSeenOn"
            FROM device d LEFT JOIN device_heartbeat h ON h.device_id = d.id
            WHERE d.site_code = :site AND d.deleted_on IS NULL AND d.state <> 'Retired'
            ORDER BY d.code LIMIT :cap
            """, new Dictionary<string, object> { ["site"] = siteCode, ["cap"] = Limits.MaxDevices + 1 }, ct);
        if (devices.Count > Limits.MaxDevices)
            return TooLarge();
        var outages = await ExecuteSqlAsync<OutageRow>("""
            SELECT device_code AS "DeviceCode", from_utc AS "FromUtc", to_utc AS "ToUtc", closed AS "Closed"
            FROM zone_outage WHERE zone_key LIKE :prefix AND from_utc < :to AND (to_utc > :from OR NOT closed)
            ORDER BY device_code, from_utc LIMIT :cap
            """, new Dictionary<string, object> { ["prefix"] = prefix, ["from"] = calendar.FromUtc, ["to"] = calendar.ToUtc, ["cap"] = Limits.MaxOutages + 1 }, ct);
        if (outages.Count > Limits.MaxOutages)
            return TooLarge();
        var outagesByDevice = outages.ToLookup(o => o.DeviceCode, StringComparer.Ordinal);
        var reportDevices = devices.Select(d =>
        {
            var spans = outagesByDevice[d.Code]
                .Select(o => (FromUtc: Utc(o.FromUtc), ToUtc: o.Closed ? Utc(o.ToUtc) : (DateTime?)null)).ToList();
            // An outage the stream has not recorded yet: the registry has the device offline since it was last heard (as the daily report).
            if (d.State == "Offline" && spans.All(s => s.ToUtc.HasValue))
                spans.Add((d.LastSeenOn.HasValue ? Utc(d.LastSeenOn.Value) : calendar.FromUtc, null));
            return new ReportDevice(d.Code, d.ZoneName, spans);
        }).ToList();

        // Desk minutes by the desk's key (site/checkpoint/desk) of the site's own desks. A key shared by checkpoints of more than
        // one kind is left out (fail closed: it could carry border data under an airport kind); airport kinds only with AirportDesks.View.
        var desks = await ExecuteSqlAsync<DeskRow>("""
            WITH keys AS (
                SELECT d.site_code || '/' || c.code || '/' || d.code AS desk_key, min(c.kind) AS kind, min(c.code) AS checkpoint, min(d.code) AS desk,
                       count(DISTINCT c.kind) AS kinds
                FROM desk d JOIN checkpoint c ON c.id = d.checkpoint_id
                WHERE d.site_code = :site AND c.site_code = :site
                GROUP BY 1
            )
            SELECT k.kind AS "Kind", k.checkpoint AS "Checkpoint", k.desk AS "Desk", m.lane AS "Lane",
                   width_bucket(m.minute_utc, CAST(string_to_array(:days, ',') AS timestamptz[])) AS "Day",
                   CAST(count(*) AS integer) AS "Minutes",
                   CAST(sum(m.closed_seconds) AS double precision) AS "Closed", CAST(sum(m.idle_seconds) AS double precision) AS "Idle",
                   CAST(sum(m.serving_seconds) AS double precision) AS "Serving", CAST(sum(m.paused_seconds) AS double precision) AS "Paused",
                   CAST(sum(m.unknown_seconds) AS double precision) AS "Unknown"
            FROM keys k JOIN desk_minute m ON m.desk_code = k.desk_key
            WHERE k.kinds = 1 AND m.minute_utc >= :from AND m.minute_utc < :to
              AND (k.kind IN ('Immigration', 'Emigration') OR (CAST(:airport AS boolean) AND k.kind IN ('CheckIn', 'Security')))
            GROUP BY 1, 2, 3, 4, 5 ORDER BY 1, 2, 3, 4, 5 LIMIT :cap
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode, ["from"] = calendar.FromUtc, ["to"] = calendar.ToUtc, ["days"] = days, ["airport"] = seesAirport, ["cap"] = Limits.MaxDeskRows + 1
        }, ct);
        if (desks.Count > Limits.MaxDeskRows)
            return TooLarge();

        var availability = seesQuality
            ? await ledger.CountsByDayAsync(siteCode, calendar.Days[0].Date, calendar.Days[^1].Date, ct)
            : new Dictionary<DateOnly, Ariva.Core.Availability.AvailabilityCounts>();

        return new Result<WeeklySummary>(WeeklySummaries.Build(new WeeklySummaryInput(
            siteCode, weekStart, zone, nowUtc, seesBorder, seesAirport, seesQuality, zones, hourRows, binRows, healthRows, overflowRows,
            [.. alerts.Select(a => new WeeklyAlertRow(a.RuleCode, a.RuleName, a.ZoneName, a.Severity, a.State, Utc(a.RaisedUtc),
                a.AcknowledgedUtc.HasValue ? Utc(a.AcknowledgedUtc.Value) : null, a.EscalatedUtc.HasValue ? Utc(a.EscalatedUtc.Value) : null))],
            reportDevices,
            availability,
            [.. desks.Select(d => new WeeklyDeskRow(d.Kind, d.Checkpoint, d.Desk, d.Lane, d.Day - 1, d.Minutes, d.Closed, d.Idle, d.Serving, d.Paused, d.Unknown))])));
    }

    private static Result<WeeklySummary> TooLarge() => Result.Error<WeeklySummary>(WeeklySummaryErrors.TooLarge);

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private static T[] Numbers<T>(string csv, Func<string, NumberStyles, IFormatProvider, T> parse) =>
        string.IsNullOrEmpty(csv) ? [] : [.. csv.Split(',').Select(n => parse(n, NumberStyles.Integer, CultureInfo.InvariantCulture))];

    #region Rows

    private sealed class TextRow
    {
        public string Value { get; set; }
    }

    private sealed class ZoneRow
    {
        public string Name { get; set; }
        public string Lane { get; set; }
    }

    private sealed class HourRow
    {
        public string ZoneKey { get; set; }
        public int Hour { get; set; }
        public long? Entries { get; set; }
        public long? Waits { get; set; }
        public int? MaxQueueLength { get; set; }
        public bool? Provisional { get; set; }
        public bool? Missing { get; set; }
        public string Buckets { get; set; }
        public string Counts { get; set; }
    }

    private sealed class BinRow
    {
        public string ZoneKey { get; set; }
        public int Day { get; set; }
        public int Bins { get; set; }
        public int FinalBins { get; set; }
        public int GoodBins { get; set; }
        public int DegradedBins { get; set; }
        public int CompletionOnlyBins { get; set; }
        public int UnknownBins { get; set; }
    }

    private sealed class HealthRow
    {
        public string ZoneKey { get; set; }
        public int Day { get; set; }
        public int Bins { get; set; }
        public int FinalBins { get; set; }
        public long TracksEntered { get; set; }
        public long TracksExited { get; set; }
        public int BelowTarget { get; set; }
        public int ResidualBins { get; set; }
        public long SumAbsResidual { get; set; }
        public long MaxAbsResidual { get; set; }
        public int OccupancyMinutes { get; set; }
        public int CapacityMinutes { get; set; }
        public int OutsideMinutes { get; set; }
    }

    private sealed class OverflowRow
    {
        public string ZoneKey { get; set; }
        public int Day { get; set; }
        public int OverflowMinutes { get; set; }
        public int ObservedMinutes { get; set; }
        public int? PeakBandOccupancy { get; set; }
    }

    private sealed class AlertRow
    {
        public string RuleCode { get; set; }
        public string RuleName { get; set; }
        public string ZoneName { get; set; }
        public string Severity { get; set; }
        public string State { get; set; }
        public DateTime RaisedUtc { get; set; }
        public DateTime? AcknowledgedUtc { get; set; }
        public DateTime? EscalatedUtc { get; set; }
    }

    private sealed class DeviceRow
    {
        public string Code { get; set; }
        public string ZoneName { get; set; }
        public string State { get; set; }
        public DateTime? LastSeenOn { get; set; }
    }

    private sealed class OutageRow
    {
        public string DeviceCode { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public bool Closed { get; set; }
    }

    private sealed class DeskRow
    {
        public string Kind { get; set; }
        public string Checkpoint { get; set; }
        public string Desk { get; set; }
        public string Lane { get; set; }
        public int Day { get; set; }
        public int Minutes { get; set; }
        public double Closed { get; set; }
        public double Idle { get; set; }
        public double Serving { get; set; }
        public double Paused { get; set; }
        public double Unknown { get; set; }
    }

    #endregion
}
