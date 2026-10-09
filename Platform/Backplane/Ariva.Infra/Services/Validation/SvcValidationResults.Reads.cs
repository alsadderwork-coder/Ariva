using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ariva.Core.Availability;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Core.Validation.Comparison;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// The validation results service's reads through the runtime login (ARV-104g2): one campaign's ground truth by its id and site,
/// and the stored outputs of its site by exact keys only (CWE-863): queue zones by <see cref="ZoneKeys.For"/>, desks by
/// <see cref="DeskKeys.For"/> (never a prefix, a suffix or a pattern), lines by name within those zone keys. Every statement is a
/// constant with bound parameters (CWE-89). Every read is bounded (CWE-120, CWE-400): at most <c>RowLimit</c> rows, refused
/// beyond (a null list, never cut short), read per zone or per contiguous run of planned days, so no read holds more than the
/// results settings allow. Line counts are summed per 15-minute bin from <c>line_minute</c> itself, because
/// <c>line_minute_15m</c> refreshes only its last 3 days (script 0037). Rows are passed on as stored, their times as UTC: the
/// F18 engine checks every row again before use (CWE-501); a stored name that is not one of an enum's becomes a value outside
/// it, so the engine refuses the row instead of this read guessing.
/// </summary>
internal sealed partial class SvcValidationResults
{
    #region Statements

    private const string QueueMinutesSql = """
        SELECT minute_utc AS "MinuteUtc", profile_version AS "ProfileVersion", status AS "Status", waits AS "Waits", mean_wait_minutes AS "MeanWaitMinutes",
               nowcast_minutes AS "NowcastMinutes", no_service AS "NoService", nowcast_degraded AS "NowcastDegraded"
          FROM queue_minute
         WHERE zone_key = :key AND minute_utc >= :from AND minute_utc < :to
         ORDER BY minute_utc
         LIMIT :limit
        """;

    private const string QueueBinsSql = """
        SELECT zone_key AS "ZoneKey", start_utc AS "StartUtc", length_minutes AS "LengthMinutes", revision AS "Revision", status AS "Status", quality AS "Quality",
               profile_version AS "ProfileVersion", entries AS "Entries", abandoned AS "Abandoned"
          FROM queue_bin
         WHERE zone_key IN (:keys) AND start_utc >= :from AND start_utc < :to
         ORDER BY zone_key, start_utc, revision
         LIMIT :limit
        """;

    private const string HealthBinsSql = """
        SELECT zone_key AS "ZoneKey", start_utc AS "StartUtc", length_minutes AS "LengthMinutes", revision AS "Revision", status AS "Status",
               profile_version AS "ProfileVersion", entries AS "Entries", exits AS "Exits", occupancy_start AS "OccupancyStart", occupancy_end AS "OccupancyEnd",
               conservation_residual AS "ConservationResidual", tracks_entered AS "TracksEntered", tracks_exited AS "TracksExited",
               tracks_abandoned AS "TracksAbandoned", tracks_fragmented AS "TracksFragmented", tracks_censored AS "TracksCensored",
               tracks_rejected AS "TracksRejected", tracks_open AS "TracksOpen", track_completion_rate AS "TrackCompletionRate",
               occupancy_minutes AS "OccupancyMinutes", capacity_minutes AS "CapacityMinutes", minutes_outside_capacity AS "MinutesOutsideCapacity"
          FROM zone_health_bin
         WHERE zone_key IN (:keys) AND start_utc >= :from AND start_utc < :to
         ORDER BY zone_key, start_utc, revision
         LIMIT :limit
        """;

    // Ariva's and a vendor's crossings per line, source, version and 15-minute bin, as line_minute_15m sums them.
    private const string LineBinsSql = """
        SELECT zone_key AS "ZoneKey", line_name AS "LineName", source AS "Source", profile_version AS "ProfileVersion",
               date_bin(INTERVAL '15 minutes', minute_utc, TIMESTAMPTZ '2000-01-01 00:00:00+00') AS "BinStartUtc",
               CAST(sum(crossings_in) AS bigint) AS "CrossingsIn", CAST(sum(crossings_out) AS bigint) AS "CrossingsOut"
          FROM line_minute
         WHERE zone_key IN (:keys) AND line_name IN (:lines) AND minute_utc >= :from AND minute_utc < :to
         GROUP BY zone_key, line_name, source, profile_version, date_bin(INTERVAL '15 minutes', minute_utc, TIMESTAMPTZ '2000-01-01 00:00:00+00')
         ORDER BY 1, 2, 3, 4, 5
         LIMIT :limit
        """;

