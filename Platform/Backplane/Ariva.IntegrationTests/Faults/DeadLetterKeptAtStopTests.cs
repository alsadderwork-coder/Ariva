using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
using Npgsql;

namespace Ariva.IntegrationTests.Faults;

/// <summary>Fails the poison message of the running test and records every message as it starts.</summary>
public sealed class KeptProbeConsumer : IConsumer<OutageProbe>
{
    public static string PoisonKey { get; set; }

    public static readonly ConcurrentQueue<(string Key, int Step)> Received = new();

    public Task Consume(ConsumeContext<OutageProbe> context)
    {
        Received.Enqueue((context.Message.Key, context.Message.Step));
        if (context.Message.Key == PoisonKey)
            throw new InvalidOperationException("Poison probe (ARV-171).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// ARV-171 on real Kafka: a dead letter the broker refuses when its endpoint stops is kept as an outbox row before the record
/// completes, so no record is skipped. Kafka refuses only the dead-letter topic: its <c>max.message.bytes</c> is set to 1
/// (the broker answers MESSAGE_TOO_LARGE at once), while the source topic, the consumer group's commits and the database
/// stay available. Toxiproxy cuts the whole broker connection, which would refuse the commits as well, so it cannot make
/// this case. The order of events is the one ARV-166 left open: the record fails and its dead letter is refused, a later
/// record of the same partition is fetched, then the endpoint stops. Before ARV-171 the poison record was faulted at the
/// stop and so skipped, neither processed nor dead-lettered; now the letter is in the outbox before the record completes,
/// and once the topic takes messages again the relay of the next host sends it. Before ARV-253 the later record (another
/// key, so another of MassTransit's lanes) completed during the poison record's dead-letter loop; with one lane per
/// consumer it waits behind it, is never handled by the stopping host, and is not passed: the stop commits just past the
/// poison record, and the next host consumes the later record. The unreadable-record path (the rider's
/// dead-letter pipe, at receive level) is proven the same way, with a NUL character in the record's key and in a header value
/// (ARV-175): PostgreSQL text refuses NUL, so before ARV-175 that letter could not be kept and the record was left
/// unacknowledged; now each NUL is replaced with U+FFFD, the letter is flagged, and its outbox row is keyed by a digest.
/// <para>
/// Every wait is for a condition within one budget (ARV-156); the topic's limit is restored in every case.
/// </para>
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class DeadLetterKeptAtStopTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Purpose = "dead-letter-kept";
    private const string Group = "ariva-dlkept." + Purpose;
    private const string Kept = "kept in the outbox, which sends it later";
    private static readonly string DeadLetterTopic = KafkaTopics.DeadLetter(KafkaTopics.SlaBreachDetected);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(240);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _steps = [];
    private readonly ConcurrentQueue<string> _log = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        KeptProbeConsumer.PoisonKey = null;
        await AcceptDeadLettersAsync();
    }

    private Confluent.Kafka.IAdminClient Admin() =>
        new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();

    /// <summary>The dead-letter topic refuses every message (and nothing else is touched).</summary>
    private async Task RefuseDeadLettersAsync() => await SetMaxMessageBytesAsync("1");

    /// <summary>The dead-letter topic takes messages again, with the limit the provisioner gives it.</summary>
    private async Task AcceptDeadLettersAsync() => await SetMaxMessageBytesAsync(KafkaSettings.MaxMessageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private async Task SetMaxMessageBytesAsync(string value)
    {
        using var admin = Admin();
        try
        {
            await admin.IncrementalAlterConfigsAsync(
                new Dictionary<Confluent.Kafka.Admin.ConfigResource, List<Confluent.Kafka.Admin.ConfigEntry>>
                {
                    [new Confluent.Kafka.Admin.ConfigResource { Type = Confluent.Kafka.Admin.ResourceType.Topic, Name = DeadLetterTopic }] =
                    [
                        new Confluent.Kafka.Admin.ConfigEntry
                        {
                            Name = "max.message.bytes", Value = value, IncrementalOperation = Confluent.Kafka.Admin.AlterConfigOpType.Set
                        }
                    ]
                },
                new Confluent.Kafka.Admin.IncrementalAlterConfigsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        }
        catch (Confluent.Kafka.Admin.IncrementalAlterConfigsException e) when (e.Results.All(r => r.Error.Code == Confluent.Kafka.ErrorCode.UnknownTopicOrPart))
        {
            // The topic is created by the first host (the provisioner); nothing to restore before that.
        }
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
            ["Kafka:ServiceName"] = "dlkept",
            ["Kafka:ProvisionTopics"] = "true",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Outbox:PollMilliseconds"] = "200",
            ["Kafka:Consumers:RetryCount"] = "0",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1"
        });
        builder.Logging.AddProvider(new Recorder(_log));
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(OutageProbe).Assembly)
            .Consume<OutageProbe, KeptProbeConsumer>(KafkaTopics.SlaBreachDetected, Purpose));
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done("a host started");
        return host;
    }

    private static async Task StopAsync(IHost host)
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await host.StopAsync(shutdown.Token);
        host.Dispose();
    }

    /// <summary>The topic's end and the group's committed offset on the probe topic, read past the proxy.</summary>
    private async Task<(long End, long Committed)> OffsetsAsync()
    {
        using var admin = Admin();
        var partition = new Confluent.Kafka.TopicPartition(KafkaTopics.SlaBreachDetected, new Confluent.Kafka.Partition(0));
        var latest = await Confluent.Kafka.IAdminClientExtensions.ListOffsetsAsync(admin,
            [new Confluent.Kafka.Admin.TopicPartitionOffsetSpec { TopicPartition = partition, OffsetSpec = Confluent.Kafka.Admin.OffsetSpec.Latest() }],
            new Confluent.Kafka.Admin.ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var committed = await admin.ListConsumerGroupOffsetsAsync([new Confluent.Kafka.ConsumerGroupTopicPartitions(Group, [partition])],
            new Confluent.Kafka.Admin.ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        return (latest.ResultInfos.Single().TopicPartitionOffsetError.Offset.Value, committed.Single().Partitions.Single().Offset.Value);
    }

    /// <summary>The dead letters for a key, read past the proxy up to the topic's end as it was when the read began.</summary>
    private List<DeadLetter> LettersFor(string key)
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
        var letters = new List<DeadLetter>();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (end > 0 && DateTime.UtcNow < until)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is not { } record)
                continue;
            if (record.Message.Key == key)
                letters.Add(JsonSerializer.Deserialize<DeadLetter>(record.Message.Value, EventCatalog.Json));
            if (record.Offset.Value >= end - 1)
                break;
        }

        consumer.Close();
        return letters;
    }

    /// <summary>The outbox rows of kept dead letters for a key, read past the proxy: whether each was sent.</summary>
    private async Task<List<bool>> KeptRowsAsync(string key)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT sent_on IS NOT NULL FROM outbox_message WHERE message_type = @type AND topic = @topic AND message_key = @key ORDER BY seq", sql);
        command.Parameters.AddWithValue("type", DeadLetters.OutboxMessageType);
        command.Parameters.AddWithValue("topic", DeadLetterTopic);
        command.Parameters.AddWithValue("key", key);
        var rows = new List<bool>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(reader.GetBoolean(0));
        return rows;
    }

    private int Logged(string text) => _log.Count(l => l.Contains(text, StringComparison.Ordinal));

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

    private static int Count(string key) => KeptProbeConsumer.Received.Count(r => r.Key == key);

    /// <summary>
    /// The lane MassTransit's rider gave a key with eight lanes (Murmur3 of the key), as before ARV-253: the later record's
    /// key is outside the poison record's, so that with lanes it would have completed during the dead-letter loop.
    /// </summary>
    private static long LaneOf(string key) => new MassTransit.Middleware.Murmur3UnsafeHashGenerator().Hash(Encoding.UTF8.GetBytes(key)) % 8;

    /// <summary>A key of the run whose old lane is not <paramref name="taken"/>'s.</summary>
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

    [Theory(Timeout = 300_000)]
    [InlineData(false)] // a message whose consumer fails: the consume pipe's dead-letter filter
    [InlineData(true)]  // a record that is not a readable message: the rider's dead-letter pipe, at receive level
    public async Task Stop_Should_KeepTheRefusedDeadLetterAndSkipNoRecord_When_ALaterRecordWaitsBehindIt(bool unreadable)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var poison = unreadable ? "P-" + run + "\0" : "P-" + run;
        // The kept letter's outbox row, and so its record on the dead-letter topic, is keyed by the record's key, or by a digest
        // of the key as the letter holds it when a character was replaced (ARV-175).
        var letterKey = unreadable
            ? "sha256-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(poison.Replace('\0', '\uFFFD'))))
            : poison;
        var later = KeyOutside("L", run, poison);
        var raw = Encoding.UTF8.GetBytes("not a message (ARV-171) " + run);
        KeptProbeConsumer.PoisonKey = poison;
        await AcceptDeadLettersAsync();
        var host = await StartHostAsync();
        // ARV-125a: the refused letter is counted once, as kept.
        using var counts = new InstrumentReader(host.Services.GetRequiredService<KafkaProducerMetrics>().Meter);
        var stopped = false;
        try
        {
            // The new group reads and commits the records already on the topic first, so that the poison record is the
            // first one pending.
            await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new OutageProbe { Key = "F-" + run, Step = 0, CorrelationId = "corr" }, Ct);
            await WaitForAsync(async () => await OffsetsAsync() is { End: > 0 } o && o.Committed == o.End, "the consumer caught up and committed");
            var poisonOffset = (await OffsetsAsync()).End;

            await RefuseDeadLettersAsync();
            Done("the dead-letter topic refusing");
            if (unreadable)
            {
                using var producer = new Confluent.Kafka.ProducerBuilder<string, byte[]>(new Confluent.Kafka.ProducerConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
                await producer.ProduceAsync(KafkaTopics.SlaBreachDetected, new Confluent.Kafka.Message<string, byte[]>
                {
                    Key = poison, Value = raw, Headers = new Confluent.Kafka.Headers { { "probe", Encoding.UTF8.GetBytes("v\0w") } }
                }, Ct);
            }
            else
            {
                await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new OutageProbe { Key = poison, Step = 1, CorrelationId = "corr" }, Ct);
            }

            await WaitForAsync(() => Logged("not accepted (attempt") >= 1, "the record failed and Kafka refused its dead letter");

            // A later record of another key on the same partition, fetched while the poison record is still pending: it waits
            // behind it in the consumer's one lane (ARV-253).
            await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new OutageProbe { Key = later, Step = 2, CorrelationId = "corr" }, Ct);
            await WaitForAsync(async () => (await OffsetsAsync()).End == poisonOffset + 2, "a later record of another key on the topic");
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            Count(later).Should().Be(0, "the later record waits behind the poison record's dead-letter loop");
            (await OffsetsAsync()).Committed.Should().Be(poisonOffset, "nothing is committed past the pending poison record before the stop");

            await StopAsync(host);
            stopped = true;
            Done("the host stopped");
            (await OffsetsAsync()).Committed.Should().Be(poisonOffset + 1,
                "the poison record completed with its letter kept, and the later record, never handled, is not passed (ARV-253)");
            Count(later).Should().Be(0, "the stopping host never handled the later record");
            Logged("left unacknowledged").Should().Be(0, "the database was there, so the record was not faulted (and so skipped)");
            Logged(Kept).Should().Be(1, "the refused dead letter was kept in the outbox at the stop");
            counts.Sum(KafkaProducerMetrics.DeadLettersName, ("topic", KafkaTopics.SlaBreachDetected), ("group", Group), ("outcome", KafkaProducerMetrics.KeptOutcome))
                .Should().Be(1);
            (await KeptRowsAsync(letterKey)).Should().Equal([false], "one outbox row holds the letter, not sent yet: the topic still refuses");

            // The topic takes messages again: the next host's relay sends the kept letter.
            await AcceptDeadLettersAsync();
            Done("the dead-letter topic accepting again");
            var next = await StartHostAsync();
            try
            {
                await WaitForAsync(async () => (await KeptRowsAsync(letterKey)) is [true], "the relay sent the kept dead letter");
                await WaitForAsync(() => LettersFor(letterKey).Count >= 1, "the dead letter on the dead-letter topic");
                await WaitForAsync(async () => await OffsetsAsync() is var o && o.Committed == o.End, "every record committed");
                await Task.Delay(TimeSpan.FromSeconds(3), Ct);

                var letter = LettersFor(letterKey).Should().ContainSingle("the record completed after its letter was kept, so it was not delivered and dead-lettered again").Subject;
                letter.Topic.Should().Be(KafkaTopics.SlaBreachDetected);
                letter.Partition.Should().Be(0);
                letter.Offset.Should().Be(poisonOffset, "the letter names the record");
                letter.Consumer.Should().Be(Group);
                if (unreadable)
                {
                    letter.ErrorType.Should().Be(typeof(InvalidDataException).FullName);
                    Convert.FromBase64String(letter.BodyBase64).Should().Equal(raw, "the raw record travels with the letter");
                    Count(poison).Should().Be(0, "an unreadable record never reaches the consumer");
                    letter.Key.Should().Be("P-" + run + "\uFFFD", "the NUL replaced, so PostgreSQL took the letter (ARV-175)");
                    letter.Headers.Should().Contain("probe", "v\uFFFDw").And.Contain(DeadLetters.TextReplacedHeader, "headers,key");
                }
                else
                {
                    letter.ErrorType.Should().Be(typeof(InvalidOperationException).FullName);
                    letter.ErrorMessage.Should().Be("Poison probe (ARV-171).");
                    Count(poison).Should().Be(1, "the poison record completed at the stop and is not delivered again");
                }

                Count(later).Should().Be(1, "the next host processes the later record, once");
            }
            finally
            {
                await StopAsync(next);
            }
        }
        finally
        {
            KeptProbeConsumer.PoisonKey = null;
            await AcceptDeadLettersAsync();
            if (!stopped)
                _ = Task.Run(host.Dispose, CancellationToken.None);
        }
    }

    private sealed class Recorder(ConcurrentQueue<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(log, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> log, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category.StartsWith("Ariva.Infra.Messaging.Kafka", StringComparison.Ordinal) && logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (IsEnabled(logLevel))
                    log.Enqueue(formatter(state, exception));
            }
        }
    }
}
