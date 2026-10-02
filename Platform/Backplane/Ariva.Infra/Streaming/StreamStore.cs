using System.Text.Json;
using Ariva.Core.Desks;
using Ariva.Core.Queueing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Settings;
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
/// Persistence of the stream host (ARV-034, script 0018). A checkpoint is one transaction: rows are written by binary COPY
/// into staging tables and upserted by key (zone or desk and minute, bin and revision), the zones' snapshots replace
/// their previous ones, and the consumer group's next offsets are recorded. Only after it commits does the consumer
/// commit its Kafka offsets; on assignment it starts from the offsets saved here, so a record is applied to a saved state
/// exactly once and replaying the records after it rewrites the same rows.
/// </summary>
public sealed class StreamStore(DatabaseSettings database, TimeProvider timeProvider)
{
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
        if (checkpoint.DeskMinutes.Count > 0)
            await WriteDeskMinutesAsync(connection, checkpoint.DeskMinutes, now, ct);
        foreach (var state in checkpoint.States)
            await SaveStateAsync(connection, state, now, ct);
        if (checkpoint.Offsets.Count > 0)
            await SaveOffsetsAsync(connection, checkpoint.ConsumerGroup, checkpoint.Offsets, now, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task WriteMinutesAsync(NpgsqlConnection connection, List<(string Zone, MinuteResult Minute)> rows, Func<string, int> version, DateTime now, CancellationToken ct)
    {
        await Execute(connection, """
            CREATE TEMP TABLE stage_queue_minute (zone_key varchar(220), minute_utc timestamptz, profile_version integer, status varchar(12),
                entries bigint, exits bigint, waits bigint, mean_wait_minutes double precision, p50_wait_minutes double precision,
                p90_wait_minutes double precision, p95_wait_minutes double precision, share_within_target double precision) ON COMMIT DROP
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
            }

            await copy.CompleteAsync(ct);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, p50_wait_minutes,
                p90_wait_minutes, p95_wait_minutes, share_within_target, updated_on)
            SELECT zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, p50_wait_minutes,
                p90_wait_minutes, p95_wait_minutes, share_within_target, @now FROM stage_queue_minute
            ON CONFLICT (zone_key, minute_utc) DO UPDATE SET profile_version = EXCLUDED.profile_version, status = EXCLUDED.status,
                entries = EXCLUDED.entries, exits = EXCLUDED.exits, waits = EXCLUDED.waits, mean_wait_minutes = EXCLUDED.mean_wait_minutes,
                p50_wait_minutes = EXCLUDED.p50_wait_minutes, p90_wait_minutes = EXCLUDED.p90_wait_minutes, p95_wait_minutes = EXCLUDED.p95_wait_minutes,
                share_within_target = EXCLUDED.share_within_target, updated_on = EXCLUDED.updated_on
            """, connection);
        upsert.Parameters.AddWithValue("now", now);
        await upsert.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteLiveAsync(NpgsqlConnection connection, List<QueueLiveMinute> rows, Func<string, int> version, DateTime now, CancellationToken ct)
    {
        await Execute(connection, """
            CREATE TEMP TABLE stage_queue_live (zone_key varchar(220), minute_utc timestamptz, profile_version integer, queue_length integer,
                length_from_sensors boolean, length_degraded boolean, nowcast_minutes double precision, throughput_per_minute double precision,
                no_service varchar(20), nowcast_degraded boolean) ON COMMIT DROP
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
            }

            await copy.CompleteAsync(ct);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, queue_length, length_from_sensors, length_degraded, nowcast_minutes,
                throughput_per_minute, no_service, nowcast_degraded, updated_on)
            SELECT zone_key, minute_utc, profile_version, queue_length, length_from_sensors, length_degraded, nowcast_minutes,
                throughput_per_minute, no_service, nowcast_degraded, @now FROM stage_queue_live
            ON CONFLICT (zone_key, minute_utc) DO UPDATE SET queue_length = EXCLUDED.queue_length, length_from_sensors = EXCLUDED.length_from_sensors,
                length_degraded = EXCLUDED.length_degraded, nowcast_minutes = EXCLUDED.nowcast_minutes, throughput_per_minute = EXCLUDED.throughput_per_minute,
                no_service = EXCLUDED.no_service, nowcast_degraded = EXCLUDED.nowcast_degraded, updated_on = EXCLUDED.updated_on
            """, connection);
        upsert.Parameters.AddWithValue("now", now);
        await upsert.ExecuteNonQueryAsync(ct);
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

    private static async Task WriteDeskMinutesAsync(NpgsqlConnection connection, IReadOnlyList<DeskMinute> rows, DateTime now, CancellationToken ct)
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

    private static Task Nullable(NpgsqlBinaryImporter copy, double? value, CancellationToken ct) =>
        value is { } v && double.IsFinite(v) ? copy.WriteAsync(v, NpgsqlDbType.Double, ct) : copy.WriteNullAsync(ct);

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
