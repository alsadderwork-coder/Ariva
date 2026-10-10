using Ariva.Core.Availability;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Availability;

/// <summary>
/// ARV-118: the availability rule of formulas F18 (Proposed). A live minute is available when every published queue zone
/// has a fresh snapshot that reached the minute without lag, and a queue_minute row; a minute decided after its live
/// window uses the database alone and is never available from today's Redis state; the ratio and its grouping by day,
/// week and range.
/// </summary>
public sealed class AvailabilityRuleTests
{
    #region Helpers

    private static readonly AvailabilitySettings Settings = new();
    private static readonly DateTime Minute = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The first run that may decide the minute: its end plus the grace period, 10:03:00.</summary>
    private static readonly DateTime Now = Minute.AddMinutes(1).AddSeconds(120);

    private static ZoneObservation Zone(string key, bool hasMinute = true, double? publishedAgoSeconds = 30, DateTime? snapshotMinute = null) =>
        new($"DMO/{key}", hasMinute, publishedAgoSeconds is { } ago ? new SnapshotTimes(snapshotMinute ?? Minute.AddMinutes(1), Now.AddSeconds(-ago)) : null);

    private static AvailabilityDecision Live(params ZoneObservation[] zones) => AvailabilityRule.Decide(Minute, zones, Now, live: true, Settings);

    #endregion

    #region Timing

