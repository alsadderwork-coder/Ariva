using Ariva.Core.Queueing;

namespace Ariva.Core.Reports;

/// <summary>One queue zone of the site as the report names it: its key, name and lane category (CIT, VIS, EG...).</summary>
public sealed record ReportZone(string ZoneKey, string Name, string LaneCategory);

/// <summary>A stored minute of a queue zone (queue_minute): entries, realised waits with their histogram, queue length.</summary>
public sealed record ReportMinute(
    string ZoneKey,
    DateTime MinuteUtc,
    bool Final,
    long Entries,
    long Waits,
    int? QueueLength,
    IReadOnlyList<int> Buckets,
    IReadOnlyList<int> Counts);

/// <summary>
/// One queue zone's local hour of the day (ARV-234): <paramref name="Hour"/> counts whole hours from the start of the local day
/// (not the UTC hour: a site half an hour off the hour, or a 25-hour day, is where the two part). The sums of entries and waits,
/// the longest queue, provisional when a minute is not final (or has no status), a missing histogram when a minute with waits has
/// none, and the merged histogram of its minutes in bucket order. Read per zone hour in the database
/// (<c>ReportReader.HoursSql</c>), or computed from minutes by <see cref="DailyReports.HoursOf"/>, the reference the
/// database's read is held to.
/// </summary>
public sealed record ReportHour(
    string ZoneKey,
    int Hour,
    long Entries,
    long Waits,
    int? MaxQueueLength,
    bool Provisional,
    bool HistogramMissing,
    IReadOnlyList<(int Bucket, long Count)> Histogram);

/// <summary>An alert raised during the day at the site, as the report shows it.</summary>
public sealed record ReportAlert(
    string RuleCode,
    string RuleName,
    string ZoneName,
    string DeviceCode,
    string Severity,
    string State,
    DateTime RaisedUtc,
    DateTime? ClearedUtc);

/// <summary>A device of the site and its outages that overlap the day (to_utc null while still open).</summary>
public sealed record ReportDevice(string Code, string ZoneName, IReadOnlyList<(DateTime FromUtc, DateTime? ToUtc)> Outages);

/// <summary>
/// ARV-096d, ARV-234 (CWE-400, CWE-120): the bounds of one daily report's reads; a read that would pass one is refused, never cut
/// short (the alerts keep their own cut of 5,000). The queue minutes are read aggregated per zone and local hour in the database, so
/// the bound is in zone hours: the <see cref="MaxQueueZones"/> queue zones a profile can hold
/// (<see cref="Domain.Entities.ZoneProfile.MaxZones"/>) over a local day of 25 hours, the longest a change of clock makes, with at
/// most <see cref="ZoneHourBuckets"/> merged histogram buckets a zone hour on average (<see cref="MaxHistogramPairs"/>, above the
/// 241 buckets a wait censored at the default 120 minutes fills). Measured at that bound in <c>docs/security/costly-operations.md</c>
/// section 4. The hours statement first takes one of <see cref="DatabaseSlots"/> transaction advisory locks across the cluster
/// without waiting, and a report that finds every slot taken is answered busy (<c>ReportErrors.Busy</c>) rather than queued.
/// </summary>
/// <param name="MaxZoneHours">Zone hours (queue zone and local hour with stored minutes) of the site's day.</param>
/// <param name="MaxZones">Queue zone names of the site's published and retired profiles.</param>
/// <param name="MaxDevices">Devices of the site.</param>
/// <param name="MaxOutages">Device outages that overlap the day.</param>
/// <param name="MaxHistogramPairs">Merged histogram pairs (bucket and count) of the day's zone hours, counted row by row.</param>
/// <param name="DatabaseSlots">Hours statements the database runs at once across the cluster (Proposed, decisions.md): reports on
/// demand take any, scheduled deliveries the last only.</param>
public sealed record DailyReportLimits(
    int MaxZoneHours = DailyReportLimits.MaxQueueZones * 25,
    int MaxZones = 2_000,
    int MaxDevices = 2_000,
    int MaxOutages = 20_000,
    long MaxHistogramPairs = DailyReportLimits.MaxQueueZones * 25L * DailyReportLimits.ZoneHourBuckets,
    int DatabaseSlots = 2)
{
    /// <summary>The queue zones whose full day <see cref="MaxZoneHours"/> holds by default: as many as a profile holds.</summary>
    public const int MaxQueueZones = Domain.Entities.ZoneProfile.MaxZones;

    /// <summary>The merged histogram buckets a zone hour holds on average within <see cref="MaxHistogramPairs"/>.</summary>
    public const int ZoneHourBuckets = 250;

    public static DailyReportLimits Default { get; } = new();
}

