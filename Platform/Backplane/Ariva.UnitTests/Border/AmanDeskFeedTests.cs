using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Core.Flights;
using FluentAssertions;

namespace Ariva.UnitTests.Border;

/// <summary>
/// ARV-049: AMAN's stored records into the desk state engine at their F10 rank (sessions rank 2, interval transactions
/// rank 1 as activity), the feed itself as the heartbeat of every AMAN desk, the read cursor that takes each record once,
/// and e-gate rejects joining their manual lane (F12).
/// </summary>
public sealed class AmanDeskFeedTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
    private const string V1 = "DMO/IMM/AR-09";
    private const string V2 = "DMO/IMM/AR-10";

    private static AmanFeedRecord Session(string desk, DateTime at, string state, DateTime? received = null) =>
        new(AmanRecordKind.DeskSession, Guid.NewGuid(), received ?? at, desk, null, at, state);

    private static AmanFeedRecord Interval(string desk, DateTime start, int transactions) =>
        new(AmanRecordKind.DeskInterval, Guid.NewGuid(), start.AddSeconds(62), desk, null, start, Transactions: transactions);

    private static AmanFeedRecord Gate(DateTime start, int attempts, int rejected, string gate = "DMO/IMM/AG-1") =>
        new(AmanRecordKind.EgateInterval, Guid.NewGuid(), start.AddSeconds(62), gate, "EG", start, Attempts: attempts, Rejected: rejected, MeanCycleSeconds: 18);

    [Fact]
    public void Step_Should_MapEachRecordToItsRank_When_AmanRecordsArrive()
    {
        var step = AmanDeskFeed.Step([
            Session(V1, T0.AddSeconds(30), "Opened"),
            Session(V2, T0.AddSeconds(40), "Paused"),
            Interval(V1, T0, 3),
            Gate(T0, 10, 3),
            Interval(null, T0, 5),
            Session(V1, T0.AddSeconds(50), "opened")
        ], [V1, V2], null, T0.AddMinutes(2));

        step.Signals.OfType<DeskSessionChangedSignal>().Should().BeEquivalentTo([
            new DeskSessionChangedSignal(V1, T0.AddSeconds(30), DeskSessionSignal.Opened),
            new DeskSessionChangedSignal(V2, T0.AddSeconds(40), DeskSessionSignal.Paused)
        ], "a state that is not an exact name gives nothing");
        step.Signals.OfType<DeskTransactionsCompleted>().Should().ContainSingle().Which.Should().Be(new DeskTransactionsCompleted(V1, T0.AddMinutes(1), 3),
            "an interval is its transactions completed when it closes, never a transaction in progress");
        step.EgateMinutes.Should().ContainSingle().Which.Should().Be(new EgateMinute("DMO/IMM/AG-1", "EG", T0, 10, 3, 18));
        step.Signals.Select(s => s.TimeUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Step_Should_GiveEveryDeskAHeartbeatEachMinuteTheFeedIsAlive_When_RecordsArrive()
    {
        // First read: the minute of the latest record only.
        var first = AmanDeskFeed.Step([Gate(T0, 4, 0)], [V1, V2], null, T0.AddMinutes(2));
        first.HeartbeatUtc.Should().Be(T0.AddMinutes(1), "an interval shows the feed alive when it closes");
        first.Signals.OfType<DeskHeartbeat>().Should().HaveCount(4).And.OnlyContain(h => h.TimeUtc == T0.AddMinutes(1));

        // Later reads fill every minute since the last heartbeat, for the session and transaction sources of every desk; an unmapped record counts.
        var later = AmanDeskFeed.Step([Interval(null, T0.AddMinutes(3), 2)], [V1], T0.AddMinutes(1), T0.AddMinutes(5));
        later.Signals.OfType<DeskHeartbeat>().Select(h => (h.TimeUtc, h.Of)).Should().BeEquivalentTo([
            (T0.AddMinutes(2), DeskSource.Session), (T0.AddMinutes(2), DeskSource.Transactions),
            (T0.AddMinutes(3), DeskSource.Session), (T0.AddMinutes(3), DeskSource.Transactions),
            (T0.AddMinutes(4), DeskSource.Session), (T0.AddMinutes(4), DeskSource.Transactions)
        ]);
        later.HeartbeatUtc.Should().Be(T0.AddMinutes(4));

        // No record, no heartbeat: when AMAN stops, its desks go stale. A long gap is caught up from its end only.
        AmanDeskFeed.Step([], [V1], T0, T0.AddMinutes(1)).Should().Match<AmanDeskFeedStep>(s => s.Signals.Count == 0 && s.HeartbeatUtc == T0);
        AmanDeskFeed.Step([Gate(T0.AddHours(2), 1, 0)], [V1], T0, T0.AddHours(3)).Signals.Should().HaveCount(2 * AmanDeskFeed.MaxHeartbeatMinutes);
    }

    [Fact]
    public void Engine_Should_KeepAClosedDeskClosedWhileTheFeedLivesAndUnknownOnceItStops_When_FedByAman()
    {
        var engine = new DeskStateEngine([Profile(V1), Profile(V2)], T0, new DeskStateSettings { Lateness = TimeSpan.FromSeconds(90) });
        var heartbeat = (DateTime?)null;
        void Feed(DateTime now, params AmanFeedRecord[] records)
        {
            var step = AmanDeskFeed.Step(records, [V1, V2], heartbeat, now);
            heartbeat = step.HeartbeatUtc;
            foreach (var signal in step.Signals)
                engine.Offer(signal, now);
            engine.Advance(now);
        }

        Feed(T0.AddMinutes(1), Session(V1, T0.AddSeconds(30), "Opened"), Session(V2, T0.AddSeconds(30), "Closed"), Gate(T0, 4, 0));
        for (var m = 1; m <= 30; m++)
            Feed(T0.AddMinutes(m + 1).AddSeconds(30), Interval(V1, T0.AddMinutes(m), 2), Gate(T0.AddMinutes(m), 4, 0));
        engine.Status(V1).Status.Should().Be(DeskStatus.Idle, "logged in with transactions: open (rank 2 and rank 1 activity)");
        engine.Status(V2).Status.Should().Be(DeskStatus.Closed, "a logout half an hour ago still holds while AMAN's feed is alive");
        engine.Lane("VIS").OpenServers.Should().Be(1);

        Feed(T0.AddMinutes(33), Session(V1, T0.AddMinutes(31).AddSeconds(10), "Paused"), Gate(T0.AddMinutes(31), 4, 0));
        engine.Status(V1).Status.Should().Be(DeskStatus.Paused, "AMAN's on break");

        Feed(T0.AddMinutes(45));
        engine.Status(V1).Status.Should().Be(DeskStatus.Unknown, "the feed stopped beyond T_stale");
        engine.Status(V2).Status.Should().Be(DeskStatus.Unknown);
    }

    private static DeskProfile Profile(string desk) => new(desk, "VIS", HasTransactions: true, HasSession: true, HasStaffZone: false, HasServiceZone: false);

    [Fact]
    public void Cursor_Should_TakeEachRecordOnceAndRereadTheOverlap_When_ReadsOverlap()
    {
        var cursor = AmanFeedCursor.Start(T0);
        cursor.ReadFromUtc.Should().Be(T0 - AmanFeedCursor.Overlap);
        var a = Session(V1, T0, "Opened", T0.AddSeconds(10));
        var b = Session(V1, T0, "Closed", T0.AddSeconds(20));
        var (fresh, next) = cursor.Take([b, a]);
        fresh.Should().Equal(a, b);
        next.PositionUtc.Should().Be(T0.AddSeconds(20));

        // A record committed late with an earlier receipt time is still found in the overlap; the others are not taken again.
        var late = Session(V2, T0, "Opened", T0.AddSeconds(15));
        var (again, after) = next.Take([a, late, b]);
        again.Should().Equal(late);
        after.Taken.Select(t => t.Id).Should().BeEquivalentTo([a.Id, b.Id, late.Id]);

        // Records older than the overlap are forgotten.
        var (_, much) = after.Take([Session(V1, T0, "Opened", T0.AddMinutes(10))]);
        much.Taken.Should().ContainSingle();
        var (none, still) = much.Take([]);
        none.Should().BeEmpty();
        still.PositionUtc.Should().Be(much.PositionUtc, "nothing read, nothing moves");
    }

    // The feed's read: records received from the cursor's start, not taken yet, in receipt and id order, at most a limit.
    private static IReadOnlyList<AmanFeedRecord> Read(IEnumerable<AmanFeedRecord> stored, AmanFeedCursor cursor, int limit) =>
        stored.Where(r => r.ReceivedUtc >= cursor.ReadFromUtc && !cursor.TakenIds.Contains(r.Id)).OrderBy(r => r.ReceivedUtc).ThenBy(r => r.Id).Take(limit).ToList();

    [Fact]
    public void Cursor_Should_ReadABurstLargerThanTheLimitOverSeveralSteps_When_ManyRecordsShareAReceiptTime()
    {
        const int limit = 100;
        var stored = Enumerable.Range(0, 3 * limit).Select(_ => Session(V1, T0, "Opened", T0.AddSeconds(5))).ToList();
        var cursor = AmanFeedCursor.Start(T0);
        var taken = new List<Guid>();
        for (var step = 0; step < 4; step++)
        {
            var (fresh, next) = cursor.Take(Read(stored, cursor, limit), 2 * limit);
            taken.AddRange(fresh.Select(r => r.Id));
            cursor = next;
        }

        taken.Should().OnlyHaveUniqueItems().And.HaveCount(3 * limit, "every record of the burst once, though the limit is a third of it");

        // A later record still arrives: the read position is never stuck behind the burst.
        var later = Session(V2, T0, "Closed", T0.AddSeconds(30));
        stored.Add(later);
        cursor.Take(Read(stored, cursor, limit), 2 * limit).Fresh.Should().Equal(later);
    }

    [Fact]
    public void Cursor_Should_ForgetTheOldestAndNotReadBehindThem_When_MoreAreTakenThanItKeeps()
    {
        var stored = Enumerable.Range(0, 30).Select(i => Session(V1, T0, "Opened", T0.AddSeconds(i))).ToList();
        var (fresh, next) = AmanFeedCursor.Start(T0).Take(Read(stored, AmanFeedCursor.Start(T0), 100), 10);
        fresh.Should().HaveCount(30);
        next.Taken.Should().HaveCount(10);
        next.Floor.Should().Be(T0.AddSeconds(20));
        Read(stored, next, 100).Should().BeEmpty("nothing before the floor is read again, so nothing is taken twice");
    }

    [Fact]
    public void Step_Should_NeverMoveTheHeartbeatPastNow_When_ARecordIsDatedAhead()
    {
        var now = T0.AddMinutes(10);
        var ahead = AmanDeskFeed.Step([Session(V1, T0.AddHours(24), "Opened")], [V1], T0.AddMinutes(9), now);
        ahead.HeartbeatUtc.Should().Be(T0.AddMinutes(9), "a record a day ahead does not show the feed alive");
        AmanDeskFeed.Step([Gate(T0.AddMinutes(12), 1, 0)], [V1], T0.AddMinutes(9), now).HeartbeatUtc.Should().Be(now, "never past Ariva's clock");
        AmanDeskFeed.Step([Gate(T0.AddMinutes(9), 1, 0)], [V1], T0.AddHours(24), now).HeartbeatUtc.Should().Be(now,
            "a heartbeat saved in the future is brought back to now, so the next minutes are heard");

        // A day-ahead record among the normal ones: a closed desk stays Closed minute after minute.
        var engine = new DeskStateEngine([Profile(V2)], T0, new DeskStateSettings { Lateness = TimeSpan.FromSeconds(90) });
        DateTime? heartbeat = null;
        for (var m = 0; m < 20; m++)
        {
            var at = T0.AddMinutes(m + 1).AddSeconds(30);
            AmanFeedRecord[] records = m == 0
                ? [Session(V2, T0.AddSeconds(10), "Closed"), Session(V2, T0.AddHours(24), "Opened"), Gate(T0, 1, 0)]
                : [Gate(T0.AddMinutes(m), 1, 0)];
            var step = AmanDeskFeed.Step(records, [V2], heartbeat, at);
            heartbeat = step.HeartbeatUtc;
            foreach (var signal in step.Signals)
                engine.Offer(signal, at);
            engine.Advance(at);
        }

        engine.Status(V2).Status.Should().Be(DeskStatus.Closed);
    }

    [Fact]
    public void Couple_Should_AddTheRejectsToTheirManualLaneAfterTheLag_When_FormulasF12()
    {
        // F12: e-gate flow 100 in an interval with r = 0.07 adds 7 to the manual lane's arrivals, a lag later.
        var minutes = new List<MinuteDemand>
        {
            new(T0, new LaneCounts(0, 0, 10, 0, 100)),
            new(T0.AddMinutes(1), new LaneCounts(0, 0, 10, 0, 0)),
            new(T0.AddMinutes(2), new LaneCounts(0, 0, 10, 0, 0))
        };
        var coupled = EgateCoupling.Couple(minutes, 0.07, 1, "VIS", new Dictionary<DateTime, double> { [T0.AddMinutes(-1)] = 4 });
        coupled.Select(m => Math.Round(m.Lanes.Vis, 9)).Should().Equal(14, 17, 10);
        coupled.Select(m => m.Lanes.EGate).Should().Equal(new[] { 100d, 0, 0 }, "rejected passengers queue at the gates first");

        EgateCoupling.RejectRate(200, 14, new EgateCouplingSettings()).Should().Be((0.07, true));
        EgateCoupling.RejectRate(10, 9, new EgateCouplingSettings()).Should().Be((0.07, false), "too few attempts: the reference rate");
        EgateCoupling.RejectRate(100, 101, new EgateCouplingSettings()).Should().Be((0.07, false));
        var badRate = () => EgateCoupling.Couple(minutes, 1.5, 1, "VIS");
        badRate.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Settings_Should_BeBounded_When_Checked()
    {
        new EgateCouplingSettings().Problems().Should().BeEmpty();
        new EgateCouplingSettings { RejectLane = "EG" }.Problems().Should().ContainSingle().Which.Should().Contain("RejectLane");
        new EgateCouplingSettings { LagMinutes = 11 }.Problems().Should().ContainSingle();
        new EgateCouplingSettings { ReferenceRejectRate = -0.1 }.Problems().Should().ContainSingle();
        new EgateCouplingSettings { RateWindowMinutes = 4 }.Problems().Should().ContainSingle();
        new Ariva.Infra.Border.DeskFeedSettings().Problems().Should().BeEmpty();
        new Ariva.Infra.Border.DeskFeedSettings { PollSeconds = 0, MaxRead = 1 }.Problems().Should().HaveCount(2);
    }
}
