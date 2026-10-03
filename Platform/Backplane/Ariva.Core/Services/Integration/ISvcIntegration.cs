using System.Net;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Integration;

public static class IntegrationErrors
{
    public const string NotFound = "The record does not exist.";
    public const string InvalidKind = "Kind is Aodb, Immigration, SensorGateway or Other.";
    public const string BeyondOwnSites = "You cannot bind a client to sites you cannot access yourself.";
    public const string UnknownSite = "Unknown site.";
    public const string Disabled = "The client is disabled.";

    /// <summary>The one answer of a failed token exchange, whatever failed (docs/architecture/integration.md).</summary>
    public const string InvalidClient = "invalid_client";
}

/// <summary>
/// Integration clients (ARV-042) for administrators, in Ariva.Api.Main. A client is visible only to administrators whose
/// sites cover all of its sites (404 otherwise). Creating a client, rotating its secret, resetting its TOTP seed and
/// changing what it may do are critical actions (step-up MFA, at the controller); every change is audited.
/// </summary>
public interface ISvcIntegrationClients : ISvcScoped
{
    Task<Result<IReadOnlyList<IntegrationClientViewModel>>> ListAsync(string siteCode, CancellationToken ct = default);

    Task<Result<IntegrationClientViewModel>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<IntegrationClientCredentialsViewModel>> CreateAsync(CreateIntegrationClientRequest request, CancellationToken ct = default);

    Task<Result<IntegrationClientViewModel>> UpdateAsync(Guid id, UpdateIntegrationClientRequest request, CancellationToken ct = default);

    Task<Result<IntegrationClientCredentialsViewModel>> RotateSecretAsync(Guid id, CancellationToken ct = default);

    Task<Result<IntegrationClientCredentialsViewModel>> ResetTotpAsync(Guid id, CancellationToken ct = default);

    Task<Result<IntegrationClientViewModel>> DisableAsync(Guid id, CancellationToken ct = default);

    Task<Result<IntegrationClientViewModel>> EnableAsync(Guid id, CancellationToken ct = default);

    Task<Result<IntegrationClientViewModel>> UnlockAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Integration client authentication (ARV-042), in Ariva.Api.Integration: the token exchange and the per-call check.
/// System calls; every failure of the exchange answers <see cref="IntegrationErrors.InvalidClient"/>.
/// </summary>
public interface ISvcIntegrationAuth : ISvcScoped
{
    Task<Result<IntegrationTokenViewModel>> ExchangeAsync(IntegrationTokenRequest request, IPAddress remote, CancellationToken ct = default);

    /// <summary>
    /// Whether a call with a valid token may go on: the client still exists and is active, the token is of the client's
    /// current version (every change of the client bumps it), the caller is inside its networks, and, when its policy says
    /// so, <paramref name="totpCode"/> is valid now. The caller as the database has it now (scopes and sites to authorize
    /// against), or the reason the call is refused (401).
    /// </summary>
    Task<IntegrationCallCheck> CheckCallAsync(string clientId, int tokenVersion, IPAddress remote, string totpCode, CancellationToken ct = default);

    /// <summary>Records one call of the Integration API (system; a separate transaction, so a failed call is recorded too).</summary>
    Task RecordCallAsync(IntegrationCallRecord call, CancellationToken ct = default);
}

/// <summary>The outcome of a per-call check: the caller, or why the call is refused.</summary>
public sealed record IntegrationCallCheck(IntegrationCallerViewModel Caller, string Reason);

/// <summary>One audited Integration API call: who, which scope, what, for which site, the answer and the payload's SHA-256.</summary>
public sealed record IntegrationCallRecord(string ClientId, Guid? SessionId, string Scope, string Method, string Route, string SiteCode, int Status, string PayloadSha256,
    long PayloadBytes, IPAddress Remote, DateTime AtUtc);
