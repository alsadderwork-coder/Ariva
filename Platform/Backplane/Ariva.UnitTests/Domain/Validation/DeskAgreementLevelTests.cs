using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-149a (owner decision 2026-10-10, formulas F18): the desk-state agreement judges each observed minute at its desk's level in
/// Ariva's own record of that minute (the zone profile version in force, given as <see cref="DeskLevelRow"/>s, never the
/// observers): a desk with a staff or service zone on its full state, a desk without one at the throughput level, open (Idle or
/// Serving) against not open (Paused or Closed); Unknown never agrees at either level; a minute no row covers, and every minute
/// of a desk whose record was refused, on the full state (fail closed); beside it, Serving against Idle at the throughput level
/// with no target; the pooled agreement counts each minute at its level against the same target.
/// </summary>
public sealed class DeskAgreementLevelTests
{
    #region Fixtures

    private const int Version = 7;

    private static readonly Guid D01 = G(31);
    private static readonly Guid D02 = G(32);
    private static readonly Guid O1 = G(901);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    private static DateTime At(int hour, int minute) => new(2026, 10, 8, hour, minute, 0, DateTimeKind.Utc);

    private static readonly DateTime Published = At(6, 0);

    private static ComparisonScope Scope() =>
        new(Version, [new ScopeZone(G(1), "A-VIS")], [], [new UtcWindow(At(18, 0), At(19, 0))])
        {
            Desks = [new ScopeDesk(D01, "IMM", "D01"), new ScopeDesk(D02, "IMM", "D02")]
        };

    private static DeskObservationRow Seen(DateTime minute, ObservedDeskState state, Guid? desk = null) => new(desk ?? D01, minute, O1, 1, state);

    private static DeskMinuteRow Stored(DateTime minute, DeskStatus state, string desk = "D01") =>
        new("IMM", desk, minute, state == DeskStatus.Closed ? 60 : 0, state == DeskStatus.Idle ? 60 : 0, state == DeskStatus.Serving ? 60 : 0,
            state == DeskStatus.Paused ? 60 : 0, state == DeskStatus.Unknown ? 60 : 0, false);

    private static DeskLevelRow Level(DeskAgreementLevel level, Guid? desk = null, DateTime? from = null, DateTime? to = null, int version = 6) =>
        new(desk ?? D01, version, from ?? Published, to, level);

    private static ComparisonResult Compare(IReadOnlyList<DeskObservationRow> observations, IReadOnlyList<DeskMinuteRow> minutes, IReadOnlyList<DeskLevelRow> levels) =>
        ValidationComparison.Compare(new ComparisonInput { Scope = Scope(), DeskObservations = observations, DeskMinutes = minutes, DeskLevels = levels });

    #endregion

    #region Each pairing at both levels

    public static TheoryData<ObservedDeskState, DeskStatus, bool, bool> Pairings()
    {
        // Observed state, stored dominant state, agrees on the full state, agrees at the throughput level.
        var data = new TheoryData<ObservedDeskState, DeskStatus, bool, bool>();
        foreach (var observed in Enum.GetValues<ObservedDeskState>())
        {
            foreach (var system in Enum.GetValues<DeskStatus>())
            {
                var seen = DeskStateAgreement.StatusOf(observed);
                var open = seen is DeskStatus.Idle or DeskStatus.Serving;
                var full = system != DeskStatus.Unknown && seen == system;
                var throughput = system != DeskStatus.Unknown && open == (system is DeskStatus.Idle or DeskStatus.Serving);
                data.Add(observed, system, full, throughput);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairings))]
    public void Agrees_Should_JudgeEachPairingAtItsLevel_When_AnObservationMeetsAStoredState(ObservedDeskState observed, DeskStatus system, bool full, bool throughput)
    {
        DeskStateAgreement.Agrees(observed, system, DeskAgreementLevel.FullState).Should().Be(full);
        DeskStateAgreement.Agrees(observed, system, DeskAgreementLevel.Throughput).Should().Be(throughput);
        DeskStateAgreement.Agrees(observed, system, DeskAgreementLevel.FullState).Should().Be(DeskStateAgreement.Agrees(observed, system), "the full state is the rule of before");

        // Through the engine: one observed minute against one stored minute, the desk without a zone, then with one.
        var minute = At(18, 10);
        foreach (var (level, expected) in new[] { (DeskAgreementLevel.Throughput, throughput), (DeskAgreementLevel.FullState, full) })
        {
            var result = Compare([Seen(minute, observed)], [Stored(minute, system)], [Level(level)]);
            var judged = result.DeskMinutes.Single();
            (judged.Level, judged.Agrees).Should().Be((level, (bool?)expected));
            result.DeskOverall.Agreeing.Should().Be(expected ? 1 : 0);
            result.DeskOverall.JudgedAtThroughput.Should().Be(level == DeskAgreementLevel.Throughput ? 1 : 0);
        }
    }

