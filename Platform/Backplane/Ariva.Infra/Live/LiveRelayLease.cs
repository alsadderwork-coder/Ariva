using Ariva.Infra.Caching;
using StackExchange.Redis;

namespace Ariva.Infra.Live;

/// <summary>
/// The live relay's lease (ARV-035): one Ariva.Api.Main replica forwards the live snapshots and alert notices to the hub's
/// groups, the holder of <see cref="RedisNames.LiveRelayKey"/>. A replica takes the lease when it is free or extends it when it
/// is its own, in one script on the server, so two replicas never both hold it. Since ARV-083b in Ariva.Infra, so the Redis ACL
/// test (RedisAclTests) runs the very script Main's ACL user is granted.
/// </summary>
public static class LiveRelayLease
{
    #region Constants

    /// <summary>The script: SET NX PX takes a free lease, GET and PEXPIRE extend the holder's own (three commands, one key).</summary>
    public const string Script =
        "if redis.call('set', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then return 1 end " +
        "if redis.call('get', KEYS[1]) == ARGV[1] then redis.call('pexpire', KEYS[1], ARGV[2]) return 1 end return 0";

    #endregion

    #region Methods

    /// <summary>Takes the lease for <paramref name="owner"/>, or extends it when it is already theirs; false when another replica holds it.</summary>
    /// <param name="redis">The host's Redis connection.</param>
    /// <param name="owner">This replica's lease owner, unique per process.</param>
    /// <param name="lease">How long the lease lasts without a renewal.</param>
    public static async Task<bool> HoldAsync(RedisConnection redis, string owner, TimeSpan lease)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var database = (await redis.GetAsync()).GetDatabase();
        var key = RedisNames.LiveRelayKey(redis.Settings.InstanceName);
        var result = await database.ScriptEvaluateAsync(Script, [key], [owner, (long)lease.TotalMilliseconds]);
        return (long)result == 1;
    }

    #endregion
}
