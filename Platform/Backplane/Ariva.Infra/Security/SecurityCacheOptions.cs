using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Security;

/// <summary>
/// ARV-081 (ASVS V16.5.3): the entry options of every cache that feeds an authentication or authorization decision
/// (sessions, roles, sites, device credentials). The hosts' default entry options serve a stale copy for up to an hour
/// when the database answers in more than 500 ms or not at all (fail-safe with a soft timeout), which is right for
/// topology and wrong for a decision: a revoked session or a removed role would pass during an outage. Here a miss goes
/// to the database every time, waits for it up to <see cref="LookupTimeout"/> and then fails (503 with Retry-After,
/// DependencyOutage) instead of answering from an expired copy. A copy still valid in the distributed cache is used as
/// before; eviction after a change reaches every host through the backplane.
/// </summary>
public static class SecurityCacheOptions
{
    /// <summary>How long a security decision waits for the database before the request fails as an outage.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    public static FusionCacheEntryOptions Apply(FusionCacheEntryOptions options, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Duration = duration;
        options.IsFailSafeEnabled = false;
        options.FactorySoftTimeout = Timeout.InfiniteTimeSpan;
        options.FactoryHardTimeout = LookupTimeout;
        options.AllowTimedOutFactoryBackgroundCompletion = false;
        return options;
    }
}
