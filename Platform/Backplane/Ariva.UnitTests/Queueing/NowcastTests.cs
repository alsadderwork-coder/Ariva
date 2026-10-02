using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-032: the nowcast and throughput of formula F8 with its documented cases, the no-service reasons, the e-gate and
/// fast-track variants, the display bands with hysteresis and the degraded band, and the reference evening measured
/// through the emulator and the queue engine crossing 15 minutes at 18:05 as the scenario does.
/// </summary>
public sealed class NowcastTests
{
    private static readonly NowcastSettings Desks = new() { Beta = 1 };

    [Fact]
    public void Compute_Should_GiveSevenAndAHalfMinutes_When_SixDesksCycleInOneAndAHalf()
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = 6, CycleMinutes = 1.5 });

        result.Throughput.Should().BeApproximately(4.0, 1e-9);
        result.Minutes.Should().BeApproximately(7.5, 1e-9);
        result.Source.Should().Be(ThroughputSource.Desks);
        NowcastDisplays.Next(NowcastDisplayState.Initial, result, stale: false).Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.Band, 5, 10));
    }

    [Fact]
    public void Compute_Should_BlendDesksAndExits_When_BothAreKnown()
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = 6, CycleMinutes = 1.5, ExitsInWindow = 18, ExitWindowMinutes = 5 });

        result.Throughput.Should().BeApproximately(3.8, 1e-9);
        result.Minutes.Should().BeApproximately(30 / 3.8, 1e-9);
        Math.Round(result.Minutes!.Value, 2).Should().Be(7.89);
        result.Source.Should().Be(ThroughputSource.Blend);
        result.Degraded.Should().BeFalse();
    }

    [Fact]
    public void Compute_Should_HalveTheThroughput_When_AFastTrackSharesHalfTheDesks()
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = 6, CycleMinutes = 1.5, MergeShare = 0.5 }, Desks);

        result.Minutes.Should().BeApproximately(15, 1e-9);
    }

    [Fact]
    public void Compute_Should_DiscountRejects_When_TheServersAreEgates()
    {
        Nowcast.EgateRate(4, 0.3, 0.07).Should().BeApproximately(4 / 0.3 * 0.93, 1e-9);
        Nowcast.EgateRate(0, 0.3, 0.07).Should().BeNull();
        Nowcast.Compute(new NowcastInput { QueueLength = 30, OpenServers = 4, CycleMinutes = 0.3, RejectRate = 0.07 }).Throughput
            .Should().BeApproximately(4 / 0.3 * 0.93, 1e-9);
    }

    [Theory]
    [InlineData(29, 0, 1.5, 18L, NoServiceReason.NothingOpen)]
    [InlineData(29, 1, 500.0, null, NoServiceReason.ThroughputTooLow)]
    [InlineData(29, null, null, null, NoServiceReason.NoThroughputData)]
    [InlineData(null, 6, 1.5, null, NoServiceReason.NoQueueLength)]
    public void Compute_Should_GiveNoServiceWithAReason_When_ThereIsNoThroughput(int? queue, int? open, double? cycle, long? exits, NoServiceReason reason)
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = queue, OpenServers = open, CycleMinutes = cycle, ExitsInWindow = exits });

        result.Minutes.Should().BeNull("the nowcast is undefined, never infinite or zero");
        result.NoService.Should().Be(reason);
        NowcastDisplays.Next(NowcastDisplayState.Initial, result, stale: false).Shown.Kind.Should().Be(NowcastDisplayKind.Neutral);
    }

    [Fact]
    public void Compute_Should_FlagTheExitOnlyEstimate_When_DeskStateIsUnknown()
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, ExitsInWindow = 20 });

        result.Minutes.Should().BeApproximately(7.5, 1e-9);
        result.Source.Should().Be(ThroughputSource.Exits);
        result.Degraded.Should().BeTrue("without desk state the estimate lags every desk change");
    }

    [Fact]
    public void Display_Should_KeepItsBand_When_TheNowcastMovesLessThanABand()
    {
        var at12 = NowcastDisplays.Next(NowcastDisplayState.Initial, new NowcastResult(12, 2, ThroughputSource.Desks, null, false), false);
        var at155 = NowcastDisplays.Next(at12, new NowcastResult(15.5, 2, ThroughputSource.Desks, null, false), false);
        var at172 = NowcastDisplays.Next(at155, new NowcastResult(17.2, 2, ThroughputSource.Desks, null, false), false);

        at12.Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.Band, 10, 15));
        at155.Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.Band, 10, 15), "15.5 is within 5 minutes of 12");
        at172.Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.Band, 15, 20));
        at172.ReferenceMinutes.Should().Be(17.2);
    }

    [Theory]
    [InlineData(12, 5, 15)]
    [InlineData(4, 0, 10)]
    [InlineData(40, 30, 50)]
    public void Display_Should_ShowTheDegradedBand_When_TheZoneIsDegraded(double minutes, int from, int to)
    {
        var shown = NowcastDisplays.Next(NowcastDisplayState.Initial, new NowcastResult(minutes, 2, ThroughputSource.Desks, null, true), false).Shown;

        shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.DegradedBand, from, to));
    }

    [Fact]
    public void Display_Should_FollowItsPrecedence_When_SeveralRulesApply()
    {
        var degraded = new NowcastResult(3, 2, ThroughputSource.Desks, null, true);

        NowcastDisplays.Next(NowcastDisplayState.Initial, degraded, stale: true).Shown.Kind.Should().Be(NowcastDisplayKind.Neutral, "stale data comes first");
        NowcastDisplays.Next(NowcastDisplayState.Initial, degraded, stale: false).Shown.Kind.Should().Be(NowcastDisplayKind.DegradedBand, "a degraded zone before under 5");
        NowcastDisplays.Next(NowcastDisplayState.Initial, degraded with { Degraded = false }, false).Shown
            .Should().Be(new NowcastDisplay(NowcastDisplayKind.UnderFive, 0, 5));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Settings_Should_BeValidated_When_TheBetaIsOutOfRange(double beta)
    {
        Action compute = () => Nowcast.Compute(new NowcastInput { QueueLength = 1 }, new NowcastSettings { Beta = beta });

        compute.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExitRate_Should_SumTheWholeMinutesBefore_When_Asked()
    {
        var rate = new ExitRate();
        var t = new DateTime(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
        rate.Add([new MovementCount(t, 0, 3, 0, 0), new MovementCount(t.AddMinutes(1), 1, 4, 0, 0), new MovementCount(t.AddMinutes(5), 0, 9, 0, 0)]);

        rate.ExitsBefore(t.AddMinutes(5.5), 5).Should().Be(7, "18:00 to 18:04, not the current minute");
        rate.ExitsBefore(t.AddMinutes(6), 1).Should().Be(9);
        Action wide = () => rate.ExitsBefore(t, 61);
        wide.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1e-300, null, NoServiceReason.NoThroughputData)]
    [InlineData(double.Epsilon, null, NoServiceReason.NoThroughputData)]
    [InlineData(0.0, null, NoServiceReason.NoThroughputData)]
    [InlineData(-1.0, null, NoServiceReason.NoThroughputData)]
    [InlineData(double.PositiveInfinity, null, NoServiceReason.NoThroughputData)]
    [InlineData(double.NaN, null, NoServiceReason.NoThroughputData)]
    [InlineData(0.01, 0L, NoServiceReason.ThroughputTooLow)]
    public void Compute_Should_NeverGiveANonFiniteWait_When_TheCycleTimeIsNotReal(double cycle, long? exits, NoServiceReason? reason)
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = 6, CycleMinutes = cycle, ExitsInWindow = exits }, Desks);

        result.NoService.Should().Be(reason);
        result.Minutes.Should().BeNull();
        if (result.Throughput is { } mu)
            double.IsFinite(mu).Should().BeTrue();
    }

    [Fact]
    public void Compute_Should_IgnoreATinyCycleTime_When_AnExitRateIsKnown()
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = 6, CycleMinutes = 1e-300, ExitsInWindow = 20 });

        result.Source.Should().Be(ThroughputSource.Exits, "a 3-second-or-less cycle is not real and counts as unknown");
        result.Minutes.Should().BeApproximately(7.5, 1e-9);
        result.Degraded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-3.0, 0.0)]
    [InlineData(1.0, 7.0)]
    public void Compute_Should_GiveNoService_When_TheShareOrRejectsLeaveNoThroughput(double share, double rejects)
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = 6, CycleMinutes = 1.5, MergeShare = share, RejectRate = rejects });

        result.Minutes.Should().BeNull();
        result.NoService.Should().Be(NoServiceReason.ThroughputTooLow);
        result.Throughput.Should().Be(0);
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 0.0)]
    [InlineData(1.0, double.NaN)]
    [InlineData(1.0, double.NegativeInfinity)]
    public void Compute_Should_FlagTheResult_When_TheShareOrRejectRateIsUnknown(double share, double rejects)
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = 6, CycleMinutes = 1.5, MergeShare = share, RejectRate = rejects });

        result.Degraded.Should().BeTrue("an unknown multiplier is not silently assumed optimistic");
        result.Minutes.Should().BeApproximately(7.5, 1e-9);
    }

    [Fact]
    public void Compute_Should_StayFinite_When_TheInputsAreExtreme()
    {
        var huge = Nowcast.Compute(new NowcastInput { QueueLength = int.MaxValue, OpenServers = 1, CycleMinutes = 0.05 });
        huge.Minutes.Should().BeApproximately((int.MaxValue + 1.0) * 0.05, 1);
        NowcastDisplays.Next(NowcastDisplayState.Initial, huge, false).Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.AboveCeiling, 120, null));

        var flood = Nowcast.Compute(new NowcastInput { QueueLength = 10, ExitsInWindow = long.MaxValue, ExitWindowMinutes = 1 });
        flood.NoService.Should().Be(NoServiceReason.Implausible);
        flood.Minutes.Should().BeNull();

        var many = Nowcast.Compute(new NowcastInput { QueueLength = 10, OpenServers = int.MaxValue, CycleMinutes = 0.05 });
        many.NoService.Should().Be(NoServiceReason.Implausible, "43 billion passengers a minute is not a real desk hall");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(61.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compute_Should_IgnoreTheExits_When_TheWindowIsOutOfRange(double window)
    {
        var result = Nowcast.Compute(new NowcastInput { QueueLength = 29, ExitsInWindow = 20, ExitWindowMinutes = window });

        result.NoService.Should().Be(NoServiceReason.NoThroughputData);
    }

    [Fact]
    public void Compute_Should_GiveNoNumber_When_TheThroughputIsBeyondReal()
    {
        var corrupt = Nowcast.Compute(new NowcastInput { QueueLength = 400, ExitsInWindow = long.MaxValue, ExitWindowMinutes = 5 });
        corrupt.Minutes.Should().BeNull("a corrupt count must not show \"under 5 min\" for any queue");
        corrupt.NoService.Should().Be(NoServiceReason.Implausible);

        Nowcast.Compute(new NowcastInput { QueueLength = 400, ExitsInWindow = 4000, ExitWindowMinutes = 5 }).Minutes.Should().BeApproximately(0.50125, 1e-9);
        Action bad = () => Nowcast.Compute(new NowcastInput { QueueLength = 1 }, new NowcastSettings { MaximumRate = 0.001 });
        bad.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(4, 1e-9, 0.0)]
    [InlineData(4, double.NaN, 0.0)]
    [InlineData(4, double.PositiveInfinity, 0.0)]
    [InlineData(4, 0.3, double.NaN)]
    [InlineData(-1, 0.3, 0.0)]
    public void EgateRate_Should_GiveNothing_When_TheInputsAreNotReal(int gates, double cycle, double rejects)
    {
        Nowcast.EgateRate(gates, cycle, rejects).Should().BeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void EgateRate_Should_GiveNothing_When_TheMinimumCycleIsNotPositive(double minimum)
    {
        Nowcast.EgateRate(1, double.Epsilon, 0, minimum).Should().BeNull();
    }

    [Theory]
    [InlineData(120.0, false)]
    [InlineData(130.0, false)]
    [InlineData(1e300, false)]
    [InlineData(120.0, true)]
    [InlineData(200.0, true)]
    public void Display_Should_ShowOverTheCeiling_When_TheWaitIsBeyondIt(double minutes, bool degraded)
    {
        var shown = NowcastDisplays.Next(NowcastDisplayState.Initial, new NowcastResult(minutes, 1, ThroughputSource.Desks, null, degraded), false).Shown;

        shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.AboveCeiling, NowcastDisplays.Ceiling, null));
    }

    [Fact]
    public void Display_Should_NotFlap_When_TheWaitHoversAtFiveMinutes()
    {
        static NowcastResult At(double w) => new(w, 2, ThroughputSource.Desks, null, false);
        var state = NowcastDisplays.Next(NowcastDisplayState.Initial, At(4.8), false);
        state.Shown.Kind.Should().Be(NowcastDisplayKind.UnderFive);

        foreach (var w in new[] { 5.2, 4.9, 5.4, 4.7, 6.1 })
        {
            state = NowcastDisplays.Next(state, At(w), false);
            state.Shown.Kind.Should().Be(NowcastDisplayKind.UnderFive, "{0} is within 5 minutes of 4.8", w);
        }

        state = NowcastDisplays.Next(state, At(9.9), false);
        state.Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.Band, 5, 10));
    }

    [Fact]
    public void Display_Should_HoldTheDegradedBand_When_TheWaitMovesLessThanABand()
    {
        static NowcastResult At(double w, bool degraded) => new(w, 2, ThroughputSource.Exits, null, degraded);
        var state = NowcastDisplays.Next(NowcastDisplayState.Initial, At(12, true), false);
        state.Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.DegradedBand, 5, 15));

        state = NowcastDisplays.Next(state, At(15.9, true), false);
        state.Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.DegradedBand, 5, 15), "15.9 is within 5 minutes of 12");

        state = NowcastDisplays.Next(state, At(15.9, false), false);
        state.Shown.Should().Be(new NowcastDisplay(NowcastDisplayKind.Band, 15, 20), "leaving the degraded state changes the kind at once");

        state = NowcastDisplays.Next(state, At(16, true), false);
        state.Shown.Kind.Should().Be(NowcastDisplayKind.DegradedBand, "entering it does too");
    }

    [Fact]
    public void Display_Should_GoNeutral_When_ServiceStopsAfterABand()
    {
        var shown = NowcastDisplays.Next(NowcastDisplayState.Initial, new NowcastResult(12, 2, ThroughputSource.Desks, null, false), false);
        var stopped = NowcastDisplays.Next(shown, new NowcastResult(12, 0, ThroughputSource.Desks, NoServiceReason.NothingOpen, false), false);

        stopped.Shown.Kind.Should().Be(NowcastDisplayKind.Neutral, "a no-service result never shows its minutes");
        stopped.ReferenceMinutes.Should().BeNull();
        NowcastDisplays.Next(shown, new NowcastResult(double.NaN, 2, ThroughputSource.Desks, null, false), false).Shown.Kind.Should().Be(NowcastDisplayKind.Neutral);
    }

    [Fact]
    public void DegradedBand_Should_StayWithinTheCeilingAndTenMinutesWide_When_TheWaitIsAnything()
    {
        foreach (var w in new[] { 0, 0.1, 4, 12, 40, 95, 100, 110, 118, 119.9, 120, 500, 1e300, double.PositiveInfinity, double.NaN, -5 })
        {
            var (from, to) = NowcastDisplays.DegradedBand(w);
            to.Should().BeLessThanOrEqualTo(NowcastDisplays.Ceiling, "w = {0}", w);
            from.Should().BeGreaterThanOrEqualTo(0, "w = {0}", w);
            (to - from).Should().BeGreaterThanOrEqualTo(10, "w = {0}", w);
            (from % 5).Should().Be(0);
            (to % 5).Should().Be(0);
        }

        NowcastDisplays.DegradedBand(200).Should().Be((90, 120), "a wait beyond the ceiling is banded as the ceiling: 0.75 x 120 to 120");
    }

    [Fact]
    public void ExitRate_Should_ReportCoverage_When_MinutesAreMissing()
    {
        var rate = new ExitRate();
        var t = new DateTime(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
        rate.Observed(t, t.AddMinutes(5));
        rate.Add([new MovementCount(t.AddMinutes(2), 0, 4, 0, 1)]);

        var full = rate.Window(t.AddMinutes(5), 5);
        full.Should().Be(new ExitWindow(4, 5, 5, 1));
        full.Complete.Should().BeTrue();

        var gap = rate.Window(t.AddMinutes(8), 5);
        gap.ObservedMinutes.Should().Be(2, "18:03 and 18:04 were observed; 18:05 to 18:07 were not");
        gap.Complete.Should().BeFalse("a gap reads as unknown, not as zero exits");
        gap.Exits.Should().Be(0);
    }

    [Fact]
    public void ExitRate_Should_SaturateAndForget_When_FedHugeCountsAndLongRuns()
    {
        var rate = new ExitRate();
        var t = new DateTime(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
        rate.Add([new MovementCount(t, 0, long.MaxValue, 0, long.MaxValue), new MovementCount(t, 0, long.MaxValue, 0, 5), null!]);
        rate.Add([new MovementCount(t.AddMinutes(1), 0, long.MaxValue, 0, 0), new MovementCount(t.AddMinutes(2), 0, -7, 0, -7)]);

        var window = rate.Window(t.AddMinutes(3), 3);
        window.Exits.Should().Be(long.MaxValue);
        window.DegradedExits.Should().Be(long.MaxValue);

        rate.Observed(t, t.AddDays(30));
        rate.Window(t.AddDays(30), 60).Complete.Should().BeTrue("a long span marks its latest hour, the one the window reads");
        rate.Window(t.AddMinutes(3), 3).ObservedMinutes.Should().Be(0, "only an hour of minutes is kept");
        rate.Window(DateTime.MinValue, 5).ObservedMinutes.Should().Be(0);
        Action edge = () => rate.Observed(DateTime.MaxValue.AddSeconds(-30), DateTime.MaxValue);
        edge.Should().NotThrow();
        Action zero = () => rate.Window(t, 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Nowcast_Should_PassFifteenMinutesAt1805_When_TheVisitorsWaveIsMeasured()
    {
        // The emulator's S-15 traffic through Ingest's mapper and the engine; the exit rate stands in for the desk
        // term (which needs the desk state engine, ARV-033), so the result is flagged Degraded but the number holds.
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var midnight = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        DateTime WallOf(double minute) => midnight.AddMinutes(minute);
        var geometry = new QueueZoneGeometry("A-VIS", new HashSet<string> { "A-VIS entry" }, new HashSet<string> { "A-VIS exit" },
            new HashSet<string>(), new HashSet<string>());
        var engine = new QueueStateEngine(geometry, new QueueEngineSettings { Lateness = TimeSpan.Zero });
        var exits = new ExitRate();
        var sensor = SensorTraffic.Sensor("S-15");
        var nowcasts = new Dictionary<int, double?>();
        for (var minute = 1020; minute < 1100; minute++)
        {
            var push = SensorTraffic.Build(day, sensor, EmulatedDialect.Canonical, minute, minute, WallOf, WallOf(minute + 1));
            using var document = JsonDocument.Parse(push.Json);
            var mapped = CanonicalPushMapper.Map(document.RootElement, 3000);
            foreach (var c in mapped.Crossings)
                engine.Offer(new QueueCrossing(c.LineName, c.Direction, "S-15/" + c.TrackId, c.TimeUtc), WallOf(minute + 1));
            foreach (var o in mapped.Occupancy)
                engine.Offer(new QueueOccupancy(o.ZoneName, o.Count, o.TimeUtc), WallOf(minute + 1));
            var step = engine.Advance(WallOf(minute + 1));
            exits.Add(step.Movements);
            nowcasts[minute] = Nowcast.Compute(new NowcastInput { QueueLength = step.Length.Count, ExitsInWindow = exits.ExitsBefore(WallOf(minute + 1), 5) }).Minutes;
        }

        var q = ScenarioModel.Q("A-VIS");
        foreach (var minute in new[] { 1084, 1085, 1086, 1090 })
            nowcasts[minute].Should().BeApproximately(day.State(q, minute).Nowcast!.Value, 2.0, "minute {0}", ScenarioMath.Clock(minute));
        nowcasts.Where(n => n.Key < 1083).Should().OnlyContain(n => n.Value == null || n.Value <= 15, "nobody waits beyond 15 before the wave");
        nowcasts[1086].Should().BeGreaterThan(15);
    }
}
