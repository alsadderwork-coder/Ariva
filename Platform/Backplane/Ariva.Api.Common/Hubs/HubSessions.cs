using System.Collections.Concurrent;
using Ariva.Core.Security;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ariva.Api.Common.Hubs;

/// <summary>
/// ARV-010b for SignalR (ADR-0026): a hub connection lives longer than the access token that opened it, so the session
/// behind it is checked on connect, on every invocation, and by <see cref="HubSessionSweeper"/> every second (with the 4 second session cache, within 5 seconds);
/// a connection whose session is revoked or expired is closed. Hubs (ARV-035) opt in with
/// <c>services.AddSignalR().AddArivaHubSessions()</c>.
/// </summary>
public sealed class HubSessionRegistry
{
    /// <summary>Open hub connections one session may hold on one replica (a few dashboard tabs; CWE-400).</summary>
    public const int MaxConnectionsPerSession = 8;

    private readonly ConcurrentDictionary<string, (Guid SessionId, HubCallerContext Context)> _connections = new(StringComparer.Ordinal);
    private readonly Lock _adding = new();

    public int Count => _connections.Count;

    /// <summary>Tracks the connection; false (nothing tracked) when the session already holds the most it may.</summary>
    public bool TryAdd(Guid sessionId, HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_adding)
        {
            if (_connections.Values.Count(c => c.SessionId == sessionId) >= MaxConnectionsPerSession)
                return false;
            _connections[context.ConnectionId] = (sessionId, context);
            return true;
        }
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyCollection<Guid> Sessions() => _connections.Values.Select(c => c.SessionId).Distinct().ToList();

    /// <summary>Closes every connection of the session; returns how many.</summary>
    public int Abort(Guid sessionId)
    {
        var closed = 0;
        foreach (var (connectionId, entry) in _connections)
        {
            if (entry.SessionId != sessionId)
                continue;
            entry.Context.Abort();
            _connections.TryRemove(connectionId, out _);
            closed++;
        }

        return closed;
    }
}

/// <summary>Refuses a connection or an invocation whose session is not active and tracks connections for the sweeper.</summary>
public sealed class SessionHubFilter(HubSessionRegistry registry) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (await SessionOf(context.Context, context.ServiceProvider) is { } sessionId && !registry.TryAdd(sessionId, context.Context))
        {
            context.Context.Abort();
            throw new HubException("too_many_connections");
        }

        await next(context);
    }

    public async ValueTask<object> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);

        await SessionOf(invocationContext.Context, invocationContext.ServiceProvider);
        return await next(invocationContext);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception exception, Func<HubLifetimeContext, Exception, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        registry.Remove(context.Context.ConnectionId);
        return next(context, exception);
    }

    /// <summary>The caller's active session id; null for principals without a subject (no session); throws when it is not active.</summary>
    private static async Task<Guid?> SessionOf(HubCallerContext caller, IServiceProvider services)
    {
        var user = caller.User;
        if (user?.FindFirst(ArivaClaims.Subject) is null)
            return null;

        if (Guid.TryParse(user.FindFirst(ArivaClaims.SessionId)?.Value, out var sessionId) &&
            await services.GetRequiredService<ISessionValidator>().CheckAsync(sessionId, caller.ConnectionAborted) == SessionState.Active)
        {
            return sessionId;
        }

        caller.Abort();
        throw new HubException("session_expired");
    }
}

/// <summary>Closes hub connections whose session was revoked or expired, checking each open session every second.</summary>
public sealed class HubSessionSweeper(HubSessionRegistry registry, IServiceScopeFactory scopes, ILogger<HubSessionSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>One pass over the open sessions; returns how many connections it closed.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var closed = 0;
        foreach (var sessionId in registry.Sessions())
        {
            await using var scope = scopes.CreateAsyncScope();
            var state = await scope.ServiceProvider.GetRequiredService<ISessionValidator>().CheckAsync(sessionId, ct);
            if (state != SessionState.Active)
                closed += registry.Abort(sessionId);
        }

        return closed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var closed = await SweepAsync(stoppingToken);
                if (closed > 0)
                    logger.LogInformation("Closed {Count} hub connection(s) of ended sessions", closed);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Hub session sweep failed; retrying");
            }
        }
    }
}

public static class HubSessionExtensions
{
    /// <summary>Session checks for every hub of the host: on connect, on each invocation and every second.</summary>
    public static ISignalRServerBuilder AddArivaHubSessions(this ISignalRServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<HubSessionRegistry>();
        builder.Services.AddSingleton<SessionHubFilter>();
        builder.Services.AddHostedService<HubSessionSweeper>();
        builder.Services.Configure<HubOptions>(options => options.AddFilter<SessionHubFilter>());
        return builder;
    }
}
