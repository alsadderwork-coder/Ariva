using Ariva.Core;
using Ariva.Core.Queueing;
using Ariva.Core.Reports;
using Ariva.Core.Security;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Reports;

/// <summary>
/// Reads one site's local day for the daily report (ARV-060) and builds it (<see cref="DailyReports"/>): its queue zones' stored
/// minutes aggregated per zone and local hour in the database (<see cref="HoursSql"/>, ARV-234), the alerts raised that day that the given roles see (the same rule
/// as the alert screens: the owner role, the escalation role once escalated, or an administrator; an alert on a
/// border-only metric only for roles holding <c>ArrivalWaveLanes.View</c>, <see cref="BorderAlerts"/>, ARV-181b2 and ARV-187; and
/// none at all for roles without <c>Alert.View</c>, <see cref="ReportAlerts"/>, ARV-180), and the outages of its devices. The caller decides who may read the site; this class only reads. ARV-096d (CWE-400, CWE-120): each read
/// takes at most its bound of <see cref="Limits"/> plus one row, and a day past a bound is refused (<see cref="ReportErrors.TooLarge"/>),
/// never cut short. ARV-234: the hours statement runs in one of <see cref="DailyReportLimits.DatabaseSlots"/> slots across the
/// cluster or not at all (<see cref="ReportErrors.Busy"/>).
/// </summary>
internal sealed class ReportReader(IUnitOfWork unitOfWork) : SvcDb(unitOfWork)
{
    /// <summary>
    /// The first key of the report slots' transaction advisory locks (the second is the slot, 1 to
    /// <see cref="DailyReportLimits.DatabaseSlots"/>); no other lock of Ariva takes this class.
    /// </summary>
    public const int SlotLockClass = 60;

    /// <summary>
    /// ARV-234 (CWE-400, CWE-120): the site's queue minutes of the day aggregated per zone and local hour, one row per zone hour,
    /// in no particular order. First a slot: the first of the advisory locks <c>(:lockClass, :first)</c> to
    /// <c>(:lockClass, :slots)</c> it takes without waiting (held to the end of the statement outside a transaction, to the commit
    /// inside one), and nothing is read without one; every row carries whether it had one (<c>slotted</c>), and a statement with
    /// no zone hour still answers one row of NULLs for it. The zone hours are grouped first (the sums of entries and waits, the
    /// longest queue, provisional when a minute is not final or has no status, a missing histogram when a minute with waits has
    /// none); then each zone hour's minutes are merged on their own through a lateral join on the table's zone and minute index,
    /// each minute's histogram taken up to the shorter of its two lists without their NULL elements (as the minutes' text read
    /// did) and left out whole when a bucket is outside 0 to the last or a count is negative, so no merge sorts the day's pairs
    /// at once or spills to disk. <c>pairs</c> is the row's merged pair count, which the reader sums row by row.
    /// </summary>
    internal const string HoursSql = """
        WITH slot AS MATERIALIZED (
            SELECT s FROM generate_series(:first, :slots) s WHERE pg_try_advisory_xact_lock(:lockClass, s) LIMIT 1),
        hours AS (
            SELECT zone_key, floor(extract(epoch FROM minute_utc - :from) / 3600)::int AS hour,
                   sum(COALESCE(entries, 0))::bigint AS entries, sum(COALESCE(waits, 0))::bigint AS waits, max(queue_length) AS queue_length,
                   bool_or(status IS DISTINCT FROM 'Final') AS provisional,
                   bool_or(COALESCE(waits, 0) > 0 AND COALESCE(cardinality(array_remove(wait_buckets, NULL)), 0) = 0) AS missing
            FROM queue_minute
            WHERE EXISTS (SELECT 1 FROM slot) AND zone_key LIKE :prefix AND minute_utc >= :from AND minute_utc < :to
            GROUP BY 1, 2),
        merged AS (
            SELECT h.zone_key, h.hour, h.entries, h.waits, h.queue_length, h.provisional, h.missing, g.buckets, g.counts,
                   COALESCE(cardinality(g.buckets), 0) AS pairs
            FROM hours h
            LEFT JOIN LATERAL (
                SELECT array_agg(p.b ORDER BY p.b) AS buckets, array_agg(p.c ORDER BY p.b) AS counts
                FROM (
                    SELECT x.b, sum(x.c)::bigint AS c
                    FROM (
                        SELECT array_remove(q.wait_buckets, NULL) AS bk, array_remove(q.wait_counts, NULL) AS ck
                        FROM queue_minute q
                        WHERE q.zone_key = h.zone_key AND q.minute_utc >= :from + h.hour * interval '1 hour'
                          AND q.minute_utc < LEAST(:from + (h.hour + 1) * interval '1 hour', :to)) m
                    CROSS JOIN LATERAL (SELECT least(COALESCE(cardinality(m.bk), 0), COALESCE(cardinality(m.ck), 0)) AS n) k
                    CROSS JOIN LATERAL unnest(m.bk[1 : k.n], m.ck[1 : k.n]) AS x(b, c)
                    WHERE k.n > 0 AND 0 <= ALL (m.bk[1 : k.n]) AND :buckets > ALL (m.bk[1 : k.n]) AND 0 <= ALL (m.ck[1 : k.n])
                    GROUP BY x.b) p) g ON true)
        SELECT r.zone_key, r.hour, r.entries, r.waits, r.queue_length, r.provisional, r.missing, r.buckets, r.counts,
               (SELECT count(*)::int FROM slot) AS slotted, COALESCE(r.pairs, 0) AS pairs
        FROM (SELECT 1) one LEFT JOIN merged r ON true
        LIMIT :cap
        """;

