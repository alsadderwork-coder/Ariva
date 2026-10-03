using Ariva.Core.Flights;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Flights;

/// <summary>
/// ARV-047, formulas.md F14: each arriving flight's passengers reach the hall from in-block plus the delay over 12
/// minutes with the reference weights; AMAN's lane demand is used when present and the default mix otherwise; the
/// in-block time and the passengers come from the best figure the feeds give; the window, the alert window and the
/// settings are bounded.
/// </summary>
public sealed class ArrivalWaveTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
    private static readonly ArrivalWaveSettings Ten = new() { DelayMinutes = 10 };

    private static ArrivingFlight Flight(string key = "QR900-20261003-A", DateTime? estimated = null, DateTime? landed = null, DateTime? onBlock = null,
        int? seats = null, int? pax = null, AmanLaneDemand aman = null) =>
        new(key, "QR", "900", null, "DOH", "T1", "B12", Now.AddMinutes(30), estimated, landed, onBlock, seats, pax, aman);

    private static AmanLaneDemand OneLane(int passengers) => new(passengers, 0, 0, passengers, 0, 0, Now.AddMinutes(-30));

    [Fact]
    public void Project_Should_SpreadAFlightOverTwelveMinutesAfterTheDelay_When_FormulasF14Example()
    {
        // F14: P = 200, one lane, T = 18:00, d = 10: 6, 12, 18, 22, 24, 24, 22, 20, 16, 14, 12, 10 from 18:10 to 18:21 (sum 200).
        var wave = ArrivalWave.Project([Flight(onBlock: Now, aman: OneLane(200))], Now, 30, Ten);

        var hall = wave.Minutes.Where(m => m.Lanes.Total > 0).ToList();
        hall.Select(m => m.MinuteUtc).Should().Equal(Enumerable.Range(10, 12).Select(i => Now.AddMinutes(i)));
        hall.Select(m => Math.Round(m.Lanes.Vis, 6)).Should().Equal(6, 12, 18, 22, 24, 24, 22, 20, 16, 14, 12, 10);
        wave.Minutes.Sum(m => m.Lanes.Total).Should().BeApproximately(200, 1e-9);
        ArrivalWave.Spread.Sum().Should().BeApproximately(1, 1e-12);
    }

    [Fact]
    public void Split_Should_RemoveTransfersAndTakeTheEgateShare_When_FormulasF14ReferenceMix()
    {
        // F14: P = 200 with the reference mix: CRW 4, CIT 42, RES 24, VIS 70, EG 44 (total 184; 16 transfers removed).
        var lanes = LaneMix.Reference.Split(200).Rounded(6);
        lanes.Should().Be(new LaneCounts(42, 24, 70, 4, 44));
        lanes.Total.Should().BeApproximately(184, 1e-9);
    }

    [Fact]
    public void Demand_Should_UseAmansLaneDemand_When_PresentAndTheDefaultMixOtherwise()
    {
        var aman = new AmanLaneDemand(200, 40, 20, 90, 4, 30, Now);
        ArrivalWave.Demand(Flight(pax: 160, seats: 180, aman: aman), ArrivalWaveSettings.Default)
            .Should().Be(((double?)200, (PassengerSource?)PassengerSource.Aman, (LaneSplitSource?)LaneSplitSource.Aman, new LaneCounts(40, 20, 90, 4, 30)));

        var (passengers, source, split, lanes) = ArrivalWave.Demand(Flight(pax: 100, seats: 180), ArrivalWaveSettings.Default);
        (passengers, source, split).Should().Be(((double?)100, (PassengerSource?)PassengerSource.PaxEstimate, (LaneSplitSource?)LaneSplitSource.DefaultMix));
        lanes.Should().Be(LaneMix.Reference.Split(100));

        ArrivalWave.Demand(Flight(seats: 180), ArrivalWaveSettings.Default).Passengers.Should().BeApproximately(144, 1e-9, "seats times the load factor 0.8");
        ArrivalWave.Demand(Flight(), ArrivalWaveSettings.Default).Should().Be(((double?)null, (PassengerSource?)null, (LaneSplitSource?)null, LaneCounts.Zero));
    }

    [Fact]
    public void InBlock_Should_TakeTheBestFigure_When_TheFeedsGiveSeveral()
    {
        var settings = ArrivalWaveSettings.Default;
        ArrivalWave.InBlock(Flight(estimated: Now.AddMinutes(9), landed: Now, onBlock: Now.AddMinutes(4)), settings).Should().Be((Now.AddMinutes(4), InBlockSource.OnBlock));
        ArrivalWave.InBlock(Flight(estimated: Now.AddMinutes(2), landed: Now), settings).Should().Be((Now.AddMinutes(5), InBlockSource.Landed));
        ArrivalWave.InBlock(Flight(estimated: Now.AddMinutes(9), landed: Now), settings).Should().Be((Now.AddMinutes(9), InBlockSource.Estimated),
            "a later estimate than landing plus taxi-in is the better guess");
        ArrivalWave.InBlock(Flight(estimated: Now.AddMinutes(9)), settings).Should().Be((Now.AddMinutes(9), InBlockSource.Estimated));
        ArrivalWave.InBlock(Flight(), settings).Should().Be((Now.AddMinutes(30), InBlockSource.Scheduled));
    }

    [Fact]
    public void Project_Should_KeepFlightsLandingInTheWindowAndThoseStillArriving_When_Projected()
    {
        var wave = ArrivalWave.Project([
            Flight("IN-WINDOW", estimated: Now.AddMinutes(30), pax: 100),
            Flight("BEYOND", estimated: Now.AddMinutes(31), pax: 100),
            Flight("STILL-ARRIVING", onBlock: Now.AddMinutes(-16), pax: 100),
            Flight("ALL-ARRIVED", onBlock: Now.AddMinutes(-22), pax: 100),
            Flight("NO-PASSENGERS", estimated: Now.AddMinutes(5)),
            null
        ], Now.AddSeconds(30), 30, Ten);

        wave.Flights.Select(f => f.Flight.FlightKey).Should().Equal("STILL-ARRIVING", "NO-PASSENGERS", "IN-WINDOW");
        wave.FlightsWithoutPassengers.Should().Be(1);
        // STILL-ARRIVING (on-block 17:44, hall 17:54 to 18:05) has 6 of its 12 minutes left: 0.11 + 0.10 + 0.08 + 0.07 + 0.06 + 0.05 of the mix.
        var still = LaneMix.Reference.Split(100).Total;
        wave.Minutes.Take(6).Sum(m => m.Lanes.Total).Should().BeApproximately(still * 0.47, 1e-9);
        wave.Minutes[0].MinuteUtc.Should().Be(Now, "minutes start at the current whole minute");
        wave.Minutes.Should().HaveCount(30 + 10 + 12);
        wave.Minutes.Sum(m => m.Lanes.Total).Should().BeApproximately(still * 0.47 + still, 1e-9);
    }

    [Fact]
    public void Project_Should_FloorTheInBlockTimeAndSumTheAlertWindow_When_Projected()
    {
        // In-block 18:00:40 counts from 18:00, so the hall minutes are 18:10 to 18:21; the alert window is 18:05 to 18:24 (20 minutes) and holds all of them.
        var wave = ArrivalWave.Project([Flight(onBlock: Now.AddSeconds(40), aman: OneLane(200))], Now, 30, Ten);
        wave.Flights.Single().HallFirstUtc.Should().Be(Now.AddMinutes(10));
        wave.AlertWindow.Vis.Should().BeApproximately(200, 1e-9);

        // On-block 18:12 with d = 10: hall 18:22 to 18:33; the alert window ends before 18:25 and holds 18:22 to 18:24 (3 + 6 + 9 percent).
        var later = ArrivalWave.Project([Flight(onBlock: Now.AddMinutes(12), aman: OneLane(200))], Now, 30, Ten);
        later.AlertWindow.Vis.Should().BeApproximately(200 * 0.18, 1e-9);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(121)]
    public void Project_Should_RefuseTheWindow_When_OutOfBounds(int minutes)
    {
        var project = () => ArrivalWave.Project([], Now, minutes, ArrivalWaveSettings.Default);
        project.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Settings_Should_BeBoundedAndBindFromConfiguration_When_Read()
    {
        ArrivalWaveSettings.Default.Problems().Should().BeEmpty();
        new ArrivalWaveSettings { DelayMinutes = 7 }.Problems().Should().ContainSingle().Which.Should().Contain("DelayMinutes is 8 to 15");
        new ArrivalWaveSettings { LoadFactor = 0 }.Problems().Should().ContainSingle().Which.Should().Contain("LoadFactor");
        new ArrivalWaveSettings { TaxiInMinutes = -1 }.Problems().Should().ContainSingle().Which.Should().Contain("TaxiInMinutes");
        new ArrivalWaveSettings { Mix = LaneMix.Reference with { Trf = 0.10 } }.Problems().Should().ContainSingle().Which.Should().Contain("add up to 1");
        new ArrivalWaveSettings { Mix = LaneMix.Reference with { EGateShare = 1.5 } }.Problems().Should().ContainSingle().Which.Should().Contain("0 to 1");
        new ArrivalWaveSettings { Mix = null }.Problems().Should().ContainSingle();

        var bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Flights:ArrivalWave:DelayMinutes"] = "12",
            ["Flights:ArrivalWave:Mix:Cit"] = "0.5", ["Flights:ArrivalWave:Mix:Res"] = "0.1", ["Flights:ArrivalWave:Mix:Vis"] = "0.3",
            ["Flights:ArrivalWave:Mix:Crw"] = "0.05", ["Flights:ArrivalWave:Mix:Trf"] = "0.05", ["Flights:ArrivalWave:Mix:EGateShare"] = "0.6"
        }).Build().GetSection(ArrivalWaveSettings.SectionName).Get<ArrivalWaveSettings>();
        bound.DelayMinutes.Should().Be(12);
        bound.Mix.Should().Be(new LaneMix(0.5, 0.1, 0.3, 0.05, 0.05, 0.6));
        bound.Problems().Should().BeEmpty();
    }
}
