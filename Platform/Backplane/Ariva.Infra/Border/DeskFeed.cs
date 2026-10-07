using System.Text.Json;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Infra.Messaging;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Border;

/// <summary>
/// The desk feed settings (<c>Border:DeskFeed</c>, ARV-049): whether Ariva.Api.Stream runs the feed, how often it reads,
/// how late AMAN's records may arrive and still be applied in order (AMAN publishes an interval when it closes, and the
/// feed reads every few seconds), and how many records one read takes at most.
/// </summary>
public sealed record DeskFeedSettings
{
    public const string SectionName = "Border:DeskFeed";

    public bool Enabled { get; init; } = true;

    public int PollSeconds { get; init; } = 15;

    public int LatenessSeconds { get; init; } = 90;

    public int MaxRead { get; init; } = 20_000;

    /// <summary>
    /// T1 for a desk without a live login source, in seconds (ARV-116, Proposed 60, pending the owner): a desk staffed by its
    /// staff zone alone is Paused once the zone has been empty this long; with an AMAN session T1 stays 3 minutes.
    /// </summary>
    public int SensorPauseSeconds { get; init; } = 60;

    /// <summary>
    /// The sensor-only desk engine's minutes written per read at most (ARV-117a, CWE-120; Proposed 120,000, also the
    /// maximum): an exact bound, the rest follow on the next read.
    /// </summary>
    public int MaxSensorMinutesPerRead { get; init; } = DeskFeed.MaxSensorMinutesPerRead;

    public IEnumerable<string> Problems()
    {
        if (PollSeconds is < 1 or > 300)
            yield return $"{SectionName}:PollSeconds is 1 to 300.";
        if (LatenessSeconds is < 0 or > 900)
            yield return $"{SectionName}:LatenessSeconds is 0 to 900.";
        if (MaxRead is < 100 or > 200_000)
            yield return $"{SectionName}:MaxRead is 100 to 200,000.";
        if (SensorPauseSeconds is < 10 or > 180)
            yield return $"{SectionName}:SensorPauseSeconds is 10 to 180 (at most T1, 3 minutes).";
        if (MaxSensorMinutesPerRead is < 100 or > DeskFeed.MaxSensorMinutesPerRead)
            yield return $"{SectionName}:MaxSensorMinutesPerRead is 100 to 120,000.";
    }

    /// <summary>The desk engine's settings: the reference values with this feed's lateness and the sensor T1 (ARV-116).</summary>
    public DeskStateSettings Engine => new()
    {
        Lateness = TimeSpan.FromSeconds(LatenessSeconds),
        MaxLate = TimeSpan.FromMinutes(15),
        SensorPauseAfter = TimeSpan.FromSeconds(SensorPauseSeconds)
    };

    /// <summary>
    /// The sensor-only desk engine's settings (ARV-117a): the published engine's, with at most
    /// <see cref="SensorOnlyDeskEngine.MaxDesks"/> desks, so the zones alone give the same states the published engine gives a
    /// desk without AMAN.
    /// </summary>
    public DeskStateSettings SensorEngine => SensorOnlyDeskEngine.Bounded(Engine);
}

