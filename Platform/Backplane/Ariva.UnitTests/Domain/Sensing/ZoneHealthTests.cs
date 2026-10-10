using System.Diagnostics.Metrics;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.Events;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Sensing;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Sensing;

/// <summary>
/// ARV-025: a queue zone is Unmonitored without commissioned devices, Healthy when all are online and Degraded while any
/// is offline or degraded; only a change of state raises ZoneHealthChanged, keyed by the zone on a compacted topic; the
/// settings and the metrics hold their rules.
/// </summary>
public sealed class ZoneHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 0, 0, ZoneHealthState.Unmonitored)]
    [InlineData(3, 0, 0, ZoneHealthState.Healthy)]
    [InlineData(3, 1, 0, ZoneHealthState.Degraded)]
    [InlineData(3, 0, 1, ZoneHealthState.Degraded)]
    [InlineData(1, 1, 0, ZoneHealthState.Degraded)]
    public void StateFor_Should_FollowTheDevices(int devices, int offline, int degraded, ZoneHealthState expected) =>
        ZoneHealth.StateFor(devices, offline, degraded).Should().Be(expected);

    [Fact]
    public void Assess_Should_RaiseAnEventOnlyWhenTheStateChanges()
    {
        var zone = new ZoneHealth("DMO", "Snake A", Now);
        zone.State.Should().Be(ZoneHealthState.Unmonitored);

        zone.Assess(2, 0, 0, Now).Should().BeTrue();
        zone.Assess(3, 0, 0, Now.AddSeconds(1)).Should().BeFalse("a third online device keeps the zone Healthy");
        zone.Devices.Should().Be(3);
        zone.Assess(3, 1, 0, Now.AddSeconds(2)).Should().BeTrue();
        zone.Assess(3, 1, 1, Now.AddSeconds(3)).Should().BeFalse();
        zone.ChangedOn.Should().Be(Now.AddSeconds(2), "the time of the last change of state");
        zone.Assess(0, 0, 0, Now.AddSeconds(4)).Should().BeTrue();

        var events = zone.DomainEvents.OfType<ZoneHealthChanged>().ToList();
        events.Select(e => (e.PreviousState, e.State)).Should().Equal(("Unmonitored", "Healthy"), ("Healthy", "Degraded"), ("Degraded", "Unmonitored"));
        events.Should().OnlyContain(e => e.GetPartitionKey() == "DMO/Snake A");
        events[1].Should().Match<ZoneHealthChanged>(e => e.Devices == 3 && e.DevicesOffline == 1 && e.DevicesDegraded == 0 && e.OccurredOn == Now.AddSeconds(2));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 0)]
    public void Assess_Should_RefuseImpossibleCounts(int devices, int offline, int degraded)
    {
        var zone = new ZoneHealth("DMO", "Snake A", Now);

        ((Action)(() => zone.Assess(devices, offline, degraded, Now))).Should().Throw<ArgumentOutOfRangeException>();
        zone.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Topic_Should_BeNamedAndCompacted()
    {
        KafkaTopics.FollowsNamingRule(KafkaTopics.DeviceZoneHealth).Should().BeTrue();
        TopicCatalog.Covers(KafkaTopics.DeviceZoneHealth).Should().BeTrue();
        TopicCatalog.ForProvisioning(new TopicDefaults()).Single(t => t.Name == KafkaTopics.DeviceZoneHealth).Compacted.Should().BeTrue("the latest value per zone is its health");
        new EventCatalog([typeof(ZoneHealthChanged).Assembly]).TopicOf(typeof(ZoneHealthChanged)).Should().Be(KafkaTopics.DeviceZoneHealth);
    }

    [Theory]
    [InlineData(180, 15, true)]
    [InlineData(30, 5, true)]
    [InlineData(29, 5, false)]
    [InlineData(3_601, 15, false)]
    [InlineData(60, 4, false)]
    [InlineData(60, 60, false)]
    public void Settings_Should_KeepTheSweepInsideTheTimeout(int timeout, int sweep, bool valid) =>
        new DeviceHealthSettings { HeartbeatTimeoutSeconds = timeout, SweepSeconds = sweep }.IsValid.Should().Be(valid);

    [Fact]
    public void Metrics_Should_CountChangesAndReportTheLastSweep()
    {
        using var metrics = new DeviceHealthMetrics();
        var measured = new List<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DeviceHealthMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measured.Add((instrument.Name, value)));
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => measured.Add((instrument.Name, value)));
        listener.Start();

        metrics.HeartbeatLost("DMO");
        metrics.Recovered("DMO");
        metrics.ZoneDegraded("DMO");
        metrics.Snapshot(2, 1, 1);
        listener.RecordObservableInstruments();

        measured.Should().Contain([("ariva.devices.heartbeat_lost", 1L), ("ariva.devices.recovered", 1L), ("ariva.zones.degraded", 1L),
            ("ariva.devices.offline", 2L), ("ariva.devices.degraded", 1L), ("ariva.zones.degraded_now", 1L)]);
    }

    [Fact]
    public void ZoneNotKeyable_Should_CountEverySkipAndSayFirstOncePerZone_When_ASweepMeetsTheZoneAgain()
    {
        // ARV-114c: the sweep skips a zone whose key cannot be written every 15 seconds; the warning comes once per zone.
        using var metrics = new DeviceHealthMetrics();
        var counted = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DeviceHealthMetrics.MeterName && instrument.Name == "ariva.zones.not_keyable")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => counted += value);
        listener.Start();

        metrics.ZoneNotKeyable("DMO", "DMO/long").Should().BeTrue();
        metrics.ZoneNotKeyable("DMO", "DMO/long").Should().BeFalse();
        metrics.ZoneNotKeyable("DMO", "DMO/other").Should().BeTrue();

        counted.Should().Be(3);
    }
}
