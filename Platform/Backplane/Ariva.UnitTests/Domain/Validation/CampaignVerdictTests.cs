using Ariva.Core.Availability;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Validation;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104g2: the campaign verdicts (formulas F18 "Campaign verdicts"; Proposed rules in docs/product/decisions.md). The engine's
/// per-line, per-zone and per-desk verdicts judge only the items they hold; a campaign verdict needs the campaign's target counts
/// of Good judged items (otherwise no data), shows the excluded share, closes the exclusion levers of the ARV-104f review
/// (strict by default, or a cap) and flags a nonzero LeftOut.QualityIntervals for review. The results projection keeps desk
/// results, observer-level results and the shadow's figures in sections of their own.
/// </summary>
public sealed class CampaignVerdictTests
{
    #region Setup

    private const int Version = 7;
    private const string Vis = "A-VIS";
    private static readonly Guid ZoneA = G(1);
    private static readonly Guid D01 = G(31);
    private static readonly Guid O1 = G(901);
    private static readonly Guid O2 = G(902);
    private static readonly ComparisonSettings Settings = new();

    private static Guid G(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    private static DateTime At(int hour, int minute) => new(2026, 10, 8, hour, minute, 0, DateTimeKind.Utc);

    private static ComparisonScope Scope(bool desks = true) =>
        new(Version, [new ScopeZone(ZoneA, Vis)], [], [new UtcWindow(At(18, 0), At(19, 0))]) { Desks = desks ? [new ScopeDesk(D01, "IMM", "D01")] : [] };

    /// <summary>A comparison of nothing (every list empty), to set the parts a rule reads with <c>with</c>.</summary>
    private static ComparisonResult Empty() => ValidationComparison.Compare(new ComparisonInput { Scope = Scope() });

    private static CriterionCheck Check(double? value, double target, CriterionVerdict verdict) => new(value, target, verdict);

    private static LineAccuracy Line(string name, int judgedBins, int excludedBins, double? lowest, CriterionVerdict verdict) =>
        new(Version, Vis, G(100 + name.Length), name, LineRole.Entry, judgedBins, judgedBins, judgedBins, excludedBins, lowest, lowest, 0, 0, StandingTally.None,
            Check(lowest, 0.95, verdict));

    private static TracerZoneSummary Tracers(int compared, int excluded, int within, double? bias, CriterionVerdict error, CriterionVerdict biasVerdict) =>
        new(Version, null, compared + excluded, compared, excluded, within, bias, null, null, StandingTally.None, 0, 0, 0, 0, 0, null,
            Check(compared > 0 ? within / (double)compared : null, 1.0, error), Check(bias, 0.05, biasVerdict));

    private static ZoneTrackCompletion Track(string zone, int goodBins, long entered, double? rate, int excluded, CriterionVerdict verdict, long otherEntered = 0) =>
        new(Version, zone, StandingTally.None, excluded, new TrackTotals(goodBins, entered, 0, 0, 0, 0, rate), new TrackTotals(excluded, otherEntered, 0, 0, 0, 0, null),
            TrackTotals.None, Check(rate, 0.90, verdict));

    private static DeskObservationRow Seen(int minute, ObservedDeskState state, Guid? observer = null) => new(D01, At(18, minute), observer ?? O1, 1, state);

    private static DeskMinuteRow Stored(int minute, DeskStatus state) =>
        new("IMM", "D01", At(18, minute), state == DeskStatus.Closed ? 60 : 0, state == DeskStatus.Idle ? 60 : 0, state == DeskStatus.Serving ? 60 : 0,
            state == DeskStatus.Paused ? 60 : 0, 0, false);

    private static QueueBinRow Bin(DateTime start) => new(Vis, start, TimeSpan.FromMinutes(15), 1, BinStatus.Final, BinQuality.Good, Version, 100, 0);

    private static QueueMinuteRow Row(int minute, double? nowcast, double realised, string noService = null, bool live = true) =>
        new(Vis, At(18, 0).AddMinutes(minute), Version, BinStatus.Final, 10, realised, live ? nowcast : null, live ? noService : null, live ? false : null);

    /// <summary>
    /// <paramref name="judged"/> minutes whose published nowcast was exactly right (error 0), then <paramref name="noService"/>
    /// Good minutes whose nowcast said NothingOpen while the next minute's entrants waited 10 minutes (under the 20-minute cut).
    /// </summary>
    private static ComparisonResult Nowcasts(int judged, int noService)
    {
        var rows = Enumerable.Range(0, judged).Select(m => Row(m, 10, 10))
            .Concat(Enumerable.Range(judged, noService).Select(m => Row(m, null, 10, "NothingOpen")))
            .Append(Row(judged + noService, null, 10, live: false)).ToList();
        return ValidationComparison.Compare(new ComparisonInput
        {
            Scope = Scope(),
            QueueMinutes = rows,
            QueueBins = [.. Enumerable.Range(0, 8).Select(i => Bin(At(18, 0).AddMinutes(15 * i)))]
        });
    }

    private static readonly CampaignVerdictSettings Strict = new();
    private static readonly CampaignVerdictSettings Capped = new() { Exclusions = ExclusionRule.Capped, MaxExcludedShare = 0.05 };

    #endregion

    #region Count accuracy

    [Fact]
    public void CountAccuracy_Should_PassOnlyWhenEveryLineHasItsTargetOfJudgedBins_When_EveryLinePasses()
    {
        var enough = Empty() with { Lines = [Line("Entry A", 20, 3, 0.97, CriterionVerdict.Pass), Line("Entry BB", 25, 0, 0.96, CriterionVerdict.Pass)] };
        var short1 = Empty() with { Lines = [Line("Entry A", 20, 3, 0.97, CriterionVerdict.Pass), Line("Entry BB", 19, 0, 0.99, CriterionVerdict.Pass)] };

        var pass = CampaignVerdicts.CountAccuracy(enough, new CampaignTargets(20, 30, false), Settings);
        var noData = CampaignVerdicts.CountAccuracy(short1, new CampaignTargets(20, 30, false), Settings);

        (pass.Verdict, pass.Reason, pass.Value, pass.Judged, pass.Required, pass.Excluded).Should().Be((CriterionVerdict.Pass, CampaignVerdictReason.Met, 0.96, 45, 40, 3));
        pass.ExcludedShare.Should().BeApproximately(3 / 48.0, 1e-12, "the excluded share is always shown");
        pass.Units.Select(u => (u.LineName, u.Verdict)).Should().Equal(("Entry A", CriterionVerdict.Pass), ("Entry BB", CriterionVerdict.Pass));
        (noData.Verdict, noData.Reason).Should().Be((CriterionVerdict.NoData, CampaignVerdictReason.TooFewJudged), "19 judged bins is below the target of 20");
        noData.Units.Single(u => u.LineName == "Entry BB").Should().Match<CampaignCriterionUnit>(u => u.JudgedVerdict == CriterionVerdict.Pass && u.Verdict == CriterionVerdict.NoData);
    }

    [Theory]
    [InlineData(20, CriterionVerdict.Fail, CampaignVerdictReason.NotMet)]
    [InlineData(19, CriterionVerdict.NoData, CampaignVerdictReason.TooFewJudged)]
    public void CountAccuracy_Should_FailOnlyOnALineWithItsTargetOfJudgedBins_When_ALineFails(int judgedBins, CriterionVerdict verdict, CampaignVerdictReason reason)
    {
        // A line that fails on fewer judged bins than the target is no data (Proposed): the line's own fail stays visible in its unit.
        var result = Empty() with { Lines = [Line("Entry A", 20, 0, 0.97, CriterionVerdict.Pass), Line("Entry BB", judgedBins, 0, 0.90, CriterionVerdict.Fail)] };

        var campaign = CampaignVerdicts.CountAccuracy(result, new CampaignTargets(20, 30, false), Settings);

        (campaign.Verdict, campaign.Reason, campaign.Value).Should().Be((verdict, reason, 0.90));
        campaign.Units.Single(u => u.LineName == "Entry BB").JudgedVerdict.Should().Be(CriterionVerdict.Fail);
    }

    [Fact]
    public void CountAccuracy_Should_GiveNoData_When_TheCampaignHasNoLineOrNothingWasCompared()
    {
        CampaignVerdicts.CountAccuracy(Empty(), new CampaignTargets(1, 0, false), Settings).Should()
            .Match<CampaignCriterionVerdict>(v => v.Verdict == CriterionVerdict.NoData && v.Reason == CampaignVerdictReason.TooFewJudged && v.Value == null);
        var refused = ValidationComparison.Compare(new ComparisonInput { Scope = Scope() with { Windows = null } });
        CampaignVerdicts.CountAccuracy(refused, new CampaignTargets(1, 0, false), Settings).Reason.Should().Be(CampaignVerdictReason.NotCompared);
    }

    #endregion

    #region Tracers

    [Theory]
    [InlineData(30, 30, CriterionVerdict.Pass, CriterionVerdict.Pass, CampaignVerdictReason.Met)]
    [InlineData(29, 30, CriterionVerdict.Pass, CriterionVerdict.NoData, CampaignVerdictReason.TooFewJudged)]
    [InlineData(30, 30, CriterionVerdict.Fail, CriterionVerdict.Fail, CampaignVerdictReason.NotMet)]
    [InlineData(0, 0, CriterionVerdict.NoData, CriterionVerdict.NoData, CampaignVerdictReason.TooFewJudged)]
    public void WaitErrorAndBias_Should_NeedTheTargetOfComparedTracerRuns_When_Judged(int compared, int target, CriterionVerdict engine, CriterionVerdict expected,
        CampaignVerdictReason reason)
    {
        var result = Empty() with { TracerOverall = Tracers(compared, 5, compared, 0.01, engine, engine) };
        var targets = new CampaignTargets(20, target, false);

        var error = CampaignVerdicts.WaitError(result, targets, Settings);
        var bias = CampaignVerdicts.WaitBias(result, targets, Settings);

        (error.Verdict, error.Reason, error.Judged, error.Required, error.Excluded).Should().Be((expected, reason, compared, target, 5));
        (bias.Verdict, bias.Reason).Should().Be((expected, reason));
        error.ExcludedShare.Should().BeApproximately(5.0 / (compared + 5), 1e-12);
    }

    #endregion

    #region Track completion

    [Fact]
    public void TrackCompletion_Should_PassOnlyWhenEveryZoneCountsTracksAndPasses_When_EachHasItsGoodBins()
    {
        var result = Empty() with { TrackCompletion = [Track(Vis, 20, 100, 0.95, 2, CriterionVerdict.Pass), Track("A-CIT", 25, 80, 0.92, 0, CriterionVerdict.Pass)] };

        var campaign = CampaignVerdicts.TrackCompletion(result, new CampaignTargets(20, 0, false), Settings);

        (campaign.Verdict, campaign.Reason, campaign.Value).Should().Be((CriterionVerdict.Pass, CampaignVerdictReason.Met, 0.92));
        CampaignVerdicts.TrackCompletion(Empty() with { TrackCompletion = [Track(Vis, 20, 100, 0.85, 2, CriterionVerdict.Fail)] }, new CampaignTargets(20, 0, false), Settings)
            .Verdict.Should().Be(CriterionVerdict.Fail);
    }

    [Fact]
    public void TrackCompletion_Should_FailClosed_When_AZoneInScopeCountedNoTrackAllCampaign()
    {
        // Security review of ARV-104g2 (M2): a tracking zone whose tracker produced nothing must not drop out and let the others
        // pass. A-CIT counted no track; A-DIP counted tracks only in excluded bins (no Good bin).
        var silent = Empty() with { TrackCompletion = [Track(Vis, 20, 100, 0.95, 2, CriterionVerdict.Pass), Track("A-CIT", 0, 0, null, 96, CriterionVerdict.NoData)] };
        var failing = Empty() with { TrackCompletion = [Track(Vis, 20, 100, 0.80, 2, CriterionVerdict.Fail), Track("A-CIT", 0, 0, null, 96, CriterionVerdict.NoData)] };
        var excludedOnly = Empty() with
        {
            TrackCompletion = [Track(Vis, 20, 100, 0.95, 2, CriterionVerdict.Pass), Track("A-DIP", 0, 0, null, 3, CriterionVerdict.NoData, otherEntered: 12)]
        };
        var none = Empty() with { TrackCompletion = [Track(Vis, 0, 0, null, 96, CriterionVerdict.NoData), Track("A-CIT", 0, 0, null, 96, CriterionVerdict.NoData)] };
        var targets = new CampaignTargets(20, 0, false);

        var campaign = CampaignVerdicts.TrackCompletion(silent, targets, Settings);

        (campaign.Verdict, campaign.Reason).Should().Be((CriterionVerdict.NoData, CampaignVerdictReason.ZonesWithoutTracks));
        campaign.Units.Select(u => (u.QueueZone, u.Judged, u.Verdict)).Should().Equal((Vis, 20, CriterionVerdict.Pass), ("A-CIT", 0, CriterionVerdict.NoData));
        CampaignVerdicts.TrackCompletion(failing, targets, Settings).Verdict.Should().Be(CriterionVerdict.Fail, "a failing zone still fails the criterion");
        CampaignVerdicts.TrackCompletion(excludedOnly, targets, Settings).Should()
            .Match<CampaignCriterionVerdict>(v => v.Verdict == CriterionVerdict.NoData && v.Reason == CampaignVerdictReason.TooFewJudged);
        var notInScope = CampaignVerdicts.TrackCompletion(none, targets, Settings);
        (notInScope.Verdict, notInScope.Reason).Should().Be((CriterionVerdict.NoData, CampaignVerdictReason.NotInScope));
        notInScope.Units.Select(u => u.QueueZone).Should().Equal(Vis, "A-CIT");
        CampaignVerdicts.Review(silent, targets, true, true).Should().Equal(CampaignReview.ZonesWithoutTracks);
        CampaignVerdicts.Review(none, targets, true, true).Should().Equal(CampaignReview.ZonesWithoutTracks);
    }

    #endregion

    #region Desk-state agreement (the observers' disagreement lever)

    /// <summary>
    /// The ARV-104f review's lever (Medium, probe D13): O1 logs Serving for 20 minutes and the stored states match 16; O2 logs
    /// Closed on just the 4 wrong minutes, so they become disagreements among observers and are excluded: 16 of 16 for the engine.
    /// </summary>
    private static ComparisonResult Contradicted() => ValidationComparison.Compare(new ComparisonInput
    {
        Scope = Scope(),
        DeskObservations = [.. Enumerable.Range(0, 20).Select(m => Seen(m, ObservedDeskState.Serving)), .. Enumerable.Range(0, 4).Select(m => Seen(m, ObservedDeskState.Closed, O2))],
        DeskMinutes = [.. Enumerable.Range(0, 20).Select(m => Stored(m, m < 4 ? DeskStatus.Idle : DeskStatus.Serving))]
    });

    [Fact]
    public void DeskStateAgreement_Should_CountTheExcludedMinutesAgainstTheSystem_When_TheRuleIsStrict()
    {
        var result = Contradicted();
        result.DeskOverall.Check.Verdict.Should().Be(CriterionVerdict.Pass, "the engine's figure over judged minutes, the lever");

        var campaign = CampaignVerdicts.DeskStateAgreement(result, new CampaignTargets(1, 0, false), Settings, Strict, campaignHasDesks: true);

        (campaign.Verdict, campaign.Reason, campaign.Value, campaign.JudgedValue).Should().Be((CriterionVerdict.Fail, CampaignVerdictReason.NotMet, 0.8, 1.0));
        (campaign.Judged, campaign.Required, campaign.Excluded).Should().Be((16, 15, 4));
        campaign.ExcludedShare.Should().Be(0.2);
    }

    [Theory]
    [InlineData(0.05, CriterionVerdict.Fail, CampaignVerdictReason.ExcludedShareAboveCap)]
    [InlineData(0.2, CriterionVerdict.Pass, CampaignVerdictReason.Met)]
    public void DeskStateAgreement_Should_FailAboveTheCap_When_TheRuleIsCapped(double cap, CriterionVerdict verdict, CampaignVerdictReason reason)
    {
        var campaign = CampaignVerdicts.DeskStateAgreement(Contradicted(), new CampaignTargets(1, 0, false), Settings, Capped with { MaxExcludedShare = cap }, true);

        (campaign.Verdict, campaign.Reason, campaign.Value, campaign.ExcludedShare).Should().Be((verdict, reason, 1.0, 0.2));
    }

    [Fact]
    public void DeskStateAgreement_Should_GiveNoData_When_TooFewMinutesAreJudgedOrTheCampaignHasNoDesks()
    {
        // 16 judged minutes against 2 target bins of 15 minutes (30).
        CampaignVerdicts.DeskStateAgreement(Contradicted(), new CampaignTargets(2, 0, false), Settings, Strict, true).Should()
            .Match<CampaignCriterionVerdict>(v => v.Verdict == CriterionVerdict.NoData && v.Reason == CampaignVerdictReason.TooFewJudged && v.Required == 30 && v.Value == 0.8);
        CampaignVerdicts.DeskStateAgreement(Empty(), new CampaignTargets(1, 0, false), Settings, Strict, campaignHasDesks: false).Reason
            .Should().Be(CampaignVerdictReason.NotInScope);
    }

    #endregion

    #region Nowcast error (the no-service lever)

    [Fact]
    public void NowcastError_Should_CountNoServiceMinutesUnderTheCutAsErrorsBeyondTheTarget_When_TheRuleIsStrict()
    {
        // 20 exact minutes and 30 NothingOpen minutes while people waited 10: the engine passes (median 0 over 20); counted as
        // errors beyond the target, the 30 put the median beyond it. With 40 exact minutes the median stays 0 and passes.
        var lever = Nowcasts(20, 30);
        lever.NowcastOverall.Check.Verdict.Should().Be(CriterionVerdict.Pass);
        lever.NowcastOverall.Published.NoServiceUnderCut.Should().Be(30);

        var strict = CampaignVerdicts.NowcastError(lever, new CampaignTargets(1, 0, false), Settings, Strict);
        var outweighed = CampaignVerdicts.NowcastError(Nowcasts(40, 30), new CampaignTargets(1, 0, false), Settings, Strict);

        (strict.Verdict, strict.Reason, strict.Value, strict.JudgedValue, strict.Judged, strict.Excluded).Should()
            .Be((CriterionVerdict.Fail, CampaignVerdictReason.NotMet, NowcastErrors.MaxErrorMinutes, 0.0, 20, 30));
        strict.ExcludedShare.Should().Be(0.6);
        (outweighed.Verdict, outweighed.Value).Should().Be((CriterionVerdict.Pass, 0.0));
    }

    [Fact]
    public void NowcastError_Should_FailAboveTheCapAndNeedItsTargetOfJudgedMinutes_When_TheRuleIsCapped()
    {
        CampaignVerdicts.NowcastError(Nowcasts(20, 30), new CampaignTargets(1, 0, false), Settings, Capped).Reason.Should().Be(CampaignVerdictReason.ExcludedShareAboveCap);
        CampaignVerdicts.NowcastError(Nowcasts(20, 1), new CampaignTargets(1, 0, false), Settings, Capped with { MaxExcludedShare = 0.05 })
            .Should().Match<CampaignCriterionVerdict>(v => v.Verdict == CriterionVerdict.Pass && v.Value == 0.0);
        CampaignVerdicts.NowcastError(Nowcasts(14, 0), new CampaignTargets(1, 0, false), Settings, Strict)
            .Should().Match<CampaignCriterionVerdict>(v => v.Verdict == CriterionVerdict.NoData && v.Reason == CampaignVerdictReason.TooFewJudged && v.Required == 15);
    }

    [Fact]
    public void VerdictSettings_Should_RefuseAnUnknownRuleOrACapOutsideAShare_When_Used()
    {
        new CampaignVerdictSettings { Exclusions = (ExclusionRule)9 }.Problems().Should().ContainSingle();
        new CampaignVerdictSettings { MaxExcludedShare = 1.5 }.Problems().Should().ContainSingle();
        new CampaignVerdictSettings { MaxExcludedShare = double.NaN }.Problems().Should().ContainSingle();
        var act = () => CampaignVerdicts.NowcastError(Empty(), new CampaignTargets(1, 0, false), Settings, new CampaignVerdictSettings { MaxExcludedShare = -1 });
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Availability

    [Theory]
    [InlineData(990, 1000, CriterionVerdict.Pass)]
    [InlineData(989, 1000, CriterionVerdict.Fail)]
    [InlineData(0, 0, CriterionVerdict.NoData)]
    public void Availability_Should_JudgeAvailableOverOperatingMinutes_When_TheLedgerRecordedThem(int available, int operating, CriterionVerdict verdict)
    {
        var campaign = CampaignVerdicts.Availability(AvailabilityCounts.Zero with { OperatingMinutes = operating, AvailableMinutes = available });

        (campaign.Verdict, campaign.Target, campaign.Judged, campaign.Excluded).Should().Be((verdict, 0.99, operating, operating - available));
    }

    #endregion

    #region Review

    [Fact]
    public void Review_Should_FlagLeftOutQualityIntervalsAndTheOtherReasons_When_TheyApply()
    {
        var clean = CampaignVerdicts.Review(Tracked(Empty()), new CampaignTargets(20, 30, false), campaignClosed: true, shadowRead: true);
        var intervals = Tracked(Empty()) with { LeftOut = new LeftOutInputs(0, 0, 0, 0, 0, 0, 1) };
        var flagged = CampaignVerdicts.Review(intervals, new CampaignTargets(20, 30, true), campaignClosed: false, shadowRead: false);

        clean.Should().BeEmpty();
        flagged.Should().Equal(CampaignReview.QualityIntervalsLeftOut, CampaignReview.PlaceholderTargets, CampaignReview.CampaignNotClosed, CampaignReview.ProofNotRead);
    }

    [Fact]
    public void Review_Should_LeaveDeskKeysOutOfTheFlags_When_OnlyDeskRowsAreUnusable()
    {
        // The flags outside the desk section must not tell an airport role anything about desks.
        var deskOnly = Tracked(Empty()) with
        {
            LeftOut = new LeftOutInputs(0, 0, 0, 0, 0, 0, 0)
            {
                UnusableKeys = [new UnusableKey(UnusableKeyKind.DeskMinute, null, null, At(18, 0), null, UnusableKeyReason.Refused, D01)]
            }
        };
        var minute = deskOnly with
        {
            LeftOut = deskOnly.LeftOut with { UnusableKeys = [new UnusableKey(UnusableKeyKind.QueueMinute, Vis, null, At(18, 0), null, UnusableKeyReason.Conflicting)] }
        };

        CampaignVerdicts.Review(deskOnly, new CampaignTargets(20, 30, false), true, true).Should().BeEmpty();
        CampaignVerdicts.Review(minute, new CampaignTargets(20, 30, false), true, true).Should().Equal(CampaignReview.UnusableRows);
    }

    [Fact]
    public void Review_Should_FlagPlannedMinutesWithoutAPublishedNowcast_When_TheCoverageMissesAny()
    {
        // L5 of the ARV-104g2 review: minutes after which people's waits are known but with no published nowcast at all are never
        // judged, so they are flagged.
        var result = Tracked(Empty());
        var covered = new NowcastCoverage(Vis, 60, 10, 9, 1, 0, 0.9);

        CampaignVerdicts.Review(result, new CampaignTargets(20, 30, false), true, true, [covered]).Should().BeEmpty();
        CampaignVerdicts.Review(result, new CampaignTargets(20, 30, false), true, true, [covered, covered with { QueueZone = "A-CIT", Missing = 1 }])
            .Should().Equal(CampaignReview.NowcastMissing);
    }

    /// <summary>The result with its one zone counting tracks, so that only the flag a test is about is raised.</summary>
    private static ComparisonResult Tracked(ComparisonResult result) => result with { TrackCompletion = [Track(Vis, 20, 100, 0.95, 0, CriterionVerdict.Pass)] };

    #endregion

    #region Results projection

    [Fact]
    public void From_Should_KeepDeskObserverAndShadowFiguresInTheirOwnSections_When_TheResultsAreBuilt()
    {
        var count = new UnusableKey(UnusableKeyKind.ManualCount, Vis, "Entry A", At(18, 0), O1, UnusableKeyReason.Refused);
        var desk = new UnusableKey(UnusableKeyKind.DeskObservation, null, null, At(18, 1), O2, UnusableKeyReason.Conflicting, D01);
        var deskMinute = new UnusableKey(UnusableKeyKind.DeskMinute, null, null, At(18, 2), null, UnusableKeyReason.Refused, D01);
        var result = Contradicted() with { LeftOut = new LeftOutInputs(1, 0, 0, 0, 0, 0, 0) { UnusableKeys = [count, desk, deskMinute], DeskObservations = 2, DeskMinutes = 1 } };
        var facts = new ValidationResultsViewModel.CampaignFacts(G(5000), "DMO", "Campaign", ValidationCampaignStatus.Closed, Version, new string('c', 64),
            ["2026-10-08"], new CampaignTargets(1, 0, false), HasDesks: true);

        var view = ValidationResultsViewModel.From(result, new ValidationResultsViewModel.Sources(facts, "Asia/Dubai", At(20, 0), null, null, true, [], null, []));

        view.Criteria.Select(c => c.Criterion).Should().Equal(CampaignCriterion.CountAccuracy, CampaignCriterion.WaitError, CampaignCriterion.WaitBias,
            CampaignCriterion.TrackCompletion, CampaignCriterion.NowcastError, CampaignCriterion.Availability);
        view.Desks.Verdict.Criterion.Should().Be(CampaignCriterion.DeskStateAgreement, "the desk criterion is border per-desk data");
        view.Desks.Verdict.Verdict.Should().Be(CriterionVerdict.Fail);
        view.LeftOut.Keys.Should().Equal(count with { ObserverId = null });
        view.Desks.Keys.Should().Equal(desk with { ObserverId = null }, deskMinute);
        view.Desks.ObserverKeys.Should().Equal(desk);
        view.Desks.ObservationsLeftOut.Should().Be(2);
        view.Observers.CountKeys.Should().Equal(count);
        (view.ProfileVersion, view.GeometryHash, view.Status, view.TimeZoneId).Should().Be((Version, new string('c', 64), "Closed", "Asia/Dubai"));
        view.PlannedDays.Should().Equal("2026-10-08");
        view.Review.Should().Equal(CampaignReview.UnusableRows, CampaignReview.ZonesWithoutTracks);
        view.Nowcast.ShadowRead.Should().BeTrue();
        view.Targets.Exclusions.Should().Be(ExclusionRule.Strict);
    }

    #endregion
}
