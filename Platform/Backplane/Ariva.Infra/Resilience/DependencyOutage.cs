using Npgsql;
using StackExchange.Redis;

namespace Ariva.Infra.Resilience;

/// <summary>
/// An exception that means a dependency (PostgreSQL, Redis) is unreachable, stalled or restarting, not that it refused
/// the request (ARV-072). The hosts answer such a request with 503 and Retry-After instead of 500, so a sensor or a
/// display keeps its data and tries again, and an outage is not logged as a code fault. The exception may be wrapped
/// (NHibernate wraps ADO.NET errors), so the chain of inner exceptions is searched.
/// </summary>
public static class DependencyOutage
{
    /// <summary>Seconds a client is asked to wait before it tries again.</summary>
    public const int RetryAfterSeconds = 5;

    private const int MaxDepth = 8;

    public static bool Is(Exception exception)
    {
        for (var (e, depth) = (exception, 0); e is not null && depth < MaxDepth; (e, depth) = (e.InnerException, depth + 1))
        {
            if (Matches(e))
                return true;
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(Is))
                return true;
        }

        return false;
    }

    private static bool Matches(Exception e) => e switch
    {
        // Connection lost or refused (08), the server shutting down or restarting (57P01 to 57P03), too many connections.
        PostgresException p => p.SqlState.StartsWith("08", StringComparison.Ordinal) || p.SqlState is "57P01" or "57P02" or "57P03" or "53300",
        NpgsqlException n => n.IsTransient,
        RedisConnectionException => true,
        // A regular expression's match timeout guards against ReDoS: a refused input, not an outage.
        System.Text.RegularExpressions.RegexMatchTimeoutException => false,
        // Command and connect timeouts of Npgsql and StackExchange.Redis, and a cache factory's hard timeout.
        TimeoutException => true,
        _ => false
    };
}