/// <summary>
/// Everything a daily report is built from: the site, its local day and the rows read for that day. The queue zones' day comes as
/// <paramref name="Hours"/> when given (the reader's, aggregated in the database, ARV-234); otherwise from
/// <paramref name="Minutes"/> through <see cref="DailyReports.HoursOf"/> (a caller with minutes, the unit tests among them).
/// </summary>
public sealed record DailyReportInput(
    string SiteCode,
    DateOnly Date,
    TimeZoneInfo TimeZone,
    IReadOnlyList<ReportZone> Zones,
    IReadOnlyList<ReportMinute> Minutes,
    IReadOnlyList<ReportAlert> Alerts,
    IReadOnlyList<ReportDevice> Devices,
    DateTime GeneratedUtc,
    IReadOnlyList<ReportHour> Hours = null);

/// <summary>One local hour of a lane. P50 and P90 come from the hour's merged histogram, exact to 30 seconds (F7).</summary>
public sealed record LaneHour(
    string Start,
    string End,
    long Passengers,
    long Waits,
    double? P50Minutes,
    double? P90Minutes,
    int? MaxQueueLength,
    bool Provisional,
    bool HistogramMissing);

/// <summary>A lane's day: totals, the day's P50 and P90 from the merged histogram, its peak hour and the hours.</summary>
public sealed record LaneReport(
    string Zone,
    string LaneCategory,
    long Passengers,
    long Waits,
    double? P50Minutes,
    double? P90Minutes,
    int? MaxQueueLength,
    LaneHour Peak,
    bool Provisional,
    IReadOnlyList<LaneHour> Hours);

/// <summary>An alert of the day in local time; minutes open until it cleared (null while it has not).</summary>
public sealed record AlertLine(
    string RuleCode,
    string RuleName,
    string Zone,
    string Device,
    string Severity,
    string State,
    string RaisedLocal,
    string ClearedLocal,
    int? OpenMinutes);

/// <summary>A device's uptime over the day: the share of the day with no outage, with the outage minutes and count.</summary>
public sealed record DeviceUptime(string Code, string Zone, double UptimePercent, int OutageMinutes, int Outages);

/// <summary>The day's headline figures.</summary>
public sealed record DailyHeadline(
    double? WorstPeakP90Minutes,
    string WorstPeakLane,
    string WorstPeakHour,
    int ZoneHoursAboveTarget,
    int Alerts,
    int CriticalAlerts,
    double? LowestUptimePercent);

/// <summary>
/// The daily report of a site (ARV-060): peaks per lane, P50 and P90 per local hour, the day's alerts and device
/// uptime. Built from stored minutes only; hours that hold provisional minutes, or minutes without a histogram, say so.
/// </summary>
public sealed record DailyReport(
    string SiteCode,
    DateOnly Date,
    string TimeZoneId,
    DateTime FromUtc,
    DateTime ToUtc,
    DateTime GeneratedUtc,
    DailyHeadline Headline,
    IReadOnlyList<LaneReport> Lanes,
    IReadOnlyDictionary<string, int> AlertsBySeverity,
    IReadOnlyList<AlertLine> Alerts,
    IReadOnlyList<DeviceUptime> Devices);

public static class DailyReports
{
    /// <summary>A peak hour needs this many realised waits, so a quiet hour with one long wait is not the day's peak.</summary>
    public const int MinWaitsForPeak = 20;

    /// <summary>The wait target the zone-hours headline counts against, in minutes (the share-within-target default).</summary>
    public const double TargetMinutes = 15;

    /// <summary>At most this many alerts are listed; the counts cover all of them.</summary>
    public const int MaxAlertLines = 500;