    // An outage still open ends where the read ends (the engine's rule for zone_outage).
    private const string OutagesSql = """
        SELECT zone_key AS "ZoneKey", from_utc AS "FromUtc", CASE WHEN closed THEN to_utc ELSE CAST(:to AS timestamptz) END AS "ToUtc"
          FROM zone_outage
         WHERE zone_key IN (:keys) AND from_utc < :to AND (to_utc > :from OR NOT closed)
         ORDER BY zone_key, from_utc, device_code
         LIMIT :limit
        """;

    // The desks in scope by their exact keys, at the minutes this campaign's observers recorded for that very desk.
    private const string DeskMinutesSql = """
        SELECT d.desk_code AS "DeskKey", d.minute_utc AS "MinuteUtc", d.closed_seconds AS "ClosedSeconds", d.idle_seconds AS "IdleSeconds",
               d.serving_seconds AS "ServingSeconds", d.paused_seconds AS "PausedSeconds", d.unknown_seconds AS "UnknownSeconds", d.degraded AS "Degraded"
          FROM desk_minute d
         WHERE d.desk_code IN (:keys) AND d.minute_utc >= :from AND d.minute_utc < :to
           AND EXISTS (SELECT 1 FROM desk_observation o
                         JOIN validation_campaign_desk v ON v.campaign_id = o.campaign_id AND v.desk_id = o.desk_id AND v.site_code = o.site_code
                        WHERE o.campaign_id = :campaign AND o.site_code = :site AND o.minute_utc = d.minute_utc
                          AND d.desk_code = v.site_code || '/' || v.checkpoint_code || '/' || v.desk_code)
         ORDER BY d.desk_code, d.minute_utc
         LIMIT :limit
        """;

    // The ledger's minutes of the planned local days, per day (ARV-118, as SvcAvailability counts them).
    private const string AvailabilitySql = """
        SELECT to_char(local_date, 'YYYY-MM-DD') AS "Date",
               CAST(count(*) AS integer) AS "Recorded",
               CAST(count(*) FILTER (WHERE calendar = 'Operating') AS integer) AS "Operating",
               CAST(count(*) FILTER (WHERE calendar = 'Operating' AND state = 'Available') AS integer) AS "Available",
               CAST(count(*) FILTER (WHERE calendar = 'Operating' AND state = 'Unavailable') AS integer) AS "Unavailable",
               CAST(count(*) FILTER (WHERE calendar = 'Operating' AND state = 'Unobserved') AS integer) AS "Unobserved",
               CAST(count(*) FILTER (WHERE calendar = 'Maintenance') AS integer) AS "Maintenance",
               CAST(count(*) FILTER (WHERE calendar = 'Closed') AS integer) AS "Closed",
               CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'StaleZone' = ANY (reasons)) AS integer) AS "StaleZone",
               CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'MissingMinute' = ANY (reasons)) AS integer) AS "MissingMinute",
               CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'StreamLag' = ANY (reasons)) AS integer) AS "StreamLag",
               CAST(count(*) FILTER (WHERE calendar = 'Operating' AND 'NoPublishedZones' = ANY (reasons)) AS integer) AS "NoPublishedZones"
          FROM availability_minute
         WHERE site_code = :site AND to_char(local_date, 'YYYY-MM-DD') IN (:dates) AND minute_utc >= :fromUtc AND minute_utc < :toUtc
         GROUP BY local_date
         ORDER BY local_date
        """;

    // The calibration records of the site's devices of the zones in scope, performed before the last planned day ended.
    private const string CalibrationsSql = """
        SELECT d.code AS "DeviceCode", d.queue_zone_name AS "QueueZone", c.method AS "Method", c.sample_size AS "SampleSize",
               c.counting_accuracy_percent AS "CountingAccuracyPercent", c.wait_time_error_minutes AS "WaitTimeErrorMinutes",
               c.threshold_percent AS "ThresholdPercent", c.passed AS "Passed", c.performed_on AS "PerformedOn"
          FROM device_calibration c
          JOIN device d ON d.id = c.device_id AND d.site_code = c.site_code
         WHERE c.site_code = :site AND d.queue_zone_name IN (:zones) AND c.performed_on < :until
         ORDER BY d.code, c.performed_on DESC, c.id
         LIMIT :limit
        """;

