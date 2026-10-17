using Ariva.Core.Desks;
using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-117d: F10's lane cycle time per person in working time from AMAN's interval statistics (contract V1
/// <c>DeskIntervalStats</c>), and what F8's desk term and nowcast make of it. The cases are the table in
/// docs/domain/formulas.md (F10, "Lane cycle time per person"): families, a lull, a pause, missing documents, NaN and
/// out-of-bound statistics. Each interval is (transactions, documents, mean service s, P90 service s, mean cycle s).
/// </summary>
public sealed class LaneCycleTests
{
    private static readonly DateTime T = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);

    private static DeskIntervalSample I(int transactions, int documents, double service, double p90, double cycle) => new(transactions, documents, service, p90, cycle);

    private static DeskIntervalSample[] Repeat(int count, DeskIntervalSample interval) => Enumerable.Repeat(interval, count).ToArray();

    public static TheoryData<string, DeskIntervalSample[], double?, int, LaneCycleFallback?, double?> Cases => new()
    {
        // name, intervals, c per person (min) with MeanService, refused, fallback, ARV-064's per-transaction reading (min)
        { "single travellers back to back", Repeat(6, I(1, 1, 90, 90, 90)), 1.5, 0, null, 1.5 },
        { "families: 4 approaches, 5 people", [I(4, 5, 90, 150, 95)], 1.2, 0, null, 95.0 / 60 },
        { "a lull: 20 idle minutes before a start", [I(1, 1, 90, 90, 1290), I(1, 1, 90, 90, 90)], 1.5, 0, null, 11.5 },
        { "a pause: paused minutes publish nothing", [I(1, 1, 90, 90, 90), I(0, 0, 0, 0, 0), I(0, 0, 0, 0, 0), I(1, 1, 90, 90, 90)], 1.5, 0, null, 1.5 },
        { "zero documents: desks idle", [I(0, 0, 0, 0, 0), I(0, 0, 0, 0, 0)], null, 0, LaneCycleFallback.NoDocuments, null },
        { "missing documents with transactions (outside V1)", [I(2, 0, 90, 90, 90)], null, 1, LaneCycleFallback.NoDocuments, 1.5 },
        { "more transactions than documents (outside V1)", [I(3, 2, 90, 90, 90), I(1, 1, 90, 90, 90)], 1.5, 1, null, 1.5 },
        { "NaN service beside a good interval", [I(1, 1, double.NaN, 90, 90), I(2, 2, 60, 80, 70)], 1.0, 1, null, 230.0 / 3 / 60 },
        { "every value not a number", [I(1, 1, double.NaN, double.NaN, double.NaN)], null, 1, LaneCycleFallback.NoDocuments, null },
        { "out of bounds: 3,601 s, negative, infinite P90, 10,001 documents", [I(1, 1, 3601, 90, 90), I(1, 1, 90, 90, -1), I(1, 1, 90, double.PositiveInfinity, 90),
            I(1, 10_001, 90, 90, 90), I(1, 1, 30, 40, 60)], 0.5, 4, null, 330.0 / 4 / 60 },
        { "transactions with a service time of 0", [I(1, 1, 0, 0, 60), I(1, 1, 120, 120, 120)], 2.0, 1, null, 1.5 },
        { "bounds held: 3,600 s and 10,000 documents", [I(10_000, 10_000, 3600, 3600, 3600)], 60.0, 0, null, 60.0 },
        { "no intervals read", [], null, 0, LaneCycleFallback.NoIntervals, null }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void PerPerson_Should_GiveTheCyclePerPersonInWorkingTime_When_TheCaseIsInTheTable(string name, DeskIntervalSample[] intervals, double? expected, int refused,
        LaneCycleFallback? missing, double? perTransaction)
    {
        var result = LaneCycle.PerPerson(intervals);

        if (expected is { } c)
            result.Minutes.Should().BeApproximately(c, 1e-9, name);
        else
            result.Minutes.Should().BeNull(name);
        result.Refused.Should().Be(refused, name);
        result.Flagged.Should().Be(refused > 0, name);
        result.Missing.Should().Be(missing, name);
        result.Intervals.Should().Be(intervals.Length, name);

        var before = LaneCycle.PerTransaction(intervals.Select(i => (i.MeanCycleSeconds, i.Transactions)));
        if (perTransaction is { } p)
            before.Should().BeApproximately(p, 1e-9, $"{name}: ARV-064's reading");
    }

    [Fact]
    public void PerPerson_Should_BoundTheCycleByTheServiceTimes_When_TheAlternativeMethodIsAsked()
    {
        // The alternative (not used): max(mean service, min(mean cycle, max(P90, mean service))) per transaction, over documents.
        LaneCycle.PerPerson([I(1, 1, 90, 90, 1290), I(1, 1, 90, 90, 90)], LaneCycleMethod.CycleCappedAtP90).Minutes.Should().BeApproximately(1.5, 1e-9, "the lull is capped");
        LaneCycle.PerPerson([I(4, 5, 90, 150, 95)], LaneCycleMethod.CycleCappedAtP90).Minutes.Should().BeApproximately(4 * 95.0 / 5 / 60, 1e-9, "a walk-up gap under P90 is kept");
        LaneCycle.PerPerson([I(1, 1, 90, 90, 40)], LaneCycleMethod.CycleCappedAtP90).Minutes.Should().BeApproximately(1.5, 1e-9, "never below the mean service");
    }

    [Fact]
    public void PerPerson_Should_RefuseAnUnknownMethodAndNullInput_When_Asked()
    {
        var unknown = () => LaneCycle.PerPerson([I(1, 1, 90, 90, 90)], (LaneCycleMethod)7);
        var nothing = () => LaneCycle.PerPerson(null);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
        nothing.Should().Throw<ArgumentNullException>();
        LaneCycle.PerPerson([null, I(1, 1, 60, 60, 60)]).Should().BeEquivalentTo(new { Minutes = 1.0, Refused = 1, Intervals = 2 });
    }

    [Fact]
    public void PerTransaction_Should_WeighTheDesksByTransactions_When_SomeIntervalsAreNotReal()
    {
        LaneCycle.PerTransaction([(90, 10), (60, 30), (double.NaN, 5), (100, 0), (-1, 4), (4000, 3), (double.PositiveInfinity, 2)])
            .Should().BeApproximately(1.125, 1e-12);
        LaneCycle.PerTransaction([]).Should().BeNull();
        LaneCycle.PerTransaction([(90, int.MaxValue), (60, int.MaxValue)]).Should().BeApproximately(1.25, 1e-12);
    }

    private static DeskMinuteSample Minute(string desk, int minute, int transactions = 1) => new(desk, T.AddMinutes(minute), 0, 60, 0, transactions, false);

    private static List<DeskMinuteSample> SixDesks(int transactions = 1) =>
        Enumerable.Range(0, 6).SelectMany(d => Enumerable.Range(0, 5).Select(m => Minute($"DMO/IMM/AR-{d:00}", m, transactions))).ToList();

    private static NowcastResult Published(DeskTerm term, long? exits = null) =>
        Nowcast.Compute(ShadowNowcasts.Inputs(new NowcastInput { QueueLength = 29, ExitsInWindow = exits, ExitWindowMinutes = 5 }, term).Published);

    [Fact]
    public void Nowcast_Should_TakeTheCyclePerPerson_When_FamiliesTravelTogether()
    {
        // F8 with F10 per person: Q = 29, six desks, families 4 approaches for 5 people at 90 s each: c = 1.2, mu = 5, 6.0 min.
        // ARV-064 read 95 s per transaction: c = 1.583, mu = 3.79, 7.92 min.
        var term = DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10), LaneCycle.PerPerson([I(4, 5, 90, 150, 95)]));
        term.CycleMinutes.Should().BeApproximately(1.2, 1e-9);
        term.Degraded.Should().BeFalse();
        Published(term).Minutes.Should().BeApproximately(6.0, 1e-9);
        Published(DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10), 95.0 / 60)).Minutes.Should().BeApproximately(30 / (6 / (95.0 / 60)), 1e-9);

        // A lull: AMAN's 1,290 s cycle after 20 idle minutes is not service; c stays 1.5, the nowcast 7.5 (ARV-064: 11.5 min, 57.5).
        var lull = DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10), LaneCycle.PerPerson([I(1, 1, 90, 90, 1290), I(1, 1, 90, 90, 90)]));
        Published(lull).Minutes.Should().BeApproximately(7.5, 1e-9);
        Published(lull, exits: 18).Minutes.Should().BeApproximately(30 / 3.8, 1e-9);
        Published(DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10), 11.5)).Minutes.Should().BeApproximately(57.5, 1e-9, "ARV-064's reading of the lull");
    }

    [Fact]
    public void DeskTerm_Should_FallBackAsF8Does_When_DocumentsAreMissingOrStatisticsRefused()
    {
        // Zero documents (idle desks): no c, and not the desk minutes' open time per transaction either; the exit term alone, flagged.
        var idle = DeskTerms.Compute(SixDesks(transactions: 2), 5, T.AddMinutes(10), LaneCycle.PerPerson([I(0, 0, 0, 0, 0)]));
        idle.CycleMinutes.Should().BeNull("AMAN's statistics were read: no per-transaction fallback");
        idle.Degraded.Should().BeFalse("idle desks are not a fault; the exit term alone is flagged by the nowcast");
        Published(idle, exits: 18).Should().BeEquivalentTo(new { Source = ThroughputSource.Exits, Degraded = true });
        Published(idle).NoService.Should().Be(NoServiceReason.NoThroughputData);

        // Every interval refused: no c, the term flagged.
        var refused = DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10), LaneCycle.PerPerson([I(2, 0, 90, 90, 90), I(1, 1, double.NaN, 90, 90)]));
        refused.CycleMinutes.Should().BeNull();
        refused.Degraded.Should().BeTrue();
        refused.LaneCycle.Refused.Should().Be(2);

        // One refused beside good ones: c from the rest, flagged, so the screens show a band.
        var partly = DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10), LaneCycle.PerPerson([I(1, 1, 90, 90, 90), I(1, 1, 4000, 90, 90)]));
        partly.CycleMinutes.Should().BeApproximately(1.5, 1e-9);
        partly.Degraded.Should().BeTrue();
        Published(partly).Should().BeEquivalentTo(new { Minutes = 7.5, Degraded = true });

        // No intervals at all (no AMAN statistics for the lane): the desk minutes' own fallback, as before (6 x 5 open minutes over 30).
        var none = DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10), LaneCycle.PerPerson([]));
        none.CycleMinutes.Should().BeApproximately(1.0, 1e-9);
        none.Degraded.Should().BeFalse();
        none.Should().BeEquivalentTo(DeskTerms.Compute(SixDesks(), 5, T.AddMinutes(10)), o => o.Excluding(t => t.LaneCycle));
    }

    [Fact]
    public void ShadowTerm_Should_BeUnchanged_When_ThePublishedLaneCycleChanges()
    {
        // The sensor-only part never reads AMAN's intervals: the same whatever the published lane cycle time.
        var minutes = SixDesks().Select(m => m with { Sensor = new DeskSensorSample(60, 0, false, 60) }).ToList();
        var settings = new SensorCycleSettings();
        var a = DeskTerms.Compute(minutes, 5, T.AddMinutes(10), LaneCycle.PerPerson([I(4, 5, 90, 150, 95)]), settings);
        var b = DeskTerms.Compute(minutes, 5, T.AddMinutes(10), LaneCycle.PerPerson([I(1, 1, double.NaN, 90, 90)]), settings);
        var c = DeskTerms.Compute(minutes, 5, T.AddMinutes(10), 95.0 / 60, settings);
        a.SensorOnly.Should().BeEquivalentTo(b.SensorOnly).And.BeEquivalentTo(c.SensorOnly);
        a.SensorOnly.CycleMinutes.Should().BeNull();
        a.SensorOnly.Degraded.Should().BeFalse("a refused AMAN interval flags the published term only");
    }
}
