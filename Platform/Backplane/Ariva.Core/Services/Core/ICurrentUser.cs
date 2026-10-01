namespace Ariva.Core.Services;

/// <summary>
/// The caller of the current request or message, read from the validated access token (ADR-0026 claims sub, sid,
/// auth_time, amr). Trimmed from AMAN's ICurrentUser: no port, shift or desk context (Ariva has none per user), and
/// no access token property, so a service can never copy the raw token into a log, an event or the database
/// (CWE-532). Implemented in Ariva.Api.Common by ARV-010a.
/// </summary>
public interface ICurrentUser : ISvcScoped
{
    /// <summary>The user id (claim sub); null when the caller is anonymous.</summary>
    Guid? Id { get; }

    /// <summary>The login name, for audit stamps.</summary>
    string UserName { get; }

    /// <summary>The server-side session id (claim sid), used for revocation checks.</summary>
    Guid? SessionId { get; }

    /// <summary>Role codes from <see cref="RoleCodes"/>.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>When the user last proved who they are (claim auth_time, UTC); drives step-up (RFC 9470).</summary>
    DateTime? AuthenticatedAt { get; }

    /// <summary>Authentication methods used (claim amr), for example "pwd" and "otp".</summary>
    IReadOnlyCollection<string> AuthenticationMethods { get; }

    bool IsAuthenticated { get; }

    /// <summary>The caller IP after forwarded-header processing (trusted proxies only); used for rate limits and audit.</summary>
    string GetCallerIpAddress();

    /// <summary>
    /// Sets a fixed system identity for background work with no HTTP request (jobs, consumers). The user name is
    /// stamped on audit fields so the row shows which job wrote it.
    /// </summary>
    void SetSystemUser(Guid id, string userName);
}
