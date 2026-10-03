namespace Ariva.Core.Domain.ViewModels;

/// <summary>An integration client as administrators see it (ARV-042): never its secret or seed.</summary>
public sealed record IntegrationClientViewModel(
    Guid Id,
    string ClientId,
    string Name,
    string Kind,
    string Status,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> SiteCodes,
    IReadOnlyList<string> AllowedNetworks,
    bool RequireTotpPerRequest,
    bool Locked,
    DateTime? LockedUntilUtc,
    int FailedAttempts,
    DateTime SecretChangedUtc,
    DateTime TotpChangedUtc,
    DateTime? LastTokenUtc);

/// <summary>
/// What a client's operator loads into their system, shown once (at creation, or the part rotated): the client secret
/// and the TOTP seed in base32 with its otpauth URI. A null field was not changed.
/// </summary>
public sealed record IntegrationClientCredentialsViewModel(IntegrationClientViewModel Client, string ClientSecret, string TotpSecret, string TotpUri);

/// <summary>The token exchange's answer (AMAN shape): a 15-minute access token for the Integration API, no refresh token.</summary>
public sealed record IntegrationTokenViewModel(string AccessToken, DateTime ExpiresAt, Guid SessionId);

/// <summary>Who the caller is, for an integrator checking its set-up.</summary>
public sealed record IntegrationCallerViewModel(string ClientId, string Name, IReadOnlyList<string> Scopes, IReadOnlyList<string> SiteCodes, bool RequireTotpPerRequest);

/// <summary>An outbound endpoint as administrators see it (ARV-045): never its secret material.</summary>
public sealed record OutboundEndpointViewModel(
    Guid Id,
    string Code,
    string Name,
    string Purpose,
    string Status,
    IReadOnlyList<string> SiteCodes,
    string BaseUrl,
    IReadOnlyList<string> AllowedNetworks,
    string AuthKind,
    string TokenPath,
    string ClientId,
    string Scope,
    string HeaderName,
    string KeyId,
    bool TotpPerRequest,
    bool HasPinnedCa,
    bool HasClientCertificate,
    int TimeoutSeconds,
    int RetryCount,
    int BreakerFailures,
    int BreakSeconds,
    string PullPath,
    int PollSeconds,
    DateTime SecretChangedUtc,
    DateTime? LastPollUtc,
    string LastStatus,
    int ConsecutiveFailures);
