using System.Collections.Concurrent;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// One computation per key at a time, shared by every caller that asks meanwhile, within a timeout and a host-wide limit of
/// computations at once (ARV-104g2: the validation results of a campaign, about 12 s and up to a few GB at the engine's bounds).
/// The computation runs apart from its callers: a caller that stops waiting (its request ends) cancels neither the computation
/// nor the other callers' wait; only the timeout cancels it, its wait for a turn included, and then every caller gets
/// <paramref name="timedOut"/> as an error. The flight ends when its computation does, so a later call computes again.
/// Holds the flights as tasks only, never their results once they end.
/// </summary>
internal sealed class SingleFlight<T>(int maxConcurrent, TimeSpan timeout, TimeProvider timeProvider, string timedOut)
{
    #region Fields

    private readonly ConcurrentDictionary<Guid, Lazy<Task<Result<T>>>> _flights = new();
    private readonly SemaphoreSlim _turns = new(maxConcurrent, maxConcurrent);

    #endregion

    #region Properties

    /// <summary>The keys computing or waiting for a turn now (for tests).</summary>
    internal int Flying => _flights.Count;

    #endregion

    #region Methods

    /// <summary>
    /// The result of <paramref name="work"/> for <paramref name="key"/>: the flight already under way for it, or a new one.
    /// <paramref name="ct"/> ends only this caller's wait.
    /// </summary>
    public Task<Result<T>> RunAsync(Guid key, Func<CancellationToken, Task<Result<T>>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        Lazy<Task<Result<T>>> mine = null;
        mine = new Lazy<Task<Result<T>>>(() => Start(key, work, mine), LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = _flights.GetOrAdd(key, mine);
        try
        {
            return flight.Value.WaitAsync(ct);
        }
        catch
        {
            // A flight that could not start would stay in the Lazy, its exception cached, and fail the key until a restart (L4 of
            // the ARV-104g2 review): it leaves, so the next call starts afresh.
            _flights.TryRemove(new KeyValuePair<Guid, Lazy<Task<Result<T>>>>(key, flight));
            throw;
        }
    }

    private Task<Result<T>> Start(Guid key, Func<CancellationToken, Task<Result<T>>> work, Lazy<Task<Result<T>>> flight)
    {
        // Apart from the caller: no request state (its user, its scope, any AsyncLocal) flows into the computation. When the
        // caller has already suppressed the flow, suppressing it again would throw; nothing flows either way.
        if (ExecutionContext.IsFlowSuppressed())
            return Task.Run(() => FlyAsync(key, work, flight));
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => FlyAsync(key, work, flight));
    }

    private async Task<Result<T>> FlyAsync(Guid key, Func<CancellationToken, Task<Result<T>>> work, Lazy<Task<Result<T>>> flight)
    {
        using var limit = new CancellationTokenSource(timeout, timeProvider);
        var turn = false;
        try
        {
            await _turns.WaitAsync(limit.Token);
            turn = true;
            return await work(limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return Result.Error<T>(timedOut);
        }
        finally
        {
            if (turn)
                _turns.Release();
            _flights.TryRemove(new KeyValuePair<Guid, Lazy<Task<Result<T>>>>(key, flight));
        }
    }

    #endregion
}
