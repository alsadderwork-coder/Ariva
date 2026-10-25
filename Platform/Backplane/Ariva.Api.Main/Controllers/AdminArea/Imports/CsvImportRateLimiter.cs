using System.Threading.RateLimiting;
using Ariva.Api.Common.Settings;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Main.Controllers.AdminArea.Imports;

/// <summary>
/// The CSV imports' previews and commits per user (ARV-155, <see cref="RateLimitingSettings.CsvImports"/>): a fixed window per
/// user and step in this host process, taken by <see cref="CsvImportLimitAttribute"/> once the caller is authorized, so a
/// forged token cannot spend another user's allowance. Counted per replica: with N replicas a user may reach N times the limit,
/// which still bounds how long one user holds a site's lock and a host's upload permit.
/// </summary>
public sealed class CsvImportRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<(string Caller, string Step)> _limiter;

    public CsvImportRateLimiter(IOptions<RateLimitingSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var window = settings.Value.CsvImports;
        Window = TimeSpan.FromSeconds(window.WindowSeconds);
        _limiter = PartitionedRateLimiter.Create<(string Caller, string Step), (string, string)>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = window.PermitLimit,
                Window = Window,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    /// <summary>The window's length: the longest a refused user waits, and their Retry-After when the lease names none.</summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// True when the caller may take one more <paramref name="step"/> now; otherwise the Retry-After to give: the refused lease's
    /// own, which for a fixed window is the window's length (never shorter than the wait, often longer), else the window.
    /// </summary>
    /// <param name="caller">The signed-in caller's key (<see cref="CsvImportLimitAttribute.CallerOf"/>).</param>
    /// <param name="step">The step counted.</param>
    public (bool Allowed, TimeSpan RetryAfter) TryAcquire(string caller, string step)
    {
        ArgumentException.ThrowIfNullOrEmpty(caller);
        ArgumentException.ThrowIfNullOrEmpty(step);
        using var lease = _limiter.AttemptAcquire((caller, step));
        if (lease.IsAcquired)
            return (true, TimeSpan.Zero);
        return (false, lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : Window);
    }

    public void Dispose() => _limiter.Dispose();
}
