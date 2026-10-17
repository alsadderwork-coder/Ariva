using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Infra.Services.Seed;
using Ariva.Simulation.Api.Emulators.Sensors;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-139b: the illustrative AUH Terminal A arrivals evening (site AUH-TA, seed 9304) tells its scripted story. The
/// arrivals Visitors nowcast (A-VIS) passes 15 minutes at 18:12 during the evening shift handover (R-001, cleared 18:34);
/// sensor Q-RES-04 over the residents' queue is offline from 18:25 to 18:35 (R-003, A-RES degraded); the smart gate fault
/// (SG-05 to SG-34 out of service 18:40 to 19:30) meets the resident-heavy hub wave, so the smart gates' queue (A-EG)
/// passes its 250-person snake from 19:12 and its band A-EG-OV is occupied (R-002 from 19:14 to 19:27). The same seed
/// gives the same outputs (locked in auh-terminal-a-golden.json); the scenario's site matches the ARV-139a seed (counters
/// by lane, smart gates, sensors and their queue zones); and the reference site's outputs are untouched (the parity and
/// golden replay tests of seed 9303 still pass unchanged).
/// </summary>
public sealed class AuhTerminalAScenarioTests
{
    private static readonly Lazy<ScenarioDay> Auh = new(() => ScenarioDay.Run(ScenarioConfig.AuhTerminalA()));

    private static int At(string clock) => int.Parse(clock[..2], CultureInfo.InvariantCulture) * 60 + int.Parse(clock[3..], CultureInfo.InvariantCulture);

    private static int Q(string id) => Auh.Value.Site.Q(id);

    #region Site and seed

    [Fact]
    public void Scenario_Should_PlayAuhTaWithSeed9304_When_NoSeedIsGiven()
    {
        var config = ScenarioConfig.AuhTerminalA();

        config.SiteCode.Should().Be("AUH-TA");
        config.Seed.Should().Be(9304u);
        ScenarioConfig.AuhTerminalADefaultSeed.Should().Be(9304u);
        ScenarioConfig.ForSite("AUH-TA", 7).Should().Be(ScenarioConfig.AuhTerminalA(7));
        ScenarioConfig.ForSite("DMO", 9303).Should().Be(ScenarioConfig.Reference());
        ScenarioConfig.ForSite("XYZ", 1).Should().BeNull();
        Auh.Value.Site.Code.Should().Be("AUH-TA");
        Auh.Value.Schedule.Departures.Should().BeEmpty("AUH-TA is an arrivals hall");
        Auh.Value.Schedule.Arrivals.Should().Contain(f => f.Scripted).And.OnlyContain(f => f.Carrier == "HC" || f.Carrier == "RG" || f.Carrier == "LV");
    }

    [Fact]
    public void Run_Should_Refuse_When_TheSiteIsNotAScenarioSite()
    {
        var run = () => ScenarioDay.Run(new ScenarioConfig { SiteCode = "AUH-TB" });

        run.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("AUH-TB", "a caller's value is never echoed");
    }

