using System.Collections;
using System.Data.Common;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Core.Staffing;
using Ariva.Infra.Staffing;
using StaffingRow = Ariva.Infra.Staffing.StaffingReadBack.StaffingRow;

namespace Ariva.Infra.Alerting;

/// <summary>One target of a rule: a queue zone, or a device of it for a sensor rule.</summary>
public sealed record AlertTarget(string ZoneName, string DeviceCode)
{
    public string Key => DeviceCode is null ? ZoneName : ZoneName + "|" + DeviceCode;
}

/// <summary>A target's minutes in a range, in time order, and the last minute there is data for.</summary>
public sealed record AlertTargetSeries(AlertTarget Target, IReadOnlyList<AlertMinute> Minutes);

/// <summary>
/// The minutes a rule is judged on (ARV-038), read from what the stream stored, for the live evaluation and the backtest
/// alike so that both judge the same values:
/// <list type="bullet">
/// <item><c>Nowcast</c>: the queue minute's nowcast, nothing when it is missing or the queue length is degraded (a sensor outage or a
/// stale reading, as the prototype skips a degraded zone); with the queue length for the minimum-queue gate. A nowcast flagged
/// only because its throughput comes from the exit rate (no desk state before ARV-049) is judged;</item>
/// <item><c>QueueLength</c>: the queue minute's queue length;</item>
/// <item><c>BinP90</c>: the largest P90 among the zone's bins that ended within the last 150 minutes (latest revision), with that bin.
/// The read reaches back that look-back and one bin (<see cref="BinReadFrom"/>, ARV-228a), since the stream writes bins of
/// <see cref="LongestBin"/> only; before, it reached back a day more, for bin lengths no writer produces;</item>
/// <item><c>SensorOffline</c>: per commissioned device of the zones, 1 for a minute inside one of its outages (<c>zone_outage</c>, and an
/// outage still open while the registry has it offline), otherwise 0;</item>
/// <item><c>PredictedNowcast</c>: <see cref="PredictedWait"/> from the queue minute's length and throughput and the arrival wave over the
/// lead time (nothing while the length is degraded), for the minute of the projected peak. The wave of a lane queue is its
/// share of the arrival wave (<see cref="IArrivalWaveSource.ArrivalsAsync"/>, read here only), so the value carries the lane
/// split: border data since ARV-187, the rule and its alerts reach border roles only (<see cref="Ariva.Core.Security.BorderAlerts"/>);</item>
/// <item><c>OverflowOccupied</c> (ARV-115): per minute, 1 when a watched overflow band held anyone in it (TC-19, decided 2026-10-06:
/// the band's highest reading in the minute above zero), 0 when the watched bands that reported held no one, nothing for a minute
/// without a band reading, from <c>overflow_minute</c>. Only bands with a reading in the minute count: a band silent within the
/// freshness window or Unknown after it (the stream's <c>OverflowDetected</c> Unknown) has no row, so it is neither occupied nor
/// empty and adds neither a 1 nor a 0; a zone whose only band is Unknown has no value, and the rule keeps its state until a
/// reading comes. A queue zone's target watches every band of the zone; an overflow zone's target watches that band only;</item>
/// <item><c>DesksBelowPlan</c> (ARV-181b2, F16): per lane minute the job computed, open desks below recommendation
/// (<see cref="StaffingAlertSeries.DesksBelow"/>) from the minute's interval 0 in <c>lane_staffing</c> (the quarter hour now),
/// only when that row starts at its own minute, passes the staffing API's read-back check
/// (<see cref="StaffingReadBack.Plausible"/>) and has no gap (<see cref="StaffingMinute"/>); a minute the job missed has no
/// row and so no value. The rows are immutable (the first computation of a minute stands), so the live tick, which takes a
/// zone's minutes up to its latest computed one (<see cref="LatestStaffingMinutesAsync"/>), and a backtest judge the same
/// values. Border data: the rule and its alerts reach border roles only (<see cref="Ariva.Core.Security.BorderAlerts"/>);</item>
/// <item><c>ConservationResidual</c>, <c>TrackCompletion</c> and <c>OccupancyOutOfRange</c> (ARV-114b): per queue minute, the
/// settled value of the zone's bin that ended last by the minute's end, from the latest revision of its <c>zone_health_bin</c>
/// row (<see cref="HealthAlertSeries"/>); the live tick takes a zone's minutes only up to the last whose bin is settled
/// (<see cref="LatestHealthMinutesAsync"/>).</item>
/// </list>
/// Minutes are those after <c>fromUtc</c> up to and including <c>toUtc</c>. A rule's targets are its zones that are queue or
/// overflow zones of the site's published profile (queue zones only for a health-check rule and for open desks below
/// recommendation, <see cref="AlertRuleValues.WatchesQueueZonesOnly"/>; or the commissioned devices of those zones).
/// <para>
/// Every read maps its rows by column position with the reader's typed getters (<see cref="IStorageProvider.ReadSqlAsync{T}"/>,
/// ARV-228b), never by reflection, and a zone's rows are taken where the read put them, never copied per zone: a backtest at its
/// bound reads 92,160 zone minutes, and the memory it takes follows the minutes, not the rows' mapping.
/// </para>
/// </summary>
public sealed class AlertInputs(IUnitOfWork unitOfWork, IArrivalWaveSource arrivals)
{
    /// <summary>How far back a P90 rule looks for bins, as in the prototype.</summary>
    public static readonly TimeSpan BinLookBack = TimeSpan.FromMinutes(150);

    /// <summary>The most devices one sensor rule watches.</summary>
    public const int MaxDevices = 1000;

    private IStorageProvider Storage => unitOfWork.StorageProvider;

