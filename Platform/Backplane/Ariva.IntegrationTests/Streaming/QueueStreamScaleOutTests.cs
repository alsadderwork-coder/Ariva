using System.Text.Json;
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
/// ARV-034: partitions added to the sensing topics while the queue stream worker runs (a scale-out) lose nothing: the
/// worker takes up the new count on the rebalance and still accepts the records hashed over the old one. A broker of
/// its own, since the topics are changed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamScaleOutTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static readonly DateTime Start = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Zones = [.. Enumerable.Range(0, 12).Select(k => $"QG{k}")];

    private async Task ProduceAsync(int fromMinute, int toMinute)
    {
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers, EnableIdempotence = true }).Build();
        foreach (var batch in Zones.SelectMany((z, k) => QueueStreamTests.Evening(z, k)).OrderBy(b => b.ReceivedUtc)
                     .Where(b => b.ReceivedUtc >= Start.AddMinutes(fromMinute) && b.ReceivedUtc < Start.AddMinutes(toMinute)))
        {
            var topic = batch is VendorLineCrossingBatch ? KafkaTopics.DeviceVendorLineCrossing : KafkaTopics.DeviceZoneOccupancy;
            await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = batch.ZoneKey, Value = JsonSerializer.SerializeToUtf8Bytes(batch, batch.GetType(), EventCatalog.Json) }, Ct);
        }
    }

    private async Task<bool> CaughtUpAsync(string database, int partitions)
    {
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig { BootstrapServers = kafka.BootstrapServers, GroupId = "it-ends" }).Build();
        var ends = new Dictionary<(string, int), long>();
        foreach (var topic in QueueStreamWorker.Topics)
        {
            for (var p = 0; p < partitions; p++)
                ends[(topic, p)] = consumer.QueryWatermarkOffsets(new TopicPartition(topic, new Partition(p)), TimeSpan.FromSeconds(10)).High.Value;
        }

        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT topic, partition_no, next_offset FROM stream_offset", connection);
        var saved = new Dictionary<(string, int), long>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            saved[(reader.GetString(0), reader.GetInt32(1))] = reader.GetInt64(2);
        return ends.All(e => e.Value == 0 || saved.GetValueOrDefault(e.Key) >= e.Value);
    }

    private async Task WaitAsync(string database, int partitions)
    {
        var until = DateTime.UtcNow.AddSeconds(180);
        while (!await CaughtUpAsync(database, partitions) && DateTime.UtcNow < until)
            await Task.Delay(250, Ct);
        (await CaughtUpAsync(database, partitions)).Should().BeTrue();
    }

    [Fact]
    public async Task Worker_Should_KeepEveryZone_When_PartitionsAreAddedWhileItRuns()
    {
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            await admin.CreateTopicsAsync(QueueStreamWorker.Topics.Select(t => new TopicSpecification { Name = t, NumPartitions = 3, ReplicationFactor = 1 })
                .Concat(QueueStreamWorker.Topics.Select(t => new TopicSpecification { Name = KafkaTopics.DeadLetter(t), NumPartitions = 1, ReplicationFactor = 1 })));
        }

        var database = await postgres.CreateDatabaseAsync(TestDatabase.StreamScaleOut);
        await new SqlScriptRunner(postgres.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        using var host = Build(database, "it-scale");
        await host.StartAsync(Ct);

        await ProduceAsync(0, 30);
        await WaitAsync(database, 3);
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build())
            await admin.CreatePartitionsAsync(QueueStreamWorker.Topics.Select(t => new PartitionsSpecification { Topic = t, IncreaseTo = 6 }));
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        await ProduceAsync(30, 200);
        await WaitAsync(database, 6);
        await host.StopAsync(Ct);
        (await ZoneRowsAsync(database)).Values.Should().OnlyContain(n => n > 95, "every zone kept its records after the partitions were added");

        // A recompute from scratch (runbook 4.5b) by a fresh process, which reads the records produced before the
        // increase too: the recorded partition counts let it accept them.
        await using (var reset = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await reset.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("DELETE FROM stream_zone_state; DELETE FROM stream_offset; DELETE FROM queue_minute; DELETE FROM queue_bin", reset);
            await command.ExecuteNonQueryAsync(Ct);
        }

        using var fresh = Build(database, "it-scale-fresh");
        await fresh.StartAsync(Ct);
        await WaitAsync(database, 6);
        await fresh.StopAsync(Ct);
        var rows = await ZoneRowsAsync(database);
        rows.Keys.Should().BeEquivalentTo(Zones.Select(z => $"DMO/{z}"));
        rows.Values.Should().OnlyContain(n => n > 95, "a fresh process accepts the records hashed over the earlier partition count");
    }

    private IHost Build(string database, string service)
    {
        var builder = Host.CreateApplicationBuilder();
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

    private async Task<Dictionary<string, long>> ZoneRowsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT zone_key, count(*) FROM queue_minute GROUP BY zone_key ORDER BY zone_key", connection);
        var rows = new Dictionary<string, long>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows[reader.GetString(0)] = reader.GetInt64(1);
        return rows;
    }
}
