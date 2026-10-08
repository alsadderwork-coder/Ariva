using System.Text.Json;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Messaging;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Setup;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>Writes the host's log to a file when ARIVA_IT_LOG names one (diagnosing a failing run).</summary>
internal sealed class FileLoggerProvider(string path) : Microsoft.Extensions.Logging.ILoggerProvider
{
    private static readonly Lock Gate = new();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(path, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string path, string category) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (Gate)
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {logLevel} {category}: {formatter(state, exception)} {exception}\n");
        }
    }
}

/// <summary>The geometry of the synthetic zones, without a published zone profile.</summary>
internal sealed class FixedGeometrySource() : ZoneGeometrySource(null)
{
    public override Task<ZoneGeometry> LoadAsync(string siteCode, string queueZoneName, CancellationToken ct) =>
        Task.FromResult(queueZoneName.StartsWith('Q')
            ? new ZoneGeometry(new QueueZoneGeometry(queueZoneName, new HashSet<string> { $"{queueZoneName} entry" }, new HashSet<string> { $"{queueZoneName} exit" },
                new HashSet<string>(),
                // ARV-115: Q1 has an overflow band that its sensor reports (QueueStreamTests.Evening with a snake capacity).
                queueZoneName.StartsWith("Q1", StringComparison.Ordinal) ? new HashSet<string> { $"{queueZoneName} band" } : new HashSet<string>())
            {
                // ARV-114a: a snake smaller than the evening's busiest minutes, so some minutes are outside it.
                Capacities = new Dictionary<string, int>(StringComparer.Ordinal) { [queueZoneName] = 20 },
                // ARV-116: Q1 zones have a desk with a staff and a service zone.
                DeskZones = queueZoneName.StartsWith("Q1", StringComparison.Ordinal)
                    ? new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal)
                    {
                        [$"{queueZoneName} staff"] = new($"DMO/IMM/{queueZoneName}-D1", DeskSource.StaffZone),
                        [$"{queueZoneName} service"] = new($"DMO/IMM/{queueZoneName}-D1", DeskSource.ServiceZone)
                    }
                    : new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal)
            }, 7)
            : null);
}

