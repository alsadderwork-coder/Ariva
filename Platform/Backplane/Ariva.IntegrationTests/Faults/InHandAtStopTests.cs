using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.IntegrationTests.Security;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ariva.IntegrationTests.Faults;

/// <summary>A probe on a topic no other test consumes (ARV-253), so a host whose stop outlasts its wait touches nothing of theirs.</summary>
[KafkaTopic(KafkaTopics.SlaEvaluationFinalised)]
public sealed class InHandProbe : EventBase
{
    public string Key { get; set; }

    public override string GetPartitionKey() => Key;
}

/// <summary>
/// The slow record's first delivery takes <see cref="SlowAttempt"/> and then fails, as an attempt waiting on a stalled
/// database fails at its command timeout; every other delivery, and that record's next one, completes at once.
/// </summary>
public sealed class InHandProbeConsumer : IConsumer<InHandProbe>
{
    public static readonly TimeSpan SlowAttempt = TimeSpan.FromSeconds(20);

    private static int _slowLeft;
    private static int _slowEnded;

    public static string SlowKey { get; private set; }

    public static bool SlowEnded => Volatile.Read(ref _slowEnded) == 1;

    public static readonly ConcurrentQueue<string> Received = new();

    public static void SlowOnce(string key)
    {
        SlowKey = key;
        Volatile.Write(ref _slowEnded, 0);
        Volatile.Write(ref _slowLeft, 1);
    }

    public async Task Consume(ConsumeContext<InHandProbe> context)
    {
        Received.Enqueue(context.Message.Key);
        if (context.Message.Key == SlowKey && Interlocked.Exchange(ref _slowLeft, 0) == 1)
        {
            try
            {
                // Not the message's token: a database call stalled past the stop does not end with it.
                await Task.Delay(SlowAttempt, CancellationToken.None);
                throw new TimeoutException("The database did not answer within its command timeout (ARV-253 probe).");
            }
            finally
            {
                Volatile.Write(ref _slowEnded, 1);
            }
        }
    }
}

