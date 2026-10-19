using Ariva.Api.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// Writes the request-level security events (<see cref="SecurityEvents"/>, ARV-091) at Warning; ArivaLogging pins this
/// category to Warning, so no configuration silences them. Who, the client address and the trace id come from the request
/// enricher (<see cref="RequestLogContext"/>) and Serilog; each event adds where (the method, or OTHER for a method HTTP does
/// not define, and the endpoint's route pattern or display name) and a fixed reason. Nothing the caller sent is written:
/// no path, query, header, body, member name or value.
/// <para>
/// Throttle (CWE-400, CWE-779; CWE-223): per kind and process at most <see cref="MaxPerSecond"/> events a second, and per
/// source (the caller, else the client address) at most <see cref="MaxPerSourcePerSecond"/>, so one source's flood cannot
/// hide another source's event (<see cref="SecurityEventThrottle"/>). Every event, written or left out, is counted in the
/// <c>ariva.security.events</c> metric (<see cref="SecurityEventMetrics"/>). Events left out are written as one summary line
/// per kind (<see cref="SecurityEvents.EventsSuppressed"/>) every <see cref="SummaryPeriod"/>, so a flood that stops still
/// reports its count; at shutdown (the host disposes this service) the pending counts are written once more. A process that
/// is killed loses at most one period's pending counts, which the metric has already counted.
/// </para>
/// <para>
/// A security event never changes an answer: a failure while building or writing one is contained here and counted as
/// <see cref="SecurityEventMetrics.Failed"/>, because every caller (the authorization decorator after the answer is decided,
/// the rate limiter, the model state factory, the filters) runs on the request's answer path.
/// </para>
/// </summary>
public sealed partial class SecurityEventLog : IDisposable
{
    #region Constants

    /// <summary>Events of one kind written per second and process at most.</summary>
    public const int MaxPerSecond = 60;

    /// <summary>Events of one kind one source writes per second at most.</summary>
    public const int MaxPerSourcePerSecond = 3;

    /// <summary>Sources counted apart per kind and second; the rest share one overflow share.</summary>
    public const int MaxSourcesPerSecond = 1024;

    /// <summary>The endpoint of a request that matched none.</summary>
    public const string NoEndpoint = "(none)";

    /// <summary>A method that is not one of HTTP's own.</summary>
    public const string OtherMethod = "OTHER";

    /// <summary>How often the counts the throttle left out are written.</summary>
    public static readonly TimeSpan SummaryPeriod = TimeSpan.FromSeconds(10);

    #endregion

    #region Fields

    private static readonly string[] KnownMethods =
    [
        HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options,
        HttpMethods.Trace, HttpMethods.Connect
    ];

    private static readonly SecurityEventKind[] Kinds = Enum.GetValues<SecurityEventKind>();

    private readonly ILogger _logger;
    private readonly SecurityEventMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly SecurityEventThrottle[] _throttles;
    private readonly ITimer _summaries;
    private readonly Lock _summaryGate = new();
    private readonly long _origin;
    private long _lastSummary;
    private bool _disposed;

    #endregion

    #region Constructors

    /// <summary>The security event log.</summary>
    /// <param name="logger">The logger (category Ariva.Api.Common.Logging.SecurityEventLog).</param>
    /// <param name="metrics">The security event metrics; none in tests that do not read them.</param>
    /// <param name="timeProvider">The clock of the throttle and the summaries.</param>
    public SecurityEventLog(ILogger<SecurityEventLog> logger, SecurityEventMetrics metrics = null, TimeProvider timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _metrics = metrics;
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _throttles = [.. Kinds.Select(_ => new SecurityEventThrottle(MaxPerSecond, MaxPerSourcePerSecond, MaxSourcesPerSecond, Milliseconds))];
        _lastSummary = _time.GetTimestamp();
        // Created without the creating request's execution context: a summary line must not carry that request's caller.
        if (ExecutionContext.IsFlowSuppressed())
            _summaries = _time.CreateTimer(_ => WriteSummaries(), null, SummaryPeriod, SummaryPeriod);
        else
        {
            using (ExecutionContext.SuppressFlow())
                _summaries = _time.CreateTimer(_ => WriteSummaries(), null, SummaryPeriod, SummaryPeriod);
        }
    }

