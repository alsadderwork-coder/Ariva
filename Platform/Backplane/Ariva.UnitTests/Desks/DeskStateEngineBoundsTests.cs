using Ariva.Core.Desks;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-069a: the desk state engine's Offer and Restore were in Stryker's safe mode until the first checkpoint, so their
/// mutants were never run. These tests pin the bounds that survived once they were: the lateness limit, what counts as
/// ahead of the clock, the per-desk ahead cap, the buffer and the fair share once it is half full, the order of signals of
/// one instant, and every check a snapshot passes on restore, at its exact limits.
/// </summary>
public sealed class DeskStateEngineBoundsTests
{
    #region Helpers

    private static readonly DateTime T = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DeskStateSettings NoLateness = new() { Lateness = TimeSpan.Zero };

    private static DeskProfile Desk(string code) => new(code, "VIS", false, true, false, false);

    private static DeskProfile[] Desks(int count) => [.. Enumerable.Range(0, count).Select(k => Desk($"D{k:00}"))];

    private static DeskSignal Beat(string desk, DateTime at) => new DeskHeartbeat(desk, at, DeskSource.Session);

    private static DeskPendingState Pending(string desk, int second, bool ahead = false) =>
        new(new DeskSignalState("heartbeat", desk, T.AddSeconds(second), DeskSource.Session), ahead, second);

    /// <summary>A fresh engine's snapshot with its first desk changed by <paramref name="change"/>.</summary>
    private static DeskEngineState WithFirst(DeskProfile[] desks, DeskStateSettings settings, Func<DeskState, DeskState> change)
    {
        var state = new DeskStateEngine(desks, T, settings).Capture();
        return state with { Desks = [change(state.Desks[0]), .. state.Desks.Skip(1)] };
    }

    #endregion

    #region Offer

    [Fact]
    public void Offer_Should_AcceptASignal_When_ItIsExactlyAtTheLatenessLimit()
    {
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness);
        engine.Advance(T.AddMinutes(30));

        engine.Offer(Beat("D01", T.AddMinutes(15)), T.AddMinutes(30));
        engine.Offer(Beat("D01", T.AddMinutes(15).AddTicks(-1)), T.AddMinutes(30));

