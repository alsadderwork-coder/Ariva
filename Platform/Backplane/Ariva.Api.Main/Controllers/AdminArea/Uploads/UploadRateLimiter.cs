using System.Threading.RateLimiting;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Settings;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Main.Controllers.AdminArea.Uploads;

/// <summary>
/// Every upload's count and place in this host process, taken by <see cref="UploadLimitAttribute"/> once the caller is authorized,
/// so a forged token can neither spend another user's allowance nor take a place.
/// <para>
/// The count (ARV-155 for the CSV imports, ARV-250 for every other endpoint of the <c>upload</c> policy): a sliding window per user
/// and kind. The CSV imports' previews and commits take <see cref="RateLimitingSettings.CsvImports"/>, every other kind
/// <see cref="RateLimitingSettings.Uploads"/>, each kind its own allowance. A sliding window frees a permit one window after the
/// segment it was taken in began, so at most the limit is taken in any stretch one segment shorter than the window (a fixed window
/// let twice it through across its end). Counted per replica: with N replicas a user may reach N times the limit.
/// </para>
/// <para>
/// The places (ARV-265): the host takes <see cref="RateLimitingExtensions.UploadPermits"/> upload at a time and
/// <see cref="RateLimitingExtensions.UploadQueue"/> waiting, oldest first, and one user holds at most
/// <see cref="RateLimitingSettings.UploadShare"/> of them at once, running or waiting (one by default). The rate limiting middleware
/// took these places before ARV-265, ahead of authentication, so one user's three slow transfers could hold the permit and both
/// waiting places for about 33 minutes; now a user's next transfer is refused at once while one of theirs runs or waits, and another
/// user finds the permit or a waiting place unless as many other users hold them.
/// </para>
/// </summary>
public sealed class UploadRateLimiter : IDisposable
{
    #region Fields

    private readonly PartitionedRateLimiter<(string Caller, string Kind)> _limiter;
    private readonly PartitionedRateLimiter<string> _shares;
    private readonly ConcurrencyLimiter _places;
    private readonly SlidingWindowSettings _csvImports;
    private readonly SlidingWindowSettings _uploads;

    #endregion

    public UploadRateLimiter(IOptions<RateLimitingSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _csvImports = settings.Value.CsvImports;
        _uploads = settings.Value.Uploads;
        var share = settings.Value.UploadShare.PermitLimit;
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
        _shares = PartitionedRateLimiter.Create<string, string>(caller => RateLimitPartition.GetConcurrencyLimiter(caller, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = share,
            QueueLimit = 0
        }));
        _places = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = RateLimitingExtensions.UploadPermits,
            QueueLimit = RateLimitingExtensions.UploadQueue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    #region Methods

    /// <summary>The window a kind is counted in: the CSV imports' for their previews and commits, the uploads' for the rest.</summary>
    /// <param name="kind">An <see cref="UploadLimitAttribute"/> kind.</param>
    public SlidingWindowSettings SettingsOf(string kind) => UploadLimitAttribute.IsCsvImport(kind) ? _csvImports : _uploads;

    /// <summary>
    /// Whether the caller's window for <paramref name="kind"/> is used up now, without counting this request (ARV-265): the count is
    /// taken only once the request holds the host's permit (<see cref="TryAcquire"/>), so a request the host turns away spends none of
    /// the caller's allowance, and this check refuses a caller past it before they take a place. Null while a permit is left, else
    /// the Retry-After to give.
    /// </summary>
    /// <param name="caller">The signed-in caller's key (<see cref="UploadLimitAttribute.CallerOf"/>).</param>
    /// <param name="kind">The kind counted.</param>
    public TimeSpan? Exhausted(string caller, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(caller);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        return _limiter.GetStatistics((caller, kind)) is { CurrentAvailablePermits: > 0 } ? null : WindowRetryAfter(kind);
    }

    /// <summary>
    /// The caller's share of the host's upload places (ARV-265): acquired while the caller holds fewer places than
    /// <see cref="RateLimitingSettings.UploadShare"/>, running or waiting, and never waits. Dispose it when the request ends.
    /// </summary>
    /// <param name="caller">The signed-in caller's key (<see cref="UploadLimitAttribute.CallerOf"/>).</param>
    public RateLimitLease TakeShare(string caller)
    {
        ArgumentException.ThrowIfNullOrEmpty(caller);
        return _shares.AttemptAcquire(caller);
    }

    /// <summary>
    /// One of the host's upload places (ARV-265): the permit at once when it is free, else a waiting place, oldest first, until the
    /// permit frees or <paramref name="ct"/> ends (the request's: its client goes or its <c>transfer</c> timeout passes); not acquired
    /// when every waiting place is taken. Dispose it when the request ends.
    /// </summary>
    /// <param name="ct">The request's token.</param>
    public ValueTask<RateLimitLease> TakePlaceAsync(CancellationToken ct) => _places.AcquireAsync(1, ct);

    /// <summary>The host's upload places now: whether the permit is free and how many requests wait for it.</summary>
    public RateLimiterStatistics Places() => _places.GetStatistics();

    /// <summary>
    /// True when the caller may take one more upload of <paramref name="kind"/> now, and counts it; otherwise the Retry-After to give:
    /// the window and a margin, by which every permit counted now is free again (a sliding window's refused lease names no time). The
    /// margin is one segment, or a tenth of a second for each segment when that is more: the partitioned limiter replenishes on a
    /// 100 ms tick, so each segment may start up to that late.
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
        return (false, lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : WindowRetryAfter(kind));
    }

    public void Dispose()
    {
        _limiter.Dispose();
        _shares.Dispose();
        _places.Dispose();
    }

    #endregion

    #region Helpers

    private TimeSpan WindowRetryAfter(string kind)
    {
        var window = SettingsOf(kind);
        var margin = Math.Max((double)window.WindowSeconds / window.SegmentsPerWindow, window.SegmentsPerWindow * 0.1);
        return TimeSpan.FromSeconds(window.WindowSeconds + margin);
    }

    #endregion
}
