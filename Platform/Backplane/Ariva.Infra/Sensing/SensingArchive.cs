using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;
using Ariva.Infra.Settings;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Sensing;

/// <summary>
/// The raw sensing event archive on PostgreSQL and TimescaleDB (ARV-026, script 0017). A write takes any number of
/// sensing batches in one transaction:
/// <list type="bullet">
/// <item>each batch and event is checked again (they crossed Kafka): a batch without a site, zone or device, or
/// received outside a sane window, is skipped whole; an event out of the canonical bounds, with unknown flags or a time
/// far from its receipt is skipped alone; both are counted and logged, never failing the write;</item>
/// <item>batch ids are locked (advisory lock class 26, in key order) and recorded in <c>sensing_batch</c> unless
/// archived within three days (the topics keep three days, so nothing older can be redelivered): a batch seen before is
/// skipped whole;</item>
/// <item>track ids become pseudonyms of their UTC day (HMAC-SHA256 with the day's random key), so a track cannot be
/// linked across days (D4: track ids never persist past the operating day); keys older than three days are destroyed;</item>
/// <item>events are written by binary COPY, at most <see cref="CopyChunk"/> per COPY.</item>
/// </list>
/// Reads stream one zone of one site over at most 31 days in time order, one day per statement. Every value is a
/// parameter or a COPY field; no SQL is built from input (CWE-89).
/// </summary>
public sealed class SensingArchive(DatabaseSettings database, TimeProvider timeProvider, ILogger<SensingArchive> logger = null) : ISensingArchive
{
    /// <summary>Events per COPY at most; larger writes are split into several COPYs inside the same transaction.</summary>
    public const int CopyChunk = 20_000;

    /// <summary>How long a batch id is remembered, and a day's pseudonym key kept: the topics' retention and the ingest's maximum age.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(3);

    private readonly ILogger<SensingArchive> _logger = logger ?? NullLogger<SensingArchive>.Instance;
    private readonly ConcurrentDictionary<DateOnly, byte[]> _keys = new();

