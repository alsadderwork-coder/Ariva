using Ariva.Core.Desks;
using Ariva.Core.Queueing;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-033: the desk state engine over time: exact per-desk minute aggregates, transitions at the moment a threshold
/// passes, staleness, reordering and superseded late signals, its bounds, the lane view feeding the nowcast, and the
/// reference day's desk staffing replayed as session signals.
/// </summary>
public sealed class DeskStateEngineTests
{
    private static readonly DateTime T = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DeskStateSettings NoLateness = new() { Lateness = TimeSpan.Zero };

    private static DeskProfile Desk(string code, bool tx = false, bool session = true, bool staff = false, bool service = false, string lane = "VIS") =>
        new(code, lane, tx, session, staff, service);

    private static void Beat(DeskStateEngine engine, DeskProfile desk, DateTime from, DateTime to)
    {
        for (var t = from; t < to; t = t.AddMinutes(1))
            foreach (var source in Enum.GetValues<DeskSource>())
                if (desk.Has(source))
                    engine.Offer(new DeskHeartbeat(desk.DeskCode, t, source), t);
    }

    [Fact]
    public void Engine_Should_SplitMinutesExactly_When_ATransactionRunsWithinAMinute()
    {
        var desk = Desk("D01", tx: true);
        var engine = new DeskStateEngine([desk], T, NoLateness);
        Beat(engine, desk, T, T.AddMinutes(3));
        engine.Offer(new DeskSessionChangedSignal("D01", T.AddSeconds(30), DeskSessionSignal.Opened), T);
        engine.Offer(new DeskTransactionStarted("D01", T.AddMinutes(1)), T);
        engine.Offer(new DeskTransactionEnded("D01", T.AddSeconds(105)), T);

        var step = engine.Advance(T.AddMinutes(2));

        step.Minutes.Should().HaveCount(2);
        step.Minutes[0].Should().BeEquivalentTo(new { MinuteUtc = T, Closed = TimeSpan.FromSeconds(30), Idle = TimeSpan.FromSeconds(30), Serving = TimeSpan.Zero, Transactions = 0 });
        step.Minutes[1].Should().BeEquivalentTo(new { MinuteUtc = T.AddMinutes(1), Idle = TimeSpan.FromSeconds(15), Serving = TimeSpan.FromSeconds(45), Transactions = 1 });
        step.Minutes[1].Open.Should().Be(TimeSpan.FromMinutes(1));
        step.Transitions.Select(t => (t.From, t.To, t.AtUtc)).Should().Equal(
            (DeskStatus.Unknown, DeskStatus.Closed, T),
            (DeskStatus.Closed, DeskStatus.Idle, T.AddSeconds(30)),
            (DeskStatus.Idle, DeskStatus.Serving, T.AddMinutes(1)),
            (DeskStatus.Serving, DeskStatus.Idle, T.AddSeconds(105)));
    }

