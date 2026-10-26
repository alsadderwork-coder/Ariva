using Ariva.Core.Messaging;
using Ariva.Infra.Messaging.Outbox;
using Ariva.Infra.Streaming;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-125a: the backlogs the system alerts read. <c>ariva.stream.consumer_lag</c> is the records behind the end of each
/// sensing topic for the partitions a Stream instance owns, per topic and group; <c>ariva.outbox.unsent</c> and
/// <c>ariva.outbox.oldest_unsent_age</c> are the outbox rows not yet sent and the age of the oldest. Each reports its last
/// measurement only within its window, so a worker or relay that stopped measuring reads as missing.
/// </summary>
public sealed class BacklogMetricsTests
{
    private const string Group = "ariva-stream.queue-engine";

    [Fact]
    public void LagByTopic_Should_SumThePartitionsBehindTheirEnds_When_TheirPositionsAreKnown()
    {
        var lags = StreamMetrics.LagByTopic(
        [
            (KafkaTopics.DeviceZoneOccupancy, 120, 100),
            (KafkaTopics.DeviceZoneOccupancy, 50, 50),
            (KafkaTopics.DeviceZoneOccupancy, 10, 12), // read past a high watermark fetched before: no negative lag
            (KafkaTopics.DeviceVendorLineCrossing, 7, 0),
            (KafkaTopics.DeviceHealth, -1001, 4),      // the high watermark not known yet: left out
            (KafkaTopics.DeviceTrackSample, 9, -1001)  // nor the position: left out
        ]);

        lags.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [KafkaTopics.DeviceZoneOccupancy] = 20,
            [KafkaTopics.DeviceVendorLineCrossing] = 7
        });
    }

    [Fact]
    public void ConsumerLag_Should_ReportEachTopicWithItsGroup_When_TheWorkerHasMeasured()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var metrics = new StreamMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe(StreamMetrics.ConsumerLagName).Should().BeEmpty("nothing before the first measurement");

        metrics.Report(Group, new Dictionary<string, long> { [KafkaTopics.DeviceZoneOccupancy] = 20, [KafkaTopics.DeviceHealth] = 0 }, TimeSpan.FromSeconds(30));

        reader.Observe(StreamMetrics.ConsumerLagName).Select(m => (m.Tags["topic"], m.Tags["group"], m.Value))
            .Should().BeEquivalentTo([(KafkaTopics.DeviceHealth, Group, 0d), (KafkaTopics.DeviceZoneOccupancy, Group, 20d)]);

        clock.Advance(TimeSpan.FromSeconds(31));
        reader.Observe(StreamMetrics.ConsumerLagName).Should().BeEmpty("a worker that stopped measuring (stuck or stopped) reads as missing");

        metrics.Report(Group, new Dictionary<string, long> { [KafkaTopics.DeviceZoneOccupancy] = 3 }, TimeSpan.FromSeconds(30));
        reader.Observe(StreamMetrics.ConsumerLagName).Should().ContainSingle().Which.Value.Should().Be(3);
        metrics.Clear();
        reader.Observe(StreamMetrics.ConsumerLagName).Should().BeEmpty("the worker stopped");
    }

    [Fact]
    public void Outbox_Should_ReportTheUnsentRowsAndTheOldestAge_When_ItWasMeasured()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        using var metrics = new OutboxMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        reader.Observe(OutboxMetrics.UnsentName).Should().BeEmpty("nothing before the first measurement");

        metrics.Report(new OutboxBacklog(42, new DateTime(2026, 10, 14, 11, 55, 0, DateTimeKind.Utc)), TimeSpan.FromSeconds(90));

        var unsent = reader.Observe(OutboxMetrics.UnsentName).Should().ContainSingle().Subject;
        unsent.Value.Should().Be(42);
        unsent.Tags.Should().BeEmpty("the outbox's figures carry no tag");
        reader.Observe(OutboxMetrics.OldestUnsentAgeName).Should().ContainSingle().Which.Value.Should().Be(300, "the oldest row was five minutes old when measured");

        clock.Advance(TimeSpan.FromSeconds(60));
        reader.Observe(OutboxMetrics.OldestUnsentAgeName).Should().ContainSingle().Which.Value.Should().Be(300, "the age as measured, not extrapolated");
        clock.Advance(TimeSpan.FromSeconds(31));
        reader.Observe(OutboxMetrics.UnsentName).Should().BeEmpty("a relay that cannot measure (the database away) reports nothing");
        reader.Observe(OutboxMetrics.OldestUnsentAgeName).Should().BeEmpty();

        metrics.Report(new OutboxBacklog(0, null), TimeSpan.FromSeconds(90));
        reader.Observe(OutboxMetrics.UnsentName).Should().ContainSingle().Which.Value.Should().Be(0);
        reader.Observe(OutboxMetrics.OldestUnsentAgeName).Should().ContainSingle().Which.Value.Should().Be(0, "an empty outbox has no oldest row");
    }

    /// <summary>A transport that is never ready, counting how often a pass asked (each pass asks once, first).</summary>
    private sealed class NotReadyTransport : IOutboxTransport
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public bool IsReady
        {
            get
            {
                Interlocked.Increment(ref _asked);
                return false;
            }
        }

        public Task SendAsync(OutboxEnvelope message, CancellationToken ct) => throw new InvalidOperationException("Not ready.");
    }

    private static OutboxRelay RelayWith(IOutboxTransport transport, OutboxMetrics metrics, Func<CancellationToken, Task<OutboxBacklog>> measure,
        TimeSpan? interval = null) =>
        new(new Ariva.Infra.Settings.DatabaseSettings(), new Ariva.Infra.Messaging.KafkaSettings { Outbox = new Ariva.Infra.Messaging.OutboxRelaySettings { PollMilliseconds = 20 } },
            transport, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxRelay>.Instance, metrics)
        {
            MeasureBacklog = measure,
            MeasureInterval = interval ?? OutboxRelay.MeasureEvery
        };

    /// <summary>
    /// ARV-179 (the ARV-125a review's finding 2): the backlog is measured on a loop of its own, so a measurement that does not
    /// end (a slow count in a large backlog, here one that never answers) never delays a relay pass. Before, the relay
    /// awaited the measurement before its pass, so this relay would have run no pass at all.
    /// </summary>
    [Fact]
    public async Task Relay_Should_GoOnWithItsPasses_When_TheBacklogMeasurementDoesNotEnd()
    {
        var transport = new NotReadyTransport();
        var measuring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var metrics = new OutboxMetrics();
        var relay = RelayWith(transport, metrics, async ct =>
        {
            measuring.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new OutboxBacklog(0, null);
        });

        await relay.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await measuring.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var before = transport.Asked; // the passes counted from the measurement's start, not the relay's
            var until = DateTime.UtcNow.AddSeconds(10);
            while (transport.Asked - before < 10 && DateTime.UtcNow < until)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            (transport.Asked - before).Should().BeGreaterThanOrEqualTo(10, "the passes go on while the measurement has not ended");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await relay.StopAsync(stop.Token);
        }

        relay.ExecuteTask.Should().NotBeNull();
        relay.ExecuteTask.IsCompleted.Should().BeTrue("the stop ends the measurement too");
    }

    /// <summary>
    /// ARV-179 (its review's Low 1): a measurement that does not take its token holds the relay's stop at most
    /// <see cref="OutboxRelay.MeasureStopBound"/>, so it never eats into the time the host's later services have to stop.
    /// Before, the stop waited for the measurement until the host's own shutdown timeout.
    /// </summary>
    [Fact]
    public async Task Relay_Should_StopWithinItsBound_When_TheMeasurementIgnoresItsToken()
    {
        var measuring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var metrics = new OutboxMetrics();
        var relay = RelayWith(new NotReadyTransport(), metrics, async _ =>
        {
            measuring.TrySetResult();
            await released.Task; // never the token
            return new OutboxBacklog(0, null);
        });

        await relay.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await measuring.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await relay.StopAsync(stop.Token);
            watch.Stop();

            relay.ExecuteTask.IsCompleted.Should().BeTrue("the relay's own task ends at the bound, not when the measurement does");
            watch.Elapsed.Should().BeLessThan(OutboxRelay.MeasureStopBound + TimeSpan.FromSeconds(3));
        }
        finally
        {
            released.TrySetResult();
        }
    }

    /// <summary>
    /// ARV-179 (its review's Low 2): a measurement that fails (the database away) leaves the loop measuring, and the next
    /// one is reported. Were the loop to end at a failure, the host would report no backlog for the rest of its life.
    /// </summary>
    [Fact]
    public async Task Relay_Should_GoOnMeasuring_When_AMeasurementFails()
    {
        var calls = 0;
        using var metrics = new OutboxMetrics(timeProvider: new ManualClock(DateTimeOffset.UtcNow));
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = RelayWith(new NotReadyTransport(), metrics, _ => Interlocked.Increment(ref calls) == 1
            ? Task.FromException<OutboxBacklog>(new TimeoutException("The database is away."))
            : Task.FromResult(new OutboxBacklog(7, null)), TimeSpan.FromMilliseconds(50));

        await relay.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (reader.Observe(OutboxMetrics.UnsentName).Count == 0 && DateTime.UtcNow < until)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            reader.Observe(OutboxMetrics.UnsentName).Should().ContainSingle().Which.Value.Should().Be(7, "the measurement after the failed one is reported");
            Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await relay.StopAsync(stop.Token);
        }
    }
}
