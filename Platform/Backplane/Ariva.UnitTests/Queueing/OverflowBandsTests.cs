using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-115: overflow band occupancy. Each band's readings are held per minute until the watermark passes the minute, then
/// released with the lowest and highest reading; a minute with any occupancy is an overflow minute (TC-19, Proposed). The
/// band's first occupied minute after an empty spell raises Occupied once, its first empty minute after an occupied spell
/// raises Emptied once; a minute without readings changes nothing while the band's readings are fresh, and a band silent
/// beyond the occupancy freshness window becomes Unknown (the owner's decision of 2026-10-06) until it reports again.
/// Snapshots carry the open minutes and the band states, and a snapshot the engine never wrote is refused (CWE-501, CWE-120).
/// </summary>
public sealed class OverflowBandsTests
{
    #region Helpers

    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    private static readonly QueueZoneGeometry Geometry = new(
        "A-VIS",
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV entry", "A-OV2 entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV", "A-OV2" });

    private static readonly QueueEngineSettings Thirty = new() { Lateness = TimeSpan.FromSeconds(30) };

    private static QueueOccupancy Reading(string zone, int count, double minute) => new(zone, count, At(minute));

    /// <summary>Offers each reading when it happened and advances to <paramref name="until"/>; returns what the bands released.</summary>
    private static (IReadOnlyList<OverflowMinute> Minutes, IReadOnlyList<OverflowChange> Changes) Play(QueueStateEngine engine, OverflowBands bands, double until,
        params QueueInput[] inputs)
    {
        foreach (var input in inputs)
            engine.Offer(input, input.TimeUtc);
        return bands.Accept(engine.Advance(At(until)));
    }

    #endregion

    [Fact]
    public void Accept_Should_KeepEachBandsLowestAndHighestReadingPerMinute_When_TheWatermarkPassesTheMinute()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);

        var (minutes, _) = Play(engine, bands, 3,
            Reading("A-VIS", 50, 0.1),
            Reading("A-OV", 4, 0.2),
            Reading("A-OV", 9, 0.5),
            Reading("A-OV", 2, 0.9),
            Reading("A-OV2", 0, 0.9),
            Reading("A-OV", 0, 1.5),
            Reading("Elsewhere", 7, 1.6));

