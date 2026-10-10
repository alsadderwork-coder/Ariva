using Ariva.Infra.Services.Validation;
using FluentAssertions;
using Fluentx;

namespace Ariva.UnitTests.Services;

/// <summary>
/// ARV-104g2: one computation of a campaign's results at a time, shared by every caller that asks meanwhile, within a timeout
/// and a host-wide limit (<see cref="SingleFlight{T}"/>). A caller that stops waiting cancels neither the computation nor the
/// others' wait; the timeout is a result with an error for every caller; a later call computes again.
/// </summary>
public sealed class SingleFlightTests
{
    private const string TimedOut = "timed out";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SingleFlight<int> Flights(int maxConcurrent = 2, double timeoutSeconds = 30) =>
        new(maxConcurrent, TimeSpan.FromSeconds(timeoutSeconds), TimeProvider.System, TimedOut);

    [Fact]
    public async Task RunAsync_Should_ComputeOnceForEveryCaller_When_TheyAskForOneKeyMeanwhile()
    {
        var flights = Flights();
        var release = new TaskCompletionSource();
        var runs = 0;
        async Task<Result<int>> Work(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            await release.Task.WaitAsync(ct);
            return new Result<int>(42);
        }

        var key = Guid.CreateVersion7();
        var callers = Enumerable.Range(0, 8).Select(_ => flights.RunAsync(key, Work, Ct)).ToList();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        runs.Should().Be(1);
        results.Should().OnlyContain(r => !r.HasErrors && r.Data == 42);
        flights.Flying.Should().Be(0, "the flight ends with its computation");
        (await flights.RunAsync(key, Work, Ct)).Data.Should().Be(42);
        runs.Should().Be(2, "a later call computes again");
    }

    [Fact]
    public async Task RunAsync_Should_KeepComputingForTheOthers_When_OneCallerStopsWaiting()
    {
        var flights = Flights();
        var release = new TaskCompletionSource();
        var cancelled = false;
        async Task<Result<int>> Work(CancellationToken ct)
        {
            await release.Task;
            cancelled = ct.IsCancellationRequested;
            return new Result<int>(7);
        }

        var key = Guid.CreateVersion7();
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = flights.RunAsync(key, Work, leaving.Token);
        var second = flights.RunAsync(key, Work, Ct);
        await leaving.CancelAsync();
        release.SetResult();

        await first.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        (await second).Data.Should().Be(7);
        cancelled.Should().BeFalse("only the timeout cancels the computation");
    }

    [Fact]
    public async Task RunAsync_Should_ReturnTheTimeoutAsAnError_When_TheComputationTakesTooLong()
    {
        var flights = Flights(timeoutSeconds: 0.2);

        var result = await flights.RunAsync(Guid.CreateVersion7(), async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new Result<int>(1);
        }, Ct);

        result.HasErrors.Should().BeTrue();
        result.ErrorMessages.Should().Equal(TimedOut);
        flights.Flying.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_Should_LetOnlyTheLimitComputeAtOnce_When_ManyKeysAsk()
    {
        // One computation at a time on the host: while the first key computes (its work does not stop at the timeout here), the
        // second waits for a turn, and that wait counts against its timeout.
        var flights = Flights(maxConcurrent: 1, timeoutSeconds: 0.5);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var running = 0;
        var most = 0;
        async Task<Result<int>> Holding(CancellationToken ct)
        {
            InterlockedMax(ref most, Interlocked.Increment(ref running));
            started.SetResult();
            await release.Task;
            Interlocked.Decrement(ref running);
            return new Result<int>(1);
        }

        async Task<Result<int>> Quick(CancellationToken ct)
        {
            InterlockedMax(ref most, Interlocked.Increment(ref running));
            Interlocked.Decrement(ref running);
            return await Task.FromResult(new Result<int>(2));
        }

        var first = flights.RunAsync(Guid.CreateVersion7(), Holding, Ct);
        await started.Task.WaitAsync(Ct);
        var waited = await flights.RunAsync(Guid.CreateVersion7(), Quick, Ct);
        release.SetResult();

        waited.ErrorMessages.Should().ContainSingle().Which.Should().Be(TimedOut, "it never got a turn within its timeout");
        (await first).Data.Should().Be(1);
        most.Should().Be(1);
        (await flights.RunAsync(Guid.CreateVersion7(), Quick, Ct)).Data.Should().Be(2, "the turn is free again");
    }

    private static readonly AsyncLocal<string> CallerState = new();

