using System.Net;
using System.Security.Claims;
using Ariva.Api.Common.Security;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// Who and from where, on every log event written while a request runs (ARV-091, ASVS 5.0 V16.2.1).
/// <see cref="RequestLogContextMiddleware"/> creates one per request right after the forwarded headers middleware, so
/// <see cref="CallerAddressProperty"/> is the client the trusted proxy names, and pushes it onto Serilog's log context
/// (<c>Enrich.FromLogContext</c>). The caller is <see cref="Anonymous"/> until the authorization middleware has authenticated
/// the endpoint's schemes and checked the session (<see cref="SecurityEventResultHandler"/> calls <see cref="Identify"/>);
/// from then on the events carry:
/// <list type="bullet">
/// <item><see cref="CallerUserIdProperty"/>: a user's subject claim, a GUID (never a name or an email); <see cref="Anonymous"/>
/// without a credential; <see cref="Unknown"/> for an authenticated principal that names no id Ariva knows.</item>
/// <item><see cref="CallerSessionIdProperty"/>: the token's sid, the id of the server-side session row. It is logged as it
/// is: it authenticates nothing (every request needs the signed token that carries it) and finds no token (refresh tokens
/// are kept as hashes, by family), while it ties a line to the session's sign-in record.</item>
/// <item><see cref="CallerClientIdProperty"/>, <see cref="CallerDeviceIdProperty"/> or <see cref="CallerDisplayIdProperty"/>
/// for an integration client (its public client id), a sensor device or a display player, in place of a user.</item>
/// </list>
/// The <c>Caller</c> names are reserved for this enricher and always win over a template's own value, so a line that names a
/// target account as <c>{UserId}</c> or <c>{SessionId}</c> (an administrator resetting a password, for example) keeps the
/// target in those and the administrator, the administrator's session and address in the <c>Caller</c> ones: the two never
/// mix. The trace id is Serilog's own: every event written under an activity carries <c>TraceId</c> and <c>SpanId</c>, which
/// the JSON formatter writes beside the properties. The values are copied when they are set, so a line written after the
/// request has ended (work the request started) still names the request's caller, never another request's.
/// </summary>
public sealed class RequestLogContext : ILogEventEnricher
{
    #region Constants

    public const string CallerAddressProperty = "CallerAddress";
    public const string CallerUserIdProperty = "CallerUserId";
    public const string CallerSessionIdProperty = "CallerSessionId";
    public const string CallerClientIdProperty = "CallerClientId";
    public const string CallerDeviceIdProperty = "CallerDeviceId";
    public const string CallerDisplayIdProperty = "CallerDisplayId";