    [Theory]
    [InlineData(ObservedDeskState.Idle, DeskStatus.Serving, true, false)]
    [InlineData(ObservedDeskState.Serving, DeskStatus.Idle, true, false)]
    [InlineData(ObservedDeskState.Paused, DeskStatus.Closed, true, false)]
    [InlineData(ObservedDeskState.Closed, DeskStatus.Paused, true, false)]
    [InlineData(ObservedDeskState.Serving, DeskStatus.Paused, false, false)]
    [InlineData(ObservedDeskState.Idle, DeskStatus.Closed, false, false)]
    [InlineData(ObservedDeskState.Closed, DeskStatus.Idle, false, false)]
    [InlineData(ObservedDeskState.Serving, DeskStatus.Unknown, false, false)]
    [InlineData(ObservedDeskState.Closed, DeskStatus.Unknown, false, false)]
    [InlineData(ObservedDeskState.Serving, DeskStatus.Serving, true, true)]
    [InlineData(ObservedDeskState.Paused, DeskStatus.Paused, true, true)]
    public void Agrees_Should_MatchTheTableOfF18_When_TheLevelsDiffer(ObservedDeskState observed, DeskStatus system, bool throughput, bool full)
    {
        // The rows of formulas F18's table (ARV-149a): open against not open, Unknown never agreeing.
        DeskStateAgreement.Agrees(observed, system, DeskAgreementLevel.Throughput).Should().Be(throughput);
        DeskStateAgreement.Agrees(observed, system, DeskAgreementLevel.FullState).Should().Be(full);
    }

    [Theory]
    [InlineData((ObservedDeskState)9, DeskStatus.Closed)]
    [InlineData((ObservedDeskState)9, DeskStatus.Idle)]
    [InlineData((ObservedDeskState)9, DeskStatus.Unknown)]
    public void Agrees_Should_NeverAgree_When_TheObservedStateIsNoneAPersonCanSee(ObservedDeskState observed, DeskStatus system)
    {
        DeskStateAgreement.Agrees(observed, system, DeskAgreementLevel.Throughput).Should().BeFalse();
        DeskStateAgreement.Agrees(observed, system, DeskAgreementLevel.FullState).Should().BeFalse();
    }

    [Fact]
    public void Agrees_Should_JudgeOnTheFullState_When_TheLevelIsNeitherOfAMinutesTwo()
    {
        DeskStateAgreement.Agrees(ObservedDeskState.Idle, DeskStatus.Serving, DeskAgreementLevel.Mixed).Should().BeFalse();
        DeskStateAgreement.Agrees(ObservedDeskState.Idle, DeskStatus.Serving, (DeskAgreementLevel)7).Should().BeFalse();
        DeskStateAgreement.Agrees(ObservedDeskState.Idle, DeskStatus.Idle, (DeskAgreementLevel)7).Should().BeTrue();
    }

    #endregion

    #region The level from Ariva's record