    [Fact]
    public void Site_Should_MatchTheIllustrativeSeed_When_ComparedWithItsLayout()
    {
        var site = Auh.Value.Site;

        // The counters by lane and the smart gates (ARV-139a, owner-accepted split: CRW 2, DIP 1, CIT 4, RES 6, GCC 6, VIS 17, TRF 2).
        foreach (var lane in AuhTerminalALayout.CounterLanes)
        {
            var queue = site.Queues[site.Q(AuhTerminalALayout.QueueName(lane.Code))];
            queue.Lane.Should().Be(lane.Code);
            queue.Group.Should().Be("imm");
            queue.Servers.Should().Equal(Enumerable.Range(lane.FirstCounter, lane.LastCounter - lane.FirstCounter + 1).Select(AuhTerminalALayout.CounterCode));
        }

        site.Queues.Where(q => q.Group == "imm").Select(q => q.Servers.Count).Should().Equal(2, 1, 4, 6, 6, 17, 2);
        var gates = site.Queues[site.Q("A-EG")];
        gates.Group.Should().Be("egate");
        gates.Servers.Should().Equal(Enumerable.Range(1, AuhTerminalALayout.SmartGates).Select(AuhTerminalALayout.SmartGateCode), "every smart gate on lane EG only");
        site.Queues.Select(q => q.Id).Should().Equal(AuhTerminalALayout.Lanes.Select(l => AuhTerminalALayout.QueueName(l.Code)));
        site.Bands.Keys.Should().BeEquivalentTo(AuhTerminalALayout.Lanes.Where(l => l.HasOverflow).Select(l => AuhTerminalALayout.OverflowName(l.Code)));

        // The 84 sensors: the seed's codes, each on the seed's queue zone; the band leads on their bands.
        var seeded = AuhTerminalALayout.Sensors();
        site.Sensors.Should().HaveCount(84);
        site.Sensors.Select(s => (s.Id, s.QueueZone)).Should().Equal(seeded.Select(s => (s.Code, s.QueueZoneName)));
        site.Sensors.Should().OnlyContain(s => s.SiteCode == "AUH-TA");
        site.Sensors.Where(s => s.Role == SensorRole.QueueLead).Select(s => s.Id).Should()
            .Equal("Q-CRW-01", "Q-DIP-01", "Q-CIT-01", "Q-RES-01", "Q-GCC-01", "Q-VIS-01", "Q-TRF-01", "Q-EG-01");
        site.Sensors.Where(s => s.Role == SensorRole.OverflowLead).Select(s => (s.Id, s.Zone, s.QueueZone)).Should()
            .Equal(("O-VIS-01", "A-VIS-OV", "A-VIS"), ("O-EG-01", "A-EG-OV", "A-EG"));

        // Every counter's staff and service zones are reported by exactly one desk sensor of its lane, four counters at most.
        var desks = site.Sensors.Where(s => s.Role == SensorRole.DeskZones).ToList();
        desks.Should().HaveCount(13).And.OnlyContain(s => s.Desks.Count > 0 && s.Desks.Count <= 4 && s.Id.StartsWith("D-", StringComparison.Ordinal));
        desks.SelectMany(s => s.Desks).Should().Equal(Enumerable.Range(1, AuhTerminalALayout.Counters).Select(AuhTerminalALayout.CounterCode));
        foreach (var sensor in desks)
            sensor.Desks.Should().OnlyContain(d => site.Queues[site.Q(sensor.QueueZone)].Servers.Contains(d), "{0} watches its own lane's counters", sensor.Id);
    }

    [Fact]
    public void Lanes_Should_SplitEveryPassenger_When_AFlightArrives()
    {
        var site = Auh.Value.Site;
        var mix = new PaxMix(Cit: 0.2, Res: 0.3, Vis: 0.3, Crw: 0.02, Trf: 0.03) { Dip = 0.01, Gcc = 0.14 };

        var lanes = site.SplitArrivals(100, mix);

        site.Lanes.Should().Equal("CRW", "DIP", "CIT", "RES", "GCC", "VIS", "TRF", "EG");
        lanes.Should().HaveCount(8);
        lanes.Sum().Should().BeApproximately(100, 1e-9, "every passenger reaching the hall goes to one lane");
        lanes[^1].Should().BeApproximately(100 * (0.2 * 0.75 + 0.3 * 0.55 + 0.14 * 0.40 + 0.3 * 0.05), 1e-9, "the assumed smart-gate shares");
        ScenarioSites.Dmo.SplitArrivals(100, new PaxMix(0.35, 0.20, 0.35, 0.02, 0.08)).Should()
            .Equal(100 * 0.02, 100 * 0.35 * (1 - 0.4), 100 * 0.20 * (1 - 0.4), 100 * 0.35, 100 * (0.35 + 0.20) * 0.4);
    }

