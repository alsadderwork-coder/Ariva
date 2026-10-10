using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Foundation;
using Microsoft.Extensions.Options;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Sensing;

/// <summary>
/// Device heartbeats and zone degradation (ARV-025). Reads are filtered to the caller's sites (404 outside, CWE-862);
/// recording and sweeping run without a user (the health consumer and the heartbeat monitor). A device's row is locked
/// while its state changes, so a report and the sweep never both decide; the sweep takes only devices no report is
/// holding (SKIP LOCKED) and checks their heartbeat again after locking. A zone is re-assessed under an advisory lock
/// of its key after any change of its devices, and by every sweep, so registry changes (calibration, move, retirement)
/// are reflected within one sweep. Device changes raise DeviceRegistryChanged and zone changes ZoneHealthChanged,
/// both through the outbox.
/// </summary>
internal sealed class SvcDeviceHealth(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    IOptions<DeviceHealthSettings> settings,
    DeviceHealthMetrics metrics,
    ILogger<SvcDeviceHealth> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDeviceHealth
{
    private TimeSpan Timeout => TimeSpan.FromSeconds(settings.Value.HeartbeatTimeoutSeconds);

    #region Reads

    public async Task<Result<DeviceHealthOverviewViewModel>> OverviewAsync(string siteCode, CancellationToken ct = default)
    {
        var access = await siteScope.GetAsync(ct);
        if (!string.IsNullOrWhiteSpace(siteCode) && !access.Allows(siteCode))
            return Result.Error<DeviceHealthOverviewViewModel>(TopologyErrors.NotFound);

        var devices = QueryAsNoTracking<Device>().WithinSites(access).Where(d => d.State != DeviceState.Retired);
        var zones = QueryAsNoTracking<ZoneHealth>().WithinSites(access);
        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            devices = devices.Where(d => d.SiteCode == siteCode);
            zones = zones.Where(z => z.SiteCode == siteCode);
        }

        // Offline and degraded devices first, so a truncated list still shows what needs attention.
        var deviceRows = await devices.OrderByDescending(d => d.State == DeviceState.Offline).ThenByDescending(d => d.State == DeviceState.Degraded)
            .ThenBy(d => d.SiteCode).ThenBy(d => d.Code).Take(MaxRows + 1).ToListAsync(ct);
        var zoneRows = await zones.OrderByDescending(z => z.State == ZoneHealthState.Degraded).ThenBy(z => z.SiteCode).ThenBy(z => z.QueueZoneName)
            .Take(MaxRows + 1).ToListAsync(ct);
        var truncated = deviceRows.Count > MaxRows || zoneRows.Count > MaxRows;
        deviceRows = [.. deviceRows.Take(MaxRows)];
        var heartbeats = await HeartbeatsAsync(deviceRows.Select(d => d.SiteCode), ct);
        var now = UtcNow;
        return new Result<DeviceHealthOverviewViewModel>(new DeviceHealthOverviewViewModel(
            settings.Value.HeartbeatTimeoutSeconds,
            [.. deviceRows.Select(d => View(d, heartbeats.GetValueOrDefault(d.Id.GetValueOrDefault()), now))],
            [.. zoneRows.Take(MaxRows).Select(View)],
            truncated));
    }

    public async Task<Result<DeviceHealthViewModel>> GetAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await GetAsync<Device>(deviceId, ct);
        if (device is null || !(await siteScope.GetAsync(ct)).Allows(device.SiteCode))
            return Result.Error<DeviceHealthViewModel>(TopologyErrors.NotFound);
        var rows = await ExecuteSqlAsync<HeartbeatRow>("""
            SELECT device_id AS "DeviceId", last_seen_on AS "LastSeenOn", reported_online AS "ReportedOnline", frame_rate AS "FrameRate",
               temperature_celsius AS "TemperatureCelsius", clock_offset_milliseconds AS "ClockOffsetMilliseconds", clock_state AS "ClockState",
               site_code AS "SiteCodeOf"
            FROM device_heartbeat WHERE device_id = :id
            """, new Dictionary<string, object> { ["id"] = deviceId }, ct);
        return new Result<DeviceHealthViewModel>(View(device, rows.FirstOrDefault(), UtcNow));
    }

    /// <summary>Rows per overview at most; a site has tens of zones and at most a few hundred devices.</summary>
    private const int MaxRows = 2_000;


    /// <summary>The heartbeats of the sites of the listed devices, read site by site (a deployment has a handful of sites).</summary>
    private async Task<Dictionary<Guid, HeartbeatRow>> HeartbeatsAsync(IEnumerable<string> sites, CancellationToken ct)
    {
        var heartbeats = new Dictionary<Guid, HeartbeatRow>();
        foreach (var site in sites.Distinct(StringComparer.Ordinal))
        {
            var rows = await ExecuteSqlAsync<HeartbeatRow>("""
                SELECT device_id AS "DeviceId", last_seen_on AS "LastSeenOn", reported_online AS "ReportedOnline", frame_rate AS "FrameRate",
                   temperature_celsius AS "TemperatureCelsius", clock_offset_milliseconds AS "ClockOffsetMilliseconds", clock_state AS "ClockState",
                   site_code AS "SiteCodeOf"
                FROM device_heartbeat WHERE site_code = :site
                """, new Dictionary<string, object> { ["site"] = site }, ct);
            foreach (var row in rows)
                heartbeats[row.DeviceId] = row;
        }

        return heartbeats;
    }

    #endregion

    #region Recording

    public async Task RecordAsync(DeviceHealthReported report, CancellationToken ct = default)
    {
        if (report?.Status is not { } status || report.DeviceId == Guid.Empty)
            return;
        // The report crossed a trust boundary (Kafka, produced by Ingest): checked again, never trusted.
        if (CanonicalEventRules.Validate(status).Count > 0 || report.ReceivedUtc == default ||
            (report.Clock is { } clock && (!Enum.IsDefined(clock.State) || !double.IsFinite(clock.OffsetMilliseconds))))
        {
            logger.LogWarning("Device health report {EventId} for {DeviceId} is out of bounds; ignored", report.Id, report.DeviceId);
            return;
        }

        var now = UtcNow;
        var seen = DateTime.SpecifyKind(report.ReceivedUtc, DateTimeKind.Utc);
        if (seen > now)
            seen = now;

        // The device's row is locked first: a report and the sweep change a device one at a time.
        var locked = await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM device WHERE id = :id AND deleted_on IS NULL FOR UPDATE""",
            new Dictionary<string, object> { ["id"] = report.DeviceId }, ct);
        if (locked.Count == 0)
            return;
        var device = await GetAsync<Device>(report.DeviceId, ct);
        if (device is null || device.State == DeviceState.Retired)
            return;
        if (!string.Equals(report.SiteCode, device.SiteCode, StringComparison.Ordinal))
        {
            logger.LogWarning("Device health report {EventId} names site {ReportedSite} for a device of another site; ignored", report.Id, report.SiteCode);
            return;
        }

        var newer = await ExecuteCommandAsync<LockRow>("""
            INSERT INTO device_heartbeat (device_id, site_code, last_seen_on, reported_online, frame_rate, temperature_celsius, clock_offset_milliseconds, clock_state)
            VALUES (:id, :site, :seen, :online, NULLIF(:frameRate, -1.0), NULLIF(:temperature, -999.0), NULLIF(:offset, 1e12), NULLIF(:clock, ''))
            ON CONFLICT (device_id) DO UPDATE SET
                last_seen_on = EXCLUDED.last_seen_on, reported_online = EXCLUDED.reported_online, frame_rate = EXCLUDED.frame_rate,
                temperature_celsius = EXCLUDED.temperature_celsius, clock_offset_milliseconds = EXCLUDED.clock_offset_milliseconds,
                clock_state = EXCLUDED.clock_state
            WHERE device_heartbeat.last_seen_on < EXCLUDED.last_seen_on
            RETURNING 1 AS "Value"
            """, new Dictionary<string, object>
        {
            ["id"] = device.Id.GetValueOrDefault(),
            ["site"] = device.SiteCode,
            ["seen"] = seen,
            ["online"] = status.Online,
            // Absent values travel as sentinels outside the valid ranges (NHibernate cannot type a null parameter).
            ["frameRate"] = status.FrameRate ?? -1.0,
            ["temperature"] = status.TemperatureCelsius ?? -999.0,
            ["offset"] = ClockOffset(report) ?? 1e12,
            ["clock"] = report.Clock?.State.ToString() ?? string.Empty
        }, ct);

        // An older report (out of order) or one older than the timeout (a backlog after an outage) changes nothing.
        if (newer.Count == 0 || seen < now - Timeout || !device.IsCommissioned)
            return;

        var target = status.Online ? DeviceState.Online : DeviceState.Degraded;
        if (device.State == target)
            return;
        var previous = device.State;
        device.SetHealth(target, now);
        await UpdateAsync(device, ct);
        if (previous == DeviceState.Offline && target == DeviceState.Online)
            metrics.Recovered(device.SiteCode);
        logger.LogInformation("Device {Device} of {Site} is {State} (was {Previous})", device.Code, device.SiteCode, target, previous);
        await AssessZoneAsync(device.SiteCode, device.QueueZoneName, now, ct);
    }

    /// <summary>Ariva's clock estimate when it is within bounds, else what the device reported (already checked).</summary>
    private static double? ClockOffset(DeviceHealthReported report) =>
        report.Clock is { } clock && double.IsFinite(clock.OffsetMilliseconds) && Math.Abs(clock.OffsetMilliseconds) <= CanonicalEventRules.MaxClockOffsetMilliseconds
            ? clock.OffsetMilliseconds
            : report.Status.ClockOffsetMilliseconds;

    #endregion

    #region Sweep

    public async Task<DeviceHealthSweep> SweepAsync(CancellationToken ct = default)
    {
        // One sweep at a time across the replicas of Main; the others skip this round. Advisory lock class 25 (ARV-025):
        // key 0 is the sweep, every other key a zone.
        var leader = await ExecuteCommandAsync<FlagRow>("""SELECT pg_try_advisory_xact_lock(25, 0) AS "Value" """, null, ct);
        if (leader.Count == 0 || !leader[0].Value)
            return new DeviceHealthSweep(false, 0, 0, 0, 0, 0);

        var now = UtcNow;
        var cutoff = now - Timeout;
        // Devices a report is changing right now are skipped (SKIP LOCKED); their heartbeat is fresh anyway.
        var stale = await ExecuteCommandAsync<IdRow>("""
            SELECT d.id AS "Id" FROM device d LEFT JOIN device_heartbeat h ON h.device_id = d.id
            WHERE d.deleted_on IS NULL AND d.state IN ('Online', 'Degraded')
              AND COALESCE(h.last_seen_on, d.modified_on, d.created_on) < :cutoff
            ORDER BY d.id
            LIMIT 500
            FOR UPDATE OF d SKIP LOCKED
            """, new Dictionary<string, object> { ["cutoff"] = cutoff }, ct);

        var marked = 0;
        foreach (var row in stale)
        {
            // Read again after the lock: a report committed between the scan and the lock wins.
            var fresh = await ExecuteSqlAsync<LockRow>("""SELECT 1 AS "Value" FROM device_heartbeat WHERE device_id = :id AND last_seen_on >= :cutoff""",
                new Dictionary<string, object> { ["id"] = row.Id, ["cutoff"] = cutoff }, ct);
            var device = await GetAsync<Device>(row.Id, ct);
            if (fresh.Count > 0 || device is null || device.State is not (DeviceState.Online or DeviceState.Degraded))
                continue;
            device.SetHealth(DeviceState.Offline, now);
            await UpdateAsync(device, ct);
            metrics.HeartbeatLost(device.SiteCode);
            logger.LogWarning("Device {Device} of {Site} lost its heartbeat (nothing for {Seconds} s); Offline", device.Code, device.SiteCode, settings.Value.HeartbeatTimeoutSeconds);
            marked++;
        }

        if (marked > 0)
            await FlushAsync(ct);

        // Every zone whose stored health differs from its devices: new devices, calibrations, moves and retirements too.
        var counts = await ExecuteSqlAsync<ZoneCountRow>("""
            SELECT site_code AS "SiteCode", queue_zone_name AS "QueueZoneName", CAST(count(*) AS integer) AS "Devices",
                   CAST(count(*) FILTER (WHERE state = 'Offline') AS integer) AS "Offline", CAST(count(*) FILTER (WHERE state = 'Degraded') AS integer) AS "Degraded"
            FROM device WHERE deleted_on IS NULL AND state IN ('Online', 'Degraded', 'Offline')
            GROUP BY site_code, queue_zone_name
            """, null, ct);
        // Plain rows, not entities: AssessZoneAsync must read the zone again after its lock, not this session's copy.
        var stored = (await ExecuteSqlAsync<ZoneCountRow>("""
            SELECT site_code AS "SiteCode", queue_zone_name AS "QueueZoneName", devices AS "Devices", devices_offline AS "Offline",
                   devices_degraded AS "Degraded", state AS "State"
            FROM zone_health
            """, null, ct)).ToDictionary(z => (z.SiteCode, z.QueueZoneName));
        var byZone = counts.ToDictionary(c => (c.SiteCode, c.QueueZoneName));
        var changed = 0;
        foreach (var zone in byZone.Keys.Union(stored.Keys).ToList())
        {
            var count = byZone.GetValueOrDefault(zone);
            var current = stored.GetValueOrDefault(zone);
            var expected = ZoneHealth.StateFor(count?.Devices ?? 0, count?.Offline ?? 0, count?.Degraded ?? 0);
            if (current is not null && current.State == expected.ToString() && current.Devices == (count?.Devices ?? 0) &&
                current.Offline == (count?.Offline ?? 0) && current.Degraded == (count?.Degraded ?? 0))
                continue;
            if (await AssessZoneAsync(zone.SiteCode, zone.QueueZoneName, now, ct))
                changed++;
        }

        var totals = counts.Aggregate((Offline: 0, Degraded: 0, Zones: 0), (t, c) =>
            (t.Offline + c.Offline, t.Degraded + c.Degraded, t.Zones + (c.Offline + c.Degraded > 0 ? 1 : 0)));
        metrics.Snapshot(totals.Offline, totals.Degraded, totals.Zones);
        return new DeviceHealthSweep(true, marked, changed, totals.Offline, totals.Degraded, totals.Zones);
    }

    /// <summary>
    /// Re-assesses one queue zone from its devices under the zone's advisory lock; true when its state changed. A zone
    /// whose key does not fit a message key (a name stored before ARV-114c refused it) is skipped with one warning per
    /// process: its ZoneHealthChanged could not be written to the outbox, which would fail this unit of work and with it
    /// every other zone of the sweep and the device changes beside it.
    /// </summary>
    private async Task<bool> AssessZoneAsync(string siteCode, string queueZoneName, DateTime now, CancellationToken ct)
    {
        if (!ZoneKeys.Fits(siteCode, queueZoneName))
        {
            if (metrics.ZoneNotKeyable(siteCode, ZoneKeys.For(siteCode, queueZoneName)))
                logger.LogWarning(
                    "Zone {Zone} of {Site} is not assessed: its key is longer than {Max} characters, so its health events cannot be written; rename the queue zone (operations runbook, ARV-114c)",
                    queueZoneName, siteCode, Ariva.Core.Messaging.MessageKeys.MaxLength);
            return false;
        }

        await FlushAsync(ct);
        await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM (SELECT pg_advisory_xact_lock(25, CASE WHEN hashtext(:key) = 0 THEN 1 ELSE hashtext(:key) END)) l""",
            new Dictionary<string, object> { ["key"] = ZoneKeys.For(siteCode, queueZoneName) }, ct);
        var counts = await ExecuteSqlAsync<ZoneCountRow>("""
            SELECT :site AS "SiteCode", :zone AS "QueueZoneName", CAST(count(*) AS integer) AS "Devices",
                   CAST(count(*) FILTER (WHERE state = 'Offline') AS integer) AS "Offline", CAST(count(*) FILTER (WHERE state = 'Degraded') AS integer) AS "Degraded"
            FROM device WHERE deleted_on IS NULL AND site_code = :site AND queue_zone_name = :zone AND state IN ('Online', 'Degraded', 'Offline')
            """, new Dictionary<string, object> { ["site"] = siteCode, ["zone"] = queueZoneName }, ct);
        var count = counts.Single();
        var health = await Query<ZoneHealth>().FirstOrDefaultAsync(z => z.SiteCode == siteCode && z.QueueZoneName == queueZoneName, ct);
        if (health is null)
        {
            if (count.Devices == 0)
                return false;
            health = new ZoneHealth(siteCode, queueZoneName, now);
            health.Assess(count.Devices, count.Offline, count.Degraded, now);
            await SaveAsync(health, ct);
            if (health.State == ZoneHealthState.Degraded)
                metrics.ZoneDegraded(siteCode);
            return true;
        }

        var changed = health.Assess(count.Devices, count.Offline, count.Degraded, now);
        await UpdateAsync(health, ct);
        if (changed && health.State == ZoneHealthState.Degraded)
            metrics.ZoneDegraded(siteCode);
        if (changed)
            logger.LogInformation("Zone {Zone} of {Site} is {State}", queueZoneName, siteCode, health.State);
        return changed;
    }

    #endregion

    #region Views

    private DeviceHealthViewModel View(Device d, HeartbeatRow heartbeat, DateTime now) => new(
        d.Id.GetValueOrDefault(), d.Code, d.SiteCode, d.QueueZoneName, d.State.ToString(),
        heartbeat?.LastSeenOn, heartbeat is null ? null : Math.Max(0, (now - heartbeat.LastSeenOn).TotalSeconds),
        heartbeat?.ReportedOnline, heartbeat?.FrameRate, heartbeat?.TemperatureCelsius, heartbeat?.ClockOffsetMilliseconds, heartbeat?.ClockState);

    private static ZoneHealthViewModel View(ZoneHealth z) =>
        new(z.SiteCode, z.QueueZoneName, z.State.ToString(), z.Devices, z.DevicesOffline, z.DevicesDegraded, z.ChangedOn);

    #endregion

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    private sealed class FlagRow
    {
        public bool Value { get; set; }
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    private sealed class HeartbeatRow
    {
        public Guid DeviceId { get; set; }
        public DateTime LastSeenOn { get; set; }
        public bool ReportedOnline { get; set; }
        public double? FrameRate { get; set; }
        public double? TemperatureCelsius { get; set; }
        public double? ClockOffsetMilliseconds { get; set; }
        public string ClockState { get; set; }
        public string SiteCodeOf { get; set; }
    }

    private sealed class ZoneCountRow
    {
        public string State { get; set; }
        public string SiteCode { get; set; }
        public string QueueZoneName { get; set; }
        public int Devices { get; set; }
        public int Offline { get; set; }
        public int Degraded { get; set; }
    }
}