    [Fact]
    public void Compare_Should_JudgeEachMinuteAtItsLevel_When_ADesksZoneComesAndGoesDuringTheCampaign()
    {
        // D01 has a zone until 18:20 (version 6), none from 18:20 (version 7) and one again from 18:40 (version 8). The observer
        // sees Serving every minute; the stored minutes say Idle: they agree only while the desk has no zone.
        var minutes = Enumerable.Range(0, 60).Select(m => At(18, 0).AddMinutes(m)).ToList();
        var result = Compare([.. minutes.Select(m => Seen(m, ObservedDeskState.Serving))], [.. minutes.Select(m => Stored(m, DeskStatus.Idle))],
        [
            Level(DeskAgreementLevel.FullState, to: At(18, 20)),
            Level(DeskAgreementLevel.Throughput, from: At(18, 20), to: At(18, 40), version: 7),
            Level(DeskAgreementLevel.FullState, from: At(18, 40), version: 8)
        ]);

        result.DeskMinutes.Where(m => m.Level == DeskAgreementLevel.Throughput).Select(m => m.MinuteUtc).Should().Equal(minutes.GetRange(20, 20));
        var desk = result.Desks.Single(d => d.DeskId == D01);
        (desk.Judged, desk.JudgedAtThroughput, desk.Agreeing, desk.Level).Should().Be((60, 20, 20, (DeskAgreementLevel?)DeskAgreementLevel.Mixed));
        desk.Check.Value.Should().BeApproximately(20.0 / 60, 1e-12);
        // Serving against Idle at the throughput level: 20 minutes both call open, none agreeing on which.
        (desk.ServingIdleMinutes, desk.ServingIdleAgreeing, desk.ServingIdleAgreement).Should().Be((20, 0, (double?)0.0));
    }

    [Fact]
    public void Compare_Should_PoolEachMinuteAtItsLevel_When_DesksAreAtDifferentLevels()
    {
        // D01 without a zone: 20 minutes Serving observed, Idle stored (open both: agree), Serving against Idle agreeing in 0 of 20.
        // D02 with a zone: 20 minutes Serving observed, 19 Serving stored and 1 Idle (agrees 19). Pooled: 39 of 40, a pass.
        var minutes = Enumerable.Range(0, 20).Select(m => At(18, 0).AddMinutes(m)).ToList();
        var result = Compare(
            [.. minutes.Select(m => Seen(m, ObservedDeskState.Serving)), .. minutes.Select(m => Seen(m, ObservedDeskState.Serving, D02))],
            [.. minutes.Select(m => Stored(m, DeskStatus.Idle)), .. minutes.Select((m, i) => Stored(m, i == 0 ? DeskStatus.Idle : DeskStatus.Serving, "D02"))],
            [Level(DeskAgreementLevel.Throughput), Level(DeskAgreementLevel.FullState, D02)]);

        var d01 = result.Desks.Single(d => d.DeskId == D01);
        var d02 = result.Desks.Single(d => d.DeskId == D02);
        (d01.Agreeing, d01.Level, d01.ServingIdleAgreement, d01.Check.Verdict).Should().Be((20, (DeskAgreementLevel?)DeskAgreementLevel.Throughput, (double?)0.0, CriterionVerdict.Pass));
        (d02.Agreeing, d02.Level, d02.ServingIdleAgreement, d02.Check.Verdict).Should().Be((19, (DeskAgreementLevel?)DeskAgreementLevel.FullState, (double?)null, CriterionVerdict.Pass));
        var overall = result.DeskOverall;
        (overall.Judged, overall.Agreeing, overall.JudgedAtThroughput, overall.Level).Should().Be((40, 39, 20, (DeskAgreementLevel?)DeskAgreementLevel.Mixed));
        overall.Check.Should().Be(new CriterionCheck(39.0 / 40, 0.95, CriterionVerdict.Pass));
        overall.StrictAgreement.Should().BeApproximately(39.0 / 40, 1e-12);
        overall.ThroughputAgreement.Should().Be(1.0, "open or not open agrees everywhere");
        // The confusion stays observed against dominant state, whatever the level.
        overall.Confusion.Single(c => c.Observed == ObservedDeskState.Serving && c.System == DeskStatus.Idle).Minutes.Should().Be(21);
    }

    [Fact]
    public void Compare_Should_NeverAgreeOnUnknown_When_TheDeskIsAtEitherLevel()
    {
        var minute = At(18, 5);
        foreach (var level in new[] { DeskAgreementLevel.Throughput, DeskAgreementLevel.FullState })
        {
            var stored = Compare([Seen(minute, ObservedDeskState.Closed)], [Stored(minute, DeskStatus.Unknown)], [Level(level)]);
            stored.DeskMinutes.Single().Agrees.Should().BeFalse("an Unknown stored minute never agrees at {0}", level);
            var missing = Compare([Seen(minute, ObservedDeskState.Closed)], [], [Level(level)]);
            (missing.DeskMinutes.Single().SystemState, missing.DeskMinutes.Single().Agrees).Should().Be((DeskStatus.Unknown, (bool?)false), "no stored minute is Unknown");
        }
    }

