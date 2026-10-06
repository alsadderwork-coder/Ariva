using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

// Ariva.UnitTests.HealthChecks (the hosts' health endpoints) would shadow the class.
using HealthChecks = Ariva.Core.Queueing.HealthChecks;

/// <summary>
/// ARV-114a: the continuous health checks of F18 per queue zone and bin. The formulas (conservation residual, track
/// completion rate, occupancy sanity) from the cases in docs/domain/formulas.md; the engine's inputs to them (the queue's
/// sensor occupancy at minute boundaries, readings per zone and minute, tracked entries and outcomes); their attribution
/// to bins beside each bin result; and the snapshot that carries them across a restart.
/// </summary>
public sealed class HealthChecksTests
{
    #region Helpers

    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    private static QueueZoneGeometry Geometry(int? queueCapacity = 5, int? bandCapacity = null)
    {
        var capacities = new Dictionary<string, int>(StringComparer.Ordinal);
        if (queueCapacity is { } q)
            capacities["A-VIS"] = q;
        if (bandCapacity is { } b)
            capacities["A-OV"] = b;
        return new QueueZoneGeometry("A-VIS",
            new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" },
            new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
            new HashSet<string>(StringComparer.Ordinal) { "A-OV entry" },
            new HashSet<string>(StringComparer.Ordinal) { "A-OV" })
        { Capacities = capacities };
    }

    private static readonly QueueEngineSettings Settings = new() { Lateness = TimeSpan.FromSeconds(30), CensorAfter = TimeSpan.FromMinutes(5) };

    private static QueueCrossing In(double minute, string track = null) => new("A-VIS entry", CrossingDirection.In, track, At(minute));

    private static QueueCrossing Out(double minute, string track = null) => new("A-VIS exit", CrossingDirection.Out, track, At(minute));

    private static QueueOccupancy Occupancy(double minute, int count, string zone = "A-VIS") => new(zone, count, At(minute));

    /// <summary>Offers each event at its own time, advances to <paramref name="until"/> and accepts the step into the bins.</summary>
    private static (QueueStep Step, BinUpdate Update) Play(QueueStateEngine engine, BinAccumulator bins, double until, params QueueInput[] inputs)
    {
        foreach (var input in inputs)
            engine.Offer(input, input.TimeUtc);
        var step = engine.Advance(At(until));
        return (step, bins.Accept(step));
    }

    private static ZoneHealthBin Latest(IEnumerable<ZoneHealthBin> health, double binStart) => health.Last(h => h.StartUtc == At(binStart));

    #endregion

    #region Formulas (F18)

    [Theory]
    [InlineData(30, 25, 10, 15, 0L)]
    [InlineData(30, 25, 10, 12, 3L)]
    [InlineData(30, 25, 10, 18, -3L)]
    [InlineData(0, 0, 4, 4, 0L)]
    [InlineData(0, 0, 4, 6, -2L)]
    [InlineData(0, 7, 7, 0, 0L)]
    public void ConservationResidual_Should_FollowF18_When_TheOccupancyIsKnownAtBothEnds(long entries, long exits, int start, int end, long residual)
    {
        HealthChecks.ConservationResidual(entries, exits, start, end).Should().Be(residual);
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData(10, null)]
    [InlineData(null, null)]
    public void ConservationResidual_Should_BeUnknown_When_AnEndHasNoSensorOccupancy(int? start, int? end)
    {
        HealthChecks.ConservationResidual(30, 25, start, end).Should().BeNull();
    }

    [Theory]
    [InlineData(20, 18, 0.9)]
    [InlineData(20, 20, 1.0)]
    [InlineData(4, 1, 0.25)]
    [InlineData(5, 0, 0.0)]
    public void TrackCompletionRate_Should_BeTracksExitedOverTracksEntered_When_TracksEntered(long entered, long exited, double rate)
    {
        HealthChecks.TrackCompletionRate(entered, exited).Should().Be(rate);
    }

    [Fact]
    public void TrackCompletionRate_Should_BeUnknown_When_NoTrackEntered()
    {
        HealthChecks.TrackCompletionRate(0, 0).Should().BeNull("a bin with no entries has no rate, neither 0 nor 1");
        HealthChecks.TrackCompletionRate(0, 3).Should().BeNull();
    }

