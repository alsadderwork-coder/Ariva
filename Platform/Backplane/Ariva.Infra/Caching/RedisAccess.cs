using System.Text;

namespace Ariva.Infra.Caching;

/// <summary>
/// The Redis ACL user of each host (ARV-083b; CWE-269, CWE-285; ASVS 5.0 V13.2.1, V13.2.2): one declared manifest of the
/// commands, key patterns and channel patterns each host's user may use, no more. Ariva ships no Redis, so the site's Redis
/// administrator creates the users with the lines <see cref="SetUser"/> writes (wiki 04, section 6.2a, generated from here and
/// held equal by RedisAccessTests); each host signs in as its own user (Redis:Username and Redis:Password from its own secret).
/// <para>
/// Derived from every Redis use of each host: the StackExchange.Redis connection itself (its handshake and the readiness ping;
/// since the ARV-083b review without CONFIG, a configuration channel or a tie-breaker key, <see cref="RedisSettings.Options"/>),
/// the FusionCache distributed level (Microsoft's RedisCache, under <see cref="RedisNames.CacheKeyPrefix"/>) and its backplane
/// (<see cref="RedisNames.BackplaneChannelPattern"/>) in every host
/// (sessions, permissions, site scopes and topology are cached and evicted everywhere), and the live keys and channels
/// (<see cref="RedisNames"/>): Ariva.Api.Stream writes the zone snapshots and announces them and the alert notices;
/// Ariva.Api.Main reads the snapshots (the live hub, the displays), listens on both live channels, announces alert notices
/// (an acknowledgement), holds the relay's lease and runs the hub's SignalR backplane; Ariva.Api.Cronz reads the snapshots (the
/// availability ledger). Ariva.Api.Ingest and Ariva.Api.Integration use the cache alone. RedisAclTests runs each host's paths as
/// its user on a Redis with these users and finds no refusal in the ACL log, and shows each user refused what it is not given.
/// </para>
/// Redis channel rules cannot tell a publish from a subscribe: a host given a channel may do both.
/// <para>
/// Residual, recorded by the ARV-083b review (CWE-269; ARV-083f): every host's user reads and writes the whole cache
/// (<c>ariva:cache:*</c>), which holds security decisions every host trusts: the session snapshots (SessionValidator), the
/// accounts' roles (StoredPermissionResolver), the site scopes (SiteScope) and the device credential records (SvcDeviceGateway).
/// A compromised Ariva.Api.Ingest or Ariva.Api.Integration could write an entry that Ariva.Api.Main then reads as a user's
/// administrator rights, past the database split of ARV-083a. Not a regression (before ARV-083b every host shared one user); the
/// cure, per-concern prefixes that Main alone writes or entries authenticated by their writer, is ARV-083f.
/// </para>
/// A new Redis key, channel or command a host uses adds its grant here, in the same change.
/// </summary>
internal static class RedisAccess
{
    #region Types

    /// <summary>How a key pattern is granted: read only (<c>%R~</c>), write only (<c>%W~</c>) or both (<c>~</c>; Redis 7 and later).</summary>
    public enum KeyAccess
    {
        Read,
        Write,
        ReadWrite
    }

    /// <summary>A key pattern and how it is granted.</summary>
    public sealed record KeyGrant(string Pattern, KeyAccess Access)
    {
        /// <summary>The rule as ACL SETUSER takes it.</summary>
        public string Rule => Access switch
        {
            KeyAccess.Read => $"%R~{Pattern}",
            KeyAccess.Write => $"%W~{Pattern}",
            _ => $"~{Pattern}"
        };
    }

    /// <summary>One host's user: the host (its Kafka:ServiceName), its commands, its key patterns and its channel patterns.</summary>
    public sealed record HostGrant(string Service, IReadOnlyList<string> Commands, IReadOnlyList<KeyGrant> Keys, IReadOnlyList<string> Channels);

    #endregion

    #region Constants

    /// <summary>The hosts, by their Kafka:ServiceName (the chart's host keys).</summary>
    public static readonly IReadOnlyList<string> Hosts = ["main", "ingest", "stream", "cronz", "integration"];

