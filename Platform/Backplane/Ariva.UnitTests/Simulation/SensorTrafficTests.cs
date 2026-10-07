using System.Text.Json;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Seed;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-028: the emulator's traffic is what Ingest accepts. Every push of every sensor maps through Ingest's own
/// canonical and Xovis mappers, every event passes the canonical rules and names a line or zone of the demo airport's
/// zone profile (the device's queue zone and its overflow band), the expected counts are what the mappers produce,
/// passengers are conserved (occupancy is always entries minus exits, every exit carries an entry's track id) and S-17
/// sends nothing while the scenario has it offline.
/// </summary>
public sealed class SensorTrafficTests
{
    private static readonly Lazy<ScenarioDay> Day = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }));
    private static readonly DateTime Anchor = new(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>Real time at speed 1: demo minute m of the run is Anchor plus m minutes.</summary>
    private static DateTime WallOf(double minute) => Anchor.AddMinutes(minute);

    private static readonly Lazy<ZoneProfile> Profile = new(() =>
    {
        var terminal = new Airport("DMO", null, "Demo International Airport", "Asia/Dubai").AddTerminal("T1", "Terminal 1", "DMO");
        var arrivals = terminal.AddLevel("ARR", "Arrivals", 0, 100, 60);
        var departures = terminal.AddLevel("DEP", "Departures", 1, 100, 60);
        arrivals.Id = Guid.Parse("0199a000-0000-7000-8000-0000000000a1");
        departures.Id = Guid.Parse("0199a000-0000-7000-8000-0000000000d1");
        return DemoTopologySeed.BuildProfile(arrivals, departures);
    });

    /// <summary>
    /// The names Ingest checks for a device of this queue zone: the queue, the overflow bands feeding it, and their lines;
    /// for a desk zone sensor (ARV-116) also its desks' staff and service zones, which a profile with desk zones has (the
    /// demo's v12 has none, so those sensors are not registered in the demo).
    /// </summary>
    private static HashSet<string> NamesOf(string queueZone, SensorDef sensor = null)
    {
        var queue = Profile.Value.Zones.Single(z => z.Kind == ZoneKind.Queue && z.Name == queueZone);
        var zones = Profile.Value.Zones.Where(z => z == queue || z.QueueZone == queue).ToList();
        var desks = sensor is null ? [] : SensorTraffic.DesksOf(sensor).SelectMany(d => new[] { SensorTraffic.StaffZoneOf(d), SensorTraffic.ServiceZoneOf(d) });
        return [.. zones.Select(z => z.Name), .. Profile.Value.Lines.Where(l => l.Zone is not null && zones.Contains(l.Zone)).Select(l => l.Name), .. desks];
    }

    private static MappedPush Map(SensorPush push, EmulatedDialect dialect)
    {
        using var document = JsonDocument.Parse(push.Json);
        return dialect == EmulatedDialect.Xovis
            ? XovisPushMapper.Map(document.RootElement, new DevicePose(0, 0, 0), 3000)
            : CanonicalPushMapper.Map(document.RootElement, 3000);
    }

    public static TheoryData<string, EmulatedDialect> EverySensor()
    {
        var data = new TheoryData<string, EmulatedDialect>();
        foreach (var sensor in ScenarioModel.Sensors)
        {
            data.Add(sensor.Id, EmulatedDialect.Canonical);
            data.Add(sensor.Id, EmulatedDialect.Xovis);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EverySensor))]
    public void Build_Should_ProduceWhatIngestAccepts_When_TheEveningPeakIsPlayed(string sensorId, EmulatedDialect dialect)
    {
        var sensor = SensorTraffic.Sensor(sensorId);
        var queueZone = SensorTraffic.QueueZoneOf(sensor.Zone);
        var names = NamesOf(queueZone, sensor);

        for (var minute = 1080; minute < 1140; minute++)
        {
            var push = SensorTraffic.Build(Day.Value, sensor, dialect, minute, minute, WallOf, WallOf(minute + 1).AddSeconds(1));
            if (push is null)
            {
                ScenarioDay.SensorOffline(sensorId, minute).Should().BeTrue("only an offline sensor sends nothing");
                continue;
            }

            push.Path.Should().Be($"api/v1/ingest/zones/{queueZone}/{(dialect == EmulatedDialect.Xovis ? "xovis" : "events")}");
            var mapped = Map(push, dialect);
            var events = mapped.Crossings.Cast<CanonicalEvent>().Concat(mapped.Occupancy).Concat(mapped.Intervals).ToList();
            events.Where(e => CanonicalEventRules.Validate(e).Any()).Should().BeEmpty("every event passes the canonical rules");
            mapped.Crossings.Select(c => c.LineName).Concat(mapped.Intervals.Select(i => i.LineName)).Concat(mapped.Occupancy.Select(o => o.ZoneName))
                .Where(n => !names.Contains(n)).Should().BeEmpty("every name is in {0}'s published geometry", queueZone);
            events.Where(e => e.TimeUtc < WallOf(minute) || e.TimeUtc > WallOf(minute + 1)).Should().BeEmpty("events fall in their minute");
            mapped.Ignored.Should().Be(0);
            var accepted = events.Count + (mapped.Status is not null ? 1 : 0);
            accepted.Should().Be(push.ExpectedAccepted, "the emulator expects what Ingest will accept");
            mapped.Crossings.Count.Should().Be(push.Crossings);
            mapped.Occupancy.Count.Should().Be(push.Occupancy);
            mapped.Intervals.Count.Should().Be(push.Intervals);
        }
    }

    [Fact]
    public void Build_Should_ConservePassengers_When_ALeadSensorCountsItsQueueAllDay()
    {
        var day = Day.Value;
        foreach (var lead in ScenarioModel.Sensors.Where(s => SensorTraffic.RoleOf(s) == SensorRole.QueueLead))
        {
            static int Number(string track) => int.Parse(track[(track.LastIndexOf('.') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var entered = new HashSet<int>();
            int? firstEntry = null, previous = null;
            var band = ScenarioModel.Sensors.SingleOrDefault(s => SensorTraffic.RoleOf(s) == SensorRole.OverflowLead && SensorTraffic.QueueZoneOf(s.Zone) == lead.Zone);
            SensorTraffic.HasOverflowBand(lead.Zone).Should().Be(band is not null);
            for (var minute = 0; minute < ScenarioModel.Day; minute++)
            {
                var push = SensorTraffic.Build(day, lead, EmulatedDialect.Canonical, minute, minute, WallOf, WallOf(minute + 1));
                var mapped = Map(push, EmulatedDialect.Canonical);
                // A queue with an overflow band: the lead counts up to the capacity and the band the rest (ARV-064).
                var overflow = band is null ? 0 : Map(SensorTraffic.Build(day, band, EmulatedDialect.Canonical, minute, minute, WallOf, WallOf(minute + 1)),
                    EmulatedDialect.Canonical).Occupancy.Single().Count;
                if (band is not null)
                    mapped.Occupancy.Single().Count.Should().BeLessThanOrEqualTo((int)ScenarioDay.DefaultCaps[lead.Zone]);
                var ins = mapped.Crossings.Where(c => c.Direction == CrossingDirection.In).Select(c => Number(c.TrackId)).ToList();
                var outs = mapped.Crossings.Where(c => c.Direction == CrossingDirection.Out).Select(c => Number(c.TrackId)).ToList();
                foreach (var n in ins)
                {
                    entered.Add(n).Should().BeTrue("a passenger enters once");
                    firstEntry ??= n;
                }

                // Passengers queuing since before midnight entered the previous evening; everyone after entered here, in order.
                outs.Where(n => n >= firstEntry && !entered.Contains(n)).Should().BeEmpty("an exit is a passenger who entered (FIFO)");
                var occupancy = mapped.Occupancy.Single().Count + overflow;
                if (previous is { } p)
                    occupancy.Should().Be(p + ins.Count - outs.Count, "{0} at {1}: the queue and its band are entries minus exits", lead.Zone, ScenarioMath.Clock(minute));
                occupancy.Should().BeGreaterThanOrEqualTo(0);
                previous = occupancy;
            }
        }
    }

    [Fact]
    public void Build_Should_SendNothingForS17_When_TheScenarioHasItOffline()
    {
        var s17 = SensorTraffic.Sensor("S-17");

        SensorTraffic.RoleOf(s17).Should().Be(SensorRole.Heartbeat);
        SensorTraffic.Build(Day.Value, s17, EmulatedDialect.Canonical, 1099, 1, WallOf, Anchor).Should().NotBeNull();
        for (var minute = 1100; minute < 1110; minute++)
            SensorTraffic.Build(Day.Value, s17, EmulatedDialect.Canonical, minute, 1, WallOf, Anchor).Should().BeNull();
        SensorTraffic.Build(Day.Value, s17, EmulatedDialect.Canonical, 1110, 1, WallOf, Anchor).Should().NotBeNull();
    }

    [Fact]
    public void Roles_Should_GiveOneCountingSensorPerZone_When_TheSensorsAreListed()
    {
        var leads = ScenarioModel.Sensors.Where(s => SensorTraffic.RoleOf(s) == SensorRole.QueueLead).Select(s => s.Zone).ToList();
        var overflow = ScenarioModel.Sensors.Where(s => SensorTraffic.RoleOf(s) == SensorRole.OverflowLead).Select(s => s.Zone).ToList();

        leads.Should().BeEquivalentTo(ScenarioModel.Queues.Select(q => q.Id), "every queue has exactly one counting sensor");
        overflow.Should().BeEquivalentTo("SEC-OV", "A-OV", "D-OV");
        SensorTraffic.QueueZoneOf("A-OV").Should().Be("A-VIS");
        SensorTraffic.QueueZoneOf("SEC-OV").Should().Be("SEC-N");
    }

    [Fact]
    public void Build_Should_BeDeterministic_When_TheSameMinuteIsPlayedTwice()
    {
        var sensor = SensorTraffic.Sensor("S-15");
        var a = SensorTraffic.Build(Day.Value, sensor, EmulatedDialect.Canonical, 1085, 7, WallOf, Anchor);
        var b = SensorTraffic.Build(ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }), sensor, EmulatedDialect.Canonical, 1085, 7, WallOf, Anchor);

        b.Json.Should().Be(a.Json);
        a.Crossings.Should().BeGreaterThan(0, "the Visitors wave is arriving at 18:05");
    }

    [Fact]
    public void Roles_Should_GiveTheVisitorsDesksToS18ToS20_When_TheSensorsAreListed()
    {
        // ARV-116: the fourth to sixth sensors over the arrivals Visitors hall report five Visitors desks each.
        var deskSensors = ScenarioModel.Sensors.Where(s => SensorTraffic.RoleOf(s) == SensorRole.DeskZones).ToList();

        deskSensors.Select(s => s.Id).Should().Equal("S-18", "S-19", "S-20");
        deskSensors.SelectMany(SensorTraffic.DesksOf).Should().Equal(ScenarioModel.Queues[ScenarioModel.Q("A-VIS")].Servers, "every Visitors desk once, in order");
        deskSensors.Should().OnlyContain(s => SensorTraffic.DesksOf(s).Count == SensorTraffic.DesksPerSensor);
        SensorTraffic.RoleOf(SensorTraffic.Sensor("S-16")).Should().Be(SensorRole.Heartbeat);
        SensorTraffic.RoleOf(SensorTraffic.Sensor("S-17")).Should().Be(SensorRole.Heartbeat, "S-17's outage stays a heartbeat sensor's");
        ScenarioModel.Sensors.Where(s => s.Zone != "A-VIS").Should().OnlyContain(s => SensorTraffic.DesksOf(s).Count == 0);
    }

    [Fact]
    public void Build_Should_ReportEachDesksZonesFromItsScenarioState_When_ADeskZoneSensorPlaysTheEvening()
    {
        var day = Day.Value;
        var q = ScenarioModel.Q("A-VIS");
        foreach (var sensor in ScenarioModel.Sensors.Where(s => SensorTraffic.RoleOf(s) == SensorRole.DeskZones))
        {
            for (var minute = 1020; minute < 1230; minute++)
            {
                var mapped = Map(SensorTraffic.Build(day, sensor, EmulatedDialect.Canonical, minute, minute, WallOf, WallOf(minute + 1)), EmulatedDialect.Canonical);
                mapped.Crossings.Should().BeEmpty();
                var readings = mapped.Occupancy.ToDictionary(o => o.ZoneName, o => o);
                foreach (var server in day.ServerStates(q, minute).Where(s => SensorTraffic.DesksOf(sensor).Contains(s.Id)))
                {
                    if (server.State == "unknown")
                    {
                        readings.Should().NotContainKey(SensorTraffic.StaffZoneOf(server.Id));
                        continue;
                    }

                    readings[SensorTraffic.StaffZoneOf(server.Id)].Count.Should().Be(server.State is "serving" or "idle" ? 1 : 0, "{0} {1} at {2}", server.Id, server.State, minute);
                    readings[SensorTraffic.ServiceZoneOf(server.Id)].Count.Should().Be(server.State == "serving" ? 1 : 0);
                    readings[SensorTraffic.StaffZoneOf(server.Id)].TimeUtc.Should().Be(WallOf(minute), "a desk's state holds from its minute's start");
                }
            }
        }
    }
}
