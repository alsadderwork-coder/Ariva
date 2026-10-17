using System.Collections.Frozen;
using System.Globalization;
using Ariva.Simulation.Api.Emulators.Sensors;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>
/// The illustrative AUH Terminal A arrivals hall as a scenario site (ARV-139b): the queues, counters, smart gates and
/// sensors of the ARV-139a seed (site AUH-TA, docs/demo/auh-terminal-a.md) and an arrivals evening built from assumed
/// flight waves, with its own default seed (9304).
/// <para>
/// From the seed (its cited facts and its owner-accepted assumptions): 38 counters IC-01 to IC-38 split CRW 2, DIP 1,
/// CIT 4, RES 6, GCC 6, VIS 17 and TRF 2; 34 smart gates SG-01 to SG-34 on lane EG only; queue zones A-CRW, A-DIP, A-CIT,
/// A-RES, A-GCC, A-VIS, A-TRF and A-EG; overflow bands A-VIS-OV and A-EG-OV; 84 sensors with the seed's codes, each on
/// its lane's queue zone (Q- over a queue, O- over a band, D- over up to four counters).
/// </para>
/// <para>
/// ASSUMPTIONS of this scenario (none is a public fact, and none is the airport's data): every flight, carrier code,
/// load and walk; the flight waves (banks) and the scripted evening; the passenger mix per carrier; the smart-gate
/// share per lane; the service time per lane; the roster and its shift handover; the smart gate fault; the sensor
/// outage; the snake capacities of A-VIS and A-EG (the stanchioned snake inside each zone, smaller than the zone's
/// area capacity); rejected smart-gate attempts joining the visitors' counters; the passengers counted being those
/// who reach the arrivals hall (transfers who stay airside are not in the loads; the TRF lane serves the few who
/// pass immigration to connect, itself an assumption of the seed). Carrier codes HC, RG and LV are fictional: any
/// match with a real airline's code is coincidental.
/// </para>
/// <para>
/// The scripted evening (seed 9304; ScenarioAuhTerminalATests reproduces it): a visitor-heavy long-haul wave lands
/// while half the visitors' counters change shift, so the arrivals Visitors nowcast (A-VIS) passes 15 minutes
/// (R-001); sensor Q-RES-04 over the residents' queue is offline (R-003, the zone degraded); a smart gate fault takes
/// 28 of the 34 gates out of service while a resident-heavy hub wave lands, so the smart gates' queue (A-EG) passes
/// its snake capacity and spills into the A-EG-OV band (R-002).
/// </para>
/// </summary>
internal sealed class AuhTerminalAScenarioSite : ScenarioSite
{
    #region Identity

    public const string SiteCodeValue = "AUH-TA";

    public const uint DefaultSeedValue = 9304;

    #endregion

    #region Lanes (from the ARV-139a seed)

    /// <summary>A counter lane of the seed: its code, label, first and last counter (IC-nn), and its service seconds per person (ASSUMPTION).</summary>
    internal sealed record CounterLane(string Code, string Label, int FirstCounter, int LastCounter, double Svc);

    /// <summary>The seed's split of the 38 counters by lane (owner-accepted assumption, 2026-10-07); service times are this scenario's assumptions.</summary>
    internal static readonly IReadOnlyList<CounterLane> CounterLanes =
    [
        new("CRW", "Crew", 1, 2, 25),
        new("DIP", "Diplomatic", 3, 3, 45),
        new("CIT", "Citizens", 4, 7, 30),
        new("RES", "Residents", 8, 13, 40),
        new("GCC", "GCC nationals", 14, 19, 35),
        new("VIS", "Visitors and visa on arrival", 20, 36, 80),
        new("TRF", "Transfer (assumption)", 37, 38, 50)
    ];

    public const int Counters = 38;
    public const int SmartGates = 34;

    /// <summary>ASSUMPTION: seconds per person at a smart gate.</summary>
    public const double SmartGateSvc = 20;