    #endregion

    #region Ground truth

    /// <summary>Every revision of the campaign's manual counts (ARV-104a), by campaign and site; null beyond the bound.</summary>
    internal async Task<List<ManualCountRow>> ManualCountsAsync(ValidationCampaign campaign, CancellationToken ct)
    {
        var id = campaign.Id.GetValueOrDefault();
        var rows = await QueryAsNoTracking<ManualCount>().Where(c => c.CampaignId == id && c.SiteCode == campaign.SiteCode)
            .Select(c => new { c.LineId, c.BinStartUtc, c.ObserverId, c.Revision, c.CrossingsIn, c.CrossingsOut })
            .Take(RowLimit + 1).ToListAsync(ct);
        return rows.Count > RowLimit ? null : [.. rows.Select(c => new ManualCountRow(c.LineId, Utc(c.BinStartUtc), c.ObserverId, c.Revision, c.CrossingsIn, c.CrossingsOut))];
    }

    /// <summary>The campaign's tracer runs (ARV-104b), by campaign and site; null beyond the bound.</summary>
    internal async Task<List<TracerRunRow>> TracerRunsAsync(ValidationCampaign campaign, CancellationToken ct)
    {
        var id = campaign.Id.GetValueOrDefault();
        var rows = await QueryAsNoTracking<TracerRun>().Where(r => r.CampaignId == id && r.SiteCode == campaign.SiteCode).Take(RowLimit + 1).ToListAsync(ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Select(TracerRunRow.Of).Select(r => r with
            {
                JoinedRawUtc = Utc(r.JoinedRawUtc), ExitedRawUtc = Utc(r.ExitedRawUtc), JoinedUtc = Utc(r.JoinedUtc), ExitedUtc = Utc(r.ExitedUtc)
            })];
    }

    /// <summary>Every revision of the campaign's desk states (ARV-104b), by campaign and site; null beyond the bound.</summary>
    internal async Task<List<DeskObservationRow>> DeskObservationsAsync(ValidationCampaign campaign, CancellationToken ct)
    {
        if (campaign.Desks.Count == 0)
            return [];
        var id = campaign.Id.GetValueOrDefault();
        var rows = await QueryAsNoTracking<DeskObservation>().Where(o => o.CampaignId == id && o.SiteCode == campaign.SiteCode)
            .Select(o => new { o.DeskId, o.MinuteUtc, o.ObserverId, o.Revision, o.State })
            .Take(RowLimit + 1).ToListAsync(ct);
        return rows.Count > RowLimit ? null : [.. rows.Select(o => new DeskObservationRow(o.DeskId, Utc(o.MinuteUtc), o.ObserverId, o.Revision, o.State))];
    }

    #endregion

    #region Stored outputs

    /// <summary>A queue zone's stored minutes in a window, by its exact key; null beyond the bound.</summary>
    internal async Task<List<QueueMinuteRow>> QueueMinutesAsync(string siteCode, string zone, UtcWindow window, CancellationToken ct)
    {
        var rows = await ExecuteSqlAsync<QueueMinuteData>(QueueMinutesSql, new Dictionary<string, object>
        {
            ["key"] = ZoneKeys.For(siteCode, zone),
            ["from"] = window.FromUtc,
            ["to"] = window.ToUtc,
            ["limit"] = RowLimit + 1
        }, ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Select(r => new QueueMinuteRow(zone, Utc(r.MinuteUtc), r.ProfileVersion, r.Status is null ? null : Parse<BinStatus>(r.Status), r.Waits,
                r.MeanWaitMinutes, r.NowcastMinutes, r.NoService, r.NowcastDegraded))];
    }

    /// <summary>The zones' stored bins (every revision) whose start falls in the window; null beyond the bound.</summary>
    internal async Task<List<QueueBinRow>> QueueBinsAsync(IReadOnlyDictionary<string, string> zonesByKey, UtcWindow window, CancellationToken ct)
    {
        var rows = await ExecuteSqlAsync<QueueBinData>(QueueBinsSql, Window(zonesByKey, window), ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Where(r => zonesByKey.ContainsKey(r.ZoneKey)).Select(r => new QueueBinRow(zonesByKey[r.ZoneKey], Utc(r.StartUtc), TimeSpan.FromMinutes(r.LengthMinutes),
                r.Revision, Parse<BinStatus>(r.Status), Parse<BinQuality>(r.Quality), r.ProfileVersion, r.Entries, r.Abandoned))];
    }

