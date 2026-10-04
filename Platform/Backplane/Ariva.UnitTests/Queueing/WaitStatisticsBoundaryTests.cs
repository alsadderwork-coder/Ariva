using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// F7's edges, from the ARV-069 mutation triage: the percentile domain (0, 1], samples that cannot count (no weight, a
/// weight or a wait that is not finite), an empty input, a wait exactly at the target, and the histogram's buckets
/// (order, range, zero counts, the open-ended last bucket, 64-bit overflow).
/// </summary>
public sealed class WaitStatisticsBoundaryTests
{
    private static readonly (double Wait, double Weight)[] Waits = [(2, 1), (4, 1), (6, 1), (8, 1)];
    private static readonly (int Bucket, long Count)[] Histogram = [(0, 1), (3, 1)];

    public static TheoryData<double> OutsideTheDomain => [0, -0.1, 1.000001, double.NaN];

    [Theory]
    [MemberData(nameof(OutsideTheDomain))]
    public void Percentiles_Should_RefuseAShare_When_ItIsOutsideZeroToOne(double p)
    {
        FluentActions.Invoking(() => WaitStatistics.Percentile(Waits, p)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => WaitStatistics.Percentiles([1.0, 2.0], 0.5, p)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => WaitStatistics.Percentile(Histogram, p)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Percentiles_Should_AcceptOne_AsTheLargestWait()
    {
        WaitStatistics.Percentile(Waits, 1).Should().Be(8);
        WaitStatistics.Percentiles([3.0, 1.0, 2.0], 1).Should().Equal(3.0);
        WaitStatistics.Percentile(Histogram, 1).Should().Be(2.0, "bucket 3's upper edge is 2 minutes");
    }

    [Fact]
    public void Percentile_Should_IgnoreSamples_When_TheyHaveNoWeightOrAWaitThatIsNotFinite()
    {
        (double, double)[] samples = [(2, 1), (4, 1), (double.PositiveInfinity, 5), (double.NaN, 5), (100, 0), (100, -3)];
        WaitStatistics.Percentile(samples, 1).Should().Be(4);
        WaitStatistics.Percentile(samples, 0.5).Should().Be(2);
        WaitStatistics.Percentile([(5.0, 0.0)], 0.5).Should().BeNull("no weight");
    }

    [Fact]
    public void MeanAndShare_Should_IgnoreSamples_When_TheirWeightOrWaitCannotCount()
    {
        (double, double)[] samples = [(2, 1), (4, 1), (1000, double.PositiveInfinity), (1000, -1), (1000, 0), (double.PositiveInfinity, 1)];
        WaitStatistics.Mean(samples).Should().Be(3);
        WaitStatistics.Share(samples, 3).Should().Be(0.5);
    }

    [Fact]
    public void MeanAndShare_Should_BeNull_When_ThereIsNoWeight()
    {
        WaitStatistics.Mean([]).Should().BeNull();
        WaitStatistics.Mean([(4.0, 0.0)]).Should().BeNull();
        WaitStatistics.Share([], 5).Should().BeNull();
        WaitStatistics.Share([(4.0, -1.0)], 5).Should().BeNull();
    }

    [Fact]
    public void Share_Should_CountWaitsAtTheTarget_AndAddEveryWeight()
    {
        WaitStatistics.Share([(5, 1), (5, 2), (9, 1)], 5).Should().Be(0.75, "waits of exactly the target meet it; the weights add up");
        WaitStatistics.Share([(1, 1), (2, 3), (9, 4)], 5).Should().Be(0.5);
    }

    [Fact]
    public void Percentiles_Should_BeNull_When_ThereAreNoWaits()
    {
        WaitStatistics.Percentiles([], 0.5, 0.9).Should().Equal(new double?[] { null, null });
        WaitStatistics.Percentiles([double.NaN, double.PositiveInfinity], 0.5).Should().Equal((double?)null);
    }

    [Fact]
    public void BucketOf_Should_PutZeroAndNegativeWaitsInTheFirstBucket_AndLongOnesInTheLast()
    {
        WaitStatistics.BucketOf(TimeSpan.FromSeconds(-90)).Should().Be(0);
        WaitStatistics.BucketOf(TimeSpan.Zero).Should().Be(0);
        WaitStatistics.BucketOf(TimeSpan.FromSeconds(30)).Should().Be(1);
        WaitStatistics.BucketOf(TimeSpan.FromDays(3)).Should().Be(WaitStatistics.Buckets - 1);
    }

    [Fact]
    public void HistogramPercentile_Should_ReadTheOpenEndedBucket_AsItsLowerEdge()
    {
        WaitStatistics.Percentile([(WaitStatistics.Buckets - 1, 2L)], 0.5).Should().Be((WaitStatistics.Buckets - 1) * 0.5);
        WaitStatistics.Percentile([(WaitStatistics.Buckets - 2, 2L)], 0.5).Should().Be((WaitStatistics.Buckets - 1) * 0.5, "the bucket before it reads as its upper edge");
    }

    [Fact]
    public void HistogramPercentile_Should_AcceptZeroCounts_AndRefuseDisorderOrBucketsOutOfRange()
    {
        WaitStatistics.Percentile([(0, 0L), (2, 1L)], 0.5).Should().Be(1.5);
        WaitStatistics.Percentile([(0, 0L)], 0.5).Should().BeNull("no passenger");
        foreach (var bad in new (int, long)[][] { [(2, 1), (2, 1)], [(3, 1), (2, 1)], [(WaitStatistics.Buckets, 1)], [(-1, 1)], [(1, -1)] })
            FluentActions.Invoking(() => WaitStatistics.Percentile(bad, 0.5)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Histograms_Should_RefuseToOverflow_When_CountsAddPastLongMaxValue()
    {
        FluentActions.Invoking(() => WaitStatistics.Percentile([(0, long.MaxValue), (1, 1L)], 0.5)).Should().Throw<OverflowException>();
        FluentActions.Invoking(() => WaitStatistics.Merge([(0, long.MaxValue)], [(0, 1L)])).Should().Throw<OverflowException>();
    }

    [Fact]
    public void Merge_Should_SkipAMissingHistogram_AcceptTheFirstAndLastBucket_AndRefuseOthers()
    {
        WaitStatistics.Merge(null, [(0, 2L), (WaitStatistics.Buckets - 1, 1L)], [(0, 0L)])
            .Should().Equal((0, 2L), (WaitStatistics.Buckets - 1, 1L));
        foreach (var bad in new (int, long)[][] { [(WaitStatistics.Buckets, 1)], [(-1, 1)], [(0, -1)] })
            FluentActions.Invoking(() => WaitStatistics.Merge(bad)).Should().Throw<ArgumentException>();
    }
}
