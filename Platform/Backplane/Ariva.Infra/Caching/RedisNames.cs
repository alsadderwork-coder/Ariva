namespace Ariva.Infra.Caching;

/// <summary>
/// Every Redis key and channel name Ariva uses (ARV-083b), each under <see cref="RedisSettings.InstanceName"/> (<c>ariva:</c>):
/// the code builds its names here, and each host's Redis ACL user (<see cref="RedisAccess"/>) is granted these names alone, so a
/// name added or moved without its grant fails the unit test (RedisAccessTests) and the ACL integration test (RedisAclTests).
/// <list type="bullet">
/// <item><c>{instance}cache:</c>: the FusionCache distributed level (Microsoft's RedisCache instance name; every key of the
/// level, its entries and its tag expirations, starts with it). Every host reads and writes it.</item>
/// <item><c>{instance}{environment}.Backplane:v2</c>: the FusionCache backplane's channel, on which every host announces and hears evictions.</item>
/// <item><c>{instance}live:zone:{site}/{zone}</c>: the live snapshot of a zone, written by Ariva.Api.Stream and read by
/// Ariva.Api.Main (the live hub and the displays) and Ariva.Api.Cronz (the availability ledger).</item>
/// <item><c>{instance}live:zones</c> and <c>{instance}live:alerts</c>: the live channels; Stream announces snapshots and alert
/// notices, Main announces alert notices too and alone listens.</item>
/// <item><c>{instance}live:relay</c>: the live relay's lease, Main's alone.</item>
/// <item><c>{instance}signalr</c>: the prefix of every channel of the live hub's SignalR backplane, Main's alone.</item>
/// </list>
/// </summary>
public static class RedisNames
{
    #region Constants

    /// <summary>The segment after the instance name that holds the cache's keys.</summary>
    public const string CacheSegment = "cache:";

    /// <summary>The segment after the instance name of every live key and channel.</summary>
    public const string LiveSegment = "live:";

    /// <summary>The segment after the instance name of the live hub's backplane channels.</summary>
    public const string SignalRSegment = "signalr";

    #endregion

    #region Names

    /// <summary>The FusionCache distributed level's key prefix: Microsoft's RedisCache instance name.</summary>
    public static string CacheKeyPrefix(string instance) => $"{instance}{CacheSegment}";

    /// <summary>The FusionCache backplane's channel prefix, one per environment so two environments on one Redis never mix.</summary>
    public static string BackplaneChannelPrefix(string instance, string environment) => $"{instance}{environment}";

    /// <summary>
    /// The FusionCache backplane's channels, as an ACL channel pattern (ARV-083b review): FusionCache 2 names its channel
    /// <c>{prefix}.Backplane:{wire format version}</c> (<c>ariva:k8s-prd.Backplane:v2</c>, shown by RedisAclTests), so the pattern
    /// ends after <c>.Backplane:</c> and never matches another environment's (<c>ariva:k8s-prd2...</c>).
    /// </summary>
    public static string BackplaneChannelPattern(string instance, string environment) => $"{BackplaneChannelPrefix(instance, environment)}.Backplane:*";

    /// <summary>The key of a zone's live snapshot.</summary>
    public static string LiveZoneKey(string instance, string zoneKey) => $"{instance}{LiveSegment}zone:{zoneKey}";

    /// <summary>The prefix of every live snapshot key.</summary>
    public static string LiveZoneKeyPrefix(string instance) => $"{instance}{LiveSegment}zone:";

    /// <summary>The channel that announces each live snapshot.</summary>
    public static string LiveZonesChannel(string instance) => $"{instance}{LiveSegment}zones";

    /// <summary>The channel that announces each alert notice.</summary>
    public static string LiveAlertsChannel(string instance) => $"{instance}{LiveSegment}alerts";

    /// <summary>The live relay's lease (one Ariva.Api.Main replica forwards).</summary>
    public static string LiveRelayKey(string instance) => $"{instance}{LiveSegment}relay";

    /// <summary>The live hub's SignalR backplane channel prefix (StackExchange.Redis's ChannelPrefix, before every channel name).</summary>
    public static string SignalRChannelPrefix(string instance) => $"{instance}{SignalRSegment}";

    #endregion
}
