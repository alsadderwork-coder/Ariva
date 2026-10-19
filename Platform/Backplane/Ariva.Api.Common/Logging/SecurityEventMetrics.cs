using System.Diagnostics.Metrics;

namespace Ariva.Api.Common.Logging;

/// <summary>The kinds of request-level security event (<see cref="SecurityEvents"/>), as the metric and the summary name them.</summary>
public enum SecurityEventKind
{
    AuthorizationForbidden,
    AuthorizationChallenged,
    RateLimitRejected,
    InvalidModelRefused,
    StrictJsonRefused,
    RepeatedQueryRefused,
    NulCharacterRefused,
    SiteScopeRefused,
    RequestRefused
}

/// <summary>
/// Security event metrics (meter <c>Ariva.Security</c>, ARV-091): <see cref="EventsName"/> counts every request-level security
/// event by kind and outcome, whether the throttle wrote it or left it out, so a flood is measured in full even when its
/// lines are not; <see cref="ProtectionFailuresName"/> counts the log properties the redaction enricher replaced because their
/// protection failed. Tags: <c>kind</c> (a <see cref="SecurityEventKind"/> name) and <c>outcome</c> (<see cref="Written"/>,
/// <see cref="Suppressed"/> or <see cref="Failed"/>); never a caller, an address or a request's value.
/// </summary>
public sealed class SecurityEventMetrics : IDisposable
{
    #region Constants

    public const string MeterName = "Ariva.Security";
    public const string EventsName = "ariva.security.events";

    /// <summary>Log properties whose protection threw and that were written as [REDACTED] instead (the redaction enricher fails closed).</summary>
    public const string ProtectionFailuresName = "ariva.security.log_protection_failures";

    /// <summary>The event was written.</summary>
    public const string Written = "written";

    /// <summary>The throttle left the event out; the periodic summary counts it.</summary>
    public const string Suppressed = "suppressed";

    /// <summary>Building or writing the event failed; the request's answer is unchanged.</summary>
    public const string Failed = "failed";

    #endregion

    #region Fields

    private static readonly string[] Kinds = Enum.GetNames<SecurityEventKind>();

    private readonly Meter _meter;
    private readonly Counter<long> _events;
    private readonly Counter<long> _protectionFailures;

    #endregion

    #region Constructors

    public SecurityEventMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _events = _meter.CreateCounter<long>(EventsName, description: "Request-level security events by kind and outcome (written, suppressed by the throttle, failed)");
        _protectionFailures = _meter.CreateCounter<long>(ProtectionFailuresName, description: "Log properties written as [REDACTED] because their protection failed");
    }

    #endregion

    #region Methods

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    public Meter Meter => _meter;

    /// <summary>Counts one event of <paramref name="kind"/> with its outcome.</summary>
    public void Count(SecurityEventKind kind, string outcome) =>
        _events.Add(1, new KeyValuePair<string, object>("kind", Kinds[(int)kind]), new KeyValuePair<string, object>("outcome", outcome));

    /// <summary>Counts one log property whose protection failed.</summary>
    public void ProtectionFailed() => _protectionFailures.Add(1);

    public void Dispose() => _meter.Dispose();

    #endregion
}
