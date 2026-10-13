using System.Globalization;
using System.Text;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Messaging;
using Ariva.Core.Services.Sensing;
using Ariva.Di.Extensions;
using Ariva.Infra.Sensing;
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
/// ARV-168 end to end, on a broker of its own: a T1 counter pushes interval counts through Ingest's sensing path (the
/// canonical mapper, the clock estimate, the batch ids and the Kafka sink of Ariva.Api.Ingest) and sends one push again
/// twice, as a device does when it dropped the request; the same body gets the same batch id, the three records reach
/// the queue stream, and the stream's minute counts apply the interval once: the zone's line and queue minutes are those
/// of a zone whose device never resent.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamResendTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private const string Resent = "Q8rs";
    private const string Control = "Q9rs";
    private const int Minutes = 12;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The published geometry Ingest checks line names against: the zone's entry and exit lines, as the stream's <see cref="FixedGeometrySource"/> has them.</summary>
    private sealed class Gateway : ISvcDeviceGateway
    {
        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult<DeviceCredentialRecord>(null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, 7, new string('b', 64),
                [new(Guid.NewGuid(), queueZoneName, "Queue", Guid.NewGuid(), null, null, "10 10,34 10,34 22,10 22", 288)],
                [
                    new(Guid.NewGuid(), $"{queueZoneName} entry", "Entry", null, Guid.NewGuid(), 10, 12, 10, 16, 4),
                    new(Guid.NewGuid(), $"{queueZoneName} exit", "Exit", null, Guid.NewGuid(), 30, 22, 34, 22, 4)
                ])));
    }

    private static DeviceCredentialRecord Device(string code, string zone) =>
        new(Guid.Parse($"0199e0a0-0000-7000-8000-0000000000{code[^2..]}"), code, "DMO", zone, "Online", new string('a', 64), [], null);

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>A canonical push of minute <paramref name="m"/>: 5 in on the entry line and 3 out on the exit line, sent half a second after it.</summary>
    private static byte[] Push(string zone, DateTime start, int m)
    {
        var from = start.AddMinutes(m);
        var to = from.AddMinutes(1);
        return Encoding.UTF8.GetBytes($$"""
            { "sentUtc": "{{Utc(to.AddSeconds(0.5))}}", "packageId": {{m + 1}},
              "intervals": [
                { "lineName": "{{zone}} entry", "in": 5, "out": 0, "fromUtc": "{{Utc(from)}}", "toUtc": "{{Utc(to)}}" },
                { "lineName": "{{zone}} exit", "in": 0, "out": 3, "fromUtc": "{{Utc(from)}}", "toUtc": "{{Utc(to)}}" } ] }
            """);
    }

    private IHost IngestHost(string database)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = postgres.AdminUsername,
            ["Database:Password"] = postgres.AdminPassword,
            ["Redis:Enabled"] = "false",
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Kafka:ServiceName"] = "it-ingest-resend"
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new FileLoggerProvider(log));
        // Ariva.Api.Ingest's sensing path: the caches, the bus and the ingest with its Kafka sink; the device registry's
        // lookup of the published zone is replaced by the zone's lines.
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaCaching(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration);
        builder.Services.AddArivaSensingIngest(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, Gateway>());
        return builder.Build();
    }

    private IHost StreamHost(string database)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = postgres.AdminUsername,
            ["Database:Password"] = postgres.AdminPassword,
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Kafka:ServiceName"] = "it-stream-resend",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Stream:Enabled"] = "true",
            ["Stream:IdleTickSeconds"] = "0",
            ["Stream:LatenessSeconds"] = "90",
            ["Stream:PushIntervalSeconds"] = "60"
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new FileLoggerProvider(log));
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging.StreamQueueZones());
        builder.Services.AddArivaQueueStream(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<ZoneGeometrySource, FixedGeometrySource>());
        return builder.Build();
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

    /// <summary>The batch ids of the interval-count records on the topic, per zone key, in offset order.</summary>
    private Dictionary<string, List<Guid>> IntervalRecords()
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = "it-resend-reader",
            EnableAutoCommit = false
        }).Build();
        var ids = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        for (var p = 0; p < 3; p++)
        {
            var partition = new TopicPartition(KafkaTopics.DeviceIntervalCount, new Partition(p));
            var high = consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10)).High.Value;
            if (high == 0)
                continue;
            consumer.Assign(new TopicPartitionOffset(partition, Offset.Beginning));
            while (consumer.Consume(TimeSpan.FromSeconds(10)) is { } record)
            {
                using var json = JsonDocument.Parse(record.Message.Value);
                if (!ids.TryGetValue(record.Message.Key, out var list))
                    ids[record.Message.Key] = list = [];
                list.Add(json.RootElement.GetProperty("id").GetGuid());
                if (record.Offset.Value + 1 >= high)
                    break;
            }

            consumer.Unassign();
        }

        return ids;
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
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), CultureInfo.InvariantCulture))));
        return rows;
    }

    [Fact]
    public async Task Stream_Should_ApplyTheIntervalOnce_When_ADeviceResendsAPushThroughIngestWithTheSameBatchId()
    {
        // Twelve minutes ending half an hour ago (Ingest and the stream check event times against their clocks). S-81 pushes
        // its counts a second after each minute and sends minute 3's push again 20 seconds later and after its next push:
        // both times before the push of minute 5, which would have made the repeat late, so without ARV-168 the engine
        // applied the interval three times (the repeats spread over nothing: 10 more entries in the next minute). S-92
        // pushes the same counts for the control zone and never resends.
        await CreateTopicsAsync();
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc).AddMinutes(-30);
        DateTime Received(int m) => start.AddMinutes(m + 1).AddSeconds(1);
        var sends = new List<(DeviceCredentialRecord Device, byte[] Body, DateTime ReceivedUtc)>();
        for (var m = 0; m < Minutes; m++)
        {
            sends.Add((Device("S-81", Resent), Push(Resent, start, m), Received(m)));
            sends.Add((Device("S-92", Control), Push(Control, start, m), Received(m)));
        }

        sends.Add((Device("S-81", Resent), Push(Resent, start, 3), Received(3).AddSeconds(19)));
        sends.Add((Device("S-81", Resent), Push(Resent, start, 3), Received(4).AddSeconds(4)));

        var database = await postgres.CreateDatabaseAsync(TestDatabase.StreamResend);
        await new SqlScriptRunner(postgres.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        using (var ingestHost = IngestHost(database))
        {
            await ingestHost.StartAsync(Ct);
            foreach (var (device, body, received) in sends.OrderBy(s => s.ReceivedUtc))
            {
                await using var scope = ingestHost.Services.CreateAsyncScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<SensingIngest>().IngestAsync(device, DeviceDialect.Canonical, body, received, Ct);
                outcome.HasErrors.Should().BeFalse(string.Join(", ", outcome.ErrorMessages ?? []));
                outcome.Data.Accepted.Should().Be(2, string.Join(", ", outcome.Data.Problems));
            }

            await ingestHost.StopAsync(Ct);
        }

        // Ingest gave the resent body its first id: the stream got minute 3's batch three times under one id.
        var records = IntervalRecords();
        records["DMO/" + Resent].Should().HaveCount(Minutes + 2);
        var minute3 = records["DMO/" + Resent][3];
        records["DMO/" + Resent].Count(id => id == minute3).Should().Be(3, "the two resends carry minute 3's batch id");
        records["DMO/" + Resent].Where(id => id != minute3).Should().HaveCount(Minutes - 1).And.OnlyHaveUniqueItems();
        records["DMO/" + Control].Should().HaveCount(Minutes).And.OnlyHaveUniqueItems();

        using (var host = StreamHost(database))
        {
            await host.StartAsync(Ct);
            await WaitForAsync(database, "ariva-it-stream-resend.queue-engine");
            await host.StopAsync(Ct);
        }

        // The stream's minute counts: every closed minute of the entry line holds one push's 5 entries (minute 3 and the
        // next among them); the zone's lines and minutes are the control zone's, minute by minute.
        var resent = new Dictionary<string, object> { ["zone"] = "DMO/" + Resent };
        var control = new Dictionary<string, object> { ["zone"] = "DMO/" + Control };
        const string EntryMinutes = """
            SELECT to_char(minute_utc AT TIME ZONE 'UTC', 'HH24:MI'), crossings_in FROM line_minute WHERE zone_key = @zone AND line_role = 'Entry' ORDER BY minute_utc
            """;
        var entries = await RowsAsync(database, EntryMinutes, resent);
        entries.Should().HaveCountGreaterThanOrEqualTo(8, "at least the first eight minutes closed").And.OnlyContain(r => r.EndsWith("|5", StringComparison.Ordinal),
            "each minute's 5 entries count once, the resent minute 3 and minute 4 included");
        entries.Should().Contain($"{start.AddMinutes(3):HH:mm}|5").And.Contain($"{start.AddMinutes(4):HH:mm}|5");
        const string Lines = "SELECT line_role, minute_utc, crossings_in, crossings_out FROM line_minute WHERE zone_key = @zone ORDER BY line_role, minute_utc";
        (await RowsAsync(database, Lines, resent)).Should().Equal(await RowsAsync(database, Lines, control), "the resends change no line minute");
        const string QueueMinutes = "SELECT minute_utc, status, entries, exits, waits FROM queue_minute WHERE zone_key = @zone ORDER BY minute_utc";
        var minutes = await RowsAsync(database, QueueMinutes, resent);
        minutes.Should().NotBeEmpty().And.Equal(await RowsAsync(database, QueueMinutes, control), "nor any queue minute's entries, exits and waits");
        (await RowsAsync(database, "SELECT coalesce(sum(entries), 0) FROM queue_minute WHERE zone_key = @zone AND minute_utc = @minute",
            new Dictionary<string, object> { ["zone"] = "DMO/" + Resent, ["minute"] = start.AddMinutes(3) })).Should().Equal(["5"], "minute 3's interval is applied once");
    }
}
