using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-030: the queue state engine against formulas F5, F6 and F11. Track and FIFO waits, cumulative curves from
/// interval counts, abandonment, censoring, fragmentation, re-anchoring, late and out-of-order events behind a
/// watermark, clock skew, duplicates, the queue length, and the bounds that keep one zone's state finite.
/// </summary>
public sealed partial class QueueStateEngineTests
{
    #region Helpers

    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    private static readonly QueueZoneGeometry Geometry = new(
        "A-VIS",
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-OV" });

    private static QueueStateEngine Engine(QueueEngineSettings settings = null) => new(Geometry, settings ?? new QueueEngineSettings { Lateness = TimeSpan.Zero });

    private static QueueCrossing In(double minute, string track = null, bool degraded = false) => new("A-VIS entry", CrossingDirection.In, track, At(minute), degraded);
    private static QueueCrossing Out(double minute, string track = null, bool degraded = false) => new("A-VIS exit", CrossingDirection.Out, track, At(minute), degraded);

    /// <summary>Offers every event as it arrives at its own time (no network delay) and advances to <paramref name="until"/>.</summary>
    private static QueueStep Play(QueueStateEngine engine, double until, params QueueInput[] inputs)
    {
        foreach (var input in inputs)
            engine.Offer(input, input.TimeUtc);
        return engine.Advance(At(until));
    }

    private static double[] Minutes(QueueStep step) => [.. step.Waits.Select(w => Math.Round(w.Wait.TotalMinutes, 9))];

    #endregion

    #region F5 realised wait

    [Fact]
    public void Track_Should_GiveTwelveAndAHalfMinutes_When_ItEntersAt1800AndExitsAt181230()
    {
        var step = Play(Engine(), 30, In(0, "S-15/7"), Out(12.5, "S-15/7"));

        step.Waits.Should().ContainSingle().Which.Should().Match<RealisedWait>(w =>
            w.Method == WaitMethod.Track && w.Wait == TimeSpan.FromMinutes(12.5) && w.TrackKey == "S-15/7" && !w.Degraded);
    }

    [Fact]
    public void Fifo_Should_PairTheNthEntrantWithTheNthExit_When_CrossingsAreAnonymous()
    {
        var step = Play(Engine(), 10, In(0), In(1), In(2), Out(5), Out(6), Out(8));

        Minutes(step).Should().Equal(5, 5, 6);
        step.Waits.Should().OnlyContain(w => w.Method == WaitMethod.Fifo);
    }

    [Fact]
    public void Track_Should_KeepFifoAligned_When_ATrackedPersonOvertakes()
    {
        // B (tracked) overtakes the anonymous A: B's exit closes B, and the anonymous exit pairs with A, not with B.
        var step = Play(Engine(), 20, In(0), In(1, "S-15/B"), Out(4, "S-15/B"), Out(9));

        step.Waits.Should().HaveCount(2);
        step.Waits.Single(w => w.Method == WaitMethod.Track).Wait.Should().Be(TimeSpan.FromMinutes(3));
        step.Waits.Single(w => w.Method == WaitMethod.Fifo).Wait.Should().Be(TimeSpan.FromMinutes(9));
    }

    [Fact]
    public void Track_Should_NotStealAnotherTrack_When_ItsOwnEntryWasBeforeTheEngineStarted()
    {
        // S-15/old entered before the engine started; its exit must not take S-15/new's place in the sequence.
        var step = Play(Engine(), 20, In(1, "S-15/new"), Out(2, "S-15/old"), Out(6, "S-15/new"));

        step.Rejections.UnmatchedExits.Should().Be(1);
        step.Waits.Should().ContainSingle().Which.Should().Match<RealisedWait>(w => w.Method == WaitMethod.Track && w.Wait == TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Fifo_Should_PairAcrossDevices_When_EntryAndExitAreTrackedByDifferentSensors()
    {
        var step = Play(Engine(), 20, In(0, "S-15/1"), In(1, "S-15/2"), Out(4, "S-16/9"), Out(5, "S-16/8"));

        Minutes(step).Should().Equal(4, 4);
        step.Waits.Should().OnlyContain(w => w.Method == WaitMethod.Fifo);
    }

    [Fact]
    public void Track_Should_CountOverflowTimeAsWait_When_ItEntersThroughTheOverflowBand()
    {
        var step = Play(Engine(), 30,
            new QueueCrossing("A-OV entry", CrossingDirection.In, "S-25/9", At(0)),
            In(6, "S-25/9"),
            Out(20, "S-25/9"));

        step.Waits.Should().ContainSingle().Which.Wait.Should().Be(TimeSpan.FromMinutes(20), "the first inward crossing of any entry line starts the wait");
        step.Rejections.Duplicates.Should().Be(1, "the second entry line does not count again");
        step.Movements.Sum(m => m.Entries).Should().Be(1);
    }

    [Fact]
    public void Overflow_Should_NotCountAnonymousEntries_When_TheSamePersonCrossesTheQueueEntryLater()
    {
        var step = Play(Engine(), 30, new QueueCrossing("A-OV entry", CrossingDirection.In, null, At(0)), In(6), Out(20));

        step.Movements.Sum(m => m.Entries).Should().Be(1);
        Minutes(step).Should().Equal(14);
    }

    [Fact]
    public void Cumulative_Should_UseIntervalCountsSpreadOverTheInterval_When_TheDeviceCountsLines()
    {
        var step = Play(Engine(), 20,
            new QueueInterval("A-VIS entry", 3, 0, At(0), At(3)),
            new QueueInterval("A-VIS exit", 0, 3, At(5), At(8)));

        Minutes(step).Should().Equal(5, 5, 5);
        step.Waits.Should().OnlyContain(w => w.Method == WaitMethod.Cumulative);
        step.Waits.Select(w => w.EntryUtc).Should().Equal(At(0.5), At(1.5), At(2.5));
    }

    [Fact]
    public void Interval_Should_SpreadOnlyAfterTheLastProcessedEvent_When_ItReachesBack()
    {
        // The exit interval started before the entry at 17 was processed: its exit is spread over 17 to 20, never into
        // minutes already processed, so it cannot pair with an entrant before that entrant entered.
        var step = Play(Engine(), 30, In(17), new QueueInterval("A-VIS exit", 0, 1, At(10), At(20)));

        Minutes(step).Should().Equal(1.5);
        step.Rejections.NegativeWaits.Should().Be(0);
        step.Movements.Should().OnlyContain(m => m.MinuteUtc >= At(17));
    }

    #endregion

    #region F6 outcomes

    [Fact]
    public void Abandon_Should_ResolveTheTrack_When_ItLeavesBackOverTheEntryLine()
    {
        var step = Play(Engine(), 30, In(0, "t1"), new QueueCrossing("A-VIS entry", CrossingDirection.Out, "t1", At(4)), Out(9, "t1"));

        step.Resolutions.Should().ContainSingle().Which.Outcome.Should().Be(EntrantOutcome.Abandoned);
        step.Waits.Should().BeEmpty();
        step.Rejections.Duplicates.Should().Be(1, "the exit of a resolved track is a duplicate, not a FIFO pair");
    }

    [Fact]
    public void Abandon_Should_RemoveTheLatestAnonymousEntrant_When_SomeoneWalksOutAtTheBack()
    {
        var step = Play(Engine(), 30, In(0), In(1), In(2), new QueueCrossing("A-VIS entry", CrossingDirection.Out, null, At(3)), Out(10), Out(11));

        step.Resolutions.Should().ContainSingle().Which.EntryUtc.Should().Be(At(2));
        Minutes(step).Should().Equal(10, 10);
    }

    [Fact]
    public void MissingExit_Should_BeCensored_When_TCensorPasses()
    {
        var engine = Engine();
        var first = Play(engine, 100, In(0, "t1"), In(5));
        var second = engine.Advance(At(126));

        first.Resolutions.Should().BeEmpty();
        first.OpenEntrants.Should().Be(2);
        second.Resolutions.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == EntrantOutcome.Censored);
        second.Resolutions.Select(r => r.ResolvedUtc).Should().Equal(At(120), At(125));
        second.OpenEntrants.Should().Be(0);
    }

    [Fact]
    public void Track_Should_BeFragmented_When_LostInsideTheZoneBeyondTheHandover()
    {
        // F6: a track entering at 18:05 and lost at 18:10 inside the zone is Fragmented; it does not hold its bin open.
        var step = Play(Engine(), 20, In(5, "t1"), new QueueTrackSeen("t1", At(9.8)), new QueueTrackSeen("t1", At(10)));

        step.Resolutions.Should().ContainSingle().Which.Should().Match<EntrantResolution>(r =>
            r.Outcome == EntrantOutcome.Fragmented && r.EntryUtc == At(5) && r.ResolvedUtc == At(10.5));
    }

    [Fact]
    public void Track_Should_StayOpen_When_ItIsStillSeenOrHasNoPositions()
    {
        var step = Play(Engine(), 20, In(5, "t1"), In(6, "t2"), new QueueTrackSeen("t1", At(19.8)));

        step.Resolutions.Should().BeEmpty();
        step.OpenEntrants.Should().Be(2);
    }

    #endregion

    #region Re-anchoring and the queue length

    [Fact]
    public void Occupancy_Should_ReanchorTheFifoSequence_When_TheQueueIsObservedEmpty()
    {
        // Three entries, one exit, then every zone reads zero: the two held are a residual and leave the sequence.
        var step = Play(Engine(), 30,
            In(0), In(1), In(2), Out(4),
            new QueueOccupancy("A-VIS", 0, At(6)), new QueueOccupancy("A-OV", 0, At(6)),
            In(7), Out(9));

        step.Reanchors.Should().Be(1);
        step.Resolutions.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == EntrantOutcome.Reanchored);
        Minutes(step).Should().Equal(4, 2);
        step.Waits.Should().OnlyContain(w => !w.Degraded, "two dropped is within the tolerance");
    }

    [Fact]
    public void Reanchor_Should_FlagFollowingFifoWaits_When_TheResidualExceedsTheTolerance()
    {
        var step = Play(Engine(), 30,
            In(0), In(0.5), In(1), In(1.5), In(2),
            new QueueOccupancy("A-VIS", 0, At(3)), new QueueOccupancy("A-OV", 0, At(3)),
            In(4), Out(6));

        step.Resolutions.Should().HaveCount(5);
        step.Waits.Should().ContainSingle().Which.Degraded.Should().BeTrue("waits after a large residual are suspect (F5)");
    }

    [Fact]
    public void Reanchor_Should_WaitForEveryZone_When_OnlyTheQueueReadsZero()
    {
        var step = Play(Engine(), 30, In(0), In(1), new QueueOccupancy("A-VIS", 0, At(3)));

        step.Reanchors.Should().Be(0, "the overflow band has not reported");
        step.OpenEntrants.Should().Be(2);
    }

    [Fact]
    public void Length_Should_ComeFromFreshSensorsOrFromTheHeldCount_When_Asked()
    {
        var engine = Engine();
        var counted = Play(engine, 3, In(0), In(1), In(2));
        var sensed = Play(engine, 5, new QueueOccupancy("A-VIS", 41, At(4)), new QueueOccupancy("A-OV", 6, At(4), Degraded: true));
        var stale = engine.Advance(At(7));

        counted.Length.Should().Be(new QueueLength(3, false, false, At(3)));
        sensed.Length.Should().Be(new QueueLength(47, true, true, At(5)), "queue plus overflow band, degraded when a reading is");
        stale.Length.FromSensors.Should().BeFalse("readings older than two minutes give way to the held count");
        stale.Length.Count.Should().Be(3);
    }

    #endregion

    #region Late, out-of-order and skewed events

    [Fact]
    public void Advance_Should_ProcessOutOfOrderEventsInTimeOrder_When_TheyArriveWithinTheLateness()
    {
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30) });

        // The exit's device delivers late: it arrives after the second entry although it happened first.
        engine.Offer(In(0), At(0));
        engine.Offer(In(1), At(1));
        engine.Offer(Out(0.9), At(1.2));
        engine.Offer(Out(3), At(3));
        var step = engine.Advance(At(4));

        Minutes(step).Should().Equal(0.9, 2);
        step.Rejections.Late.Should().Be(0);
        step.WatermarkUtc.Should().Be(At(4) - TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Offer_Should_CountLateEvents_When_TheyArriveBehindTheWatermark()
    {
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30) });
        engine.Offer(In(0), At(0));
        engine.Advance(At(10));

        engine.Offer(Out(2), At(10.1));
        engine.Offer(Out(5), At(10.1));
        var step = engine.Advance(At(11));

        step.Rejections.Late.Should().Be(2);
        step.Rejections.EarliestLateUtc.Should().Be(At(2), "the earliest late minute tells a recomputation where to start");
        step.Waits.Should().BeEmpty();
        step.OpenEntrants.Should().Be(1, "late events are not applied");
    }

    [Fact]
    public void Offer_Should_RefuseEventsFromTheFuture_When_TheClockIsSkewed()
    {
        var engine = Engine();
        engine.Offer(In(10), At(0));
        engine.Offer(In(4.9), At(0));
        engine.Offer(new QueueCrossing("A-VIS entry", CrossingDirection.In, null, DateTime.SpecifyKind(At(1), DateTimeKind.Unspecified)), At(1));
        var step = engine.Advance(At(6));

        step.Rejections.Future.Should().Be(1, "10 minutes ahead is beyond the 5 allowed");
        step.Rejections.Invalid.Should().Be(1, "a time without a UTC kind is refused");
        step.Movements.Sum(m => m.Entries).Should().Be(1);
    }

    [Fact]
    public void Wait_Should_BeDegraded_When_AnInputCameFromACorrectedClock()
    {
        var step = Play(Engine(), 20, In(0, "t1", degraded: true), Out(5, "t1"), In(6), Out(8, degraded: true));

        step.Waits.Should().HaveCount(2).And.OnlyContain(w => w.Degraded, "a corrected or skewed clock makes the result Degraded (F11)");
    }

    [Fact]
    public void Watermark_Should_NeverGoBack_When_TheReferenceClockDoes()
    {
        var engine = Engine();
        engine.Advance(At(10));

        var step = engine.Advance(At(5));

        step.WatermarkUtc.Should().Be(At(10));
    }

    #endregion

    #region Duplicates, geometry and bounds

    [Fact]
    public void Duplicates_Should_BeIgnored_When_AKafkaRedeliveryRepeatsATrack()
    {
        var step = Play(Engine(), 20, In(0, "t1"), In(0.1, "t1"), Out(5, "t1"), Out(5.1, "t1"));

        step.Waits.Should().ContainSingle();
        step.Rejections.Duplicates.Should().Be(2);
    }

    [Fact]
    public void Geometry_Should_IgnoreOtherLinesAndZones_When_TheyAreNotTheQueueZones()
    {
        var step = Play(Engine(), 20,
            new QueueCrossing("CI-C entry", CrossingDirection.In, null, At(0)),
            new QueueOccupancy("CI-C", 4, At(1)),
            new QueueCrossing("A-VIS exit", CrossingDirection.In, null, At(2)),
            Out(3));

        step.Rejections.UnknownGeometry.Should().Be(2);
        step.Rejections.Reverse.Should().Be(1);
        step.Rejections.UnmatchedExits.Should().Be(1, "an exit before anyone entered (since the engine started)");
        step.Waits.Should().BeEmpty();
    }

    [Fact]
    public void Bounds_Should_CensorTheEarliest_When_TooManyPeopleAreHeld()
    {
        var engine = Engine(new QueueEngineSettings { Lateness = TimeSpan.Zero, MaxOpenEntrants = 3 });

        var step = Play(engine, 10, In(0), In(1), In(2), In(3), In(4));

        step.OpenEntrants.Should().Be(3);
        step.Resolutions.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == EntrantOutcome.Censored);
        step.Resolutions.Select(r => r.EntryUtc).Should().Equal(At(0), At(1));
    }

    [Fact]
    public void Bounds_Should_RefuseNewEvents_When_TheBufferIsFullOfEventsNotYetDue()
    {
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.FromMinutes(20), MaxBufferedEvents = 10 });

        for (var k = 0; k < 11; k++)
            engine.Offer(In(k), At(k));
        var step = engine.Advance(At(10.5));

        engine.BufferedEvents.Should().Be(10);
        step.Rejections.BufferFull.Should().Be(1);
        step.WatermarkUtc.Should().Be(At(-9.5), "the watermark never runs ahead of the clock minus the lateness");
    }

    [Fact]
    public void Bounds_Should_ProcessDueEventsToMakeRoom_When_TheBufferIsFull()
    {
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), MaxBufferedEvents = 10 });

        for (var k = 0; k < 10; k++)
            engine.Offer(In(k * 0.1), At(k * 0.1));
        engine.Offer(In(5), At(5));
        var step = engine.Advance(At(5));

        step.Rejections.ForcedAdvances.Should().Be(1);
        step.Rejections.BufferFull.Should().Be(0);
        step.Movements.Sum(m => m.Entries).Should().Be(10, "the ten due events were processed; the one at 18:05 is not yet due");
    }

    [Fact]
    public void Bounds_Should_KeepFutureFloodsFromMovingTheWatermark_When_ADeviceSendsAheadOfTheClock()
    {
        // A faulty device fills the buffer with events up to 5 minutes ahead; the others' events keep being processed.
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30), MaxBufferedEvents = 100 });
        for (var k = 0; k < 500; k++)
            engine.Offer(new QueueTrackSeen("S-99/x", At(4.9)), At(0));
        for (var k = 0; k < 20; k++)
            engine.Offer(In(k * 0.05), At(1));
        var step = engine.Advance(At(1.5));

        step.Rejections.BufferFull.Should().Be(490, "at most a tenth of the buffer holds events ahead of the clock");
        step.Rejections.Late.Should().Be(0);
        step.Movements.Sum(m => m.Entries).Should().Be(20);
        step.WatermarkUtc.Should().Be(At(1.5) - TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Bounds_Should_EndTheStepEarly_When_ItsOutputIsFull()
    {
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.Zero, MaxStepRecords = 1_000 });
        for (var k = 0; k < 3_000; k++)
        {
            engine.Offer(In(k * 0.001, "S-15/" + k), At(k * 0.001));
            engine.Offer(Out(k * 0.001 + 0.0005, "S-15/" + k), At(k * 0.001));
        }

        var steps = new List<QueueStep> { engine.Advance(At(10)) };
        while (steps[^1].More)
            steps.Add(engine.Advance(At(10)));

        steps.Count.Should().BeGreaterThan(2);
        steps.Should().OnlyContain(s => s.Waits.Count + s.Resolutions.Count + s.Movements.Count <= 1_000 + 2);
        steps.Sum(s => s.Waits.Count).Should().Be(3_000, "nothing is lost across the shortened steps");
        steps.Sum(s => s.Rejections.Late).Should().Be(0);
    }

    [Fact]
    public void Bounds_Should_KeepDeviceGroupsConsistent_When_TheCensoredPersonWasTheLastOfTheirDevice()
    {
        // The bound censors the only person tracked by "evil" just as "evil" enters someone else.
        var engine = Engine(new QueueEngineSettings { Lateness = TimeSpan.Zero, MaxOpenEntrants = 3 });

        var step = Play(engine, 20, In(0, "evil/1"), In(1), In(2), In(3, "evil/2"), In(4, "evil/3"), Out(6, "evil/2"), Out(7), Out(8, "evil/3"));

        step.Resolutions.Select(r => r.TrackKey).Should().Equal("evil/1", null);
        step.Waits.Select(w => w.TrackKey).Should().Equal("evil/2", null, "evil/3");
        step.OpenEntrants.Should().Be(0);
    }

    [Fact]
    public void Interval_Should_NotReachBehindTheWatermark_When_TheZoneWasQuiet()
    {
        var engine = Engine();
        engine.Advance(At(60));

        var step = Play(engine, 70, new QueueInterval("A-VIS entry", 2, 0, At(-1300), At(65)));

        step.Movements.Should().OnlyContain(m => m.MinuteUtc >= At(60));
        step.Resolutions.Should().BeEmpty("nobody is spread far enough back to be censored");
        step.OpenEntrants.Should().Be(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Engine_Should_NeverThrowAndConservePeople_When_FedRandomInput(int seed)
    {
        // Random crossings, intervals, readings and sightings from a few devices against small bounds: every person who
        // entered is exited, resolved or held, and no input throws.
        // A seeded mulberry32 stream (the scenario engine's): reproducible, and not used for anything secret.
        var stream = new Ariva.Simulation.Api.Scenarios.Engine.Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        int Between(int min, int max) => min + Next(max - min);
        double NextDouble() => stream.Next();
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings
        {
            Lateness = TimeSpan.FromSeconds(20), MaxOpenEntrants = 25, MaxBufferedEvents = 40, MaxStepRecords = 1_000, MaxIntervalPeople = 8,
            MaxDevicesPerZone = 3, MaxRememberedTracks = 30, HandoverWindow = TimeSpan.FromSeconds(10), CensorAfter = TimeSpan.FromMinutes(5)
        });
        string[] lines = ["A-VIS entry", "A-VIS exit", "A-OV entry", "Elsewhere"];
        string[] zones = ["A-VIS", "A-OV", "CI-C"];
        long entries = 0, exits = 0, resolved = 0, negative = 0;
        var clock = 0.0;
        for (var k = 0; k < 3_000; k++)
        {
            clock += NextDouble() * 0.05;
            var time = At(clock - NextDouble() * 0.6);
            string Track() => Next(3) == 0 ? null : $"{"abcde"[Next(5)]}/{Next(40)}";
            QueueInput input = Next(10) switch
            {
                < 5 => new QueueCrossing(lines[Next(lines.Length)], Next(2) == 0 ? CrossingDirection.In : CrossingDirection.Out, Track(), time),
                5 => new QueueInterval(lines[Next(lines.Length)], Between(-1, 10), Between(-1, 10), time.AddMinutes(-NextDouble() * 3), time),
                6 or 7 => new QueueOccupancy(zones[Next(zones.Length)], Between(-1, 4), time),
                _ => new QueueTrackSeen(Track(), time)
            };
            engine.Offer(input, At(clock));
            if (Next(20) == 0)
            {
                var step = engine.Advance(At(clock));
                entries += step.Movements.Sum(m => m.Entries);
                exits += step.Movements.Sum(m => m.Exits);
                resolved += step.Waits.Count + step.Resolutions.Count;
                negative += step.Rejections.NegativeWaits;
            }
        }

        var last = engine.Advance(At(clock + 60));
        entries += last.Movements.Sum(m => m.Entries);
        resolved += last.Waits.Count + last.Resolutions.Count;
        negative += last.Rejections.NegativeWaits;

        (resolved + engine.OpenEntrants).Should().Be(entries, "a rejected pair is a resolution too");
        engine.OpenEntrants.Should().BeLessThanOrEqualTo(25);
        engine.BufferedEvents.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void Interval_Should_BeRefused_When_ItCountsMoreThanALineCan()
    {
        var step = Play(Engine(), 30,
            new QueueInterval("A-VIS entry", 100_000, 0, At(0), At(1)),
            new QueueInterval("A-VIS entry", 3, 0, At(2), At(1)),
            new QueueInterval("A-VIS entry", 3, -1, At(0), At(1)),
            new QueueInterval("A-VIS entry", 3, 0, At(-2000), At(1)),
            new QueueOccupancy("A-VIS", 1_000_000, At(1)));

        step.Rejections.Invalid.Should().Be(5);
        step.OpenEntrants.Should().Be(0);
    }

    [Fact]
    public void Devices_Should_BeCountedAnonymously_When_TheZoneHasTooMany()
    {
        var engine = Engine(new QueueEngineSettings { Lateness = TimeSpan.Zero, MaxDevicesPerZone = 2 });

        var step = Play(engine, 10, In(0, "A/1"), In(1, "B/1"), In(2, "C/1"), Out(5, "C/1"));

        step.Rejections.TooManyDevices.Should().Be(1);
        step.Movements.Sum(m => m.Entries).Should().Be(3, "the third device's people are still counted");
        step.Waits.Should().ContainSingle().Which.Method.Should().Be(WaitMethod.Fifo);
    }

    [Fact]
    public void Work_Should_StayLinear_When_ADeviceSendsHostileButValidInput()
    {
        // 20,000 people tracked by one device, then 20,000 exits with unknown keys from the same device, 5,000
        // abandons in one interval and 20,000 position samples: each costs the number of devices, not of people.
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.Zero });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var k = 0; k < 20_000; k++)
            engine.Offer(In(k * 0.001, "S-15/" + k), At(k * 0.001));
        for (var k = 0; k < 20_000; k++)
            engine.Offer(new QueueTrackSeen("S-15/" + k, At(20 + k * 0.0001)), At(22));
        for (var k = 0; k < 20_000; k++)
            engine.Offer(Out(25 + k * 0.0001, "S-15/unknown" + k), At(27));
        for (var k = 0; k < 5_000; k++)
            engine.Offer(In(30 + k * 0.0001), At(31));
        engine.Offer(new QueueInterval("A-VIS entry", 0, 5_000, At(30), At(31)), At(31));
        var steps = new List<QueueStep> { engine.Advance(At(32)) };
        while (steps[^1].More)
            steps.Add(engine.Advance(At(32)));
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "about 90,000 hostile events cost microseconds each");
        var r = steps.Select(s => s.Rejections).ToList();
        (steps.Sum(s => s.Rejections.UnmatchedExits), steps.Sum(s => s.Waits.Count), steps.Sum(s => s.Resolutions.Count), r.Sum(x => x.Future), r.Sum(x => x.Late), r.Sum(x => x.BufferFull))
            .Should().Be((20_000L, 0, 25_000, 0L, 0L, 0L), "a device's unknown tracks never take the people it tracks; 20,000 lost tracks and 5,000 walk-outs are resolved");
    }

    [Fact]
    public void Bounds_Should_ForgetOldTracks_When_TheMemoryIsFull()
    {
        var engine = Engine(new QueueEngineSettings { Lateness = TimeSpan.Zero, MaxRememberedTracks = 2 });

        var step = Play(engine, 20, In(0, "a"), Out(1, "a"), In(2, "b"), Out(3, "b"), In(4, "c"), Out(5, "c"), In(6, "a"), Out(7, "a"));

        step.Waits.Should().HaveCount(4, "track a was forgotten, so its reuse counts as a new person");
    }

    [Theory]
    [InlineData(-1, 120, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 120, 0)]
    public void Settings_Should_BeValidated_When_TheEngineIsCreated(int latenessSeconds, int censorMinutes, int maxOpen)
    {
        var settings = new QueueEngineSettings
        {
            Lateness = TimeSpan.FromSeconds(latenessSeconds), CensorAfter = TimeSpan.FromMinutes(censorMinutes), MaxOpenEntrants = maxOpen
        };

        Action create = () => _ = new QueueStateEngine(Geometry, settings);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Engine_Should_StayPure_When_ItsSourceIsRead()
    {
        // No I/O and no clock: time is passed in (ARV-030).
        var folder = RepositoryPaths.Resolve("Platform/Backplane/Ariva.Core/Queueing");
        var source = string.Concat(Directory.GetFiles(folder, "*.cs").Select(File.ReadAllText));

        Impure().Matches(source).Select(m => m.Value).Should().BeEmpty();
    }

    [GeneratedRegex(@"DateTime(Offset)?\.(Utc)?Now|TimeProvider|System\.IO|File\.|HttpClient|Task\.Delay|Thread\.|Random\b|Environment\.")]
    private static partial Regex Impure();

    [Fact]
    public void Engine_Should_StayBounded_When_ALongDayIsPlayed()
    {
        // A day of steady flow with a third of the people never seen leaving: the state stays bounded.
        var engine = new QueueStateEngine(Geometry, new QueueEngineSettings { Lateness = TimeSpan.FromSeconds(30) });
        var maxOpen = 0;
        for (var minute = 0; minute < 1440; minute++)
        {
            for (var k = 0; k < 6; k++)
                engine.Offer(In(minute + k / 6.0), At(minute + k / 6.0));
            for (var k = 0; k < 4; k++)
                engine.Offer(Out(minute + k / 4.0 + 0.01), At(minute + k / 4.0 + 0.01));
            maxOpen = Math.Max(maxOpen, engine.Advance(At(minute + 1)).OpenEntrants);
        }

        // FIFO exits take the earliest; censoring takes whoever is still held after 120 minutes, so at most the people
        // who entered within T_censor are held.
        maxOpen.Should().BeLessThanOrEqualTo(6 * 120 + 6);
        engine.BufferedEvents.Should().BeLessThan(20);
    }

    #endregion
}
