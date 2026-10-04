using System.Collections.Concurrent;
using System.Diagnostics;
using Ariva.Infra.Caching;
using Ariva.Infra.Live;
using Ariva.Infra.Resilience;
using FluentAssertions;
using StackExchange.Redis;

namespace Ariva.IntegrationTests.Faults;

/// <summary>
/// ARV-072, Redis lost and stalled under the live view. Ariva.Api.Stream's checkpoint publishes each zone's snapshot
/// (kept and announced); the live hub's relay is subscribed; the display board reads the kept snapshot. While Redis is
/// gone: a publish fails within the client's timeout with an exception Stream's checkpoint catches (it logs and goes on,
/// the minute is already in the database), and a read fails as a dependency outage, so the board answers 503 with
/// Retry-After and the player keeps its last board until it is stale. Once Redis is back the subscription is restored
/// without a restart and new snapshots reach the hub again; the snapshot kept from before the loss carries its old
/// publication time, which is what turns screens and boards stale (150 seconds on screens, the display's threshold on boards).
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class RedisLossTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "redis";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LiveZoneSnapshot Snapshot(int queue) =>
        new("FLT/Hall", DateTime.UtcNow.AddMinutes(-1), queue, true, false, 7.5, 4.0, null, false, DateTime.UtcNow);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await faults.RestoreAsync(Proxy);

    private RedisConnection Connection(string instance) =>
        new(new RedisSettings { Enabled = true, ConnectionString = faults.RedisConnectionString, InstanceName = instance });

    /// <summary>Stream's checkpoint catches these and carries on (QueueStreamWorker.Checkpoint).</summary>
    private static bool StreamCarriesOn(Exception e) => e is RedisException or TimeoutException;

    [Fact]
    public async Task LiveView_Should_FailFastWhileRedisIsCutOffAndRecoverWithoutRestart_When_RedisComesBack()
    {
        const string instance = "it-faults-cut:";
        await using var writerConnection = Connection(instance);
        await using var readerConnection = Connection(instance);
        var writer = new RedisLiveSnapshots(writerConnection);
        var reader = new RedisLiveSnapshots(readerConnection);
        var received = new ConcurrentQueue<LiveZoneSnapshot>();
        using var stop = new CancellationTokenSource();
        await reader.SubscribeAsync(s =>
        {
            received.Enqueue(s);
            return Task.CompletedTask;
        }, stop.Token);
        var before = Snapshot(40);
        await writer.PublishAsync([before], Ct);
        await WaitForAsync(() => received.Any(r => r.QueueLength == 40), TimeSpan.FromSeconds(10));

        await faults.CutAsync(Proxy);

        var clock = Stopwatch.StartNew();
        var publish = await Record(() => writer.PublishAsync([Snapshot(41)], Ct));
        publish.Should().NotBeNull("a publish cannot succeed without Redis");
        StreamCarriesOn(publish).Should().BeTrue($"Stream's checkpoint must catch it, not stop the worker ({publish!.GetType().Name})");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the checkpoint waits at most the client's timeout");
        var read = await Record(() => reader.GetAsync(before.ZoneKey, Ct));
        read.Should().NotBeNull();
        DependencyOutage.Is(read!).Should().BeTrue($"the board and the hub answer 503 with Retry-After, not 500 ({read.GetType().Name})");

        await faults.RestoreAsync(Proxy);

        // No restart: the multiplexer reconnects and resubscribes; the next checkpoint's snapshot reaches the hub.
        await WaitForAsync(async () =>
        {
            if (await Record(() => writer.PublishAsync([Snapshot(42)], Ct)) is not null)
                return false;
            await Task.Delay(500, Ct);
            return received.Any(r => r.QueueLength == 42);
        }, TimeSpan.FromSeconds(90));
        (await reader.GetAsync(before.ZoneKey, Ct))!.QueueLength.Should().Be(42);
        received.Should().NotContain(r => r.QueueLength == 41, "the failed publish was not retried behind Stream's back");
        await stop.CancelAsync();
    }

    [Fact]
    public async Task LiveView_Should_TimeOutRatherThanHang_When_RedisStalls()
    {
        const string instance = "it-faults-stall:";
        await using var connection = Connection(instance);
        var live = new RedisLiveSnapshots(connection);
        var kept = Snapshot(30) with { PublishedUtc = DateTime.UtcNow };
        await live.PublishAsync([kept], Ct);

        await faults.StallAsync(Proxy);

        var clock = Stopwatch.StartNew();
        var publish = await Record(() => live.PublishAsync([Snapshot(31)], Ct));
        var read = await Record(() => live.GetAsync(kept.ZoneKey, Ct));
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25), "both calls end at the client's timeout, not when Redis answers");
        StreamCarriesOn(publish!).Should().BeTrue(publish?.GetType().Name);
        DependencyOutage.Is(read!).Should().BeTrue(read?.GetType().Name);

        await faults.RestoreAsync(Proxy);

        // Back: the kept snapshot is the last one written before the stall, with its own (older) publication time, so the
        // screens and boards age it into stale rather than showing it as current.
        LiveZoneSnapshot after = null;
        await WaitForAsync(async () => (after = await TryAsync(() => live.GetAsync(kept.ZoneKey, Ct))) is not null, TimeSpan.FromSeconds(90));
        after!.PublishedUtc.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(-1));
        after.QueueLength.Should().BeOneOf(30, 31);
    }

    private static async Task<Exception> Record(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !Ct.IsCancellationRequested)
        {
            return e;
        }
    }

    private static async Task<T> TryAsync<T>(Func<Task<T>> action) where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            return null;
        }
    }

    private static Task WaitForAsync(Func<bool> condition, TimeSpan timeout) => WaitForAsync(() => Task.FromResult(condition()), timeout);

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"not within {timeout}");
            await Task.Delay(250, Ct);
        }
    }
}
