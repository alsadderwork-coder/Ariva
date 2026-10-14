using System.Collections.Concurrent;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
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
using Microsoft.Extensions.Logging;
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

    /// <summary>The message id MassTransit read from the record's headers, per event id (ARV-167).</summary>
    public static readonly ConcurrentDictionary<Guid, Guid?> MessageIds = new();

    public Task Consume(ConsumeContext<KafkaProbe> context)
    {
        Attempts.AddOrUpdate(context.Message.Id, 1, (_, n) => n + 1);
        MessageIds[context.Message.Id] = context.MessageId;
        if (context.Message.Mode == "fail")
            throw new InvalidOperationException("poison " + context.Message.ContractId);
        Applied.AddOrUpdate(context.Message.Id, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }
}

/// <summary>A single-broker Kafka for the pipeline tests (KRaft is not needed for these).</summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = KafkaContainers.Builder().Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// ARV-020 end to end on a real broker (Testcontainers.Kafka) and PostgreSQL: an event published through the bus
/// reaches its consumer through the Ariva consume pipe; a poison message is retried, then lands on
/// <c>&lt;topic&gt;.dlq.v1</c> with its key, partition, offset and error, and the partition moves on; a redelivery after
/// a lost checkpoint (offsets rewound, as after a crash between the write and the checkpoint) is skipped by the inbox,
/// so the effect happens once; and the values on the wire are plain JSON with the outbox headers. ARV-167's contract: what
/// Ariva's own producer writes is what MassTransit's rider wrote (key, value, partition and every header a reader uses), and
/// it is consumed through the unchanged MassTransit consumer and inbox.
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
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Services.AddScoped<ICurrentUser, Ariva.IntegrationTests.Security.TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(KafkaProbe).Assembly)
            .Consume<KafkaProbe, KafkaProbeConsumer>(KafkaTopics.SlaDisputeChanged, Purpose));

        var host = builder.Build();
        await host.StartAsync(Ct);
        var checks = host.Services.GetRequiredService<HealthCheckService>();
        // A cold broker (the first test after the container starts) can take well over a minute to elect and provision.
        var until = DateTime.UtcNow.AddSeconds(240);
        HealthStatus status;
        while ((status = (await checks.CheckHealthAsync(c => c.Tags.Contains("ready"), Ct)).Status) != HealthStatus.Healthy && DateTime.UtcNow < until)
            await Task.Delay(250, Ct);
        status.Should().Be(HealthStatus.Healthy, "the bus and its rider are ready before a test publishes");
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

    /// <summary>
    /// ARV-167's contract test. The same event goes out twice: first through <see cref="ISvcMessageBus"/> (Ariva's own
    /// producer), then through a MassTransit rider producer set up exactly as Ariva's were before ARV-167 (the reference).
    /// The existing consumer applies the first through the inbox, with the message id MassTransit reads from the headers,
    /// and the inbox drops the second as the same event. On the wire the two records have the same key, value and partition,
    /// and the same headers except the three MassTransit addressing headers the rider added and nothing reads. With tracing
    /// on, both write the send span's id in <c>MT-Activity-Id</c>, and MassTransit's receive span is a child of Ariva's send
    /// span (the trace continues across Kafka). A dead letter sent both ways compares the same way.
    /// </summary>
    [Fact]
    public async Task Producer_Should_WriteWhatTheRiderWroteAndBeConsumedThroughTheInbox_When_AnEventIsPublished()
    {
        var spans = new ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name is "MassTransit" or KafkaProduce.ActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var host = await StartHostAsync();
        using var rider = await StartRiderReferenceAsync();
        var key = "C-contract-" + Guid.NewGuid().ToString("N")[..8];
        var probe = new KafkaProbe { ContractId = key, CorrelationId = "corr-contract", CausationId = "cause-contract" };

        await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(probe, Ct);
        System.Diagnostics.Activity.Current.Should().BeNull("a send leaves the caller's current span as it was");
        await WaitAsync(() => KafkaProbeConsumer.Applied.ContainsKey(probe.Id));
        KafkaProbeConsumer.MessageIds[probe.Id].Should().Be(probe.Id, "MassTransit reads the message id from the MessageId header");
        var send = spans.Single(a => a.Source.Name == KafkaProduce.ActivitySourceName && (string)a.GetTagItem("messaging.message.id") == probe.Id.ToString("D"));
        await WaitAsync(() => spans.Any(a => a.Source.Name == "MassTransit" && a.ParentSpanId == send.SpanId));
        spans.First(a => a.Source.Name == "MassTransit" && a.ParentSpanId == send.SpanId).TraceId.Should().Be(send.TraceId,
            "MassTransit's receive span continues the send's trace");

        await RiderProduceAsync(rider, KafkaTopics.SlaDisputeChanged, key, probe, probe.Id, OutboxHeaders.For(probe));
        var records = ReadAll(KafkaTopics.SlaDisputeChanged, key, 2);
        records.Should().HaveCount(2, "the event went out once each way");
        var (ours, theirs) = (records[0], records[1]);
        await WaitAsync(() => CommittedOffset() > theirs.Offset.Value, seconds: 60);
        KafkaProbeConsumer.Applied[probe.Id].Should().Be(1, "the inbox took the rider's copy for the same event");
        KafkaProbeConsumer.Attempts[probe.Id].Should().Be(1, "the consumer never saw the copy");
        Claims(probe.Id).Should().Be(1);

        SameOnTheWire(ours, theirs);
        HeadersOf(ours).Should().Equal(
            (OutboxHeaders.EventType, nameof(KafkaProbe)),
            (OutboxHeaders.EventVersion, "1"),
            (OutboxHeaders.CorrelationId, "corr-contract"),
            (OutboxHeaders.CausationId, "cause-contract"),
            (KafkaProduce.ActivityIdHeader, send.Id),
            (KafkaProduce.MessageIdHeader, probe.Id.ToString("D")));

        var letter = DeadLetters.Create(KafkaTopics.SlaDisputeChanged, 0, 9, key + "-dead", [1, 2, 3], [new("h1", "v1")], GroupId,
            new InvalidOperationException("contract"), DateTime.UtcNow);
        await host.Services.GetRequiredService<IDeadLetterSink>().SendAsync(letter, Ct);
        await RiderProduceAsync(rider, KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged), letter.Key, letter, Guid.CreateVersion7(), new Dictionary<string, string>());
        var letters = ReadAll(KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged), letter.Key, 2);
        letters.Should().HaveCount(2);
        SameOnTheWire(letters[0], letters[1], sameMessageId: false);
        HeadersOf(letters[0]).Select(h => h.Name).Should().Equal(KafkaProduce.ActivityIdHeader, KafkaProduce.MessageIdHeader);
        await host.StopAsync(Ct);
        await rider.StopAsync(Ct);
    }

    /// <summary>
    /// Ours against the rider's: the same key, value and partition, both stamped by the client; the same headers in the same
    /// order, once the rider's three addressing headers are set aside. Those three must be there on the rider's side, so the
    /// comparison is not empty by accident.
    /// </summary>
    private static void SameOnTheWire(ConsumeResult<byte[], byte[]> ours, ConsumeResult<byte[], byte[]> theirs, bool sameMessageId = true)
    {
        string[] addressing = ["ConversationId", "DestinationAddress", "SourceAddress"];
        ours.Message.Key.Should().Equal(theirs.Message.Key, "the key is the same UTF-8 bytes");
        ours.Message.Value.Should().Equal(theirs.Message.Value, "the value is the same JSON");
        ours.Partition.Should().Be(theirs.Partition, "the same key lands on the same partition");
        ours.Message.Timestamp.Type.Should().Be(TimestampType.CreateTime);
        theirs.Message.Timestamp.Type.Should().Be(TimestampType.CreateTime);
        var theirHeaders = HeadersOf(theirs);
        theirHeaders.Select(h => h.Name).Should().Contain(addressing, "the rider wrote MassTransit's addressing headers");
        var kept = theirHeaders.Where(h => !addressing.Contains(h.Name)).ToList();
        HeadersOf(ours).Select(h => h.Name).Should().Equal(kept.Select(h => h.Name), "every header a reader uses is written, in the rider's order");
        // Each send has its own span, so MT-Activity-Id differs by value; a dead letter's message id is new for each send.
        string[] own = sameMessageId ? [KafkaProduce.ActivityIdHeader] : [KafkaProduce.ActivityIdHeader, KafkaProduce.MessageIdHeader];
        HeadersOf(ours).Where(h => !own.Contains(h.Name)).Should().Equal(kept.Where(h => !own.Contains(h.Name)), "and with the rider's values");

        HeadersOf(ours).Select(h => h.Name).Should().NotContain(addressing, "nothing in Ariva reads them");
    }

    private static List<(string Name, string Value)> HeadersOf(ConsumeResult<byte[], byte[]> record) =>
        [.. record.Message.Headers.Select(h => (h.Key, System.Text.Encoding.UTF8.GetString(h.GetValueBytes())))];

    /// <summary>The first <paramref name="count"/> records on <paramref name="topic"/> with <paramref name="key"/>, as bytes.</summary>
    private List<ConsumeResult<byte[], byte[]>> ReadAll(string topic, string key, int count)
    {
        using var consumer = new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = "it-reader-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
        consumer.Subscribe(topic);
        var found = new List<ConsumeResult<byte[], byte[]>>();
        var until = DateTime.UtcNow.AddSeconds(60);
        try
        {
            while (found.Count < count && DateTime.UtcNow < until)
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message?.Key is { } bytes && System.Text.Encoding.UTF8.GetString(bytes) == key)
                    found.Add(result);
            }

            return found;
        }
        finally
        {
            consumer.Close();
        }
    }

    /// <summary>
    /// A MassTransit rider producer for the probe's topic and its dead-letter topic, registered as Ariva's were before
    /// ARV-167 (the same client settings and JSON value serializer), on a bus of its own.
    /// </summary>
    private async Task<IHost> StartRiderReferenceAsync()
    {
        var settings = new KafkaSettings { Enabled = true, BootstrapServers = _kafka.BootstrapServers, ServiceName = "it-rider" };
        var config = KafkaClientConfig.Producer(settings);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMassTransit(bus =>
        {
            bus.DisableUsageTelemetry();
            bus.UsingInMemory((context, memory) => memory.ConfigureEndpoints(context));
            bus.AddRider(rider =>
            {
                rider.AddProducer<string, KafkaProbe>(KafkaTopics.SlaDisputeChanged, config,
                    (_, producer) => producer.SetValueSerializer(new JsonValueSerializer<KafkaProbe>()));
                rider.AddProducer<string, DeadLetter>(KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged), config,
                    (_, producer) => producer.SetValueSerializer(new JsonValueSerializer<DeadLetter>()));
                rider.UsingKafka((_, kafka) =>
                {
                    kafka.Host(settings.BootstrapServers);
                    kafka.ClientId = $"ariva-{settings.ServiceName}";
                    kafka.Acks = Acks.All;
                    kafka.MessageMaxBytes = KafkaSettings.MaxMessageBytes;
                });
            });
        });
        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    /// <summary>A produce through the reference rider, as Ariva's <c>KafkaProduce</c> did before ARV-167.</summary>
    private static async Task RiderProduceAsync<T>(IHost rider, string topic, string key, T value, Guid messageId, IReadOnlyDictionary<string, string> headers)
        where T : class
    {
        await using var scope = rider.Services.CreateAsyncScope();
        var producer = scope.ServiceProvider.GetRequiredService<ITopicProducerProvider>().GetProducer<string, T>(new Uri($"topic:{topic}"));
        await producer.Produce(key, value, Pipe.Execute<KafkaSendContext<string, T>>(context =>
        {
            context.MessageId = messageId;
            foreach (var (name, header) in headers)
                context.Headers.Set(name, header);
        }), Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
    }

    /// <summary>
    /// ARV-167, the lead's decision: the relay waits for the producer, not for MassTransit's bus, since sending does not need
    /// the bus. The bus is stopped (not Healthy); the producer is started and fine, so the relay drains the outbox: the row is
    /// marked sent and its record is on its topic. Before, the relay skipped every pass while the bus was not Healthy, so an
    /// unhealthy bus stopped the outbox of the whole host. (ARV-167 made the bus unhealthy with a consumer of a missing
    /// topic, which faulted the bus's start; since ARV-176 a missing topic no longer does, so the test stops the bus.)
    /// </summary>
    [Fact]
    public async Task Relay_Should_DrainTheOutbox_When_TheBusIsNotHealthyButTheProducerIs()
    {
        using var host = await StartHostAsync();
        var bus = host.Services.GetRequiredService<IBusControl>();
        await bus.StopAsync(Ct);
        var produce = host.Services.GetRequiredService<KafkaProduce>();
        produce.IsReady.Should().BeTrue("the host started its producer");
        var key = "C-relay-" + Guid.NewGuid().ToString("N")[..8];
        var probe = new KafkaProbe { ContractId = key, CorrelationId = "corr-relay" };

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            unitOfWork.StorageProvider.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
            await new Ariva.Infra.Messaging.Outbox.NHibernateDomainEventOutbox(host.Services.GetRequiredService<EventCatalog>(), TimeProvider.System)
                .WriteAsync(unitOfWork.StorageProvider, [probe], Ct);
            unitOfWork.PromiseToCommit();
            await unitOfWork.EndAsync(Ct);
        }

        bus.CheckHealth().Status.Should().NotBe(BusHealthStatus.Healthy, "the bus is not Healthy before the relay sends");
        await WaitAsync(() => Sent(key), seconds: 90);
        bus.CheckHealth().Status.Should().NotBe(BusHealthStatus.Healthy, "nor when the relay has sent the row");
        var record = ReadOne(KafkaTopics.SlaDisputeChanged, key);
        record.Should().NotBeNull("the row is on its topic");
        using (var json = JsonDocument.Parse(record.Message.Value))
            json.RootElement.GetProperty("id").GetGuid().Should().Be(probe.Id);
        await host.StopAsync(Ct);
    }

    private bool Sent(string key)
    {
        using var connection = new Npgsql.NpgsqlConnection(postgres.ConnectionString(_database));
        connection.Open();
        using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM outbox_message WHERE message_key = @key AND sent_on IS NOT NULL", connection);
        command.Parameters.AddWithValue("key", key);
        return (long)command.ExecuteScalar()! == 1;
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
