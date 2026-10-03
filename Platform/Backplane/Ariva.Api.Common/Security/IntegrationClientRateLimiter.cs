using System.Threading.RateLimiting;
using Ariva.Api.Common.Settings;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Common.Security;

/// <summary>
/// Batches per integration client (ARV-043, <see cref="RateLimitingSettings.IntegrationClient"/>): a fixed window per
/// client id in this host process, taken only after the client's token was validated and its record checked, so a
/// forged token naming another client cannot spend that client's allowance. Counted per replica: with N replicas a
/// client may send up to N times the limit, which still bounds the work and the idempotency keys one client creates.
/// </summary>
public sealed class IntegrationClientRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public IntegrationClientRateLimiter(IOptions<RateLimitingSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var window = settings.Value.IntegrationClient;
        _limiter = PartitionedRateLimiter.Create<string, string>(clientId => RateLimitPartition.GetFixedWindowLimiter(clientId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = window.PermitLimit,
            Window = TimeSpan.FromSeconds(window.WindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    }

    /// <summary>True when the client may send one more batch now; otherwise how long to wait.</summary>
    public (bool Allowed, TimeSpan? RetryAfter) TryAcquire(string clientId)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        using var lease = _limiter.AttemptAcquire(clientId);
        if (lease.IsAcquired)
            return (true, null);
        return (false, lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : null);
    }

    public void Dispose() => _limiter.Dispose();
}
