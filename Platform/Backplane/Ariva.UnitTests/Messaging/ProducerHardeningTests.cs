using System.Collections.Concurrent;
using System.Diagnostics;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Confluent.Kafka;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-165: the producer's hardening from the ARV-167 review (a recreation that fails in any way is tried again, a report
/// that never comes is swept), the failures the relay does not count against a row, the <c>kafka</c> readiness check, the
/// bus's stop bound within the host's shutdown timeout, and the stop of endpoints the bus skipped. The broker side is
/// proven in Ariva.IntegrationTests (CommitRaceTests).
/// </summary>
public sealed class ProducerHardeningTests
{
    private const string BusRefusal = "*leaves the message bus*";
    private const string GraceRefusal = "*must be positive and at most Hosting:GracePeriodSeconds*";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A client that keeps what it is given and reports only when told; raising a fatal error refuses later produces.</summary>
    private sealed class FakeClient
    {
        public Mock<IProducer<string, byte[]>> Mock { get; } = new(MockBehavior.Strict);
        public List<Action<DeliveryReport<string, byte[]>>> Reports { get; } = [];
        public Action<Error> RaiseError { get; init; }

        public FakeClient()
        {
            Mock.Setup(c => c.Produce(It.IsAny<string>(), It.IsAny<Message<string, byte[]>>(), It.IsAny<Action<DeliveryReport<string, byte[]>>>()))
                .Callback<string, Message<string, byte[]>, Action<DeliveryReport<string, byte[]>>>((_, _, report) =>
                {
                    lock (Reports)
                        Reports.Add(report);
                });
            Mock.Setup(c => c.Flush(It.IsAny<TimeSpan>())).Returns(0);
            Mock.Setup(c => c.Dispose());
        }

        public void Report(int index, ErrorCode code)
        {
            Action<DeliveryReport<string, byte[]>> report;
            lock (Reports)
                report = Reports[index];
            report(new DeliveryReport<string, byte[]> { Error = new Error(code) });
        }
    }

    /// <summary>A clock whose timestamps the test moves; its timers run in real time.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>The relay's log lines with their level (ARV-264).</summary>
    private sealed class RelayLines : ILogger<OutboxRelay>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Logged { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Logged.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>Records log lines with their level.</summary>
    private sealed class Lines : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Logged { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Logged.Enqueue((logLevel, formatter(state, exception)));
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
    public async Task Recreate_Should_LogCriticalAndTryAgain_When_MakingTheNewClientThrowsAnyException()
    {
        // ARV-165 (the ARV-167 review): the recreation runs on a timer, where an escaping exception ends the process, and it
        // caught only Kafka, argument and invalid operation exceptions. Any other one is now logged and the recreation tried
        // again after the interval.
        var made = new List<FakeClient>();
        var calls = 0;
        IProducer<string, byte[]> Make(Action<Error> raise)
        {
            if (Interlocked.Increment(ref calls) == 2)
                throw new IOException("the native library could not be loaded");
            var client = new FakeClient { RaiseError = raise };
            lock (made)
                made.Add(client);
            return client.Mock.Object;
        }

        var lines = new Lines();
        using var produce = new KafkaProduce(Make, TimeProvider.System, lines, TimeSpan.FromMilliseconds(300), null);
        produce.Started();

        made[0].RaiseError(new Error(ErrorCode.Local_Fatal, "idempotence refused", true));
        await UntilAsync(() => Volatile.Read(ref calls) >= 3 && produce.IsReady, "the recreation tried again and succeeded");

        made.Should().HaveCount(2, "the failed attempt made no client; the next one did");
        produce.ClientNumber.Should().Be(3, "each attempt takes a number, the failed one included");
        lines.Logged.Should().Contain(l => l.Level == LogLevel.Critical && l.Text.Contains("could not be recreated", StringComparison.Ordinal),
            "the failed recreation is logged Critical");
        var send = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        made[1].Report(0, ErrorCode.NoError);
        await send.Invoking(s => s).Should().NotThrowAsync("the recreated client sends");
    }

    [Fact]
    public async Task Sweep_Should_EndOnlyTheWaitersWhoseReportIsMoreThanTwiceTheSendBoundLate()
    {
        // ARV-165 (the ARV-167 review): librdkafka reports every message it was given, so the sweep covers only a report
        // that never comes; it ends that entry (and frees it) once it is twice the send bound late.
        var clock = new SteppingClock();
        var client = new FakeClient();
        using var produce = new KafkaProduce(client.Mock.Object, clock, NullLogger.Instance);
        var stale = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60));
        var recent = produce.SendAsync("ariva.flow.nowcast.v1", "zone-2", [2], Guid.NewGuid(), null, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(29));
        produce.Sweep().Should().Be(0, "nothing is yet more than 90 seconds late (89 and 29)");
        clock.Advance(TimeSpan.FromSeconds(2));
        produce.Sweep().Should().Be(1, "the first message is now 91 seconds without a report");

