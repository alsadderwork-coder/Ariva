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

/// <summary>A line of an SSIM file Ariva could not read: its number and the reason (never the line's text).</summary>
public sealed record SsimLineErrorViewModel(int Line, string Reason);

/// <summary>A leg an SSIM file would create or update, for the preview.</summary>
public sealed record SsimLegViewModel(string FlightKey, string Direction, DateTime ScheduledUtc, string Origin, string Destination);

/// <summary>
/// What an SSIM file would do for a site (ARV-046): its SHA-256, the preview token (what the import must present: it binds
/// the file, the site, the horizon, the window and the user, for two hours), lines and leg records read, the leg records
/// touching the site, the legs they expand to within the horizon (arrivals and departures), the window, the first 20
/// legs and the first 100 line errors with their total.
/// </summary>
public sealed record SsimPreviewViewModel(
    string Sha256,
    string PreviewToken,
    int Lines,
    int LegRecords,
    int LegRecordsOfSite,
    int Legs,
    int Arrivals,
    int Departures,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SsimLegViewModel> Sample,
    IReadOnlyList<SsimLineErrorViewModel> Errors,
    int ErrorCount);

/// <summary>What an import did: legs sent to the flight model, applied, unchanged (a live feed has reported the leg, or an earlier import said the same) and refused.</summary>
public sealed record SsimImportViewModel(string Sha256, int Legs, int Applied, int Unchanged, int Refused, int LineErrors);