    [Fact]
    public async Task RunAsync_Should_HideTheCallersAsyncLocalState_When_TheWorkRuns()
    {
        // L1 of the ARV-104g2 review: nothing of the caller's request (its user, its scope, any AsyncLocal, as
        // IHttpContextAccessor keeps the request) flows into the computation, which other callers share.
        var flights = Flights();
        CallerState.Value = "first caller's user";
        string seen = "unset";

        var result = await flights.RunAsync(Guid.CreateVersion7(), _ =>
        {
            seen = CallerState.Value;
            return Task.FromResult(new Result<int>(1));
        }, Ct);

        result.Data.Should().Be(1);
        seen.Should().BeNull("the work runs without the caller's execution context");
        CallerState.Value.Should().Be("first caller's user", "the caller's own context is untouched");
    }

    [Fact]
    public async Task RunAsync_Should_StartAndLeaveNoBrokenFlight_When_TheCallerHasAlreadySuppressedTheFlow()
    {
        // L4 of the ARV-104g2 review: suppressing a flow already suppressed throws, and a Lazy would keep that exception for the
        // key until a restart. The flight checks first, and a flight that cannot start leaves the table.
        var flights = Flights();
        var key = Guid.CreateVersion7();
        Task<Result<int>> started;
        using (ExecutionContext.SuppressFlow())
            started = flights.RunAsync(key, _ => Task.FromResult(new Result<int>(3)), CancellationToken.None);

        (await started).Data.Should().Be(3);
        flights.Flying.Should().Be(0);
        (await flights.RunAsync(key, _ => Task.FromResult(new Result<int>(4)), Ct)).Data.Should().Be(4);
    }

    [Fact]
    public async Task Guarded_Should_AnswerEveryCallerOfTheFlightWithBusy_When_TheRuntimePoolIsExhausted()
    {
        // M1 of the ARV-104g2 review: the runtime login's pool all in use surfaces as an NpgsqlException, wrapped by NHibernate
        // in a GenericADOException; every caller sharing the flight gets the Busy result, none an exception.
        var flights = Flights();
        var release = new TaskCompletionSource();
        var key = Guid.CreateVersion7();
        Task<Result<int>> Work(CancellationToken ct) => SvcValidationResults.Guarded<int>(async () =>
        {
            await release.Task;
            throw new NHibernate.Exceptions.GenericADOException("could not execute query",
                new Npgsql.NpgsqlException("The connection pool has been exhausted, either raise 'Max Pool Size' or 'Timeout' value in the connection string.",
                    new TimeoutException()));
        }, null, ct);

        var callers = Enumerable.Range(0, 3).Select(_ => flights.RunAsync(key, Work, Ct)).ToList();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        results.Should().OnlyContain(r => r.HasErrors && r.ErrorMessages.Single() == Ariva.Core.Services.Validation.ValidationResultsErrors.Busy);
        var direct = await SvcValidationResults.Guarded<int>(() => throw new Npgsql.NpgsqlException("refused"), null, Ct);
        direct.ErrorMessages.Should().Equal(Ariva.Core.Services.Validation.ValidationResultsErrors.Busy);
        var other = () => SvcValidationResults.Guarded<int>(() => throw new InvalidOperationException("a defect"), null, Ct);
        await other.Should().ThrowAsync<InvalidOperationException>("only database connection failures become a result");
    }

    [Theory]
    [InlineData("IMM", "D01", "IMM", "D02", false)]
    [InlineData("IMM", "D01", "IMM", "D01", true)]
    [InlineData("A/B", "C", "A", "B/C", true)]
    [InlineData("IMM", "D/1", "ARR", "D01", true)]
    public void CollidingDeskKeys_Should_RefuseDesksThatShareAKey_When_ACodeHoldsTheSeparator(string checkpointA, string deskA, string checkpointB, string deskB,
        bool collide)
    {
        // L6 of the ARV-104g2 review: DeskKeys.For joins the codes with '/', so a code holding '/' could make two desks one key.
        SvcValidationResults.CollidingDeskKeys("DMO", [(checkpointA, deskA), (checkpointB, deskB)]).Should().Be(collide);
    }

    [Fact]
    public void HalfDays_Should_CutAWindowIntoPiecesOfAtMostTwelveHours_When_DeskMinutesAreRead()
    {
        // L3 of the ARV-104g2 review: 100 desks over a 25-hour day are 150,000 desk minutes, beyond one read; 12 hours are 72,000.
        var from = new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Utc);
        var pieces = SvcValidationResults.HalfDays(new Ariva.Core.Validation.Comparison.UtcWindow(from, from.AddHours(25))).ToList();

        pieces.Select(p => (p.FromUtc, p.ToUtc)).Should().Equal((from, from.AddHours(12)), (from.AddHours(12), from.AddHours(24)), (from.AddHours(24), from.AddHours(25)));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
