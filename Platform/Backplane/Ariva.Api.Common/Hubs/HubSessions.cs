using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
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
/// them to <see cref="MaxConnections"/> in all (<see cref="HubSettings"/>) beside the most a session may hold; since ARV-229a also to
/// <see cref="MaxConnectionsPerAccount"/> of one account (the token's subject, every sign-in together). The counts of each session and
/// each account change only under one lock, with the connection they count, so an admission, a close and a negotiate's check take
/// constant time whatever the replica holds; closing an ended session's connections (<see cref="Abort"/>) walks them all, as before.
/// </summary>
public sealed class HubSessionRegistry
{
    /// <summary>Open hub connections one session may hold on one replica (a few dashboard tabs; CWE-400).</summary>
    public const int MaxConnectionsPerSession = 8;

    private readonly ConcurrentDictionary<string, Tracked> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _perSession = [];
    private readonly Dictionary<string, int> _perAccount = new(StringComparer.Ordinal);
    private readonly Lock _counting = new();

    /// <summary>A registry holding the replica to <see cref="HubSettings.DefaultMaxConnections"/>.</summary>
    public HubSessionRegistry() : this(HubSettings.DefaultMaxConnections)
    {
    }

    /// <summary>A registry holding the replica to <paramref name="maxConnections"/> open connections in all, and an account to <see cref="HubSettings.DefaultMaxConnectionsPerAccount"/>.</summary>
    public HubSessionRegistry(int maxConnections) : this(maxConnections, HubSettings.DefaultMaxConnectionsPerAccount)
    {
    }

    /// <summary>A registry holding the replica to <paramref name="maxConnections"/> open connections in all, and one account to <paramref name="maxConnectionsPerAccount"/>.</summary>
    public HubSessionRegistry(int maxConnections, int maxConnectionsPerAccount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnectionsPerAccount, 1);
        MaxConnections = maxConnections;
        MaxConnectionsPerAccount = maxConnectionsPerAccount;
    }

    public int Count => _connections.Count;

    /// <summary>The most open connections the replica holds, every session together (ARV-096b).</summary>
    public int MaxConnections { get; }

    /// <summary>The most open connections the replica holds of one account, every session of the subject together (ARV-229a).</summary>
    public int MaxConnectionsPerAccount { get; }

    /// <summary>Whether the replica holds <see cref="MaxConnections"/> already (a negotiate is refused through <see cref="AdmissionOf"/>, which also reads the account).</summary>
    public bool Full => _connections.Count >= MaxConnections;

    /// <summary>The account a connection counts against: the token's subject; null for a principal without one (it counts in the total only).</summary>
    public static string AccountOf(ClaimsPrincipal user) =>
        user?.FindFirst(ArivaClaims.Subject)?.Value is { } subject && !string.IsNullOrWhiteSpace(subject) ? subject : null;

    /// <summary>Tracks the connection; false (nothing tracked) when the session, its account or the replica already holds the most it may.</summary>
    public bool TryAdd(Guid sessionId, HubCallerContext context) => Admit(sessionId, context) == HubAdmission.Admitted;

    /// <summary>
    /// Tracks the connection, of a session or (<paramref name="sessionId"/> null) of none, unless the replica holds
    /// <see cref="MaxConnections"/> already, the session the most it may, or the connection's account (<see cref="AccountOf"/>)
    /// <see cref="MaxConnectionsPerAccount"/>; nothing is tracked then. The narrower limit is named first: the session's, then the
    /// account's. A connection tracked already keeps its place and is not counted twice.
    /// </summary>
    public HubAdmission Admit(Guid? sessionId, HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var account = AccountOf(context.User);
        lock (_counting)
        {
            if (_connections.ContainsKey(context.ConnectionId))
                return HubAdmission.Admitted;
            if (_connections.Count >= MaxConnections)
                return HubAdmission.ReplicaFull;
            if (sessionId is { } session && _perSession.GetValueOrDefault(session) >= MaxConnectionsPerSession)
                return HubAdmission.SessionFull;
            if (account is not null && _perAccount.GetValueOrDefault(account) >= MaxConnectionsPerAccount)
                return HubAdmission.AccountFull;

            _connections[context.ConnectionId] = new Tracked(sessionId, account, context);
            if (sessionId is { } counted)
                _perSession[counted] = _perSession.GetValueOrDefault(counted) + 1;
            if (account is not null)
                _perAccount[account] = _perAccount.GetValueOrDefault(account) + 1;
            return HubAdmission.Admitted;
        }
    }

    /// <summary>
    /// What a negotiate of <paramref name="user"/> meets now (ARV-229a): <see cref="HubAdmission.ReplicaFull"/> when the replica holds
    /// its total, <see cref="HubAdmission.AccountFull"/> when the user's account holds its most, else <see cref="HubAdmission.Admitted"/>.
    /// A session's own 8 is left to the handshake, where the connection's session is known and checked.
    /// </summary>
    public HubAdmission AdmissionOf(ClaimsPrincipal user)
    {
        var account = AccountOf(user);
        lock (_counting)
        {
            if (_connections.Count >= MaxConnections)
                return HubAdmission.ReplicaFull;
            return account is not null && _perAccount.GetValueOrDefault(account) >= MaxConnectionsPerAccount
                ? HubAdmission.AccountFull
                : HubAdmission.Admitted;
        }
    }

    /// <summary>Open connections of one account on this replica.</summary>
    public int CountOf(string account)
    {
        lock (_counting)
            return account is null ? 0 : _perAccount.GetValueOrDefault(account);
    }

    public void Remove(string connectionId)
    {
        lock (_counting)
            Untrack(connectionId);
    }

    public IReadOnlyCollection<Guid> Sessions()
    {
        lock (_counting)
            return [.. _perSession.Keys];
    }

    /// <summary>Closes every connection of the session; returns how many.</summary>
    public int Abort(Guid sessionId)
    {
        List<HubCallerContext> closing = [];
        lock (_counting)
        {
            foreach (var (connectionId, entry) in _connections)
            {
                if (entry.SessionId == sessionId && Untrack(connectionId))
                    closing.Add(entry.Context);
            }
        }

        // Outside the lock: an abort may run the connection's own callbacks, which give its place back (a second Remove finds nothing).
        foreach (var context in closing)
            context.Abort();
        return closing.Count;
    }

    /// <summary>Forgets the connection and takes it off its session's and its account's counts; false when it was not tracked. Under <see cref="_counting"/>.</summary>
    private bool Untrack(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var entry))
            return false;
        if (entry.SessionId is { } session)
            Decrement(_perSession, session);
        if (entry.Account is not null)
            Decrement(_perAccount, entry.Account);
        return true;
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key)
    {
        var left = counts.GetValueOrDefault(key) - 1;
        if (left > 0)
            counts[key] = left;
        else
            counts.Remove(key);
    }

    private readonly record struct Tracked(Guid? SessionId, string Account, HubCallerContext Context);
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
            throw new HubException(ReasonOf(admission));
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

    /// <summary>The reason a refused connection is closed with: <c>hub_full</c> (the replica), <c>account_full</c> (the account, ARV-229a) or <c>too_many_connections</c> (the session).</summary>
    public static string ReasonOf(HubAdmission admission) => admission switch
    {
        HubAdmission.ReplicaFull => "hub_full",
        HubAdmission.AccountFull => "account_full",
        HubAdmission.SessionFull => "too_many_connections",
        _ => throw new ArgumentOutOfRangeException(nameof(admission), admission, "An admitted connection is not refused.")
    };

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
    ReplicaFull,
    AccountFull
}

