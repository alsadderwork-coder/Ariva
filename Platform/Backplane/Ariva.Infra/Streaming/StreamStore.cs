using System.Text.Json;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Events;
using Ariva.Core.Messaging;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Outbox;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Streaming;

/// <summary>A consumer group's position in one partition, as saved with the state it produced.</summary>
public sealed record StreamOffset(string Topic, int Partition, long NextOffset);

/// <summary>What one checkpoint writes in one transaction: the zones' outputs and states, desk minutes, and the offsets.</summary>
public sealed record StreamCheckpoint(
    string ConsumerGroup,
    IReadOnlyList<ZoneOutputs> Outputs,
    IReadOnlyList<ZoneProcessorState> States,
    IReadOnlyList<DeskMinute> DeskMinutes,
    IReadOnlyList<StreamOffset> Offsets);

/// <summary>
/// Persistence of the stream host (ARV-034, script 0018; line_minute since ARV-113, script 0037; zone_health_bin since
/// ARV-114a, script 0038; overflow_minute and the OverflowDetected outbox rows since ARV-115, script 0039; the desks' staff
/// and service zone readings in desk_zone_reading since ARV-116, script 0040). A checkpoint is one
/// transaction: rows are written by binary COPY into staging tables and upserted by key (zone or desk and minute, zone,
/// line, source and minute, bin and revision), the zones' snapshots replace
/// their previous ones, and the consumer group's next offsets are recorded. Only after it commits does the consumer
/// commit its Kafka offsets; on assignment it starts from the offsets saved here, so a record is applied to a saved state
/// exactly once and replaying the records after it rewrites the same rows.
/// </summary>
public sealed class StreamStore(DatabaseSettings database, TimeProvider timeProvider, ILogger<StreamStore> logger = null)
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>A zone snapshot larger than this is refused (CWE-120): a corrupt or hostile state is not loaded.</summary>
    public const int MaxStateBytes = 64 * 1024 * 1024;

    public async Task SaveAsync(StreamCheckpoint checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var minutes = checkpoint.Outputs.SelectMany(o => o.Minutes.Select(m => (o.ZoneKey, Minute: m))).ToList();
        var live = checkpoint.Outputs.SelectMany(o => o.Live).ToList();
        var bins = checkpoint.Outputs.SelectMany(o => o.Bins.Select(b => (o.ZoneKey, Bin: b))).ToList();
        var versions = checkpoint.States.ToDictionary(s => s.ZoneKey, s => s.ProfileVersion, StringComparer.Ordinal);
        int Version(string zone) => versions.GetValueOrDefault(zone);

        if (minutes.Count > 0)
            await WriteMinutesAsync(connection, minutes, Version, now, ct);
        if (live.Count > 0)
            await WriteLiveAsync(connection, live, Version, now, ct);
        if (bins.Count > 0)
            await WriteBinsAsync(connection, bins, now, ct);
        var health = checkpoint.Outputs.SelectMany(o => o.Health.Select(h => (o.ZoneKey, Health: h))).ToList();
        if (health.Count > 0)
            await WriteHealthAsync(connection, health, now, ct);
        var lines = checkpoint.Outputs.SelectMany(o => o.Lines.Select(l => (o.ZoneKey, Line: l))).ToList();
        if (lines.Count > 0)
            await WriteLineMinutesAsync(connection, lines, Version, now, ct);
        var overflow = checkpoint.Outputs.SelectMany(o => o.Overflow.Select(m => (o.ZoneKey, Minute: m))).ToList();
        if (overflow.Count > 0)
            await WriteOverflowMinutesAsync(connection, overflow, Version, now, ct);
        var changes = checkpoint.Outputs.SelectMany(o => o.OverflowChanges.Select(c => (o.ZoneKey, Change: c))).ToList();
        if (changes.Count > 0)
            await WriteOverflowEventsAsync(connection, changes, Version, now, ct);
        var deskReadings = checkpoint.Outputs.SelectMany(o => o.DeskReadings.Select(d => (o.ZoneKey, Reading: d))).ToList();
        if (deskReadings.Count > 0)
            await WriteDeskZoneReadingsAsync(connection, deskReadings, Version, now, ct);
        if (checkpoint.DeskMinutes.Count > 0)
            await WriteDeskMinutesAsync(connection, checkpoint.DeskMinutes, now, ct);
        var outages = checkpoint.Outputs.SelectMany(o => o.Outages).ToList();
        if (outages.Count > 0)
            await WriteOutagesAsync(connection, outages, now, ct);
        foreach (var state in checkpoint.States)
            await SaveStateAsync(connection, state, now, ct);
        if (checkpoint.Offsets.Count > 0)
            await SaveOffsetsAsync(connection, checkpoint.ConsumerGroup, checkpoint.Offsets, now, ct);
        await transaction.CommitAsync(ct);
    }

    // A zone's device outages (ARV-036), keyed by zone, device and start: a replay of the same records writes the same rows.
    private static async Task WriteOutagesAsync(NpgsqlConnection connection, List<DeviceOutage> outages, DateTime now, CancellationToken ct)
    {
        var rows = outages.GroupBy(o => (o.ZoneKey, o.DeviceCode, o.FromUtc)).Select(g => g.Last()).ToList();
        await using var upsert = new NpgsqlCommand("""
            INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on)
            SELECT * FROM unnest(@zones, @devices, @froms, @tos, @closed, @now)
            ON CONFLICT (zone_key, device_code, from_utc) DO UPDATE SET to_utc = EXCLUDED.to_utc, closed = EXCLUDED.closed, recorded_on = EXCLUDED.recorded_on
            """, connection);
        upsert.Parameters.AddWithValue("zones", rows.Select(o => o.ZoneKey).ToArray());
        upsert.Parameters.AddWithValue("devices", rows.Select(o => o.DeviceCode).ToArray());
        upsert.Parameters.AddWithValue("froms", rows.Select(o => Utc(o.FromUtc)).ToArray());
        upsert.Parameters.AddWithValue("tos", rows.Select(o => Utc(o.ToUtc)).ToArray());
        upsert.Parameters.AddWithValue("closed", rows.Select(o => o.Closed).ToArray());
        upsert.Parameters.AddWithValue("now", rows.Select(_ => now).ToArray());
        await upsert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The longest site code (<c>site.code</c>, varchar(17)).</summary>
    private const int MaxSiteCodeLength = 17;

    // The desks' staff and service zone readings (ARV-116): counts only, keyed by desk, role and reading time, so a replay
    // after a restart offers the same rows again and the insert keeps the first (ON CONFLICT DO NOTHING: the row and the
    // id the desk feed may already have taken stay as they were). A row the table would refuse (a desk key not of the
    // zone's site or too long, a role or count out of bounds) is dropped with one warning per zone, never a key from a
    // device in the log (CWE-117); the checkpoint commits.
    private async Task WriteDeskZoneReadingsAsync(NpgsqlConnection connection, List<(string Zone, DeskZoneSample Reading)> rows, Func<string, int> version,
        DateTime now, CancellationToken ct)
    {
        var kept = new Dictionary<(string Desk, DeskSource Source, DateTime Time), (string Zone, string Site, DeskZoneSample Reading)>();
        var order = new List<(string Desk, DeskSource Source, DateTime Time)>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (zone, r) in rows)
        {
            var slash = zone?.IndexOf('/', StringComparison.Ordinal) ?? -1;
            var site = slash > 0 ? zone[..slash] : null;
            if (site is not { Length: <= MaxSiteCodeLength } || zone.Length > 220 || r is null || !DeskKeys.Fits(r.DeskKey) ||
                !r.DeskKey.StartsWith(site + "/", StringComparison.Ordinal) || r.DeskKey.Length <= site.Length + 1 ||
                r.Source is not (DeskSource.StaffZone or DeskSource.ServiceZone) || r.Count is < 0 or > DeskZoneReadings.MaxCount)
            {
                dropped[zone ?? string.Empty] = dropped.GetValueOrDefault(zone ?? string.Empty) + 1;
                continue;
            }

            var key = (r.DeskKey, r.Source, Utc(r.TimeUtc));
            if (kept.TryAdd(key, (zone, site, r)))
                order.Add(key);
        }

        foreach (var (zone, count) in dropped)
            _logger.LogWarning("Dropped {Count} desk zone readings of zone {Zone}: a desk key not of the zone's site or too long, or a role or count out of bounds",
                count, zone);
        if (kept.Count == 0)
            return;
        await Execute(connection, "CREATE TEMP TABLE stage_desk_zone_reading (LIKE desk_zone_reading INCLUDING DEFAULTS) ON COMMIT DROP", ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_desk_zone_reading (id, site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version, written_utc)
            FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var key in order)
            {
                var (zone, site, r) = kept[key];
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(Guid.CreateVersion7(now), NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(site, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(key.Desk, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(key.Source.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(key.Time, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync((short)r.Count, NpgsqlDbType.Smallint, ct);
                await copy.WriteAsync(r.Degraded, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(zone, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(version(zone), NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await Execute(connection, """
            INSERT INTO desk_zone_reading (id, site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version, written_utc)
            SELECT id, site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version, written_utc FROM stage_desk_zone_reading
            ON CONFLICT (desk_code, source, reading_utc) DO NOTHING
            """, ct);
    }

    private static async Task WriteMinutesAsync(NpgsqlConnection connection, List<(string Zone, MinuteResult Minute)> rows, Func<string, int> version, DateTime now, CancellationToken ct)
    {
        await Execute(connection, """
            CREATE TEMP TABLE stage_queue_minute (zone_key varchar(220), minute_utc timestamptz, profile_version integer, status varchar(12),
                entries bigint, exits bigint, waits bigint, mean_wait_minutes double precision, p50_wait_minutes double precision,
                p90_wait_minutes double precision, p95_wait_minutes double precision, share_within_target double precision,
                wait_buckets integer[], wait_counts integer[]) ON COMMIT DROP
            """, ct);
        await using (var copy = await connection.BeginBinaryImportAsync(
            "COPY stage_queue_minute FROM STDIN (FORMAT BINARY)", ct))
        {
            // The last result of a minute in this checkpoint wins (results come in order).
            foreach (var (zone, m) in rows.GroupBy(r => (r.Zone, r.Minute.StartUtc)).Select(g => g.Last()))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(zone, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Utc(m.StartUtc), NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(version(zone), NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(m.Status.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(m.Entries, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(m.Exits, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(m.Waits.Waits, NpgsqlDbType.Bigint, ct);
                await Nullable(copy, m.Waits.MeanMinutes, ct);
                await Nullable(copy, m.Waits.P50Minutes, ct);
                await Nullable(copy, m.Waits.P90Minutes, ct);
                await Nullable(copy, m.Waits.P95Minutes, ct);
                await Nullable(copy, m.Waits.ShareWithinTarget, ct);
                // The minute's histogram (ARV-060, F7), so hours and days merge counts instead of averaging percentiles.
                var histogram = m.Waits.Histogram ?? [];
                await copy.WriteAsync(histogram.Select(h => h.Bucket).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(histogram.Select(h => checked((int)h.Count)).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, p50_wait_minutes,
                p90_wait_minutes, p95_wait_minutes, share_within_target, wait_buckets, wait_counts, updated_on)
            SELECT zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, p50_wait_minutes,
                p90_wait_minutes, p95_wait_minutes, share_within_target, wait_buckets, wait_counts, @now FROM stage_queue_minute
            ON CONFLICT (zone_key, minute_utc) DO UPDATE SET profile_version = EXCLUDED.profile_version, status = EXCLUDED.status,
                entries = EXCLUDED.entries, exits = EXCLUDED.exits, waits = EXCLUDED.waits, mean_wait_minutes = EXCLUDED.mean_wait_minutes,
                p50_wait_minutes = EXCLUDED.p50_wait_minutes, p90_wait_minutes = EXCLUDED.p90_wait_minutes, p95_wait_minutes = EXCLUDED.p95_wait_minutes,
                share_within_target = EXCLUDED.share_within_target, wait_buckets = EXCLUDED.wait_buckets, wait_counts = EXCLUDED.wait_counts,
                updated_on = EXCLUDED.updated_on
            """, connection);
        upsert.Parameters.AddWithValue("now", now);
        await upsert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The longest line name a profile allows (Topology names, varchar(200) in line_minute).</summary>
    private const int MaxLineNameLength = 200;

    // Per-line minute counts (ARV-113). A line minute is written once it is closed and never changes in the engine, so a
    // replay of the same records after a restart writes the same values again: the upsert by (zone, line, source, minute)
    // replaces the row, a no-op in effect (idempotent). The exception is a minute released early, when a zone held more
    // open line minutes than LineCounts allows: its parts come as Additive rows and are added to the row (merged first
    // when one checkpoint holds several), so no count is lost. Outputs and the zone's snapshot commit together, so after
    // a restart each part is produced and added once; the exception is a commit that succeeds on the server but reports an
    // error and is retried by the same process, which adds an additive part twice (reachable only after an early release).
    private async Task WriteLineMinutesAsync(NpgsqlConnection connection, List<(string Zone, LineMinute Line)> rows, Func<string, int> version, DateTime now,
        CancellationToken ct)
    {
        var merged = new Dictionary<(string Zone, string Line, LineCountSource Source, DateTime Minute), (LineMinute Line, bool Additive)>();
        var order = new List<(string Zone, string Line, LineCountSource Source, DateTime Minute)>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (zone, l) in rows)
        {
            if (l.LineName is not { Length: > 0 and <= MaxLineNameLength } || l.Role == QueueLineRole.Unknown)
            {
                dropped[zone] = dropped.GetValueOrDefault(zone) + 1;
                continue;
            }

            var key = (zone, l.LineName, l.Source, Utc(l.MinuteUtc));
            if (!merged.TryGetValue(key, out var held))
            {
                merged[key] = (l, l.Additive);
                order.Add(key);
            }
            else if (!l.Additive)
                merged[key] = (l, false); // the whole minute replaces what came before it
            else
                merged[key] = (held.Line with { Role = l.Role, In = checked(held.Line.In + l.In), Out = checked(held.Line.Out + l.Out) }, held.Additive);
        }

        // Never written (the table refuses them): one warning per zone with the count; the zone key is the only value
        // logged, never a line name from a device (CWE-117).
        foreach (var (zone, count) in dropped)
            _logger.LogWarning("Dropped {Count} line minutes of zone {Zone}: empty or overlong line name, or unknown role", count, zone);
        if (merged.Count == 0)
            return;
        await Execute(connection, """
            CREATE TEMP TABLE stage_line_minute (LIKE line_minute INCLUDING DEFAULTS, additive boolean NOT NULL DEFAULT false) ON COMMIT DROP
            """, ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_line_minute (zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out, updated_on, additive)
            FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var key in order)
            {
                var (l, additive) = merged[key];
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(key.Zone, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(l.LineName, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(l.Role.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(l.Source.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(key.Minute, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(version(key.Zone), NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(l.In, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(l.Out, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(additive, NpgsqlDbType.Boolean, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await Execute(connection, """
            INSERT INTO line_minute (zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out, updated_on)
            SELECT zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out, updated_on
            FROM stage_line_minute WHERE NOT additive
            ON CONFLICT (zone_key, line_name, source, minute_utc) DO UPDATE SET line_role = EXCLUDED.line_role,
                profile_version = EXCLUDED.profile_version, crossings_in = EXCLUDED.crossings_in, crossings_out = EXCLUDED.crossings_out,
                updated_on = EXCLUDED.updated_on
            """, ct);
        await Execute(connection, """
            INSERT INTO line_minute (zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out, updated_on)
            SELECT zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out, updated_on
            FROM stage_line_minute WHERE additive
            ON CONFLICT (zone_key, line_name, source, minute_utc) DO UPDATE SET line_role = EXCLUDED.line_role,
                profile_version = EXCLUDED.profile_version, crossings_in = line_minute.crossings_in + EXCLUDED.crossings_in,
                crossings_out = line_minute.crossings_out + EXCLUDED.crossings_out, updated_on = EXCLUDED.updated_on
            """, ct);
    }

    // Each overflow band's minutes (ARV-115, script 0039), keyed by zone, band and minute. A band minute is written once
    // it is closed and never changes in the engine, so a replay after a restart writes the same values again. A part of a
    // minute released early (beyond OverflowBands.MaxOpenBandMinutes) merges with what was written: the lowest of the
    // lows and the highest of the highs, the same row whatever the order or the number of times it is written.
    private async Task WriteOverflowMinutesAsync(NpgsqlConnection connection, List<(string Zone, OverflowMinute Minute)> rows, Func<string, int> version, DateTime now,
        CancellationToken ct)
    {
        var merged = new Dictionary<(string Zone, string Band, DateTime Minute), (int Min, int Max)>();
        var order = new List<(string Zone, string Band, DateTime Minute)>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (zone, m) in rows)
        {
            if (m.BandName is not { Length: > 0 and <= MaxLineNameLength } || m.MinOccupancy < 0 || m.MaxOccupancy < m.MinOccupancy ||
                m.MaxOccupancy > CanonicalEventRules.MaxOccupancy)
            {
                dropped[zone] = dropped.GetValueOrDefault(zone) + 1;
                continue;
            }

            var key = (zone, m.BandName, Utc(m.MinuteUtc));
            if (merged.TryGetValue(key, out var held))
            {
                merged[key] = (Math.Min(held.Min, m.MinOccupancy), Math.Max(held.Max, m.MaxOccupancy));
            }
            else
            {
                merged[key] = (m.MinOccupancy, m.MaxOccupancy);
                order.Add(key);
            }
        }

        // Never written (the table refuses them): one warning per zone with the count, never a name from a device (CWE-117).
        foreach (var (zone, count) in dropped)
            _logger.LogWarning("Dropped {Count} overflow minutes of zone {Zone}: empty or overlong band name, or occupancy out of order or bounds", count, zone);
        if (merged.Count == 0)
            return;
        await Execute(connection, "CREATE TEMP TABLE stage_overflow_minute (LIKE overflow_minute INCLUDING DEFAULTS) ON COMMIT DROP", ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var key in order)
            {
                var (min, max) = merged[key];
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(key.Zone, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(key.Band, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(key.Minute, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(version(key.Zone), NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(min, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(max, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await Execute(connection, """
            INSERT INTO overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on)
            SELECT zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on FROM stage_overflow_minute
            ON CONFLICT (zone_key, band_name, minute_utc) DO UPDATE SET profile_version = EXCLUDED.profile_version,
                min_occupancy = LEAST(overflow_minute.min_occupancy, EXCLUDED.min_occupancy),
                max_occupancy = GREATEST(overflow_minute.max_occupancy, EXCLUDED.max_occupancy), updated_on = EXCLUDED.updated_on
            """, ct);
    }

    // OverflowDetected (ARV-115) goes to the transactional outbox in the checkpoint's transaction, beside the band minute
    // that decided it (ADR-0018: never published from inside a transaction; the relay produces it after the commit). The
    // event id is derived from the zone, band, minute, change and profile version, so a checkpoint written again (a replay
    // of the same records after a restart) adds no second row: ON CONFLICT on the id keeps the first.
    private async Task WriteOverflowEventsAsync(NpgsqlConnection connection, List<(string Zone, OverflowChange Change)> rows, Func<string, int> version, DateTime now,
        CancellationToken ct)
    {
        var events = new List<OverflowDetected>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (zone, change) in rows)
        {
            // The event's key is the zone key, which outbox_message.message_key must hold (OutboxLimits): a site of up to 17
            // characters and a zone name of up to 200 can exceed it, and the insert would then fail the whole checkpoint with
            // 22001, which the worker retries forever. Such a change is dropped instead; the band's minutes are still written.
            var slash = zone.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == zone.Length - 1 || !OutboxLimits.FitsMessageKey(zone) || change.BandName is not { Length: > 0 and <= MaxLineNameLength } ||
                change.PeakOccupancy is < 0 or > CanonicalEventRules.MaxOccupancy)
            {
                dropped[zone] = dropped.GetValueOrDefault(zone) + 1;
                continue;
            }

            events.Add(OverflowDetected.From(zone, zone[(slash + 1)..], version(zone), change));
        }

        foreach (var (zone, count) in dropped)
            _logger.LogWarning("Dropped {Count} overflow changes of zone {Zone}: the zone key (as an outbox key) or band name is not valid, or the peak is out of bounds",
                count, zone);
        if (events.Count == 0)
            return;
        await using var insert = new NpgsqlCommand("""
            INSERT INTO outbox_message (id, topic, message_key, message_type, payload, headers, created_on)
            SELECT e.id, @topic, e.message_key, @type, e.payload, e.headers, @now
            FROM unnest(@ids, @keys, @payloads, @headers) AS e(id, message_key, payload, headers)
            ON CONFLICT (id) DO NOTHING
            """, connection);
        insert.Parameters.AddWithValue("topic", KafkaTopics.FlowOverflowDetected);
        insert.Parameters.AddWithValue("type", nameof(OverflowDetected));
        insert.Parameters.AddWithValue("now", now);
        insert.Parameters.AddWithValue("ids", events.Select(e => e.Id).ToArray());
        insert.Parameters.AddWithValue("keys", events.Select(e => e.GetPartitionKey()).ToArray());
        insert.Parameters.Add(new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Jsonb)
        {
            Value = events.Select(e => JsonSerializer.Serialize(e, EventCatalog.Json)).ToArray()
        });
        insert.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Array | NpgsqlDbType.Jsonb)
        {
            Value = events.Select(e => JsonSerializer.Serialize(OutboxHeaders.For(e), EventCatalog.Json)).ToArray()
        });
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteLiveAsync(NpgsqlConnection connection, List<QueueLiveMinute> rows, Func<string, int> version, DateTime now, CancellationToken ct)
    {
        await Execute(connection, """
            CREATE TEMP TABLE stage_queue_live (zone_key varchar(220), minute_utc timestamptz, profile_version integer, queue_length integer,
                length_from_sensors boolean, length_degraded boolean, nowcast_minutes double precision, throughput_per_minute double precision,
                no_service varchar(20), nowcast_degraded boolean, shadow_nowcast_minutes double precision, shadow_no_service varchar(20),
                shadow_nowcast_degraded boolean, shadow_cycle_minutes double precision, PRIMARY KEY (zone_key, minute_utc)) ON COMMIT DROP
            """, ct);
        await using (var copy = await connection.BeginBinaryImportAsync("COPY stage_queue_live FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var l in rows.GroupBy(r => (r.ZoneKey, r.MinuteUtc)).Select(g => g.Last()))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(l.ZoneKey, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Utc(l.MinuteUtc), NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(version(l.ZoneKey), NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(l.QueueLength, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(l.LengthMeasured, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(l.LengthDegraded, NpgsqlDbType.Boolean, ct);
                await Nullable(copy, l.NowcastMinutes, ct);
                await Nullable(copy, l.Throughput, ct);
                if (l.NoService is { } reason)
                    await copy.WriteAsync(reason.ToString(), NpgsqlDbType.Varchar, ct);
                else
                    await copy.WriteNullAsync(ct);
                await copy.WriteAsync(l.NowcastDegraded, NpgsqlDbType.Boolean, ct);
                // ARV-117: the shadow nowcast without AMAN inputs, staged with the published row and written to its own table
                // below (ARV-117a; written only, read by the validation comparison).
                await Nullable(copy, l.Shadow?.Minutes, ct);
                if (l.Shadow?.NoService is { } shadowReason)
                    await copy.WriteAsync(shadowReason.ToString(), NpgsqlDbType.Varchar, ct);
                else
                    await copy.WriteNullAsync(ct);
                // The flag only with a number or a reason: a shadow with neither, which Nowcast.Compute never returns, is
                // written as no shadow (no row, ck_queue_minute_shadow_one) rather than refused with the whole checkpoint.
                if (l.Shadow is { } shadow && (shadow.Minutes is not null || shadow.NoService is not null))
                    await copy.WriteAsync(shadow.Degraded, NpgsqlDbType.Boolean, ct);
                else
                    await copy.WriteNullAsync(ct);
                // ARV-117b: the sensor cycle time the shadow's desk term took (script 0045), null when it fell back. A value
                // outside the column's check (which the formula's bounds never give) is written as none rather than refused
                // with the whole checkpoint.
                await Nullable(copy, l.Shadow?.CycleMinutes is { } cycle && double.IsFinite(cycle) && cycle is >= 0.05 and <= 60 ? cycle : null, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, length_from_sensors, length_degraded, nowcast_minutes,
                throughput_per_minute, no_service, nowcast_degraded, updated_on)
            SELECT zone_key, minute_utc, profile_version, queue_length, length_from_sensors, length_degraded, nowcast_minutes,
                throughput_per_minute, no_service, nowcast_degraded, @now FROM stage_queue_live
            ON CONFLICT (zone_key, minute_utc) DO UPDATE SET queue_length = EXCLUDED.queue_length, length_from_sensors = EXCLUDED.length_from_sensors,
                length_degraded = EXCLUDED.length_degraded, nowcast_minutes = EXCLUDED.nowcast_minutes, throughput_per_minute = EXCLUDED.throughput_per_minute,
                no_service = EXCLUDED.no_service, nowcast_degraded = EXCLUDED.nowcast_degraded, updated_on = EXCLUDED.updated_on
            """, connection))
        {
            upsert.Parameters.AddWithValue("now", now);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        // ARV-117a: the shadow nowcast in its own table (script 0043), in the same transaction as the published row. The
        // runtime role may write it but read only its key columns, so the upsert never reads a value column: a reference
        // to EXCLUDED.<column> needs SELECT on that column of the table, so a conflicting row takes its new values from
        // the staging table by key (a correlated subquery over the key columns, indexed by the stage's primary key).
        await using var shadowUpsert = new NpgsqlCommand("""
            INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, no_service, nowcast_degraded, sensor_cycle_minutes, updated_on)
            SELECT zone_key, minute_utc, shadow_nowcast_minutes, shadow_no_service, shadow_nowcast_degraded, shadow_cycle_minutes, @now FROM stage_queue_live
             WHERE shadow_nowcast_degraded IS NOT NULL
            ON CONFLICT (zone_key, minute_utc) DO UPDATE SET (nowcast_minutes, no_service, nowcast_degraded, sensor_cycle_minutes, updated_on) =
                (SELECT s.shadow_nowcast_minutes, s.shadow_no_service, s.shadow_nowcast_degraded, s.shadow_cycle_minutes, @now FROM stage_queue_live s
                  WHERE s.zone_key = queue_minute_shadow.zone_key AND s.minute_utc = queue_minute_shadow.minute_utc)
            """, connection);
        shadowUpsert.Parameters.AddWithValue("now", now);
        await shadowUpsert.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteBinsAsync(NpgsqlConnection connection, List<(string Zone, BinResult Bin)> rows, DateTime now, CancellationToken ct)
    {
        await Execute(connection, """
            CREATE TEMP TABLE stage_queue_bin (LIKE queue_bin INCLUDING DEFAULTS) ON COMMIT DROP
            """, ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, mean_wait_minutes,
                p50_wait_minutes, p90_wait_minutes, p95_wait_minutes, share_within_target, abandoned, fragmented, censored, reanchored, rejected,
                open_people, late_events, profile_version, revision_reason, updated_on) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var (zone, b) in rows.GroupBy(r => (r.Zone, r.Bin.StartUtc, r.Bin.Revision)).Select(g => g.Last()))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(zone, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Utc(b.StartUtc), NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(b.Revision, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync((int)b.Length.TotalMinutes, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(b.Status.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(b.Quality.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(b.Entries, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(b.Exits, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(b.Waits.Waits, NpgsqlDbType.Bigint, ct);
                await Nullable(copy, b.Waits.MeanMinutes, ct);
                await Nullable(copy, b.Waits.P50Minutes, ct);
                await Nullable(copy, b.Waits.P90Minutes, ct);
                await Nullable(copy, b.Waits.P95Minutes, ct);
                await Nullable(copy, b.Waits.ShareWithinTarget, ct);
                foreach (var count in new[] { b.Abandoned, b.Fragmented, b.Censored, b.Reanchored, b.Rejected, b.Open, b.LateEvents })
                    await copy.WriteAsync(count, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(b.ZoneProfileVersion, NpgsqlDbType.Integer, ct);
                var reason = b.RevisionReason is { Length: > 200 } text ? text[..200] : b.RevisionReason;
                if (reason is null)
                    await copy.WriteNullAsync(ct);
                else
                    await copy.WriteAsync(reason, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        // A Final revision is written once and never rewritten (a replay gives the same values; a recomputation adds a revision).
        await Execute(connection, """
            INSERT INTO queue_bin SELECT * FROM stage_queue_bin
            ON CONFLICT (zone_key, start_utc, revision) DO UPDATE SET length_minutes = EXCLUDED.length_minutes, status = EXCLUDED.status,
                quality = EXCLUDED.quality, entries = EXCLUDED.entries, exits = EXCLUDED.exits, waits = EXCLUDED.waits,
                mean_wait_minutes = EXCLUDED.mean_wait_minutes, p50_wait_minutes = EXCLUDED.p50_wait_minutes, p90_wait_minutes = EXCLUDED.p90_wait_minutes,
                p95_wait_minutes = EXCLUDED.p95_wait_minutes, share_within_target = EXCLUDED.share_within_target, abandoned = EXCLUDED.abandoned,
                fragmented = EXCLUDED.fragmented, censored = EXCLUDED.censored, reanchored = EXCLUDED.reanchored, rejected = EXCLUDED.rejected,
                open_people = EXCLUDED.open_people, late_events = EXCLUDED.late_events, profile_version = EXCLUDED.profile_version,
                revision_reason = EXCLUDED.revision_reason, updated_on = EXCLUDED.updated_on
            WHERE queue_bin.status <> 'Final'
            """, ct);
    }

    // The health checks of each bin (ARV-114a, F18), one row per bin and revision as for queue_bin: the last result of a
    // bin in this checkpoint wins, and a Final row is written once and never rewritten (the trigger of 0038 refuses it).
    private static async Task WriteHealthAsync(NpgsqlConnection connection, List<(string Zone, ZoneHealthBin Health)> rows, DateTime now, CancellationToken ct)
    {
        await Execute(connection, "CREATE TEMP TABLE stage_zone_health_bin (LIKE zone_health_bin INCLUDING DEFAULTS) ON COMMIT DROP", ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start,
                occupancy_end, conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored,
                tracks_rejected, tracks_open, track_completion_rate, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on)
            FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var (zone, h) in rows.GroupBy(r => (r.Zone, Utc(r.Health.StartUtc), r.Health.Revision)).Select(g => g.Last()))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(zone, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Utc(h.StartUtc), NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(h.Revision, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync((int)h.Length.TotalMinutes, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(h.Status.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(h.ZoneProfileVersion, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(h.Entries, NpgsqlDbType.Bigint, ct);
                await copy.WriteAsync(h.Exits, NpgsqlDbType.Bigint, ct);
                await NullableInt(copy, h.OccupancyStart, ct);
                await NullableInt(copy, h.OccupancyEnd, ct);
                if (h.ConservationResidual is { } residual)
                    await copy.WriteAsync(residual, NpgsqlDbType.Bigint, ct);
                else
                    await copy.WriteNullAsync(ct);
                foreach (var count in new[] { h.TracksEntered, h.TracksExited, h.TracksAbandoned, h.TracksFragmented, h.TracksCensored, h.TracksRejected, h.TracksOpen })
                    await copy.WriteAsync(count, NpgsqlDbType.Bigint, ct);
                await Nullable(copy, h.TrackCompletionRate, ct);
                await copy.WriteAsync(h.OccupancyMinutes, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(h.CapacityMinutes, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(h.MinutesOutsideCapacity, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await Execute(connection, """
            INSERT INTO zone_health_bin SELECT * FROM stage_zone_health_bin
            ON CONFLICT (zone_key, start_utc, revision) DO UPDATE SET length_minutes = EXCLUDED.length_minutes, status = EXCLUDED.status,
                profile_version = EXCLUDED.profile_version, entries = EXCLUDED.entries, exits = EXCLUDED.exits,
                occupancy_start = EXCLUDED.occupancy_start, occupancy_end = EXCLUDED.occupancy_end, conservation_residual = EXCLUDED.conservation_residual,
                tracks_entered = EXCLUDED.tracks_entered, tracks_exited = EXCLUDED.tracks_exited, tracks_abandoned = EXCLUDED.tracks_abandoned,
                tracks_fragmented = EXCLUDED.tracks_fragmented, tracks_censored = EXCLUDED.tracks_censored, tracks_rejected = EXCLUDED.tracks_rejected,
                tracks_open = EXCLUDED.tracks_open, track_completion_rate = EXCLUDED.track_completion_rate,
                occupancy_minutes = EXCLUDED.occupancy_minutes, capacity_minutes = EXCLUDED.capacity_minutes,
                minutes_outside_capacity = EXCLUDED.minutes_outside_capacity, updated_on = EXCLUDED.updated_on
            WHERE zone_health_bin.status <> 'Final'
            """, ct);
    }

    internal static async Task WriteDeskMinutesAsync(NpgsqlConnection connection, IReadOnlyList<DeskMinute> rows, DateTime now, CancellationToken ct)
    {
        await Execute(connection, "CREATE TEMP TABLE stage_desk_minute (LIKE desk_minute INCLUDING DEFAULTS) ON COMMIT DROP", ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_desk_minute (desk_code, lane, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds,
                transactions, sensor_derived_seconds, present_seconds, degraded, updated_on) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var d in rows.GroupBy(r => (r.DeskCode, r.MinuteUtc)).Select(g => g.Last()))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(d.DeskCode, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(d.Lane, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Utc(d.MinuteUtc), NpgsqlDbType.TimestampTz, ct);
                foreach (var span in new[] { d.Closed, d.Idle, d.Serving, d.Paused, d.Unknown })
                    await copy.WriteAsync(Math.Min(60, span.TotalSeconds), NpgsqlDbType.Double, ct);
                await copy.WriteAsync(d.Transactions, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(Math.Min(60, d.SensorDerived.TotalSeconds), NpgsqlDbType.Double, ct);
                await copy.WriteAsync(Math.Min(60, d.PresentNotProcessing.TotalSeconds), NpgsqlDbType.Double, ct);
                await copy.WriteAsync(d.Degraded, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await Execute(connection, """
            INSERT INTO desk_minute SELECT * FROM stage_desk_minute
            ON CONFLICT (desk_code, minute_utc) DO UPDATE SET lane = EXCLUDED.lane, closed_seconds = EXCLUDED.closed_seconds,
                idle_seconds = EXCLUDED.idle_seconds, serving_seconds = EXCLUDED.serving_seconds, paused_seconds = EXCLUDED.paused_seconds,
                unknown_seconds = EXCLUDED.unknown_seconds, transactions = EXCLUDED.transactions, sensor_derived_seconds = EXCLUDED.sensor_derived_seconds,
                present_seconds = EXCLUDED.present_seconds, degraded = EXCLUDED.degraded, updated_on = EXCLUDED.updated_on
            """, ct);
    }

    /// <summary>
    /// The sensor-only desk engine's closed minutes (ARV-117a, script 0044), written by the desk feed in the same
    /// transaction as the published desk minutes: binary COPY into a staging table, then an upsert by desk and minute, so a
    /// replayed minute rewrites the same row. Seconds in each state from the zones alone and the F11 flag; no transactions.
    /// </summary>
    internal static async Task WriteDeskSensorMinutesAsync(NpgsqlConnection connection, IReadOnlyList<DeskMinute> rows, DateTime now, CancellationToken ct)
    {
        await Execute(connection, """
            CREATE TEMP TABLE stage_desk_sensor_minute (desk_code varchar(64), minute_utc timestamptz, closed_seconds double precision,
                idle_seconds double precision, serving_seconds double precision, paused_seconds double precision, unknown_seconds double precision,
                degraded boolean, updated_on timestamptz) ON COMMIT DROP
            """, ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_desk_sensor_minute (desk_code, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, degraded, updated_on)
            FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var d in rows.GroupBy(r => (r.DeskCode, r.MinuteUtc)).Select(g => g.Last()))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(d.DeskCode, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Utc(d.MinuteUtc), NpgsqlDbType.TimestampTz, ct);
                foreach (var span in new[] { d.Closed, d.Idle, d.Serving, d.Paused, d.Unknown })
                    await copy.WriteAsync(Math.Clamp(span.TotalSeconds, 0, 60), NpgsqlDbType.Double, ct);
                await copy.WriteAsync(d.Degraded, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await Execute(connection, """
            INSERT INTO desk_sensor_minute (desk_code, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, degraded, updated_on)
            SELECT desk_code, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, degraded, updated_on FROM stage_desk_sensor_minute
            ON CONFLICT (desk_code, minute_utc) DO UPDATE SET closed_seconds = EXCLUDED.closed_seconds, idle_seconds = EXCLUDED.idle_seconds,
                serving_seconds = EXCLUDED.serving_seconds, paused_seconds = EXCLUDED.paused_seconds, unknown_seconds = EXCLUDED.unknown_seconds,
                degraded = EXCLUDED.degraded, updated_on = EXCLUDED.updated_on
            """, ct);
    }

    private static async Task SaveStateAsync(NpgsqlConnection connection, ZoneProcessorState state, DateTime now, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state, EventCatalog.Json);
        if (json.Length > MaxStateBytes)
            throw new InvalidOperationException($"The state of zone {state.ZoneKey} is {json.Length} bytes, above {MaxStateBytes}.");
        await using var command = new NpgsqlCommand("""
            INSERT INTO stream_zone_state (zone_key, profile_version, state, state_bytes, reference_utc, updated_on)
            VALUES (@zone, @version, @state, @bytes, @reference, @now)
            ON CONFLICT (zone_key) DO UPDATE SET profile_version = EXCLUDED.profile_version, state = EXCLUDED.state, state_bytes = EXCLUDED.state_bytes,
                reference_utc = EXCLUDED.reference_utc, updated_on = EXCLUDED.updated_on
            """, connection);
        command.Parameters.AddWithValue("zone", state.ZoneKey);
        command.Parameters.AddWithValue("version", state.ProfileVersion);
        command.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Jsonb) { Value = System.Text.Encoding.UTF8.GetString(json) });
        command.Parameters.AddWithValue("bytes", json.Length);
        command.Parameters.AddWithValue("reference", Utc(state.ReferenceUtc));
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SaveOffsetsAsync(NpgsqlConnection connection, string group, IReadOnlyList<StreamOffset> offsets, DateTime now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO stream_offset (consumer_group, topic, partition_no, next_offset, updated_on)
            SELECT @group, o.topic, o.partition_no, o.next_offset, @now FROM unnest(@topics, @partitions, @offsets) AS o(topic, partition_no, next_offset)
            ON CONFLICT (consumer_group, topic, partition_no) DO UPDATE SET next_offset = EXCLUDED.next_offset, updated_on = EXCLUDED.updated_on
            """, connection);
        command.Parameters.AddWithValue("group", group);
        command.Parameters.AddWithValue("topics", offsets.Select(o => o.Topic).ToArray());
        command.Parameters.AddWithValue("partitions", offsets.Select(o => o.Partition).ToArray());
        command.Parameters.AddWithValue("offsets", offsets.Select(o => o.NextOffset).ToArray());
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Records a partition count of a topic set (kept for good: records produced under it stay in the topics).</summary>
    public async Task SavePartitionCountAsync(string topicSet, int partitions, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO stream_partition_count (topic_set, partitions, first_seen) VALUES (@set, @partitions, @now) ON CONFLICT DO NOTHING", connection);
        command.Parameters.AddWithValue("set", topicSet);
        command.Parameters.AddWithValue("partitions", partitions);
        command.Parameters.AddWithValue("now", timeProvider.GetUtcNow().UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every partition count recorded for a topic set.</summary>
    public async Task<IReadOnlyList<int>> LoadPartitionCountsAsync(string topicSet, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT partitions FROM stream_partition_count WHERE topic_set = @set", connection);
        command.Parameters.AddWithValue("set", topicSet);
        var counts = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts.Add(reader.GetInt32(0));
        return counts;
    }

    /// <summary>The saved next offsets of <paramref name="group"/> for these partitions (missing ones are not in the result).</summary>
    public async Task<IReadOnlyList<StreamOffset>> LoadOffsetsAsync(string group, IReadOnlyList<(string Topic, int Partition)> partitions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        if (partitions.Count == 0)
            return [];
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT s.topic, s.partition_no, s.next_offset FROM stream_offset s
            JOIN unnest(@topics, @partitions) AS p(topic, partition_no) ON p.topic = s.topic AND p.partition_no = s.partition_no
            WHERE s.consumer_group = @group
            """, connection);
        command.Parameters.AddWithValue("group", group);
        command.Parameters.AddWithValue("topics", partitions.Select(p => p.Topic).ToArray());
        command.Parameters.AddWithValue("partitions", partitions.Select(p => p.Partition).ToArray());
        var result = new List<StreamOffset>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new StreamOffset(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        return result;
    }

    /// <summary>A zone's saved state, or null; a state above <see cref="MaxStateBytes"/> or unreadable throws.</summary>
    public async Task<ZoneProcessorState> LoadStateAsync(string zoneKey, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT state_bytes, state::text FROM stream_zone_state WHERE zone_key = @zone", connection);
        command.Parameters.AddWithValue("zone", zoneKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        if (reader.GetInt32(0) > MaxStateBytes)
            throw new InvalidDataException($"The saved state of zone {zoneKey} is above {MaxStateBytes} bytes.");
        var text = reader.GetString(1);
        if (text.Length > MaxStateBytes)
            throw new InvalidDataException($"The saved state of zone {zoneKey} is above {MaxStateBytes} bytes.");
        return JsonSerializer.Deserialize<ZoneProcessorState>(text, EventCatalog.Json);
    }

    private static async Task Execute(NpgsqlConnection connection, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(ct);
    }

    private static Task NullableInt(NpgsqlBinaryImporter copy, int? value, CancellationToken ct) =>
        value is { } v ? copy.WriteAsync(v, NpgsqlDbType.Integer, ct) : copy.WriteNullAsync(ct);

    private static Task Nullable(NpgsqlBinaryImporter copy, double? value, CancellationToken ct) =>
        value is { } v && double.IsFinite(v) ? copy.WriteAsync(v, NpgsqlDbType.Double, ct) : copy.WriteNullAsync(ct);

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