    /// <summary>The zones' stored health bins (every revision) whose start falls in the window; null beyond the bound.</summary>
    internal async Task<List<ZoneHealthBin>> HealthBinsAsync(IReadOnlyDictionary<string, string> zonesByKey, UtcWindow window, CancellationToken ct)
    {
        var rows = await ExecuteSqlAsync<HealthBinData>(HealthBinsSql, Window(zonesByKey, window), ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Where(r => zonesByKey.ContainsKey(r.ZoneKey)).Select(r => new ZoneHealthBin(zonesByKey[r.ZoneKey], Utc(r.StartUtc), TimeSpan.FromMinutes(r.LengthMinutes),
                r.Revision, Parse<BinStatus>(r.Status), r.ProfileVersion, r.Entries, r.Exits, r.OccupancyStart, r.OccupancyEnd, r.ConservationResidual, r.TracksEntered,
                r.TracksExited, r.TracksAbandoned, r.TracksFragmented, r.TracksCensored, r.TracksRejected, r.TracksOpen, r.TrackCompletionRate, r.OccupancyMinutes,
                r.CapacityMinutes, r.MinutesOutsideCapacity))];
    }

    /// <summary>The lines' crossings per 15-minute bin in the window, every source and version, from line_minute; null beyond the bound.</summary>
    internal async Task<List<LineBinCount>> LineBinsAsync(IReadOnlyDictionary<string, string> zonesByKey, IReadOnlyCollection<string> lines, UtcWindow window,
        CancellationToken ct)
    {
        if (lines.Count == 0)
            return [];
        var parameters = Window(zonesByKey, window);
        parameters["lines"] = lines;
        var rows = await ExecuteSqlAsync<LineBinData>(LineBinsSql, parameters, ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Where(r => zonesByKey.ContainsKey(r.ZoneKey)).Select(r => new LineBinCount(zonesByKey[r.ZoneKey], r.LineName, Utc(r.BinStartUtc), r.ProfileVersion,
                r.CrossingsIn, r.CrossingsOut, Parse<LineCountSource>(r.Source)))];
    }

    /// <summary>The zones' device outages over the window as Degraded intervals (an open one ends at the window's end); null beyond the bound.</summary>
    internal async Task<List<QualityInterval>> OutagesAsync(IReadOnlyDictionary<string, string> zonesByKey, UtcWindow window, CancellationToken ct)
    {
        var rows = await ExecuteSqlAsync<OutageData>(OutagesSql, Window(zonesByKey, window), ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Where(r => zonesByKey.ContainsKey(r.ZoneKey)).Select(r => new QualityInterval(zonesByKey[r.ZoneKey], Utc(r.FromUtc), Utc(r.ToUtc), BinQuality.Degraded))];
    }

    /// <summary>The desks' stored minutes at the minutes the campaign's observers recorded for each, in the window; null beyond the bound.</summary>
    internal async Task<List<DeskMinuteRow>> DeskMinutesAsync(ValidationCampaign campaign, UtcWindow window, CancellationToken ct)
    {
        if (campaign.Desks.Count == 0)
            return [];
        // The exact keys of the desks in scope (DeskKeys.For), each mapped back to its checkpoint and desk codes; the caller has
        // refused a campaign whose keys collide (CollidingDeskKeys), so no key stands for two desks.
        var desks = campaign.Desks.ToDictionary(d => DeskKeys.For(campaign.SiteCode, d.CheckpointCode, d.DeskCode), StringComparer.Ordinal);
        var rows = await ExecuteSqlAsync<DeskMinuteData>(DeskMinutesSql, new Dictionary<string, object>
        {
            ["keys"] = desks.Keys.ToList(),
            ["campaign"] = campaign.Id.GetValueOrDefault(),
            ["site"] = campaign.SiteCode,
            ["from"] = window.FromUtc,
            ["to"] = window.ToUtc,
            ["limit"] = RowLimit + 1
        }, ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Where(r => desks.ContainsKey(r.DeskKey)).Select(r => new DeskMinuteRow(desks[r.DeskKey].CheckpointCode, desks[r.DeskKey].DeskCode, Utc(r.MinuteUtc),
                r.ClosedSeconds, r.IdleSeconds, r.ServingSeconds, r.PausedSeconds, r.UnknownSeconds, r.Degraded))];
    }

