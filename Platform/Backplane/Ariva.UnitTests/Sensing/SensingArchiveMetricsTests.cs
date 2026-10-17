using System.Diagnostics.Metrics;
using Ariva.Core.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Infra.Settings;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-159: the archive's skips are counted, not only logged. A sensing batch whose queue zone name breaks the display text
/// rule is skipped whole, an event out of the canonical bounds alone, a device health report out of bounds alone; each is
/// acknowledged and never archived, and <c>ariva.sensing.archive_skipped</c> counts it by kind. Nothing here reaches a
/// database: a write with nothing left to archive returns before it connects. Invisible characters are escapes.
/// </summary>
public sealed class SensingArchiveMetricsTests
{
    private static readonly DateTime At = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static ManualClock Clock() => new(new DateTimeOffset(At.AddMinutes(1)));

    /// <summary>Listens to this instance's meter alone, so other tests' archives running in parallel are not counted.</summary>
    private static (MeterListener Listener, List<(string Kind, long Value)> Measured) Listen(SensingArchiveMetrics metrics)
    {
        var measured = new List<(string Kind, long Value)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter) && instrument.Name == SensingArchiveMetrics.SkippedName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "kind")
                {
                    lock (measured)
                        measured.Add(((string)tag.Value, value));
                }
            }
        });
        listener.Start();
        return (listener, measured);
    }

    private static ZoneOccupancyBatch Occupancy(string zone, params ZoneOccupancy[] events) => new()
    {
        DeviceId = Guid.Parse("0199a000-0000-7000-8000-0000000d0159"),
        DeviceCode = "S-59",
        SiteCode = "DMO",
        QueueZoneName = zone,
        Dialect = "Canonical",
        Commissioned = true,
        ReceivedUtc = At,
        Clock = new ClockReading(0, true, ClockState.Ok),
        Occupancy = [.. events.Select(e => new Sensed<ZoneOccupancy>(e, e.TimeUtc, SensedFlags.None))]
    };

    private static DeviceHealthReported Report(string zone) => new()
    {
        DeviceId = Guid.Parse("0199a000-0000-7000-8000-0000000d0159"),
        DeviceCode = "S-59",
        SiteCode = "DMO",
        QueueZoneName = zone,
        Status = new DeviceStatus(true, 40, 10, 0, At),
        Clock = new ClockReading(0, true, ClockState.Ok),
        ReceivedUtc = At,
        Commissioned = true
    };

    [Fact]
    public async Task SensingArchive_Should_CountEachSkippedBatchAndEvent_When_TheyBreakTheRules()
    {
        using var metrics = new SensingArchiveMetrics();
        var (listener, measured) = Listen(metrics);
        using (listener)
        {
            var archive = new SensingArchive(new DatabaseSettings(), Clock(), metrics: metrics);

            var written = await archive.WriteAsync(
            [
                Occupancy("Snake \x202EA", new ZoneOccupancy("A-VIS", 10, At)),
                Occupancy("Snake \U000E0041", new ZoneOccupancy("A-VIS", 10, At)),
                Occupancy("Snake A", new ZoneOccupancy("A-VIS\U000E0041", 10, At), new ZoneOccupancy("A-VIS", -1, At))
            ], TestContext.Current.CancellationToken);

            written.Events.Should().Be(0);
            measured.Should().BeEquivalentTo(new[] { (SensingArchiveMetrics.BatchKind, 2L), (SensingArchiveMetrics.EventKind, 2L) });
        }
    }

    [Fact]
    public async Task DeviceHealthArchive_Should_CountEachReportOutOfBounds_When_ItsQueueZoneNameBreaksTheRule()
    {
        using var metrics = new SensingArchiveMetrics();
        var (listener, measured) = Listen(metrics);
        using (listener)
        {
            var archive = new DeviceHealthArchive(new DatabaseSettings(), Clock(), metrics: metrics);

            var written = await archive.WriteAsync([Report("Snake \x202EA"), Report("Snake \U000E0041"), Report("Snake\x200BA")], TestContext.Current.CancellationToken);

            written.Should().Be(0);
            measured.Should().BeEquivalentTo(new[] { (SensingArchiveMetrics.HealthReportKind, 3L) });
        }
    }

    [Fact]
    public void Skipped_Should_RecordNothing_When_TheCountIsZero()
    {
        using var metrics = new SensingArchiveMetrics();
        var (listener, measured) = Listen(metrics);
        using (listener)
        {
            metrics.Skipped(SensingArchiveMetrics.BatchKind, 0);
            metrics.Skipped(SensingArchiveMetrics.HealthReportKind, 2);

            measured.Should().Equal((SensingArchiveMetrics.HealthReportKind, 2L));
        }
    }
}