    [Theory]
    [InlineData(0, 40, 40, false)]
    [InlineData(0, 41, 40, true)]
    [InlineData(-1, 3, 40, true)]
    [InlineData(0, 9_000, null, false)]
    [InlineData(-1, 0, null, true)]
    public void OutsideCapacity_Should_FlagReadingsBelowZeroOrAboveTheCapacity_When_Checked(int min, int max, int? capacity, bool outside)
    {
        HealthChecks.OutsideCapacity(min, max, capacity).Should().Be(outside);
    }

    [Fact]
    public void Open_Should_BeTheTracksNotYetResolved_When_SomeAreCensoredOrStillInTheQueue()
    {
        new TrackOutcomes(20, 16, 1, 1, 1, 0).Open.Should().Be(1);
        new TrackOutcomes(2, 3, 0, 0, 0, 0).Open.Should().Be(0, "never below zero");
        TrackOutcomes.None.Open.Should().Be(0);
    }

    [Fact]
    public void Of_Should_CarryTheBinAndItsChecks_When_Evaluated()
    {
        var bin = new BinResult("A-VIS", At(0), TimeSpan.FromMinutes(15), 2, BinStatus.Final, BinQuality.Good, 30, 25,
            new WaitSummary(0, null, null, null, null, null, []), 0, 0, 0, 0, 0, 0, 0, 12, "recomputed");

        var health = HealthChecks.Of(bin, new TrackOutcomes(20, 18, 0, 1, 1, 0), 10, 12, new OccupancyMinutes(15, 15, 2));

        health.Should().Be(new ZoneHealthBin("A-VIS", At(0), TimeSpan.FromMinutes(15), 2, BinStatus.Final, 12, 30, 25, 10, 12, 3,
            20, 18, 0, 1, 1, 0, 0, 0.9, 15, 15, 2));
    }

    #endregion

    #region Engine inputs

    [Fact]
    public void Advance_Should_SampleTheQueuesOccupancyAtEachMinuteBoundary_When_ItsReadingsAreFresh()
    {
        var engine = new QueueStateEngine(Geometry(), Settings);
        foreach (var o in new[] { Occupancy(0.2, 3), Occupancy(0.8, 7), Occupancy(1.3, 9), Occupancy(2, 4) })
            engine.Offer(o, o.TimeUtc);

        var step = engine.Advance(At(6.5));

        // 18:01 sees the reading of 00:48 (7), not the later 9; 18:02 sees the reading exactly at it; the last reading
        // (18:02) is fresh for two minutes, so 18:03 and 18:04 have samples and 18:05 does not.
        step.Occupancy.Should().Equal(new OccupancySample(At(1), 7), new OccupancySample(At(2), 4), new OccupancySample(At(3), 4), new OccupancySample(At(4), 4));
        step.Readings.Should().Equal(new ZoneReadingMinute("A-VIS", At(0), 3, 7), new ZoneReadingMinute("A-VIS", At(1), 9, 9), new ZoneReadingMinute("A-VIS", At(2), 4, 4));
    }

    [Fact]
    public void Advance_Should_AddTheBandsHeardAndSkipBoundaries_When_ABandIsStale()
    {
        var engine = new QueueStateEngine(Geometry(), Settings);
        foreach (var o in new[] { Occupancy(0.5, 3), Occupancy(0.6, 2, "A-OV"), Occupancy(1.5, 4), Occupancy(2.5, 5), Occupancy(3.5, 6) })
            engine.Offer(o, o.TimeUtc);

        var step = engine.Advance(At(4.5));

        // A band heard once counts while fresh (to 18:02:36), then the queue has no full sensor reading: no sample.
        step.Occupancy.Should().Equal(new OccupancySample(At(1), 5), new OccupancySample(At(2), 6));
        step.Readings.Should().Contain(new ZoneReadingMinute("A-OV", At(0), 2, 2));
    }

    [Fact]
    public void Advance_Should_TakeNoSample_When_TheQueueZoneWasNeverRead()
    {
        var engine = new QueueStateEngine(Geometry(), Settings);
        engine.Offer(Occupancy(0.5, 3, "A-OV"), At(0.5));
        engine.Offer(In(0.7), At(0.7));

        var step = engine.Advance(At(3));

        step.Occupancy.Should().BeEmpty();
        step.Readings.Should().ContainSingle();
    }

