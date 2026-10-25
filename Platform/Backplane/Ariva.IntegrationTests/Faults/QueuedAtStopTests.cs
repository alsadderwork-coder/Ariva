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
using Microsoft.Extensions.Logging;

namespace Ariva.IntegrationTests.Faults;

/// <summary>A probe of the stop's records (ARV-253), on topics no other test consumes.</summary>
[KafkaTopic(KafkaTopics.ForecastPublished)]
public sealed class QueuedProbe : EventBase
{
    public string Key { get; set; }

    public override string GetPartitionKey() => Key;
}

/// <summary>The slow key takes four seconds and completes; every other key completes at once.</summary>
public sealed class QueuedProbeConsumer : IConsumer<QueuedProbe>
{
    public static string SlowKey { get; set; }

    public static readonly ConcurrentQueue<string> Received = new();

    public async Task Consume(ConsumeContext<QueuedProbe> context)
    {
        Received.Enqueue(context.Message.Key);
        if (context.Message.Key == SlowKey)
            await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
    }
}

/// <summary>
/// ARV-253 on real Kafka: a record waiting behind one in hand when its endpoint begins to stop is not lost. MassTransit
/// 8.5.11 never handles such a record (<c>KafkaMessageConsumer.Handle</c> returns at once for a stopping consumer) and cancels
/// it when the consumer closes; its checkpointer passes a cancelled record as it passes a faulted one, so with lanes by key a
/// later record of another lane that had completed meanwhile was committed past it, and the record was consumed by no host
/// (found by ARV-177's probe: the group's offset 4 with the record at 2). With one lane per consumer the records of a
/// partition complete in the order they were fetched, so after the stop the group's committed offset is the waiting record's
/// own and the next host consumes it (the first case, with eight lanes as before, failed every run: offset 4 committed with
/// the record at 2). The second case has two partitions and two consumers per endpoint: a record of the other partition
/// completes while the slow one is in hand (in parallel, on the other consumer), and still nothing of the slow record's
/// partition is passed.
/// <para>
/// The records are produced to chosen partitions (the event's JSON and its message id header, as Ariva's producer writes
/// them); every wait is bounded.
/// </para>
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class QueuedAtStopTests(FaultsFixture faults)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private void Done(string step) =>
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step} at {_clock.Elapsed.TotalSeconds:F1} s"));

    private async Task<IHost> StartHostAsync(string topic, string purpose, int consumers)
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
            ["Kafka:ServiceName"] = "queued",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Consumers:RetryCount"] = "0",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1",
            ["Kafka:Consumers:ConsumersPerEndpoint"] = consumers.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(QueuedProbe).Assembly)
            .Consume<QueuedProbe, QueuedProbeConsumer>(topic, purpose));
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done("a host started");
        return host;
    }

    private IAdminClient Admin() => new AdminClientBuilder(new AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();

    /// <summary>The topic with at least <paramref name="partitions"/> partitions (another test's host may have created it with one).</summary>
    private async Task EnsurePartitionsAsync(string topic, int partitions)
    {
        using var admin = Admin();
        var existing = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.FirstOrDefault(t => t.Topic == topic && t.Error.Code == ErrorCode.NoError);
        if (existing is null)
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = partitions, ReplicationFactor = 1 }]);
        else if (existing.Partitions.Count < partitions)
            await admin.CreatePartitionsAsync([new PartitionsSpecification { Topic = topic, IncreaseTo = partitions }]);
        Done($"{topic} has {partitions} partitions");
    }

    private async Task<Dictionary<int, (long End, long Committed)>> OffsetsAsync(string topic, string group, int partitions)
    {
        using var admin = Admin();
        var parts = Enumerable.Range(0, partitions).Select(p => new TopicPartition(topic, new Partition(p))).ToList();
        var latest = await admin.ListOffsetsAsync(parts.Select(p => new TopicPartitionOffsetSpec { TopicPartition = p, OffsetSpec = OffsetSpec.Latest() }),
            new ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var committed = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, parts)],
            new ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        return parts.ToDictionary(p => p.Partition.Value, p => (
            latest.ResultInfos.Single(r => r.TopicPartitionOffsetError.Partition == p.Partition).TopicPartitionOffsetError.Offset.Value,
            committed.Single().Partitions.Single(c => c.Partition == p.Partition).Offset.Value));
    }

    /// <summary>Produces a probe to a chosen partition as Ariva's producer writes it; returns its offset.</summary>
    private async Task<long> ProduceAsync(IProducer<string, byte[]> producer, string topic, int partition, string key)
    {
        var probe = new QueuedProbe { Key = key };
        var result = await producer.ProduceAsync(new TopicPartition(topic, new Partition(partition)), new Message<string, byte[]>
        {
            Key = key,
            Value = KafkaProduce.ValueOf(probe, typeof(QueuedProbe)),
            Headers = KafkaProduce.HeadersOf(probe.Id, null)
        }, Ct);
        return result.Offset.Value;
    }

    private static int Count(string key) => QueuedProbeConsumer.Received.Count(r => r == key);

    /// <summary>The lane MassTransit's rider gave a key with eight lanes (Murmur3 of the key), as before ARV-253.</summary>
    private static long LaneOf(string key) => new MassTransit.Middleware.Murmur3UnsafeHashGenerator().Hash(System.Text.Encoding.UTF8.GetBytes(key)) % 8;

    private static string Key(string prefix, string run, Func<string, bool> fits)
    {
        // One key in eight fits; 10,000 tries miss with a chance of (7/8)^10000.
        for (var i = 0; i < 10_000; i++)
        {
            var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{prefix}-{run}-{i}");
            if (fits(key))
                return key;
        }

        throw new InvalidOperationException($"No key for {prefix} in 10,000 tries.");
    }

    private async Task WaitForAsync(Func<bool> condition, TimeSpan bound, string what)
    {
        var waited = Stopwatch.StartNew();
        while (!condition() && waited.Elapsed < bound)
            await Task.Delay(100, Ct);
        Done($"{what}: {(condition() ? "yes" : "no")}");
    }

    [Theory(Timeout = 240_000)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Stop_Should_PassNoRecordItNeverHandled_When_RecordsWaitBehindOneInHand(int partitions, int consumers)
    {
        var topic = partitions == 1 ? KafkaTopics.ForecastPublished : KafkaTopics.ForecastArrivalWave;
        var purpose = partitions == 1 ? "queued-one" : "queued-two";
        var group = "ariva-queued." + purpose;
        var run = Guid.NewGuid().ToString("N")[..8];
        var slow = "S-" + run;
        // Keys chosen as MassTransit's lanes by key would split them (the waiting record in the slow one's lane, the record
        // behind it in another), so that the same test fails with lanes (shown in ARV-253 by setting them back) and passes with one.
        var queued = Key("Q", run, k => LaneOf(k) == LaneOf(slow));
        var behind = Key("B", run, k => LaneOf(k) != LaneOf(slow));
        var parallel = "P-" + run;
        QueuedProbeConsumer.SlowKey = slow;
        await EnsurePartitionsAsync(topic, partitions);
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        var host = await StartHostAsync(topic, purpose, consumers);
        var stopped = false;
        try
        {
            // The group reads and commits what is already on the topic first.
            await ProduceAsync(producer, topic, 0, "F-" + run);
            var caughtUp = Stopwatch.StartNew();
            while ((await OffsetsAsync(topic, group, partitions)).Values.Any(o => o.Committed < o.End && o.End > 0) && caughtUp.Elapsed < TimeSpan.FromSeconds(60))
                await Task.Delay(250, Ct);
            Done("caught up");
            (await OffsetsAsync(topic, group, partitions)).Values.Should().OnlyContain(o => o.End == 0 || o.Committed == o.End,
                "the group has read and committed what was on the topic, so the slow record is the first one pending");

            // The slow record in hand on partition 0 and two records waiting behind it there; with two partitions, a record
            // on partition 1 (the other consumer) too.
            var slowOffset = await ProduceAsync(producer, topic, 0, slow);
            await WaitForAsync(() => Count(slow) == 1, TimeSpan.FromSeconds(30), "the slow record in hand");
            Count(slow).Should().Be(1, "the stop must find the slow record in hand");
            var queuedOffset = await ProduceAsync(producer, topic, 0, queued);
            await ProduceAsync(producer, topic, 0, behind);
            if (partitions > 1)
            {
                await ProduceAsync(producer, topic, 1, parallel);
                await WaitForAsync(() => Count(parallel) == 1, TimeSpan.FromSeconds(10), "the other partition's record consumed while the slow one is in hand");
                Count(parallel).Should().Be(1, "the other consumer handles its partition in parallel");
            }
            else
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Count(queued).Should().Be(0, "the waiting record is behind the slow one");

            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                await host.StopAsync(stop.Token);
            await Task.Run(host.Dispose, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            stopped = true;
            var after = await OffsetsAsync(topic, group, partitions);
            Done($"stopped: slow {Count(slow)}, queued {Count(queued)}, behind {Count(behind)}; partition 0 end {after[0].End}, committed {after[0].Committed} (slow {slowOffset}, queued {queuedOffset})");
            after[0].Committed.Should().BeLessThanOrEqualTo(Count(queued) > 0 ? after[0].End : queuedOffset,
                "nothing of the slow record's partition is committed past a record that was never handled");

            var next = await StartHostAsync(topic, purpose, consumers);
            try
            {
                await WaitForAsync(() => Count(queued) >= 1 && Count(behind) >= 1, TimeSpan.FromSeconds(60), "every record consumed by one host or the other");
                Count(queued).Should().BeGreaterThan(0, "the waiting record is delivered again after the restart, not passed");
                Count(behind).Should().BeGreaterThan(0);
                Count(slow).Should().BeInRange(1, 2, "the slow record completed at the stop; its own commit may not have been made before the consumer closed");
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await next.StopAsync(stop.Token);
                await Task.Run(next.Dispose, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            }
        }
        finally
        {
            QueuedProbeConsumer.SlowKey = null;
            if (!stopped)
                _ = Task.Run(host.Dispose, CancellationToken.None);
        }
    }
}
