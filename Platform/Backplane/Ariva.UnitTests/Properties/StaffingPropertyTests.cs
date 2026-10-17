using Ariva.Core.Staffing;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Properties;

/// <summary>
/// ARV-181a, formulas.md F16 (pilot version): invariants of the staffing recommendation and of the measured throughput per
/// open desk over generated inputs (the fixed cases are in <c>Domain/Staffing</c>). A failure prints CsCheck's shrunk input
/// and seed; the case then joins the formula's own tests.
/// </summary>
public sealed class StaffingPropertyTests
{
    private static readonly int Iter = 5 * Fuzz.Iterations;
    private static readonly DateTime T0 = new(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<(double Queue, double Arrivals, int Minutes, double Rate, double Target, double Cap, int Desks)> Interval =
        Gen.Select(Gen.Double[0, 500], Gen.Double[0, 500], Gen.Int[1, 15], Gen.Double[0.05, 5], Gen.Double[1, 120], Gen.Double[0.5, 1], Gen.Int[0, 60])
            .Select((q, a, d, r, t, u, n) => (q, a, d, r, t, u, n));

    private static bool Meets(double q, double a, int n, double r, int d, double t, double u, double slack)
    {
        var (wait, _) = StaffingRecommendation.Step(q, a, n, r, d);
        var busy = a <= 0 || (n > 0 && a <= u * n * r * d * (1 + slack) + slack);
        return wait is { } w && w <= t * (1 + slack) + slack && busy;
    }

    [Fact]
    public void F16_Choose_Should_BeTheFewestDesksMeetingTheTargetAndTheCap_OrTheLimit()
    {
        Interval.Sample(x =>
        {
            var (n, bound, atLimit, shortfall) = StaffingRecommendation.Choose(x.Queue, x.Arrivals, x.Minutes, x.Rate, x.Target, x.Cap, x.Desks);
            n.Should().BeInRange(0, Math.Max(0, x.Desks));
            if (atLimit)
            {
                n.Should().Be(x.Desks);
                shortfall.Should().BePositive();
                Meets(x.Queue, x.Arrivals, x.Desks, x.Rate, x.Minutes, x.Target, x.Cap, -1e-6).Should().BeFalse("the limit is short of what the lane needs");
                Meets(x.Queue, x.Arrivals, x.Desks + shortfall!.Value, x.Rate, x.Minutes, x.Target, x.Cap, 1e-6).Should().BeTrue("the shortfall closes the gap");
                return;
            }

            Meets(x.Queue, x.Arrivals, n, x.Rate, x.Minutes, x.Target, x.Cap, 1e-6).Should().BeTrue();
            if (n > 0)
                Meets(x.Queue, x.Arrivals, n - 1, x.Rate, x.Minutes, x.Target, x.Cap, -1e-6).Should().BeFalse("one desk fewer misses the target or the cap");
            (bound == StaffingBound.None).Should().Be(n == 0);
        }, iter: Iter);
    }

    [Fact]
    public void F16_Choose_Should_NeverFall_When_TheQueueOrArrivalsRise_AndNeverRise_When_RateTargetOrCapRise()
    {
        Gen.Select(Interval, Gen.Double[0, 100], Gen.Double[0, 1]).Sample((x, more, share) =>
        {
            int Desks(double q, double a, double r, double t, double u) => StaffingRecommendation.Choose(q, a, x.Minutes, r, t, u, 10_000).Desks;
            var n = Desks(x.Queue, x.Arrivals, x.Rate, x.Target, x.Cap);
            Desks(x.Queue + more, x.Arrivals, x.Rate, x.Target, x.Cap).Should().BeGreaterThanOrEqualTo(n);
            Desks(x.Queue, x.Arrivals + more, x.Rate, x.Target, x.Cap).Should().BeGreaterThanOrEqualTo(n);
            Desks(x.Queue, x.Arrivals, x.Rate * (1 + share), x.Target, x.Cap).Should().BeLessThanOrEqualTo(n);
            Desks(x.Queue, x.Arrivals, x.Rate, Math.Min(120, x.Target + more), x.Cap).Should().BeLessThanOrEqualTo(n);
            Desks(x.Queue, x.Arrivals, x.Rate, x.Target, Math.Min(1, x.Cap + share / 2)).Should().BeLessThanOrEqualTo(n);
        }, iter: Iter);
    }

    private static readonly Gen<(double Queue, double[] Arrivals, double Rate, double Target, double Cap, int Desks, int Open)> Lane =
        Gen.Select(Gen.Double[0, 300], Gen.Double[0, 200].Array[1, 13], Gen.Double[0.05, 3], Gen.Double[1, 60], Gen.Double[0.5, 1], Gen.Int[1, 40], Gen.Int[0, 40])
            .Select((q, a, r, t, u, n, o) => (q, a, r, t, u, n, o));

    private static LaneStaffing Recommend(double queue, double[] arrivals, double rate, double target, double cap, int desks, int? open) =>
        StaffingRecommendation.Recommend(new LaneStaffingInput(queue, [.. arrivals.Select((a, k) => new LaneDemandInterval(T0.AddMinutes(15 * k), 15, a))], rate, open, desks,
            target, cap));

    [Fact]
    public void F16_Recommend_Should_StayWithinTheDesks_AndConserveThePassengers_When_CarryingTheBacklog()
    {
        Lane.Sample(x =>
        {
            var lane = Recommend(x.Queue, x.Arrivals, x.Rate, x.Target, x.Cap, x.Desks, x.Open);
            var q = x.Queue;
            for (var k = 0; k < x.Arrivals.Length; k++)
            {
                var i = lane.Intervals[k];
                i.Gap.Should().Be(StaffingGap.None);
                i.Recommended.Should().BeInRange(0, x.Desks);
                var capacity = i.Recommended!.Value * x.Rate * 15;
                var served = Math.Min(q + x.Arrivals[k], capacity);
                i.BacklogEnd!.Value.Should().BeApproximately(q + x.Arrivals[k] - served, 1e-6, "Q_(k+1) = Q_k + A_k - D_k (F9)");
                if (!i.AtLimit)
                    i.WaitRecommendedMinutes!.Value.Should().BeLessThanOrEqualTo(x.Target * (1 + 1e-6) + 1e-6);
                q = i.BacklogEnd.Value;
            }
        }, iter: Iter);
    }

    [Fact]
    public void F16_Recommend_Should_PredictNoShorterWaitWithTheDesksOpenNow_When_TheyAreNeverMoreThanRecommended()
    {
        Lane.Sample(x =>
        {
            var lane = Recommend(x.Queue, x.Arrivals, x.Rate, x.Target, x.Cap, x.Desks, x.Open);
            if (x.Open == 0 || lane.Intervals.Any(i => i.Recommended < x.Open))
                return;
            foreach (var i in lane.Intervals)
                i.WaitOpenNowMinutes!.Value.Should().BeGreaterThanOrEqualTo(i.WaitRecommendedMinutes!.Value - 1e-9);
        }, iter: Iter);
    }

    [Fact]
    public void F16_OpenNowWait_Should_NeverFall_When_TheQueueOrAnyArrivalRises()
    {
        Gen.Select(Lane, Gen.Double[0, 100], Gen.Int[0, 12]).Sample((x, more, at) =>
        {
            if (x.Open == 0)
                return;
            var waits = Recommend(x.Queue, x.Arrivals, x.Rate, x.Target, x.Cap, x.Desks, x.Open).Intervals.Select(i => i.WaitOpenNowMinutes!.Value).ToList();
            var busier = x.Arrivals.Select((a, k) => k == at % x.Arrivals.Length ? a + more : a).ToArray();
            var longer = Recommend(x.Queue + more, x.Arrivals, x.Rate, x.Target, x.Cap, x.Desks, x.Open).Intervals.Select(i => i.WaitOpenNowMinutes!.Value).ToList();
            var later = Recommend(x.Queue, busier, x.Rate, x.Target, x.Cap, x.Desks, x.Open).Intervals.Select(i => i.WaitOpenNowMinutes!.Value).ToList();
            longer.Zip(waits).Should().OnlyContain(p => p.First >= p.Second - 1e-9);
            later.Zip(waits).Should().OnlyContain(p => p.First >= p.Second - 1e-9);
        }, iter: Iter);
    }

    private static readonly DateTime AsOf = new(2026, 10, 15, 13, 59, 0, DateTimeKind.Utc);

    private static readonly Gen<(int Desks, int[] Open, int[] Queue, int[] Exits)> Window =
        Gen.Int[1, 6].SelectMany(desks => Gen.Select(Gen.Int[0, desks].Array[60], Gen.Int[0, 15].Array[60], Gen.Int[0, 12].Array[60])
            .Select((open, queue, exits) => (desks, open, queue, exits)));

    private static (string[] Keys, List<StaffingDeskMinute> Desks, List<QueueMinuteSample> Queue) Build((int Desks, int[] Open, int[] Queue, int[] Exits) x, int copies = 1)
    {
        var keys = Enumerable.Range(1, x.Desks * copies).Select(i => $"S/C/D{i}").ToArray();
        var desks = new List<StaffingDeskMinute>();
        var queue = new List<QueueMinuteSample>();
        for (var i = 0; i < 60; i++)
        {
            var minute = AsOf.AddMinutes(-i);
            for (var d = 0; d < keys.Length; d++)
                desks.Add(new StaffingDeskMinute(keys[d], minute, 0, d % x.Desks < x.Open[i] ? 60 : 0, 0, false));
            queue.Add(new QueueMinuteSample(minute, x.Exits[i] * copies, x.Queue[i] * copies, false));
        }

        return (keys, desks, queue);
    }

    // Another order of the same rows: rotated by the seed, then reversed.
    private static List<T> Rotate<T>(List<T> items, int seed)
    {
        var shift = items.Count == 0 ? 0 : (int)((uint)seed % (uint)items.Count);
        return [.. items.Skip(shift).Concat(items.Take(shift)).Reverse()];
    }

    [Fact]
    public void F16_Throughput_Should_NotDependOnOrder_AndStayWithinItsBounds_AndScaleWithTheLane()
    {
        var settings = new StaffingSettings();
        Gen.Select(Window, Gen.Int).Sample((x, seed) =>
        {
            var (keys, desks, queue) = Build(x);
            var result = LaneThroughput.PerOpenDesk(keys, desks, queue, AsOf, settings, null);
            var shuffled = LaneThroughput.PerOpenDesk(keys.Reverse().ToArray(), Rotate(desks, seed), Rotate(queue, seed), AsOf, settings, null);
            shuffled.Should().BeEquivalentTo(result);
            if (result.Source == DeskThroughputSource.Measured)
                result.CycleMinutes!.Value.Should().BeInRange(settings.CycleMinMinutes, settings.CycleMaxMinutes);
            else
                result.RatePerDeskMinute.Should().BeNull();

            // Twice the desks, the queue and the exits: the same throughput per open desk.
            var (keys2, desks2, queue2) = Build(x, 2);
            var doubled = LaneThroughput.PerOpenDesk(keys2, desks2, queue2, AsOf, settings, null);
            doubled.MinutesUsed.Should().Be(result.MinutesUsed);
            if (result.Source == DeskThroughputSource.Measured && doubled.Source == DeskThroughputSource.Measured)
                doubled.RatePerDeskMinute!.Value.Should().BeApproximately(result.RatePerDeskMinute!.Value, 1e-9);
        }, iter: Iter);
    }

    [Fact]
    public void F16_Throughput_Should_NotChange_When_TheExitsOfMinutesThatWereNotBusyChange()
    {
        var settings = new StaffingSettings();
        Window.Sample(x =>
        {
            var (keys, desks, queue) = Build(x);
            var before = LaneThroughput.PerOpenDesk(keys, desks, queue, AsOf, settings, null);
            // A minute with fewer waiting than open desks says nothing about capacity: any exits there change nothing.
            var quiet = queue.Select((q, i) => x.Queue[i] < x.Open[i] ? q with { Exits = 1_000 } : q).ToList();
            var after = LaneThroughput.PerOpenDesk(keys, desks, quiet, AsOf, settings, null);
            after.Exits.Should().Be(before.Exits);
            after.OpenDeskMinutes.Should().Be(before.OpenDeskMinutes);
        }, iter: Iter);
    }
}