    /// <summary>
    /// StackExchange.Redis 2.10's own, on every connection, as measured on Redis 8 (RedisAclTests): the sign-in, the handshake (the
    /// client's name, library and id, the server's version and cluster state, an echo, the database) and the readiness ping, with
    /// SUBSCRIBE and UNSUBSCRIBE for the backplane. Never CONFIG (it reads the default user's password, the replication password
    /// and the TLS key passphrases: <see cref="RedisSettings.Options"/> takes it out of the client's command map), and no
    /// configuration channel or tie-breaker key, so no GET outside a host's own keys. AUTH and QUIT are not checked against the
    /// ACL; AUTH is listed for a reader.
    /// </summary>
    private static readonly string[] ConnectionCommands =
    [
        "auth", "client|setname", "client|setinfo", "client|id", "info", "cluster|nodes", "echo", "select", "ping", "subscribe", "unsubscribe"
    ];

    /// <summary>
    /// The FusionCache distributed level as Microsoft's RedisCache 10 keeps it (a hash per entry, HMSET and EXPIRE, read with HMGET,
    /// removed with UNLINK) and the backplane's announcements (PUBLISH; its listening is SUBSCRIBE above).
    /// </summary>
    private static readonly string[] CacheCommands = ["hmget", "hmset", "expire", "unlink", "publish"];

    #endregion

    #region Grants

    /// <summary>Each host's user for the deployment's instance name (<c>ariva:</c>) and environment (the backplane's channels).</summary>
    public static IReadOnlyList<HostGrant> All(string instance, string environment) => [.. Hosts.Select(h => For(h, instance, environment))];

    /// <summary>The user of <paramref name="service"/> (main, ingest, stream, cronz or integration).</summary>
    public static HostGrant For(string service, string instance, string environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        ArgumentException.ThrowIfNullOrEmpty(instance);
        ArgumentException.ThrowIfNullOrEmpty(environment);
        var commands = new List<string>(ConnectionCommands);
        commands.AddRange(CacheCommands);
        var keys = new List<KeyGrant> { new(RedisNames.CacheKeyPrefix(instance) + "*", KeyAccess.ReadWrite) };
        var channels = new List<string> { RedisNames.BackplaneChannelPattern(instance, environment) };
        var liveZones = RedisNames.LiveZoneKeyPrefix(instance) + "*";
        switch (service)
        {
            case "main":
                // The live hub and the displays read the snapshots (GET); the relay listens on both live channels and holds its
                // lease (one script, loaded and run: SET NX PX, GET, PEXPIRE); an acknowledged alert is announced; the hub's
                // backplane is its own.
                commands.AddRange(["get", "set", "pexpire", "eval", "evalsha", "script|load"]);
                keys.Add(new(liveZones, KeyAccess.Read));
                keys.Add(new(RedisNames.LiveRelayKey(instance), KeyAccess.ReadWrite));
                channels.Add(RedisNames.LiveZonesChannel(instance));
                channels.Add(RedisNames.LiveAlertsChannel(instance));
                channels.Add(RedisNames.SignalRChannelPrefix(instance) + "*");
                break;
            case "stream":
                // The queue engine writes each zone's snapshot (SETEX, kept a day) and announces it and the alert notices it raises.
                commands.Add("setex");
                keys.Add(new(liveZones, KeyAccess.Write));
                channels.Add(RedisNames.LiveZonesChannel(instance));
                channels.Add(RedisNames.LiveAlertsChannel(instance));
                break;
            case "cronz":
                // The availability ledger reads the snapshots.
                commands.Add("get");
                keys.Add(new(liveZones, KeyAccess.Read));
                break;
            case "ingest":
            case "integration":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(service), service, "Not an Ariva host.");
        }

        return new HostGrant(service, [.. commands.Distinct(StringComparer.Ordinal)], keys, channels);
    }

    #endregion

    #region Listing

    /// <summary>
    /// The ACL SETUSER line of <paramref name="grant"/> for <paramref name="user"/> with a SHA-256 password hash
    /// (<paramref name="passwordHash"/>, written as <c>#hash</c> so the password itself is never on a command line): every rule
    /// reset first, the user on, no command but its own, its keys and its channels only.
    /// </summary>
    public static string SetUser(HostGrant grant, string user, string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrEmpty(user);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        var line = new StringBuilder($"ACL SETUSER {user} reset on #{passwordHash} resetchannels -@all");
        foreach (var command in grant.Commands)
            line.Append(" +").Append(command);
        foreach (var key in grant.Keys)
            line.Append(' ').Append(key.Rule);
        foreach (var channel in grant.Channels)
            line.Append(" &").Append(channel);
        return line.ToString();
    }

    #endregion
}
