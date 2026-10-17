using System.Text.Json;
using Ariva.Core.Desks;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-149: the desk state engine over time for desks with AMAN's sources alone (F10 rows 5a to 5f): AMAN's completed
/// transactions at the end of each interval, a lane context each minute, the exact moments the state changes, the waiting
/// stretch, staleness of the context, refusals (CWE-501), snapshots, and desks with zones unchanged.
/// </summary>
public sealed class DeskRhythmEngineTests
{
    private static readonly DateTime T = new(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DeskStateSettings NoLateness = new() { Lateness = TimeSpan.Zero };

    private static DeskProfile Aman(string code = "D01", bool staff = false, bool service = false) =>
        new(code, "VIS", HasTransactions: true, HasSession: true, HasStaffZone: staff, HasServiceZone: service);

    // AMAN live through minute `to`: a heartbeat for its session and transactions each minute, the login at T.
    private static DeskStateEngine LoggedIn(DeskProfile desk, int to, DeskStateSettings settings = null)
    {
        var engine = new DeskStateEngine([desk], T, settings ?? NoLateness);
        engine.Offer(new DeskSessionChangedSignal(desk.DeskCode, T, DeskSessionSignal.Opened), T);
        for (var m = 0; m <= to; m++)
        {
            engine.Offer(new DeskHeartbeat(desk.DeskCode, T.AddMinutes(m), DeskSource.Session), T.AddMinutes(m));
            engine.Offer(new DeskHeartbeat(desk.DeskCode, T.AddMinutes(m), DeskSource.Transactions), T.AddMinutes(m));
        }

        return engine;
    }

    // One transaction completed in the interval ending at minute m (AMAN's statistics, reported at the interval's end).
    private static void Completed(DeskStateEngine engine, string desk, params int[] minutes)
    {
        foreach (var m in minutes)
            engine.Offer(new DeskTransactionsCompleted(desk, T.AddMinutes(m), 1), T.AddMinutes(m));
    }

    // A lane context at each whole minute from `from` to `to` with s seconds and q waiting.
    private static void Contexts(DeskStateEngine engine, string desk, int from, int to, double? s, int? q, bool degraded = false)
    {
        for (var m = from; m <= to; m++)
            engine.Offer(new DeskLaneContext(desk, T.AddMinutes(m), s, q, degraded), T.AddMinutes(m));
    }

    private static IReadOnlyList<(DeskStatus To, DateTime At)> Transitions(DeskStep step) => [.. step.Transitions.Select(t => (t.To, t.AtUtc))];

    [Fact]
    public void Engine_Should_ServeThroughAWaveAndIdleAfterIt_When_TransactionsKeepTheRhythm()
    {
        // People wait from the login until minute 10, then nobody; a transaction ends in each interval ending at 2, 4, 6 and
        // 8; s = 1.5 min. Serving from the login (5b), kept by the rhythm (5a), Idle once the rhythm ends with nobody
        // waiting: 2.5 min after the interval ending at 8 (5d).
        var engine = LoggedIn(Aman(), 20);
        Completed(engine, "D01", 2, 4, 6, 8);
        Contexts(engine, "D01", 0, 9, 90, 3);
        Contexts(engine, "D01", 10, 20, 90, 0);

        var step = engine.Advance(T.AddMinutes(20));

        Transitions(step).Should().Equal((DeskStatus.Serving, T), (DeskStatus.Idle, T.AddMinutes(10.5)));
        step.Minutes.Sum(m => m.Serving.TotalMinutes).Should().Be(10.5);
        step.Minutes.Sum(m => m.Paused.TotalMinutes).Should().Be(0);
        step.Minutes.Should().OnlyContain(m => !m.Degraded && m.SensorDerived == TimeSpan.Zero && m.Closed + m.Idle + m.Serving + m.Paused + m.Unknown == TimeSpan.FromMinutes(1));
        step.Minutes.Sum(m => m.Transactions).Should().Be(4);
    }

    [Fact]
    public void Engine_Should_PauseAfterKTimesTheServiceTime_When_PeopleWaitAndTheDeskFallsSilent()
    {
        // People wait all along; transactions in the intervals ending at 2 and 4, then silence until 12: P = 4 x 1.5 = 6 min
        // after the interval ending at 4, so Paused at 10, Serving again from 12 (5a) until 18.
        var engine = LoggedIn(Aman(), 20);
        Completed(engine, "D01", 2, 4, 12);
        Contexts(engine, "D01", 0, 20, 90, 5);

        Transitions(engine.Advance(T.AddMinutes(17))).Should().Equal((DeskStatus.Serving, T), (DeskStatus.Paused, T.AddMinutes(10)), (DeskStatus.Serving, T.AddMinutes(12)));
    }

    [Fact]
    public void Engine_Should_KeepTheRowsOfBefore_When_NoLaneContextArrives()
    {
        // The same signals without a lane context: Idle, Paused T1 after the last transaction, Idle again at the next.
        var engine = LoggedIn(Aman(), 20);
        Completed(engine, "D01", 2, 4, 12);

        Transitions(engine.Advance(T.AddMinutes(20))).Should().Equal((DeskStatus.Idle, T), (DeskStatus.Paused, T.AddMinutes(7)), (DeskStatus.Idle, T.AddMinutes(12)),
            (DeskStatus.Paused, T.AddMinutes(15)));
    }

    [Fact]
    public void Engine_Should_GiveTheDeskPToStart_When_AQueueFormsAfterAQuietSpell()
    {
        // Nobody waits until minute 20 (Idle all along, no transaction since the login), then people wait from 20: Serving
        // at once (the silence counts from the start of the waiting), Paused at 26 without a transaction.
        var engine = LoggedIn(Aman(), 30);
        Contexts(engine, "D01", 0, 19, 90, 0);
        Contexts(engine, "D01", 20, 30, 90, 2);

        Transitions(engine.Advance(T.AddMinutes(30))).Should().Equal((DeskStatus.Idle, T), (DeskStatus.Serving, T.AddMinutes(20)), (DeskStatus.Paused, T.AddMinutes(26)));
    }

    [Fact]
    public void Engine_Should_StartTheWaitingAgain_When_TheQueueEmptiesOrIsNotKnownBetween()
    {
        // People wait 0 to 9, nobody at 10, people again from 11: the stretch starts at 11 (Paused would be at 6 otherwise).
        // A minute with the queue not known breaks it too (21), and so does a gap of T_stale in the contexts (30 to 33).
        var engine = LoggedIn(Aman(), 45);
        Contexts(engine, "D01", 0, 9, 90, 3);
        Contexts(engine, "D01", 10, 10, 90, 0);
        Contexts(engine, "D01", 11, 20, 90, 3);
        Contexts(engine, "D01", 21, 21, 90, null);
        Contexts(engine, "D01", 22, 30, 90, 3);
        Contexts(engine, "D01", 33, 45, 90, 3);

        var transitions = Transitions(engine.Advance(T.AddMinutes(45)));

        transitions.Should().Equal(
            (DeskStatus.Serving, T), (DeskStatus.Paused, T.AddMinutes(6)), (DeskStatus.Idle, T.AddMinutes(10)), (DeskStatus.Serving, T.AddMinutes(11)),
            (DeskStatus.Paused, T.AddMinutes(17)), (DeskStatus.Serving, T.AddMinutes(22)), (DeskStatus.Paused, T.AddMinutes(28)),
            // The contexts stop at 30 and the next is at 33: stale from 32 (rows 5e: Paused, silent since the login), and the
            // waiting starts again at 33.
            (DeskStatus.Serving, T.AddMinutes(33)), (DeskStatus.Paused, T.AddMinutes(39)));
    }

    [Fact]
    public void Engine_Should_FallBackToT1_When_TheLaneContextStops()
    {
        // Contexts until 5 with people waiting; a transaction ending at 4. From 7 (the context 2 min old) the rows of before:
        // Paused, T1 after the transaction having passed at 7.
        var engine = LoggedIn(Aman(), 20);
        Completed(engine, "D01", 4);
        Contexts(engine, "D01", 0, 5, 90, 4);

        Transitions(engine.Advance(T.AddMinutes(20))).Should().Equal((DeskStatus.Serving, T), (DeskStatus.Paused, T.AddMinutes(7)));
    }

    [Fact]
    public void Engine_Should_FlagTheMinutes_When_ADegradedQueueLengthDecidesTheState()
    {
        var engine = LoggedIn(Aman(), 6);
        Contexts(engine, "D01", 0, 2, 90, 0, degraded: true);
        Contexts(engine, "D01", 3, 6, 90, 0);

        var minutes = engine.Advance(T.AddMinutes(6)).Minutes;

        minutes.Select(m => m.Degraded).Should().Equal(true, true, true, false, false, false);
        minutes.Should().OnlyContain(m => m.Idle == TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Engine_Should_TurnUnknown_When_AmanFallsSilentWhateverTheLaneContextSays()
    {
        // The context is not a source: AMAN silent beyond T_stale is Unknown even while the contexts keep arriving.
        var engine = LoggedIn(Aman(), 3);
        Contexts(engine, "D01", 0, 10, 90, 4);

        var step = engine.Advance(T.AddMinutes(10));

        Transitions(step).Should().Equal((DeskStatus.Serving, T), (DeskStatus.Unknown, T.AddMinutes(5)));
        engine.Lane("VIS").Should().BeEquivalentTo(new { Unknown = 1, Open = 0, Degraded = true });
    }

    [Fact]
    public void Engine_Should_SupersedeAnOlderContext_When_ItArrivesLate()
    {
        var engine = LoggedIn(Aman(), 10);
        Contexts(engine, "D01", 0, 5, 90, 0);
        engine.Advance(T.AddMinutes(5));

        // A context of minute 4 arriving after the one of minute 5 does not undo it.
        engine.Offer(new DeskLaneContext("D01", T.AddMinutes(4), 90, 7), T.AddMinutes(5));
        engine.Advance(T.AddMinutes(6));

        engine.Counters.Should().BeEquivalentTo(new { Superseded = 1, Late = 1 });
        engine.Status("D01").Status.Should().Be(DeskStatus.Idle, "minute 5's context said nobody waits");
    }

    [Fact]
    public void Engine_Should_RefuseALaneContext_When_TheDeskHasZonesOrItsValuesAreOutOfBounds()
    {
        // CWE-501: only a desk with AMAN's sources alone takes a context, and only within bounds.
        var engine = new DeskStateEngine([Aman(), Aman("D02", staff: true), Aman("D03", service: true),
            new DeskProfile("D04", "VIS", HasTransactions: true, HasSession: false, HasStaffZone: false, HasServiceZone: false)], T, NoLateness);

        foreach (var desk in new[] { "D02", "D03", "D04" })
            engine.Offer(new DeskLaneContext(desk, T, 90, 1), T);
        foreach (var s in new double?[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 3_600.001 })
            engine.Offer(new DeskLaneContext("D01", T, s, 1), T);
        foreach (var q in new int?[] { -1, DeskLaneContext.MaxWaiting + 1, int.MinValue })
            engine.Offer(new DeskLaneContext("D01", T, 90, q), T);
        engine.Offer(new DeskLaneContext("D01", T, null, null), T);
        engine.Offer(new DeskLaneContext("D01", T.AddSeconds(1), 3_600, DeskLaneContext.MaxWaiting), T);
        engine.Offer(new DeskHeartbeat("D01", T, DeskSource.LaneContext), T);

        engine.Counters.Should().BeEquivalentTo(new { Invalid = 13, Accepted = 2 }, "a heartbeat cannot keep a lane context alive either");
    }

    [Fact]
    public void Engine_Should_ContinueExactly_When_RestoredFromASnapshotWithLaneContexts()
    {
        var desk = Aman();
        DeskStateEngine Build()
        {
            var e = LoggedIn(desk, 30);
            Completed(e, "D01", 2, 4, 12);
            Contexts(e, "D01", 0, 9, 90, 3);
            Contexts(e, "D01", 10, 30, 90, 2, degraded: true);
            return e;
        }

        var whole = Build();
        var uninterrupted = whole.Advance(T.AddMinutes(30)).Minutes;

        var first = Build();
        var before = first.Advance(T.AddMinutes(11.5)).Minutes;
        // As the desk feed stores it (desk_feed_state).
        var json = JsonSerializer.Serialize(first.Capture(), Ariva.Infra.Messaging.EventCatalog.Json);
        var state = JsonSerializer.Deserialize<DeskEngineState>(json, Ariva.Infra.Messaging.EventCatalog.Json);
        state.Desks.Single().Pending.Should().Contain(p => p.Signal.Kind == "context" && p.Signal.Waiting == 2 && p.Signal.ServiceSeconds == 90 && p.Signal.Degraded);
        state.Desks.Single().Memory.QueueWaitingSince.Should().Be(T, "the waiting has gone on since the first context");
        var restored = DeskStateEngine.Restore([desk], NoLateness, state);
        var after = restored.Advance(T.AddMinutes(30)).Minutes;

        before.Concat(after).Should().BeEquivalentTo(uninterrupted, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Restore_Should_RefuseASnapshot_When_ItsLaneContextIsOutOfBounds()
    {
        var desk = Aman();
        var engine = LoggedIn(desk, 5);
        Contexts(engine, "D01", 0, 5, 90, 1);
        engine.Advance(T.AddMinutes(2));
        var state = engine.Capture();
        var saved = state.Desks.Single();

        DeskEngineState With(DeskMemory memory) => state with { Desks = [saved with { Memory = memory }] };
        DeskEngineState WithPending(DeskSignalState signal) => state with { Desks = [saved with { Pending = [new DeskPendingState(signal, false, 999)] }] };

        foreach (var bad in new[]
                 {
                     With(saved.Memory with { LaneServiceSeconds = double.NaN }), With(saved.Memory with { LaneServiceSeconds = 0 }),
                     With(saved.Memory with { QueueWaiting = -1 }), With(saved.Memory with { LaneContextAt = T.AddDays(2) }),
                     With(saved.Memory with { QueueWaitingSince = T.AddDays(2) }),
                     // A context dated after the desk's cursor (here 18:02) could not have been applied yet.
                     With(saved.Memory with { LaneContextAt = T.AddMinutes(2).AddSeconds(1) }),
                     // The waiting starts at or before the context that says people wait, and only while they do.
                     With(saved.Memory with { LaneContextAt = T.AddMinutes(1), QueueWaitingSince = T.AddMinutes(1.5) }),
                     With(saved.Memory with { QueueWaiting = 0 }), With(saved.Memory with { QueueWaiting = null }),
                     With(saved.Memory with { LaneContextAt = null }),
                     WithPending(new DeskSignalState("context", "D01", T.AddMinutes(3), DeskSource.LaneContext, ServiceSeconds: -5, Waiting: 1)),
                     WithPending(new DeskSignalState("context", "D01", T.AddMinutes(3), DeskSource.LaneContext, ServiceSeconds: 90, Waiting: DeskLaneContext.MaxWaiting + 1))
                 })
        {
            Action restore = () => DeskStateEngine.Restore([desk], NoLateness, bad);
            restore.Should().Throw<InvalidDataException>();
        }

        // A context pending for a desk that has a zone now is refused as Offer would refuse it.
        Action zoned = () => DeskStateEngine.Restore([Aman(staff: true)], NoLateness, state);
        zoned.Should().Throw<InvalidDataException>();
        // An older snapshot without the lane memory is valid, and so is the captured one.
        saved.Memory.Should().Match<DeskMemory>(m => m.LaneContextAt == T.AddMinutes(2) && m.QueueWaitingSince == T && m.QueueWaiting == 1);
        DeskStateEngine.Restore([desk], NoLateness, With(DeskMemory.Empty)).Status("D01").Should().NotBeNull();
        DeskStateEngine.Restore([desk], NoLateness, state).Status("D01").Status.Should().Be(DeskStatus.Serving);
    }

    [Fact]
    public void SensorOnlyEngine_Should_NeverTakeALaneContext_When_OfferedOrRestored()
    {
        var desks = new[] { Aman("D01", staff: true, service: true) };
        var sensor = SensorOnlyDeskEngine.Start(desks, T, NoLateness);
        sensor.Offer(new DeskLaneContext("D01", T, 90, 3), T);
        sensor.Refused.Should().Be(1);

        var state = sensor.Capture();
        var desk = state.Desks.Single();
        foreach (var memory in new[]
                 {
                     desk.Memory with { LaneContextAt = T }, desk.Memory with { LaneServiceSeconds = 90 }, desk.Memory with { QueueWaiting = 0 },
                     desk.Memory with { QueueWaitingSince = T }, desk.Memory with { QueueDegraded = true }
                 })
        {
            Action restore = () => SensorOnlyDeskEngine.Restore(desks, NoLateness, state with { Desks = [desk with { Memory = memory }] });
            restore.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void LaneContext_Should_CarryNoIdentities_When_Published()
    {
        // Data boundary: aggregates only (no officer, traveller or document identity).
        string[] forbidden = ["officer", "traveller", "traveler", "passenger", "document", "passport", "person", "name", "user", "badge", "track"];
        foreach (var type in new[] { typeof(DeskLaneContext), typeof(LaneQueueSample), typeof(LaneIntervalSample), typeof(DeskRhythmContexts) })
            type.GetProperties().Select(p => p.Name.ToLowerInvariant()).Should().NotContain(n => forbidden.Any(n.Contains), type.Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Engine_Should_AccountForEverySecond_When_FedRandomTransactionsAndContexts(int seed)
    {
        // A seeded mulberry32 stream (the scenario engine's): reproducible, and not used for anything secret.
        var stream = new Ariva.Simulation.Api.Scenarios.Engine.Mulberry32((uint)seed * 2654435761u);
        int Next(int max) => (int)(stream.Next() * max);
        var desks = new[] { Aman("A"), Aman("B"), Aman("C", staff: true) };
        var engine = new DeskStateEngine(desks, T, new DeskStateSettings { Lateness = TimeSpan.FromSeconds(90) });
        var minutes = new List<DeskMinute>();
        var transitions = new List<DeskTransition>();
        for (var second = 0; second < 3 * 3600; second += 15)
        {
            var now = T.AddSeconds(second);
            if (second % 60 == 0)
            {
                foreach (var desk in desks)
                {
                    if (Next(40) > 0)
                    {
                        engine.Offer(new DeskHeartbeat(desk.DeskCode, now, DeskSource.Session), now);
                        engine.Offer(new DeskHeartbeat(desk.DeskCode, now, DeskSource.Transactions), now);
                    }

                    if (Next(30) == 0)
                        engine.Offer(new DeskSessionChangedSignal(desk.DeskCode, now, (DeskSessionSignal)Next(3)), now);
                    if (Next(3) == 0)
                        engine.Offer(new DeskTransactionsCompleted(desk.DeskCode, now, Next(3)), now);
                    if (Next(10) > 0)
                        engine.Offer(new DeskLaneContext(desk.DeskCode, now.AddSeconds(-Next(120)), Next(5) == 0 ? null : 20 + Next(400), Next(4) == 0 ? null : Next(4) * Next(3)), now);
                }
            }

            var step = engine.Advance(now);
            minutes.AddRange(step.Minutes);
            transitions.AddRange(step.Transitions);
        }

        foreach (var desk in desks)
        {
            var mine = minutes.Where(m => m.DeskCode == desk.DeskCode).ToList();
            mine.Count.Should().BeGreaterThan(170);
            mine.Select(m => m.MinuteUtc).Should().Equal(Enumerable.Range(0, mine.Count).Select(m => T.AddMinutes(m)));
            mine.Should().OnlyContain(m => m.Closed + m.Idle + m.Serving + m.Paused + m.Unknown == TimeSpan.FromMinutes(1) && m.SensorDerived == TimeSpan.Zero);
            var chain = transitions.Where(t => t.DeskCode == desk.DeskCode).ToList();
            for (var i = 1; i < chain.Count; i++)
            {
                chain[i].From.Should().Be(chain[i - 1].To, "a desk leaves the state it entered");
                chain[i].AtUtc.Should().BeOnOrAfter(chain[i - 1].AtUtc);
            }
        }

        engine.Counters.Invalid.Should().BeGreaterThan(0, "desk C has a staff zone and refuses its contexts");
    }
}