        (await stale.Invoking(s => s.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().ThrowAsync<KafkaException>()).Which.Error.Code.Should().Be(ErrorCode.Local_TimedOut);
        recent.IsCompleted.Should().BeFalse("the second is still waited for");
        produce.Unreported.Should().Be(1);
        client.Report(1, ErrorCode.NoError);
        await recent.Invoking(r => r).Should().NotThrowAsync();
        produce.Unreported.Should().Be(0);
        client.Invoking(c => c.Report(0, ErrorCode.NoError)).Should().NotThrow("a report that comes after the sweep is ignored");
    }

    [Theory]
    [InlineData(ErrorCode.Local_MsgTimedOut, true)]
    [InlineData(ErrorCode.Local_AllBrokersDown, true)]
    [InlineData(ErrorCode.Local_Transport, true)]
    [InlineData(ErrorCode.Local_QueueFull, true)]
    [InlineData(ErrorCode.Local_Destroy, true)]
    [InlineData(ErrorCode.Local_TimedOut, true)]
    [InlineData(ErrorCode.Local_PurgeQueue, true)]
    [InlineData(ErrorCode.Local_PurgeInflight, true)]
    [InlineData(ErrorCode.TopicAuthorizationFailed, false)]
    [InlineData(ErrorCode.UnknownTopicOrPart, false)]
    [InlineData(ErrorCode.MsgSizeTooLarge, false)]
    [InlineData(ErrorCode.Local_UnknownTopic, false)]
    public void IsBrokerUnavailable_Should_ClassifyTheClientsErrors(ErrorCode code, bool unavailable)
    {
        // ARV-165: the broker's absence does not count against an outbox row; the row's own problems (a missing topic, a
        // missing permission, a message too large) still do, with their backoff.
        KafkaProduce.IsBrokerUnavailable(new KafkaException(new Error(code))).Should().Be(unavailable);
        KafkaProduce.IsBrokerUnavailable(new ProduceException<string, byte[]>(new Error(code), null)).Should().Be(unavailable);
    }

    [Theory]
    [InlineData(ErrorCode.Local_Destroy, false, true)]
    [InlineData(ErrorCode.Local_PurgeQueue, false, true)]
    [InlineData(ErrorCode.Local_PurgeInflight, false, true)]
    [InlineData(ErrorCode.Local_Fatal, true, true)]
    [InlineData(ErrorCode.OutOfOrderSequenceNumber, true, true)]
    [InlineData(ErrorCode.Local_MsgTimedOut, false, false)]
    [InlineData(ErrorCode.Local_AllBrokersDown, false, false)]
    [InlineData(ErrorCode.Local_Transport, false, false)]
    [InlineData(ErrorCode.Local_QueueFull, false, false)]
    [InlineData(ErrorCode.Local_TimedOut, false, false)]
    [InlineData(ErrorCode.TopicAuthorizationFailed, false, false)]
    public void IsProducerUnavailable_Should_NameOnlyThisHostsProducersOwnConditions(ErrorCode code, bool fatal, bool own)
    {
        // ARV-246: a producer stopping or retired (Local_Destroy), disposed, after a fatal client error, or a message it purged at
        // that error (librdkafka purges its queue then; Ariva never purges) is the producer's, not the topic's; the broker's absence
        // is not, nor is a refusal. Each of the producer's own conditions is also a broker unavailability, so the row counts no
        // attempt.
        var error = new Error(code, "probe", fatal);

        KafkaProduce.IsProducerUnavailable(new KafkaException(error)).Should().Be(own);
        KafkaProduce.IsProducerUnavailable(new ProduceException<string, byte[]>(error, null)).Should().Be(own);
        if (own)
            KafkaProduce.IsBrokerUnavailable(new KafkaException(error)).Should().BeTrue();
        KafkaProduce.IsProducerUnavailable(new ObjectDisposedException(nameof(KafkaProduce))).Should().BeTrue();
        KafkaProduce.IsProducerUnavailable(new KafkaProduceTimeoutException("ariva.flow.nowcast.v1", KafkaProduce.SendTimeout)).Should().BeFalse();
        KafkaProduce.IsProducerUnavailable(new InvalidOperationException("Outbox row 1 has an empty payload.")).Should().BeFalse();
    }

