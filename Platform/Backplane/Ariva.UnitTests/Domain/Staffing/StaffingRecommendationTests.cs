using Ariva.Core.Flights;
using Ariva.Core.Staffing;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Staffing;

/// <summary>
/// ARV-181a, formulas.md F16 (pilot version): the fewest open desks per interval that keep the predicted wait
/// max(Q_k, Q_(k+1)) / (n r) at or below the target and each desk busy at most the cap's share of the interval with its
/// arrivals, decided forward because n_k sets the backlog carried into k+1 (F9). Table cases from F16 (C is the capacity
/// per desk per 15 minutes, r = C / 15); every case without a recommendation carries its reason.
/// </summary>
public sealed class StaffingRecommendationTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);

    private static List<LaneDemandInterval> Quarters(params double[] arrivals) =>
        [.. arrivals.Select((a, k) => new LaneDemandInterval(T0.AddMinutes(15 * k), 15, a))];

    private static LaneStaffing Run(double queue, double[] arrivals, double perDeskPer15, double target, int desks, int? open = null, double busyCap = 0.85) =>
        StaffingRecommendation.Recommend(new LaneStaffingInput(queue, Quarters(arrivals), perDeskPer15 / 15, open, desks, target, busyCap));

    private static void ShouldBe(IEnumerable<double?> actual, params double[] expected) =>
        actual.Select(v => v ?? double.NaN).Should().Equal(expected, (a, e) => Math.Abs(a - e) < 1e-9);

    [Fact]
    public void Recommend_Should_OpenThreeDesks_When_FormulasF16Example()
    {
        // F16: C = 10 per 15 min per desk, Q_0 = 0, A = 25, target "Q_(k+1) = 0" (T = 0): n = 3 (2 desks leave a backlog of 5).
        var lane = Run(0, [25], 10, 0, 10, open: 2);

        var k = lane.Intervals.Single();
        lane.Gap.Should().Be(StaffingGap.None);
        k.Recommended.Should().Be(3);
        k.Bound.Should().Be(StaffingBound.Both, "the cap 25 / 8.5 = 2.94 also gives 3");
        k.WaitRecommendedMinutes.Should().Be(0);
        k.BacklogEnd.Should().Be(0);
        k.WaitOpenNowMinutes.Should().BeApproximately(3.75, 1e-9, "2 desks leave 5 waiting, served at 4/3 a minute");
        StaffingRecommendation.Step(0, 25, 2, 10.0 / 15, 15).Backlog.Should().BeApproximately(5, 1e-9);
    }

    [Fact]
    public void Recommend_Should_BeSetByTheBusyCap_When_TheTargetAloneWouldTakeFewerDesks()
    {
        // F16 with T = 10: the wait alone needs 2 (W = 3.75); the cap needs 3.
        var k = Run(0, [25], 10, 10, 10).Intervals.Single();

        k.Recommended.Should().Be(3);
        k.Bound.Should().Be(StaffingBound.Busy);
        StaffingRecommendation.Step(0, 25, 2, 10.0 / 15, 15).WaitMinutes.Should().BeApproximately(3.75, 1e-9);
        StaffingRecommendation.Step(0, 25, 1, 10.0 / 15, 15).WaitMinutes.Should().BeApproximately(22.5, 1e-9);
    }

    [Theory]
    [InlineData(0.85, 5)]
    [InlineData(1.0, 4)]
    [InlineData(0.5, 8)]
    public void Choose_Should_KeepEachDeskBusyAtMostTheCapsShare_When_TheCapBinds(double cap, int expected)
    {
        // r 0.5 (C 7.5), Q 0, A 30, T 10: the wait alone needs 3 (W 5.0); u 1: 4; u 0.85: 30 / 6.375 = 4.71 gives 5; u 0.5: 8.
        var (desks, bound, atLimit, shortfall) = StaffingRecommendation.Choose(0, 30, 15, 0.5, 10, cap, 10);

        desks.Should().Be(expected);
        bound.Should().Be(StaffingBound.Busy);
        atLimit.Should().BeFalse();
        shortfall.Should().BeNull();
        StaffingRecommendation.Step(0, 30, 3, 0.5, 15).WaitMinutes.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void Recommend_Should_CapAtTheDeskLimitAndShowTheShortfall_When_TheLaneHasTooFewDesks()
    {
        // F9's first test as a lane of 3 desks: C 10, Q_0 0, A 60, 60, 0, T 10.
        var lane = Run(0, [60, 60, 0], 10, 10, 3, open: 3);

        lane.Intervals.Select(i => i.Recommended).Should().Equal(3, 3, 3);
        lane.Intervals.Should().OnlyContain(i => i.AtLimit);
        lane.Intervals.Select(i => i.ShortfallDesks).Should().Equal(5, 5, 6);
        lane.Intervals.Select(i => i.Bound).Should().Equal(StaffingBound.Busy, StaffingBound.Busy, StaffingBound.Wait);
        ShouldBe(lane.Intervals.Select(i => i.BacklogEnd), 30, 60, 30);
        ShouldBe(lane.Intervals.Select(i => i.WaitRecommendedMinutes), 15, 30, 30);
        ShouldBe(lane.Intervals.Select(i => i.WaitOpenNowMinutes), 15, 30, 30);
    }

    [Fact]
    public void Recommend_Should_OpenNoDesk_When_NoOneQueuesOrArrives()
    {
        var lane = Run(0, [.. Enumerable.Repeat(0.0, 13)], 10, 10, 6, open: 2);

        lane.Intervals.Should().HaveCount(13).And.OnlyContain(i => i.Recommended == 0 && i.Bound == StaffingBound.None && !i.AtLimit &&
            i.WaitRecommendedMinutes == 0 && i.WaitOpenNowMinutes == 0 && i.BacklogEnd == 0 && i.Gap == StaffingGap.None);
    }

    [Fact]
    public void Recommend_Should_ClearTheQueueNowThenOpenNone_When_NoMoreArrive()
    {
        // Q_0 12, A 0, 0, 0: 2 desks (W = 12 / (4/3) = 9.0; 1 desk gives 18.0), then none.
        var lane = Run(12, [0, 0, 0], 10, 10, 6, open: 2);

        lane.Intervals.Select(i => i.Recommended).Should().Equal(2, 0, 0);
        lane.Intervals.Select(i => i.Bound).Should().Equal(StaffingBound.Wait, StaffingBound.None, StaffingBound.None);
        ShouldBe(lane.Intervals.Select(i => i.WaitRecommendedMinutes), 9, 0, 0);
        ShouldBe(lane.Intervals.Select(i => i.WaitOpenNowMinutes), 9, 0, 0);
        StaffingRecommendation.Step(12, 0, 1, 10.0 / 15, 15).WaitMinutes.Should().BeApproximately(18, 1e-9);
    }

    [Fact]
    public void Recommend_Should_CarryTheBacklogForward_When_AFirstPartialIntervalAndASurge()
    {
        // t = 13:52 (8, 15, 15 minutes), r 0.5 (C 7.5), Q_0 20, A 8, 120, 30, T 10, N 12, open now 4.
        var start = new DateTime(2026, 10, 15, 13, 52, 0, DateTimeKind.Utc);
        List<LaneDemandInterval> intervals = [new(start, 8, 8), new(start.AddMinutes(8), 15, 120), new(start.AddMinutes(23), 15, 30)];

        var lane = StaffingRecommendation.Recommend(new LaneStaffingInput(20, intervals, 0.5, 4, 12, 10, 0.85));

        lane.Intervals.Select(i => i.Recommended).Should().Equal(4, 12, 9);
        lane.Intervals.Select(i => i.Bound).Should().Equal(StaffingBound.Wait, StaffingBound.Busy, StaffingBound.Wait);
        lane.Intervals.Select(i => i.AtLimit).Should().Equal(false, true, false);
        lane.Intervals[1].ShortfallDesks.Should().Be(7, "the cap asks 120 / 6.375 = 18.8, so 19");
        ShouldBe(lane.Intervals.Select(i => i.BacklogEnd), 12, 42, 4.5);
        ShouldBe(lane.Intervals.Select(i => i.WaitRecommendedMinutes), 10, 7, 42 / 4.5);
        ShouldBe(lane.Intervals.Select(i => i.WaitOpenNowMinutes), 10, 51, 51);

        // The wait equals the target at 4 desks (3 give 13.33); 8 desks in the last interval give 10.5.
        StaffingRecommendation.Step(20, 8, 3, 0.5, 8).WaitMinutes.Should().BeApproximately(40.0 / 3, 1e-9);
        StaffingRecommendation.Step(42, 30, 8, 0.5, 15).WaitMinutes.Should().BeApproximately(10.5, 1e-9);

        var wide = StaffingRecommendation.Recommend(new LaneStaffingInput(20, intervals, 0.5, 4, 20, 10, 0.85));
        wide.Intervals.Select(i => i.Recommended).Should().Equal(4, 19, 5);
        wide.Intervals.Select(i => i.Bound).Should().Equal(StaffingBound.Wait, StaffingBound.Busy, StaffingBound.Busy);
        ShouldBe(wide.Intervals.Select(i => i.BacklogEnd), 12, 0, 0);
        ShouldBe(wide.Intervals.Select(i => i.WaitRecommendedMinutes), 10, 12 / 9.5, 0);
    }

    [Fact]
    public void Recommend_Should_GiveNoRecommendation_When_TheLaneLacksAnInput()
    {
        var arrivals = Quarters(25, 30);

        var noQueue = StaffingRecommendation.Recommend(new LaneStaffingInput(null, arrivals, 0.5, 2, 6, 10, 0.85));
        var noRate = StaffingRecommendation.Recommend(new LaneStaffingInput(4, arrivals, null, 2, 6, 10, 0.85));
        var noDesks = StaffingRecommendation.Recommend(new LaneStaffingInput(null, arrivals, null, 0, 0, 10, 0.85));

        noQueue.Gap.Should().Be(StaffingGap.NoQueueLength);
        noRate.Gap.Should().Be(StaffingGap.NoThroughput);
        noDesks.Gap.Should().Be(StaffingGap.NoDesks, "no desks is the first reason");
        foreach (var lane in new[] { noQueue, noRate, noDesks })
        {
            lane.Intervals.Should().OnlyContain(i => i.Recommended == null && i.WaitRecommendedMinutes == null && i.WaitOpenNowMinutes == null && i.Gap == lane.Gap);
            lane.Intervals.Select(i => i.Arrivals).Should().Equal([25, 30], "the arrivals are still given");
        }
    }

    [Fact]
    public void Recommend_Should_StopAtAFlightWithoutPassengers_AndMarkTheBacklogUnknownAfterIt()
    {
        List<LaneDemandInterval> arrivals =
        [
            new(T0, 15, 25), new(T0.AddMinutes(15), 15, 30), new(T0.AddMinutes(30), 15, null, StaffingGap.FlightWithoutPassengers),
            new(T0.AddMinutes(45), 15, 10), new(T0.AddMinutes(60), 15, null, StaffingGap.FlightWithoutPassengers), new(T0.AddMinutes(75), 15, 5)
        ];

        var lane = StaffingRecommendation.Recommend(new LaneStaffingInput(0, arrivals, 10.0 / 15, 2, 10, 10, 0.85));

        lane.Gap.Should().Be(StaffingGap.None);
        lane.Intervals.Select(i => i.Gap).Should().Equal(StaffingGap.None, StaffingGap.None, StaffingGap.FlightWithoutPassengers, StaffingGap.BacklogUnknown,
            StaffingGap.FlightWithoutPassengers, StaffingGap.BacklogUnknown);
        lane.Intervals.Take(2).Should().OnlyContain(i => i.Recommended != null);
        lane.Intervals.Skip(2).Should().OnlyContain(i => i.Recommended == null && i.WaitRecommendedMinutes == null && i.WaitOpenNowMinutes == null);
        lane.Intervals.Select(i => i.Arrivals).Should().Equal([25, 30, null, 10, null, 5], "known arrivals are still given");
    }

    [Fact]
    public void Recommend_Should_GiveTheRecommendationWithoutTheOpenNowWait_When_OpenNowIsUnknownOrZero()
    {
        var unknown = Run(0, [25], 10, 10, 10, open: null).Intervals.Single();
        var none = Run(0, [25, 0], 10, 10, 10, open: 0).Intervals;
        var quiet = Run(0, [0], 10, 10, 10, open: 0).Intervals.Single();

        unknown.Recommended.Should().Be(3);
        unknown.WaitOpenNowMinutes.Should().BeNull();
        unknown.NoServiceOpenNow.Should().BeFalse();
        none.Should().OnlyContain(i => i.NoServiceOpenNow && i.WaitOpenNowMinutes == null, "the 25 still wait in the second interval");
        quiet.NoServiceOpenNow.Should().BeFalse();
        quiet.WaitOpenNowMinutes.Should().Be(0);
    }

    [Fact]
    public void Recommend_Should_CapTheWaitAt120AndReportNoShortfallNumber_When_TheTargetCannotBeReached()
    {
        var capped = Run(1000, [0], 10, 10, 1).Intervals.Single();
        capped.WaitRecommendedMinutes.Should().Be(StaffingRecommendation.WaitCapMinutes);
        capped.AtLimit.Should().BeTrue();
        capped.ShortfallDesks.Should().Be(149, "1000 / (2/3 x 10) = 150 desks clear the queue within the target");

        // T = 0 with people waiting: no count of desks makes the wait 0.
        var unreachable = Run(5, [0], 10, 0, 10).Intervals.Single();
        unreachable.AtLimit.Should().BeTrue();
        unreachable.ShortfallDesks.Should().BeNull();
        unreachable.Recommended.Should().Be(10);
        unreachable.Bound.Should().Be(StaffingBound.Wait);
    }

    [Fact]
    public void Choose_Should_OpenADeskForAnyoneWaitingOrArriving_When_TheFluidAmountsRoundAway()
    {
        // CsCheck seeds 6W_f7mk3Grh4 and 8BvzdVg2kMQ2 (StaffingPropertyTests): amounts below 1e-9 rounded the need to no desk,
        // which is no service for whoever waits.
        StaffingRecommendation.Choose(5168E-67, 6316E-76, 4, 2, 115, 0.7930698696642542, 0).Should().Be((0, StaffingBound.Wait, true, (int?)1));
        StaffingRecommendation.Choose(5168E-67, 6316E-76, 4, 2, 115, 0.7930698696642542, 3).Should().Be((1, StaffingBound.Wait, false, (int?)null));
        var lane = StaffingRecommendation.Recommend(new LaneStaffingInput(2983E-32, [new LaneDemandInterval(T0, 15, 5518E-77)], 3, 5, 2, 12, 0.8488071439774217));
        lane.Intervals.Single().Should().Match<StaffingIntervalResult>(i => i.Recommended == 1 && i.WaitRecommendedMinutes != null && i.BacklogEnd == 0);
    }

    public static TheoryData<LaneStaffingInput> Refused => new()
    {
        new LaneStaffingInput(-1, Quarters(1), 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(double.NaN, Quarters(1), 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(2e6, Quarters(1), 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, Quarters(1), 0, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, Quarters(1), double.PositiveInfinity, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, Quarters(1), 0.5, -1, 1, 10, 0.85),
        new LaneStaffingInput(0, Quarters(1), 0.5, 1, 1_001, 10, 0.85),
        new LaneStaffingInput(0, Quarters(1), 0.5, 1, 1, 121, 0.85),
        new LaneStaffingInput(0, Quarters(1), 0.5, 1, 1, -1, 0.85),
        new LaneStaffingInput(0, Quarters(1), 0.5, 1, 1, 10, 0),
        new LaneStaffingInput(0, Quarters(1), 0.5, 1, 1, 10, 1.01),
        new LaneStaffingInput(0, Quarters(-1), 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, Quarters(double.NaN), 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, [new LaneDemandInterval(T0, 15, null)], 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, [new LaneDemandInterval(T0, 16, 1)], 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, [new LaneDemandInterval(T0, 15, 1), new LaneDemandInterval(T0.AddMinutes(16), 15, 1)], 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, [new LaneDemandInterval(DateTime.SpecifyKind(T0, DateTimeKind.Local), 15, 1)], 0.5, 1, 1, 10, 0.85),
        new LaneStaffingInput(0, [], 0.5, 1, 1, 10, 0.85)
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Recommend_Should_RefuseTheInput_When_AValueIsOutOfItsBounds(LaneStaffingInput input)
    {
        var recommend = () => StaffingRecommendation.Recommend(input);

        recommend.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(13, 52, 30, 52, 8, 17, 0)]
    [InlineData(14, 0, 0, 0, 15, 17, 15)]
    [InlineData(14, 14, 59, 14, 1, 17, 15)]
    public void Plan_Should_RunFromNowToTheNextQuarterHourThenTwelveQuarters_When_Asked(int h, int m, int s, int firstMinute, int firstLength, int endHour, int endMinute)
    {
        var now = new DateTime(2026, 10, 15, h, m, s, DateTimeKind.Utc);

        var plan = StaffingRecommendation.Plan(now);

        plan.Should().HaveCount(StaffingRecommendation.Intervals);
        plan[0].Should().Be(new StaffingSlot(new DateTime(2026, 10, 15, h, firstMinute, 0, DateTimeKind.Utc), firstLength));
        plan.Skip(1).Should().OnlyContain(slot => slot.Minutes == 15 && slot.StartUtc.Minute % 15 == 0);
        plan.Zip(plan.Skip(1)).Should().OnlyContain(p => p.First.EndUtc == p.Second.StartUtc);
        plan[^1].EndUtc.Should().Be(new DateTime(2026, 10, 15, endHour, endMinute, 0, DateTimeKind.Utc));
        (plan[^1].EndUtc - now).Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(3)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(195));
        SiteStaffing.ProjectionWindow(now, plan).Should().BeInRange(181, ArrivalWave.MaxProjectionWindow);
        FluentActions.Invoking(() => StaffingRecommendation.Plan(DateTime.SpecifyKind(now, DateTimeKind.Unspecified))).Should().Throw<ArgumentException>();
    }

    private static ArrivingFlight Flight(string key, DateTime onBlock, AmanLaneDemand aman = null) =>
        new(key, "QR", "900", null, "DOH", "T1", "B12", onBlock, null, null, onBlock, null, null, aman);

    [Fact]
    public void LaneArrivals_Should_SumTheHallMinutesOfTheLanePerInterval_When_FormulasF14Example()
    {
        // F14: P = 200 VIS, on-block 14:00, d = 10: 6, 12, 18, 22, 24 from 14:10 to 14:14, then 24, 22, 20, 16, 14, 12, 10.
        var plan = StaffingRecommendation.Plan(T0);
        var wave = ArrivalWave.Project([Flight("F1", T0, new AmanLaneDemand(200, 0, 0, 200, 0, 0, T0))], T0, 195, new ArrivalWaveSettings { DelayMinutes = 10 });

        var vis = StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "VIS", 1, false);
        var shared = StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "VIS", 2, false);
        var cit = StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "CIT", 1, false);

        ShouldBe(vis.Select(i => i.Arrivals), [82, 118, .. Enumerable.Repeat(0.0, 11)]);
        ShouldBe(shared.Take(2).Select(i => i.Arrivals), 41, 59);
        cit.Should().OnlyContain(i => i.Arrivals == 0 && i.Gap == StaffingGap.None);
    }

    [Fact]
    public void LaneArrivals_Should_SumTheManualCategoriesTheLaneTakes_When_TheQueueIsShared()
    {
        // ARV-185 (owner decision 2026-10-26): one flight of 240 (CIT 50, RES 30, VIS 100, CRW 20, EG 40) on-block 14:00, d = 10:
        // 41 % of each category in the first interval and 59 % in the second (F14's example shape).
        var plan = StaffingRecommendation.Plan(T0);
        var wave = ArrivalWave.Project([Flight("F1", T0, new AmanLaneDemand(240, 50, 30, 100, 20, 40, T0))], T0, 195, new ArrivalWaveSettings { DelayMinutes = 10 });

        // The NBJ seed's hall: one shared queue takes every manual category (200), never the e-gates'.
        var all = StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "ALL", 1, false);
        // A visitors queue of its own: the shared queue takes CIT, RES and CRW (100), here shared by two queues.
        var rest = StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, LaneDemand.Categories("ALL", ["ALL", "VIS"]), 2, false);
        // E-gates are not desks: the recommendation takes the manual categories only.
        var withGates = StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, ["CIT", "EG"], 1, false);

        ShouldBe(all.Select(i => i.Arrivals), [82, 118, .. Enumerable.Repeat(0.0, 11)]);
        ShouldBe(rest.Take(2).Select(i => i.Arrivals), 20.5, 29.5);
        ShouldBe(withGates.Take(2).Select(i => i.Arrivals), 20.5, 29.5);
        all.Should().OnlyContain(i => i.Gap == StaffingGap.None);
        StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, LaneDemand.Categories("ALL", ["ALL", "CIT", "RES", "VIS", "CRW"]), 1, false)
            .Should().OnlyContain(i => i.Gap == StaffingGap.NoLaneDemand && i.Arrivals == null, "every manual category has a queue of its own");
        StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, ["EG"], 1, false).Should().OnlyContain(i => i.Gap == StaffingGap.NoLaneDemand);
        FluentActions.Invoking(() => StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, (IReadOnlyList<string>)null, 1, false))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void LaneArrivals_Should_NotGuess_When_AFlightHasNoPassengersTheLegsAreTruncatedOrTheLaneHasNoDemand()
    {
        var plan = StaffingRecommendation.Plan(T0);
        var settings = new ArrivalWaveSettings { DelayMinutes = 10 };
        // On-block 14:15: hall minutes 14:25 to 14:36, in the second and third intervals.
        var wave = ArrivalWave.Project([Flight("F1", T0, new AmanLaneDemand(200, 0, 0, 200, 0, 0, T0)), Flight("F2", T0.AddMinutes(15))], T0, 195, settings);

        var lanes = StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "VIS", 1, false);

        lanes.Select(i => i.Gap).Take(4).Should().Equal(StaffingGap.None, StaffingGap.FlightWithoutPassengers, StaffingGap.FlightWithoutPassengers, StaffingGap.None);
        lanes[1].Arrivals.Should().BeNull();
        lanes[0].Arrivals.Should().BeApproximately(82, 1e-9);
        StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "VIS", 1, true).Should().OnlyContain(i => i.Gap == StaffingGap.ArrivalsTruncated && i.Arrivals == null);
        StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "FT", 1, false).Should().OnlyContain(i => i.Gap == StaffingGap.NoLaneDemand);
        StaffingRecommendation.LaneArrivals(plan, wave.Minutes, wave.Flights, "EG", 1, false).Should().OnlyContain(i => i.Gap == StaffingGap.NoLaneDemand,
            "e-gates are not desks: the recommendation is for the manual lanes");

        var lane = StaffingRecommendation.Recommend(new LaneStaffingInput(0, lanes, 0.5, 4, 20, 10, 0.85));
        lane.Intervals.Select(i => i.Gap).Should().Equal([StaffingGap.None, StaffingGap.FlightWithoutPassengers, StaffingGap.FlightWithoutPassengers,
            .. Enumerable.Repeat(StaffingGap.BacklogUnknown, 10)]);
    }

    public static TheoryData<string, double, int> OutOfRangeMinutes => new()
    {
        { "NaN", double.NaN, 1 },
        { "+Infinity", double.PositiveInfinity, 1 },
        { "-Infinity", double.NegativeInfinity, 1 },
        { "negative", -0.5, 1 },
        { "the largest double", double.MaxValue, 1 },
        { "just above a million in the interval", 1_000_000.5, 1 },
        { "just above a million after the sharing", 2_000_002, 2 }
    };

    [Theory]
    [MemberData(nameof(OutOfRangeMinutes))]
    public void LaneArrivals_Should_GiveThatIntervalArrivalsOutOfRange_When_AMinuteIsNotAPlausibleNumber(string why, double value, int sharing)
    {
        // CWE-501: the bad minute (14:20, in the second interval) stays with its interval; the others keep their arrivals
        // and the lane's later intervals carry BacklogUnknown, never a throw for the site.
        var plan = StaffingRecommendation.Plan(T0);
        List<MinuteDemand> minutes = [new(T0.AddMinutes(1), LaneCounts.Zero.Add("VIS", 30)), new(T0.AddMinutes(20), LaneCounts.Zero.Add("VIS", value)),
            new(T0.AddMinutes(31), LaneCounts.Zero.Add("VIS", 6))];

        var lanes = StaffingRecommendation.LaneArrivals(plan, minutes, [], "VIS", sharing, false);

        lanes[1].Should().Be(new LaneDemandInterval(plan[1].StartUtc, 15, null, StaffingGap.ArrivalsOutOfRange), why);
        lanes[0].Arrivals.Should().Be(30.0 / sharing, why);
        lanes[2].Arrivals.Should().Be(6.0 / sharing, why);
        lanes.Where((_, k) => k != 1).Should().OnlyContain(i => i.Gap == StaffingGap.None, why);
        var lane = StaffingRecommendation.Recommend(new LaneStaffingInput(0, lanes, 0.5, 4, 20, 10, 0.85));
        lane.Intervals.Select(i => i.Gap).Should().Equal([StaffingGap.None, StaffingGap.ArrivalsOutOfRange, .. Enumerable.Repeat(StaffingGap.BacklogUnknown, 11)], why);
    }

    [Theory]
    [InlineData(1_000_000, 1)]
    [InlineData(999_999.5, 1)]
    [InlineData(2_000_000, 2)]
    [InlineData(0, 1)]
    public void LaneArrivals_Should_KeepTheInterval_When_ItsArrivalsAreWithinTheBound(double value, int sharing)
    {
        var plan = StaffingRecommendation.Plan(T0);

        var lanes = StaffingRecommendation.LaneArrivals(plan, [new MinuteDemand(T0.AddMinutes(20), LaneCounts.Zero.Add("VIS", value))], [], "VIS", sharing, false);

        lanes[1].Gap.Should().Be(StaffingGap.None);
        lanes[1].Arrivals.Should().Be(value / sharing);
    }

    [Fact]
    public void LaneArrivals_Should_GiveArrivalsOutOfRange_When_TheIntervalsSumOverflows()
    {
        // Each minute is finite and within any bound alone; together they pass the largest double.
        var plan = StaffingRecommendation.Plan(T0);
        List<MinuteDemand> minutes = [new(T0.AddMinutes(16), LaneCounts.Zero.Add("VIS", double.MaxValue)), new(T0.AddMinutes(17), LaneCounts.Zero.Add("VIS", double.MaxValue))];

        StaffingRecommendation.LaneArrivals(plan, minutes, [], "VIS", 1, false)[1].Gap.Should().Be(StaffingGap.ArrivalsOutOfRange);
    }
}