    [Fact]
    public void Compare_Should_JudgeOnTheFullState_When_ArivasRecordSaysNothingOrCannotBeUsed()
    {
        var minute = At(18, 5);
        var observed = new[] { Seen(minute, ObservedDeskState.Idle) };
        var stored = new[] { Stored(minute, DeskStatus.Serving) };

        // No row, a row of another time, a row of a desk out of scope: the full state (Idle against Serving disagrees).
        Compare(observed, stored, []).DeskMinutes.Single().Should().Match<DeskMinuteAgreement>(m => m.Level == DeskAgreementLevel.FullState && m.Agrees == false);
        Compare(observed, stored, [Level(DeskAgreementLevel.Throughput, to: At(18, 5))]).DeskMinutes.Single().Level.Should().Be(DeskAgreementLevel.FullState,
            "a period ending at the minute's start does not cover it");
        var outOfScope = Compare(observed, stored, [Level(DeskAgreementLevel.Throughput, G(99))]);
        (outOfScope.DeskMinutes.Single().Level, outOfScope.LeftOut.DeskLevels).Should().Be((DeskAgreementLevel.FullState, 1));

        // A refused row (an end before its start, a time not UTC, an undefined level, Mixed, before 2000) puts its desk on the full
        // state for the whole comparison, even where a usable throughput row covers the minute; it is left out and counted.
        foreach (var refused in new[]
                 {
                     Level(DeskAgreementLevel.Throughput, from: At(18, 30), to: At(18, 0)),
                     Level(DeskAgreementLevel.Throughput, from: DateTime.SpecifyKind(Published, DateTimeKind.Unspecified)),
                     Level((DeskAgreementLevel)7, from: At(18, 30)),
                     Level(DeskAgreementLevel.Mixed, from: At(18, 30)),
                     Level(DeskAgreementLevel.Throughput, from: new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: At(18, 0))
                 })
        {
            var result = Compare(observed, stored, [Level(DeskAgreementLevel.Throughput), refused]);
            (result.DeskMinutes.Single().Level, result.DeskMinutes.Single().Agrees, result.LeftOut.DeskLevels).Should().Be((DeskAgreementLevel.FullState, (bool?)false, 1),
                refused.ToString());
        }

        // Two versions in force at once (publication does not allow it): the full state wins where they overlap.
        var overlap = Compare(observed, stored, [Level(DeskAgreementLevel.Throughput), Level(DeskAgreementLevel.FullState, from: At(18, 0), to: At(18, 10), version: 7)]);
        overlap.DeskMinutes.Single().Level.Should().Be(DeskAgreementLevel.FullState);
        // Without the overlap the same desk minute is at the throughput level and agrees (Idle and Serving are both open).
        Compare(observed, stored, [Level(DeskAgreementLevel.Throughput)]).DeskMinutes.Single().Should()
            .Match<DeskMinuteAgreement>(m => m.Level == DeskAgreementLevel.Throughput && m.Agrees == true);
    }

