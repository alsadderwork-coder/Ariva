using System.Diagnostics;
using Ariva.Infra.Resilience;
using FluentAssertions;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-096e (CWE-400, ASVS 5.0 V15.2.2): <see cref="RunBound"/>, the run timeout every TickerQ job and background loop runs under.
/// A run that outlasts its timeout is cancelled and reported as <see cref="RunTimeoutException"/>; a host's stop stays a
/// cancellation; a run that swallows its cancellation, or ends in an error wrapping it, is still reported as cut, and so is one that
/// fails after its timeout passed while the timer was late; a run within its timeout returns its result and its own failures
/// unchanged.
/// </summary>
public sealed class RunBoundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task RunAsync_Should_CancelTheRunAndSaySo_When_ItOutlastsItsTimeout()
    {
        var token = CancellationToken.None;
        var watch = Stopwatch.StartNew();

        var run = () => RunBound.RunAsync("Probe run", Short, TimeProvider.System, async ct =>
        {
            token = ct;
            await Task.Delay(Timeout.Infinite, ct);
        }, Ct);

        var cut = (await run.Should().ThrowAsync<RunTimeoutException>()).Which;
        // A timer may fire a fraction of a millisecond before the stopwatch reads its due time (199.5 ms seen in a gate run).
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(Short - TimeSpan.FromMilliseconds(20)).And.BeLessThan(TimeSpan.FromSeconds(10));
        token.IsCancellationRequested.Should().BeTrue("the run's own token was cancelled, so its reads and calls stop");
        cut.Run.Should().Be("Probe run");
        cut.Timeout.Should().Be(Short);
        cut.InnerException.Should().BeAssignableTo<OperationCanceledException>();
        cut.Message.Should().Match("Probe run ran past its run timeout of * s and was cancelled; the next run starts afresh.");
        cut.Should().BeAssignableTo<TimeoutException>();
    }

    [Fact]
    public async Task RunAsync_Should_LeaveTheStopACancellation_When_TheHostStops()
    {
        using var stopping = new CancellationTokenSource(Short);

        var run = () => RunBound.RunAsync("Probe run", TimeSpan.FromMinutes(1), TimeProvider.System, ct => Task.Delay(Timeout.Infinite, ct), stopping.Token);

        var stopped = (await run.Should().ThrowAsync<OperationCanceledException>()).Which;
        stopped.Should().NotBeOfType<RunTimeoutException>();
    }

    [Fact]
    public async Task RunAsync_Should_StillReportTheCut_When_TheRunSwallowsItsCancellation()
    {
        var run = () => RunBound.RunAsync("Probe run", Short, TimeProvider.System, async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // A loop that records the cut and returns, as the pollers do.
            }

            return 7;
        }, Ct);

        (await run.Should().ThrowAsync<RunTimeoutException>()).Which.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_Should_ReportTheCut_When_TheRunEndsInAnErrorWrappingIt()
    {
        var run = () => RunBound.RunAsync("Probe run", Short, TimeProvider.System, async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException e)
            {
                throw new InvalidOperationException("a driver's own error", e);
            }
        }, Ct);

        (await run.Should().ThrowAsync<RunTimeoutException>()).Which.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task RunAsync_Should_ReportTheCut_When_TheRunFailsAfterItsTimeoutBeforeItsTimerFires()
    {
        // A busy thread pool ran the timeout's timer late while a connection open that does not take the token failed at its own
        // timeout (the AmanPoller stall test in a story gate): the run still went past its timeout, so it is the cut.
        var clock = new LateTimers();

        var run = () => RunBound.RunAsync("Probe run", Short, clock, ct =>
        {
            clock.Pass(TimeSpan.FromSeconds(2));
            ct.IsCancellationRequested.Should().BeFalse("the timer has not fired");
            return Task.FromException(new TimeoutException("the driver's connect timeout"));
        }, Ct);

        var cut = (await run.Should().ThrowAsync<RunTimeoutException>()).Which;
        cut.InnerException.Should().BeOfType<TimeoutException>();
        cut.Message.Should().Be("Probe run ran past its run timeout of 0 s and ended in an error before its cancellation; the next run starts afresh.");
    }

    [Fact]
    public async Task RunAsync_Should_LeaveTheStopAsItEnded_When_TheHostStopsAfterTheTimeoutPassedWithTheTimerLate()
    {
        // The stop is checked before the time: a host stopping a run that is already past its timeout passes on what the run
        // ended in, a cancellation or an error, never a run timeout.
        foreach (var ending in new Func<CancellationToken, Exception>[] { ct => new OperationCanceledException(ct), _ => new InvalidOperationException("its own") })
        {
            var clock = new LateTimers();
            using var stopping = new CancellationTokenSource();

            var run = () => RunBound.RunAsync("Probe run", Short, clock, ct =>
            {
                clock.Pass(TimeSpan.FromSeconds(2));
                stopping.Cancel();
                return Task.FromException(ending(ct));
            }, stopping.Token);

            var thrown = (await run.Should().ThrowAsync<Exception>()).Which;
            thrown.Should().NotBeOfType<RunTimeoutException>();
            new[] { typeof(OperationCanceledException), typeof(TaskCanceledException), typeof(InvalidOperationException) }.Should().Contain(thrown.GetType());
        }
    }

    [Fact]
    public async Task RunAsync_Should_PassAFailureOn_When_ItComesBeforeTheTimeoutWithTheTimerLate()
    {
        var clock = new LateTimers();

        var run = () => RunBound.RunAsync("Probe run", TimeSpan.FromSeconds(5), clock, _ =>
        {
            clock.Pass(TimeSpan.FromSeconds(1));
            return Task.FromException(new InvalidOperationException("its own"));
        }, Ct);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("its own");
    }

    /// <summary>A clock whose timers never fire, as a starved thread pool's are late, and whose time passes only when told.</summary>
    private sealed class LateTimers : TimeProvider
    {
        private long _ticks;

        public void Pass(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period) => new Never();

        private sealed class Never : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_Should_ReturnTheResultAndPassFailuresOn_When_TheRunEndsInTime()
    {
        (await RunBound.RunAsync("Probe run", TimeSpan.FromMinutes(1), TimeProvider.System, _ => Task.FromResult(42), Ct)).Should().Be(42);

        var failing = () => RunBound.RunAsync("Probe run", TimeSpan.FromMinutes(1), TimeProvider.System, _ => Task.FromException(new InvalidOperationException("its own")), Ct);
        await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("its own");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7201)]
    public async Task RunAsync_Should_RefuseATimeout_When_ItIsNotPositiveOrPastTwoHours(int seconds)
    {
        var run = () => RunBound.RunAsync("Probe run", TimeSpan.FromSeconds(seconds), TimeProvider.System, _ => Task.CompletedTask, Ct);

        await run.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