        engine.Counters.Should().BeEquivalentTo(new { Accepted = 1, Late = 1, TooLate = 1 }, "MaxLate (15 minutes) behind the watermark is still accepted");
    }

    [Fact]
    public void Offer_Should_NotCountASignalAsAhead_When_ItIsAtTheReferenceTime()
    {
        // MaxBufferedSignalsPerDesk 10 allows one signal ahead of the clock; signals at the reference time are not ahead.
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness with { MaxBufferedSignalsPerDesk = 10 });

        for (var k = 0; k < 3; k++)
            engine.Offer(Beat("D01", T.AddMinutes(1)), T.AddMinutes(1));

        engine.Counters.Should().BeEquivalentTo(new { Accepted = 3, BufferFull = 0 });
    }

    [Fact]
    public void Offer_Should_HoldATenthOfTheDeskCapAhead_When_SignalsRunAheadOfTheClock()
    {
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness with { MaxBufferedSignalsPerDesk = 100 });

        for (var k = 0; k < 11; k++)
            engine.Offer(Beat("D01", T.AddMinutes(1).AddSeconds(k)), T);

        engine.Counters.Should().BeEquivalentTo(new { Accepted = 10, BufferFull = 1 }, "a tenth of 100 may wait ahead of the reference clock");
    }

    [Fact]
    public void Offer_Should_RefuseTheNextSignal_When_TheBufferIsExactlyFull()
    {
        // Twelve desks in turn: 100 signals leave each desk below its share (10), so only the full buffer refuses the 101st.
        var desks = Desks(12);
        var engine = new DeskStateEngine(desks, T, NoLateness with { MaxBufferedSignals = 100, MaxBufferedSignalsPerDesk = 100 });

        for (var k = 0; k < 101; k++)
            engine.Offer(Beat(desks[k % 12].DeskCode, T.AddSeconds(k)), T.AddMinutes(5));

        engine.Counters.Should().BeEquivalentTo(new { Accepted = 100, BufferFull = 1 });
    }

    [Fact]
    public void Offer_Should_HoldADeskToItsShare_When_TheBufferIsHalfFull()
    {
        // Two desks and a buffer of 100: once 50 are buffered, a desk holds at most 100 / 2 / 2 = 25.
        var engine = new DeskStateEngine([Desk("D01"), Desk("D02")], T, NoLateness with { MaxBufferedSignals = 100, MaxBufferedSignalsPerDesk = 100 });

        for (var k = 0; k < 30; k++)
            engine.Offer(Beat("D02", T.AddSeconds(k)), T.AddMinutes(5));
        for (var k = 0; k < 26; k++)
            engine.Offer(Beat("D01", T.AddSeconds(k)), T.AddMinutes(5));

        engine.Counters.Should().BeEquivalentTo(new { Accepted = 55, BufferFull = 1 });
    }

    [Fact]
    public void Offer_Should_ApplySignalsOfOneInstantInArrivalOrder_When_TheyConflict()
    {
        var engine = new DeskStateEngine([Desk("D01")], T, NoLateness);
        engine.Offer(Beat("D01", T), T);
        engine.Offer(new DeskSessionChangedSignal("D01", T.AddSeconds(10), DeskSessionSignal.Opened), T);
        engine.Offer(new DeskSessionChangedSignal("D01", T.AddSeconds(10), DeskSessionSignal.Closed), T);

        engine.Advance(T.AddSeconds(30));

        engine.Status("D01").Status.Should().Be(DeskStatus.Closed, "the logout arrived after the login of the same instant");
    }

    #endregion

    #region Restore

    [Fact]
    public void Restore_Should_RefuseTheWatermark_When_ItIsInTheYear9000()
    {
        var state = new DeskStateEngine(Desks(1), T, NoLateness).Capture() with { WatermarkUtc = new DateTime(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        var restore = () => DeskStateEngine.Restore(Desks(1), NoLateness, state);

        restore.Should().Throw<InvalidDataException>().WithMessage("*watermark*");
    }

    [Fact]
    public void Restore_Should_TakeAsManyDesksAsTheEngineKeeps_When_TheSnapshotHasExactlyThatMany()
    {
        var settings = NoLateness with { MaxDesks = 2 };
        var state = new DeskStateEngine(Desks(2), T, settings).Capture();

        DeskStateEngine.Restore(Desks(2), settings, state).Capture().Desks.Should().HaveCount(2);

        var more = state with { Desks = [.. state.Desks, state.Desks[0] with { DeskCode = "D99" }] };
        var restore = () => DeskStateEngine.Restore(Desks(2), settings, more);
        restore.Should().Throw<InvalidDataException>().WithMessage("*more desks*");
    }

    [Fact]
    public void Restore_Should_StartEveryDeskFresh_When_TheSnapshotHasNoDeskList()
    {
        var state = new DeskStateEngine(Desks(2), T, NoLateness).Capture() with { Desks = null };

        DeskStateEngine.Restore(Desks(2), NoLateness, state).Capture().Desks.Select(d => d.DeskCode).Should().Equal("D00", "D01");
    }

    [Fact]
    public void Restore_Should_DropADesk_When_ItIsNoLongerConfigured()
    {
        var state = new DeskStateEngine(Desks(2), T, NoLateness).Capture();

        var restored = DeskStateEngine.Restore(Desks(1), NoLateness, state);

        restored.Capture().Desks.Select(d => d.DeskCode).Should().Equal("D00");
    }

    [Fact]
    public void Restore_Should_AcceptEveryValue_When_ItIsExactlyAtItsLimit()
    {
        var settings = NoLateness with { MaxBufferedSignalsPerDesk = 10 };
        var desks = Desks(1);
        var state = WithFirst(desks, settings, d => d with
        {
            Pending = [.. Enumerable.Range(0, 10).Select(k => Pending(d.DeskCode, k))],
            Ticks = [TimeSpan.TicksPerMinute, 0, 0, 0, 0],
            SensorTicks = TimeSpan.TicksPerMinute,
            PresentTicks = TimeSpan.TicksPerMinute,
            CursorUtc = T.AddDays(1)
        });

        var restore = () => DeskStateEngine.Restore(desks, settings, state);

        restore.Should().NotThrow("a desk's cap of pending signals, a whole minute of ticks and a cursor a day ahead are allowed");
    }

    public static TheoryData<string, Func<DeskState, DeskState>> BeyondLimits => new()
    {
        { "one pending signal more than the desk's cap", d => d with { Pending = [.. Enumerable.Range(0, 11).Select(k => Pending(d.DeskCode, k))] } },
        { "ticks adding up to more than a minute, none above it", d => d with { Ticks = [TimeSpan.TicksPerMinute / 2 + 1, TimeSpan.TicksPerMinute / 2 + 1, 0, 0, 0] } },
        { "sensor ticks above a minute", d => d with { SensorTicks = TimeSpan.TicksPerMinute + 1 } },
        { "present ticks above a minute", d => d with { PresentTicks = TimeSpan.TicksPerMinute + 1 } },
        { "a cursor more than a day ahead", d => d with { CursorUtc = T.AddDays(1).AddTicks(1) } },
        { "an empty pending signal", d => d with { Pending = [new DeskPendingState(null, false, 0)] } },
        { "a pending signal of another desk", d => d with { Pending = [Pending("D77", 0)] } }
    };

    [Theory]
    [MemberData(nameof(BeyondLimits))]
    public void Restore_Should_RefuseTheSnapshot_When_AValueIsBeyondItsLimit(string because, Func<DeskState, DeskState> change)
    {
        var settings = NoLateness with { MaxBufferedSignalsPerDesk = 10 };
        var desks = Desks(1);
        var state = WithFirst(desks, settings, change);

        var restore = () => DeskStateEngine.Restore(desks, settings, state);

        restore.Should().Throw<InvalidDataException>(because);
    }

    [Fact]
    public void Restore_Should_RestoreADesk_When_ItHasNoPendingList()
    {
        var desks = Desks(1);
        var state = WithFirst(desks, NoLateness, d => d with { Pending = null });

        DeskStateEngine.Restore(desks, NoLateness, state).Capture().Desks.Single().Pending.Should().BeEmpty();
    }

    [Fact]
    public void Restore_Should_RefuseTheSnapshot_When_ItHoldsMoreSignalsThanTheEngineKeeps()
    {
        var settings = NoLateness with { MaxBufferedSignals = 100, MaxBufferedSignalsPerDesk = 60 };
        var desks = Desks(2);
        var state = new DeskStateEngine(desks, T, settings).Capture();
        DeskEngineState Holding(int second) => state with
        {
            Desks = [state.Desks[0] with { Pending = [.. Enumerable.Range(0, 50).Select(k => Pending("D00", k))] },
                state.Desks[1] with { Pending = [.. Enumerable.Range(0, second).Select(k => Pending("D01", k))] }]
        };

        ((Action)(() => DeskStateEngine.Restore(desks, settings, Holding(50)))).Should().NotThrow("100 is the engine's buffer");
        ((Action)(() => DeskStateEngine.Restore(desks, settings, Holding(51)))).Should().Throw<InvalidDataException>().WithMessage("*more signals*");
    }

    [Fact]
    public void Restore_Should_KeepTheAheadCap_When_TheSnapshotHoldsSignalsAhead()
    {
        // A tenth of 100 may wait ahead: the restored engine knows ten already do, so it refuses the eleventh.
        var settings = NoLateness with { MaxBufferedSignalsPerDesk = 100 };
        var engine = new DeskStateEngine([Desk("D01")], T, settings);
        for (var k = 0; k < 10; k++)
            engine.Offer(Beat("D01", T.AddMinutes(1).AddSeconds(k)), T);

        var restored = DeskStateEngine.Restore([Desk("D01")], settings, engine.Capture());
        restored.Offer(Beat("D01", T.AddMinutes(1).AddSeconds(30)), T);

        restored.Counters.BufferFull.Should().Be(1);
        restored.Counters.Accepted.Should().Be(10);
    }

    [Fact]
    public void Restore_Should_KeepTheBufferCount_When_TheSnapshotHoldsSignals()
    {
        var desks = Desks(12);
        var settings = NoLateness with { MaxBufferedSignals = 100, MaxBufferedSignalsPerDesk = 100 };
        var engine = new DeskStateEngine(desks, T, settings);
        for (var k = 0; k < 100; k++)
            engine.Offer(Beat(desks[k % 12].DeskCode, T.AddSeconds(k)), T.AddMinutes(5));

        var restored = DeskStateEngine.Restore(desks, settings, engine.Capture());
        restored.Offer(Beat("D11", T.AddSeconds(200)), T.AddMinutes(5));

        restored.Counters.BufferFull.Should().Be(1, "the restored buffer is still full");
    }

    #endregion
}
