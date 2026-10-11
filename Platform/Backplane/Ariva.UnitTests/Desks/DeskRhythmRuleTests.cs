using Ariva.Core.Desks;
using Ariva.UnitTests.Properties;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-149: F10 rows 5a to 5f, the state of a desk whose only sources are AMAN's session and transactions (no staff or
/// service zone), from its transaction rhythm against its lane's expected service time s and the people waiting in its
/// queue. The table is the formula's (docs/domain/formulas.md F10, "AMAN-only desks"): T1 = 3 min, T2 = 10 min,
/// T_stale = 2 min, the longest transaction 30 min, k = 4 and I = 1 min (the reference values).
/// </summary>
public sealed class DeskRhythmRuleTests
{
    private static readonly DateTime T = new(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DeskStateSettings Settings = new();
    private static readonly DeskProfile Aman = new("DMO/IMM/AR-09", "VIS", HasTransactions: true, HasSession: true, HasStaffZone: false, HasServiceZone: false);
    private static readonly DeskProfile WithStaffZone = Aman with { DeskCode = "DMO/IMM/AR-10", HasStaffZone = true };
    private static readonly DeskProfile WithServiceZone = Aman with { DeskCode = "DMO/IMM/AR-11", HasServiceZone = true };

    // Logged in 30 minutes ago with AMAN live (both sources heard at T); a lane context taken contextAgo minutes before T
    // (none when null) with s seconds and q waiting since waitingAgo minutes; the last interval with a transaction ended
    // lastAgo minutes before T (none when null).
    private static DeskMemory Memory(double? lastAgo, double? s, int? q, double? waitingAgo = 30, double? contextAgo = 0, bool queueDegraded = false) => new()
    {
        TransactionsHeard = T,
        SessionHeard = T,
        Session = DeskSessionSignal.Opened,
        SessionSince = T.AddMinutes(-30),
        SessionSignalAt = T.AddMinutes(-30),
        LastTransaction = lastAgo.HasValue ? T.AddMinutes(-lastAgo.Value) : null,
        LaneContextAt = contextAgo.HasValue ? T.AddMinutes(-contextAgo.Value) : null,
        LaneServiceSeconds = contextAgo.HasValue ? s : null,
        QueueWaiting = contextAgo.HasValue ? q : null,
        QueueWaitingSince = contextAgo.HasValue && q > 0 && waitingAgo.HasValue ? T.AddMinutes(-waitingAgo.Value) : null,
        QueueDegraded = queueDegraded
    };

    // The formula's table: case, last interval with a transaction ended (min ago), s (seconds), Q, people waiting since (min
    // ago), lane context taken (min ago; null: none), expected status.
    public static TheoryData<string, double?, double?, int?, double?, double?, DeskStatus> Rows => new()
    {
        // 5a: s = 1.5 min, so the rhythm window is s + I = 2.5 min (P = 6 min).
        { "5a: an interval with a transaction ended 2 min ago", 2, 90, 0, null, 0, DeskStatus.Serving },
        { "5a: the rhythm counts whatever the queue is not known", 2, 90, null, null, 0, DeskStatus.Serving },
        { "5a ends at s + I: 2.5 min, nobody waiting (5d)", 2.5, 90, 0, null, 0, DeskStatus.Idle },
        { "5d: logged in with nobody waiting, silent 45 min (the empty hall)", 45, 90, 0, null, 0, DeskStatus.Idle },
        { "5d: nobody waiting and no transaction yet", null, null, 0, null, 0, DeskStatus.Idle },
        // 5b and 5c: P = max(T1, min(k x s, 30 min)) = 6 min for s = 1.5 min.
        { "5b: people waiting, silent 5 min (a long transaction)", 5, 90, 4, 20, 0, DeskStatus.Serving },
        { "5c: people waiting, silent 6 min", 6, 90, 4, 20, 0, DeskStatus.Paused },
        { "5b: a queue formed 2 min ago after a quiet spell", 20, 90, 4, 2, 0, DeskStatus.Serving },
        { "5c: a queue waiting 7 min, silent 20 min", 20, 90, 4, 7, 0, DeskStatus.Paused },
        { "5b: people waiting, no transaction since the login 30 min ago, waiting 1 min", null, 90, 4, 1, 0, DeskStatus.Serving },
        // 5e and 5f: the queue not known (no zone, or no fresh minute).
        { "5f: queue not known, silent 5 min", 5, 90, null, null, 0, DeskStatus.Idle },
        { "5e: queue not known, silent 6 min", 6, 90, null, null, 0, DeskStatus.Paused },
        // The bounds of P: s = 30 s gives k x s = 2 min, below T1, so P = 3 min (rhythm window 1.5 min).
        { "P at least T1: s = 0.5 min, silent 2.5 min", 2.5, 30, 4, 20, 0, DeskStatus.Serving },
        { "P at least T1: s = 0.5 min, silent 3 min", 3, 30, 4, 20, 0, DeskStatus.Paused },
        { "P at least T1: s = 0.5 min, rhythm window 1.5 min", 1.4, 30, null, null, 0, DeskStatus.Serving },
        // s = 10 min gives k x s = 40 min, capped at the longest transaction, 30 min (rhythm window s + I = 11 min).
        { "P at most 30 min: s = 10 min, silent 29 min", 29, 600, 4, 40, 0, DeskStatus.Serving },
        { "P at most 30 min: s = 10 min, silent 30 min", 30, 600, 4, 40, 0, DeskStatus.Paused },
        { "rhythm window s + I: s = 10 min, 10.9 min, queue not known", 10.9, 600, null, null, 0, DeskStatus.Serving },
        // s not known (no documents in the window): P = T1 and no rhythm.
        { "s not known, people waiting, silent 2 min", 2, null, 4, 10, 0, DeskStatus.Serving },
        { "s not known, people waiting, silent 3 min", 3, null, 4, 10, 0, DeskStatus.Paused },
        { "s not known, nobody waiting, silent 20 min", 20, null, 0, null, 0, DeskStatus.Idle },
        // No lane context, or a stale one (T_stale = 2 min): rows 5 and 7 as before (Paused after T1 without a transaction).
        { "no lane context: 2 min after a transaction", 2, null, null, null, null, DeskStatus.Idle },
        { "no lane context: 4 min after a transaction", 4, null, null, null, null, DeskStatus.Paused },
        { "a stale context (2 min old) counts as none", 4, 90, 4, 20, 2, DeskStatus.Paused },
        { "a context 1.9 min old still counts", 4, 90, 4, 20, 1.9, DeskStatus.Serving },
        { "a stale context gives no rhythm either", 2, 90, 0, null, 2, DeskStatus.Idle }
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Rule_Should_FollowTheTransactionRhythm_When_ADeskHasAmansSourcesAlone(string because, double? lastAgo, double? s, int? q, double? waitingAgo,
        double? contextAgo, DeskStatus expected)
    {
        var e = DeskRule.Evaluate(Aman, Memory(lastAgo, s, q, waitingAgo, contextAgo), T, Settings);

        e.Status.Should().Be(expected, because);
        e.SensorDerived.Should().BeFalse("the login is AMAN's: the shadow nowcast never counts it as seen by the sensors");
        e.PresentNotProcessing.Should().BeFalse();
        e.Degraded.Should().BeFalse("every source is live and no queue length was degraded");
    }

    [Fact]
    public void Rule_Should_KeepTheHigherRows_When_ALogoutABreakATransactionOrSilenceDecides()
    {
        var waiting = Memory(1, 90, 4, 20);

        DeskRule.Evaluate(Aman, waiting with { Session = DeskSessionSignal.Closed }, T, Settings).Status.Should().Be(DeskStatus.Closed, "row 2: a logout");
        DeskRule.Evaluate(Aman, waiting with { Session = DeskSessionSignal.Paused }, T, Settings).Status.Should().Be(DeskStatus.Paused, "row 3a: AMAN's on break");
        DeskRule.Evaluate(Aman, Memory(45, 90, 0) with { TransactionSince = T.AddSeconds(-20) }, T, Settings).Status
            .Should().Be(DeskStatus.Serving, "row 3: a transaction in progress");
        DeskRule.Evaluate(Aman, waiting with { TransactionsHeard = T.AddMinutes(-3), SessionHeard = T.AddMinutes(-3) }, T, Settings).Status
            .Should().Be(DeskStatus.Unknown, "row 1: AMAN silent beyond T_stale, whatever the queue");
        DeskRule.Evaluate(Aman, waiting with { Session = null, SessionSince = null, SessionSignalAt = null }, T, Settings).Status
            .Should().Be(DeskStatus.Closed, "row 9: never logged in");
    }

    [Fact]
    public void Rule_Should_StayIdleAndDegraded_When_TheTransactionSourceIsStale()
    {
        // As before ARV-149: without a live activity source the login alone is Idle, flagged; the rhythm needs transactions.
        var e = DeskRule.Evaluate(Aman, Memory(10, 90, 4, 20) with { TransactionsHeard = T.AddMinutes(-3) }, T, Settings);

        e.Status.Should().Be(DeskStatus.Idle);
        e.Degraded.Should().BeTrue();
    }

    [Fact]
    public void Rule_Should_FlagTheState_When_ADegradedQueueLengthDecidedIt()
    {
        DeskRule.Evaluate(Aman, Memory(20, 90, 0, queueDegraded: true), T, Settings).Should().Be(new DeskEvaluation(DeskStatus.Idle, false, false, true), "5d");
        DeskRule.Evaluate(Aman, Memory(4, 90, 3, 20, queueDegraded: true), T, Settings).Should().Be(new DeskEvaluation(DeskStatus.Serving, false, false, true), "5b");
        DeskRule.Evaluate(Aman, Memory(7, 90, 3, 20, queueDegraded: true), T, Settings).Should().Be(new DeskEvaluation(DeskStatus.Paused, false, false, true), "5c");
        DeskRule.Evaluate(Aman, Memory(1, 90, 3, 20, queueDegraded: true), T, Settings).Should().Be(new DeskEvaluation(DeskStatus.Serving, false, false, false),
            "5a: the rhythm decided, not the queue");
    }

    [Fact]
    public void Rule_Should_IgnoreTheLaneContext_When_TheDeskHasAStaffOrServiceZone()
    {
        // Desks with zones are unchanged: staff present is Idle (row 7) whatever the queue; the empty staff zone pauses after T1.
        var present = Memory(5, 90, 4, 20) with { StaffHeard = T, StaffCount = 1 };
        DeskRule.Evaluate(WithStaffZone, present, T, Settings).Status.Should().Be(DeskStatus.Idle);
        DeskRule.Evaluate(WithStaffZone, present with { StaffCount = 0, StaffEmptySince = T.AddMinutes(-4) }, T, Settings).Status.Should().Be(DeskStatus.Paused);
        DeskRule.Evaluate(WithServiceZone, Memory(1, 90, 0) with { ServiceHeard = T, ServiceCount = 0 }, T, Settings).Status
            .Should().Be(DeskStatus.Idle, "the rhythm's Serving is for desks without zones only");
        DeskRule.FollowsRhythm(Aman).Should().BeTrue();
        DeskRule.FollowsRhythm(WithStaffZone).Should().BeFalse();
        DeskRule.FollowsRhythm(WithServiceZone).Should().BeFalse();
        DeskRule.FollowsRhythm(Aman with { HasSession = false }).Should().BeFalse("a desk without a login source keeps rows 4 to 7");
        DeskRule.FollowsRhythm(Aman with { HasTransactions = false }).Should().BeFalse();
        DeskRule.FollowsRhythm(null).Should().BeFalse();
    }

    [Fact]
    public void Rule_Should_TreatAContextOutOfBoundsAsNotKnown_When_ARestoredMemoryHoldsOne()
    {
        // CWE-501: a memory is data from storage; s or Q out of bounds is not known (rows 5e and 5f), never an exception.
        foreach (var s in new[] { double.NaN, double.PositiveInfinity, -1, 0, 3_601 })
            DeskRule.Evaluate(Aman, Memory(2, s, null), T, Settings).Status.Should().Be(DeskStatus.Idle, "s = {0} gives no rhythm and P = T1", s);
        foreach (var q in new[] { -1, DeskLaneContext.MaxWaiting + 1 })
            DeskRule.Evaluate(Aman, Memory(4, 90, q, 1), T, Settings).Status.Should().Be(DeskStatus.Idle, "Q = {0} is not known: under P = 6 min", q);
        DeskRule.Evaluate(Aman, Memory(2, 90, 0, contextAgo: -1), T, Settings).Status.Should().Be(DeskStatus.Idle, "a context dated after the moment counts as none");
    }

    [Fact]
    public void PauseAfter_Should_BeKTimesSWithinT1AndTheLongestTransaction_When_SIsKnown()
    {
        Settings.RhythmPauseAfter(null).Should().Be(TimeSpan.FromMinutes(3), "T1 without s");
        Settings.RhythmPauseAfter(TimeSpan.FromSeconds(90)).Should().Be(TimeSpan.FromMinutes(6));
        Settings.RhythmPauseAfter(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromMinutes(3));
        Settings.RhythmPauseAfter(TimeSpan.FromMinutes(10)).Should().Be(TimeSpan.FromMinutes(30));
        (Settings with { RhythmPauseMultiple = 2 }).RhythmPauseAfter(TimeSpan.FromSeconds(95)).Should().Be(TimeSpan.FromSeconds(190), "2 x 95 s is above T1");
        (Settings with { RhythmPauseMultiple = double.NaN }).RhythmPauseAfter(TimeSpan.FromSeconds(90)).Should().Be(TimeSpan.FromMinutes(30), "never an exception");
    }

    // F10 rows 5a to 5f: each threshold NextChange gives on its own (T_stale 2, T1 3, T2 10; s = 1.5 min: s + I 2.5, P 6).
    public static TheoryData<string, DeskMemory, double, double?> Thresholds => new()
    {
        { "the rhythm window after the last interval with a transaction", new DeskMemory { LastTransaction = T, LaneServiceSeconds = 90 }, 0, 2.5 },
        { "then T1", new DeskMemory { LastTransaction = T, LaneServiceSeconds = 90 }, 2.5, 3 },
        { "then P", new DeskMemory { LastTransaction = T, LaneServiceSeconds = 90 }, 3, 6 },
        { "then T2", new DeskMemory { LastTransaction = T, LaneServiceSeconds = 90 }, 6, 10 },
        { "the lane context going stale", new DeskMemory { LaneContextAt = T }, 0, 2 },
        { "T1 after the queue formed", new DeskMemory { QueueWaitingSince = T }, 0, 3 },
        { "P after the queue formed", new DeskMemory { QueueWaitingSince = T, LaneServiceSeconds = 90 }, 3, 6 },
        { "P after the login", new DeskMemory { SessionSince = T, LaneServiceSeconds = 90 }, 3, 6 },
        { "nothing after the waiting's P", new DeskMemory { QueueWaitingSince = T, LaneServiceSeconds = 90 }, 6, null },
        { "s out of bounds is not a threshold", new DeskMemory { QueueWaitingSince = T, LaneServiceSeconds = double.NaN }, 3, null }
    };

    [Theory]
    [MemberData(nameof(Thresholds))]
    public void NextChange_Should_GiveEachRhythmThreshold_When_ItIsTheOnlyOneAhead(string because, DeskMemory memory, double afterMinutes, double? expectedMinutes)
    {
        DeskRule.NextChange(Aman, memory, T.AddMinutes(afterMinutes), Settings).Should().Be(expectedMinutes.HasValue ? T.AddMinutes(expectedMinutes.Value) : null, because);
    }

    [Fact]
    public void NextChange_Should_IgnoreTheLaneContext_When_TheDeskHasZones()
    {
        DeskRule.NextChange(WithStaffZone, new DeskMemory { LaneContextAt = T, QueueWaitingSince = T, LaneServiceSeconds = 90 }, T, Settings).Should().BeNull();
    }

    [Theory]
    [InlineData(1.9, 60)]
    [InlineData(20.1, 60)]
    [InlineData(double.NaN, 60)]
    [InlineData(4, 9)]
    [InlineData(4, 901)]
    public void Settings_Should_BeRefused_When_TheRhythmIsOutOfBounds(double multiple, int intervalSeconds)
    {
        new DeskStateSettings { RhythmPauseMultiple = multiple, CompletedInterval = TimeSpan.FromSeconds(intervalSeconds) }.Problems().Should().ContainSingle();
    }

    [Fact]
    public void Settings_Should_TakeTheReferenceRhythm_When_NotGiven()
    {
        Settings.RhythmPauseMultiple.Should().Be(4);
        Settings.CompletedInterval.Should().Be(TimeSpan.FromMinutes(1), "AMAN contract V1's interval");
        Settings.Problems().Should().BeEmpty();
        new DeskStateSettings { RhythmPauseMultiple = 2, CompletedInterval = TimeSpan.FromSeconds(10) }.Problems().Should().BeEmpty();
        new DeskStateSettings { RhythmPauseMultiple = 20, CompletedInterval = TimeSpan.FromMinutes(15) }.Problems().Should().BeEmpty();
    }

    #region Properties

    // Random lane contexts: s from 1 s to an hour, Q from 0 to 50, the silence and the waiting up to two hours, k from 2 to 20.
    private static readonly Gen<(double S, int Q, double Last, double Waiting, double K)> Inputs =
        Gen.Select(Gen.Double[1, 3_600], Gen.Int[0, 50], Gen.Double[0, 120], Gen.Double[0, 120], Gen.Double[2, 20]);

    private static DeskStatus Status(double s, int? q, double lastAgo, double waitingAgo, double k) =>
        DeskRule.Evaluate(Aman, Memory(lastAgo, s, q, waitingAgo), T, Settings with { RhythmPauseMultiple = k }).Status;

    [Fact]
    public void Rule_Should_NeverLeavePausedAsTheSilenceGrows_When_PeopleWait()
    {
        // Monotone in the silence: with the context fixed, a desk Paused after a silence is Paused after any longer one.
        Gen.Select(Inputs, Gen.Double[0, 60]).Sample((input, more) =>
        {
            var (s, q, last, waiting, k) = input;
            var people = Math.Max(1, q);
            if (Status(s, people, last, waiting, k) == DeskStatus.Paused)
                Status(s, people, last + more, waiting + more, k).Should().Be(DeskStatus.Paused);
        }, iter: Fuzz.Iterations);
    }

    [Fact]
    public void Rule_Should_NeverPause_When_NobodyWaits()
    {
        Inputs.Sample(input =>
        {
            var (s, _, last, waiting, k) = input;
            Status(s, 0, last, waiting, k).Should().BeOneOf(DeskStatus.Idle, DeskStatus.Serving);
        }, iter: Fuzz.Iterations);
    }

    [Fact]
    public void Rule_Should_GiveAnOpenOrPausedState_When_ALoggedInDeskHasALaneContext()
    {
        // Bounds: a logged-in desk with AMAN live is never Closed or Unknown by these rows, and the rhythm's Serving does not
        // depend on the queue.
        Inputs.Sample(input =>
        {
            var (s, q, last, waiting, k) = input;
            Status(s, q, last, waiting, k).Should().BeOneOf(DeskStatus.Idle, DeskStatus.Serving, DeskStatus.Paused);
            if (Status(s, q, last, waiting, k) == DeskStatus.Serving && last < Math.Min(s / 60 + 1, Settings.RhythmPauseAfter(TimeSpan.FromSeconds(s)).TotalMinutes))
                Status(s, 0, last, waiting, k).Should().Be(DeskStatus.Serving);
        }, iter: Fuzz.Iterations);
    }

    [Fact]
    public void Rule_Should_NeverPauseEarlier_When_TheMultipleIsLarger()
    {
        Gen.Select(Inputs, Gen.Double[0, 18]).Sample((input, extra) =>
        {
            var (s, q, last, waiting, k) = input;
            if (Status(s, Math.Max(1, q), last, waiting, Math.Min(20, k + extra)) == DeskStatus.Paused)
                Status(s, Math.Max(1, q), last, waiting, k).Should().Be(DeskStatus.Paused);
        }, iter: Fuzz.Iterations);
    }

    [Fact]
    public void PauseAfter_Should_StayWithinT1AndTheLongestTransaction_When_SAndKVary()
    {
        Gen.Select(Gen.Double[0.001, 3_600], Gen.Double[2, 20]).Sample((s, k) =>
        {
            var pause = (Settings with { RhythmPauseMultiple = k }).RhythmPauseAfter(TimeSpan.FromSeconds(s));
            pause.Should().BeGreaterThanOrEqualTo(Settings.PauseAfter).And.BeLessThanOrEqualTo(Settings.MaxTransaction);
            (Settings with { RhythmPauseMultiple = k }).RhythmPauseAfter(TimeSpan.FromSeconds(Math.Min(3_600, s * 2))).Should().BeGreaterThanOrEqualTo(pause, "monotone in s");
        }, iter: Fuzz.Iterations);
    }

    #endregion
}
