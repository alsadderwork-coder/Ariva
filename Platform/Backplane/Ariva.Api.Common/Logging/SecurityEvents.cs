using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// The request-level security events every Backplane host writes (ARV-091, ASVS 5.0 V16.3.2, V16.3.3), numbered after the
/// account events of <c>SvcAuthenticator</c> (9101 to 9113), which keep their numbers; 9120 to 9126 are the account
/// administration and proof of play events. Each is written at Warning by <see cref="SecurityEventLog"/>, so it passes the
/// Microsoft Warning override and reaches every sink. Each names who (the request enricher's <c>CallerUserId</c>, or anonymous,
/// <c>CallerSessionId</c> and <c>CallerAddress</c>), where (the method and the endpoint's route pattern, never a path or a query
/// string), the trace id (Serilog's <c>TraceId</c> and <c>SpanId</c>) and why (a fixed reason), and never the refused input, a
/// member name or a value.
/// </summary>
public static class SecurityEvents
{
    #region Event ids

    public const int AuthorizationForbiddenId = 9114;
    public const string AuthorizationForbiddenName = "SecurityEvent.AuthorizationForbidden";
    public const int AuthorizationChallengedId = 9115;
    public const string AuthorizationChallengedName = "SecurityEvent.AuthorizationChallenged";
    public const int RateLimitRejectedId = 9116;
    public const string RateLimitRejectedName = "SecurityEvent.RateLimitRejected";
    public const int InvalidModelRefusedId = 9117;
    public const string InvalidModelRefusedName = "SecurityEvent.InvalidModelRefused";
    public const int StrictJsonRefusedId = 9118;
    public const string StrictJsonRefusedName = "SecurityEvent.StrictJsonRefused";
    public const int RepeatedQueryRefusedId = 9119;
    public const string RepeatedQueryRefusedName = "SecurityEvent.RepeatedQueryRefused";
    public const int NulCharacterRefusedId = 9127;
    public const string NulCharacterRefusedName = "SecurityEvent.NulCharacterRefused";
    public const int SiteScopeRefusedId = 9128;
    public const string SiteScopeRefusedName = "SecurityEvent.SiteScopeRefused";
    public const int RequestRefusedId = 9129;
    public const string RequestRefusedName = "SecurityEvent.RequestRefused";
    public const int EventsSuppressedId = 9130;
    public const string EventsSuppressedName = "SecurityEvent.EventsSuppressed";

    /// <summary>403 from the authorization middleware: authenticated, but a requirement of the endpoint's policy is not met.</summary>
    public static readonly EventId AuthorizationForbidden = new(AuthorizationForbiddenId, AuthorizationForbiddenName);

    /// <summary>401 from the authorization middleware: no valid credential, or a critical action without a recent second factor.</summary>
    public static readonly EventId AuthorizationChallenged = new(AuthorizationChallengedId, AuthorizationChallengedName);

    /// <summary>429 from the rate limiter: the policy and the kind of partition, never its key.</summary>
    public static readonly EventId RateLimitRejected = new(RateLimitRejectedId, RateLimitRejectedName);

    /// <summary>400 for an invalid request model (validation attributes, a value that does not bind, a missing body).</summary>
    public static readonly EventId InvalidModelRefused = new(InvalidModelRefusedId, InvalidModelRefusedName);

    /// <summary>400 for a body the strict JSON reader refused (an unknown or repeated member, a number as text, JSON that does not parse).</summary>
    public static readonly EventId StrictJsonRefused = new(StrictJsonRefusedId, StrictJsonRefusedName);

    /// <summary>400 for a query parameter sent more than once.</summary>
    public static readonly EventId RepeatedQueryRefused = new(RepeatedQueryRefusedId, RepeatedQueryRefusedName);

    /// <summary>400 for a NUL character in the query, a route value or a form field.</summary>
    public static readonly EventId NulCharacterRefused = new(NulCharacterRefusedId, NulCharacterRefusedName);

    /// <summary>404 for a <c>siteCode</c> outside the caller's sites, or one that cannot be a site code (<c>[SiteScoped]</c>).</summary>
    public static readonly EventId SiteScopeRefused = new(SiteScopeRefusedId, SiteScopeRefusedName);

    /// <summary>
    /// 401, 403 or 429 answered outside the authorization middleware and the rate limiter, when no other security event was
    /// recorded for the request: an ended or revoked session, a pending account, a service's own refusal, an integration
    /// client's own limit, an upload limit. Written when the request ends, with the status and a fixed reason per status.
    /// </summary>
    public static readonly EventId RequestRefused = new(RequestRefusedId, RequestRefusedName);

    /// <summary>The events of one kind the throttle left out in the last period (a summary line, written every 10 s while there are any, and at shutdown).</summary>
    public static readonly EventId EventsSuppressed = new(EventsSuppressedId, EventsSuppressedName);

