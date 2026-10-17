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
}