    [Fact]
    public void Site_Should_HaveNoAmanCodes_When_TheAmanEmulatorLooksForThem()
    {
        // ARV-139b decision: AMAN plays the reference site only. AUH-TA's counters and gates have no AMAN code.
        Auh.Value.Site.Queues.SelectMany(q => q.Servers).Should().OnlyContain(s => Ariva.Simulation.Api.Emulators.Aman.AmanCodes.Of(s) == null);
    }

    #endregion

    #region The scripted evening

    [Fact]
    public void Nowcast_Should_PassFifteenMinutesAt1812_When_TheVisitorWaveMeetsTheShiftHandover()
    {
        var day = Auh.Value;
        var q = Q("A-VIS");

        day.State(q, At("18:11")).Nowcast.Should().BeLessThanOrEqualTo(15);
        day.State(q, At("18:12")).Nowcast.Should().BeGreaterThan(15);
        for (var m = At("16:30"); m < At("18:12"); m++)
            (day.State(q, m).Nowcast ?? 0).Should().BeLessThanOrEqualTo(15, "the evening wave at {0} is the first breach of the evening", ScenarioMath.Clock(m));
        day.State(q, At("18:12")).Open.Should().Be(11, "eleven of the seventeen visitors' counters are open during the handover");

        var alert = day.Alerts.Single(a => a.RuleId == "R-001" && a.RaisedAt is > 1080 and < 1110);
        alert.RaisedAt.Should().Be(At("18:12"));
        alert.ClearedAt.Should().Be(At("18:34"));
        alert.Zone.Should().Be("Arrival immigration: Visitors and visa on arrival");
        alert.Text.Should().Be("Arrival immigration: Visitors and visa on arrival: nowcast 16 min");
        alert.Owner.Should().Be("Border shift supervisor");
    }

    [Fact]
    public void Sensor_Should_BeOfflineFrom1825To1835_When_TheAuhEveningRuns()
    {
        var day = Auh.Value;

        day.IsSensorOffline("Q-RES-04", At("18:24")).Should().BeFalse();
        day.IsSensorOffline("Q-RES-04", At("18:25")).Should().BeTrue();
        day.IsSensorOffline("Q-RES-04", At("18:34")).Should().BeTrue();
        day.IsSensorOffline("Q-RES-04", At("18:35")).Should().BeFalse();
        ScenarioDay.SensorOffline("Q-RES-04", At("18:30")).Should().BeFalse("the static helper is the reference site's");
        day.IsSensorOffline("S-17", At("18:25")).Should().BeFalse("S-17 is a DMO sensor");

        var alert = day.Alerts.Single(a => a.RuleId == "R-003");
        alert.Sensor.Should().Be("Q-RES-04");
        alert.RaisedAt.Should().Be(At("18:25"));
        alert.ClearedAt.Should().Be(At("18:35"));
        alert.Text.Should().Be("Sensor Q-RES-04 offline; zone degraded, wait shown as a band");

        var degraded = day.State(Q("A-RES"), At("18:30"));
        degraded.Degraded.Should().BeTrue();
        degraded.Band.Should().NotBeNull("a degraded zone shows its wait as a band");
    }