/// <summary>
/// ARV-034 end to end on a real broker and TimescaleDB: the queue stream worker reads two zones' crossings and
/// occupancy from the sensing topics (three partitions each), merges the topics in receive order, and writes minute rows,
/// bins, snapshots and offsets in one transaction per checkpoint. A run stopped in the middle and restarted, even with
/// its Kafka offsets lost, ends with exactly the rows of a run that never stopped, and a bad record is dead-lettered.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static readonly DateTime Start = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A deterministic evening: people enter each zone through the minute and leave after a few minutes, and the queue
    // zone reports its occupancy at the end of every minute. One crossing and one occupancy batch per zone and minute.
    // With a snake capacity (ARV-115) the queue zone reports up to it and its band ("<zone> band") the rest, as the
    // scenario's sensors do, so the sum is still the people inside.
    internal static List<SensingBatch> Evening(string zone, int salt, int? snake = null, bool desks = false)
    {
        var people = new List<(int Id, DateTime In, DateTime Out)>();
        var id = 0;
        for (var m = 0; m < 90; m++)
        {
            var arrivals = 2 + ((m * 5 + salt) % 6);
            for (var j = 0; j < arrivals; j++)
            {
                var entry = Start.AddMinutes(m).AddSeconds(60.0 * (j + 0.5) / arrivals);
                var wait = TimeSpan.FromSeconds(60 + ((m * 37 + j * 53 + salt * 11) % 420));
                people.Add((++id, entry, entry + wait));
            }
        }

        var batches = new List<SensingBatch>();
        for (var m = 0; m < 100; m++)
        {
            var from = Start.AddMinutes(m);
            var to = from.AddMinutes(1);
            var received = to.AddSeconds(2);
            var crossings = people.Where(p => p.In >= from && p.In < to).Select(p => (p.In, new LineCrossing($"{zone} entry", CrossingDirection.In, $"{p.Id}", p.In)))
                .Concat(people.Where(p => p.Out >= from && p.Out < to).Select(p => (p.Out, new LineCrossing($"{zone} exit", CrossingDirection.Out, $"{p.Id}", p.Out))))
                .OrderBy(c => c.Item1).Select(c => new Sensed<LineCrossing>(c.Item2, c.Item1, SensedFlags.None)).ToList();
            var inside = people.Count(p => p.In < to && p.Out >= to);
            T Stamp<T>(T batch) where T : SensingBatch
            {
                batch.Id = Guid.NewGuid();
                batch.DeviceId = Guid.Parse("00000000-0000-0000-0000-000000000042");
                batch.DeviceCode = "S-42";
                batch.SiteCode = "DMO";
                batch.QueueZoneName = zone;
                batch.Dialect = "canonical";
                batch.Commissioned = true;
                batch.ReceivedUtc = received;
                batch.Clock = new ClockReading(0, true, ClockState.Ok);
                return batch;
            }

            if (crossings.Count > 0)
                batches.Add(Stamp(new VendorLineCrossingBatch { Crossings = crossings }));
            var readings = new List<Sensed<ZoneOccupancy>>
            {
                new(new ZoneOccupancy(zone, snake is { } cap ? Math.Min(inside, cap) : inside, to.AddSeconds(-1)), to.AddSeconds(-1), SensedFlags.None)
            };
            if (snake is { } capacity)
                readings.Add(new(new ZoneOccupancy($"{zone} band", Math.Max(0, inside - capacity), to.AddSeconds(-1)), to.AddSeconds(-1), SensedFlags.None));
            // ARV-116: the zone's desk is staffed for seven minutes, then not for seven, and serves every other three; its
            // zones are read at the minute's start (a minute old when they arrive, so behind the queue's watermark).
            if (desks)
            {
                var staffed = m / 7 % 2 == 0;
                readings.Add(new(new ZoneOccupancy($"{zone} staff", staffed ? 1 : 0, from), from, SensedFlags.None));
                readings.Add(new(new ZoneOccupancy($"{zone} service", staffed && m / 3 % 2 == 0 ? 1 : 0, from), from, SensedFlags.None));
            }
            batches.Add(Stamp(new ZoneOccupancyBatch { Occupancy = readings }));
        }

        return batches;
    }

    private async Task<string> DatabaseAsync(TestDatabase database)
    {
        var name = await postgres.CreateDatabaseAsync(database);
        await new SqlScriptRunner(postgres.ConnectionString(name), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        return name;
    }

    private async Task<Dictionary<(string Topic, int Partition), long>> ProduceAsync(string suffix, int fromMinute = 0, int toMinute = int.MaxValue)
    {
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            try
            {
                await admin.CreateTopicsAsync(QueueStreamWorker.Topics.Concat(QueueStreamWorker.Topics.Select(KafkaTopics.DeadLetter))
                    .Select(t => new TopicSpecification { Name = t, NumPartitions = t.EndsWith(KafkaTopics.DeadLetterSuffix, StringComparison.Ordinal) ? 1 : 3, ReplicationFactor = 1 }));
            }
            catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
            {
                // Created by an earlier test of this class.
            }
        }

        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers, EnableIdempotence = true }).Build();
        var ends = new Dictionary<(string, int), long>();
        foreach (var batch in Evening($"Q1{suffix}", 0, snake: 15, desks: true).Concat(Evening($"Q2{suffix}", 3)).OrderBy(b => b.ReceivedUtc)
                     .Where(b => b.ReceivedUtc >= Start.AddMinutes(fromMinute) && (toMinute == int.MaxValue || b.ReceivedUtc < Start.AddMinutes(toMinute))))
        {
            var topic = batch is VendorLineCrossingBatch ? KafkaTopics.DeviceVendorLineCrossing : KafkaTopics.DeviceZoneOccupancy;
            var result = await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = batch.ZoneKey, Value = JsonSerializer.SerializeToUtf8Bytes(batch, batch.GetType(), EventCatalog.Json) }, Ct);
            ends[(topic, result.Partition.Value)] = result.Offset.Value + 1;
            result.Partition.Value.Should().Be(QueueStreamWorker.ExpectedPartition(batch.ZoneKey, 3), "the worker's partition check matches librdkafka's default partitioner");
        }

        return ends;
    }

    private IHost Host(string database, string service)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = postgres.AdminUsername,
            ["Database:Password"] = postgres.AdminPassword,
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Kafka:ServiceName"] = service,
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Stream:Enabled"] = "true",
            ["Stream:IdleTickSeconds"] = "0"
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new FileLoggerProvider(log));
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging.StreamQueueZones());
        builder.Services.AddArivaQueueStream(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<ZoneGeometrySource, FixedGeometrySource>());
        return builder.Build();
    }

    // The end of every partition of the sensing topics now.
    private Dictionary<(string Topic, int Partition), long> Ends()
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig { BootstrapServers = kafka.BootstrapServers, GroupId = "it-ends" }).Build();
        var ends = new Dictionary<(string, int), long>();
        foreach (var topic in QueueStreamWorker.Topics)
        {
            for (var p = 0; p < 3; p++)
                ends[(topic, p)] = consumer.QueryWatermarkOffsets(new TopicPartition(topic, new Partition(p)), TimeSpan.FromSeconds(10)).High.Value;
        }

        return ends;
    }

    private async Task<bool> CaughtUpAsync(string database, string group, Dictionary<(string Topic, int Partition), long> ends)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT topic, partition_no, next_offset FROM stream_offset WHERE consumer_group = @group", connection);
        command.Parameters.AddWithValue("group", group);
        var saved = new Dictionary<(string, int), long>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            saved[(reader.GetString(0), reader.GetInt32(1))] = reader.GetInt64(2);
        return ends.All(e => e.Value == 0 || saved.GetValueOrDefault(e.Key) >= e.Value);
    }

    private async Task WaitForAsync(string database, string group, int seconds = 180)
    {
        var ends = Ends();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await CaughtUpAsync(database, group, ends) && DateTime.UtcNow < until)
            await Task.Delay(250, Ct);
        (await CaughtUpAsync(database, group, ends)).Should().BeTrue("the worker should have applied every record within {0} seconds", seconds);
    }

    /// <summary>The rows of a literal query, values bound as <paramref name="parameters"/> (never interpolated, CWE-89).</summary>
    private async Task<List<string>> RowsAsync(string database, string sql, IReadOnlyDictionary<string, object> parameters = null)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters ?? new Dictionary<string, object>())
            command.Parameters.AddWithValue(name, value);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }

    private const string MinuteRows = """
        SELECT zone_key, minute_utc, profile_version, status, entries, exits, waits, mean_wait_minutes, p50_wait_minutes, p90_wait_minutes, p95_wait_minutes,
               share_within_target, queue_length, length_from_sensors, length_degraded, nowcast_minutes, throughput_per_minute, no_service, nowcast_degraded
        FROM queue_minute ORDER BY zone_key, minute_utc
        """;

    // The shadow nowcasts (ARV-117a, script 0043), read with the test's administrator login (the runtime role cannot).
    private const string ShadowRows = """
        SELECT zone_key, minute_utc, nowcast_minutes, no_service, nowcast_degraded, sensor_cycle_minutes FROM queue_minute_shadow ORDER BY zone_key, minute_utc
        """;

    private const string BinRows = """
        SELECT zone_key, start_utc, revision, status, quality, entries, exits, waits, mean_wait_minutes, p90_wait_minutes, abandoned, fragmented, censored,
               reanchored, rejected, open_people, late_events, profile_version FROM queue_bin ORDER BY zone_key, start_utc, revision
        """;

    private const string LineRows = """
        SELECT zone_key, line_name, line_role, source, minute_utc, profile_version, crossings_in, crossings_out
        FROM line_minute ORDER BY zone_key, line_name, source, minute_utc
        """;

    // The desks' zone readings (ARV-116), without the row id and the time it was written.
    private const string DeskRows = """
        SELECT site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version FROM desk_zone_reading
        WHERE zone_key IN ('DMO/Q1', 'DMO/Q2') ORDER BY desk_code, source, reading_utc
        """;

    private const string OverflowRows = """
        SELECT zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy FROM overflow_minute ORDER BY zone_key, band_name, minute_utc
        """;

    // The OverflowDetected events in the outbox (ARV-115), in their order per key, without the relay's columns.
    private const string OverflowEvents = """
        SELECT id, message_key, message_type, payload::text FROM outbox_message WHERE topic = 'ariva.flow.overflow-detected.v1'
        ORDER BY message_key, payload->>'minuteUtc', payload->>'state'
        """;

    private const string HealthRows = """
        SELECT zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, occupancy_start, occupancy_end, conservation_residual,
               tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open, track_completion_rate,
               occupancy_minutes, capacity_minutes, minutes_outside_capacity
        FROM zone_health_bin ORDER BY zone_key, start_utc, revision
        """;

    [Fact]
    public async Task Worker_Should_WriteTheSameRows_When_StoppedMidStreamAndRestartedWithItsKafkaOffsetsLost()
    {
        // The first part of the evening; the restarting run processes it and stops.
        await ProduceAsync("", toMinute: 47);
        var straight = await DatabaseAsync(TestDatabase.StreamStraight);
        var restart = await DatabaseAsync(TestDatabase.StreamRestart);
        // The evening is dated 2026-09-28: without this, desk_zone_reading's 7-day retention job (ARV-116) could drop its
        // readings between the two runs.
        foreach (var database in new[] { straight, restart })
            await RowsAsync(database, "SELECT remove_retention_policy('desk_zone_reading')");
        using (var host = Host(restart, "it-restart"))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(restart, "ariva-it-restart.queue-engine");
            await host.StopAsync(Ct);
        }

        // The rest arrives while it is down; the uninterrupted run reads everything in one go.
        await ProduceAsync("", fromMinute: 47);
        using (var host = Host(straight, "it-straight"))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(straight, "ariva-it-straight.queue-engine");
            await host.StopAsync(Ct);
        }

        // As if the Kafka commit had been lost after the last checkpoint: the saved positions must win.
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build())
            await admin.DeleteGroupsAsync(["ariva-it-restart.queue-engine"]);
        (await RowsAsync(restart, "SELECT max(minute_utc) < '2026-09-28 17:47:00+00' FROM queue_minute WHERE zone_key IN ('DMO/Q1', 'DMO/Q2')")).Should().Equal(["True"], "the first run stopped in the middle");

        using (var host = Host(restart, "it-restart"))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(restart, "ariva-it-restart.queue-engine");
            await host.StopAsync(Ct);
        }

        var expectedMinutes = await RowsAsync(straight, MinuteRows);
        expectedMinutes.Should().HaveCountGreaterThan(150, "two zones over 100 minutes");
        expectedMinutes.Should().Contain(r => r.Contains("|Final|", StringComparison.Ordinal));
        (await RowsAsync(restart, MinuteRows)).Should().Equal(expectedMinutes);
        // ARV-117a: the shadow nowcasts are written in the same checkpoints, to their own table, and the restart rewrites
        // exactly the same rows.
        var expectedShadows = await RowsAsync(straight, ShadowRows);
        expectedShadows.Should().HaveCountGreaterThan(150);
        (await RowsAsync(restart, ShadowRows)).Should().Equal(expectedShadows);
        var expectedBins = await RowsAsync(straight, BinRows);
        expectedBins.Should().Contain(r => r.Contains("|Final|Good|", StringComparison.Ordinal));
        (await RowsAsync(restart, BinRows)).Should().Equal(expectedBins);
        // ARV-113: the line minutes are written in the same checkpoints, and the restart rewrites exactly the same ones.
        var expectedLines = await RowsAsync(straight, LineRows);
        expectedLines.Should().HaveCountGreaterThan(300, "two zones, two lines, about 90 minutes");
        expectedLines.Should().OnlyContain(r => r.Contains("|Ariva|", StringComparison.Ordinal));
        (await RowsAsync(restart, LineRows)).Should().Equal(expectedLines);
        // ARV-114a: the health of every bin is written with it, and the restart rewrites exactly the same rows. The
        // evening's occupancy is its entries minus its exits, so every bin with the occupancy at both ends (17:15 to
        // 18:15, five per zone: the first starts before the first reading, the last ends after the last) conserves
        // people; some minutes are above the snake's 20.
        var expectedHealth = await RowsAsync(straight, HealthRows);
        expectedHealth.Should().HaveCount(expectedBins.Count, "one health row per bin and revision");
        (await RowsAsync(restart, HealthRows)).Should().Equal(expectedHealth);
        (await RowsAsync(straight, """
            SELECT count(*) FILTER (WHERE conservation_residual IS NOT NULL) = 10, count(*) FILTER (WHERE conservation_residual <> 0),
                   sum(minutes_outside_capacity) > 0, bool_and(track_completion_rate = 1) FILTER (WHERE status = 'Final' AND tracks_entered > 0)
            FROM zone_health_bin WHERE zone_key IN ('DMO/Q1', 'DMO/Q2')
            """)).Should().Equal(["True|0|True|True"]);
        (await RowsAsync(straight, """
            SELECT (SELECT sum(crossings_in) FROM line_minute WHERE zone_key = 'DMO/Q1' AND line_role = 'Entry')
                 = (SELECT sum(entries) FROM queue_minute WHERE zone_key = 'DMO/Q1' AND minute_utc IN (SELECT minute_utc FROM line_minute WHERE zone_key = 'DMO/Q1'))
            """)).Should().Equal(["True"], "the entry line counts what the zone counted as entries");
        (await RowsAsync(restart, "SELECT zone_key FROM stream_zone_state ORDER BY zone_key")).Should().Contain(["DMO/Q1", "DMO/Q2"]);
        // ARV-117: every live minute has its shadow nowcast (ARV-117a: in queue_minute_shadow, same key); without a desk term
        // (the evening's desks have no recent minutes) it is the published nowcast.
        (await RowsAsync(straight, """
            SELECT count(*) > 0, count(*) FILTER (WHERE s.zone_key IS NOT NULL AND s.nowcast_minutes IS NOT DISTINCT FROM q.nowcast_minutes
                                                  AND s.no_service IS NOT DISTINCT FROM q.no_service AND s.nowcast_degraded = q.nowcast_degraded) = count(*)
            FROM queue_minute q LEFT JOIN queue_minute_shadow s ON s.zone_key = q.zone_key AND s.minute_utc = q.minute_utc
            WHERE q.zone_key IN ('DMO/Q1', 'DMO/Q2') AND q.queue_length IS NOT NULL
            """)).Should().Equal(["True|True"]);

        // ARV-116: Q1's desk zone readings are written in the same checkpoints (counts only), each once: the restart writes
        // exactly the same readings, and the queue's rows above are those of a queue without desk zones (Q2 has none).
        var expectedDesks = await RowsAsync(straight, DeskRows);
        expectedDesks.Should().HaveCount(200, "Q1's desk has a staff and a service reading in each of the 100 minutes");
        expectedDesks.Should().OnlyContain(r => r.StartsWith("DMO|DMO/IMM/Q1-D1|", StringComparison.Ordinal) && r.EndsWith("|False|DMO/Q1|7", StringComparison.Ordinal));
        (await RowsAsync(restart, DeskRows)).Should().Equal(expectedDesks);
        (await RowsAsync(straight, "SELECT count(*) FROM desk_zone_reading WHERE zone_key = 'DMO/Q1' AND source = 'StaffZone' AND occupancy = 1")).Should().Equal(["51"],
            "staffed in minutes 0 to 6, 14 to 20 and so on to 84 to 90, and 98 and 99");
        (await RowsAsync(straight, "SELECT count(DISTINCT id) = count(*), bool_and(written_utc IS NOT NULL) FROM desk_zone_reading")).Should().Equal("True|True");

        // ARV-115: Q1's band minutes and the OverflowDetected events are written in the same checkpoints; the restart writes
        // exactly the same rows and events (the same ids, so none is added twice), and the events alternate per band.
        var expectedOverflow = await RowsAsync(straight, OverflowRows);
        expectedOverflow.Count(r => r.StartsWith("DMO/Q1|Q1 band|", StringComparison.Ordinal)).Should().BeGreaterThan(80, "Q1's band reports at the end of every minute");
        expectedOverflow.Should().OnlyContain(r => r.Contains(" band|", StringComparison.Ordinal), "only Q1 zones have a band");
        (await RowsAsync(restart, OverflowRows)).Should().Equal(expectedOverflow);
        var expectedEvents = await RowsAsync(straight, OverflowEvents);
        expectedEvents.Count(r => r.Contains("|DMO/Q1|OverflowDetected|", StringComparison.Ordinal)).Should().BeGreaterThan(1);
        (await RowsAsync(restart, OverflowEvents)).Should().Equal(expectedEvents);
        (await RowsAsync(straight, """
            SELECT string_agg(payload->>'state', ',' ORDER BY payload->>'minuteUtc') FROM outbox_message
            WHERE topic = 'ariva.flow.overflow-detected.v1' AND message_key = 'DMO/Q1'
            """)).Single().Should().MatchRegex("^Occupied(,Emptied,Occupied)*(,Emptied)?$", "each change once: occupied, then emptied, in turn");
        // Overflow minutes per 15-minute bin (TC-19): the minutes in which the band held anyone.
        (await RowsAsync(straight, """
            SELECT (SELECT sum(overflow_minutes) FROM overflow_bin_15m WHERE zone_key = 'DMO/Q1')
                 = (SELECT count(*) FROM overflow_minute WHERE zone_key = 'DMO/Q1' AND max_occupancy > 0),
                   (SELECT sum(observed_minutes) FROM overflow_bin_15m WHERE zone_key = 'DMO/Q1') = (SELECT count(*) FROM overflow_minute WHERE zone_key = 'DMO/Q1')
            """)).Should().Equal(["True|True"]);
    }

    [Fact]
    public async Task Store_Should_UpsertIdempotentlyAndRefuseAHugeState_When_ACheckpointIsWrittenTwice()
    {
        var database = await DatabaseAsync(TestDatabase.StreamStore);
        var log = new WarningLog();
        var store = new StreamStore(new DatabaseSettings
        {
            Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = postgres.AdminUsername, Password = postgres.AdminPassword
        }, TimeProvider.System, log);
        var geometry = new QueueZoneGeometry("Q1", new HashSet<string> { "Q1 entry" }, new HashSet<string> { "Q1 exit" }, new HashSet<string>(), new HashSet<string>());
        var zone = new ZoneProcessor("DMO/Q1", geometry, 7);
        foreach (var batch in Evening("Q1", 0))
            zone.Offer(batch, DateTime.MaxValue);
        var outputs = zone.Drain();
        var state = zone.Capture();
        var checkpoint = new StreamCheckpoint("g", [outputs], [state], [], [new StreamOffset("t", 0, 42)]);

        await store.SaveAsync(checkpoint, Ct);
        var first = await RowsAsync(database, MinuteRows);
        await store.SaveAsync(checkpoint, Ct);

        (await RowsAsync(database, MinuteRows)).Should().Equal(first);
        var lines = await RowsAsync(database, LineRows);
        lines.Should().HaveCount(outputs.Lines.Count).And.NotBeEmpty("the evening's closed line minutes are written once each");
        // ARV-114a: one health row per bin and revision, the last of the checkpoint, rewritten to the same values.
        var health = await RowsAsync(database, HealthRows);
        health.Should().HaveCount(outputs.Health.Select(h => (h.StartUtc, h.Revision)).Distinct().Count()).And.NotBeEmpty();
        health.Should().HaveCount((await RowsAsync(database, BinRows)).Count);
        var finalBin = outputs.Health.Last(h => h.Status == BinStatus.Final);
        (await RowsAsync(database, "SELECT entries, exits, status FROM zone_health_bin WHERE start_utc = @start",
                new Dictionary<string, object> { ["start"] = DateTime.SpecifyKind(finalBin.StartUtc, DateTimeKind.Utc) }))
            .Should().Equal($"{finalBin.Entries}|{finalBin.Exits}|Final");
        first.Should().HaveCount(outputs.Live.Select(l => l.MinuteUtc).Concat(outputs.Minutes.Select(m => m.StartUtc)).Distinct().Count());
        (await store.LoadOffsetsAsync("g", [("t", 0), ("t", 1)], Ct)).Should().Equal(new StreamOffset("t", 0, 42));
        var loaded = await store.LoadStateAsync("DMO/Q1", Ct);
        JsonSerializer.Serialize(loaded, EventCatalog.Json).Should().Be(JsonSerializer.Serialize(state, EventCatalog.Json));
        (await store.LoadStateAsync("DMO/none", Ct)).Should().BeNull();

        // The 15-minute continuous aggregate exists and refreshes over the rows.
        await using (var connection = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await connection.OpenAsync(Ct);
            // The aggregate's own policy may be refreshing at the same moment (55P03): try again shortly.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var refresh = new NpgsqlCommand("CALL refresh_continuous_aggregate('queue_minute_15m', NULL, NULL)", connection);
                    await refresh.ExecuteNonQueryAsync(Ct);
                    break;
                }
                catch (PostgresException e) when (e.SqlState == "55P03" && attempt < 20)
                {
                    await Task.Delay(500, Ct);
                }
            }
        }

        (await RowsAsync(database, "SELECT bucket_utc, entries FROM queue_minute_15m WHERE zone_key = 'DMO/Q1' ORDER BY bucket_utc")).Should().HaveCountGreaterThan(5);

        // ARV-115: band minutes and OverflowDetected. A rewrite changes nothing and adds no second event; parts of a minute
        // released early merge (lowest of the lows, highest of the highs) whatever their order.
        var bandMinute = new DateTime(2026, 9, 28, 19, 10, 0, DateTimeKind.Utc);
        var bands = new ZoneOutputs("DMO/Q1", [], [], [], [], [], [], [],
            [new OverflowMinute("Q1 band", bandMinute, 2, 6), new OverflowMinute("Q1 band", bandMinute.AddMinutes(1), 0, 0)],
            [new OverflowChange("Q1 band", bandMinute, OverflowChangeKind.Occupied, 6, null), new OverflowChange("Q1 band", bandMinute.AddMinutes(1), OverflowChangeKind.Emptied, 6, bandMinute)]);
        var bandCheckpoint = new StreamCheckpoint("g", [bands], [state], [], []);
        await store.SaveAsync(bandCheckpoint, Ct);
        await store.SaveAsync(bandCheckpoint, Ct);
        await store.SaveAsync(new StreamCheckpoint("g", [new ZoneOutputs("DMO/Q1", [], [], [], [], [], [], [], [new OverflowMinute("Q1 band", bandMinute, 1, 4), new OverflowMinute("Q1 band", bandMinute, 3, 9)])], [state], [], []), Ct);
        (await RowsAsync(database, "SELECT to_char(minute_utc AT TIME ZONE 'UTC', 'HH24:MI'), profile_version, min_occupancy, max_occupancy FROM overflow_minute WHERE zone_key = 'DMO/Q1' ORDER BY minute_utc"))
            .Should().Equal("19:10|7|1|9", "19:11|7|0|0");
        (await RowsAsync(database, """
            SELECT message_key, message_type, payload->>'state', payload->>'bandName', payload->>'peakOccupancy', payload->>'zoneProfileVersion', headers->>'ariva-event-type'
            FROM outbox_message WHERE topic = 'ariva.flow.overflow-detected.v1' ORDER BY payload->>'minuteUtc'
            """)).Should().Equal("DMO/Q1|OverflowDetected|Occupied|Q1 band|6|7|OverflowDetected", "DMO/Q1|OverflowDetected|Emptied|Q1 band|6|7|OverflowDetected");
        (await RowsAsync(database, "SELECT count(*) FROM outbox_message WHERE id = @id",
                new Dictionary<string, object> { ["id"] = Ariva.Core.Domain.Events.OverflowDetected.IdOf("DMO/Q1", "Q1 band", bandMinute, OverflowChangeKind.Occupied, 7) }))
            .Should().Equal("1");
        (await RowsAsync(database, "SELECT overflow_minutes, observed_minutes, peak_band_occupancy FROM overflow_bin_15m WHERE zone_key = 'DMO/Q1'"))
            .Should().Equal("1|2|9");
        (await RowsAsync(database, """
            SELECT has_table_privilege('ariva_runtime', 'overflow_bin_15m', 'SELECT'), has_table_privilege('ariva_runtime', 'overflow_bin_15m', 'INSERT'),
                   has_table_privilege('ariva_runtime', 'overflow_minute', 'DELETE'), has_table_privilege('ariva_runtime', 'overflow_minute', 'INSERT')
            """)).Should().Equal("True|False|False|True");

        // ARV-113: every line role and both sources are kept apart under one key; a rewrite changes nothing.
        var minute = new DateTime(2026, 9, 28, 19, 0, 0, DateTimeKind.Utc);
        var roles = new ZoneOutputs("DMO/Q1", [], [], [], [], [],
        [
            new LineMinute("Q1 entry", QueueLineRole.Entry, minute, 4, 1), new LineMinute("Q1 exit", QueueLineRole.Exit, minute, 0, 3),
            new LineMinute("Q1 count", QueueLineRole.Count, minute, 2, 2), new LineMinute("Q1-OV entry", QueueLineRole.OverflowEntry, minute, 5, 0),
            new LineMinute("Q1 entry", QueueLineRole.Entry, minute, 6, 0, LineCountSource.Vendor)
        ]);
        var lineCheckpoint = new StreamCheckpoint("g", [roles], [state], [], []);
        await store.SaveAsync(lineCheckpoint, Ct);
        await store.SaveAsync(lineCheckpoint, Ct);
        (await RowsAsync(database, "SELECT line_name, line_role, source, crossings_in, crossings_out, profile_version FROM line_minute WHERE minute_utc = '2026-09-28 19:00:00+00' ORDER BY line_name, source"))
            .Should().Equal("Q1 count|Count|Ariva|2|2|7", "Q1 entry|Entry|Ariva|4|1|7", "Q1 entry|Entry|Vendor|6|0|7", "Q1 exit|Exit|Ariva|0|3|7", "Q1-OV entry|OverflowEntry|Ariva|5|0|7");
        await using (var connection = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await connection.OpenAsync(Ct);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var refresh = new NpgsqlCommand("CALL refresh_continuous_aggregate('line_minute_15m', NULL, NULL)", connection);
                    await refresh.ExecuteNonQueryAsync(Ct);
                    break;
                }
                catch (PostgresException e) when (e.SqlState == "55P03" && attempt < 20)
                {
                    await Task.Delay(500, Ct);
                }
            }
        }

        (await RowsAsync(database, "SELECT source, crossings_in FROM line_minute_15m WHERE zone_key = 'DMO/Q1' AND line_name = 'Q1 entry' AND bucket_utc = '2026-09-28 19:00:00+00' ORDER BY source"))
            .Should().Equal("Ariva|4", "Vendor|6");

        // A minute released early (the zone held more open line minutes than the bound) comes in additive parts: parts in
        // one checkpoint are merged, parts in later checkpoints add to the written row, and nothing is lost; a whole minute
        // in the same checkpoint still replaces.
        var early = new DateTime(2026, 9, 28, 19, 1, 0, DateTimeKind.Utc);
        await store.SaveAsync(new StreamCheckpoint("g", [new ZoneOutputs("DMO/Q1", [], [], [], [], [],
        [
            new LineMinute("Q1 entry", QueueLineRole.Entry, early, 2, 1) { Additive = true },
            new LineMinute("Q1 entry", QueueLineRole.Entry, early, 3, 0) { Additive = true },
            new LineMinute("Q1 exit", QueueLineRole.Exit, early, 0, 1) { Additive = true },
            new LineMinute("Q1 exit", QueueLineRole.Exit, early, 0, 4),
            new LineMinute("", QueueLineRole.Entry, early, 9, 9) { Additive = true },
            new LineMinute(new string('x', 201), QueueLineRole.Entry, early, 9, 9)
        ])], [state], [], []), Ct);
        await store.SaveAsync(new StreamCheckpoint("g", [new ZoneOutputs("DMO/Q1", [], [], [], [], [],
            [new LineMinute("Q1 entry", QueueLineRole.Entry, early, 1, 2) { Additive = true }])], [state], [], []), Ct);
        (await RowsAsync(database, "SELECT line_name, crossings_in, crossings_out FROM line_minute WHERE minute_utc = '2026-09-28 19:01:00+00' ORDER BY line_name"))
            .Should().Equal("Q1 entry|6|3", "Q1 exit|0|4");
        log.Warnings.Should().Equal("Dropped 2 line minutes of zone DMO/Q1: empty or overlong line name, or unknown role");

        // ARV-060: every minute with waits keeps its histogram, whose counts add up to its waits (F7), so reports merge hours.
        (await RowsAsync(database, """
            SELECT count(*) FILTER (WHERE waits > 0), count(*) FILTER (WHERE waits > 0 AND wait_buckets IS NOT NULL
                   AND (SELECT sum(c) FROM unnest(wait_counts) AS c) = waits)
            FROM queue_minute WHERE zone_key = 'DMO/Q1'
            """)).Single().Split('|').Should().Match<string[]>(r => r[0] == r[1] && r[0] != "0", "each minute with waits has a complete histogram");

        // A stored state above the bound is refused before it is read (CWE-120).
        await using (var connection = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await connection.OpenAsync(Ct);
            await using var huge = new NpgsqlCommand(
                "INSERT INTO stream_zone_state (zone_key, profile_version, state, state_bytes, reference_utc, updated_on) VALUES ('DMO/huge', 7, '{}', @bytes, now(), now())", connection);
            huge.Parameters.AddWithValue("bytes", StreamStore.MaxStateBytes + 1);
            await huge.ExecuteNonQueryAsync(Ct);
        }

        Func<Task> load = () => store.LoadStateAsync("DMO/huge", Ct);
        await load.Should().ThrowAsync<InvalidDataException>();
    }

    // ARV-115 review: band minutes and changes the tables or the outbox would refuse are dropped before the insert, with
    // one warning per zone and kind that names the zone key and the count only (never the band name, CWE-117), so the
    // checkpoint still commits. A refused insert would fail the whole checkpoint (22001 or 23514), which the worker
    // retries forever. The longest zone key (a 17-character site and a 200-character zone name) does not fit the outbox's
    // message_key (200): its change is dropped, its minute is still written.
    [Fact]
    public async Task Store_Should_WriteTheShadowNowcastInItsOwnTable_When_ALiveMinuteCarriesIt()
    {
        // ARV-117a: the shadow nowcast without AMAN inputs goes to queue_minute_shadow, keyed like queue_minute, in the same
        // checkpoint as the published row; a later checkpoint of the minute replaces both, and the table refuses a shadow
        // that has a number and a no-service reason at once, neither, a negative number or an unknown reason. ARV-117b:
        // the sensor cycle time the shadow took is written beside it (script 0045), null when it fell back; a value outside
        // the column's check is written as none, and the check refuses one written by hand.
        var database = await DatabaseAsync(TestDatabase.StreamShadow);
        var store = new StreamStore(new DatabaseSettings
        {
            Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = postgres.AdminUsername, Password = postgres.AdminPassword
        }, TimeProvider.System, new WarningLog());
        var minute = new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc);
        var state = new ZoneProcessor("DMO/Q1", new QueueZoneGeometry("Q1", new HashSet<string> { "Q1 entry" }, new HashSet<string> { "Q1 exit" },
            new HashSet<string>(), new HashSet<string>()), 7).Capture();
        ZoneOutputs Live(params QueueLiveMinute[] rows) => new("DMO/Q1", [], [], rows, []);
        const string Row = """
            SELECT q.minute_utc, q.nowcast_minutes, q.no_service, q.nowcast_degraded, s.nowcast_minutes, s.no_service, s.nowcast_degraded, s.sensor_cycle_minutes
            FROM queue_minute q LEFT JOIN queue_minute_shadow s ON s.zone_key = q.zone_key AND s.minute_utc = q.minute_utc
            WHERE q.zone_key = 'DMO/Q1' ORDER BY q.minute_utc
            """;

        await store.SaveAsync(new StreamCheckpoint("g-shadow", [Live(
            new QueueLiveMinute("DMO/Q1", minute, 61, true, false, 15.25, 4.0, null, false) { Shadow = new ShadowNowcast(17.5, null, false, 1.5) },
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(1), 61, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(null, NoServiceReason.NoThroughputData, true) },
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(2), 61, true, false, null, 0, NoServiceReason.NothingOpen, false),
            // Neither a number nor a reason (Nowcast.Compute never returns it): written as no shadow, never refused.
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(3), 61, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(null, null, true, 1.5) },
            // A cycle time outside the column's check (the formula's bounds never give one): the shadow without it.
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(4), 61, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(8.5, null, true, 61) },
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(5), 61, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(8.5, null, true, double.NaN) })],
            [state], [], [new StreamOffset("t", 0, 1)]), Ct);
        (await RowsAsync(database, Row)).Should().Equal(
            "09/28/2026 18:05:00|15.25|null|False|17.5|null|False|1.5",
            "09/28/2026 18:06:00|15.5|null|False|null|NoThroughputData|True|null",
            "09/28/2026 18:07:00|null|NothingOpen|False|null|null|null|null",
            "09/28/2026 18:08:00|15.5|null|False|null|null|null|null",
            "09/28/2026 18:09:00|15.5|null|False|8.5|null|True|null",
            "09/28/2026 18:10:00|15.5|null|False|8.5|null|True|null");
        (await RowsAsync(database, "SELECT count(*) FROM queue_minute_shadow")).Should().Equal(["4"], "a minute without a shadow has no row");

        await store.SaveAsync(new StreamCheckpoint("g-shadow", [Live(
            new QueueLiveMinute("DMO/Q1", minute, 62, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(null, NoServiceReason.NothingOpen, false) },
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(1), 62, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(7.25, null, false, 1.25) })],
            [state], [], [new StreamOffset("t", 0, 2)]), Ct);
        (await RowsAsync(database, Row)).Take(2).Should().Equal(["09/28/2026 18:05:00|15.5|null|False|null|NothingOpen|False|null", "09/28/2026 18:06:00|15.5|null|False|7.25|null|False|1.25"],
            "the minute's later checkpoint replaces both nowcasts and the sensor cycle time");

        foreach (var forged in new[]
                 {
                     "UPDATE queue_minute_shadow SET nowcast_minutes = 3, no_service = 'NothingOpen' WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET no_service = 'Bogus', nowcast_minutes = NULL WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET nowcast_minutes = -1, no_service = NULL WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET nowcast_minutes = NULL, no_service = NULL WHERE zone_key = 'DMO/Q1'",
                     // ARV-117b, script 0045: a cycle time from 0.05 to 60 minutes a passenger, or none.
                     "UPDATE queue_minute_shadow SET sensor_cycle_minutes = 0.04 WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET sensor_cycle_minutes = 60.5 WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET sensor_cycle_minutes = -1 WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET sensor_cycle_minutes = 'NaN' WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET sensor_cycle_minutes = 'Infinity' WHERE zone_key = 'DMO/Q1'"
                 })
        {
            var act = () => RowsAsync(database, forged);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", "a check of script 0043 or 0045 refuses: {0}", forged);
        }

        var flagless = () => RowsAsync(database, "UPDATE queue_minute_shadow SET nowcast_degraded = NULL WHERE zone_key = 'DMO/Q1'");
        (await flagless.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23502", "a shadow always carries its F11 flag");
        // Script 0043 copied 0041's rows and dropped its columns and checks.
        (await RowsAsync(database, """
            SELECT count(*) FROM information_schema.columns WHERE table_name = 'queue_minute' AND column_name LIKE 'shadow%'
            UNION ALL SELECT count(*) FROM pg_constraint WHERE conrelid = 'queue_minute'::regclass AND conname LIKE '%shadow%'
            """)).Should().Equal("0", "0");
    }

    // ARV-117a (CWE-862, CWE-863), the owner's decision of 2026-10-07: the database enforces the no-read rule. A runtime
    // login (as ariva_ensure_runtime_login creates it for the hosts) writes and rewrites the shadow through the stream
    // store, and every read of a shadow value fails with 42501: a column, a wildcard, a whole row, a filter or an
    // assignment over a value, RETURNING, COPY, and a chunk read directly. Only the key columns are readable (the
    // upsert's conflict target needs them). The read role for the validation comparison reads it (ARV-104g1 grants it to the
    // validation reader login only: ValidationReaderLoginTests).
    [Fact]
    public async Task RuntimeLogin_Should_WriteButNeverReadTheShadow_When_TheStreamStoreUpserts()
    {
        var database = await DatabaseAsync(TestDatabase.StreamShadowRuntime);
        const string login = "it_runtime_shadow";
        var password = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
            ensure.Parameters.AddWithValue("login", login);
            ensure.Parameters.AddWithValue("password", password);
            await ensure.ExecuteNonQueryAsync(Ct);
        }

        var store = new StreamStore(new DatabaseSettings { Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = login, Password = password },
            TimeProvider.System, new WarningLog());
        var minute = new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc);
        var state = new ZoneProcessor("DMO/Q1", new QueueZoneGeometry("Q1", new HashSet<string> { "Q1 entry" }, new HashSet<string> { "Q1 exit" },
            new HashSet<string>(), new HashSet<string>()), 7).Capture();
        ZoneOutputs Live(params QueueLiveMinute[] rows) => new("DMO/Q1", [], [], rows, []);

        // Insert, then a later checkpoint of the same minutes (the ON CONFLICT path) and a minute in another chunk.
        await store.SaveAsync(new StreamCheckpoint("g-rt", [Live(
            new QueueLiveMinute("DMO/Q1", minute, 61, true, false, 15.25, 4.0, null, false) { Shadow = new ShadowNowcast(17.5, null, true) },
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(1), 61, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(9.5, null, false) })],
            [state], [], [new StreamOffset("t", 0, 1)]), Ct);
        await store.SaveAsync(new StreamCheckpoint("g-rt", [Live(
            new QueueLiveMinute("DMO/Q1", minute, 62, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(null, NoServiceReason.NothingOpen, false) },
            new QueueLiveMinute("DMO/Q1", minute.AddMinutes(1), 62, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(8.25, null, true) },
            new QueueLiveMinute("DMO/Q1", minute.AddDays(30), 62, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(1.5, null, true, 1.75) })],
            [state], [], [new StreamOffset("t", 0, 2)]), Ct);
        await store.SaveAsync(new StreamCheckpoint("g-rt", [Live(
            new QueueLiveMinute("DMO/Q1", minute.AddDays(30), 62, true, false, 15.5, 4.0, null, false) { Shadow = new ShadowNowcast(2.5, null, false, 1.25) })],
            [state], [], [new StreamOffset("t", 0, 3)]), Ct);
        (await RowsAsync(database, ShadowRows)).Should().Equal(
            "DMO/Q1|09/28/2026 18:05:00|null|NothingOpen|False|null",
            "DMO/Q1|09/28/2026 18:06:00|8.25|null|True|null",
            "DMO/Q1|10/28/2026 18:05:00|2.5|null|False|1.25");

        var chunk = (await RowsAsync(database, "SELECT format('%I.%I', chunk_schema, chunk_name) FROM timescaledb_information.chunks WHERE hypertable_name = 'queue_minute_shadow' ORDER BY range_start LIMIT 1"))[0];
        await using var runtime = new NpgsqlConnection(postgres.ConnectionString(database, login, password));
        await runtime.OpenAsync(Ct);
        async Task<string> RunAsync(string sql)
        {
#pragma warning disable CA2100 // the statements are this test's literals and the chunk name read from the catalog
            await using var command = new NpgsqlCommand(sql, runtime);
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(Ct))
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), System.Globalization.CultureInfo.InvariantCulture))));
            return string.Join(";", rows);
        }

        foreach (var read in new[]
                 {
                     "SELECT nowcast_minutes FROM queue_minute_shadow",
                     "SELECT no_service FROM queue_minute_shadow",
                     "SELECT nowcast_degraded FROM queue_minute_shadow",
                     "SELECT updated_on FROM queue_minute_shadow",
                     // ARV-117b: the sensor cycle time (script 0045) is a value column like the others.
                     "SELECT sensor_cycle_minutes FROM queue_minute_shadow",
                     "SELECT zone_key FROM queue_minute_shadow WHERE sensor_cycle_minutes > 1",
                     "SELECT zone_key FROM queue_minute_shadow WHERE sensor_cycle_minutes IS NULL",
                     "SELECT max(sensor_cycle_minutes) FROM queue_minute_shadow",
                     "UPDATE queue_minute_shadow SET sensor_cycle_minutes = sensor_cycle_minutes * 2 WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET sensor_cycle_minutes = 1 WHERE zone_key = 'DMO/Q1' RETURNING sensor_cycle_minutes",
                     "UPDATE queue_minute_shadow SET nowcast_degraded = true WHERE sensor_cycle_minutes > 1",
                     "SELECT * FROM queue_minute_shadow",
                     "TABLE queue_minute_shadow",
                     "SELECT row_to_json(s) FROM queue_minute_shadow s",
                     "SELECT s FROM queue_minute_shadow s",
                     "SELECT zone_key FROM queue_minute_shadow WHERE nowcast_minutes > 5",
                     "SELECT zone_key FROM queue_minute_shadow ORDER BY no_service",
                     "UPDATE queue_minute_shadow SET nowcast_minutes = nowcast_minutes + 1 WHERE zone_key = 'DMO/Q1'",
                     "UPDATE queue_minute_shadow SET nowcast_degraded = true WHERE zone_key = 'DMO/Q1' RETURNING nowcast_minutes",
                     "INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, nowcast_degraded, updated_on) VALUES ('DMO/Q1', '2026-09-28T18:05:00Z', 1, true, now()) ON CONFLICT (zone_key, minute_utc) DO UPDATE SET nowcast_minutes = EXCLUDED.nowcast_minutes",
                     "INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, nowcast_degraded, updated_on) VALUES ('DMO/Q9', now(), 1, true, now()) RETURNING *",
                     "COPY queue_minute_shadow TO STDOUT",
                     "COPY (SELECT nowcast_minutes FROM queue_minute_shadow) TO STDOUT",
                     $"SELECT nowcast_minutes FROM {chunk}",
                     $"SELECT * FROM {chunk}",
                     "DELETE FROM queue_minute_shadow WHERE zone_key = 'DMO/Q1'",
                     "TRUNCATE queue_minute_shadow",
                     "UPDATE queue_minute_shadow SET zone_key = 'DMO/Q2' WHERE zone_key = 'DMO/Q1'"
                 })
        {
            var act = () => RunAsync(read);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", "the runtime login cannot: {0}", read);
        }

        // The key columns only: what the upsert's conflict target needs, no shadow value.
        (await RunAsync("SELECT count(*), min(zone_key) FROM queue_minute_shadow")).Should().Be("3|DMO/Q1");
        (await RunAsync("SELECT has_column_privilege('queue_minute_shadow', 'nowcast_minutes', 'SELECT'), has_table_privilege('queue_minute_shadow', 'SELECT'), " +
                        "has_table_privilege('queue_minute_shadow', 'INSERT'), has_column_privilege('queue_minute_shadow', 'nowcast_minutes', 'UPDATE'), " +
                        "has_column_privilege('queue_minute_shadow', 'zone_key', 'UPDATE'), pg_has_role('ariva_validation_reader', 'MEMBER'), " +
                        "has_column_privilege('queue_minute_shadow', 'sensor_cycle_minutes', 'SELECT'), has_column_privilege('queue_minute_shadow', 'sensor_cycle_minutes', 'UPDATE')"))
            .Should().Be("False|False|True|True|False|False|False|True");

        // The validation comparison's read role (ARV-104g1 grants it to the validation reader login) reads every value.
        await using var reader = new NpgsqlConnection(postgres.ConnectionString(database));
        await reader.OpenAsync(Ct);
        await using (var role = new NpgsqlCommand("SET ROLE ariva_validation_reader", reader))
            await role.ExecuteNonQueryAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FILTER (WHERE nowcast_degraded IS NOT NULL) + count(sensor_cycle_minutes) FROM queue_minute_shadow", reader);
        (await count.ExecuteScalarAsync(Ct)).Should().Be(4L, "three shadows, one with a sensor cycle time");
        await using var other = new NpgsqlCommand("SELECT count(*) FROM queue_minute", reader);
        var denied = () => other.ExecuteScalarAsync(Ct);
        (await denied.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", "the read role reads the shadow only");
    }

    // ARV-117a security review: script 0043 on a database that has 0041's shadows. Scripts up to 0042, then queue_minute rows
    // with a shadow number, with a shadow reason (in another chunk) and with no shadow, then 0043: exactly the two shadows are
    // copied with their values, flags and updated_on, the minute without a shadow has no row, every queue_minute row stays,
    // and no shadow_* column or check is left on queue_minute. The remaining scripts then apply on top.
    [Fact]
    public async Task Script0043_Should_CopyEveryShadowOf0041AndDropItsColumns_When_QueueMinuteHoldsShadows()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.StreamShadowCopy);
        var scripts = SqlScriptCatalog.Embedded();
        var runner = new SqlScriptRunner(postgres.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance);
        await runner.ApplyAsync([.. scripts.Where(s => s.Number <= 42)], Ct);
        await RowsAsync(database, """
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, nowcast_minutes, no_service, nowcast_degraded, updated_on,
                                      shadow_nowcast_minutes, shadow_no_service, shadow_nowcast_degraded) VALUES
                ('DMO/Q1', '2026-09-28T18:05:00Z', 7, 15.25, NULL, false, '2026-09-28T18:05:41.123456Z', 17.375, NULL, true),
                ('DMO/Q1', '2026-09-28T18:06:00Z', 7, 15.5, NULL, false, '2026-09-28T18:06:42.654321Z', NULL, NULL, NULL),
                ('DMO/Q2', '2026-10-28T18:05:00Z', 7, NULL, 'NothingOpen', false, '2026-10-28T18:05:43.000001Z', NULL, 'NoThroughputData', false)
            """);

        (await runner.ApplyAsync([.. scripts.Where(s => s.Number <= 43)], Ct)).Should().Equal("0043_queue_minute_shadow.sql");

        const string Copied = """
            SELECT zone_key, to_char(minute_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI'), nowcast_minutes, no_service, nowcast_degraded,
                   to_char(updated_on AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US')
            FROM queue_minute_shadow ORDER BY zone_key, minute_utc
            """;
        (await RowsAsync(database, Copied)).Should().Equal(
            "DMO/Q1|2026-09-28T18:05|17.375|null|True|2026-09-28T18:05:41.123456",
            "DMO/Q2|2026-10-28T18:05|null|NoThroughputData|False|2026-10-28T18:05:43.000001");
        (await RowsAsync(database, """
            SELECT count(*) FROM queue_minute_shadow s JOIN queue_minute q USING (zone_key, minute_utc) WHERE s.updated_on = q.updated_on
            UNION ALL SELECT count(*) FROM queue_minute
            UNION ALL SELECT count(*) FROM queue_minute_shadow WHERE minute_utc = '2026-09-28T18:06:00Z'
            UNION ALL SELECT count(*) FROM information_schema.columns WHERE table_name = 'queue_minute' AND column_name LIKE 'shadow%'
            UNION ALL SELECT count(*) FROM pg_constraint WHERE conrelid = 'queue_minute'::regclass AND conname LIKE '%shadow%'
            """)).Should().Equal(["2", "3", "0", "0", "0"],
            "updated_on as the source rows, every queue_minute row kept, no row for the minute without a shadow, no shadow column or check left");

        (await runner.ApplyAsync(scripts, Ct)).Should().NotBeEmpty("the later scripts apply on top");
    }

    // ARV-117a security review (CWE-269): the validation comparison's read role has no attribute and no privilege beyond
    // reading the shadow. Checked in the catalogs (pg_roles, pg_auth_members, the ACLs of relations, columns, schemas, the
    // database, functions and default privileges) and in information_schema.role_table_grants.
    [Fact]
    public async Task ValidationReader_Should_HoldNothingButSelectOnTheShadow_When_TheMigrationsHaveRun()
    {
        var database = await StoreDatabaseAsync();
        (await RowsAsync(database, """
            SELECT rolcanlogin, rolsuper, rolcreaterole, rolcreatedb, rolbypassrls, rolreplication FROM pg_roles WHERE rolname = 'ariva_validation_reader'
            """)).Should().Equal(["False|False|False|False|False|False"], "NOLOGIN, no superuser, createrole, createdb, bypassrls or replication");
        (await RowsAsync(database, """
            SELECT count(*) FROM pg_auth_members WHERE member = 'ariva_validation_reader'::regrole
            """)).Should().Equal(["0"], "a member of no other role");
        (await RowsAsync(database, """
            SELECT DISTINCT privilege_type,
                   CASE WHEN format('%I.%I', table_schema, table_name)::regclass = 'queue_minute_shadow'::regclass
                          OR format('%I.%I', table_schema, table_name)::regclass IN (SELECT inhrelid FROM pg_inherits WHERE inhparent = 'queue_minute_shadow'::regclass)
                        THEN 'queue_minute_shadow' ELSE table_schema || '.' || table_name END
            FROM information_schema.role_table_grants WHERE grantee = 'ariva_validation_reader'
            """)).Should().Equal(["SELECT|queue_minute_shadow"], "SELECT on the shadow (and its chunks) and no other table privilege");
        (await RowsAsync(database, """
            SELECT 'relation ' || CASE WHEN c.oid = 'queue_minute_shadow'::regclass OR c.oid IN (SELECT inhrelid FROM pg_inherits WHERE inhparent = 'queue_minute_shadow'::regclass)
                                       THEN 'queue_minute_shadow' ELSE c.oid::regclass::text END || ' ' || a.privilege_type AS grant_
              FROM pg_class c, aclexplode(c.relacl) a WHERE a.grantee = 'ariva_validation_reader'::regrole
            UNION SELECT 'column ' || t.attrelid::regclass::text || '.' || t.attname || ' ' || a.privilege_type FROM pg_attribute t, aclexplode(t.attacl) a WHERE a.grantee = 'ariva_validation_reader'::regrole
            UNION SELECT 'schema ' || n.nspname || ' ' || a.privilege_type FROM pg_namespace n, aclexplode(n.nspacl) a WHERE a.grantee = 'ariva_validation_reader'::regrole
            UNION SELECT 'database ' || d.datname || ' ' || a.privilege_type FROM pg_database d, aclexplode(d.datacl) a WHERE a.grantee = 'ariva_validation_reader'::regrole
            UNION SELECT 'function ' || p.oid::regprocedure::text || ' ' || a.privilege_type FROM pg_proc p, aclexplode(p.proacl) a WHERE a.grantee = 'ariva_validation_reader'::regrole
            UNION SELECT 'default ' || x.defaclobjtype::text || ' ' || a.privilege_type FROM pg_default_acl x, aclexplode(x.defaclacl) a WHERE a.grantee = 'ariva_validation_reader'::regrole
            ORDER BY 1
            """)).Should().Equal(["relation queue_minute_shadow SELECT", "schema public USAGE"], "SELECT on the shadow and USAGE on schema public, nothing else");
        (await RowsAsync(database, """
            SELECT has_table_privilege('ariva_validation_reader', 'queue_minute', 'SELECT'), has_schema_privilege('ariva_validation_reader', 'public', 'CREATE'),
                   has_table_privilege('ariva_validation_reader', 'queue_minute_shadow', 'INSERT, UPDATE, DELETE, TRUNCATE')
            """)).Should().Equal(["False|False|False"]);
    }

    // ARV-117a security review (CWE-862, CWE-863), accepted residual (docs/product/decisions.md): a runtime session can learn
    // one bit per minute, whether the shadow holds a number or a reason, with an UPDATE by key that trips
    // ck_queue_minute_shadow_one (23514) and is rolled back. Pinned here: the probe yields 23514 exactly when the other one is
    // held, and neither the message nor the row detail (asked for with Include Error Detail) carries the number, the
    // reason or the flag; the probe that succeeds is rolled back and changes nothing.
    [Fact]
    public async Task RuntimeLogin_Should_LearnOnlyWhetherANumberIsHeld_When_ItProbesTheShadowCheck()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.StreamShadowProbe);
        await new SqlScriptRunner(postgres.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        const string login = "it_runtime_shadow_probe";
        var password = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
            ensure.Parameters.AddWithValue("login", login);
            ensure.Parameters.AddWithValue("password", password);
            await ensure.ExecuteNonQueryAsync(Ct);
        }

        await RowsAsync(database, """
            INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, no_service, nowcast_degraded, updated_on) VALUES
                ('DMO/Q1', '2026-09-28T18:05:00Z', 17.375, NULL, true, now()),
                ('DMO/Q1', '2026-09-28T18:06:00Z', NULL, 'NoThroughputData', true, now())
            """);
        var before = await RowsAsync(database, ShadowRows);

        await using var runtime = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(postgres.ConnectionString(database, login, password)) { IncludeErrorDetail = true }.ConnectionString);
        await runtime.OpenAsync(Ct);
        async Task<int> ProbeAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
        {
            await using var transaction = await runtime.BeginTransactionAsync(Ct);
            try
            {
#pragma warning disable CA2100 // the probes are this test's literals
                await using var command = new NpgsqlCommand(sql, runtime, transaction);
#pragma warning restore CA2100
                return await command.ExecuteNonQueryAsync(Ct);
            }
            finally
            {
                await transaction.RollbackAsync(Ct);
            }
        }

        // A number is held: setting a reason trips the check, and nothing of the number, the reason or the flag comes back.
        var onNumber = () => ProbeAsync("UPDATE queue_minute_shadow SET no_service = 'NothingOpen' WHERE zone_key = 'DMO/Q1' AND minute_utc = '2026-09-28T18:05:00Z'");
        var numberError = (await onNumber.Should().ThrowAsync<PostgresException>()).Which;
        numberError.SqlState.Should().Be("23514");
        numberError.ConstraintName.Should().Be("ck_queue_minute_shadow_one");
        numberError.Detail.Should().NotBeNull("the server's row detail is asked for, so its content is what is checked");
        // The row detail lists the key columns and the column the statement set, with the prober's own value only.
        numberError.Detail.Should().StartWith("Failing row contains (zone_key, minute_utc, no_service) = (DMO/Q1, ").And.EndWith(", NothingOpen).");
        foreach (var text in new[] { numberError.MessageText, numberError.Detail, numberError.ToString() })
            text.Should().NotContain("17.375").And.NotContain("17,375").And.NotContain("nowcast_minutes").And.NotContain("nowcast_degraded");

        // A reason is held: the same probe passes (the one bit), is rolled back, and the opposite probe trips the check
        // without revealing the reason.
        (await ProbeAsync("UPDATE queue_minute_shadow SET no_service = 'NothingOpen' WHERE zone_key = 'DMO/Q1' AND minute_utc = '2026-09-28T18:06:00Z'")).Should().Be(1);
        var onReason = () => ProbeAsync("UPDATE queue_minute_shadow SET nowcast_minutes = 1 WHERE zone_key = 'DMO/Q1' AND minute_utc = '2026-09-28T18:06:00Z'");
        var reasonError = (await onReason.Should().ThrowAsync<PostgresException>()).Which;
        reasonError.SqlState.Should().Be("23514");
        reasonError.Detail.Should().StartWith("Failing row contains (zone_key, minute_utc, nowcast_minutes) = (DMO/Q1, ").And.EndWith(", 1).");
        foreach (var text in new[] { reasonError.MessageText, reasonError.Detail, reasonError.ToString() })
            text.Should().NotContain("NoThroughputData").And.NotContain("no_service").And.NotContain("nowcast_degraded");

        // ARV-117b: the sensor cycle time's check (script 0045) names that column alone, so a probe on it learns nothing
        // about the stored row: a value inside the bounds passes on a number and on a reason alike, one outside fails on both
        // with the prober's own value only.
        (await ProbeAsync("UPDATE queue_minute_shadow SET sensor_cycle_minutes = 1.5 WHERE zone_key = 'DMO/Q1' AND minute_utc = '2026-09-28T18:05:00Z'")).Should().Be(1);
        (await ProbeAsync("UPDATE queue_minute_shadow SET sensor_cycle_minutes = 1.5 WHERE zone_key = 'DMO/Q1' AND minute_utc = '2026-09-28T18:06:00Z'")).Should().Be(1);
        foreach (var outside in new Func<Task<int>>[]
                 {
                     () => ProbeAsync("UPDATE queue_minute_shadow SET sensor_cycle_minutes = 99 WHERE zone_key = 'DMO/Q1' AND minute_utc = '2026-09-28T18:05:00Z'"),
                     () => ProbeAsync("UPDATE queue_minute_shadow SET sensor_cycle_minutes = 99 WHERE zone_key = 'DMO/Q1' AND minute_utc = '2026-09-28T18:06:00Z'")
                 })
        {
            var cycleError = (await outside.Should().ThrowAsync<PostgresException>()).Which;
            cycleError.ConstraintName.Should().Be("ck_queue_minute_shadow_cycle");
            cycleError.Detail.Should().StartWith("Failing row contains (zone_key, minute_utc, sensor_cycle_minutes) = (DMO/Q1, ").And.EndWith(", 99).");
            foreach (var text in new[] { cycleError.MessageText, cycleError.Detail, cycleError.ToString() })
                text.Should().NotContain("17.375").And.NotContain("NoThroughputData").And.NotContain("nowcast_minutes").And.NotContain("no_service");
        }

        (await RowsAsync(database, ShadowRows)).Should().Equal(before, "every probe was rolled back");
    }

    // ARV-117 reviews and ARV-117a (CWE-862, CWE-863): no database object reads the shadow table or the sensor-only desk
    // minutes. The runtime role cannot create views, functions, triggers or publications (0001), but a migration could,
    // and an object runs with its owner's rights, so every view, materialized view, continuous aggregate or rule (through
    // pg_rewrite) and every SQL-standard function (through pg_proc) that depends on either table or one of its chunks, at
    // any column or the whole row (refobjsubid 0), is listed and compared with an exact allowlist, empty today: a new
    // dependent must change this test on purpose (and, if it reads the shadow, bring its own authorization). Also no
    // user trigger and no publication over either table (the third ARV-117 review's optional hardening). queue_minute
    // itself no longer holds the shadow (script 0043 dropped its columns), so it is no longer listed here.
    // ARV-104g1 (changed on purpose): the shadow now has a read path, and it is not a database object: the validation service
    // reads the table as a client through the validation reader login (script 0049), so the allowlist of dependents stays
    // empty (a view or function over the table would run with its owner's rights and bypass that login). What the read path
    // adds is a grantee, so the test now also lists every role of this database's ACLs that may read a value of the table
    // (table-level or column-level SELECT, its chunks included, the owner and the runtime role's key columns aside) and compares
    // it with an exact allowlist: ariva_validation_reader alone, which only the reader login holds.
    private const string ShadowDependents = """
        SELECT label FROM (
            SELECT DISTINCT CASE WHEN x.kind = 'function' THEN 'function ' || x.nspname || '.' || x.relname ELSE x.nspname || '.' || x.relname END
                   || CASE WHEN x.rulename IS NOT NULL AND x.rulename <> '_RETURN' THEN ' rule ' || x.rulename ELSE '' END
                   || ' on ' || x.target AS label
            FROM (
                SELECT n.nspname, c.relname, r.rulename, 'rule' AS kind, t.target
                FROM pg_depend d
                JOIN pg_rewrite r ON r.oid = d.objid
                JOIN pg_class c ON c.oid = r.ev_class
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN (SELECT 'queue_minute_shadow' AS target, 'queue_minute_shadow'::regclass AS oid
                      UNION ALL SELECT 'queue_minute_shadow', inhrelid FROM pg_inherits WHERE inhparent = 'queue_minute_shadow'::regclass
                      UNION ALL SELECT 'desk_sensor_minute', 'desk_sensor_minute'::regclass
                      UNION ALL SELECT 'desk_sensor_minute', inhrelid FROM pg_inherits WHERE inhparent = 'desk_sensor_minute'::regclass) t ON t.oid = d.refobjid
                WHERE d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_class'::regclass
                UNION
                SELECT n.nspname, p.proname, NULL, 'function', t.target
                FROM pg_depend d
                JOIN pg_proc p ON p.oid = d.objid
                JOIN pg_namespace n ON n.oid = p.pronamespace
                JOIN (SELECT 'queue_minute_shadow' AS target, 'queue_minute_shadow'::regclass AS oid
                      UNION ALL SELECT 'queue_minute_shadow', inhrelid FROM pg_inherits WHERE inhparent = 'queue_minute_shadow'::regclass
                      UNION ALL SELECT 'desk_sensor_minute', 'desk_sensor_minute'::regclass
                      UNION ALL SELECT 'desk_sensor_minute', inhrelid FROM pg_inherits WHERE inhparent = 'desk_sensor_minute'::regclass) t ON t.oid = d.refobjid
                WHERE d.classid = 'pg_proc'::regclass AND d.refclassid = 'pg_class'::regclass
                UNION
                SELECT n.nspname, g.tgname, NULL, 'trigger', c.relname
                FROM pg_trigger g JOIN pg_class c ON c.oid = g.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE NOT g.tgisinternal AND g.tgname <> 'ts_insert_blocker' AND c.oid IN ('queue_minute_shadow'::regclass, 'desk_sensor_minute'::regclass)
                UNION
                SELECT 'publication', p.pubname, NULL, 'publication', r.prrelid::regclass::text
                FROM pg_publication_rel r JOIN pg_publication p ON p.oid = r.prpubid
                WHERE r.prrelid IN ('queue_minute_shadow'::regclass, 'desk_sensor_minute'::regclass)
                UNION
                SELECT 'publication', p.pubname, NULL, 'publication', 'every table' FROM pg_publication p WHERE p.puballtables
            ) x
        ) s
        ORDER BY label COLLATE "C"
        """;

    // Every dependent of the two tables after the migrations (pg17, TimescaleDB 2.30): none.
    private static readonly string[] AllowedShadowDependents = [];

    // ARV-104g1: every grantee (PUBLIC included) that may SELECT a value of queue_minute_shadow, on the table, one of its chunks
    // or a value column, apart from the owner; the runtime role's SELECT of the two key columns (the upsert's conflict target) is
    // not a value.
    private const string ShadowReaders = """
        SELECT grantee FROM (
        WITH t AS (
            SELECT c.oid, c.relacl, c.relowner FROM pg_class c
             WHERE c.oid = 'queue_minute_shadow'::regclass OR c.oid IN (SELECT inhrelid FROM pg_inherits WHERE inhparent = 'queue_minute_shadow'::regclass)
        )
        SELECT DISTINCT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END AS grantee
          FROM t, aclexplode(t.relacl) a
         WHERE a.privilege_type = 'SELECT' AND a.grantee <> t.relowner
        UNION
        SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee)::text END
          FROM t JOIN pg_attribute att ON att.attrelid = t.oid, aclexplode(att.attacl) a
         WHERE att.attnum > 0 AND att.attname NOT IN ('zone_key', 'minute_utc') AND a.privilege_type = 'SELECT' AND a.grantee <> t.relowner
        ) g
        ORDER BY grantee COLLATE "C"
        """;

    // The one read path (ARV-104g1): the reader role, granted to the validation reader login only.
    private static readonly string[] AllowedShadowReaders = ["ariva_validation_reader"];

    [Fact]
    public async Task Migrations_Should_LeaveNoReadPathOverTheShadowOrTheSensorOnlyDeskMinutesButTheReaderRole_When_Applied()
    {
        var database = await DatabaseAsync(TestDatabase.StreamShadowViews);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);

        async Task<List<string>> DependentsAsync(NpgsqlTransaction transaction = null)
        {
            await using var command = new NpgsqlCommand(ShadowDependents, connection, transaction);
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
                names.Add(reader.GetString(0));
            return names;
        }

        async Task<List<string>> ReadersAsync(NpgsqlTransaction transaction = null)
        {
            await using var command = new NpgsqlCommand(ShadowReaders, connection, transaction);
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
                names.Add(reader.GetString(0));
            return names;
        }

        (await DependentsAsync()).Should().Equal(AllowedShadowDependents,
            "no view, materialized view, continuous aggregate, rule, SQL function, trigger or publication reads the shadow or the sensor-only desk minutes");
        (await ReadersAsync()).Should().Equal(AllowedShadowReaders, "only the reader role may read a value of the shadow (ARV-104g1)");
        await using (var roles = new NpgsqlCommand(
                         "SELECT pg_has_role('ariva_runtime', 'ariva_validation_reader', 'MEMBER') OR pg_has_role('ariva_migration', 'ariva_validation_reader', 'MEMBER')", connection))
            (await roles.ExecuteScalarAsync(Ct)).Should().Be(false, "neither the runtime nor the migration role holds the reader role");

        // The check bites, whatever the dependent reads: a wildcard view, a materialized view naming a shadow value, whole-row
        // reads (refobjsubid 0), a SQL-standard function, a view over one of the hypertable's chunks, a trigger and a
        // publication are all caught, then rolled back.
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            foreach (var ddl in new[]
                     {
                         "CREATE VIEW it_shadow_leak AS SELECT * FROM queue_minute_shadow",
                         "CREATE MATERIALIZED VIEW it_shadow_leak_m AS SELECT zone_key, max(nowcast_minutes) AS m FROM queue_minute_shadow GROUP BY zone_key WITH NO DATA",
                         "CREATE VIEW it_shadow_row_to_json AS SELECT row_to_json(q) AS r FROM queue_minute_shadow q",
                         "CREATE VIEW it_shadow_whole_row AS SELECT q FROM queue_minute_shadow q",
                         "CREATE FUNCTION it_shadow_function() RETURNS json LANGUAGE sql STABLE RETURN (SELECT json_agg(q) FROM queue_minute_shadow q)",
                         "CREATE VIEW it_desk_sensor AS SELECT desk_code, idle_seconds FROM desk_sensor_minute",
                         // ARV-117b: the sensor cycle time (script 0045) alone.
                         "CREATE VIEW it_shadow_cycle AS SELECT zone_key, sensor_cycle_minutes FROM queue_minute_shadow",
                         "CREATE FUNCTION it_shadow_cycle_function() RETURNS double precision LANGUAGE sql STABLE RETURN (SELECT avg(sensor_cycle_minutes) FROM queue_minute_shadow)",
                         "INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, nowcast_degraded, updated_on) VALUES ('DMO/IT', '2026-09-28T18:05:00Z', 1, true, now())",
                         "DO $$ BEGIN EXECUTE format('CREATE VIEW it_shadow_chunk AS SELECT to_jsonb(c) AS r FROM %s c', (SELECT show_chunks('queue_minute_shadow') LIMIT 1)); END $$",
                         "CREATE FUNCTION it_copy() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN RETURN NEW; END $f$",
                         "CREATE TRIGGER it_shadow_trigger AFTER INSERT ON queue_minute_shadow FOR EACH ROW EXECUTE FUNCTION it_copy()",
                         "CREATE PUBLICATION it_shadow_publication FOR TABLE desk_sensor_minute",
                         // ARV-104g1: grantees that could read a shadow value: a value column for the runtime role, a new role on the
                         // table, PUBLIC on a chunk. All caught, then rolled back (the role too).
                         "GRANT SELECT (nowcast_minutes) ON queue_minute_shadow TO ariva_runtime",
                         "CREATE ROLE it_shadow_peek NOLOGIN",
                         "GRANT SELECT ON queue_minute_shadow TO it_shadow_peek",
                         "DO $$ BEGIN EXECUTE format('GRANT SELECT ON %s TO PUBLIC', (SELECT show_chunks('queue_minute_shadow') LIMIT 1)); END $$"
                     })
            {
#pragma warning disable CA2100 // literal DDL above
                await using var create = new NpgsqlCommand(ddl, connection, transaction);
#pragma warning restore CA2100
                await create.ExecuteNonQueryAsync(Ct);
            }

            (await DependentsAsync(transaction)).Should().Equal(
            [
                "function public.it_shadow_cycle_function on queue_minute_shadow", "function public.it_shadow_function on queue_minute_shadow",
                "public.it_desk_sensor on desk_sensor_minute", "public.it_shadow_chunk on queue_minute_shadow", "public.it_shadow_cycle on queue_minute_shadow",
                "public.it_shadow_leak on queue_minute_shadow", "public.it_shadow_leak_m on queue_minute_shadow", "public.it_shadow_row_to_json on queue_minute_shadow",
                "public.it_shadow_trigger on queue_minute_shadow", "public.it_shadow_whole_row on queue_minute_shadow", "publication.it_shadow_publication on desk_sensor_minute"
            ]);
            (await ReadersAsync(transaction)).Should().Equal(["PUBLIC", "ariva_runtime", "ariva_validation_reader", "it_shadow_peek"]);
            await transaction.RollbackAsync(Ct);
        }

        (await DependentsAsync()).Should().Equal(AllowedShadowDependents);
        (await ReadersAsync()).Should().Equal(AllowedShadowReaders);
    }

    [Fact]
    public async Task Store_Should_KeepTheFirstDeskReadingAndDropWhatTheTableWouldRefuse_When_ACheckpointCarriesDeskReadings()
    {
        // ARV-116: a reading is written once (a repeated checkpoint keeps the row and the id the desk feed may have taken);
        // a reading the table would refuse is dropped with one warning per zone that names no desk, and the checkpoint commits.
        var database = await DatabaseAsync(TestDatabase.StreamDeskReadings);
        var log = new WarningLog();
        var store = new StreamStore(new DatabaseSettings
        {
            Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = postgres.AdminUsername, Password = postgres.AdminPassword
        }, TimeProvider.System, log);
        // Dated now: the table's 7-day retention job drops older chunks as soon as it first runs.
        var at = DateTime.UtcNow.AddMinutes(-5);
        at = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        ZoneOutputs Readings(string zone, params DeskZoneSample[] samples) => new(zone, [], [], [], [], [], [], [], [], [], samples);
        var good = Readings("DMO/Q1", new DeskZoneSample("DMO/IMM/D1", DeskSource.StaffZone, at, 1, false), new DeskZoneSample("DMO/IMM/D1", DeskSource.ServiceZone, at, 0, true),
            new DeskZoneSample("DMO/IMM/D1", DeskSource.StaffZone, at, 0, false));
        var state = new ZoneProcessor("DMO/Q1", new QueueZoneGeometry("Q1", new HashSet<string> { "Q1 entry" }, new HashSet<string> { "Q1 exit" },
            new HashSet<string>(), new HashSet<string>()), 7).Capture();

        await store.SaveAsync(new StreamCheckpoint("g-desks", [good], [state], [], [new StreamOffset("t", 0, 1)]), Ct);
        var first = await RowsAsync(database, "SELECT id, desk_code, source, occupancy, degraded, profile_version FROM desk_zone_reading ORDER BY source");
        await store.SaveAsync(new StreamCheckpoint("g-desks", [good with { DeskReadings = [new DeskZoneSample("DMO/IMM/D1", DeskSource.StaffZone, at, 0, true)] }], [state], [],
            [new StreamOffset("t", 0, 2)]), Ct);

        first.Should().HaveCount(2).And.Contain(r => r.EndsWith("|DMO/IMM/D1|StaffZone|1|False|7", StringComparison.Ordinal), "the first reading of a key in a checkpoint is kept");
        (await RowsAsync(database, "SELECT id, desk_code, source, occupancy, degraded, profile_version FROM desk_zone_reading ORDER BY source")).Should().Equal(first,
            "a repeated checkpoint adds nothing and changes nothing");

        var overlong = "DMO/IMM/" + new string('D', 57);
        await store.SaveAsync(new StreamCheckpoint("g-desks",
        [
            Readings("DMO/Q1", new DeskZoneSample("XS2/IMM/V1", DeskSource.StaffZone, at, 1, false), new DeskZoneSample(overlong, DeskSource.StaffZone, at, 1, false),
                new DeskZoneSample("DMO/", DeskSource.StaffZone, at, 1, false), new DeskZoneSample("DMO/IMM/D1", DeskSource.Session, at.AddSeconds(1), 1, false),
                new DeskZoneSample("DMO/IMM/D1", DeskSource.StaffZone, at.AddSeconds(1), 51, false), new DeskZoneSample("DMO/IMM/D1", DeskSource.StaffZone, at.AddSeconds(1), -1, false)),
            Readings("Q1", new DeskZoneSample("Q1/IMM/D1", DeskSource.StaffZone, at, 1, false)),
            Readings("DMO/Q2", new DeskZoneSample("DMO/IMM/D2", DeskSource.ServiceZone, at, 1, false))
        ], [], [], [new StreamOffset("t", 0, 3)]), Ct);

        overlong.Length.Should().Be(65);
        (await store.LoadOffsetsAsync("g-desks", [("t", 0)], Ct)).Should().Equal([new StreamOffset("t", 0, 3)], "the checkpoint committed");
        (await RowsAsync(database, "SELECT desk_code, source, occupancy FROM desk_zone_reading ORDER BY desk_code, source")).Should().Equal(
            "DMO/IMM/D1|ServiceZone|0", "DMO/IMM/D1|StaffZone|1", "DMO/IMM/D2|ServiceZone|1");
        log.Warnings.Should().Equal(
            "Dropped 6 desk zone readings of zone DMO/Q1: a desk key not of the zone's site or too long, or a role or count out of bounds",
            "Dropped 1 desk zone readings of zone Q1: a desk key not of the zone's site or too long, or a role or count out of bounds");
        (await RowsAsync(database, "SELECT has_table_privilege('ariva_runtime', 'desk_zone_reading', 'INSERT'), has_table_privilege('ariva_runtime', 'desk_zone_reading', 'SELECT')"))
            .Should().Equal("True|True");
    }

    [Fact]
    public async Task Store_Should_DropWhatTheTablesWouldRefuseAndCommit_When_ACheckpointCarriesInvalidOverflowRows()
    {
        var database = await DatabaseAsync(TestDatabase.StreamOverflowDrops);
        var log = new WarningLog();
        var store = new StreamStore(new DatabaseSettings
        {
            Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = postgres.AdminUsername, Password = postgres.AdminPassword
        }, TimeProvider.System, log);
        var minute = new DateTime(2026, 9, 28, 19, 10, 0, DateTimeKind.Utc);
        var longKey = "DMO-TERMINAL-T1AB/" + new string('Q', 200);
        var overlong = new string('b', 201);
        var checkpoint = new StreamCheckpoint("g-overflow",
        [
            new ZoneOutputs(longKey, [], [], [], [], [], [], [],
                [new OverflowMinute("Q band", minute, 0, 5)],
                [new OverflowChange("Q band", minute, OverflowChangeKind.Occupied, 5, null)]),
            new ZoneOutputs("DMO/Q1", [], [], [], [], [], [], [],
            [
                new OverflowMinute("", minute, 0, 1), new OverflowMinute(overlong, minute, 0, 1), new OverflowMinute("Q1 band", minute, 4, 2),
                new OverflowMinute("Q1 band", minute, -1, 2), new OverflowMinute("Q1 band", minute, 0, CanonicalEventRules.MaxOccupancy + 1)
            ],
            [
                new OverflowChange("", minute, OverflowChangeKind.Occupied, 1, null), new OverflowChange(overlong, minute, OverflowChangeKind.Occupied, 1, null),
                new OverflowChange("Q1 band", minute, OverflowChangeKind.Occupied, CanonicalEventRules.MaxOccupancy + 1, null)
            ]),
            new ZoneOutputs("Q1", [], [], [], [], [], [], [], [], [new OverflowChange("Q1 band", minute, OverflowChangeKind.Unknown, 0, null)]),
            new ZoneOutputs("/Q1", [], [], [], [], [], [], [], [], [new OverflowChange("Q1 band", minute, OverflowChangeKind.Emptied, 0, null)])
        ], [], [], [new StreamOffset("t", 0, 42)]);

        await store.SaveAsync(checkpoint, Ct);

        longKey.Length.Should().Be(218).And.BeGreaterThan(Ariva.Infra.Messaging.Outbox.OutboxLimits.MaxMessageKeyLength);
        (await store.LoadOffsetsAsync("g-overflow", [("t", 0)], Ct)).Should().Equal([new StreamOffset("t", 0, 42)], "the checkpoint committed");
        (await RowsAsync(database, "SELECT length(zone_key), band_name, min_occupancy, max_occupancy FROM overflow_minute ORDER BY zone_key"))
            .Should().Equal("218|Q band|0|5");
        (await RowsAsync(database, "SELECT count(*) FROM outbox_message")).Should().Equal("0");
        log.Warnings.Should().Equal(
            "Dropped 5 overflow minutes of zone DMO/Q1: empty or overlong band name, or occupancy out of order or bounds",
            $"Dropped 1 overflow changes of zone {longKey}: the zone key (as an outbox key) or band name is not valid, or the peak is out of bounds",
            "Dropped 3 overflow changes of zone DMO/Q1: the zone key (as an outbox key) or band name is not valid, or the peak is out of bounds",
            "Dropped 1 overflow changes of zone Q1: the zone key (as an outbox key) or band name is not valid, or the peak is out of bounds",
            "Dropped 1 overflow changes of zone /Q1: the zone key (as an outbox key) or band name is not valid, or the peak is out of bounds");
        log.Warnings.Should().NotContain(w => w.Contains("Q band", StringComparison.Ordinal) || w.Contains("Q1 band", StringComparison.Ordinal) || w.Contains(overlong, StringComparison.Ordinal),
            "a warning never carries a band name");
    }

    [Theory]
    [InlineData("INSERT INTO queue_minute_15m (zone_key, bucket_utc, entries) VALUES ('DMO/Q1', now(), 100000)", "42501")]
    [InlineData("DELETE FROM queue_minute_15m", "42501")]
    [InlineData("DELETE FROM queue_minute", "42501")]
    [InlineData("DELETE FROM line_minute", "42501")]
    [InlineData("TRUNCATE line_minute", "42501")]
    [InlineData("INSERT INTO line_minute_15m (zone_key, line_name, bucket_utc, crossings_in) VALUES ('DMO/Q1', 'Q1 entry', now(), 100000)", "42501")]
    [InlineData("UPDATE line_minute_15m SET crossings_in = 100000", "42501")]
    [InlineData("DELETE FROM line_minute_15m", "42501")]
    [InlineData("INSERT INTO line_minute (zone_key, line_name, line_role, source, minute_utc, profile_version, updated_on) VALUES ('DMO/Q1', 'x', 'Entry', 'Sensor', now(), 1, now())", "23514")]
    [InlineData("DELETE FROM queue_bin", "42501")]
    [InlineData("DELETE FROM stream_zone_state", "42501")]
    [InlineData("DELETE FROM stream_offset", "42501")]
    [InlineData("UPDATE queue_bin SET entries = 0 WHERE status = 'Final'", "23001")]
    [InlineData("DELETE FROM overflow_minute", "42501")]
    [InlineData("TRUNCATE overflow_minute", "42501")]
    [InlineData("INSERT INTO overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on) VALUES ('DMO/Q1', 'Q1 band', now(), 7, 5, 2, now())", "23514")]
    [InlineData("INSERT INTO overflow_minute (zone_key, band_name, minute_utc, profile_version, min_occupancy, max_occupancy, updated_on) VALUES ('DMO/Q1', 'Q1 band', now(), 7, -1, 2, now())", "23514")]
    // The bin view aggregates, so nobody can write through it (55000); the runtime role holds SELECT on it only (Store test).
    [InlineData("INSERT INTO overflow_bin_15m (zone_key, start_utc, overflow_minutes) VALUES ('DMO/Q1', now(), 15)", "55000")]
    [InlineData("DELETE FROM overflow_bin_15m", "55000")]
    [InlineData("UPDATE desk_zone_reading SET occupancy = 0", "42501")]
    [InlineData("DELETE FROM desk_zone_reading", "42501")]
    [InlineData("TRUNCATE desk_zone_reading", "42501")]
    [InlineData("INSERT INTO desk_zone_reading (id, site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version, written_utc) VALUES (gen_random_uuid(), 'DMO', 'DMO/IMM/X', 'StaffZone', now(), 51, false, 'DMO/Q1', 7, now())", "23514")]
    [InlineData("INSERT INTO desk_zone_reading (id, site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version, written_utc) VALUES (gen_random_uuid(), 'DMO', 'XS2/IMM/V1', 'StaffZone', now(), 1, false, 'DMO/Q1', 7, now())", "23514")]
    [InlineData("INSERT INTO desk_zone_reading (id, site_code, desk_code, source, reading_utc, occupancy, degraded, zone_key, profile_version, written_utc) VALUES (gen_random_uuid(), 'DMO', 'DMO/IMM/X', 'Session', now(), 1, false, 'DMO/Q1', 7, now())", "23514")]
    // ARV-117a: the sensor-only desk minutes (script 0044) and the shadow table (0043).
    [InlineData("DELETE FROM desk_sensor_minute", "42501")]
    [InlineData("TRUNCATE desk_sensor_minute", "42501")]
    [InlineData("INSERT INTO desk_sensor_minute (desk_code, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, degraded, updated_on) VALUES ('DMO/IMM/X', now(), 0, 61, 0, 0, 0, false, now())", "23514")]
    [InlineData("INSERT INTO desk_sensor_minute (desk_code, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, degraded, updated_on) VALUES ('DMO/IMM/X', now(), 30, 30, 30, 0, 0, false, now())", "23514")]
    [InlineData("INSERT INTO desk_sensor_minute (desk_code, minute_utc, closed_seconds, idle_seconds, serving_seconds, paused_seconds, unknown_seconds, degraded, updated_on) VALUES ('NOSITE', now(), 0, 60, 0, 0, 0, false, now())", "23514")]
    [InlineData("SELECT nowcast_minutes FROM queue_minute_shadow", "42501")]
    [InlineData("DELETE FROM queue_minute_shadow", "42501")]
    [InlineData("TRUNCATE queue_minute_shadow", "42501")]
    [InlineData("DELETE FROM zone_health_bin", "42501")]
    [InlineData("TRUNCATE zone_health_bin", "42501")]
    [InlineData("UPDATE zone_health_bin SET conservation_residual = 0, occupancy_start = 0, occupancy_end = 0 WHERE status = 'Final'", "23001")]
    [InlineData("INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, conservation_residual, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on) VALUES ('DMO/Q1', '2030-01-01', 1, 15, 'Provisional', 7, 1, 1, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, now())", "23514")]
    [InlineData("INSERT INTO zone_health_bin (zone_key, start_utc, revision, length_minutes, status, profile_version, entries, exits, tracks_entered, tracks_exited, tracks_abandoned, tracks_fragmented, tracks_censored, tracks_rejected, tracks_open, occupancy_minutes, capacity_minutes, minutes_outside_capacity, updated_on) VALUES ('DMO/Q1', '2030-01-01', 1, 15, 'Provisional', 7, 1, 1, 0, 0, 0, 0, 0, 0, 0, 2, 0, 3, now())", "23514")]
    public async Task RuntimeRole_Should_NotForgeOrDeleteResults_When_ItTries(string statement, string sqlState)
    {
        var database = await StoreDatabaseAsync();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using (var role = new NpgsqlCommand("SET ROLE ariva_runtime", connection))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // the statements are the theory's literals
        await using var command = new NpgsqlCommand(statement, connection);
#pragma warning restore CA2100

        Func<Task> run = () => command.ExecuteNonQueryAsync(Ct);

        (await run.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);
    }

    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private static string _storeDatabase;

    // A database with a written checkpoint (a Final bin among its rows), shared by the role tests.
    private async Task<string> StoreDatabaseAsync()
    {
        await StoreGate.WaitAsync(Ct);
        try
        {
            if (_storeDatabase is not null)
                return _storeDatabase;
            var database = await DatabaseAsync(TestDatabase.StreamRoles);
            var store = new StreamStore(new DatabaseSettings
            {
                Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = postgres.AdminUsername, Password = postgres.AdminPassword
            }, TimeProvider.System);
            var zone = new ZoneProcessor("DMO/Q1", new QueueZoneGeometry("Q1", new HashSet<string> { "Q1 entry" }, new HashSet<string> { "Q1 exit" },
                new HashSet<string>(), new HashSet<string>()), 7);
            foreach (var batch in Evening("Q1", 0))
                zone.Offer(batch, DateTime.MaxValue);
            var outputs = zone.Drain();
            outputs.Bins.Should().Contain(b => b.Status == BinStatus.Final);
            await store.SaveAsync(new StreamCheckpoint("g", [outputs], [zone.Capture()], [], [new StreamOffset("t", 0, 1)]), Ct);
            _storeDatabase = database;
            return database;
        }
        finally
        {
            StoreGate.Release();
        }
    }

    [Fact]
    public async Task Worker_Should_KeepAZone_When_ARecordOfItArrivesFirstOnTheWrongPartition()
    {
        var batches = Evening("Q1mp", 0);
        var natural = QueueStreamWorker.ExpectedPartition(batches[0].ZoneKey, 3);
        using (var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            await ProduceAsync("none", toMinute: 0);
            var stray = batches.First(b => b is ZoneOccupancyBatch);
            await producer.ProduceAsync(new TopicPartition(KafkaTopics.DeviceZoneOccupancy, new Partition((natural + 1) % 3)),
                new Message<string, byte[]> { Key = stray.ZoneKey, Value = JsonSerializer.SerializeToUtf8Bytes(stray, stray.GetType(), EventCatalog.Json) }, Ct);
            foreach (var batch in batches)
            {
                var topic = batch is VendorLineCrossingBatch ? KafkaTopics.DeviceVendorLineCrossing : KafkaTopics.DeviceZoneOccupancy;
                await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = batch.ZoneKey, Value = JsonSerializer.SerializeToUtf8Bytes(batch, batch.GetType(), EventCatalog.Json) }, Ct);
            }
        }

        var database = await DatabaseAsync(TestDatabase.StreamMisplaced);
        using (var host = Host(database, "it-misplaced"))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(database, "ariva-it-misplaced.queue-engine");
            await host.StopAsync(Ct);
        }

        int.Parse((await RowsAsync(database, "SELECT count(*) FROM queue_minute WHERE zone_key = 'DMO/Q1mp'"))[0], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(95, "the stray record is dead-lettered and the zone's own records are applied");
    }

    [Fact]
    public async Task Worker_Should_DeadLetterARecord_When_ItIsNotABatchOfItsZone()
    {
        await ProduceAsync("dl");
        var database = await DatabaseAsync(TestDatabase.StreamDeadLetter);
        using (var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            await producer.ProduceAsync(KafkaTopics.DeviceZoneOccupancy, new Message<string, byte[]> { Key = "DMO/Q9", Value = "{not json"u8.ToArray() }, Ct);
            var wrongKey = Evening("Q1", 0)[1];
            await producer.ProduceAsync(KafkaTopics.DeviceZoneOccupancy, new Message<string, byte[]> { Key = "DMO/elsewhere", Value = JsonSerializer.SerializeToUtf8Bytes(wrongKey, wrongKey.GetType(), EventCatalog.Json) }, Ct);
            // ARV-036: a health report published before the topic was keyed by zone is skipped, not dead-lettered; an
            // unreadable one is.
            var legacy = new DeviceHealthReported
            {
                DeviceId = Guid.Parse("0199a000-0000-7000-8000-0000000000a1"), DeviceCode = "S-15", SiteCode = "DMO", QueueZoneName = "Q1", Commissioned = true,
                Status = new DeviceStatus(true, null, null, null, Start.AddMinutes(30)), ReceivedUtc = Start.AddMinutes(30)
            };
            await producer.ProduceAsync(KafkaTopics.DeviceHealth, new Message<string, byte[]> { Key = legacy.DeviceId.ToString(), Value = JsonSerializer.SerializeToUtf8Bytes(legacy, EventCatalog.Json) }, Ct);
            await producer.ProduceAsync(KafkaTopics.DeviceHealth, new Message<string, byte[]> { Key = "DMO/Q9", Value = "{not json"u8.ToArray() }, Ct);
        }

        using var host = Host(database, "it-deadletter");
        await host.StartAsync(Ct);
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers, GroupId = "it-dl-reader-" + Guid.NewGuid().ToString("N"), AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe(KafkaTopics.DeadLetter(KafkaTopics.DeviceZoneOccupancy));
        var keys = new HashSet<string>();
        var until = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < until && !(keys.Contains("DMO/Q9") && keys.Contains("DMO/elsewhere")))
        {
            if (consumer.Consume(TimeSpan.FromSeconds(1)) is { Message: { } message })
                keys.Add(message.Key);
        }

        using var health = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers, GroupId = "it-dl-health-" + Guid.NewGuid().ToString("N"), AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        health.Subscribe(KafkaTopics.DeadLetter(KafkaTopics.DeviceHealth));
        var healthKeys = new HashSet<string>();
        until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && !healthKeys.Contains("DMO/Q9"))
        {
            if (health.Consume(TimeSpan.FromSeconds(1)) is { Message: { } message })
                healthKeys.Add(message.Key);
        }

        await host.StopAsync(Ct);
        keys.Should().Contain(["DMO/Q9", "DMO/elsewhere"]);
        healthKeys.Should().Equal(["DMO/Q9"], "the report keyed by device id is skipped, not dead-lettered");
    }

    /// <summary>Collects the warnings a store logs (ARV-113: dropped line minutes name the zone key and the count only).</summary>
    private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger<StreamStore>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
