using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Infra.Live;
using Ariva.Infra.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;

namespace Ariva.Api.Main.Hubs;

/// <summary>
/// The live hub (ARV-035): a screen joins the groups of the queue zones it shows and receives each zone's snapshot as
/// it changes. Connecting needs an access token with <c>LiveQueue.View</c> (the hub's policy, checked at negotiate and
/// connect; the session is checked on connect, on every call and every second, ADR-0026). Joining a zone is authorised
/// again, per call, against the caller's stored grants and sites (CWE-862, CWE-863), and the zone must be a queue zone of
/// the site's published profile: a zone of a site the caller does not hold answers the same "forbidden" as an unknown
/// zone. A connection joins at most <see cref="MaxGroups"/> zones and calls JoinZone at most <see cref="MaxJoinsPerMinute"/>
/// times a minute (each new group is a Redis subscription on the backplane).
/// </summary>
[Permission(nameof(Global.Defaults.Permissions.ViewLiveQueue))]
public sealed class LiveHub(
    ILiveSnapshotStore snapshots,
    IPermissionResolver permissions,
    SiteAccessResolver sites,
    LiveZoneDirectory zones,
    TimeProvider timeProvider,
    ILogger<LiveHub> logger) : Hub
{
    public const string Path = "/hubs/live";

    /// <summary>The client method a snapshot arrives on.</summary>
    public const string ZoneMethod = "zone";

    public const int MaxGroups = 64;

    public const int MaxJoinsPerMinute = 120;

    private const string JoinedKey = "ariva.live.zones";
    private const string JoinsKey = "ariva.live.joins";

    public static string Group(string zoneKey) => "zone:" + zoneKey;

    /// <summary>
    /// The endpoint's transport options: WebSockets only (no long-polling or server-sent events, whose URLs would carry
    /// the token on every request), closed when the token expires, small buffers.
    /// </summary>
    public static void Configure(HttpConnectionDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Transports = HttpTransportType.WebSockets;
        options.CloseOnAuthenticationExpiration = true;
        options.ApplicationMaxBufferSize = 8 * 1024;
        options.TransportMaxBufferSize = 64 * 1024;
    }

    /// <summary>Joins a zone's group and returns its latest snapshot (null when none yet).</summary>
    public async Task<LiveZoneSnapshot> JoinZone(string zoneKey)
    {
        if (!LiveZones.IsZoneKey(zoneKey))
            throw new HubException("invalid_zone");
        CountJoin();
        await AuthoriseAsync(zoneKey);
        var joined = Joined();
        lock (joined)
        {
            if (!joined.Contains(zoneKey) && joined.Count >= MaxGroups)
                throw new HubException("too_many_zones");
            joined.Add(zoneKey);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(zoneKey), Context.ConnectionAborted);
        return await snapshots.GetAsync(zoneKey, Context.ConnectionAborted);
    }

    public async Task LeaveZone(string zoneKey)
    {
        if (!LiveZones.IsZoneKey(zoneKey))
            throw new HubException("invalid_zone");
        var joined = Joined();
        lock (joined)
            joined.Remove(zoneKey);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(zoneKey), Context.ConnectionAborted);
    }

    // A fixed one-minute window per connection; the hub runs one invocation per client at a time.
    private void CountJoin()
    {
        var now = timeProvider.GetUtcNow();
        var (start, count) = Context.Items.TryGetValue(JoinsKey, out var value) && value is ValueTuple<DateTimeOffset, int> window ? window : (now, 0);
        if (now - start >= TimeSpan.FromMinutes(1))
            (start, count) = (now, 0);
        if (count >= MaxJoinsPerMinute)
            throw new HubException("too_many_joins");
        Context.Items[JoinsKey] = (start, count + 1);
    }

    private HashSet<string> Joined()
    {
        if (Context.Items.TryGetValue(JoinedKey, out var value) && value is HashSet<string> set)
            return set;
        var created = new HashSet<string>(StringComparer.Ordinal);
        Context.Items[JoinedKey] = created;
        return created;
    }

    // The grant and the site are read from storage on every join, so a change of roles or sites applies to the next join.
    private async Task AuthoriseAsync(string zoneKey)
    {
        var user = Context.User;
        var granted = user is not null && (await permissions.GetPermissionsAsync(user, Context.ConnectionAborted))
            .Contains(Global.Defaults.Permissions.ViewLiveQueue);
        var allowed = false;
        if (granted && Guid.TryParse(user.FindFirst(ArivaClaims.Subject)?.Value, out var userId))
            allowed = (await sites.ForUserAsync(userId, Context.ConnectionAborted)).Allows(LiveZones.SiteOf(zoneKey)) &&
                      await zones.ContainsAsync(zoneKey, Context.ConnectionAborted);
        if (!allowed)
        {
            logger.LogWarning("Live hub join refused for connection {Connection}: no grant for the zone's site or no such zone", Context.ConnectionId);
            throw new HubException("forbidden");
        }
    }
}