    #endregion

    #region Events

    /// <summary>403 from the authorization middleware; the reason and requirements are worked out only for an event that is written.</summary>
    /// <param name="context">The request.</param>
    /// <param name="failure">The authorization failure.</param>
    public void Forbidden(HttpContext context, AuthorizationFailure failure)
    {
        if (Admit(SecurityEventKind.AuthorizationForbidden, context))
            Write(SecurityEventKind.AuthorizationForbidden, () => LogForbidden(_logger, MethodOf(context), EndpointOf(context),
                SecurityEventResultHandler.ReasonOf(failure), SecurityEventResultHandler.RequirementsOf(failure)));
    }

    /// <summary>401 from the authorization middleware.</summary>
    /// <param name="context">The request.</param>
    /// <param name="reason"><see cref="SecurityEvents.NotAuthenticated"/> or <see cref="SecurityEvents.SecondFactorRequired"/>.</param>
    public void Challenged(HttpContext context, string reason)
    {
        if (Admit(SecurityEventKind.AuthorizationChallenged, context))
            Write(SecurityEventKind.AuthorizationChallenged, () => LogChallenged(_logger, MethodOf(context), EndpointOf(context), reason));
    }

    /// <summary>429 from the rate limiter.</summary>
    /// <param name="context">The request.</param>
    /// <param name="policy">The policy's name, or <see cref="SecurityEvents.GlobalPolicy"/>.</param>
    /// <param name="partitionKind">The kind of partition (never its key), worked out only for an event that is written.</param>
    public void RateLimited(HttpContext context, string policy, Func<HttpContext, string> partitionKind)
    {
        if (Admit(SecurityEventKind.RateLimitRejected, context))
            Write(SecurityEventKind.RateLimitRejected, () => LogRateLimited(_logger, MethodOf(context), EndpointOf(context), policy, partitionKind(context)));
    }

    /// <summary>400 for an invalid request model.</summary>
    /// <param name="context">The request.</param>
    public void InvalidModel(HttpContext context)
    {
        if (Admit(SecurityEventKind.InvalidModelRefused, context))
            Write(SecurityEventKind.InvalidModelRefused, () => LogInvalidModel(_logger, MethodOf(context), EndpointOf(context), SecurityEvents.InvalidModel));
    }

    /// <summary>400 for a body that is not strict JSON for the request model.</summary>
    /// <param name="context">The request.</param>
    public void StrictJson(HttpContext context)
    {
        if (Admit(SecurityEventKind.StrictJsonRefused, context))
            Write(SecurityEventKind.StrictJsonRefused, () => LogStrictJson(_logger, MethodOf(context), EndpointOf(context), SecurityEvents.NotStrictJson));
    }

    /// <summary>400 for a repeated query parameter.</summary>
    /// <param name="context">The request.</param>
    public void RepeatedQuery(HttpContext context)
    {
        if (Admit(SecurityEventKind.RepeatedQueryRefused, context))
            Write(SecurityEventKind.RepeatedQueryRefused, () => LogRepeatedQuery(_logger, MethodOf(context), EndpointOf(context), SecurityEvents.RepeatedQuery));
    }

    /// <summary>400 for a NUL character in the query, a route value, a form field or a JSON string.</summary>
    /// <param name="context">The request.</param>
    public void NulCharacter(HttpContext context)
    {
        if (Admit(SecurityEventKind.NulCharacterRefused, context))
            Write(SecurityEventKind.NulCharacterRefused, () => LogNulCharacter(_logger, MethodOf(context), EndpointOf(context), SecurityEvents.NulCharacter));
    }

    /// <summary>404 for a site outside the caller's sites or a value that cannot be a site code.</summary>
    /// <param name="context">The request.</param>
    /// <param name="reason"><see cref="SecurityEvents.OutsideSiteScope"/> or <see cref="SecurityEvents.NotASiteCode"/>.</param>
    public void SiteScope(HttpContext context, string reason)
    {
        if (Admit(SecurityEventKind.SiteScopeRefused, context))
            Write(SecurityEventKind.SiteScopeRefused, () => LogSiteScope(_logger, MethodOf(context), EndpointOf(context), reason));
    }

