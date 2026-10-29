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
/// ARV-259e1 on the live path, on a broker of its own (as <see cref="QueueStreamEventTimeTests"/>: the stream merges each partition in
/// its offset order, so another test's records on the same partitions would hold this zone's records back): a zone whose only device
/// goes offline has no measurement at all, its bins are stored Unknown, and the spell survives a restart of the stream.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamCoverageTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
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
            // Created already on this broker.
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

    [Fact]
    public async Task Worker_Should_StoreTheBinsUnknownAndKeepTheSpellOverARestart_When_TheZonesOnlyDeviceGoesOffline()
    {
        // ARV-259e1 (formulas F11) on the live path: S-74, the only device of Q7et, reads the queue each minute, reports itself offline
        // at 17:12 and is heard again with its reading of 17:25, received at 17:26:00.5 (a push that covers 17:25, ARV-148). The zone has
        // no measurement at all from 17:12 to 17:25: its bins are Unknown, stored as such. The stream is stopped and started again
        // while the spell is open, so the spell is carried by the version 14 zone snapshot.
        await CreateTopicsAsync();
        const string zone = "Q7et";
        DateTime End(int m) => Start.AddMinutes(m + 1);
        (string Topic, Ariva.Core.Domain.Contracts.IEvent Event) Reading(int m) =>
            (KafkaTopics.DeviceZoneOccupancy, new ZoneOccupancyBatch
            {
                Id = Guid.NewGuid(), DeviceId = Guid.Parse("0199a000-0000-7000-8000-000000000074"), DeviceCode = "S-74", SiteCode = "DMO", QueueZoneName = zone,
                Dialect = "canonical", Commissioned = true, ReceivedUtc = End(m).AddSeconds(0.5), Clock = new ClockReading(0, true, ClockState.Ok),
                Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy(zone, 2, End(m)), End(m), SensedFlags.None)]
            });
        var offlineAt = Start.AddMinutes(12);
        var before = Enumerable.Range(0, 11).Select(Reading).Append((KafkaTopics.DeviceHealth, new DeviceHealthReported
        {
            Id = Guid.NewGuid(), DeviceId = Guid.Parse("0199a000-0000-7000-8000-000000000074"), DeviceCode = "S-74", SiteCode = "DMO", QueueZoneName = zone,
            Commissioned = true, Status = new DeviceStatus(false, null, null, null, offlineAt), ReceivedUtc = offlineAt, OccurredOn = offlineAt
        }));
        var after = Enumerable.Range(25, 31).Select(Reading);

        async Task ProduceAsync(IEnumerable<(string Topic, Ariva.Core.Domain.Contracts.IEvent Event)> records)
        {
            using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers, EnableIdempotence = true }).Build();
            foreach (var (topic, e) in records)
            {
                var key = e is SensingBatch b ? b.ZoneKey : ((DeviceHealthReported)e).ZoneKey;
                await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = JsonSerializer.SerializeToUtf8Bytes(e, e.GetType(), EventCatalog.Json) }, Ct);
            }
        }

        var database = await DatabaseAsync(TestDatabase.StreamCoverage);
        var settings = new Dictionary<string, string> { ["Stream:Sites:DMO:LatenessSeconds"] = "90", ["Stream:Sites:DMO:PushIntervalSeconds"] = "60" };
        var at = new Dictionary<string, object> { ["zone"] = "DMO/" + zone };
        const string Saved = "SELECT state->>'version', jsonb_array_length(state->'coverage') FROM stream_zone_state WHERE zone_key = @zone";
        await ProduceAsync(before);
        using (var host = Host(database, "it-coverage", settings))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(database, "ariva-it-coverage.queue-engine");
            await host.StopAsync(Ct);
        }

        (await RowsAsync(database, Saved, at)).Should().Equal(["14|2"], "both lines are without a live covering device when the stream stops");

        await ProduceAsync(after);
        using (var host = Host(database, "it-coverage", settings))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(database, "ariva-it-coverage.queue-engine");
            await host.StopAsync(Ct);
        }

        (await RowsAsync(database, "SELECT device_code, from_utc, to_utc, closed FROM zone_outage WHERE zone_key = @zone", at))
            .Should().Equal(["S-74|09/28/2026 17:12:00|09/28/2026 17:25:00|True"], "out from its offline report to the minute its next push covers");
        const string Finals = """
            SELECT to_char(start_utc AT TIME ZONE 'UTC', 'HH24:MI'), quality FROM queue_bin
            WHERE zone_key = @zone AND status = 'Final' AND start_utc >= '2026-09-28 17:00:00+00' AND start_utc < '2026-09-28 17:45:00+00'
            ORDER BY start_utc, revision
            """;
        (await RowsAsync(database, Finals, at)).Should().Equal(["17:00|Unknown", "17:15|Unknown", "17:30|Good"], "no measurement at all from 17:12 to 17:25 (F11)");
        (await RowsAsync(database, Saved, at)).Should().Equal(["14|0"], "the spell ended with S-74's reading of 17:25");
    }
}
