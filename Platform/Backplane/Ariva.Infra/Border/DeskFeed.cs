using System.Text.Json;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Infra.Messaging;
using Ariva.Infra.Resilience;
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

    /// <summary>
    /// k of F10 rows 5b to 5e (ARV-149, Proposed 4, 2 to 20): a desk without staff or service zones is Paused only after a
    /// silence of k times its lane's expected service time, never less than T1 (3 minutes) nor more than 30 minutes.
    /// </summary>
    public double RhythmPauseMultiple { get; init; } = 4;

    /// <summary>
    /// The window of AMAN intervals the lane's expected service time is taken over, in minutes (ARV-149, Proposed 15, 5 to 60),
    /// plus the 2 minutes an interval may arrive late by.
    /// </summary>
    public int RhythmWindowMinutes { get; init; } = 15;

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
        if (!double.IsFinite(RhythmPauseMultiple) || RhythmPauseMultiple < 2 || RhythmPauseMultiple > 20)
            yield return $"{SectionName}:RhythmPauseMultiple is 2 to 20.";
        if (RhythmWindowMinutes is < 5 or > 60)
            yield return $"{SectionName}:RhythmWindowMinutes is 5 to 60.";
    }

    /// <summary>
    /// The desk engine's settings: the reference values with this feed's lateness, the sensor T1 (ARV-116) and the pause
    /// multiple of the transaction rhythm (ARV-149).
    /// </summary>
    public DeskStateSettings Engine => new()
    {
        Lateness = TimeSpan.FromSeconds(LatenessSeconds),
        MaxLate = TimeSpan.FromMinutes(15),
        SensorPauseAfter = TimeSpan.FromSeconds(SensorPauseSeconds),
        RhythmPauseMultiple = RhythmPauseMultiple
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
/// state saved before, which then starts at the time of the read, (ARV-117a) the sensor-only desk engine, absent in a
/// state saved before (or refused on restore), which is then rebuilt by replaying the stored zone readings, and (ARV-149)
/// the whole minute of the latest lane contexts given, so each minute gives them once.
/// </summary>
public sealed record DeskFeedState(int Version, DeskEngineState Engine, AmanFeedCursor Cursor, DateTime? HeartbeatUtc, AmanFeedCursor ZoneCursor = null,
    DeskEngineState SensorEngine = null, DateTime? ContextUtc = null)
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
/// Since ARV-149 each AMAN desk without staff or service zones gets, once per whole minute and from the engine's watermark of
/// the read, its lane context (F10 rows 5a to 5f, <see cref="DeskRhythm"/>): the lane cycle time per person of the queue zones it serves (as the nowcast's desk term
/// matches them) from AMAN's intervals of the last <see cref="DeskFeedSettings.RhythmWindowMinutes"/>, and the people in
/// those zones from their latest live minute (<c>queue_minute</c>), so its state follows the transaction rhythm.
/// Each site is one transaction holding a per-site advisory lock (class 49), so replicas share the sites and none is read
/// twice; both engines' snapshots, the read positions and the heartbeat are saved with the rows.
/// Keys are site, checkpoint and desk code (<see cref="AmanDeskFeed.Key"/>). Parameterised SQL only.
/// </summary>
public sealed class DeskFeed(DatabaseSettings database, TimeProvider timeProvider, DeskFeedSettings settings, ILogger<DeskFeed> logger,
    StreamSettings stream = null)
{
    /// <summary>A saved state larger than this is refused (CWE-120).</summary>
    public const int MaxStateBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The seconds D allows beyond the stream's timing for writing a closed minute's length (ARV-149a): the checkpoint that
    /// writes it (<c>Kafka:Consumers:CheckpointSeconds</c>, 5 by default) and the write itself.
    /// </summary>
    public const int QueueLengthWriteSeconds = 15;

    /// <summary>
    /// D of the lane context's queue (ARV-149a, F10): how long after a minute ends the stream has written its length, for
    /// <paramref name="siteCode"/>, from the stream's own settings in this host (Ariva.Api.Stream runs both): the site's
    /// lateness (a minute closes when the zone's watermark, its clock less the lateness, passes its end), its push interval
    /// (a zone's clock trails the receive clock by up to a push, and a zone without events moves on with the receive clock
    /// less it), the idle tick (a zone without events moves its clock on at most this often) and
    /// <see cref="QueueLengthWriteSeconds"/>. 180 s with the defaults (90 + 60 + 15 + 15). Too short a D only makes a zone
    /// not known sooner (fail closed); a long one cannot let a minute stand beyond the queue's freshness of
    /// <see cref="DeskRhythm.QueueFreshMinutes"/> minutes.
    /// </summary>
    public static TimeSpan QueueLengthDue(StreamSettings streamSettings, string siteCode)
    {
        var timing = streamSettings ?? new StreamSettings();
        var (lateness, push) = timing.TimingOf(siteCode);
        return TimeSpan.FromSeconds((long)Math.Max(0, lateness) + Math.Max(0, push) + Math.Max(0, timing.IdleTickSeconds) + QueueLengthWriteSeconds);
    }

    /// <summary>D for <paramref name="siteCode"/> from the stream settings this feed was built with (<see cref="QueueLengthDue"/>).</summary>
    public TimeSpan QueueLengthDueFor(string siteCode) => QueueLengthDue(stream, siteCode);

    /// <summary>
    /// The longest D for which a fresh minute's missing length can ever be overdue (ARV-149a): a minute is fresh back to the
    /// queue's freshness before the engine's watermark (the read less the feed's lateness), and overdue from a minute and D
    /// after its start, so D must be at most the freshness less a minute plus the feed's lateness: 240 + 90 = 330 s by default.
    /// </summary>
    public static TimeSpan QueueLengthDueWindow(DeskFeedSettings feedSettings) =>
        TimeSpan.FromMinutes(DeskRhythm.QueueFreshMinutes - 1) + TimeSpan.FromSeconds(Math.Max(0, (feedSettings ?? new DeskFeedSettings()).LatenessSeconds));

    /// <summary>
    /// The sites whose D is beyond <see cref="QueueLengthDueWindow"/> (ARV-149a, R-f of its security re-check), in site code
    /// order, with their D and the window: their fresh minutes' missing lengths are never overdue, so their zones stay not known
    /// in the lane contexts without a count or a warning per read. A null site stands for the stream's defaults (every site
    /// without timing of its own). Only keys that are site codes are named (the stream refuses the others at start).
    /// </summary>
    public static IReadOnlyList<(string Site, TimeSpan Due, TimeSpan Window)> QueueLengthsNeverDue(StreamSettings streamSettings, DeskFeedSettings feedSettings)
    {
        var timing = streamSettings ?? new StreamSettings();
        var window = QueueLengthDueWindow(feedSettings);
        var found = new List<(string Site, TimeSpan Due, TimeSpan Window)>();
        var byDefault = QueueLengthDue(timing, null);
        if (byDefault > window)
            found.Add((null, byDefault, window));
        foreach (var code in (timing.Sites?.Keys ?? Enumerable.Empty<string>()).Where(Ariva.Core.Domain.Entities.Site.IsValidCode).Order(StringComparer.Ordinal))
        {
            var due = QueueLengthDue(timing, code);
            if (due > window)
                found.Add((code, due, window));
        }

        return found;
    }

    /// <summary>
    /// Warns once, when the feed starts (<see cref="DeskFeedWorker"/>), of each site whose lane contexts can never have a queue
    /// (<see cref="QueueLengthsNeverDue"/>): its desks without zones follow rows 5 and 7. The site code and the two numbers only.
    /// Returns how many warnings were written.
    /// </summary>
    public int WarnOfQueueLengthsNeverDue()
    {
        var found = QueueLengthsNeverDue(stream, settings);
        foreach (var (site, due, window) in found)
        {
            if (site is null)
                logger.LogWarning("Desk feed: the stream's default timing makes a queue minute's length due {DueSeconds} s after it, beyond the {WindowSeconds} s the lane contexts can wait; sites without timing of their own get no queue in their lane contexts",
                    (long)due.TotalSeconds, (long)window.TotalSeconds);
            else
                logger.LogWarning("Desk feed of site {Site}: the stream's timing makes a queue minute's length due {DueSeconds} s after it, beyond the {WindowSeconds} s the lane contexts can wait; its desks without zones get no queue in their lane contexts",
                    site, (long)due.TotalSeconds, (long)window.TotalSeconds);
        }

        return found.Count;
    }

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
        catch (InvalidDataException)
        {
            // The exception text may name a desk; the warning names the site only (as for the sensor-only engine below).
            logger.LogWarning("The desk feed state of site {Site} is not valid; the desks start again from now", siteCode);
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

        // ARV-149: the lane context of the AMAN desks without staff or service zones (F10 rows 5a to 5f), once per whole minute,
        // from the engine's watermark of this read (the read's time less the lateness): the earliest moment it can apply
        // without revising a closed minute, so the queue it carries (the zones' latest live minutes) is as little behind as
        // the stream allows.
        var contextUtc = state?.ContextUtc;
        var contextMinute = Floor(now);
        // CWE-501: the minute of the latest contexts comes back from storage; one after the next minute (a replica's clock a
        // little ahead is allowed for) would hold the contexts back until then, so it counts as absent.
        if (contextUtc.HasValue && contextUtc.Value > contextMinute.AddMinutes(1))
        {
            logger.LogWarning("The desk feed state of site {Site} gives its lane contexts' minute ahead of the clock; it counts as absent", siteCode);
            contextUtc = null;
        }

        var rhythmDesks = profiles.Where(DeskRule.FollowsRhythm).Select(p => p.DeskCode).ToHashSet(StringComparer.Ordinal);
        if (rhythmDesks.Count > 0 && (!contextUtc.HasValue || contextMinute > contextUtc.Value))
        {
            foreach (var context in await LaneContextsAsync(connection, transaction, siteCode, rhythmDesks, now - engineSettings.Lateness, now, ct))
                engine.Offer(context, now);
            contextUtc = contextMinute;
        }

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

        var json = JsonSerializer.Serialize(new DeskFeedState(DeskFeedState.CurrentVersion, engine.Capture(), next, step.HeartbeatUtc, nextZones, sensor.Capture(),
            contextUtc), EventCatalog.Json);
        if (json.Length > MaxStateBytes / 4)
        {
            // A state this large (the cursor or the engine's buffers far beyond any airport) is not kept: the desks start
            // again from now at the read position reached (logged), keeping the records taken at that very instant so none is
            // taken twice, and the next read stays bounded; the sensor-only engine is not kept and is rebuilt by replay then.
            logger.LogWarning("The desk feed state of site {Site} is too large to keep; its desks start again from now", siteCode);
            json = JsonSerializer.Serialize(new DeskFeedState(DeskFeedState.CurrentVersion, new DeskStateEngine(profiles, Floor(now), engineSettings).Capture(),
                new AmanFeedCursor(next.PositionUtc, [.. next.Taken.Where(t => t.ReceivedUtc == next.PositionUtc)], next.PositionUtc), step.HeartbeatUtc,
                new AmanFeedCursor(nextZones.PositionUtc, [.. nextZones.Taken.Where(t => t.ReceivedUtc == nextZones.PositionUtc)], nextZones.PositionUtc),
                ContextUtc: contextUtc), EventCatalog.Json);
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

    /// <summary>
    /// The lane contexts at <paramref name="atUtc"/> (the engine's watermark of the read) of the site's desks that follow the
    /// transaction rhythm (ARV-149, F10 rows 5a to 5f). The queue zones of the site's latest published profile with a lane category are matched to the desks (kind
    /// Desk) of the immigration and emigration checkpoints on their level whose lane categories include it, as the nowcast's
    /// desk term matches them (<c>DeskTermSource</c>); each zone's live minutes in the last
    /// <see cref="DeskRhythm.QueueFreshMinutes"/> minutes, the latest deciding (ARV-149a: one without a length whose length is
    /// due at <paramref name="readUtc"/>, <see cref="QueueLengthDue"/>, makes the zone not known, fail closed, and an earlier
    /// minute never stands in for it; one whose length is not yet due is skipped), and AMAN's intervals of the matched desks that started in the rhythm
    /// window, are read back and checked again before use (CWE-501: <see cref="DeskRhythm"/>, <see cref="LaneCycle"/>; what
    /// is left out is counted in a warning that names the site and the count only). Bounded (CWE-120): a site whose read
    /// reaches <see cref="DeskTermSource.MaxRows"/> rows of any kind gets no contexts this minute (its desks keep rows 5 and 7).
    /// Parameterised SQL only; only this site's rows (CWE-863).
    /// </summary>
    private async Task<IReadOnlyList<DeskLaneContext>> LaneContextsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode,
        IReadOnlySet<string> rhythmDesks, DateTime atUtc, DateTime readUtc, CancellationToken ct)
    {
        var max = Ariva.Infra.Streaming.DeskTermSource.MaxRows;
        var rows = await ReadAsync(connection, transaction, """
            WITH profile AS (
                SELECT id FROM zone_profile WHERE site_code = @site AND status = 'Published' ORDER BY version DESC LIMIT 1
            ), queues AS (
                SELECT z.name AS zone, z.lane_category AS lane, z.level_id
                  FROM zone z JOIN profile p ON p.id = z.profile_id
                 WHERE z.kind = 'Queue' AND z.lane_category IS NOT NULL
            ), desks AS (
                SELECT DISTINCT q.zone, d.id AS desk_id, c.code AS checkpoint_code, d.code AS desk_code
                  FROM queues q
                  JOIN checkpoint c ON c.level_id = q.level_id AND c.site_code = @site AND c.deleted_on IS NULL AND c.kind IN ('Immigration', 'Emigration')
                  JOIN desk d ON d.checkpoint_id = c.id AND d.site_code = @site AND d.deleted_on IS NULL AND d.kind = 'Desk'
                 WHERE q.lane = ANY(string_to_array(COALESCE(d.lane_category_codes, ''), ','))
            )
            (SELECT 'desk' AS what, ds.zone, ds.checkpoint_code AS a, ds.desk_code AS b, NULL::timestamptz AS at, 0 AS n1, 0 AS n2, false AS degraded,
                    0::float8 AS f1, 0::float8 AS f2, 0::float8 AS f3
               FROM desks ds
              LIMIT @max)
            UNION ALL
            (SELECT 'queue', q.zone, NULL, NULL, m.minute_utc, m.queue_length, 0, COALESCE(m.length_degraded, false), 0, 0, 0
               FROM queues q
               CROSS JOIN LATERAL (
                    SELECT qm.minute_utc, qm.queue_length, qm.length_degraded FROM queue_minute qm
                     WHERE qm.zone_key = @site || '/' || q.zone AND qm.minute_utc >= @fresh AND qm.minute_utc <= @at
                     ORDER BY qm.minute_utc DESC
                     LIMIT @perZone) m
              LIMIT @max)
            UNION ALL
            (SELECT 'interval', ds.zone, i.id::text, NULL, i.interval_start_utc, i.transactions_processed, i.documents_processed, false,
                    i.mean_service_seconds, i.p90_service_seconds, i.mean_cycle_seconds
               FROM desks ds JOIN border_desk_interval i ON i.desk_id = ds.desk_id AND i.site_code = @site
              WHERE i.interval_start_utc >= @from AND i.interval_start_utc <= @at
              LIMIT @max)
            """, [new NpgsqlParameter("site", siteCode), new NpgsqlParameter("max", max),
                // ARV-149a: every fresh minute of a zone (one a minute, so at most the freshness and one), so that a minute whose
                // length is not yet due can give way to the one before it.
                new NpgsqlParameter("perZone", DeskRhythm.QueueFreshMinutes + 1),
                new NpgsqlParameter("fresh", NpgsqlDbType.TimestampTz) { Value = atUtc.AddMinutes(-DeskRhythm.QueueFreshMinutes) },
                new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = atUtc },
                new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = atUtc.AddMinutes(-settings.RhythmWindowMinutes - 2) }],
            r => new LaneContextRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? DateTime.MinValue : Utc(r.GetDateTime(4)), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetInt32(6), r.GetBoolean(7), r.GetDouble(8), r.GetDouble(9),
                r.GetDouble(10)), ct);
        if (rows.GroupBy(r => r.What, StringComparer.Ordinal).Any(g => g.Count() >= max))
        {
            logger.LogWarning("Desk feed of site {Site}: {Max} lane context rows or more; its desks without zones keep the rows without a lane context this minute",
                siteCode, max);
            return [];
        }

        var served = rows.Where(r => r.What == "desk" && r.A is not null && r.B is not null)
            .Select(r => (DeskKey: AmanDeskFeed.Key(siteCode, r.A, r.B), r.Zone))
            .Where(s => rhythmDesks.Contains(s.DeskKey))
            .ToList();
        var queues = rows.Where(r => r.What == "queue").Select(r => new LaneQueueSample(r.Zone, r.At, r.N1, r.Degraded));
        var intervals = rows.Where(r => r.What == "interval" && r.A is not null && r.N1.HasValue)
            .Select(r => new LaneIntervalSample(r.Zone, r.A, new DeskIntervalSample(r.N1.Value, r.N2, r.F1, r.F2, r.F3)));
        // An interval read back without its transactions (the column is NOT NULL, so never in practice) is refused like one out of bounds.
        var withoutCount = rows.Count(r => r.What == "interval" && r.A is not null && !r.N1.HasValue);
        // ARV-149a: a minute's length is due D after the minute ends (D from this host's stream settings for the site).
        var result = DeskRhythm.Contexts(served, queues, intervals, atUtc, readUtc - TimeSpan.FromMinutes(1) - QueueLengthDue(stream, siteCode));
        if (result.RefusedIntervals + result.RefusedQueues + withoutCount > 0)
            logger.LogWarning("Desk feed of site {Site}: {Count} AMAN intervals or queue lengths outside their bounds were left out of the lane contexts",
                siteCode, result.RefusedIntervals + result.RefusedQueues + withoutCount);
        // ARV-149a: zones whose latest fresh minute has no length though it is due count as not known (fail closed); the site and
        // a count only. A minute whose length is not yet due is normal (the stream writes it once the minute closes): no warning.
        if (result.QueuesWithoutLength > 0)
            logger.LogWarning("Desk feed of site {Site}: {Count} queue zones' latest live minutes have no length; they count as not known in the lane contexts",
                siteCode, result.QueuesWithoutLength);
        return result.Contexts;
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

/// <summary>
/// One row of the lane context read (ARV-149): a desk serving a queue zone (its checkpoint and desk code), a queue zone's latest
/// live minute (its length, null when the minute has none, ARV-149a, and degraded flag) or an AMAN interval of a matched desk
/// (its id, counts and times). Counts only.
/// </summary>
internal sealed record LaneContextRow(string What, string Zone, string A, string B, DateTime At, int? N1, int N2, bool Degraded, double F1, double F2, double F3);

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

/// <summary>
/// Runs the desk feed every <see cref="DeskFeedSettings.PollSeconds"/> in Ariva.Api.Stream (ARV-049). A read ends at
/// <see cref="RunTimeout"/> (ARV-096e); each site is one transaction, so the sites it finished keep their minutes and positions,
/// the site it cut is rolled back, and the next read goes on from the saved positions.
/// </summary>
public sealed class DeskFeedWorker(DeskFeed feed, DeskFeedSettings settings, TimeProvider timeProvider, ILogger<DeskFeedWorker> logger) : BackgroundService, IBoundedRuns
{
    /// <summary>Two minutes: a site's read is bounded in records and its statements by the database command timeout.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>One read under the run timeout; returns the sites read. Public for tests.</summary>
    public Task<int> RunOnceAsync(CancellationToken stoppingToken) => RunBound.RunAsync("Desk feed", RunTimeout, timeProvider, feed.TickAsync, stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ARV-149a: a site whose stream timing leaves its lane contexts without a queue is named once, at start.
        feed.WarnOfQueueLengthsNeverDue();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PollSeconds), timeProvider);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
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
