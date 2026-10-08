using Ariva.Core.Desks;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104f: the desk-state agreement of F18. The formula (observed minutes whose dominant stored state equals the observed
/// state over observed minutes; 19 of 20 is 95 percent and passes, 18 of 20 fails), the Proposed rules (the dominant state of
/// a stored minute and its ties, Unknown as disagreement, each observer's latest revision, several observers of one minute,
/// Degraded minutes counted and tallied), and the guards (an unusable latest revision never lets an earlier one stand in, an
/// observer's unusable state leaves the desk minute unjudged, an unusable stored minute is Unknown, rows out of scope or off
/// the planned days left out and counted, no division by zero).
/// </summary>
public sealed class DeskAgreementTests
{
    #region Fixtures

    private const int Version = 7;
    private const string Vis = "A-VIS";

    private static readonly Guid ZoneA = G(1);
    private static readonly Guid D01 = G(31);
    private static readonly Guid D02 = G(32);
    private static readonly Guid D03 = G(33);
    private static readonly Guid O1 = G(901);
    private static readonly Guid O2 = G(902);
    private static readonly Guid O3 = G(903);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    private static DateTime At(int hour, int minute, int second = 0) => new(2026, 10, 8, hour, minute, second, DateTimeKind.Utc);

    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private static ComparisonScope Scope(params UtcWindow[] windows) =>
        new(Version, [new ScopeZone(ZoneA, Vis)], [], windows.Length == 0 ? [new UtcWindow(At(18, 0), At(19, 0))] : windows)
        {
            Desks = [new ScopeDesk(D01, "IMM", "D01"), new ScopeDesk(D02, "IMM", "D02"), new ScopeDesk(D03, "EMI", "D03")]
        };

    private static DeskObservationRow Seen(DateTime minute, ObservedDeskState state, Guid? desk = null, Guid? observer = null, int revision = 1) =>
        new(desk ?? D01, minute, observer ?? O1, revision, state);

    /// <summary>A stored minute of a desk spent wholly in one state (Unknown for 60 seconds Unknown).</summary>
    private static DeskMinuteRow Stored(DateTime minute, DeskStatus state, string desk = "D01", bool degraded = false, string checkpoint = "IMM") =>
        Stored(minute, state == DeskStatus.Closed ? 60 : 0, state == DeskStatus.Idle ? 60 : 0, state == DeskStatus.Serving ? 60 : 0,
            state == DeskStatus.Paused ? 60 : 0, state == DeskStatus.Unknown ? 60 : 0, desk, degraded, checkpoint);

    private static DeskMinuteRow Stored(DateTime minute, double closed, double idle, double serving, double paused, double unknown, string desk = "D01",
        bool degraded = false, string checkpoint = "IMM") =>
        new(checkpoint, desk, minute, closed, idle, serving, paused, unknown, degraded);

    private static ComparisonResult Compare(ComparisonInput input, ComparisonSettings settings = null) => ValidationComparison.Compare(input, settings);

    private static ComparisonResult Compare(IReadOnlyList<DeskObservationRow> observations, IReadOnlyList<DeskMinuteRow> minutes, ComparisonScope scope = null) =>
        Compare(new ComparisonInput { Scope = scope ?? Scope(), DeskObservations = observations, DeskMinutes = minutes });

    private static DeskAgreementSummary DeskOf(ComparisonResult result, Guid desk) => result.Desks.Single(d => d.DeskId == desk);

    #endregion

    #region Formulas (F18)

    [Theory]
    [InlineData(0, 0, 60, 0, 0, DeskStatus.Serving)]
    [InlineData(0, 60, 0, 0, 0, DeskStatus.Idle)]
    [InlineData(60, 0, 0, 0, 0, DeskStatus.Closed)]
    [InlineData(0, 0, 0, 60, 0, DeskStatus.Paused)]
    [InlineData(0, 0, 0, 0, 60, DeskStatus.Unknown)]
    [InlineData(0, 20, 40, 0, 0, DeskStatus.Serving)]
    [InlineData(20, 0, 40, 0, 0, DeskStatus.Serving)]
    [InlineData(31, 29, 0, 0, 0, DeskStatus.Closed)]
    [InlineData(0, 30, 30, 0, 0, DeskStatus.Serving)]
    [InlineData(30, 30, 0, 0, 0, DeskStatus.Idle)]
    [InlineData(30, 0, 0, 30, 0, DeskStatus.Paused)]
    [InlineData(15, 15, 15, 15, 0, DeskStatus.Serving)]
    [InlineData(0, 0, 30, 0, 30, DeskStatus.Unknown)]
    [InlineData(0, 0, 31, 0, 29, DeskStatus.Serving)]
    [InlineData(10, 10, 10, 10, 20, DeskStatus.Unknown)]
    [InlineData(0, 0, 30, 0, 0, DeskStatus.Unknown)]
    [InlineData(0, 0, 31, 0, 0, DeskStatus.Serving)]
    [InlineData(0, 0, 0, 0, 0, DeskStatus.Unknown)]
    [InlineData(25, 0, 25, 0, 0, DeskStatus.Serving)]
    [InlineData(20, 0, 20, 0, 0, DeskStatus.Unknown)]
    public void Dominant_Should_TakeTheStateWithTheMostSecondsAndUnknownOnATie_When_AMinuteIsSplit(double closed, double idle, double serving, double paused,
        double unknown, DeskStatus dominant)
    {
        // Unaccounted seconds are Unknown: 30 s Serving alone ties 30 s unaccounted, and 20 and 20 lose to 20 unaccounted (a tie).
        DeskStateAgreement.Dominant(closed, idle, serving, paused, unknown).Should().Be(dominant);
    }

    [Fact]
    public void Dominant_Should_TieWithinANanosecond_When_SecondsComeFromTicks()
    {
        DeskStateAgreement.Dominant(0, 0, 30 + 1e-10, 0, 30 - 1e-10).Should().Be(DeskStatus.Unknown, "within 1e-9 seconds is a tie, and Unknown wins a tie");
        DeskStateAgreement.Dominant(0, 0, 30 + 1e-8, 0, 30 - 1e-8).Should().Be(DeskStatus.Serving);
        DeskStateAgreement.Dominant(0, 30 + 1e-10, 30 - 1e-10, 0, 0).Should().Be(DeskStatus.Serving, "a tie between known states goes to the more active");
    }

    [Theory]
    [InlineData(double.NaN, 0, 0, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0, 0, 0)]
    [InlineData(0, 0, -0.001, 0, 0)]
    [InlineData(0, 0, 60.001, 0, 0)]
    [InlineData(0, 0, 0, 0, double.NegativeInfinity)]
    [InlineData(30, 30, 1, 0, 0)]
    [InlineData(60, 0, 0, 0, 60)]
    public void Dominant_Should_BeNull_When_TheSecondsCannotBeAMinute(double closed, double idle, double serving, double paused, double unknown)
    {
        DeskStateAgreement.Dominant(closed, idle, serving, paused, unknown).Should().BeNull();
    }

    [Fact]
    public void Dominant_Should_AllowAMicrosecondOfRounding_When_TheStatesSumToAMinute()
    {
        DeskStateAgreement.Dominant(20, 20, 20.0000004, 0, 0).Should().Be(DeskStatus.Serving);
        DeskStateAgreement.Dominant(20, 20, 20.000002, 0, 0).Should().BeNull();
    }

    [Theory]
    [InlineData(19, 20, 0.95)]
    [InlineData(20, 20, 1.0)]
    [InlineData(0, 20, 0.0)]
    [InlineData(18, 20, 0.9)]
    [InlineData(1, 3, 1.0 / 3)]
    public void Agreement_Should_BeAgreeingOverJudged_When_MinutesWereJudged(int agreeing, int judged, double agreement)
    {
        DeskStateAgreement.Agreement(agreeing, judged).Should().BeApproximately(agreement, 1e-12);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(21, 20)]
    [InlineData(-1, 20)]
    [InlineData(0, -1)]
    public void Agreement_Should_BeNull_When_NothingWasJudgedOrTheCountsCannotBe(int agreeing, int judged)
    {
        DeskStateAgreement.Agreement(agreeing, judged).Should().BeNull();
    }

    [Theory]
    [InlineData(ObservedDeskState.Closed, DeskStatus.Closed, true)]
    [InlineData(ObservedDeskState.Idle, DeskStatus.Idle, true)]
    [InlineData(ObservedDeskState.Serving, DeskStatus.Serving, true)]
    [InlineData(ObservedDeskState.Paused, DeskStatus.Paused, true)]
    [InlineData(ObservedDeskState.Idle, DeskStatus.Serving, false)]
    [InlineData(ObservedDeskState.Closed, DeskStatus.Paused, false)]
    [InlineData(ObservedDeskState.Closed, DeskStatus.Unknown, false)]
    [InlineData(ObservedDeskState.Serving, DeskStatus.Unknown, false)]
    [InlineData((ObservedDeskState)9, DeskStatus.Unknown, false)]
    [InlineData((ObservedDeskState)9, DeskStatus.Closed, false)]
    public void Agrees_Should_MatchTheStateAndNeverUnknown_When_AnObservationMeetsAStoredState(ObservedDeskState observed, DeskStatus system, bool agrees)
    {
        DeskStateAgreement.Agrees(observed, system).Should().Be(agrees);
    }

    [Theory]
    [InlineData(DeskStatus.Idle, true)]
    [InlineData(DeskStatus.Serving, true)]
    [InlineData(DeskStatus.Paused, false)]
    [InlineData(DeskStatus.Closed, false)]
    [InlineData(DeskStatus.Unknown, false)]
    public void CountsForThroughput_Should_BeIdleOrServing_When_AStateIsGiven(DeskStatus status, bool counts)
    {
        DeskStateAgreement.CountsForThroughput(status).Should().Be(counts);
    }

    #endregion

    #region Agreement

    [Theory]
    [InlineData(1, 0.95, CriterionVerdict.Pass)]
    [InlineData(0, 1.0, CriterionVerdict.Pass)]
    [InlineData(2, 0.90, CriterionVerdict.Fail)]
    [InlineData(20, 0.0, CriterionVerdict.Fail)]
    public void Compare_Should_JudgeTheShareOfAgreeingMinutesAgainst95Percent_When_20MinutesWereObserved(int disagreeing, double agreement, CriterionVerdict verdict)
    {
        var observations = Enumerable.Range(0, 20).Select(m => Seen(At(18, m), ObservedDeskState.Serving)).ToList();
        var minutes = Enumerable.Range(0, 20).Select(m => Stored(At(18, m), m < disagreeing ? DeskStatus.Idle : DeskStatus.Serving)).ToList();

        var result = Compare(observations, minutes);

        var desk = DeskOf(result, D01);
        (desk.Minutes, desk.Judged, desk.Agreeing, desk.Excluded).Should().Be((20, 20, 20 - disagreeing, 0));
        desk.Check.Should().Be(new CriterionCheck(agreement, 0.95, verdict));
        result.DeskOverall.Check.Should().Be(new CriterionCheck(agreement, 0.95, verdict), "the other desks hold no observed minute");
        result.DeskMinutes.Should().HaveCount(20).And.OnlyContain(m => m.Standing == ComparisonStanding.Good && m.SystemStored && m.Observers == 1);
        result.DeskMinutes.Count(m => m.Agrees == false).Should().Be(disagreeing);
        result.LeftOut.Should().Be(LeftOutInputs.None);
    }

    [Fact]
    public void Compare_Should_GiveNoDataInsteadOfDividingByZero_When_NoMinuteWasObserved()
    {
        var result = Compare([], [Stored(At(18, 0), DeskStatus.Serving), Stored(At(18, 1), DeskStatus.Idle, "D02")]);

        result.DeskMinutes.Should().BeEmpty();
        result.Desks.Select(d => d.DeskCode).Should().Equal("D03", "D01", "D02");
        result.Desks.Append(result.DeskOverall).Should().AllSatisfy(d =>
        {
            d.Check.Should().Be(new CriterionCheck(null, 0.95, CriterionVerdict.NoData));
            (d.Minutes, d.Judged, d.Agreeing, d.Excluded, d.WithoutSystemMinute).Should().Be((0, 0, 0, 0, 0));
            d.ThroughputAgreement.Should().BeNull();
            d.Confusion.Should().HaveCount(20).And.OnlyContain(c => c.Minutes == 0);
        });
        (result.DeskOverall.DeskId, result.DeskOverall.CheckpointCode, result.DeskOverall.DeskCode).Should().Be(((Guid?)null, (string)null, (string)null));
    }

    [Fact]
    public void Compare_Should_GiveNoData_When_TheScopeHasNoDesks()
    {
        var result = Compare(new ComparisonInput { Scope = Scope() with { Desks = null }, DeskObservations = [Seen(At(18, 0), ObservedDeskState.Idle)] });

        result.Problem.Should().BeNull("a campaign planned without desks has none");
        result.Desks.Should().BeEmpty();
        result.DeskOverall.Check.Verdict.Should().Be(CriterionVerdict.NoData);
        result.LeftOut.DeskObservations.Should().Be(1, "a desk out of scope");
    }

    public static TheoryData<string, DeskMinuteRow[], bool, UnusableKeyReason?> UnknownStoredMinutes() => new()
    {
        { "60 seconds Unknown", [Stored(At(18, 5), DeskStatus.Unknown)], true, null },
        { "30 seconds Serving beside 30 Unknown (a tie)", [Stored(At(18, 5), 0, 0, 30, 0, 30)], true, null },
        { "30 seconds Serving and 30 not accounted for", [Stored(At(18, 5), 0, 0, 30, 0, 0)], true, null },
        { "no stored minute", [], false, null },
        { "no stored minute of this desk", [Stored(At(18, 5), DeskStatus.Serving, "D02")], false, null },
        { "a stored minute of more than 60 seconds", [Stored(At(18, 5), 30, 0, 31, 0, 0)], false, UnusableKeyReason.Refused },
        { "a stored minute with a negative second", [Stored(At(18, 5), 0, 0, 61, 0, -1)], false, UnusableKeyReason.Refused },
        { "a stored minute that is not a number", [Stored(At(18, 5), 0, 0, double.NaN, 0, 0)], false, UnusableKeyReason.Refused },
        { "a stored minute not in UTC", [Stored(Unspecified(At(18, 5)), DeskStatus.Serving)], false, UnusableKeyReason.Refused },
        { "a stored minute with a twin not in UTC", [Stored(At(18, 5), DeskStatus.Serving), Stored(Unspecified(At(18, 5)), DeskStatus.Serving)], false, UnusableKeyReason.Refused },
        { "two stored minutes that disagree", [Stored(At(18, 5), DeskStatus.Serving), Stored(At(18, 5), DeskStatus.Idle)], false, UnusableKeyReason.Conflicting },
        { "two stored minutes that disagree on the flag", [Stored(At(18, 5), DeskStatus.Serving), Stored(At(18, 5), DeskStatus.Serving, degraded: true)], false, UnusableKeyReason.Conflicting }
    };

    [Theory]
    [MemberData(nameof(UnknownStoredMinutes))]
    public void Compare_Should_CountTheMinuteAsADisagreeingUnknown_When_TheStoredStateIsUnknownMissingOrUnusable(string because, DeskMinuteRow[] stored, bool storedRow,
        UnusableKeyReason? reason)
    {
        var result = Compare([Seen(At(18, 5), ObservedDeskState.Serving)], stored);

        var minute = result.DeskMinutes.Should().ContainSingle().Subject;
        (minute.SystemState, minute.SystemStored, minute.Standing, minute.Agrees).Should().Be((DeskStatus.Unknown, storedRow, ComparisonStanding.Unknown, (bool?)false), because);
        var desk = DeskOf(result, D01);
        (desk.Judged, desk.Agreeing, desk.Excluded, desk.WithoutSystemMinute).Should().Be((1, 0, 0, storedRow ? 0 : 1), because);
        desk.Standings.Should().Be(StandingTally.None with { Unknown = 1 }, because);
        desk.Check.Should().Be(new CriterionCheck(0.0, 0.95, CriterionVerdict.Fail), "Unknown counts as disagreement (F18, Proposed)");
        if (reason is { } r)
            result.LeftOut.UnusableKeys.Should().Equal(new UnusableKey(UnusableKeyKind.DeskMinute, null, null, At(18, 5), null, r, D01));
        else
            result.LeftOut.UnusableKeys.Should().BeEmpty(because);
    }

    [Fact]
    public void Compare_Should_CountDegradedMinutesInTheAgreementAndTallyThemApart_When_TheStoredMinuteIsFlaggedOrPartlyUnknown()
    {
        var result = Compare(
            [Seen(At(18, 0), ObservedDeskState.Serving), Seen(At(18, 1), ObservedDeskState.Serving), Seen(At(18, 2), ObservedDeskState.Idle), Seen(At(18, 3), ObservedDeskState.Idle)],
            [
                Stored(At(18, 0), DeskStatus.Serving, degraded: true), Stored(At(18, 1), 0, 0, 50, 0, 10), Stored(At(18, 2), 0, 59, 0, 0, 0),
                Stored(At(18, 3), DeskStatus.Idle)
            ]);

        result.DeskMinutes.Select(m => (m.SystemState, m.Standing, m.Agrees)).Should().Equal((DeskStatus.Serving, ComparisonStanding.Degraded, (bool?)true),
            (DeskStatus.Serving, ComparisonStanding.Degraded, true), (DeskStatus.Idle, ComparisonStanding.Degraded, true), (DeskStatus.Idle, ComparisonStanding.Good, true));
        var desk = DeskOf(result, D01);
        desk.Standings.Should().Be(StandingTally.None with { Good = 1, Degraded = 3 });
        desk.Check.Should().Be(new CriterionCheck(1.0, 0.95, CriterionVerdict.Pass), "Degraded minutes are judged: only Unknown disagrees by rule");
    }

    [Fact]
    public void Compare_Should_ReportTheConfusionAndTheThroughputAgreement_When_StatesAreMistaken()
    {
        var result = Compare(
            [
                Seen(At(18, 0), ObservedDeskState.Idle), Seen(At(18, 1), ObservedDeskState.Paused), Seen(At(18, 2), ObservedDeskState.Closed),
                Seen(At(18, 3), ObservedDeskState.Serving), Seen(At(18, 4), ObservedDeskState.Serving), Seen(At(18, 5), ObservedDeskState.Idle)
            ],
            [
                Stored(At(18, 0), DeskStatus.Serving), Stored(At(18, 1), DeskStatus.Closed), Stored(At(18, 2), DeskStatus.Unknown),
                Stored(At(18, 3), DeskStatus.Serving), Stored(At(18, 4), DeskStatus.Paused), Stored(At(18, 5), DeskStatus.Idle)
            ]);

        var desk = DeskOf(result, D01);
        desk.Check.Value.Should().BeApproximately(2.0 / 6, 1e-12);
        // Idle against Serving and Paused against Closed agree on throughput; Closed against Unknown does not (Unknown disagrees);
        // Serving against Paused does not.
        desk.ThroughputAgreement.Should().BeApproximately(4.0 / 6, 1e-12);
        desk.Confusion.Where(c => c.Minutes > 0).Select(c => (c.Observed, c.System, c.Minutes)).Should().Equal(
            (ObservedDeskState.Closed, DeskStatus.Unknown, 1), (ObservedDeskState.Idle, DeskStatus.Idle, 1), (ObservedDeskState.Idle, DeskStatus.Serving, 1),
            (ObservedDeskState.Serving, DeskStatus.Serving, 1), (ObservedDeskState.Serving, DeskStatus.Paused, 1), (ObservedDeskState.Paused, DeskStatus.Closed, 1));
        desk.Confusion.Select(c => (c.Observed, c.System)).Should().Equal(
            Enum.GetValues<ObservedDeskState>().SelectMany(o => Enum.GetValues<DeskStatus>().Select(s => (o, s))), "every pair, in enum order");
    }

    [Fact]
    public void Compare_Should_SummariseEveryDeskAndPoolTheOverall_When_SeveralDesksWereObserved()
    {
        var result = Compare(
            [
                Seen(At(18, 0), ObservedDeskState.Serving), Seen(At(18, 1), ObservedDeskState.Serving), Seen(At(18, 0), ObservedDeskState.Idle, D02),
                Seen(At(18, 1), ObservedDeskState.Closed, D02), Seen(At(18, 2), ObservedDeskState.Closed, D02)
            ],
            [
                Stored(At(18, 0), DeskStatus.Serving), Stored(At(18, 1), DeskStatus.Serving), Stored(At(18, 0), DeskStatus.Idle, "D02"),
                Stored(At(18, 1), DeskStatus.Idle, "D02"), Stored(At(18, 2), DeskStatus.Closed, "D02")
            ]);

        result.Desks.Select(d => (d.CheckpointCode, d.DeskCode, d.Judged, d.Agreeing)).Should().Equal(("EMI", "D03", 0, 0), ("IMM", "D01", 2, 2), ("IMM", "D02", 3, 2));
        result.DeskOverall.Check.Value.Should().BeApproximately(0.8, 1e-12, "pooled over the minutes, never an average of the desks' shares");
        result.DeskOverall.Check.Verdict.Should().Be(CriterionVerdict.Fail);
        result.DeskMinutes.Select(m => (m.DeskCode, m.MinuteUtc)).Should().Equal(("D01", At(18, 0)), ("D01", At(18, 1)), ("D02", At(18, 0)), ("D02", At(18, 1)),
            ("D02", At(18, 2)));
    }

    [Fact]
    public void Compare_Should_UseTheCampaignTarget_When_TheSettingsGiveOne()
    {
        var input = new ComparisonInput
        {
            Scope = Scope(),
            DeskObservations = [Seen(At(18, 0), ObservedDeskState.Serving), Seen(At(18, 1), ObservedDeskState.Serving)],
            DeskMinutes = [Stored(At(18, 0), DeskStatus.Serving), Stored(At(18, 1), DeskStatus.Idle)]
        };

        Compare(input, new ComparisonSettings { DeskAgreementTarget = 0.5 }).DeskOverall.Check.Should().Be(new CriterionCheck(0.5, 0.5, CriterionVerdict.Pass));
        Compare(input).DeskOverall.Check.Should().Be(new CriterionCheck(0.5, 0.95, CriterionVerdict.Fail));
    }

    #endregion

    #region Observers and corrections

    [Fact]
    public void Compare_Should_JudgeOneObservedMinute_When_TwoObserversAgree()
    {
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle), Seen(At(18, 0), ObservedDeskState.Idle, observer: O2)], [Stored(At(18, 0), DeskStatus.Idle)]);

        var minute = result.DeskMinutes.Should().ContainSingle().Subject;
        (minute.Observers, minute.ObservedState, minute.ObserversDisagree, minute.Agrees).Should().Be((2, (ObservedDeskState?)ObservedDeskState.Idle, false, (bool?)true));
        DeskOf(result, D01).Judged.Should().Be(1, "two observers of one desk and minute are one observed minute");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Compare_Should_SetTheMinuteApartUnjudged_When_ItsObserversDisagree(int observers)
    {
        var third = observers == 3 ? new[] { Seen(At(18, 0), ObservedDeskState.Idle, observer: O3) } : [];
        DeskObservationRow[] observations =
            [Seen(At(18, 0), ObservedDeskState.Idle), Seen(At(18, 0), ObservedDeskState.Serving, observer: O2), .. third, Seen(At(18, 1), ObservedDeskState.Serving)];

        var result = Compare(observations, [Stored(At(18, 0), DeskStatus.Idle), Stored(At(18, 1), DeskStatus.Serving)]);

        var minute = result.DeskMinutes[0];
        (minute.Observers, minute.ObservedState, minute.ObserversDisagree, minute.Agrees, minute.SystemState).Should()
            .Be((observers, (ObservedDeskState?)null, true, (bool?)null, DeskStatus.Idle), "the ground truth itself disagrees");
        var desk = DeskOf(result, D01);
        (desk.Minutes, desk.Judged, desk.Agreeing, desk.ObserversDisagree, desk.UnusableObservations, desk.Excluded).Should().Be((2, 1, 1, 1, 0, 1));
        desk.Check.Should().Be(new CriterionCheck(1.0, 0.95, CriterionVerdict.Pass));
        result.LeftOut.Should().Be(LeftOutInputs.None, "a disagreement of observers is reported, not left out");
    }

    [Fact]
    public void Compare_Should_ExposeTheExclusionLeverAndTheStrictAgreement_When_ASecondObserverContradictsTheWrongMinutes()
    {
        // Security review of ARV-104f (Medium, probe D13 and D13b): O1 logs Serving for 20 minutes and the stored states match 16
        // (0.80, a fail). O2 logging Closed on just the 4 wrong minutes makes them observers' disagreements, excluded: 16 of 16
        // pass. The engine keeps the Proposed rule and exposes the strict agreement (excluded minutes as disagreements, 0.80) and
        // every count the campaign's verdict (ARV-104g) needs to count them against the system or cap their share.
        var observations = Enumerable.Range(0, 20).Select(m => Seen(At(18, m), ObservedDeskState.Serving)).ToList();
        var minutes = Enumerable.Range(0, 20).Select(m => Stored(At(18, m), m < 4 ? DeskStatus.Idle : DeskStatus.Serving)).ToList();

        var alone = DeskOf(Compare(observations, minutes), D01);
        var contradicted = DeskOf(Compare([.. observations, .. Enumerable.Range(0, 4).Select(m => Seen(At(18, m), ObservedDeskState.Closed, observer: O2))], minutes), D01);

        (alone.Minutes, alone.Judged, alone.Agreeing, alone.Excluded).Should().Be((20, 20, 16, 0));
        alone.Check.Should().Be(new CriterionCheck(0.8, 0.95, CriterionVerdict.Fail));
        alone.StrictAgreement.Should().Be(0.8);
        (contradicted.Minutes, contradicted.Judged, contradicted.Agreeing, contradicted.ObserversDisagree, contradicted.UnusableObservations, contradicted.Excluded)
            .Should().Be((20, 16, 16, 4, 0, 4));
        contradicted.Check.Should().Be(new CriterionCheck(1.0, 0.95, CriterionVerdict.Pass), "the lever, documented for ARV-104g's campaign verdict");
        contradicted.StrictAgreement.Should().Be(0.8, "counted against the system, the excluded minutes give back the truth");
    }

    [Fact]
    public void Compare_Should_CountUnusableObservationsInTheStrictAgreement_When_AStateCannotBeUsed()
    {
        // An observer's unusable latest state excludes its minute too (and is listed): the strict agreement counts it against
        // the system, over the desk and over every desk.
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle), Seen(At(18, 1), ObservedDeskState.Idle), Seen(At(18, 1), (ObservedDeskState)9, revision: 2)],
            [Stored(At(18, 0), DeskStatus.Idle), Stored(At(18, 1), DeskStatus.Serving)]);

        foreach (var summary in new[] { DeskOf(result, D01), result.DeskOverall })
        {
            (summary.Judged, summary.Agreeing, summary.UnusableObservations, summary.Excluded).Should().Be((1, 1, 1, 1));
            summary.Check.Value.Should().Be(1.0);
            summary.StrictAgreement.Should().Be(0.5);
        }

        DeskOf(result, D02).StrictAgreement.Should().BeNull("no minute observed: no data");
    }

    [Fact]
    public void Compare_Should_TakeEachObserversLatestRevision_When_StatesWereCorrected()
    {
        // O1 corrected Idle to Serving; O2 corrected Serving to Idle and back to Serving.
        var result = Compare(
            [
                Seen(At(18, 0), ObservedDeskState.Idle), Seen(At(18, 0), ObservedDeskState.Serving, revision: 2), Seen(At(18, 0), ObservedDeskState.Serving, observer: O2),
                Seen(At(18, 0), ObservedDeskState.Idle, observer: O2, revision: 2), Seen(At(18, 0), ObservedDeskState.Serving, observer: O2, revision: 3)
            ],
            [Stored(At(18, 0), DeskStatus.Serving)]);

        var minute = result.DeskMinutes.Should().ContainSingle().Subject;
        (minute.Observers, minute.ObservedState, minute.Agrees).Should().Be((2, (ObservedDeskState?)ObservedDeskState.Serving, (bool?)true));
        result.LeftOut.Should().Be(LeftOutInputs.None, "superseded revisions are corrections, not rows left out");
    }

    public static TheoryData<string, DeskObservationRow[], UnusableKeyReason, int> UnusableLatestStates() => new()
    {
        {
            "a correction held by two rows that disagree", [Seen(At(18, 0), ObservedDeskState.Serving, revision: 2), Seen(At(18, 0), ObservedDeskState.Closed, revision: 2)],
            UnusableKeyReason.Conflicting, 2
        },
        { "a correction to a state that is not one", [Seen(At(18, 0), (ObservedDeskState)9, revision: 2)], UnusableKeyReason.Refused, 1 },
        { "a correction beyond the 100th revision", [Seen(At(18, 0), ObservedDeskState.Serving, revision: 101)], UnusableKeyReason.Refused, 1 },
        { "a correction at the same instant not in UTC", [Seen(Unspecified(At(18, 0)), ObservedDeskState.Serving, revision: 2)], UnusableKeyReason.Refused, 1 },
        {
            "a correction with a twin not in UTC", [Seen(At(18, 0), ObservedDeskState.Serving, revision: 2), Seen(Unspecified(At(18, 0)), ObservedDeskState.Serving, revision: 2)],
            UnusableKeyReason.Refused, 2
        }
    };

    [Theory]
    [MemberData(nameof(UnusableLatestStates))]
    public void Compare_Should_NotJudgeTheDeskMinute_When_AnObserversLatestStateIsUnusable(string because, DeskObservationRow[] corrections, UnusableKeyReason reason,
        int rows)
    {
        // O1's revision 1 (Idle) agrees with the stored Idle, and so does O2: neither may stand in for O1's unusable correction.
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle), .. corrections, Seen(At(18, 0), ObservedDeskState.Idle, observer: O2), Seen(At(18, 1), ObservedDeskState.Idle)],
            [Stored(At(18, 0), DeskStatus.Idle), Stored(At(18, 1), DeskStatus.Idle)]);

        result.DeskMinutes.Select(m => m.MinuteUtc).Should().Equal([At(18, 1)], because);
        var desk = DeskOf(result, D01);
        (desk.Judged, desk.UnusableObservations, desk.Excluded).Should().Be((1, 1, 1), because);
        result.DeskOverall.UnusableObservations.Should().Be(1);
        result.LeftOut.DeskObservations.Should().Be(rows + 1, "the correction's rows and O2's state of the minute, left out with it");
        result.LeftOut.UnusableKeys.Should().Equal(new UnusableKey(UnusableKeyKind.DeskObservation, null, null, At(18, 0), O1, reason, D01));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Compare_Should_JudgeOnlyRevisionsFrom1To100_When_AStateHasOneRevision(int revision, bool judged)
    {
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle, revision: revision)], [Stored(At(18, 0), DeskStatus.Idle)]);

        result.DeskMinutes.Should().HaveCount(judged ? 1 : 0);
        result.LeftOut.UnusableKeys.Should().HaveCount(judged ? 0 : 1, "a revision outside 1 to 100 is refused at its key, not dropped");
    }

    [Fact]
    public void LeftOutInputs_Should_CountAndCompareTheNewKinds_When_RowsOfThemAreLeftOut()
    {
        var leftOut = LeftOutInputs.None with { DeskObservations = 1, DeskMinutes = 2, ShadowMinutes = 4 };

        leftOut.Total.Should().Be(7);
        (LeftOutInputs.None with { DeskObservations = 1 }).Should().NotBe(LeftOutInputs.None);
        (LeftOutInputs.None with { DeskMinutes = 1 }).Should().NotBe(LeftOutInputs.None);
        (LeftOutInputs.None with { ShadowMinutes = 1 }).Should().NotBe(LeftOutInputs.None);
        leftOut.Should().Be(LeftOutInputs.None with { DeskObservations = 1, DeskMinutes = 2, ShadowMinutes = 4 });
        leftOut.GetHashCode().Should().Be((LeftOutInputs.None with { DeskObservations = 1, DeskMinutes = 2, ShadowMinutes = 4 }).GetHashCode());
    }

    [Fact]
    public void Compare_Should_NotJudgeTheDeskMinute_When_ARowOfItHasNoObserver()
    {
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle), Seen(At(18, 0), ObservedDeskState.Serving, observer: Guid.Empty)], [Stored(At(18, 0), DeskStatus.Idle)]);

        result.DeskMinutes.Should().BeEmpty("a row naming the desk minute without an observer must not leave the other to stand alone");
        result.LeftOut.DeskObservations.Should().Be(2);
        result.LeftOut.UnusableKeys.Should().Equal(new UnusableKey(UnusableKeyKind.DeskObservation, null, null, At(18, 0), Guid.Empty, UnusableKeyReason.Refused, D01));
    }

    [Fact]
    public void Compare_Should_UseTheLatestRevision_When_AnEarlierOneIsUnusable()
    {
        var result = Compare(
            [Seen(At(18, 0), (ObservedDeskState)9), Seen(At(18, 0), ObservedDeskState.Closed, revision: 2), Seen(At(18, 0), ObservedDeskState.Closed, revision: 2)],
            [Stored(At(18, 0), DeskStatus.Closed)]);

        result.DeskMinutes.Should().ContainSingle().Which.Agrees.Should().BeTrue();
        result.LeftOut.DeskObservations.Should().Be(2, "the refused superseded revision and the copy of the latest");
        result.LeftOut.UnusableKeys.Should().BeEmpty();
    }

    #endregion

    #region Rows left out

    public static TheoryData<string, DeskObservationRow> ObservationsOutOfPlace() => new()
    {
        { "a desk out of scope", Seen(At(18, 0), ObservedDeskState.Idle, G(99)) },
        { "a minute not whole", Seen(At(18, 0, 30), ObservedDeskState.Idle) },
        { "a minute between the planned days", Seen(At(18, 45), ObservedDeskState.Idle) },
        { "a minute after the planned days", Seen(At(19, 30), ObservedDeskState.Idle) },
        { "a minute before 2000", Seen(new DateTime(1999, 12, 31, 23, 59, 0, DateTimeKind.Utc), ObservedDeskState.Idle) },
        { "a null row", null }
    };

    [Theory]
    [MemberData(nameof(ObservationsOutOfPlace))]
    public void Compare_Should_LeaveOutAndCountAnObservation_When_ItCannotBePlaced(string because, DeskObservationRow row)
    {
        var result = Compare([row, Seen(At(18, 29), ObservedDeskState.Idle)], [Stored(At(18, 29), DeskStatus.Idle)],
            Scope(new UtcWindow(At(18, 0), At(18, 30)), new UtcWindow(At(19, 0), At(19, 30))));

        result.DeskMinutes.Select(m => m.MinuteUtc).Should().Equal([At(18, 29)], because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { DeskObservations = 1 }, because);
    }

    public static TheoryData<string, DeskMinuteRow> DeskMinutesOutOfPlace() => new()
    {
        { "a desk out of scope", Stored(At(18, 0), DeskStatus.Idle, "D99") },
        { "a checkpoint out of scope", Stored(At(18, 0), DeskStatus.Idle, checkpoint: "EMI") },
        { "codes in another case", Stored(At(18, 0), DeskStatus.Idle, "d01") },
        { "no desk code", Stored(At(18, 0), DeskStatus.Idle, null) },
        { "a minute not whole", Stored(At(18, 0, 1), DeskStatus.Idle) },
        { "a minute in 3000", Stored(new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc), DeskStatus.Idle) },
        { "a null row", null }
    };

    [Theory]
    [MemberData(nameof(DeskMinutesOutOfPlace))]
    public void Compare_Should_LeaveOutAndCountADeskMinute_When_ItCannotBePlaced(string because, DeskMinuteRow row)
    {
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle)], [row]);

        result.DeskMinutes.Should().ContainSingle().Which.SystemStored.Should().BeFalse(because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { DeskMinutes = 1 }, because);
    }

    [Fact]
    public void Compare_Should_KeepOneCopy_When_StoredMinutesAreEqualInEveryValue()
    {
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle)], [Stored(At(18, 0), DeskStatus.Idle), Stored(At(18, 0), DeskStatus.Idle)]);

        result.DeskMinutes.Should().ContainSingle().Which.Agrees.Should().BeTrue();
        result.LeftOut.Should().Be(LeftOutInputs.None with { DeskMinutes = 1 });
    }

    [Fact]
    public void Compare_Should_ReadStoredMinutesOutsideThePlannedDays_When_NoneIsObservedThere()
    {
        var result = Compare([Seen(At(18, 0), ObservedDeskState.Idle)], [Stored(At(18, 0), DeskStatus.Idle), Stored(At(20, 0), DeskStatus.Idle)]);

        result.LeftOut.Should().Be(LeftOutInputs.None, "a stored minute is not ground truth: outside the planned days it is simply not compared");
    }

    #endregion

    #region Scope, mapping, version and order

    public static TheoryData<string, ScopeDesk[], ComparisonProblem> InvalidDesks() => new()
    {
        { "a desk twice by id", [new ScopeDesk(D01, "IMM", "D01"), new ScopeDesk(D01, "IMM", "D02")], ComparisonProblem.InvalidScope },
        { "a desk twice by codes", [new ScopeDesk(D01, "IMM", "D01"), new ScopeDesk(D02, "IMM", "D01")], ComparisonProblem.InvalidScope },
        { "an empty id", [new ScopeDesk(Guid.Empty, "IMM", "D01")], ComparisonProblem.InvalidScope },
        { "no checkpoint code", [new ScopeDesk(D01, "", "D01")], ComparisonProblem.InvalidScope },
        { "no desk code", [new ScopeDesk(D01, "IMM", null)], ComparisonProblem.InvalidScope },
        { "a null desk", [null], ComparisonProblem.InvalidScope },
        { "101 desks", [.. Enumerable.Range(1, 101).Select(n => new ScopeDesk(G(5000 + n), "IMM", $"D{n:D3}"))], ComparisonProblem.InputTooLarge }
    };

    [Theory]
    [MemberData(nameof(InvalidDesks))]
    public void Compare_Should_CompareNothing_When_TheScopesDesksAreInvalid(string because, ScopeDesk[] desks, ComparisonProblem problem)
    {
        var result = Compare(new ComparisonInput { Scope = Scope() with { Desks = desks }, DeskObservations = [Seen(At(18, 0), ObservedDeskState.Idle)] });

        result.Problem.Should().Be(problem, because);
        result.DeskMinutes.Should().BeEmpty(because);
        result.Desks.Should().BeEmpty(because);
        result.DeskOverall.Should().BeNull(because);
        result.NowcastMinutes.Should().BeEmpty(because);
        result.NowcastZones.Should().BeEmpty(because);
        result.NowcastOverall.Should().BeNull(because);
    }

    [Fact]
    public void Compare_Should_CompareAtTheBound_When_TheScopeHas100Desks()
    {
        var desks = Enumerable.Range(1, 100).Select(n => new ScopeDesk(G(5000 + n), "IMM", $"D{n:D3}")).ToList();

        var result = Compare(new ComparisonInput { Scope = Scope() with { Desks = desks } });

        result.Problem.Should().BeNull();
        result.Desks.Should().HaveCount(100);
    }

    [Fact]
    public void Compare_Should_CompareNothing_When_AListOfDeskOrShadowRowsExceedsItsBound()
    {
        // The engine's bound of rows of a kind (1,000,000), counted as read.
        const int MaxRows = 1_000_000;
        var rows = Enumerable.Range(0, MaxRows + 1).Select(_ => Seen(At(18, 0), ObservedDeskState.Idle));

        Compare(new ComparisonInput { Scope = Scope(), DeskObservations = [.. rows] }).Problem.Should().Be(ComparisonProblem.InputTooLarge);
        Compare(new ComparisonInput { Scope = Scope(), DeskMinutes = [.. Enumerable.Repeat<DeskMinuteRow>(null, MaxRows + 1)] }).Problem.Should()
            .Be(ComparisonProblem.InputTooLarge);
        Compare(new ComparisonInput { Scope = Scope(), ShadowMinutes = [.. Enumerable.Repeat<ShadowMinuteRow>(null, MaxRows + 1)] }).Problem.Should()
            .Be(ComparisonProblem.InputTooLarge);
        var atTheBound = Compare(new ComparisonInput { Scope = Scope(), ShadowMinutes = [.. Enumerable.Repeat<ShadowMinuteRow>(null, MaxRows)] });
        atTheBound.Problem.Should().BeNull();
        atTheBound.LeftOut.ShadowMinutes.Should().Be(MaxRows);
    }

    [Fact]
    public void Compare_Should_CarryTheProfileVersionAndGiveTheSameResult_When_TheInputsComeInAnotherOrder()
    {
        var input = new ComparisonInput
        {
            Scope = Scope(),
            DeskObservations =
            [
                Seen(At(18, 0), ObservedDeskState.Idle), Seen(At(18, 0), ObservedDeskState.Serving, observer: O2), Seen(At(18, 1), ObservedDeskState.Idle, D02),
                Seen(At(18, 2), ObservedDeskState.Closed, D03), Seen(At(18, 2), ObservedDeskState.Paused, D03, revision: 2), Seen(At(18, 3), (ObservedDeskState)7), Seen(At(18, 3), (ObservedDeskState)7, D02)
            ],
            DeskMinutes =
            [
                Stored(At(18, 0), DeskStatus.Idle), Stored(At(18, 1), DeskStatus.Serving, "D02"), Stored(At(18, 2), DeskStatus.Paused, "D03", checkpoint: "EMI"),
                Stored(At(18, 3), DeskStatus.Idle), Stored(At(18, 3), DeskStatus.Closed)
            ]
        };
        var reversed = input with
        {
            DeskObservations = [.. input.DeskObservations.Reverse()],
            DeskMinutes = [.. input.DeskMinutes.Reverse()],
            Scope = input.Scope with { Desks = [.. input.Scope.Desks.Reverse()] }
        };

        var expected = Compare(input);
        expected.DeskMinutes.Should().HaveCount(3).And.AllSatisfy(m => m.ProfileVersion.Should().Be(Version));
        expected.Desks.Should().HaveCount(3).And.AllSatisfy(d => d.ProfileVersion.Should().Be(Version));
        expected.DeskOverall.ProfileVersion.Should().Be(Version);
        expected.LeftOut.UnusableKeys.Select(k => (k.Kind, k.DeskId)).Should().Equal((UnusableKeyKind.DeskObservation, D01), (UnusableKeyKind.DeskObservation, D02),
            (UnusableKeyKind.DeskMinute, D01));
        Compare(reversed).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering().ComparingRecordsByMembers());
    }

    [Fact]
    public void Of_Should_MapTheCampaignsDesksAndTheirObservations_When_TheyWereCaptured()
    {
        var now = new DateTime(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
        var dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
        var level = new Airport("DMO", null, "Demo", "Asia/Dubai").AddTerminal("T1", "T1", "DMO").AddLevel("L0", "Arrivals", 0, 100, 60);
        level.Id = Guid.CreateVersion7();
        var profile = new ZoneProfile("DMO", "Arrivals");
        var snake = profile.AddZone("Snake A", ZoneKind.Queue, level, [new(10, 10), new(34, 10), new(34, 22), new(10, 22)]);
        var entry = profile.AddLine("Entry A", LineRole.Entry, level, new FloorPoint(10, 12), new FloorPoint(10, 16), snake);
        profile.AddLine("Exit A", LineRole.Exit, level, new FloorPoint(30, 22), new FloorPoint(34, 22), snake);
        profile.Id = Guid.CreateVersion7();
        foreach (var z in profile.Zones)
            z.Id = Guid.CreateVersion7();
        foreach (var l in profile.Lines)
            l.Id = Guid.CreateVersion7();
        profile.Publish(Version, new Dictionary<Guid, Level> { [level.Id!.Value] = level }, "admin", now.AddDays(-10)).Should().BeEmpty();
        var immigration = level.AddCheckpoint("IMM", "Immigration", CheckpointKind.Immigration);
        var d01 = immigration.AddDesk("D01", null, DeskKind.Desk, ["CIT"]);
        d01.Id = Guid.CreateVersion7();
        var manager = Guid.CreateVersion7();
        var campaign = new ValidationCampaign("Pilot", profile, [snake.Id!.Value], [entry.Id!.Value], [new DateOnly(2026, 10, 8)], new DateOnly(2026, 10, 8),
            null, null, manager, now, [d01]);
        campaign.Id = Guid.CreateVersion7();
        campaign.Start(manager, now, ZoneProfileStatus.Published);
        var states = new ObservedDeskState?[DeskObservationBatch.MinutesPerBin];
        states[0] = ObservedDeskState.Serving;
        var (_, observations) = campaign.ObserveDesks(new DateTime(2026, 10, 8, 5, 30, 0, DateTimeKind.Utc), [new DeskMinutesInput(d01.Id!.Value, states)], O1, now, dubai,
            null, "tablet-07:batch-0001");
        var first = observations.Single();
        first.Id = Guid.CreateVersion7();
        var corrected = campaign.CorrectDeskObservation(first, O1, ObservedDeskState.Idle, "Tapped the wrong row", now.AddMinutes(1));

        var scope = ComparisonScope.Of(campaign, dubai);
        scope.Desks.Should().Equal(new ScopeDesk(d01.Id!.Value, "IMM", "D01"));
        DeskObservationRow.Of(first).Should().Be(new DeskObservationRow(d01.Id!.Value, new DateTime(2026, 10, 8, 5, 30, 0, DateTimeKind.Utc), O1, 1, ObservedDeskState.Serving));
        DeskObservationRow.Of(corrected).Should().Be(new DeskObservationRow(d01.Id!.Value, new DateTime(2026, 10, 8, 5, 30, 0, DateTimeKind.Utc), O1, 2, ObservedDeskState.Idle));
        FluentActions.Invoking(() => DeskObservationRow.Of(null)).Should().Throw<ArgumentNullException>();

        // The mapped revisions compare as any others: the correction decides.
        var result = Compare(new ComparisonInput
        {
            Scope = scope,
            DeskObservations = [DeskObservationRow.Of(first), DeskObservationRow.Of(corrected)],
            DeskMinutes = [new DeskMinuteRow("IMM", "D01", new DateTime(2026, 10, 8, 5, 30, 0, DateTimeKind.Utc), 0, 60, 0, 0, 0, false)]
        });
        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.DeskMinutes.Should().ContainSingle().Which.Agrees.Should().BeTrue();
    }

    #endregion
}