    [Theory]
    [InlineData("2026-10-06T10:05:20Z", "2026-10-06T10:02:00Z")]
    [InlineData("2026-10-06T10:05:00Z", "2026-10-06T10:02:00Z")]
    [InlineData("2026-10-06T10:04:59Z", "2026-10-06T10:01:00Z")]
    public void LatestDecidable_Should_BeTheLastMinuteWhoseGraceHasPassed_When_Asked(string now, string expected)
    {
        AvailabilityRule.LatestDecidable(DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Settings)
            .Should().Be(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
    }

    [Fact]
    public void IsLive_Should_HoldUntilTheLiveWindowEnds_When_TheMinuteWaits()
    {
        // 10:00 ends at 10:01; grace 2 minutes, live window 3 minutes: live until 10:06:00.
        AvailabilityRule.IsLive(Minute, Minute.AddMinutes(6), Settings).Should().BeTrue();
        AvailabilityRule.IsLive(Minute, Minute.AddMinutes(6).AddTicks(1), Settings).Should().BeFalse();
    }

    #endregion

    #region Live decisions

    [Fact]
    public void Decide_Should_BeAvailable_When_EveryZoneIsLiveAndHasTheMinute()
    {
        var decision = Live(Zone("A-VIS"), Zone("A-CIT"));

        decision.State.Should().Be(AvailabilityState.Available);
        decision.Reasons.Should().BeEmpty();
        decision.ZonesExpected.Should().Be(2);
    }

    [Fact]
    public void Decide_Should_BeUnavailable_When_TheSiteHasNoPublishedZone()
    {
        var decision = Live();

        decision.State.Should().Be(AvailabilityState.Unavailable);
        decision.Reasons.Should().Equal(AvailabilityReasons.NoPublishedZones);
    }

    [Theory]
    [InlineData(150, false)]
    [InlineData(150.001, true)]
    [InlineData(-60, false)]
    [InlineData(-60.001, true)]
    public void Decide_Should_MarkTheZoneStale_When_ItsSnapshotIsOlderThanTheThreshold(double publishedAgoSeconds, bool stale)
    {
        var decision = Live(Zone("A-VIS", publishedAgoSeconds: publishedAgoSeconds), Zone("A-CIT"));

        decision.Reasons.Contains(AvailabilityReasons.StaleZone).Should().Be(stale);
        decision.ZonesStale.Should().Be(stale ? 1 : 0);
    }

    [Fact]
    public void Decide_Should_MarkTheZoneStale_When_ItsMinuteIsAheadOfTheClock()
    {
        // Now is 10:03:00: a snapshot of 10:05 (ending 10:06, 3 minutes ahead) cannot come from a live stream; counted as
        // stale (not lagging), since it is no evidence of liveness, and it never passes the reached and lag checks.
        var decision = Live(Zone("A-VIS", snapshotMinute: Minute.AddMinutes(5)), Zone("A-CIT"));

        decision.State.Should().Be(AvailabilityState.Unavailable);
        decision.Reasons.Should().Equal(AvailabilityReasons.StaleZone);
        decision.ZonesStale.Should().Be(1);
        decision.ZonesLagging.Should().Be(0);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Decide_Should_AcceptTheMinuteInProgress_When_ItEndsWithinTheTolerance(long ticksBeyond, bool stale)
    {
        // Now is 10:03:00: the minute 10:03 ends at 10:04, exactly the 60 seconds allowed; a tick later is beyond it.
        var decision = Live(Zone("A-VIS", snapshotMinute: Minute.AddMinutes(3).AddTicks(ticksBeyond)));

        decision.Reasons.Contains(AvailabilityReasons.StaleZone).Should().Be(stale);
        decision.State.Should().Be(stale ? AvailabilityState.Unavailable : AvailabilityState.Available);
    }

    [Fact]
    public void Decide_Should_MarkTheZoneStale_When_ItHasNoSnapshot()
    {
        var decision = Live(Zone("A-VIS", publishedAgoSeconds: null));

        decision.State.Should().Be(AvailabilityState.Unavailable);
        decision.Reasons.Should().Equal(AvailabilityReasons.StaleZone);
    }

    [Fact]
    public void Decide_Should_MarkStreamLag_When_TheLiveStateHasNotReachedTheMinute()
    {
        var decision = Live(Zone("A-VIS", snapshotMinute: Minute.AddMinutes(-1)), Zone("A-CIT", snapshotMinute: Minute));

        decision.Reasons.Should().Equal(AvailabilityReasons.StreamLag);
        decision.ZonesLagging.Should().Be(1, "a snapshot of the minute itself has reached it");
    }

    [Fact]
    public void Decide_Should_MarkStreamLag_When_TheLiveMinuteTrailsRealTimeTooFar()
    {
        // Decided at 10:06 (the end of the live window): a snapshot of 10:01 trails by 4 minutes, above 3.
        var now = Minute.AddMinutes(6);
        var zone = new ZoneObservation("DMO/A-VIS", true, new SnapshotTimes(Minute.AddMinutes(1), now.AddSeconds(-10)));

        AvailabilityRule.Decide(Minute, [zone], now, live: true, Settings).Reasons.Should().Equal(AvailabilityReasons.StreamLag);
        AvailabilityRule.Decide(Minute, [zone with { Snapshot = new SnapshotTimes(Minute.AddMinutes(2), now.AddSeconds(-10)) }], now, live: true, Settings)
            .State.Should().Be(AvailabilityState.Available, "10:02 trails by exactly 3 minutes");
    }

    [Fact]
    public void Decide_Should_MarkTheMissingMinute_When_AZoneHasNoQueueMinuteRow()
    {
        var decision = Live(Zone("A-VIS", hasMinute: false), Zone("A-CIT"));

        decision.State.Should().Be(AvailabilityState.Unavailable);
        decision.Reasons.Should().Equal(AvailabilityReasons.MissingMinute);
        decision.ZonesMissing.Should().Be(1);
    }

    [Fact]
    public void Decide_Should_ListEveryReasonInAFixedOrder_When_SeveralZonesFail()
    {
        var decision = Live(Zone("A", snapshotMinute: Minute.AddMinutes(-3)), Zone("B", hasMinute: false), Zone("C", publishedAgoSeconds: null));

        decision.Reasons.Should().Equal(AvailabilityReasons.StaleZone, AvailabilityReasons.MissingMinute, AvailabilityReasons.StreamLag);
        (decision.ZonesStale, decision.ZonesMissing, decision.ZonesLagging).Should().Be((1, 1, 1));
    }

    #endregion

    #region Catch-up

    [Fact]
    public void Decide_Should_BeUnobserved_When_TheMinuteIsDecidedAfterItsLiveWindow()
    {
        // Every zone has a fresh, perfect snapshot now: that says nothing about the past minute.
        var decision = AvailabilityRule.Decide(Minute, [Zone("A-VIS"), Zone("A-CIT")], Now.AddHours(5), live: false, Settings);

        decision.State.Should().Be(AvailabilityState.Unobserved);
        decision.Reasons.Should().Equal(AvailabilityReasons.NotObservedLive);
    }

    [Fact]
    public void Decide_Should_BeUnavailable_When_ACatchUpFindsAMissingMinute()
    {
        var decision = AvailabilityRule.Decide(Minute, [Zone("A-VIS", hasMinute: false), Zone("A-CIT")], Now.AddHours(5), live: false, Settings);

        decision.State.Should().Be(AvailabilityState.Unavailable);
        decision.Reasons.Should().Equal(AvailabilityReasons.MissingMinute, AvailabilityReasons.NotObservedLive);
        decision.ZonesStale.Should().Be(0, "staleness is not judged after the fact");
    }

    [Fact]
    public void Decide_Should_GiveTheSameAnswer_When_TheSameCatchUpRunsTwice()
    {
        var zones = new[] { Zone("A-VIS", hasMinute: false), Zone("A-CIT") };

        AvailabilityRule.Decide(Minute, zones, Now.AddHours(5), false, Settings).Should().BeEquivalentTo(
            AvailabilityRule.Decide(Minute, zones, Now.AddDays(3), false, Settings));
    }

    #endregion

    #region Settings

    [Fact]
    public void Problems_Should_BeEmpty_When_TheDefaultsAreUsed()
    {
        new AvailabilitySettings().Problems().Should().BeEmpty();
    }

    [Fact]
    public void Problems_Should_NameEverySetting_When_TheyAreOutOfRange()
    {
        var problems = new AvailabilitySettings { GraceSeconds = 10, LiveWindowSeconds = 0, StaleAfterSeconds = 5, MaxLagSeconds = 99_999, MaxMinutesPerRun = 0, MaxSitesPerRun = 0 }
            .Problems().ToList();

        problems.Should().HaveCount(6);
    }

    #endregion

    #region Ratio and grouping

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(0, 960, 0.0)]
    [InlineData(950, 960, 0.9895833333333333)]
    [InlineData(9_504, 9_600, 0.99)]
    [InlineData(960, 960, 1.0)]
    public void Ratio_Should_BeAvailableOverOperatingMinutes_When_Computed(int available, int operating, double? expected)
    {
        var ratio = AvailabilitySummary.Ratio(available, operating);

        if (expected is { } value)
            ratio.Should().BeApproximately(value, 1e-12);
        else
            ratio.Should().BeNull("no operating minute gives no ratio, neither 0 nor 1");
    }

    private static AvailabilityCounts Counts(int operating, int available, int unobserved = 0, int maintenance = 0, int closed = 0) =>
        new(operating + maintenance + closed, operating, available, operating - available - unobserved, unobserved, maintenance, closed, 0, operating - available - unobserved, 0, 0);

    [Fact]
    public void Weeks_Should_StartOnMonday_When_TheRangeCutsTheFirstAndLastWeeks()
    {
        // Thursday 1 October to Tuesday 13 October 2026.
        var counts = new Dictionary<DateOnly, AvailabilityCounts>
        {
            [new DateOnly(2026, 10, 1)] = Counts(960, 960),
            [new DateOnly(2026, 10, 5)] = Counts(960, 950, maintenance: 60, closed: 420),
            [new DateOnly(2026, 10, 13)] = Counts(960, 900, unobserved: 30)
        };
        var days = AvailabilitySummary.Days(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 13), counts);
        var weeks = AvailabilitySummary.Weeks(days);
        var total = AvailabilitySummary.Total(days);

        days.Should().HaveCount(13);
        days[1].Counts.Should().Be(AvailabilityCounts.Zero, "a day without rows");
        days[1].Counts.Availability.Should().BeNull();
        weeks.Select(w => (w.WeekStart, w.Days)).Should().Equal(
            (new DateOnly(2026, 9, 28), 4), (new DateOnly(2026, 10, 5), 7), (new DateOnly(2026, 10, 12), 2));
        weeks[1].Counts.OperatingMinutes.Should().Be(960);
        weeks[1].Counts.MaintenanceMinutes.Should().Be(60);
        weeks[1].Counts.Availability.Should().BeApproximately(950 / 960d, 1e-12);
        weeks[1].Counts.AvailabilityMaintenanceAsUnavailable.Should().BeApproximately(950 / 1_020d, 1e-12, "maintenance counted as unavailable");
        weeks[0].Counts.AvailabilityMaintenanceAsUnavailable.Should().Be(weeks[0].Counts.Availability, "no maintenance, one figure");
        weeks[2].Counts.UnobservedMinutes.Should().Be(30);
        total.OperatingMinutes.Should().Be(2_880);
        total.AvailableMinutes.Should().Be(2_810);
        total.Availability.Should().BeApproximately(2_810 / 2_880d, 1e-12, "unobserved minutes count as not available");
        total.RecordedMinutes.Should().Be(2_880 + 60 + 420);
    }