    /// <summary>
    /// The site's device outages overlapping the day, and those still open (ARV-228a: a range of zone_outage's end and open
    /// indexes, script 0068, whatever the table's history; OutageReadPlanTests explains it as written here).
    /// </summary>
    internal const string OutagesSql = """
        SELECT device_code AS "DeviceCode", from_utc AS "FromUtc", to_utc AS "ToUtc", closed AS "Closed"
        FROM zone_outage WHERE zone_key LIKE :prefix AND from_utc < :to AND (to_utc > :from OR NOT closed)
        ORDER BY device_code, from_utc LIMIT :cap
        """;

    /// <summary>The bounds of the reads; tests lower them.</summary>
    public DailyReportLimits Limits { get; init; } = DailyReportLimits.Default;

    /// <summary>The site's time zone (its airports' IANA zone), or UTC for a site without one.</summary>
    public async Task<TimeZoneInfo> TimeZoneAsync(string siteCode, CancellationToken ct)
    {
        var zones = await ExecuteSqlAsync<TextRow>("""
            SELECT a.time_zone_id AS "Value" FROM terminal t JOIN airport a ON a.id = t.airport_id
            WHERE t.site_code = :site AND t.deleted_on IS NULL AND a.deleted_on IS NULL ORDER BY a.iata_code LIMIT 1
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct);
        return zones.Count > 0 && SiteTimeZones.TryFind(zones[0].Value, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    public async Task<bool> SiteExistsAsync(string siteCode, CancellationToken ct) =>
        (await ExecuteSqlAsync<TextRow>("""SELECT code AS "Value" FROM site WHERE code = :site""", new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count > 0;

    /// <summary>Who reads: a report on demand takes any slot, a scheduled delivery the last only (<see cref="DailyReportLimits.DatabaseSlots"/>).</summary>
    internal enum ReadFor
    {
        OnDemand,
        Delivery
    }

    /// <summary>The report of the site's local day on demand; see <see cref="ReadAsync(string, DateOnly, IReadOnlyCollection{string}, DateTime, ReadFor, CancellationToken)"/>.</summary>
    public Task<Result<DailyReport>> ReadAsync(string siteCode, DateOnly date, IReadOnlyCollection<string> roles, DateTime nowUtc, CancellationToken ct) =>
        ReadAsync(siteCode, date, roles, nowUtc, ReadFor.OnDemand, ct);

    /// <summary>
    /// The report of the site's local day; <paramref name="roles"/> decide which alerts it lists. Refused
    /// (<see cref="ReportErrors.TooLarge"/>) when a read passes its bound in <see cref="Limits"/>, and answered
    /// <see cref="ReportErrors.Busy"/> when every slot <paramref name="readFor"/> may take is in use.
    /// </summary>
    internal async Task<Result<DailyReport>> ReadAsync(string siteCode, DateOnly date, IReadOnlyCollection<string> roles, DateTime nowUtc, ReadFor readFor, CancellationToken ct)
    {
        var tz = await TimeZoneAsync(siteCode, ct);
        var (from, to) = DailyReports.DayBounds(date, tz);

        var read = await ReadHoursAsync(siteCode, from, to, readFor, ct);
        if (!read.Slotted)
            return Result.Error<DailyReport>(ReportErrors.Busy);
        if (read.Hours.Count > Limits.MaxZoneHours || read.Pairs > Limits.MaxHistogramPairs)
            return TooLarge();

        // Every queue zone of the published profile, and any other zone with minutes that day (a zone since renamed or
        // retired); the lane category comes from the newest profile that names the zone.
        var zones = await ExecuteSqlAsync<ZoneRow>("""
            SELECT DISTINCT ON (z.name) z.name AS "Name", z.lane_category AS "Lane"
            FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND z.kind = 'Queue' AND p.status IN ('Published', 'Retired')
            ORDER BY z.name, p.version DESC LIMIT :cap
            """, new Dictionary<string, object> { ["site"] = siteCode, ["cap"] = Limits.MaxZones + 1 }, ct);
        if (zones.Count > Limits.MaxZones)
            return TooLarge();
        // The published profile's queue zones are among the names above, so this read is within the same bound.
        var published = (await ExecuteSqlAsync<TextRow>("""
            SELECT z.name AS "Value" FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND z.kind = 'Queue' AND p.status = 'Published' LIMIT :cap
            """, new Dictionary<string, object> { ["site"] = siteCode, ["cap"] = Limits.MaxZones + 1 }, ct)).Select(r => r.Value).ToHashSet(StringComparer.Ordinal);
        var withData = read.Hours.Select(h => h.ZoneKey).ToHashSet(StringComparer.Ordinal);
        var lanes = zones.ToDictionary(z => z.Name, z => z.Lane, StringComparer.Ordinal);
        var reportZones = published.Concat(withData.Select(k => k[(siteCode.Length + 1)..])).Distinct(StringComparer.Ordinal)
            .Select(name => new ReportZone(siteCode + "/" + name, name, lanes.GetValueOrDefault(name)))
            .ToList();

        var admin = Enumerable.Contains(roles, RoleCodes.SystemAdministrator, StringComparer.Ordinal);
        // ARV-181b2, ARV-187: alerts on a border-only metric only for roles holding ArrivalWaveLanes.View, whatever their owner and
        // escalation roles (the on-demand report, its CSV and the scheduled deliveries all read here with their reader's roles).
        // ARV-180: and alerts at all only for roles holding Alert.View (ReportAlerts), an alert without an owner included.
        var alerts = await ExecuteSqlAsync<AlertRow>("""
            SELECT rule_code AS "RuleCode", rule_name AS "RuleName", zone_name AS "ZoneName", device_code AS "DeviceCode", severity AS "Severity",
                   state AS "State", raised_utc AS "RaisedUtc", COALESCE(cleared_utc, resolved_utc) AS "ClearedUtc"
            FROM alert
            WHERE site_code = :site AND raised_utc >= :from AND raised_utc < :to AND :alertView
              AND (:admin OR owner_role IS NULL OR owner_role IN (:roles) OR (escalated_utc IS NOT NULL AND escalate_to_role IN (:roles)))
              AND (:border OR metric NOT IN (:borderMetrics))
            ORDER BY raised_utc LIMIT 5000
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode,
            ["from"] = from,
            ["to"] = to,
            ["admin"] = admin,
            // An empty IN list is not valid SQL; a role code that cannot exist stands in for "none".
            ["roles"] = roles.Count > 0 ? roles.ToList() : ["-"],
            ["border"] = BorderAlerts.Receives(roles),
            ["borderMetrics"] = BorderAlerts.MetricNames,
            ["alertView"] = ReportAlerts.Receives(roles)
        }, ct);

