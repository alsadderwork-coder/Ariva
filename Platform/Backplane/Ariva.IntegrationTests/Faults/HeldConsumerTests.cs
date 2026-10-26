using System.Collections.Concurrent;
using System.Diagnostics;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging.Kafka;
using Ariva.IntegrationTests.Security;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.IntegrationTests.Faults;

/// <summary>A probe of a held consumer (ARV-254), on a topic no other test consumes.</summary>
[KafkaTopic(KafkaTopics.TopologyDeskChanged)]
public sealed class HeldProbe : EventBase
{
    public string Key { get; set; }

    public override string GetPartitionKey() => Key;
}

/// <summary>The slow key is held for <see cref="Hold"/> the first time it is handled; every other record completes at once.</summary>
public sealed class HeldProbeConsumer : IConsumer<HeldProbe>
{
    private static int _held;

    public static string SlowKey { get; set; }

    public static TimeSpan Hold { get; set; }

    public static readonly ConcurrentQueue<string> Received = new();

    public static void Reset(string slowKey, TimeSpan hold) => (SlowKey, Hold, _held) = (slowKey, hold, 0);

    public async Task Consume(ConsumeContext<HeldProbe> context)
    {
        Received.Enqueue(context.Message.Key);
        if (context.Message.Key == SlowKey && Interlocked.Exchange(ref _held, 1) == 0)
            await Task.Delay(Hold, CancellationToken.None);
    }
}

