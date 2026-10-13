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
/// Pushes through Ingest's sensing path into the queue stream, on a test class's own broker (ARV-168, ARV-173): the hosts of
/// Ariva.Api.Ingest (caches, bus, ingest with its Kafka sink; only the registry's lookup of the published zone replaced
/// by the zone's lines) and of Ariva.Api.Stream (the queue engine worker with <see cref="FixedGeometrySource"/>), the topics
/// and the reads the tests check. <paramref name="name"/> names the hosts' services, so their consumer groups.
/// </summary>
internal sealed class IngestToStream(PostgresFixture postgres, KafkaFixture kafka, string name)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The stream worker's consumer group (<c>ariva-&lt;Kafka:ServiceName&gt;.queue-engine</c>).</summary>
    public string StreamGroup => $"ariva-it-stream-{name}.{QueueStreamWorker.Purpose}";

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

    /// <summary>A commissioned canonical device of <paramref name="zone"/> at DMO; the code's last two characters make its id.</summary>
    public static DeviceCredentialRecord Device(string code, string zone) =>
        new(Guid.Parse($"0199e0a0-0000-7000-8000-0000000000{code[^2..]}"), code, "DMO", zone, "Online", new string('a', 64), [], null);

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// A canonical push of the readings of minute <paramref name="m"/> after <paramref name="start"/> (each over the whole minute,
    /// in the order given), sent half a second after the minute; the same arguments always give the same body.
    /// </summary>
    public static byte[] Push(DateTime start, int m, long packageId, params (string Line, int In, int Out)[] readings)
    {
        var from = Utc(start.AddMinutes(m));
        var to = Utc(start.AddMinutes(m + 1));
        var intervals = string.Join(",\n    ", readings.Select(r =>
            $$"""{ "lineName": "{{r.Line}}", "in": {{r.In}}, "out": {{r.Out}}, "fromUtc": "{{from}}", "toUtc": "{{to}}" }"""));
        return Encoding.UTF8.GetBytes($$"""
            { "sentUtc": "{{Utc(start.AddMinutes(m + 1).AddSeconds(0.5))}}", "packageId": {{packageId}},
              "intervals": [
                {{intervals}} ] }
            """);
    }

    /// <summary>A fresh database of <paramref name="database"/> with every script applied.</summary>
    public async Task<string> DatabaseAsync(TestDatabase database)
    {
        var created = await postgres.CreateDatabaseAsync(database);
        await new SqlScriptRunner(postgres.ConnectionString(created), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        return created;
    }

    public async Task CreateTopicsAsync()
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

    private Dictionary<string, string> Base(string database, string service) => new()
    {
        ["Database:Host"] = postgres.Hostname,
        ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
        ["Database:Name"] = database,
        ["Database:Username"] = postgres.AdminUsername,
        ["Database:Password"] = postgres.AdminPassword,
        ["Kafka:Enabled"] = "true",
        ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
        ["Kafka:ServiceName"] = service
    };

    private IHost IngestHost(string database)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Base(database, $"it-ingest-{name}"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string> { ["Redis:Enabled"] = "false" });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new FileLoggerProvider(log));
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaCaching(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration);
        builder.Services.AddArivaSensingIngest(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Scoped<ISvcDeviceGateway, Gateway>());
        return builder.Build();
    }

    /// <summary>
    /// Ingests every push in receive order through Ingest's host (each must be accepted whole: <paramref name="accepted"/>
    /// events), then stops the host.
    /// </summary>
    public async Task IngestAsync(string database, IEnumerable<(DeviceCredentialRecord Device, byte[] Body, DateTime ReceivedUtc)> sends, Func<byte[], int> accepted)
    {
        using var host = IngestHost(database);
        await host.StartAsync(Ct);
        foreach (var (device, body, received) in sends.OrderBy(s => s.ReceivedUtc))
        {
            await using var scope = host.Services.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<SensingIngest>().IngestAsync(device, DeviceDialect.Canonical, body, received, Ct);
            outcome.HasErrors.Should().BeFalse(string.Join(", ", outcome.ErrorMessages ?? []));
            outcome.Data.Accepted.Should().Be(accepted(body), string.Join(", ", outcome.Data.Problems));
        }

        await host.StopAsync(Ct);
    }

    /// <summary>Runs the queue stream over everything on the topics (lateness 90 s, push interval 60 s, no idle ticks) and stops it.</summary>
    public async Task StreamAsync(string database)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Base(database, $"it-stream-{name}"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
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
        using var host = builder.Build();
        await host.StartAsync(Ct);
        await WaitForAsync(database);
        await host.StopAsync(Ct);
    }

    /// <summary>The batch ids of the interval-count records on the topic, per zone key, in offset order.</summary>
    public Dictionary<string, List<Guid>> IntervalRecords()
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = $"it-{name}-reader",
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

    private async Task WaitForAsync(string database, int seconds = 180)
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
            command.Parameters.AddWithValue("group", StreamGroup);
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
    public async Task<List<string>> RowsAsync(string database, string sql, IReadOnlyDictionary<string, object> parameters)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (key, value) in parameters)
            command.Parameters.AddWithValue(key, value);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), CultureInfo.InvariantCulture))));
        return rows;
    }
}