    [Fact]
    public void Engine_Should_PauseAndClose_When_T1AndT2PassWithTheStaffZoneEmpty()
    {
        var desk = Desk("D01", staff: true);
        var engine = new DeskStateEngine([desk], T, NoLateness);
        Beat(engine, desk, T, T.AddMinutes(16));
        engine.Offer(new DeskSessionChangedSignal("D01", T, DeskSessionSignal.Opened), T);
        engine.Offer(new DeskZoneReading("D01", T, DeskSource.StaffZone, 1), T);
        for (var m = 1; m < 16; m++)
            engine.Offer(new DeskZoneReading("D01", T.AddMinutes(m), DeskSource.StaffZone, 0), T);

        var step = engine.Advance(T.AddMinutes(15));

        step.Transitions.Select(t => (t.To, t.AtUtc)).Should().Equal(
            (DeskStatus.Idle, T), (DeskStatus.Paused, T.AddMinutes(4)), (DeskStatus.Closed, T.AddMinutes(11)));
        step.Minutes.Sum(m => m.Paused.TotalMinutes).Should().Be(7);
        step.Minutes.Should().OnlyContain(m => m.Closed + m.Idle + m.Serving + m.Paused + m.Unknown == TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Engine_Should_TurnUnknown_When_EverySourceFallsSilent()
    {
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness);
        engine.Offer(new DeskSessionChangedSignal("D01", T, DeskSessionSignal.Opened), T);

        var step = engine.Advance(T.AddMinutes(4));

        step.Transitions.Last().Should().BeEquivalentTo(new { From = DeskStatus.Idle, To = DeskStatus.Unknown, AtUtc = T.AddMinutes(2) });
        step.Minutes.Select(m => (m.Idle.TotalSeconds, m.Unknown.TotalSeconds, m.Degraded)).Should().Equal((60, 0, false), (60, 0, false), (0, 60, true), (0, 60, true));
        engine.Lane("VIS").Should().BeEquivalentTo(new { Open = 0, Unknown = 1, Degraded = true });

        engine.Offer(new DeskHeartbeat("D01", T.AddMinutes(4), DeskSource.Session), T.AddMinutes(4));
        engine.Advance(T.AddMinutes(4)).Transitions.Single().To.Should().Be(DeskStatus.Idle, "the session state is remembered across the outage");
    }

    [Fact]
    public void Engine_Should_ApplySignalsInEventOrder_When_TheyArriveOutOfOrderWithinTheLateness()
    {
        var desk = Desk("D01", tx: true);
        var engine = new DeskStateEngine([desk], T, new DeskStateSettings { Lateness = TimeSpan.FromSeconds(30) });
        Beat(engine, desk, T, T.AddMinutes(3));
        engine.Offer(new DeskTransactionEnded("D01", T.AddSeconds(105)), T.AddSeconds(110));
        engine.Offer(new DeskSessionChangedSignal("D01", T, DeskSessionSignal.Opened), T.AddSeconds(110));
        engine.Offer(new DeskTransactionStarted("D01", T.AddMinutes(1)), T.AddSeconds(115));

        var step = engine.Advance(T.AddSeconds(150));

        step.WatermarkUtc.Should().Be(T.AddMinutes(2));
        step.Minutes[1].Serving.Should().Be(TimeSpan.FromSeconds(45));
        engine.Counters.Late.Should().Be(0);
    }

    [Fact]
    public void Engine_Should_NotLetALateLogoutUndoANewerLogin_When_ItArrivesAfterwards()
    {
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness);
        engine.Offer(new DeskSessionChangedSignal("D01", T.AddMinutes(1), DeskSessionSignal.Closed), T.AddMinutes(1));
        engine.Offer(new DeskSessionChangedSignal("D01", T.AddMinutes(5), DeskSessionSignal.Opened), T.AddMinutes(5));
        engine.Offer(new DeskSessionChangedSignal("D01", T.AddMinutes(5.5), DeskSessionSignal.Opened), T.AddMinutes(5.5));
        engine.Advance(T.AddMinutes(6));

        engine.Offer(new DeskSessionChangedSignal("D01", T.AddMinutes(5.2), DeskSessionSignal.Closed), T.AddMinutes(6));
        engine.Advance(T.AddMinutes(6.5));

        engine.Status("D01").Status.Should().Be(DeskStatus.Idle);
        engine.Counters.Should().BeEquivalentTo(new { Late = 1, Superseded = 1 });
    }

    [Fact]
    public void Engine_Should_ApplyALateSignalAtItsDeskTime_When_ItIsNewerThanWhatItKnows()
    {
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness);
        engine.Offer(new DeskSessionChangedSignal("D01", T, DeskSessionSignal.Opened), T);
        engine.Advance(T.AddMinutes(1));

        engine.Offer(new DeskSessionChangedSignal("D01", T.AddSeconds(50), DeskSessionSignal.Closed), T.AddMinutes(1));
        var step = engine.Advance(T.AddMinutes(1));

