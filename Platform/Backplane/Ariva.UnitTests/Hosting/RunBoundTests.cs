using System.Diagnostics;
using Ariva.Infra.Resilience;
using FluentAssertions;

namespace Ariva.UnitTests.Hosting;

/// <summary>
/// ARV-096e (CWE-400, ASVS 5.0 V15.2.2): <see cref="RunBound"/>, the run timeout every TickerQ job and background loop runs under.
/// A run that outlasts its timeout is cancelled and reported as <see cref="RunTimeoutException"/>; a host's stop stays a
/// cancellation; a run that swallows its cancellation, or ends in an error wrapping it, is still reported as cut; a run within its
/// timeout returns its result and its own failures unchanged.
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