    #endregion

    /// <summary>
    /// Whether two desks of the campaign share a desk key (L6 of the ARV-104g2 review): <see cref="DeskKeys.For"/> joins the codes
    /// with '/', so a code holding '/' could make two desks one key and mix their minutes. The topology's codes cannot hold '/'
    /// (script 0008), and the campaign copies them; this guard holds whatever a row says.
    /// </summary>
    internal static bool CollidingDeskKeys(string siteCode, IEnumerable<(string CheckpointCode, string DeskCode)> desks)
    {
        ArgumentNullException.ThrowIfNull(desks);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (checkpoint, desk) in desks)
        {
            if (checkpoint is null || desk is null || checkpoint.Contains('/', StringComparison.Ordinal) || desk.Contains('/', StringComparison.Ordinal) ||
                !keys.Add(DeskKeys.For(siteCode, checkpoint, desk)))
                return true;
        }

        return false;
    }

    /// <summary>A window cut into pieces of at most 12 hours (desk minutes of 100 desks over a day would pass the rows one read may hold; L3).</summary>
    internal static IEnumerable<UtcWindow> HalfDays(UtcWindow window)
    {
        for (var from = window.FromUtc; from < window.ToUtc; from += TimeSpan.FromHours(12))
            yield return new UtcWindow(from, from + TimeSpan.FromHours(12) < window.ToUtc ? from + TimeSpan.FromHours(12) : window.ToUtc);
    }

    #region Evidence beside the comparison

    /// <summary>The availability ledger over the campaign's planned local days (ARV-118): each planned day and the total.</summary>
    private async Task<ValidationResultsViewModel.AvailabilityView> AvailabilityAsync(ValidationCampaign campaign, CancellationToken ct)
    {
        var days = campaign.Days;
        if (days.Count == 0)
            return new ValidationResultsViewModel.AvailabilityView(AvailabilitySummary.PilotTarget, AvailabilityCounts.Zero, []);
        // The UTC bounds only let the hypertable skip chunks; the local dates decide. Two days either side cover any offset.
        var rows = await ExecuteSqlAsync<AvailabilityData>(AvailabilitySql, new Dictionary<string, object>
        {
            ["site"] = campaign.SiteCode,
            ["dates"] = days.Select(ValidationCampaign.FormatDay).ToList(),
            ["fromUtc"] = DateTime.SpecifyKind(days.Min().AddDays(-2).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            ["toUtc"] = DateTime.SpecifyKind(days.Max().AddDays(3).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
        }, ct);
        var counts = new Dictionary<DateOnly, AvailabilityCounts>();
        foreach (var row in rows)
        {
            if (CalendarDates.TryParse(row.Date, out var date))
                counts[date] = new AvailabilityCounts(row.Recorded, row.Operating, row.Available, row.Unavailable, row.Unobserved, row.Maintenance, row.Closed,
                    row.StaleZone, row.MissingMinute, row.StreamLag, row.NoPublishedZones);
        }

        IReadOnlyList<AvailabilityDay> planned = [.. days.Order().Select(d => new AvailabilityDay(d, counts.GetValueOrDefault(d) ?? AvailabilityCounts.Zero))];
        return new ValidationResultsViewModel.AvailabilityView(AvailabilitySummary.PilotTarget, AvailabilitySummary.Total(planned), planned);
    }

    /// <summary>The calibration records of the site's devices of the zones in scope, performed before <paramref name="untilUtc"/>; null beyond the bound.</summary>
    private async Task<List<ValidationResultsViewModel.CalibrationView>> CalibrationsAsync(ValidationCampaign campaign, DateTime untilUtc, CancellationToken ct)
    {
        var rows = await ExecuteSqlAsync<CalibrationData>(CalibrationsSql, new Dictionary<string, object>
        {
            ["site"] = campaign.SiteCode,
            ["zones"] = campaign.Zones.Select(z => z.ZoneName).Distinct(StringComparer.Ordinal).ToList(),
            ["until"] = untilUtc,
            ["limit"] = RowLimit + 1
        }, ct);
        return rows.Count > RowLimit
            ? null
            : [.. rows.Select(r => new ValidationResultsViewModel.CalibrationView(r.DeviceCode, r.QueueZone, r.Method, r.SampleSize, r.CountingAccuracyPercent,
                r.WaitTimeErrorMinutes, r.ThresholdPercent, r.Passed, Utc(r.PerformedOn)))];
    }

    #endregion

    #region Helpers

    private Dictionary<string, object> Window(IReadOnlyDictionary<string, string> zonesByKey, UtcWindow window) => new()
    {
        ["keys"] = zonesByKey.Keys.ToList(),
        ["from"] = window.FromUtc,
        ["to"] = window.ToUtc,
        ["limit"] = RowLimit + 1
    };

    /// <summary>A stored name of <typeparamref name="T"/> exactly as written, or a value outside the enum (the engine refuses the row).</summary>
    private static T Parse<T>(string name) where T : struct, Enum =>
        name is not null && Enum.GetNames<T>().Contains(name, StringComparer.Ordinal) ? Enum.Parse<T>(name) : (T)(object)(-1);

    /// <summary>A stored instant as UTC (timestamptz comes back as UTC; a value of another kind is converted, never relabelled).</summary>
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class QueueMinuteData
    {
        public DateTime MinuteUtc { get; set; }
        public int ProfileVersion { get; set; }
        public string Status { get; set; }
        public long Waits { get; set; }
        public double? MeanWaitMinutes { get; set; }
        public double? NowcastMinutes { get; set; }
        public string NoService { get; set; }
        public bool? NowcastDegraded { get; set; }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class QueueBinData
    {
        public string ZoneKey { get; set; }
        public DateTime StartUtc { get; set; }
        public int LengthMinutes { get; set; }
        public int Revision { get; set; }
        public string Status { get; set; }
        public string Quality { get; set; }
        public int ProfileVersion { get; set; }
        public long Entries { get; set; }
        public long Abandoned { get; set; }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class HealthBinData
    {
        public string ZoneKey { get; set; }
        public DateTime StartUtc { get; set; }
        public int LengthMinutes { get; set; }
        public int Revision { get; set; }
        public string Status { get; set; }
        public int ProfileVersion { get; set; }
        public long Entries { get; set; }
        public long Exits { get; set; }
        public int? OccupancyStart { get; set; }
        public int? OccupancyEnd { get; set; }
        public long? ConservationResidual { get; set; }
        public long TracksEntered { get; set; }
        public long TracksExited { get; set; }
        public long TracksAbandoned { get; set; }
        public long TracksFragmented { get; set; }
        public long TracksCensored { get; set; }
        public long TracksRejected { get; set; }
        public long TracksOpen { get; set; }
        public double? TrackCompletionRate { get; set; }
        public int OccupancyMinutes { get; set; }
        public int CapacityMinutes { get; set; }
        public int MinutesOutsideCapacity { get; set; }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class LineBinData
    {
        public string ZoneKey { get; set; }
        public string LineName { get; set; }
        public string Source { get; set; }
        public int ProfileVersion { get; set; }
        public DateTime BinStartUtc { get; set; }
        public long CrossingsIn { get; set; }
        public long CrossingsOut { get; set; }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class OutageData
    {
        public string ZoneKey { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class DeskMinuteData
    {
        public string DeskKey { get; set; }
        public DateTime MinuteUtc { get; set; }
        public double ClosedSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double ServingSeconds { get; set; }
        public double PausedSeconds { get; set; }
        public double UnknownSeconds { get; set; }
        public bool Degraded { get; set; }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class AvailabilityData
    {
        public string Date { get; set; }
        public int Recorded { get; set; }
        public int Operating { get; set; }
        public int Available { get; set; }
        public int Unavailable { get; set; }
        public int Unobserved { get; set; }
        public int Maintenance { get; set; }
        public int Closed { get; set; }
        public int StaleZone { get; set; }
        public int MissingMinute { get; set; }
        public int StreamLag { get; set; }
        public int NoPublishedZones { get; set; }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Created by NHibernate's result transformer.")]
    private sealed class CalibrationData
    {
        public string DeviceCode { get; set; }
        public string QueueZone { get; set; }
        public string Method { get; set; }
        public int SampleSize { get; set; }
        public double CountingAccuracyPercent { get; set; }
        public double WaitTimeErrorMinutes { get; set; }
        public double ThresholdPercent { get; set; }
        public bool Passed { get; set; }
        public DateTime PerformedOn { get; set; }
    }

    #endregion
}
