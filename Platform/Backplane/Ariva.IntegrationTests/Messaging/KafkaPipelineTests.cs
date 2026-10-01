using System.Collections.Concurrent;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Kafka;

namespace Ariva.IntegrationTests.Messaging;

[KafkaTopic(KafkaTopics.SlaDisputeChanged)]
public sealed class KafkaProbe : EventBase
{
    public string ContractId { get; set; }

    /// <summary>"ok" or "fail".</summary>
    public string Mode { get; set; } = "ok";

    public override string GetPartitionKey() => ContractId;
}

public sealed class KafkaProbeConsumer : IConsumer<KafkaProbe>
{
    public static readonly ConcurrentDictionary<Guid, int> Applied = new();
    public static readonly ConcurrentDictionary<Guid, int> Attempts = new();

    public Task Consume(ConsumeContext<KafkaProbe> context)
    {
        Attempts.AddOrUpdate(context.Message.Id, 1, (_, n) => n + 1);
        if (context.Message.Mode == "fail")
            throw new InvalidOperationException("poison " + context.Message.ContractId);
        Applied.AddOrUpdate(context.Message.Id, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }
}

/// <summary>A single-broker Kafka for the pipeline tests (KRaft is not needed for these).</summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// ARV-020 end to end on a real broker (Testcontainers.Kafka) and PostgreSQL: an event published through the bus
/// reaches its consumer through the Ariva consume pipe; a poison message is retried, then lands on
/// <c>&lt;topic&gt;.dlq.v1</c> with its key, partition, offset and error, and the partition moves on; a redelivery after
/// a lost checkpoint (offsets rewound, as after a crash between the write and the checkpoint) is skipped by the inbox,
/// so the effect happens once; and the values on the wire are plain JSON with the outbox headers.
/// These tests need Docker and the librdkafka native library that the Confluent.Kafka package brings.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class KafkaPipelineTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>, IAsyncLifetime
{
    private const string Purpose = "it-pipeline";
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static string _sharedDatabase;
    private readonly KafkaFixture _kafka = kafka;
    private string _database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string GroupId => $"ariva-it.{Purpose}";

    public async ValueTask InitializeAsync()
    {
        await DatabaseGate.WaitAsync(Ct);
        try
        {
            if (_sharedDatabase is null)
            {
                var name = await postgres.CreateDatabaseAsync(TestDatabase.Kafka);
                await new SqlScriptRunner(postgres.ConnectionString(name), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
                _sharedDatabase = name;
            }

            _database = _sharedDatabase;
        }
        finally
        {
            DatabaseGate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IHost> StartHostAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = _database,
            ["Database:Username"] = postgres.AdminUsername,
            ["Database:Password"] = postgres.AdminPassword,
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = _kafka.BootstrapServers,
            ["Kafka:ServiceName"] = "it",
            ["Kafka:ProvisionTopics"] = "true",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Consumers:RetryCount"] = "2",
            ["Kafka:Consumers:RetryMinMilliseconds"] = "10",
            ["Kafka:Consumers:RetryMaxMilliseconds"] = "50",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1"
        });
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(KafkaProbe).Assembly)
            .Consume<KafkaProbe, KafkaProbeConsumer>(KafkaTopics.SlaDisputeChanged, Purpose));