    [Fact]
    public void SmartGates_Should_SpillIntoTheirBand_When_TheGateFaultMeetsTheHubWave()
    {
        var day = Auh.Value;
        var q = Q("A-EG");
        var above = Enumerable.Range(0, ScenarioModel.Day).Where(m => day.L[q][m + ScenarioModel.Pre] > 250).ToList();

        above.Should().NotBeEmpty();
        ScenarioMath.Clock(above.First()).Should().Be("19:12");
        ScenarioMath.Clock(above.Last()).Should().Be("19:24");
        above.Should().HaveCount(13, "the band is occupied without a break");
        day.SnakeCapacity("A-EG").Should().Be(250);

        var during = day.ServerStates(q, At("19:00"));
        during.Count(s => s.State == "oos").Should().Be(30, "SG-05 to SG-34 are out of service");
        during.Where(s => s.State != "oos").Select(s => s.Id).Should().Equal("SG-01", "SG-02", "SG-03", "SG-04");
        day.ServerStates(q, At("19:30")).Should().NotContain(s => s.State == "oos", "the fault ends at 19:30");

        var overflow = day.Alerts.Single(a => a.RuleId == "R-002");
        overflow.Zone.Should().Be("Arrival immigration: Smart gates");
        overflow.RaisedAt.Should().Be(At("19:14"), "three minutes above the snake");
        overflow.ClearedAt.Should().Be(At("19:27"));
        day.Alerts.Single(a => a.RuleId == "R-001" && a.Zone == "Arrival immigration: Smart gates").RaisedAt.Should().Be(At("19:08"));
    }

    [Fact]
    public void Alerts_Should_TellTheEveningStoryInOrder_When_TheAuhEveningRuns()
    {
        var evening = Auh.Value.Alerts
            .Where(a => a.RaisedAt >= At("17:00") && a.RaisedAt < At("21:00"))
            .Select(a => a.RuleId + " " + ScenarioMath.Clock(a.RaisedAt) + " " + (a.Sensor ?? Auh.Value.Site.Queues[a.Q].Id))
            .ToList();

        evening.Should().Equal("R-001 18:12 A-VIS", "R-003 18:25 Q-RES-04", "R-001 19:08 A-EG", "R-002 19:14 A-EG");
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(42u)]
    [InlineData(9303u)]
    public void ScriptedEvents_Should_StayInTheEvening_When_AnotherSeedRuns(uint seed)
    {
        var day = ScenarioDay.Run(ScenarioConfig.AuhTerminalA(seed));

        day.Alerts.Should().Contain(a => a.RuleId == "R-001" && a.Zone == "Arrival immigration: Visitors and visa on arrival" && a.RaisedAt >= At("18:05") && a.RaisedAt <= At("18:20"));
        day.Alerts.Single(a => a.RuleId == "R-003").RaisedAt.Should().Be(At("18:25"), "the outage is scripted");
        day.Alerts.Should().Contain(a => a.RuleId == "R-002" && a.Zone == "Arrival immigration: Smart gates" && a.RaisedAt >= At("19:05") && a.RaisedAt <= At("19:20"));
    }

    #endregion

    #region Determinism

    [Fact]
    public void Run_Should_GiveIdenticalOutputs_When_TheSameSeedRunsTwice()
    {
        var first = Fingerprint(ScenarioDay.Run(ScenarioConfig.AuhTerminalA()));
        var second = Fingerprint(ScenarioDay.Run(ScenarioConfig.AuhTerminalA()));

        second.Should().Equal(first);
        Fingerprint(ScenarioDay.Run(ScenarioConfig.AuhTerminalA(9305)))["schedule"].Should().NotBe(first["schedule"], "another seed draws other flights");
    }

    [Fact]
    public void Run_Should_GiveTheLockedFingerprints_When_Seed9304Runs()
    {
        var locked = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Simulation/auh-terminal-a-golden.json")))
            .RootElement.GetProperty("fingerprints").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

        Fingerprint(Auh.Value).Should().Equal(locked,
            "the AUH-TA evening is locked like the reference day; a change is reviewed and the value updated with the human's approval");
    }

