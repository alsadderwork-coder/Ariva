using System.ComponentModel.DataAnnotations;

namespace Ariva.Core.Domain.InputModels;

/// <summary>
/// A new outbound endpoint (ARV-045): where Ariva may call, from which networks the answer may come, how it
/// authenticates and, for the ACRIS flight pull, which site it feeds. The secret material is set with it and never shown again.
/// </summary>
public sealed record CreateOutboundEndpointRequest(
    [Required, MaxLength(24)] string Code,
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(16)] string Purpose,
    [Required, MaxLength(32)] IReadOnlyList<string> SiteCodes,
    [Required] OutboundConnectionRequest Connection,
    [Required] OutboundSecretRequest Secret);

/// <summary>
/// Everything but the code; a change takes effect on the next call (cached clients are rebuilt). Changing where the
/// endpoint is or how it is trusted (base URL, networks, pinned CA, token path) needs the secret again in
/// <see cref="Secret"/>, so the stored secret is never sent to a place its owner did not choose (CWE-522).
/// </summary>
public sealed record UpdateOutboundEndpointRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(32)] IReadOnlyList<string> SiteCodes,
    [Required] OutboundConnectionRequest Connection,
    OutboundSecretRequest Secret = null);

/// <summary>
/// The connection: base URL (HTTPS), the networks its resolved addresses must fall in, timeouts and resilience, the
/// authentication kind with its non-secret settings, an optional pinned CA (PEM) and, for the ACRIS pull, the path and interval.
/// </summary>
public sealed record OutboundConnectionRequest(
    [Required, MaxLength(300)] string BaseUrl,
    [Required, MaxLength(16)] IReadOnlyList<string> AllowedNetworks,
    [Required, MaxLength(32)] string AuthKind,
    [MaxLength(200)] string TokenPath = null,
    [MaxLength(128)] string ClientId = null,
    [MaxLength(200)] string Scope = null,
    [MaxLength(64)] string HeaderName = null,
    [MaxLength(64)] string KeyId = null,
    bool TotpPerRequest = false,
    [MaxLength(16384)] string PinnedCaPem = null,
    int TimeoutSeconds = 10,
    int RetryCount = 2,
    int BreakerFailures = 5,
    int BreakSeconds = 60,
    [MaxLength(300)] string PullPath = null,
    int PollSeconds = 60);

/// <summary>Secret material, by authentication kind: only the fields the kind uses may be given. Write-only.</summary>
public sealed record OutboundSecretRequest(
    [MaxLength(256)] string ClientSecret = null,
    [MaxLength(128)] string TotpSeed = null,
    [MaxLength(512)] string ApiKey = null,
    [MaxLength(256)] string HmacKey = null,
    [MaxLength(90000)] string CertificatePfxBase64 = null,
    [MaxLength(256)] string CertificatePassword = null);