    [Fact]
    public void Advance_Should_CountTrackedEntriesAndMarkTrackedOutcomes_When_TracksAndAnonymousPeopleEnter()
    {
        var engine = new QueueStateEngine(Geometry(), Settings);
        foreach (var e in new QueueInput[] { In(0.1, "S-15/1"), In(0.2), In(0.3, "S-15/2"), new QueueCrossing("A-VIS entry", CrossingDirection.Out, "S-15/2", At(0.5)) })
            engine.Offer(e, e.TimeUtc);

        var step = engine.Advance(At(2));

        step.Movements.Should().ContainSingle().Which.Should().Be(new MovementCount(At(0), 3, 0, 0, 0) { TrackedEntries = 2 });
        step.Resolutions.Should().ContainSingle().Which.Should().Be(new EntrantResolution(At(0.3), At(0.5), EntrantOutcome.Abandoned, "S-15/2") { Tracked = true });
    }

    [Fact]
    public void Restore_Should_GiveTheSameHealthInputs_When_TheEngineIsRestoredMidway()
    {
        var inputs = new QueueInput[] { Occupancy(0.5, 3), In(0.7, "S-15/1"), Occupancy(1.5, 4), Out(2.2, "S-15/1"), Occupancy(2.5, 3), Occupancy(3.4, 2) };
        var whole = new QueueStateEngine(Geometry(), Settings);
        var split = new QueueStateEngine(Geometry(), Settings);
        foreach (var i in inputs)
        {
            whole.Offer(i, i.TimeUtc);
            split.Offer(i, i.TimeUtc);
        }

        var first = split.Advance(At(1.7));
        var json = JsonSerializer.Serialize(split.Capture());
        var restored = QueueStateEngine.Restore(Geometry(), Settings, JsonSerializer.Deserialize<QueueEngineState>(json)!);
        var second = restored.Advance(At(5));
        var all = whole.Advance(At(5));

        first.Occupancy.Concat(second.Occupancy).Should().Equal(all.Occupancy);
        first.Readings.Concat(second.Readings).Should().Equal(all.Readings);
        first.Movements.Concat(second.Movements).Sum(m => m.TrackedEntries).Should().Be(all.Movements.Sum(m => m.TrackedEntries)).And.Be(1);
    }

    public static TheoryData<string, Func<QueueEngineState, QueueEngineState>> HostileEngineStates => new()
    {
        { "a reading of another zone", s => s with { Readings = [new ZoneReadingMinute("Elsewhere", At(0), 1, 2)] } },
        { "a reading off a minute", s => s with { Readings = [new ZoneReadingMinute("A-VIS", At(0.5), 1, 2)] } },
        { "a reading below zero", s => s with { Readings = [new ZoneReadingMinute("A-VIS", At(0), -1, 2)] } },
        { "a reading above the canonical bound", s => s with { Readings = [new ZoneReadingMinute("A-VIS", At(0), 1, 10_001)] } },
        { "a minimum above the maximum", s => s with { Readings = [new ZoneReadingMinute("A-VIS", At(0), 5, 2)] } },
        { "a zone's minute twice", s => s with { Readings = [new ZoneReadingMinute("A-VIS", At(0), 1, 2), new ZoneReadingMinute("A-VIS", At(0), 1, 2)] } },
        { "a sample off a boundary", s => s with { OccupancySamples = [new OccupancySample(At(0.25), 3)] } },
        { "a sample below zero", s => s with { OccupancySamples = [new OccupancySample(At(1), -3)] } },
        { "a sample above the queue's bound (queue zone and one band at 10,000)", s => s with { OccupancySamples = [new OccupancySample(At(1), 20_001)] } },
        { "a zone occupancy above the canonical bound", s => s with { Occupancy = [new QueueOccupancyState("A-VIS", 10_001, At(0), false)] } },
        { "a sample of a minute twice", s => s with { OccupancySamples = [new OccupancySample(At(1), 3), new OccupancySample(At(1), 4)] } },
        { "more tracked entries than entries", s => s with { Movements = [new MovementCount(At(0), 1, 0, 0, 0) { TrackedEntries = 2 }] } }
    };

    [Theory]
    [MemberData(nameof(HostileEngineStates))]
    public void Restore_Should_RefuseHealthInputsTheEngineNeverWrites_When_TheSnapshotIsHostile(string because, Func<QueueEngineState, QueueEngineState> tamper)
    {
        var state = tamper(new QueueStateEngine(Geometry(), Settings).Capture());

        var restore = () => QueueStateEngine.Restore(Geometry(), Settings, state);

        restore.Should().Throw<InvalidDataException>(because);
    }

