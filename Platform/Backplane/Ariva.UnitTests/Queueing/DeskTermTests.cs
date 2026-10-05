using System.Text.Json;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Sensing;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// ARV-064: the desk term of the nowcast (F8). The desks serving a queue give n_open and the cycle time from their
/// closed minutes, and a zone with a fresh term blends it with the exit rate, so its nowcast is no longer flagged for
/// lacking desk state; a stale or missing term leaves the exit rate alone, flagged as before.
/// </summary>
public sealed class DeskTermTests
{
    private static readonly DateTime T = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);

    private static DeskMinuteSample Minute(string desk, int minute, double idle = 0, double serving = 60, double unknown = 0, int transactions = 2, bool degraded = false) =>
        new(desk, T.AddMinutes(minute), idle, serving, unknown, transactions, degraded);

    [Fact]
    public void Compute_Should_CountOpenDesksAndTheCycleTime_When_DesksCloseMinutes()
    {
        List<DeskMinuteSample> minutes =
        [
            .. Enumerable.Range(0, 5).Select(m => Minute("DMO/IMM/AR-08", m)),
            .. Enumerable.Range(0, 5).Select(m => Minute("DMO/IMM/AR-09", m, idle: 20, serving: 40)),
            .. Enumerable.Range(0, 5).Select(m => Minute("DMO/IMM/AR-10", m, idle: 0, serving: 0, transactions: 0))
        ];

        var term = DeskTerms.Compute(minutes, 5, T.AddMinutes(10));

        term.AsOfMinuteUtc.Should().Be(T.AddMinutes(4));
        term.OpenServers.Should().Be(2, "the third desk was closed all minute");
        term.Desks.Should().Be(3);
        term.CycleMinutes.Should().BeApproximately(10.0 / 20, 1e-9, "10 open minutes over 20 transactions");
        term.Degraded.Should().BeFalse();
    }

    [Fact]
    public void Compute_Should_FlagTheTerm_When_ADeskIsUnknownDegradedOrBehind()
    {
        var unknown = DeskTerms.Compute([Minute("A", 0), Minute("B", 0, serving: 20, unknown: 40)], 5, T.AddMinutes(1));
        var degraded = DeskTerms.Compute([Minute("A", 0), Minute("B", 0, degraded: true)], 5, T.AddMinutes(1));
        var behind = DeskTerms.Compute([Minute("A", 0), Minute("A", 1), Minute("B", 0)], 5, T.AddMinutes(2));

        unknown.Degraded.Should().BeTrue();
        unknown.OpenServers.Should().Be(1);
        DeskTerms.Compute([Minute("A", 0, serving: 0, unknown: 60), Minute("B", 0, serving: 0, unknown: 60)], 5, T.AddMinutes(1))
            .Should().BeNull("desks that are all Unknown say nothing about how many are open");
        DeskTerms.Compute([Minute("A", 0, serving: 0, transactions: 0), Minute("B", 0, serving: 0, unknown: 60)], 5, T.AddMinutes(1))
            .Should().BeNull("one closed desk and one Unknown is not \"nothing open\"");
        DeskTerms.Compute([Minute("A", 0, serving: 0, transactions: 0)], 5, T.AddMinutes(1)).OpenServers
            .Should().Be(0, "every desk known closed is nothing open");
        degraded.Degraded.Should().BeTrue();
        behind.Degraded.Should().BeTrue("desk B's feed stopped a minute before desk A's");
        behind.OpenServers.Should().Be(1, "only the desks of the latest minute are counted");
    }

    [Fact]
    public void Compute_Should_TakeTheLaneCycleTime_When_TheBorderSystemGivesOne()
    {
        List<DeskMinuteSample> minutes = [Minute("A", 0, transactions: 6), Minute("B", 0, idle: 30, serving: 30, transactions: 0)];

        DeskTerms.Compute(minutes, 5, T.AddMinutes(1), laneCycleMinutes: 1.7).CycleMinutes.Should().Be(1.7, "AMAN's own cycle statistics win");
        DeskTerms.Compute(minutes, 5, T.AddMinutes(1), laneCycleMinutes: double.NaN).CycleMinutes.Should().BeApproximately(2.0 / 6, 1e-9, "a cycle that is not a number falls back");
        DeskTerms.Compute(minutes, 5, T.AddMinutes(1), laneCycleMinutes: 0).CycleMinutes.Should().BeApproximately(2.0 / 6, 1e-9);
    }

    [Fact]
    public void Compute_Should_GiveNoCycle_When_NoTransactionWasSeen()
    {
        var term = DeskTerms.Compute([Minute("A", 0, transactions: 0), Minute("A", 1, transactions: 0)], 5, T.AddMinutes(2));

        term.OpenServers.Should().Be(1);
        term.CycleMinutes.Should().BeNull();
    }

    [Fact]
    public void Compute_Should_IgnoreMinutesAfterTheLimitAndOutsideTheWindow()
    {
        List<DeskMinuteSample> minutes = [Minute("A", -20, transactions: 100), Minute("A", 0, transactions: 3), Minute("A", 5, transactions: 50)];

        var term = DeskTerms.Compute(minutes, 5, T.AddMinutes(1));

        term.AsOfMinuteUtc.Should().Be(T);
        term.CycleMinutes.Should().BeApproximately(1.0 / 3, 1e-9);
        DeskTerms.Compute([], 5, T).Should().BeNull();
        DeskTerms.Compute([Minute("A", 0, idle: double.NaN)], 5, T.AddMinutes(1)).Should().BeNull("a reading that is not a number is not used");
        var bad = () => DeskTerms.Compute([], 0, T);
        bad.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static readonly DateTime Midnight = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
    private static readonly QueueZoneGeometry Geometry = new("A-VIS", new HashSet<string> { "A-VIS entry" }, new HashSet<string> { "A-VIS exit" },
        new HashSet<string>(), new HashSet<string>());

    private static DateTime WallOf(double minute) => Midnight.AddMinutes(minute);

    // The reference evening's S-15 traffic (as in ZoneProcessorTests), each minute offered with the given desk term.
    private static List<QueueLiveMinute> Evening(Func<int, DeskTerm> desks)
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });
        var sensor = SensorTraffic.Sensor("S-15");
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 3);
        var live = new List<QueueLiveMinute>();
        for (var minute = 1020; minute < 1110; minute++)
        {
            var received = WallOf(minute + 1);
            var push = SensorTraffic.Build(day, sensor, EmulatedDialect.Canonical, minute, minute, WallOf, received);
            using var document = JsonDocument.Parse(push.Json);
            var mapped = CanonicalPushMapper.Map(document.RootElement, 3000);
            T Stamp<T>(T batch) where T : SensingBatch
            {
                batch.DeviceId = Guid.Parse("00000000-0000-0000-0000-000000000015");
                batch.DeviceCode = "S-15";
                batch.SiteCode = "DMO";
                batch.QueueZoneName = "A-VIS";
                batch.Dialect = "canonical";
                batch.Commissioned = true;
                batch.ReceivedUtc = received;
                batch.Clock = new ClockReading(0, true, ClockState.Ok);
                return batch;
            }

            zone.UseDesks(desks(minute));
            if (mapped.Crossings.Count > 0)
                zone.Offer(Stamp(new VendorLineCrossingBatch { Crossings = [.. mapped.Crossings.Select(c => new Sensed<LineCrossing>(c, c.TimeUtc, SensedFlags.None))] }), DateTime.MaxValue);
            if (mapped.Occupancy.Count > 0)
                zone.Offer(Stamp(new ZoneOccupancyBatch { Occupancy = [.. mapped.Occupancy.Select(o => new Sensed<ZoneOccupancy>(o, o.TimeUtc, SensedFlags.None))] }), DateTime.MaxValue);
            live.AddRange(zone.Drain().Live);
        }

        return live;
    }

    [Fact]
    public void Zone_Should_BlendTheDeskTerm_When_ItIsFresh()
    {
        var withDesks = Evening(minute => new DeskTerm(WallOf(minute - 2), 6, 1.5, false, 15)).ToDictionary(l => l.MinuteUtc);
        var exitsOnly = Evening(_ => null).ToDictionary(l => l.MinuteUtc);
        var minute = WallOf(1090);

        exitsOnly[minute].NowcastDegraded.Should().BeTrue("the exit rate alone lags every desk change (F8)");
        withDesks[minute].NowcastDegraded.Should().BeFalse("desk state and cycle time are known");
        withDesks[minute].Throughput.Should().BeApproximately(0.5 * (6 / 1.5) + 0.5 * exitsOnly[minute].Throughput!.Value, 1e-9, "beta 0.5 blends the two terms");
        withDesks[minute].QueueLength.Should().Be(exitsOnly[minute].QueueLength, "desks change the nowcast, never the queue");
    }

    [Fact]
    public void Zone_Should_IgnoreTheDeskTerm_When_ItIsStaleOrDegraded()
    {
        var stale = Evening(minute => new DeskTerm(WallOf(minute - 10), 6, 1.5, false, 15)).ToDictionary(l => l.MinuteUtc);
        var degraded = Evening(minute => new DeskTerm(WallOf(minute - 1), 6, 1.5, true, 15)).ToDictionary(l => l.MinuteUtc);
        var closed = Evening(minute => new DeskTerm(WallOf(minute - 1), 0, null, false, 15)).ToDictionary(l => l.MinuteUtc);
        var minute = WallOf(1090);

        stale[minute].NowcastDegraded.Should().BeTrue("a term older than DeskTermFreshMinutes is not used");
        degraded[minute].NowcastDegraded.Should().BeTrue("an Unknown desk makes the estimate a band");
        closed[minute].NoService.Should().Be(NoServiceReason.NothingOpen);
        closed[minute].NowcastMinutes.Should().BeNull();
    }

    [Fact]
    public void Zone_Should_NotKeepTheDeskTerm_When_Restored()
    {
        var zone = new ZoneProcessor("DMO/A-VIS", Geometry, 3);
        zone.UseDesks(new DeskTerm(WallOf(1000), 4, 1, false, 4));

        var restored = ZoneProcessor.Restore("DMO/A-VIS", Geometry, 3, new ZoneProcessorSettings(), zone.Capture(), WallOf(1440));

        zone.Desks.Should().NotBeNull();
        restored.Desks.Should().BeNull("the host gives the term again; the snapshot carries only the stream's own state");
        new ZoneProcessorSettings { DeskTermFreshMinutes = 0 }.Problems().Should().Contain(p => p.Contains("DeskTermFreshMinutes"));
    }
}