/// <summary>
/// What the desk feed keeps per site between reads (<c>desk_feed_state</c>, script 0031): the engine, the read position in
/// AMAN's records, the feed heartbeat, (ARV-116) the read position in the staff and service zone readings, absent in a
/// state saved before, which then starts at the time of the read, and (ARV-117a) the sensor-only desk engine, absent in a
/// state saved before (or refused on restore), which is then rebuilt by replaying the stored zone readings.
/// </summary>
public sealed record DeskFeedState(int Version, DeskEngineState Engine, AmanFeedCursor Cursor, DateTime? HeartbeatUtc, AmanFeedCursor ZoneCursor = null,
    DeskEngineState SensorEngine = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// The desk feed of Ariva.Api.Stream (ARV-049): for each site with AMAN desk code mappings, AMAN's stored records
/// (ARV-048, read by receipt time from <see cref="AmanFeedCursor"/>) become desk state signals at their F10 rank
/// (<see cref="AmanDeskFeed"/>) for the site's <see cref="DeskStateEngine"/> (one per site, AMAN desks with a session and
/// transaction source each), whose closed minutes are written to <c>desk_minute</c>; AMAN's e-gate intervals become
/// <c>egate_minute</c> rows. Since ARV-116 the site's staff and service zones join: a desk that a staff or service zone
/// of the published zone profile names has that source too (F10 ranks 3 and 4, below AMAN's), a desk with such zones and
/// no AMAN code is in the engine with them alone (so it gets a state without AMAN), and a site with such zones and no
/// AMAN desks is read too; the readings the stream wrote to <c>desk_zone_reading</c> (counts only) are read by the time
/// they were written, with the same overlap and memory as AMAN's records, and offered as <see cref="DeskZoneReading"/>.
/// Since ARV-117a a second, sensor-only desk engine per site (<see cref="SensorOnlyDeskEngine"/>) runs beside the published
/// one: the desks with staff or service zones, with those zones as their only sources, fed the same zone readings and
/// nothing from AMAN; its closed minutes go to <c>desk_sensor_minute</c> (script 0044) for the shadow nowcast's desk term.
/// Each site is one transaction holding a per-site advisory lock (class 49), so replicas share the sites and none is read
/// twice; both engines' snapshots, the read positions and the heartbeat are saved with the rows.
/// Keys are site, checkpoint and desk code (<see cref="AmanDeskFeed.Key"/>). Parameterised SQL only.
/// </summary>
public sealed class DeskFeed(DatabaseSettings database, TimeProvider timeProvider, DeskFeedSettings settings, ILogger<DeskFeed> logger)
{
    /// <summary>A saved state larger than this is refused (CWE-120).</summary>
    public const int MaxStateBytes = 64 * 1024 * 1024;

    /// <summary>
    /// How far back the sensor-only engine is rebuilt from the stored zone readings when its snapshot is absent or
    /// refused (ARV-117a): the engine's own limit for late readings (<see cref="DeskStateSettings.MaxLate"/>), long enough
    /// for T2 (10 minutes) to be known again.
    /// </summary>
    public static readonly TimeSpan SensorRebuild = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The sensor-only engine's minutes written per read at most (CWE-120), the default and the maximum of
    /// <see cref="DeskFeedSettings.MaxSensorMinutesPerRead"/>; the rest follow on the next read.
    /// </summary>
    public const int MaxSensorMinutesPerRead = 120_000;

    /// <summary>Reads every site's new records once; returns the sites read.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var sites = await SitesAsync(ct);
        var read = 0;
        foreach (var site in sites)
        {
            try
            {
                if (await SiteAsync(site, ct))
                    read++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // one site that fails is logged and tried again next time; the other sites go on
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Desk feed of site {Site} failed", site);
            }
        }

        return read;
    }

    /// <summary>
    /// The sites the feed reads: those with AMAN desk code mappings, and (ARV-116) those whose published zone profile has a
    /// staff or service zone naming a desk that is not an e-gate.
    /// </summary>
    public async Task<IReadOnlyList<string>> SitesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadAsync(connection, null, """
            SELECT m.site_code FROM desk_code_mapping m JOIN desk d ON d.id = m.desk_id AND d.site_code = m.site_code
             WHERE m.system = 'Aman' AND m.deleted_on IS NULL AND d.deleted_on IS NULL
            UNION
            SELECT p.site_code FROM zone_profile p JOIN zone z ON z.profile_id = p.id
              JOIN desk d ON d.id = z.desk_id AND d.site_code = p.site_code AND d.deleted_on IS NULL AND d.kind <> 'EGate'
             WHERE p.status = 'Published' AND z.kind IN ('Staff', 'Service')
            ORDER BY 1
            """, [], r => r.GetString(0), ct);
    }

    /// <summary>Reads one site's new records into its engine and minutes; false when another replica holds the site.</summary>
    public async Task<bool> SiteAsync(string siteCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteCode);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var site = new NpgsqlParameter("site", siteCode);
        var locked = await ReadAsync(connection, transaction, "SELECT pg_try_advisory_xact_lock(49, hashtext(@site))", [site], r => r.GetBoolean(0), ct);
        if (!locked[0])
            return false;

        // The site's AMAN desks and gates, and (ARV-116) the desks a staff or service zone of the published profile names,
        // with the zone roles they have; e-gates have no staff or service zone.
        var desks = await ReadAsync(connection, transaction, """
            WITH profile AS (
                SELECT id FROM zone_profile WHERE site_code = @site AND status = 'Published' ORDER BY version DESC LIMIT 1
            ), zones AS (
                SELECT z.desk_id, bool_or(z.kind = 'Staff') AS staff, bool_or(z.kind = 'Service') AS service
                  FROM zone z JOIN profile p ON p.id = z.profile_id
                 WHERE z.kind IN ('Staff', 'Service') AND z.desk_id IS NOT NULL AND z.queue_zone_id IS NOT NULL
                 GROUP BY z.desk_id
            ), aman AS (
                SELECT DISTINCT m.desk_id FROM desk_code_mapping m WHERE m.site_code = @site AND m.system = 'Aman' AND m.deleted_on IS NULL
            )
            SELECT c.code, d.code, COALESCE(d.lane_category_codes, ''), d.kind, a.desk_id IS NOT NULL,
                   d.kind <> 'EGate' AND COALESCE(zn.staff, false), d.kind <> 'EGate' AND COALESCE(zn.service, false)
              FROM desk d
              JOIN checkpoint c ON c.id = d.checkpoint_id
              LEFT JOIN aman a ON a.desk_id = d.id
              LEFT JOIN zones zn ON zn.desk_id = d.id
             WHERE d.site_code = @site AND d.deleted_on IS NULL
               AND ((a.desk_id IS NOT NULL AND d.kind IN ('Desk', 'EGate')) OR (zn.desk_id IS NOT NULL AND d.kind <> 'EGate'))
             ORDER BY c.code, d.code
            """, [site.Clone()], r => (Key: AmanDeskFeed.Key(siteCode, r.GetString(0), r.GetString(1)), Lane: Lane(r.GetString(2)), Kind: r.GetString(3),
                Aman: r.GetBoolean(4), Staff: r.GetBoolean(5), Service: r.GetBoolean(6)), ct);
        // Checkpoint codes are unique per level, so two desks can share a key: both are left out (and logged) rather than merged.
        var shared = desks.GroupBy(d => d.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        if (shared.Count > 0)
            logger.LogWarning("Desk feed of site {Site}: {Count} desk keys are shared by desks of checkpoints with the same code on different levels; they are left out",
                siteCode, shared.Count);
        // AMAN desks have AMAN's sources and their zones; a desk with zones and no AMAN code has its zones alone (ARV-116). A key
        // longer than the desk tables hold is left out (the engine refuses it).
        var profiles = desks.Where(d => !shared.Contains(d.Key) && DeskKeys.Fits(d.Key) && ((d.Aman && d.Kind == "Desk") || (!d.Aman && (d.Staff || d.Service))))
            .Select(d => new DeskProfile(d.Key, d.Lane, HasTransactions: d.Aman, HasSession: d.Aman, HasStaffZone: d.Staff, HasServiceZone: d.Service)).ToList();
        var amanDesks = profiles.Where(p => p.HasSession).Select(p => p.DeskCode).ToList();

        var saved = await ReadAsync(connection, transaction,
            "SELECT CASE WHEN octet_length(state::text) <= @max THEN state::text END FROM desk_feed_state WHERE site_code = @site FOR UPDATE",
            [site.Clone(), new NpgsqlParameter("max", MaxStateBytes)], r => r.IsDBNull(0) ? null : r.GetString(0), ct);
        if (saved.Count > 0 && saved[0] is null)
            logger.LogWarning("The desk feed state of site {Site} is larger than {Max} bytes; its desks start again from now", siteCode, MaxStateBytes);
        var state = saved.Count == 0 || saved[0] is null ? null : Load(saved[0]);
        var engineSettings = settings.Engine;
        DeskStateEngine engine;
        try
        {
            engine = state?.Engine is null ? new DeskStateEngine(profiles, Floor(now), engineSettings) : DeskStateEngine.Restore(profiles, engineSettings, state.Engine);
        }
        catch (InvalidDataException e)
        {
            logger.LogWarning(e, "The desk feed state of site {Site} is not valid; the desks start again from now", siteCode);
            engine = new DeskStateEngine(profiles, Floor(now), engineSettings);
            state = null;
        }

        // ARV-117a: the sensor-only engine of the site's desks with zones, from its snapshot when valid, otherwise rebuilt
        // from the stored readings (a state saved before, a refused or oversized snapshot, a refused published state).
        var zoneCursor = state?.ZoneCursor ?? AmanFeedCursor.Start(now);
        var sensorSettings = settings.SensorEngine;
        SensorOnlyDeskEngine sensor = null;
        var sensorMinutes = new List<DeskMinute>();
        if (state?.SensorEngine is { } savedSensor)
        {
            try
            {
                sensor = SensorOnlyDeskEngine.Restore(profiles, sensorSettings, savedSensor);
            }
            catch (InvalidDataException)
            {
                // The exception text may name a desk; the warning names the site only.
                logger.LogWarning("The sensor-only desk state of site {Site} is not valid; it is rebuilt from the stored zone readings", siteCode);
            }
        }

        sensor ??= await RebuildSensorAsync(connection, transaction, siteCode, profiles, sensorSettings, zoneCursor, now, sensorMinutes, ct);
        if (sensor.LeftOut > 0)
            logger.LogWarning("Sensor-only desk engine of site {Site}: {Count} desks with zones beyond its cap are left out", siteCode, sensor.LeftOut);
        var sensorRefusedBefore = Refused(sensor.Counters);

        var cursor = state?.Cursor ?? AmanFeedCursor.Start(now);
        var records = await ReadAsync(connection, transaction, """
            SELECT 'S', b.id, b.received_utc, c.code, d.code, b.occurred_utc, b.state, 0, 0, 0, 0
              FROM border_desk_session b LEFT JOIN desk d ON d.id = b.desk_id AND d.site_code = b.site_code LEFT JOIN checkpoint c ON c.id = d.checkpoint_id
             WHERE b.site_code = @site AND b.received_utc >= @from AND b.id <> ALL(@taken)
            UNION ALL
            SELECT 'I', b.id, b.received_utc, c.code, d.code, b.interval_start_utc, NULL, b.transactions_processed, 0, 0, b.mean_cycle_seconds
              FROM border_desk_interval b LEFT JOIN desk d ON d.id = b.desk_id AND d.site_code = b.site_code LEFT JOIN checkpoint c ON c.id = d.checkpoint_id
             WHERE b.site_code = @site AND b.received_utc >= @from AND b.id <> ALL(@taken)
            UNION ALL
            SELECT 'G', b.id, b.received_utc, c.code, d.code, b.interval_start_utc, NULL, 0, b.attempts, b.rejected, b.mean_cycle_seconds
              FROM border_egate_interval b LEFT JOIN desk d ON d.id = b.desk_id AND d.site_code = b.site_code LEFT JOIN checkpoint c ON c.id = d.checkpoint_id
             WHERE b.site_code = @site AND b.received_utc >= @from AND b.id <> ALL(@taken)
             ORDER BY 3, 2
             LIMIT @limit
            """, [site.Clone(), new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = cursor.ReadFromUtc },
                new NpgsqlParameter("taken", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = cursor.TakenIds }, new NpgsqlParameter("limit", settings.MaxRead)],
            r => new AmanFeedRecord(
                r.GetString(0) switch { "S" => AmanRecordKind.DeskSession, "I" => AmanRecordKind.DeskInterval, _ => AmanRecordKind.EgateInterval },
                r.GetGuid(1), Utc(r.GetDateTime(2)),
                r.IsDBNull(3) || r.IsDBNull(4) || shared.Contains(AmanDeskFeed.Key(siteCode, r.GetString(3), r.GetString(4)))
                    ? null : AmanDeskFeed.Key(siteCode, r.GetString(3), r.GetString(4)),
                r.GetString(0) == "G" ? Ariva.Core.Domain.Entities.LaneCategory.EGateEligible : null,
                Utc(r.GetDateTime(5)), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9),
                r.IsDBNull(10) ? 0 : r.GetDouble(10)), ct);

        var (fresh, next) = cursor.Take(records, 2 * settings.MaxRead);
        var step = AmanDeskFeed.Step(fresh, amanDesks, state?.HeartbeatUtc, now);
        foreach (var signal in step.Signals)
            engine.Offer(signal, now);

        // The staff and service zone readings the stream wrote since the last read (ARV-116). Values come back from storage
        // and are checked again (CWE-501): the role by its exact name; the engine refuses a desk it does not hold, a role
        // the desk has no zone for, and a count above its bound. Readings older than a day are not read (the engine refuses
        // anything 15 minutes behind its watermark).
        var readings = await ReadAsync(connection, transaction, """
            SELECT id, written_utc, desk_code, source, reading_utc, occupancy, degraded FROM desk_zone_reading
             WHERE site_code = @site AND written_utc >= @from AND reading_utc >= @oldest AND id <> ALL(@taken)
             ORDER BY written_utc, id
             LIMIT @limit
            """, [site.Clone(), new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = zoneCursor.ReadFromUtc },
                new NpgsqlParameter("oldest", NpgsqlDbType.TimestampTz) { Value = now.AddDays(-1) },
                new NpgsqlParameter("taken", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = zoneCursor.TakenIds }, new NpgsqlParameter("limit", settings.MaxRead)],
            r => new DeskZoneRow(r.GetGuid(0), Utc(r.GetDateTime(1)), r.GetString(2), r.GetString(3), Utc(r.GetDateTime(4)), r.GetInt16(5), r.GetBoolean(6)), ct);
        var (freshReadings, nextZones) = zoneCursor.Take(readings, z => z.Id, z => z.WrittenUtc, 2 * settings.MaxRead);
        foreach (var reading in freshReadings)
        {
            if (reading.Signal() is { } signal)
            {
                engine.Offer(signal, now);
                // The same reading, and nothing else, to the sensor-only engine (ARV-117a; it refuses any other signal).
                sensor.Offer(signal, now);
            }
        }

        var minutes = new List<DeskMinute>();
        for (var i = 0; i < 100; i++)
        {
            var advanced = engine.Advance(now);
            minutes.AddRange(advanced.Minutes);
            if (!advanced.More)
                break;
        }

        AdvanceSensor(sensor, now, sensorMinutes, siteCode);
        var sensorRefused = Refused(sensor.Counters) - sensorRefusedBefore;
        if (sensorRefused > 0)
            logger.LogWarning("Sensor-only desk engine of site {Site}: {Count} zone readings refused (bounds or validation)", siteCode, sensorRefused);

        if (minutes.Count > 0)
            await StreamStore.WriteDeskMinutesAsync(connection, minutes, now, ct);
        // In the same transaction as the published desk minutes (ARV-117a).
        if (sensorMinutes.Count > 0)
            await StreamStore.WriteDeskSensorMinutesAsync(connection, sensorMinutes, now, ct);
        if (step.EgateMinutes.Count > 0)
            await WriteEgateMinutesAsync(connection, transaction, step.EgateMinutes, now, ct);

        var json = JsonSerializer.Serialize(new DeskFeedState(DeskFeedState.CurrentVersion, engine.Capture(), next, step.HeartbeatUtc, nextZones, sensor.Capture()),
            EventCatalog.Json);
        if (json.Length > MaxStateBytes / 4)
        {
            // A state this large (the cursor or the engine's buffers far beyond any airport) is not kept: the desks start
            // again from now at the read position reached (logged), keeping the records taken at that very instant so none is
            // taken twice, and the next read stays bounded; the sensor-only engine is not kept and is rebuilt by replay then.
            logger.LogWarning("The desk feed state of site {Site} is too large to keep; its desks start again from now", siteCode);
            json = JsonSerializer.Serialize(new DeskFeedState(DeskFeedState.CurrentVersion, new DeskStateEngine(profiles, Floor(now), engineSettings).Capture(),
                new AmanFeedCursor(next.PositionUtc, [.. next.Taken.Where(t => t.ReceivedUtc == next.PositionUtc)], next.PositionUtc), step.HeartbeatUtc,
                new AmanFeedCursor(nextZones.PositionUtc, [.. nextZones.Taken.Where(t => t.ReceivedUtc == nextZones.PositionUtc)], nextZones.PositionUtc)), EventCatalog.Json);
        }

        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO desk_feed_state (site_code, state, updated_on) VALUES (@site, CAST(@state AS jsonb), @now)
            ON CONFLICT (site_code) DO UPDATE SET state = EXCLUDED.state, updated_on = EXCLUDED.updated_on
            """, connection, transaction))
        {
            upsert.Parameters.Add(new NpgsqlParameter("site", siteCode));
            upsert.Parameters.Add(new NpgsqlParameter("state", json));
            upsert.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        if (fresh.Count > 0 || freshReadings.Count > 0)
            logger.LogDebug("Desk feed of site {Site}: {Records} records, {Readings} zone readings, {Minutes} desk minutes, {Sensor} sensor-only desk minutes, {Gates} e-gate minutes",
                siteCode, fresh.Count, freshReadings.Count, minutes.Count, sensorMinutes.Count, step.EgateMinutes.Count);
        return true;
    }

    // The readings an engine refused for its bounds or as invalid (CWE-120, CWE-501), for the counted warning.
    private static long Refused(DeskEngineCounters c) => c.BufferFull + c.TooLate + c.Future + c.Invalid + c.UnknownDesk;

    // Moves the sensor-only engine to the clock with at most MaxSensorMinutesPerRead minutes in the read, counting those a
    // rebuild already closed: each step takes only what the cap leaves, so the bound is exact (the rest follow next read).
    private void AdvanceSensor(SensorOnlyDeskEngine sensor, DateTime now, List<DeskMinute> minutes, string siteCode)
    {
        var cap = settings.MaxSensorMinutesPerRead;
        for (var i = 0; i < 100; i++)
        {
            var room = cap - minutes.Count;
            if (room <= 0)
                break;
            var advanced = sensor.Advance(now, room);
            minutes.AddRange(advanced.Minutes);
            if (!advanced.More)
                return;
        }

        // The site and a count only (no desk key and no exception text).
        logger.LogWarning("Sensor-only desk engine of site {Site}: {Count} minutes in one read; the rest follow on the next read", siteCode, minutes.Count);
    }

    /// <summary>
    /// A sensor-only engine rebuilt by replaying the site's stored zone readings of the last <see cref="SensorRebuild"/>
    /// (ARV-117a): every reading the zone cursor has already passed (the rest are this read's fresh readings), in reading
    /// time, a minute at a time, so the engine holds at most a minute of readings per desk; the minutes it closes are
    /// returned for writing (an upsert, so a minute written before is replaced by the same zones' result). At most
    /// <see cref="DeskFeedSettings.MaxRead"/> readings, the latest ones. Values come back from storage and are checked
    /// again (CWE-501): the role by its exact name, and the engine refuses what it would refuse live.
    /// </summary>
    private async Task<SensorOnlyDeskEngine> RebuildSensorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode,
        IReadOnlyList<DeskProfile> profiles, DeskStateSettings sensorSettings, AmanFeedCursor zoneCursor, DateTime now, List<DeskMinute> minutes, CancellationToken ct)
    {
        var start = Floor(now - SensorRebuild);
        var sensor = SensorOnlyDeskEngine.Start(profiles, start, sensorSettings);
        var stored = await ReadAsync(connection, transaction, """
            SELECT id, written_utc, desk_code, source, reading_utc, occupancy, degraded FROM desk_zone_reading
             WHERE site_code = @site AND reading_utc >= @start AND reading_utc <= @now AND (written_utc < @from OR id = ANY(@taken))
             ORDER BY reading_utc DESC, id DESC
             LIMIT @limit
            """, [new NpgsqlParameter("site", siteCode), new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = start },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = zoneCursor.ReadFromUtc },
                new NpgsqlParameter("taken", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = zoneCursor.TakenIds }, new NpgsqlParameter("limit", settings.MaxRead)],
            r => new DeskZoneRow(r.GetGuid(0), Utc(r.GetDateTime(1)), r.GetString(2), r.GetString(3), Utc(r.GetDateTime(4)), r.GetInt16(5), r.GetBoolean(6)), ct);
        stored.Reverse();
        var boundary = start.AddMinutes(1);
        foreach (var row in stored)
        {
            // Within the minutes-per-read cap too (a low configured cap): once it is reached the replay stops advancing, the
            // readings are still offered, and the rest follow on the next read.
            while (row.TimeUtc >= boundary && boundary <= now && settings.MaxSensorMinutesPerRead - minutes.Count > 0)
            {
                minutes.AddRange(sensor.Advance(boundary + sensorSettings.Lateness, settings.MaxSensorMinutesPerRead - minutes.Count).Minutes);
                boundary = boundary.AddMinutes(1);
            }

            if (row.Signal() is { } signal)
                sensor.Offer(signal, now);
        }

        logger.LogInformation("Sensor-only desk engine of site {Site} rebuilt from {Count} stored zone readings", siteCode, stored.Count);
        return sensor;
    }

    private DeskFeedState Load(string json)
    {
        if (json.Length > MaxStateBytes)
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<DeskFeedState>(json, EventCatalog.Json);
            return state?.Version == DeskFeedState.CurrentVersion ? state : null;
        }
        catch (JsonException e)
        {
            logger.LogWarning(e, "A desk feed state could not be read; its site starts again from now");
            return null;
        }
    }

    private static async Task WriteEgateMinutesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<EgateMinute> rows, DateTime now,
        CancellationToken ct)
    {
        foreach (var g in rows.GroupBy(r => (r.GateKey, r.MinuteUtc)).Select(g => g.Last()))
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO egate_minute (gate_code, lane, minute_utc, in_service_seconds, processed, rejected, mean_cycle_seconds, degraded, updated_on)
                VALUES (@gate, @lane, @minute, 60, @processed, @rejected, @cycle, false, @now)
                ON CONFLICT (gate_code, minute_utc) DO UPDATE SET lane = EXCLUDED.lane, in_service_seconds = EXCLUDED.in_service_seconds,
                    processed = EXCLUDED.processed, rejected = EXCLUDED.rejected, mean_cycle_seconds = EXCLUDED.mean_cycle_seconds,
                    degraded = EXCLUDED.degraded, updated_on = EXCLUDED.updated_on
                """, connection, transaction);
            upsert.Parameters.Add(new NpgsqlParameter("gate", g.GateKey));
            upsert.Parameters.Add(new NpgsqlParameter("lane", g.Lane));
            upsert.Parameters.Add(new NpgsqlParameter("minute", NpgsqlDbType.TimestampTz) { Value = g.MinuteUtc });
            upsert.Parameters.Add(new NpgsqlParameter("processed", g.Processed));
            upsert.Parameters.Add(new NpgsqlParameter("rejected", g.Rejected));
            upsert.Parameters.Add(new NpgsqlParameter("cycle", NpgsqlDbType.Double) { Value = (object)g.MeanCycleSeconds ?? DBNull.Value });
            upsert.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            await upsert.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<List<T>> ReadAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, NpgsqlParameter[] parameters, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }

    // A desk's lanes as the engine groups it (its lane category codes; a border desk always has one, ARV-013).
    private static string Lane(string codes) => string.IsNullOrWhiteSpace(codes) ? "UNASSIGNED" : codes.Length > 64 ? codes[..64] : codes;

    private static DateTime Floor(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>A stored staff or service zone reading as the desk feed reads it (ARV-116, <c>desk_zone_reading</c>): counts only.</summary>
internal sealed record DeskZoneRow(Guid Id, DateTime WrittenUtc, string DeskKey, string Source, DateTime TimeUtc, int Count, bool Degraded)
{
    /// <summary>The engine's signal, or null for a role that is not a staff or service zone (only the exact names are taken).</summary>
    public DeskZoneReading Signal() => Source switch
    {
        nameof(DeskSource.StaffZone) => new DeskZoneReading(DeskKey, TimeUtc, DeskSource.StaffZone, Count, Degraded),
        nameof(DeskSource.ServiceZone) => new DeskZoneReading(DeskKey, TimeUtc, DeskSource.ServiceZone, Count, Degraded),
        _ => null
    };
}

/// <summary>Runs the desk feed every <see cref="DeskFeedSettings.PollSeconds"/> in Ariva.Api.Stream (ARV-049).</summary>
public sealed class DeskFeedWorker(DeskFeed feed, DeskFeedSettings settings, TimeProvider timeProvider, ILogger<DeskFeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PollSeconds), timeProvider);
        do
        {
            try
            {
                await feed.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one failed read is logged; the next tries again from the saved position
            catch (Exception e)
#pragma warning restore CA1031
            {
                logger.LogError(e, "Desk feed read failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