    [Theory]
    [InlineData(ErrorCode.UnknownTopicOrPart, true)]
    [InlineData(ErrorCode.Local_UnknownTopic, true)]
    [InlineData(ErrorCode.TopicAuthorizationFailed, true)]
    [InlineData(ErrorCode.ClusterAuthorizationFailed, false)]
    [InlineData(ErrorCode.MsgSizeTooLarge, false)]
    [InlineData(ErrorCode.Local_MsgTimedOut, false)]
    [InlineData(ErrorCode.Local_AllBrokersDown, false)]
    public void IsTopicRefused_Should_NameTheFailuresOfATopicAlone(ErrorCode code, bool refused)
    {
        // ARV-177: a missing topic, or one this principal may not write to, fails every row of that topic the same way, so the
        // relay skips the topic for the rest of its pass; a failure of the cluster or of the row itself is not the topic's.
        KafkaProduce.IsTopicRefused(new KafkaException(new Error(code))).Should().Be(refused);
        KafkaProduce.IsTopicRefused(new ProduceException<string, byte[]>(new Error(code), null)).Should().Be(refused);
        KafkaProduce.IsTopicRefused(new InvalidOperationException("Outbox row 1 has an empty payload.")).Should().BeFalse();
        KafkaProduce.IsTopicRefused(new ObjectDisposedException(nameof(KafkaProduce))).Should().BeFalse();
    }

    [Theory]
    [InlineData(ErrorCode.Local_MsgTimedOut, true)]
    [InlineData(ErrorCode.Local_AllBrokersDown, false)]
    [InlineData(ErrorCode.Local_Transport, false)]
    [InlineData(ErrorCode.Local_QueueFull, false)]
    [InlineData(ErrorCode.Local_TimedOut, false)]
    [InlineData(ErrorCode.UnknownTopicOrPart, false)]
    [InlineData(ErrorCode.MsgSizeTooLarge, false)]
    public void IsPartitionUnavailable_Should_NameTheClientsMessageTimeoutAlone(ErrorCode code, bool partition)
    {
        // ARV-264: only a message that waited out the client's message timeout may be a partition without a leader (the relay
        // then asks the probe whether the brokers answer); the cluster's absence, the producer's own states and the row's or
        // topic's own failures are not.
        KafkaProduce.IsPartitionUnavailable(new KafkaException(new Error(code))).Should().Be(partition);
        KafkaProduce.IsPartitionUnavailable(new ProduceException<string, byte[]>(new Error(code), null)).Should().Be(partition);
        KafkaProduce.IsPartitionUnavailable(new KafkaProduceTimeoutException("ariva.flow.nowcast.v1", KafkaProduce.SendTimeout)).Should().BeFalse();
        KafkaProduce.IsPartitionUnavailable(new ObjectDisposedException(nameof(KafkaProduce))).Should().BeFalse();
        KafkaProduce.IsPartitionUnavailable(new KafkaException(new Error(ErrorCode.Local_Fatal, "fatal", true))).Should().BeFalse();
    }

    [Fact]
    public void KafkaClientConfig_Should_LetNoClientCreateATopicAndFailAMissingTopicBeforeTheMessageTimeout()
    {
        // ARV-177: no Ariva client asks a broker to create a topic it names (librdkafka's default for a producer-type client,
        // the admin client included, is to ask), and a send for a missing topic fails as the send's own failure
        // (UnknownTopicOrPart, then Local_UnknownTopic) well before the message timeout, which reads as the broker being away.
        // Proven on a broker that creates topics on request: Ariva.IntegrationTests, ImplicitTopicTests.
        var settings = new KafkaSettings { Enabled = true, BootstrapServers = "localhost:9092", ServiceName = "unit" };
        var producer = KafkaClientConfig.Producer(settings);

        producer.AllowAutoCreateTopics.Should().BeFalse();
        KafkaClientConfig.Admin(settings).AllowAutoCreateTopics.Should().BeFalse("the readiness probe's and the provisioner's admin clients");
        KafkaClientConfig.Consumer(settings, "ariva-unit.probe").AllowAutoCreateTopics.Should().BeFalse("the stream engine's consumer");
        KafkaClientConfig.Rider().AllowAutoCreateTopics.Should().BeFalse("MassTransit's topic endpoints");
        producer.TopicMetadataPropagationMaxMs.Should().Be(KafkaClientConfig.TopicPropagationMilliseconds);
        KafkaClientConfig.TopicPropagationMilliseconds.Should().BeLessThan(KafkaClientConfig.MessageTimeoutMilliseconds);
    }