    #endregion

    #region Bins

    /// <summary>
    /// The 18:00 bin: two people already queue (reading 2 at 17:59:30); tracks 1 to 4 enter at 18:01 to 18:04; track 1
    /// exits at 18:05, track 2 walks back out, track 3 is seen once and lost, track 4 is censored after 5 minutes; the
    /// queue zone reads 6 at 18:10 (above its capacity of 5) and 4 at 18:14:30.
    /// </summary>
    private static (List<ZoneHealthBin> Health, List<BinResult> Bins, QueueStateEngine Engine, BinAccumulator Accumulator) Evening(double until = 16)
    {
        var engine = new QueueStateEngine(Geometry(), Settings);
        var bins = new BinAccumulator("A-VIS", 12, new BinSettings(), Geometry());
        var (_, update) = Play(engine, bins, until,
            Occupancy(-0.5, 2), In(1, "S-15/1"), In(2, "S-15/2"), In(3, "S-15/3"), new QueueTrackSeen("S-15/3", At(3.2)), In(4, "S-15/4"),
            new QueueCrossing("A-VIS entry", CrossingDirection.Out, "S-15/2", At(4.5)), Out(5, "S-15/1"), Occupancy(10, 6), Occupancy(14.5, 4));
        return ([.. update.Health], [.. update.Bins], engine, bins);
    }

    [Fact]
    public void Accept_Should_ReportTheBinsHealthWithItsResult_When_TheBinIsFinal()
    {
        var (health, bins, _, _) = Evening();

        health.Should().HaveSameCount(bins);
        var bin = Latest(health, 0);
        bin.Status.Should().Be(BinStatus.Final);
        bin.Should().Be(new ZoneHealthBin("A-VIS", At(0), TimeSpan.FromMinutes(15), 1, BinStatus.Final, 12, 4, 1,
            OccupancyStart: 2, OccupancyEnd: 4, ConservationResidual: 1,
            TracksEntered: 4, TracksExited: 1, TracksAbandoned: 1, TracksFragmented: 1, TracksCensored: 1, TracksRejected: 0, TracksOpen: 0,
            TrackCompletionRate: 0.25, OccupancyMinutes: 2, CapacityMinutes: 2, MinutesOutsideCapacity: 1));
        Latest(health, 15).OccupancyStart.Should().Be(4, "the 18:15 bin starts where the 18:00 bin ends");
    }

    [Fact]
    public void Accept_Should_ReportOpenTracksAndNoResidual_When_TheBinIsStillProvisional()
    {
        var (health, _, _, _) = Evening(until: 5.2);

        var bin = Latest(health, 0);
        bin.Status.Should().Be(BinStatus.Provisional);
        bin.TracksEntered.Should().Be(4);
        bin.TracksAbandoned.Should().Be(1);
        bin.TracksFragmented.Should().Be(1, "track 3 was not seen within the hand-over window");
        bin.TracksOpen.Should().Be(2, "tracks 1 and 4 are still in the queue");
        bin.TrackCompletionRate.Should().Be(0, "none has exited yet");
        bin.ConservationResidual.Should().BeNull("the bin has not ended");
    }

    [Fact]
    public void Accept_Should_GiveAZeroResidualAndNoRate_When_ABinHasNoEntries()
    {
        var engine = new QueueStateEngine(Geometry(), Settings);
        var bins = new BinAccumulator("A-VIS", 12, new BinSettings(), Geometry());
        // One person through the 18:00 bin opens the zone's bins; the 18:15 bin then has readings but nobody entering.
        var (_, update) = Play(engine, bins, 31, In(1), Out(2), Occupancy(14.5, 4), Occupancy(20, 4), Occupancy(29.5, 4));

        var bin = Latest(update.Health, 15);

        bin.Status.Should().Be(BinStatus.Final);
        bin.Entries.Should().Be(0);
        bin.ConservationResidual.Should().Be(0);
        bin.TrackCompletionRate.Should().BeNull();
        bin.TracksOpen.Should().Be(0);
        bin.MinutesOutsideCapacity.Should().Be(0);
        bin.OccupancyMinutes.Should().Be(2);
    }