    /// <summary>The properties this enricher owns; no message template may use these names.</summary>
    public static readonly IReadOnlySet<string> CallerProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        CallerAddressProperty, CallerUserIdProperty, CallerSessionIdProperty, CallerClientIdProperty, CallerDeviceIdProperty, CallerDisplayIdProperty
    };

    /// <summary>The caller before authorization, and a caller without a credential.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>An authenticated principal whose subject is neither a user id nor an integration client id.</summary>
    public const string Unknown = "unknown";

    /// <summary>The address of a connection that has none (an in-memory test server).</summary>
    public const string UnknownAddress = "unknown";

    /// <summary>The public id of an integration client starts so (ARV-042).</summary>
    private const string IntegrationClientPrefix = "ic_";

    #endregion

    #region Fields

    private static readonly Caller AnonymousCaller = new([new(CallerUserIdProperty, new ScalarValue(Anonymous))], null);
    private static readonly Caller UnknownCaller = new([new(CallerUserIdProperty, new ScalarValue(Unknown))], null);

    private readonly LogEventProperty _callerAddress;
    private readonly string _addressSource;
    private volatile Caller _caller = AnonymousCaller;
    private volatile bool _identified;
    private volatile bool _recorded;

    #endregion

    #region Constructors

    /// <summary>The context of a request from <paramref name="address"/>.</summary>
    /// <param name="address">The connection's remote address after the forwarded headers middleware.</param>
    public RequestLogContext(IPAddress address)
    {
        var client = address is null ? null : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        ClientAddress = client?.ToString() ?? UnknownAddress;
        _callerAddress = new LogEventProperty(CallerAddressProperty, new ScalarValue(ClientAddress));
        _addressSource = "address:" + SourceOf(client);
    }

    #endregion

    #region Properties

    /// <summary>The client address as logged.</summary>
    public string ClientAddress { get; }

    /// <summary>Whether <see cref="Identify"/> has run for this request.</summary>
    public bool Identified => _identified;

    /// <summary>Whether a security event was recorded for this request (written, or left out by the throttle).</summary>
    public bool Recorded => _recorded;

    /// <summary>
    /// Whose share of the security event throttle this request spends: the identified caller (a user, client, device or
    /// display id), otherwise the client address: an IPv4 address whole, an IPv6 address by its /64, since one host can hold a
    /// whole /64 and would otherwise have as many shares as it likes. The logged address stays whole.
    /// </summary>
    public string SourceKey => _caller.Key ?? _addressSource;

    #endregion

    #region Methods

    /// <summary>The request's context, or null outside a request or in a pipeline without the middleware.</summary>
    public static RequestLogContext Of(HttpContext context) => context?.Features.Get<RequestLogContext>();

    /// <summary>Takes the caller from the principal the authorization middleware decided on; later events name it.</summary>
    /// <param name="user">The request's principal after authentication, the session check and the policy's schemes.</param>
    /// <param name="integrationClientId">The checked integration client of the request, when the principal does not name one.</param>
    public void Identify(ClaimsPrincipal user, string integrationClientId = null)
    {
        _caller = CallerOf(user, integrationClientId);
        _identified = true;
    }

    /// <summary>Notes that a security event was recorded for this request, so the request's end writes no generic one.</summary>
    public void MarkRecorded() => _recorded = true;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        // AddOrUpdate: the Caller names are this enricher's own, so the request's caller is never mixed with a template's.
        logEvent.AddOrUpdateProperty(_callerAddress);
        foreach (var property in _caller.Properties)
            logEvent.AddOrUpdateProperty(property);
    }

    /// <summary>The properties that name a principal: ids only, never a name, an email or a credential.</summary>
    private static Caller CallerOf(ClaimsPrincipal user, string integrationClientId)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return IsClientId(integrationClientId) ? new([Property(CallerClientIdProperty, integrationClientId)], "client:" + integrationClientId) : AnonymousCaller;

        if (Guid.TryParse(user.FindFirst(DeviceAuthentication.DeviceIdClaim)?.Value, out var device))
            return new([Property(CallerDeviceIdProperty, device.ToString())], "device:" + device);
        if (Guid.TryParse(user.FindFirst(DisplayAuthentication.DisplayIdClaim)?.Value, out var display))
            return new([Property(CallerDisplayIdProperty, display.ToString())], "display:" + display);

        var subject = user.FindFirst(ArivaClaims.Subject)?.Value;
        var session = Guid.TryParse(user.FindFirst(ArivaClaims.SessionId)?.Value, out var sid) ? Property(CallerSessionIdProperty, sid.ToString()) : null;
        if (Guid.TryParse(subject, out var userId))
            return new(session is null ? [Property(CallerUserIdProperty, userId.ToString())] : [Property(CallerUserIdProperty, userId.ToString()), session], "user:" + userId);
        if (IsClientId(subject))
            return new(session is null ? [Property(CallerClientIdProperty, subject)] : [Property(CallerClientIdProperty, subject), session], "client:" + subject);
        return UnknownCaller;
    }

    /// <summary>The address's throttle source: IPv4 whole, IPv6 as its /64 network, unknown without one.</summary>
    private static string SourceOf(IPAddress address)
    {
        if (address is null)
            return UnknownAddress;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }

    private static bool IsClientId(string value) => value is { Length: <= 64 } && value.StartsWith(IntegrationClientPrefix, StringComparison.Ordinal);

    private static LogEventProperty Property(string name, string value) => new(name, new ScalarValue(value));

    #endregion

    #region Types

    /// <summary>The caller's properties, and the key of its throttle share (null: the address's).</summary>
    private sealed record Caller(LogEventProperty[] Properties, string Key);

    #endregion
}
