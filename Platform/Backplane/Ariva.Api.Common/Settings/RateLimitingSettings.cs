namespace Ariva.Api.Common.Settings;

/// <summary>
/// Rate limits bound from <c>Security:RateLimiting</c>. Both limiters partition by client IP address, which is the
/// real client only when the reverse proxy is listed in <c>Security:ForwardedHeaders</c>.
/// </summary>
public sealed class RateLimitingSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Security:RateLimiting";

    /// <summary>Global limiter applied to every request. Default 1000 requests per minute per IP address.</summary>
    public FixedWindowSettings Global { get; set; } = new() { PermitLimit = 1_000, WindowSeconds = 60 };

    /// <summary>Stricter <c>auth</c> policy for login, token and TOTP endpoints. Default 10 requests per minute per IP address (ADR-0026).</summary>
    public FixedWindowSettings Auth { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>
    /// The <c>device</c> policy for sensor pushes (ARV-022), partitioned by the presented credential's prefix (by address
    /// when there is none). Default 600 requests per minute per device: a push every 100 ms.
    /// </summary>
    public FixedWindowSettings Device { get; set; } = new() { PermitLimit = 600, WindowSeconds = 60 };

    /// <summary>
    /// The <c>integration-auth</c> policy for the integration token exchange (ARV-042): 20 a minute per client address
    /// (the 5 a minute per client are counted in the database).
    /// </summary>
    public FixedWindowSettings IntegrationAuth { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    /// <summary>
    /// The <c>integration-batch</c> policy for the Integration API's batch endpoints (ARV-043): batches of up to 1 MB
    /// handled at once per host process, and how many may wait, before any body is read; the rest get 429 (CWE-400).
    /// Default 8 at once and 16 waiting.
    /// </summary>
    public ConcurrencySettings IntegrationBatch { get; set; } = new() { PermitLimit = 8, QueueLimit = 16 };

    /// <summary>
    /// The <c>integration-aidx</c> policy for AIDX messages (ARV-044): up to 5 MB each and read into a document, so fewer
    /// at once per host process than JSON batches. Default 2 at once and 4 waiting.
    /// </summary>
    public ConcurrencySettings IntegrationAidx { get; set; } = new() { PermitLimit = 2, QueueLimit = 4 };

    /// <summary>
    /// Batches one integration client may send per window to one host process (ARV-043), counted after its token is
    /// validated so no one can spend another client's allowance. Default 120 a minute.
    /// </summary>
    public FixedWindowSettings IntegrationClient { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };

    /// <summary>
    /// The <c>validation-results</c> policy (ARV-104g): requests for validation results handled at once per host process, and how
    /// many may wait for a turn; the rest get 429. Default 4 at once and 8 waiting.
    /// </summary>
    public ConcurrencySettings ValidationResults { get; set; } = new() { PermitLimit = 4, QueueLimit = 8 };
}

/// <summary>A concurrency limiter: <see cref="PermitLimit"/> requests at once and <see cref="QueueLimit"/> waiting.</summary>
public sealed class ConcurrencySettings
{
    public int PermitLimit { get; set; }

    public int QueueLimit { get; set; }

    public bool IsValid => PermitLimit > 0 && QueueLimit >= 0;
}

/// <summary>
/// A fixed window limiter: <see cref="PermitLimit"/> requests every <see cref="WindowSeconds"/> seconds.
/// </summary>
public sealed class FixedWindowSettings
{
    /// <summary>Requests allowed per window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>Window length in seconds.</summary>
    public int WindowSeconds { get; set; }

    /// <summary>Requests queued once the window is exhausted. Default 0: reject immediately with 429.</summary>
    public int QueueLimit { get; set; }

    /// <summary>True when every value is usable by the limiter.</summary>
    public bool IsValid => PermitLimit > 0 && WindowSeconds > 0 && QueueLimit >= 0;
}
