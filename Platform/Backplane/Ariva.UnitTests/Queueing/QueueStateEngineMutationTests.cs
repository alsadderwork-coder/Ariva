using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-069a: the queue engine's Exit, FifoPartner and Reanchor were in Stryker's safe mode until the first checkpoint, so
/// their mutants were never run. These tests kill the ones that survived once they were: a duplicate exit that must not
/// pair, the method of mixed counting and crossing pairs, the earliest entrant across device groups, the instant at which
/// the queue reads empty, and the rejected resolution of an exit before its entrant's entry.
/// </summary>
public sealed partial class QueueStateEngineTests
{
    #region ARV-069a

    [Fact]
    public void Exit_Should_NotPairAnyoneElse_When_ATrackExitsTwice()
    {
        // The second exit of S-15/1 is a duplicate: it must stop there, not pair with the anonymous entrant still held.
        var step = Play(Engine(), 20, In(0, "S-15/1"), In(1), Out(3, "S-15/1"), Out(3.5, "S-15/1"));

        step.Waits.Should().ContainSingle().Which.Method.Should().Be(WaitMethod.Track);
        step.Rejections.Duplicates.Should().Be(1);
        step.Rejections.UnmatchedExits.Should().Be(0);
        step.Movements.Sum(m => m.Exits).Should().Be(1, "a duplicate is not an exit");
        step.Length.Count.Should().Be(1, "the anonymous entrant is still in the queue");
    }

    [Fact]
    public void Exit_Should_BeCumulative_When_ACrossingExitPairsWithACountedEntrant()
    {
        var step = Play(Engine(), 20, new QueueInterval("A-VIS entry", 1, 0, At(0), At(2)), Out(5));

        step.Waits.Should().ContainSingle().Which.Method.Should().Be(WaitMethod.Cumulative, "either side counted makes the pair cumulative");
    }

    [Fact]
    public void Exit_Should_BeCumulative_When_ACountedExitPairsWithACrossingEntrant()
    {
        var step = Play(Engine(), 20, In(0), new QueueInterval("A-VIS exit", 0, 1, At(4), At(6)));

        step.Waits.Should().ContainSingle().Which.Method.Should().Be(WaitMethod.Cumulative);
    }

    [Fact]
    public void Exit_Should_BeFifo_When_BothSidesAreCrossings()
    {
        var step = Play(Engine(), 20, In(0), Out(5));

        step.Waits.Should().ContainSingle().Which.Method.Should().Be(WaitMethod.Fifo);
    }

    [Fact]
    public void FifoPartner_Should_TakeTheEarliestAcrossDeviceGroups_When_ATrackedExitHasNoOpenTrack()
    {
        // The anonymous entrant at 18:00 is earlier than S-16's at 18:01; S-17's unknown track pairs with the earliest.
        var step = Play(Engine(), 20, In(0), In(1, "S-16/1"), Out(5, "S-17/9"));

        step.Waits.Should().ContainSingle().Which.Should().Match<RealisedWait>(w =>
            w.Method == WaitMethod.Fifo && w.EntryUtc == At(0) && w.Wait == TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void FifoPartner_Should_TakeTheEarliestAcrossDeviceGroups_When_TheAnonymousEntrantIsTheLater()
    {
        var step = Play(Engine(), 20, In(0, "S-16/1"), In(1), Out(5, "S-17/9"));

        step.Waits.Should().ContainSingle().Which.EntryUtc.Should().Be(At(0));
    }

    [Fact]
    public void Reanchor_Should_DropAnEntrantOfTheSameInstant_When_EveryZoneReadsZeroThen()
    {
        // F5 re-anchoring compares entry times with "at or before" the instant the queue reads empty: the reading of that
        // instant is applied with every other event of it, so someone who entered then is not in the queue either.
        var step = Play(Engine(), 30,
            In(0), In(6),
            new QueueOccupancy("A-VIS", 0, At(6)), new QueueOccupancy("A-OV", 0, At(6)),
            Out(8));

        step.Reanchors.Should().Be(1);
        step.Resolutions.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == EntrantOutcome.Reanchored);
        step.Waits.Should().BeEmpty();
        step.Rejections.UnmatchedExits.Should().Be(1);
    }

    [Fact]
    public void Exit_Should_RejectTheResolution_When_ItsEntrantEnteredAfterIt()
    {
        // Only a snapshot can hold an entrant from after a later exit (the engine processes in time order); the exit then
        // resolves it as rejected, never as a negative wait, and keeps the entrant's track.
        var engine = Engine();
        Play(engine, 2, In(1, "S-15/1"));
        var state = engine.Capture();
        var skewed = state with { Held = [.. state.Held.Select(h => h with { EntryUtc = At(30) })] };
        var restored = QueueStateEngine.Restore(Geometry, new QueueEngineSettings { Lateness = TimeSpan.Zero }, skewed);

        var step = Play(restored, 6, Out(5));

        step.Rejections.NegativeWaits.Should().Be(1);
        step.Waits.Should().BeEmpty();
        step.Resolutions.Should().ContainSingle().Which.Should().Match<EntrantResolution>(r =>
            r.Outcome == EntrantOutcome.Rejected && r.EntryUtc == At(30) && r.ResolvedUtc == At(30) && r.TrackKey == "S-15/1" && r.Tracked);
        step.Movements.Sum(m => m.Exits).Should().Be(1, "the exit is counted although its wait is refused");
    }

    [Fact]
    public void Exit_Should_RejectAnAnonymousResolution_When_ItsEntrantEnteredAfterIt()
    {
        var engine = Engine();
        Play(engine, 2, In(1));
        var state = engine.Capture();
        var skewed = state with { Held = [.. state.Held.Select(h => h with { EntryUtc = At(30) })] };
        var restored = QueueStateEngine.Restore(Geometry, new QueueEngineSettings { Lateness = TimeSpan.Zero }, skewed);

        var step = Play(restored, 6, Out(5));

        step.Resolutions.Should().ContainSingle().Which.Should().Match<EntrantResolution>(r =>
            r.Outcome == EntrantOutcome.Rejected && r.TrackKey == null && !r.Tracked);
    }

    #endregion
}
