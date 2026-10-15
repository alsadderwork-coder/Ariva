using Ariva.Core.Staffing;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Staffing;

/// <summary>
/// ARV-181a, formulas.md F16 (pilot version): the throughput per open desk is the lane queue's exits over its desks' open
/// (Idle plus Serving) desk-minutes in the busy minutes of the last 60 (at least one person waiting per open desk), with at
/// least 30 open desk-minutes, 20 exits and a cycle of 0.2 to 10 minutes; otherwise the fallback configured for the lane's
/// category, or none. Cases T1 to T6 of F16.
/// </summary>
public sealed class LaneThroughputTests
{
    private static readonly DateTime AsOf = new(2026, 10, 15, 13, 59, 0, DateTimeKind.Utc);
    private static readonly StaffingSettings Settings = new();

    private static string[] Desks(int n) => [.. Enumerable.Range(1, n).Select(i => $"DMO/IMM/AR-{i:00}")];

    private static DateTime Minute(int i) => AsOf.AddMinutes(-i);

    // Every desk open (serving) the whole minute for the given minutes back from AsOf (0 is AsOf itself).
    private static List<StaffingDeskMinute> Open(string[] desks, IEnumerable<int> minutes, double serving = 60, double idle = 0) =>
        [.. from i in minutes from d in desks select new StaffingDeskMinute(d, Minute(i), idle, serving, 0, false)];

    private static List<QueueMinuteSample> Queue(IEnumerable<int> minutes, int length, long exits) =>
        [.. minutes.Select(i => new QueueMinuteSample(Minute(i), exits, length, false))];

    private static LaneThroughputResult Measure(string[] desks, List<StaffingDeskMinute> deskMinutes, List<QueueMinuteSample> queue, double? fallback = null) =>
        LaneThroughput.PerOpenDesk(desks, deskMinutes, queue, AsOf, Settings, fallback);

    [Fact]
    public void PerOpenDesk_Should_DivideExitsByOpenDeskMinutes_When_EveryMinuteIsBusy()
    {
        // T1: 4 desks open every minute, 10 waiting, 2 exits a minute: 120 over 240 = 0.5 (cycle 2.0 min, C 7.5).
        var desks = Desks(4);
        var all = Enumerable.Range(0, 60).ToList();

        var result = Measure(desks, Open(desks, all), Queue(all, 10, 2));

        result.Source.Should().Be(DeskThroughputSource.Measured);
        result.RatePerDeskMinute.Should().BeApproximately(0.5, 1e-12);
        result.CycleMinutes.Should().BeApproximately(2.0, 1e-12);
        result.Should().Match<LaneThroughputResult>(r => r.Exits == 120 && r.OpenDeskMinutes == 240 && r.MinutesUsed == 60 && r.MinutesLeftOut == 0 && r.Miss == null);
    }

    [Fact]
    public void PerOpenDesk_Should_LeaveOutMinutesWithFewerWaitingThanOpenDesks_When_TheQueueIsShort()
    {
        // T2: 30 minutes with 3 waiting for 4 open desks and 1 exit: idle time, not capacity; left out (open time over exits
        // would give 90 / 240 = 0.375).
        var desks = Desks(4);
        var all = Enumerable.Range(0, 60).ToList();
        var queue = Queue(all.Where(i => i % 2 == 0), 10, 2).Concat(Queue(all.Where(i => i % 2 == 1), 3, 1)).ToList();

        var result = Measure(desks, Open(desks, all), queue);

        result.RatePerDeskMinute.Should().BeApproximately(0.5, 1e-12);
        result.MinutesUsed.Should().Be(30);
        result.Exits.Should().Be(60);
        result.MinutesLeftOut.Should().Be(0, "a quiet minute is not a fault: it is not used");
    }

    [Fact]
    public void PerOpenDesk_Should_TakeTheConfiguredFallback_When_TooFewOpenDeskMinutes()
    {
        // T3: 20 busy minutes of one desk (20 < 30 open desk-minutes); VIS's fallback 95 s gives r = 60 / 95 (C 9.47).
        var desks = Desks(1);
        var twenty = Enumerable.Range(0, 20).ToList();

        var withFallback = Measure(desks, Open(desks, twenty), Queue(twenty, 5, 1), 95);
        var without = Measure(desks, Open(desks, twenty), Queue(twenty, 5, 1));

        withFallback.Source.Should().Be(DeskThroughputSource.Fallback);
        withFallback.Miss.Should().Be(ThroughputMiss.TooFewOpenDeskMinutes);
        withFallback.RatePerDeskMinute.Should().BeApproximately(60.0 / 95, 1e-12);
        (withFallback.RatePerDeskMinute * 15).Should().BeApproximately(9.4737, 1e-4);
        without.RatePerDeskMinute.Should().BeNull();
        without.Source.Should().BeNull();
        without.MinutesLeftOut.Should().Be(40, "the 40 minutes without data are left out");
    }

