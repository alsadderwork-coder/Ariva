using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-027: the reference day (seed 9303 at DMO) tells its evening story. The arrivals Visitors nowcast passes
/// 15 minutes at 18:05; sensor S-17 is offline from 18:20 to 18:30; Handler B's check-in breaches its SLA from
/// 19:10, and the 19:00, 19:15 and 19:30 bins end above 15 minutes.
/// </summary>
public sealed class ReferenceScenarioTests
{
    private static readonly Lazy<ScenarioDay> Reference = new(() => ScenarioDay.Run(ScenarioConfig.Reference()));

    private static int At(string clock) => int.Parse(clock[..2], System.Globalization.CultureInfo.InvariantCulture) * 60 +
                                           int.Parse(clock[3..], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Nowcast_Should_PassFifteenMinutesAt1805_When_TheReferenceDayRuns()
    {
        var day = Reference.Value;
        var q = ScenarioModel.Q("A-VIS");

        day.State(q, At("18:04")).Nowcast.Should().BeLessThanOrEqualTo(15);
        day.State(q, At("18:05")).Nowcast.Should().BeGreaterThan(15);
        for (var m = 0; m < At("18:05"); m++)
            (day.State(q, m).Nowcast ?? 0).Should().BeLessThanOrEqualTo(15, "the evening peak at {0} is the first breach", ScenarioMath.Clock(m));

        var alert = day.Alerts.Single(a => a.RuleId == "R-001");
        alert.RaisedAt.Should().Be(At("18:05"));
        alert.Zone.Should().Be("Arrival immigration: Visitors");
        alert.Text.Should().Be("Arrival immigration: Visitors: nowcast 16 min");
        alert.Owner.Should().Be("Border shift supervisor");
        alert.Severity.Should().Be("critical");
    }

    [Fact]
    public void Sensor_Should_BeOfflineFrom1820To1830_When_TheReferenceDayRuns()
    {
        var day = Reference.Value;

        ScenarioDay.SensorOffline("S-17", At("18:19")).Should().BeFalse();
        ScenarioDay.SensorOffline("S-17", At("18:20")).Should().BeTrue();
        ScenarioDay.SensorOffline("S-17", At("18:29")).Should().BeTrue();
        ScenarioDay.SensorOffline("S-17", At("18:30")).Should().BeFalse();
        ScenarioModel.Sensors.Should().HaveCount(59);
        ScenarioModel.Sensors.Single(s => s.Id == "S-17").Zone.Should().Be("A-VIS");

        var alert = day.Alerts.Single(a => a.RuleId == "R-003");
        alert.Sensor.Should().Be("S-17");
        alert.RaisedAt.Should().Be(At("18:20"));
        alert.ClearedAt.Should().Be(At("18:30"));
        alert.Text.Should().Be("Sensor S-17 offline; zone degraded, wait shown as a band");
        alert.EscalateTo.Should().Be("Border systems engineer");

        var degraded = day.State(ScenarioModel.Q("A-VIS"), At("18:25"));
        degraded.Degraded.Should().BeTrue();
        degraded.Band.Should().NotBeNull("a degraded zone shows its wait as a band");
        (degraded.Band[1] - degraded.Band[0]).Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void HandlerB_Should_BreachFrom1910ForThreeBins_When_TheReferenceDayRuns()
    {
        var day = Reference.Value;
        var q = ScenarioModel.Q("CI-C");

        var alert = day.Alerts.Single(a => a.RuleId == "R-004");
        alert.RaisedAt.Should().Be(At("19:10"));
        alert.Bin.Should().Be(At("19:00"));
        alert.Text.Should().Be("Check-in island C (Handler B): bin 19:00 in breach (provisional)");
        alert.Owner.Should().Be("Handler B station manager");
        alert.EscalateTo.Should().Be("Terminal duty manager");

        var breached = Enumerable.Range(0, ScenarioModel.Day / 15)
            .Select(b => day.Bin(q, b * 15, 1439))
            .Where(b => b.P90 > 15)
            .Select(b => ScenarioMath.Clock(b.Start))
            .ToList();
        breached.Should().Equal("19:00", "19:15", "19:30");
        day.Bin(q, At("19:00"), 1439).Status.Should().Be("final");
    }

    [Fact]
    public void Alerts_Should_TellTheEveningStoryInOrder_When_TheReferenceDayRuns()
    {
        var evening = Reference.Value.Alerts
            .Where(a => a.RaisedAt >= At("18:00") && a.RaisedAt < At("21:00"))
            .Select(a => a.RuleId + " " + ScenarioMath.Clock(a.RaisedAt))
            .ToList();

        evening.Should().Equal("R-001 18:05", "R-003 18:20", "R-004 19:10", "R-005 19:11");
    }

    [Fact]
    public void Lite_Should_SkipAlerts_When_Asked()
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });

        day.Alerts.Should().BeEmpty();
        day.SetRules(null).Should().BeEmpty("no rules were given");
    }

    [Fact]
    public void ScenarioMath_Should_RoundHalvesUpLikeJavaScript_When_Rounding()
    {
        ScenarioMath.Round(2.5).Should().Be(3);
        ScenarioMath.Round(-2.5).Should().Be(-2);
        ScenarioMath.Round(0.49999999999999994).Should().Be(0);
        ScenarioMath.Mod(-1, 96).Should().Be(95);
        ScenarioMath.Clock(-1).Should().Be("23:59");
    }

    [Fact]
    public void Mulberry32_Should_MatchTheReferenceStream_When_Seeded()
    {
        // First draws of mulberry32(mix32(9303 ^ 0x5eed1234)), from sim.js.
        ScenarioMath.Mix32(9303u ^ 0x5eed1234u).Should().Be(170758156u);
        var r = new Mulberry32(ScenarioMath.Mix32(9303u ^ 0x5eed1234u));

        r.Next().Should().Be(0.5185956982895732);
        r.Next().Should().Be(0.3318164541851729);
        r.Next().Should().Be(0.5460337384138256);
        ScenarioMath.H3(9303u ^ 0x1234567u, 5, 77).Should().Be(0.2019944277126342);
        ScenarioMath.StrHash("ADH-1").Should().Be(3038699234u);
    }

    [Fact]
    public void Show_Should_MatchThePowShapeWithinAFewUlp_When_Recomputed()
    {
        var recomputed = ScenarioModel.ShowShape();

        ScenarioModel.Show.Should().HaveCount(ScenarioModel.ShowLen);
        ScenarioModel.Show.Sum().Should().BeApproximately(1, 1e-12);
        for (var k = 0; k < recomputed.Length; k++)
            ScenarioModel.Show[k].Should().BeApproximately(recomputed[k], Math.Abs(recomputed[k]) * 1e-14, "weight {0} is the same shape", k);
    }
}
