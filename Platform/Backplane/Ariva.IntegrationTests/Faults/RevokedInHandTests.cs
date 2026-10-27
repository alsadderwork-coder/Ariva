using System.Collections.Concurrent;
using System.Diagnostics;
using Ariva.Infra.Messaging.Kafka;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.IntegrationTests.Faults;

/// <summary>A probe for a rebalance during a slow record (ARV-255).</summary>
public sealed class RevokeProbe
{
    public string Key { get; set; }
}

/// <summary>
/// Every delivery each host's consumer received, noted on entry; the first delivery of the slow key is held, and whether
/// its own token was cancelled at any moment of the hold is noted too.
/// </summary>
public static class RevokeLedger
{
    private static int _slowCancelled;
    private static int _slowHeld;
    private static int _slowDone;

    public static readonly ConcurrentQueue<(string Host, string Key, TimeSpan At)> Received = new();

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static string SlowKey { get; private set; }

    public static TimeSpan Hold { get; private set; }

    /// <summary>Whether the slow record's own token was cancelled at any moment of its hold.</summary>
    public static bool SlowCancelled => Volatile.Read(ref _slowCancelled) == 1;

    /// <summary>Whether the hold of the slow record's first delivery has ended.</summary>
    public static bool SlowDone => Volatile.Read(ref _slowDone) == 1;

    public static TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TimeSpan Now => Clock.Elapsed;

