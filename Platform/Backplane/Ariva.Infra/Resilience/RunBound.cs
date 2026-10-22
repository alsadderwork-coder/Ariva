namespace Ariva.Infra.Resilience;

/// <summary>
/// ARV-096e (CWE-400, ASVS 5.0 V15.2.2): one run of a TickerQ job or of a background loop ends at its run timeout. The run's
/// work gets a token that the timeout cancels as well as the host's stop, and passes it to every read and call, so a stalled
/// database or partner ends the run instead of holding it, its connection and the runs queued behind it. A run cut by its
/// timeout throws <see cref="RunTimeoutException"/>, which the loop logs as a failed run before its next tick, as any other
/// failure; a stop is a cancellation, as before. Work that catches its own cancellation and returns still counts as cut.
/// Work that never looks at its token cannot be cut (each run's reads and calls take it).
/// </summary>
public static class RunBound
{
    /// <summary>The longest run timeout any job or loop may declare (the inventory test holds every one to it).</summary>
    public static readonly TimeSpan MaxRunTimeout = TimeSpan.FromHours(2);

    /// <summary>Runs <paramref name="work"/> until it ends, <paramref name="stopping"/> is cancelled or <paramref name="timeout"/> passes.</summary>
    public static async Task<T> RunAsync<T>(string run, TimeSpan timeout, TimeProvider timeProvider, Func<CancellationToken, Task<T>> work, CancellationToken stopping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxRunTimeout);

        using var limit = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, limit.Token);
        T result;
        try
        {
            result = await work(linked.Token);
        }
        // Whatever the cut ended in (a cancellation, an error wrapping one) is the cut.
        catch (Exception e) when (limit.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            throw new RunTimeoutException(run, timeout, e);
        }

        if (limit.IsCancellationRequested && !stopping.IsCancellationRequested)
            throw new RunTimeoutException(run, timeout);
        return result;
    }

    /// <summary>The same for work without a result.</summary>
    public static Task RunAsync(string run, TimeSpan timeout, TimeProvider timeProvider, Func<CancellationToken, Task> work, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(run, timeout, timeProvider, async ct =>
        {
            await work(ct);
            return true;
        }, stopping);
    }
}

/// <summary>A job's or a background loop's run that went past its run timeout and was cancelled (ARV-096e).</summary>
public sealed class RunTimeoutException : TimeoutException
{
    public RunTimeoutException()
    {
    }

    public RunTimeoutException(string message) : base(message)
    {
    }

    public RunTimeoutException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public RunTimeoutException(string run, TimeSpan timeout, Exception innerException = null)
        : base(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{run} ran past its run timeout of {timeout.TotalSeconds:0} s and was cancelled; the next run starts afresh."), innerException)
    {
        Run = run;
        Timeout = timeout;
    }

    /// <summary>The job or loop whose run was cut.</summary>
    public string Run { get; }

    /// <summary>Its run timeout.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// A background loop whose every run ends at its run timeout (ARV-096e, <see cref="RunBound"/>). Every loop a host runs is one,
/// or is named with its reason in the inventory test (<c>RunTimeoutInventoryTests</c>).
/// </summary>
public interface IBoundedRuns
{
    /// <summary>How long one run may take before it is cancelled.</summary>
    TimeSpan RunTimeout { get; }
}
