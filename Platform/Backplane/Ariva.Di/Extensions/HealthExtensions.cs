using Ariva.Infra.Caching;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ariva.Di.Extensions;

/// <summary>
/// Readiness of a host's own dependencies (ARV-066), tagged "ready" so /health/readiness runs them (HealthEndpoints):
/// PostgreSQL always (every host reads or writes it), as Unhealthy, so Kubernetes and the Aspire AppHost stop routing to
/// a host that cannot reach its store; Redis when it is enabled, as Degraded only, because the cache serves from memory
/// with fail-safe while Redis is away (ARV-008) and taking hosts out of rotation for it would do more harm than good.
/// Liveness never runs them, so an outage does not restart pods. Each check has a short timeout and reports no detail.
/// </summary>
public static class HealthExtensions
{
    public const string ReadyTag = "ready";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddArivaDependencyChecks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var checks = services.AddHealthChecks();
        checks.AddCheck<DatabaseReadiness>("database", HealthStatus.Unhealthy, [ReadyTag], Timeout);
        if (RedisSettings.From(configuration).Enabled)
            checks.AddCheck<RedisReadiness>("redis", HealthStatus.Degraded, [ReadyTag], Timeout);
        return services;
    }
}

/// <summary>The runtime login reaches the database and runs a statement.</summary>
internal sealed class DatabaseReadiness(DatabaseSettings settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(settings.BuildConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // No message: it could name the host or the login.
            return new HealthCheckResult(context.Registration.FailureStatus);
        }
    }
}

/// <summary>Redis answers a ping on the host's shared connection.</summary>
internal sealed class RedisReadiness(RedisConnection redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await redis.GetAsync().WaitAsync(cancellationToken);
            await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is StackExchange.Redis.RedisException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus);
        }
    }
}