    public static void Reset(string slowKey, TimeSpan hold)
    {
        Received.Clear();
        (SlowKey, Hold) = (slowKey, hold);
        Volatile.Write(ref _slowCancelled, 0);
        Volatile.Write(ref _slowHeld, 0);
        Volatile.Write(ref _slowDone, 0);
        Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Notes the delivery on entry; the slow key's first delivery is held, its token watched (not obeyed).</summary>
    public static async Task HandleAsync(string host, ConsumeContext<RevokeProbe> context)
    {
        Received.Enqueue((host, context.Message.Key, Now));
        if (context.Message.Key != SlowKey || Interlocked.Exchange(ref _slowHeld, 1) == 1)
            return;

        Started.TrySetResult();
        using (context.CancellationToken.Register(() => Volatile.Write(ref _slowCancelled, 1)))
            await Task.Delay(Hold, CancellationToken.None);
        Volatile.Write(ref _slowDone, 1);
    }
}

public sealed class RevokeProbeConsumerA : IConsumer<RevokeProbe>
{
    public Task Consume(ConsumeContext<RevokeProbe> context) => RevokeLedger.HandleAsync("A", context);
}

public sealed class RevokeProbeConsumerB : IConsumer<RevokeProbe>
{
    public Task Consume(ConsumeContext<RevokeProbe> context) => RevokeLedger.HandleAsync("B", context);
}

/// <summary>
/// ARV-255 on real Kafka: a rebalance never cancels a record in hand. Ariva's dead-letter filter takes any failure after
/// the message's own token was cancelled for the stop's (no dead letter, the record left for the next host), which is
/// right only while MassTransit 8.5.11 cancels a Kafka receive context when its consumer stops and never while it keeps
/// the partition. Its decompiled source (ADR-0018, note of 2026-10-26 on ARV-255) shows one cancel site,
/// <c>ConsumerAgent.CancelPendingConsumers</c>, reached only when the consumer agent stops; a revoke
/// (<c>ConsumerLockContext.OnUnAssigned</c>) closes the partition's checkpointer, which waits for the records it was given
/// to complete and commits them. Here host A holds a record for 12 s and host B joins the group 2 s into the hold; the
/// group is seen rebalancing while the record is still in hand, so the revoke meets it. The record completes with its
/// token never cancelled, is delivered once (every delivery is noted on entry, so a redelivery after the rebalance would
/// show), and the group's committed offset on its partition is past it; B then consumes a partition of its own.
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class RevokedInHandTests(FaultsFixture faults)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Notes a step on the ledger's clock, the one its deliveries are noted on.</summary>
    private static void Done(string step) =>
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step} at {RevokeLedger.Now.TotalSeconds:F1} s"));

    [Fact(Timeout = 180_000)]
    public async Task Rebalance_Should_LeaveTheRecordInHandUncancelledAndHandledOnce_When_AnotherHostJoinsDuringIt()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var topic = $"ariva-it.revoke.{run}";
        var group = $"ariva-it.revoke-{run}";
        var slow = "S-" + run;
        RevokeLedger.Reset(slow, TimeSpan.FromSeconds(12));
        await CreateTopicAsync(topic);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        using var a = await StartRiderAsync<RevokeProbeConsumerA>(topic, group, "A");
        IHost b = null;
        try
        {
            var slowOffset = (await ProduceAsync(producer, topic, 0, slow)).Offset.Value;
            await RevokeLedger.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Done("the slow record in hand on A");
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            b = await StartRiderAsync<RevokeProbeConsumerB>(topic, group, "B");

            // The rebalance B's join starts must be under way while A still holds the record, or no revoke met it.
            var rebalancingInHand = await RebalancingWhileInHandAsync(admin, group);
            Done($"the group rebalancing while the record was in hand: {rebalancingInHand}");

            // After the rebalance each host owns one partition: records to both, until B has handled one of its own.
            await WaitForAsync(() => RevokeLedger.SlowDone, TimeSpan.FromSeconds(60), "the slow record's hold over");
            for (var i = 0; i < 20 && !RevokeLedger.Received.Any(r => r.Host == "B"); i++)
            {
                await ProduceAsync(producer, topic, 0, $"P0-{i}-{run}");
                await ProduceAsync(producer, topic, 1, $"P1-{i}-{run}");
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }

            var firstOfB = RevokeLedger.Received.Where(r => r.Host == "B").Select(r => (TimeSpan?)r.At).FirstOrDefault();
            // Long enough for a redelivery of the slow record (it is noted on entry) to show.
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            var committed = await CommittedAsync(admin, group, new TopicPartition(topic, new Partition(0)));
            Done($"B's first record at {firstOfB?.TotalSeconds:F1} s, partition 0 committed at {committed}, the slow record at {slowOffset}");

            rebalancingInHand.Should().BeTrue("B joined while A held the record, so the revoke came during the hold");
            RevokeLedger.SlowCancelled.Should().BeFalse("a revoke closes the partition's checkpointer and never cancels the record in hand");
            RevokeLedger.Received.Where(r => r.Key == slow).Should().ContainSingle("the record was delivered once, to A, and not again after the rebalance")
                .Which.Host.Should().Be("A");
            committed.Should().BeGreaterThan(slowOffset, "the revoke waited for the record and committed it before the partition was released");
            firstOfB.Should().NotBeNull("B joined the group and was assigned a partition: the rebalance happened");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (b is not null)
            {
                await b.StopAsync(stop.Token);
                b.Dispose();
            }

            await a.StopAsync(stop.Token);
        }
    }

    /// <summary>Whether the group was seen preparing or completing a rebalance before the slow record's hold ended.</summary>
    private static async Task<bool> RebalancingWhileInHandAsync(IAdminClient admin, string group)
    {
        while (!RevokeLedger.SlowDone)
        {
            var described = await admin.DescribeConsumerGroupsAsync([group]);
            var state = described.ConsumerGroupDescriptions.Single().State;
            if (state is ConsumerGroupState.PreparingRebalance or ConsumerGroupState.CompletingRebalance)
                return !RevokeLedger.SlowDone;
            await Task.Delay(100, Ct);
        }

        return false;
    }

    /// <summary>The group's committed offset on a partition (-1001, Offset.Unset, when none).</summary>
    private static async Task<long> CommittedAsync(IAdminClient admin, string group, TopicPartition partition)
    {
        var offsets = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, [partition])]);
        return offsets.Single().Partitions.Single().Offset.Value;
    }

    /// <summary>A rider of its own: one lane, Ariva's value reader, a 6 s session and a poll interval above the hold.</summary>
    private async Task<IHost> StartRiderAsync<TConsumer>(string topic, string group, string name)
        where TConsumer : class, IConsumer<RevokeProbe>
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMassTransit(bus =>
        {
            bus.DisableUsageTelemetry();
            bus.UsingInMemory((context, memory) => memory.ConfigureEndpoints(context));
            bus.AddRider(rider =>
            {
                rider.AddConsumer<TConsumer>();
                rider.UsingKafka((context, kafka) =>
                {
                    kafka.Host(faults.KafkaDirectBootstrap);
                    kafka.TopicEndpoint<string, RevokeProbe>(topic, group, endpoint =>
                    {
                        endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
                        endpoint.ConcurrentMessageLimit = 1;
                        endpoint.ConcurrentDeliveryLimit = 1;
                        endpoint.SessionTimeout = TimeSpan.FromSeconds(6);
                        endpoint.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        endpoint.MaxPollInterval = TimeSpan.FromSeconds(60);
                        endpoint.CheckpointInterval = TimeSpan.FromSeconds(1);
                        endpoint.CheckpointMessageCount = 1;
                        endpoint.SetValueDeserializer(new JsonValueSerializer<RevokeProbe>());
                        endpoint.ConfigureConsumer<TConsumer>(context);
                    });
                });
            });
        });
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done($"host {name} started");
        return host;
    }

    private async Task CreateTopicAsync(string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 2, ReplicationFactor = 1 }]);
    }

    /// <summary>Produces a probe to the given partition as Ariva's producer writes it.</summary>
    private static async Task<DeliveryResult<string, byte[]>> ProduceAsync(IProducer<string, byte[]> producer, string topic, int partition, string key)
    {
        var probe = new RevokeProbe { Key = key };
        return await producer.ProduceAsync(new TopicPartition(topic, new Partition(partition)), new Message<string, byte[]>
        {
            Key = key,
            Value = KafkaProduce.ValueOf(probe, typeof(RevokeProbe)),
            Headers = KafkaProduce.HeadersOf(Guid.NewGuid(), null)
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
