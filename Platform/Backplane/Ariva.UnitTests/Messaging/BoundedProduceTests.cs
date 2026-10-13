using System.Diagnostics;
using Ariva.Infra.Messaging.Kafka;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-166: every produce Ariva awaits ends within its bound whatever the client does, a caller that gives up never
/// cancels a produce other sends share a producer with, and the dead-letter loops end when their endpoint stops. The broker
/// side of the same story (a produce queued behind one in flight on a dropped connection) is proven against a real
/// broker in Ariva.IntegrationTests (StuckProduceTests, DeadLetterShutdownTests).
/// </summary>
public sealed class BoundedProduceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(300);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Produce_Should_FailAsATimeoutWithinItsBound_When_TheClientNeverReports()
    {
        // A delivery report that never comes and a client that ignores its token: the wait still ends at the bound.
        var never = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();

        var act = () => KafkaProduce.BoundedAsync("ariva.flow.nowcast.v1", Bound, TimeProvider.System, _ => never.Task, Ct);

        var thrown = await act.Should().ThrowAsync<KafkaProduceTimeoutException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        thrown.Which.Should().BeAssignableTo<TimeoutException>("Ingest answers a timeout 503, and the relay ends its pass on it");
        thrown.Which.Message.Should().Contain("ariva.flow.nowcast.v1");
    }

    [Fact]
    public async Task Produce_Should_CancelItsOwnTokenAndFailAsATimeout_When_TheBoundPasses()
    {
        // The produce's token and the wait have a timer each, with the same bound, and nothing orders the two: on a loaded
        // machine the wait's timer often runs first and the token's a moment later, so the timeout can reach the test before
        // the token is cancelled (ARV-172). The test therefore waits for the cancellation itself, within a bound.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var act = () => KafkaProduce.BoundedAsync("ariva.flow.nowcast.v1", Bound, TimeProvider.System, token =>
        {
            _ = token.Register(() => cancelled.TrySetResult());
            return Task.Delay(Timeout.Infinite, token);
        }, Ct);

        (await act.Should().ThrowAsync<KafkaProduceTimeoutException>()).Which.Should().NotBeAssignableTo<OperationCanceledException>(
            "a produce cancelled at its bound is a failure of the send, not the caller's cancellation");
        await cancelled.Should().CompleteWithinAsync(TimeSpan.FromSeconds(10),
            "the produce's own token cancels at the bound, so the client lets go of it");
    }

    [Fact]
    public async Task Produce_Should_StopWaitingWithoutCancellingTheProduce_When_TheCallerGivesUp()
    {
        // MassTransit disposes a topic's producer when a send on it is cancelled, which strands every other send on it, so
        // the caller's token must never reach the produce (ARV-166).
        var report = new TaskCompletionSource();
        CancellationToken given = default;
        using var caller = new CancellationTokenSource();

        var produce = KafkaProduce.BoundedAsync("ariva.device.track-sample.v1", TimeSpan.FromSeconds(30), TimeProvider.System, token =>
        {
            given = token;
            return report.Task;
        }, caller.Token);
        await caller.CancelAsync();

        await produce.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();
        given.IsCancellationRequested.Should().BeFalse("the produce runs on until its report or its own bound");
        report.SetException(new KafkaException(ErrorCode.Local_MsgTimedOut));
        await Task.Yield();
    }

    [Fact]
    public async Task Produce_Should_ObserveItsOutcome_When_TheCallerStoppedWaitingAndItFailsLater()
    {
        // Nobody awaits a produce its caller gave up on, so its later failure must be observed where it ends, or it would
        // surface as an unobserved task exception. A task faulted the same way and never observed is the control.
        var ours = "abandoned-" + Guid.NewGuid().ToString("N");
        var control = "control-" + Guid.NewGuid().ToString("N");
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                seen.Add(inner.Message);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await AbandonAsync(ours);
            FaultUnobserved(control);
            for (var i = 0; i < 50 && !seen.Contains(control); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(20, Ct);
            }

            seen.Should().Contain(control, "the check sees a failure that nobody observed");
            seen.Should().NotContain(ours, "the abandoned produce's failure is observed when it ends");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonAsync(string message)
    {
        var report = new TaskCompletionSource();
        using var caller = new CancellationTokenSource();
        var produce = KafkaProduce.BoundedAsync("ariva.flow.nowcast.v1", TimeSpan.FromSeconds(30), TimeProvider.System, _ => report.Task, caller.Token);
        await caller.CancelAsync();
        await produce.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();
        report.SetException(new InvalidOperationException(message));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FaultUnobserved(string message) => _ = Task.FromException(new InvalidOperationException(message));

    [Fact]
    public async Task Produce_Should_PassTheClientsOwnFailureThrough_When_TheBrokerRefuses()
    {
        var act = () => KafkaProduce.BoundedAsync("ariva.flow.nowcast.v1", TimeSpan.FromSeconds(30), TimeProvider.System,
            _ => Task.FromException(new KafkaException(ErrorCode.Local_MsgTimedOut)), Ct);

        (await act.Should().ThrowAsync<KafkaException>()).Which.Error.Code.Should().Be(ErrorCode.Local_MsgTimedOut);
    }

    [Fact]
    public async Task Produce_Should_Complete_When_TheBrokerAcknowledges()
    {
        // The test acknowledges while the produce is waited for, rather than a timer racing the bound's own timers (ARV-172).
        var report = new TaskCompletionSource();

        var produce = KafkaProduce.BoundedAsync("ariva.flow.nowcast.v1", TimeSpan.FromSeconds(30), TimeProvider.System, _ => report.Task, Ct);
        produce.IsCompleted.Should().BeFalse("the produce is waited for until its report");
        report.SetResult();

        await produce.Invoking(p => p).Should().NotThrowAsync();
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