    [Fact]
    public void Accept_Should_CheckEachZoneAgainstItsOwnCapacity_When_BandsHaveOne()
    {
        var engine = new QueueStateEngine(Geometry(queueCapacity: null, bandCapacity: 3), Settings);
        var bins = new BinAccumulator("A-VIS", 12, new BinSettings(), Geometry(queueCapacity: null, bandCapacity: 3));
        var (_, update) = Play(engine, bins, 16, In(0.5), Occupancy(1, 900), Occupancy(2, 4, "A-OV"), Occupancy(3, 3, "A-OV"));

        var bin = Latest(update.Health, 0);

        bin.OccupancyMinutes.Should().Be(3);
        bin.CapacityMinutes.Should().Be(2, "the queue zone has no capacity, so its minute is observed but not checked");
        bin.MinutesOutsideCapacity.Should().Be(1, "the band held 4 at 18:02, above its 3");
    }

    [Fact]
    public void Restore_Should_ContinueWithTheSameHealth_When_TheBinsAreRestoredMidBin()
    {
        var inputs = new QueueInput[]
        {
            Occupancy(-0.5, 2), In(1, "S-15/1"), In(2, "S-15/2"), Out(4, "S-15/1"), Out(5, "S-15/2"), Occupancy(6, 9), Occupancy(14.5, 2)
        };
        var engine = new QueueStateEngine(Geometry(), Settings);
        var whole = new BinAccumulator("A-VIS", 12, new BinSettings(), Geometry());
        var split = new BinAccumulator("A-VIS", 12, new BinSettings(), Geometry());
        foreach (var i in inputs)
            engine.Offer(i, i.TimeUtc);
        var steps = new[] { engine.Advance(At(6.6)), engine.Advance(At(16)) };

        var expected = steps.SelectMany(s => whole.Accept(s).Health).ToList();
        var before = split.Accept(steps[0]).Health;
        var state = JsonSerializer.Deserialize<BinAccumulatorState>(JsonSerializer.Serialize(split.Capture()))!;
        var restored = BinAccumulator.Restore(new BinSettings(), state, Geometry());
        var after = restored.Accept(steps[1]).Health;

        before.Concat(after).Should().Equal(expected);
        Latest(expected, 0).Should().Match<ZoneHealthBin>(h => h.ConservationResidual == 0 && h.TrackCompletionRate == 1 && h.MinutesOutsideCapacity == 1);
    }

    public static TheoryData<string, Func<BinHealthState, BinHealthState>> HostileBinHealth => new()
    {
        { "a negative track count", h => h with { TracksExited = -1 } },
        { "a negative occupancy", h => h with { OccupancyStart = -4 } },
        { "an opening occupancy above the queue's bound", h => h with { OccupancyStart = 20_001 } },
        { "a closing occupancy above the queue's bound", h => h with { OccupancyEnd = 20_001 } },
        { "a minute outside the bin", h => h with { Minutes = [new HealthMinuteState(At(15), true, false)] } },
        { "a minute twice", h => h with { Minutes = [new HealthMinuteState(At(1), true, false), new HealthMinuteState(At(1), false, true)] } },
        { "a minute off the minute", h => h with { Minutes = [new HealthMinuteState(At(1.5), true, false)] } },
        { "more minutes than the bin has", h => h with { Minutes = [.. Enumerable.Range(0, 16).Select(m => new HealthMinuteState(At(m), true, false))] } }
    };

    [Theory]
    [MemberData(nameof(HostileBinHealth))]
    public void Restore_Should_RefuseHealthTalliesTheBinsNeverWrite_When_TheSnapshotIsHostile(string because, Func<BinHealthState, BinHealthState> tamper)
    {
        var (_, _, _, accumulator) = Evening(until: 5.2);
        var state = accumulator.Capture();
        var open = state.Open.Single(b => b.StartUtc == At(0));
        var hostile = state with { Open = [.. state.Open.Select(b => b == open ? b with { Health = tamper(b.Health!) } : b)] };

        var restore = () => BinAccumulator.Restore(new BinSettings(), hostile, Geometry());

        restore.Should().Throw<InvalidDataException>(because);
    }

    [Fact]
    public void Restore_Should_StartTheHealthEmpty_When_TheSnapshotPredatesIt()
    {
        var (_, _, _, accumulator) = Evening(until: 5.2);
        var state = accumulator.Capture();
        var old = state with { Open = [.. state.Open.Select(b => b with { Health = null })] };

        var restored = BinAccumulator.Restore(new BinSettings(), old, Geometry());

        restored.OpenBins.Should().Be(state.Open.Count);
    }

    #endregion
}