        step.Transitions.Single().Should().BeEquivalentTo(new { To = DeskStatus.Closed, AtUtc = T.AddMinutes(1) }, "minutes already emitted are not revised");
        engine.Counters.Late.Should().Be(1);
    }

    [Fact]
    public void Engine_Should_RefuseSignals_When_TheyAreNotValidForTheDesk()
    {
        var engine = new DeskStateEngine([Desk("D01", staff: true)], T, NoLateness);

        engine.Offer(new DeskHeartbeat("D99", T, DeskSource.Session), T);
        engine.Offer(new DeskHeartbeat(null!, T, DeskSource.Session), T);
        engine.Offer(new DeskZoneReading("D01", T, DeskSource.ServiceZone, 1), T);
        engine.Offer(new DeskZoneReading("D01", T, DeskSource.StaffZone, 51), T);
        engine.Offer(new DeskZoneReading("D01", T, DeskSource.StaffZone, -1), T);
        engine.Offer(new DeskZoneReading("D01", T, DeskSource.Session, 1), T);
        engine.Offer(new DeskTransactionStarted("D01", T), T);
        engine.Offer(new DeskHeartbeat("D01", T, (DeskSource)9), T);
        engine.Offer(new DeskSessionChangedSignal("D01", T, (DeskSessionSignal)7), T);
        engine.Offer(new DeskHeartbeat("D01", new DateTime(2026, 9, 28, 18, 0, 0, DateTimeKind.Local), DeskSource.Session), T);
        engine.Offer(new DeskHeartbeat("D01", T.AddMinutes(6), DeskSource.Session), T);
        Action local = () => engine.Offer(new DeskHeartbeat("D01", T, DeskSource.Session), DateTime.Now);

        local.Should().Throw<ArgumentException>();
        engine.Counters.Should().Be(new DeskEngineCounters(Accepted: 0, Late: 0, Superseded: 0, Future: 1, Invalid: 8, UnknownDesk: 2, BufferFull: 0));
    }

    [Fact]
    public void Engine_Should_BoundItsBufferAndSteps_When_FloodedOrFarBehind()
    {
        var engine = new DeskStateEngine([Desk("D01"), Desk("D02")], T, NoLateness with { MaxBufferedSignals = 100, MaxStepRecords = 100 });
        for (var k = 0; k < 150; k++)
            engine.Offer(new DeskHeartbeat("D01", T.AddSeconds(k), DeskSource.Session), T.AddMinutes(3));
        engine.Counters.BufferFull.Should().Be(100, "past half the buffer a desk holds no more than an equal share");
        for (var k = 0; k < 10; k++)
            engine.Offer(new DeskHeartbeat("D02", T.AddSeconds(k), DeskSource.Session), T.AddMinutes(3));
        engine.Counters.Accepted.Should().Be(60, "the other desk keeps its share");

        var minutes = new List<DeskMinute>();
        var steps = 0;
        DeskStep step;
        do
        {
            step = engine.Advance(T.AddHours(3));
            step.Minutes.Count.Should().BeLessThanOrEqualTo(100);
            minutes.AddRange(step.Minutes);
            steps++;
        }
        while (step.More && steps < 100);

        step.More.Should().BeFalse();
        foreach (var desk in new[] { "D01", "D02" })
            minutes.Where(m => m.DeskCode == desk).Select(m => m.MinuteUtc).Should().Equal(Enumerable.Range(0, 180).Select(m => T.AddMinutes(m)));
    }

    [Fact]
    public void Engine_Should_KeepOtherDesksAlive_When_OneDeskIsFloodedWithFutureSignals()
    {
        var desks = Enumerable.Range(0, 50).Select(k => Desk($"D{k}")).ToList();
        var engine = new DeskStateEngine(desks, T, NoLateness);
        foreach (var desk in desks)
            engine.Offer(new DeskSessionChangedSignal(desk.DeskCode, T, DeskSessionSignal.Opened), T);
        for (var second = 0; second <= 180; second += 10)
        {
            var now = T.AddSeconds(second);
            for (var k = 0; k < (second == 0 ? 100_000 : 2_000); k++)
                engine.Offer(new DeskHeartbeat("D0", now.AddMinutes(4), DeskSource.Session), now);
            foreach (var desk in desks.Skip(1))
                engine.Offer(new DeskHeartbeat(desk.DeskCode, now, DeskSource.Session), now);
            engine.Advance(now);
        }

        engine.Lane("VIS").Should().BeEquivalentTo(new { Open = 49, Unknown = 1 }, "only the flooded desk, whose signals all lie ahead, goes Unknown");
        engine.Status("D0").Status.Should().Be(DeskStatus.Unknown);
        engine.Counters.BufferFull.Should().BeGreaterThan(100_000);
    }

    [Fact]
    public void Engine_Should_GiveMemoryBack_When_ADesksFloodDrains()
    {
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness with { MaxBufferedSignals = 10_000_000, MaxBufferedSignalsPerDesk = 100_000, MaxDesks = 10 });
        for (var k = 0; k < 100_000; k++)
            engine.Offer(new DeskHeartbeat("D01", T.AddTicks(k), DeskSource.Session), T);
        engine.Advance(T.AddMinutes(1));

        var desk = ((System.Collections.IList)typeof(DeskStateEngine).GetField("_order", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!)[0]!;
        var pending = desk.GetType().GetProperty("Pending")!.GetValue(desk)!;
        var nodes = (Array)pending.GetType().GetField("_nodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pending)!;
        nodes.Length.Should().Be(0, "a drained queue does not keep its grown array");
    }

    [Fact]
    public void Engine_Should_RefuseSignals_When_TheyAreFarBehindTheWatermark()
    {
        var engine = new DeskStateEngine([Desk("D01", tx: true)], T, NoLateness);
        engine.Advance(T.AddMinutes(30));

        engine.Offer(new DeskTransactionsCompleted("D01", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), 1000), T.AddMinutes(30));
        engine.Offer(new DeskTransactionsCompleted("D01", T.AddMinutes(14), 1000), T.AddMinutes(30));
        engine.Offer(new DeskTransactionsCompleted("D01", T.AddMinutes(16), 3), T.AddMinutes(30));

        engine.Counters.Should().BeEquivalentTo(new { TooLate = 2, Late = 1, Accepted = 1 });
        engine.Advance(T.AddMinutes(31)).Minutes.Single().Transactions.Should().Be(3);
    }

    [Fact]
    public void Engine_Should_SkipTheGap_When_TheClockJumpsFarAhead()
    {
        var engine = new DeskStateEngine([Desk("D01"), Desk("D02")], T, NoLateness with { MaxStepRecords = 10_000_000 });
        engine.Offer(new DeskSessionChangedSignal("D01", T, DeskSessionSignal.Opened), T);

        var step = engine.Advance(T.AddDays(365));

        step.Minutes.Count.Should().Be(2 * 24 * 60, "one day per desk, not a year");
        step.Minutes.Should().OnlyContain(m => m.Unknown == TimeSpan.FromMinutes(1));
        engine.Counters.SkippedMinutes.Should().Be(2L * 364 * 24 * 60);
        step.Minutes.Where(m => m.DeskCode == "D01").Select(m => m.MinuteUtc).Should().Equal(Enumerable.Range(0, 1440).Select(m => T.AddDays(364).AddMinutes(m)));
    }

    [Fact]
    public void Engine_Should_StopAtTheEndOfTime_When_AdvancedToTheLastTick()
    {
        var start = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).AddMinutes(-2);
        var engine = new DeskStateEngine([Desk("D01")], start, NoLateness);
        engine.Offer(new DeskHeartbeat("D01", DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), DeskSource.Session), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));

        Action advance = () => engine.Advance(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));

        advance.Should().NotThrow();
    }

    [Fact]
    public void Engine_Should_KeepASensorDeskOpen_When_TheStaffZoneDropsOutBriefly()
    {
        var desk = Desk("S01", session: false, staff: true, service: true);
        var engine = new DeskStateEngine([desk], T, NoLateness);
        Beat(engine, desk, T, T.AddMinutes(20));
        engine.Offer(new DeskZoneReading("S01", T, DeskSource.StaffZone, 1), T);
        engine.Offer(new DeskZoneReading("S01", T.AddSeconds(60), DeskSource.StaffZone, 0), T);
        engine.Offer(new DeskZoneReading("S01", T.AddSeconds(65), DeskSource.StaffZone, 1), T);
        engine.Offer(new DeskZoneReading("S01", T.AddMinutes(5), DeskSource.StaffZone, 0), T);
        for (var m = 6; m < 20; m++)
            engine.Offer(new DeskZoneReading("S01", T.AddMinutes(m), DeskSource.StaffZone, 0), T);

        var step = engine.Advance(T.AddMinutes(20));

        step.Transitions.Select(t => (t.To, t.AtUtc)).Should().Equal(
            (DeskStatus.Idle, T), (DeskStatus.Paused, T.AddMinutes(8)), (DeskStatus.Closed, T.AddMinutes(15)));
        engine.Lane("VIS").Should().BeEquivalentTo(new { Degraded = false, SensorDerived = 0 });
    }

    [Fact]
    public void Lane_Should_NotBeDegraded_When_ItsDesksAreSensorDerivedOnHealthySensors()
    {
        var desk = Desk("S01", session: false, staff: true, service: true);
        var engine = new DeskStateEngine([desk], T, NoLateness);
        Beat(engine, desk, T, T.AddMinutes(2));
        engine.Offer(new DeskZoneReading("S01", T, DeskSource.StaffZone, 1), T);
        engine.Advance(T.AddMinutes(1));

        engine.Lane("VIS").Should().BeEquivalentTo(new { Open = 1, Idle = 1, SensorDerived = 1, Degraded = false });
    }

    [Fact]
    public void Lane_Should_GiveTheNowcastItsOpenDesks_When_SomeArePausedOrUnknown()
    {
        var desks = Enumerable.Range(1, 8).Select(k => Desk($"D{k:00}")).ToList();
        var engine = new DeskStateEngine([.. desks, Desk("X01", lane: "CIT")], T, NoLateness);
        foreach (var desk in desks.Take(7))
            Beat(engine, desk, T, T.AddMinutes(5));
        for (var k = 1; k <= 6; k++)
            engine.Offer(new DeskSessionChangedSignal($"D{k:00}", T, DeskSessionSignal.Opened), T);
        engine.Offer(new DeskSessionChangedSignal("D07", T, DeskSessionSignal.Paused), T);
        engine.Advance(T.AddMinutes(4));

        var lane = engine.Lane("VIS");
        lane.Should().BeEquivalentTo(new { Open = 6, Idle = 6, Paused = 1, Unknown = 1, Degraded = true });
        var nowcast = Nowcast.Compute(new NowcastInput { QueueLength = 29, OpenServers = lane.OpenServers, CycleMinutes = 1.5, Degraded = lane.Degraded });
        nowcast.Minutes.Should().BeApproximately(7.5, 1e-9);
        nowcast.Degraded.Should().BeTrue("an Unknown desk flags the lane");
        engine.Lane("NONE").Should().BeEquivalentTo(new { Open = 0, Unknown = 0, Degraded = false });
    }

    [Fact]
    public void LaneCycle_Should_WeighTheDesksByTransactions_When_SomeIntervalsAreNotReal()
    {
        LaneCycle.Minutes([(90, 10), (60, 30), (double.NaN, 5), (100, 0), (-1, 4), (4000, 3), (double.PositiveInfinity, 2)])
            .Should().BeApproximately(1.125, 1e-12);
        LaneCycle.Minutes([]).Should().BeNull();
        LaneCycle.Minutes([(90, int.MaxValue), (60, int.MaxValue)]).Should().BeApproximately(1.25, 1e-12);
    }

    [Fact]
    public void Engine_Should_RefuseItsDesks_When_TheyAreNotValid()
    {
        Action duplicate = () => _ = new DeskStateEngine([Desk("D01"), Desk("D01")], T);
        Action silent = () => _ = new DeskStateEngine([new DeskProfile("D01", "VIS", false, false, false, false)], T);
        Action unnamed = () => _ = new DeskStateEngine([Desk(" ")], T);
        Action longName = () => _ = new DeskStateEngine([Desk(new string('D', 65))], T);
        Action many = () => _ = new DeskStateEngine(Enumerable.Range(0, 11).Select(k => Desk($"D{k}")), T, new DeskStateSettings { MaxDesks = 10 });
        Action local = () => _ = new DeskStateEngine([Desk("D01")], DateTime.Now);
        Action settings = () => _ = new DeskStateEngine([Desk("D01")], T, new DeskStateSettings { StaleAfter = TimeSpan.Zero });

        foreach (var bad in new[] { duplicate, silent, unnamed, longName, many, local, settings })
            bad.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Outputs_Should_CarryNoIdentities_When_Published()
    {
        string[] forbidden = ["officer", "traveller", "traveler", "passenger", "document", "passport", "person", "name", "user", "badge", "track"];
        var types = new[] { typeof(DeskMinute), typeof(DeskTransition), typeof(LaneDesks), typeof(DeskTransactionStarted), typeof(DeskTransactionEnded),
            typeof(DeskTransactionsCompleted), typeof(DeskSessionChangedSignal), typeof(DeskZoneReading), typeof(DeskHeartbeat) };

        foreach (var type in types)
            type.GetProperties().Select(p => p.Name.ToLowerInvariant()).Should().NotContain(n => forbidden.Any(n.Contains), type.Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Engine_Should_AccountForEverySecond_When_FedRandomSignals(int seed)
    {
        // A seeded mulberry32 stream (the scenario engine's): reproducible, and not used for anything secret.
        var stream = new Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        var desks = new[]
        {
            Desk("A", tx: true, staff: true, service: true), Desk("B", staff: true), Desk("C", session: false, staff: true, service: true),
            Desk("D", tx: true), Desk("E", tx: true, session: false)
        };
        var engine = new DeskStateEngine(desks, T, new DeskStateSettings { Lateness = TimeSpan.FromSeconds(20), MaxBufferedSignals = 400, MaxStepRecords = 200 });
        var minutes = new List<DeskMinute>();
        var transitions = new List<DeskTransition>();
        var offered = 0;
        for (var second = 0; second < 3 * 3600; second += 5)
        {
            var now = T.AddSeconds(second);
            for (var k = Next(3); k > 0; k--)
            {
                var desk = desks[Next(desks.Length)];
                var at = now.AddSeconds(-Next(90));
                DeskSignal signal = Next(8) switch
                {
                    0 => new DeskTransactionStarted(desk.DeskCode, at),
                    1 => new DeskTransactionEnded(desk.DeskCode, at),
                    2 => new DeskTransactionsCompleted(desk.DeskCode, at, Next(5) - 1),
                    3 => new DeskSessionChangedSignal(desk.DeskCode, at, (DeskSessionSignal)Next(3)),
                    4 => new DeskZoneReading(desk.DeskCode, at, DeskSource.StaffZone, Next(3) - (Next(20) == 0 ? 5 : 0)),
                    5 => new DeskZoneReading(desk.DeskCode, at, DeskSource.ServiceZone, Next(3)),
                    6 => new DeskHeartbeat(desk.DeskCode, at.AddMinutes(Next(10) == 0 ? 10 : 0), (DeskSource)Next(4)),
                    _ => new DeskHeartbeat(desks[Next(desks.Length)].DeskCode + (Next(10) == 0 ? "?" : ""), at, (DeskSource)Next(4))
                };
                engine.Offer(signal, now);
                offered++;
            }

            if (second % 20 == 0)
            {
                var step = engine.Advance(now);
                minutes.AddRange(step.Minutes);
                transitions.AddRange(step.Transitions);
            }
        }

        for (var guard = 0; guard < 1000; guard++)
        {
            var step = engine.Advance(T.AddHours(4));
            minutes.AddRange(step.Minutes);
            transitions.AddRange(step.Transitions);
            if (!step.More)
                break;
        }

        var c = engine.Counters;
        (c.Accepted + c.Future + c.Invalid + c.UnknownDesk + c.BufferFull).Should().Be(offered);
        foreach (var desk in desks)
        {
            var mine = minutes.Where(m => m.DeskCode == desk.DeskCode).ToList();
            mine.Select(m => m.MinuteUtc).Should().Equal(Enumerable.Range(0, mine.Count).Select(m => T.AddMinutes(m)));
            mine.Count.Should().BeGreaterThanOrEqualTo(4 * 60 - 1);
            mine.Should().OnlyContain(m => m.Closed + m.Idle + m.Serving + m.Paused + m.Unknown == TimeSpan.FromMinutes(1));
            mine.Should().OnlyContain(m => m.SensorDerived <= TimeSpan.FromMinutes(1) && m.PresentNotProcessing <= TimeSpan.FromMinutes(1) && m.Transactions >= 0);
            var chain = transitions.Where(t => t.DeskCode == desk.DeskCode).ToList();
            for (var i = 1; i < chain.Count; i++)
            {
                chain[i].From.Should().Be(chain[i - 1].To);
                chain[i].AtUtc.Should().BeOnOrAfter(chain[i - 1].AtUtc);
            }
        }
    }

    [Fact]
    public void Engine_Should_FollowTheReferenceDaysDesks_When_ItsStaffingIsReplayedAsSessions()
    {
        // The scenario's per-minute server states for the Visitors desks, as AMAN would report them: a session change
        // when a desk opens, closes or pauses and a heartbeat each minute, silence while the scenario marks it unknown.
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var q = ScenarioModel.Q("A-VIS");
        var def = ScenarioModel.Queues[q];
        var midnight = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var engine = new DeskStateEngine(def.Servers.Select(s => Desk(s)), midnight.AddMinutes(900), NoLateness);
        var last = new Dictionary<string, DeskSessionSignal>();
        var silentSince = new Dictionary<string, int>();
        var compared = 0;
        for (var minute = 900; minute < 1320; minute++)
        {
            var at = midnight.AddMinutes(minute);
            var states = day.ServerStates(q, minute);
            foreach (var server in states)
            {
                if (server.State == "unknown")
                {
                    silentSince.TryAdd(server.Id, minute);
                    continue;
                }

                silentSince.Remove(server.Id);
                var session = server.State switch { "closed" or "oos" => DeskSessionSignal.Closed, "paused" => DeskSessionSignal.Paused, _ => DeskSessionSignal.Opened };
                if (!last.TryGetValue(server.Id, out var previous) || previous != session)
                    engine.Offer(new DeskSessionChangedSignal(server.Id, at, session), at);
                else
                    engine.Offer(new DeskHeartbeat(server.Id, at, DeskSource.Session), at);
                last[server.Id] = session;
            }

            engine.Advance(at.AddSeconds(30));
            var lane = engine.Lane("VIS");
            var open = states.Count(s => s.State is "serving" or "idle");
            // Last heard at the minute before it fell silent, so stale 2 minutes later: Unknown from the next minute on.
            var unknownLong = states.Count(s => s.State == "unknown" && minute - silentSince[s.Id] >= 1);
            lane.Unknown.Should().Be(unknownLong, "a silent desk turns Unknown after T_stale, minute {0}", ScenarioMath.Clock(minute));
            if (states.Any(s => s.State == "unknown"))
                continue;
            lane.Open.Should().Be(open, "minute {0}", ScenarioMath.Clock(minute));

            var state = day.State(q, minute);
            if (state.Rate > 0.01 && open > 0 && open == state.Active)
            {
                var nowcast = Nowcast.Compute(new NowcastInput
                {
                    QueueLength = (int)Math.Round(state.Length), OpenServers = lane.OpenServers, CycleMinutes = state.Active / state.Rate
                });
                nowcast.Minutes.Should().BeApproximately(state.Nowcast!.Value, 0.5 / state.Rate + 1e-9, "minute {0}", ScenarioMath.Clock(minute));
                compared++;
            }
        }

        compared.Should().BeGreaterThan(200);
    }
}
