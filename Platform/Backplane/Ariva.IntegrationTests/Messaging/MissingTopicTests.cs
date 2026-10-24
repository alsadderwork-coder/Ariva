using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.IntegrationTests.Messaging;

/// <summary>A record of the feed topic that is missing at first (ARV-176).</summary>
public sealed class LateFeed
{
    public string Id { get; set; }
}

public sealed class LateFeedConsumer : IConsumer<LateFeed>
{
    public static readonly ConcurrentQueue<string> Received = new();

    public Task Consume(ConsumeContext<LateFeed> context)
    {
        Received.Enqueue(context.Message.Id);
        return Task.CompletedTask;
    }
}

/// <summary>Records the probes consumed on the topic that exists (ARV-176).</summary>
public sealed class PresentTopicConsumer : IConsumer<KafkaProbe>
{
    public static readonly ConcurrentQueue<string> Received = new();

    public Task Consume(ConsumeContext<KafkaProbe> context)
    {
        Received.Enqueue(context.Message.ContractId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// ARV-176, on a real broker: a host consumes an Ariva topic that exists and one of AMAN's feed topics that the broker does
/// not have (AMAN creates its own topics; a site without e-gates, or an AMAN release not yet publishing a contract, can
/// leave one missing), as Ariva.Api.Integration consumes AMAN's four. The host starts once its wait for the topics ends
/// (2 s here; 60 s in the hosts). A record on the topic that exists must be consumed within <see cref="ConsumeBound"/>
/// of its publish while the other topic is still missing; and once the missing topic is created, a record on it must be
/// consumed within <see cref="LateTopicBound"/> of its creation, without a restart. The operator's signal is checked on
/// the way: the host's <c>ariva.kafka.consumed_topic_missing</c> gauge reads 1 for the missing topic and 0 for the other,
/// then 0 for both once the late record is consumed, and a Warning names the missing topic.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MissingTopicTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    /// <summary>A working endpoint consumes a new record within this (a group join and a fetch take a few seconds).</summary>
    private static readonly TimeSpan ConsumeBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A topic created after the host started is consumed within this of its creation: the host looks for it every
    /// <c>Kafka:Consumers:TopicWatchSeconds</c> (10 s), then the new consumer joins its group and reads from the earliest
    /// offset; 60 s leaves room for a slow join on a loaded machine.
    /// </summary>
    private static readonly TimeSpan LateTopicBound = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private void Done(string step) =>
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step} at {_clock.Elapsed.TotalSeconds:F1} s"));

    private async Task<IHost> StartHostAsync(string database, ConcurrentQueue<string> warnings)
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
            ["Kafka:ServiceName"] = "missing",
            // Ariva's topics only: AMAN's feed topics are AMAN's to create.
            ["Kafka:ProvisionTopics"] = "true",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1"
            // No Kafka:Consumers:StartWaitSeconds: the provisioner creates the present topic beside the host's start (a
            // BackgroundService), so the start waits for the topics Ariva owns as in production (60 s; AMAN's feed topic is
            // never waited for). A 2 s wait lost that race on a loaded machine and the gauge read 1 for the present topic.
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Logging.AddProvider(new WarningRecorder(warnings));
        builder.Services.AddScoped<ICurrentUser, Ariva.IntegrationTests.Security.TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(KafkaProbe).Assembly)
            .Consume<KafkaProbe, PresentTopicConsumer>(KafkaTopics.SlaDisputeChanged, "present-topic")
            .Consume<LateFeed, LateFeedConsumer>(KafkaTopics.AmanEgateIntervalStats, "late-topic"));
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done("the host started");
        return host;
    }

    [Fact(Timeout = 240_000)]
    public async Task Consumers_Should_KeepConsumingAndPickUpTheTopicLater_When_AConsumedTopicIsMissing()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.MissingTopic);
        await new SqlScriptRunner(postgres.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        // All topics' metadata, never one topic's: a metadata request naming a topic lets the broker create it (librdkafka
        // allows that for every client but a consumer, and the broker's auto.create.topics.enable is on by default).
        admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(t => t.Topic).Should().NotContain(KafkaTopics.AmanEgateIntervalStats,
            "the feed topic is missing when the host starts");

        var warnings = new ConcurrentQueue<string>();
        using var host = await StartHostAsync(database, warnings);
        // The operator's signal (ARV-176): the host's gauge, read from its own meter, and the Warning naming the topic.
        using var gauge = new GaugeReader(host.Services.GetRequiredService<KafkaProducerMetrics>());
        var lateSeries = (KafkaTopics.AmanEgateIntervalStats, "ariva-missing.late-topic");
        var presentSeries = (KafkaTopics.SlaDisputeChanged, "ariva-missing.present-topic");
        try
        {
            var reading = gauge.Read();
            reading.Should().Contain(lateSeries, 1, "the gauge reads 1 for the consumed topic the broker does not have");
            reading.Should().Contain(presentSeries, 0, "and 0 for the one it has");
            warnings.Should().Contain(w => w.Contains(KafkaTopics.AmanEgateIntervalStats, StringComparison.Ordinal) && w.Contains("does not exist", StringComparison.Ordinal),
                "the Warning names the missing topic");

            // The topic that exists: a record published now is consumed within the bound, the other topic still missing.
            var key = "M-" + Guid.NewGuid().ToString("N")[..8];
            var published = Stopwatch.StartNew();
            await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new KafkaProbe { ContractId = key }, Ct);
            while (!PresentTopicConsumer.Received.Contains(key) && published.Elapsed < ConsumeBound + TimeSpan.FromSeconds(30))
                await Task.Delay(100, Ct);
            Done($"the record on the existing topic {(PresentTopicConsumer.Received.Contains(key) ? "consumed" : "not consumed")} after {published.Elapsed.TotalSeconds:F1} s");
            PresentTopicConsumer.Received.Should().Contain(key, "a missing topic must not stall the host's other consumers");
            published.Elapsed.Should().BeLessThan(ConsumeBound);

            admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(t => t.Topic).Should().NotContain(KafkaTopics.AmanEgateIntervalStats,
                "the feed topic was still missing then (nothing created it)");
            gauge.Read().Should().Contain(lateSeries, 1, "still missing");

            // The missing topic appears (AMAN creates it): a record on it is consumed without a restart.
            await admin.CreateTopicsAsync([new TopicSpecification { Name = KafkaTopics.AmanEgateIntervalStats, NumPartitions = 1, ReplicationFactor = 1 }]);
            var created = Stopwatch.StartNew();
            Done("the missing topic created");
            var late = "L-" + Guid.NewGuid().ToString("N")[..8];
            using (var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build())
                await producer.ProduceAsync(KafkaTopics.AmanEgateIntervalStats,
                    new Message<string, string>
                    {
                        Key = late,
                        Value = JsonSerializer.Serialize(new LateFeed { Id = late }, EventCatalog.Json),
                        // The inbox's key for a record that is not an Ariva event, as MassTransit reads it.
                        Headers = new Confluent.Kafka.Headers { { "MessageId", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D")) } }
                    }, Ct);
            while (!LateFeedConsumer.Received.Contains(late) && created.Elapsed < LateTopicBound + TimeSpan.FromSeconds(30))
                await Task.Delay(100, Ct);
            Done($"the record on the late topic {(LateFeedConsumer.Received.Contains(late) ? "consumed" : "not consumed")} after {created.Elapsed.TotalSeconds:F1} s");
            LateFeedConsumer.Received.Should().Contain(late, "a topic created after the host started is consumed without a restart");
            created.Elapsed.Should().BeLessThan(LateTopicBound);
            reading = gauge.Read();
            reading.Should().Contain(lateSeries, 0, "the topic exists and its consumer runs");
            reading.Should().Contain(presentSeries, 0);

            // And the existing topic still works after it.
            var again = "A-" + Guid.NewGuid().ToString("N")[..8];
            await host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new KafkaProbe { ContractId = again }, Ct);
            var since = Stopwatch.StartNew();
            while (!PresentTopicConsumer.Received.Contains(again) && since.Elapsed < ConsumeBound)
                await Task.Delay(100, Ct);
            PresentTopicConsumer.Received.Should().Contain(again);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await host.StopAsync(stop.Token);
        }
    }

    /// <summary>Reads the host's <see cref="KafkaProducerMetrics.ConsumedTopicMissingName"/> gauge, from its own meter only.</summary>
    private sealed class GaugeReader : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly ConcurrentDictionary<(string Topic, string Group), int> _seen = new();

        public GaugeReader(KafkaProducerMetrics metrics)
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter == metrics.Meter && instrument.Name == KafkaProducerMetrics.ConsumedTopicMissingName)
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            {
                string topic = null, group = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "topic")
                        topic = (string)tag.Value;
                    else if (tag.Key == "group")
                        group = (string)tag.Value;
                }

                _seen[(topic, group)] = value;
            });
            _listener.Start();
        }

        /// <summary>One collection of the gauge: its value per topic and group.</summary>
        public IReadOnlyDictionary<(string Topic, string Group), int> Read()
        {
            _seen.Clear();
            _listener.RecordObservableInstruments();
            return new Dictionary<(string Topic, string Group), int>(_seen);
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Records the Warnings of the host's consumer endpoints (topic and group names only).</summary>
    private sealed class WarningRecorder(ConcurrentQueue<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Recorder(log, categoryName);

        public void Dispose()
        {
        }

        private sealed class Recorder(ConcurrentQueue<string> log, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category == typeof(KafkaConsumerEndpoints).FullName && logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (IsEnabled(logLevel))
                    log.Enqueue(formatter(state, exception));
            }
        }
    }
}
