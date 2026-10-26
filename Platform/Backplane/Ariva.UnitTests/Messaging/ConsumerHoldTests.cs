using System.Diagnostics;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-254: a rider endpoint held by one record is visible and bounded. The holds (<see cref="KafkaConsumerHolds"/>) report
/// each endpoint's oldest record in hand on <c>ariva.kafka.consumer_hold_seconds</c>, by topic and group only; a dead letter the
/// broker refuses for its whole send window is kept in the outbox, so the record completes; <c>Kafka:Consumers:RetryCount</c>
/// is refused at start outside 0 to 10; and every rider endpoint gets the receive filter and a poll interval above the longest
/// hold the settings allow (MassTransit 8.5.11 stops polling while a held record's followers fill its prefetch; the real
/// broker's side is HeldConsumerTests in Ariva.IntegrationTests).
/// </summary>
public sealed class ConsumerHoldTests
{
    #region Fields

    private const string Group = "ariva-main.hold-probe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Holds

    [Fact]
    public async Task Holds_Should_ReportTheOldestRecordInHandPerEndpoint_When_RecordsAreHeld()
    {
        var clock = new ManualClock();
        var holds = new KafkaConsumerHolds(clock);
        var held = holds.For(KafkaTopics.FlowNowcast, Group);
        var idle = holds.For(KafkaTopics.AlertStateChanged, Group);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();

        var one = held.Send(Mock.Of<ReceiveContext>(), Pipe(first.Task));
        clock.Advance(TimeSpan.FromSeconds(20));
        var two = held.Send(Mock.Of<ReceiveContext>(), Pipe(second.Task));
        clock.Advance(TimeSpan.FromSeconds(10));
        var during = holds.Holds().ToDictionary(h => h.Topic, h => h.Seconds);
        first.SetResult();
        await one;
        var after = holds.Holds().Single(h => h.Topic == KafkaTopics.FlowNowcast).Seconds;
        second.SetException(new InvalidOperationException("a fault ends the hold too"));
        await held.Awaiting(_ => two).Should().ThrowAsync<InvalidOperationException>();

        during[KafkaTopics.FlowNowcast].Should().Be(30, "the oldest record in hand, 20 s before the second");
        during[KafkaTopics.AlertStateChanged].Should().Be(0, "an endpoint holding nothing reports 0");
        after.Should().Be(10, "the second record is the oldest once the first completed");
        holds.Holds().Single(h => h.Topic == KafkaTopics.FlowNowcast).Seconds.Should().Be(0, "a faulted record is no longer held");
        holds.For(KafkaTopics.FlowNowcast, Group).Should().BeSameAs(held, "one filter per endpoint");
        idle.Should().NotBeSameAs(held);
    }

    [Fact]
    public void Holds_Should_TagAnUnknownTopicAndGroup_When_TheEndpointNamesNoneOfAriva()
    {
        var holds = new KafkaConsumerHolds(new ManualClock());

        holds.For("someone.elses.topic", "Not A Group");

        holds.Holds().Should().ContainSingle().Which.Should().Be((KafkaProducerMetrics.Unknown, KafkaProducerMetrics.Unknown, 0d));
    }

    [Fact]
    public async Task Gauge_Should_ReportEachEndpointsHoldByTopicAndGroup_When_Collected()
    {
        var clock = new ManualClock();
        using var metrics = new KafkaProducerMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);
        var holds = new KafkaConsumerHolds(clock, metrics);
        var release = new TaskCompletionSource();
        var record = holds.For(KafkaTopics.FlowNowcast, Group).Send(Mock.Of<ReceiveContext>(), Pipe(release.Task));
        clock.Advance(TimeSpan.FromMinutes(8));

        var measured = reader.Observe(KafkaProducerMetrics.ConsumerHoldName);
        release.SetResult();
        await record;