    [Fact]
    public void IsBrokerUnavailable_Should_CountTheProducersOwnStatesAsUnavailable()
    {
        KafkaProduce.IsBrokerUnavailable(new KafkaProduceTimeoutException("ariva.flow.nowcast.v1", KafkaProduce.SendTimeout)).Should().BeTrue();
        KafkaProduce.IsBrokerUnavailable(new KafkaException(new Error(ErrorCode.Local_Fatal, "fatal", true))).Should().BeTrue("the producer's fatal gap");
        KafkaProduce.IsBrokerUnavailable(new ObjectDisposedException(nameof(KafkaProduce))).Should().BeTrue("a host stopping");
        KafkaProduce.IsBrokerUnavailable(new InvalidOperationException("Outbox row 1 has an empty payload.")).Should().BeFalse();
        KafkaProduce.IsBrokerUnavailable(new System.Text.Json.JsonException()).Should().BeFalse();
    }

    [Fact]
    public async Task Readiness_Should_BeDegradedWithoutDetail_When_TheProducerIsNotStartedOrNoBrokerAnswers()
    {
        var client = new FakeClient();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, NullLogger.Instance);
        var check = new KafkaReadiness(produce);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("kafka", check, HealthStatus.Degraded, ["ready"]) };

        var notStarted = await check.CheckHealthAsync(context, Ct);
        notStarted.Status.Should().Be(HealthStatus.Degraded, "a producer the host has not started cannot send");

        // Started, but the client gives no handle the probe's DescribeCluster request could use (as a client whose brokers are
        // all away gives no answer): the probe ends false within its bound, never with an exception.
        produce.Started();
        var clock = Stopwatch.StartNew();
        var noBroker = await check.CheckHealthAsync(context, Ct);
        noBroker.Status.Should().Be(HealthStatus.Degraded, "an outage raises the alert; readiness keeps answering 200 (the owner's ARV-125 decision)");
        noBroker.Description.Should().BeNull("the readiness answer carries no detail (CWE-200)");
        noBroker.Exception.Should().BeNull();
        clock.Elapsed.Should().BeLessThan(KafkaReadiness.ProbeTimeout + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Readiness_Should_AskTheBrokersOnceForABurstOfChecks()
    {
        // At most one probe in flight, and a result reused for a second: a burst of readiness requests asks the broker once.
        var client = new FakeClient();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, NullLogger.Instance);
        produce.Started();

        var first = produce.BrokersAnswerAsync(KafkaReadiness.ProbeTimeout);
        var second = produce.BrokersAnswerAsync(KafkaReadiness.ProbeTimeout);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task BrokersAnswer_Should_EndAsFalseWithinItsBoundAndAskAfresh_When_TheDescriptionNeverEnds()
    {
        // ARV-245: the probe's task is shared and kept, and the outbox relay waits on it before each pass after a broker
        // failure, so a description the client never ends (its client retired under it) must not keep that task from ending:
        // it ends as false ProbeSlack after its timeout, and a probe after the reuse time asks again.
        var calls = 0;
        var client = new FakeClient();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, NullLogger.Instance)
        {
            DescribeCluster = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return new TaskCompletionSource<bool>().Task;
            }
        };
        produce.Started();
        var timeout = TimeSpan.FromMilliseconds(200);
        var clock = Stopwatch.StartNew();

        (await produce.BrokersAnswerAsync(timeout).WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeFalse("a description that never ends is no answer");
        clock.Elapsed.Should().BeLessThan(timeout + KafkaProduce.ProbeSlack + TimeSpan.FromSeconds(2), "the probe keeps to its own bound");

        (await produce.BrokersAnswerAsync(timeout).WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeFalse();
        calls.Should().Be(2, "past the reuse time the next probe asks afresh instead of the stranded one");
    }

    [Fact]
    public async Task OutboxTransport_Should_AnswerWithTheProducersProbe_When_TheRelayAsksForTheBrokers()
    {
        // ARV-245: the relay's gate through the real transport: false while the producer has not started, then the probe's
        // answer (true, and false once the brokers do not answer, after the probe's reuse time), and false within the
        // readiness probe's bound for a client with no handle to describe the cluster through.
        var answers = true;
        var client = new FakeClient();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, NullLogger.Instance) { DescribeCluster = (_, _) => Task.FromResult(answers) };
        IOutboxTransport transport = new KafkaOutboxTransport(produce, new EventCatalog([]));

        (await transport.BrokersAnswerAsync(Ct)).Should().BeFalse("the producer has not started");
        produce.Started();
        (await transport.BrokersAnswerAsync(Ct)).Should().BeTrue();
        answers = false;
        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct);
        (await transport.BrokersAnswerAsync(Ct)).Should().BeFalse("the brokers do not answer now");

