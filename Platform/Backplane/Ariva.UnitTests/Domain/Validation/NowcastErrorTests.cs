using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104f: the nowcast error of F18 and the ground-truth proof. The formula (the nowcast stored for minute m against the final
/// mean realised wait of the people who entered in minute m + 1; the median absolute error over minutes with a realised wait
/// under 20 minutes, within 2 minutes), the Proposed rules (pairing with the next minute, the cut, the nowcast's own flag
/// judged and reported, the standing over both minutes and the entrants' wait), minutes without service, Degraded, Unknown,
/// Provisional and OtherVersion minutes reported apart, the published and the shadow nowcast side by side with the minutes
/// each covers, and the guards (unusable rows Unknown and listed, never a division by zero, a median pooled over zones).
/// </summary>
public sealed class NowcastErrorTests
{
    #region Fixtures

    private const int Version = 7;
    private const string Vis = "A-VIS";
    private const string Cit = "A-CIT";

    private static readonly Guid ZoneA = G(1);
    private static readonly Guid ZoneB = G(2);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    private static DateTime At(int hour, int minute, int second = 0) => new(2026, 10, 8, hour, minute, second, DateTimeKind.Utc);

    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private static ComparisonScope Scope(params UtcWindow[] windows) =>
        new(Version, [new ScopeZone(ZoneA, Vis), new ScopeZone(ZoneB, Cit)], [], windows.Length == 0 ? [new UtcWindow(At(18, 0), At(19, 0))] : windows);

    private static QueueBinRow Bin(DateTime start, BinQuality quality = BinQuality.Good, BinStatus status = BinStatus.Final, int version = Version, string zone = Vis) =>
        new(zone, start, TimeSpan.FromMinutes(15), 1, status, quality, version, 100, 0);

    /// <summary>Final Good bins of both zones from 18:00 to 20:00, so every pair's minutes and entrants' wait are covered.</summary>
    private static List<QueueBinRow> GoodBins() =>
        [.. Enumerable.Range(0, 8).SelectMany(i => new[] { Bin(At(18, 0).AddMinutes(15 * i)), Bin(At(18, 0).AddMinutes(15 * i), zone: Cit) })];

    /// <summary>
    /// A stored queue minute: the people who entered in it waited <paramref name="realised"/> on average (null: none waited),
    /// and its live part, the nowcast computed at its end (a number, or a reason with <paramref name="noService"/>), unless
    /// <paramref name="live"/> is false.
    /// </summary>
    private static QueueMinuteRow Row(DateTime minute, double? nowcast, double? realised, string noService = null, bool flagged = false, BinStatus? status = BinStatus.Final,
        int version = Version, string zone = Vis, bool live = true) =>
        new(zone, minute, version, status, realised is null ? 0 : 10, realised, live ? nowcast : null, live ? noService : null, live ? flagged : null);

    private static ShadowMinuteRow Shadow(DateTime minute, double? nowcast, string noService = null, bool flagged = false, double? cycle = null, string zone = Vis) =>
        new(zone, minute, nowcast, noService, flagged, cycle);

    /// <summary>
    /// 18:00 to 18:05 of A-VIS: nowcasts 7.5, 8, 9, 10 and 12 at 18:00 to 18:04 against the realised waits 8, 8, 10, 13 and 12
    /// of 18:01 to 18:05 (18:05 has no live part): errors -0.5, 0, -1, -3 and 0, absolute median 0.5.
    /// </summary>
    private static List<QueueMinuteRow> Basic() =>
        [Row(At(18, 0), 7.5, 6), Row(At(18, 1), 8, 8), Row(At(18, 2), 9, 8), Row(At(18, 3), 10, 10), Row(At(18, 4), 12, 13), Row(At(18, 5), null, 12, live: false)];

    private static ComparisonResult Compare(ComparisonInput input, ComparisonSettings settings = null) => ValidationComparison.Compare(input, settings);

    private static ComparisonResult Compare(IReadOnlyList<QueueMinuteRow> minutes, IReadOnlyList<ShadowMinuteRow> shadows = null, IReadOnlyList<QueueBinRow> bins = null,
        IReadOnlyList<QualityInterval> intervals = null, ComparisonSettings settings = null, ComparisonScope scope = null) =>
        Compare(new ComparisonInput
        {
            Scope = scope ?? Scope(),
            QueueMinutes = minutes,
            ShadowMinutes = shadows ?? [],
            QueueBins = bins ?? GoodBins(),
            QualityIntervals = intervals ?? []
        }, settings);

    private static NowcastZoneErrors ZoneOf(ComparisonResult result, string zone) => result.NowcastZones.Single(z => z.QueueZone == zone);

    #endregion

    #region Formulas (F18)

    [Theory]
    [InlineData(7.5, 8, -0.5)]
    [InlineData(12, 8, 4)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 3, -3)]
    public void Error_Should_BeTheNowcastLessTheRealisedWait_When_BothAreWaits(double nowcast, double realised, double error)
    {
        NowcastErrors.Error(nowcast, realised).Should().BeApproximately(error, 1e-12);
    }

    [Theory]
    [InlineData(double.NaN, 8)]
    [InlineData(8, double.NaN)]
    [InlineData(-1, 8)]
    [InlineData(8, -0.1)]
    [InlineData(double.PositiveInfinity, 8)]
    public void Error_Should_BeNull_When_EitherIsNotAWait(double nowcast, double realised)
    {
        NowcastErrors.Error(nowcast, realised).Should().BeNull();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(19.9, true)]
    [InlineData(19.999999, true)]
    [InlineData(19.9999999999, false)]
    [InlineData(20, false)]
    [InlineData(20.0000000001, false)]
    [InlineData(25, false)]
    public void IsUnderCut_Should_BeStrictlyUnder20Minutes_When_ARealisedWaitIsGiven(double realised, bool under)
    {
        NowcastErrors.IsUnderCut(realised, 20).Should().Be(under);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    public void IsUnderCut_Should_BeNull_When_TheRealisedWaitIsNotAWait(double realised)
    {
        NowcastErrors.IsUnderCut(realised, 20).Should().BeNull();
    }

    [Fact]
    public void IsUnderCut_Should_BeNull_When_TheCutIsNotANumber()
    {
        NowcastErrors.IsUnderCut(5, double.NaN).Should().BeNull();
        NowcastErrors.IsUnderCut(5, double.PositiveInfinity).Should().BeNull();
    }

    [Theory]
    [InlineData(new double[] { 3 }, 3.0)]
    [InlineData(new double[] { 1, 3 }, 2.0)]
    [InlineData(new double[] { 5, 1, 3 }, 3.0)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    [InlineData(new double[] { 0, 0, 0.5, 1, 3 }, 0.5)]
    [InlineData(new double[] { -2, -1 }, -1.5)]
    public void Median_Should_TakeTheMiddleOrTheMeanOfTheTwoMiddle_When_ValuesAreGiven(double[] values, double median)
    {
        NowcastErrors.Median(values).Should().BeApproximately(median, 1e-12);
    }

    [Fact]
    public void Median_Should_BeNull_When_ThereIsNoValueOrOneIsNotANumber()
    {
        NowcastErrors.Median([]).Should().BeNull();
        NowcastErrors.Median([1, double.NaN]).Should().BeNull();
        NowcastErrors.Median([double.PositiveInfinity]).Should().BeNull();
        FluentActions.Invoking(() => NowcastErrors.Median(null)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1.9, true)]
    [InlineData(2.0000000001, true)]
    [InlineData(2.000001, false)]
    [InlineData(2.1, false)]
    public void IsWithinTarget_Should_AllowTheTargetItself_When_AMedianIsJudged(double median, bool within)
    {
        NowcastErrors.IsWithinTarget(median, 2).Should().Be(within);
    }

    [Fact]
    public void Stats_Should_GiveTheMedianAndMeanAbsoluteAndTheSignedMean_When_ErrorsAreGiven()
    {
        NowcastErrors.Stats([-0.5, 0, -1, -3, 0]).Should().Be(new NowcastErrorStats(5, 0.5, 0.9, -0.9));
        NowcastErrors.Stats([]).Should().Be(NowcastErrorStats.None);
        NowcastErrors.Stats([1, double.NaN]).Should().Be(new NowcastErrorStats(1, 1, 1, 1), "an error that is not a number is no error");
        FluentActions.Invoking(() => NowcastErrors.Stats(null)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("NothingOpen", NoServiceReason.NothingOpen)]
    [InlineData("ThroughputTooLow", NoServiceReason.ThroughputTooLow)]
    [InlineData("NoThroughputData", NoServiceReason.NoThroughputData)]
    [InlineData("NoQueueLength", NoServiceReason.NoQueueLength)]
    [InlineData("Implausible", NoServiceReason.Implausible)]
    [InlineData("nothingopen", null)]
    [InlineData(" NothingOpen", null)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReasonOf_Should_TakeOnlyTheExactName_When_AStoredReasonIsRead(string name, NoServiceReason? reason)
    {
        NowcastErrors.ReasonOf(name).Should().Be(reason);
    }

    #endregion

    #region Nowcast error

    [Fact]
    public void Compare_Should_PairEachNowcastWithTheNextMinutesRealisedWait_When_MinutesAreFinalAndGood()
    {
        var result = Compare(Basic());

        result.NowcastMinutes.Select(m => (m.MinuteUtc, m.RealisedWaitMinutes, m.Published.ErrorMinutes)).Should().Equal((At(18, 0), 8.0, -0.5), (At(18, 1), 8.0, 0.0),
            (At(18, 2), 10.0, -1.0), (At(18, 3), 13.0, -3.0), (At(18, 4), 12.0, 0.0));
        result.NowcastMinutes.Should().OnlyContain(m => m.Standing == ComparisonStanding.Good && m.UnderCut == true && m.RealisedWaits == 10 && m.Shadow == null &&
                                                         m.Published.Standing == ComparisonStanding.Good && m.Published.Flagged == false);
        var zone = ZoneOf(result, Vis);
        zone.Published.Judged.Should().Be(new NowcastErrorStats(5, 0.5, 0.9, -0.9));
        (zone.Published.Minutes, zone.Published.Excluded, zone.Published.NoService).Should().Be((5, 0, 0));
        zone.Check.Should().Be(new CriterionCheck(0.5, 2, CriterionVerdict.Pass));
        result.NowcastOverall.Check.Should().Be(new CriterionCheck(0.5, 2, CriterionVerdict.Pass));
        ZoneOf(result, Cit).Check.Should().Be(new CriterionCheck(null, 2, CriterionVerdict.NoData));
        result.LeftOut.Should().Be(LeftOutInputs.None);
    }

    [Fact]
    public void Compare_Should_CompareWithTheNextMinuteNotTheNowcastsOwn_When_TheyDiffer()
    {
        // The nowcast is computed at the end of its minute, for someone joining then: the people of the next minute.
        var result = Compare([Row(At(18, 0), 10, 4), Row(At(18, 1), null, 10, live: false)]);

        result.NowcastMinutes.Should().ContainSingle().Which.Published.ErrorMinutes.Should().Be(0);
    }

    [Theory]
    [InlineData(8, 2.0, CriterionVerdict.Pass)]
    [InlineData(12, 2.0, CriterionVerdict.Pass)]
    [InlineData(7.9, 2.1, CriterionVerdict.Fail)]
    [InlineData(13, 3.0, CriterionVerdict.Fail)]
    public void Compare_Should_JudgeTheMedianAgainstTwoMinutes_When_OneMinuteIsJudged(double realised, double median, CriterionVerdict verdict)
    {
        var result = Compare([Row(At(18, 0), 10, 5), Row(At(18, 1), null, realised, live: false)]);

        var check = ZoneOf(result, Vis).Check;
        check.Value.Should().BeApproximately(median, 1e-9);
        check.Verdict.Should().Be(verdict);
    }

    [Fact]
    public void Compare_Should_TakeTheMeanOfTheTwoMiddleErrors_When_AnEvenNumberIsJudged()
    {
        var result = Compare([Row(At(18, 0), 11, 5), Row(At(18, 1), 6, 10), Row(At(18, 2), 13, 8), Row(At(18, 3), 4, 10), Row(At(18, 4), null, 8, live: false)]);

        // Errors 1, -2, 3, -4: absolute 1, 2, 3, 4, median 2.5.
        ZoneOf(result, Vis).Check.Should().Be(new CriterionCheck(2.5, 2, CriterionVerdict.Fail));
        ZoneOf(result, Vis).Published.Judged.Should().Be(new NowcastErrorStats(4, 2.5, 2.5, -0.5));
    }

    [Theory]
    [InlineData(19.9, true)]
    [InlineData(19.9999999999, false)]
    [InlineData(20, false)]
    [InlineData(25, false)]
    public void Compare_Should_JudgeOnlyRealisedWaitsUnder20MinutesAndReportTheRestApart_When_AWaitReachesTheCut(double realised, bool judged)
    {
        var result = Compare([Row(At(18, 0), 18, 5), Row(At(18, 1), null, realised, live: false)]);

        var minute = result.NowcastMinutes.Should().ContainSingle().Subject;
        minute.UnderCut.Should().Be(judged);
        var published = ZoneOf(result, Vis).Published;
        (published.Judged.Minutes, published.AtOrAboveCut.Minutes, published.Excluded).Should().Be((judged ? 1 : 0, judged ? 0 : 1, 0));
        published.AtOrAboveCut.MedianAbsoluteErrorMinutes.Should().Be(judged ? null : Math.Abs(18 - realised), "values are shown where they exist");
        ZoneOf(result, Vis).Check.Verdict.Should().Be(judged ? CriterionVerdict.Pass : CriterionVerdict.NoData);
    }

    [Fact]
    public void Compare_Should_UseTheCampaignsTargetAndCut_When_TheSettingsGiveThem()
    {
        var settings = new ComparisonSettings { NowcastErrorTargetMinutes = 0.4, NowcastWaitCutMinutes = 10 };

        var result = Compare(Basic(), settings: settings);

        // With the cut at 10, the realised waits 10, 13 and 12 are at or above it: 18:00 and 18:01 remain (errors -0.5 and 0).
        var zone = ZoneOf(result, Vis);
        zone.Published.Judged.Should().Be(new NowcastErrorStats(2, 0.25, 0.25, -0.25));
        zone.Published.AtOrAboveCut.Minutes.Should().Be(3);
        zone.Check.Should().Be(new CriterionCheck(0.25, 0.4, CriterionVerdict.Pass));
        ZoneOf(Compare(Basic(), settings: new ComparisonSettings { NowcastErrorTargetMinutes = 0.4 }), Vis).Check.Verdict.Should().Be(CriterionVerdict.Fail);
    }

    [Theory]
    [InlineData(NoServiceReason.NothingOpen)]
    [InlineData(NoServiceReason.ThroughputTooLow)]
    [InlineData(NoServiceReason.NoThroughputData)]
    [InlineData(NoServiceReason.NoQueueLength)]
    [InlineData(NoServiceReason.Implausible)]
    public void Compare_Should_ReportMinutesWithoutServiceApartPerReason_When_TheNowcastHasNoNumber(NoServiceReason reason)
    {
        var result = Compare([Row(At(18, 0), null, 5, reason.ToString()), Row(At(18, 1), 8, 8), Row(At(18, 2), null, 9, live: false)]);

        result.NowcastMinutes[0].Published.Should().Be(new NowcastReading(ComparisonStanding.Good, null, reason, false, null, null));
        var published = ZoneOf(result, Vis).Published;
        (published.Minutes, published.NoService, published.Judged.Minutes, published.Excluded).Should().Be((2, 1, 1, 1));
        published.NoServiceReasons.Should().Equal(Enum.GetValues<NoServiceReason>().Select(r => new NoServiceCount(r, r == reason ? 1 : 0)));
    }

    [Fact]
    public void Compare_Should_JudgeAFlaggedNowcastAndReportItBeside_When_ItRestsOnTheExitTermAlone()
    {
        var result = Compare([Row(At(18, 0), 9, 5, flagged: true), Row(At(18, 1), 8, 8), Row(At(18, 2), null, 9, live: false)]);

        // 9 against 8 (flagged) and 8 against 9: errors 1 and -1.
        var published = ZoneOf(result, Vis).Published;
        published.Judged.Should().Be(new NowcastErrorStats(2, 1, 1, 0));
        published.Flagged.Should().Be(new NowcastErrorStats(1, 1, 1, 1), "the nowcast's own flag is part of the published number, judged all the same");
        published.WithSensorCycle.Should().BeNull("only the shadow takes a sensor cycle time");
    }

    [Fact]
    public void Compare_Should_ReportEveryMinuteApartAndGiveNoData_When_AllMinutesAreDegraded()
    {
        var bins = GoodBins().Where(b => !(b.QueueZone == Vis && b.StartUtc == At(18, 0))).Append(Bin(At(18, 0), BinQuality.Degraded)).ToList();

        var result = Compare(Basic(), bins: bins);

        result.NowcastMinutes.Should().OnlyContain(m => m.Standing == ComparisonStanding.Degraded && m.Published.Standing == ComparisonStanding.Degraded);
        var zone = ZoneOf(result, Vis);
        zone.Published.Standings.Should().Be(StandingTally.None with { Degraded = 5 });
        (zone.Published.Judged, zone.Published.Excluded).Should().Be((NowcastErrorStats.None, 5));
        zone.Published.Degraded.Should().Be(new NowcastErrorStats(5, 0.5, 0.9, -0.9), "their errors are shown, not judged");
        zone.Check.Should().Be(new CriterionCheck(null, 2, CriterionVerdict.NoData));
        result.NowcastOverall.Check.Verdict.Should().Be(CriterionVerdict.NoData);
    }

    [Fact]
    public void Compare_Should_GiveNoDataInsteadOfDividingByZero_When_NoMinuteHoldsANowcast()
    {
        var result = Compare([Row(At(18, 0), null, 5, live: false)]);

        result.NowcastMinutes.Should().BeEmpty();
        result.NowcastZones.Append(result.NowcastOverall).Should().AllSatisfy(z =>
        {
            z.Check.Should().Be(new CriterionCheck(null, 2, CriterionVerdict.NoData));
            (z.Published.Minutes, z.Shadow.Minutes, z.Both.Minutes).Should().Be((0, 0, 0));
            z.Published.Judged.Should().Be(NowcastErrorStats.None);
            z.Shadow.WithSensorCycle.Should().Be(NowcastErrorStats.None);
        });
        result.NowcastZones.Select(z => z.QueueZone).Should().Equal(Cit, Vis);
        result.NowcastOverall.QueueZone.Should().BeNull();
    }

    [Theory]
    [InlineData(16, 20, ComparisonStanding.Degraded)]
    [InlineData(17, 20, ComparisonStanding.Good)]
    [InlineData(9, 10, ComparisonStanding.Good)]
    [InlineData(10, 11, ComparisonStanding.Degraded)]
    public void Compare_Should_CoverTheEntrantsWait_When_AnOutageFollowsTheMinutes(int fromMinute, int toMinute, ComparisonStanding standing)
    {
        // The nowcast of 18:10 against the people of 18:11, who waited 5 minutes: the stored results over 18:10 to 18:17 count.
        var result = Compare([Row(At(18, 10), 6, 5), Row(At(18, 11), null, 5, live: false)],
            intervals: [new QualityInterval(Vis, At(18, fromMinute), At(18, toMinute), BinQuality.Degraded)]);

        result.NowcastMinutes.Should().ContainSingle().Which.Standing.Should().Be(standing);
    }

    public static TheoryData<string, QueueMinuteRow[], QueueBinRow[], QualityInterval[], ComparisonStanding> NotJudged() => new()
    {
        { "the next minute provisional", [Row(At(18, 1), null, 8, status: BinStatus.Provisional, live: false)], null, null, ComparisonStanding.Provisional },
        { "the next minute with no waits", [Row(At(18, 1), null, null, live: false)], null, null, ComparisonStanding.NoSystemWait },
        { "the next minute without a minute result", [Row(At(18, 1), 9, null, status: null)], null, null, ComparisonStanding.NoSystemWait },
        { "no next minute", [], null, null, ComparisonStanding.NoSystemWait },
        { "the next minute of another version", [Row(At(18, 1), null, 8, version: 6, live: false)], null, null, ComparisonStanding.OtherVersion },
        { "the next minute's row unusable", [Row(At(18, 1), null, double.NaN, live: false)], null, null, ComparisonStanding.Unknown },
        { "the bin provisional", [Row(At(18, 1), null, 8, live: false)], [Bin(At(18, 0), status: BinStatus.Provisional)], null, ComparisonStanding.Provisional },
        { "the bin of another version", [Row(At(18, 1), null, 8, live: false)], [Bin(At(18, 0), version: 6)], null, ComparisonStanding.OtherVersion },
        { "the bin Unknown", [Row(At(18, 1), null, 8, live: false)], [Bin(At(18, 0), BinQuality.Unknown)], null, ComparisonStanding.Unknown },
        { "no bin stored", [Row(At(18, 1), null, 8, live: false)], [Bin(At(18, 15))], null, ComparisonStanding.Unknown },
        { "an uncovered line", [Row(At(18, 1), null, 8, live: false)], null, [new QualityInterval(Vis, At(18, 1), At(18, 2), BinQuality.Unknown)], ComparisonStanding.Unknown },
        { "an outage", [Row(At(18, 1), null, 8, live: false)], null, [new QualityInterval(Vis, At(18, 0, 30), At(18, 0, 31), BinQuality.Degraded)], ComparisonStanding.Degraded }
    };

    [Theory]
    [MemberData(nameof(NotJudged))]
    public void Compare_Should_SayWhyAMinuteIsNotJudged_When_ItsStoredResultsAreNotComparable(string because, QueueMinuteRow[] next, QueueBinRow[] bins,
        QualityInterval[] intervals, ComparisonStanding standing)
    {
        var result = Compare([Row(At(18, 0), 10, 5), .. next], bins: bins is null ? null : [.. bins, .. GoodBins().Where(b => b.StartUtc != At(18, 0) || b.QueueZone != Vis)],
            intervals: intervals);

        var minute = result.NowcastMinutes[0];
        (minute.MinuteUtc, minute.Standing, minute.Published.Standing).Should().Be((At(18, 0), standing, standing), because);
        var published = ZoneOf(result, Vis).Published;
        published.Judged.Minutes.Should().Be(0, because);
        published.Excluded.Should().Be(published.Minutes, because);
        ZoneOf(result, Vis).Check.Verdict.Should().Be(CriterionVerdict.NoData, because);
    }

    [Fact]
    public void Compare_Should_MarkTheMinuteOtherVersion_When_TheNowcastsOwnMinuteIsOfAnotherVersion()
    {
        var result = Compare([Row(At(18, 0), 10, 5, version: 6), Row(At(18, 1), null, 10, live: false)]);

        var minute = result.NowcastMinutes.Should().ContainSingle().Subject;
        (minute.Standing, minute.Published.ErrorMinutes).Should().Be((ComparisonStanding.OtherVersion, 0.0), "the value is shown, not judged");
    }

    public static TheoryData<string, QueueMinuteRow[], UnusableKeyReason> UnusableNowcasts() => new()
    {
        { "a number and a reason", [Row(At(18, 0), 10, 5, "NothingOpen")], UnusableKeyReason.Refused },
        { "a flag with neither", [Row(At(18, 0), null, 5)], UnusableKeyReason.Refused },
        { "a number without a flag", [new QueueMinuteRow(Vis, At(18, 0), Version, BinStatus.Final, 10, 5, 10, null, null)], UnusableKeyReason.Refused },
        { "a reason without a flag", [new QueueMinuteRow(Vis, At(18, 0), Version, BinStatus.Final, 10, 5, null, "NothingOpen", null)], UnusableKeyReason.Refused },
        { "a nowcast not a number", [Row(At(18, 0), double.NaN, 5)], UnusableKeyReason.Refused },
        { "a negative nowcast", [Row(At(18, 0), -0.1, 5)], UnusableKeyReason.Refused },
        { "an infinite nowcast", [Row(At(18, 0), double.PositiveInfinity, 5)], UnusableKeyReason.Refused },
        { "a reason in another case", [Row(At(18, 0), null, 5, "nothingopen")], UnusableKeyReason.Refused },
        { "a reason as a number", [Row(At(18, 0), null, 5, "0")], UnusableKeyReason.Refused },
        { "an empty reason", [Row(At(18, 0), null, 5, "")], UnusableKeyReason.Refused },
        { "its own realised mean above a day", [Row(At(18, 0), 10, 1_441)], UnusableKeyReason.Refused },
        { "a minute not in UTC", [Row(Unspecified(At(18, 0)), 10, 5)], UnusableKeyReason.Refused },
        { "twins that disagree on the nowcast", [Row(At(18, 0), 10, 5), Row(At(18, 0), 11, 5)], UnusableKeyReason.Conflicting },
        { "twins that disagree on the flag", [Row(At(18, 0), 10, 5), Row(At(18, 0), 10, 5, flagged: true)], UnusableKeyReason.Conflicting },
        { "a twin not in UTC", [Row(At(18, 0), 10, 5), Row(Unspecified(At(18, 0)), 10, 5)], UnusableKeyReason.Refused }
    };

    [Theory]
    [MemberData(nameof(UnusableNowcasts))]
    public void Compare_Should_MakeTheMinuteUnknownAndListIt_When_ItsStoredNowcastCannotBeUsed(string because, QueueMinuteRow[] rows, UnusableKeyReason reason)
    {
        var result = Compare([.. rows, Row(At(18, 1), 8, 10), Row(At(18, 2), null, 8, live: false)], [Shadow(At(18, 0), 9)]);

        var minute = result.NowcastMinutes[0];
        (minute.MinuteUtc, minute.Standing, minute.RealisedWaitMinutes).Should().Be((At(18, 0), ComparisonStanding.Unknown, (double?)null), because);
        minute.Published.Should().Be(new NowcastReading(ComparisonStanding.Unknown, null, null, null, null, null), because);
        minute.Shadow.Standing.Should().Be(ComparisonStanding.Unknown, "the shadow's version is its queue minute's, which cannot be told");
        var published = ZoneOf(result, Vis).Published;
        published.Standings.Should().Be(StandingTally.None with { Good = 1, Unknown = 1 }, because);
        published.Judged.Minutes.Should().Be(1, "the next minute is judged on its own");
        result.LeftOut.UnusableKeys.Should().Equal(new UnusableKey(UnusableKeyKind.QueueMinute, Vis, null, At(18, 0), null, reason));
    }

    [Fact]
    public void Compare_Should_ListOnlyThePlannedDaysMinutes_When_RowsLieOutsideThem()
    {
        var result = Compare([Row(At(18, 28), 9, 5), Row(At(18, 29), 10, 9), Row(At(18, 30), 12, 10), Row(At(18, 31), null, 12, live: false)],
            scope: Scope(new UtcWindow(At(18, 0), At(18, 30))));

        result.NowcastMinutes.Select(m => (m.MinuteUtc, m.Published.ErrorMinutes)).Should().Equal((At(18, 28), 0.0), (At(18, 29), 0.0));
        result.LeftOut.Should().Be(LeftOutInputs.None, "the realised wait after the last planned minute is read, and stored rows outside are simply not compared");
    }

    [Fact]
    public void Compare_Should_MakeAZonesMinutesUnknown_When_OneOfItsQualityIntervalsCannotBePlaced()
    {
        var result = Compare([.. Basic(), Row(At(18, 0), 4, 5, zone: Cit), Row(At(18, 1), null, 4, live: false, zone: Cit)],
            intervals: [new QualityInterval(Vis, Unspecified(At(18, 40)), At(18, 50), BinQuality.Degraded)]);

        ZoneOf(result, Vis).Published.Standings.Should().Be(StandingTally.None with { Unknown = 5 });
        ZoneOf(result, Cit).Published.Judged.Minutes.Should().Be(1, "the other zone keeps its results");
    }

    #endregion

    #region Ground-truth proof (published and shadow side by side)

    [Fact]
    public void Compare_Should_SetThePublishedAndTheShadowSideBySideWithTheMinutesEachCovers_When_BothWereStored()
    {
        // Shadows 8, 9, 10 and 11 at 18:00 to 18:03 (none at 18:04) against 8, 8, 10 and 13: errors 0, 1, 0 and -2. The first two
        // took the sensor cycle time.
        var result = Compare(Basic(), [Shadow(At(18, 0), 8, cycle: 1.5), Shadow(At(18, 1), 9, cycle: 1.6), Shadow(At(18, 2), 10, flagged: true), Shadow(At(18, 3), 11)]);

        var zone = ZoneOf(result, Vis);
        zone.Published.Judged.Should().Be(new NowcastErrorStats(5, 0.5, 0.9, -0.9));
        zone.Shadow.Judged.Should().Be(new NowcastErrorStats(4, 0.5, 0.75, -0.25));
        (zone.Published.Minutes, zone.Shadow.Minutes).Should().Be((5, 4), "the shadow covers the minutes with a shadow row");
        zone.Shadow.WithSensorCycle.Should().Be(new NowcastErrorStats(2, 0.5, 0.5, 0.5));
        zone.Shadow.Flagged.Should().Be(new NowcastErrorStats(1, 0, 0, 0));
        zone.Both.Should().Be(new NowcastPairedErrors(4, new NowcastErrorStats(4, 0.75, 1.125, -1.125), new NowcastErrorStats(4, 0.5, 0.75, -0.25)));
        zone.Check.Should().Be(new CriterionCheck(0.5, 2, CriterionVerdict.Pass), "the proof has no target; the check is the published nowcast's");
        result.NowcastMinutes[4].Shadow.Should().BeNull();
        result.NowcastMinutes[0].Shadow.Should().Be(new NowcastReading(ComparisonStanding.Good, 8, null, false, 1.5, 0));
        result.NowcastOverall.Both.Minutes.Should().Be(4);
    }

    [Fact]
    public void Compare_Should_ReportTheShadowsMinutesWithoutService_When_ItsZonesShowEveryDeskClosed()
    {
        var result = Compare(Basic(), [Shadow(At(18, 0), null, "NothingOpen"), Shadow(At(18, 1), 8)]);

        var shadow = ZoneOf(result, Vis).Shadow;
        (shadow.Minutes, shadow.NoService, shadow.Judged.Minutes, shadow.Excluded).Should().Be((2, 1, 1, 1));
        shadow.NoServiceReasons.Single(r => r.Reason == NoServiceReason.NothingOpen).Minutes.Should().Be(1);
        ZoneOf(result, Vis).Both.Minutes.Should().Be(1, "both are judged only at 18:01");
    }

    public static TheoryData<string, ShadowMinuteRow[], UnusableKeyReason> UnusableShadows() => new()
    {
        { "twins that disagree", [Shadow(At(18, 0), 8), Shadow(At(18, 0), 9)], UnusableKeyReason.Conflicting },
        { "twins that disagree on the cycle time", [Shadow(At(18, 0), 8, cycle: 1.5), Shadow(At(18, 0), 8)], UnusableKeyReason.Conflicting },
        { "a minute not in UTC", [Shadow(Unspecified(At(18, 0)), 8)], UnusableKeyReason.Refused },
        { "a twin not in UTC", [Shadow(At(18, 0), 8), Shadow(Unspecified(At(18, 0)), 8)], UnusableKeyReason.Refused },
        { "a number and a reason", [Shadow(At(18, 0), 8, "NothingOpen")], UnusableKeyReason.Refused },
        { "neither a number nor a reason", [Shadow(At(18, 0), null)], UnusableKeyReason.Refused },
        { "a reason that is not one", [Shadow(At(18, 0), null, "Closed")], UnusableKeyReason.Refused },
        { "a nowcast not a number", [Shadow(At(18, 0), double.NaN)], UnusableKeyReason.Refused },
        { "a negative nowcast", [Shadow(At(18, 0), -1)], UnusableKeyReason.Refused },
        { "a cycle time under 0.05 minutes", [Shadow(At(18, 0), 8, cycle: 0.049)], UnusableKeyReason.Refused },
        { "a cycle time over 60 minutes", [Shadow(At(18, 0), 8, cycle: 60.001)], UnusableKeyReason.Refused },
        { "a cycle time not a number", [Shadow(At(18, 0), 8, cycle: double.NaN)], UnusableKeyReason.Refused }
    };

    [Theory]
    [MemberData(nameof(UnusableShadows))]
    public void Compare_Should_MakeTheShadowReadingUnknownAndKeepThePublished_When_TheShadowRowCannotBeUsed(string because, ShadowMinuteRow[] shadows,
        UnusableKeyReason reason)
    {
        var result = Compare(Basic(), shadows);

        var minute = result.NowcastMinutes[0];
        minute.Shadow.Should().Be(new NowcastReading(ComparisonStanding.Unknown, null, null, null, null, null), because);
        (minute.Standing, minute.Published.ErrorMinutes).Should().Be((ComparisonStanding.Good, -0.5), because);
        var zone = ZoneOf(result, Vis);
        (zone.Shadow.Minutes, zone.Shadow.Judged.Minutes, zone.Shadow.Standings.Unknown, zone.Published.Judged.Minutes, zone.Both.Minutes).Should().Be((1, 0, 1, 5, 0), because);
        result.LeftOut.UnusableKeys.Should().Equal(new UnusableKey(UnusableKeyKind.ShadowMinute, Vis, null, At(18, 0), null, reason));
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(60)]
    public void Compare_Should_UseTheShadow_When_ItsCycleTimeIsAtABound(double cycle)
    {
        var result = Compare(Basic(), [Shadow(At(18, 0), 8, cycle: cycle)]);

        result.NowcastMinutes[0].Shadow.Should().Be(new NowcastReading(ComparisonStanding.Good, 8, null, false, cycle, 0));
        result.LeftOut.Should().Be(LeftOutInputs.None);
    }

    [Fact]
    public void Compare_Should_ListAShadowWithoutItsQueueMinuteAsUnknown_When_NoPublishedRowWasStored()
    {
        var result = Compare(Basic(), [Shadow(At(18, 20), 8)]);

        var minute = result.NowcastMinutes.Single(m => m.MinuteUtc == At(18, 20));
        (minute.Standing, minute.Published, minute.Shadow).Should().Be((ComparisonStanding.Unknown, (NowcastReading)null,
            new NowcastReading(ComparisonStanding.Unknown, 8, null, false, null, null)), "its version cannot be told without its queue minute");
    }

    public static TheoryData<string, ShadowMinuteRow> ShadowsOutOfPlace() => new()
    {
        { "a zone out of scope", Shadow(At(18, 0), 8, zone: "B-VIS") },
        { "no zone", Shadow(At(18, 0), 8, zone: null) },
        { "a minute not whole", Shadow(At(18, 0, 1), 8) },
        { "a minute before 2000", Shadow(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), 8) },
        { "a null row", null }
    };

    [Theory]
    [MemberData(nameof(ShadowsOutOfPlace))]
    public void Compare_Should_LeaveOutAndCountAShadow_When_ItCannotBePlaced(string because, ShadowMinuteRow row)
    {
        var result = Compare(Basic(), [row]);

        result.NowcastMinutes.Should().OnlyContain(m => m.Shadow == null, because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { ShadowMinutes = 1 }, because);
    }

    [Fact]
    public void Compare_Should_PoolTheMedianOverEveryMinute_When_SeveralZonesAreCompared()
    {
        // A-VIS errors 0, 0 and 3 (median 0), A-CIT errors 1 and -1 (median 1): pooled 0, 0, 1, 1, 3 gives 1, where the median of
        // the zones' medians would give 0.5.
        var result = Compare(
        [
            Row(At(18, 0), 5, 5), Row(At(18, 1), 6, 5), Row(At(18, 2), 10, 6), Row(At(18, 3), null, 7, live: false), Row(At(18, 0), 4, 4, zone: Cit),
            Row(At(18, 1), 4, 3, zone: Cit), Row(At(18, 2), null, 5, live: false, zone: Cit)
        ]);

        result.NowcastZones.Select(z => (z.QueueZone, z.Check.Value)).Should().Equal((Cit, 1.0), (Vis, 0.0));
        result.NowcastOverall.Check.Should().Be(new CriterionCheck(1.0, 2, CriterionVerdict.Pass));
        result.NowcastOverall.Published.Judged.Minutes.Should().Be(5);
    }

    [Fact]
    public void Compare_Should_CarryTheProfileVersionAndGiveTheSameResult_When_TheInputsComeInAnotherOrder()
    {
        var input = new ComparisonInput
        {
            Scope = Scope(),
            QueueMinutes = [.. Basic(), Row(At(18, 0), 4, 4, zone: Cit), Row(At(18, 1), null, 3, "NothingOpen", zone: Cit), Row(At(18, 2), null, 5, live: false, zone: Cit),
                Row(At(18, 30), 3, double.NaN)],
            ShadowMinutes = [Shadow(At(18, 0), 8, cycle: 1.5), Shadow(At(18, 1), 9), Shadow(At(18, 0), 5, zone: Cit), Shadow(At(18, 2), 1), Shadow(At(18, 2), 2)],
            QueueBins = GoodBins(),
            QualityIntervals = [new QualityInterval(Cit, At(18, 2), At(18, 3), BinQuality.Degraded)]
        };
        var reversed = input with
        {
            QueueMinutes = [.. input.QueueMinutes.Reverse()],
            ShadowMinutes = [.. input.ShadowMinutes.Reverse()],
            QueueBins = [.. input.QueueBins.Reverse()],
            Scope = input.Scope with { Zones = [.. input.Scope.Zones.Reverse()] }
        };

        var expected = Compare(input);
        expected.NowcastMinutes.Should().HaveCount(8).And.AllSatisfy(m => m.ProfileVersion.Should().Be(Version));
        expected.NowcastZones.Should().HaveCount(2).And.AllSatisfy(z => z.ProfileVersion.Should().Be(Version));
        expected.NowcastOverall.ProfileVersion.Should().Be(Version);
        Compare(reversed).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering().ComparingRecordsByMembers());
    }

    #endregion

    #region Security review of ARV-104f: levers and finite statistics

    [Fact]
    public void Compare_Should_ExposeTheNoServiceLever_When_TheNowcastSaidNothingOpenWhilePeopleWaited()
    {
        // Probe N2: one judged minute and 30 Good minutes whose published nowcast said NothingOpen while the next minute's
        // entrants waited 10 minutes. The 30 are not judged (no number), so the zone passes on 1 minute; the engine exposes them
        // as NoServiceUnderCut, which the campaign's verdict (ARV-104g) counts against the nowcast or caps.
        List<QueueMinuteRow> rows = [Row(At(18, 0), 10, 10), .. Enumerable.Range(1, 30).Select(m => Row(At(18, m), null, 10, "NothingOpen")),
            Row(At(18, 31), null, 10, live: false)];

        var zone = ZoneOf(Compare(rows), Vis);

        (zone.Published.Minutes, zone.Published.Judged.Minutes, zone.Published.NoService, zone.Published.NoServiceUnderCut, zone.Published.Excluded)
            .Should().Be((31, 1, 30, 30, 30));
        zone.Check.Should().Be(new CriterionCheck(0, 2, CriterionVerdict.Pass), "the lever, documented for ARV-104g's campaign verdict");
        zone.Published.NoServiceReasons.Single(r => r.Reason == NoServiceReason.NothingOpen).Minutes.Should().Be(30);
    }

    [Fact]
    public void Compare_Should_CountOnlyNoServiceMinutesUnderTheCut_When_SomeEntrantsWaitedLonger()
    {
        var zone = ZoneOf(Compare([Row(At(18, 0), null, 5, "NothingOpen"), Row(At(18, 1), null, 25, "ThroughputTooLow"), Row(At(18, 2), null, 8, live: false)]), Vis);

        (zone.Published.NoService, zone.Published.NoServiceUnderCut).Should().Be((2, 1), "18:00's entrants waited 25 minutes (at or above the cut), 18:01's 8");
    }

    [Fact]
    public void Compare_Should_GiveFiniteStatisticsThatSerialise_When_StoredNowcastsAreNearTheLargestDouble()
    {
        // Probes N1 and N1b: nowcasts of 1e308 (published) and 1.7e308 (shadow) are finite and stored as they are (the script
        // checks only that they are at least 0); their errors made the median and the means Infinity and the result could not
        // be written as JSON. Capped at a billion minutes, such an error still counts, and fails.
        var published = Compare([Row(At(18, 0), 1e308, 5), Row(At(18, 1), 1e308, 5), Row(At(18, 2), null, 5, live: false)]);
        var shadow = Compare([Row(At(18, 0), 5, 5), Row(At(18, 1), 5, 5), Row(At(18, 2), null, 5, live: false)], [Shadow(At(18, 0), 1.7e308), Shadow(At(18, 1), double.MaxValue)]);

        var capped = new NowcastErrorStats(2, NowcastErrors.MaxErrorMinutes, NowcastErrors.MaxErrorMinutes, NowcastErrors.MaxErrorMinutes);
        ZoneOf(published, Vis).Published.Judged.Should().Be(capped);
        ZoneOf(published, Vis).Check.Should().Be(new CriterionCheck(NowcastErrors.MaxErrorMinutes, 2, CriterionVerdict.Fail));
        published.NowcastOverall.Published.Judged.Should().Be(capped);
        published.NowcastMinutes[0].Published.Should().Be(new NowcastReading(ComparisonStanding.Good, 1e308, null, false, null, NowcastErrors.MaxErrorMinutes));
        ZoneOf(shadow, Vis).Shadow.Judged.Should().Be(capped);
        ZoneOf(shadow, Vis).Both.Should().Be(new NowcastPairedErrors(2, new NowcastErrorStats(2, 0, 0, 0), capped));
        ZoneOf(shadow, Vis).Check.Verdict.Should().Be(CriterionVerdict.Pass, "the published nowcast was right; only the shadow is capped");
        foreach (var result in new[] { published, shadow })
            FluentActions.Invoking(() => System.Text.Json.JsonSerializer.Serialize(result)).Should().NotThrow();
    }

    [Theory]
    [InlineData(1e308, 5, 1e9)]
    [InlineData(double.MaxValue, 0, 1e9)]
    [InlineData(0, double.MaxValue, -1e9)]
    [InlineData(1e9 + 5, 5, 1e9)]
    [InlineData(1e9 + 6, 5, 1e9)]
    [InlineData(5, 1e9 + 6, -1e9)]
    public void Error_Should_CapItsSizeAtABillionMinutes_When_TheValuesAreHuge(double nowcast, double realised, double error)
    {
        NowcastErrors.Error(nowcast, realised).Should().Be(error);
    }

    [Fact]
    public void MedianAndStats_Should_StayFinite_When_ValuesAreNearTheLargestDouble()
    {
        NowcastErrors.Median([1.7e308, 1.7e308]).Should().Be(1.7e308, "the two middle values are halved before they are added");
        NowcastErrors.Median([double.MaxValue, double.MaxValue, 1, 2]).Should().Be((2 / 2.0) + (double.MaxValue / 2));
        NowcastErrors.Stats([1.7e308, -1.7e308, 1.7e308]).Should().Be(new NowcastErrorStats(3, 1e9, 1e9, 1e9 / 3));
        NowcastErrors.Stats([double.MaxValue, double.MaxValue]).Should().Be(new NowcastErrorStats(2, 1e9, 1e9, 1e9));
    }

    #endregion

    #region Read-only results

    [Fact]
    public void Compare_Should_ReturnListsThatCannotBeChanged_When_DesksAndNowcastsAreCompared()
    {
        var desk = new ScopeDesk(G(31), "IMM", "D01");
        var result = Compare(new ComparisonInput
        {
            Scope = Scope() with { Desks = [desk] },
            QueueMinutes = Basic(),
            ShadowMinutes = [Shadow(At(18, 0), 8)],
            QueueBins = GoodBins(),
            DeskObservations = [new DeskObservationRow(desk.DeskId, At(18, 0), G(901), 1, ObservedDeskState.Idle)],
            DeskMinutes = [new DeskMinuteRow("IMM", "D01", At(18, 0), 0, 60, 0, 0, 0, false)]
        });
        var failed = Compare(new ComparisonInput { Scope = null });

        foreach (var r in new[] { result, failed })
        {
            ShouldBeReadOnly(r.DeskMinutes);
            ShouldBeReadOnly(r.Desks);
            ShouldBeReadOnly(r.NowcastMinutes);
            ShouldBeReadOnly(r.NowcastZones);
        }

        result.DeskMinutes.Should().ContainSingle();
        foreach (var summary in result.Desks.Append(result.DeskOverall))
            ShouldBeReadOnly(summary.Confusion);
        foreach (var zone in result.NowcastZones.Append(result.NowcastOverall))
        {
            ShouldBeReadOnly(zone.Published.NoServiceReasons);
            ShouldBeReadOnly(zone.Shadow.NoServiceReasons);
        }
    }

    /// <summary>Not a List that a caller could cast back and change; adding and, when it holds a row, replacing one are refused.</summary>
    private static void ShouldBeReadOnly<T>(IReadOnlyList<T> list)
    {
        list.Should().NotBeAssignableTo<List<T>>();
        var collection = list.Should().BeAssignableTo<IList<T>>().Subject;
        collection.IsReadOnly.Should().BeTrue();
        FluentActions.Invoking(() => collection.Add(default)).Should().Throw<NotSupportedException>();
        if (list.Count > 0)
            FluentActions.Invoking(() => collection[0] = list[0]).Should().Throw<NotSupportedException>();
    }

    #endregion

    #region Settings

    public static TheoryData<ComparisonSettings, string> InvalidSettings() => new()
    {
        { new ComparisonSettings { DeskAgreementTarget = 0 }, "DeskAgreementTarget" },
        { new ComparisonSettings { DeskAgreementTarget = 1.01 }, "DeskAgreementTarget" },
        { new ComparisonSettings { DeskAgreementTarget = double.NaN }, "DeskAgreementTarget" },
        { new ComparisonSettings { NowcastErrorTargetMinutes = 0 }, "NowcastErrorTargetMinutes" },
        { new ComparisonSettings { NowcastErrorTargetMinutes = 60.5 }, "NowcastErrorTargetMinutes" },
        { new ComparisonSettings { NowcastErrorTargetMinutes = double.PositiveInfinity }, "NowcastErrorTargetMinutes" },
        { new ComparisonSettings { NowcastWaitCutMinutes = -1 }, "NowcastWaitCutMinutes" },
        { new ComparisonSettings { NowcastWaitCutMinutes = 1_441 }, "NowcastWaitCutMinutes" },
        { new ComparisonSettings { NowcastWaitCutMinutes = double.NaN }, "NowcastWaitCutMinutes" }
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Compare_Should_Throw_When_TheNewSettingsAreInvalid(ComparisonSettings settings, string problem)
    {
        FluentActions.Invoking(() => Compare(Basic(), settings: settings)).Should().Throw<ArgumentException>().WithMessage($"*{problem}*");
    }

    [Fact]
    public void Problems_Should_BeNone_When_TheNewSettingsAreAtTheirBounds()
    {
        new ComparisonSettings { DeskAgreementTarget = 1, NowcastErrorTargetMinutes = 60, NowcastWaitCutMinutes = 1_440 }.Problems().Should().BeEmpty();
        new ComparisonSettings { DeskAgreementTarget = 1e-9, NowcastErrorTargetMinutes = 1e-9, NowcastWaitCutMinutes = 1e-9 }.Problems().Should().BeEmpty();
    }

    #endregion
}
