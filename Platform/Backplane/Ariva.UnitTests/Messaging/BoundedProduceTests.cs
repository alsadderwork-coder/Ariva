using System.Diagnostics;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-166 and ARV-167: every produce Ariva awaits ends within its bound whatever the client does, a caller that gives up
/// stops only its own wait, a send never reaches the client with a token (so nothing a caller or a failure does can dispose
/// the host's one producer), a full queue refuses at once, the message carries the key, value and headers the rider wrote,
/// and the dead-letter loops end when their endpoint stops. The broker side of the same story (a produce queued behind one
/// in flight on a dropped connection, a full queue during an outage) is proven against a real broker in
/// Ariva.IntegrationTests (StuckProduceTests, ProducerQueueTests, ProducerContractTests, DeadLetterShutdownTests).
/// </summary>
public sealed class BoundedProduceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(300);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A client that keeps every message it is given, with its delivery handler, and reports only when told.</summary>
    private sealed class Client
    {
        private int _fatal;
        private int _disposed;

        public Mock<IProducer<string, byte[]>> Mock { get; } = new(MockBehavior.Strict);
        public List<(string Topic, Message<string, byte[]> Message, Action<DeliveryReport<string, byte[]>> Report)> Given { get; } = [];

        /// <summary>The producer's error callback for this client (set when the producer makes it).</summary>
        public Action<Error> RaiseError { get; set; }

        /// <summary>
        /// When the producer began this client's generation, on the test's clock: the producer's own timestamp, which starts
        /// the client's recreation window, read back through a <see cref="StampingClock"/> (null without one).
        /// </summary>
        public TimeSpan? CreatedAt { get; init; }

        /// <summary>When the client's mock was built, on the test's clock (after <see cref="CreatedAt"/>; for the test output only).</summary>
        public TimeSpan MadeAt { get; init; }

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public Client(ErrorCode? refuse = null, bool autoReport = false)
        {
            var produce = Mock.Setup(c => c.Produce(It.IsAny<string>(), It.IsAny<Message<string, byte[]>>(), It.IsAny<Action<DeliveryReport<string, byte[]>>>()));
            if (refuse is { } code)
            {
                produce.Throws(new ProduceException<string, byte[]>(new Error(code), null));
            }
            else
            {
                produce.Callback<string, Message<string, byte[]>, Action<DeliveryReport<string, byte[]>>>((topic, message, report) =>
                {
                    // librdkafka refuses every produce once its client has raised a fatal error.
                    if (Volatile.Read(ref _fatal) != 0)
                        throw new ProduceException<string, byte[]>(new Error(ErrorCode.Local_Fatal, "fatal", true), null);
                    lock (Given)
                        Given.Add((topic, message, report));
                    if (autoReport)
                        ThreadPool.QueueUserWorkItem(_ => report(new DeliveryReport<string, byte[]> { Error = new Error(ErrorCode.NoError) }));
                });
            }

            Mock.Setup(c => c.Flush(It.IsAny<TimeSpan>())).Returns(0);
            Mock.Setup(c => c.Dispose()).Callback(() => Volatile.Write(ref _disposed, 1));
        }

        public void Report(int index, ErrorCode code)
        {
            Action<DeliveryReport<string, byte[]>> report;
            lock (Given)
                report = Given[index].Report;
            report(new DeliveryReport<string, byte[]> { Error = new Error(code) });
        }

        /// <summary>The client raises a fatal error, as librdkafka does once per client (an idempotence refusal).</summary>
        public void Fail()
        {
            Volatile.Write(ref _fatal, 1);
            RaiseError(new Error(ErrorCode.Local_Fatal, "idempotence refused", true));
        }

        public string[] MessageIds()
        {
            lock (Given)
                return [.. Given.Select(g => System.Text.Encoding.UTF8.GetString(g.Message.Headers.GetLastBytes(KafkaProduce.MessageIdHeader)))];
        }
    }

    /// <summary>
    /// The system clock, remembering on each thread the last timestamp it gave. <c>KafkaProduce.NewGeneration</c> takes a
    /// generation's timestamp (the start of its client's recreation window) right before it calls the client factory, on
    /// the same thread, so the factory reads it back here: the window is measured where the product measures it, not after
    /// the factory has built its mock, which under load can take longer for one client than for the next.
    /// </summary>
    private sealed class StampingClock : TimeProvider
    {
        // Per thread, with the clock that gave it, so that nothing needs disposing while the producer's timers may still read the clock.
        [ThreadStatic]
        private static StampingClock _owner;

        [ThreadStatic]
        private static long _stamp;

        public override long GetTimestamp()
        {
            var now = base.GetTimestamp();
            _owner = this;
            _stamp = now;
            return now;
        }

        /// <summary>The last timestamp this clock gave on the calling thread (0 if none).</summary>
        public long LastOnThisThread => ReferenceEquals(_owner, this) ? _stamp : 0;
    }

    /// <summary>The clients a producer made, in order, each a <see cref="Client"/>.</summary>
    /// <param name="autoReport">Whether each client reports every message at once.</param>
    /// <param name="stamps">The producer's clock, when the test reads the generations' own timestamps.</param>
    private sealed class Clients(bool autoReport = false, StampingClock stamps = null)
    {
        private readonly List<Client> _made = [];
        private readonly long _origin = Stopwatch.GetTimestamp();

        public int Count
        {
            get
            {
                lock (_made)
                    return _made.Count;
            }
        }

        public Client this[int index]
        {
            get
            {
                lock (_made)
                    return _made[index];
            }
        }

        public IProducer<string, byte[]> Make(Action<Error> raiseError)
        {
            TimeSpan? createdAt = null;
            if (stamps is not null)
            {
                var stamp = stamps.LastOnThisThread;
                if (stamp == 0)
                    throw new InvalidOperationException("The producer took no timestamp on this thread before it called the client factory; the window can no longer be read here.");
                createdAt = Stopwatch.GetElapsedTime(_origin, stamp);
            }

            var client = new Client(autoReport: autoReport) { RaiseError = raiseError, CreatedAt = createdAt, MadeAt = Stopwatch.GetElapsedTime(_origin) };
            lock (_made)
                _made.Add(client);
            return client.Mock.Object;
        }
    }

    /// <summary>The producer's counter, summed per kind.</summary>
    private static (System.Diagnostics.Metrics.MeterListener Listener, System.Collections.Concurrent.ConcurrentDictionary<string, long> Counts) Listen(KafkaProducerMetrics metrics)
    {
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == metrics.Meter)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "kind")
                    counts.AddOrUpdate((string)tag.Value!, value, (_, sum) => sum + value);
            }
        });
        listener.Start();
        return (listener, counts);
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

    /// <summary>The timer's clock counts whole milliseconds, so it may fire up to one tick before a Stopwatch says it is due.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// The time between two clients' generations, by the producer's own timestamps (where its recreation window is
    /// counted from), written to the test output with the gap between their mocks' construction for comparison.
    /// </summary>
    private static TimeSpan Gap(string what, Client earlier, Client later)
    {
        var gap = later.CreatedAt!.Value - earlier.CreatedAt!.Value;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{what}: {gap.TotalMilliseconds:F1} ms between the generations ({(later.MadeAt - earlier.MadeAt).TotalMilliseconds:F1} ms between the mocks)"));
        return gap;
    }

    [Fact]
    public async Task Wait_Should_FailAsATimeoutAtItsBound_When_TheClientNeverReports()
    {
        // ARV-172: one timer ends the wait, so the timeout lands at the bound, not before it and not long after it. The send
        // has no token of its own any more (ARV-167): the bound's timer is the only one.
        var never = new TaskCompletionSource<Error>();
        var clock = Stopwatch.StartNew();
        TimeSpan ended = default;

        var act = async () =>
        {
            try
            {
                await KafkaProduce.WaitAsync("ariva.flow.nowcast.v1", Bound, TimeProvider.System, never.Task, Ct);
            }
            finally
            {
                ended = clock.Elapsed;
            }
        };

        var thrown = await act.Should().ThrowAsync<KafkaProduceTimeoutException>();
        // The timer's clock counts whole milliseconds, so it may fire up to one tick before the Stopwatch says the bound passed.
        ended.Should().BeGreaterThanOrEqualTo(Bound - TimeSpan.FromMilliseconds(16), "the wait lasts its whole bound")
            .And.BeLessThan(Bound + TimeSpan.FromSeconds(5), "and ends at it");
        thrown.Which.Should().BeAssignableTo<TimeoutException>("Ingest answers a timeout 503, and the relay ends its pass on it")
            .And.NotBeAssignableTo<OperationCanceledException>("a send that reached its bound failed; the caller did not cancel it");
        thrown.Which.Message.Should().Contain("ariva.flow.nowcast.v1");
    }

    [Fact]
    public async Task Produce_Should_StopOnlyItsOwnWait_When_TheCallerGivesUp()
    {
        // ARV-166: MassTransit disposed a topic's producer when a send on it was cancelled, which stranded every other send on
        // it. The client's Produce takes no token, so the caller's cancellation ends its wait and nothing else.
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);
        using var caller = new CancellationTokenSource();

        var send = produce.SendAsync("ariva.device.track-sample.v1", "zone-1", [1], Guid.NewGuid(), null, caller.Token);
        var other = produce.SendAsync("ariva.device.track-sample.v1", "zone-1", [2], Guid.NewGuid(), null, Ct);
        await caller.CancelAsync();

        (await send.Invoking(s => s).Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(caller.Token,
            "the wait ends as the caller's own cancellation");
        client.Report(0, ErrorCode.NoError);
        client.Report(1, ErrorCode.NoError);
        await other.Invoking(o => o).Should().NotThrowAsync("the other send on the same producer got its report");
        client.Mock.Verify(c => c.Dispose(), Times.Never(), "nothing a caller does disposes the producer");
        produce.Unreported.Should().Be(0);
    }

    [Fact]
    public async Task Produce_Should_LeaveNothingUnobserved_When_TheReportFailsAfterTheCallerGaveUp()
    {
        // Nobody awaits a send its caller gave up on, so its later failure must not surface as an unobserved task exception.
        // A task faulted the same way and never observed is the control.
        var control = "control-" + Guid.NewGuid().ToString("N");
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                seen.Add(inner.GetType().Name + ": " + inner.Message);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await AbandonAsync();
            FaultUnobserved(control);
            for (var i = 0; i < 50 && !seen.Any(m => m.Contains(control, StringComparison.Ordinal)); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(20, Ct);
            }

            seen.Should().Contain(m => m.Contains(control, StringComparison.Ordinal), "the check sees a failure that nobody observed");
            seen.Should().NotContain(m => m.Contains("KafkaException", StringComparison.Ordinal), "the abandoned send's late failure is not left unobserved");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonAsync()
    {
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);
        using var caller = new CancellationTokenSource();
        var send = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, caller.Token);
        await caller.CancelAsync();
        await send.Invoking(s => s).Should().ThrowAsync<OperationCanceledException>();
        client.Report(0, ErrorCode.Local_MsgTimedOut);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FaultUnobserved(string message) => _ = Task.FromException(new InvalidOperationException(message));

    [Fact]
    public async Task Produce_Should_FailWithTheReportsError_When_KafkaDidNotTakeIt()
    {
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);

        var send = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        client.Report(0, ErrorCode.Local_MsgTimedOut);

        (await send.Invoking(s => s).Should().ThrowAsync<KafkaException>()).Which.Error.Code.Should().Be(ErrorCode.Local_MsgTimedOut);
    }

    [Fact]
    public async Task Produce_Should_Complete_When_TheBrokerAcknowledges()
    {
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);

        var send = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        send.IsCompleted.Should().BeFalse("the send is waited for until its report");
        client.Report(0, ErrorCode.NoError);

        await send.Invoking(s => s).Should().NotThrowAsync();
        produce.Unreported.Should().Be(0);
    }

    [Fact]
    public void Produce_Should_RefuseAtOnce_When_TheQueueIsFull()
    {
        // librdkafka refuses in Produce itself when its bounded queue is full (ARV-167): nothing waits.
        var client = new Client(ErrorCode.Local_QueueFull);
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);

        Action act = () => _ = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);

        act.Should().Throw<ProduceException<string, byte[]>>("the refusal is thrown by the call itself, not by a task that ends later").Which.Error.Code.Should().Be(ErrorCode.Local_QueueFull);
        produce.Unreported.Should().Be(0, "a refused message leaves nothing waiting");
    }

    [Fact]
    public async Task Produce_Should_WriteTheRidersKeyValueAndHeaders_When_AnEventIsPublished()
    {
        // The wire format MassTransit's rider wrote (ARV-167; captured before the change, and compared on a real broker in
        // ProducerContractTests): the key as UTF-8, the value as the runtime type's JSON, Ariva's headers, then MessageId.
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);
        var bus = new KafkaMessageBus(produce, new EventCatalog([typeof(CatalogProbe).Assembly]));
        IEvent probe = new CatalogProbe { ZoneId = "zone-7", CorrelationId = "corr-7", CausationId = "cause-7" };

        var send = bus.PublishAsync(probe, Ct);
        client.Report(0, ErrorCode.NoError);
        await send;

        var (topic, message, _) = client.Given.Single();
        topic.Should().Be(KafkaTopics.FlowNowcast);
        message.Key.Should().Be("zone-7");
        message.Value.Should().Equal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes((CatalogProbe)probe, EventCatalog.Json),
            "the value is the runtime type's JSON, as JsonValueSerializer<T> wrote it");
        message.Headers.Select(h => (h.Key, System.Text.Encoding.UTF8.GetString(h.GetValueBytes()))).Should().Equal(
            (OutboxHeaders.EventType, nameof(CatalogProbe)),
            (OutboxHeaders.EventVersion, "1"),
            (OutboxHeaders.CorrelationId, "corr-7"),
            (OutboxHeaders.CausationId, "cause-7"),
            (KafkaProduce.MessageIdHeader, probe.Id.ToString("D")));
        message.Timestamp.Type.Should().Be(TimestampType.NotAvailable, "the client stamps the create time, as it did under the rider");
    }

    [Fact]
    public async Task Produce_Should_CarryTheSendSpanAndLeaveTheCallersSpan_When_Traced()
    {
        // Trace context crosses Kafka as the rider carried it (MT-Activity-Id, read by MassTransit's consume side as the parent
        // of its receive span); the send span is a child of the caller's span and never becomes the caller's current span.
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is KafkaProduce.ActivitySourceName or "test-caller",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); }
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test-caller");
        using var caller = source.StartActivity("push")!;

        var send = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        Activity.Current.Should().BeSameAs(caller, "the caller's current span is still its own");
        client.Report(0, ErrorCode.NoError);
        await send;

        Activity span;
        lock (stopped)
            span = stopped.Single(a => a.Source.Name == KafkaProduce.ActivitySourceName && a.TraceId == caller.TraceId); // other tests may trace sends too
        span.ParentSpanId.Should().Be(caller.SpanId);
        span.Kind.Should().Be(ActivityKind.Producer);
        var header = client.Given.Single().Message.Headers.Single(h => h.Key == KafkaProduce.ActivityIdHeader);
        System.Text.Encoding.UTF8.GetString(header.GetValueBytes()).Should().Be(span.Id);
    }

    [Theory]
    [InlineData("aman.feed.desk-session-changed.v1")] // AMAN's topic: Ariva consumes it, never produces it
    [InlineData("ariva.flow.other.v1")]
    [InlineData("ariva.flow.nowcast.v1.dlq.v2")]
    [InlineData("")]
    public void Produce_Should_RefuseATopic_When_ArivaDoesNotProduceIt(string topic)
    {
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);

        Action act = () => _ = produce.SendAsync(topic, "zone-1", [1], Guid.NewGuid(), null, Ct);

        act.Should().Throw<ArgumentException>();
        client.Given.Should().BeEmpty("nothing reached the client");
    }

    [Fact]
    public void Produce_Should_AllowEveryOwnedTopicAndEveryDeadLetterTopic()
    {
        foreach (var topic in KafkaTopics.Owned)
            KafkaProduce.IsProducible(topic).Should().BeTrue(topic);
        foreach (var topic in KafkaTopics.All)
            KafkaProduce.IsProducible(KafkaTopics.DeadLetter(topic)).Should().BeTrue(KafkaTopics.DeadLetter(topic));
    }

    [Fact]
    public async Task Relay_Should_RefuseAnEventRow_When_ItsTopicIsNotItsTypesTopic()
    {
        // ARV-167: no producer registration limits the topics any more, so the relay's transport checks a row's topic against
        // its event type's: a row written by hand, or damaged, fails like an unreadable row (only its key waits).
        var client = new Client();
        using var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);
        var transport = new KafkaOutboxTransport(produce, new EventCatalog([typeof(CatalogProbe).Assembly]));
        var payload = System.Text.Json.JsonSerializer.Serialize(new CatalogProbe { ZoneId = "zone-1" }, EventCatalog.Json);

        foreach (var topic in new[] { KafkaTopics.AlertStateChanged, KafkaTopics.DeadLetter(KafkaTopics.FlowNowcast) })
        {
            var row = new OutboxEnvelope(1, Guid.NewGuid(), topic, "zone-1", nameof(CatalogProbe), payload, new Dictionary<string, string>());
            await transport.Invoking(t => t.SendAsync(row, Ct)).Should().ThrowAsync<InvalidOperationException>($"a {nameof(CatalogProbe)} goes only to its own topic, not {topic}");
        }

        client.Given.Should().BeEmpty("nothing reached the client");
        var right = new OutboxEnvelope(2, Guid.NewGuid(), KafkaTopics.FlowNowcast, "zone-1", nameof(CatalogProbe), payload, new Dictionary<string, string>());
        var send = transport.SendAsync(right, Ct);
        client.Report(0, ErrorCode.NoError);
        await send;
        client.Given.Single().Topic.Should().Be(KafkaTopics.FlowNowcast, "the control: the row with its own topic is sent");
    }

    [Fact]
    public async Task Dispose_Should_FailTheSendsStillWaiting_When_TheHostStops()
    {
        // A disposed client gives no more reports; its waiters fail at once instead of waiting out their bound.
        var client = new Client();
        var produce = new KafkaProduce(client.Mock.Object, TimeProvider.System, null);
        var send = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);

        produce.Dispose();

        (await send.Invoking(s => s.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().ThrowAsync<KafkaException>()).Which.Error.Code.Should().Be(ErrorCode.Local_Destroy);
        client.Mock.Verify(c => c.Dispose(), Times.Once());
        Action after = () => _ = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        after.Should().Throw<ObjectDisposedException>("a disposed producer takes nothing more");
    }

    [Fact]
    public async Task Producer_Should_FailSendsAtOnceUntilRecreatedThenSendOnTheNewClient_When_ItsClientRaisesAFatalError()
    {
        // ARV-167, the lead's decision: a fatal client error is the one thing that replaces the client, in place, at most once
        // per window counted from the client's creation; sends in the gap fail at once, and the relay waits meanwhile.
        var stamps = new StampingClock();
        var clients = new Clients(stamps: stamps);
        using var metrics = new KafkaProducerMetrics();
        var (listener, counts) = Listen(metrics);
        using var listening = listener;
        var window = TimeSpan.FromMilliseconds(600);
        using var produce = new KafkaProduce(clients.Make, stamps, null, window, metrics);
        var transport = new KafkaOutboxTransport(produce, new EventCatalog([typeof(CatalogProbe).Assembly]));
        transport.IsReady.Should().BeFalse("the relay waits until the host has started the producer");
        produce.Started();
        transport.IsReady.Should().BeTrue();

        var first = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        clients[0].Report(0, ErrorCode.NoError);
        await first;
        clients[0].RaiseError(new Error(ErrorCode.Local_AllBrokersDown, "1/1 brokers are down", false));
        transport.IsReady.Should().BeTrue("a transient client error changes nothing; librdkafka recovers by itself");

        clients[0].Fail();
        transport.IsReady.Should().BeFalse("the relay waits in the gap instead of failing rows");
        Action inTheGap = () => _ = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [2], Guid.NewGuid(), null, Ct);
        inTheGap.Should().Throw<KafkaException>("a send in the gap fails at once").Which.Error.IsFatal.Should().BeTrue();
        clients[0].Given.Should().HaveCount(1, "nothing reaches the failed client");

        await UntilAsync(() => clients.Count == 2 && transport.IsReady, "the producer recreated");
        Gap("first to second client", clients[0], clients[1]).Should().BeGreaterThanOrEqualTo(window - Tick, "not before the window from the failed client's creation");
        produce.ClientNumber.Should().Be(2);
        var after = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [3], Guid.NewGuid(), null, Ct);
        clients[1].Report(0, ErrorCode.NoError);
        await after.Invoking(a => a).Should().NotThrowAsync("the new client sends");
        clients[0].Given.Should().HaveCount(1);
        await UntilAsync(() => clients[0].Disposed, "the failed client disposed, off the send path");
        counts.Should().Contain(KafkaProducerMetrics.FatalErrorKind, 1).And.Contain(KafkaProducerMetrics.RecreatedKind, 1);
    }

    [Fact]
    public async Task Producer_Should_WaitForTheWindow_When_ASecondFatalErrorComesSoonAfterARecreation()
    {
        var stamps = new StampingClock();
        var clients = new Clients(stamps: stamps);
        using var metrics = new KafkaProducerMetrics();
        var (listener, counts) = Listen(metrics);
        using var listening = listener;
        var window = TimeSpan.FromMilliseconds(800);
        using var produce = new KafkaProduce(clients.Make, stamps, null, window, metrics);
        produce.Started();

        clients[0].Fail();
        await UntilAsync(() => clients.Count == 2, "the first recreation");
        clients[1].Fail();
        produce.IsReady.Should().BeFalse();
        Action inTheGap = () => _ = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        inTheGap.Should().Throw<KafkaException>();
        await UntilAsync(() => clients.Count == 3 && produce.IsReady, "the second recreation");

        Gap("second to third client", clients[1], clients[2]).Should().BeGreaterThanOrEqualTo(window - Tick,
            "a client that fails soon after it was made is replaced only once its window has passed, so a misconfiguration recreates at most once per window");
        counts.Should().Contain(KafkaProducerMetrics.FatalErrorKind, 2).And.Contain(KafkaProducerMetrics.RecreatedKind, 2);
    }

    [Fact]
    public async Task Producer_Should_EndEveryWaiterOfTheReplacedClient_When_ItIsRecreated()
    {
        // The replaced client's own reports end their own waiters; what it never reports fails with Local_Destroy at its
        // retirement (the senders send it again). Nothing is left waiting for its 45 second bound.
        var clients = new Clients();
        using var produce = new KafkaProduce(clients.Make, TimeProvider.System, null, TimeSpan.FromMilliseconds(300), null);
        produce.Started();
        var reported = produce.SendAsync("ariva.flow.nowcast.v1", "zone-1", [1], Guid.NewGuid(), null, Ct);
        var unreported = produce.SendAsync("ariva.flow.nowcast.v1", "zone-2", [2], Guid.NewGuid(), null, Ct);
        var clock = Stopwatch.StartNew();

        clients[0].Fail();
        clients[0].Report(0, ErrorCode.Local_Fatal);
        (await reported.Invoking(r => r).Should().ThrowAsync<KafkaException>()).Which.Error.Code.Should().Be(ErrorCode.Local_Fatal,
            "its own report ended it");
        (await unreported.Invoking(u => u.WaitAsync(TimeSpan.FromSeconds(15), Ct)).Should().ThrowAsync<KafkaException>()).Which.Error.Code.Should().Be(ErrorCode.Local_Destroy,
            "the retirement failed what the replaced client still held");
        clock.Elapsed.Should().BeLessThan(KafkaProduce.SendTimeout, "never stranded until the bound");
        await UntilAsync(() => clients[0].Disposed, "the replaced client disposed");

        clients[0].Invoking(c => c.Report(1, ErrorCode.NoError)).Should().NotThrow("a late report of a retired client is ignored");
        produce.Unreported.Should().Be(0);
    }

    [Fact]
    public async Task Producer_Should_GiveEachSendToOneClientAndEndIt_When_SendsRunDuringTheSwap()
    {
        // Eight senders run through a fatal error and the recreation. Every send ends (by its report, or failed at once in the
        // gap, refused by the failed client, or failed at its retirement), none is given to two clients, and each client
        // reads as one: a send uses the client it read first.
        // In the gap a send fails synchronously, so a sender that failed waits a millisecond before its next send, as a real
        // sender backs off; without it eight synchronous loops hold the four thread pool threads the recreation timer needs.
        // Each sender is bounded (MaxSends) and the finally block stops them, so a failure cannot leave them running.
        const int MaxSends = 20_000;
        var clients = new Clients(autoReport: true);
        using var produce = new KafkaProduce(clients.Make, TimeProvider.System, null, TimeSpan.FromMilliseconds(200), null);
        produce.Started();
        using var stop = new CancellationTokenSource();
        var sent = new System.Collections.Concurrent.ConcurrentBag<string>();
        var failed = new System.Collections.Concurrent.ConcurrentBag<ErrorCode>();
        var other = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var senders = Enumerable.Range(0, 8).Select(lane => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < MaxSends && !stop.IsCancellationRequested; attempt++)
            {
                var id = Guid.NewGuid();
                var refused = false;
                try
                {
                    await produce.SendAsync("ariva.flow.nowcast.v1", "zone-" + lane, [1], id, null, Ct);
                    sent.Add(id.ToString("D"));
                }
                catch (KafkaException e)
                {
                    failed.Add(e.Error.Code);
                    refused = true;
                }
                catch (Exception e)
                {
                    other.Add(e);
                    refused = true;
                }

                if (refused)
                    await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None)).ToList();

        try
        {
            await UntilAsync(() => sent.Count > 100, "sends on the first client");
            clients[0].Fail();
            await UntilAsync(() => clients.Count == 2, "the recreation");
            var before = sent.Count;
            await UntilAsync(() => sent.Count > before + 100, "sends on the new client");
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        }

        other.Should().BeEmpty("a send ends by its report or a Kafka error, never otherwise");
        failed.Should().NotBeEmpty("sends in the gap failed at once").And.OnlyContain(c => c == ErrorCode.Local_Fatal || c == ErrorCode.Local_Destroy);
        var first = clients[0].MessageIds();
        var second = clients[1].MessageIds();
        first.Intersect(second).Should().BeEmpty("no message is given to two clients");
        first.Concat(second).Should().OnlyHaveUniqueItems();
        sent.Should().OnlyContain(id => first.Contains(id) || second.Contains(id), "every acknowledged send went to one client");
        second.Should().NotBeEmpty();
        produce.Unreported.Should().Be(0, "nothing left waiting");
    }

    [Fact]
    public async Task DeadLetter_Should_StopRetryingAsTheMessagesCancellation_When_TheEndpointStops()
    {
        var sink = new RefusingSink();
        var shutdown = new EndpointStopping();
        using var message = new CancellationTokenSource();

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, message.Token, shutdown.Token);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        shutdown.Signal();

        var ended = await loop.Invoking(l => l.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().ThrowAsync<OperationCanceledException>(
            "the loop ends at once instead of waiting out its backoff");
        ended.Which.CancellationToken.Should().Be(message.Token,
            "MassTransit passes on only a cancellation of the message's own token; anything else is retried, then rescued and acknowledged (ARV-166)");
        var attempts = sink.Attempts;
        await Task.Delay(2500, Ct);
        sink.Attempts.Should().Be(attempts, "nothing is tried after the endpoint began to stop");
    }

    [Fact]
    public async Task DeadLetter_Should_EndTheAttemptInFlightAsTheMessagesCancellation_When_TheEndpointStops()
    {
        var sink = new RefusingSink { Hang = true };
        var shutdown = new EndpointStopping();
        using var message = new CancellationTokenSource();

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, message.Token, shutdown.Token);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        shutdown.Signal();

        (await loop.Invoking(l => l.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should().Be(message.Token);
    }

    [Fact]
    public async Task DeadLetter_Should_EndAsTheMessagesCancellation_When_TheMessagesTokenIsCancelled()
    {
        var sink = new RefusingSink();
        var shutdown = new EndpointStopping();
        using var message = new CancellationTokenSource();

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, message.Token, shutdown.Token);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await message.CancelAsync();

        (await loop.Invoking(l => l.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should().Be(message.Token);
        shutdown.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task DeadLetter_Should_RetryUntilAccepted_When_TheBrokerTakesItLater()
    {
        var sink = new RefusingSink { RefuseTimes = 1 };
        var shutdown = new EndpointStopping();

        await DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, CancellationToken.None, shutdown.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        sink.Attempts.Should().Be(2, "a refused dead letter is tried again (after 2 seconds) and taken");
    }

    [Fact]
    public async Task EndpointStopping_Should_SignalOnStoppingAndRenew_When_TheEndpointIsReadyAgain()
    {
        var stopping = new EndpointStopping();
        var first = stopping.Token;

        await stopping.Ready(null);
        stopping.Token.Should().Be(first, "a ready endpoint that never stopped keeps its token");
        first.IsCancellationRequested.Should().BeFalse();

        await stopping.Stopping(null);
        first.IsCancellationRequested.Should().BeTrue("the endpoint's stop ends every loop waiting on it");
        stopping.Token.IsCancellationRequested.Should().BeTrue();

        await stopping.Ready(null);
        stopping.Token.IsCancellationRequested.Should().BeFalse("an endpoint started again after a stop does not end its loops at once");
        stopping.Signal();
        stopping.Token.IsCancellationRequested.Should().BeTrue();
    }

    private static DeadLetter Letter() => DeadLetters.Create("ariva.flow.nowcast.v1", 0, 7, "zone", [1, 2, 3], [], "ariva-it.probe",
        new InvalidOperationException("probe"), DateTime.UtcNow);

    private sealed class RefusingSink : IDeadLetterSink
    {
        private int _attempts;

        public bool Hang { get; init; }
        public int RefuseTimes { get; init; } = int.MaxValue;
        public int Attempts => Volatile.Read(ref _attempts);
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(DeadLetter letter, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            Attempted.TrySetResult();
            if (Hang)
                await Task.Delay(Timeout.Infinite, ct);
            if (attempt <= RefuseTimes)
                throw new KafkaProduceTimeoutException(DeadLetters.TopicFor(letter), KafkaProduce.SendTimeout);
        }
    }
}