    /// <summary>
    /// A 401, 403 or 429 that no other security event recorded: answered by a middleware or an endpoint outside the
    /// authorization middleware and the rate limiter (an ended session, a pending account, a service's refusal, an
    /// integration client's own limit). Written by <see cref="Middlewares.RequestLogContextMiddleware"/> when the request ends.
    /// </summary>
    /// <param name="context">The request, its answer decided.</param>
    public void Refused(HttpContext context)
    {
        if (Admit(SecurityEventKind.RequestRefused, context))
            Write(SecurityEventKind.RequestRefused, () =>
            {
                var status = context.Response.StatusCode;
                LogRefused(_logger, status, MethodOf(context), EndpointOf(context), SecurityEvents.RefusedReasonOf(status));
            });
    }

    #endregion

    #region Summaries

    /// <summary>
    /// Writes one <see cref="SecurityEvents.EventsSuppressed"/> line per kind whose events the throttle left out since the last
    /// summary. The timer calls it every <see cref="SummaryPeriod"/>, and <see cref="Dispose"/> once more.
    /// </summary>
    public void WriteSummaries()
    {
        lock (_summaryGate)
        {
            var now = _time.GetTimestamp();
            var seconds = (long)Math.Ceiling(_time.GetElapsedTime(_lastSummary, now).TotalSeconds);
            _lastSummary = now;
            foreach (var kind in Kinds)
            {
                var count = _throttles[(int)kind].TakeSuppressed();
                if (count == 0)
                    continue;
                try
                {
                    LogSuppressed(_logger, count, kind.ToString(), SecurityEvents.EventIdOf(kind), seconds);
                }
#pragma warning disable CA1031 // a summary that cannot be written is counted, never thrown into a timer or the host's shutdown
                catch (Exception)
#pragma warning restore CA1031
                {
                    _metrics?.Count(kind, SecurityEventMetrics.Failed);
                }
            }
        }
    }

    /// <summary>Stops the summaries and writes the counts still pending.</summary>
    public void Dispose()
    {
        lock (_summaryGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _summaries.Dispose();
        WriteSummaries();
    }

    #endregion

    #region Helpers

    /// <summary>The request's method if HTTP defines it, otherwise <see cref="OtherMethod"/> (a method is any token a client chooses).</summary>
    public static string MethodOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var method = context.Request.Method;
        foreach (var known in KnownMethods)
        {
            if (string.Equals(method, known, StringComparison.Ordinal))
                return known;
        }

        return OtherMethod;
    }

