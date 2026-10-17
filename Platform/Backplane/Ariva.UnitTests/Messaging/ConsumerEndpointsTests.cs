using System.Collections.Concurrent;
using System.Diagnostics;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Confluent.Kafka;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-176 (the security review's R2 and recommendations 1 to 5): the consumer endpoints' watch for missing topics, through
/// the internal constructor that takes the broker's topics as a delegate. The stop never waits for a check in flight and
/// connects nothing after it began; a connect that throws is tried again at the next check; the watch outlives a check
/// that throws; a broker that cannot be read is logged as such, not as missing topics, and leaves the gauge without a
/// value; the start's wait for the consumers to be ready is bounded. The broker side is proven in Ariva.IntegrationTests
/// (MissingTopicTests).
/// </summary>
public sealed class ConsumerEndpointsTests
{
    /// <summary>AMAN's topic: never waited for at start, so the start reads the topics once and goes on.</summary>
    private const string Feed = KafkaTopics.AmanEgateIntervalStats;

    private const string Group = "ariva-integration.aman-egate-intervals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KafkaSettings Settings(int startWaitSeconds = 0) =>
        new() { Enabled = true, Consumers = new ConsumerSettings { TopicWatchSeconds = 1, StartWaitSeconds = startWaitSeconds } };

    private static HostReceiveEndpointHandle Handle(Task<ReceiveEndpointReady> ready = null)
    {
        var handle = new Mock<HostReceiveEndpointHandle>();
        handle.Setup(h => h.Ready).Returns(ready ?? Task.FromResult<ReceiveEndpointReady>(null));
        return handle.Object;
    }

    /// <summary>Records log lines with their level.</summary>
    private sealed class Lines : ILogger<KafkaConsumerEndpoints>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Logged { get; } = new();

        public IEnumerable<string> Warnings => Logged.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Logged.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// Reads one gauge of one metrics instance: <see cref="KafkaProducerMetrics.ConsumedTopicMissingName"/> by default, or
    /// <see cref="KafkaProducerMetrics.ConsumerUnconnectedName"/> (ARV-125a).
    /// </summary>
    private sealed class Gauge : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly ConcurrentDictionary<(string Topic, string Group), int> _seen = new();

        public Gauge(KafkaProducerMetrics metrics, string name = KafkaProducerMetrics.ConsumedTopicMissingName)
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter == metrics.Meter && instrument.Name == name)
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

        public IReadOnlyDictionary<(string Topic, string Group), int> Read()
        {
            _seen.Clear();
            _listener.RecordObservableInstruments();
            return new Dictionary<(string Topic, string Group), int>(_seen);
        }

        public void Dispose() => _listener.Dispose();
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException(what + ": not within 15 s");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Stop_Should_EndWithinASecondAndConnectNothing_When_ACheckIsInFlight()
    {
        // R2: librdkafka's metadata request cannot be cancelled and runs up to its 10 s timeout. The watch awaits it with the
        // stop's token, so the stop does not wait for it, and the topics it brings back after the stop connect nothing.
        using var inFlight = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var connects = 0;
        IReadOnlySet<string> Read()
        {
            if (Interlocked.Increment(ref reads) == 1)
                return new HashSet<string>(StringComparer.Ordinal);
            inFlight.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            returned.TrySetResult();
            return new HashSet<string>(StringComparer.Ordinal) { Feed };
        }

        var endpoints = new KafkaConsumerEndpoints(Settings(), [new ConsumedTopic(Feed, Group, _ =>
        {
            Interlocked.Increment(ref connects);
            return Handle();
        })], () => null, Read, TimeProvider.System, new Lines());
        try
        {
            await endpoints.StartAsync(Ct);
            (await inFlight.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Should().BeTrue("the watch's check is in flight");

            var clock = Stopwatch.StartNew();
            await endpoints.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the stop does not wait for the metadata request");

            release.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            // Room for anything the finished read could still set off.
            await Task.Delay(300, Ct);
            Volatile.Read(ref connects).Should().Be(0, "nothing is connected once the stop has begun, though the topic came back");
        }
        finally
        {
            release.Set();
            endpoints.Dispose();
        }
    }

    [Fact]
    public async Task Watch_Should_ConnectAtTheNextCheck_When_TheConnectThrows()
    {
        // Recommendation 5: the failure names the group and the topic, and the consumer is tried again at the next check
        // (MassTransit removes an endpoint whose start threw, so the retry does not meet "already added"). Once every
        // consumer is connected the watch ends and reads nothing more.
        var connects = 0;
        HostReceiveEndpointHandle Connect(IKafkaRider rider) =>
            Interlocked.Increment(ref connects) == 1 ? throw new InvalidOperationException("the endpoint could not be built") : Handle();
        var reads = 0;
        var lines = new Lines();
        using var metrics = new KafkaProducerMetrics();
        using var gauge = new Gauge(metrics);
        using var endpoints = new KafkaConsumerEndpoints(Settings(), [new ConsumedTopic(Feed, Group, Connect)], () => null, () =>
        {
            Interlocked.Increment(ref reads);
            return new HashSet<string>(StringComparer.Ordinal) { Feed };
        }, TimeProvider.System, lines, metrics);

        await endpoints.StartAsync(Ct);

        lines.Warnings.Should().Contain(w => w.Contains(Group, StringComparison.Ordinal) && w.Contains(Feed, StringComparison.Ordinal) && w.Contains("could not be connected", StringComparison.Ordinal));
        gauge.Read().Should().Contain((Feed, Group), 0, "the topic exists; only its connect failed");
        await UntilAsync(() => Volatile.Read(ref connects) == 2, "the connect tried again at the next check");
        var readsThen = Volatile.Read(ref reads);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        Volatile.Read(ref connects).Should().Be(2, "a connected consumer is not connected again");
        Volatile.Read(ref reads).Should().Be(readsThen, "every consumer is connected, so the watch looks no more");
        gauge.Read().Should().Contain((Feed, Group), 0);
        await endpoints.StopAsync(Ct);
    }

    [Fact]
    public async Task UnconnectedGauge_Should_ReadOneWhileTheConnectKeepsFailing_When_TheTopicExists()
    {
        // ARV-125a (ARV-176 re-review, recommendation a): a consumer whose topic exists but whose connect keeps failing
        // consumes nothing and reads 0 on consumed_topic_missing; it reads 1 here until it connects, then 0.
        var failing = 1;
        var connects = 0;
        HostReceiveEndpointHandle Connect(IKafkaRider rider)
        {
            Interlocked.Increment(ref connects);
            return Volatile.Read(ref failing) == 1 ? throw new InvalidOperationException("the endpoint could not be built") : Handle();
        }

        using var metrics = new KafkaProducerMetrics();
        using var missing = new Gauge(metrics);
        using var unconnected = new Gauge(metrics, KafkaProducerMetrics.ConsumerUnconnectedName);
        using var endpoints = new KafkaConsumerEndpoints(Settings(), [new ConsumedTopic(Feed, Group, Connect)], () => null,
            () => new HashSet<string>(StringComparer.Ordinal) { Feed }, TimeProvider.System, new Lines(), metrics);

        await endpoints.StartAsync(Ct);
        await UntilAsync(() => Volatile.Read(ref connects) >= 2, "the connect tried again at the next check");

        unconnected.Read().Should().Contain((Feed, Group), 1, "its topic exists and its connect keeps failing");
        missing.Read().Should().Contain((Feed, Group), 0, "the topic is not missing, which is why this gauge exists");

        Volatile.Write(ref failing, 0);
        await UntilAsync(() => unconnected.Read().GetValueOrDefault((Feed, Group), -1) == 0, "connected at the next check");
        await endpoints.StopAsync(Ct);
    }

    [Theory]
    [InlineData("invalid operation")]
    [InlineData("cancelled elsewhere")]
    public async Task Watch_Should_KeepLooking_When_ACheckThrowsAnExceptionThatIsNotKafkas(string kind)
    {
        // R2 and recommendation 3: any exception a check throws (one that is not Kafka's, or a cancellation that is not the
        // stop's) is logged, and the watch looks again at its next check.
        var reads = 0;
        var connects = 0;
        IReadOnlySet<string> Read() => Interlocked.Increment(ref reads) switch
        {
            1 => new HashSet<string>(StringComparer.Ordinal),
            2 => kind == "invalid operation" ? throw new InvalidOperationException("unexpected") : throw new OperationCanceledException("not the stop's"),
            _ => new HashSet<string>(StringComparer.Ordinal) { Feed }
        };
        var lines = new Lines();
        using var endpoints = new KafkaConsumerEndpoints(Settings(), [new ConsumedTopic(Feed, Group, _ =>
        {
            Interlocked.Increment(ref connects);
            return Handle();
        })], () => null, Read, TimeProvider.System, lines);

        await endpoints.StartAsync(Ct);
        await UntilAsync(() => Volatile.Read(ref connects) == 1, "the consumer connected after the check that threw");

        Volatile.Read(ref reads).Should().BeGreaterThanOrEqualTo(3);
        lines.Warnings.Should().Contain(w => w.Contains("could not be read", StringComparison.Ordinal));
        await endpoints.StopAsync(Ct);
    }

    [Fact]
    public async Task Watch_Should_ReportKafkaNotReachableAndNoTopicMissing_When_TheBrokerCannotBeRead()
    {
        // Recommendation 2: a broker that cannot be read is logged once as such, never as topics that do not exist, and the
        // gauge has no value for a topic it never read. Once the broker answers, a missing topic is logged and reads 1;
        // once it exists, its consumer connects and it reads 0.
        var broker = 0;
        var connects = 0;
        IReadOnlySet<string> Read() => Volatile.Read(ref broker) switch
        {
            0 => throw new KafkaException(ErrorCode.Local_Transport),
            1 => new HashSet<string>(StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal) { Feed }
        };
        var lines = new Lines();
        using var metrics = new KafkaProducerMetrics();
        using var gauge = new Gauge(metrics);
        using var endpoints = new KafkaConsumerEndpoints(Settings(), [new ConsumedTopic(Feed, Group, _ =>
        {
            Interlocked.Increment(ref connects);
            return Handle();
        })], () => null, Read, TimeProvider.System, lines, metrics);

        await endpoints.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        lines.Warnings.Should().ContainSingle(w => w.Contains("Kafka not reachable", StringComparison.Ordinal), "logged once while it lasts")
            .Which.Should().Contain(Feed);
        lines.Warnings.Should().NotContain(w => w.Contains("does not exist", StringComparison.Ordinal));
        gauge.Read().Should().NotContainKey((Feed, Group), "the topic was never read");

        Volatile.Write(ref broker, 1);
        await UntilAsync(() => lines.Warnings.Any(w => w.Contains("does not exist", StringComparison.Ordinal)), "the missing topic logged once the broker answers");
        lines.Warnings.Should().ContainSingle(w => w.Contains("does not exist", StringComparison.Ordinal)).Which.Should().Contain(Feed).And.Contain(Group);
        lines.Logged.Should().Contain(l => l.Level == LogLevel.Information && l.Text.Contains("Kafka reachable again", StringComparison.Ordinal));
        gauge.Read().Should().Contain((Feed, Group), 1);

        Volatile.Write(ref broker, 2);
        await UntilAsync(() => Volatile.Read(ref connects) == 1, "the consumer connected once its topic exists");
        gauge.Read().Should().Contain((Feed, Group), 0);
        lines.Logged.Should().Contain(l => l.Level == LogLevel.Information && l.Text.Contains("now exists", StringComparison.Ordinal));
        await endpoints.StopAsync(Ct);
    }

    [Fact]
    public async Task Start_Should_EndWithinTheReadyWait_When_AConsumerIsNeverReady()
    {
        // Recommendation 1: the host's start holds its web server, so the wait for the consumers to be ready is bounded
        // (30 s in the hosts, shortened here); a consumer that is not ready by then keeps trying on its own.
        var never = new TaskCompletionSource<ReceiveEndpointReady>();
        var lines = new Lines();
        using var endpoints = new KafkaConsumerEndpoints(Settings(startWaitSeconds: 60), [new ConsumedTopic(Feed, Group, _ => Handle(never.Task))], () => null,
            () => new HashSet<string>(StringComparer.Ordinal) { Feed }, TimeProvider.System, lines)
        {
            ReadyWait = TimeSpan.FromMilliseconds(300)
        };

        var clock = Stopwatch.StartNew();
        await endpoints.StartAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the start ends at the ready wait, not at the topic wait");
        lines.Warnings.Should().Contain(w => w.Contains("not ready yet", StringComparison.Ordinal) && w.Contains(Group, StringComparison.Ordinal));
        KafkaConsumerEndpoints.ReadyWaitSeconds.Should().Be(30);
        await endpoints.StopAsync(Ct);
    }
}
