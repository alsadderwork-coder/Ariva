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
