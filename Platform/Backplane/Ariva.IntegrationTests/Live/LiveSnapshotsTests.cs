using System.Collections.Concurrent;
using Ariva.Infra.Caching;
using Ariva.Infra.Live;
using Ariva.IntegrationTests.Caching;
using FluentAssertions;

namespace Ariva.IntegrationTests.Live;

/// <summary>
/// ARV-035 on a real Redis: Stream's snapshots are kept per zone and announced; a subscriber (the live hub's relay)
/// receives each one, and what does not pass the checks is neither kept nor announced.
/// </summary>
public sealed class LiveSnapshotsTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LiveZoneSnapshot Visitors = new("DMO/A-VIS", new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc), 61, true, false, 15.4, 4.0, null,
        false, new DateTime(2026, 9, 28, 18, 5, 2, DateTimeKind.Utc));

    [Fact]
    public async Task Snapshots_Should_BeKeptAndAnnounced_When_StreamPublishesThem()
    {
        var settings = new RedisSettings { Enabled = true, ConnectionString = redis.ConnectionString, InstanceName = "it-live:" };
        await using var writerConnection = new RedisConnection(settings);
        await using var readerConnection = new RedisConnection(settings);
        var writer = new RedisLiveSnapshots(writerConnection);
        var reader = new RedisLiveSnapshots(readerConnection);
        var received = new ConcurrentQueue<LiveZoneSnapshot>();
        using var stop = new CancellationTokenSource();
        await reader.SubscribeAsync(s =>
        {
            received.Enqueue(s);
            return Task.CompletedTask;
        }, stop.Token);

        await writer.PublishAsync([Visitors, Visitors with { ZoneKey = "DMO/A-CIT", QueueLength = 12 }, Visitors with { ZoneKey = "bogus", QueueLength = 1 }], Ct);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (received.Count < 2 && DateTime.UtcNow < until)
            await Task.Delay(50, Ct);

        received.Select(r => r.ZoneKey).Should().BeEquivalentTo(["DMO/A-VIS", "DMO/A-CIT"], "the implausible one is not announced");
        (await reader.GetAsync("DMO/A-VIS", Ct)).Should().Be(Visitors);
        (await reader.GetAsync("DMO/none", Ct)).Should().BeNull();
        (await reader.GetAsync("bogus", Ct)).Should().BeNull();
        await stop.CancelAsync();
    }
}