        var outages = await ExecuteSqlAsync<OutageRow>(OutagesSql, new Dictionary<string, object> { ["prefix"] = siteCode + "/%", ["from"] = from, ["to"] = to, ["cap"] = Limits.MaxOutages + 1 }, ct);
        if (outages.Count > Limits.MaxOutages)
            return TooLarge();
        var devices = await ExecuteSqlAsync<DeviceRow>("""
            SELECT d.code AS "Code", d.queue_zone_name AS "ZoneName", d.state AS "State", h.last_seen_on AS "LastSeenOn"
            FROM device d LEFT JOIN device_heartbeat h ON h.device_id = d.id
            WHERE d.site_code = :site AND d.deleted_on IS NULL AND d.state <> 'Retired'
            ORDER BY d.code LIMIT :cap
            """, new Dictionary<string, object> { ["site"] = siteCode, ["cap"] = Limits.MaxDevices + 1 }, ct);
        if (devices.Count > Limits.MaxDevices)
            return TooLarge();
        // Each device's outages once, not a scan of every outage per device.
        var outagesOf = outages.ToLookup(o => o.DeviceCode, StringComparer.Ordinal);
        var reportDevices = devices.Select(d =>
        {
            var spans = outagesOf[d.Code]
                .Select(o => (FromUtc: Utc(o.FromUtc), ToUtc: o.Closed ? Utc(o.ToUtc) : (DateTime?)null)).ToList();
            // An outage the stream has not recorded yet: the registry has the device offline since it was last heard.
            if (d.State == "Offline" && spans.All(s => s.ToUtc is not null))
                spans.Add((d.LastSeenOn is { } seen ? Utc(seen) : from, null));
            return new ReportDevice(d.Code, d.ZoneName, spans);
        }).ToList();

