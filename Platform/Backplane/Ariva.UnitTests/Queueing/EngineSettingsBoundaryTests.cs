using System.Reflection;
using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using FluentAssertions;

namespace Ariva.UnitTests.Queueing;

/// <summary>
/// The engines' settings at their edges, from the ARV-069 mutation triage: each bound is accepted at its limit and refused
/// just past it with one problem that names the setting, so a host never starts on a value the engine was not built for.
/// </summary>
public sealed class EngineSettingsBoundaryTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    public sealed record Bound(Type Settings, string Property, object[] Valid, object[] Invalid, string Named)
    {
        public override string ToString() => $"{Settings.Name}.{Property}";
    }

    public static TheoryData<Bound> Bounds =>
    [
        // F5, F6: the queue state engine
        Q(nameof(QueueEngineSettings.Lateness), [TimeSpan.Zero, TimeSpan.FromMinutes(30)], [-Tick, TimeSpan.FromMinutes(30) + Tick]),
        Q(nameof(QueueEngineSettings.CensorAfter), [TimeSpan.FromMinutes(1), TimeSpan.FromHours(24)], [TimeSpan.FromMinutes(1) - Tick, TimeSpan.FromHours(24) + Tick]),
        Q(nameof(QueueEngineSettings.HandoverWindow), [TimeSpan.Zero, TimeSpan.FromMinutes(30)], [-Tick, TimeSpan.FromMinutes(30) + Tick]),
        Q(nameof(QueueEngineSettings.MaxAhead), [TimeSpan.Zero, TimeSpan.FromHours(1)], [-Tick, TimeSpan.FromHours(1) + Tick]),
        Q(nameof(QueueEngineSettings.OccupancyFreshFor), [Tick, TimeSpan.FromHours(1)], [TimeSpan.Zero, TimeSpan.FromHours(1) + Tick]),
        Q(nameof(QueueEngineSettings.ResidualTolerance), [0, 1000], [-1, 1001]),
        Q(nameof(QueueEngineSettings.MaxOpenEntrants), [1, 100_000], [0, 100_001]),
        Q(nameof(QueueEngineSettings.MaxBufferedEvents), [10, 1_000_000], [9, 1_000_001]),
        Q(nameof(QueueEngineSettings.MaxRememberedTracks), [0, 100_000], [-1, 100_001]),
        Q(nameof(QueueEngineSettings.MaxStepRecords), [1_000, 1_000_000], [999, 1_000_001]),
        Q(nameof(QueueEngineSettings.MaxIntervalPeople), [1, CanonicalEventRules.MaxIntervalCount], [0, CanonicalEventRules.MaxIntervalCount + 1]),
        Q(nameof(QueueEngineSettings.MaxDevicesPerZone), [1, 1024], [0, 1025]),
        Q(nameof(QueueEngineSettings.LateHorizon), [TimeSpan.FromMinutes(1), TimeSpan.FromDays(7)], [TimeSpan.FromMinutes(1) - Tick, TimeSpan.FromDays(7) + Tick]),
        // F6, F7: bins
        B(nameof(BinSettings.BinLength), [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), TimeSpan.FromDays(1)],
            [TimeSpan.FromSeconds(59), TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(7), TimeSpan.FromSeconds(90)]),
        B(nameof(BinSettings.DayStartOffset), [TimeSpan.FromMinutes(-1439), TimeSpan.Zero, TimeSpan.FromMinutes(1439)],
            [TimeSpan.FromHours(-24), TimeSpan.FromHours(24), TimeSpan.FromSeconds(30)]),
        B(nameof(BinSettings.TargetMinutes), [1e-9, 24.0 * 60], [0.0, 24.0 * 60 + 0.01, double.NaN, double.PositiveInfinity]),
        B(nameof(BinSettings.MinimumPassengers), [1, 10_000], [0, 10_001]),
        B(nameof(BinSettings.CensoredDegradedShare), [0.0, 1.0], [-0.01, 1.01, double.NaN]),
        B(nameof(BinSettings.MaxExactWaitsPerBin), [0, 1_000_000], [-1, 1_000_001], "bin bounds"),
        B(nameof(BinSettings.MaxOpenBins), [1, 10_000], [0, 10_001], "bin bounds"),
        B(nameof(BinSettings.MaxMarks), [1, 10_000], [0, 10_001], "bin bounds"),
        // F8: the nowcast
        N(nameof(NowcastSettings.MinimumCycleMinutes), [1e-9, 10.0], [0.0, 10.001, double.NaN, double.PositiveInfinity]),
        N(nameof(NowcastSettings.Beta), [0.0, 1.0], [-0.001, 1.001, double.NaN]),
        N(nameof(NowcastSettings.MinimumRate), [1e-9, 10.0], [0.0, 10.001, double.NaN]),
        N(nameof(NowcastSettings.MaximumRate), [0.02, 100_000.0], [0.01, 100_000.1, double.NaN]),
        // The zone processor
        Z(nameof(ZoneProcessorSettings.ExitWindowMinutes), [1, 60], [0, 61]),
        Z(nameof(ZoneProcessorSettings.MaxPendingOutputs), [100, 1_000_000], [99, 1_000_001]),
        Z(nameof(ZoneProcessorSettings.DeviceSilenceSeconds), [30, 3_600], [29, 3_601]),
        Z(nameof(ZoneProcessorSettings.DeviceForgetHours), [1, 72], [0, 73]),
        Z(nameof(ZoneProcessorSettings.MaxDevices), [1, 4_096], [0, 4_097]),
        Z(nameof(ZoneProcessorSettings.Engine), [], [null], "Engine settings are required"),
        Z(nameof(ZoneProcessorSettings.Bins), [], [null], "Bin settings are required"),
        Z(nameof(ZoneProcessorSettings.Nowcast), [], [null], "Nowcast settings are required"),
        // F12: e-gate coupling
        E(nameof(EgateCouplingSettings.RejectLane), ["CIT", "CRW"], [null, "EG", "cit"]),
        E(nameof(EgateCouplingSettings.LagMinutes), [0, 10], [-1, 11]),
        E(nameof(EgateCouplingSettings.ReferenceRejectRate), [0.0, 1.0], [-0.01, 1.01, double.NaN]),
        E(nameof(EgateCouplingSettings.RateWindowMinutes), [5, 240], [4, 241]),
        E(nameof(EgateCouplingSettings.MinAttempts), [1, 100_000], [0, 100_001]),
        // F14: the arrival wave
        A(nameof(ArrivalWaveSettings.DelayMinutes), [ArrivalWave.MinDelay, ArrivalWave.MaxDelay], [ArrivalWave.MinDelay - 1, ArrivalWave.MaxDelay + 1]),
        A(nameof(ArrivalWaveSettings.TaxiInMinutes), [0, 60], [-1, 61]),
        A(nameof(ArrivalWaveSettings.LoadFactor), [1e-9, 1.0], [0.0, 1.01, double.NaN]),
        A(nameof(ArrivalWaveSettings.Mix), [], [null], "Mix is required"),
        A(nameof(ArrivalWaveSettings.Mix), [], [LaneMix.Reference with { Cit = 2 }], "every share is 0 to 1"),
        A(nameof(ArrivalWaveSettings.Mix), [], [LaneMix.Reference with { Cit = LaneMix.Reference.Cit + 0.1 }], "add up to 1")
    ];

    private static Bound Q(string p, object[] valid, object[] invalid) => new(typeof(QueueEngineSettings), p, valid, invalid, p);
    private static Bound B(string p, object[] valid, object[] invalid, string named = null) => new(typeof(BinSettings), p, valid, invalid, named ?? p);
    private static Bound N(string p, object[] valid, object[] invalid) => new(typeof(NowcastSettings), p, valid, invalid, p);
    private static Bound Z(string p, object[] valid, object[] invalid, string named = null) => new(typeof(ZoneProcessorSettings), p, valid, invalid, named ?? p);
    private static Bound E(string p, object[] valid, object[] invalid) => new(typeof(EgateCouplingSettings), p, valid, invalid, $"{EgateCouplingSettings.SectionName}:{p}");
    private static Bound A(string p, object[] valid, object[] invalid, string named = null) => new(typeof(ArrivalWaveSettings), p, valid, invalid, named ?? $"{ArrivalWaveSettings.SectionName}:{p}");

    /// <summary>A copy of the defaults with one setting changed (records: the compiler's clone, then the init setter).</summary>
    private static object With(Type type, string property, object value)
    {
        var copy = type.GetMethod("<Clone>$")!.Invoke(Activator.CreateInstance(type), null)!;
        type.GetProperty(property)!.SetValue(copy, value);
        return copy;
    }

    private static List<string> Problems(object settings) =>
        [.. (IEnumerable<string>)settings.GetType().GetMethod("Problems", Type.EmptyTypes)!.Invoke(settings, null)!];

    [Theory]
    [MemberData(nameof(Bounds))]
    public void Settings_Should_AcceptTheLimits_AndRefuseJustPastThem_WithOneProblemNamingTheSetting(Bound bound)
    {
        Problems(Activator.CreateInstance(bound.Settings)!).Should().BeEmpty("the defaults are valid");
        foreach (var value in bound.Valid)
            Problems(With(bound.Settings, bound.Property, value)).Should().BeEmpty("{0} = {1} is within its bounds", bound, value);
        foreach (var value in bound.Invalid)
            Problems(With(bound.Settings, bound.Property, value)).Should().ContainSingle("{0} = {1} is out of bounds", bound, value)
                .Which.Should().Contain(bound.Named);
    }

    [Fact]
    public void ZoneProcessorSettings_Should_PassOnTheProblemsOfTheEngineBinsAndNowcast()
    {
        var settings = new ZoneProcessorSettings
        {
            Engine = new QueueEngineSettings { Lateness = TimeSpan.FromHours(1) },
            Bins = new BinSettings { MinimumPassengers = 0 },
            Nowcast = new NowcastSettings { Beta = 2 }
        };
        settings.Problems().Should().HaveCount(3).And.Contain(p => p.Contains("Lateness")).And.Contain(p => p.Contains("MinimumPassengers")).And.Contain(p => p.Contains("Beta"));
        new ZoneProcessorSettings { DeviceForgetHours = 1, DeviceSilenceSeconds = 3_600 }.Problems().Should().ContainSingle()
            .Which.Should().Contain("longer than the silence limit");
    }

    [Theory]
    [InlineData("CIT")]
    [InlineData("RES")]
    [InlineData("VIS")]
    [InlineData("CRW")]
    [InlineData("EG")]
    public void LaneCounts_Should_AddToAndReadOnlyTheNamedLane(string lane)
    {
        var start = new LaneCounts(1, 2, 3, 4, 5);
        var added = start.Add(lane, 10);
        added.Of(lane).Should().Be(start.Of(lane) + 10);
        added.Total.Should().Be(start.Total + 10);
        foreach (var other in new[] { "CIT", "RES", "VIS", "CRW", "EG" }.Where(l => l != lane))
            added.Of(other).Should().Be(start.Of(other), "only {0} changes", lane);
        start.Of("TRF").Should().Be(0);
        FluentActions.Invoking(() => start.Add("TRF", 1)).Should().Throw<ArgumentException>();
    }
}
