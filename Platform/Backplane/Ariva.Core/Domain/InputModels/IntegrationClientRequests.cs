using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// A new integration client (ARV-042). <c>RequireTotpPerRequest</c> left out takes the kind's default: on for immigration
/// clients, off otherwise. The client id, secret and TOTP seed are generated and shown once.
/// </summary>
public sealed record CreateIntegrationClientRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(16)] string Kind,
    [Required, MaxLength(6)] IReadOnlyList<string> Scopes,
    [Required, MaxLength(32)] IReadOnlyList<string> SiteCodes,
    [MaxLength(16)] IReadOnlyList<string> AllowedNetworks = null,
    bool? RequireTotpPerRequest = null);

/// <summary>What a client may do: name, scopes, sites, networks and the per-request TOTP policy. Its tokens stop working when any of these change.</summary>
public sealed record UpdateIntegrationClientRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(6)] IReadOnlyList<string> Scopes,
    [Required, MaxLength(32)] IReadOnlyList<string> SiteCodes,
    [Required, MaxLength(16)] IReadOnlyList<string> AllowedNetworks,
    [Required] bool? RequireTotpPerRequest);

/// <summary>The token exchange (AMAN field names): client id, client secret and the current TOTP code.</summary>
public sealed record IntegrationTokenRequest(
    [MaxLength(64)] string ClientId,
    [MaxLength(128)] string ClientSecret,
    [MaxLength(16)] string TotpCode);
