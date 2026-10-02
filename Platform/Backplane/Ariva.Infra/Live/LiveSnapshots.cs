using System.Text.Json;
using System.Text.RegularExpressions;
using Ariva.Core.Queueing;
using Ariva.Infra.Caching;
using Ariva.Infra.Messaging;
using StackExchange.Redis;

namespace Ariva.Infra.Live;

/// <summary>
/// A queue zone's live state as screens see it (ARV-035): the latest minute's queue length and nowcast, written by
/// Ariva.Api.Stream after each checkpoint and pushed by Ariva.Api.Main's live hub to the zone's group. No identities.
/// </summary>
public sealed record LiveZoneSnapshot(
    string ZoneKey,
    DateTime MinuteUtc,
    int QueueLength,
    bool LengthFromSensors,
    bool LengthDegraded,
    double? NowcastMinutes,
    double? ThroughputPerMinute,
    string NoService,
    bool NowcastDegraded,
    DateTime PublishedUtc)
{
    public static LiveZoneSnapshot From(QueueLiveMinute live, DateTime publishedUtc)
    {
        ArgumentNullException.ThrowIfNull(live);
        return new LiveZoneSnapshot(live.ZoneKey, live.MinuteUtc, live.QueueLength, live.LengthMeasured, live.LengthDegraded, live.NowcastMinutes,
            live.Throughput, live.NoService?.ToString(), live.NowcastDegraded, publishedUtc);
    }

    /// <summary>The site part of the zone key (<c>&lt;site&gt;/&lt;queue zone name&gt;</c>).</summary>
    public string SiteCode => LiveZones.SiteOf(ZoneKey);
}

/// <summary>Zone keys as the live hub accepts them, and the checks on a snapshot read back from Redis (CWE-501).</summary>
public static partial class LiveZones
{
    /// <summary>A site code (as topology allows) and a queue zone name of printable characters, at most 220 in all.</summary>
    public static bool IsZoneKey(string value) => value is { Length: > 3 and <= 220 } && ZoneKeyRule().IsMatch(value);

    public static string SiteOf(string zoneKey) => zoneKey is null ? null : zoneKey[..Math.Max(0, zoneKey.IndexOf('/', StringComparison.Ordinal))];

    /// <summary>The queue zone name part of the zone key.</summary>
    public static string NameOf(string zoneKey) => zoneKey is null ? null : zoneKey[(zoneKey.IndexOf('/', StringComparison.Ordinal) + 1)..];

    /// <summary>Whether a snapshot from storage is plausible enough to send to a screen.</summary>
    public static bool Plausible(LiveZoneSnapshot s) =>
        s is not null && IsZoneKey(s.ZoneKey) && s.QueueLength is >= 0 and <= 1_000_000 &&
        (s.NowcastMinutes is null || (double.IsFinite(s.NowcastMinutes.Value) && s.NowcastMinutes >= 0 && s.NowcastMinutes <= 100_000)) &&
        (s.ThroughputPerMinute is null || (double.IsFinite(s.ThroughputPerMinute.Value) && s.ThroughputPerMinute >= 0)) &&
        (s.NoService is null || (s.NoService.Length <= 32 && Enum.TryParse<NoServiceReason>(s.NoService, out _))) &&
        s.MinuteUtc.Year is >= 2000 and < 9000 && s.PublishedUtc.Year is >= 2000 and < 9000;

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9-]{0,16}/[^\u0000-\u001F/][^\u0000-\u001F]{0,199}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ZoneKeyRule();
}

/// <summary>Where live snapshots are kept and announced.</summary>
public interface ILiveSnapshotStore
{
    /// <summary>Writes each zone's latest snapshot and announces it to the hubs.</summary>
    Task PublishAsync(IReadOnlyCollection<LiveZoneSnapshot> snapshots, CancellationToken ct);

    /// <summary>A zone's latest snapshot, or null.</summary>
    Task<LiveZoneSnapshot> GetAsync(string zoneKey, CancellationToken ct);

    /// <summary>Calls <paramref name="handler"/> for every snapshot announced from now on.</summary>
    Task SubscribeAsync(Func<LiveZoneSnapshot, Task> handler, CancellationToken ct);
}

/// <summary>No Redis configured: nothing is kept, nothing announced (development without the live hub).</summary>
public sealed class NoLiveSnapshots : ILiveSnapshotStore
{
    public Task PublishAsync(IReadOnlyCollection<LiveZoneSnapshot> snapshots, CancellationToken ct) => Task.CompletedTask;

    public Task<LiveZoneSnapshot> GetAsync(string zoneKey, CancellationToken ct) => Task.FromResult<LiveZoneSnapshot>(null);

    public Task SubscribeAsync(Func<LiveZoneSnapshot, Task> handler, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Live snapshots in Redis: one key per zone (<c>{instance}live:zone:{zoneKey}</c>, kept a day) and an announcement on
/// the channel <c>{instance}live:zones</c> carrying the same snapshot. Values are JSON of at most 4 KB; anything read
/// back that is larger, unreadable or implausible is dropped (CWE-501: Redis is shared infrastructure).
/// </summary>
public sealed class RedisLiveSnapshots(RedisConnection redis) : ILiveSnapshotStore
{
    public const int MaxBytes = 4 * 1024;
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    private string Key(string zoneKey) => $"{redis.Settings.InstanceName}live:zone:{zoneKey}";

    private RedisChannel Channel => RedisChannel.Literal($"{redis.Settings.InstanceName}live:zones");

    public async Task PublishAsync(IReadOnlyCollection<LiveZoneSnapshot> snapshots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
            return;
        var connection = await redis.GetAsync();
        var database = connection.GetDatabase();
        var batch = database.CreateBatch();
        var pending = new List<Task>();
        foreach (var snapshot in snapshots.Where(LiveZones.Plausible))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, EventCatalog.Json);
            if (json.Length > MaxBytes)
                continue;
            pending.Add(batch.StringSetAsync(Key(snapshot.ZoneKey), json, Keep));
            pending.Add(batch.PublishAsync(Channel, json));
        }

        batch.Execute();
        await Task.WhenAll(pending).WaitAsync(ct);
    }

    public async Task<LiveZoneSnapshot> GetAsync(string zoneKey, CancellationToken ct)
    {
        if (!LiveZones.IsZoneKey(zoneKey))
            return null;
        var connection = await redis.GetAsync();
        var value = await connection.GetDatabase().StringGetAsync(Key(zoneKey)).WaitAsync(ct);
        var snapshot = Read(value);
        return snapshot is not null && string.Equals(snapshot.ZoneKey, zoneKey, StringComparison.Ordinal) ? snapshot : null;
    }

    public async Task SubscribeAsync(Func<LiveZoneSnapshot, Task> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var connection = await redis.GetAsync();
        var queue = await connection.GetSubscriber().SubscribeAsync(Channel);
        queue.OnMessage(async message =>
        {
            if (Read(message.Message) is { } snapshot)
                await handler(snapshot);
        });
        ct.Register(() => queue.Unsubscribe());
    }

    internal static LiveZoneSnapshot Read(RedisValue value)
    {
        if (value.IsNullOrEmpty)
            return null;
        byte[] bytes = value;
        if (bytes is null || bytes.Length > MaxBytes)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<LiveZoneSnapshot>(bytes, EventCatalog.Json);
            return LiveZones.Plausible(snapshot) ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
