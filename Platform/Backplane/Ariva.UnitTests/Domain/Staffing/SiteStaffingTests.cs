using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Staffing;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Staffing;

/// <summary>
/// ARV-181a: a site's minute put together from what the job reads: the queue now (fresh and not degraded), the desks open now
/// (F8's desk term from the desks' latest closed minute), the measured throughput, the arrival wave per lane with the e-gate
/// rejects coupled and shared among the lane's queue zones, and the settings' target and busy cap per site and lane.
/// </summary>
public sealed class SiteStaffingTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 14, 0, 30, DateTimeKind.Utc);
    private static readonly DateTime Minute = new(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);
    private static readonly ArrivalWaveSettings Wave = new() { DelayMinutes = 10 };
    private static readonly EgateCouplingSettings Coupling = new() { LagMinutes = 0 };

    private static string[] Desks(string lane, int n) => [.. Enumerable.Range(1, n).Select(i => $"DMO/IMM/{lane}-{i:00}")];

    // The desks open (serving) every minute of the last hour up to 13:59.
    private static IEnumerable<StaffingDeskMinute> Open(IEnumerable<string> desks, int lastMinutesAgo = 1) =>
        from i in Enumerable.Range(lastMinutesAgo, 60) from d in desks select new StaffingDeskMinute(d, Minute.AddMinutes(-i), 0, 60, 0, false);

    private static List<QueueMinuteSample> Queue(int length, long exits, int lastMinutesAgo = 1, bool degraded = false) =>
        [.. Enumerable.Range(lastMinutesAgo, 60).Select(i => new QueueMinuteSample(Minute.AddMinutes(-i), exits, length, degraded && i == lastMinutesAgo))];

    private static ArrivingFlight Flight(string key, DateTime onBlock, AmanLaneDemand aman) =>
        new(key, "QR", "900", null, "DOH", "T1", "B12", onBlock, null, null, onBlock, null, null, aman);

    private static SiteStaffingInput Site(IReadOnlyList<StaffingLaneInput> lanes, IEnumerable<StaffingDeskMinute> desks, IReadOnlyList<ArrivingFlight> flights = null,
        double rejectRate = 0, bool truncated = false) =>
        new("DMO", Now, lanes, [.. desks], flights ?? [], truncated, rejectRate, new Dictionary<DateTime, double>());

    [Fact]
    public void Compute_Should_TakeTheQueueOpenDesksThroughputAndArrivalsOfTheLane_When_EveryInputIsThere()
    {
        // VIS: 4 desks serving, 10 waiting, 2 exits a minute (r 0.5); a flight on-block 13:55 with AMAN's 100 VIS reaches
        // the hall from 14:05: 89 in [14:00, 14:15) and 11 in [14:15, 14:30).
        var vis = Desks("AR", 4);
        var lane = new StaffingLaneInput("A-VIS", "VIS", vis, 4, Queue(10, 2));
        var flight = Flight("F1", Minute.AddMinutes(-5), new AmanLaneDemand(100, 0, 0, 100, 0, 0, Minute));

        var row = SiteStaffing.Compute(Site([lane], Open(vis), [flight]), new StaffingSettings(), Wave, Coupling).Single();

        row.Should().Match<LaneStaffingRow>(r => r.Zone == "A-VIS" && r.Lane == "VIS" && r.MinuteUtc == Minute && r.QueueMinuteUtc == Minute.AddMinutes(-1) &&
            r.QueueNow == 10 && r.OpenNow == 4 && !r.OpenNowDegraded && r.Desks == 4 && r.TargetMinutes == 10 && r.BusyCap == 0.85);
        row.Throughput.Source.Should().Be(DeskThroughputSource.Measured);
        row.Throughput.RatePerDeskMinute.Should().BeApproximately(0.5, 1e-12);
        row.Staffing.Intervals.Should().HaveCount(StaffingRecommendation.Intervals);
        row.Staffing.Intervals[0].Arrivals.Should().BeApproximately(89, 1e-9);
        row.Staffing.Intervals[1].Arrivals.Should().BeApproximately(11, 1e-9);

        // The same lane through the engine directly gives the same intervals.
        var direct = StaffingRecommendation.Recommend(new LaneStaffingInput(10,
            StaffingRecommendation.LaneArrivals(StaffingRecommendation.Plan(Now), ArrivalWave.Project([flight], Now, 195, Wave).Minutes, [], "VIS", 1, false), 0.5, 4, 4, 10, 0.85));
        row.Staffing.Should().BeEquivalentTo(direct);
        row.Staffing.Intervals[0].Should().Match<StaffingIntervalResult>(i => i.Recommended == 4 && i.AtLimit && i.ShortfallDesks == 10 && i.Bound == StaffingBound.Busy,
            "89 arrivals need 89 / (0.85 x 7.5) = 13.96, so 14 desks");
    }

    [Fact]
    public void Compute_Should_ShareTheLanesArrivalsAndCoupleTheRejects_When_TwoQueuesServeTheLane()
    {
        // Two VIS queues share VIS's arrivals equally; with a reject rate of 0.5 and no lag, half of the e-gate eligible join VIS.
        var vis = Desks("AR", 2);
        List<StaffingLaneInput> lanes = [new("A-VIS", "VIS", vis, 2, Queue(10, 1)), new("A-VIS-2", "VIS", vis, 2, Queue(10, 1))];
        var flight = Flight("F1", Minute.AddMinutes(-5), new AmanLaneDemand(160, 0, 0, 100, 0, 60, Minute));

        var rows = SiteStaffing.Compute(Site(lanes, Open(vis), [flight], rejectRate: 0.5), new StaffingSettings(), Wave, Coupling);

        // VIS 100 + 0.5 x 60 rejects = 130 over the wave; 0.89 of it in the first interval, shared by two queues.
        rows.Should().HaveCount(2).And.OnlyContain(r => Math.Abs(r.Staffing.Intervals[0].Arrivals.Value - 0.89 * 130 / 2) < 1e-9);
    }

    [Fact]
    public void Compute_Should_TakeTheSitesAndLanesOwnTargetAndCap_When_Configured()
    {
        var vis = Desks("AR", 4);
        var cit = Desks("AC", 2);
        List<StaffingLaneInput> lanes = [new("A-VIS", "VIS", vis, 4, Queue(10, 2)), new("A-CIT", "CIT", cit, 2, Queue(10, 2))];
        var settings = new StaffingSettings
        {
            TargetMinutes = 12,
            Sites = new(StringComparer.Ordinal)
            {
                ["DMO"] = new StaffingSiteSettings { BusyCap = 0.9, LaneTargetMinutes = new(StringComparer.Ordinal) { ["CIT"] = 5 } }
            }
        };

        var rows = SiteStaffing.Compute(Site(lanes, Open([.. vis, .. cit])), settings, Wave, Coupling);

        rows.Single(r => r.Lane == "VIS").Should().Match<LaneStaffingRow>(r => r.TargetMinutes == 12 && r.BusyCap == 0.9);
        rows.Single(r => r.Lane == "CIT").Should().Match<LaneStaffingRow>(r => r.TargetMinutes == 5 && r.BusyCap == 0.9);
    }

    [Fact]
    public void Compute_Should_GiveNoQueueNowOrOpenNow_When_TheirMinutesAreStaleOrDegraded()
    {
        var vis = Desks("AR", 4);
        var stale = new StaffingLaneInput("A-VIS", "VIS", vis, 4, Queue(10, 2, lastMinutesAgo: 4));
        var degraded = new StaffingLaneInput("A-VIS", "VIS", vis, 4, Queue(10, 2, degraded: true));

        var staleRow = SiteStaffing.Compute(Site([stale], Open(vis, lastMinutesAgo: 6)), new StaffingSettings(), Wave, Coupling).Single();
        var degradedRow = SiteStaffing.Compute(Site([degraded], Open(vis)), new StaffingSettings(), Wave, Coupling).Single();

        staleRow.QueueNow.Should().BeNull("its latest live minute is 4 minutes old, more than 3");
        staleRow.QueueMinuteUtc.Should().Be(Minute.AddMinutes(-4));
        staleRow.OpenNow.Should().BeNull("the desks' latest closed minute is 6 minutes old, more than 5");
        staleRow.Staffing.Gap.Should().Be(StaffingGap.NoQueueLength);
        degradedRow.QueueNow.Should().BeNull();
        degradedRow.OpenNow.Should().Be(4);
        degradedRow.Staffing.Gap.Should().Be(StaffingGap.NoQueueLength);
    }

    [Fact]
    public void Compute_Should_GiveTheLanesReason_When_ItHasNoDesksNoThroughputNoDemandOrTruncatedLegs()
    {
        List<StaffingLaneInput> lanes =
        [
            new("A-VIS", "VIS", [], 0, Queue(10, 2)),
            new("A-CIT", "CIT", Desks("AC", 2), 2, Queue(10, 2)),
            new("A-FT", "FT", Desks("AF", 2), 2, Queue(10, 2))
        ];

        var rows = SiteStaffing.Compute(Site(lanes, Open(Desks("AF", 2))), new StaffingSettings(), Wave, Coupling);
        var truncated = SiteStaffing.Compute(Site([lanes[2]], Open(Desks("AF", 2)), truncated: true), new StaffingSettings
        {
            FallbackCycleSeconds = new(StringComparer.Ordinal) { ["FT"] = 60 }
        }, Wave, Coupling).Single();

        rows.Single(r => r.Lane == "VIS").Staffing.Gap.Should().Be(StaffingGap.NoDesks);
        rows.Single(r => r.Lane == "CIT").Staffing.Gap.Should().Be(StaffingGap.NoThroughput, "its desks report nothing and no fallback is configured");
        rows.Single(r => r.Lane == "FT").Staffing.Intervals.Should().OnlyContain(i => i.Gap == StaffingGap.NoLaneDemand);
        truncated.Staffing.Intervals.Should().OnlyContain(i => i.Gap == StaffingGap.ArrivalsTruncated);
    }

    [Theory]
    [InlineData(1_000, 1_000, true)]
    [InlineData(1_001, 1_000, false)]
    [InlineData(1_000, 1_001, false)]
    [InlineData(4, -1, false)]
    [InlineData(0, 0, true)]
    public void Takes_Should_TakeALaneUpToTheDeskBound_When_Asked(int desks, int inService, bool takes)
    {
        SiteStaffing.Takes(new StaffingLaneInput("A-VIS", "VIS", Desks("AR", desks), inService, [])).Should().Be(takes);
    }

    [Fact]
    public void Takes_Should_CountDistinctDesks_When_AKeyRepeats()
    {
        string[] keys = [.. Desks("AR", 1_000), "DMO/IMM/AR-01", null];

        SiteStaffing.Takes(new StaffingLaneInput("A-VIS", "VIS", keys, 1_000, [])).Should().BeTrue("1,000 distinct desks; the repeated key and null are not desks");
        SiteStaffing.Takes(null).Should().BeFalse();
    }

    [Fact]
    public void Compute_Should_RefuseOnlyTheLaneWithMoreDesksThanTheBound_And_KeepItInTheSharing_When_Given()
    {
        // CWE-120: the lane beyond the bound has no row; the other VIS queue is still computed and still shares VIS's
        // arrivals with it (it is still one of the hall's two VIS queues), and the CIT lane is untouched.
        var vis = Desks("AR", 2);
        List<StaffingLaneInput> lanes =
        [
            new("A-VIS", "VIS", Desks("AX", 1_001), 1_001, Queue(10, 2)),
            new("A-VIS-2", "VIS", vis, 2, Queue(10, 1)),
            new("A-CIT", "CIT", Desks("AC", 1), 1, Queue(0, 0))
        ];
        var flight = Flight("F1", Minute.AddMinutes(-5), new AmanLaneDemand(100, 0, 0, 100, 0, 0, Minute));

        var rows = SiteStaffing.Compute(Site(lanes, Open(vis), [flight]), new StaffingSettings(), Wave, Coupling);

        rows.Select(r => r.Zone).Should().Equal("A-VIS-2", "A-CIT");
        rows[0].Staffing.Intervals[0].Arrivals.Should().BeApproximately(89.0 / 2, 1e-9, "shared with the refused queue");
        rows[0].Staffing.Gap.Should().Be(StaffingGap.None);
    }

    [Fact]
    public void Compute_Should_GiveTheSharedQueueTheCategoriesWithoutAQueueOfTheirOwn_When_AHallHasBoth()
    {
        // ARV-185 (owner decision 2026-10-26): with a VIS queue of its own the shared queue ALL takes CIT, RES and CRW; the
        // e-gate rejects join VIS (the reference reject lane), so the shared queue takes none of them. Alone (the NBJ seed's
        // halls) it takes every manual category and the rejects.
        var all = Desks("AA", 4);
        var vis = Desks("AR", 4);
        List<StaffingLaneInput> lanes = [new("A-ALL", "ALL", all, 4, Queue(10, 2)), new("A-VIS", "VIS", vis, 4, Queue(10, 2))];
        var flight = Flight("F1", Minute.AddMinutes(-5), new AmanLaneDemand(260, 50, 30, 100, 20, 60, Minute));

        var rows = SiteStaffing.Compute(Site(lanes, Open([.. all, .. vis]), [flight], rejectRate: 0.5), new StaffingSettings(), Wave, Coupling);
        var alone = SiteStaffing.Compute(Site([lanes[0]], Open(all), [flight], rejectRate: 0.5), new StaffingSettings(), Wave, Coupling).Single();

        rows.Single(r => r.Lane == "ALL").Staffing.Intervals[0].Arrivals.Should().BeApproximately(0.89 * 100, 1e-9);
        rows.Single(r => r.Lane == "VIS").Staffing.Intervals[0].Arrivals.Should().BeApproximately(0.89 * (100 + 0.5 * 60), 1e-9);
        alone.Staffing.Intervals[0].Arrivals.Should().BeApproximately(0.89 * (200 + 0.5 * 60), 1e-9);
        alone.Staffing.Gap.Should().Be(StaffingGap.None);
    }

    [Fact]
    public void Compute_Should_KeepAZoneWhoseKeyDoesNotFitInTheSharing_And_GiveItNoRow_When_Given()
    {
        // ARV-185: a zone whose key does not fit (ZoneKeys.Fits) is not computed, but it is still one of the hall's two VIS
        // queues: the other takes half of VIS's arrivals, and the shared queue still leaves VIS to them.
        var vis = Desks("AR", 2);
        var all = Desks("AA", 2);
        List<StaffingLaneInput> lanes =
        [
            new("A-VIS", "VIS", vis, 2, [], KeyFits: false),
            new("A-VIS-2", "VIS", vis, 2, Queue(10, 1)),
            new("A-ALL", "ALL", all, 2, Queue(10, 1))
        ];
        var flight = Flight("F1", Minute.AddMinutes(-5), new AmanLaneDemand(150, 50, 0, 100, 0, 0, Minute));

        var rows = SiteStaffing.Compute(Site(lanes, Open([.. vis, .. all]), [flight]), new StaffingSettings(), Wave, Coupling);

        SiteStaffing.Takes(lanes[0]).Should().BeFalse();
        rows.Select(r => r.Zone).Should().Equal("A-VIS-2", "A-ALL");
        rows[0].Staffing.Intervals[0].Arrivals.Should().BeApproximately(0.89 * 100 / 2, 1e-9, "shared with the zone that does not fit");
        rows[1].Staffing.Intervals[0].Arrivals.Should().BeApproximately(0.89 * 50, 1e-9);
    }

    [Theory]
    [InlineData(1_000_000, true)]
    [InlineData(0, true)]
    [InlineData(1_000_001, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    public void Compute_Should_GiveOnlyThatLaneNoQueueLength_When_ItsQueueLengthIsOutOfRange(int length, bool used)
    {
        // CWE-501: a length the stream could never write (the table keeps it non-negative) stays with its lane.
        var vis = Desks("AR", 4);
        List<StaffingLaneInput> lanes = [new("A-VIS", "VIS", vis, 4, [.. Queue(10, 2).Select(q => q with { QueueLength = q.MinuteUtc == Minute.AddMinutes(-1) ? length : 10 })]),
            new("A-VIS-2", "VIS", vis, 4, Queue(10, 2))];

        var rows = SiteStaffing.Compute(Site(lanes, Open(vis)), new StaffingSettings(), Wave, Coupling);

        rows.Should().HaveCount(2);
        rows[0].QueueNow.Should().Be(used ? length : null);
        rows[0].Staffing.Gap.Should().Be(used ? StaffingGap.None : StaffingGap.NoQueueLength);
        rows[1].QueueNow.Should().Be(10);
        rows[1].Staffing.Gap.Should().Be(StaffingGap.None);
    }
}