        var host = builder.Build();
        await host.StartAsync(Ct);
        var checks = host.Services.GetRequiredService<HealthCheckService>();
        var until = DateTime.UtcNow.AddSeconds(90);
        while ((await checks.CheckHealthAsync(c => c.Tags.Contains("ready"), Ct)).Status != HealthStatus.Healthy && DateTime.UtcNow < until)
            await Task.Delay(250, Ct);
        return host;
    }

    private static async Task WaitAsync(Func<bool> done, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
            await Task.Delay(100, Ct);
        done().Should().BeTrue("the condition should hold within {0} seconds", seconds);
    }

    /// <summary>The first record on <paramref name="topic"/> with <paramref name="key"/>, or null after a minute.</summary>
    private ConsumeResult<string, string> ReadOne(string topic, string key)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = "it-reader-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
        consumer.Subscribe(topic);
        var until = DateTime.UtcNow.AddSeconds(60);
        try
        {
            while (DateTime.UtcNow < until)
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message?.Key == key)
                    return result;
            }

            return null;
        }
        finally
        {
            consumer.Close();
        }
    }

    [Fact]
    public async Task Bus_Should_DeliverPlainJsonWithKeyAndHeaders_When_AnEventIsPublished()
    {
        using var host = await StartHostAsync();
        var probe = new KafkaProbe { ContractId = "C-plain", CorrelationId = "corr-plain" };

        await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(probe, Ct);
        await WaitAsync(() => KafkaProbeConsumer.Applied.ContainsKey(probe.Id));

        var record = ReadOne(KafkaTopics.SlaDisputeChanged, "C-plain");
        record.Message.Key.Should().Be("C-plain");
        using var json = JsonDocument.Parse(record.Message.Value);
        json.RootElement.GetProperty("contractId").GetString().Should().Be("C-plain");
        record.Message.Headers.Any(h => h.Key == "ariva-correlation-id").Should().BeTrue();
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Poison_Should_ReachTheDeadLetterTopicAfterRetries_When_TheConsumerKeepsFailing()
    {
        using var host = await StartHostAsync();
        var poison = new KafkaProbe { ContractId = "C-poison", Mode = "fail" };
        var healthy = new KafkaProbe { ContractId = "C-poison" };
        var bus = host.Services.GetRequiredService<ISvcMessageBus>();

        await bus.PublishAsync(poison, Ct);
        await bus.PublishAsync(healthy, Ct);
        await WaitAsync(() => KafkaProbeConsumer.Applied.ContainsKey(healthy.Id));

        KafkaProbeConsumer.Attempts[poison.Id].Should().Be(3, "one delivery and two retries");
        var record = ReadOne(KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged), "C-poison");
        record.Should().NotBeNull();
        var letter = JsonSerializer.Deserialize<DeadLetter>(record.Message.Value, EventCatalog.Json);
        letter.Topic.Should().Be(KafkaTopics.SlaDisputeChanged);
        letter.Partition.Should().Be(0);
        letter.Offset.Should().NotBeNull();
        letter.Consumer.Should().Be(GroupId);
        letter.ErrorMessage.Should().Be("poison C-poison");
        letter.BodyBase64.Should().NotBeEmpty();
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task UnreadableValue_Should_ReachTheDeadLetterTopic_When_ItIsNotJson()
    {
        using var host = await StartHostAsync();
        using (var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _kafka.BootstrapServers }).Build())
            await producer.ProduceAsync(KafkaTopics.SlaDisputeChanged, new Message<string, string> { Key = "C-garbage", Value = "{not json" }, Ct);

        var record = ReadOne(KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged), "C-garbage");

        record.Should().NotBeNull("a value the consumer cannot read is dead-lettered, not dropped and not blocking the partition");
        var letter = JsonSerializer.Deserialize<DeadLetter>(record.Message.Value, EventCatalog.Json);
        letter.Key.Should().Be("C-garbage");
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(letter.BodyBase64)).Should().Be("{not json");
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Redelivery_Should_BeSkippedByTheInbox_When_TheCheckpointWasLost()
    {
        var probe = new KafkaProbe { ContractId = "C-replay" };
        using (var first = await StartHostAsync())
        {
            await first.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(probe, Ct);
            await WaitAsync(() => KafkaProbeConsumer.Applied.ContainsKey(probe.Id));
            await first.StopAsync(Ct);
        }

        // As after a crash between the consumer's commit and the checkpoint: the group's offset goes back to the start.
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.BootstrapServers }).Build())
        {
            await admin.AlterConsumerGroupOffsetsAsync([
                new ConsumerGroupTopicPartitionOffsets(GroupId, [new TopicPartitionOffset(KafkaTopics.SlaDisputeChanged, 0, new Offset(0))])
            ]);
        }

        using var second = await StartHostAsync();
        // The redelivery is acknowledged without running the consumer, so watch the group's committed offset move on.
        await WaitAsync(() => CommittedOffset() >= 1, seconds: 60);

        KafkaProbeConsumer.Applied[probe.Id].Should().Be(1, "the redelivered event was found in the inbox and skipped");
        KafkaProbeConsumer.Attempts[probe.Id].Should().Be(1, "the consumer never saw the duplicate");
        Claims(probe.Id).Should().Be(1);
        await second.StopAsync(Ct);
    }

    private long CommittedOffset()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.BootstrapServers }).Build();
        var groups = admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(GroupId, [new TopicPartition(KafkaTopics.SlaDisputeChanged, 0)])])
            .GetAwaiter().GetResult();
        return groups.Single().Partitions.Single().Offset.Value;
    }

    private long Claims(Guid id)
    {
        using var connection = new Npgsql.NpgsqlConnection(postgres.ConnectionString(_database));
        connection.Open();
        using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM processed_event WHERE event_id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)command.ExecuteScalar()!;
    }
}