    /// <summary>The UTC bounds of a local day: 23 or 25 hours on a daylight-saving change.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) DayBounds(DateOnly date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (LocalToUtc(date.ToDateTime(TimeOnly.MinValue), zone), LocalToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
    }

    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        // A local midnight skipped by a clock change starts the day at the first valid minute after it.
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }

    public static DailyReport Build(DailyReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (from, to) = DayBounds(input.Date, input.TimeZone);
        var hours = HourStarts(from, to);

        var byZone = (input.Hours ?? HoursOf(input.Minutes ?? [], from, to))
            .Where(h => h.Hour >= 0 && h.Hour < hours.Count)
            .ToLookup(h => h.ZoneKey, StringComparer.Ordinal);
        var lanes = input.Zones
            .OrderBy(z => z.LaneCategory ?? "~", StringComparer.Ordinal).ThenBy(z => z.Name, StringComparer.Ordinal)
            .Select(zone => Lane(zone, byZone[zone.ZoneKey].ToDictionary(h => h.Hour), hours, to, input.TimeZone))
            .ToList();

        var alerts = input.Alerts.Where(a => a.RaisedUtc >= from && a.RaisedUtc < to).OrderBy(a => a.RaisedUtc).ThenBy(a => a.RuleCode, StringComparer.Ordinal).ToList();
        var bySeverity = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in alerts)
            bySeverity[a.Severity] = bySeverity.GetValueOrDefault(a.Severity) + 1;
        var alertLines = alerts.Take(MaxAlertLines).Select(a => new AlertLine(
            a.RuleCode, a.RuleName, a.ZoneName, a.DeviceCode, a.Severity, a.State, LocalText(a.RaisedUtc, input.TimeZone),
            a.ClearedUtc is { } cleared ? LocalText(cleared, input.TimeZone) : null,
            a.ClearedUtc is { } end ? (int)Math.Round((end - a.RaisedUtc).TotalMinutes) : null)).ToList();

        var devices = input.Devices.OrderBy(d => d.Code, StringComparer.Ordinal).Select(d => Uptime(d, from, to, input.GeneratedUtc)).ToList();

        var peaks = lanes.Where(l => l.Peak?.P90Minutes is not null).OrderByDescending(l => l.Peak.P90Minutes).ThenBy(l => l.Zone, StringComparer.Ordinal).FirstOrDefault();
        var headline = new DailyHeadline(
            peaks?.Peak.P90Minutes,
            peaks?.Zone,
            peaks?.Peak.Start,
            lanes.Sum(l => l.Hours.Count(h => h.Waits >= MinWaitsForPeak && h.P90Minutes > TargetMinutes)),
            alerts.Count,
            alerts.Count(a => a.Severity == "Critical"),
            devices.Count > 0 ? devices.Min(d => d.UptimePercent) : null);

        return new DailyReport(input.SiteCode, input.Date, input.TimeZone.Id, from, to, input.GeneratedUtc, headline, lanes, bySeverity, alertLines, devices);
    }

    private static List<DateTime> HourStarts(DateTime from, DateTime to)
    {
        var starts = new List<DateTime>();
        for (var t = from; t < to; t = t.AddHours(1))
            starts.Add(t);
        return starts;
    }

    /// <summary>
    /// The zone hours of minutes in [<paramref name="from"/>, <paramref name="to"/>), one per queue zone and local hour with a
    /// minute, by the report's rules for minutes: the sums of entries and waits, the longest queue, provisional when a minute is not
    /// final, a missing histogram when a minute with waits has none, and the merge of the minutes' histograms (each taken up to the
    /// shorter of its two lists, and left out whole when a bucket is outside 0 to the last or a count is negative). The reference
    /// the database's read (<c>ReportReader.HoursSql</c>) is held to; in no particular order.
    /// </summary>
    public static IReadOnlyList<ReportHour> HoursOf(IEnumerable<ReportMinute> minutes, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        return [.. minutes
            .Where(m => m.MinuteUtc >= from && m.MinuteUtc < to)
            .GroupBy(m => (m.ZoneKey, Hour: (int)Math.Floor((m.MinuteUtc - from).TotalHours)))
            .Select(g =>
            {
                var inHour = g.ToList();
                return new ReportHour(
                    g.Key.ZoneKey,
                    g.Key.Hour,
                    inHour.Sum(m => m.Entries),
                    inHour.Sum(m => m.Waits),
                    inHour.Max(m => m.QueueLength),
                    inHour.Any(m => !m.Final),
                    inHour.Any(m => m.Waits > 0 && m.Buckets is not { Count: > 0 }),
                    WaitStatistics.Merge([.. inHour.Where(m => m.Buckets is { Count: > 0 }).Select(Histogram)]));
            })];
    }

    private static LaneReport Lane(ReportZone zone, Dictionary<int, ReportHour> zoneHours, List<DateTime> hours, DateTime dayEnd, TimeZoneInfo tz)
    {
        var rows = new List<LaneHour>();
        var dayHistograms = new List<IReadOnlyList<(int Bucket, long Count)>>();
        for (var i = 0; i < hours.Count; i++)
        {
            var start = hours[i];
            var end = start.AddHours(1) < dayEnd ? start.AddHours(1) : dayEnd;
            // An hour without a stored minute reads as before: no passengers, no waits, no percentiles, nothing provisional.
            var hour = zoneHours.GetValueOrDefault(i);
            var merged = hour?.Histogram ?? [];
            dayHistograms.Add(merged);
            rows.Add(new LaneHour(
                LocalText(start, tz)[^5..],
                LocalText(end, tz)[^5..],
                hour?.Entries ?? 0,
                hour?.Waits ?? 0,
                Round(WaitStatistics.Percentile(merged, 0.5)),
                Round(WaitStatistics.Percentile(merged, 0.9)),
                hour?.MaxQueueLength,
                hour?.Provisional ?? false,
                hour?.HistogramMissing ?? false));
        }

        var day = WaitStatistics.Merge([.. dayHistograms]);
        var peak = rows.Where(h => h.Waits >= MinWaitsForPeak && h.P90Minutes is not null)
            .OrderByDescending(h => h.P90Minutes).ThenBy(h => h.Start, StringComparer.Ordinal).FirstOrDefault();
        return new LaneReport(
            zone.Name,
            zone.LaneCategory,
            rows.Sum(h => h.Passengers),
            rows.Sum(h => h.Waits),
            Round(WaitStatistics.Percentile(day, 0.5)),
            Round(WaitStatistics.Percentile(day, 0.9)),
            rows.Max(h => h.MaxQueueLength),
            peak,
            rows.Any(h => h.Provisional),
            rows);
    }

    private static IReadOnlyList<(int Bucket, long Count)> Histogram(ReportMinute m)
    {
        var count = Math.Min(m.Buckets.Count, m.Counts?.Count ?? 0);
        var pairs = new List<(int Bucket, long Count)>(count);
        for (var i = 0; i < count; i++)
        {
            // A stored histogram that breaks the format is left out of the merge rather than failing the whole report.
            if (m.Buckets[i] is < 0 or >= WaitStatistics.Buckets || m.Counts[i] < 0)
                return [];
            pairs.Add((m.Buckets[i], m.Counts[i]));
        }

        pairs.Sort((a, b) => a.Bucket.CompareTo(b.Bucket));
        return pairs;
    }

    /// <summary>A device's uptime over [from, to) clipped at now (the weekly summary, ARV-119, uses it for its week).</summary>
    internal static DeviceUptime Uptime(ReportDevice device, DateTime from, DateTime to, DateTime now)
    {
        var end = to < now ? to : now;
        var spans = device.Outages
            .Select(o => (Start: o.FromUtc < from ? from : o.FromUtc, End: (o.ToUtc ?? end) > end ? end : o.ToUtc ?? end))
            .Where(o => o.End > o.Start)
            .OrderBy(o => o.Start)
            .ToList();
        // Overlapping outages of one device (one per zone it serves) count once.
        var minutes = 0.0;
        DateTime? openStart = null, openEnd = null;
        foreach (var (start, stop) in spans)
        {
            if (openEnd is { } e && start <= e)
            {
                if (stop > e)
                    openEnd = stop;
                continue;
            }

            if (openStart is { } s1 && openEnd is { } e1)
                minutes += (e1 - s1).TotalMinutes;
            openStart = start;
            openEnd = stop;
        }

        if (openStart is { } s2 && openEnd is { } e2)
            minutes += (e2 - s2).TotalMinutes;
        var window = Math.Max(1, (end > from ? end - from : TimeSpan.Zero).TotalMinutes);
        var uptime = Math.Clamp(100 * (1 - (minutes / window)), 0, 100);
        return new DeviceUptime(device.Code, device.ZoneName, Math.Round(uptime, 2), (int)Math.Round(minutes), spans.Count);
    }

    private static double? Round(double? minutes) => minutes is { } m ? Math.Round(m, 2) : null;

    /// <summary>An instant as "YYYY-MM-DD HH:MM" in the site's time zone.</summary>
    public static string LocalText(DateTime utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