/// <summary>
/// ARV-253 on real Kafka and PostgreSQL: a record in hand whose attempt outlasts the bus's stop wait (20 s against 11 s here:
/// a shutdown timeout of 26 s less the 15 s kept for what stops after the bus), with a later record of its partition
/// waiting behind it. MassTransit cancels the record in hand at the stop wait's end and the waiting one when the consumer
/// closes, and its checkpointer passes both as it passes a faulted record; with lanes by key a later record that had
/// completed in another lane was then committed past them. With one lane per consumer nothing after the record in hand
/// completes, so the committed offset stays at that record, and the next host consumes it and the record behind it. The
/// slow attempt's failure, which comes after the stop wait into a host being disposed, is the stop's, not the message's
/// (ARV-177a): with no retry after it (the second case) it used to be dead-lettered, a valid event kept in the outbox and
/// sent to the dead-letter topic by the next host's relay while that host processed the record as well; now no dead
/// letter is kept or sent in either case.
/// <para>
/// Every wait is for a condition within one budget (ARV-156).
/// </para>
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class InHandAtStopTests(FaultsFixture faults)
{
    private const string Purpose = "in-hand";
    private const string Group = "ariva-inhand." + Purpose;
    private static readonly string Topic = KafkaTopics.SlaEvaluationFinalised;
    private static readonly string DeadLetterTopic = KafkaTopics.DeadLetter(KafkaTopics.SlaEvaluationFinalised);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _steps = [];

    private Confluent.Kafka.IAdminClient Admin() =>
        new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();

    /// <summary>
    /// A host with a stop budget a slow attempt outlasts: the bus waits 11 s (26 less 15), which covers the retry schedule
    /// (one retry, 200 ms, up to 250 ms with MassTransit's jitter, ARV-268) and the dead letter's keep (10 s), as the host's
    /// start check requires, but not a 20 s attempt.
    /// </summary>
    private async Task<IHost> StartHostAsync(int retryCount)
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
            ["Kafka:ServiceName"] = "inhand",
            ["Kafka:ProvisionTopics"] = "true",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Outbox:PollMilliseconds"] = "200",
            ["Kafka:Consumers:RetryCount"] = retryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Kafka:Consumers:RetryMinMilliseconds"] = "200",
            ["Kafka:Consumers:RetryMaxMilliseconds"] = "200",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1",
            ["Hosting:ShutdownSeconds"] = "26",
            ["Hosting:GracePeriodSeconds"] = "31"
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(InHandProbe).Assembly)
            .Consume<InHandProbe, InHandProbeConsumer>(Topic, Purpose));
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done("a host started");
        return host;
    }

    /// <summary>The topic's end and the group's committed offset.</summary>
    private async Task<(long End, long Committed)> OffsetsAsync()
    {
        using var admin = Admin();
        var partition = new Confluent.Kafka.TopicPartition(Topic, new Confluent.Kafka.Partition(0));
        var latest = await Confluent.Kafka.IAdminClientExtensions.ListOffsetsAsync(admin,
            [new Confluent.Kafka.Admin.TopicPartitionOffsetSpec { TopicPartition = partition, OffsetSpec = Confluent.Kafka.Admin.OffsetSpec.Latest() }],
            new Confluent.Kafka.Admin.ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var committed = await admin.ListConsumerGroupOffsetsAsync([new Confluent.Kafka.ConsumerGroupTopicPartitions(Group, [partition])],
            new Confluent.Kafka.Admin.ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        return (latest.ResultInfos.Single().TopicPartitionOffsetError.Offset.Value, committed.Single().Partitions.Single().Offset.Value);
    }

    /// <summary>The outbox rows of kept dead letters for a key.</summary>
    private async Task<int> KeptRowsAsync(string key)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_message WHERE message_type = @type AND topic = @topic AND message_key = @key", sql);
        command.Parameters.AddWithValue("type", DeadLetters.OutboxMessageType);
        command.Parameters.AddWithValue("topic", DeadLetterTopic);
        command.Parameters.AddWithValue("key", key);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Done(string step)
    {
        var entry = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step} at {_clock.Elapsed.TotalSeconds:F1} s");
        _steps.Add(entry);
        TestContext.Current.TestOutputHelper?.WriteLine(entry);
    }

    private async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        while (!await condition())
        {
            if (_clock.Elapsed > Budget)
                throw new TimeoutException($"{what}: not within the test's {Budget.TotalSeconds:F0} s (done: {string.Join(", ", _steps)})");
            await Task.Delay(250, Ct);
        }

        Done(what);
    }

    private Task WaitForAsync(Func<bool> condition, string what) => WaitForAsync(() => Task.FromResult(condition()), what);

    private static int Count(string key) => InHandProbeConsumer.Received.Count(r => r == key);

    /// <summary>The lane MassTransit's rider gave a key with eight lanes (Murmur3 of the key), as before ARV-253.</summary>
    private static long LaneOf(string key) => new MassTransit.Middleware.Murmur3UnsafeHashGenerator().Hash(Encoding.UTF8.GetBytes(key)) % 8;

    private static string KeyOutside(string prefix, string run, string taken)
    {
        // Seven keys in eight fit; 10,000 tries miss with a chance of (1/8)^10000.
        for (var i = 0; i < 10_000; i++)
        {
            var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{prefix}-{run}-{i}");
            if (LaneOf(taken) != LaneOf(key))
                return key;
        }

        throw new InvalidOperationException($"No key for {prefix} in 10,000 tries.");
    }

    /// <summary>Records on the dead-letter topic for a key, up to the topic's end as it was when the read began (failing if it is not reached).</summary>
    private int LettersFor(string key)
    {
        using var consumer = new Confluent.Kafka.ConsumerBuilder<string, string>(new Confluent.Kafka.ConsumerConfig
        {
            BootstrapServers = faults.KafkaDirectBootstrap,
            GroupId = "it-dead-letter-reader-" + Guid.NewGuid().ToString("N")[..8],
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
        var partition = new Confluent.Kafka.TopicPartition(DeadLetterTopic, new Confluent.Kafka.Partition(0));
        var end = consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10)).High.Value;
        consumer.Assign(new Confluent.Kafka.TopicPartitionOffset(partition, Confluent.Kafka.Offset.Beginning));
        var count = 0;
        var reached = end == 0;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!reached && DateTime.UtcNow < until)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is not { } record)
                continue;
            if (record.Message.Key == key)
                count++;
            reached = record.Offset.Value >= end - 1;
        }

        consumer.Close();
        reached.Should().BeTrue("the dead-letter topic is read to its end");
        return count;
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1)] // the slow attempt has a retry after it
    [InlineData(0)] // the slow attempt is the last one (ARV-177a): before it, its failure was dead-lettered
    public async Task Stop_Should_PassNeitherTheRecordInHandNorTheOneBehindIt_When_AnAttemptOutlastsTheBusStopWait(int retryCount)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var slow = "S-" + run;
        // Outside the slow record's old lane, so that with lanes it would have completed while the slow one was in hand.
        var later = KeyOutside("L", run, slow);
        InHandProbeConsumer.SlowOnce(slow);
        var first = await StartHostAsync(retryCount);
        IHost next = null;
        var firstDisposed = false;
        try
        {
            // The group reads and commits what is already on the topic, so the slow record is the first one pending.
            await first.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new InHandProbe { Key = "F-" + run }, Ct);
            await WaitForAsync(async () => await OffsetsAsync() is { End: > 0 } o && o.Committed == o.End, "the consumer caught up and committed");
            var slowOffset = (await OffsetsAsync()).End;

            await first.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new InHandProbe { Key = slow }, Ct);
            await WaitForAsync(() => Count(slow) == 1, "the slow record's first attempt running");
            await first.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new InHandProbe { Key = later }, Ct);
            await WaitForAsync(async () => (await OffsetsAsync()).End == slowOffset + 2, "a later record of another key on the topic");
            await Task.Delay(TimeSpan.FromSeconds(2), Ct); // two checkpoint intervals
            Count(later).Should().Be(0, "the later record waits behind the slow one in the consumer's one lane");
            (await OffsetsAsync()).Committed.Should().Be(slowOffset, "nothing is committed past the pending slow record");

            // The stop: the bus waits 11 s and cancels the record in hand; the slow attempt runs on, then the host disposes.
            var stopping = Stopwatch.StartNew();
            using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                await first.StopAsync(shutdown.Token);
            Done($"the first host's StopAsync returned after {stopping.Elapsed.TotalSeconds:F1} s");
            InHandProbeConsumer.SlowEnded.Should().BeFalse("the bus's stop wait ended with the slow attempt still running");
            var disposal = Task.Run(first.Dispose, CancellationToken.None);
            await disposal.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            firstDisposed = true;
            Done("the first host disposed");

            await WaitForAsync(() => InHandProbeConsumer.SlowEnded, "the slow attempt ended");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct); // a checkpoint interval and more, had anything been let go
            (await OffsetsAsync()).Committed.Should().Be(slowOffset,
                "the record in hand was cancelled, and the record behind it never handled: neither is passed");
            Count(later).Should().Be(0, "the stopping host never handled the later record");
            Done($"after the first host: slow {Count(slow)}, later {Count(later)}, kept dead letters {await KeptRowsAsync(slow)}");

            next = await StartHostAsync(retryCount);
            await WaitForAsync(() => Count(slow) >= 2 && Count(later) >= 1, "both records delivered again to the next host");
            await WaitForAsync(async () => await OffsetsAsync() is var o && o.Committed == o.End, "every record committed");
            Count(slow).Should().Be(2, "delivered once more, after the restart, and completed there");
            Count(later).Should().Be(1, "the next host processes the later record, once");
            await Task.Delay(TimeSpan.FromSeconds(2), Ct); // the next host's relay polls every 200 ms
            (await KeptRowsAsync(slow)).Should().Be(0, "a failure after the stop wait is the stop's, not the message's: no dead letter is kept (ARV-177a)");
            LettersFor(slow).Should().Be(0, "and none reaches the dead-letter topic");
        }
        finally
        {
            if (next is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                await next.StopAsync(stop.Token);
                next.Dispose();
            }

            if (!firstDisposed)
                _ = Task.Run(first.Dispose, CancellationToken.None);
        }
    }
}
