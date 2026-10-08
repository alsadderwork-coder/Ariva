using System.Collections;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104e: the F18 comparison engine. The formulas from the cases in docs/domain/formulas.md (96 of 100 gives 96 percent; a
/// tracer of 8 minutes against 8.9 passes, 20 against 22.5 fails; a manual count of 0 gives the absolute error), the Proposed
/// rules (directions per line role, two observers, the floor at 0, the matching rule, abandoned runs apart, the offset outlier
/// bound, the sensitivity shift, track completion pooled over Good bins), Degraded, Unknown, Provisional and OtherVersion items
/// counted apart, and the guards: unusable rows left out and counted, an invalid or oversized scope compared not at all, no
/// division by zero.
/// </summary>
public sealed class ComparisonTests
{
    #region Fixtures

    private const int Version = 7;
    private const string Vis = "A-VIS";
    private const string Cit = "A-CIT";

    private static readonly Guid ZoneA = G(1);
    private static readonly Guid ZoneB = G(2);
    private static readonly Guid EntryA = G(11);
    private static readonly Guid ExitA = G(12);
    private static readonly Guid CountA = G(13);
    private static readonly Guid OverflowA = G(14);
    private static readonly Guid EntryB = G(21);
    private static readonly Guid O1 = G(901);
    private static readonly Guid O2 = G(902);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    private static DateTime At(int hour, int minute, int second = 0) => new(2026, 10, 8, hour, minute, second, DateTimeKind.Utc);

    private static ComparisonScope Scope(params UtcWindow[] windows) =>
        new(Version, [new ScopeZone(ZoneA, Vis), new ScopeZone(ZoneB, Cit)],
            [
                new ScopeLine(EntryA, "A-VIS entry", LineRole.Entry, Vis), new ScopeLine(ExitA, "A-VIS exit", LineRole.Exit, Vis),
                new ScopeLine(CountA, "A-VIS count", LineRole.Count, Vis), new ScopeLine(OverflowA, "A-OV entry", LineRole.OverflowEntry, Vis),
                new ScopeLine(EntryB, "A-CIT entry", LineRole.Entry, Cit)
            ],
            windows.Length == 0 ? [new UtcWindow(At(18, 0), At(19, 0))] : windows);

    private static ManualCountRow Count(Guid line, DateTime bin, int crossingsIn, int crossingsOut = 0, Guid? observer = null, int revision = 1) =>
        new(line, bin, observer ?? O1, revision, crossingsIn, crossingsOut);

    private static LineBinCount LineBin(string line, DateTime bin, long crossingsIn, long crossingsOut = 0, int version = Version, string zone = Vis,
        LineCountSource source = LineCountSource.Ariva) =>
        new(zone, line, bin, version, crossingsIn, crossingsOut, source);

    private static QueueBinRow Bin(DateTime start, BinQuality quality = BinQuality.Good, BinStatus status = BinStatus.Final, int version = Version, string zone = Vis,
        int revision = 1, long entries = 100, long abandoned = 0) =>
        new(zone, start, TimeSpan.FromMinutes(15), revision, status, quality, version, entries, abandoned);

    /// <summary>Final Good bins of a zone from <paramref name="from"/> to before <paramref name="to"/>.</summary>
    private static IEnumerable<QueueBinRow> Bins(DateTime from, DateTime to, string zone = Vis)
    {
        for (var start = from; start < to; start = start.AddMinutes(15))
            yield return Bin(start, zone: zone);
    }

    private static QueueMinuteRow Minute(DateTime minute, double? mean, long? waits = null, BinStatus? status = BinStatus.Final, int version = Version,
        string zone = Vis) =>
        new(zone, minute, version, status, waits ?? (mean is null ? 0 : 10), mean);

    /// <summary>Tracer n (code T-nn, run id G(1000 + n)) of batch b (id G(2000 + b)), corrected times as given, device times ahead by the offset.</summary>
    private static TracerRunRow Run(int n, DateTime joined, DateTime exited, int offsetMs = 0, bool abandoned = false, Guid? zone = null, int batch = 1,
        Guid? observer = null) =>
        new(G(1000 + n), G(2000 + batch), observer ?? O1, zone ?? ZoneA, $"T-{n:D2}", joined.AddMilliseconds(offsetMs), exited.AddMilliseconds(offsetMs), offsetMs,
            joined, exited, abandoned);

    private static ZoneHealthBin Health(DateTime start, long entered, long exited, BinStatus status = BinStatus.Final, int version = Version, string zone = Vis,
        int revision = 1, long abandoned = 0, long fragmented = 0, long censored = 0) =>
        new(zone, start, TimeSpan.FromMinutes(15), revision, status, version, 0, 0, null, null, null, entered, exited, abandoned, fragmented, censored, 0,
            0, null, 0, 0, 0);

    private static ComparisonResult Compare(ComparisonInput input, ComparisonSettings settings = null) => ValidationComparison.Compare(input, settings);

    /// <summary>The same instant without the UTC kind: DateTime equality, and so a key, ignores the kind.</summary>
    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private static readonly DateTime Year2000 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TracerComparison Tracer(ComparisonResult result, int n) => result.Tracers.Single(t => t.RunId == G(1000 + n));

    private static TracerZoneSummary ZoneOf(ComparisonResult result, string zone) => result.TracerZones.Single(z => z.QueueZone == zone);

    /// <summary>
    /// A list whose <see cref="Count"/> says <paramref name="count"/> and which yields <paramref name="rows"/> (lazily, so a
    /// million rows cost no memory here): the engine counts the rows it reads, never the list's own Count.
    /// </summary>
    private sealed class Listed<T>(int count, IEnumerable<T> rows) : IReadOnlyList<T>
    {
        public int Count => count;

        public T this[int index] => rows.ElementAt(index);

        public IEnumerator<T> GetEnumerator() => rows.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary><paramref name="count"/> null rows, as many as the list says.</summary>
    private static Listed<T> Nulls<T>(int count) where T : class => new(count, Enumerable.Repeat<T>(null, count));

    private static UnusableKey Key(UnusableKeyKind kind, DateTime start, UnusableKeyReason reason = UnusableKeyReason.Refused, string line = null,
        Guid? observer = null, string zone = Vis) =>
        new(kind, zone, line, start, observer, reason);

    #endregion

    #region Formulas (F18)

    [Theory]
    [InlineData(96, 100, 0.96)]
    [InlineData(104, 100, 0.96)]
    [InlineData(100, 100, 1.0)]
    [InlineData(95, 100, 0.95)]
    [InlineData(0, 100, 0.0)]
    [InlineData(150, 100, 0.5)]
    [InlineData(200, 100, 0.0)]
    [InlineData(350, 100, 0.0)]
    [InlineData(3, 2.5, 0.8)]
    public void Accuracy_Should_FollowF18AndStayWithinZeroAndOne_When_TheManualCountIsAboveZero(double system, double manual, double accuracy)
    {
        CountAccuracy.Accuracy(system, manual).Should().BeApproximately(accuracy, 1e-9);
        CountAccuracy.AbsoluteError(system, manual).Should().BeApproximately(Math.Abs(system - manual), 1e-9);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    public void Accuracy_Should_BeNullAndTheAbsoluteErrorStand_When_TheManualCountIsZero(double system, double error)
    {
        CountAccuracy.Accuracy(system, 0).Should().BeNull();
        CountAccuracy.AbsoluteError(system, 0).Should().Be(error);
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void Accuracy_Should_BeNull_When_AnInputIsNotACount(double system, double manual)
    {
        CountAccuracy.Accuracy(system, manual).Should().BeNull();
        CountAccuracy.AbsoluteError(system, manual).Should().BeNull();
    }

    [Theory]
    [InlineData(LineRole.Entry, new[] { CrossingDirection.In })]
    [InlineData(LineRole.OverflowEntry, new[] { CrossingDirection.In })]
    [InlineData(LineRole.Exit, new[] { CrossingDirection.Out })]
    [InlineData(LineRole.Count, new[] { CrossingDirection.In, CrossingDirection.Out })]
    public void Directions_Should_CompareTheDirectionOfTheRole_When_ALineIsCounted(LineRole role, CrossingDirection[] directions)
    {
        CountAccuracy.Directions(role).Should().Equal(directions);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8, 1)]
    [InlineData(10, 1)]
    [InlineData(12, 1.2)]
    [InlineData(20, 2)]
    [InlineData(45, 4.5)]
    public void Tolerance_Should_BeTheLargerOfOneMinuteAndTenPercent_When_ATracerWaited(double tracer, double tolerance)
    {
        TracerWaits.Tolerance(tracer).Should().BeApproximately(tolerance, 1e-12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.5)]
    public void Tolerance_Should_BeNull_When_TheWaitIsNotAWait(double tracer)
    {
        TracerWaits.Tolerance(tracer).Should().BeNull();
        TracerWaits.IsWithin(0, tracer).Should().BeNull();
    }

    [Theory]
    [InlineData(8, 8.9, true)]
    [InlineData(20, 22.5, false)]
    [InlineData(20, 22, true)]
    [InlineData(20, 18, true)]
    [InlineData(20, 17.9, false)]
    [InlineData(8, 7, true)]
    [InlineData(8, 6.99, false)]
    [InlineData(10, 11, true)]
    [InlineData(10, 11.0000001, false)]
    [InlineData(30, 33, true)]
    [InlineData(30, 26.9, false)]
    public void IsWithin_Should_FollowF18_When_ATracerIsComparedWithTheSystem(double tracer, double system, bool within)
    {
        TracerWaits.IsWithin(system - tracer, tracer).Should().Be(within);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void IsWithin_Should_BeNull_When_TheErrorIsNotANumber(double error)
    {
        TracerWaits.IsWithin(error, 10).Should().BeNull();
    }

    [Fact]
    public void Bias_Should_FollowF18AndItsSignTheErrors_When_TracersAreCompared()
    {
        TracerWaits.Bias([(8.9, 8), (22.5, 20)]).Should().BeApproximately(3.4 / 28, 1e-12);
        TracerWaits.Bias([(7, 8), (18, 20)]).Should().BeApproximately(-3.0 / 28, 1e-12);
        TracerWaits.Bias([(9, 8), (19, 20)]).Should().Be(0);
        TracerWaits.Bias([(0, 10)]).Should().Be(-1);
    }

    [Fact]
    public void Bias_Should_BeNull_When_ThereIsNothingToDivideBy()
    {
        TracerWaits.Bias([]).Should().BeNull();
        TracerWaits.Bias([(0, 0)]).Should().BeNull();
        TracerWaits.Bias([(double.NaN, 8)]).Should().BeNull();
        TracerWaits.Bias([(8, double.PositiveInfinity)]).Should().BeNull();
        TracerWaits.Bias([(8, 8), (-1, 8)]).Should().BeNull();
        TracerWaits.Bias([(8, 8), (8, -1)]).Should().BeNull();
        FluentActions.Invoking(() => TracerWaits.Bias(null)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(new int[0], null)]
    [InlineData(new[] { 5 }, 5.0)]
    [InlineData(new[] { 3, 1, 2 }, 2.0)]
    [InlineData(new[] { 4, 1, 3, 2 }, 2.5)]
    [InlineData(new[] { 100, -100 }, 0.0)]
    [InlineData(new[] { 600, 500, 9000, 400 }, 550.0)]
    public void Median_Should_TakeTheMiddleOrTheMeanOfTheTwoMiddle_When_OffsetsAreGiven(int[] offsets, double? median)
    {
        ClockOffsets.Median(offsets).Should().Be(median);
    }

    [Theory]
    [InlineData(6_000, 0, true)]
    [InlineData(5_000, 0, false)]
    [InlineData(-5_000, 0, false)]
    [InlineData(-5_001, 0, true)]
    [InlineData(5_550, 550, false)]
    [InlineData(9_000, 550, true)]
    public void IsOutlier_Should_FlagAnOffsetBeyondTheBound_When_ComparedWithTheMedian(int offset, double median, bool outlier)
    {
        ClockOffsets.IsOutlier(offset, median, TimeSpan.FromSeconds(5)).Should().Be(outlier);
    }

    [Theory]
    [InlineData(20, 18, 0.9)]
    [InlineData(20, 20, 1.0)]
    [InlineData(20, 0, 0.0)]
    [InlineData(0, 0, null)]
    [InlineData(10, 11, null)]
    [InlineData(-1, 0, null)]
    [InlineData(10, -1, null)]
    public void Rate_Should_FollowF18_When_TracksEnteredAndExited(long entered, long exited, double? rate)
    {
        TrackCompletion.Rate(entered, exited).Should().Be(rate);
    }

    #endregion

    #region Count accuracy

    [Fact]
    public void Compare_Should_GiveNinetySixPercent_When_TheSystemCounts96OfAManual100()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100, 7)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 96, 3)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.Problem.Should().BeNull();
        var item = result.CountBins.Should().ContainSingle().Subject;
        item.Should().BeEquivalentTo(new
        {
            ProfileVersion = Version, QueueZone = Vis, LineId = EntryA, LineName = "A-VIS entry", Role = LineRole.Entry, BinStartUtc = At(18, 0),
            Direction = CrossingDirection.In, Standing = ComparisonStanding.Good, Observers = 1, ManualCount = 100.0, ObserverDifference = (double?)null,
            SystemCount = 96L, AbsoluteError = 4.0, MeetsTarget = true
        });
        item.Accuracy.Should().BeApproximately(0.96, 1e-12);

        var line = result.Lines.Single(l => l.LineId == EntryA);
        line.Should().BeEquivalentTo(new { ProfileVersion = Version, QueueZone = Vis, Judged = 1, Passing = 1, ManualZero = 0, ManualZeroAbsoluteError = 0.0 });
        line.LowestAccuracy.Should().BeApproximately(0.96, 1e-12);
        line.PooledAccuracy.Should().BeApproximately(0.96, 1e-12);
        line.Bins.Should().Be(new StandingTally(1, 0, 0, 0, 0, 0));
        line.Check.Should().BeEquivalentTo(new { Target = 0.95, Verdict = CriterionVerdict.Pass });
        line.Check.Value.Should().BeApproximately(0.96, 1e-12);
    }

    [Fact]
    public void Compare_Should_FailTheLine_When_OneJudgedBinIsBelowTheTarget()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 15), 100), Count(EntryA, At(18, 30), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 96), LineBin("A-VIS entry", At(18, 15), 94), LineBin("A-VIS entry", At(18, 30), 95)],
            QueueBins = [.. Bins(At(18, 0), At(18, 45))]
        });

        var line = result.Lines.Single(l => l.LineId == EntryA);
        line.Judged.Should().Be(3);
        line.Passing.Should().Be(2, "95 of 100 meets the target exactly, 94 does not");
        line.LowestAccuracy.Should().BeApproximately(0.94, 1e-12);
        line.PooledAccuracy.Should().BeApproximately(1 - (15.0 / 300), 1e-12);
        line.Check.Verdict.Should().Be(CriterionVerdict.Fail);
        result.CountBins.Select(i => i.MeetsTarget).Should().Equal(true, false, true);
    }

    [Fact]
    public void Compare_Should_ReportTheAbsoluteErrorAndNoVerdict_When_TheManualCountIsZero()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 0), Count(EntryA, At(18, 15), 0)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 3)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        result.CountBins.Select(i => (i.Accuracy, i.AbsoluteError, i.MeetsTarget, i.SystemCount))
            .Should().Equal((null, 3.0, null, 3L), (null, 0.0, null, 0L));
        var line = result.Lines.Single(l => l.LineId == EntryA);
        line.Should().BeEquivalentTo(new { Judged = 0, Passing = 0, LowestAccuracy = (double?)null, PooledAccuracy = (double?)null, ManualZero = 2, ManualZeroAbsoluteError = 3.0 });
        line.Check.Should().Be(new CriterionCheck(null, 0.95, CriterionVerdict.NoData));
    }

    [Fact]
    public void Compare_Should_CompareTheDirectionOfEachRole_When_LinesHaveDifferentRoles()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts =
            [
                Count(EntryA, At(18, 0), 100, 50), Count(ExitA, At(18, 0), 50, 80), Count(CountA, At(18, 0), 40, 20), Count(OverflowA, At(18, 0), 10, 70)
            ],
            LineBins =
            [
                LineBin("A-VIS entry", At(18, 0), 100, 1), LineBin("A-VIS exit", At(18, 0), 1, 76), LineBin("A-VIS count", At(18, 0), 38, 21),
                LineBin("A-OV entry", At(18, 0), 9, 0)
            ],
            QueueBins = [Bin(At(18, 0))]
        });

        result.CountBins.Select(i => (i.LineName, i.Direction, i.ManualCount, i.SystemCount)).Should().Equal(
            ("A-OV entry", CrossingDirection.In, 10.0, 9L),
            ("A-VIS count", CrossingDirection.In, 40.0, 38L),
            ("A-VIS count", CrossingDirection.Out, 20.0, 21L),
            ("A-VIS entry", CrossingDirection.In, 100.0, 100L),
            ("A-VIS exit", CrossingDirection.Out, 80.0, 76L));
        result.Lines.Single(l => l.LineId == CountA).Judged.Should().Be(2);
        result.Lines.Single(l => l.LineId == CountA).Bins.Good.Should().Be(1, "a count line's two directions are one bin");
        result.Lines.Single(l => l.LineId == ExitA).LowestAccuracy.Should().BeApproximately(0.95, 1e-12);
    }

    [Fact]
    public void Compare_Should_AverageTheObserversAndReportTheirDifference_When_TwoCountedTheSameBin()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 104, observer: O2), Count(EntryA, At(18, 0), 100, observer: O1)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 100)],
            QueueBins = [Bin(At(18, 0))]
        });

        var item = result.CountBins.Should().ContainSingle().Subject;
        item.Observers.Should().Be(2);
        item.ManualCount.Should().Be(102);
        item.ObserverDifference.Should().Be(4);
        item.Accuracy.Should().BeApproximately(1 - (2.0 / 102), 1e-12);
    }

    [Fact]
    public void Compare_Should_TakeEachObserversLatestRevision_When_CountsWereCorrected()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts =
            [
                Count(EntryA, At(18, 0), 100, revision: 3), Count(EntryA, At(18, 0), 80, revision: 1), Count(EntryA, At(18, 0), 90, revision: 2),
                Count(EntryA, At(18, 0), 50, observer: O2)
            ],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 75)],
            QueueBins = [Bin(At(18, 0))]
        });

        var item = result.CountBins.Should().ContainSingle().Subject;
        item.ManualCount.Should().Be(75, "revision 3 of the first observer (100) and the second's 50");
        item.ObserverDifference.Should().Be(50);
        item.Accuracy.Should().Be(1);
        result.LeftOut.Should().Be(LeftOutInputs.None, "superseded revisions are corrections, not rows left out");
    }

    [Fact]
    public void Compare_Should_CountZeroCrossings_When_AStoredBinHasNoRowForTheLine()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 10)],
            QueueBins = [Bin(At(18, 0))]
        });

        var item = result.CountBins.Should().ContainSingle().Subject;
        (item.SystemCount, item.AbsoluteError, item.Accuracy, item.MeetsTarget, item.Standing).Should()
            .Be(((long?)0L, (double?)10.0, (double?)0.0, (bool?)false, ComparisonStanding.Good));
    }

    [Fact]
    public void Compare_Should_JudgeAgainstTheLatestRevisionOfAStoredBin_When_ItWasRecomputed()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 15), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 100), LineBin("A-VIS entry", At(18, 15), 100)],
            QueueBins =
            [
                Bin(At(18, 0), BinQuality.Degraded, revision: 1), Bin(At(18, 0), revision: 2), Bin(At(18, 15), revision: 3),
                Bin(At(18, 15), BinQuality.Unknown, revision: 4)
            ]
        });

        result.CountBins.Select(i => i.Standing).Should().Equal(ComparisonStanding.Good, ComparisonStanding.Unknown);
    }

    [Fact]
    public void Compare_Should_ReportDegradedAndUnknownBinsApartWithTheirCounts_When_TheZoneWasNotHealthy()
    {
        // 18:00 Good; 18:15 Degraded and 18:30 Unknown in queue_bin; 18:45 has no stored bin (Unknown); 19:00 is Good in queue_bin
        // but a device outage covers part of it (Degraded); 19:15 Good. Each holds a manual count of 100 against 90 counted.
        var bins = new[] { At(18, 0), At(18, 15), At(18, 30), At(18, 45), At(19, 0), At(19, 15) };
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(19, 30))),
            ManualCounts = [.. bins.Select(b => Count(EntryA, b, 100))],
            LineBins = [.. bins.Select(b => LineBin("A-VIS entry", b, 90))],
            QueueBins =
            [
                Bin(At(18, 0)), Bin(At(18, 15), BinQuality.Degraded), Bin(At(18, 30), BinQuality.Unknown), Bin(At(19, 0)), Bin(At(19, 15))
            ],
            QualityIntervals = [new QualityInterval(Vis, At(19, 10), At(19, 12), BinQuality.Degraded)]
        });

        result.CountBins.Select(i => i.Standing).Should().Equal(ComparisonStanding.Good, ComparisonStanding.Degraded, ComparisonStanding.Unknown,
            ComparisonStanding.Unknown, ComparisonStanding.Degraded, ComparisonStanding.Good);
        result.CountBins.Select(i => i.SystemCount).Should().Equal(90L, 90L, 90L, null, 90L, 90L);
        result.CountBins.Count(i => i.Accuracy is not null).Should().Be(5, "values are shown for every bin a stored result covers");
        var line = result.Lines.Single(l => l.LineId == EntryA);
        line.Bins.Should().Be(new StandingTally(2, 0, 2, 2, 0, 0));
        line.Judged.Should().Be(2, "only the Good bins are judged; the others are counted apart, never dropped");
        (line.JudgedBins, line.ExcludedBins).Should().Be((2, 4));
        line.Check.Verdict.Should().Be(CriterionVerdict.Fail);
    }

    [Fact]
    public void Compare_Should_CountProvisionalAndOtherVersionBinsApart_When_TheStoredResultsAreNotComparableYet()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 15), 100), Count(EntryA, At(18, 30), 100), Count(EntryA, At(18, 45), 100)],
            LineBins =
            [
                LineBin("A-VIS entry", At(18, 0), 99), LineBin("A-VIS entry", At(18, 15), 99), LineBin("A-VIS entry", At(18, 30), 99),
                LineBin("A-VIS entry", At(18, 45), 60), LineBin("A-VIS entry", At(18, 45), 39, version: 6)
            ],
            QueueBins =
            [
                Bin(At(18, 0), status: BinStatus.Provisional, quality: BinQuality.Degraded), Bin(At(18, 15), version: 6), Bin(At(18, 30)), Bin(At(18, 45))
            ]
        });

        result.CountBins.Select(i => (i.Standing, i.SystemCount)).Should().Equal(
            (ComparisonStanding.Provisional, 99L), (ComparisonStanding.OtherVersion, null), (ComparisonStanding.Good, 99L), (ComparisonStanding.OtherVersion, null));
        result.Lines.Single(l => l.LineId == EntryA).Bins.Should().Be(new StandingTally(1, 0, 0, 0, 1, 2));
    }

    [Fact]
    public void Compare_Should_SummariseEveryLineInScope_When_SomeHaveNoCounts()
    {
        var result = Compare(new ComparisonInput { Scope = Scope() });

        result.Lines.Select(l => (l.QueueZone, l.LineName)).Should().Equal((Cit, "A-CIT entry"), (Vis, "A-OV entry"), (Vis, "A-VIS count"), (Vis, "A-VIS entry"),
            (Vis, "A-VIS exit"));
        result.Lines.Should().AllSatisfy(l =>
        {
            l.Check.Should().Be(new CriterionCheck(null, 0.95, CriterionVerdict.NoData));
            l.Bins.Should().Be(StandingTally.None);
            l.PooledAccuracy.Should().BeNull();
        });
    }

    [Fact]
    public void Compare_Should_LeaveVendorCountsOut_When_BothSourcesCountedALine()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 100), LineBin("A-VIS entry", At(18, 0), 50, source: LineCountSource.Vendor)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.CountBins.Single().SystemCount.Should().Be(100);
        result.LeftOut.LineBins.Should().Be(1);
    }

    [Fact]
    public void Compare_Should_UseTheCampaignTarget_When_TheSettingsGiveOne()
    {
        var input = new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 97)],
            QueueBins = [Bin(At(18, 0))]
        };

        Compare(input, new ComparisonSettings { CountAccuracyTarget = 0.98 }).Lines.Single(l => l.LineId == EntryA).Check.Verdict.Should().Be(CriterionVerdict.Fail);
        Compare(input, new ComparisonSettings { CountAccuracyTarget = 0.97 }).Lines.Single(l => l.LineId == EntryA).Check.Verdict.Should().Be(CriterionVerdict.Pass);
    }

    #endregion

    #region Tracer wait error and bias

    [Fact]
    public void Compare_Should_PassTheTracer_When_ItWaited8MinutesAgainstASystem8Point9()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 30), At(18, 13, 30))],
            QueueMinutes = [Minute(At(18, 5), 8.9)],
            QueueBins = [Bin(At(18, 0))]
        });

        var run = Tracer(result, 1);
        run.Should().BeEquivalentTo(new
        {
            ProfileVersion = Version, TracerCode = "T-01", QueueZone = Vis, Standing = ComparisonStanding.Good, TracerWaitMinutes = 8.0, SystemWaitMinutes = 8.9,
            ToleranceMinutes = 1.0, WithinTolerance = true, Abandoned = false, OffsetOutlier = false, ObserverId = O1, BatchId = G(2001)
        });
        run.ErrorMinutes.Should().BeApproximately(0.9, 1e-12);
        var zone = ZoneOf(result, Vis);
        (zone.Runs, zone.Compared, zone.WithinTolerance).Should().Be((1, 1, 1));
        zone.Bias.Should().BeApproximately(0.9 / 8, 1e-12);
        zone.ErrorCheck.Should().Be(new CriterionCheck(1, 1, CriterionVerdict.Pass));
        zone.BiasCheck.Verdict.Should().Be(CriterionVerdict.Fail, "a single tracer 0.9 minutes over 8 is 11 percent of bias");
        result.TracerOverall.QueueZone.Should().BeNull();
        result.TracerOverall.Compared.Should().Be(1);
    }

    [Fact]
    public void Compare_Should_FailTheTracer_When_ItWaited20MinutesAgainstASystem22Point5()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 30), At(18, 25, 30))],
            QueueMinutes = [Minute(At(18, 5), 22.5)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        var run = Tracer(result, 1);
        (run.ErrorMinutes, run.ToleranceMinutes, run.WithinTolerance).Should().Be(((double?)2.5, 2.0, (bool?)false));
        ZoneOf(result, Vis).ErrorCheck.Should().Be(new CriterionCheck(0, 1, CriterionVerdict.Fail));
    }

    [Theory]
    [InlineData(5 * 60 + 30, 8.0)]
    [InlineData(5 * 60 + 45, 8.5)]
    [InlineData(5 * 60 + 15, 7.5)]
    [InlineData(5 * 60, 7.0)]
    [InlineData(6 * 60, 9.0)]
    [InlineData(4 * 60 + 30, 6.0)]
    [InlineData(4 * 60, 6.0)]
    [InlineData(6 * 60 + 59, 10.0)]
    public void Compare_Should_ReadTheSystemWaitBetweenMinuteCentres_When_TheTracerJoinedBetweenThem(int secondsAfter1800, double system)
    {
        var joined = At(18, 0).AddSeconds(secondsAfter1800);
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, joined, joined.AddMinutes(10))],
            QueueMinutes = [Minute(At(18, 4), 6), Minute(At(18, 5), 8), Minute(At(18, 6), 10)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        Tracer(result, 1).SystemWaitMinutes.Should().BeApproximately(system, 1e-12);
    }

    public static TheoryData<string, QueueMinuteRow> UnusableNeighbours => new()
    {
        { "provisional", Minute(At(18, 6), 10, status: BinStatus.Provisional) },
        { "no status", Minute(At(18, 6), 10, status: null) },
        { "another version", Minute(At(18, 6), 10, version: 6) },
        { "no waits", Minute(At(18, 6), null) },
        { "another zone", Minute(At(18, 6), 10, zone: Cit) }
    };

    [Theory]
    [MemberData(nameof(UnusableNeighbours))]
    public void Compare_Should_HoldTheJoinMinutesOwnMean_When_TheNeighbourIsNotUsable(string because, QueueMinuteRow neighbour)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 45), At(18, 15, 45))],
            QueueMinutes = [Minute(At(18, 5), 8), neighbour],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        Tracer(result, 1).SystemWaitMinutes.Should().Be(8, because);
    }

    public static TheoryData<string, QueueMinuteRow[], QueueBinRow[], ComparisonStanding, double?> UnmatchedRuns => new()
    {
        { "join minute provisional", [Minute(At(18, 5), 8, status: BinStatus.Provisional)], [Bin(At(18, 0))], ComparisonStanding.Provisional, null },
        { "join minute without a status", [Minute(At(18, 5), 8, status: null)], [Bin(At(18, 0))], ComparisonStanding.Provisional, null },
        { "join minute of another version", [Minute(At(18, 5), 8, version: 6)], [Bin(At(18, 0))], ComparisonStanding.OtherVersion, null },
        { "join minute without waits", [Minute(At(18, 5), null)], [Bin(At(18, 0))], ComparisonStanding.NoSystemWait, null },
        { "no join minute", [], [Bin(At(18, 0))], ComparisonStanding.NoSystemWait, null },
        { "no join minute in a Degraded bin", [], [Bin(At(18, 0), BinQuality.Degraded)], ComparisonStanding.Degraded, null },
        { "a bin of another version", [Minute(At(18, 5), 8)], [Bin(At(18, 0), version: 6)], ComparisonStanding.OtherVersion, 8 },
        { "no stored bin", [Minute(At(18, 5), 8)], [], ComparisonStanding.Unknown, 8 },
        { "a provisional bin with a final minute", [Minute(At(18, 5), 8)], [Bin(At(18, 0), status: BinStatus.Provisional)], ComparisonStanding.Good, 8 }
    };

    [Theory]
    [MemberData(nameof(UnmatchedRuns))]
    public void Compare_Should_SayWhyARunIsNotCompared_When_TheSystemHasNoComparableWait(string because, QueueMinuteRow[] minutes, QueueBinRow[] bins,
        ComparisonStanding standing, double? system)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 30), At(18, 13, 30))],
            QueueMinutes = minutes,
            QueueBins = bins
        });

        var run = Tracer(result, 1);
        run.Standing.Should().Be(standing, because);
        run.SystemWaitMinutes.Should().Be(system, because);
        run.WithinTolerance.Should().Be(system is null ? null : true, because);
        var zone = ZoneOf(result, Vis);
        zone.Standings.Total.Should().Be(1);
        zone.Compared.Should().Be(standing == ComparisonStanding.Good ? 1 : 0, because);
        zone.ErrorCheck.Verdict.Should().Be(standing == ComparisonStanding.Good ? CriterionVerdict.Pass : CriterionVerdict.NoData, because);
    }

    [Fact]
    public void Compare_Should_LeaveAbandonedRunsOutOfTheErrorAndBiasAndCountThem_When_ATracerLeftUnserved()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(19, 15))),
            TracerRuns =
            [
                Run(1, At(18, 5, 30), At(18, 13, 30)), Run(2, At(18, 20, 10), At(18, 31, 10), abandoned: true),
                Run(3, At(18, 22, 0), At(18, 25, 0), abandoned: true), Run(4, At(18, 47, 0), At(18, 50, 0), abandoned: true),
                Run(5, At(19, 1, 0), At(19, 5, 0), abandoned: true)
            ],
            QueueMinutes = [Minute(At(18, 5), 8.9), Minute(At(18, 20), 30), Minute(At(18, 22), 30)],
            QueueBins =
            [
                Bin(At(18, 0)), Bin(At(18, 15), entries: 120, abandoned: 3), Bin(At(18, 30)), Bin(At(18, 45), status: BinStatus.Provisional, abandoned: 9),
                Bin(At(19, 0), version: 6, abandoned: 4)
            ]
        });

        Tracer(result, 2).Should().BeEquivalentTo(new
        {
            Abandoned = true, Standing = ComparisonStanding.Good, SystemWaitMinutes = (double?)null, ErrorMinutes = (double?)null, WithinTolerance = (bool?)null,
            ErrorOffsetMinusMinutes = (double?)null, ErrorOffsetPlusMinutes = (double?)null, TracerWaitMinutes = 11.0
        });
        Tracer(result, 2).ToleranceMinutes.Should().BeApproximately(1.1, 1e-12);
        var zone = ZoneOf(result, Vis);
        zone.Should().BeEquivalentTo(new { Runs = 5, Compared = 1, Excluded = 0, WithinTolerance = 1, Abandoned = 4, AbandonedBins = 1, SystemAbandoned = 3L, SystemEntries = 120L },
            "a provisional bin and a bin of another version add nothing beside the abandoned runs");
        zone.Standings.Should().Be(new StandingTally(1, 0, 0, 0, 0, 0), "abandoned runs are counted apart, not by standing");
        zone.Bias.Should().BeApproximately(0.9 / 8, 1e-12, "the abandoned runs add nothing to the bias");
        result.TracerOverall.Should().BeEquivalentTo(new { Runs = 5, Compared = 1, Abandoned = 4, AbandonedBins = 1, SystemAbandoned = 3L, SystemEntries = 120L });
    }

    [Fact]
    public void Compare_Should_ReportDegradedAndUnknownRunsApart_When_TheZoneWasNotHealthyWhileTheyWaited()
    {
        // T-04 waits in a Good bin; T-01 into the Degraded 18:15 bin; T-02 into 18:45, which has no stored bin; T-03 through an
        // Unknown interval (an exit line without coverage) inside the Good 18:30 bin.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns =
            [
                Run(1, At(18, 5, 30), At(18, 25, 30)), Run(2, At(18, 35, 30), At(18, 55, 30)), Run(3, At(18, 39, 40), At(18, 44, 40)),
                Run(4, At(18, 1, 0), At(18, 6, 0))
            ],
            QueueMinutes = [Minute(At(18, 1), 5), Minute(At(18, 5), 20), Minute(At(18, 35), 20), Minute(At(18, 39), 5)],
            QueueBins = [Bin(At(18, 0)), Bin(At(18, 15), BinQuality.Degraded), Bin(At(18, 30))],
            QualityIntervals = [new QualityInterval(Vis, At(18, 40), At(18, 41), BinQuality.Unknown)]
        });

        result.Tracers.Select(t => (t.TracerCode, t.Standing)).Should().Equal(("T-04", ComparisonStanding.Good), ("T-01", ComparisonStanding.Degraded),
            ("T-02", ComparisonStanding.Unknown), ("T-03", ComparisonStanding.Unknown));
        Tracer(result, 1).SystemWaitMinutes.Should().Be(20, "the value is shown, but not judged");
        Tracer(result, 1).ErrorMinutes.Should().Be(0);
        var zone = ZoneOf(result, Vis);
        zone.Standings.Should().Be(new StandingTally(1, 0, 1, 2, 0, 0));
        (zone.Runs, zone.Compared, zone.Excluded, zone.WithinTolerance).Should().Be((4, 1, 3, 1));
        zone.Bias.Should().Be(0, "only T-04 is compared");
        zone.Sensitivity.Runs.Should().Be(0, "2 seconds before T-04's join is 18:00:58, a minute with no stored waits");
        Tracer(result, 4).ErrorOffsetMinusMinutes.Should().Be(0);
        Tracer(result, 4).ErrorOffsetPlusMinutes.Should().BeNull();
    }

    [Fact]
    public void Compare_Should_SummariseBiasAndErrorsPerZone_When_SeveralTracersRan()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns =
            [
                Run(1, At(18, 5, 30), At(18, 13, 30)), Run(2, At(18, 6, 30), At(18, 26, 30)), Run(3, At(18, 7, 30), At(18, 17, 30), zone: ZoneB),
                Run(4, At(18, 8, 30), At(18, 18, 30))
            ],
            QueueMinutes = [Minute(At(18, 5), 8.9), Minute(At(18, 6), 19), Minute(At(18, 7), 9.5, zone: Cit), Minute(At(18, 8), 9.6)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30)), .. Bins(At(18, 0), At(18, 30), Cit)]
        }, new ComparisonSettings { WithinToleranceTarget = 0.6, BiasTarget = 0.05 });

        // Zone A: errors 0.9 (8), -1 (20) and -0.4 (10): all within; bias -0.5 / 38.
        var a = ZoneOf(result, Vis);
        (a.Compared, a.WithinTolerance).Should().Be((3, 3));
        a.Bias.Should().BeApproximately(-0.5 / 38, 1e-12);
        a.MeanErrorMinutes.Should().BeApproximately(-0.5 / 3, 1e-12);
        a.LargestAbsoluteErrorMinutes.Should().BeApproximately(1, 1e-12);
        a.ErrorCheck.Verdict.Should().Be(CriterionVerdict.Pass);
        a.BiasCheck.Verdict.Should().Be(CriterionVerdict.Pass);
        ZoneOf(result, Cit).Bias.Should().BeApproximately(-0.05, 1e-12);
        ZoneOf(result, Cit).BiasCheck.Verdict.Should().Be(CriterionVerdict.Pass, "exactly 5 percent is within the target");
        result.TracerOverall.Bias.Should().BeApproximately(-1.0 / 48, 1e-12);
        result.TracerOverall.Compared.Should().Be(4);
        result.TracerZones.Select(z => z.QueueZone).Should().Equal(Cit, Vis);
    }

    [Fact]
    public void Compare_Should_FlagTheBatch_When_ItsOffsetIsFarFromTheObserversMedian()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns =
            [
                Run(1, At(18, 1, 0), At(18, 9, 0), offsetMs: 400, batch: 1), Run(2, At(18, 2, 0), At(18, 10, 0), offsetMs: 500, batch: 2),
                Run(3, At(18, 3, 0), At(18, 11, 0), offsetMs: 600, batch: 3), Run(4, At(18, 4, 0), At(18, 12, 0), offsetMs: 9_000, batch: 4),
                Run(5, At(18, 5, 0), At(18, 13, 0), offsetMs: 9_000, batch: 4),
                Run(6, At(18, 6, 0), At(18, 14, 0), offsetMs: -1_200, batch: 5, observer: O2)
            ]
        });

        result.Observers.Should().Equal(new ObserverOffsetSpread(O1, 4, 5, 400, 550, 9_000, 1), new ObserverOffsetSpread(O2, 1, 1, -1_200, -1_200, -1_200, 0));
        result.Batches.Should().Equal(new BatchOffset(G(2001), O1, 400, 1, -150, false), new BatchOffset(G(2002), O1, 500, 1, -50, false),
            new BatchOffset(G(2003), O1, 600, 1, 50, false), new BatchOffset(G(2004), O1, 9_000, 2, 8_450, true),
            new BatchOffset(G(2005), O2, -1_200, 1, 0, false));
        result.Tracers.Where(t => t.OffsetOutlier).Select(t => t.TracerCode).Should().Equal("T-04", "T-05");
        ZoneOf(result, Vis).OffsetOutliers.Should().Be(2);
        result.TracerOverall.OffsetOutliers.Should().Be(2);
    }

    [Theory]
    [InlineData(10_000, 5, false)]
    [InlineData(10_002, 5, true)]
    [InlineData(2_002, 1, true)]
    [InlineData(2_000, 1, false)]
    public void Compare_Should_FlagOnlyBeyondTheBound_When_TwoBatchesDisagree(int second, int boundSeconds, bool outliers)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 1, 0), At(18, 9, 0), offsetMs: 0, batch: 1), Run(2, At(18, 2, 0), At(18, 10, 0), offsetMs: second, batch: 2)]
        }, new ComparisonSettings { OffsetOutlierBound = TimeSpan.FromSeconds(boundSeconds) });

        result.Batches.Select(b => b.Outlier).Should().Equal(outliers, outliers);
        result.Observers.Single().MedianMs.Should().Be(second / 2.0);
    }

    [Fact]
    public void Compare_Should_ReportTheWaitErrorsSensitivity_When_EveryOffsetShiftsByTwoSeconds()
    {
        // Minute 18:k holds a mean wait of 5 + k: the curve rises one minute a minute, so a 2-second shift moves the system's
        // wait by 2 / 60 minutes either way; the tracer's own wait does not move.
        var minutes = Enumerable.Range(0, 21).Select(k => Minute(At(18, k), 5 + k)).ToArray();
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 20), At(18, 15, 20))],
            QueueMinutes = minutes,
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        var run = Tracer(result, 1);
        run.SystemWaitMinutes.Should().BeApproximately(10 - (10.0 / 60), 1e-12);
        run.ErrorMinutes.Should().BeApproximately(-10.0 / 60, 1e-12);
        run.ErrorOffsetMinusMinutes.Should().BeApproximately(-8.0 / 60, 1e-12, "lower offsets put the corrected join 2 seconds later");
        run.ErrorOffsetPlusMinutes.Should().BeApproximately(-12.0 / 60, 1e-12);
        var sensitivity = ZoneOf(result, Vis).Sensitivity;
        sensitivity.Shift.Should().Be(TimeSpan.FromSeconds(2));
        (sensitivity.Runs, sensitivity.WithinOffsetMinus, sensitivity.Within, sensitivity.WithinOffsetPlus).Should().Be((1, 1, 1, 1));
        sensitivity.BiasOffsetMinus.Should().BeApproximately(-8.0 / 600, 1e-12);
        sensitivity.Bias.Should().BeApproximately(-10.0 / 600, 1e-12);
        sensitivity.BiasOffsetPlus.Should().BeApproximately(-12.0 / 600, 1e-12);
        sensitivity.LargestChangeMinutes.Should().BeApproximately(2.0 / 60, 1e-12);
    }

    [Fact]
    public void Compare_Should_CountTheSensitivityOnlyOverRunsKnownAtEveryShift_When_AShiftReachesAMinuteNotFinal()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 59), At(18, 13, 59)), Run(2, At(18, 8, 0), At(18, 17, 0))],
            QueueMinutes = [Minute(At(18, 5), 8), Minute(At(18, 6), 8, status: BinStatus.Provisional), Minute(At(18, 8), 9), Minute(At(18, 7), 9)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        }, new ComparisonSettings { SensitivityShift = TimeSpan.FromSeconds(3) });

        Tracer(result, 1).ErrorMinutes.Should().Be(0);
        Tracer(result, 1).ErrorOffsetMinusMinutes.Should().BeNull("the join 3 seconds later falls in a provisional minute");
        Tracer(result, 1).ErrorOffsetPlusMinutes.Should().Be(0);
        Tracer(result, 2).ErrorOffsetPlusMinutes.Should().Be(0, "3 seconds earlier is 18:07:57, a final minute");
        var sensitivity = ZoneOf(result, Vis).Sensitivity;
        sensitivity.Runs.Should().Be(1);
        sensitivity.Shift.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Compare_Should_ReportTheLargestChangeAndEachShiftsVerdicts_When_TheShiftsMoveTheErrorUnequally()
    {
        // Joined 18:04:31, a second after the 18:04 centre: later joins climb towards 18:05's mean, earlier ones meet the
        // missing 18:03 minute and hold 18:04's mean.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 4, 31), At(18, 9, 31)), Run(2, At(18, 5, 40), At(18, 10, 40))],
            QueueMinutes = [Minute(At(18, 4), 6), Minute(At(18, 5), 8), Minute(At(18, 6), 8)],
            QueueBins = [Bin(At(18, 0))]
        });

        var run = Tracer(result, 1);
        run.ErrorMinutes.Should().BeApproximately(1 + (2.0 / 60), 1e-12);
        run.ErrorOffsetMinusMinutes.Should().BeApproximately(1 + (6.0 / 60), 1e-12);
        run.ErrorOffsetPlusMinutes.Should().BeApproximately(1, 1e-12);
        (Tracer(result, 2).ErrorMinutes, Tracer(result, 2).ErrorOffsetMinusMinutes, Tracer(result, 2).ErrorOffsetPlusMinutes).Should()
            .Be(((double?)3, (double?)3, (double?)3), "18:05 and 18:06 hold the same mean");
        var sensitivity = ZoneOf(result, Vis).Sensitivity;
        sensitivity.Runs.Should().Be(2);
        sensitivity.LargestChangeMinutes.Should().BeApproximately(4.0 / 60, 1e-12, "the larger change of either run");
        (sensitivity.WithinOffsetMinus, sensitivity.Within, sensitivity.WithinOffsetPlus).Should().Be((0, 0, 1));
        sensitivity.BiasOffsetMinus.Should().BeApproximately((1 + (6.0 / 60) + 3) / 10, 1e-12);
        sensitivity.BiasOffsetPlus.Should().BeApproximately(0.4, 1e-12);
    }

    [Fact]
    public void Compare_Should_OrderRunsByZoneJoinCodeAndId_When_TheyTie()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns =
            [
                Run(2, At(18, 5), At(18, 10)), Run(1, At(18, 5), At(18, 10), batch: 2), Run(5, At(18, 5), At(18, 10), batch: 3) with { RunId = G(1009) },
                Run(5, At(18, 5), At(18, 10), batch: 4, observer: O2) with { RunId = G(1008) }, Run(3, At(18, 6), At(18, 10), zone: ZoneB, batch: 5)
            ]
        });

        result.Tracers.Select(t => (t.QueueZone, t.TracerCode, t.RunId)).Should().Equal((Cit, "T-03", G(1003)), (Vis, "T-01", G(1001)),
            (Vis, "T-02", G(1002)), (Vis, "T-05", G(1008)), (Vis, "T-05", G(1009)));
    }

    [Fact]
    public void Compare_Should_GiveNoDataInsteadOfDividingByZero_When_ThereIsNothingToCompare()
    {
        var result = Compare(new ComparisonInput { Scope = Scope() });

        result.Problem.Should().BeNull();
        result.CountBins.Should().BeEmpty();
        result.Tracers.Should().BeEmpty();
        result.Observers.Should().BeEmpty();
        result.Batches.Should().BeEmpty();
        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.TracerZones.Concat([result.TracerOverall]).Should().AllSatisfy(z =>
        {
            z.Should().BeEquivalentTo(new
            {
                ProfileVersion = Version, Runs = 0, Compared = 0, Excluded = 0, WithinTolerance = 0, Bias = (double?)null, MeanErrorMinutes = (double?)null,
                LargestAbsoluteErrorMinutes = (double?)null, Abandoned = 0, AbandonedBins = 0, SystemAbandoned = 0L, SystemEntries = 0L
            });
            z.Sensitivity.Should().Be(new WaitSensitivity(TimeSpan.FromSeconds(2), 0, null, null, null, 0, 0, 0, null));
            z.ErrorCheck.Should().Be(new CriterionCheck(null, 1, CriterionVerdict.NoData));
            z.BiasCheck.Should().Be(new CriterionCheck(null, 0.05, CriterionVerdict.NoData));
        });
        result.TrackCompletion.Should().AllSatisfy(z =>
        {
            z.Bins.Should().Be(new StandingTally(0, 0, 0, 4, 0, 0));
            z.ExcludedBins.Should().Be(4);
            z.Unknown.Should().Be(new TrackTotals(4, 0, 0, 0, 0, 0, null));
            z.Check.Should().Be(new CriterionCheck(null, 0.9, CriterionVerdict.NoData));
        });
    }

    #endregion

    #region Track completion

    [Fact]
    public void Compare_Should_PoolTrackCompletionOverGoodBinsAndReportDegradedAndUnknownApart_When_TheZoneWasNotAlwaysHealthy()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            HealthBins =
            [
                Health(At(18, 0), 20, 18), Health(At(18, 15), 10, 10), Health(At(18, 30), 10, 5, abandoned: 2, fragmented: 2, censored: 1)
            ],
            QueueBins = [Bin(At(18, 0)), Bin(At(18, 15)), Bin(At(18, 30), BinQuality.Degraded), Bin(At(18, 45))]
        });

        var zone = result.TrackCompletion.Single(z => z.QueueZone == Vis);
        zone.ProfileVersion.Should().Be(Version);
        zone.Bins.Should().Be(new StandingTally(2, 0, 1, 1, 0, 0));
        zone.ExcludedBins.Should().Be(2);
        zone.Good.Should().BeEquivalentTo(new { Bins = 2, Entered = 30L, Exited = 28L, Abandoned = 0L, Fragmented = 0L, Censored = 0L });
        zone.Good.Rate.Should().BeApproximately(28.0 / 30, 1e-12, "pooled, not the mean of 0.9 and 1.0");
        zone.Degraded.Should().Be(new TrackTotals(1, 10, 5, 2, 2, 1, 0.5));
        zone.Unknown.Should().Be(new TrackTotals(1, 0, 0, 0, 0, 0, null), "the 18:45 bin has no stored row");
        zone.Check.Verdict.Should().Be(CriterionVerdict.Pass);
        result.TrackCompletion.Select(z => z.QueueZone).Should().Equal(Cit, Vis);
        result.TrackCompletion.Single(z => z.QueueZone == Cit).Bins.Should().Be(new StandingTally(0, 0, 0, 4, 0, 0));
    }

    [Theory]
    [InlineData(20, 17, CriterionVerdict.Fail)]
    [InlineData(20, 18, CriterionVerdict.Pass)]
    [InlineData(0, 0, CriterionVerdict.NoData)]
    public void Compare_Should_JudgeTrackCompletionAgainstNinetyPercent_When_TheBinsAreGood(long entered, long exited, CriterionVerdict verdict)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(18, 15))),
            HealthBins = [Health(At(18, 0), entered, exited)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.TrackCompletion.Single(z => z.QueueZone == Vis).Check.Verdict.Should().Be(verdict);
    }

    [Fact]
    public void Compare_Should_TakeTheLatestRevisionAndCountProvisionalAndOtherVersionBinsApart_When_TheHealthWasRecomputed()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            HealthBins =
            [
                Health(At(18, 0), 10, 5, BinStatus.Provisional), Health(At(18, 0), 10, 9, revision: 2), Health(At(18, 15), 10, 9, BinStatus.Provisional),
                Health(At(18, 30), 10, 9, version: 6), Health(At(18, 45), 10, 9)
            ],
            QueueBins = [Bin(At(18, 0), revision: 2), Bin(At(18, 15)), Bin(At(18, 30)), Bin(At(18, 45), version: 6)]
        });

        var zone = result.TrackCompletion.Single(z => z.QueueZone == Vis);
        zone.Bins.Should().Be(new StandingTally(1, 0, 0, 0, 1, 2));
        zone.Good.Should().Be(new TrackTotals(1, 10, 9, 0, 0, 0, 0.9));
        zone.Check.Verdict.Should().Be(CriterionVerdict.Pass);
    }

    [Theory]
    [InlineData(0, 0, 60, 4)]
    [InlineData(0, 1, 60, 3)]
    [InlineData(5, 0, 60, 3)]
    [InlineData(14, 59, 60, 3)]
    [InlineData(15, 0, 60, 3)]
    [InlineData(0, 0, 45, 3)]
    [InlineData(0, 0, 46, 4)]
    public void Compare_Should_TakeTheBinsStartingInsideAWindow_When_ItsEdgesAreOffTheQuarterHour(int fromMinute, int fromSecond, int toMinute, int bins)
    {
        var result = Compare(new ComparisonInput { Scope = Scope(new UtcWindow(At(18, fromMinute, fromSecond), At(18, 0).AddMinutes(toMinute))) });

        result.TrackCompletion.Single(z => z.QueueZone == Vis).Bins.Unknown.Should().Be(bins);
    }

    [Fact]
    public void Compare_Should_WaitForTheStoredBin_When_ItsHealthIsFinalButTheBinIsNot()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(18, 15))),
            HealthBins = [Health(At(18, 0), 10, 9)],
            QueueBins = [Bin(At(18, 0), status: BinStatus.Provisional)]
        });

        result.TrackCompletion.Single(z => z.QueueZone == Vis).Bins.Should().Be(new StandingTally(0, 0, 0, 0, 1, 0));
    }

    [Fact]
    public void Compare_Should_UseRowsOfVersionAndRevisionZero_When_TheCampaignsVersionIsZero()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope() with { ProfileVersion = 0 },
            ManualCounts = [Count(EntryA, At(18, 0), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 96, version: 0)],
            TracerRuns = [Run(1, At(18, 5, 30), At(18, 13, 30))],
            QueueMinutes = [Minute(At(18, 5), 8.9, version: 0)],
            QueueBins = [Bin(At(18, 0), version: 0, revision: 0)],
            HealthBins = [Health(At(18, 0), 10, 9, version: 0, revision: 0)]
        });

        result.Problem.Should().BeNull();
        result.ProfileVersion.Should().Be(0);
        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.CountBins.Single().Accuracy.Should().BeApproximately(0.96, 1e-12);
        (Tracer(result, 1).Standing, Tracer(result, 1).SystemWaitMinutes).Should().Be((ComparisonStanding.Good, (double?)8.9));
        result.TrackCompletion.Single(z => z.QueueZone == Vis).Good.Should().Be(new TrackTotals(1, 10, 9, 0, 0, 0, 0.9));
    }

    [Fact]
    public void Compare_Should_TakeTheFirstInstantOf2000_When_ARowOrWindowStartsThere()
    {
        var start = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(start, start.AddMinutes(15))),
            ManualCounts = [Count(EntryA, start, 10)],
            QueueBins = [Bin(start)],
            QualityIntervals = [new QualityInterval(Vis, start, start.AddMinutes(1), BinQuality.Degraded)]
        });

        result.Problem.Should().BeNull();
        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.CountBins.Single().Standing.Should().Be(ComparisonStanding.Degraded);
    }

    [Fact]
    public void Compare_Should_CountEveryBinOfALongDay_When_TheClocksGoBack()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var windows = ComparisonScope.WindowsOf([new DateOnly(2026, 10, 25)], london);
        var result = Compare(new ComparisonInput { Scope = Scope([.. windows]) });

        result.TrackCompletion.Single(z => z.QueueZone == Vis).Bins.Unknown.Should().Be(100, "a 25-hour day has 100 bins");
    }

    #endregion

    #region Guards and scope

    /// <summary>Unusable manual counts, and whether the key is valid (listed unusable) or not (no such key: only counted).</summary>
    public static TheoryData<string, ManualCountRow, bool> UnusableCounts => new()
    {
        { "null", null, false },
        { "a line out of scope", Count(G(999), At(18, 0), 1), false },
        { "a bin off the quarter hour", Count(EntryA, At(18, 7), 1), false },
        { "a bin that is not UTC", Count(EntryA, DateTime.SpecifyKind(At(18, 0), DateTimeKind.Unspecified), 1), true },
        { "a bin before 2000", Count(EntryA, new DateTime(1999, 12, 31, 23, 45, 0, DateTimeKind.Utc), 1), false },
        { "a bin before the planned days", Count(EntryA, At(17, 45), 1), false },
        { "a bin starting where the planned days end", Count(EntryA, At(19, 0), 1), false },
        { "no observer", Count(EntryA, At(18, 0), 1, observer: Guid.Empty), false },
        { "revision 0", Count(EntryA, At(18, 0), 1, revision: 0), false },
        { "crossings in below 0", Count(EntryA, At(18, 0), -1), true },
        { "crossings out below 0", Count(EntryA, At(18, 0), 1, -1), true },
        { "crossings above the bound", Count(EntryA, At(18, 0), 10_001), true }
    };

    [Theory]
    [MemberData(nameof(UnusableCounts))]
    public void Compare_Should_LeaveOutAndCountAManualCount_When_ItIsNotUsable(string because, ManualCountRow row, bool listed)
    {
        var result = Compare(new ComparisonInput { Scope = Scope(), ManualCounts = [row, Count(EntryA, At(18, 15), 5)], QueueBins = [.. Bins(At(18, 0), At(18, 30))] });

        (result.LeftOut.ManualCounts, result.LeftOut.Total).Should().Be((1, 1), because);
        result.LeftOut.UnusableKeys.Should().Equal(listed ? [Key(UnusableKeyKind.ManualCount, At(18, 0), line: "A-VIS entry", observer: O1)] : [], because);
        result.CountBins.Should().ContainSingle(because).Which.BinStartUtc.Should().Be(At(18, 15));
    }

    public static TheoryData<string, TracerRunRow> UnusableRuns => new()
    {
        { "null", null },
        { "no run id", Run(1, At(18, 5), At(18, 10)) with { RunId = Guid.Empty } },
        { "no batch id", Run(1, At(18, 5), At(18, 10)) with { BatchId = Guid.Empty } },
        { "no observer", Run(1, At(18, 5), At(18, 10)) with { ObserverId = Guid.Empty } },
        { "a zone out of scope", Run(1, At(18, 5), At(18, 10), zone: G(999)) },
        { "a name for a code", Run(1, At(18, 5), At(18, 10)) with { TracerCode = "Ahmad" } },
        { "a code with a line feed", Run(1, At(18, 5), At(18, 10)) with { TracerCode = "T-07\n" } },
        { "no code", Run(1, At(18, 5), At(18, 10)) with { TracerCode = null } },
        { "a join that is not UTC", Run(1, At(18, 5), At(18, 10)) with { JoinedUtc = DateTime.SpecifyKind(At(18, 5), DateTimeKind.Local) } },
        { "a raw exit that is not UTC", Run(1, At(18, 5), At(18, 10)) with { ExitedRawUtc = DateTime.SpecifyKind(At(18, 10), DateTimeKind.Unspecified) } },
        { "an offset beyond five minutes", Run(1, At(18, 5), At(18, 10), offsetMs: 300_001) },
        { "the least offset there is", Run(1, At(18, 5), At(18, 10)) with { ClockOffsetMs = int.MinValue } },
        { "a join not corrected by the offset", Run(1, At(18, 5), At(18, 10), offsetMs: 1_000) with { JoinedRawUtc = At(18, 5) } },
        { "an exit not corrected by the offset", Run(1, At(18, 5), At(18, 10), offsetMs: 1_000) with { ExitedRawUtc = At(18, 10) } },
        { "an exit before the join", Run(1, At(18, 10), At(18, 5)) },
        { "an exit at the join", Run(1, At(18, 5), At(18, 5)) },
        { "a run longer than three hours", Run(1, At(18, 5), At(21, 5, 1)) },
        { "a join before the planned days", Run(1, At(17, 59, 59), At(18, 5)) },
        { "a join where the planned days end", Run(1, At(19, 0), At(19, 5)) },
        { "a run in 1999", Run(1, At(18, 5), At(18, 10)) with { JoinedRawUtc = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), JoinedUtc = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc) } },
        { "a run in the year 3000", Run(1, At(18, 5), At(18, 10)) with { ExitedRawUtc = new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc), ExitedUtc = new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc) } }
    };

    [Theory]
    [MemberData(nameof(UnusableRuns))]
    public void Compare_Should_LeaveOutAndCountATracerRun_When_ItIsNotUsable(string because, TracerRunRow row)
    {
        var result = Compare(new ComparisonInput { Scope = Scope(), TracerRuns = [row, Run(2, At(18, 20), At(18, 30), batch: 2)] });

        result.LeftOut.Should().Be(LeftOutInputs.None with { TracerRuns = 1 }, because);
        result.Tracers.Should().ContainSingle(because).Which.TracerCode.Should().Be("T-02");
    }

    [Fact]
    public void Compare_Should_RunWithTheLongestRunAndTheLargestOffset_When_TheyAreAtTheirBounds()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5), At(21, 5), offsetMs: 300_000), Run(2, At(18, 6), At(18, 11), offsetMs: -300_000, batch: 2)]
        });

        result.LeftOut.Should().Be(LeftOutInputs.None, "a run whose join is planned may end after the planned days");
        result.Tracers.Select(t => t.TracerWaitMinutes).Should().Equal(180, 5);
    }

    [Fact]
    public void Compare_Should_UseNoRunOfABatch_When_ItsRunsDisagreeOnTheObserverOrOffset()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns =
            [
                Run(1, At(18, 1), At(18, 9), offsetMs: 100, batch: 1), Run(2, At(18, 2), At(18, 10), offsetMs: 200, batch: 1),
                Run(3, At(18, 3), At(18, 11), batch: 2), Run(4, At(18, 4), At(18, 12), batch: 2, observer: O2),
                Run(5, At(18, 5), At(18, 13), batch: 3), Run(6, At(18, 6), At(18, 14), batch: 3)
            ]
        });

        result.LeftOut.TracerRuns.Should().Be(4);
        result.Tracers.Select(t => t.TracerCode).Should().Equal("T-05", "T-06");
        result.Batches.Should().ContainSingle().Which.Runs.Should().Be(2);
    }

    [Fact]
    public void Compare_Should_KeepOneCopyAndLeaveConflictsOut_When_RowsShareAKey()
    {
        var run = Run(1, At(18, 5, 30), At(18, 13, 30));
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 15), 10), Count(EntryA, At(18, 15), 11)],
            TracerRuns = [run, run, Run(2, At(18, 6), At(18, 16)), Run(2, At(18, 6), At(18, 17))],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 96), LineBin("A-VIS entry", At(18, 0), 96), LineBin("A-VIS entry", At(18, 15), 1), LineBin("A-VIS entry", At(18, 15), 2)],
            QueueMinutes = [Minute(At(18, 5), 8.9), Minute(At(18, 5), 8.9), Minute(At(18, 6), 1), Minute(At(18, 6), 2)],
            QueueBins = [Bin(At(18, 0)), Bin(At(18, 0)), Bin(At(18, 15)), Bin(At(18, 15), BinQuality.Degraded)],
            HealthBins = [Health(At(18, 0), 10, 9), Health(At(18, 0), 10, 9), Health(At(18, 15), 10, 9), Health(At(18, 15), 10, 8)]
        });

        result.LeftOut.Should().Be(new LeftOutInputs(3, 3, 3, 3, 3, 3, 0)
        {
            UnusableKeys =
            [
                Key(UnusableKeyKind.ManualCount, At(18, 15), UnusableKeyReason.Conflicting, "A-VIS entry", O1),
                Key(UnusableKeyKind.LineBin, At(18, 15), UnusableKeyReason.Conflicting, "A-VIS entry"),
                Key(UnusableKeyKind.QueueMinute, At(18, 6), UnusableKeyReason.Conflicting),
                Key(UnusableKeyKind.QueueBin, At(18, 15), UnusableKeyReason.Conflicting),
                Key(UnusableKeyKind.HealthBin, At(18, 15), UnusableKeyReason.Conflicting)
            ]
        });
        result.LeftOut.Should().NotBe(new LeftOutInputs(3, 3, 3, 3, 3, 3, 0), "the unusable keys are part of what was left out");
        result.LeftOut.Total.Should().Be(18);
        new LeftOutInputs(1, 2, 3, 4, 5, 6, 7).Total.Should().Be(28);
        new StandingTally(1, 2, 3, 4, 5, 6).Total.Should().Be(21);
        result.CountBins.Should().ContainSingle().Which.Accuracy.Should().BeApproximately(0.96, 1e-12);
        result.Tracers.Should().ContainSingle().Which.SystemWaitMinutes.Should().Be(8.9);
        var zone = result.TrackCompletion.Single(z => z.QueueZone == Vis);
        zone.Good.Should().Be(new TrackTotals(1, 10, 9, 0, 0, 0, 0.9));
        zone.Bins.Unknown.Should().Be(3, "18:15 has conflicting stored bins and 18:30 and 18:45 none");
    }

    /// <summary>
    /// Unusable line bins, and whether the row is the line's Ariva row of the campaign's version (its crossings are then not
    /// known: the item Unknown without N_system, the key listed) or not one at all (a vendor's, out of scope, off the bin: the
    /// line has no Ariva row there and crossed 0 times).
    /// </summary>
    public static TheoryData<string, LineBinCount, bool> UnusableLineBins => new()
    {
        { "null", null, false },
        { "a vendor's count", LineBin("A-VIS entry", At(18, 0), 1, source: LineCountSource.Vendor), false },
        { "a line out of scope", LineBin("A-VIS side door", At(18, 0), 1), false },
        { "another zone's line", LineBin("A-CIT entry", At(18, 0), 1), false },
        { "a zone out of scope", LineBin("A-VIS entry", At(18, 0), 1, zone: "A-XYZ"), false },
        { "a bin off the quarter hour", LineBin("A-VIS entry", At(18, 1), 1), false },
        { "a bin after 2999", LineBin("A-VIS entry", new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1), false },
        { "a version below 0", LineBin("A-VIS entry", At(18, 0), 1, version: -1), false },
        { "an undefined source", LineBin("A-VIS entry", At(18, 0), 1, source: (LineCountSource)9), false },
        { "a bin that is not UTC", LineBin("A-VIS entry", Unspecified(At(18, 0)), 1), true },
        { "crossings in below 0", LineBin("A-VIS entry", At(18, 0), -1), true },
        { "crossings out below 0", LineBin("A-VIS entry", At(18, 0), 1, -1), true },
        { "crossings above the bound", LineBin("A-VIS entry", At(18, 0), 1_000_001), true },
        { "crossings out above the bound", LineBin("A-VIS entry", At(18, 0), 1, 1_000_001), true }
    };

    [Theory]
    [MemberData(nameof(UnusableLineBins))]
    public void Compare_Should_LeaveOutAndCountALineBin_When_ItIsNotUsable(string because, LineBinCount row, bool campaignRow)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 10)],
            LineBins = [row],
            QueueBins = [Bin(At(18, 0))]
        });

        (result.LeftOut.LineBins, result.LeftOut.Total).Should().Be((1, 1), because);
        result.LeftOut.UnusableKeys.Should().Equal(campaignRow ? [Key(UnusableKeyKind.LineBin, At(18, 0), line: "A-VIS entry")] : [], because);
        var item = result.CountBins.Single();
        (item.Standing, item.SystemCount, item.AbsoluteError).Should().Be(campaignRow
            ? (ComparisonStanding.Unknown, (long?)null, (double?)null)
            : (ComparisonStanding.Good, 0L, 10.0), because);
    }

    [Fact]
    public void Compare_Should_TakeTheLargestCount_When_ItIsAtTheBound()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 10_000)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 1_000_000, 1_000_000)],
            QueueBins = [Bin(At(18, 0), entries: 1_000_000, abandoned: 1_000_000)]
        });

        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.CountBins.Single().SystemCount.Should().Be(1_000_000);
    }

    /// <summary>
    /// Unusable queue minutes, and whether the row is the join minute's own (a valid key with a value that cannot be: the minute
    /// is unusable, the run Unknown, the key listed) or not one at all (no row for the join minute: NoSystemWait).
    /// </summary>
    public static TheoryData<string, QueueMinuteRow, bool> UnusableMinutes => new()
    {
        { "null", null, false },
        { "a zone out of scope", Minute(At(18, 5), 8, zone: "A-XYZ"), false },
        { "a minute with seconds", Minute(At(18, 5, 1), 8), false },
        { "a minute that is not UTC", Minute(DateTime.SpecifyKind(At(18, 5), DateTimeKind.Local), 8), true },
        { "a minute before 2000", Minute(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), 8), false },
        { "a version below 0", Minute(At(18, 5), 8, version: -1), true },
        { "waits below 0", Minute(At(18, 5), null, waits: -1), true },
        { "waits above the bound", Minute(At(18, 5), 8, waits: 1_000_001), true },
        { "waits without a mean", Minute(At(18, 5), null, waits: 5), true },
        { "a mean without waits", Minute(At(18, 5), 8, waits: 0), true },
        { "a mean that is not a number", Minute(At(18, 5), double.NaN), true },
        { "an infinite mean", Minute(At(18, 5), double.PositiveInfinity), true },
        { "a mean below 0", Minute(At(18, 5), -0.1), true },
        { "a mean above a day", Minute(At(18, 5), 1440.001), true },
        { "an undefined status", Minute(At(18, 5), 8, status: (BinStatus)9), true }
    };

    [Theory]
    [MemberData(nameof(UnusableMinutes))]
    public void Compare_Should_LeaveOutAndCountAQueueMinute_When_ItIsNotUsable(string because, QueueMinuteRow row, bool joinMinute)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 30), At(18, 13, 30))],
            QueueMinutes = [row],
            QueueBins = [Bin(At(18, 0))]
        });

        (result.LeftOut.QueueMinutes, result.LeftOut.Total).Should().Be((1, 1), because);
        result.LeftOut.UnusableKeys.Should().Equal(joinMinute ? [Key(UnusableKeyKind.QueueMinute, At(18, 5))] : [], because);
        Tracer(result, 1).Standing.Should().Be(joinMinute ? ComparisonStanding.Unknown : ComparisonStanding.NoSystemWait, because);
        Tracer(result, 1).SystemWaitMinutes.Should().BeNull(because);
    }

    [Fact]
    public void Compare_Should_TakeAMeanOfADay_When_ItIsAtTheBound()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 5, 30), At(18, 13, 30))],
            QueueMinutes = [Minute(At(18, 5), 1440, waits: 1_000_000), Minute(At(18, 6), 0, waits: 1)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.LeftOut.Should().Be(LeftOutInputs.None);
        Tracer(result, 1).SystemWaitMinutes.Should().Be(1440);
    }

    /// <summary>Unusable queue bins, and whether the key (zone, start, revision) is valid (the key listed unusable) or not (only counted).</summary>
    public static TheoryData<string, QueueBinRow, bool> UnusableBins => new()
    {
        { "null", null, false },
        { "a zone out of scope", Bin(At(18, 0), zone: "A-XYZ"), false },
        { "a start off the quarter hour", Bin(At(18, 5)), false },
        { "a revision below 0", Bin(At(18, 0), revision: -1), false },
        { "a start that is not UTC", Bin(Unspecified(At(18, 0))), true },
        { "an hour long", Bin(At(18, 0)) with { Length = TimeSpan.FromHours(1) }, true },
        { "an undefined status", Bin(At(18, 0), status: (BinStatus)9), true },
        { "an undefined quality", Bin(At(18, 0), quality: (BinQuality)9), true },
        { "a version below 0", Bin(At(18, 0), version: -1), true },
        { "entries below 0", Bin(At(18, 0), entries: -1), true },
        { "entries above the bound", Bin(At(18, 0), entries: 1_000_001), true },
        { "abandoned below 0", Bin(At(18, 0), abandoned: -1), true },
        { "more abandoned than entries", Bin(At(18, 0), entries: 5, abandoned: 6), true }
    };

    [Theory]
    [MemberData(nameof(UnusableBins))]
    public void Compare_Should_LeaveOutAndCountAQueueBin_When_ItIsNotUsable(string because, QueueBinRow row, bool listed)
    {
        var result = Compare(new ComparisonInput { Scope = Scope(), ManualCounts = [Count(EntryA, At(18, 0), 10)], QueueBins = [row] });

        (result.LeftOut.QueueBins, result.LeftOut.Total).Should().Be((1, 1), because);
        result.LeftOut.UnusableKeys.Should().Equal(listed ? [Key(UnusableKeyKind.QueueBin, At(18, 0))] : [], because);
        result.CountBins.Single().Standing.Should().Be(ComparisonStanding.Unknown, because);
    }

    /// <summary>Unusable zone health bins, and whether the key (zone, start, revision) is valid (the key listed unusable) or not (only counted).</summary>
    public static TheoryData<string, ZoneHealthBin, bool> UnusableHealth => new()
    {
        { "null", null, false },
        { "a zone out of scope", Health(At(18, 0), 10, 9, zone: "A-XYZ"), false },
        { "a start off the quarter hour", Health(At(18, 0), 10, 9) with { StartUtc = At(18, 14) }, false },
        { "a start that is not UTC", Health(At(18, 0), 10, 9) with { StartUtc = DateTime.SpecifyKind(At(18, 0), DateTimeKind.Unspecified) }, true },
        { "a revision below 0", Health(At(18, 0), 10, 9, revision: -1), false },
        { "five minutes long", Health(At(18, 0), 10, 9) with { Length = TimeSpan.FromMinutes(5) }, true },
        { "an undefined status", Health(At(18, 0), 10, 9, status: (BinStatus)9), true },
        { "a version below 0", Health(At(18, 0), 10, 9, version: -1), true },
        { "tracks entered below 0", Health(At(18, 0), -1, 0), true },
        { "tracks entered above the bound", Health(At(18, 0), 1_000_001, 0), true },
        { "more exited than entered", Health(At(18, 0), 10, 11), true },
        { "exited below 0", Health(At(18, 0), 10, -1), true },
        { "more abandoned than entered", Health(At(18, 0), 10, 0, abandoned: 11), true },
        { "abandoned below 0", Health(At(18, 0), 10, 0, abandoned: -1), true },
        { "more fragmented than entered", Health(At(18, 0), 10, 0, fragmented: 11), true },
        { "fragmented below 0", Health(At(18, 0), 10, 0, fragmented: -1), true },
        { "more censored than entered", Health(At(18, 0), 10, 0, censored: 11), true },
        { "censored below 0", Health(At(18, 0), 10, 0, censored: -1), true }
    };

    [Theory]
    [MemberData(nameof(UnusableHealth))]
    public void Compare_Should_LeaveOutAndCountAZoneHealthBin_When_ItIsNotUsable(string because, ZoneHealthBin row, bool listed)
    {
        var result = Compare(new ComparisonInput { Scope = Scope(new UtcWindow(At(18, 0), At(18, 15))), HealthBins = [row], QueueBins = [Bin(At(18, 0))] });

        (result.LeftOut.HealthBins, result.LeftOut.Total).Should().Be((1, 1), because);
        result.LeftOut.UnusableKeys.Should().Equal(listed ? [Key(UnusableKeyKind.HealthBin, At(18, 0))] : [], because);
        result.TrackCompletion.Single(z => z.QueueZone == Vis).Bins.Should().Be(new StandingTally(0, 0, 0, 1, 0, 0), because);
    }

    /// <summary>Intervals that touch nothing compared: left out and counted, the zone's results unchanged.</summary>
    public static TheoryData<string, QualityInterval> UnusableIntervals => new()
    {
        { "null", null },
        { "a zone out of scope", new QualityInterval("A-XYZ", At(18, 0), At(18, 5), BinQuality.Degraded) },
        { "a zone out of scope, not UTC", new QualityInterval("A-XYZ", Unspecified(At(18, 0)), At(18, 5), (BinQuality)9) },
        { "a Good interval", new QualityInterval(Vis, At(18, 0), At(18, 5), BinQuality.Good) },
        { "wholly before 2000", new QualityInterval(Vis, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1999, 6, 1, 0, 0, 0, DateTimeKind.Utc), BinQuality.Degraded) },
        { "from the year 3000 on", new QualityInterval(Vis, new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.MaxValue, BinQuality.Degraded) }
    };

    [Theory]
    [MemberData(nameof(UnusableIntervals))]
    public void Compare_Should_LeaveOutAndCountAQualityInterval_When_ItIsNotUsable(string because, QualityInterval row)
    {
        var result = Compare(new ComparisonInput { Scope = Scope(), ManualCounts = [Count(EntryA, At(18, 0), 10)], QueueBins = [Bin(At(18, 0))], QualityIntervals = [row] });

        result.LeftOut.Should().Be(LeftOutInputs.None with { QualityIntervals = 1 }, because);
        result.CountBins.Single().Standing.Should().Be(ComparisonStanding.Good, because);
    }

    [Theory]
    [InlineData(18, 14, 18, 15, ComparisonStanding.Degraded)]
    [InlineData(18, 15, 18, 20, ComparisonStanding.Good)]
    [InlineData(17, 50, 18, 0, ComparisonStanding.Good)]
    [InlineData(17, 59, 18, 1, ComparisonStanding.Degraded)]
    public void Compare_Should_DegradeABinOnlyWhereAnIntervalOverlapsIt_When_AnOutageTouchesItsEdges(int fromHour, int fromMinute, int toHour, int toMinute,
        ComparisonStanding standing)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 10)],
            QueueBins = [Bin(At(18, 0))],
            QualityIntervals = [new QualityInterval(Vis, At(fromHour, fromMinute), At(toHour, toMinute), BinQuality.Degraded)]
        });

        result.CountBins.Single().Standing.Should().Be(standing);
    }

    [Fact]
    public void Compare_Should_FindEveryOverlap_When_IntervalsNestOverlapOrTouch()
    {
        // Degraded 18:05 to 18:10 inside 18:00 to 18:40 (given first) and 18:40 to 18:41 touching it; Unknown 18:52 to 18:53 and
        // 18:50 to 18:52:30 overlapping; Degraded 19:14 to 19:15, inside the 19:00 bin and ending where the 19:15 bin starts; the
        // other zone's interval touches none of these bins.
        var bins = new[] { At(18, 0), At(18, 15), At(18, 30), At(18, 45), At(19, 0), At(19, 15) };
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(19, 30))),
            ManualCounts = [.. bins.Select(b => Count(EntryA, b, 10))],
            QueueBins = [.. Bins(At(18, 0), At(19, 30))],
            QualityIntervals =
            [
                new QualityInterval(Vis, At(18, 5), At(18, 10), BinQuality.Degraded), new QualityInterval(Vis, At(18, 0), At(18, 40), BinQuality.Degraded),
                new QualityInterval(Vis, At(18, 40), At(18, 41), BinQuality.Degraded), new QualityInterval(Vis, At(18, 52), At(18, 53), BinQuality.Unknown),
                new QualityInterval(Vis, At(18, 50), At(18, 52, 30), BinQuality.Unknown), new QualityInterval(Cit, At(19, 0), At(19, 15), BinQuality.Degraded),
                new QualityInterval(Vis, At(19, 14), At(19, 15), BinQuality.Degraded)
            ]
        });

        result.CountBins.Select(i => i.Standing).Should().Equal(ComparisonStanding.Degraded, ComparisonStanding.Degraded, ComparisonStanding.Degraded,
            ComparisonStanding.Unknown, ComparisonStanding.Degraded, ComparisonStanding.Good);
        result.LeftOut.Should().Be(LeftOutInputs.None);
    }

    [Fact]
    public void Compare_Should_MergeOverlappingIntervals_When_OneIsNestedOrExtendsAnother()
    {
        // A-VIS: 18:05 to 18:10 nested in 18:00 to 18:40, so the 18:15 bin overlaps only the outer one. A-CIT: 18:10 to 18:40
        // extends 18:00 to 18:20, so the 18:30 bin overlaps only the extension.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 15), 10), Count(EntryB, At(18, 30), 10)],
            QueueBins = [.. Bins(At(18, 0), At(19, 0)), .. Bins(At(18, 0), At(19, 0), Cit)],
            QualityIntervals =
            [
                new QualityInterval(Vis, At(18, 0), At(18, 40), BinQuality.Degraded), new QualityInterval(Vis, At(18, 5), At(18, 10), BinQuality.Degraded),
                new QualityInterval(Cit, At(18, 0), At(18, 20), BinQuality.Degraded), new QualityInterval(Cit, At(18, 10), At(18, 40), BinQuality.Degraded)
            ]
        });

        result.CountBins.Select(i => (i.QueueZone, i.Standing)).Should().Equal((Cit, ComparisonStanding.Degraded), (Vis, ComparisonStanding.Degraded));
    }

    [Fact]
    public void Compare_Should_TreatNullListsAsEmpty_When_TheCallerLeavesThemOut()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(), ManualCounts = null, TracerRuns = null, LineBins = null, QueueMinutes = null, QueueBins = null, HealthBins = null, QualityIntervals = null
        });

        result.Problem.Should().BeNull();
        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.Lines.Should().HaveCount(5);
    }

    public static TheoryData<string> InvalidScopes =>
    [
        "no scope", "a version below 0", "no zones", "no lines", "no windows", "a null zone", "a zone without an id", "a zone without a name", "a zone twice",
        "a zone name twice", "a null line", "a line without an id", "a line without a name", "a line of an undefined role", "a line without a zone",
        "a line of a zone out of scope", "a line twice", "a line name twice in a zone", "a null window", "a window ending at its start",
        "a window ending before it starts", "a window that is not UTC", "a window before 2000", "a window into the year 3000", "a valid and an inverted window"
    ];

    private static ComparisonScope InvalidScope(string name)
    {
        var scope = Scope();
        var zone = new ScopeZone(ZoneA, Vis);
        var line = new ScopeLine(EntryA, "A-VIS entry", LineRole.Entry, Vis);
        return name switch
        {
            "no scope" => null,
            "a version below 0" => scope with { ProfileVersion = -1 },
            "no zones" => scope with { Zones = null },
            "no lines" => scope with { Lines = null },
            "no windows" => scope with { Windows = null },
            "a null zone" => scope with { Zones = [zone, null] },
            "a zone without an id" => scope with { Zones = [zone with { ZoneId = Guid.Empty }] },
            "a zone without a name" => scope with { Zones = [zone, new ScopeZone(ZoneB, "")] },
            "a zone twice" => scope with { Zones = [zone, new ScopeZone(ZoneA, Cit)] },
            "a zone name twice" => scope with { Zones = [zone, new ScopeZone(ZoneB, Vis)] },
            "a null line" => scope with { Lines = [line, null] },
            "a line without an id" => scope with { Lines = [line with { LineId = Guid.Empty }] },
            "a line without a name" => scope with { Lines = [line with { Name = null }] },
            "a line of an undefined role" => scope with { Lines = [line with { Role = (LineRole)9 }] },
            "a line without a zone" => scope with { Lines = [line with { QueueZone = null }] },
            "a line of a zone out of scope" => scope with { Lines = [line with { QueueZone = "A-XYZ" }] },
            "a line twice" => scope with { Lines = [line, line with { Name = "A-VIS other" }] },
            "a line name twice in a zone" => scope with { Lines = [line, line with { LineId = ExitA }] },
            "a null window" => scope with { Windows = [null] },
            "a window ending at its start" => scope with { Windows = [new UtcWindow(At(18, 0), At(18, 0))] },
            "a window ending before it starts" => scope with { Windows = [new UtcWindow(At(18, 0), At(17, 0))] },
            "a window that is not UTC" => scope with { Windows = [new UtcWindow(DateTime.SpecifyKind(At(18, 0), DateTimeKind.Local), At(19, 0))] },
            "a window before 2000" => scope with { Windows = [new UtcWindow(new DateTime(1999, 12, 31, 23, 0, 0, DateTimeKind.Utc), At(19, 0))] },
            "a window into the year 3000" => scope with { Windows = [new UtcWindow(At(18, 0), new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc))] },
            "a valid and an inverted window" => scope with { Windows = [new UtcWindow(At(18, 0), At(19, 0)), new UtcWindow(At(20, 0), At(19, 30))] },
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    [Theory]
    [MemberData(nameof(InvalidScopes))]
    public void Compare_Should_CompareNothing_When_TheScopeIsInvalid(string name)
    {
        var result = Compare(new ComparisonInput { Scope = InvalidScope(name), ManualCounts = [Count(EntryA, At(18, 0), 10)], QueueBins = [Bin(At(18, 0))] });

        result.Problem.Should().Be(ComparisonProblem.InvalidScope, name);
        result.Should().BeEquivalentTo(new
        {
            CountBins = Array.Empty<LineBinAccuracy>(), Lines = Array.Empty<LineAccuracy>(), Tracers = Array.Empty<TracerComparison>(),
            TracerZones = Array.Empty<TracerZoneSummary>(), TracerOverall = (TracerZoneSummary)null, TrackCompletion = Array.Empty<ZoneTrackCompletion>(),
            Observers = Array.Empty<ObserverOffsetSpread>(), Batches = Array.Empty<BatchOffset>(), LeftOut = LeftOutInputs.None
        });
        result.ProfileVersion.Should().Be(name switch { "no scope" => 0, "a version below 0" => -1, _ => Version });
    }

    public static TheoryData<string> OversizedScopes =>
    [
        "51 zones", "201 lines", "63 windows", "3,201 bins", "3,201 bins in two windows", "a million and one counts", "a million and one runs",
        "a million and one line bins", "a million and one minutes", "a million and one bins", "a million and one health bins", "a million and one intervals",
        "a million and one counts said to be one", "a million and one intervals said to be none", "51 zones said to be one", "201 lines said to be one",
        "63 windows said to be one"
    ];

    private static ComparisonInput Oversized(string name)
    {
        var input = new ComparisonInput { Scope = Scope() };
        var scope = input.Scope;
        const int over = 1_000_001;
        return name switch
        {
            "51 zones" => input with { Scope = scope with { Zones = [.. Enumerable.Range(0, 51).Select(i => new ScopeZone(G(5000 + i), $"Z{i}"))], Lines = [] } },
            "201 lines" => input with { Scope = scope with { Lines = [.. Enumerable.Range(0, 201).Select(i => new ScopeLine(G(6000 + i), $"L{i}", LineRole.Count, Vis))] } },
            "63 windows" => input with { Scope = scope with { Windows = [.. Enumerable.Range(0, 63).Select(i => new UtcWindow(At(0, 0).AddDays(i), At(0, 15).AddDays(i)))] } },
            "3,201 bins" => input with { Scope = scope with { Windows = [new UtcWindow(At(0, 0), At(0, 0).AddMinutes(15 * 3_201))] } },
            "3,201 bins in two windows" => input with
            {
                Scope = scope with { Windows = [new UtcWindow(At(0, 0), At(0, 0).AddMinutes(15 * 3_000)), new UtcWindow(At(0, 0).AddDays(40), At(0, 0).AddDays(40).AddMinutes(15 * 201))] }
            },
            "a million and one counts" => input with { ManualCounts = Nulls<ManualCountRow>(over) },
            "a million and one runs" => input with { TracerRuns = Nulls<TracerRunRow>(over) },
            "a million and one line bins" => input with { LineBins = Nulls<LineBinCount>(over) },
            "a million and one minutes" => input with { QueueMinutes = Nulls<QueueMinuteRow>(over) },
            "a million and one bins" => input with { QueueBins = Nulls<QueueBinRow>(over) },
            "a million and one health bins" => input with { HealthBins = Nulls<ZoneHealthBin>(over) },
            "a million and one intervals" => input with { QualityIntervals = Nulls<QualityInterval>(over) },
            "a million and one counts said to be one" => input with { ManualCounts = new Listed<ManualCountRow>(1, Enumerable.Repeat(Count(EntryA, At(18, 0), 1), over)) },
            "a million and one intervals said to be none" => input with { QualityIntervals = new Listed<QualityInterval>(0, Enumerable.Repeat<QualityInterval>(null, over)) },
            "51 zones said to be one" => input with
            {
                Scope = scope with { Zones = new Listed<ScopeZone>(1, Enumerable.Range(0, 51).Select(i => new ScopeZone(G(5000 + i), $"Z{i}"))), Lines = [] }
            },
            "201 lines said to be one" => input with
            {
                Scope = scope with { Lines = new Listed<ScopeLine>(1, Enumerable.Range(0, 201).Select(i => new ScopeLine(G(6000 + i), $"L{i}", LineRole.Count, Vis))) }
            },
            "63 windows said to be one" => input with
            {
                Scope = scope with { Windows = new Listed<UtcWindow>(1, Enumerable.Range(0, 63).Select(i => new UtcWindow(At(0, 0).AddDays(i), At(0, 1).AddDays(i)))) }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    [Theory]
    [MemberData(nameof(OversizedScopes))]
    public void Compare_Should_CompareNothing_When_TheInputExceedsItsBounds(string name)
    {
        var result = Compare(Oversized(name));

        result.Problem.Should().Be(ComparisonProblem.InputTooLarge, name);
        result.Lines.Should().BeEmpty();
        result.TracerOverall.Should().BeNull();
    }

    [Fact]
    public void Compare_Should_CompareAtTheBounds_When_TheInputReachesThemExactly()
    {
        var zones = Enumerable.Range(0, 50).Select(i => new ScopeZone(G(5000 + i), $"Z{i:D2}")).ToList();
        var lines = Enumerable.Range(0, 200).Select(i => new ScopeLine(G(6000 + i), $"L{i:D3}", LineRole.Count, "Z00")).ToList();
        var windows = Enumerable.Range(0, 61).Select(i => new UtcWindow(At(0, 0).AddDays(i), At(0, 15).AddDays(i)))
            .Append(new UtcWindow(At(0, 0).AddDays(100), At(0, 0).AddDays(100).AddMinutes(15 * 3_139))).ToList();
        var result = Compare(new ComparisonInput { Scope = new ComparisonScope(Version, zones, lines, windows), ManualCounts = Nulls<ManualCountRow>(1_000_000) });

        result.Problem.Should().BeNull();
        result.LeftOut.ManualCounts.Should().Be(1_000_000, "rows read and not usable are counted");
        result.TrackCompletion.Should().HaveCount(50);
        result.TrackCompletion[0].Bins.Unknown.Should().Be(3_200);
    }

    public static TheoryData<ComparisonSettings, string> InvalidSettings => new()
    {
        { new ComparisonSettings { CountAccuracyTarget = 0 }, "CountAccuracyTarget" },
        { new ComparisonSettings { CountAccuracyTarget = 1.01 }, "CountAccuracyTarget" },
        { new ComparisonSettings { CountAccuracyTarget = double.NaN }, "CountAccuracyTarget" },
        { new ComparisonSettings { WithinToleranceTarget = 0 }, "WithinToleranceTarget" },
        { new ComparisonSettings { WithinToleranceTarget = double.PositiveInfinity }, "WithinToleranceTarget" },
        { new ComparisonSettings { BiasTarget = -0.05 }, "BiasTarget" },
        { new ComparisonSettings { BiasTarget = 1.5 }, "BiasTarget" },
        { new ComparisonSettings { TrackCompletionTarget = 0 }, "TrackCompletionTarget" },
        { new ComparisonSettings { TrackCompletionTarget = 2 }, "TrackCompletionTarget" },
        { new ComparisonSettings { OffsetOutlierBound = TimeSpan.Zero }, "OffsetOutlierBound" },
        { new ComparisonSettings { OffsetOutlierBound = TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1)) }, "OffsetOutlierBound" },
        { new ComparisonSettings { SensitivityShift = TimeSpan.FromTicks(9_999) }, "SensitivityShift" },
        { new ComparisonSettings { SensitivityShift = TimeSpan.FromSeconds(61) }, "SensitivityShift" }
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Compare_Should_Throw_When_TheSettingsAreInvalid(ComparisonSettings settings, string problem)
    {
        settings.Problems().Should().ContainSingle().Which.Should().StartWith(problem);
        FluentActions.Invoking(() => Compare(new ComparisonInput { Scope = Scope() }, settings)).Should().Throw<ArgumentException>().WithMessage($"{problem}*");
    }

    [Fact]
    public void Problems_Should_BeNone_When_TheSettingsAreAtTheirBounds()
    {
        new ComparisonSettings().Problems().Should().BeEmpty();
        new ComparisonSettings
        {
            CountAccuracyTarget = 1, WithinToleranceTarget = 1, BiasTarget = 1, TrackCompletionTarget = 1, OffsetOutlierBound = TimeSpan.FromMinutes(5),
            SensitivityShift = TimeSpan.FromMinutes(1)
        }.Problems().Should().BeEmpty();
        new ComparisonSettings { OffsetOutlierBound = TimeSpan.FromMilliseconds(1), SensitivityShift = TimeSpan.FromMilliseconds(1) }.Problems().Should().BeEmpty();
        FluentActions.Invoking(() => ValidationComparison.Compare(null)).Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Latest revisions and unusable keys (security review, CWE-501)

    public static TheoryData<string, ZoneHealthBin[], UnusableKeyReason, int> UnusableLatestHealth => new()
    {
        { "refused: more abandoned than entered", [Health(At(18, 0), 100, 95), Health(At(18, 0), 100, 80, revision: 2, abandoned: 101)], UnusableKeyReason.Refused, 1 },
        { "conflicting", [Health(At(18, 0), 100, 95), Health(At(18, 0), 100, 80, revision: 2), Health(At(18, 0), 100, 81, revision: 2)], UnusableKeyReason.Conflicting, 2 }
    };

    [Theory]
    [MemberData(nameof(UnusableLatestHealth))]
    public void Compare_Should_NotFallBackToAnEarlierHealthRevision_When_TheLatestIsUnusable(string because, ZoneHealthBin[] health, UnusableKeyReason reason,
        int leftOut)
    {
        // Revision 1 holds 95 of 100 (0.95, a pass); the latest, revision 2, is 80 of 100 (a fail) but cannot be used. Before
        // the review revision 1 took its place and the zone passed.
        var result = Compare(new ComparisonInput { Scope = Scope(new UtcWindow(At(18, 0), At(18, 15))), QueueBins = [Bin(At(18, 0))], HealthBins = health });

        var zone = result.TrackCompletion.Single(z => z.QueueZone == Vis);
        zone.Bins.Should().Be(new StandingTally(0, 0, 0, 1, 0, 0), because);
        zone.Good.Should().Be(TrackTotals.None, because);
        zone.Check.Should().Be(new CriterionCheck(null, 0.9, CriterionVerdict.NoData), because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { HealthBins = leftOut, UnusableKeys = [Key(UnusableKeyKind.HealthBin, At(18, 0), reason)] }, because);
    }

    public static TheoryData<string, QueueBinRow[], UnusableKeyReason, int> UnusableLatestBins => new()
    {
        { "refused: Degraded with more abandoned than entries", [Bin(At(18, 0)), Bin(At(18, 0), BinQuality.Degraded, revision: 2, entries: 5, abandoned: 6)], UnusableKeyReason.Refused, 1 },
        { "conflicting", [Bin(At(18, 0)), Bin(At(18, 0), BinQuality.Degraded, revision: 2), Bin(At(18, 0), revision: 2)], UnusableKeyReason.Conflicting, 2 }
    };

    [Theory]
    [MemberData(nameof(UnusableLatestBins))]
    public void Compare_Should_NotFallBackToAnEarlierQueueBinRevision_When_TheLatestIsUnusable(string because, QueueBinRow[] bins, UnusableKeyReason reason,
        int leftOut)
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 97)],
            QueueBins = bins
        });

        var item = result.CountBins.Single();
        (item.Standing, item.SystemCount).Should().Be((ComparisonStanding.Unknown, (long?)null), because);
        result.Lines.Single(l => l.LineId == EntryA).Check.Verdict.Should().Be(CriterionVerdict.NoData, because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { QueueBins = leftOut, UnusableKeys = [Key(UnusableKeyKind.QueueBin, At(18, 0), reason)] }, because);
    }

    public static TheoryData<string, ManualCountRow[], UnusableKeyReason, int> UnusableLatestCounts => new()
    {
        { "conflicting correction", [Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 0), 120, revision: 2), Count(EntryA, At(18, 0), 121, revision: 2)], UnusableKeyReason.Conflicting, 2 },
        { "refused correction", [Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 0), 10_001, revision: 2)], UnusableKeyReason.Refused, 1 }
    };

    [Theory]
    [MemberData(nameof(UnusableLatestCounts))]
    public void Compare_Should_NotJudgeTheLineAndBin_When_AnObserversLatestCountIsUnusable(string because, ManualCountRow[] counts, UnusableKeyReason reason,
        int leftOut)
    {
        // Observer 1 corrected 100 to 120 (and 121 in a second row): revision 1 (100, accuracy 1 against 100) must not stand in.
        // Observer 2's count of the same line and bin is left out with it: the mean would silently lose observer 1.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [.. counts, Count(EntryA, At(18, 0), 100, observer: O2), Count(EntryA, At(18, 15), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 100), LineBin("A-VIS entry", At(18, 15), 96)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        result.CountBins.Should().ContainSingle(because).Which.BinStartUtc.Should().Be(At(18, 15));
        var line = result.Lines.Single(l => l.LineId == EntryA);
        (line.Judged, line.JudgedBins, line.ExcludedBins).Should().Be((1, 1, 1), because);
        line.LowestAccuracy.Should().BeApproximately(0.96, 1e-12, because);
        result.LeftOut.Should().Be(LeftOutInputs.None with
        {
            ManualCounts = leftOut + 1, UnusableKeys = [Key(UnusableKeyKind.ManualCount, At(18, 0), reason, "A-VIS entry", O1)]
        }, because);
    }

    [Fact]
    public void Compare_Should_JudgeAgainstTheObserversCount_When_ItsLatestCountHasNoConflict()
    {
        // The probe's correction without the conflicting row: 120 against 100 counted by Ariva is 0.83, a fail.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 0), 120, revision: 2)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 100)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.CountBins.Single().Accuracy.Should().BeApproximately(1 - (20.0 / 120), 1e-12);
        result.Lines.Single(l => l.LineId == EntryA).Check.Verdict.Should().Be(CriterionVerdict.Fail);
        result.LeftOut.Should().Be(LeftOutInputs.None);
    }

    [Fact]
    public void Compare_Should_UseTheLatestRevision_When_AnEarlierOneIsRefusedOrConflicting()
    {
        // Superseded revisions are never used: one that cannot be, or two that disagree, are counted and change nothing.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(18, 15))),
            ManualCounts = [Count(EntryA, At(18, 0), -1), Count(EntryA, At(18, 0), 1, revision: 2), Count(EntryA, At(18, 0), 2, revision: 2), Count(EntryA, At(18, 0), 100, revision: 3)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 96)],
            QueueBins = [Bin(At(18, 0), entries: 5, abandoned: 6), Bin(At(18, 0), revision: 2)],
            HealthBins = [Health(At(18, 0), 10, 11), Health(At(18, 0), 10, 9, revision: 2)]
        });

        result.CountBins.Single().Should().BeEquivalentTo(new { Standing = ComparisonStanding.Good, ManualCount = 100.0, SystemCount = 96L });
        result.TrackCompletion.Single(z => z.QueueZone == Vis).Good.Should().Be(new TrackTotals(1, 10, 9, 0, 0, 0, 0.9));
        result.LeftOut.Should().Be(LeftOutInputs.None with { ManualCounts = 3, QueueBins = 1, HealthBins = 1 });
    }

    public static TheoryData<string, LineBinCount[], UnusableKeyReason> UnusableCampaignLineRows => new()
    {
        { "refused: above the bound", [LineBin("A-VIS entry", At(18, 0), 1_000_001)], UnusableKeyReason.Refused },
        { "conflicting", [LineBin("A-VIS entry", At(18, 0), 3), LineBin("A-VIS entry", At(18, 0), 4)], UnusableKeyReason.Conflicting }
    };

    [Theory]
    [MemberData(nameof(UnusableCampaignLineRows))]
    public void Compare_Should_LeaveNSystemUnknown_When_TheLinesRowOfTheCampaignsVersionIsUnusable(string because, LineBinCount[] rows, UnusableKeyReason reason)
    {
        // The probe: a manual count of 0 beside a refused row read as "crossed 0 times" gave a Good item with no error.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 0)],
            LineBins = [.. rows, LineBin("A-VIS entry", At(18, 0), 9, source: LineCountSource.Vendor)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.CountBins.Single().Should().BeEquivalentTo(new
        {
            Standing = ComparisonStanding.Unknown, SystemCount = (long?)null, AbsoluteError = (double?)null, Accuracy = (double?)null, MeetsTarget = (bool?)null
        }, because);
        var line = result.Lines.Single(l => l.LineId == EntryA);
        (line.ManualZero, line.ManualZeroAbsoluteError, line.ExcludedBins).Should().Be((0, 0.0, 1), because);
        result.LeftOut.Should().Be(LeftOutInputs.None with
        {
            LineBins = rows.Length + 1, UnusableKeys = [Key(UnusableKeyKind.LineBin, At(18, 0), reason, "A-VIS entry")]
        }, because);
    }

    [Fact]
    public void Compare_Should_CountTheBinAsOtherVersion_When_ARowOfAnotherVersionCannotBeUsed()
    {
        // Another version counted the line in this bin too, whatever its values: the campaign version's count is partial.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 99), LineBin("A-VIS entry", At(18, 0), -1, version: 6)],
            QueueBins = [Bin(At(18, 0))]
        });

        (result.CountBins.Single().Standing, result.CountBins.Single().SystemCount).Should().Be((ComparisonStanding.OtherVersion, (long?)null));
        result.LeftOut.Should().Be(LeftOutInputs.None with { LineBins = 1 }, "another version's rows are never read for their values");
    }

    #endregion

    #region Neighbour minutes and the minutes read (security review, CWE-501)

    public static TheoryData<string, QueueBinRow[], QualityInterval[]> NeighboursNotGood => new()
    {
        { "a Degraded bin under an outage", [Bin(At(18, 0), BinQuality.Degraded), Bin(At(18, 15))], [new QualityInterval(Vis, At(18, 13), At(18, 15), BinQuality.Degraded)] },
        { "a Degraded bin", [Bin(At(18, 0), BinQuality.Degraded), Bin(At(18, 15))], [] },
        { "an Unknown bin", [Bin(At(18, 0), BinQuality.Unknown), Bin(At(18, 15))], [] },
        { "no stored bin", [Bin(At(18, 15))], [] },
        { "a bin of another version", [Bin(At(18, 0), version: 6), Bin(At(18, 15))], [] },
        { "a provisional bin", [Bin(At(18, 0), status: BinStatus.Provisional), Bin(At(18, 15))], [] },
        { "an outage over 18:14 only", [Bin(At(18, 0)), Bin(At(18, 15))], [new QualityInterval(Vis, At(18, 14), At(18, 15), BinQuality.Degraded)] },
        { "an uncovered line over 18:14 only", [Bin(At(18, 0)), Bin(At(18, 15))], [new QualityInterval(Vis, At(18, 14), At(18, 15), BinQuality.Unknown)] }
    };

    [Theory]
    [MemberData(nameof(NeighboursNotGood))]
    public void Compare_Should_HoldTheJoinMinutesOwnMean_When_TheNeighboursStoredResultsAreNotGood(string because, QueueBinRow[] bins, QualityInterval[] intervals)
    {
        // The probe: a tracer joins at 18:15:00 and waits 8 minutes; 18:15 holds 12 in a Good bin, 18:14 holds 4. With 18:14
        // the straight line gives 8 (error 0, a pass) while the run stays Good; without it the join minute's 12 holds: error 4
        // against a tolerance of 1, a fail.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 15), At(18, 23))],
            QueueMinutes = [Minute(At(18, 14), 4), Minute(At(18, 15), 12)],
            QueueBins = bins,
            QualityIntervals = intervals
        });

        var run = Tracer(result, 1);
        (run.Standing, run.SystemWaitMinutes, run.ErrorMinutes, run.WithinTolerance).Should().Be((ComparisonStanding.Good, (double?)12, (double?)4, (bool?)false), because);
        ZoneOf(result, Vis).ErrorCheck.Should().Be(new CriterionCheck(0, 1, CriterionVerdict.Fail), because);
    }

    [Fact]
    public void Compare_Should_InterpolateWithTheNeighbour_When_ItsStoredResultsAreGoodAndFinal()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 15), At(18, 23))],
            QueueMinutes = [Minute(At(18, 14), 4), Minute(At(18, 15), 12)],
            QueueBins = [Bin(At(18, 0)), Bin(At(18, 15))]
        });

        (Tracer(result, 1).Standing, Tracer(result, 1).SystemWaitMinutes, Tracer(result, 1).WithinTolerance).Should().Be((ComparisonStanding.Good, (double?)8, (bool?)true));
    }

    [Theory]
    [InlineData(BinQuality.Degraded, ComparisonStanding.Degraded)]
    [InlineData(BinQuality.Unknown, ComparisonStanding.Unknown)]
    public void Compare_Should_SetTheRunApart_When_AnIntervalCoversTheJoinMinuteBeforeTheJoin(BinQuality quality, ComparisonStanding standing)
    {
        // The join minute's mean includes people who entered from 18:15:00, before the tracer joined at 18:15:40.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 15, 40), At(18, 23, 40))],
            QueueMinutes = [Minute(At(18, 15), 8), Minute(At(18, 16), 8)],
            QueueBins = [Bin(At(18, 15))],
            QualityIntervals = [new QualityInterval(Vis, At(18, 15), At(18, 15, 20), quality)]
        });

        (Tracer(result, 1).Standing, Tracer(result, 1).SystemWaitMinutes).Should().Be((standing, (double?)8), "the value is shown, but not judged");
        var zone = ZoneOf(result, Vis);
        (zone.Compared, zone.Excluded, zone.ErrorCheck.Verdict).Should().Be((0, 1, CriterionVerdict.NoData));
    }

    [Fact]
    public void Compare_Should_LeaveTheShiftedErrorOut_When_AShiftedJoinReadsAMinuteWhoseStoredResultsAreNotGood()
    {
        // Joined 18:15:01: two seconds earlier is 18:14:59, a final minute of a Degraded bin; two seconds later stays in 18:15.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 15, 1), At(18, 23, 1))],
            QueueMinutes = [Minute(At(18, 14), 8), Minute(At(18, 15), 8)],
            QueueBins = [Bin(At(18, 0), BinQuality.Degraded), Bin(At(18, 15))]
        });

        var run = Tracer(result, 1);
        (run.Standing, run.ErrorMinutes, run.ErrorOffsetMinusMinutes, run.ErrorOffsetPlusMinutes).Should()
            .Be((ComparisonStanding.Good, (double?)0, (double?)0, (double?)null));
        ZoneOf(result, Vis).Sensitivity.Runs.Should().Be(0, "the run's error is not known at every shift");
    }

    #endregion

    #region Rows as read (security review, CWE-400 and CWE-501)

    [Fact]
    public void Compare_Should_CountTheRowsItReads_When_AListsCountUnderstatesThem()
    {
        // The probe: a list that says 1 and yields 3 counts made the left-out count -2.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = new Listed<ManualCountRow>(1, [Count(EntryA, At(18, 0), 1), Count(EntryA, At(18, 0), 1, observer: O2), Count(EntryA, At(18, 0), 1, observer: G(903))]),
            QueueMinutes = new Listed<QueueMinuteRow>(0, [null, null]),
            QueueBins = [Bin(At(18, 0))]
        });

        result.LeftOut.Should().Be(LeftOutInputs.None with { QueueMinutes = 2 });
        result.CountBins.Single().Observers.Should().Be(3);
    }

    [Fact]
    public void Compare_Should_CompareTheRowsItReads_When_AListsCountOverstatesThem()
    {
        var result = Compare(new ComparisonInput { Scope = Scope(), ManualCounts = new Listed<ManualCountRow>(1_000_001, [Count(EntryA, At(18, 0), 1)]) });

        result.Problem.Should().BeNull();
        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.CountBins.Should().ContainSingle();
    }

    #endregion

    #region Ground truth in the planned days and one run per join (security review, CWE-501)

    [Fact]
    public void Compare_Should_UseNeitherRun_When_TwoRunsShareTheObserverTracerAndDeviceJoin()
    {
        // Script 0048's ux_tracer_run_join: one run per observer, tracer code and device join time. Two runs holding one such
        // key are a conflict (both left out, each counted once), never one wait counted twice. Copies of one run are one run.
        var run = Run(1, At(18, 5), At(18, 13));
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns =
            [
                run, run, run with { RunId = G(1009), ExitedRawUtc = At(18, 14), ExitedUtc = At(18, 14) }, Run(2, At(18, 5), At(18, 13)),
                Run(1, At(18, 5), At(18, 13), observer: O2, batch: 2) with { RunId = G(1010) }
            ],
            QueueMinutes = [Minute(At(18, 5), 8)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.Tracers.Select(t => (t.TracerCode, t.ObserverId)).Should().Equal(("T-01", O2), ("T-02", O1));
        result.LeftOut.Should().Be(LeftOutInputs.None with { TracerRuns = 3 }, "one copy, and the two runs of one join");
        ZoneOf(result, Vis).Compared.Should().Be(2);
    }

    [Fact]
    public void Compare_Should_LeaveOutCountsAndRunsOutsideThePlannedDays_When_TheyFallBetweenThem()
    {
        // Two planned days, 18:00 to 18:30 and 19:00 to 19:30: a count of 18:30 and a run joined at 18:45 fall between them.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(18, 30)), new UtcWindow(At(19, 0), At(19, 30))),
            ManualCounts = [Count(EntryA, At(18, 15), 10), Count(EntryA, At(18, 30), 10), Count(EntryA, At(19, 0), 10)],
            TracerRuns = [Run(1, At(18, 29, 59), At(18, 40)), Run(2, At(18, 45), At(19, 5), batch: 2), Run(3, At(19, 0), At(19, 5), batch: 3)]
        });

        result.CountBins.Select(i => i.BinStartUtc).Should().Equal(At(18, 15), At(19, 0));
        result.Tracers.Select(t => t.TracerCode).Should().Equal("T-01", "T-03");
        result.LeftOut.Should().Be(LeftOutInputs.None with { ManualCounts = 1, TracerRuns = 1 });
    }

    #endregion

    #region Judged and excluded (security review: verdicts and sample size)

    [Fact]
    public void Compare_Should_ReportJudgedAndExcludedCounts_When_OneBinIsJudgedAndNineAreDegraded()
    {
        // The probe: one Good bin at 99 of 100 passes the line while nine bins at 50 of 100 sit under an outage. The engine's
        // line verdict judges only what it holds; the campaign verdict (ARV-104g) needs the target of judged bins per line and
        // shows the excluded share, from these counts.
        var bins = Enumerable.Range(0, 10).Select(i => At(18, 0).AddMinutes(15 * i)).ToList();
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(20, 30))),
            ManualCounts = [.. bins.Select(b => Count(EntryA, b, 100))],
            LineBins = [.. bins.Select((b, i) => LineBin("A-VIS entry", b, i == 0 ? 99 : 50))],
            QueueBins = [.. bins.Select(b => Bin(b))],
            HealthBins = [.. bins.Select(b => Health(b, 10, 10))],
            QualityIntervals = [new QualityInterval(Vis, At(18, 15), At(20, 30), BinQuality.Degraded)]
        });

        var line = result.Lines.Single(l => l.LineId == EntryA);
        (line.Judged, line.JudgedBins, line.ExcludedBins, line.Bins.Degraded, line.Check.Verdict).Should().Be((1, 1, 9, 9, CriterionVerdict.Pass));
        var completion = result.TrackCompletion.Single(z => z.QueueZone == Vis);
        (completion.Good.Bins, completion.ExcludedBins).Should().Be((1, 9));
        result.Lines.Single(l => l.LineId == CountA).Should().BeEquivalentTo(new { Judged = 0, JudgedBins = 0, ExcludedBins = 0 });
    }

    [Fact]
    public void Compare_Should_CountACountLinesBinOnce_When_BothDirectionsAreJudged()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(CountA, At(18, 0), 40, 20), Count(CountA, At(18, 15), 40, 0)],
            LineBins = [LineBin("A-VIS count", At(18, 0), 38, 21), LineBin("A-VIS count", At(18, 15), 40, 0)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        var line = result.Lines.Single(l => l.LineId == CountA);
        (line.Judged, line.JudgedBins, line.ExcludedBins, line.ManualZero).Should().Be((3, 2, 0, 1));
    }

    #endregion

    #region Read-only results

    [Fact]
    public void Compare_Should_ReturnListsThatCannotBeChanged_When_ItCompares()
    {
        var result = Compare(Mixed() with { QueueMinutes = [Minute(At(18, 5), 8.9), Minute(At(18, 6), double.NaN)] });
        var failed = Compare(new ComparisonInput { Scope = null });

        foreach (var r in new[] { result, failed })
        {
            ShouldBeReadOnly(r.CountBins);
            ShouldBeReadOnly(r.Lines);
            ShouldBeReadOnly(r.Tracers);
            ShouldBeReadOnly(r.TracerZones);
            ShouldBeReadOnly(r.TrackCompletion);
            ShouldBeReadOnly(r.Observers);
            ShouldBeReadOnly(r.Batches);
            ShouldBeReadOnly(r.LeftOut.UnusableKeys);
        }

        result.LeftOut.UnusableKeys.Should().ContainSingle();
        var keys = new List<UnusableKey> { Key(UnusableKeyKind.QueueBin, At(18, 0)) };
        var leftOut = LeftOutInputs.None with { UnusableKeys = keys };
        keys.Clear();
        leftOut.UnusableKeys.Should().ContainSingle("the keys are copied");
        ShouldBeReadOnly(leftOut.UnusableKeys);
        (LeftOutInputs.None with { UnusableKeys = null }).Should().Be(LeftOutInputs.None);
        leftOut.GetHashCode().Should().NotBe(LeftOutInputs.None.GetHashCode());
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

    #region Times not in UTC, run twins and quality intervals (security review re-check, CWE-501)

    public static TheoryData<string, ManualCountRow[], int> NonUtcCorrections => new()
    {
        { "alone", [Count(EntryA, Unspecified(At(18, 0)), 50, revision: 2)], 1 },
        { "after a UTC copy", [Count(EntryA, At(18, 0), 50, revision: 2), Count(EntryA, Unspecified(At(18, 0)), 50, revision: 2)], 2 },
        { "before a UTC copy", [Count(EntryA, Unspecified(At(18, 0)), 50, revision: 2), Count(EntryA, At(18, 0), 50, revision: 2)], 2 }
    };

    [Theory]
    [MemberData(nameof(NonUtcCorrections))]
    public void Compare_Should_NotJudgeTheLineAndBin_When_TheLatestCountsTimeIsNotUtc(string because, ManualCountRow[] corrections, int leftOut)
    {
        // The probe: revision 1 (100, UTC) and revision 2 (50, the same instant without the UTC kind). Revision 2 is the same key
        // and the latest; dropped before the grouping, revision 1 stood in (accuracy 1, a pass). A UTC copy beside it changes
        // nothing, in either order: every row of the deciding revision is checked.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100), .. corrections],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 100)],
            QueueBins = [Bin(At(18, 0))]
        });

        result.CountBins.Should().BeEmpty(because);
        result.Lines.Single(l => l.LineId == EntryA).Check.Verdict.Should().Be(CriterionVerdict.NoData, because);
        result.LeftOut.Should().Be(LeftOutInputs.None with
        {
            ManualCounts = leftOut, UnusableKeys = [Key(UnusableKeyKind.ManualCount, At(18, 0), line: "A-VIS entry", observer: O1)]
        }, because);
        result.LeftOut.UnusableKeys.Single().StartUtc.Kind.Should().Be(DateTimeKind.Utc, because);
    }

    [Fact]
    public void Compare_Should_LeaveTheBinUnknown_When_ItsLatestQueueOrHealthRevisionsTimeIsNotUtc()
    {
        // The probe: health revision 2 (80 of 100) and a Degraded queue bin revision 2, both without the UTC kind, beside Good
        // revisions 1 (95 of 100): revision 1 gave 0.95 and a Good count bin, both passes.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(18, 15))),
            ManualCounts = [Count(EntryA, At(18, 0), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 97)],
            QueueBins = [Bin(At(18, 0)), Bin(Unspecified(At(18, 0)), BinQuality.Degraded, revision: 2)],
            HealthBins = [Health(At(18, 0), 100, 95), Health(Unspecified(At(18, 0)), 100, 80, revision: 2)]
        });

        (result.CountBins.Single().Standing, result.CountBins.Single().SystemCount).Should().Be((ComparisonStanding.Unknown, (long?)null));
        var zone = result.TrackCompletion.Single(z => z.QueueZone == Vis);
        (zone.Bins.Unknown, zone.Check.Verdict).Should().Be((1, CriterionVerdict.NoData));
        result.LeftOut.Should().Be(LeftOutInputs.None with
        {
            QueueBins = 1, HealthBins = 1, UnusableKeys = [Key(UnusableKeyKind.QueueBin, At(18, 0)), Key(UnusableKeyKind.HealthBin, At(18, 0))]
        });
    }

    [Fact]
    public void Compare_Should_SeeAConflict_When_AMinutesOrALineBinsTwinIsNotUtc()
    {
        // The probe: a minute's twin (a mean of 30 against 8) and a line bin's twin (10 against 100 crossings), each without the
        // UTC kind, were not seen as the same key: the UTC rows were used.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 15), 100)],
            TracerRuns = [Run(1, At(18, 15), At(18, 23))],
            LineBins = [LineBin("A-VIS entry", At(18, 15), 100), LineBin("A-VIS entry", Unspecified(At(18, 15)), 10)],
            QueueMinutes = [Minute(At(18, 15), 8), Minute(Unspecified(At(18, 15)), 30)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        (result.CountBins.Single().Standing, result.CountBins.Single().SystemCount).Should().Be((ComparisonStanding.Unknown, (long?)null));
        (Tracer(result, 1).Standing, Tracer(result, 1).SystemWaitMinutes).Should().Be((ComparisonStanding.Unknown, (double?)null));
        result.LeftOut.Should().Be(LeftOutInputs.None with
        {
            LineBins = 2, QueueMinutes = 2,
            UnusableKeys =
            [
                Key(UnusableKeyKind.LineBin, At(18, 15), UnusableKeyReason.Conflicting, "A-VIS entry"),
                Key(UnusableKeyKind.QueueMinute, At(18, 15), UnusableKeyReason.Conflicting)
            ]
        });
    }

    public static TheoryData<string, TracerRunRow[]> RefusedRunTwins => new()
    {
        { "one run id, one row's exit before its join", [Run(1, At(18, 15), At(18, 23)), Run(1, At(18, 15), At(18, 10))] },
        { "a twin of the join whose exit is not its device exit less the offset", [Run(1, At(18, 15), At(18, 23)), Run(1, At(18, 15), At(18, 23), batch: 2) with { RunId = G(1009), ExitedUtc = At(18, 40) }] },
        { "a twin of the join in a zone out of scope", [Run(1, At(18, 15), At(18, 23)), Run(1, At(18, 15), At(18, 40), zone: G(999), batch: 2) with { RunId = G(1009) }] },
        { "a batch with a run beyond the offset bound", [Run(1, At(18, 15), At(18, 23)), Run(2, At(18, 16), At(18, 24), offsetMs: 400_000)] },
        { "a batch with a run longer than three hours", [Run(1, At(18, 15), At(18, 23)), Run(2, At(18, 16), At(21, 17))] },
        { "a copy whose join is not UTC", [Run(1, At(18, 15), At(18, 23)), Run(1, At(18, 15), At(18, 23)) with { JoinedUtc = Unspecified(At(18, 15)) }] }
    };

    [Theory]
    [MemberData(nameof(RefusedRunTwins))]
    public void Compare_Should_UseNoRunOfAKeyOrBatch_When_AnyOfItsRowsCannotBe(string because, TracerRunRow[] runs)
    {
        // The probe: the refused row was dropped before the conflict and batch checks, so its twin (or its batch's other run) was
        // compared and passed. Run 3, in a batch of its own, is compared as before.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [.. runs, Run(3, At(18, 15), At(18, 23), batch: 3)],
            QueueMinutes = [Minute(At(18, 15), 8)],
            QueueBins = [.. Bins(At(18, 0), At(18, 45))]
        });

        result.Tracers.Should().ContainSingle(because).Which.TracerCode.Should().Be("T-03", because);
        ZoneOf(result, Vis).Compared.Should().Be(1, because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { TracerRuns = 2 }, because);
    }

    public static TheoryData<string, TracerRunRow[], int> RunsLeftOutWithTheirTwinOrBatch => new()
    {
        // Run 1's rows disagree (no row of it is used) and run 1009 in batch 2 holds run 1's observer, tracer code and device join.
        { "a run id whose rows disagree shares its join with another run", [Run(1, At(18, 15), At(18, 23)), Run(1, At(18, 15), At(18, 24)), Run(1, At(18, 15), At(18, 23), batch: 2) with { RunId = G(1009) }], 3 },
        { "a batch holds a row without an observer", [Run(1, At(18, 15), At(18, 23)), Run(2, At(18, 16), At(18, 24)) with { ObserverId = Guid.Empty }], 2 },
        { "a batch holds a row without a run id", [Run(1, At(18, 15), At(18, 23)), Run(2, At(18, 16), At(18, 24)) with { RunId = Guid.Empty }], 2 }
    };

    [Theory]
    [MemberData(nameof(RunsLeftOutWithTheirTwinOrBatch))]
    public void Compare_Should_UseNoRunOfAKeyOrBatch_When_ARowLeftOutForItsIdsWouldHideTheConflict(string because, TracerRunRow[] runs, int leftOut)
    {
        // The second re-check: a run id with disagreeing rows was dropped before the join check, and a row with an empty id before
        // the batch check, so the other run was compared and passed. Run 3, in a batch of its own, is compared as before.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [.. runs, Run(3, At(18, 15), At(18, 23), batch: 3)],
            QueueMinutes = [Minute(At(18, 15), 8)],
            QueueBins = [.. Bins(At(18, 0), At(18, 45))]
        });

        result.Tracers.Should().ContainSingle(because).Which.TracerCode.Should().Be("T-03", because);
        ZoneOf(result, Vis).Compared.Should().Be(1, because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { TracerRuns = leftOut }, because);
    }

    public static TheoryData<string, QualityInterval> ClampedIntervals => new()
    {
        { "an open outage ending at DateTime.MaxValue", new QualityInterval(Vis, At(17, 0), DateTime.MaxValue, BinQuality.Degraded) },
        { "an open outage ending at DateTime.MaxValue in UTC", new QualityInterval(Vis, At(17, 0), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), BinQuality.Degraded) },
        { "an end in the year 3001", new QualityInterval(Vis, At(17, 0), new DateTime(3001, 1, 1, 0, 0, 0, DateTimeKind.Utc), BinQuality.Degraded) },
        { "a start at DateTime.MinValue", new QualityInterval(Vis, DateTime.MinValue, At(18, 5), BinQuality.Degraded) },
        { "a start before 2000", new QualityInterval(Vis, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), At(18, 5), BinQuality.Degraded) }
    };

    [Theory]
    [MemberData(nameof(ClampedIntervals))]
    public void Compare_Should_DegradeTheItemsUnderAnInterval_When_ItsEndsLieBeyondTheEnginesYears(string because, QualityInterval row)
    {
        // The probe: an outage still open, passed as ending at DateTime.MaxValue (as AlertInputs does), was dropped and the bin
        // under it judged Good (a pass). Clamped to the years 2000 to 2999, it covers the bin.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 100)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 99)],
            QueueBins = [Bin(At(18, 0))],
            QualityIntervals = [row]
        });

        result.CountBins.Single().Standing.Should().Be(ComparisonStanding.Degraded, because);
        result.Lines.Single(l => l.LineId == EntryA).Check.Verdict.Should().Be(CriterionVerdict.NoData, because);
        result.LeftOut.Should().Be(LeftOutInputs.None, because);
    }

    [Fact]
    public void Compare_Should_JudgeTrackCompletionWithoutTheBinUnderAnOpenOutage_When_ItEndsAtDateTimeMaxValue()
    {
        // The probe: 85 of 100 (a fail) and 100 of 100 under an open outage gave 0.925, a pass.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(new UtcWindow(At(18, 0), At(18, 30))),
            QueueBins = [.. Bins(At(18, 0), At(18, 30))],
            HealthBins = [Health(At(18, 0), 100, 85), Health(At(18, 15), 100, 100)],
            QualityIntervals = [new QualityInterval(Vis, At(18, 15), DateTime.MaxValue, BinQuality.Degraded)]
        });

        var zone = result.TrackCompletion.Single(z => z.QueueZone == Vis);
        (zone.Good.Rate, zone.Check.Verdict, zone.ExcludedBins).Should().Be(((double?)0.85, CriterionVerdict.Fail, 1));
        zone.Degraded.Should().Be(new TrackTotals(1, 100, 100, 0, 0, 0, 1.0));
    }

    public static TheoryData<string, QualityInterval> UnplaceableIntervals => new()
    {
        { "a start that is not UTC", new QualityInterval(Vis, DateTime.SpecifyKind(At(18, 40), DateTimeKind.Local), At(18, 45), BinQuality.Degraded) },
        { "an end that is not UTC", new QualityInterval(Vis, At(18, 40), Unspecified(At(18, 45)), BinQuality.Degraded) },
        { "an end at the start", new QualityInterval(Vis, At(18, 40), At(18, 40), BinQuality.Degraded) },
        { "an end before the start", new QualityInterval(Vis, At(18, 45), At(18, 40), BinQuality.Degraded) },
        { "an open start that is not DateTime.MinValue", new QualityInterval(Vis, Unspecified(DateTime.MinValue.AddTicks(1)), At(18, 45), BinQuality.Degraded) },
        { "an undefined quality", new QualityInterval(Vis, At(18, 40), At(18, 45), (BinQuality)9) }
    };

    [Theory]
    [MemberData(nameof(UnplaceableIntervals))]
    public void Compare_Should_MakeTheZoneUnknownThroughout_When_AQualityIntervalCannotBePlaced(string because, QualityInterval row)
    {
        // Dropped, such an interval left the zone's results Good (failing open). Where it lies cannot be told, so nothing of the
        // zone is Good, even at 18:00; the other zone keeps its results (rather than a Problem that would compare nothing).
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 10), Count(EntryB, At(18, 0), 10)],
            TracerRuns = [Run(1, At(18, 5, 30), At(18, 13, 30))],
            QueueMinutes = [Minute(At(18, 5), 8)],
            QueueBins = [.. Bins(At(18, 0), At(19, 0)), .. Bins(At(18, 0), At(19, 0), Cit)],
            HealthBins = [Health(At(18, 0), 10, 9), Health(At(18, 0), 10, 9, zone: Cit)],
            QualityIntervals = [row]
        });

        result.CountBins.Select(i => (i.QueueZone, i.Standing)).Should().Equal([(Cit, ComparisonStanding.Good), (Vis, ComparisonStanding.Unknown)], because);
        (Tracer(result, 1).Standing, Tracer(result, 1).SystemWaitMinutes).Should().Be((ComparisonStanding.Unknown, (double?)8), because);
        result.TrackCompletion.Single(z => z.QueueZone == Vis).Bins.Should().Be(new StandingTally(0, 0, 0, 4, 0, 0), because);
        result.TrackCompletion.Single(z => z.QueueZone == Cit).Bins.Good.Should().Be(1, because);
        result.LeftOut.Should().Be(LeftOutInputs.None with { QualityIntervals = 1, UnusableKeys = [Key(UnusableKeyKind.QualityInterval, Year2000)] }, because);
    }

    [Fact]
    public void Compare_Should_ListAZoneOnce_When_SeveralOfItsIntervalsCannotBePlaced()
    {
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            QualityIntervals =
            [
                new QualityInterval(Vis, At(18, 45), At(18, 40), BinQuality.Degraded), new QualityInterval(Vis, Unspecified(At(18, 0)), At(18, 5), BinQuality.Unknown),
                new QualityInterval(Cit, At(18, 0), At(18, 5), BinQuality.Degraded)
            ]
        });

        result.LeftOut.Should().Be(LeftOutInputs.None with { QualityIntervals = 2, UnusableKeys = [Key(UnusableKeyKind.QualityInterval, Year2000)] });
    }

    [Fact]
    public void Compare_Should_HoldTheJoinMinutesOwnMean_When_TheNeighbourMinuteIsUnusable()
    {
        // A refused or conflicting neighbour is absent, as a neighbour without a row: the join minute's mean holds, the run stays
        // Good (only the minutes read count in its standing) and the minute's key is listed.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            TracerRuns = [Run(1, At(18, 15, 10), At(18, 23, 10))],
            QueueMinutes = [Minute(At(18, 14), 4), Minute(At(18, 14), 5), Minute(At(18, 15), 12)],
            QueueBins = [.. Bins(At(18, 0), At(18, 30))]
        });

        (Tracer(result, 1).Standing, Tracer(result, 1).SystemWaitMinutes).Should().Be((ComparisonStanding.Good, (double?)12));
        result.LeftOut.UnusableKeys.Should().Equal(Key(UnusableKeyKind.QueueMinute, At(18, 14), UnusableKeyReason.Conflicting));
    }

    #endregion

    #region Every result, order and mapping

    /// <summary>A campaign's worth of every input: counts, runs with an abandoned one, minutes, bins, health and an outage.</summary>
    private static ComparisonInput Mixed() =>
        new()
        {
            Scope = Scope(),
            ManualCounts =
            [
                Count(EntryA, At(18, 0), 100), Count(EntryA, At(18, 0), 98, observer: O2), Count(ExitA, At(18, 15), 3, 40), Count(CountA, At(18, 30), 7, 9),
                Count(EntryB, At(18, 45), 0)
            ],
            TracerRuns =
            [
                Run(1, At(18, 5, 30), At(18, 13, 30), offsetMs: 400), Run(2, At(18, 6, 10), At(18, 20, 40), offsetMs: 9_000, batch: 2),
                Run(3, At(18, 22, 0), At(18, 25, 0), abandoned: true, batch: 3, observer: O2), Run(4, At(18, 7, 0), At(18, 17, 0), zone: ZoneB, batch: 4)
            ],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 96), LineBin("A-VIS exit", At(18, 15), 2, 41), LineBin("A-VIS count", At(18, 30), 7, 8)],
            QueueMinutes = [Minute(At(18, 5), 8.9), Minute(At(18, 6), 13), Minute(At(18, 7), 10, zone: Cit)],
            QueueBins = [.. Bins(At(18, 0), At(19, 0)), .. Bins(At(18, 0), At(19, 0), Cit)],
            HealthBins = [Health(At(18, 0), 20, 18), Health(At(18, 15), 10, 10), Health(At(18, 30), 4, 4)],
            QualityIntervals = [new QualityInterval(Vis, At(18, 40), At(18, 50), BinQuality.Degraded)]
        };

    [Fact]
    public void Compare_Should_CarryTheProfileVersionOnEveryResult_When_EveryKindOfInputIsGiven()
    {
        var result = Compare(Mixed());

        result.ProfileVersion.Should().Be(Version);
        result.CountBins.Should().NotBeEmpty().And.AllSatisfy(i => i.ProfileVersion.Should().Be(Version));
        result.Lines.Should().NotBeEmpty().And.AllSatisfy(i => i.ProfileVersion.Should().Be(Version));
        result.Tracers.Should().NotBeEmpty().And.AllSatisfy(i => i.ProfileVersion.Should().Be(Version));
        result.TracerZones.Should().NotBeEmpty().And.AllSatisfy(i => i.ProfileVersion.Should().Be(Version));
        result.TracerOverall.ProfileVersion.Should().Be(Version);
        result.CountBins.Select(i => i.QueueZone).Distinct().Should().Equal(Cit, Vis);
        result.TrackCompletion.Should().NotBeEmpty().And.AllSatisfy(i => i.ProfileVersion.Should().Be(Version));
        result.Tracers.Select(t => (t.TracerCode, t.Standing, t.Abandoned, t.OffsetOutlier)).Should().Equal(("T-04", ComparisonStanding.Good, false, false),
            ("T-01", ComparisonStanding.Good, false, false), ("T-02", ComparisonStanding.Good, false, true), ("T-03", ComparisonStanding.Good, true, false));
        result.Observers.Select(o => (o.ObserverId, o.MedianMs)).Should().Equal((O1, 400.0), (O2, 0.0));
    }

    [Fact]
    public void Compare_Should_GiveTheSameResult_When_TheInputsComeInAnotherOrder()
    {
        var input = Mixed();
        var reversed = input with
        {
            ManualCounts = [.. input.ManualCounts.Reverse()],
            TracerRuns = [.. input.TracerRuns.Reverse()],
            LineBins = [.. input.LineBins.Reverse()],
            QueueMinutes = [.. input.QueueMinutes.Reverse()],
            QueueBins = [.. input.QueueBins.Reverse()],
            HealthBins = [.. input.HealthBins.Reverse()],
            QualityIntervals = [.. input.QualityIntervals.Reverse()],
            Scope = input.Scope with { Zones = [.. input.Scope.Zones.Reverse()], Lines = [.. input.Scope.Lines.Reverse()] }
        };

        var expected = Compare(input);
        expected.CountBins.Should().NotBeEmpty();
        Compare(reversed).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering().ComparingRecordsByMembers());
    }

    [Fact]
    public void Compare_Should_GiveFiniteResultsThatSerialise_When_EveryStoredValueIsAtItsBound()
    {
        // Security review of ARV-104f (Low-1, checked for ARV-104e's statistics): every stored value the engine accepts is
        // bounded (1,000,000 crossings, people or tracks a bin, 10,000 a manual count, a mean wait of a day, a tracer of 3 hours),
        // so counts, accuracy, errors, bias, sensitivity and completion stay finite and the result is written as JSON, even at
        // the bounds and with a tracer of a millisecond against a wait of a day.
        var result = Compare(new ComparisonInput
        {
            Scope = Scope(),
            ManualCounts = [Count(EntryA, At(18, 0), 10_000), Count(EntryA, At(18, 0), 0, observer: O2), Count(ExitA, At(18, 0), 0, 10_000)],
            LineBins = [LineBin("A-VIS entry", At(18, 0), 1_000_000, 1_000_000), LineBin("A-VIS exit", At(18, 0), 0, 1_000_000)],
            QueueMinutes = [.. Enumerable.Range(0, 60).Select(m => Minute(At(18, m), m % 2 == 0 ? 1_440 : 0.001, waits: 1_000_000))],
            QueueBins = [.. Enumerable.Range(0, 12).Select(i => Bin(At(18, 0).AddMinutes(15 * i), entries: 1_000_000, abandoned: 1_000_000))],
            HealthBins = [.. Enumerable.Range(0, 4).Select(i => Health(At(18, 0).AddMinutes(15 * i), 1_000_000, i % 2 == 0 ? 1_000_000 : 0))],
            TracerRuns = [Run(1, At(18, 0), At(21, 0)), Run(2, At(18, 2, 30), At(18, 2, 30).AddMilliseconds(1), batch: 2), Run(3, At(18, 4), At(18, 34), abandoned: true, batch: 3)]
        });

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        json.Should().NotContain("Infinity").And.NotContain("NaN");
        result.TracerOverall.Compared.Should().Be(2);
        double.IsFinite(result.TracerOverall.Bias.GetValueOrDefault(double.NaN)).Should().BeTrue();
        result.Lines.Where(l => l.PooledAccuracy is not null).Should().OnlyContain(l => l.PooledAccuracy >= 0 && l.PooledAccuracy <= 1);
        result.TrackCompletion.Single(z => z.QueueZone == Vis).Good.Rate.Should().Be(0.5);
    }

        [Fact]
    public void WindowsOf_Should_GiveEachLocalDayInUtc_When_DaysAreGivenInAnyOrderOrTwice()
    {
        var dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        ComparisonScope.WindowsOf([new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8)], dubai).Should().Equal(
            new UtcWindow(new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc)),
            new UtcWindow(new DateTime(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc)));
        ComparisonScope.WindowsOf([new DateOnly(2026, 3, 29)], london).Should().Equal(
            new UtcWindow(new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 29, 23, 0, 0, DateTimeKind.Utc)));
        FluentActions.Invoking(() => ComparisonScope.WindowsOf(null, dubai)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ComparisonScope.WindowsOf([], null)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Of_Should_MapTheCampaignItsCountsAndItsRuns_When_TheyWereCaptured()
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
        var manager = Guid.CreateVersion7();
        var campaign = new ValidationCampaign("Pilot", profile, [snake.Id!.Value], [entry.Id!.Value], [new DateOnly(2026, 10, 8)], new DateOnly(2026, 10, 8),
            null, null, manager, now);
        campaign.Id = Guid.CreateVersion7();
        campaign.Start(manager, now, ZoneProfileStatus.Published);
        var count = campaign.Capture(entry.Id!.Value, new DateTime(2026, 10, 8, 5, 0, 0, DateTimeKind.Utc), O1, 37, 2, now, dubai, null);
        var (batch, runs) = campaign.RecordTracerRuns([new TracerRunInput(snake.Id!.Value, "T-07", now.AddMinutes(-50).AddMilliseconds(1_500),
            now.AddMinutes(-32).AddMilliseconds(1_500), true)], O1, now.AddMilliseconds(1_500), now, dubai, null, "tablet-07:batch-0001");
        var run = runs.Single();
        run.Id = Guid.CreateVersion7();

        var scope = ComparisonScope.Of(campaign, dubai);
        scope.ProfileVersion.Should().Be(Version);
        scope.Zones.Should().Equal(new ScopeZone(snake.Id!.Value, "Snake A"));
        scope.Lines.Should().Equal(new ScopeLine(entry.Id!.Value, "Entry A", LineRole.Entry, "Snake A"));
        scope.Windows.Should().Equal(new UtcWindow(new DateTime(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc)));
        ManualCountRow.Of(count).Should().Be(new ManualCountRow(entry.Id!.Value, new DateTime(2026, 10, 8, 5, 0, 0, DateTimeKind.Utc), O1, 1, 37, 2));
        TracerRunRow.Of(run).Should().Be(new TracerRunRow(run.Id!.Value, batch.Id!.Value, O1, snake.Id!.Value, "T-07", now.AddMinutes(-50).AddMilliseconds(1_500),
            now.AddMinutes(-32).AddMilliseconds(1_500), 1_500, now.AddMinutes(-50), now.AddMinutes(-32), true));
        FluentActions.Invoking(() => ComparisonScope.Of(null, dubai)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ManualCountRow.Of(null)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => TracerRunRow.Of(null)).Should().Throw<ArgumentNullException>();

        // The mapped run compares as any other (abandoned: counted apart).
        var result = Compare(new ComparisonInput { Scope = scope, TracerRuns = [TracerRunRow.Of(run)], ManualCounts = [ManualCountRow.Of(count)] });
        result.LeftOut.Should().Be(LeftOutInputs.None);
        result.TracerOverall.Abandoned.Should().Be(1);
    }

    #endregion
}
