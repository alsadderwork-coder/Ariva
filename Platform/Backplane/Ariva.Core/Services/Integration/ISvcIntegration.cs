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

/// <summary>
/// Idempotency of Integration API batches (ARV-043, script 0026). <see cref="ClaimAsync"/> runs in the transaction that
/// applies the batch: the first call with a key claims it, a concurrent call with the same key waits for that transaction,
/// and a later call gets what the first one stored with <see cref="CompleteAsync"/>. Keys are per client and kept for
/// <see cref="Integration.IntegrationBatches.KeyLifetime"/>. System calls (the caller is the authenticated client).
/// </summary>
public interface ISvcIntegrationIdempotency : ISvcScoped
{
    Task<IdempotencyClaim> ClaimAsync(IdempotencyRequest request, CancellationToken ct = default);

    Task CompleteAsync(IdempotencyRequest request, int statusCode, string responseBody, CancellationToken ct = default);

    /// <summary>Deletes expired keys, at most <paramref name="limit"/>; the number deleted.</summary>
    Task<int> SweepAsync(int limit, CancellationToken ct = default);
}

/// <summary>A batch's claim on its key: the client, the key, what it does (operation and site) and the SHA-256 of its body.</summary>
public sealed record IdempotencyRequest(string ClientId, string Key, string Operation, string SiteCode, string RequestSha256);

public enum IdempotencyOutcome
{
    /// <summary>The key is this call's: apply the batch and complete the key.</summary>
    Claimed,

    /// <summary>The key answered an identical request before: send <see cref="IdempotencyClaim.StatusCode"/> and the body again.</summary>
    Replay,

    /// <summary>The key was used for another request (body, operation or site): refuse (422).</summary>
    Mismatch
}

public sealed record IdempotencyClaim(IdempotencyOutcome Outcome, int StatusCode = 0, string ResponseBody = null)
{
    public static readonly IdempotencyClaim Claimed = new(IdempotencyOutcome.Claimed);
    public static readonly IdempotencyClaim Mismatch = new(IdempotencyOutcome.Mismatch);
}

public static class OutboundErrors
{
    public const string NotFound = "The record does not exist.";
    public const string InvalidPurpose = "Purpose is Generic or AcrisFlights.";
    public const string DuplicateCode = "An endpoint with this code exists.";
    public const string BeyondOwnSites = "You cannot bind an endpoint to sites you cannot access yourself.";
    public const string UnknownSite = "Unknown site.";
}

/// <summary>
/// Outbound endpoints for administrators (ARV-045). Visible only when the caller's sites cover all of the endpoint's
/// (404 otherwise); bound only to sites the caller holds. Secret material is checked, protected and never returned; every
/// change is audited without it. Changes are critical actions (a second factor in the last 15 minutes, at the controller).
/// </summary>
public interface ISvcOutboundEndpoints : ISvcScoped
{
    Task<Result<IReadOnlyList<OutboundEndpointViewModel>>> ListAsync(string siteCode, CancellationToken ct = default);

    Task<Result<OutboundEndpointViewModel>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<OutboundEndpointViewModel>> CreateAsync(CreateOutboundEndpointRequest request, CancellationToken ct = default);

    Task<Result<OutboundEndpointViewModel>> UpdateAsync(Guid id, UpdateOutboundEndpointRequest request, CancellationToken ct = default);

    Task<Result<OutboundEndpointViewModel>> SetSecretAsync(Guid id, OutboundSecretRequest request, CancellationToken ct = default);

    Task<Result<OutboundEndpointViewModel>> DisableAsync(Guid id, CancellationToken ct = default);

    Task<Result<OutboundEndpointViewModel>> EnableAsync(Guid id, CancellationToken ct = default);
}