        measured.Should().ContainSingle();
        measured[0].Value.Should().Be(480);
        measured[0].Tags.Should().Equal(new Dictionary<string, string> { ["topic"] = KafkaTopics.FlowNowcast, ["group"] = Group });
        reader.Observe(KafkaProducerMetrics.ConsumerHoldName).Single().Value.Should().Be(0);
    }

    #endregion

    #region Dead letter window

    [Fact]
    public async Task Retry_Should_KeepTheLetterAndComplete_When_TheBrokerRefusesItForTheWholeWindow()
    {
        // Refusals at once: attempts at 0, 2, 6, 14 and 30 s, the wait of 30 s ending with the window, a last one at 60 s.
        var clock = new SteppingClock();
        var sink = new Sink { Clock = clock };
        var store = new Store();
        using var metrics = new KafkaProducerMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);

        await DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), clock, NullLogger.Instance, CancellationToken.None, CancellationToken.None, store, metrics)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        store.Kept.Should().ContainSingle("refused for its whole send window, the letter is kept and the record may complete");
        sink.Starts.Select(s => s.TotalSeconds).Should().Equal([0, 2, 6, 14, 30, 60], "the broker asked again within the window, the last time at its end");
        reader.Sum(KafkaProducerMetrics.DeadLettersName, ("outcome", KafkaProducerMetrics.KeptOutcome)).Should().Be(1);
        reader.Sum(KafkaProducerMetrics.DeadLettersName).Should().Be(1, "counted once");
    }

    [Fact]
    public async Task Retry_Should_StartTheWindowsLastAttemptByItsEnd_When_ARetryWaitWouldOutlastIt()
    {
        // Each refusal takes 11 s: the fourth ends at 58 s, and its wait of 16 s is cut to the 2 s left of the window, so the
        // last attempt starts at 60 s, not 74 s (ConsumerSettings.LongestHold counts one send after the window, no wait).
        var clock = new SteppingClock();
        var sink = new Sink { Clock = clock, SendTime = TimeSpan.FromSeconds(11) };
        var store = new Store();

        await DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), clock, NullLogger.Instance, CancellationToken.None, CancellationToken.None, store)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        store.Kept.Should().ContainSingle("refused for the whole window, the letter is kept");
        sink.Starts.Select(s => s.TotalSeconds).Should().Equal([0, 13, 28, 47, 60], "waits of 2, 4 and 8 s, then the 2 s left of the window");
    }

    [Fact]
    public async Task Retry_Should_NeverWaitANegativeTime_When_TheWindowEndsBetweenTwoReadsOfTheClock()
    {
        // Every read of this clock moves it 6 s. Checked on one read and cut on the next, the fourth attempt's window would
        // read 56 s and its cut 62 s: a wait of -2 s, which Task.Delay refuses, so the record would be acknowledged with no
        // letter. Read once, the wait is cut to what is left, and the next attempt finds the window over and keeps the letter.
        var clock = new SteppingClock { ReadStep = TimeSpan.FromSeconds(6) };
        var sink = new Sink();
        var store = new Store();

        await DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), clock, NullLogger.Instance, CancellationToken.None, CancellationToken.None, store)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        store.Kept.Should().ContainSingle("the window ended and the letter was kept, with no negative wait on the way");
        sink.Attempts.Should().Be(5);
    }

    [Fact]
    public async Task Retry_Should_TryTheBrokerForAnotherWindow_When_TheOutboxCannotTakeTheLetterEither()
    {
        // Refused for the first window and the outbox away: the outbox is tried at each window's end (60 s, then 60 s after the
        // failed keep), the broker again meanwhile, and the ninth attempt is taken.
        var sink = new Sink { Refusals = 8 };
        var store = new Store { Fail = true };
        using var metrics = new KafkaProducerMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);

        await DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), new SteppingClock(), NullLogger.Instance, CancellationToken.None, CancellationToken.None, store, metrics)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        store.Attempts.Should().Be(2, "the outbox was tried at the end of each of the two windows the refusals filled");
        sink.Attempts.Should().Be(9, "and the broker again until it took the letter");
        reader.Sum(KafkaProducerMetrics.DeadLettersName, ("outcome", KafkaProducerMetrics.SentOutcome)).Should().Be(1);
        reader.Sum(KafkaProducerMetrics.DeadLettersName).Should().Be(1);
    }

    #endregion

    #region Settings

    [Fact]
    public void LongestHold_Should_AddEveryAttemptTheRetryWaitsAndTheDeadLetter_When_Computed()
    {
        var defaults = new ConsumerSettings();
        var most = new ConsumerSettings { RetryCount = ConsumerSettings.MaxRetryCount, HandlerTimeoutSeconds = ConsumerSettings.MaxHandlerTimeoutSeconds, RetryMaxMilliseconds = 5_000 };

        defaults.LongestHold.Should().Be(TimeSpan.FromSeconds(4 * 70 + 3 * 5 + 60 + 45 + 10), "four attempts, three waits, the window, one more send, the keep");
        defaults.MaxPollInterval.Should().Be(defaults.LongestHold + TimeSpan.FromMinutes(1));
        defaults.MaxPollInterval.Should().BeGreaterThan(TimeSpan.FromSeconds(300), "librdkafka's default was shorter than the hold");
        most.LongestHold.Should().Be(TimeSpan.FromSeconds(11 * 120 + 10 * 5 + 60 + 45 + 10));
        most.MaxPollInterval.Should().BeLessThan(ConsumerSettings.MaxPollIntervalLimit, "librdkafka's own bound on max.poll.interval.ms");
        ConsumerSettings.MaxPollIntervalLimit.Should().Be(TimeSpan.FromMilliseconds(86_400_000));
    }

    [Theory]
    [InlineData("8490500", false)]
    [InlineData("8490501", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_ThePollIntervalWouldPassADay(string retryMaxMilliseconds, bool refused)
    {
        // Ten retries at the largest handler timeout: 11 x 120 s, the dead letter's 115 s and the minute come to 1,495 s, so ten
        // waits of 8,490.5 s reach the day exactly and a millisecond more passes it. Such waits fail the stop budget as well
        // (checked after this, unless the grace period is above a day), so the case below the day is refused for that alone.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Consumers:RetryCount"] = "10",
            ["Kafka:Consumers:HandlerTimeoutSeconds"] = "120",
            ["Kafka:Consumers:RetryMaxMilliseconds"] = retryMaxMilliseconds
        }).Build();

        var thrown = Record.Exception(() => MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration));

        if (refused)
            thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("at most a day, librdkafka's largest max.poll.interval.ms (ARV-254)");
        else
            (thrown?.Message ?? string.Empty).Should().NotContain("max.poll.interval.ms", "the day is reached, not passed");
    }

    [Theory]
    [InlineData("-1", true)]
    [InlineData("0", false)]
    [InlineData("3", false)]
    [InlineData("10", false)]
    [InlineData("11", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheRetryCountIsOutOfBounds(string retries, bool refused)
    {
        // The retry waits short enough for the stop budget at every count, so only the count's own bound refuses.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Consumers:RetryCount"] = retries,
            ["Kafka:Consumers:RetryMaxMilliseconds"] = "1000"
        }).Build();

        var add = () => MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Consumers:RetryCount must be from 0 to 10 (ARV-254)*");
        else
            add.Should().NotThrow();
    }

    #endregion

    #region Endpoint

    [Fact]
    public void TopicEndpoint_Should_GetTheHoldFilterAndAPollIntervalAboveTheLongestHold_When_Connected()
    {
        var settings = new KafkaSettings { Enabled = true, ServiceName = "main" };
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<KafkaProducerMetrics>()
            .AddSingleton(provider => new KafkaConsumerHolds(provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<KafkaProducerMetrics>()))
            .AddSingleton(Mock.Of<IDeadLetterSink>())
            .BuildServiceProvider();
        var context = new Mock<IRiderRegistrationContext>();
        context.Setup(c => c.GetService(It.IsAny<Type>())).Returns<Type>(services.GetService);
        var endpoint = new Mock<IKafkaTopicReceiveEndpointConfigurator<string, HoldProbe>>();
        Action<IReceivePipeConfigurator> receive = null;
        endpoint.Setup(e => e.ConfigureReceive(It.IsAny<Action<IReceivePipeConfigurator>>())).Callback<Action<IReceivePipeConfigurator>>(action => receive = action);
        var receivePipe = new Mock<IReceivePipeConfigurator>();
        Action<IRiderRegistrationContext, IKafkaTopicReceiveEndpointConfigurator<string, HoldProbe>> configure = null;
        var rider = new Mock<IKafkaRider>();
        rider.Setup(r => r.ConnectTopicEndpoint(KafkaTopics.FlowNowcast, "ariva-main.hold-probe",
                It.IsAny<Action<IRiderRegistrationContext, IKafkaTopicReceiveEndpointConfigurator<string, HoldProbe>>>()))
            .Callback<string, string, Action<IRiderRegistrationContext, IKafkaTopicReceiveEndpointConfigurator<string, HoldProbe>>>((_, _, action) => configure = action);
        var builder = new ArivaMessagingBuilder().Consume<HoldProbe, HoldProbeConsumer>(KafkaTopics.FlowNowcast, "hold-probe");

        builder.Consumers.Single().Connect(rider.Object, settings);
        configure.Should().NotBeNull("the rider was asked to connect the endpoint");
        configure(context.Object, endpoint.Object);

        receive.Should().NotBeNull("the endpoint's receive pipe is configured");
        receive(receivePipe.Object);

        endpoint.VerifySet(e => e.MaxPollInterval = settings.Consumers.MaxPollInterval, Times.Once());
        endpoint.Verify(e => e.ConfigureReceive(It.IsAny<Action<IReceivePipeConfigurator>>()), Times.Once());
        receivePipe.Verify(p => p.AddPipeSpecification(It.IsAny<MassTransit.Configuration.IPipeSpecification<ReceiveContext>>()), Times.Once(), "the hold filter on the receive pipe");
        services.GetRequiredService<KafkaConsumerHolds>().Holds().Should().ContainSingle()
            .Which.Should().Be((KafkaTopics.FlowNowcast, "ariva-main.hold-probe", 0d), "the endpoint's hold is reported from its connection on");
    }

    #endregion

    #region Helpers

    private static IPipe<ReceiveContext> Pipe(Task held)
    {
        var pipe = new Mock<IPipe<ReceiveContext>>();
        pipe.Setup(p => p.Send(It.IsAny<ReceiveContext>())).Returns(held);
        return pipe.Object;
    }

    private static DeadLetter Letter() => DeadLetters.Create(KafkaTopics.FlowNowcast, 0, 7, "zone-1", [1, 2, 3], [], Group,
        new InvalidOperationException("probe"), DateTime.UtcNow);

    /// <summary>A probe message of the endpoint test.</summary>
    public sealed class HoldProbe : EventBase
    {
        public override string GetPartitionKey() => "probe";
    }

    public sealed class HoldProbeConsumer : IConsumer<HoldProbe>
    {
        public Task Consume(ConsumeContext<HoldProbe> context) => Task.CompletedTask;
    }

    /// <summary>A clock that moves only when told.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = Stopwatch.GetTimestamp();

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * Stopwatch.Frequency));
    }

    /// <summary>
    /// A clock that moves only by what it is told, by the timers made on it (each moves it by its due time and fires at once)
    /// and, when <see cref="ReadStep"/> is set, by that much after each read.
    /// </summary>
    private sealed class SteppingClock : TimeProvider
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private long _moved;

        public TimeSpan ReadStep { get; init; }

        public TimeSpan Elapsed => GetElapsedTime(_start);

        public override long GetTimestamp()
        {
            var now = _start + Interlocked.Read(ref _moved);
            if (ReadStep > TimeSpan.Zero)
                Advance(ReadStep);
            return now;
        }

        public void Advance(TimeSpan by) => Interlocked.Add(ref _moved, (long)(by.TotalSeconds * Stopwatch.Frequency));

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new Fired();
        }

        private sealed class Fired : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Sink : IDeadLetterSink
    {
        private int _attempts;

        /// <summary>How many attempts are refused before one is taken; all of them by default.</summary>
        public int Refusals { get; init; } = int.MaxValue;

        /// <summary>When set, each attempt is noted at its start on this clock and moves it by <see cref="SendTime"/>.</summary>
        public SteppingClock Clock { get; init; }

        public TimeSpan SendTime { get; init; }

        public List<TimeSpan> Starts { get; } = [];

        public int Attempts => Volatile.Read(ref _attempts);

        public Task SendAsync(DeadLetter letter, CancellationToken ct)
        {
            if (Clock is not null)
            {
                lock (Starts)
                    Starts.Add(Clock.Elapsed);
                Clock.Advance(SendTime);
            }

            if (Interlocked.Increment(ref _attempts) > Refusals)
                return Task.CompletedTask;
            throw new KafkaProduceTimeoutException(DeadLetters.TopicFor(letter), KafkaProduce.SendTimeout);
        }
    }

    private sealed class Store : IDeadLetterStore
    {
        private int _attempts;

        public bool Fail { get; init; }
        public List<DeadLetter> Kept { get; } = [];
        public int Attempts => Volatile.Read(ref _attempts);

        public Task KeepAsync(DeadLetter letter, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            if (Fail)
                throw new TimeoutException("The database is away.");
            lock (Kept)
                Kept.Add(letter);
            return Task.CompletedTask;
        }
    }

    #endregion
}