        minutes.Should().Equal(
            new OverflowMinute("A-OV", At(0), 2, 9),
            new OverflowMinute("A-OV2", At(0), 0, 0),
            new OverflowMinute("A-OV", At(1), 0, 0));
        minutes.Select(m => m.Occupied).Should().Equal(true, false, false);
        bands.Open.Should().Be(0);
    }

    [Fact]
    public void Accept_Should_HoldTheWatermarksMinuteOpen_When_ItHasNotPassedIt()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);

        var first = Play(engine, bands, 1.6, Reading("A-OV", 3, 0.4), Reading("A-OV", 5, 1.0));
        var second = Play(engine, bands, 2.6, Reading("A-OV", 1, 1.3));

        first.Minutes.Should().Equal(new OverflowMinute("A-OV", At(0), 3, 3));
        // A reading late but inside the lateness allowance still revises the open minute.
        second.Minutes.Should().Equal(new OverflowMinute("A-OV", At(1), 1, 5));
    }

    [Fact]
    public void Accept_Should_ReportEachChangeOnce_When_ABandFillsAndEmptiesTwice()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);

        var (minutes, changes) = Play(engine, bands, 10,
            Reading("A-OV", 0, 0.5),
            Reading("A-OV", 3, 1.5),
            Reading("A-OV", 8, 2.5),
            Reading("A-OV", 6, 3.5),
            Reading("A-OV", 0, 4.5),
            Reading("A-OV", 0, 5.5),
            Reading("A-OV", 2, 6.2),
            Reading("A-OV", 0, 6.8),
            Reading("A-OV", 0, 7.5));

        minutes.Should().HaveCount(8);
        changes.Should().Equal(
            new OverflowChange("A-OV", At(1), OverflowChangeKind.Occupied, 3, null),
            new OverflowChange("A-OV", At(4), OverflowChangeKind.Emptied, 8, At(1)),
            new OverflowChange("A-OV", At(6), OverflowChangeKind.Occupied, 2, null),
            new OverflowChange("A-OV", At(7), OverflowChangeKind.Emptied, 2, At(6)));
        minutes.Count(m => m.Occupied).Should().Be(4, "an overflow minute is a minute with any occupancy (TC-19): 18:01 to 18:03 and 18:06");
        bands.IsOccupied("A-OV").Should().BeFalse();
    }

    [Fact]
    public void Accept_Should_ChangeNothing_When_ABandIsNeverOccupiedOrSilentWithinTheWindow()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);

        var occupied = Play(engine, bands, 2, Reading("A-OV", 4, 0.5), Reading("A-OV2", 0, 0.5));
        // Minutes 1 and 2 closed without a band reading: inside the 2-minute freshness window of minute 0's readings.
        var silent = Play(engine, bands, 3.4, Reading("A-VIS", 40, 3.2));
        var stillEmpty = Play(engine, bands, 4, Reading("A-OV2", 0, 3.5));

        occupied.Changes.Should().Equal(new OverflowChange("A-OV", At(0), OverflowChangeKind.Occupied, 4, null));
        silent.Minutes.Should().BeEmpty("a minute without a band reading has no row: unknown, never zero");
        silent.Changes.Should().BeEmpty("a sensor silent within the freshness window keeps the band's state");
        stillEmpty.Changes.Should().BeEmpty("a band that was never occupied cannot empty");
        bands.IsOccupied("A-OV").Should().BeTrue();
        bands.IsOccupied("A-OV2").Should().BeFalse();
        bands.IsUnknown("A-OV").Should().BeFalse();
    }

    [Theory]
    [InlineData(2 * 60.0, 3)]
    [InlineData(90.0, 3)]
    [InlineData(60.0, 2)]
    [InlineData(1e-7, 1)]
    [InlineData(5 * 60.0, 6)]
    public void UnknownFrom_Should_BeTheFirstMinuteWithEveryReadingOfTheLastMinuteStale_When_TheFreshnessIsSet(double freshSeconds, int minutesAfter)
    {
        var bands = new OverflowBands(Geometry, TimeSpan.FromSeconds(freshSeconds));

        bands.UnknownFrom(At(10)).Should().Be(At(10 + minutesAfter));
    }

    [Fact]
    public void Accept_Should_MakeAnOccupiedBandUnknown_When_ItIsSilentBeyondTheWindow()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);

        var filled = Play(engine, bands, 1.5, Reading("A-OV", 0, 0.2), Reading("A-OV", 6, 0.7), Reading("A-OV", 9, 1.2));
        // Minute 1 is the band's last minute with readings, so it is Unknown from minute 4 once minute 4 has closed
        // (watermark 18:05 less a tick, the reference 30 seconds later): not a tick before.
        var justBefore = bands.Accept(engine.Advance(At(5.5).AddTicks(-2)));
        var silent = bands.Accept(engine.Advance(At(5.5)));
        var later = Play(engine, bands, 20);

        filled.Changes.Should().Equal(new OverflowChange("A-OV", At(0), OverflowChangeKind.Occupied, 6, null));
        justBefore.Changes.Should().BeEmpty();
        bands.IsOccupied("A-OV").Should().BeFalse();
        silent.Minutes.Should().BeEmpty();
        silent.Changes.Should().Equal(new OverflowChange("A-OV", At(4), OverflowChangeKind.Unknown, 9, At(0)));
        later.Changes.Should().BeEmpty("Unknown is reported once");
        bands.IsUnknown("A-OV").Should().BeTrue();
        bands.IsOccupied("A-OV").Should().BeFalse("an unknown band is neither occupied nor empty");
        bands.CaptureBands().Should().Equal(new OverflowBandState("A-OV", false, null, 0, At(1), Unknown: true));
    }

    [Fact]
    public void Accept_Should_MakeAnEmptyBandUnknownAndRecover_When_ItIsSilentThenReportsAgain()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);

        var empty = Play(engine, bands, 1, Reading("A-OV2", 0, 0.5));
        var silent = Play(engine, bands, 4.5);
        var back = Play(engine, bands, 9, Reading("A-OV2", 0, 7.5));
        var silentAgain = Play(engine, bands, 11.5);
        var filled = Play(engine, bands, 14.5, Reading("A-OV2", 3, 13.1));

        empty.Changes.Should().BeEmpty("a band starts empty");
        silent.Changes.Should().Equal(new OverflowChange("A-OV2", At(3), OverflowChangeKind.Unknown, 0, null));
        back.Changes.Should().Equal(new OverflowChange("A-OV2", At(7), OverflowChangeKind.Emptied, 0, null));
        silentAgain.Changes.Should().Equal(new OverflowChange("A-OV2", At(10), OverflowChangeKind.Unknown, 0, null));
        filled.Changes.Should().Equal(new OverflowChange("A-OV2", At(13), OverflowChangeKind.Occupied, 3, null));
        bands.IsOccupied("A-OV2").Should().BeTrue();
        bands.IsUnknown("A-OV2").Should().BeFalse();
    }

    [Fact]
    public void Accept_Should_NotFlap_When_ReadingsArriveWithinTheWindow()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);

        // A sensor that reports every third minute: minute U = L + 3 always has a reading, so the band never goes Unknown.
        var (minutes, changes) = Play(engine, bands, 13.5,
            Reading("A-OV", 5, 0.5), Reading("A-OV", 4, 3.5), Reading("A-OV", 6, 6.5), Reading("A-OV", 0, 9.5), Reading("A-OV", 0, 12.2));

        minutes.Should().HaveCount(5);
        changes.Should().Equal(
            new OverflowChange("A-OV", At(0), OverflowChangeKind.Occupied, 5, null),
            new OverflowChange("A-OV", At(9), OverflowChangeKind.Emptied, 6, At(0)));
    }

    [Fact]
    public void Accept_Should_GiveTheSameChangesInMinuteOrder_When_OneStepOrManyStepsCoverTheSilence()
    {
        QueueInput[] Inputs() => [Reading("A-OV", 4, 0.5), Reading("A-OV2", 0, 0.5), Reading("A-OV2", 0, 1.5), Reading("A-OV2", 2, 8.5), Reading("A-OV", 0, 9.2)];

        var oneEngine = new QueueStateEngine(Geometry, Thirty);
        var one = Play(oneEngine, new OverflowBands(Geometry), 11, Inputs());

        // The same inputs, each offered when it happened, with the engine stepped every quarter minute.
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);
        var changes = new List<OverflowChange>();
        var pending = new Queue<QueueInput>(Inputs());
        for (var t = 0.25; t <= 11; t += 0.25)
        {
            while (pending.Count > 0 && pending.Peek().TimeUtc <= At(t))
            {
                var input = pending.Dequeue();
                engine.Offer(input, input.TimeUtc);
            }

            changes.AddRange(bands.Accept(engine.Advance(At(t))).Changes);
        }

        one.Changes.Should().Equal(
            new OverflowChange("A-OV", At(0), OverflowChangeKind.Occupied, 4, null),
            new OverflowChange("A-OV", At(3), OverflowChangeKind.Unknown, 4, At(0)),
            new OverflowChange("A-OV2", At(4), OverflowChangeKind.Unknown, 0, null),
            new OverflowChange("A-OV2", At(8), OverflowChangeKind.Occupied, 2, null),
            new OverflowChange("A-OV", At(9), OverflowChangeKind.Emptied, 0, null));
        changes.Should().Equal(one.Changes, "the changes do not depend on how the engine's steps fall");
    }

    [Fact]
    public void Restore_Should_KeepAnUnknownBandAndItsRecovery_When_RestoredThroughJson()
    {
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);
        Play(engine, bands, 1.2, Reading("A-OV", 7, 0.5));
        Play(engine, bands, 5);
        bands.IsUnknown("A-OV").Should().BeTrue();

        var open = JsonSerializer.Deserialize<List<OverflowMinuteState>>(JsonSerializer.Serialize(bands.CaptureOpen(), EventCatalog.Json), EventCatalog.Json)!;
        var json = JsonSerializer.Serialize(bands.CaptureBands(), EventCatalog.Json);
        var states = JsonSerializer.Deserialize<List<OverflowBandState>>(json, EventCatalog.Json)!;
        var restored = OverflowBands.Restore(Geometry, open, states);

        json.Should().Contain("\"unknown\":true");
        restored.IsUnknown("A-OV").Should().BeTrue();
        Play(engine, restored, 9).Changes.Should().BeEmpty("an unknown band is reported once, also across a restart");
        Play(engine, restored, 11.5, Reading("A-OV", 2, 10.1)).Changes.Should().Equal(new OverflowChange("A-OV", At(10), OverflowChangeKind.Occupied, 2, null));
    }

    [Fact]
    public void Restore_Should_ReadABandStateWithoutUnknown_When_AVersion5SnapshotHoldsIt()
    {
        // A version 5 band state, as the stream wrote it before the Unknown state: no "unknown" property.
        var states = JsonSerializer.Deserialize<List<OverflowBandState>>(
            "[{\"bandName\":\"A-OV\",\"occupied\":true,\"occupiedSinceUtc\":\"2026-09-28T18:00:00Z\",\"peakOccupancy\":3,\"lastMinuteUtc\":\"2026-09-28T18:01:00Z\"}]",
            EventCatalog.Json)!;

        var restored = OverflowBands.Restore(Geometry, [], states);

        restored.IsOccupied("A-OV").Should().BeTrue();
        restored.IsUnknown("A-OV").Should().BeFalse();
        restored.CaptureBands().Should().Equal(new OverflowBandState("A-OV", true, At(0), 3, At(1)));
    }

    [Fact]
    public void Accept_Should_IgnoreReadingsOfOtherZones_When_TheQueueZoneOrAnUnknownZoneReports()
    {
        var bands = new OverflowBands(Geometry);
        var step = new QueueStep("A-VIS", At(5), [], [], [], new QueueLength(0, false, false, At(5)), new QueueRejections(), 0, 0, 0, false)
        {
            Readings =
            [
                new ZoneReadingMinute("A-VIS", At(0), 10, 20),
                new ZoneReadingMinute("B-OV", At(0), 1, 2),
                new ZoneReadingMinute("A-OV", At(1), 5, 2),
                new ZoneReadingMinute("A-OV", At(2), -1, 3),
                new ZoneReadingMinute("A-OV", At(3), 0, CanonicalEventRules.MaxOccupancy + 1),
                null!
            ]
        };

        var (minutes, changes) = bands.Accept(step);

        minutes.Should().BeEmpty("only the queue's bands count, and only readings the engine could have taken (CWE-501)");
        changes.Should().BeEmpty();
    }

    [Fact]
    public void Restore_Should_ContinueExactly_When_RestoredMidMinuteAndMidSpell()
    {
        QueueInput[] Inputs() =>
        [
            Reading("A-OV", 0, 0.5), Reading("A-OV", 3, 1.2), Reading("A-OV", 7, 1.8), Reading("A-OV", 5, 2.4),
            Reading("A-OV", 0, 3.2), Reading("A-OV", 0, 3.9), Reading("A-OV", 2, 4.6), Reading("A-OV", 0, 5.4)
        ];
        var straightEngine = new QueueStateEngine(Geometry, Thirty);
        var straight = new OverflowBands(Geometry);
        var expected = new List<OverflowMinute>();
        var expectedChanges = new List<OverflowChange>();
        foreach (var input in Inputs())
        {
            straightEngine.Offer(input, input.TimeUtc);
            var (m, c) = straight.Accept(straightEngine.Advance(input.TimeUtc));
            expected.AddRange(m);
            expectedChanges.AddRange(c);
        }

        var (lastMinutes, lastChanges) = straight.Accept(straightEngine.Advance(At(8)));
        expected.AddRange(lastMinutes);
        expectedChanges.AddRange(lastChanges);

        // The same inputs with a snapshot after every one of them, through JSON as the stream store keeps it.
        var engine = new QueueStateEngine(Geometry, Thirty);
        var bands = new OverflowBands(Geometry);
        var minutes = new List<OverflowMinute>();
        var changes = new List<OverflowChange>();
        foreach (var input in Inputs())
        {
            engine.Offer(input, input.TimeUtc);
            var (m, c) = bands.Accept(engine.Advance(input.TimeUtc));
            minutes.AddRange(m);
            changes.AddRange(c);
            var open = JsonSerializer.Deserialize<List<OverflowMinuteState>>(JsonSerializer.Serialize(bands.CaptureOpen(), EventCatalog.Json), EventCatalog.Json)!;
            var states = JsonSerializer.Deserialize<List<OverflowBandState>>(JsonSerializer.Serialize(bands.CaptureBands(), EventCatalog.Json), EventCatalog.Json)!;
            bands = OverflowBands.Restore(Geometry, open, states);
        }

        var (endMinutes, endChanges) = bands.Accept(engine.Advance(At(8)));
        minutes.AddRange(endMinutes);
        changes.AddRange(endChanges);

        minutes.Should().Equal(expected);
        changes.Should().Equal(expectedChanges);
        changes.Select(c => c.Kind).Should().Equal(OverflowChangeKind.Occupied, OverflowChangeKind.Emptied, OverflowChangeKind.Occupied, OverflowChangeKind.Emptied);
    }

    [Fact]
    public void Accept_Should_ReleaseTheEarliestMinuteEarly_When_MoreBandMinutesAreOpenThanTheBound()
    {
        var bands = new OverflowBands(Geometry);
        // A step whose watermark has not passed any of its readings, holding one more open minute than the bound (two bands).
        var readings = Enumerable.Range(0, OverflowBands.MaxOpenBandMinutes / 2 + 1)
            .SelectMany(m => new[] { new ZoneReadingMinute("A-OV", At(m), 1, 1), new ZoneReadingMinute("A-OV2", At(m), 0, 0) }).ToList();
        var step = new QueueStep("A-VIS", At(-1), [], [], [], new QueueLength(0, false, false, At(-1)), new QueueRejections(), 0, 0, 0, false) { Readings = readings };

        var (minutes, changes) = bands.Accept(step);

        bands.Open.Should().Be(OverflowBands.MaxOpenBandMinutes, "the bound holds whatever the step carries (CWE-120)");
        minutes.Should().Equal(new OverflowMinute("A-OV", At(0), 1, 1), new OverflowMinute("A-OV2", At(0), 0, 0));
        changes.Should().Equal(new OverflowChange("A-OV", At(0), OverflowChangeKind.Occupied, 1, null));

        // A later part of the minute released early is released again; it never empties the band it filled.
        var later = new QueueStep("A-VIS", At(-1), [], [], [], new QueueLength(0, false, false, At(-1)), new QueueRejections(), 0, 0, 0, false)
        {
            Readings = [new ZoneReadingMinute("A-OV", At(0), 0, 0), new ZoneReadingMinute("A-OV2", At(0), 4, 4)]
        };
        var (again, againChanges) = bands.Accept(later);
        again.Should().Contain(new OverflowMinute("A-OV", At(0), 0, 0));
        // A later part may only add occupancy the first part missed.
        againChanges.Should().Equal(new OverflowChange("A-OV2", At(0), OverflowChangeKind.Occupied, 4, null));
        bands.IsOccupied("A-OV").Should().BeTrue();
    }

    public static TheoryData<string, OverflowMinuteState[], OverflowBandState[]> HostileSnapshots => new()
    {
        { "an open minute of a zone that is not a band of the queue", [new("B-OV", T0, 0, 1)], [] },
        { "an open minute of the queue zone itself", [new("A-VIS", T0, 0, 1)], [] },
        { "an unaligned open minute", [new("A-OV", T0.AddSeconds(30), 0, 1)], [] },
        { "an open minute before the year 2000", [new("A-OV", new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0, 1)], [] },
        { "a negative reading", [new("A-OV", T0, -1, 1)], [] },
        { "a lowest reading above the highest", [new("A-OV", T0, 5, 1)], [] },
        { "a reading above the canonical bound", [new("A-OV", T0, 0, CanonicalEventRules.MaxOccupancy + 1)], [] },
        { "the same open minute twice", [new("A-OV", T0, 0, 1), new("A-OV", T0, 0, 2)], [] },
        { "a null open minute", [null!], [] },
        { "the state of a zone that is not a band", [], [new("B-OV", false, null, 0, T0)] },
        { "an occupied band without its first minute", [], [new("A-OV", true, null, 3, T0)] },
        { "an occupied band with no peak", [], [new("A-OV", true, T0, 0, T0)] },
        { "an occupied spell that starts after its last minute", [], [new("A-OV", true, T0.AddMinutes(2), 3, T0)] },
        { "an empty band with a spell", [], [new("A-OV", false, T0, 0, T0)] },
        { "an empty band with a peak", [], [new("A-OV", false, null, 4, T0)] },
        { "an unaligned last minute", [], [new("A-OV", false, null, 0, T0.AddSeconds(1))] },
        { "a peak above the canonical bound", [], [new("A-OV", true, T0, CanonicalEventRules.MaxOccupancy + 1, T0)] },
        { "the same band twice", [], [new("A-OV", false, null, 0, T0), new("A-OV", false, null, 0, T0)] },
        { "more band states than the queue has bands", [], [new("A-OV", false, null, 0, T0), new("A-OV2", false, null, 0, T0), new("A-OV3", false, null, 0, T0)] },
        { "an unknown band that is occupied", [], [new("A-OV", true, T0, 3, T0, Unknown: true)] },
        { "an unknown band with a spell", [], [new("A-OV", false, T0, 0, T0, Unknown: true)] },
        { "an unknown band with a peak", [], [new("A-OV", false, null, 3, T0, Unknown: true)] },
        { "an unknown band that was never heard", [], [new("A-OV", false, null, 0, DateTime.MinValue, Unknown: true)] },
        { "an unknown band with an unaligned last minute", [], [new("A-OV", false, null, 0, T0.AddSeconds(7), Unknown: true)] },
        { "an unknown band of a zone that is not a band", [], [new("B-OV", false, null, 0, T0, Unknown: true)] }
    };

    [Theory]
    [MemberData(nameof(HostileSnapshots))]
    public void Restore_Should_RefuseTheSnapshot_When_TheEngineNeverWroteIt(string because, OverflowMinuteState[] open, OverflowBandState[] states)
    {
        var restore = () => OverflowBands.Restore(Geometry, open, states);

        restore.Should().Throw<InvalidDataException>(because);
    }

    [Fact]
    public void Restore_Should_RefuseTheSnapshot_When_ItHoldsMoreOpenMinutesThanTheBound()
    {
        var open = Enumerable.Range(0, OverflowBands.MaxOpenBandMinutes + 1).Select(m => new OverflowMinuteState("A-OV", At(m), 0, 0)).ToList();

        var restore = () => OverflowBands.Restore(Geometry, open, []);

        restore.Should().Throw<InvalidDataException>().WithMessage("*more than 5000 open overflow minutes*");
    }

    [Fact]
    public void Restore_Should_StartEveryBandEmpty_When_TheSnapshotPredatesTheBands()
    {
        var restored = OverflowBands.Restore(Geometry, null, null);

        restored.Open.Should().Be(0);
        restored.IsOccupied("A-OV").Should().BeFalse();
        restored.CaptureBands().Should().BeEmpty();
    }

    [Fact]
    public void ZoneProcessor_Should_ReleaseBandMinutesAndChanges_When_ItDrains()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        var batch = new ZoneOccupancyBatch
        {
            Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), DeviceCode = "S-25", SiteCode = "DMO", QueueZoneName = "A-VIS", Dialect = "canonical",
            Commissioned = true, ReceivedUtc = At(0.6), Clock = new ClockReading(0, true, ClockState.Ok),
            Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-OV", 6, At(0.5)), At(0.5), SensedFlags.None)]
        };
        zone.Offer(batch, At(10));
        zone.Tick(At(3));

        var peeked = zone.Peek();
        var drained = zone.Drain();

        drained.Overflow.Should().Equal(new OverflowMinute("A-OV", At(0), 6, 6));
        drained.OverflowChanges.Should().Equal(new OverflowChange("A-OV", At(0), OverflowChangeKind.Occupied, 6, null));
        peeked.Count.Should().Be(drained.Count);
        zone.Peek().Overflow.Should().BeEmpty();
        var state = zone.Capture();
        state.Version.Should().Be(6);
        state.OverflowBands.Should().Equal(new OverflowBandState("A-OV", true, At(0), 6, At(0)));
    }

    [Fact]
    public void ZoneProcessor_Should_RefuseTheSnapshot_When_ItsOverflowStateIsHostile()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Tick(At(1));
        var hostile = zone.Capture() with { OverflowBands = [new OverflowBandState("Not a band", true, T0, 3, T0)] };

        var restore = () => ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), hostile, At(2));

        restore.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ZoneProcessor_Should_RestoreAVersion4Snapshot_When_ItHasNoOverflowState()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Tick(At(1));
        // A version 4 snapshot as the stream stored it before ARV-115: no overflow properties at all.
        var json = JsonSerializer.SerializeToNode(zone.Capture() with { Version = 4 }, EventCatalog.Json)!.AsObject();
        json.Remove("overflowOpen");
        json.Remove("overflowBands");
        var old = json.Deserialize<ZoneProcessorState>(EventCatalog.Json)!;

        var restored = ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), old, At(2));

        restored.Capture().OverflowBands.Should().BeEmpty("every band starts empty");
    }

    [Fact]
    public void ZoneProcessor_Should_MakeASilentBandUnknown_When_OnlyTimePasses()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Offer(new ZoneOccupancyBatch
        {
            Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), DeviceCode = "S-25", SiteCode = "DMO", QueueZoneName = "A-VIS", Dialect = "canonical",
            Commissioned = true, ReceivedUtc = At(0.6), Clock = new ClockReading(0, true, ClockState.Ok),
            Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-OV", 6, At(0.5)), At(0.5), SensedFlags.None)]
        }, At(10));
        zone.Tick(At(2));
        zone.Drain().OverflowChanges.Should().Equal(new OverflowChange("A-OV", At(0), OverflowChangeKind.Occupied, 6, null));

        // No batch at all: the stream ticks idle zones, and the band's silence closes with the minutes.
        zone.Tick(At(6));
        var drained = zone.Drain();

        drained.Overflow.Should().BeEmpty();
        drained.OverflowChanges.Should().Equal(new OverflowChange("A-OV", At(3), OverflowChangeKind.Unknown, 6, At(0)));
        zone.Capture().OverflowBands.Should().Equal(new OverflowBandState("A-OV", false, null, 0, At(0), Unknown: true));
    }

    [Fact]
    public void ZoneProcessor_Should_RestoreAVersion5Snapshot_When_ItsBandStatesHaveNoUnknown()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Offer(new ZoneOccupancyBatch
        {
            Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), DeviceCode = "S-25", SiteCode = "DMO", QueueZoneName = "A-VIS", Dialect = "canonical",
            Commissioned = true, ReceivedUtc = At(0.6), Clock = new ClockReading(0, true, ClockState.Ok),
            Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-OV", 6, At(0.5)), At(0.5), SensedFlags.None)]
        }, At(10));
        zone.Tick(At(2));
        zone.Drain();
        // A version 5 snapshot as the stream stored it before the Unknown state: band states without the property.
        var json = JsonSerializer.SerializeToNode(zone.Capture() with { Version = 5 }, EventCatalog.Json)!.AsObject();
        foreach (var band in json["overflowBands"]!.AsArray())
            band!.AsObject().Remove("unknown");
        var old = json.Deserialize<ZoneProcessorState>(EventCatalog.Json)!;

        var restored = ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), old, At(3));
        restored.Tick(At(6));

        restored.Capture().Version.Should().Be(ZoneProcessorState.CurrentVersion);
        restored.Drain().OverflowChanges.Should().Equal([new OverflowChange("A-OV", At(3), OverflowChangeKind.Unknown, 6, At(0))],
            "the restored band was occupied, as the version 5 snapshot said, and goes Unknown like any other");
    }

    [Fact]
    public void ZoneProcessor_Should_RefuseTheSnapshot_When_AnUnknownBandIsOccupied()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Tick(At(1));
        var hostile = zone.Capture() with { OverflowBands = [new OverflowBandState("A-OV", true, T0, 3, T0, Unknown: true)] };

        var restore = () => ZoneProcessor.Restore("DMO/A-VIS", Geometry, 12, new ZoneProcessorSettings(), hostile, At(2));

        restore.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(10, true)]
    public void ZoneProcessor_Should_ReportOnlySilencesInsideTheRange_When_AReplayFinishes(double endMinute, bool unknown)
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 12);
        zone.Offer(new ZoneOccupancyBatch
        {
            Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), DeviceCode = "S-25", SiteCode = "DMO", QueueZoneName = "A-VIS", Dialect = "canonical",
            Commissioned = true, ReceivedUtc = At(0.6), Clock = new ClockReading(0, true, ClockState.Ok),
            Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("A-OV", 6, At(0.5)), At(0.5), SensedFlags.None)]
        }, At(endMinute));

        // The inputs end at the range's end, which is not a silent sensor: settling past it makes no band Unknown.
        zone.Finish(At(endMinute), At(endMinute + 30));

        zone.Drain().OverflowChanges.Select(c => c.Kind).Should().Equal(unknown
            ? [OverflowChangeKind.Occupied, OverflowChangeKind.Unknown]
            : [OverflowChangeKind.Occupied]);
    }
}