        return new Result<DailyReport>(DailyReports.Build(new DailyReportInput(
            siteCode, date, tz, reportZones,
            [],
            [.. alerts.Select(a => new ReportAlert(a.RuleCode, a.RuleName, a.ZoneName, a.DeviceCode, a.Severity, a.State, Utc(a.RaisedUtc),
                a.ClearedUtc is { } c ? Utc(c) : null))],
            reportDevices,
            nowUtc,
            read.Hours)));
    }

    /// <summary>The zone hours of [<paramref name="from"/>, <paramref name="to"/>) for a report on demand.</summary>
    internal Task<HoursRead> ReadHoursAsync(string siteCode, DateTime from, DateTime to, CancellationToken ct) => ReadHoursAsync(siteCode, from, to, ReadFor.OnDemand, ct);

    /// <summary>
    /// The site's zone hours of [<paramref name="from"/>, <paramref name="to"/>) through <see cref="HoursSql"/>, at most
    /// <see cref="DailyReportLimits.MaxZoneHours"/> plus one; the merged pairs are counted row by row and no histogram is read once
    /// the count passes <see cref="DailyReportLimits.MaxHistogramPairs"/> (the caller refuses such a day).
    /// </summary>
    internal async Task<HoursRead> ReadHoursAsync(string siteCode, DateTime from, DateTime to, ReadFor readFor, CancellationToken ct)
    {
        long pairs = 0;
        var rows = await ReadSqlAsync(HoursSql, new Dictionary<string, object>
        {
            ["first"] = readFor == ReadFor.Delivery ? Limits.DatabaseSlots : 1,
            ["slots"] = Limits.DatabaseSlots,
            ["lockClass"] = SlotLockClass,
            ["prefix"] = siteCode + "/%",
            ["from"] = from,
            ["to"] = to,
            ["buckets"] = WaitStatistics.Buckets,
            ["cap"] = Limits.MaxZoneHours + 1
        }, r =>
        {
            pairs += r.GetInt32(10);
            return RowOf(r, pairs <= Limits.MaxHistogramPairs);
        }, ct);
        return new HoursRead(rows.Count > 0 && rows[0].Slotted, pairs, [.. rows.Where(r => r.Hour is not null).Select(r => r.Hour)]);
    }

    /// <summary>
    /// A row of <see cref="HoursSql"/> by position, its histogram read only when <paramref name="withHistogram"/>; the merged
    /// histogram's two arrays are as long as each other.
    /// </summary>
    private static HoursRow RowOf(System.Data.Common.DbDataReader r, bool withHistogram)
    {
        var slotted = r.GetInt32(9) > 0;
        if (r.IsDBNull(0))
            return new HoursRow(slotted, null);
        var buckets = !withHistogram || r.IsDBNull(7) ? [] : r.GetFieldValue<int[]>(7);
        var counts = !withHistogram || r.IsDBNull(8) ? [] : r.GetFieldValue<long[]>(8);
        var histogram = new (int Bucket, long Count)[Math.Min(buckets.Length, counts.Length)];
        for (var i = 0; i < histogram.Length; i++)
            histogram[i] = (buckets[i], counts[i]);
        return new HoursRow(slotted, new ReportHour(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt32(4),
            r.GetBoolean(5), r.GetBoolean(6), histogram));
    }

    /// <summary>A day's zone hours as read: whether the statement had a slot, the merged pairs it counted, and the hours.</summary>
    internal sealed record HoursRead(bool Slotted, long Pairs, List<ReportHour> Hours);

    private sealed record HoursRow(bool Slotted, ReportHour Hour);

    private static Result<DailyReport> TooLarge() => Result.Error<DailyReport>(ReportErrors.TooLarge);

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private sealed class TextRow
    {
        public string Value { get; set; }
    }

    private sealed class ZoneRow
    {
        public string Name { get; set; }
        public string Lane { get; set; }
    }

    private sealed class AlertRow
    {
        public string RuleCode { get; set; }
        public string RuleName { get; set; }
        public string ZoneName { get; set; }
        public string DeviceCode { get; set; }
        public string Severity { get; set; }
        public string State { get; set; }
        public DateTime RaisedUtc { get; set; }
        public DateTime? ClearedUtc { get; set; }
    }

    private sealed class OutageRow
    {
        public string DeviceCode { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public bool Closed { get; set; }
    }

    private sealed class DeviceRow
    {
        public string Code { get; set; }
        public string ZoneName { get; set; }
        public string State { get; set; }
        public DateTime? LastSeenOn { get; set; }
    }
}