    [Fact]
    public void AvailabilityMaintenanceAsUnavailable_Should_ExposeMaintenance_When_ItWouldInflateTheRatio()
    {
        // A week of 960 operating minutes a day where 2 hours a day of outages were declared maintenance in advance.
        var counts = Counts(operating: 840 * 7, available: 840 * 7, maintenance: 120 * 7);

        counts.Availability.Should().Be(1.0, "maintenance is not operating time in the lenient figure");
        counts.AvailabilityMaintenanceAsUnavailable.Should().BeApproximately(840 / 960d, 1e-12);
        new AvailabilityCounts(5, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0).Should().Match<AvailabilityCounts>(c => c.Availability == null && c.AvailabilityMaintenanceAsUnavailable == 0,
            "only maintenance: no operating ratio, the strict one is 0");
        AvailabilityCounts.Zero.AvailabilityMaintenanceAsUnavailable.Should().BeNull();
    }

    [Theory]
    [InlineData(2026, 10, 5, 2026, 10, 5)]
    [InlineData(2026, 10, 11, 2026, 10, 5)]
    [InlineData(2026, 10, 12, 2026, 10, 12)]
    [InlineData(2027, 1, 1, 2026, 12, 28)]
    public void WeekStart_Should_BeTheMonday_When_GivenAnyDay(int y, int m, int d, int wy, int wm, int wd)
    {
        AvailabilitySummary.WeekStart(new DateOnly(y, m, d)).Should().Be(new DateOnly(wy, wm, wd));
    }

    #endregion
}
