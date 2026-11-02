using Ariva.Api.Common.Logging;
using Ariva.Infra.Resilience;

namespace Ariva.Api.Cronz.Jobs;

/// <summary>
/// ARV-285 (CWE-532, CWE-200): one run of a TickerQ job as <see cref="RunBound"/> runs it, with what escapes the run protected as
/// log text is. TickerQ keeps a failed run's exception (its message, and its details with the stack) and its dashboard shows
/// it: text outside the log pipeline, where <see cref="ProtectedExceptionSink"/> does not reach. An exception whose text,
/// message or stack, or an inner exception's, holds a credential or an email address (<see cref="RedactionEnricher.ProtectText"/>)
/// is thrown on as a <see cref="ProtectedException"/> chain, every part protected, its message still starting with the original
/// type's name; any other is thrown on as it is, so a cancellation stays a cancellation and a <see cref="RunTimeoutException"/>
/// keeps its type. It fails closed: an exception whose text cannot be read or protected is replaced by one that says so
/// (<see cref="ProtectedExceptionSink.Unprotectable"/>). Every TickerQ function runs its work through it, never through
/// <see cref="RunBound"/> directly (<c>JobRunTests</c> reads each function's code); the rest of a function only logs and
/// counts. Not covered: what TickerQ itself fails with before or around a function (resolving the job's class, its own
/// scheduling), which holds no text of the work's (docs/security/log-inventory.md, section 2.1).
/// </summary>
public static class JobRun
{
    /// <summary>Runs <paramref name="work"/> as <see cref="RunBound.RunAsync{T}"/> does, and throws on what escapes it protected.</summary>
    public static async Task<T> RunAsync<T>(string run, TimeSpan timeout, TimeProvider timeProvider, Func<CancellationToken, Task<T>> work, CancellationToken stopping)
    {
        try
        {
            return await RunBound.RunAsync(run, timeout, timeProvider, work, stopping);
        }
        catch (Exception e)
        {
            var replacement = Protected(e);
            if (replacement is null)
                throw;
            throw replacement;
        }
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

    /// <summary>
    /// The protected chain to throw in place of <paramref name="error"/> (<see cref="ProtectedExceptionSink.Protect"/>), or null
    /// when no part of its text holds anything to protect; one that says the text was left out when it cannot be protected.
    /// A chain deeper than the sink follows (<see cref="ProtectedExceptionSink.MaxDepth"/>) is replaced whole, its text protected
    /// and its inner exceptions left out: TickerQ walks a chain to its root without a limit, so what the sink does not look
    /// at could otherwise be kept as it is.
    /// </summary>
    public static Exception Protected(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            if (DeeperThanTheSinkFollows(error))
                return new ProtectedException(error.GetType().FullName + ": " + RedactionEnricher.ProtectText(error.Message), RedactionEnricher.ProtectText(error.ToString()));
            return ProtectedExceptionSink.Protect(error);
        }
#pragma warning disable CA1031 // fail closed: the exception's text is left out, never kept as it is
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ProtectedException(ProtectedExceptionSink.Unprotectable, ProtectedExceptionSink.Unprotectable);
        }
    }

    /// <summary>Whether a chain of single inner exceptions, or of an aggregate's, goes past <see cref="ProtectedExceptionSink.MaxDepth"/>.</summary>
    private static bool DeeperThanTheSinkFollows(Exception error)
    {
        var depth = 0;
        for (var current = error; current is not null; current = current is AggregateException aggregate ? aggregate.InnerExceptions.FirstOrDefault() : current.InnerException)
        {
            if (++depth > ProtectedExceptionSink.MaxDepth + 1)
                return true;
        }

        return false;
    }
}
