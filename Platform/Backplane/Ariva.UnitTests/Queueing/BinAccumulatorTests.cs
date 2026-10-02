using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-031: realised waits attributed to the entry bin, provisional until final, minute aggregates and arrival-weighted
/// percentiles, exactly as formulas F6, F7 and F11 and ADR-0007 state them, with the documented numeric cases.
/// </summary>
public sealed class BinAccumulatorTests
{
    #region Helpers

    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double minutes) => T0.AddMinutes(minutes);

    private static readonly QueueZoneGeometry Geometry = new(
        "A-VIS",
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS entry" },
        new HashSet<string>(StringComparer.Ordinal) { "A-VIS exit" },
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    private sealed class Pipeline(TimeSpan? lateness = null, BinSettings settings = null)
    {
        public QueueStateEngine Engine { get; } = new(Geometry, new QueueEngineSettings
        {
            Lateness = lateness ?? TimeSpan.Zero, HandoverWindow = TimeSpan.FromSeconds(30)
        });

        public BinAccumulator Bins { get; } = new("A-VIS", 12, settings);
        public List<BinResult> Published { get; } = [];
        public List<MinuteResult> Minutes { get; } = [];
        public List<RecomputationRequest> Recomputations { get; } = [];

        public void Offer(params QueueInput[] inputs)
        {
            foreach (var input in inputs)
                Engine.Offer(input, input.TimeUtc);
        }

        public BinUpdate Advance(double minute)
        {
            var update = Bins.Accept(Engine.Advance(At(minute)));
            Published.AddRange(update.Bins);
            Minutes.AddRange(update.Minutes);
            Recomputations.AddRange(update.Recomputations);
            return update;
        }

        public BinResult Latest(double binMinute) => Published.Last(b => b.StartUtc == At(binMinute));
    }

    private static QueueCrossing In(double minute, string track = null, bool degraded = false) => new("A-VIS entry", CrossingDirection.In, track, At(minute), degraded);
    private static QueueCrossing Out(double minute, string track = null) => new("A-VIS exit", CrossingDirection.Out, track, At(minute));

    #endregion

    #region F6 attribution and finality

    [Fact]
    public void Wait_Should_BelongToTheEntryBin_When_ThePassengerExitsInALaterBin()
    {
        // F6: entering at 18:14:30 and exiting at 18:40:00 belongs to bin 18:00, which cannot be final before 18:40:00.
        var p = new Pipeline();
        p.Offer(In(14.5, "t1"), Out(40, "t1"));

        p.Advance(20);
        var before = p.Latest(0);
        p.Advance(39.99);
        var stillOpen = p.Latest(0);
        p.Advance(40.01);
        var final = p.Latest(0);

        before.Status.Should().Be(BinStatus.Provisional);
        before.Open.Should().Be(1);
        stillOpen.Status.Should().Be(BinStatus.Provisional, "the passenger has not exited yet");
        final.Should().Match<BinResult>(b => b.Status == BinStatus.Final && b.Entries == 1 && b.Waits.Waits == 1 && b.Waits.MeanMinutes == 25.5 &&
                                             b.Open == 0 && b.Revision == 1 && b.ZoneProfileVersion == 12);
        p.Latest(30).Exits.Should().Be(1, "the exit counts in the bin it happened in");
        p.Latest(30).Entries.Should().Be(0);
    }

    [Fact]
    public void Bin_Should_BeFinalWithItsLostTrack_When_ATrackFragments()
    {
        // F6: a track entering at 18:05 and lost at 18:10 inside the zone is Fragmented; the bin can still become final.
        var p = new Pipeline();
        p.Offer(In(5, "t1"), new QueueTrackSeen("t1", At(9.9)), new QueueTrackSeen("t1", At(10)));

        p.Advance(16);

        p.Latest(0).Should().Match<BinResult>(b => b.Status == BinStatus.Final && b.Fragmented == 1 && b.Waits.Waits == 0 && b.Waits.P90Minutes == null);
    }

    [Fact]
    public void Bin_Should_BeFinalWithZeroAndNoStatistics_When_NobodyEntered()
    {
        var p = new Pipeline();
        p.Offer(In(1), Out(3));

        p.Advance(46);

        var empty = p.Latest(15);
        empty.Should().Match<BinResult>(b => b.Status == BinStatus.Final && b.Entries == 0 && b.Waits.Waits == 0 && b.Waits.MeanMinutes == null &&
                                             b.Waits.P90Minutes == null && b.Quality == BinQuality.Good);
        p.Latest(30).Status.Should().Be(BinStatus.Final, "every bin the watermark passed is published, even without anyone");
    }

    [Fact]
    public void Bin_Should_StayProvisional_When_TheWatermarkHasNotPassedItsEnd()
    {
        var p = new Pipeline(lateness: TimeSpan.FromSeconds(30));
        p.Offer(In(1), Out(3));

        p.Advance(15.4);
        var early = p.Latest(0);
        p.Advance(15.6);

        early.Status.Should().Be(BinStatus.Provisional, "18:15:24 minus 30 seconds of lateness is still inside the bin");
        p.Latest(0).Status.Should().Be(BinStatus.Final);
    }

    [Fact]
    public void Censored_Should_DegradeTheBin_When_TheyExceedFivePercentOfEntries()
    {
        var p = new Pipeline();
        for (var k = 0; k < 10; k++)
            p.Offer(In(k));
        p.Offer(In(10.5, "never-leaves"));
        for (var k = 0; k < 10; k++)
            p.Offer(Out(12 + k * 0.1));

        p.Advance(140);

        var bin = p.Latest(0);
        bin.Status.Should().Be(BinStatus.Final);
        bin.Censored.Should().Be(1);
        bin.Quality.Should().Be(BinQuality.Degraded, "1 of 11 censored is above 5 percent");
    }

    [Fact]
    public void Quality_Should_TakeTheWorstInput_When_OutagesAndDegradedEventsAreMarked()
    {
        var p = new Pipeline();
        p.Offer(In(1, degraded: true), Out(3), In(16), Out(18), In(31), Out(33));
        p.Bins.Mark(At(32), At(34), BinQuality.Unknown);

        p.Advance(50);

        p.Latest(0).Quality.Should().Be(BinQuality.Degraded, "a corrected clock degrades its bin (F11)");
        p.Latest(15).Quality.Should().Be(BinQuality.Good);
        p.Latest(30).Should().Match<BinResult>(b => b.Quality == BinQuality.Unknown && b.Status == BinStatus.Final,
            "a line without coverage makes the bin Unknown; it still becomes final with that flag");
    }

    [Fact]
    public void Bins_Should_AlignToTheSiteDayStart_When_ItIsNotAWholeQuarterFromUtc()
    {
        // An operating day that starts 5 minutes past the hour in UTC: bins run 18:05 to 18:20.
        var bins = new BinAccumulator("A-VIS", 1, new BinSettings { DayStartOffset = TimeSpan.FromMinutes(5) });

        bins.BinOf(At(16)).Should().Be(At(5));
        bins.BinOf(At(4.99)).Should().Be(At(-10));
        new BinAccumulator("A-VIS", 1).BinOf(At(14.99)).Should().Be(At(0));
    }

    #endregion

    #region F7 statistics and minute aggregates

    [Fact]
    public void Statistics_Should_GiveTheDocumentedPercentiles_When_TenPassengersWaitOneToTenMinutes()
    {
        var p = new Pipeline();
        for (var k = 1; k <= 10; k++)
            p.Offer(In(k * 0.1, "t" + k), Out(k * 0.1 + k, "t" + k));

        p.Advance(30);

        var waits = p.Latest(0).Waits;
        waits.P90Minutes.Should().BeApproximately(9, 1e-9);
        waits.P50Minutes.Should().BeApproximately(5, 1e-9);
        waits.MeanMinutes.Should().BeApproximately(5.5, 1e-9);
        waits.ShareWithinTarget.Should().Be(1);
        waits.Histogram.Sum(h => h.Count).Should().Be(10);
    }

    [Fact]
    public void Statistics_Should_GiveNoPercentile_When_FewerThanTheMinimumWaited()
    {
        var p = new Pipeline(settings: new BinSettings { MinimumPassengers = 3 });
        p.Offer(In(1, "a"), Out(4, "a"), In(2, "b"), Out(6, "b"));

        p.Advance(30);

        p.Latest(0).Waits.Should().Match<WaitSummary>(w => w.Waits == 2 && w.P90Minutes == null && w.MeanMinutes == 3.5,
            "a bin below the contract's minimum has no percentile, not zero");
    }

    [Fact]
    public void Minutes_Should_AggregateByEntryMinute_When_PassengersEnterInDifferentMinutes()
    {
        var p = new Pipeline();
        p.Offer(In(1.2, "a"), In(1.7, "b"), In(2.5, "c"), Out(5, "a"), Out(6, "b"), Out(9, "c"));

        p.Advance(20);

        var minute1 = p.Minutes.Last(m => m.StartUtc == At(1));
        var minute2 = p.Minutes.Last(m => m.StartUtc == At(2));
        minute1.Should().Match<MinuteResult>(m => m.Entries == 2 && m.Waits.Waits == 2 && m.Status == BinStatus.Final);
        minute1.Waits.MeanMinutes.Should().BeApproximately((3.8 + 4.3) / 2, 1e-9);
        minute2.Waits.P90Minutes.Should().BeApproximately(6.5, 1e-9);
        p.Minutes.Last(m => m.StartUtc == At(9)).Exits.Should().Be(1);
    }

    [Fact]
    public void WaitStatistics_Should_UseTheWeightedNearestRank_When_SamplesCarryWeights()
    {
        (double, double)[] samples = [(5, 10), (20, 2)];

        WaitStatistics.Percentile(samples, 0.9).Should().Be(20, "0.9 x 12 = 10.8 is reached only at w = 20; averaging per-minute P90s would give 12.5");
        WaitStatistics.Share(samples, 15).Should().BeApproximately(10.0 / 12, 1e-9);
        WaitStatistics.Mean(samples).Should().BeApproximately(7.5, 1e-9);
        WaitStatistics.Percentile([1.0, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.9).Should().Be(9);
        WaitStatistics.Percentile(Array.Empty<double>(), 0.9).Should().BeNull();
        WaitStatistics.Percentile([.. Enumerable.Range(1, 10).Select(k => ((double)k, 1e-12))], 0.9).Should().Be(9, "tiny weights keep their ranks");
        WaitStatistics.Percentiles([3.0, 1, 2, 10, 4, 5, 6, 7, 8, 9], 0.5, 0.9).Should().Equal(5, 9);
    }

    [Fact]
    public void Histograms_Should_MergeExactlyToTheBucket_When_BinsAreCombined()
    {
        IReadOnlyList<(int, long)> a = [(WaitStatistics.BucketOf(TimeSpan.FromMinutes(5)), 10)];
        IReadOnlyList<(int, long)> b = [(WaitStatistics.BucketOf(TimeSpan.FromMinutes(20)), 2)];

        var merged = WaitStatistics.Merge(a, b);

        WaitStatistics.Percentile(merged, 0.9).Should().Be(20.5, "the upper edge of the 20-minute bucket");
        WaitStatistics.Percentile(merged, 0.5).Should().Be(5.5);
        WaitStatistics.BucketOf(TimeSpan.FromDays(3)).Should().Be(WaitStatistics.Buckets - 1);
        Action wrong = () => WaitStatistics.Merge(new List<(int, long)> { (-1, 1) });
        wrong.Should().Throw<ArgumentException>();
        Action unsorted = () => WaitStatistics.Percentile(new List<(int, long)> { (5, 1), (3, 1) }, 0.5);
        unsorted.Should().Throw<ArgumentException>();
        Action negative = () => WaitStatistics.Percentile(new List<(int, long)> { (5, -1) }, 0.5);
        negative.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Statistics_Should_FallBackToTheHistogram_When_ABinHoldsMoreWaitsThanItKeepsExactly()
    {
        var p = new Pipeline(settings: new BinSettings { MaxExactWaitsPerBin = 5 });
        for (var k = 1; k <= 10; k++)
            p.Offer(In(k * 0.1, "t" + k), Out(k * 0.1 + k, "t" + k));

        p.Advance(30);

        var bin = p.Latest(0);
        bin.Waits.P90Minutes.Should().Be(9.5, "the 9-minute bucket read at its upper edge");
        bin.Quality.Should().Be(BinQuality.Degraded, "percentiles from the histogram are approximate");
    }

    #endregion

    #region Revisions and late events

    [Fact]
    public void Final_Should_NeverChangeInPlace_When_ALateEventArrives()
    {
        var p = new Pipeline(lateness: TimeSpan.FromSeconds(30));
        p.Offer(In(1, "a"), Out(4, "a"));
        p.Advance(20);
        var final = p.Latest(0);

        p.Engine.Offer(In(2, "b"), At(20.1));
        p.Engine.Offer(Out(5, "b"), At(20.1));
        p.Advance(21);

        p.Published.Where(b => b.StartUtc == At(0)).Should().ContainSingle("a final bin is published once").Which.Should().Be(final);
        p.Recomputations.Should().ContainSingle().Which.Should().Match<RecomputationRequest>(r =>
            r.FromUtc == At(0) && r.ToUtc == At(15) && r.LateEvents == 2 && r.QueueZone == "A-VIS");
    }

    [Fact]
    public void LateEvents_Should_MarkProvisionalBinsForRecomputation_When_TheyFinalise()
    {
        var p = new Pipeline(lateness: TimeSpan.FromSeconds(30));
        p.Offer(In(1, "a"), Out(10, "a"));
        p.Advance(5);

        p.Engine.Offer(Out(2), At(5.1));
        p.Advance(6);
        p.Recomputations.Should().BeEmpty("the bin is still provisional");
        p.Advance(16);

        p.Latest(0).Should().Match<BinResult>(b => b.Status == BinStatus.Final && b.LateEvents == 1);
        p.Recomputations.Should().ContainSingle().Which.FromUtc.Should().Be(At(0));
    }

    [Fact]
    public void Revisions_Should_AddRevisionOnlyForChangedFinals_When_ARecomputationRuns()
    {
        var original = new Pipeline();
        original.Offer(In(1, "a"), Out(4, "a"), In(16, "b"), Out(18, "b"));
        original.Advance(30);

        // The recomputation replays the archive with the late passenger included.
        var replay = new Pipeline();
        replay.Offer(In(1, "a"), Out(4, "a"), In(2, "c"), Out(5, "c"), In(16, "b"), Out(18, "b"));
        replay.Advance(30);

        var revisions = BinRevisions.Next(original.Published, replay.Published, "late events of S-15 replayed from the archive");

        revisions.Should().ContainSingle().Which.Should().Match<BinResult>(b =>
            b.StartUtc == At(0) && b.Revision == 2 && b.Entries == 2 && b.RevisionReason == "late events of S-15 replayed from the archive");
        BinRevisions.Next(original.Published, original.Published, "nothing changed").Should().BeEmpty();
    }

    [Fact]
    public void Recomputations_Should_CoverOnlyTheBinsLateEventsBelongTo_When_TheyAreFarApart()
    {
        var p = new Pipeline(lateness: TimeSpan.FromSeconds(30));
        p.Offer(In(1, "a"), Out(4, "a"));
        p.Advance(100);

        p.Engine.Offer(Out(2), At(100.1));
        p.Engine.Offer(Out(62), At(100.1));
        p.Engine.Offer(Out(63), At(100.1));
        p.Engine.Offer(Out(-5000), At(100.1));
        var step = p.Engine.Advance(At(101));
        var update = p.Bins.Accept(step);

        step.Rejections.BeyondHorizon.Should().Be(1, "more than 3 days back is beyond what the archive can recompute");
        update.Recomputations.Select(r => (r.FromUtc, r.ToUtc, r.LateEvents)).Should().Equal((At(0), At(15), 1L), (At(60), At(75), 2L));
    }

    [Fact]
    public void Mark_Should_AskForARecomputation_When_AnOutageIsReportedForAFinalBin()
    {
        var p = new Pipeline();
        p.Offer(In(1), Out(3));
        p.Advance(31);

        var requests = p.Bins.Mark(At(10), At(40), BinQuality.Unknown);
        p.Advance(50);

        requests.Should().ContainSingle().Which.Should().Match<RecomputationRequest>(r => r.FromUtc == At(0) && r.ToUtc == At(30));
        p.Latest(30).Quality.Should().Be(BinQuality.Unknown, "the open bin takes the mark");
        p.Bins.Mark(At(10), At(10).AddDays(8), BinQuality.Unknown).Should().BeEmpty("a mark longer than a week is refused");
    }

    [Fact]
    public void Gaps_Should_BeReported_When_TheWatermarkJumpsBeyondTheOpenBinBound()
    {
        var p = new Pipeline(settings: new BinSettings { MaxOpenBins = 4 });
        p.Offer(In(1), Out(3));
        p.Advance(10);

        var update = p.Bins.Accept(p.Engine.Advance(At(15 * 20 + 1)));

        update.Gaps.Should().ContainSingle().Which.Should().Be((At(15), At(15 * 17)));
        update.Bins.Should().OnlyContain(b => b.StartUtc == At(0) || b.StartUtc >= At(15 * 17));
    }

    #endregion

    #region Bounds

    [Fact]
    public void Bounds_Should_FinaliseTheEarliestBin_When_TooManyAreOpen()
    {
        var p = new Pipeline(settings: new BinSettings { MaxOpenBins = 3 });
        p.Offer(In(1, "never"));
        for (var k = 1; k < 8; k++)
            p.Offer(In(k * 15 + 1, "p" + k), Out(k * 15 + 2, "p" + k));

        p.Advance(8 * 15);

        p.Bins.OpenBins.Should().BeLessThanOrEqualTo(4);
        p.Latest(0).Should().Match<BinResult>(b => b.Status == BinStatus.Final && b.Open == 1 && b.Quality == BinQuality.Degraded,
            "the bound publishes the earliest bin with its open person, flagged");
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(15, 25 * 3600)]
    [InlineData(15, 30)]
    [InlineData(15, 0)]
    public void Settings_Should_BeValidated_When_TheAccumulatorIsCreated(int binMinutes, int dayStartSeconds)
    {
        var settings = new BinSettings { BinLength = TimeSpan.FromMinutes(binMinutes), DayStartOffset = TimeSpan.FromSeconds(dayStartSeconds) };

        Action create = () => _ = new BinAccumulator("A-VIS", 1, settings);

        if (binMinutes == 15 && dayStartSeconds == 0)
            create.Should().NotThrow();
        else
            create.Should().Throw<ArgumentException>();
    }

    #endregion
}
