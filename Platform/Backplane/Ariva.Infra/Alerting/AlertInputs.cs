using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;

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
/// <item><c>BinP90</c>: the largest P90 among the zone's bins that ended within the last 150 minutes (latest revision), with that bin;</item>
/// <item><c>SensorOffline</c>: per commissioned device of the zones, 1 for a minute inside one of its outages (<c>zone_outage</c>, and an
/// outage still open while the registry has it offline), otherwise 0;</item>
/// <item><c>PredictedNowcast</c>: <see cref="PredictedWait"/> from the queue minute's length and throughput and the arrival wave over the
/// lead time (nothing while the length is degraded), for the minute of the projected peak;</item>
/// <item><c>OverflowOccupied</c> (ARV-115): per minute, 1 when a watched overflow band held anyone in it (TC-19, decided 2026-10-06:
/// the band's highest reading in the minute above zero), 0 when the watched bands that reported held no one, nothing for a minute
/// without a band reading, from <c>overflow_minute</c>. Only bands with a reading in the minute count: a band silent within the
/// freshness window or Unknown after it (the stream's <c>OverflowDetected</c> Unknown) has no row, so it is neither occupied nor
/// empty and adds neither a 1 nor a 0; a zone whose only band is Unknown has no value, and the rule keeps its state until a
/// reading comes. A queue zone's target watches every band of the zone; an overflow zone's target watches that band only;</item>
/// <item><c>DesksBelowPlan</c>: nothing yet; there is no staffing plan before ARV-049, so these rules have nothing to judge.</item>
/// </list>
/// Minutes are those after <c>fromUtc</c> up to and including <c>toUtc</c>. A rule's targets are its zones that are queue or
/// overflow zones of the site's published profile (or the commissioned devices of those zones).
/// </summary>
public sealed class AlertInputs(IUnitOfWork unitOfWork, IArrivalWaveSource arrivals)
{
    /// <summary>How far back a P90 rule looks for bins, as in the prototype.</summary>
    public static readonly TimeSpan BinLookBack = TimeSpan.FromMinutes(150);

