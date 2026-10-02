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
