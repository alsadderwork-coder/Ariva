using System.Globalization;
using Ariva.Core;
using Ariva.Core.Reports;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Reports;

/// <summary>
/// Reads one site's local day for the daily report (ARV-060) and builds it (<see cref="DailyReports"/>): the stored
/// minutes of its queue zones with their histograms, the alerts raised that day that the given roles see (the same rule
/// as the alert screens: the owner role, the escalation role once escalated, or an administrator), and the outages of
/// its devices. The caller decides who may read the site; this class only reads.
/// </summary>
internal sealed class ReportReader(IUnitOfWork unitOfWork) : SvcDb(unitOfWork)
{
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

    /// <summary>The report of the site's local day; <paramref name="roles"/> decide which alerts it lists.</summary>
    public async Task<DailyReport> ReadAsync(string siteCode, DateOnly date, IReadOnlyCollection<string> roles, DateTime nowUtc, CancellationToken ct)
    {
        var tz = await TimeZoneAsync(siteCode, ct);
        var (from, to) = DailyReports.DayBounds(date, tz);
        var window = new Dictionary<string, object> { ["prefix"] = siteCode + "/%", ["from"] = from, ["to"] = to };

        var minutes = await ExecuteSqlAsync<MinuteRow>("""
            SELECT zone_key AS "ZoneKey", minute_utc AS "MinuteUtc", status AS "Status", entries AS "Entries", waits AS "Waits",
                   queue_length AS "QueueLength", array_to_string(wait_buckets, ',') AS "Buckets", array_to_string(wait_counts, ',') AS "Counts"
            FROM queue_minute WHERE zone_key LIKE :prefix AND minute_utc >= :from AND minute_utc < :to
            ORDER BY zone_key, minute_utc
            """, window, ct);

        // Every queue zone of the published profile, and any other zone with minutes that day (a zone since renamed or
        // retired); the lane category comes from the newest profile that names the zone.
        var zones = await ExecuteSqlAsync<ZoneRow>("""
            SELECT DISTINCT ON (z.name) z.name AS "Name", z.lane_category AS "Lane"
            FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND z.kind = 'Queue' AND p.status IN ('Published', 'Retired')
            ORDER BY z.name, p.version DESC
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct);
        var published = (await ExecuteSqlAsync<TextRow>("""
            SELECT z.name AS "Value" FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND z.kind = 'Queue' AND p.status = 'Published'
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct)).Select(r => r.Value).ToHashSet(StringComparer.Ordinal);
        var withData = minutes.Select(m => m.ZoneKey).ToHashSet(StringComparer.Ordinal);
        var lanes = zones.ToDictionary(z => z.Name, z => z.Lane, StringComparer.Ordinal);
        var reportZones = published.Concat(withData.Select(k => k[(siteCode.Length + 1)..])).Distinct(StringComparer.Ordinal)
            .Select(name => new ReportZone(siteCode + "/" + name, name, lanes.GetValueOrDefault(name)))
            .ToList();

        var admin = Enumerable.Contains(roles, RoleCodes.SystemAdministrator, StringComparer.Ordinal);
        var alerts = await ExecuteSqlAsync<AlertRow>("""
            SELECT rule_code AS "RuleCode", rule_name AS "RuleName", zone_name AS "ZoneName", device_code AS "DeviceCode", severity AS "Severity",
                   state AS "State", raised_utc AS "RaisedUtc", COALESCE(cleared_utc, resolved_utc) AS "ClearedUtc"
            FROM alert
            WHERE site_code = :site AND raised_utc >= :from AND raised_utc < :to
              AND (:admin OR owner_role IS NULL OR owner_role IN (:roles) OR (escalated_utc IS NOT NULL AND escalate_to_role IN (:roles)))
            ORDER BY raised_utc LIMIT 5000
            """, new Dictionary<string, object>
        {
            ["site"] = siteCode,
            ["from"] = from,
            ["to"] = to,
            ["admin"] = admin,
            // An empty IN list is not valid SQL; a role code that cannot exist stands in for "none".
            ["roles"] = roles.Count > 0 ? roles.ToList() : ["-"]
        }, ct);

        var outages = await ExecuteSqlAsync<OutageRow>("""
            SELECT device_code AS "DeviceCode", from_utc AS "FromUtc", to_utc AS "ToUtc", closed AS "Closed"
            FROM zone_outage WHERE zone_key LIKE :prefix AND from_utc < :to AND (to_utc > :from OR NOT closed)
            """, window, ct);
        var devices = await ExecuteSqlAsync<DeviceRow>("""
            SELECT d.code AS "Code", d.queue_zone_name AS "ZoneName", d.state AS "State", h.last_seen_on AS "LastSeenOn"
            FROM device d LEFT JOIN device_heartbeat h ON h.device_id = d.id
            WHERE d.site_code = :site AND d.deleted_on IS NULL AND d.state <> 'Retired'
            ORDER BY d.code
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct);
        var reportDevices = devices.Select(d =>
        {
            var spans = outages.Where(o => o.DeviceCode == d.Code)
                .Select(o => (FromUtc: Utc(o.FromUtc), ToUtc: o.Closed ? Utc(o.ToUtc) : (DateTime?)null)).ToList();
            // An outage the stream has not recorded yet: the registry has the device offline since it was last heard.
            if (d.State == "Offline" && spans.All(s => s.ToUtc is not null))
                spans.Add((d.LastSeenOn is { } seen ? Utc(seen) : from, null));
            return new ReportDevice(d.Code, d.ZoneName, spans);
        }).ToList();

        return DailyReports.Build(new DailyReportInput(
            siteCode, date, tz, reportZones,
            [.. minutes.Select(m => new ReportMinute(m.ZoneKey, Utc(m.MinuteUtc), m.Status == "Final", m.Entries ?? 0, m.Waits ?? 0, m.QueueLength,
                Numbers(m.Buckets), Numbers(m.Counts)))],
            [.. alerts.Select(a => new ReportAlert(a.RuleCode, a.RuleName, a.ZoneName, a.DeviceCode, a.Severity, a.State, Utc(a.RaisedUtc),
                a.ClearedUtc is { } c ? Utc(c) : null))],
            reportDevices,
            nowUtc));
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    private static int[] Numbers(string csv) =>
        string.IsNullOrEmpty(csv) ? [] : [.. csv.Split(',').Select(n => int.Parse(n, NumberStyles.Integer, CultureInfo.InvariantCulture))];

    private sealed class TextRow
    {
        public string Value { get; set; }
    }

    private sealed class ZoneRow
    {
        public string Name { get; set; }
        public string Lane { get; set; }
    }

    private sealed class MinuteRow
    {
        public string ZoneKey { get; set; }
        public DateTime MinuteUtc { get; set; }
        public string Status { get; set; }
        public long? Entries { get; set; }
        public long? Waits { get; set; }
        public int? QueueLength { get; set; }
        public string Buckets { get; set; }
        public string Counts { get; set; }
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