    /// <summary>ASSUMPTION: the share of each group that uses the smart gates (residents and eligible nationals; most first-time visitors at a counter).</summary>
    internal static readonly FrozenDictionary<string, double> SmartGateShare = new Dictionary<string, double>
    {
        ["CIT"] = 0.75, ["RES"] = 0.55, ["GCC"] = 0.40, ["VIS"] = 0.05
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string CounterCode(int n) => "IC-" + n.ToString("00", CultureInfo.InvariantCulture);

    public static string SmartGateCode(int n) => "SG-" + n.ToString("00", CultureInfo.InvariantCulture);

    public static string QueueName(string lane) => "A-" + lane;

    public static string BandName(string lane) => "A-" + lane + "-OV";

    #endregion

    #region Carriers and flights (ASSUMPTIONS)

    /// <summary>A fictional carrier of the scenario and its passenger mix (the shares of those reaching the arrivals hall).</summary>
    internal sealed record AuhCarrier(string Code, string Description, PaxMix Mix);

    internal static readonly FrozenDictionary<string, AuhCarrier> Carriers = new Dictionary<string, AuhCarrier>
    {
        ["HC"] = new("HC", "Hub carrier, mixed network", new PaxMix(Cit: 0.15, Res: 0.45, Vis: 0.30, Crw: 0.015, Trf: 0.03) { Dip = 0.005, Gcc = 0.05 }),
        ["RG"] = new("RG", "Regional carrier, GCC routes", new PaxMix(Cit: 0.16, Res: 0.31, Vis: 0.19, Crw: 0.02, Trf: 0.01) { Dip = 0.01, Gcc = 0.30 }),
        ["LV"] = new("LV", "Long-haul leisure routes", new PaxMix(Cit: 0.04, Res: 0.20, Vis: 0.70, Crw: 0.015, Trf: 0.02) { Dip = 0.005, Gcc = 0.02 })
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The day-ahead forecast's generic mix.</summary>
    internal static readonly PaxMix AuhBaseMix = new(Cit: 0.125, Res: 0.34, Vis: 0.385, Crw: 0.016, Trf: 0.018) { Dip = 0.006, Gcc = 0.11 };

    internal static readonly int[] Seats = [174, 198, 232, 262, 290, 299, 328, 354, 380];

    /// <summary>The flight waves of the day (on-block minutes from, to, and flights), besides the scripted evening.</summary>
    internal static readonly IReadOnlyList<(int From, int To, int N)> ArrBanks =
    [
        (0, 150, 10), (300, 480, 12), (540, 840, 9), (900, 1050, 7), (1050, 1200, 4), (1200, 1320, 6), (1320, 1430, 10)
    ];

    /// <summary>The scripted evening's flights: a visitor-heavy long-haul wave, then a resident-heavy hub wave.</summary>
    internal static readonly IReadOnlyList<ScriptedArrival> ScriptArrivals =
    [
        new("LV 401", "LV", 1068, 1071, 262, 0.90, 0.92, 12, 1),
        new("LV 518", "LV", 1080, 1079, 198, 0.87, 0.90, 12, -1),
        new("HC 205", "HC", 1112, 1115, 380, 0.88, 0.90, 13, 1),
        new("HC 311", "HC", 1118, 1120, 328, 0.86, 0.90, 14, 0),
        new("RG 129", "RG", 1120, 1124, 232, 0.84, 0.88, 12, 0),
        new("HC 427", "HC", 1125, 1128, 380, 0.90, 0.93, 15, -1),
        new("RG 216", "RG", 1130, 1133, 198, 0.82, 0.86, 11, 1),
        new("RG 340", "RG", 1135, 1137, 232, 0.87, 0.90, 12, 0)
    ];

    #endregion

    #region The scripted evening (ASSUMPTIONS)

    /// <summary>The visitors' counters through the evening shift handover.</summary>
    internal static readonly IReadOnlyList<RosterLine> AuhScriptRoster =
    [
        new("A-VIS", 1050, 1110, 11, "Evening shift handover"),
        new("A-VIS", 1110, 1140, 15, "Evening shift handover"),
        new("A-VIS", 1140, 1260, 17, "Evening shift fully on"),
        new("A-RES", 1110, 1185, 6, "Planned for the hub wave"),
        new("A-GCC", 1110, 1185, 6, "Planned for the hub wave")
    ];

    /// <summary>The smart gate fault: gates SG-05 to SG-34 out of service, 18:40 to 19:30.</summary>
    internal static readonly (int FirstGate, int LastGate, int From, int To, string Note) GateFault = (5, 34, 1120, 1170, "Smart gate fault");

    /// <summary>Today's outage: Q-RES-04 over the residents' queue, 18:25 to 18:35.</summary>
    internal static readonly IReadOnlyList<Outage> AuhOutages = [new("Q-RES-04", "A-RES", 1105, 1115)];

    /// <summary>The snake capacities: the seed's area capacities (one person per 1.2 m2), except the snakes in front of bands.</summary>
    internal static readonly FrozenDictionary<string, double> SnakeCaps = new Dictionary<string, double>
    {
        ["A-CRW"] = 46, ["A-DIP"] = 22, ["A-CIT"] = 219, ["A-RES"] = 331, ["A-GCC"] = 331, ["A-VIS"] = 300, ["A-TRF"] = 46, ["A-EG"] = 250
    }.ToFrozenDictionary(StringComparer.Ordinal);

    #endregion

    public AuhTerminalAScenarioSite()
    {
        Code = SiteCodeValue;
        Name = "Zayed International Airport, Terminal A arrivals (illustrative)";
        DefaultSeed = DefaultSeedValue;
        Date = ScenarioModel.Date;
        Airport = "AUH";
        AodbTerminal = "A";
        // ASSUMPTION: origins of the scenario's flights (any airport; picked per flight code).
        AodbAirports = ["LHR", "BOM", "DEL", "MNL", "CAI", "JED", "KHI", "CMB", "IST", "CDG", "BKK", "AMM"];

        var queues = new List<QueueDef>();
        foreach (var lane in CounterLanes)
        {
            var servers = Enumerable.Range(lane.FirstCounter, lane.LastCounter - lane.FirstCounter + 1).Select(CounterCode).ToList();
            queues.Add(new QueueDef(queues.Count, QueueName(lane.Code), "arr", "imm", lane.Code, "arr", lane.Svc, lane.Label,
                "Arrival immigration: " + lane.Label, "counters", null, null, servers));
        }

        queues.Add(new QueueDef(queues.Count, QueueName("EG"), "arr", "egate", "EG", "arr", SmartGateSvc, "Smart gates", "Arrival immigration: Smart gates",
            "smart gates", null, null, Enumerable.Range(1, SmartGates).Select(SmartGateCode).ToList()));
        Queues = queues;

        Areas = new Dictionary<string, AreaDef>
        {
            ["arr"] = new("arr", "Arrival immigration", "counters", [.. CounterLanes.Select(l => QueueName(l.Code))], "imm.forecast")
        }.ToFrozenDictionary(StringComparer.Ordinal);
        RecTarget = new Dictionary<string, double> { ["arr"] = 10 }.ToFrozenDictionary(StringComparer.Ordinal);
        Lanes = [.. CounterLanes.Select(l => l.Code), "EG"];
        ArrivalQueues = [.. Lanes.Select(l => Q(QueueName(l)))];
        BaseMix = AuhBaseMix;
        EgateReject = 0.07;
        // ASSUMPTION: a longer walk than DMO's in the X-shaped terminal.
        StandardWalk = 14;
        HasDepartures = false;
        EgShareDep = null;

        Bands = new Dictionary<string, string> { [BandName("VIS")] = QueueName("VIS"), [BandName("EG")] = QueueName("EG") }.ToFrozenDictionary(StringComparer.Ordinal);
        Sensors = BuildSensors(queues);
        Outages = AuhOutages;
        OutageHistory = [];
        ScriptRoster = AuhScriptRoster;
        OutOfService = [.. Enumerable.Range(GateFault.FirstGate, GateFault.LastGate - GateFault.FirstGate + 1)
            .Select(n => new OutOfService(SmartGateCode(n), GateFault.From, GateFault.To, GateFault.Note))];
        SeedAllocations = [];
        DefaultCaps = SnakeCaps;

        IReadOnlyList<string> queueIds = [.. Lanes.Select(QueueName)];
        SeedRules =
        [
            new("R-001", "Nowcast above 15 min", queueIds, "nowcast", "gt", 15, 10, 12, 1, 1, "critical", "Border shift supervisor", 10, "Border operations duty officer", ""),
            new("R-002", "Overflow band occupied", queueIds, "overflow", "is", null, null, null, 3, 3, "warning", "zone", 15, "auto", ""),
            new("R-003", "Sensor offline", [.. queueIds, .. Bands.Keys.Order(StringComparer.Ordinal)], "sensor", "is", null, null, null, 1, 1, "warning", "zone", 15, "systems", "")
        ];
    }

    /// <summary>
    /// Counters by lane: crew, diplomats, citizens, residents and GCC nationals not using the smart gates, visitors not
    /// using them, transfers (the TRF counters); then everyone at the smart gates.
    /// </summary>
    /// <summary>ASSUMPTION: at least four visitors' counters, two each for citizens, residents and GCC nationals, one elsewhere.</summary>
    public override int MinimumServers(QueueDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return def.Lane switch
        {
            "VIS" => 4,
            "CIT" or "RES" or "GCC" => 2,
            _ => 1
        };
    }

    public override double[] SplitArrivals(double pax, PaxMix mix)
    {
        ArgumentNullException.ThrowIfNull(mix);
        double eCit = SmartGateShare["CIT"], eRes = SmartGateShare["RES"], eGcc = SmartGateShare["GCC"], eVis = SmartGateShare["VIS"];
        double cit = pax * mix.Cit, res = pax * mix.Res, gcc = pax * mix.Gcc, vis = pax * mix.Vis;
        return [pax * mix.Crw, pax * mix.Dip, cit * (1 - eCit), res * (1 - eRes), gcc * (1 - eGcc), vis * (1 - eVis), pax * mix.Trf,
            cit * eCit + res * eRes + gcc * eGcc + vis * eVis];
    }

    #region Schedule

    /// <summary>The day's arrivals: the waves of random flights from one stream, plus the scripted evening. No departures.</summary>
    public override ScenarioSchedule BuildSchedule(uint seed)
    {
        var r = new Mulberry32(ScenarioMath.Mix32(seed ^ 0xa0e1a500u));
        var used = new HashSet<string>(ScriptArrivals.Select(f => f.Code), StringComparer.Ordinal);

        string Code(string c)
        {
            while (true)
            {
                var s = c + " " + (100 + (int)Math.Floor(r.Next() * 880)).ToString(CultureInfo.InvariantCulture);
                if (used.Add(s))
                    return s;
            }
        }

        string PickCarrier()
        {
            var x = r.Next();
            return x < 0.5 ? "HC" : x < 0.75 ? "RG" : "LV";
        }

        // The groups' shares vary by flight; crew, diplomats and transfers stay as the carrier's.
        PaxMix JitterMix(PaxMix b)
        {
            var cit = b.Cit * (0.9 + r.Next() * 0.2);
            var res = b.Res * (0.9 + r.Next() * 0.2);
            var gcc = b.Gcc * (0.9 + r.Next() * 0.2);
            var vis = b.Vis * (0.9 + r.Next() * 0.2);
            var f = (1 - b.Crw - b.Dip - b.Trf) / (cit + res + gcc + vis);
            return new PaxMix(cit * f, res * f, vis * f, b.Crw, b.Trf) { Dip = b.Dip, Gcc = gcc * f };
        }

        var arrivals = new List<ArrivalFlight>();
        foreach (var b in ArrBanks)
        {
            for (var j = 0; j < b.N; j++)
            {
                var sched = ScenarioMath.RoundToInt(b.From + (b.To - b.From) * (j + r.Next()) / b.N);
                var c = PickCarrier();
                var booked = ScenarioMath.Round((0.70 + r.Next() * 0.25) * 100) / 100;
                var load = ScenarioMath.Clamp(ScenarioMath.Round((booked + (r.Next() - 0.5) * 0.08) * 100) / 100, 0.70, 0.95);
                var delay = (int)ScenarioMath.Clamp(ScenarioMath.Round((r.Next() + r.Next() + r.Next() - 1.5) * 12), -20, 20);
                var code = Code(c);
                var seats = Seats[(int)Math.Floor(r.Next() * Seats.Length)];
                var walk = 10 + (int)Math.Floor(r.Next() * 9);
                var eibtErr = ScenarioMath.RoundToInt((r.Next() - 0.5) * 6);
                arrivals.Add(new ArrivalFlight
                {
                    Code = code, Carrier = c, Sched = sched, OnBlock = sched + delay, Seats = seats, Booked = booked, Load = load, Walk = walk,
                    EibtErr = eibtErr, Mix = JitterMix(Carriers[c].Mix)
                });
            }
        }

        foreach (var s in ScriptArrivals)
        {
            arrivals.Add(new ArrivalFlight
            {
                Code = s.Code, Carrier = s.Carrier, Sched = s.Sched, OnBlock = s.OnBlock, Seats = s.Seats, Booked = s.Booked, Load = s.Load, Walk = s.Walk,
                EibtErr = s.EibtErr, Mix = Carriers[s.Carrier].Mix, Scripted = true
            });
        }

        foreach (var f in arrivals)
        {
            f.Pax = ScenarioMath.RoundToInt(f.Seats * f.Load);
            f.Eibt = f.OnBlock + f.EibtErr;
        }

        return ScenarioSchedule.Of(arrivals, []);
    }

    #endregion

    #region Sensors (the ARV-139a seed's layout)

    // The seed's geometry (AuhTerminalALayout, metres): counters from y 54 at a 2.4 m pitch, gates from y 5 at 1.2 m, wide
    // queues from x 84 and narrow ones (crew, diplomatic, transfer) from x 100 to the counters at x 112, bands from x 64 to
    // 82, a sensor grid of 10 by 10 m footprints, one desk sensor per run of up to four counters. A unit test checks that
    // the codes and zones equal the seed's.
    private const double CounterStartY = 54, CounterPitch = 2.4, GateStartY = 5, GatePitch = 1.2;
    private const double QueueStartX = 84, NarrowQueueStartX = 100, QueueEndX = 112, OverflowStartX = 64, OverflowEndX = 82, Footprint = 10;

    private static IReadOnlyList<SensorDef> BuildSensors(IReadOnlyList<QueueDef> queues)
    {
        var list = new List<SensorDef>();
        static bool Narrow(string lane) => lane is "CRW" or "DIP" or "TRF";

        (double Top, double Bottom) Band(string lane)
        {
            if (lane == "EG")
                return (Math.Round(GateStartY - 1 + 0.1, 2), Math.Round(GateStartY + (SmartGates * GatePitch) + 1 - 0.1, 2));
            var l = CounterLanes.Single(c => c.Code == lane);
            return (Math.Round(CounterStartY + ((l.FirstCounter - 1) * CounterPitch) + 0.1, 2), Math.Round(CounterStartY + (l.LastCounter * CounterPitch) - 0.1, 2));
        }

        void Grid(string kind, string lane, string zone, double x0, double x1)
        {
            var (y0, y1) = Band(lane);
            var count = (int)Math.Ceiling((x1 - x0) / Footprint) * (int)Math.Ceiling((y1 - y0) / Footprint);
            for (var n = 0; n < count; n++)
            {
                var role = n != 0 ? SensorRole.Heartbeat : kind == "Q" ? SensorRole.QueueLead : SensorRole.OverflowLead;
                list.Add(new SensorDef(SensorCode(kind, lane, n + 1), zone, "Stereo", "arr", n, count)
                {
                    SiteCode = SiteCodeValue, QueueZone = QueueName(lane), Role = role
                });
            }
        }

        foreach (var lane in CounterLanes.Select(l => l.Code).Append("EG"))
        {
            Grid("Q", lane, QueueName(lane), Narrow(lane) ? NarrowQueueStartX : QueueStartX, QueueEndX);
            if (lane is "VIS" or "EG")
                Grid("O", lane, BandName(lane), OverflowStartX, OverflowEndX);
        }

        foreach (var lane in CounterLanes)
        {
            var desks = queues.Single(q => q.Id == QueueName(lane.Code)).Servers;
            var runs = (desks.Count + 3) / 4;
            for (var n = 0; n < runs; n++)
            {
                list.Add(new SensorDef(SensorCode("D", lane.Code, n + 1), QueueName(lane.Code), "Stereo", "arr", n, runs)
                {
                    SiteCode = SiteCodeValue, QueueZone = QueueName(lane.Code), Role = SensorRole.DeskZones, Desks = [.. desks.Skip(n * 4).Take(4)]
                });
            }
        }

        return list;
    }

    private static string SensorCode(string kind, string lane, int n) => kind + "-" + lane + "-" + n.ToString("00", CultureInfo.InvariantCulture);

    #endregion
}