    /// <summary>SHA-256 of every output family of a day of any site, as ScenarioFingerprints feeds them (float64 little endian, strings UTF-8 plus a zero byte).</summary>
    internal static Dictionary<string, string> Fingerprint(ScenarioDay day)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        var nq = day.Site.NQ;
        string Hash(Action<Action<double?>, Action<string>> feed)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[8];
            void Num(double? v)
            {
                var bits = v is null || double.IsNaN(v.Value) ? 0x7ff8000000000000UL : v.Value == 0 ? 0UL : (ulong)BitConverter.DoubleToInt64Bits(v.Value);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, bits);
                hash.AppendData(buffer);
            }

            void Str(string s)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(s ?? ""));
                hash.AppendData([0]);
            }

            feed(Num, Str);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        o["schedule"] = Hash((num, str) =>
        {
            foreach (var x in day.Schedule.Arrivals)
            {
                str(x.Code); str(x.Carrier);
                foreach (var v in new double[] { x.Sched, x.OnBlock, x.Eibt, x.Seats, x.Booked, x.Load, x.Walk, x.EibtErr, x.Pax, x.Mix.Cit, x.Mix.Res, x.Mix.Vis, x.Mix.Crw, x.Mix.Trf, x.Mix.Dip, x.Mix.Gcc })
                    num(v);
            }
        });
        o["roster"] = Hash((num, _) =>
        {
            foreach (var r in day.Roster)
                foreach (var v in r)
                    num(v);
        });
        o["run"] = Hash((num, _) =>
        {
            for (var q = 0; q < nq; q++)
                foreach (var series in new[] { day.A[q], day.D[q], day.L[q], day.C[q], day.R5[q], day.MeanWait[q] })
                    foreach (var v in series)
                        num(v);
            foreach (var v in day.Rej[0])
                num(v);
        });
        o["servers"] = Hash((num, str) =>
        {
            foreach (var list in day.Servers)
            {
                foreach (var s in list)
                {
                    str(s.Id); num(s.Factor); num(s.OutOfService ? 1 : 0);
                    foreach (var p in s.Pause)
                        num(p);
                }
            }
        });
        o["alerts"] = Hash((num, str) =>
        {
            foreach (var a in day.Alerts)
            {
                foreach (var s in new[] { a.Id, a.RuleId, a.Rule, a.Kind, a.Sensor, a.Zone, a.Severity, a.Owner, a.EscalateTo, a.Perm, a.Text })
                    str(s);
                num(a.Q); num(a.RaisedAt); num(a.ClearedAt); num(a.EscalateAfter); num(a.Bin);
            }
        });
        o["bins"] = Hash((num, str) =>
        {
            for (var q = 0; q < nq; q++)
            {
                for (var bs = 0; bs < ScenarioModel.Day; bs += 15)
                {
                    var b = day.Bin(q, bs, 1439);
                    num(b.P90); num(b.Pax); str(b.Status);
                }
            }
        });
        o["states"] = Hash((num, _) =>
        {
            for (var m = 0; m < ScenarioModel.Day; m += 7)
            {
                for (var q = 0; q < nq; q++)
                {
                    var st = day.State(q, m);
                    foreach (var v in new double?[] { st.Length, st.Rate, st.Capacity, st.Open, st.Paused, st.Active, st.Nowcast, st.Arrivals, st.Served, st.Band?[0], st.Band?[1] })
                        num(v);
                    num(st.Degraded ? 1 : 0);
                }
            }
        });
        o["forecast"] = Hash((num, _) =>
        {
            var fc = day.ForecastFrom(1080);
            for (var q = 0; q < nq; q++)
            {
                foreach (var v in fc.Queues[q].P50)
                    num(v);
                foreach (var v in fc.Queues[q].P90)
                    num(v);
            }

            var rec = day.Recommend(1080, "arr", 8, runs: 4);
            foreach (var id in day.Site.Areas["arr"].Queues)
                foreach (var v in rec.Rec[id])
                    num(v);
        });
        o["alertCount"] = day.Alerts.Count.ToString(CultureInfo.InvariantCulture);
        return o;
    }

    #endregion

    #region Sensor traffic

    private static readonly DateTime Midnight = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    private static SensorPush Push(string sensor, int minute, ScenarioDay day = null) =>
        SensorTraffic.Build(day ?? Auh.Value, SensorTraffic.Sensor("AUH-TA", sensor), EmulatedDialect.Canonical, minute, minute, m => Midnight.AddMinutes(m),
            Midnight.AddMinutes(minute + 1));

    [Fact]
    public void Traffic_Should_CountTheVisitorsQueue_When_TheLeadSensorPushes()
    {
        var push = Push("Q-VIS-01", At("18:10"));

        push.Path.Should().Be("api/v1/ingest/zones/A-VIS/events");
        push.Crossings.Should().BeGreaterThan(0);
        using var body = JsonDocument.Parse(push.Json);
        body.RootElement.GetProperty("occupancy")[0].GetProperty("zoneName").GetString().Should().Be("A-VIS");
        body.RootElement.GetProperty("crossings").EnumerateArray().Select(c => c.GetProperty("lineName").GetString()).Distinct().Should()
            .BeSubsetOf(["A-VIS entry", "A-VIS exit"]);
    }

    [Fact]
    public void Traffic_Should_ReportTheSmartGatesBand_When_TheQueuePassesItsSnake()
    {
        int Count(SensorPush p) => JsonDocument.Parse(p.Json).RootElement.GetProperty("occupancy")[0].GetProperty("count").GetInt32();
        var lead = Push("Q-EG-01", At("19:18"));
        var band = Push("O-EG-01", At("19:18"));

        band.Path.Should().Be("api/v1/ingest/zones/A-EG/events", "the band's device belongs to the queue it feeds");
        Count(lead).Should().Be(250, "the snake holds 250 people");
        Count(band).Should().BePositive("the rest stand in A-EG-OV");
        JsonDocument.Parse(band.Json).RootElement.GetProperty("occupancy")[0].GetProperty("zoneName").GetString().Should().Be("A-EG-OV");
        Count(Push("O-EG-01", At("18:30"))).Should().Be(0);
    }

    [Fact]
    public void Traffic_Should_ReportTheCountersZones_When_ADeskSensorPushes()
    {
        var push = Push("D-VIS-01", At("18:15"));
        using var body = JsonDocument.Parse(push.Json);
        var zones = body.RootElement.GetProperty("occupancy").EnumerateArray().Select(o => o.GetProperty("zoneName").GetString()).ToList();

        push.Path.Should().Be("api/v1/ingest/zones/A-VIS/events");
        zones.Should().BeSubsetOf(["IC-20 staff", "IC-20 service", "IC-21 staff", "IC-21 service", "IC-22 staff", "IC-22 service", "IC-23 staff", "IC-23 service"]);
        zones.Should().NotBeEmpty();
        body.RootElement.TryGetProperty("crossings", out _).Should().BeFalse("only a lead sensor counts crossings");
    }

    [Fact]
    public void Traffic_Should_SendNothing_When_TheSensorIsOffline()
    {
        Push("Q-RES-04", At("18:30")).Should().BeNull();
        Push("Q-RES-04", At("18:35")).Should().NotBeNull();
        Push("Q-RES-02", At("18:30")).Crossings.Should().Be(0, "a heartbeat sensor only reports that it is alive");
    }

    [Fact]
    public void Traffic_Should_Refuse_When_TheSensorIsAnotherSites()
    {
        var reference = ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true });

        var mixed = () => SensorTraffic.Build(reference, SensorTraffic.Sensor("AUH-TA", "Q-VIS-01"), EmulatedDialect.Canonical, 1000, 1, m => Midnight.AddMinutes(m), Midnight);

        mixed.Should().Throw<ArgumentException>();
        SensorTraffic.Sensor("Q-VIS-01").Should().BeNull("the one-argument lookup is the reference site's");
        SensorTraffic.Sensor("AUH-TB", "Q-VIS-01").Should().BeNull();
        SensorTraffic.Sensor("DMO", "S-17").Should().Be(SensorTraffic.Sensor("S-17"));
    }

    #endregion
}
