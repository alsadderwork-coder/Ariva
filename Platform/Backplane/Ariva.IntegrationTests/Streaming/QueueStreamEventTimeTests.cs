using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Messaging;
using Ariva.Core.Sensing;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
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

/// <summary>
/// ARV-148 on the live path, on a broker of its own: the stream merges each partition in its offset order, so records of
/// other tests with overlapping receive times on the same partitions (the evening of <see cref="QueueStreamTests"/>) would
/// hold one topic's records of a zone back behind them and not the other's, which a real Ingest, appending in receive
/// order, never does.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamEventTimeTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static readonly DateTime Start = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> DatabaseAsync(TestDatabase database)
    {
        var name = await postgres.CreateDatabaseAsync(database);
        await new SqlScriptRunner(postgres.ConnectionString(name), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        return name;
    }

    private async Task CreateTopicsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
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

    private IHost Host(string database, string service, IReadOnlyDictionary<string, string> extra)
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
        builder.Configuration.AddInMemoryCollection(extra);
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new FileLoggerProvider(log));
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging.StreamQueueZones());
        builder.Services.AddArivaQueueStream(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<ZoneGeometrySource, FixedGeometrySource>());
        return builder.Build();
    }

    private async Task WaitForAsync(string database, string group, int seconds = 180)
    {
        var ends = new Dictionary<(string Topic, int Partition), long>();
        using (var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig { BootstrapServers = kafka.BootstrapServers, GroupId = "it-ends" }).Build())
        {
            foreach (var topic in QueueStreamWorker.Topics)
            {
                for (var p = 0; p < 3; p++)
                    ends[(topic, p)] = consumer.QueryWatermarkOffsets(new TopicPartition(topic, new Partition(p)), TimeSpan.FromSeconds(10)).High.Value;
            }
        }

        async Task<bool> CaughtUp()
        {
            var saved = new Dictionary<(string, int), long>();
            await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT topic, partition_no, next_offset FROM stream_offset WHERE consumer_group = @group", connection);
            command.Parameters.AddWithValue("group", group);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
                saved[(reader.GetString(0), reader.GetInt32(1))] = reader.GetInt64(2);
            return ends.All(e => e.Value == 0 || saved.GetValueOrDefault(e.Key) >= e.Value);
        }

        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await CaughtUp() && DateTime.UtcNow < until)
            await Task.Delay(250, Ct);
        (await CaughtUp()).Should().BeTrue("the worker should have applied every record within {0} seconds", seconds);
    }

    /// <summary>The rows of a literal query, values bound as <paramref name="parameters"/> (never interpolated, CWE-89).</summary>
    private async Task<List<string>> RowsAsync(string database, string sql, IReadOnlyDictionary<string, object> parameters)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }

    /// <summary>
    /// ARV-148: an hour of one zone as the live sensors push it. S-73 reads the queue at each minute's end and its push is
    /// received half a second later; S-71's push of the minute's crossings (up to 61 seconds old) a second later, after
    /// S-73's; S-72 only reports its health, and is silent for its pushes of 17:20 to 17:29. People join until 18:00; the
    /// crossings are played until 18:04 and the readings until 18:06, so that every minute with crossings closes. With <paramref name="split"/>
    /// every crossing is a record of its own, produced newest first, and each push's health report is produced before its
    /// other records. Returns the records with their topics and the entries and exits they carry.
    /// </summary>
    private static (List<(string Topic, Ariva.Core.Domain.Contracts.IEvent Event)> Records, int Entries, int Exits) LivePushes(string zone, bool split)
    {
        var people = new List<(int Id, DateTime In, DateTime Out)>();
        for (var m = 0; m < 60; m++)
        {
            var arrivals = 3 + (m * 5 % 4);
            for (var j = 0; j < arrivals; j++)
            {
                var entry = Start.AddMinutes(m).AddSeconds(60.0 * j / arrivals);
                people.Add((people.Count + 1, entry, entry.AddSeconds(60 + (m * 37 + j * 53) % 360)));
            }
        }

        T Stamp<T>(T batch, string device, DateTime received) where T : SensingBatch
        {
            batch.Id = Guid.NewGuid();
            batch.DeviceId = Guid.Parse(device == "S-71" ? "0199a000-0000-7000-8000-000000000071" : "0199a000-0000-7000-8000-000000000073");
            batch.DeviceCode = device;
            batch.SiteCode = "DMO";
            batch.QueueZoneName = zone;
            batch.Dialect = "canonical";
            batch.Commissioned = true;
            batch.ReceivedUtc = received;
            batch.Clock = new ClockReading(0, true, ClockState.Ok);
            return batch;
        }

        var records = new List<(string, Ariva.Core.Domain.Contracts.IEvent)>();
        int entries = 0, exits = 0;
        for (var m = 0; m < 66; m++)
        {
            var from = Start.AddMinutes(m);
            var end = from.AddMinutes(1);
            var crossings = people.Where(p => p.In >= from && p.In < end && m < 64).Select(p => (p.In, new LineCrossing($"{zone} entry", CrossingDirection.In, $"{p.Id}", p.In)))
                .Concat(people.Where(p => p.Out >= from && p.Out < end && m < 64).Select(p => (p.Out, new LineCrossing($"{zone} exit", CrossingDirection.Out, $"{p.Id}", p.Out))))
                .OrderBy(c => c.Item1).Select(c => new Sensed<LineCrossing>(c.Item2, c.Item1, SensedFlags.None)).ToList();
            entries += crossings.Count(c => c.Event.Direction == CrossingDirection.In);
            exits += crossings.Count(c => c.Event.Direction == CrossingDirection.Out);
            var inside = people.Count(p => p.In < end && p.Out >= end);
            records.Add((KafkaTopics.DeviceZoneOccupancy, Stamp(new ZoneOccupancyBatch
            {
                Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy(zone, inside, end), end, SensedFlags.None)]
            }, "S-73", end.AddSeconds(0.5))));
            var received = end.AddSeconds(1);
            if (m is < 20 or >= 30)
            {
                records.Add((KafkaTopics.DeviceHealth, new DeviceHealthReported
                {
                    Id = Guid.NewGuid(), DeviceId = Guid.Parse("0199a000-0000-7000-8000-000000000072"), DeviceCode = "S-72", SiteCode = "DMO", QueueZoneName = zone,
                    Commissioned = true, Status = new DeviceStatus(true, null, null, null, end), ReceivedUtc = received, OccurredOn = received
                }));
            }

            if (crossings.Count == 0)
                continue;
            if (split)
                records.AddRange(Enumerable.Reverse(crossings).Select(c => (KafkaTopics.DeviceVendorLineCrossing, (Ariva.Core.Domain.Contracts.IEvent)Stamp(new VendorLineCrossingBatch { Crossings = [c] }, "S-71", received))));
            else
                records.Add((KafkaTopics.DeviceVendorLineCrossing, Stamp(new VendorLineCrossingBatch { Crossings = crossings }, "S-71", received)));
        }

        return (records, entries, exits);
    }

    [Fact]
    public async Task Worker_Should_LoseNoCrossingAndEndTheOutageAtTheMinuteThePushCovers_When_PushesAreSplitAcrossTopics()
    {
        // ARV-148 on the live path: the stream runs each zone on its events' time under its site's lateness (DMO: 90 seconds,
        // push interval 60; the 30 seconds of the other sites would lose S-71's older crossings to S-73's earlier push), so
        // no crossing of a minute-old push is late, the same events split another way give the same rows, and S-72's
        // outage ends at 17:30, the minute its push at 17:31:01 covers (F4 of ARV-104j), leaving the 17:30 bin judged.
        await CreateTopicsAsync();
        var (whole, entries, exits) = LivePushes("Q5et", split: false);
        var (split, splitEntries, splitExits) = LivePushes("Q6et", split: true);
        (splitEntries, splitExits).Should().Be((entries, exits));
        using (var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers, EnableIdempotence = true }).Build())
        {
            foreach (var (topic, e) in whole.Concat(split))
            {
                var key = e is SensingBatch b ? b.ZoneKey : ((DeviceHealthReported)e).ZoneKey;
                await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = JsonSerializer.SerializeToUtf8Bytes(e, e.GetType(), EventCatalog.Json) }, Ct);
            }
        }

        var database = await DatabaseAsync(TestDatabase.StreamEventTime);
        using (var host = Host(database, "it-event-time", new Dictionary<string, string>
               {
                   ["Stream:LatenessSeconds"] = "30",
                   ["Stream:PushIntervalSeconds"] = "30",
                   ["Stream:Sites:DMO:LatenessSeconds"] = "90",
                   ["Stream:Sites:DMO:PushIntervalSeconds"] = "60"
               }))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(database, "ariva-it-event-time.queue-engine");
            await host.StopAsync(Ct);
        }

        foreach (var zone in new[] { "DMO/Q5et", "DMO/Q6et" })
        {
            var at = new Dictionary<string, object> { ["zone"] = zone };
            (await RowsAsync(database, "SELECT coalesce(sum(late_events), 0), count(*) >= 4 FROM queue_bin WHERE zone_key = @zone", at)).Should().Equal(["0|True"], zone);
            var name = zone[4..];
            (await RowsAsync(database, """
                SELECT coalesce(sum(crossings_in) FILTER (WHERE line_role = 'Entry'), 0), coalesce(sum(crossings_out) FILTER (WHERE line_role = 'Exit'), 0)
                FROM line_minute WHERE zone_key = @zone
                """, at)).Should().Equal([$"{entries}|{exits}"], "every crossing of {0} is counted on its line ({1})", zone, name);
            (await RowsAsync(database, "SELECT device_code, from_utc, to_utc, closed FROM zone_outage WHERE zone_key = @zone", at))
                .Should().Equal(["S-72|09/28/2026 17:20:00|09/28/2026 17:30:00|True"], "S-72 is out from its silent push of 17:20 to the minute its push at 17:31:01 covers");
            (await RowsAsync(database, "SELECT to_char(start_utc AT TIME ZONE 'UTC', 'HH24:MI'), quality FROM queue_bin WHERE zone_key = @zone AND status = 'Final' AND start_utc IN ('2026-09-28 17:15:00+00', '2026-09-28 17:30:00+00') ORDER BY start_utc", at))
                .Should().Equal(["17:15|Degraded", "17:30|Good"], "the bin after the outage is judged");
        }

        const string Bins = """
            SELECT start_utc, revision, status, quality, entries, exits, waits, mean_wait_minutes, p90_wait_minutes, abandoned, fragmented, censored, reanchored,
                   rejected, open_people, late_events FROM queue_bin WHERE zone_key = @zone ORDER BY start_utc, revision
            """;
        const string Lines = "SELECT line_role, minute_utc, crossings_in, crossings_out FROM line_minute WHERE zone_key = @zone ORDER BY line_role, minute_utc";
        var q5 = new Dictionary<string, object> { ["zone"] = "DMO/Q5et" };
        var q6 = new Dictionary<string, object> { ["zone"] = "DMO/Q6et" };
        (await RowsAsync(database, Bins, q6)).Should().Equal(await RowsAsync(database, Bins, q5), "the same events split another way give the same bins");
        (await RowsAsync(database, Lines, q6)).Should().Equal(await RowsAsync(database, Lines, q5));
    }
}