    [Fact]
    public void PerOpenDesk_Should_NotMeasure_When_TheCycleTimeIsOutsideItsBounds()
    {
        // T4: 400 exits over 60 open desk-minutes (cycle 0.15 min) and 25 exits over 300 (cycle 12 min).
        var one = Desks(1);
        var all = Enumerable.Range(0, 60).ToList();
        var fast = Queue(all.Take(40), 9, 7).Concat(Queue(all.Skip(40), 9, 6)).ToList();
        var fifteen = Desks(15);
        var twenty = Enumerable.Range(0, 20).ToList();
        var slow = Queue(twenty.Take(5), 20, 2).Concat(Queue(twenty.Skip(5), 20, 1)).ToList();

        var tooFast = Measure(one, Open(one, all), fast, 30);
        var tooSlow = Measure(fifteen, Open(fifteen, twenty), slow);

        tooFast.Exits.Should().Be(400);
        tooFast.Miss.Should().Be(ThroughputMiss.OutOfBounds);
        tooFast.Source.Should().Be(DeskThroughputSource.Fallback);
        tooFast.RatePerDeskMinute.Should().BeApproximately(2, 1e-12);
        tooSlow.Exits.Should().Be(25);
        tooSlow.OpenDeskMinutes.Should().Be(300);
        tooSlow.Miss.Should().Be(ThroughputMiss.OutOfBounds);
        tooSlow.RatePerDeskMinute.Should().BeNull();
        Measure(one, Open(one, all), Queue(all.Take(19), 9, 1).Concat(Queue(all.Skip(19), 9, 0)).ToList()).Miss.Should().Be(ThroughputMiss.TooFewExits, "19 exits are fewer than 20");
    }

    [Fact]
    public void PerOpenDesk_Should_LeaveOutAMinute_When_ADeskOrTheQueueIsDegradedUnknownMissingOrOutOfBounds()
    {
        // T5: as T1 with one degraded desk minute, one desk Unknown for 30 s, one degraded queue length, one desk missing, and
        // one desk minute outside its bounds: 5 minutes left out, 55 used: 110 over 220 is still 0.5.
        var desks = Desks(4);
        var all = Enumerable.Range(0, 60).ToList();
        var minutes = Open(desks, all);
        minutes[0] = minutes[0] with { Degraded = true };
        minutes[4] = minutes[4] with { UnknownSeconds = 30, ServingSeconds = 30 };
        minutes.RemoveAt(8);
        minutes[12] = minutes[12] with { IdleSeconds = 70 };
        var queue = Queue(all, 10, 2);
        queue[4] = queue[4] with { LengthDegraded = true };

        var result = Measure(desks, minutes, queue);

        result.MinutesLeftOut.Should().Be(5);
        result.MinutesUsed.Should().Be(55);
        result.RatePerDeskMinute.Should().BeApproximately(0.5, 1e-12);
        Measure(desks, [.. minutes.Select(m => m with { ServingSeconds = double.NaN })], queue).MinutesLeftOut.Should().Be(60);
    }

    [Theory]
    [InlineData(1_000_000, 0)]
    [InlineData(1_000_001, 1)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(-1, 1)]
    public void PerOpenDesk_Should_LeaveOutAMinute_When_ItsExitsAreOutOfRange(long exits, int leftOut)
    {
        // CWE-501: exits a minute above a million are not plausible; with long.MaxValue in two minutes the sum would wrap.
        var desks = Desks(4);
        var all = Enumerable.Range(0, 60).ToList();
        var queue = Queue(all, 10, 2);
        queue[7] = queue[7] with { Exits = exits };

        var result = Measure(desks, Open(desks, all), queue);

        result.MinutesLeftOut.Should().Be(leftOut);
        result.Exits.Should().Be(leftOut == 0 ? 118 + exits : 118);
        result.Exits.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void PerOpenDesk_Should_NotWaitForADeskNeverHeardInTheWindow_When_TheLaneListsIt()
    {
        // A fifth desk out of service (no minute at all) does not hold every minute back; one heard once does.
        var desks = Desks(5);
        var all = Enumerable.Range(0, 60).ToList();
        var four = Open(desks[..4], all);

        Measure(desks, four, Queue(all, 10, 2)).Should().Match<LaneThroughputResult>(r => r.MinutesUsed == 60 && r.Exits == 120);
        Measure(desks, [.. four, .. Open(desks[4..], [30])], Queue(all, 10, 2)).Should().Match<LaneThroughputResult>(r => r.MinutesUsed == 1 && r.MinutesLeftOut == 59);
    }

    [Fact]
    public void PerOpenDesk_Should_GiveNone_When_NothingIsMeasuredAndNoFallbackIsConfigured()
    {
        // T6: a lane of category FT with no data and no fallback (none is shipped).
        var result = Measure(Desks(2), [], []);

        result.RatePerDeskMinute.Should().BeNull();
        result.Miss.Should().Be(ThroughputMiss.TooFewOpenDeskMinutes);
        Settings.FallbackCycleSecondsFor("DMO", "VIS").Should().BeNull("no fallback cycle time is shipped (decision of 2026-10-15)");
        Measure(Desks(2), [], [], double.NaN).RatePerDeskMinute.Should().BeNull();
        Measure(Desks(2), [], [], 2).RatePerDeskMinute.Should().BeNull("a fallback below 3 s is not plausible");
    }

    [Fact]
    public void PerOpenDesk_Should_IgnoreOtherDesksAndMinutesOutsideTheWindow_When_TheyAreGiven()
    {
        var desks = Desks(4);
        var all = Enumerable.Range(0, 60).ToList();
        var minutes = Open(desks, all);
        minutes.AddRange(Open(["DMO/IMM/AR-99"], all, serving: 0, idle: 0));
        minutes.AddRange(Open(desks, [60, 61, -1]));
        var queue = Queue(all, 10, 2).Concat(Queue([60, 61, -1], 10, 50)).ToList();

        Measure(desks, minutes, queue).Should().Match<LaneThroughputResult>(r => r.Exits == 120 && r.MinutesUsed == 60);
    }
}