    [Fact]
    public void Compare_Should_JudgeAMinuteOnTheFullState_When_AVersionIsPublishedWithinIt()
    {
        // R-a of the ARV-149a review (CWE-501): the throughput level holds a minute only over all of it, so a version published
        // within a minute leaves that minute on the full state, whichever way the desk's zone changed. The observer sees Serving,
        // Ariva stored Idle: a minute agrees only at the throughput level.
        var minutes = Enumerable.Range(0, 20).Select(m => At(18, 0).AddMinutes(m)).ToList();
        var observed = minutes.Select(m => Seen(m, ObservedDeskState.Serving)).ToList();
        var stored = minutes.Select(m => Stored(m, DeskStatus.Idle)).ToList();
        var change = At(18, 10).AddSeconds(30);

        // A zone published at 18:10:30: 18:00 to 18:09 at the throughput level; 18:10, which it reached half way, on the full state.
        var came = Compare(observed, stored, [Level(DeskAgreementLevel.Throughput, to: change), Level(DeskAgreementLevel.FullState, from: change, version: 7)]);
        came.DeskMinutes.Where(m => m.Level == DeskAgreementLevel.Throughput).Select(m => m.MinuteUtc).Should().Equal(minutes.GetRange(0, 10));
        came.DeskMinutes.Single(m => m.MinuteUtc == At(18, 10)).Should().Match<DeskMinuteAgreement>(m => m.Level == DeskAgreementLevel.FullState && m.Agrees == false);
        (came.DeskOverall.JudgedAtThroughput, came.DeskOverall.Agreeing).Should().Be((10, 10));

        // The zone removed at 18:10:30: 18:10 still on the full state, the throughput level from 18:11.
        var went = Compare(observed, stored, [Level(DeskAgreementLevel.FullState, to: change), Level(DeskAgreementLevel.Throughput, from: change, version: 7)]);
        went.DeskMinutes.Where(m => m.Level == DeskAgreementLevel.Throughput).Select(m => m.MinuteUtc).Should().Equal(minutes.GetRange(11, 9));

        // With no row after it, a throughput period ending within 18:10 leaves 18:10 on the full state too; one ending at the
        // minute's end holds it, and one starting a second after its start does not.
        Compare(observed, stored, [Level(DeskAgreementLevel.Throughput, to: change)]).DeskMinutes.Single(m => m.MinuteUtc == At(18, 10)).Level
            .Should().Be(DeskAgreementLevel.FullState);
        Compare(observed, stored, [Level(DeskAgreementLevel.Throughput, to: At(18, 11))]).DeskMinutes.Single(m => m.MinuteUtc == At(18, 10)).Level
            .Should().Be(DeskAgreementLevel.Throughput);
        Compare(observed, stored, [Level(DeskAgreementLevel.Throughput, from: At(18, 10).AddSeconds(1))]).DeskMinutes
            .Where(m => m.Level == DeskAgreementLevel.Throughput).Select(m => m.MinuteUtc).Should().Equal(minutes.GetRange(11, 9));
    }

    [Fact]
    public void Compare_Should_GiveTheSameResult_When_TheLevelRowsComeInAnyOrder()
    {
        var minutes = Enumerable.Range(0, 60).Select(m => At(18, 0).AddMinutes(m)).ToList();
        var observations = minutes.Select((m, i) => Seen(m, (ObservedDeskState)(i % 4))).ToList();
        var stored = minutes.Select((m, i) => Stored(m, (DeskStatus)(i * 7 % 5))).ToList();
        DeskLevelRow[] levels =
        [
            Level(DeskAgreementLevel.FullState, to: At(18, 13)), Level(DeskAgreementLevel.Throughput, from: At(18, 13), to: At(18, 41), version: 7),
            Level(DeskAgreementLevel.FullState, from: At(18, 41), version: 8)
        ];
        var forward = Compare(observations, stored, levels);
        var backward = Compare(observations, stored, [.. levels.Reverse()]);

        System.Text.Json.JsonSerializer.Serialize(backward).Should().Be(System.Text.Json.JsonSerializer.Serialize(forward));
        forward.DeskOverall.JudgedAtThroughput.Should().Be(28);
    }

    [Fact]
    public void Compare_Should_KeepEveryDeskOnTheFullState_When_NoLevelIsGiven()
    {
        // Results of before ARV-149a, and any input without level rows: every minute on the full state, as the rule was.
        var minute = At(18, 5);
        var result = Compare([Seen(minute, ObservedDeskState.Idle)], [Stored(minute, DeskStatus.Serving)], []);
        (result.DeskOverall.JudgedAtThroughput, result.DeskOverall.Level, result.DeskOverall.ServingIdleAgreement, result.LeftOut.DeskLevels)
            .Should().Be((0, (DeskAgreementLevel?)DeskAgreementLevel.FullState, (double?)null, 0));
        result.Desks.Single(d => d.DeskId == D02).Level.Should().BeNull("a desk with no judged minute has no level");
    }

    #endregion
}
