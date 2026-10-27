using Ariva.Api.Common.Extensions;

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
    /// The <c>display-plays</c> policy for the proof of play batches of the display players (ARV-144), partitioned like the
    /// board's by the presented credential. Default 6 batches per minute per display: a player sends one a minute, and its
    /// retries; at 100 plays a batch, at most 600 plays a minute reach the schedule check and the database.
    /// </summary>
    public FixedWindowSettings DisplayPlays { get; set; } = new() { PermitLimit = 6, WindowSeconds = 60 };

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

    /// <summary>
    /// The <c>reports</c> policy (ARV-096): on-demand daily reports and their CSV files built at once per host process (each reads a
    /// site's local day of minutes, up to 720,000 rows at the zone bound), and how many may wait for a turn; the rest get 429.
    /// Default 2 at once and 4 waiting.
    /// </summary>
    public ConcurrencySettings Reports { get; set; } = new() { PermitLimit = 2, QueueLimit = 4 };

    /// <summary>
    /// The CSV imports' previews and, counted apart, their commits per user (ARV-155): each holds the site's row lock and the
    /// host's one upload permit while it runs (a preview's dry run about 3 s at 500 devices, 12 s at 10,000 mappings), so one
    /// user looping them would delay every registration, calibration and publication at the site and turn other uploads away
    /// (429). A sliding window per user and step in this host process (ARV-250; a fixed one let twice the limit through across
    /// its end), counted after authorization and before the file is read; with N replicas a user may reach N times the limit.
    /// Default 6 previews and 6 commits in any 10 minutes less one segment (10 seconds). A refused import is answered at once.
    /// </summary>
    public SlidingWindowSettings CsvImports { get; set; } = new() { PermitLimit = 6, WindowSeconds = 600, SegmentsPerWindow = 60 };

    /// <summary>
    /// Every other upload per user and kind (ARV-250): floor plans, SSIM previews and, apart, imports, site configuration
    /// exports and, apart, imports, advertisement images and videos. Each holds the host's one upload permit while its body
    /// arrives (a 20 MB file up to about 11 minutes at the slowest rate allowed) and while it is checked and stored, so one user
    /// starting them in a loop would turn every other upload away (429). This caps the transfers a user starts; how many places
    /// they hold at once is <see cref="UploadShare"/>'s (ARV-265). A sliding window per user and kind in this host process,
    /// counted once the request holds the permit, after authorization and before the body is read; with N replicas a user may
    /// reach N times the limit. Default 10 of a kind in any 10 minutes less one segment (10 seconds). A refused upload is
    /// answered at once.
    /// </summary>
    public SlidingWindowSettings Uploads { get; set; } = new() { PermitLimit = 10, WindowSeconds = 600, SegmentsPerWindow = 60 };

    /// <summary>
    /// Each user's share of the host's upload places (ARV-265): how many uploads and transfers of the <c>upload</c> policy one
    /// user may have running or waiting at once in this host process, of its one running and two waiting. Taken once the caller
    /// is authorized and before the body is read; one beyond it is refused 429 at once. Default 1, so one user's slow transfers
    /// hold one place and another user finds the permit or a waiting place unless three other users hold them. 1 to 3, refused at
    /// start otherwise; 3 shares nothing (the E2E suite's, whose workers upload in parallel with the same accounts).
    /// </summary>
    public UploadShareSettings UploadShare { get; set; } = new() { PermitLimit = 1 };
}

/// <summary>Each user's share of the host's upload places (ARV-265): <see cref="PermitLimit"/> running or waiting at once.</summary>
public sealed class UploadShareSettings
{
    /// <summary>The places one user may hold at once, running or waiting.</summary>
    public int PermitLimit { get; set; }

    /// <summary>True from 1 to the host's places (<see cref="RateLimitingExtensions.UploadPermits"/> and <see cref="RateLimitingExtensions.UploadQueue"/>).</summary>
    public bool IsValid => PermitLimit is > 0 and <= RateLimitingExtensions.UploadPermits + RateLimitingExtensions.UploadQueue;
}

/// <summary>A concurrency limiter: <see cref="PermitLimit"/> requests at once and <see cref="QueueLimit"/> waiting.</summary>
public sealed class ConcurrencySettings
{
    public int PermitLimit { get; set; }

    public int QueueLimit { get; set; }

    public bool IsValid => PermitLimit > 0 && QueueLimit >= 0;
}

/// <summary>
/// A sliding window limiter (ARV-250): <see cref="PermitLimit"/> requests in a window of <see cref="WindowSeconds"/> seconds kept
/// in <see cref="SegmentsPerWindow"/> segments. A permit is free again one window after the segment it was taken in began, so at
/// most <see cref="PermitLimit"/> are taken in any stretch one segment shorter than the window. No queue: a refusal is at once.
/// </summary>
public sealed class SlidingWindowSettings
{
    /// <summary>Requests allowed in a window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>Window length in seconds.</summary>
    public int WindowSeconds { get; set; }

    /// <summary>The window's segments: more of them make the bound hold over a stretch closer to the whole window.</summary>
    public int SegmentsPerWindow { get; set; } = 60;

    /// <summary>The longest window: a day.</summary>
    public const int MaxWindowSeconds = 86_400;

    /// <summary>The most segments: each user and kind counted keeps one number per segment.</summary>
    public const int MaxSegmentsPerWindow = 600;

    /// <summary>True when every value is usable by the limiter: a window of at most a day, a segment a whole second at least.</summary>
    public bool IsValid => PermitLimit > 0 && WindowSeconds is > 0 and <= MaxWindowSeconds && SegmentsPerWindow is > 0 and <= MaxSegmentsPerWindow &&
                           SegmentsPerWindow <= WindowSeconds;
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
