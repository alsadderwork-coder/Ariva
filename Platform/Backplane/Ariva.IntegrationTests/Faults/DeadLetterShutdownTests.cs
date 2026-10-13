using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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

namespace Ariva.IntegrationTests.Faults;

/// <summary>Fails the poison message of the running test, cutting the broker off first so that its dead letter cannot be sent.</summary>
public sealed class PoisonProbeConsumer : IConsumer<OutageProbe>
{
    public static string PoisonKey { get; set; }

    /// <summary>Run once, by the first delivery of the poison message.</summary>
    internal static Func<Task> BeforeFailing;

    public static readonly ConcurrentQueue<(string Key, int Step)> Received = new();

    public async Task Consume(ConsumeContext<OutageProbe> context)
    {
        if (context.Message.Key != PoisonKey)
            return;
        Received.Enqueue((context.Message.Key, context.Message.Step));
        if (Interlocked.Exchange(ref BeforeFailing, null) is { } cut)
            await cut();
        throw new InvalidOperationException("Poison probe (ARV-166).");
    }
}

/// <summary>
/// ARV-166: the dead-letter filter's retry loop ends with the host. A message fails for good while the broker is cut off,
/// so its dead letter cannot be sent and the filter retries it; MassTransit waits for the message before its endpoint
/// stops and does not cancel the message's token, so before ARV-166 the loop kept the bus, the host and the process alive
/// (34 attempts in 15 minutes after a dispose in the ARV-156 diagnosis). Now the loop ends when the host stops, and also
/// when the host is only disposed (in both cases its endpoint's stop signals it, EndpointStopping), and the message, never
/// acknowledged, is delivered again after the restart and dead-lettered then (at-least-once). This test caught two faults
/// on the way: ended with any token but the message's own, MassTransit's retry filter retried the cancellation and (with no
/// retries configured, as here) its receive pipe rescued the failure and acknowledged the message; and ended on the host's
/// ApplicationStopping, before the bus chose the endpoints to stop, the endpoint (its consumer already ended by the cut)
/// reported Completed, was skipped by the stop, and its transport started a new consumer in the stopped host, which took
/// the partition from the restarted one. Either way the message never came back.
/// <para>
/// The poison message is the only record in flight, so nothing later on its partition can be checkpointed past it before
/// the endpoint stops; MassTransit's checkpointer would commit past a faulted record once a later one completes, the
/// residual risk recorded with ARV-166 (DeadLetterRetry, docs/architecture/overview.md).
/// </para>
/// <para>
/// The broker is cut only once the consumer has committed everything before the poison message, so the cut cannot land in
/// an offset commit (ARV-165 owns that race), and it is restored once the loop has ended, so the endpoint's own close does
/// not wait on an unreachable broker. Every wait is for a condition within one budget (ARV-156).
/// </para>
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class DeadLetterShutdownTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "kafka";
    private const string Purpose = "dead-letter-stop";
    private const string Group = "ariva-dlstop." + Purpose;
    private const string Stopping = "messaging is stopping";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _steps = [];
    private readonly ConcurrentQueue<(TimeSpan At, string Text)> _log = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        PoisonProbeConsumer.BeforeFailing = null;
        await faults.RestoreAsync(Proxy);
    }

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
            ["Kafka:BootstrapServers"] = faults.KafkaBootstrap,
            ["Kafka:ServiceName"] = "dlstop",
            ["Kafka:ProvisionTopics"] = "true",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Outbox:Enabled"] = "false",
            ["Kafka:Consumers:RetryCount"] = "0",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1"
        });
        builder.Logging.AddProvider(new RecordingLoggerProvider(_log, _clock));
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(OutageProbe).Assembly)
            .Consume<OutageProbe, PoisonProbeConsumer>(KafkaTopics.SlaBreachDetected, Purpose));
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done("a host started");
        return host;
    }

    /// <summary>Whether the consumer's committed offset has reached the topic's end, read past the proxy.</summary>
    private static async Task<bool> CommittedAllAsync(Confluent.Kafka.IAdminClient admin)
    {
        var partition = new Confluent.Kafka.TopicPartition(KafkaTopics.SlaBreachDetected, new Confluent.Kafka.Partition(0));
        var latest = await Confluent.Kafka.IAdminClientExtensions.ListOffsetsAsync(admin,
            [new Confluent.Kafka.Admin.TopicPartitionOffsetSpec { TopicPartition = partition, OffsetSpec = Confluent.Kafka.Admin.OffsetSpec.Latest() }],
            new Confluent.Kafka.Admin.ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var committed = await admin.ListConsumerGroupOffsetsAsync([new Confluent.Kafka.ConsumerGroupTopicPartitions(Group, [partition])],
            new Confluent.Kafka.Admin.ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var end = latest.ResultInfos.Single().TopicPartitionOffsetError.Offset.Value;
        return end > 0 && committed.Single().Partitions.Single().Offset.Value == end;
    }

    /// <summary>
    /// The dead letters of the poison message on the dead-letter topic, read past the proxy up to the partition's end as it
    /// was when the read began (a fresh consumer's first fetch can take longer than one poll, so an empty poll is not the end).
    /// </summary>
    private int LettersFor(string key)
    {
        using var consumer = new Confluent.Kafka.ConsumerBuilder<string, string>(new Confluent.Kafka.ConsumerConfig
        {
            BootstrapServers = faults.KafkaDirectBootstrap,
            GroupId = "it-dead-letter-reader-" + Guid.NewGuid().ToString("N")[..8],
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
        var partition = new Confluent.Kafka.TopicPartition(KafkaTopics.DeadLetter(KafkaTopics.SlaBreachDetected), new Confluent.Kafka.Partition(0));
        var end = consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(10)).High.Value;
        consumer.Assign(new Confluent.Kafka.TopicPartitionOffset(partition, Confluent.Kafka.Offset.Beginning));
        var letters = 0;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (end > 0 && DateTime.UtcNow < until)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is not { } record)
                continue;
            if (record.Message.Key == key)
            {
                using var letter = JsonDocument.Parse(record.Message.Value);
                letter.RootElement.GetProperty("consumer").GetString().Should().Be(Group);
                letters++;
            }

            if (record.Offset.Value >= end - 1)
                break;
        }

        consumer.Close();
        return letters;
    }

    private int Logged(string text) => _log.Count(l => l.Text.Contains(text, StringComparison.Ordinal));

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

    [Theory(Timeout = 300_000)]
    [InlineData(true)]  // the host is stopped, then disposed (the orchestrator's SIGTERM)
    [InlineData(false)] // the host is only disposed (the ARV-156 diagnosis): disposing stops the bus and its endpoints
    public async Task DeadLetterLoop_Should_EndWithTheHost_When_TheLetterCannotBeSent(bool stopFirst)
    {
        var key = "P-" + Guid.NewGuid().ToString("N")[..8];
        PoisonProbeConsumer.PoisonKey = key;
        PoisonProbeConsumer.BeforeFailing = () => faults.CutAsync(Proxy);
        using var admin = new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        var host = await StartHostAsync();
        var disposed = false;
        try
        {
            // The probe topic holds the other fault tests' records: the new group reads and commits them all before the
            // poison message, so the cut below cannot land in an offset commit (ARV-165).
            await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new OutageProbe { Key = key + "-first", Step = 0, CorrelationId = "corr" }, Ct);
            await WaitForAsync(() => CommittedAllAsync(admin), "the consumer caught up and committed");
            await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new OutageProbe { Key = key, Step = 1, CorrelationId = "corr" }, Ct);

            await WaitForAsync(() => Logged("failed after retries; sending it to the dead-letter topic") >= 1, "the poison message failed and its dead letter is being sent, the broker cut off");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Logged(Stopping).Should().Be(0, "the loop keeps trying while the host runs");

            var stopStarted = _clock.Elapsed;
            var stop = Task.Run(async () =>
            {
                if (stopFirst)
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await host.StopAsync(shutdown.Token);
                }

                host.Dispose();
            }, CancellationToken.None);
            Done(stopFirst ? "the host stopping" : "the host disposing");

            // The loop ends on the stop, while the broker is still cut off: the dead letter was not sent.
            await WaitForAsync(() => Logged(Stopping) >= 1, "the dead-letter loop ended");
            _log.First(l => l.Text.Contains(Stopping, StringComparison.Ordinal)).At.Should().BeGreaterThan(stopStarted, "the stop ended it, not the message's own token");
            var attempts = Logged("not accepted (attempt");
            await faults.RestoreAsync(Proxy);
            Done("the broker back");

            (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(40), Ct)) == stop).Should().BeTrue("the host stops and is disposed within 40 s once the loop has ended");
            await stop;
            disposed = true;
            Done("the host stopped and disposed");
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            Logged("not accepted (attempt").Should().Be(attempts, "no attempt after the host stopped: the loop is over, not just abandoned");

            // At-least-once: the message was never acknowledged, so the next host gets it again and dead-letters it. (The
            // attempt that was in flight at the stop may still have reached the broker once it was back, so the letter can
            // be there twice; never not at all.)
            var again = await StartHostAsync();
            try
            {
                await WaitForAsync(() => PoisonProbeConsumer.Received.Count(r => r.Key == key) >= 2 && LettersFor(key) >= 1,
                    "the poison message delivered again and dead-lettered after the restart");
                PoisonProbeConsumer.Received.Count(r => r.Key == key).Should().BeGreaterThanOrEqualTo(2, "delivered before the stop and again after it");
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await again.StopAsync(shutdown.Token);
                again.Dispose();
            }
        }
        finally
        {
            PoisonProbeConsumer.BeforeFailing = null;
            await faults.RestoreAsync(Proxy);
            if (!disposed)
                _ = Task.Run(host.Dispose, CancellationToken.None);
        }
    }

    private sealed class RecordingLoggerProvider(ConcurrentQueue<(TimeSpan At, string Text)> log, Stopwatch clock) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Recorder(log, clock, categoryName);

        public void Dispose()
        {
        }

        private sealed class Recorder(ConcurrentQueue<(TimeSpan At, string Text)> log, Stopwatch clock, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category.StartsWith("Ariva.Infra.Messaging.Kafka", StringComparison.Ordinal) && logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (IsEnabled(logLevel))
                    log.Enqueue((clock.Elapsed, formatter(state, exception)));
            }
        }
    }
}