    /// <summary>The targets of a rule in a site: its zones, or the commissioned devices of its zones for a sensor rule.</summary>
    public async Task<IReadOnlyList<AlertTarget>> TargetsAsync(string siteCode, AlertRuleValues rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var zones = rule.Zones.Distinct(StringComparer.Ordinal).ToList();
        if (zones.Count == 0)
            return [];
        // Only zones of the site's published profile (queue and overflow zones, as a rule is checked when it is saved):
        // a zone that left the profile leaves the rule's targets, and its open alert is resolved.
        var published = (await Storage.ReadSqlAsync("""
            SELECT z.name AS "Name", z.kind AS "Kind" FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND p.status = 'Published' AND z.kind IN ('Queue', 'Overflow') AND z.name = ANY(:zones)
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zones"] = zones }, r => new ZoneRow(r.GetString(0), r.GetString(1), null), ct))
            // ARV-114b: health checks are stored per queue zone, so a health-check rule watches queue zones only; ARV-181b2: so
            // is the staffing recommendation (per lane, a queue zone).
            .Where(z => !AlertRuleValues.WatchesQueueZonesOnly(rule.Metric) || z.Kind == nameof(ZoneKind.Queue))
            .Select(z => z.Name).ToHashSet(StringComparer.Ordinal);
        zones = [.. zones.Where(published.Contains)];
        if (zones.Count == 0)
            return [];
        if (rule.Metric != AlertMetric.SensorOffline)
            return [.. zones.Select(z => new AlertTarget(z, null))];
        return await Storage.ReadSqlAsync("""
            SELECT queue_zone_name AS "ZoneName", code AS "Code" FROM device
            WHERE site_code = :site AND deleted_on IS NULL AND state IN ('Online', 'Degraded', 'Offline') AND queue_zone_name = ANY(:zones)
            ORDER BY queue_zone_name, code LIMIT 1000
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zones"] = zones }, r => new AlertTarget(r.GetString(0), r.GetString(1)), ct);
    }

    /// <summary>
    /// The latest minute whose live part (queue length and nowcast) the stream stored, for each of the zones (none for a
    /// zone without one). A minute's counts are stored while it runs and its live part about half a minute after it ends;
    /// a row with counts only is not ready, or the tick would take the minute without its nowcast and never judge it
    /// (ARV-064). The stream writes every live minute with a queue length, so that column marks the live part.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> LatestMinutesAsync(string siteCode, IReadOnlyCollection<string> zones, DateTime notAfterUtc, CancellationToken ct)
    {
        if (zones.Count == 0)
            return new Dictionary<string, DateTime>();
        var keys = zones.Select(z => ZoneKeys.For(siteCode, z)).ToList();
        var rows = await Storage.ReadSqlAsync("""
            SELECT zone_key AS "ZoneKey", max(minute_utc) AS "MinuteUtc" FROM queue_minute
            WHERE zone_key = ANY(:keys) AND minute_utc <= :notAfter AND queue_length IS NOT NULL GROUP BY zone_key
            """, new Dictionary<string, object> { ["keys"] = keys, ["notAfter"] = notAfterUtc }, LatestOf, ct);
        var prefix = siteCode + "/";
        return rows.Where(r => r.MinuteUtc is not null)
            .ToDictionary(r => r.ZoneKey[prefix.Length..], r => Utc(r.MinuteUtc.Value), StringComparer.Ordinal);
    }

    /// <summary>
    /// Each target's minutes after <paramref name="fromUtc"/> up to <paramref name="toUtc"/>; null when a health-check range
    /// holds more stored bins than <see cref="MaxHealthRows"/>, a staffing range more rows than <see cref="MaxStaffingRows"/>, or a
    /// sensor rule's range more outages than <see cref="MaxOutageRows"/> (refused, never cut short; CWE-120).
    /// </summary>
    public async Task<IReadOnlyList<AlertTargetSeries>> ReadAsync(string siteCode, AlertRuleValues rule, IReadOnlyList<AlertTarget> targets, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || toUtc <= fromUtc)
            return [];
        return rule.Metric switch
        {
            AlertMetric.Nowcast or AlertMetric.QueueLength => await QueueMinutesAsync(siteCode, rule, targets, fromUtc, toUtc, ct),
            AlertMetric.BinP90 => await BinsAsync(siteCode, targets, fromUtc, toUtc, ct),
            AlertMetric.SensorOffline => await OutagesAsync(siteCode, targets, fromUtc, toUtc, ct),
            AlertMetric.PredictedNowcast => await PredictedAsync(siteCode, rule, targets, fromUtc, toUtc, ct),
            AlertMetric.OverflowOccupied => await OverflowAsync(siteCode, targets, fromUtc, toUtc, ct),
            AlertMetric.ConservationResidual or AlertMetric.TrackCompletion or AlertMetric.OccupancyOutOfRange => await HealthAsync(siteCode, rule, targets, fromUtc, toUtc, ct),
            AlertMetric.DesksBelowPlan => await StaffingAsync(siteCode, targets, fromUtc, toUtc, ct),
            _ => [.. targets.Select(t => new AlertTargetSeries(t, []))]
        };
    }

    #region Staffing (ARV-181b2)

    /// <summary>
    /// The most <c>lane_staffing</c> rows one read takes (CWE-120, CWE-400): a backtest of a day over the 64 zones a rule may
    /// watch holds 92,160 minutes. Beyond it the read is refused (null), never cut short.
    /// </summary>
    public const int DefaultMaxStaffingRows = 100_000;

    /// <summary>How far back <c>lane_staffing</c> keeps rows (script 0053's retention, the backtest's history).</summary>
    public static readonly TimeSpan StaffingHistory = TimeSpan.FromDays(90);

    /// <summary>The bound of <see cref="DefaultMaxStaffingRows"/> for this instance (tests set a smaller one).</summary>
    public int MaxStaffingRows { get; init; } = DefaultMaxStaffingRows;

    /// <summary>
    /// For an open desks below recommendation rule: each zone's latest minute the staffing job computed (interval 0 of
    /// <c>lane_staffing</c>) at or before <paramref name="notAfterUtc"/> and within the table's history, none for a zone
    /// without one. The job writes a site's minutes in order under its lock and never fills a missed minute later, so no
    /// minute appears behind a zone's latest once the tick has taken it.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> LatestStaffingMinutesAsync(string siteCode, IReadOnlyCollection<string> zones, DateTime notAfterUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var keys = zones.Where(z => ZoneKeys.Fits(siteCode, z)).Select(z => ZoneKeys.For(siteCode, z)).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
            return new Dictionary<string, DateTime>();
        var rows = await Storage.ReadSqlAsync("""
            SELECT zone_key AS "ZoneKey", max(minute_utc) AS "MinuteUtc" FROM lane_staffing
            WHERE zone_key = ANY(:keys) AND interval_index = 0 AND minute_utc <= :notAfter AND minute_utc > :floor GROUP BY zone_key
            """, new Dictionary<string, object> { ["keys"] = keys, ["notAfter"] = notAfterUtc, ["floor"] = notAfterUtc - StaffingHistory }, LatestOf, ct);
        var prefix = siteCode + "/";
        return rows.Where(r => r.MinuteUtc is not null && r.ZoneKey.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(r => r.ZoneKey[prefix.Length..], r => Utc(r.MinuteUtc.Value), StringComparer.Ordinal);
    }

    /// <summary>
    /// Interval 0 of each computed minute of the zones in a range, at most <c>:limit</c> rows. A range of the partial index of interval
    /// 0 (script 0068, ARV-228a), so the read touches no row of intervals 1 to 12 (OutageReadPlanTests explains it as written here).
    /// The columns in the order <see cref="StaffingOf"/> reads them.
    /// </summary>
    internal const string StaffingSql = """
        SELECT zone_key AS "ZoneKey", minute_utc AS "MinuteUtc", CAST(interval_index AS integer) AS "IntervalIndex", lane AS "Lane",
               start_utc AS "StartUtc", CAST(minutes AS integer) AS "Minutes", arrivals AS "Arrivals", CAST(recommended AS integer) AS "Recommended",
               bound AS "Bound", at_limit AS "AtLimit", shortfall AS "Shortfall", wait_recommended AS "WaitRecommended", wait_open AS "WaitOpen",
               no_service_open AS "NoServiceOpen", gap AS "Gap", lane_gap AS "LaneGap", queue_now AS "QueueNow", CAST(open_now AS integer) AS "OpenNow",
               open_now_degraded AS "OpenNowDegraded", CAST(desks AS integer) AS "Desks", target_minutes AS "TargetMinutes", busy_cap AS "BusyCap",
               throughput_per_desk AS "ThroughputPerDesk", throughput_source AS "ThroughputSource"
          FROM lane_staffing
         WHERE zone_key = ANY(:keys) AND interval_index = 0 AND minute_utc > :from AND minute_utc <= :to
         ORDER BY zone_key, minute_utc
         LIMIT :limit
        """;

    /// <summary>
    /// The open desks below recommendation minutes of the targets: interval 0 of each computed minute after
    /// <paramref name="fromUtc"/> up to <paramref name="toUtc"/>, in one constant query for every target (each by its site's
    /// key, so another site's lanes of the same name are never read); null when the range holds more rows than
    /// <see cref="MaxStaffingRows"/>.
    /// </summary>
    private async Task<IReadOnlyList<AlertTargetSeries>> StaffingAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct)
    {
        var keys = targets.Where(t => ZoneKeys.Fits(siteCode, t.ZoneName)).Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        // Each row judged as it is read: the minute is kept, the row is not.
        var rows = keys.Count == 0 ? [] : await Storage.ReadSqlAsync(StaffingSql,
            new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc, ["to"] = toUtc, ["limit"] = MaxStaffingRows + 1 },
            r => (ZoneKey: r.GetString(0), Minute: StaffingMinute(StaffingOf(r))), ct);
        if (rows.Count > MaxStaffingRows)
            return null;
        var byZone = new ZoneRuns<(string ZoneKey, AlertMinute Minute)>(rows, r => r.ZoneKey);
        return
        [
            .. targets.Select(t => new AlertTargetSeries(t, ZoneKeys.Fits(siteCode, t.ZoneName)
                ? byZone.Select(ZoneKeys.For(siteCode, t.ZoneName), r => r.Minute)
                : []))
        ];
    }

    /// <summary>One row of <see cref="StaffingSql"/>, by position, as the staffing API's read-back check takes it.</summary>
    internal static StaffingRow StaffingOf(DbDataReader r) => new()
    {
        ZoneKey = r.GetString(0), MinuteUtc = Utc(r.GetDateTime(1)), IntervalIndex = r.GetInt32(2), Lane = r.GetString(3), StartUtc = Utc(r.GetDateTime(4)),
        Minutes = r.GetInt32(5), Arrivals = DoubleAt(r, 6), Recommended = IntAt(r, 7), Bound = r.GetString(8), AtLimit = r.GetBoolean(9), Shortfall = IntAt(r, 10),
        WaitRecommended = DoubleAt(r, 11), WaitOpen = DoubleAt(r, 12), NoServiceOpen = r.GetBoolean(13), Gap = r.GetString(14), LaneGap = r.GetString(15),
        QueueNow = DoubleAt(r, 16), OpenNow = IntAt(r, 17), OpenNowDegraded = r.GetBoolean(18), Desks = r.GetInt32(19), TargetMinutes = r.GetDouble(20),
        BusyCap = r.GetDouble(21), ThroughputPerDesk = DoubleAt(r, 22), ThroughputSource = TextAt(r, 23)
    };

    /// <summary>
    /// One stored row as the alert judges it (F16, CWE-501): its minute, with open desks below recommendation only when the
    /// row is the minute's current interval (index 0, starting at the minute itself), passes the staffing API's read-back
    /// check (<see cref="StaffingReadBack.Plausible"/>) and has no gap; otherwise the minute has no value (skipped, neither
    /// counting toward nor breaking a run). <see cref="StaffingAlertSeries.DesksBelow"/> leaves an unknown or degraded open
    /// count without a value too.
    /// </summary>
    internal static AlertMinute StaffingMinute(StaffingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var judged = row.IntervalIndex == 0 && row.StartUtc == row.MinuteUtc && row.Gap == nameof(StaffingGap.None) && StaffingReadBack.Plausible(row);
        return new AlertMinute(Utc(row.MinuteUtc), judged ? StaffingAlertSeries.DesksBelow(row.Recommended, row.OpenNow, row.OpenNowDegraded) : null);
    }

    #endregion

    /// <summary>The most health rows one read takes (ARV-114b, CWE-120): beyond it the read is refused (null), never cut short.</summary>
    public const int DefaultMaxHealthRows = 50_000;

    /// <summary>The bound of <see cref="DefaultMaxHealthRows"/> for this instance (tests set a smaller one).</summary>
    public int MaxHealthRows { get; init; } = DefaultMaxHealthRows;

    /// <summary>
    /// The longest bin the stream writes: <see cref="BinSettings"/> is not configured per host or site, so every bin is its
    /// default length (15 minutes). A minute's bin starts at most two of them before the minute.
    /// </summary>
    public static readonly TimeSpan LongestBin = new BinSettings().BinLength;

    /// <summary>
    /// Groups items whose times lie within <paramref name="span"/> of the group's earliest, in time order, so one read covers
    /// a group without stretching over the gap to items far before or after it (CWE-120): a zone whose sensors stopped a month
    /// ago is read in its own small window, never with every other zone over the month between.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> Clusters<T>(IEnumerable<T> items, Func<T, DateTime> at, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(at);
        var clusters = new List<IReadOnlyList<T>>();
        List<T> current = null;
        var first = DateTime.MinValue;
        foreach (var item in items.OrderBy(at))
        {
            var time = at(item);
            if (current is null || time - first > span)
            {
                current = [];
                clusters.Add(current);
                first = time;
            }

            current.Add(item);
        }

        return clusters;
    }

    /// <summary>
    /// The latest revision of each health row of the zones that can govern a minute ending after <paramref name="governFromUtc"/>
    /// (a bin [s, s + L) governs the minutes ending from s + L to before s + 2L) and that ended by <paramref name="endUpToUtc"/>,
    /// at most <paramref name="limit"/> rows: null beyond it (refused, never cut short; CWE-120).
    /// </summary>
    private async Task<Dictionary<string, List<HealthBinReading>>> HealthRowsAsync(IReadOnlyCollection<string> keys, DateTime governFromUtc, DateTime endUpToUtc,
        int limit, CancellationToken ct)
    {
        if (keys.Count == 0 || endUpToUtc <= governFromUtc - LongestBin - LongestBin)
            return new Dictionary<string, List<HealthBinReading>>(StringComparer.Ordinal);
        var rows = await Storage.ReadSqlAsync("""
            SELECT DISTINCT ON (zone_key, start_utc) zone_key AS "ZoneKey", start_utc AS "StartUtc", length_minutes AS "LengthMinutes", status AS "Status",
                   conservation_residual AS "ConservationResidual", occupancy_end AS "OccupancyEnd", tracks_entered AS "TracksEntered",
                   tracks_exited AS "TracksExited", capacity_minutes AS "CapacityMinutes", minutes_outside_capacity AS "MinutesOutsideCapacity"
            FROM zone_health_bin
            WHERE zone_key = ANY(:keys) AND start_utc > :scanFrom AND start_utc < :endUpTo
              AND start_utc + length_minutes * interval '1 minute' <= :endUpTo
              AND start_utc + 2 * length_minutes * interval '1 minute' > :governFrom
            ORDER BY zone_key, start_utc, revision DESC
            LIMIT :limit
            """, new Dictionary<string, object>
        {
            ["keys"] = keys.ToList(), ["scanFrom"] = governFromUtc - LongestBin - LongestBin, ["endUpTo"] = endUpToUtc, ["governFrom"] = governFromUtc,
            ["limit"] = limit + 1
        }, r => (ZoneKey: r.GetString(0), Reading: new HealthBinReading(Utc(r.GetDateTime(1)), r.GetInt32(2),
            r.GetString(3) == nameof(BinStatus.Final) ? BinStatus.Final : BinStatus.Provisional,
            LongAt(r, 4), IntAt(r, 5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetInt32(9))), ct);
        if (rows.Count > limit)
            return null;
        var byZone = new Dictionary<string, List<HealthBinReading>>(StringComparer.Ordinal);
        foreach (var (key, reading) in rows)
        {
            if (!byZone.TryGetValue(key, out var list))
                byZone[key] = list = [];
            list.Add(reading);
        }

        return byZone;
    }

    /// <summary>The health-check minutes of the targets; null when the range holds more rows than <see cref="MaxHealthRows"/>.</summary>
    private async Task<IReadOnlyList<AlertTargetSeries>> HealthAsync(string siteCode, AlertRuleValues rule, IReadOnlyList<AlertTarget> targets, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct)
    {
        var keys = targets.Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        // The bins that can govern a minute after fromUtc (whose end falls after fromUtc + 1 minute) and that ended by toUtc's end.
        var health = await HealthRowsAsync(keys, fromUtc.AddMinutes(1), toUtc.AddMinutes(1), MaxHealthRows, ct);
        if (health is null)
            return null;
        var minutes = await MinuteRowsAsync(siteCode, targets, fromUtc, toUtc, ct);
        return
        [
            .. targets.Select(t =>
            {
                var key = ZoneKeys.For(siteCode, t.ZoneName);
                return new AlertTargetSeries(t, HealthAlertSeries.Minutes(rule.Metric, minutes.Of(key).Select(r => (r.MinuteUtc, r.QueueLength)), health.GetValueOrDefault(key) ?? []));
            })
        ];
    }

    /// <summary>
    /// For a health-check rule (ARV-114b): the latest minute the live tick may take for each zone, the zone's latest live
    /// minute (<see cref="LatestMinutesAsync"/>) held back before the first minute of a bin that is not settled yet
    /// (<see cref="HealthAlertSeries.SettledThrough"/>), so the tick never judges a minute without the value a backtest
    /// would find for it later. Each zone needs only the bins ending within the settle limit before its own latest minute, so
    /// zones are read in clusters of nearby latest minutes (<see cref="Clusters{T}"/>), every zone in a window of at most twice
    /// the settle limit and two bins; null when the reads together exceed <see cref="MaxHealthRows"/> (CWE-120).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> LatestHealthMinutesAsync(string siteCode, AlertMetric metric, IReadOnlyCollection<string> zones,
        DateTime notAfterUtc, CancellationToken ct)
    {
        var latest = await LatestMinutesAsync(siteCode, zones, notAfterUtc, ct);
        if (latest.Count == 0)
            return latest;
        var health = new Dictionary<string, List<HealthBinReading>>(StringComparer.Ordinal);
        var read = 0;
        foreach (var cluster in Clusters(latest, l => l.Value, HealthAlertSeries.SettleLimit))
        {
            var rows = await HealthRowsAsync([.. cluster.Select(l => ZoneKeys.For(siteCode, l.Key))], cluster[0].Value.AddMinutes(1) - HealthAlertSeries.SettleLimit,
                cluster[^1].Value.AddMinutes(1), MaxHealthRows - read, ct);
            if (rows is null)
                return null;
            foreach (var (key, list) in rows)
            {
                health[key] = list;
                read += list.Count;
            }
        }

        return latest.ToDictionary(l => l.Key,
            l => HealthAlertSeries.SettledThrough(metric, l.Value, health.GetValueOrDefault(ZoneKeys.For(siteCode, l.Key)) ?? []), StringComparer.Ordinal);
    }

    /// <summary>The queue minutes of the targets' zones in a range, each zone's in time order.</summary>
    private async Task<ZoneRuns<MinuteRow>> MinuteRowsAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var keys = targets.Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        var rows = await Storage.ReadSqlAsync("""
            SELECT zone_key AS "ZoneKey", minute_utc AS "MinuteUtc", queue_length AS "QueueLength", nowcast_minutes AS "Nowcast",
                   length_degraded AS "LengthDegraded", throughput_per_minute AS "Throughput"
            FROM queue_minute WHERE zone_key = ANY(:keys) AND minute_utc > :from AND minute_utc <= :to
            ORDER BY zone_key, minute_utc
            """, new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc, ["to"] = toUtc },
            r => new MinuteRow(r.GetString(0), Utc(r.GetDateTime(1)), IntAt(r, 2), DoubleAt(r, 3), BoolAt(r, 4), DoubleAt(r, 5)), ct);
        return new ZoneRuns<MinuteRow>(rows, r => r.ZoneKey);
    }

    private async Task<IReadOnlyList<AlertTargetSeries>> QueueMinutesAsync(string siteCode, AlertRuleValues rule, IReadOnlyList<AlertTarget> targets, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct)
    {
        var rows = await MinuteRowsAsync(siteCode, targets, fromUtc, toUtc, ct);
        return
        [
            .. targets.Select(t => new AlertTargetSeries(t, rows.Select(ZoneKeys.For(siteCode, t.ZoneName), r => new AlertMinute(r.MinuteUtc, rule.Metric == AlertMetric.Nowcast
                ? r.LengthDegraded == true ? null : r.Nowcast
                : r.QueueLength, r.QueueLength))))
        ];
    }

    /// <summary>
    /// The earliest start a bin may have and still be read for a P90 rule's minutes after <paramref name="fromUtc"/> (ARV-228a): a
    /// minute looks at the bins that ended within <see cref="BinLookBack"/> before it, so the read takes every bin that ends after
    /// <paramref name="fromUtc"/> less the look-back and is at most <see cref="LongestBin"/> long. One bin of slack at the most;
    /// no reach for longer bins (queue_bin's CHECK allows up to a day, but the stream writes <see cref="LongestBin"/> only).
    /// </summary>
    internal static DateTime BinReadFrom(DateTime fromUtc) => fromUtc - BinLookBack - LongestBin;

    private async Task<IReadOnlyList<AlertTargetSeries>> BinsAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var minutes = await MinuteRowsAsync(siteCode, targets, fromUtc, toUtc, ct);
        var keys = targets.Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        // The bins that end after the earliest minute's look-back and by the last minute's end (a bin a minute can use), read from
        // at most one bin before that look-back (BinReadFrom), so the read follows the range rather than a day before it.
        var bins = await Storage.ReadSqlAsync("""
            SELECT DISTINCT ON (zone_key, start_utc) zone_key AS "ZoneKey", start_utc AS "StartUtc", length_minutes AS "LengthMinutes", p90_wait_minutes AS "P90"
            FROM queue_bin WHERE zone_key = ANY(:keys) AND start_utc > :from AND start_utc <= :to
              AND start_utc + length_minutes * interval '1 minute' > :lookFrom AND start_utc + length_minutes * interval '1 minute' <= :end
            ORDER BY zone_key, start_utc, revision DESC
            """, new Dictionary<string, object>
        {
            ["keys"] = keys, ["from"] = BinReadFrom(fromUtc), ["to"] = toUtc, ["lookFrom"] = fromUtc - BinLookBack, ["end"] = toUtc.AddMinutes(1)
        }, r => new BinRow(r.GetString(0), Utc(r.GetDateTime(1)), r.GetInt32(2), DoubleAt(r, 3)), ct);
        var byZone = new ZoneRuns<BinRow>(bins, b => b.ZoneKey);
        var series = new List<AlertTargetSeries>();
        foreach (var target in targets)
        {
            var key = ZoneKeys.For(siteCode, target.ZoneName);
            // The zone's bins with a P90, by end; each minute looks only at those that ended within its look-back.
            var zoneBins = byZone.Of(key)
                .Where(b => b.P90 is { } p && double.IsFinite(p))
                .Select(b => (Start: b.StartUtc, End: b.StartUtc.AddMinutes(b.LengthMinutes), P90: b.P90!.Value))
                .OrderBy(b => b.End).ToArray();
            var ends = zoneBins.Select(b => b.End).ToArray();
            var list = new List<AlertMinute>(minutes.CountOf(key));
            foreach (var minute in minutes.Of(key))
            {
                var at = minute.MinuteUtc;
                // Bins that ended by the end of this minute and within the look-back; the largest P90, the earliest bin on a tie.
                var lo = UpperBound(ends, at - BinLookBack);
                var hi = UpperBound(ends, at.AddMinutes(1));
                (DateTime Start, DateTime End, double P90)? best = null;
                for (var i = lo; i < hi; i++)
                    if (best is not { } b || zoneBins[i].P90 > b.P90 || (zoneBins[i].P90 == b.P90 && zoneBins[i].Start < b.Start))
                        best = zoneBins[i];
                list.Add(best is { } found ? new AlertMinute(at, found.P90, minute.QueueLength, found.Start) : new AlertMinute(at, 0, minute.QueueLength));
            }

            series.Add(new AlertTargetSeries(target, list));
        }

        return series;
    }

    /// <summary>
    /// The most <c>zone_outage</c> rows one read takes (CWE-120, CWE-400, ARV-228b): a sensor rule watches up to
    /// <see cref="MaxDevices"/> devices, and a flapping device's outages over a range are bounded only by the range. A backtest's
    /// day at the bound with four outages a device holds 4,000. Beyond it the read is refused (null), never cut short.
    /// </summary>
    public const int DefaultMaxOutageRows = 100_000;

    /// <summary>The bound of <see cref="DefaultMaxOutageRows"/> for this instance (tests set a smaller one).</summary>
    public int MaxOutageRows { get; init; } = DefaultMaxOutageRows;

    /// <summary>
    /// The zones' device outages overlapping a range, at most <c>:limit</c> rows: a range of zone_outage's end index within the
    /// chunks that start by the range's end (script 0068, ARV-228a), whatever the table's history (OutageReadPlanTests explains it
    /// as written here).
    /// </summary>
    internal const string OutagesSql = """
        SELECT zone_key AS "ZoneKey", device_code AS "DeviceCode", from_utc AS "FromUtc", to_utc AS "ToUtc" FROM zone_outage
        WHERE zone_key = ANY(:keys) AND from_utc <= :to AND to_utc > :from
        LIMIT :limit
        """;

    /// <summary>
    /// The sensor rule's minutes of each device target: 1 inside one of its outages, 0 otherwise, for every minute of the range.
    /// The minutes are computed when the fold takes them (<see cref="OutageMinutes"/>), never stored: a day of 1,000 devices is
    /// 1.44 million of them. Null when the range holds more outages than <see cref="MaxOutageRows"/>.
    /// </summary>
    private async Task<IReadOnlyList<AlertTargetSeries>> OutagesAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var keys = targets.Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        var outages = await Storage.ReadSqlAsync(OutagesSql, new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc, ["to"] = toUtc, ["limit"] = MaxOutageRows + 1 },
            r => (ZoneKey: r.GetString(0), DeviceCode: r.GetString(1), From: Minute(Utc(r.GetDateTime(2))), To: Utc(r.GetDateTime(3))), ct);
        if (outages.Count > MaxOutageRows)
            return null;
        // An outage the stream has not closed yet: the registry has the device offline since it was last heard.
        var open = await Storage.ReadSqlAsync("""
            SELECT d.queue_zone_name AS "ZoneName", d.code AS "DeviceCode", h.last_seen_on AS "LastSeenOn" FROM device d
            LEFT JOIN device_heartbeat h ON h.device_id = d.id
            WHERE d.site_code = :site AND d.deleted_on IS NULL AND d.state = 'Offline'
            """, new Dictionary<string, object> { ["site"] = siteCode }, r => (ZoneName: TextAt(r, 0), DeviceCode: r.GetString(1), LastSeenOn: TimeAt(r, 2)), ct);

        var spans = new Dictionary<(string ZoneKey, string DeviceCode), List<(DateTime From, DateTime To)>>();
        foreach (var o in outages)
        {
            if (!spans.TryGetValue((o.ZoneKey, o.DeviceCode), out var list))
                spans[(o.ZoneKey, o.DeviceCode)] = list = [];
            list.Add((o.From, o.To));
        }

        // The first offline row of a device, as the targets took it before.
        var offline = new Dictionary<(string ZoneName, string DeviceCode), DateTime?>();
        foreach (var o in open)
            offline.TryAdd((o.ZoneName, o.DeviceCode), o.LastSeenOn);

        var start = Minute(fromUtc).AddMinutes(1);
        var count = toUtc < start ? 0 : (int)((toUtc - start).Ticks / TimeSpan.TicksPerMinute) + 1;
        var series = new List<AlertTargetSeries>(targets.Count);
        foreach (var target in targets)
        {
            var mine = spans.GetValueOrDefault((ZoneKeys.For(siteCode, target.ZoneName), target.DeviceCode)) ?? [];
            if (offline.TryGetValue((target.ZoneName, target.DeviceCode), out var seen))
                mine = [.. mine, (Minute(seen ?? fromUtc), DateTime.MaxValue)];
            series.Add(new AlertTargetSeries(target, new OutageMinutes(start, count, mine)));
        }

        return series;
    }

    private async Task<IReadOnlyList<AlertTargetSeries>> PredictedAsync(string siteCode, AlertRuleValues rule, IReadOnlyList<AlertTarget> targets, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct)
    {
        var lead = rule.LeadMinutes ?? AlertRule.MinLeadMinutes;
        var rows = await MinuteRowsAsync(siteCode, targets, fromUtc, toUtc, ct);
        var series = new List<AlertTargetSeries>();
        // The lead's arrivals of one minute, refilled for each (ARV-228b): a minute missing from the wave is NaN, so it has no peak.
        var ahead = new double[Math.Max(1, lead)];
        foreach (var target in targets)
        {
            var key = ZoneKeys.For(siteCode, target.ZoneName);
            var wave = rows.CountOf(key) == 0 ? null : await arrivals.ArrivalsAsync(siteCode, target.ZoneName, fromUtc, toUtc.AddMinutes(lead), ct);
            var list = new List<AlertMinute>(rows.CountOf(key));
            foreach (var r in rows.Of(key))
            {
                var at = r.MinuteUtc;
                PredictedPeak peak = null;
                if (wave is not null && r.QueueLength is { } q && r.LengthDegraded != true)
                {
                    for (var i = 1; i <= lead; i++)
                        ahead[i - 1] = wave.TryGetValue(at.AddMinutes(i), out var a) ? a : double.NaN;
                    peak = PredictedWait.Peak(q, r.Throughput, ahead, lead);
                }

                list.Add(new AlertMinute(at, peak?.Minutes, r.QueueLength, null, peak is null ? null : at.AddMinutes(peak.AheadMinutes)));
            }

            series.Add(new AlertTargetSeries(target, list));
        }

        return series;
    }

    /// <summary>
    /// What each zone of an overflow rule watches (ARV-115): a queue zone of the site's published profile watches every band
    /// of its zone key; an overflow zone watches its own band under its queue zone's key. Zones that are neither are left out.
    /// </summary>
    private async Task<Dictionary<string, (string Key, string Band)>> WatchedBandsAsync(string siteCode, IReadOnlyCollection<string> zoneNames, CancellationToken ct)
    {
        var watched = new Dictionary<string, (string Key, string Band)>(StringComparer.Ordinal);
        if (zoneNames.Count == 0)
            return watched;
        var zones = await Storage.ReadSqlAsync("""
            SELECT z.name AS "Name", z.kind AS "Kind", q.name AS "QueueZone" FROM zone z
            JOIN zone_profile p ON p.id = z.profile_id
            LEFT JOIN zone q ON q.id = z.queue_zone_id AND q.profile_id = z.profile_id
            WHERE p.site_code = :site AND p.status = 'Published' AND z.kind IN ('Queue', 'Overflow') AND z.name = ANY(:zones)
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zones"] = zoneNames.Distinct(StringComparer.Ordinal).ToList() },
            r => new ZoneRow(r.GetString(0), r.GetString(1), TextAt(r, 2)), ct);
        foreach (var z in zones)
        {
            if (z.Kind == nameof(ZoneKind.Queue))
                watched[z.Name] = (ZoneKeys.For(siteCode, z.Name), null);
            else if (z.QueueZone is { Length: > 0 } queue)
                watched[z.Name] = (ZoneKeys.For(siteCode, queue), z.Name);
        }

        return watched;
    }

    /// <summary>
    /// The latest minute with a band reading the stream stored, for each zone of an overflow rule (none for a zone without
    /// one), at or before <paramref name="notAfterUtc"/>. Every band minute of a minute closes in the same engine step and is
    /// written in the same checkpoint, so a minute that is there is there for every band that reported in it.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> LatestOverflowMinutesAsync(string siteCode, IReadOnlyCollection<string> zones, DateTime notAfterUtc,
        CancellationToken ct)
    {
        var watched = await WatchedBandsAsync(siteCode, zones, ct);
        var keys = watched.Values.Select(w => w.Key).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
            return new Dictionary<string, DateTime>();
        var rows = await Storage.ReadSqlAsync("""
            SELECT zone_key AS "ZoneKey", band_name AS "BandName", max(minute_utc) AS "MinuteUtc", 0 AS "MaxOccupancy" FROM overflow_minute
            WHERE zone_key = ANY(:keys) AND minute_utc <= :notAfter GROUP BY zone_key, band_name
            """, new Dictionary<string, object> { ["keys"] = keys, ["notAfter"] = notAfterUtc }, OverflowOf, ct);
        var latest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (zone, (key, band)) in watched)
        {
            var mine = rows.Where(r => r.ZoneKey == key && (band is null || r.BandName == band)).Select(r => r.MinuteUtc).ToList();
            if (mine.Count > 0)
                latest[zone] = mine.Max();
        }

        return latest;
    }

    private async Task<IReadOnlyList<AlertTargetSeries>> OverflowAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct)
    {
        var watched = await WatchedBandsAsync(siteCode, [.. targets.Select(t => t.ZoneName)], ct);
        var keys = watched.Values.Select(w => w.Key).Distinct(StringComparer.Ordinal).ToList();
        var rows = keys.Count == 0 ? [] : await Storage.ReadSqlAsync("""
            SELECT zone_key AS "ZoneKey", band_name AS "BandName", minute_utc AS "MinuteUtc", max_occupancy AS "MaxOccupancy" FROM overflow_minute
            WHERE zone_key = ANY(:keys) AND minute_utc > :from AND minute_utc <= :to
            ORDER BY zone_key, minute_utc, band_name
            """, new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc, ["to"] = toUtc }, OverflowOf, ct);
        var byZone = new ZoneRuns<OverflowRow>(rows, r => r.ZoneKey);
        var series = new List<AlertTargetSeries>();
        foreach (var target in targets)
        {
            if (!watched.TryGetValue(target.ZoneName, out var w) || byZone.CountOf(w.Key) == 0)
            {
                series.Add(new AlertTargetSeries(target, []));
                continue;
            }

            // One value per minute with a reading of a watched band: 1 when any of them held anyone in it, else 0.
            var minutes = byZone.Of(w.Key).Where(r => w.Band is null || string.Equals(r.BandName, w.Band, StringComparison.Ordinal))
                .GroupBy(r => r.MinuteUtc)
                .OrderBy(g => g.Key)
                .Select(g => new AlertMinute(g.Key, g.Any(r => r.MaxOccupancy > 0) ? 1 : 0))
                .ToList();
            series.Add(new AlertTargetSeries(target, minutes));
        }

        return series;
    }

    // The index of the first element after the value.
    private static int UpperBound(DateTime[] sorted, DateTime value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] <= value)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime Minute(DateTime value) => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);

    #region Typed reads (ARV-228b)

    // A column by position, null for SQL NULL, with the reader's typed getters (no boxed value per column).
    private static string TextAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static int? IntAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static long? LongAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static double? DoubleAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static bool? BoolAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);

    private static DateTime? TimeAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : Utc(r.GetDateTime(i));

    private static (string ZoneKey, DateTime? MinuteUtc) LatestOf(DbDataReader r) => (r.GetString(0), TimeAt(r, 1));

    private static OverflowRow OverflowOf(DbDataReader r) => new(r.GetString(0), r.GetString(1), Utc(r.GetDateTime(2)), r.GetInt32(3));

    private readonly record struct MinuteRow(string ZoneKey, DateTime MinuteUtc, int? QueueLength, double? Nowcast, bool? LengthDegraded, double? Throughput);

    private readonly record struct BinRow(string ZoneKey, DateTime StartUtc, int LengthMinutes, double? P90);

    private readonly record struct ZoneRow(string Name, string Kind, string QueueZone);

    private readonly record struct OverflowRow(string ZoneKey, string BandName, DateTime MinuteUtc, int MaxOccupancy);

    /// <summary>
    /// Rows a read gave in zone key order, each zone's rows one run of the read's own list, taken in place (ARV-228b): never
    /// grouped into a copy per zone. Every read that builds one orders by the zone key first, so a key has one run; a second run
    /// of a key would be rows out of order, and is refused rather than merged.
    /// </summary>
    private sealed class ZoneRuns<T>
    {
        private readonly List<T> _rows;
        private readonly Dictionary<string, (int Start, int Count)> _runs = new(StringComparer.Ordinal);

        public ZoneRuns(List<T> rows, Func<T, string> key)
        {
            _rows = rows;
            for (var i = 0; i < rows.Count;)
            {
                var start = i;
                var zone = key(rows[i]);
                while (i < rows.Count && string.Equals(key(rows[i]), zone, StringComparison.Ordinal))
                    i++;
                if (!_runs.TryAdd(zone, (start, i - start)))
                    throw new InvalidOperationException("A read's rows are not in zone key order.");
            }
        }

        /// <summary>How many rows the zone has.</summary>
        public int CountOf(string key) => _runs.TryGetValue(key, out var run) ? run.Count : 0;

        /// <summary>The zone's rows, in the read's order.</summary>
        public IEnumerable<T> Of(string key)
        {
            if (!_runs.TryGetValue(key, out var run))
                yield break;
            for (var i = run.Start; i < run.Start + run.Count; i++)
                yield return _rows[i];
        }

        /// <summary>The zone's rows mapped, in the read's order, into a list of exactly their number.</summary>
        public List<TResult> Select<TResult>(string key, Func<T, TResult> map)
        {
            var list = new List<TResult>(CountOf(key));
            if (_runs.TryGetValue(key, out var run))
            {
                for (var i = run.Start; i < run.Start + run.Count; i++)
                    list.Add(map(_rows[i]));
            }

            return list;
        }
    }

    /// <summary>
    /// A device's minutes for a sensor rule, computed when taken (ARV-228b): the minute <paramref name="first"/> and the
    /// <paramref name="count"/> after it in order, each 1 when it lies inside one of the device's outages (an outage covers the
    /// minutes from its start up to, not including, its end) and 0 otherwise. The outages are merged into spans that do not
    /// overlap, in time order, so a minute is found by a binary search: no minute is stored and none allocates.
    /// </summary>
    internal sealed class OutageMinutes : IReadOnlyList<AlertMinute>
    {
        private readonly DateTime _first;
        private readonly (DateTime From, DateTime To)[] _spans;

        public OutageMinutes(DateTime first, int count, IEnumerable<(DateTime From, DateTime To)> outages)
        {
            ArgumentNullException.ThrowIfNull(outages);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _first = first;
            Count = count;
            _spans = Merged(outages);
        }

        public int Count { get; }

        public AlertMinute this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                var minute = _first.AddMinutes(index);
                return new AlertMinute(minute, Inside(minute) ? 1 : 0);
            }
        }

        public IEnumerator<AlertMinute> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Inside a span: the last span that starts by the minute ends after it (the spans do not overlap).
        private bool Inside(DateTime minute)
        {
            int lo = 0, hi = _spans.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_spans[mid].From <= minute)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo > 0 && minute < _spans[lo - 1].To;
        }

        // The outages as their union: in order of start, each overlapping or touching the one before joined to it; an empty one
        // (its end not after its start) covers no minute and is left out.
        private static (DateTime From, DateTime To)[] Merged(IEnumerable<(DateTime From, DateTime To)> outages)
        {
            var merged = new List<(DateTime From, DateTime To)>();
            foreach (var span in outages.Where(o => o.From < o.To).OrderBy(o => o.From))
            {
                if (merged.Count > 0 && span.From <= merged[^1].To)
                    merged[^1] = (merged[^1].From, span.To > merged[^1].To ? span.To : merged[^1].To);
                else
                    merged.Add(span);
            }

            return [.. merged];
        }
    }

    #endregion
}