    public async Task<SensingArchiveWrite> WriteAsync(IReadOnlyList<SensingBatch> batches, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var skippedBatches = 0;
        var skippedEvents = 0;
        var rows = new List<(SensingBatch Batch, SensingKind Kind, List<Sensed<CanonicalEvent>> Events)>();
        foreach (var batch in batches.Where(b => b is not null).GroupBy(b => b.Id).Select(g => g.First()))
        {
            if (!Usable(batch, now))
            {
                skippedBatches++;
                continue;
            }

            var events = EventsOf(batch);
            if (events.Count > IngestSettings.MaxEventsLimit)
            {
                skippedBatches++;
                continue;
            }

            var kept = events.Where(e => Usable(batch, e, now)).ToList();
            skippedEvents += events.Count - kept.Count;
            if (kept.Count > 0)
                rows.Add((batch, KindOf(batch), kept));
        }

        if (skippedBatches + skippedEvents > 0)
            _logger.LogWarning("Sensing archive skipped {Batches} batches and {Events} events out of bounds", skippedBatches, skippedEvents);
        if (rows.Count == 0)
            return new SensingArchiveWrite(0, 0, 0, skippedEvents);

        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Two writers of the same batch (a rebalance) wait for each other instead of both passing the check; locks are
        // taken in key order, so two multi-batch writers cannot wait on each other in a cycle.
        await using (var locks = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(26, k) FROM (SELECT DISTINCT hashtext(id::text) AS k FROM unnest(@ids) AS id) keys ORDER BY k
            """, connection, transaction))
        {
            locks.Parameters.AddWithValue("ids", rows.Select(r => r.Batch.Id).ToArray());
            await locks.ExecuteNonQueryAsync(ct);
        }

        var archived = new HashSet<Guid>();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO sensing_batch (id, received_on, site_code, queue_zone_name, kind, device_id, events, archived_on)
            SELECT b.id, b.received_on, b.site_code, b.queue_zone_name, b.kind, b.device_id, b.events, @now
            FROM unnest(@ids, @received, @sites, @zones, @kinds, @devices, @events) AS b(id, received_on, site_code, queue_zone_name, kind, device_id, events)
            WHERE NOT EXISTS (SELECT 1 FROM sensing_batch s WHERE s.id = b.id AND s.received_on >= b.received_on - @window AND s.received_on <= b.received_on + @window)
            RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("ids", rows.Select(r => r.Batch.Id).ToArray());
            insert.Parameters.AddWithValue("received", rows.Select(r => Utc(r.Batch.ReceivedUtc)).ToArray());
            insert.Parameters.AddWithValue("sites", rows.Select(r => r.Batch.SiteCode).ToArray());
            insert.Parameters.AddWithValue("zones", rows.Select(r => r.Batch.QueueZoneName).ToArray());
            insert.Parameters.AddWithValue("kinds", rows.Select(r => r.Kind.ToString()).ToArray());
            insert.Parameters.AddWithValue("devices", rows.Select(r => r.Batch.DeviceId).ToArray());
            insert.Parameters.AddWithValue("events", rows.Select(r => r.Events.Count).ToArray());
            insert.Parameters.AddWithValue("window", Window);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                archived.Add(reader.GetGuid(0));
        }

        var accepted = rows.Where(r => archived.Contains(r.Batch.Id)).ToList();
        var keys = await DayKeysAsync(connection, transaction,
            [.. accepted.SelectMany(r => r.Events).Where(e => e.Event is TrackPosition or LineCrossing { TrackId: not null }).Select(e => DateOnly.FromDateTime(Utc(e.TimeUtc))).Distinct()],
            now, ct);

        var written = 0;
        var pending = accepted.SelectMany(r => r.Events.Select((e, i) => (r.Batch, r.Kind, Event: e, Ordinal: i)));
        foreach (var chunk in Enumerable.Chunk(pending, CopyChunk))
        {
            await using var copy = await connection.BeginBinaryImportAsync("""
                COPY sensing_event (time_utc, site_code, queue_zone_name, kind, batch_id, ordinal, device_id, device_code, received_utc, flags, commissioned,
                                    track_id, x, y, height_metres, name, direction, count_value, in_count, out_count, from_utc)
                FROM STDIN (FORMAT BINARY)
                """, ct);
            foreach (var (batch, kind, sensed, ordinal) in chunk)
            {
                var time = Utc(sensed.TimeUtc);
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(time, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(batch.SiteCode, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(batch.QueueZoneName, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(kind.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(batch.Id, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(ordinal, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(batch.DeviceId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(batch.DeviceCode, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(Utc(batch.ReceivedUtc), NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync((short)sensed.Flags, NpgsqlDbType.Smallint, ct);
                await copy.WriteAsync(batch.Commissioned, NpgsqlDbType.Boolean, ct);
                switch (sensed.Event)
                {
                    case TrackPosition t:
                        await WriteFieldsAsync(copy, Pseudonym(keys, batch.DeviceCode, t.TrackId, time), t.X, t.Y, t.HeightMetres, null, null, null, null, null, null, ct);
                        break;
                    case LineCrossing c:
                        await WriteFieldsAsync(copy, c.TrackId is null ? null : Pseudonym(keys, batch.DeviceCode, c.TrackId, time), null, null, null, c.LineName,
                            c.Direction.ToString(), null, null, null, null, ct);
                        break;
                    case ZoneOccupancy o:
                        await WriteFieldsAsync(copy, null, null, null, null, o.ZoneName, null, o.Count, null, null, null, ct);
                        break;
                    case IntervalCount i:
                        await WriteFieldsAsync(copy, null, null, null, null, i.LineName, null, null, i.In, i.Out, Utc(i.FromUtc), ct);
                        break;
                    default:
                        throw new InvalidOperationException($"{sensed.Event.GetType().Name} is not archived.");
                }

                written++;
            }

            await copy.CompleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
        // Only keys the database has now are kept for later writes: a key from a rolled-back write would split a day's pseudonyms.
        foreach (var (day, key) in keys)
            _keys.TryAdd(day, key);
        return new SensingArchiveWrite(archived.Count, rows.Count - archived.Count, written, skippedEvents);
    }

    /// <summary>
    /// The pseudonym key of each day (created at random on first use, shared through <c>sensing_day_key</c> by every
    /// writer), after destroying the keys of days no event can still arrive for.
    /// </summary>
    private async Task<Dictionary<DateOnly, byte[]>> DayKeysAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<DateOnly> days,
        DateTime now, CancellationToken ct)
    {
        var oldest = DateOnly.FromDateTime(now - Window);
        foreach (var stale in _keys.Keys.Where(d => d < oldest).ToList())
            _keys.TryRemove(stale, out _);
        await using (var purge = new NpgsqlCommand("DELETE FROM sensing_day_key WHERE day < @oldest", connection, transaction))
        {
            purge.Parameters.AddWithValue("oldest", oldest);
            await purge.ExecuteNonQueryAsync(ct);
        }

        var missing = days.Where(d => d >= oldest && !_keys.ContainsKey(d)).ToArray();
        if (missing.Length > 0)
        {
            await using (var create = new NpgsqlCommand("""
                INSERT INTO sensing_day_key (day, key, created_on) SELECT k.day, k.key, @now FROM unnest(@days, @keys) AS k(day, key)
                ON CONFLICT (day) DO NOTHING
                """, connection, transaction))
            {
                create.Parameters.AddWithValue("now", now);
                create.Parameters.AddWithValue("days", missing);
                create.Parameters.AddWithValue("keys", missing.Select(_ => RandomNumberGenerator.GetBytes(32)).ToArray());
                await create.ExecuteNonQueryAsync(ct);
            }

        }

        var keys = new Dictionary<DateOnly, byte[]>();
        foreach (var day in days.Where(d => d >= oldest))
        {
            if (_keys.TryGetValue(day, out var cached))
                keys[day] = cached;
        }

        if (missing.Length > 0)
        {
            await using var read = new NpgsqlCommand("SELECT day, key FROM sensing_day_key WHERE day = ANY(@days)", connection, transaction);
            read.Parameters.AddWithValue("days", missing);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                keys[reader.GetFieldValue<DateOnly>(0)] = reader.GetFieldValue<byte[]>(1);
        }

        return keys;
    }

    /// <summary>The device code and a 22-character pseudonym of the track id for its UTC day; the code alone without the day's key.</summary>
    private static string Pseudonym(Dictionary<DateOnly, byte[]> keys, string deviceCode, string trackId, DateTime time)
    {
        if (!keys.TryGetValue(DateOnly.FromDateTime(time), out var key))
            return deviceCode + "/~";
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(trackId));
        return deviceCode + "/~" + Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static readonly TimeSpan MaxLate = TimeSpan.FromDays(3) + TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxAhead = TimeSpan.FromHours(1);
    private const SensedFlags KnownFlags = SensedFlags.Skewed | SensedFlags.Corrected;

    /// <summary>A batch the archive can file: site, zone and device as Ingest writes them, received within the window.</summary>
    private static bool Usable(SensingBatch batch, DateTime now)
    {
        if (batch.Id == Guid.Empty || batch.DeviceId == Guid.Empty || batch is not (TrackSampleBatch or VendorLineCrossingBatch or ZoneOccupancyBatch or IntervalCountBatch))
            return false;
        if (batch.SiteCode is not { Length: >= 2 and <= 17 } || batch.QueueZoneName is not { Length: >= 1 and <= 200 } || batch.DeviceCode is not { Length: >= 1 and <= 16 })
            return false;
        if (!Ariva.Core.Domain.Components.DisplayText.IsClean(batch.QueueZoneName) || !Site.IsValidCode(batch.SiteCode) || !DeviceCode(batch.DeviceCode))
            return false;
        var received = Utc(batch.ReceivedUtc);
        return received > now - MaxLate && received < now + MaxAhead;
    }

    /// <summary>An event within the canonical bounds, with known flags and a time near its receipt (Ingest's own rules, again).</summary>
    private static bool Usable(SensingBatch batch, Sensed<CanonicalEvent> sensed, DateTime now)
    {
        if (sensed?.Event is null || (sensed.Flags & ~KnownFlags) != 0)
            return false;
        var time = Utc(sensed.TimeUtc);
        var received = Utc(batch.ReceivedUtc);
        // Older than the key window, a track id could not be pseudonymised; the topics would have dropped it anyway.
        if (time > received + TimeSpan.FromMinutes(5) || time < received - TimeSpan.FromDays(3) || DateOnly.FromDateTime(time) < DateOnly.FromDateTime(now - Window))
            return false;
        // Track ids arrive namespaced by device (<code>/<id>); the canonical rules check the device's own part.
        var canonical = sensed.Event switch
        {
            TrackPosition t => t with { TrackId = Local(batch, t.TrackId) },
            LineCrossing { TrackId: not null } c => Local(batch, c.TrackId) is { } local ? c with { TrackId = local } : null,
            _ => sensed.Event
        };
        return canonical is not null && CanonicalEventRules.Validate(canonical).Count == 0;
    }

    /// <summary>A device code as the registry writes it: upper case letters and digits in hyphen-separated groups.</summary>
    private static bool DeviceCode(string code) =>
        code.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-') && code[0] != '-' && code[^1] != '-' && !code.Contains("--", StringComparison.Ordinal);

    private static string Local(SensingBatch batch, string trackId) =>
        trackId is not null && trackId.StartsWith(batch.DeviceCode + "/", StringComparison.Ordinal) ? trackId[(batch.DeviceCode.Length + 1)..] : null;

    private static async Task WriteFieldsAsync(NpgsqlBinaryImporter copy, string trackId, double? x, double? y, double? height, string name, string direction,
        int? count, int? inCount, int? outCount, DateTime? from, CancellationToken ct)
    {
        await WriteAsync(copy, trackId, NpgsqlDbType.Varchar, ct);
        await WriteAsync(copy, x, NpgsqlDbType.Double, ct);
        await WriteAsync(copy, y, NpgsqlDbType.Double, ct);
        await WriteAsync(copy, height, NpgsqlDbType.Double, ct);
        await WriteAsync(copy, name, NpgsqlDbType.Varchar, ct);
        await WriteAsync(copy, direction, NpgsqlDbType.Varchar, ct);
        await WriteAsync(copy, count, NpgsqlDbType.Integer, ct);
        await WriteAsync(copy, inCount, NpgsqlDbType.Integer, ct);
        await WriteAsync(copy, outCount, NpgsqlDbType.Integer, ct);
        await WriteAsync(copy, from, NpgsqlDbType.TimestampTz, ct);
    }

    private static Task WriteAsync<T>(NpgsqlBinaryImporter copy, T value, NpgsqlDbType type, CancellationToken ct) =>
        value is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(value, type, ct);

    public async IAsyncEnumerable<ArchivedSensingEvent> ReadAsync(SensingReplayQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.SiteCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.QueueZoneName);
        if (query.FromUtc.Kind != DateTimeKind.Utc || query.ToUtc.Kind != DateTimeKind.Utc || query.ToUtc <= query.FromUtc || query.ToUtc - query.FromUtc > SensingReplayQuery.MaxRange)
            throw new ArgumentOutOfRangeException(nameof(query), "A replay covers a UTC range of at most 31 days, from before to.");
        var kinds = (query.Kinds is { Count: > 0 } k ? k.AsEnumerable() : Enum.GetValues<SensingKind>()).Select(x => x.ToString()).ToArray();

        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        // One day per statement: no snapshot held for the whole range, and each statement within its own timeout.
        for (var from = query.FromUtc; from < query.ToUtc; from = from.AddDays(1))
        {
            var to = from.AddDays(1) < query.ToUtc ? from.AddDays(1) : query.ToUtc;
            await using var command = new NpgsqlCommand("""
                SELECT time_utc, site_code, queue_zone_name, kind, batch_id, ordinal, device_id, device_code, received_utc, flags, commissioned,
                       track_id, x, y, height_metres, name, direction, count_value, in_count, out_count, from_utc
                FROM sensing_event
                WHERE site_code = @site AND queue_zone_name = @zone AND time_utc >= @from AND time_utc < @to AND kind = ANY(@kinds)
                ORDER BY time_utc, batch_id, ordinal
                """, connection) { CommandTimeout = 300 };
            command.Parameters.AddWithValue("site", query.SiteCode);
            command.Parameters.AddWithValue("zone", query.QueueZoneName);
            command.Parameters.AddWithValue("from", from);
            command.Parameters.AddWithValue("to", to);
            command.Parameters.AddWithValue("kinds", kinds);
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
                yield return Row(reader);
        }
    }

    private static ArchivedSensingEvent Row(NpgsqlDataReader reader) => new(
        DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
        reader.GetString(1),
        reader.GetString(2),
        Enum.Parse<SensingKind>(reader.GetString(3)),
        reader.GetGuid(4),
        reader.GetInt32(5),
        reader.GetGuid(6),
        reader.GetString(7),
        DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
        (SensedFlags)reader.GetInt16(9),
        reader.GetBoolean(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetDouble(12),
        reader.IsDBNull(13) ? null : reader.GetDouble(13),
        reader.IsDBNull(14) ? null : reader.GetDouble(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : Enum.Parse<CrossingDirection>(reader.GetString(16)),
        reader.IsDBNull(17) ? null : reader.GetInt32(17),
        reader.IsDBNull(18) ? null : reader.GetInt32(18),
        reader.IsDBNull(19) ? null : reader.GetInt32(19),
        reader.IsDBNull(20) ? null : DateTime.SpecifyKind(reader.GetDateTime(20), DateTimeKind.Utc));

    private static SensingKind KindOf(SensingBatch batch) => batch switch
    {
        TrackSampleBatch => SensingKind.Track,
        VendorLineCrossingBatch => SensingKind.Crossing,
        ZoneOccupancyBatch => SensingKind.Occupancy,
        _ => SensingKind.Interval
    };

    private static List<Sensed<CanonicalEvent>> EventsOf(SensingBatch batch) => batch switch
    {
        TrackSampleBatch t => [.. (t.Samples ?? []).Select(s => s is null ? null : new Sensed<CanonicalEvent>(s.Event, s.TimeUtc, s.Flags))],
        VendorLineCrossingBatch c => [.. (c.Crossings ?? []).Select(s => s is null ? null : new Sensed<CanonicalEvent>(s.Event, s.TimeUtc, s.Flags))],
        ZoneOccupancyBatch o => [.. (o.Occupancy ?? []).Select(s => s is null ? null : new Sensed<CanonicalEvent>(s.Event, s.TimeUtc, s.Flags))],
        IntervalCountBatch i => [.. (i.Intervals ?? []).Select(s => s is null ? null : new Sensed<CanonicalEvent>(s.Event, s.TimeUtc, s.Flags))],
        _ => []
    };

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

/// <summary>
/// Archives every sensing batch of one sensing topic (ARV-026), through the Ariva consume pipe. The archive itself
/// skips batches already archived, so redelivery is harmless. A database outage (connection lost, failover, too many
/// connections) is waited out for about a minute per attempt (four minutes with the pipe's retries) before the message
/// fails, so a failover does not send a stretch of evidence to the dead-letter topic.
/// </summary>
public sealed class SensingArchiveConsumer<TBatch>(ISensingArchive archive, TimeProvider timeProvider) : IConsumer<TBatch> where TBatch : SensingBatch
{
    /// <summary>
    /// About a minute in all, so that with the pipe's three retries one message stays under Kafka's default
    /// five-minute poll interval and an outage does not also cause a rebalance.
    /// </summary>
    private static readonly TimeSpan[] OutageDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    public async Task Consume(ConsumeContext<TBatch> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await archive.WriteAsync([context.Message], context.CancellationToken);
                return;
            }
            catch (Exception e) when (SensingArchiveOutage.Is(e) && attempt < OutageDelays.Length)
            {
                await Task.Delay(OutageDelays[attempt], timeProvider, context.CancellationToken);
            }
        }
    }
}

/// <summary>Errors that mean the database is unreachable or restarting, not refusing the data.</summary>
public static class SensingArchiveOutage
{
    public static bool Is(Exception e) => e switch
    {
        PostgresException p => p.SqlState.StartsWith("08", StringComparison.Ordinal) || p.SqlState is "57P01" or "57P02" or "57P03" or "53300" or "40001" or "40P01",
        NpgsqlException n => n.IsTransient,
        TimeoutException => true,
        _ => false
    };
}
