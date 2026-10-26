using System.Threading.RateLimiting;
using Ariva.Api.Common.Settings;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Main.Controllers.AdminArea.Uploads;

/// <summary>
/// Every upload's count per user (ARV-155 for the CSV imports, ARV-250 for every other endpoint of the <c>upload</c> policy):
/// a sliding window per user and kind in this host process, taken by <see cref="UploadLimitAttribute"/> once the caller is
/// authorized, so a forged token cannot spend another user's allowance. The CSV imports' previews and commits take
/// <see cref="RateLimitingSettings.CsvImports"/>, every other kind <see cref="RateLimitingSettings.Uploads"/>, each kind its own
/// allowance. A sliding window frees a permit one window after the segment it was taken in began, so at most the limit is taken
/// in any stretch one segment shorter than the window (a fixed window let twice it through across its end). Counted per replica:
/// with N replicas a user may reach N times the limit. It caps how many transfers a user starts, not how long one holds the host's
/// upload permit: the <c>upload</c> policy's queue is taken before authentication, so three slow transfers of one user can hold the
/// permit and both waiting places (docs/security/costly-operations.md section 15, item 17; ARV-265).
/// </summary>
public sealed class UploadRateLimiter : IDisposable
{
    #region Fields

    private readonly PartitionedRateLimiter<(string Caller, string Kind)> _limiter;
    private readonly SlidingWindowSettings _csvImports;
    private readonly SlidingWindowSettings _uploads;

    #endregion

    public UploadRateLimiter(IOptions<RateLimitingSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _csvImports = settings.Value.CsvImports;
        _uploads = settings.Value.Uploads;
        _limiter = PartitionedRateLimiter.Create<(string Caller, string Kind), (string, string)>(key => RateLimitPartition.GetSlidingWindowLimiter(key, _ =>
        {
            var window = SettingsOf(key.Kind);
            return new SlidingWindowRateLimiterOptions
            {
                PermitLimit = window.PermitLimit,
                Window = TimeSpan.FromSeconds(window.WindowSeconds),
                SegmentsPerWindow = window.SegmentsPerWindow,
                QueueLimit = 0,
                AutoReplenishment = true
            };
        }));
    }

    #region Methods

    /// <summary>The window a kind is counted in: the CSV imports' for their previews and commits, the uploads' for the rest.</summary>
    /// <param name="kind">An <see cref="UploadLimitAttribute"/> kind.</param>
    public SlidingWindowSettings SettingsOf(string kind) => UploadLimitAttribute.IsCsvImport(kind) ? _csvImports : _uploads;

    /// <summary>
    /// True when the caller may take one more upload of <paramref name="kind"/> now; otherwise the Retry-After to give: the window
    /// and a margin, by which every permit counted now is free again (a sliding window's refused lease names no time). The margin
    /// is one segment, or a tenth of a second for each segment when that is more: the partitioned limiter replenishes on a 100 ms
    /// tick, so each segment may start up to that late.
    /// </summary>
    /// <param name="caller">The signed-in caller's key (<see cref="UploadLimitAttribute.CallerOf"/>).</param>
    /// <param name="kind">The kind counted.</param>
    public (bool Allowed, TimeSpan RetryAfter) TryAcquire(string caller, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(caller);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        using var lease = _limiter.AttemptAcquire((caller, kind));
        if (lease.IsAcquired)
            return (true, TimeSpan.Zero);
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var after))
            return (false, after);
        var window = SettingsOf(kind);
        var margin = Math.Max((double)window.WindowSeconds / window.SegmentsPerWindow, window.SegmentsPerWindow * 0.1);
        return (false, TimeSpan.FromSeconds(window.WindowSeconds + margin));
    }

    public void Dispose() => _limiter.Dispose();

    #endregion
}
