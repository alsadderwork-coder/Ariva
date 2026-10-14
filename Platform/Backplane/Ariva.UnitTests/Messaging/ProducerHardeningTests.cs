using System.Collections.Concurrent;
using System.Diagnostics;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
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

        // Started, but the client gives no handle a metadata request could use (as a client whose brokers are all away gives
        // no answer): the probe ends false within its bound, never with an exception.
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
        // retries (up to 3 x 5 s and the 10 s keep) fits, and the host stops in time while a consumer is held by a commit.
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
    [InlineData("50", "60", "3", "5000", false)] // the defaults: the bus gets 35 s, the clean stop needs 25 s
    [InlineData("40", "60", "3", "5000", false)] // the bus gets 25 s, exactly the clean stop
    [InlineData("39", "60", "3", "5000", true)] // 24 s, less than the retry schedule and the keep
    [InlineData("50", "60", "6", "5000", true)] // six retries of 5 s and the keep need 40 s, more than 35
    [InlineData("55", "60", "3", "5000", false)] // ends 5 s before the grace period
    [InlineData("56", "60", "3", "5000", true)] // leaves the dispose and the exit less than 5 s
    [InlineData("50", "50", "3", "5000", true)] // a grace period no longer than the shutdown timeout
    [InlineData("0", "60", "3", "5000", true)]
    public void AddArivaMessaging_Should_RefuseAStopBudgetThatDoesNotFit(string shutdown, string grace, string retries, string retryMax, bool refused)
    {
        var settings = new Dictionary<string, string>
        {
            ["Hosting:ShutdownSeconds"] = shutdown,
            ["Hosting:GracePeriodSeconds"] = grace,
            ["Kafka:Consumers:RetryCount"] = retries,
            ["Kafka:Consumers:RetryMaxMilliseconds"] = retryMax
        };

        var add = () => Messaging(settings).Dispose();

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*Hosting:ShutdownSeconds*");
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
