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
    [Fact]
    public async Task AlertNotices_Should_BeAnnouncedAndCheckedOnTheWayBack_When_PublishedAndForged()
    {
        var settings = new RedisSettings { Enabled = true, ConnectionString = redis.ConnectionString, InstanceName = "it-alerts:" };
        await using var writerConnection = new RedisConnection(settings);
        await using var readerConnection = new RedisConnection(settings);
        var writer = new RedisAlertNotices(writerConnection);
        var reader = new RedisAlertNotices(readerConnection);
        var received = new ConcurrentQueue<AlertNotice>();
        using var stop = new CancellationTokenSource();
        await reader.SubscribeAsync(n =>
        {
            received.Enqueue(n);
            return Task.CompletedTask;
        }, stop.Token);
        var notice = new AlertNotice(Guid.NewGuid(), "DMO", "R-001", "Nowcast above 15 min", "A-VIS", null, "Nowcast", "Critical", "Raised",
            new DateTime(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc), 16.3, "BorderShiftSupervisor", "TerminalDutyManager", false, null, DateTime.UtcNow);

        await writer.PublishAsync([notice, notice with { OwnerRole = "Root" }], Ct);
        // Someone else on the shared Redis publishes straight to the channel: oversized, unreadable and implausible notices.
        var raw = (await writerConnection.GetAsync()).GetSubscriber();
        var channel = StackExchange.Redis.RedisChannel.Literal("it-alerts:live:alerts");
        await raw.PublishAsync(channel, new string('x', RedisAlertNotices.MaxBytes + 1));
        await raw.PublishAsync(channel, "{not json");
        await raw.PublishAsync(channel, System.Text.Json.JsonSerializer.Serialize(notice with { State = "Raised,Resolved" }, Ariva.Infra.Messaging.EventCatalog.Json));
        await raw.PublishAsync(channel, System.Text.Json.JsonSerializer.Serialize(notice with { RuleCode = "R-002" }, Ariva.Infra.Messaging.EventCatalog.Json));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (received.Count < 2 && DateTime.UtcNow < until)
            await Task.Delay(50, Ct);
        await Task.Delay(300, Ct);

        received.Select(r => r.RuleCode).Should().Equal(["R-001", "R-002"], "only plausible notices reach the hub (CWE-501)");
        await stop.CancelAsync();
    }
}
