using System.Collections.Concurrent;
using System.Globalization;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Logging;
using Ariva.Api.Common.Settings;
using Ariva.Core.Security;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Hubs;

/// <summary>
/// ARV-010b for SignalR (ADR-0026): a hub connection lives longer than the access token that opened it, so the session
/// behind it is checked on connect, on every invocation, and by <see cref="HubSessionSweeper"/> every second (with the 4 second session cache, within 5 seconds);
/// a connection whose session is revoked or expired is closed. Hubs (ARV-035) opt in with
/// <c>services.AddSignalR().AddArivaHubSessions()</c>. Since ARV-096b the registry counts every open connection of the replica, and holds
/// them to <see cref="MaxConnections"/> in all (<see cref="HubSettings"/>) beside the most a session may hold.
/// </summary>
public sealed class HubSessionRegistry
{
    /// <summary>Open hub connections one session may hold on one replica (a few dashboard tabs; CWE-400).</summary>
    public const int MaxConnectionsPerSession = 8;

    private readonly ConcurrentDictionary<string, (Guid? SessionId, HubCallerContext Context)> _connections = new(StringComparer.Ordinal);
    private readonly Lock _adding = new();

    /// <summary>A registry holding the replica to <see cref="HubSettings.DefaultMaxConnections"/>.</summary>
    public HubSessionRegistry() : this(HubSettings.DefaultMaxConnections)
    {
    }

    /// <summary>A registry holding the replica to <paramref name="maxConnections"/> open connections in all.</summary>
    public HubSessionRegistry(int maxConnections)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        MaxConnections = maxConnections;
    }

    public int Count => _connections.Count;

    /// <summary>The most open connections the replica holds, every session together (ARV-096b).</summary>
    public int MaxConnections { get; }

    /// <summary>Whether the replica holds <see cref="MaxConnections"/> already, so a negotiate is answered 429.</summary>
    public bool Full => _connections.Count >= MaxConnections;

    /// <summary>Tracks the connection; false (nothing tracked) when the session or the replica already holds the most it may.</summary>
    public bool TryAdd(Guid sessionId, HubCallerContext context) => Admit(sessionId, context) == HubAdmission.Admitted;

    /// <summary>
    /// Tracks the connection, of a session or (<paramref name="sessionId"/> null) of none, unless the replica holds
    /// <see cref="MaxConnections"/> already or the session the most it may; nothing is tracked then.
    /// </summary>
    public HubAdmission Admit(Guid? sessionId, HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_adding)
        {
            if (_connections.Count >= MaxConnections)
                return HubAdmission.ReplicaFull;
            if (sessionId is { } session && _connections.Values.Count(c => c.SessionId == session) >= MaxConnectionsPerSession)
                return HubAdmission.SessionFull;
            _connections[context.ConnectionId] = (sessionId, context);
            return HubAdmission.Admitted;
        }
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyCollection<Guid> Sessions() => _connections.Values.Where(c => c.SessionId is not null).Select(c => c.SessionId.Value).Distinct().ToList();

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

        var admission = registry.Admit(await SessionOf(context.Context, context.ServiceProvider), context.Context);
        if (admission != HubAdmission.Admitted)
        {
            context.Context.Abort();
            throw new HubException(admission == HubAdmission.ReplicaFull ? "hub_full" : "too_many_connections");
        }

        try
        {
            await next(context);
        }
        catch
        {
            // SignalR does not call OnDisconnectedAsync when OnConnectedAsync throws, so the admitted connection gives its place back here
            // (security review of ARV-096b, L3: a connection without a session is never swept, and the total is a hard limit).
            registry.Remove(context.Context.ConnectionId);
            throw;
        }
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

/// <summary>Whether <see cref="HubSessionRegistry.Admit"/> tracked a connection, and if not, which limit it met.</summary>
public enum HubAdmission
{
    Admitted,
    SessionFull,
    ReplicaFull
}

public static class HubSessionExtensions
{
    /// <summary>The rate limiting policy name a refused negotiate is logged under (ARV-096b).</summary>
    public const string ConnectionsPolicy = "hub-connections";

    /// <summary>
    /// Session checks for every hub of the host: on connect, on each invocation and every second; and the replica's total
    /// (<see cref="HubSettings"/>, refused at start when out of bounds).
    /// </summary>
    public static ISignalRServerBuilder AddArivaHubSessions(this ISignalRServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services
            .AddOptions<HubSettings>()
            .BindConfiguration(HubSettings.SectionName)
            .Validate(settings => settings.IsValid,
                $"{HubSettings.SectionName}:MaxConnections is {HubSettings.LeastMaxConnections} to {HubSettings.MostMaxConnections.ToString("N0", CultureInfo.InvariantCulture)}.")
            .ValidateOnStart();
        builder.Services.AddSingleton(services => new HubSessionRegistry(services.GetRequiredService<IOptions<HubSettings>>().Value.MaxConnections));
        builder.Services.AddSingleton<SessionHubFilter>();
        builder.Services.AddHostedService<HubSessionSweeper>();
        builder.Services.Configure<HubOptions>(options => options.AddFilter<SessionHubFilter>());
        return builder;
    }

    /// <summary>
    /// ARV-096b: answers a negotiate for <paramref name="hubPath"/> 429 with Retry-After when the replica holds its most connections
    /// (<see cref="HubSessionRegistry.Full"/>), with the rate limiting security event; the hub's own check at the handshake closes any
    /// connection past the total, negotiated or not. Run it after authorization, so an anonymous caller still gets 401.
    /// </summary>
    public static IApplicationBuilder UseHubConnectionLimit(this IApplicationBuilder app, PathString hubPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        var negotiate = hubPath.Add("/negotiate");
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(negotiate, StringComparison.OrdinalIgnoreCase) &&
                context.RequestServices.GetRequiredService<HubSessionRegistry>().Full)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = RateLimitingExtensions.ConcurrencyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                context.RequestServices.GetService<SecurityEventLog>()?.RateLimited(context, ConnectionsPolicy, _ => SecurityEvents.HostPartition);
                return;
            }

            await next(context);
        });
    }
}
