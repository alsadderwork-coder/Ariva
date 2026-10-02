using Ariva.Infra.Caching;
using Ariva.Infra.Live;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Ariva.Api.Main.Hubs;

/// <summary>
/// Forwards announced snapshots to the zone groups of the live hub. With the Redis backplane a group send reaches every
/// replica's connections, so only one replica forwards: the holder of a Redis lease (renewed every 5 seconds, lost after
/// 15), and another takes over when it stops. Each snapshot was checked when read back (<see cref="LiveZones.Plausible"/>).
/// </summary>
public sealed class LiveRelay(ILiveSnapshotStore snapshots, IAlertNotices alerts, IHubContext<LiveHub> hub, IServiceProvider services, TimeProvider timeProvider,
    ILogger<LiveRelay> logger)
    : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Renew = TimeSpan.FromSeconds(5);
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private volatile bool _leader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (snapshots is NoLiveSnapshots)
            return;
        var redis = services.GetService<RedisConnection>();
        // Redis may be down when Main starts; the relay keeps trying rather than stopping the host. Each channel is
        // subscribed once: a retry after one succeeded does not subscribe it again (no doubled sends).
        bool zones = false, alertsSubscribed = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!zones)
                {
                    await snapshots.SubscribeAsync(SendAsync, stoppingToken);
                    zones = true;
                }

                if (!alertsSubscribed)
                {
                    await alerts.SubscribeAsync(SendAlertAsync, stoppingToken);
                    alertsSubscribed = true;
                }

                break;
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                logger.LogWarning(e, "Live relay could not subscribe to the live snapshots; retrying");
                await Task.Delay(Renew, timeProvider, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _leader = redis is null || await HoldLeaseAsync(redis);
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                _leader = false;
                logger.LogWarning(e, "Live relay lease not renewed");
            }

            await Task.Delay(Renew, timeProvider, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task SendAsync(LiveZoneSnapshot snapshot)
    {
        if (!_leader)
            return;
        try
        {
            await hub.Clients.Group(LiveHub.Group(snapshot.ZoneKey)).SendAsync(LiveHub.ZoneMethod, snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Live snapshot of {Zone} not sent", snapshot.ZoneKey);
        }
    }

    // An alert notice goes to the site's alert groups of the roles responsible for it (ARV-039).
    private async Task SendAlertAsync(AlertNotice notice)
    {
        if (!_leader)
            return;
        try
        {
            await hub.Clients.Groups([.. notice.Audience().Select(role => LiveHub.AlertGroup(notice.SiteCode, role))]).SendAsync(LiveHub.AlertMethod, notice);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Alert notice of {Alert} not sent", notice.AlertId);
        }
    }

    private async Task<bool> HoldLeaseAsync(RedisConnection redis)
    {
        var database = (await redis.GetAsync()).GetDatabase();
        var key = $"{redis.Settings.InstanceName}live:relay";
        // Take the lease if free, or extend it if it is ours.
        var result = await database.ScriptEvaluateAsync(
            "if redis.call('set', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then return 1 end " +
            "if redis.call('get', KEYS[1]) == ARGV[1] then redis.call('pexpire', KEYS[1], ARGV[2]) return 1 end return 0",
            [key], [_owner, (long)Lease.TotalMilliseconds]);
        return (long)result == 1;
    }
}
