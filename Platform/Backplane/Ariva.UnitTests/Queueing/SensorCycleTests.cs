using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-117b: the sensor cycle time of the shadow nowcast (F8, docs/domain/formulas.md "Sensor cycle time of the shadow").
/// c = the lane's sensor-only Serving desk minutes over the queue's exits in the same W whole minutes (Proposed W = 10,
/// at least 10 exits, 0.2 to 10 minutes, at most 5 percent Unknown desk time); otherwise no c and the F8 fallback.
/// </summary>
public sealed class SensorCycleTests
{
    private static readonly DateTime T = new(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);

    // The window ends at T + 9 (the desk term's minute) and starts at T.
    private static readonly DateTime AsOf = T.AddMinutes(9);

    private static readonly SensorCycleSettings Settings = new();

    // One desk minute with its sensor-only minute: serving and idle seconds from the zones alone.
    private static DeskMinuteSample Minute(int desk, int minute, double serving = 60, double idle = 0, double unknown = 0, bool degraded = false) =>
        new($"AUH/IMM/D{desk:00}", T.AddMinutes(minute), 0, 60, 0, 2, false, 0, new DeskSensorSample(serving + idle, unknown, degraded, serving));

    private static List<DeskMinuteSample> Lane(int desks = 6, double serving = 60, double idle = 0, int minutes = 10) =>
        [.. Enumerable.Range(0, desks).SelectMany(d => Enumerable.Range(0, minutes).Select(m => Minute(d, m, serving, idle)))];

    private static ExitWindow Exits(long exits, int observed = 10, int window = 10, long degraded = 0) => new(exits, observed, window, degraded);

    private static SensorCycleResult Cycle(IEnumerable<DeskMinuteSample> minutes, ExitWindow exits, SensorCycleSettings settings = null) =>
        SensorCycle.Compute(SensorCycle.Window(minutes, AsOf, settings ?? Settings), exits, settings ?? Settings);

