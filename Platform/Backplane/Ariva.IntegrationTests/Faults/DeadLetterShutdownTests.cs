using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
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
/// ARV-171 closed that risk: with the database there the letter is kept in the outbox at the stop and the message
/// completes, and the next host's relay sends the letter (the first two cases); only with the database away as well is
/// the message left unacknowledged as above (the third case, the Postgres proxy cut before the stop). The skip itself is
/// reproduced, with a later record committed during the stop, by DeadLetterKeptAtStopTests.
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
    private const string Kept = "kept in the outbox, which sends it later";
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
        await faults.RestoreAsync("postgres");
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
            ["Kafka:Outbox:Enabled"] = "true",
            ["Kafka:Outbox:PollMilliseconds"] = "200",
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

    /// <summary>The outbox rows of kept dead letters for a key, read past the proxy: whether each was sent (ARV-171).</summary>
    private async Task<List<bool>> KeptRowsAsync(string key)
    {
        await using var sql = new Npgsql.NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT sent_on IS NOT NULL FROM outbox_message WHERE message_type = @type AND message_key = @key ORDER BY seq", sql);
        command.Parameters.AddWithValue("type", DeadLetters.OutboxMessageType);
        command.Parameters.AddWithValue("key", key);
        var rows = new List<bool>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(reader.GetBoolean(0));
        return rows;
    }

    [Theory(Timeout = 300_000)]
    [InlineData(true, false)]  // the host is stopped, then disposed (the orchestrator's SIGTERM): the letter is kept (ARV-171)
    [InlineData(false, false)] // the host is only disposed (the ARV-156 diagnosis): disposing stops the bus and its endpoints
    [InlineData(true, true)]   // the database away as well: nothing durable, the record is left unacknowledged (ARV-166)
    public async Task DeadLetterLoop_Should_EndWithTheHost_When_TheLetterCannotBeSent(bool stopFirst, bool databaseToo)
    {
        var key = "P-" + Guid.NewGuid().ToString("N")[..8];
        PoisonProbeConsumer.PoisonKey = key;
        PoisonProbeConsumer.BeforeFailing = () => faults.CutAsync(Proxy);
        using var admin = new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        var host = await StartHostAsync();
        // ARV-125a: how the letter ended is counted (kept, or failed with the database away too).
        using var counts = new InstrumentReader(host.Services.GetRequiredService<KafkaProducerMetrics>().Meter);
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

            if (databaseToo)
            {
                await faults.CutAsync("postgres");
                Done("the database cut off as well");
            }

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

            // The loop ends on the stop, while the broker is still cut off: the dead letter was not sent. With the database
            // there it is kept in the outbox and the message completes (ARV-171); with the database away as well nothing
            // durable can be written and the message is left unacknowledged (ARV-166).
            var ending = databaseToo ? Stopping : Kept;
            await WaitForAsync(() => Logged(ending) >= 1, databaseToo ? "the dead-letter loop ended, the letter not kept" : "the dead-letter loop ended, the letter kept");
            _log.First(l => l.Text.Contains(ending, StringComparison.Ordinal)).At.Should().BeGreaterThan(stopStarted, "the stop ended it, not the message's own token");
            Logged(databaseToo ? Kept : Stopping).Should().Be(0);
            if (databaseToo)
                Logged("could not be kept in the outbox either").Should().Be(1);
            counts.Sum(KafkaProducerMetrics.DeadLettersName, ("topic", KafkaTopics.SlaBreachDetected), ("group", Group),
                ("outcome", databaseToo ? KafkaProducerMetrics.FailedOutcome : KafkaProducerMetrics.KeptOutcome)).Should().Be(1);
            counts.Sum(KafkaProducerMetrics.DeadLettersName, ("outcome", KafkaProducerMetrics.SentOutcome)).Should().Be(0, "the broker never took it");
            var attempts = Logged("not accepted (attempt");
            await faults.RestoreAsync(Proxy);
            if (databaseToo)
                await faults.RestoreAsync("postgres");
            Done("the broker back");

            (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(40), Ct)) == stop).Should().BeTrue("the host stops and is disposed within 40 s once the loop has ended");
            await stop;
            disposed = true;
            Done("the host stopped and disposed");
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            Logged("not accepted (attempt").Should().Be(attempts, "no attempt after the host stopped: the loop is over, not just abandoned");

            var again = await StartHostAsync();
            try
            {
                if (databaseToo)
                {
                    // At-least-once (ARV-166): the message was never acknowledged, so the next host gets it again and
                    // dead-letters it. (The attempt in flight at the stop may still have reached the broker once it was back,
                    // so the letter can be there twice; never not at all.)
                    await WaitForAsync(() => PoisonProbeConsumer.Received.Count(r => r.Key == key) >= 2 && LettersFor(key) >= 1,
                        "the poison message delivered again and dead-lettered after the restart");
                    PoisonProbeConsumer.Received.Count(r => r.Key == key).Should().BeGreaterThanOrEqualTo(2, "delivered before the stop and again after it");
                    (await KeptRowsAsync(key)).Should().BeEmpty("nothing could be kept with the database away");
                }
                else
                {
                    // ARV-171: the kept letter is sent by the relay. The message completed at the stop; whether its offset was
                    // committed before the endpoint stopped (the broker came back during the stop) decides whether it is
                    // delivered again, which then dead-letters it once more: at least one letter, never none.
                    await WaitForAsync(async () => (await KeptRowsAsync(key)) is [true] && LettersFor(key) >= 1,
                        "the kept dead letter sent by the relay after the restart");
                }
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
            await faults.RestoreAsync("postgres");
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