        using var handleless = new KafkaProduce(new FakeClient().Mock.Object, TimeProvider.System, NullLogger.Instance);
        handleless.Started();
        IOutboxTransport noHandle = new KafkaOutboxTransport(handleless, new EventCatalog([]));
        var clock = Stopwatch.StartNew();
        (await noHandle.BrokersAnswerAsync(Ct)).Should().BeFalse("a client without a handle describes nothing");
        clock.Elapsed.Should().BeLessThan(KafkaReadiness.ProbeTimeout + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OutboxTransport_Should_StopWaiting_When_TheRelaysTokenIsCancelled()
    {
        // ARV-245: the relay's stop ends its wait for a probe in flight at once, as a cancellation.
        var client = new FakeClient();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, NullLogger.Instance)
        {
            DescribeCluster = (_, _) => new TaskCompletionSource<bool>().Task
        };
        produce.Started();
        IOutboxTransport transport = new KafkaOutboxTransport(produce, new EventCatalog([]));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await FluentActions.Awaiting(() => transport.BrokersAnswerAsync(stop.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    private static ServiceProvider Messaging(Dictionary<string, string> settings)
    {
        settings["Kafka:Enabled"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(services, configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null, null, 50, 35)]
    [InlineData("80", "90", 80, 65)]
    public void AddArivaMessaging_Should_SetTheShutdownTimeoutAndTheBusShareOfIt(string shutdown, string grace, int shutdownSeconds, int busSeconds)
    {
        // ARV-165: every host's shutdown timeout is Hosting:ShutdownSeconds (50 by default), and the bus's stop wait is that
        // less 15 s (the skipped endpoints' stop, the producer's flush and the rest): 35 s, so a stop during a message's
        // retries (up to 3 x 5 s, 1.25 times that with MassTransit's jitter, and the 10 s keep) fits, and the host stops in time
        // while a consumer is held by a commit.
        var settings = new Dictionary<string, string>();
        if (shutdown is not null)
            settings["Hosting:ShutdownSeconds"] = shutdown;
        if (grace is not null)
            settings["Hosting:GracePeriodSeconds"] = grace;
        using var provider = Messaging(settings);

        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value.ShutdownTimeout
            .Should().Be(TimeSpan.FromSeconds(shutdownSeconds));
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MassTransitHostOptions>>().Value.StopTimeout
            .Should().Be(TimeSpan.FromSeconds(busSeconds));
    }

    [Theory]
    [InlineData("50", "60", "3", "5000", null)] // the defaults: the bus gets 35 s, the clean stop needs 28.75 s (ARV-268: 3 x 5 x 1.25 and 10)
    [InlineData("44", "60", "3", "5000", null)] // the bus gets 29 s, just more than the clean stop
    [InlineData("43", "60", "3", "5000", BusRefusal)] // 28 s, less than the retry schedule with its jitter and the keep
    [InlineData("50", "60", "4", "5000", null)] // 4 x 5 x 1.25 and the keep: exactly the bus's 35 s
    [InlineData("50", "60", "4", "5001", "*35 s to stop, less than the 35.01 s*")] // a millisecond more a wait passes it (30 s by the cap alone, which the jitter can overrun)
    [InlineData("50", "60", "6", "5000", BusRefusal)] // six retries of 5 s with their jitter and the keep need 47.5 s, more than 35
    [InlineData("55", "60", "3", "5000", null)] // ends 5 s before the grace period
    [InlineData("56", "60", "3", "5000", GraceRefusal)] // leaves the dispose and the exit less than 5 s
    [InlineData("50", "50", "3", "5000", GraceRefusal)] // a grace period no longer than the shutdown timeout
    [InlineData("0", "60", "3", "5000", GraceRefusal)]
    public void AddArivaMessaging_Should_RefuseAStopBudgetThatDoesNotFit(string shutdown, string grace, string retries, string retryMax, string refusal)
    {
        var settings = new Dictionary<string, string>
        {
            ["Hosting:ShutdownSeconds"] = shutdown,
            ["Hosting:GracePeriodSeconds"] = grace,
            ["Kafka:Consumers:RetryCount"] = retries,
            ["Kafka:Consumers:RetryMaxMilliseconds"] = retryMax
        };

        var add = () => Messaging(settings).Dispose();

        if (refusal is not null)
            add.Should().Throw<InvalidOperationException>().WithMessage(refusal);
        else
            add.Should().NotThrow();
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("5", false)]
    [InlineData("30", false)]
    [InlineData("31", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheRelaysBrokerRetryIsOutOfBounds(string seconds, bool refused)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Outbox:BrokerRetrySeconds"] = seconds
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Outbox:BrokerRetrySeconds*");
        else
            add.Should().NotThrow();
    }

    [Theory]
    [InlineData("200", "5000", false)]
    [InlineData("0", "0", false)]
    [InlineData("0", "60000", false)]
    [InlineData("60000", "60000", false)]
    [InlineData("5000", "5000", false)]
    [InlineData("-1", "5000", true)]
    [InlineData("200", "-1", true)]
    [InlineData("-200", "-100", true)]
    [InlineData("200", "60001", true)]
    [InlineData("60001", "60001", true)]
    [InlineData("5001", "5000", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheConsumersRetryWaitsAreOutOfRangeOrInverted(string min, string max, bool refused)
    {
        // ARV-266 (from the ARV-254 review): MassTransit's exponential retry takes the waits as they are, and the stop budget alone
        // let a negative pair through, so each setting is held to 0 to a minute and the base to no more than the cap. No retry
        // here, so the stop budget (the retries times the cap) does not refuse the largest caps first.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Consumers:RetryCount"] = "0",
            ["Kafka:Consumers:RetryMinMilliseconds"] = min,
            ["Kafka:Consumers:RetryMaxMilliseconds"] = max
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Consumers:RetryMinMilliseconds and RetryMaxMilliseconds*");
        else
            add.Should().NotThrow();
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("9", true)]
    [InlineData("10", false)]
    [InlineData("60", false)]
    [InlineData("120", false)]
    [InlineData("121", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheRelaysPassTimeIsOutOfBounds(string seconds, bool refused)
    {
        // ARV-264 (its security review's L2): the pass's time decides how long a pass holds its transaction and leader lock, and
        // whether a topic with a partition without a leader can be held, so it is bounded, or the host does not start.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Outbox:PassSeconds"] = seconds
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Outbox:PassSeconds*");
        else
            add.Should().NotThrow();
    }

    [Fact]
    public void MinPassSecondsForTopicHolds_Should_BeTheProducersBoundAndASecond_When_ItIsChecked() =>
        // ARV-264: the client's message timeout (30 s) is reported within the producer's bound, so a pass this long or longer
        // has seen the report before its own bound cuts the send.
        OutboxRelaySettings.MinPassSecondsForTopicHolds.Should().Be((int)KafkaProduce.SendTimeout.TotalSeconds + 1);

    [Theory]
    [InlineData(20, true)]
    [InlineData(45, true)]
    [InlineData(46, false)]
    [InlineData(60, false)]
    public async Task Relay_Should_WarnAtStart_When_ItsPassLeavesNoRoomForTheProducersReport(int passSeconds, bool warned)
    {
        // ARV-264 (its security review's L2): below 46 s a pass cuts a send that waited for a partition without a leader before
        // the producer reports it, so such a topic is never held and ends each pass at its row; the relay says so at start.
        var lines = new RelayLines();
        var transport = new Mock<IOutboxTransport>();
        transport.SetupGet(t => t.IsReady).Returns(false);
        using var relay = new OutboxRelay(new Ariva.Infra.Settings.DatabaseSettings(),
            new KafkaSettings { Outbox = new OutboxRelaySettings { PassSeconds = passSeconds, PollMilliseconds = 20 } }, transport.Object, TimeProvider.System, lines);

        await relay.StartAsync(Ct);
        // .NET 10 runs ExecuteAsync on a task of its own: the start's lines are written once it runs, and a stop before then
        // would end it unstarted.
        await UntilAsync(() => lines.Logged.Any(l => l.Text.StartsWith("Outbox relay started", StringComparison.Ordinal)), "the relay's start");
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await relay.StopAsync(stop.Token);

        lines.Logged.Any(l => l.Level == LogLevel.Warning && l.Text.Contains("Kafka:Outbox:PassSeconds", StringComparison.Ordinal)).Should().Be(warned);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("15", false)]
    [InlineData("1440", false)]
    [InlineData("1441", true)]
    [InlineData("-5", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheRelaysBrokerStreakIsOutOfBounds(string minutes, bool refused)
    {
        // ARV-245: the streak after which a row the broker keeps refusing is reported is bounded, or the host does not start.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Outbox:BrokerStreakMinutes"] = minutes
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Outbox:BrokerStreakMinutes*");
        else
            add.Should().NotThrow();
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("10", false)]
    [InlineData("300", false)]
    [InlineData("301", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheTopicWatchIsOutOfBounds(string seconds, bool refused)
    {
        // ARV-176: a consumed topic that does not exist is looked for again every TopicWatchSeconds.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Consumers:TopicWatchSeconds"] = seconds
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Consumers:TopicWatchSeconds*");
        else
            add.Should().NotThrow();
    }

    [Theory]
    [InlineData("1", null, false)]
    [InlineData("8", null, true)]
    [InlineData("2", null, true)]
    [InlineData("0", null, true)]
    [InlineData(null, "1", false)]
    [InlineData(null, "6", false)]
    [InlineData(null, "0", true)]
    [InlineData(null, "7", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_AnEndpointWouldHaveMoreThanOneLaneOrItsConsumersAreOutOfBounds(string lanes, string consumers, bool refused)
    {
        // ARV-253: one lane per Kafka consumer, so a stop cannot leave a completed record after one it never handled, which
        // MassTransit's checkpointer then commits past; parallel consumers per endpoint, each with its own partitions, instead.
        var settings = new Dictionary<string, string> { ["Kafka:Enabled"] = "true" };
        if (lanes is not null)
            settings["Kafka:Consumers:ConcurrentMessageLimit"] = lanes;
        if (consumers is not null)
            settings["Kafka:Consumers:ConsumersPerEndpoint"] = consumers;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (!refused)
            add.Should().NotThrow();
        else if (lanes is not null)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Consumers:ConcurrentMessageLimit must be 1 (ARV-253*ConsumersPerEndpoint*");
        else
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Consumers:ConsumersPerEndpoint must be from 1 to 6 (ARV-253)*");
        new ConsumerSettings().ConcurrentMessageLimit.Should().Be(1);
        new ConsumerSettings().ConsumersPerEndpoint.Should().Be(1);
    }

    [Theory]
    [InlineData("64", true)]
    [InlineData("65", false)]
    [InlineData("70", false)]
    [InlineData("120", false)]
    [InlineData("121", true)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheHandlerTimeoutIsOutOfBounds(string seconds, bool refused)
    {
        // ARV-096e: each attempt of a consumed message ends at Kafka:Consumers:HandlerTimeoutSeconds.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Consumers:HandlerTimeoutSeconds"] = seconds
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Consumers:HandlerTimeoutSeconds must be from 65 to 120*");
        else
            add.Should().NotThrow();
        new ConsumerSettings().HandlerTimeoutSeconds.Should().Be(70, "above the archive consumers' wait for a database outage, about a minute");
    }

    [Theory]
    [InlineData("60", "130", false)]
    [InlineData("70", "130", false)]
    [InlineData("71", "130", true)]
    [InlineData("70", "115", true)]
    [InlineData("55", "115", false)]
    [InlineData("300", "130", true)]
    [InlineData("-1", "130", true)]
    [InlineData("300", "360", false)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheTopicWaitDoesNotFitTheStartupAllowance(string wait, string allowance, bool refused)
    {
        // ARV-176 (recommendation 1 and R4 of the review): the host's start holds its web server while its consumers wait
        // for their topics (StartWaitSeconds) and for their readiness (30 s); with a 30 s margin (the bus's start, at most
        // 15 s, and a metadata request's overrun, at most 12 s) both must fit in the pod's startup allowance at its worst,
        // initialDelaySeconds + periodSeconds x (failureThreshold - 1): 10 + 15 x (9 - 1) = 130 s in the chart. 115 s is
        // the old probe's worst case (10 + 15 x 7), where 70 s did not fit; or the kubelet restarts the host again and again.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["Kafka:Consumers:StartWaitSeconds"] = wait,
            ["Hosting:StartupAllowanceSeconds"] = allowance
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Kafka:Consumers:StartWaitSeconds*Hosting:StartupAllowanceSeconds*");
        else
            add.Should().NotThrow();
    }

    [Fact]
    public void Defaults_Should_FitTheChartsStartupAllowance_When_NothingIsSet()
    {
        // ARV-176: the defaults (60 s topic wait, 30 s ready wait, 30 s margin with the bus's 15 s start bound in it) fit
        // the chart's 130 s, its startup probe's worst case.
        new Ariva.Infra.Settings.HostingSettings().StartupAllowanceSeconds.Should().Be(130);
        new ConsumerSettings().StartWaitSeconds.Should().Be(60);
        var services = new ServiceCollection();
        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(services,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Kafka:Enabled"] = "true" }).Build());
        add.Should().NotThrow();

        // The bus's start is waited for, at most 15 s (recommendation (b) of the re-review).
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MassTransitHostOptions>>().Value;
        bus.WaitUntilStarted.Should().BeTrue();
        bus.StartTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Gauge_Should_ReportEachConsumedTopicMissingOrPresent_When_Collected()
    {
        // ARV-176: the signal ARV-125 alerts on: 1 for a consumed topic the broker does not have, 0 otherwise, per topic and
        // consumer group, read from the consumers' state when collected.
        using var metrics = new KafkaProducerMetrics();
        var missing = new HashSet<string> { "ariva-integration.aman-egate-intervals" };
        metrics.ObserveConsumedTopics(() =>
        [
            ("aman.feed.desk-session-changed.v1", "ariva-integration.aman-desk-sessions", missing.Contains("ariva-integration.aman-desk-sessions")),
            ("aman.feed.egate-interval-stats.v1", "ariva-integration.aman-egate-intervals", missing.Contains("ariva-integration.aman-egate-intervals"))
        ]);
        var seen = new ConcurrentDictionary<string, int>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == metrics.Meter && instrument.Name == KafkaProducerMetrics.ConsumedTopicMissingName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "topic")
                    seen[(string)tag.Value!] = value;
            }
        });
        listener.Start();

        listener.RecordObservableInstruments();
        seen.Should().Contain("aman.feed.egate-interval-stats.v1", 1).And.Contain("aman.feed.desk-session-changed.v1", 0);

        missing.Clear();
        listener.RecordObservableInstruments();
        seen.Should().Contain("aman.feed.egate-interval-stats.v1", 0, "the topic appeared and its consumer started");
    }

    [Fact]
    public async Task Endpoints_Should_StopOnlyTheEndpointsTheBusSkipped_When_TheHostStops()
    {
        // ARV-165 (ARV-166's residual b): the bus stops started, ready and faulted endpoints; one between restarts (Completed)
        // is skipped, and is stopped here, which ends its transport's restarts and raises its Stopping event.
        var endpoints = new KafkaEndpoints();
        var (stopped, stoppedEndpoint) = Observed();
        var (skipped, skippedEndpoint) = Observed();
        endpoints.Add(stopped);
        endpoints.Add(skipped);

        await stopped.Ready(Event<ReceiveEndpointReady>(stoppedEndpoint.Object));
        await stopped.Stopping(Event<ReceiveEndpointStopping>(stoppedEndpoint.Object));
        await skipped.Ready(Event<ReceiveEndpointReady>(skippedEndpoint.Object));
        await skipped.Completed(Event<ReceiveEndpointCompleted>(skippedEndpoint.Object));

        var (count, inTime) = await endpoints.StopSkippedAsync(NullLogger.Instance, Ct);

        count.Should().Be(1);
        inTime.Should().BeTrue();
        skippedEndpoint.Verify(e => e.Stop(It.IsAny<CancellationToken>()), Times.Once());
        stoppedEndpoint.Verify(e => e.Stop(It.IsAny<CancellationToken>()), Times.Never(), "the bus stopped it already");
    }

    [Fact]
    public async Task Endpoints_Should_EndTheStopWithinItsBound_When_ASkippedEndpointDoesNotStop()
    {
        // A consumer held by an offset commit does not finish stopping; the host's stop goes on after the bound.
        var endpoints = new KafkaEndpoints();
        var (held, endpoint) = Observed(hang: true);
        endpoints.Add(held);
        await held.Completed(Event<ReceiveEndpointCompleted>(endpoint.Object));
        var clock = Stopwatch.StartNew();

        var (count, inTime) = await endpoints.StopSkippedAsync(NullLogger.Instance, Ct);

        count.Should().Be(1);
        inTime.Should().BeFalse();
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(KafkaEndpoints.StopTimeout - TimeSpan.FromMilliseconds(16))
            .And.BeLessThan(KafkaEndpoints.StopTimeout + TimeSpan.FromSeconds(5));
    }

    private static (EndpointStopping Stopping, Mock<IReceiveEndpoint> Endpoint) Observed(bool hang = false)
    {
        var endpoint = new Mock<IReceiveEndpoint>();
        endpoint.SetupGet(e => e.InputAddress).Returns(new Uri("loopback://localhost/kafka/ariva.flow.nowcast.v1"));
        endpoint.Setup(e => e.Stop(It.IsAny<CancellationToken>())).Returns<CancellationToken>(ct => hang ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask);
        return (new EndpointStopping(), endpoint);
    }

    private static T Event<T>(IReceiveEndpoint endpoint) where T : class, ReceiveEndpointEvent
    {
        var raised = new Mock<T>();
        raised.SetupGet(e => e.ReceiveEndpoint).Returns(endpoint);
        raised.SetupGet(e => e.InputAddress).Returns(endpoint.InputAddress);
        return raised.Object;
    }
}