public static class HubSessionExtensions
{
    /// <summary>The rate limiting policy name a refused negotiate is logged under (ARV-096b).</summary>
    public const string ConnectionsPolicy = "hub-connections";

    /// <summary>
    /// Session checks for every hub of the host: on connect, on each invocation and every second; and the replica's total and each
    /// account's most (<see cref="HubSettings"/>, refused at start when out of bounds).
    /// </summary>
    public static ISignalRServerBuilder AddArivaHubSessions(this ISignalRServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services
            .AddOptions<HubSettings>()
            .BindConfiguration(HubSettings.SectionName)
            .Validate(settings => settings.IsValid,
                $"{HubSettings.SectionName}:MaxConnections is {HubSettings.LeastMaxConnections} to {HubSettings.MostMaxConnections.ToString("N0", CultureInfo.InvariantCulture)}, " +
                $"and {HubSettings.SectionName}:MaxConnectionsPerAccount {HubSettings.LeastMaxConnectionsPerAccount} to {HubSettings.MostMaxConnectionsPerAccount.ToString("N0", CultureInfo.InvariantCulture)}.")
            .ValidateOnStart();
        builder.Services.AddSingleton(services =>
        {
            var settings = services.GetRequiredService<IOptions<HubSettings>>().Value;
            return new HubSessionRegistry(settings.MaxConnections, settings.MaxConnectionsPerAccount);
        });
        builder.Services.AddSingleton<SessionHubFilter>();
        builder.Services.AddHostedService<HubSessionSweeper>();
        builder.Services.Configure<HubOptions>(options => options.AddFilter<SessionHubFilter>());
        return builder;
    }

    /// <summary>
    /// ARV-096b: answers a negotiate for <paramref name="hubPath"/> 429 with Retry-After when the replica holds its most connections
    /// (<see cref="HubSessionRegistry.Full"/>), or (ARV-229a) the caller's account holds its own (<see cref="HubSessionRegistry.AdmissionOf"/>),
    /// with the rate limiting security event (partition <c>host</c> for the replica, <c>user</c> for the account); the hub's own check at
    /// the handshake closes any connection past either, negotiated or not. Run it after authorization, so an anonymous caller still gets 401.
    /// </summary>
    public static IApplicationBuilder UseHubConnectionLimit(this IApplicationBuilder app, PathString hubPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        var negotiate = hubPath.Add("/negotiate");
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(negotiate, StringComparison.OrdinalIgnoreCase) &&
                context.RequestServices.GetRequiredService<HubSessionRegistry>().AdmissionOf(context.User) is var admission and not HubAdmission.Admitted)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = RateLimitingExtensions.ConcurrencyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                var partition = admission == HubAdmission.AccountFull ? SecurityEvents.UserPartition : SecurityEvents.HostPartition;
                context.RequestServices.GetService<SecurityEventLog>()?.RateLimited(context, ConnectionsPolicy, _ => partition);
                return;
            }

            await next(context);
        });
    }
}