    /// <summary>The endpoint's route pattern (code, never the path the caller sent), else its display name, else <see cref="NoEndpoint"/>.</summary>
    public static string EndpointOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetEndpoint() switch
        {
            RouteEndpoint { RoutePattern.RawText: { Length: > 0 } pattern } => pattern,
            { DisplayName: { Length: > 0 } name } => name,
            _ => NoEndpoint
        };
    }

    /// <summary>
    /// Milliseconds from <paramref name="since"/> to <paramref name="now"/> on <paramref name="time"/>'s timestamps, through
    /// <see cref="TimeProvider.GetElapsedTime(long, long)"/>: never a timestamp times 1,000, which overflows a long after about
    /// 106 days of a nanosecond Stopwatch.
    /// </summary>
    public static long ElapsedMilliseconds(TimeProvider time, long since, long now)
    {
        ArgumentNullException.ThrowIfNull(time);
        return (long)time.GetElapsedTime(since, now).TotalMilliseconds;
    }

    private long Milliseconds() => ElapsedMilliseconds(_time, _origin, _time.GetTimestamp());

    /// <summary>Marks the request as recorded and takes a place in the kind's throttle; an event left out is counted.</summary>
    private bool Admit(SecurityEventKind kind, HttpContext context)
    {
        try
        {
            var log = RequestLogContext.Of(context);
            log?.MarkRecorded();
            if (_throttles[(int)kind].TryTake(log?.SourceKey))
                return true;
            _metrics?.Count(kind, SecurityEventMetrics.Suppressed);
            return false;
        }
#pragma warning disable CA1031 // a security event never changes an answer (see the class summary)
        catch (Exception)
#pragma warning restore CA1031
        {
            _metrics?.Count(kind, SecurityEventMetrics.Failed);
            return false;
        }
    }

    /// <summary>Builds and writes an admitted event; a failure is counted, never thrown at the request.</summary>
    private void Write(SecurityEventKind kind, Action write)
    {
        try
        {
            write();
            _metrics?.Count(kind, SecurityEventMetrics.Written);
        }
#pragma warning disable CA1031 // a security event never changes an answer (see the class summary)
        catch (Exception)
#pragma warning restore CA1031
        {
            _metrics?.Count(kind, SecurityEventMetrics.Failed);
        }
    }

    [LoggerMessage(EventId = SecurityEvents.AuthorizationForbiddenId, EventName = SecurityEvents.AuthorizationForbiddenName, Level = LogLevel.Warning,
        Message = "Request forbidden (403) at {Method} {Endpoint}: {Reason} ({Requirement})")]
    private static partial void LogForbidden(ILogger logger, string method, string endpoint, string reason, string requirement);

    [LoggerMessage(EventId = SecurityEvents.AuthorizationChallengedId, EventName = SecurityEvents.AuthorizationChallengedName, Level = LogLevel.Warning,
        Message = "Request challenged (401) at {Method} {Endpoint}: {Reason}")]
    private static partial void LogChallenged(ILogger logger, string method, string endpoint, string reason);

    [LoggerMessage(EventId = SecurityEvents.RateLimitRejectedId, EventName = SecurityEvents.RateLimitRejectedName, Level = LogLevel.Warning,
        Message = "Request rate limited (429) at {Method} {Endpoint}: policy {Policy} per {Partition}")]
    private static partial void LogRateLimited(ILogger logger, string method, string endpoint, string policy, string partition);

    [LoggerMessage(EventId = SecurityEvents.InvalidModelRefusedId, EventName = SecurityEvents.InvalidModelRefusedName, Level = LogLevel.Warning,
        Message = "Request input refused (400) at {Method} {Endpoint}: {Reason}")]
    private static partial void LogInvalidModel(ILogger logger, string method, string endpoint, string reason);

    [LoggerMessage(EventId = SecurityEvents.StrictJsonRefusedId, EventName = SecurityEvents.StrictJsonRefusedName, Level = LogLevel.Warning,
        Message = "Request input refused (400) at {Method} {Endpoint}: {Reason}")]
    private static partial void LogStrictJson(ILogger logger, string method, string endpoint, string reason);

    [LoggerMessage(EventId = SecurityEvents.RepeatedQueryRefusedId, EventName = SecurityEvents.RepeatedQueryRefusedName, Level = LogLevel.Warning,
        Message = "Request input refused (400) at {Method} {Endpoint}: {Reason}")]
    private static partial void LogRepeatedQuery(ILogger logger, string method, string endpoint, string reason);

    [LoggerMessage(EventId = SecurityEvents.NulCharacterRefusedId, EventName = SecurityEvents.NulCharacterRefusedName, Level = LogLevel.Warning,
        Message = "Request input refused (400) at {Method} {Endpoint}: {Reason}")]
    private static partial void LogNulCharacter(ILogger logger, string method, string endpoint, string reason);

    [LoggerMessage(EventId = SecurityEvents.SiteScopeRefusedId, EventName = SecurityEvents.SiteScopeRefusedName, Level = LogLevel.Warning,
        Message = "Request refused as not found (404) at {Method} {Endpoint}: {Reason}")]
    private static partial void LogSiteScope(ILogger logger, string method, string endpoint, string reason);

    [LoggerMessage(EventId = SecurityEvents.RequestRefusedId, EventName = SecurityEvents.RequestRefusedName, Level = LogLevel.Warning,
        Message = "Request refused ({Status}) at {Method} {Endpoint}: {Reason}")]
    private static partial void LogRefused(ILogger logger, int status, string method, string endpoint, string reason);

    [LoggerMessage(EventId = SecurityEvents.EventsSuppressedId, EventName = SecurityEvents.EventsSuppressedName, Level = LogLevel.Warning,
        Message = "{Count} {Kind} security events ({KindEventId}) were left out by the throttle in the last {Seconds} s")]
    private static partial void LogSuppressed(ILogger logger, long count, string kind, int kindEventId, long seconds);

    #endregion
}