    public static TheoryData<string, int, double, double, long, double?, SensorCycleFallback?> Cases => new()
    {
        // case, desks, serving s, idle s (per desk minute), exits in the window, c, fallback
        { "typical: six desks serving all ten minutes, 40 exits", 6, 60, 0, 40, 1.5, null },
        { "idle time is not busy time (open over exits would be 3.0)", 6, 30, 30, 20, 1.5, null },
        { "too few exits", 6, 60, 0, 9, null, SensorCycleFallback.TooFewExits },
        { "exactly the minimum", 6, 6, 0, 10, 0.6, null },
        { "zero exits", 6, 60, 0, 0, null, SensorCycleFallback.TooFewExits },
        { "open time with no exits", 6, 0, 60, 0, null, SensorCycleFallback.TooFewExits },
        { "open time, no busy time, exits: below the bounds", 6, 0, 60, 40, null, SensorCycleFallback.OutOfBounds },
        { "below the bounds: 0.15 minutes a passenger", 6, 60, 0, 400, null, SensorCycleFallback.OutOfBounds },
        { "at the lower bound", 6, 60, 0, 300, 0.2, null },
        { "above the bounds: 12.5 minutes a passenger", 15, 60, 0, 12, null, SensorCycleFallback.OutOfBounds },
        { "at the upper bound", 15, 60, 0, 15, 10, null }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compute_Should_GiveTheF8Case_When_TheLaneServes(string name, int desks, double serving, double idle, long exits, double? cycle, SensorCycleFallback? fallback)
    {
        var result = Cycle(Lane(desks, serving, idle), Exits(exits));

        if (cycle is { } c)
        {
            result.CycleMinutes.Should().BeApproximately(c, 1e-9, name);
            result.Fallback.Should().BeNull(name);
            result.Degraded.Should().BeFalse(name);
        }
        else
        {
            result.CycleMinutes.Should().BeNull(name);
            result.Fallback.Should().Be(fallback, name);
            result.Degraded.Should().BeTrue("{0}: a fallback is flagged", name);
        }
    }

    [Fact]
    public void Window_Should_LeaveOutAndCountTheMinute_When_ItIsNotANumberOrNegative()
    {
        // F8 case: NaN and negative inputs. A refused desk minute is left out, counted and counted as Unknown for the
        // minute: 1 of 60 desk minutes (1.7 percent, within 5) still gives c from the other 59, flagged.
        foreach (var bad in new[]
                 {
                     new DeskSensorSample(60, 0, false, double.NaN), new DeskSensorSample(60, 0, false, -5), new DeskSensorSample(double.NaN, 0, false, 60),
                     new DeskSensorSample(60, double.PositiveInfinity, false, 60), new DeskSensorSample(60, -1, false, 60), new DeskSensorSample(30, 0, false, 45),
                     new DeskSensorSample(61, 0, false, 0), new DeskSensorSample(60, 30, false, 60)
                 })
        {
            var lane = Lane();
            lane[0] = lane[0] with { Sensor = bad };

            var window = SensorCycle.Window(lane, AsOf, Settings);
            var result = SensorCycle.Compute(window, Exits(40), Settings);

            window.LeftOut.Should().Be(1, "{0} is refused", bad);
            window.DeskMinutes.Should().Be(59);
            result.CycleMinutes.Should().BeApproximately(59.0 / 40, 1e-9);
            result.Degraded.Should().BeTrue();
        }

        // Four refused of 60 (6.7 percent): too much of the window is not known.
        var many = Lane();
        for (var k = 0; k < 4; k++)
            many[k] = many[k] with { Sensor = new DeskSensorSample(60, 0, false, double.NaN) };
        Cycle(many, Exits(40)).Fallback.Should().Be(SensorCycleFallback.UnknownDesks);
    }

    [Fact]
    public void Window_Should_FlagOrRefuseTheCycleTime_When_DeskMinutesAreFlaggedOrUnknown()
    {
        // F8 case: a window with flagged or Unknown desk minutes. A flagged minute counts its busy time and flags c.
        var flagged = Lane();
        flagged[3] = Minute(0, 3, degraded: true);
        var result = Cycle(flagged, Exits(40));
        result.CycleMinutes.Should().BeApproximately(1.5, 1e-9);
        result.Degraded.Should().BeTrue();

        // Unknown 30 seconds in six desk minutes: 180 of 3,600 desk seconds, exactly 5 percent: c, flagged.
        var some = Lane();
        for (var k = 0; k < 6; k++)
            some[k * 10] = Minute(k, 0, serving: 30, unknown: 30);
        result = Cycle(some, Exits(40));
        result.CycleMinutes.Should().BeApproximately((3_600 - 180) / 60.0 / 40, 1e-9);
        result.Degraded.Should().BeTrue();

        // Seven: above 5 percent, no c (an Unknown desk may have served exits whose busy time is missing).
        var more = Lane();
        for (var k = 0; k < 7; k++)
            more[k] = Minute(0, k, serving: 30, unknown: 30);
        Cycle(more, Exits(40)).Fallback.Should().Be(SensorCycleFallback.UnknownDesks);

        // A desk missing from one minute counts as Unknown there (1 of 60), flagged; missing from four, refused.
        var gap = Lane().Where(m => !(m.DeskKey.EndsWith("D02", StringComparison.Ordinal) && m.MinuteUtc == T.AddMinutes(5))).ToList();
        result = Cycle(gap, Exits(40));
        result.CycleMinutes.Should().BeApproximately(59.0 / 40, 1e-9);
        result.Degraded.Should().BeTrue();
        Cycle(Lane().Where(m => !(m.DeskKey.EndsWith("D02", StringComparison.Ordinal) && m.MinuteUtc < T.AddMinutes(4))), Exits(40)).Fallback
            .Should().Be(SensorCycleFallback.UnknownDesks);
    }

    [Fact]
    public void Window_Should_GiveNoCycleTime_When_ItsDesksOrMinutesAreNotAllSensed()
    {
        // A desk of the lane without a sensor-only minute (no zones, or a minute before ARV-117a): its busy time is not known.
        var mixed = Lane();
        mixed.Add(new DeskMinuteSample("AUH/IMM/X", T.AddMinutes(4), 0, 60, 0, 2, false));
        Cycle(mixed, Exits(40)).Fallback.Should().Be(SensorCycleFallback.DesksWithoutSensors);

        // A minute of the window without any desk minute.
        Cycle(Lane().Where(m => m.MinuteUtc != T.AddMinutes(7)), Exits(40)).Fallback.Should().Be(SensorCycleFallback.IncompleteWindow);
        Cycle([], Exits(40)).Fallback.Should().Be(SensorCycleFallback.NoDeskMinutes);
        Cycle(Lane(minutes: 10).Select(m => m with { MinuteUtc = m.MinuteUtc.AddMinutes(-20) }), Exits(40)).Fallback
            .Should().Be(SensorCycleFallback.NoDeskMinutes, "minutes before the window do not count");

        // Minutes after the window's end and duplicates of a desk minute do not count.
        var later = Lane();
        later.AddRange(Lane().Select(m => m with { MinuteUtc = m.MinuteUtc.AddMinutes(10), Sensor = new DeskSensorSample(60, 0, false, 0) }));
        later.AddRange(Lane().Select(m => m with { Sensor = new DeskSensorSample(60, 0, false, 0) }));
        Cycle(later, Exits(40)).CycleMinutes.Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public void Compute_Should_GiveNoCycleTime_When_TheExitsAreNotKnownForTheWindow()
    {
        var window = SensorCycle.Window(Lane(), AsOf, Settings);

        SensorCycle.Compute(window, null, Settings).Fallback.Should().Be(SensorCycleFallback.NoExitData);
        SensorCycle.Compute(window, Exits(40, observed: 9), Settings).Fallback.Should().Be(SensorCycleFallback.NoExitData, "a minute not observed is not zero exits");
        SensorCycle.Compute(window, Exits(40, observed: 5, window: 5), Settings).Fallback.Should().Be(SensorCycleFallback.NoExitData, "another window");
        SensorCycle.Compute(window, Exits(-40), Settings).Fallback.Should().Be(SensorCycleFallback.NoExitData);
        SensorCycle.Compute(window, Exits(40, degraded: -1), Settings).Fallback.Should().Be(SensorCycleFallback.NoExitData);
        SensorCycle.Compute(null, Exits(40), Settings).Fallback.Should().Be(SensorCycleFallback.NoDeskMinutes);

        var flagged = SensorCycle.Compute(window, Exits(40, degraded: 3), Settings);
        flagged.CycleMinutes.Should().BeApproximately(1.5, 1e-9);
        flagged.Degraded.Should().BeTrue("degraded exits flag the cycle time");
    }

    [Fact]
    public void Window_Should_StopAtTheCap_When_ALaneHoldsTooManyDeskMinutes()
    {
        // CWE-120: beyond MaxDeskMinutes the window is not aggregated at all, never from an arbitrary part of it.
        var settings = new SensorCycleSettings { MaxDeskMinutes = 100 };
        SensorCycle.Window(Lane(desks: 10), AsOf, settings).Missing.Should().BeNull("100 desk minutes is the cap");
        var window = SensorCycle.Window(Lane(desks: 11), AsOf, settings);
        window.Missing.Should().Be(SensorCycleFallback.TooManyMinutes);
        window.DeskMinutes.Should().Be(101);
        SensorCycle.Compute(window, Exits(40), settings).CycleMinutes.Should().BeNull();

        // An endless sequence stops at the cap.
        IEnumerable<DeskMinuteSample> Endless()
        {
            for (var k = 0; ; k++)
                yield return Minute(k, k % 10);
        }

        SensorCycle.Window(Endless(), AsOf, settings).Missing.Should().Be(SensorCycleFallback.TooManyMinutes);
    }

    [Fact]
    public void Settings_Should_BeRefused_When_OutOfTheirRanges()
    {
        new SensorCycleSettings().Problems().Should().BeEmpty();
        SensorCycleSettings[] bad =
        [
            new() { WindowMinutes = 1 }, new() { WindowMinutes = 11 }, new() { MinimumExits = 0 }, new() { MinimumExits = 10_001 },
            new() { MinimumCycleMinutes = 0.04 }, new() { MinimumCycleMinutes = double.NaN }, new() { MaximumCycleMinutes = 0.2 },
            new() { MaximumCycleMinutes = 61 }, new() { MaxUnknownShare = -0.1 }, new() { MaxUnknownShare = 0.6 }, new() { MaxDeskMinutes = 99 },
            new() { MaxDeskMinutes = 20_001 }, new() { MaximumCycleMinutes = double.NaN },
            new() { MaximumCycleMinutes = double.PositiveInfinity }, new() { MaxUnknownShare = double.NaN }
        ];
        foreach (var settings in bad)
        {
            settings.Problems().Should().NotBeEmpty("{0}", settings);
            var window = () => SensorCycle.Window([], AsOf, settings);
            window.Should().Throw<ArgumentException>();
        }

        new SensorCycleSettings { WindowMinutes = 2, MaxDeskMinutes = 20_000, MinimumCycleMinutes = 0.05, MaximumCycleMinutes = 60 }.Problems().Should().BeEmpty();
        new Ariva.Core.Queueing.ZoneProcessorSettings { SensorCycle = null }.Problems().Should().Contain("SensorCycle settings are required.");
    }

    [Fact]
    public void SensorSample_Should_RefuseTheRow_When_ItFailsItsBoundsAsReadBack()
    {
        // CWE-501: desk_sensor_minute values read back by the stream's desk term are checked against script 0044's bounds
        // (each state from 0 to 60 seconds, together at most a minute, a flag); a row that fails is left out (null) and
        // counted by DeskTermSource, which warns with the site and the count only.
        Ariva.Infra.Streaming.DeskTermSource.SensorSample(10, 20, 25, 0, 5, false).Should().Be(new DeskSensorSample(45, 5, false, 25));
        Ariva.Infra.Streaming.DeskTermSource.SensorSample(0, 0, 60, 0, 0, true).Should().Be(new DeskSensorSample(60, 0, true, 60));
        Ariva.Infra.Streaming.DeskTermSource.SensorSample(12, 12, 12, 12, 12.0005, false).Should().NotBeNull("0044's rounding room");
        foreach (var (closed, idle, serving, paused, unknown, flag) in new (double, double, double, double, double, bool?)[]
                 {
                     (double.NaN, 0, 60, 0, 0, false), (0, double.PositiveInfinity, 0, 0, 0, false), (0, 0, -1, 0, 0, false), (0, 0, 61, 0, 0, false),
                     (0, 0, 0, -0.5, 0, false), (0, 0, 0, 0, 60.5, false), (30, 0, 31, 0, 0, false), (20, 20, 20, 0.01, 0.01, false), (0, 0, 60, 0, 0, null)
                 })
            Ariva.Infra.Streaming.DeskTermSource.SensorSample(closed, idle, serving, paused, unknown, flag).Should().BeNull("{0} {1} {2} {3} {4} {5}", closed, idle, serving, paused, unknown, flag);
    }

    [Fact]
    public void Apply_Should_GiveTheShadowTermTheSensorCycleTime_When_ThereIsOne()
    {
        var term = new DeskTerm(AsOf, 6, null, false, 6);

        SensorCycle.Apply(term, new SensorCycleResult(1.5, null, false)).Should().Be(term with { CycleMinutes = 1.5 });
        SensorCycle.Apply(term, new SensorCycleResult(1.5, null, true)).Should().Be(term with { CycleMinutes = 1.5, Degraded = true });
        SensorCycle.Apply(term with { CycleMinutes = 2 }, new SensorCycleResult(null, SensorCycleFallback.TooFewExits, true)).Should().Be(term,
            "no cycle time: Nowcast.Compute falls back to the exit term alone, which it flags, or to no service");
        SensorCycle.Apply(term with { OpenServers = 0 }, new SensorCycleResult(null, SensorCycleFallback.TooFewExits, true)).Should().Be(term with { OpenServers = 0 },
            "with nothing open the cycle time is not needed: no service, flagged only as the term is");

        // The fallback is flagged by the nowcast itself (F8 sensor-outage fallback): the exit term alone is always Degraded.
        var fallback = Nowcast.Compute(new NowcastInput { QueueLength = 29, ExitsInWindow = 18, OpenServers = 6, CycleMinutes = null });
        fallback.Source.Should().Be(ThroughputSource.Exits);
        fallback.Degraded.Should().BeTrue();
        SensorCycle.Apply(null, new SensorCycleResult(1.5, null, false)).Should().BeNull();
    }
}
