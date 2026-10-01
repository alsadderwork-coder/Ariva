namespace Ariva.Core.Security;

/// <summary>Whether a session (the access token's sid) may still be used (ARV-010b).</summary>
public enum SessionState
{
    Active,
    Expired,
    Revoked,
    Unknown
}

/// <summary>
/// The per-request session check every host runs after token validation. Implementations cache briefly (4 seconds by
/// default) so revocation reaches every node within 5 seconds.
/// </summary>
public interface ISessionValidator
{
    Task<SessionState> CheckAsync(Guid sessionId, CancellationToken ct = default);
}