/// <summary>
/// ARV-254 on real Kafka. MassTransit 8.5.11's consume loop (<c>KafkaMessageConsumer.Consume</c>) takes a slot of its prefetch
/// before it polls again, so while one record is held and the records fetched behind it fill the prefetch, the consumer does
/// not poll: a hold longer than <c>max.poll.interval.ms</c> takes it out of its group, and after it rejoins it is given the
/// records it had not committed again, the held one among them. With the poll interval above the hold, every record is
/// handled once. The two cases run a rider of their own (a prefetch of 2, a 6 s session, poll intervals of 8 s and 40 s against
/// a 20 s hold), so they take seconds, not the minutes of Ariva's own settings, whose interval is set above the longest hold
/// they allow (<c>ConsumerSettings.MaxPollInterval</c>, ConsumerHoldTests). The third case is an Ariva host: while its
/// consumer is held, <c>KafkaConsumerHolds</c> reports the hold of its endpoint, and 0 once the record completed.
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class HeldConsumerTests(FaultsFixture faults)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private void Done(string step) =>
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step} at {_clock.Elapsed.TotalSeconds:F1} s"));

    [Theory(Timeout = 180_000)]
    [InlineData(8, 2)]
    [InlineData(40, 1)]
    public async Task Consumer_Should_HandleTheHeldRecordAgainOnlyWhenTheHoldOutlastsThePollInterval_When_ThePrefetchIsFull(int pollSeconds, int deliveries)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var topic = $"ariva-it.held.{run}";
        var slow = "S-" + run;
        HeldProbeConsumer.Reset(slow, TimeSpan.FromSeconds(20));
        await CreateTopicAsync(topic);
        using var host = await StartRiderAsync(topic, $"ariva-it.held-{run}", TimeSpan.FromSeconds(pollSeconds));
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        try
        {
            // The slow record first, then more than the prefetch of 2 behind it, so the loop stops polling while it is held.
            await ProduceAsync(producer, topic, slow);
            for (var i = 0; i < 6; i++)
                await ProduceAsync(producer, topic, $"F{i}-{run}");

            var last = $"F5-{run}";
            await WaitForAsync(() => Count(last) >= 1 && (deliveries == 1 || Count(slow) >= deliveries), TimeSpan.FromSeconds(120), "every record handled");
            // A moment more for any later delivery of the held record.
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            Done($"slow handled {Count(slow)} times with a {pollSeconds} s poll interval");

            if (deliveries == 1)
                Count(slow).Should().Be(1, "with the poll interval above the hold the consumer stays in its group");
            else
                Count(slow).Should().BeGreaterThanOrEqualTo(2, "the hold outlasted the poll interval with the prefetch full: the consumer left its group and was given the record again");
            Enumerable.Range(0, 6).Should().OnlyContain(i => Count($"F{i}-{run}") >= 1, "every record is handled");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await host.StopAsync(stop.Token);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Holds_Should_ReportTheEndpointsHold_When_ItsConsumerIsHeld()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var slow = "S-" + run;
        HeldProbeConsumer.Reset(slow, TimeSpan.FromSeconds(8));
        await CreateTopicAsync(KafkaTopics.TopologyDeskChanged);
        using var host = await StartHostAsync();
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        var holds = host.Services.GetRequiredService<KafkaConsumerHolds>();
        try
        {
            await ProduceAsync(producer, KafkaTopics.TopologyDeskChanged, slow);
            await WaitForAsync(() => Count(slow) == 1, TimeSpan.FromSeconds(60), "the slow record in hand");
            await Task.Delay(TimeSpan.FromSeconds(4), Ct);
            var during = Hold(holds);
            await WaitForAsync(() => Hold(holds) == 0, TimeSpan.FromSeconds(30), "the hold over");

            Done($"held {during:F1} s");
            during.Should().BeInRange(3, 9, "the endpoint reports the record it has held for about four seconds");
            Hold(holds).Should().Be(0, "the record completed");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await host.StopAsync(stop.Token);
        }
    }

    private static double Hold(KafkaConsumerHolds holds) =>
        holds.Holds().Single(h => h.Topic == KafkaTopics.TopologyDeskChanged && h.Group == "ariva-held.held-probe").Seconds;

    private static int Count(string key) => HeldProbeConsumer.Received.Count(r => r == key);

    /// <summary>A rider of its own with a small prefetch and session, Ariva's value reader, and the given poll interval.</summary>
    private async Task<IHost> StartRiderAsync(string topic, string group, TimeSpan maxPollInterval)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMassTransit(bus =>
        {
            bus.DisableUsageTelemetry();
            bus.UsingInMemory((context, memory) => memory.ConfigureEndpoints(context));
            bus.AddRider(rider =>
            {
                rider.AddConsumer<HeldProbeConsumer>();
                rider.UsingKafka((context, kafka) =>
                {
                    kafka.Host(faults.KafkaDirectBootstrap);
                    kafka.TopicEndpoint<string, HeldProbe>(topic, group, endpoint =>
                    {
                        endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
                        endpoint.ConcurrentMessageLimit = 1;
                        endpoint.ConcurrentDeliveryLimit = 1;
                        endpoint.PrefetchCount = 2;
                        endpoint.SessionTimeout = TimeSpan.FromSeconds(6);
                        endpoint.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        endpoint.MaxPollInterval = maxPollInterval;
                        endpoint.CheckpointInterval = TimeSpan.FromSeconds(1);
                        endpoint.CheckpointMessageCount = 1;
                        endpoint.SetValueDeserializer(new JsonValueSerializer<HeldProbe>());
                        endpoint.ConfigureConsumer<HeldProbeConsumer>(context);
                    });
                });
            });
        });
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done($"a rider started with a {maxPollInterval.TotalSeconds:F0} s poll interval");
        return host;
    }

    /// <summary>An Ariva host consuming the probe through the Ariva consume pipe.</summary>
    private async Task<IHost> StartHostAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = faults.Host,
            ["Database:Port"] = faults.PostgresPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = FaultsFixture.Database,
            ["Database:Username"] = faults.PostgresUsername,
            ["Database:Password"] = faults.PostgresPassword,
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = faults.KafkaDirectBootstrap,
            ["Kafka:ServiceName"] = "held",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Consumers:RetryCount"] = "0",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1"
        });
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(HeldProbe).Assembly)
            .Consume<HeldProbe, HeldProbeConsumer>(KafkaTopics.TopologyDeskChanged, "held-probe"));
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done("an Ariva host started");
        return host;
    }

    private async Task CreateTopicAsync(string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        if (admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Any(t => t.Topic == topic && t.Error.Code == ErrorCode.NoError))
            return;
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Another case made it first.
        }
    }

    /// <summary>Produces a probe to partition 0 as Ariva's producer writes it.</summary>
    private static async Task ProduceAsync(IProducer<string, byte[]> producer, string topic, string key)
    {
        var probe = new HeldProbe { Key = key };
        await producer.ProduceAsync(new TopicPartition(topic, new Partition(0)), new Message<string, byte[]>
        {
            Key = key,
            Value = KafkaProduce.ValueOf(probe, typeof(HeldProbe)),
            Headers = KafkaProduce.HeadersOf(probe.Id, null)
        }, Ct);
    }

    private async Task WaitForAsync(Func<bool> condition, TimeSpan bound, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition() && waited.Elapsed < bound)
            await Task.Delay(100, Ct);
        Done($"{what}: {(condition() ? "yes" : "no")}");
    }
}