    /// <summary>The longest bin the look-back reaches back for (queue_bin allows up to a day).</summary>
    private const int AlertBinMaxLength = 1440;

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
        var published = (await Storage.ExecuteSqlAsync<NameRow>("""
            SELECT z.name AS "Name" FROM zone z JOIN zone_profile p ON p.id = z.profile_id
            WHERE p.site_code = :site AND p.status = 'Published' AND z.kind IN ('Queue', 'Overflow') AND z.name IN (:zones)
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zones"] = zones }, ct)).Select(z => z.Name).ToHashSet(StringComparer.Ordinal);
        zones = [.. zones.Where(published.Contains)];
        if (zones.Count == 0)
            return [];
        if (rule.Metric != AlertMetric.SensorOffline)
            return [.. zones.Select(z => new AlertTarget(z, null))];
        var devices = await Storage.ExecuteSqlAsync<DeviceRow>("""
            SELECT queue_zone_name AS "ZoneName", code AS "Code" FROM device
            WHERE site_code = :site AND deleted_on IS NULL AND state IN ('Online', 'Degraded', 'Offline') AND queue_zone_name IN (:zones)
            ORDER BY queue_zone_name, code LIMIT 1000
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zones"] = zones }, ct);
        return [.. devices.Select(d => new AlertTarget(d.ZoneName, d.Code))];
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
        var rows = await Storage.ExecuteSqlAsync<LatestRow>("""
            SELECT zone_key AS "ZoneKey", max(minute_utc) AS "MinuteUtc" FROM queue_minute
            WHERE zone_key IN (:keys) AND minute_utc <= :notAfter AND queue_length IS NOT NULL GROUP BY zone_key
            """, new Dictionary<string, object> { ["keys"] = keys, ["notAfter"] = notAfterUtc }, ct);
        var prefix = siteCode + "/";
        return rows.Where(r => r.MinuteUtc is not null)
            .ToDictionary(r => r.ZoneKey[prefix.Length..], r => Utc(r.MinuteUtc.Value), StringComparer.Ordinal);
    }

    /// <summary>Each target's minutes after <paramref name="fromUtc"/> up to <paramref name="toUtc"/>.</summary>
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
            _ => [.. targets.Select(t => new AlertTargetSeries(t, []))]
        };
    }

    private async Task<Dictionary<string, List<MinuteRow>>> MinuteRowsAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var keys = targets.Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        var rows = await Storage.ExecuteSqlAsync<MinuteRow>("""
            SELECT zone_key AS "ZoneKey", minute_utc AS "MinuteUtc", queue_length AS "QueueLength", nowcast_minutes AS "Nowcast",
                   length_degraded AS "LengthDegraded", throughput_per_minute AS "Throughput"
            FROM queue_minute WHERE zone_key IN (:keys) AND minute_utc > :from AND minute_utc <= :to
            ORDER BY zone_key, minute_utc
            """, new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc, ["to"] = toUtc }, ct);
        return rows.GroupBy(r => r.ZoneKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<AlertTargetSeries>> QueueMinutesAsync(string siteCode, AlertRuleValues rule, IReadOnlyList<AlertTarget> targets, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct)
    {
        var rows = await MinuteRowsAsync(siteCode, targets, fromUtc, toUtc, ct);
        return
        [
            .. targets.Select(t => new AlertTargetSeries(t, rows.TryGetValue(ZoneKeys.For(siteCode, t.ZoneName), out var zone)
                ? [.. zone.Select(r => new AlertMinute(Utc(r.MinuteUtc), rule.Metric == AlertMetric.Nowcast
                    ? r.LengthDegraded == true ? null : r.Nowcast
                    : r.QueueLength, r.QueueLength))]
                : []))
        ];
    }

    private async Task<IReadOnlyList<AlertTargetSeries>> BinsAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var minutes = await MinuteRowsAsync(siteCode, targets, fromUtc, toUtc, ct);
        var keys = targets.Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        var bins = await Storage.ExecuteSqlAsync<BinRow>("""
            SELECT DISTINCT ON (zone_key, start_utc) zone_key AS "ZoneKey", start_utc AS "StartUtc", length_minutes AS "LengthMinutes", p90_wait_minutes AS "P90"
            FROM queue_bin WHERE zone_key IN (:keys) AND start_utc > :from AND start_utc <= :to
            ORDER BY zone_key, start_utc, revision DESC
            """, new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc - BinLookBack - TimeSpan.FromMinutes(AlertBinMaxLength), ["to"] = toUtc }, ct);
        var byZone = bins.GroupBy(b => b.ZoneKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var series = new List<AlertTargetSeries>();
        foreach (var target in targets)
        {
            var key = ZoneKeys.For(siteCode, target.ZoneName);
            // The zone's bins with a P90, by end; each minute looks only at those that ended within its look-back.
            var zoneBins = (byZone.GetValueOrDefault(key) ?? [])
                .Where(b => b.P90 is { } p && double.IsFinite(p))
                .Select(b => (Start: Utc(b.StartUtc), End: Utc(b.StartUtc).AddMinutes(b.LengthMinutes), P90: b.P90!.Value))
                .OrderBy(b => b.End).ToArray();
            var ends = zoneBins.Select(b => b.End).ToArray();
            var list = new List<AlertMinute>();
            foreach (var minute in minutes.GetValueOrDefault(key) ?? [])
            {
                var at = Utc(minute.MinuteUtc);
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

    private async Task<IReadOnlyList<AlertTargetSeries>> OutagesAsync(string siteCode, IReadOnlyList<AlertTarget> targets, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var keys = targets.Select(t => ZoneKeys.For(siteCode, t.ZoneName)).Distinct(StringComparer.Ordinal).ToList();
        var outages = await Storage.ExecuteSqlAsync<OutageRow>("""
            SELECT zone_key AS "ZoneKey", device_code AS "DeviceCode", from_utc AS "FromUtc", to_utc AS "ToUtc" FROM zone_outage
            WHERE zone_key IN (:keys) AND from_utc <= :to AND to_utc > :from
            """, new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc, ["to"] = toUtc }, ct);
        // An outage the stream has not closed yet: the registry has the device offline since it was last heard.
        var open = await Storage.ExecuteSqlAsync<OpenRow>("""
            SELECT d.queue_zone_name AS "ZoneName", d.code AS "DeviceCode", h.last_seen_on AS "LastSeenOn" FROM device d
            LEFT JOIN device_heartbeat h ON h.device_id = d.id
            WHERE d.site_code = :site AND d.deleted_on IS NULL AND d.state = 'Offline'
            """, new Dictionary<string, object> { ["site"] = siteCode }, ct);
        var series = new List<AlertTargetSeries>();
        var start = Minute(fromUtc).AddMinutes(1);
        foreach (var target in targets)
        {
            var key = ZoneKeys.For(siteCode, target.ZoneName);
            var spans = outages.Where(o => o.ZoneKey == key && o.DeviceCode == target.DeviceCode).Select(o => (From: Minute(Utc(o.FromUtc)), To: Utc(o.ToUtc))).ToList();
            var still = open.FirstOrDefault(o => o.ZoneName == target.ZoneName && o.DeviceCode == target.DeviceCode);
            if (still is not null)
                spans.Add((Minute(still.LastSeenOn is { } seen ? Utc(seen) : fromUtc), DateTime.MaxValue));
            var list = new List<AlertMinute>();
            for (var m = start; m <= toUtc; m = m.AddMinutes(1))
                list.Add(new AlertMinute(m, spans.Any(s => s.From <= m && m < s.To) ? 1 : 0));
            series.Add(new AlertTargetSeries(target, list));
        }

        return series;
    }

    private async Task<IReadOnlyList<AlertTargetSeries>> PredictedAsync(string siteCode, AlertRuleValues rule, IReadOnlyList<AlertTarget> targets, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct)
    {
        var lead = rule.LeadMinutes ?? AlertRule.MinLeadMinutes;
        var rows = await MinuteRowsAsync(siteCode, targets, fromUtc, toUtc, ct);
        var series = new List<AlertTargetSeries>();
        foreach (var target in targets)
        {
            var zone = rows.GetValueOrDefault(ZoneKeys.For(siteCode, target.ZoneName)) ?? [];
            var wave = zone.Count == 0 ? null : await arrivals.ArrivalsAsync(siteCode, target.ZoneName, fromUtc, toUtc.AddMinutes(lead), ct);
            var list = new List<AlertMinute>();
            foreach (var r in zone)
            {
                var at = Utc(r.MinuteUtc);
                PredictedPeak peak = null;
                if (wave is not null && r.QueueLength is { } q && r.LengthDegraded != true)
                {
                    var ahead = Enumerable.Range(1, lead).Select(i => wave.TryGetValue(at.AddMinutes(i), out var a) ? a : double.NaN).ToList();
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
        var zones = await Storage.ExecuteSqlAsync<ZoneRow>("""
            SELECT z.name AS "Name", z.kind AS "Kind", q.name AS "QueueZone" FROM zone z
            JOIN zone_profile p ON p.id = z.profile_id
            LEFT JOIN zone q ON q.id = z.queue_zone_id AND q.profile_id = z.profile_id
            WHERE p.site_code = :site AND p.status = 'Published' AND z.kind IN ('Queue', 'Overflow') AND z.name IN (:zones)
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zones"] = zoneNames.Distinct(StringComparer.Ordinal).ToList() }, ct);
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
        var rows = await Storage.ExecuteSqlAsync<OverflowRow>("""
            SELECT zone_key AS "ZoneKey", band_name AS "BandName", max(minute_utc) AS "MinuteUtc", 0 AS "MaxOccupancy" FROM overflow_minute
            WHERE zone_key IN (:keys) AND minute_utc <= :notAfter GROUP BY zone_key, band_name
            """, new Dictionary<string, object> { ["keys"] = keys, ["notAfter"] = notAfterUtc }, ct);
        var latest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (zone, (key, band)) in watched)
        {
            var mine = rows.Where(r => r.ZoneKey == key && (band is null || r.BandName == band)).Select(r => Utc(r.MinuteUtc)).ToList();
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
        var rows = keys.Count == 0 ? [] : await Storage.ExecuteSqlAsync<OverflowRow>("""
            SELECT zone_key AS "ZoneKey", band_name AS "BandName", minute_utc AS "MinuteUtc", max_occupancy AS "MaxOccupancy" FROM overflow_minute
            WHERE zone_key IN (:keys) AND minute_utc > :from AND minute_utc <= :to
            ORDER BY zone_key, minute_utc, band_name
            """, new Dictionary<string, object> { ["keys"] = keys, ["from"] = fromUtc, ["to"] = toUtc }, ct);
        var byZone = rows.GroupBy(r => r.ZoneKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var series = new List<AlertTargetSeries>();
        foreach (var target in targets)
        {
            if (!watched.TryGetValue(target.ZoneName, out var w) || !byZone.TryGetValue(w.Key, out var zoneRows))
            {
                series.Add(new AlertTargetSeries(target, []));
                continue;
            }

            // One value per minute with a reading of a watched band: 1 when any of them held anyone in it, else 0.
            var minutes = zoneRows.Where(r => w.Band is null || string.Equals(r.BandName, w.Band, StringComparison.Ordinal))
                .GroupBy(r => Utc(r.MinuteUtc))
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

    private sealed class NameRow
    {
        public string Name { get; set; }
    }

    private sealed class DeviceRow
    {
        public string ZoneName { get; set; }
        public string Code { get; set; }
    }

    private sealed class LatestRow
    {
        public string ZoneKey { get; set; }
        public DateTime? MinuteUtc { get; set; }
    }

    private sealed class MinuteRow
    {
        public string ZoneKey { get; set; }
        public DateTime MinuteUtc { get; set; }
        public int? QueueLength { get; set; }
        public double? Nowcast { get; set; }
        public bool? LengthDegraded { get; set; }
        public double? Throughput { get; set; }
    }

    private sealed class BinRow
    {
        public string ZoneKey { get; set; }
        public DateTime StartUtc { get; set; }
        public int LengthMinutes { get; set; }
        public double? P90 { get; set; }
    }

    private sealed class ZoneRow
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string QueueZone { get; set; }
    }

    private sealed class OverflowRow
    {
        public string ZoneKey { get; set; }
        public string BandName { get; set; }
        public DateTime MinuteUtc { get; set; }
        public int MaxOccupancy { get; set; }
    }

    private sealed class OutageRow
    {
        public string ZoneKey { get; set; }
        public string DeviceCode { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
    }

    private sealed class OpenRow
    {
        public string ZoneName { get; set; }
        public string DeviceCode { get; set; }
        public DateTime? LastSeenOn { get; set; }
    }
}