    /// <summary>The request-level events: they carry the endpoint's route pattern, so the redaction enricher drops the request path from them.</summary>
    public static readonly IReadOnlySet<int> RequestEventIds = new HashSet<int>
    {
        AuthorizationForbiddenId, AuthorizationChallengedId, RateLimitRejectedId, InvalidModelRefusedId, StrictJsonRefusedId,
        RepeatedQueryRefusedId, NulCharacterRefusedId, SiteScopeRefusedId, RequestRefusedId
    };

    #endregion

    #region Reasons

    /// <summary>No credential, or one that did not authenticate.</summary>
    public const string NotAuthenticated = "NotAuthenticated";

    /// <summary>A critical action without a second factor within its age (RFC 9470 step-up).</summary>
    public const string SecondFactorRequired = "SecondFactorRequired";

    /// <summary>The caller holds none of the endpoint's permissions.</summary>
    public const string PermissionMissing = "PermissionMissing";

    /// <summary>The integration client lacks the endpoint's scope or the route's site.</summary>
    public const string ScopeMissing = "ScopeMissing";

    /// <summary>A device called for a queue zone that is not its own.</summary>
    public const string DeviceZoneMismatch = "DeviceZoneMismatch";

    /// <summary>Any other requirement of the policy.</summary>
    public const string PolicyNotMet = "PolicyNotMet";

    /// <summary>The request model is not valid.</summary>
    public const string InvalidModel = "InvalidModel";

    /// <summary>The body is not strict JSON for the request model.</summary>
    public const string NotStrictJson = "NotStrictJson";

    /// <summary>A query parameter appears more than once.</summary>
    public const string RepeatedQuery = "RepeatedQueryParameter";

    /// <summary>The query, a route value or a form field holds a NUL character.</summary>
    public const string NulCharacter = "NulCharacter";

    /// <summary>The site is not one of the caller's.</summary>
    public const string OutsideSiteScope = "OutsideSiteScope";

    /// <summary>The value cannot be a site code.</summary>
    public const string NotASiteCode = "NotASiteCode";

    /// <summary>A 401 answered outside the authorization middleware (an ended or revoked session, an integration call check).</summary>
    public const string CredentialRefused = "CredentialOrSessionRefused";

    /// <summary>A 403 answered outside the authorization middleware (a pending account, a service's refusal).</summary>
    public const string AccessRefused = "AccessRefused";

    /// <summary>A 429 answered outside the rate limiter (an integration client's own limit, the upload limit).</summary>
    public const string LimitReached = "LimitReached";

    #endregion

    #region Rate limit partitions

    /// <summary>Partitioned by the client address (the global limiter, sign-in, the integration token exchange).</summary>
    public const string AddressPartition = "address";

    /// <summary>Partitioned by the presented device credential's hash.</summary>
    public const string DeviceCredentialPartition = "device credential";

    /// <summary>Partitioned by the presented display credential's hash.</summary>
    public const string DisplayCredentialPartition = "display credential";

    /// <summary>One partition for the whole host process (the concurrency policies).</summary>
    public const string HostPartition = "host";

    /// <summary>The policy name of the global limiter, which has none of its own.</summary>
    public const string GlobalPolicy = "global";

    #endregion

    #region Methods

    /// <summary>The fixed reason of a <see cref="RequestRefused"/> event for its status.</summary>
    public static string RefusedReasonOf(int status) => status switch
    {
        401 => CredentialRefused,
        403 => AccessRefused,
        _ => LimitReached
    };

    /// <summary>The event id of a kind, as its summary line names it.</summary>
    public static int EventIdOf(SecurityEventKind kind) => kind switch
    {
        SecurityEventKind.AuthorizationForbidden => AuthorizationForbiddenId,
        SecurityEventKind.AuthorizationChallenged => AuthorizationChallengedId,
        SecurityEventKind.RateLimitRejected => RateLimitRejectedId,
        SecurityEventKind.InvalidModelRefused => InvalidModelRefusedId,
        SecurityEventKind.StrictJsonRefused => StrictJsonRefusedId,
        SecurityEventKind.RepeatedQueryRefused => RepeatedQueryRefusedId,
        SecurityEventKind.NulCharacterRefused => NulCharacterRefusedId,
        SecurityEventKind.SiteScopeRefused => SiteScopeRefusedId,
        _ => RequestRefusedId
    };

    /// <summary>True for an event whose EventId is one of <see cref="RequestEventIds"/> (Serilog keeps it as a structure with Id and Name).</summary>
    /// <param name="logEvent">The event.</param>
    /// <returns>Whether it is a request-level security event.</returns>
    public static bool IsRequestEvent(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (!logEvent.Properties.TryGetValue("EventId", out var value) || value is not StructureValue structure)
            return false;

        foreach (var property in structure.Properties)
        {
            if (property.Name == "Id")
                return property.Value is ScalarValue { Value: int id } && RequestEventIds.Contains(id);
        }

        return false;
    }

    #endregion
}
