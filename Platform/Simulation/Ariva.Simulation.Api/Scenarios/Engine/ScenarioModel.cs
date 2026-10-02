using System.Collections.Frozen;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>Passenger mix of a flight: shares of crew, citizens, residents, visitors and transfers.</summary>
public sealed record PaxMix(double Cit, double Res, double Vis, double Crw, double Trf);

/// <summary>A carrier of the reference airport: its handler, its check-in island and its passenger mix.</summary>
internal sealed record Carrier(string Code, string Handler, string Island, PaxMix Mix);

/// <summary>
/// One queue of the reference airport: an immigration lane, an e-gate bank, a security checkpoint or a check-in
/// island, with its servers (desks, gates, lanes or counters) and its nominal service time in seconds.
/// </summary>
internal sealed record QueueDef(
    int Index,
    string Id,
    string Area,
    string Group,
    string Lane,
    string Level,
    double Svc,
    string Label,
    string Name,
    string Unit,
    string Handler,
    string Island,
    IReadOnlyList<string> Servers);

/// <summary>A staffing area: the queues one recommendation covers.</summary>
internal sealed record AreaDef(string Id, string Name, string Unit, IReadOnlyList<string> Queues, string Perm);

/// <summary>A people-counting sensor and the zone it watches.</summary>
internal sealed record SensorDef(string Id, string Zone, string Type, string Level, int Slot, int Slots);

/// <summary>A sensor outage on the demo day, in clock minutes [From, To).</summary>
internal sealed record Outage(string Sensor, string Zone, int From, int To);

/// <summary>An outage before the demo day, for the device history.</summary>
internal sealed record PastOutage(string Sensor, string Date, string From, string To, int Minutes, string Cause);

/// <summary>A scripted arrival of the evening story.</summary>
internal sealed record ScriptedArrival(string Code, string Carrier, int Sched, int OnBlock, int Seats, double Booked, double Load, int Walk, int EibtErr);

/// <summary>A scripted departure of the evening story.</summary>
internal sealed record ScriptedDeparture(string Code, string Carrier, int Std, int Seats, double Booked, double Load, int Shift);

/// <summary>A scripted roster line of the evening story.</summary>
internal sealed record RosterLine(string Queue, int From, int To, int Count, string Note);

/// <summary>A counter allocation: counters FromCtr to ToCtr of an island are open for a flight in [Open, Close).</summary>
public sealed record CounterAllocation(string Id, string Flight, string Island, int FromCtr, int ToCtr, int Open, int Close, string By);

/// <summary>
/// The synthetic parameters, queues, sensors and scripted evening of the reference day (Demo International
/// Airport, DMO). Ported from the prototype's sim.js; every value is synthetic.
/// </summary>
internal static class ScenarioModel
{
    #region Clock

    /// <summary>Minutes in the day.</summary>
    public const int Day = 1440;

    /// <summary>Minutes simulated before midnight (from 18:00 the previous evening).</summary>
    public const int Pre = 360;

    /// <summary>Minutes simulated after the day, so late cohorts finish.</summary>
    public const int Post = 420;

    /// <summary>Minutes in one run.</summary>
    public const int N = Pre + Day + Post;

    /// <summary>The default seed.</summary>
    public const uint DefaultSeed = 9303;

    /// <summary>The demo date.</summary>
    public const string Date = "2026-09-28";

    #endregion

    #region Parameters

    public static readonly PaxMix BaseMix = new(0.35, 0.20, 0.35, 0.02, 0.08);
    public const double EgateShare = 0.40;
    public const double EgateReject = 0.07;
    public const double Online = 0.40;
    public const int WalkToSecurity = 3;
    public const int ShowFrom = 180;
    public const int ShowTo = 45;
    public const int McRuns = 40;
    public const int McOnBlock = 6;
    public const double McLoad = 0.04;
    public const double McService = 0.08;

    /// <summary>Nominal service seconds per lane or queue kind.</summary>
    public static readonly FrozenDictionary<string, double> Svc = new Dictionary<string, double>
    {
        ["CRW"] = 20, ["CIT"] = 30, ["RES"] = 45, ["VIS"] = 95, ["EG"] = 18, ["CI"] = 150, ["SEC"] = 20
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The wait target per staffing area that recommendations aim for, in minutes.</summary>
    public static readonly FrozenDictionary<string, double> RecTarget = new Dictionary<string, double>
    {
        ["arr"] = 10, ["dep"] = 10, ["ciA"] = 10, ["ciB"] = 10, ["sec"] = 8
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Share of time a server pauses, per group, discounted from the reported throughput.</summary>
    public static double PauseFraction(string group) => group switch
    {
        "imm" => 0.035,
        "ci" => 0.03,
        "sec" => 0.02,
        _ => 0
    };

    public static readonly IReadOnlyList<string> Lanes = ["CRW", "CIT", "RES", "VIS", "EG"];

    public static readonly FrozenDictionary<string, string> LaneLabel = new Dictionary<string, string>
    {
        ["CRW"] = "Crew and diplomats", ["CIT"] = "Citizens", ["RES"] = "Residents", ["VIS"] = "Visitors", ["EG"] = "E-gate eligible"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static readonly FrozenDictionary<string, Carrier> Carriers = new Dictionary<string, Carrier>
    {
        ["DM"] = new("DM", "A", null, new PaxMix(0.47, 0.22, 0.21, 0.02, 0.08)),
        ["XR"] = new("XR", "B", "C", new PaxMix(0.23, 0.18, 0.49, 0.02, 0.08)),
        ["QL"] = new("QL", "B", "D", new PaxMix(0.35, 0.20, 0.35, 0.02, 0.08))
    }.ToFrozenDictionary(StringComparer.Ordinal);

    #endregion

    #region Queues

    public static readonly IReadOnlyList<QueueDef> Queues = BuildQueues();
    public static readonly FrozenDictionary<string, int> QueueIndex = Queues.ToFrozenDictionary(q => q.Id, q => q.Index, StringComparer.Ordinal);
    public static readonly int NQ = Queues.Count;

    public static readonly FrozenDictionary<string, AreaDef> Areas = new Dictionary<string, AreaDef>
    {
        ["arr"] = new("arr", "Arrival immigration", "desks", ["A-CRW", "A-CIT", "A-RES", "A-VIS"], "imm.forecast"),
        ["dep"] = new("dep", "Departure immigration", "desks", ["D-CRW", "D-CIT", "D-RES", "D-VIS"], "imm.forecast"),
        ["ciA"] = new("ciA", "Check-in, Handler A", "counters", ["CI-A", "CI-B"], "ci.A"),
        ["ciB"] = new("ciB", "Check-in, Handler B", "counters", ["CI-C", "CI-D"], "ci.B"),
        ["sec"] = new("sec", "Security", "lanes", ["SEC-N", "SEC-S"], "sec")
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Arrival lane queues in lane order (crew, citizens, residents, visitors, e-gates).</summary>
    public static readonly int[] ArrivalQueues = [Q("A-CRW"), Q("A-CIT"), Q("A-RES"), Q("A-VIS"), Q("A-EG")];

    /// <summary>Lane shares of departures after security (transfers excluded).</summary>
    public static readonly FrozenDictionary<string, double> EgShareDep = BuildEgShareDep();

    public static int Q(string id) => QueueIndex[id];

    private static List<QueueDef> BuildQueues()
    {
        var list = new List<QueueDef>();
        void Add(string id, string area, string group, string lane, string level, double svc, string label, string name, string unit,
            string handler, string island, IReadOnlyList<string> servers) =>
            list.Add(new QueueDef(list.Count, id, area, group, lane, level, svc, label, name, unit, handler, island, servers));

        static List<string> Range(string prefix, int a, int b, bool padded)
        {
            var o = new List<string>();
            for (var k = a; k <= b; k++)
                o.Add(prefix + (padded ? Pad2(k) : k.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return o;
        }

        var ranges = new Dictionary<string, (int From, int To)> { ["CRW"] = (1, 1), ["CIT"] = (2, 4), ["RES"] = (5, 7), ["VIS"] = (8, 22) };
        foreach (var (side, area, title, deskPrefix, gatePrefix, gates) in new[]
                 {
                     ("A", "arr", "Arrival immigration", "AR-", "AG-", 6),
                     ("D", "dep", "Departure immigration", "DP-", "DG-", 4)
                 })
        {
            foreach (var ln in new[] { "CRW", "CIT", "RES", "VIS" })
            {
                Add(side + "-" + ln, area, "imm", ln, area, Svc[ln], LaneLabel[ln], title + ": " + LaneLabel[ln], "desks", null, null,
                    Range(deskPrefix, ranges[ln].From, ranges[ln].To, true));
            }

            Add(side + "-EG", area, "egate", "EG", area, Svc["EG"], "E-gates", title + ": E-gates", "e-gates", null, null, Range(gatePrefix, 1, gates, false));
        }

        Add("SEC-N", "sec", "sec", null, "dep", Svc["SEC"], "Security North", "Security North", "lanes", null, null, Range("N", 1, 5, false));
        Add("SEC-S", "sec", "sec", null, "dep", Svc["SEC"], "Security South", "Security South", "lanes", null, null, Range("S", 1, 5, false));
        foreach (var isl in new[] { "A", "B", "C", "D" })
        {
            var h = isl is "A" or "B" ? "A" : "B";
            Add("CI-" + isl, h == "A" ? "ciA" : "ciB", "ci", null, "dep", Svc["CI"], "Island " + isl, "Check-in island " + isl + " (Handler " + h + ")",
                "counters", h, isl, Range(isl, 1, 12, true));
        }

        return list;
    }

    private static FrozenDictionary<string, double> BuildEgShareDep()
    {
        var m = BaseMix;
        var nt = 1 - m.Trf;
        const double e = EgateShare;
        return new Dictionary<string, double>
        {
            ["CRW"] = m.Crw / nt,
            ["CIT"] = m.Cit * (1 - e) / nt,
            ["RES"] = m.Res * (1 - e) / nt,
            ["VIS"] = m.Vis / nt,
            ["EG"] = (m.Cit + m.Res) * e / nt
        }.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static string Pad2(int k) => k < 10 ? "0" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) : k.ToString(System.Globalization.CultureInfo.InvariantCulture);

    #endregion

    #region Sensors

    /// <summary>The 59 sensors of the reference BOQ.</summary>
    public static readonly IReadOnlyList<SensorDef> Sensors = BuildSensors();

    /// <summary>Today's outage: S-17 over arrival Visitors, 18:20 to 18:30.</summary>
    public static readonly IReadOnlyList<Outage> Outages = [new("S-17", "A-VIS", 1100, 1110)];

    public static readonly IReadOnlyList<PastOutage> OutageHistory =
    [
        new("S-17", "2026-08-14", "06:52", "06:59", 7, "PoE switch port reset"),
        new("S-17", "2026-09-03", "21:14", "21:36", 22, "Firmware update, planned")
    ];

    private static List<SensorDef> BuildSensors()
    {
        var list = new List<SensorDef>();
        var n = 1;
        void Add(string zone, int count, string type, string level)
        {
            for (var k = 0; k < count; k++)
            {
                list.Add(new SensorDef("S-" + Pad2(n), zone, type, level, k, count));
                n++;
            }
        }

        Add("SEC-N", 5, "Stereo", "dep"); Add("SEC-S", 5, "Stereo", "dep"); Add("SEC-OV", 1, "Stereo", "dep");
        Add("A-CRW", 1, "Stereo", "arr"); Add("A-CIT", 2, "Stereo", "arr"); Add("A-VIS", 6, "Stereo", "arr");
        Add("A-RES", 2, "Stereo", "arr"); Add("A-EG", 2, "Stereo", "arr"); Add("A-OV", 2, "Stereo", "arr");
        Add("D-CRW", 1, "Stereo", "dep"); Add("D-CIT", 2, "Stereo", "dep"); Add("D-RES", 2, "Stereo", "dep");
        Add("D-VIS", 5, "Stereo", "dep"); Add("D-EG", 2, "Stereo", "dep"); Add("D-OV", 1, "Stereo", "dep");
        Add("CI-A", 5, "LiDAR", "dep"); Add("CI-B", 5, "LiDAR", "dep"); Add("CI-C", 5, "LiDAR", "dep"); Add("CI-D", 5, "LiDAR", "dep");
        return list;
    }

    #endregion

    #region Schedule shape

    /// <summary>Hall arrival profile over the 12 minutes after a flight's walk.</summary>
    public static readonly double[] W12 = ScenarioMath.Normalise([3, 6, 9, 11, 12, 12, 11, 10, 8, 7, 6, 5]);

    /// <summary>Minutes in the check-in show-up window.</summary>
    public const int ShowLen = ShowFrom - ShowTo + 1;

    /// <summary>
    /// Check-in show-up profile, a beta shape over the window: w(x) = x^1.6 (1 - x)^1.3 at x = (k + 0.5) / 136,
    /// normalised. Stored as the reference's own float64 bits: Math.pow in V8 and the platform's libm (glibc,
    /// the Windows CRT) disagree in the last bit for some x, and a one-bit difference here would ripple through every
    /// departure queue. Fixed bits keep the day identical on every platform; <see cref="ShowShape"/> recomputes it.
    /// </summary>
    public static readonly double[] Show = Bits(
    [
        0x3EE832F865A67387UL, 0x3F115FF672798347UL, 0x3F237B6B24DC1A52UL, 0x3F3086A2D862B587UL,
        0x3F3876AC02456110UL, 0x3F40B24088BCD694UL, 0x3F45986268ABB89FUL, 0x3F4AE11B26599202UL,
        0x3F5040EC506A3424UL, 0x3F5338C0ECBBE0E4UL, 0x3F56540C5DD88FBFUL, 0x3F598F3D840A714DUL,
        0x3F5CE71B11FC79E1UL, 0x3F602C59C083D9EBUL, 0x3F61F0A888742159UL, 0x3F63BF384A6A9C7FUL,
        0x3F6596DD2054CC02UL, 0x3F67767DF6779B48UL, 0x3F695D1239B4B024UL, 0x3F6B499FF0C2DBD8UL,
        0x3F6D3B3A2854221EUL, 0x3F6F30FFA07E16F9UL, 0x3F70950CD715CDE1UL, 0x3F7192DDA345BB5AUL,
        0x3F729190165EB0ADUL, 0x3F7390C61DD9527CUL, 0x3F74902563BEAB00UL, 0x3F758F5709203C15UL,
        0x3F768E0768D8876EUL, 0x3F778BE5E155AA48UL, 0x3F7888A4A4695A7AUL, 0x3F7983F88C47366AUL,
        0x3F7A7D98F4FEC491UL, 0x3F7B753F99DBCA82UL, 0x3F7C6AA8762F6275UL, 0x3F7D5D91A9179677UL,
        0x3F7E4DBB5BEB34E4UL, 0x3F7F3AE7AAFCBB19UL, 0x3F80126D48391275UL, 0x3F8085ACE87C4B3CUL,
        0x3F80F71675114382UL, 0x3F81668E8122C969UL, 0x3F81D3FA6CD94F06UL, 0x3F823F405E93D352UL,
        0x3F82A8473CAF54E7UL, 0x3F830EF6A7D03C68UL, 0x3F837336F5A2AB37UL, 0x3F83D4F12C07E705UL,
        0x3F84340EFCA83BE4UL, 0x3F84907AC0E1AAEFUL, 0x3F84EA1F760C9946UL, 0x3F8540E8BA107608UL,
        0x3F8594C2C842FAC0UL, 0x3F85E59A768D429CUL, 0x3F86335D32D27D95UL, 0x3F867DF900947EC3UL,
        0x3F86C55C76D2D321UL, 0x3F870976BE217195UL, 0x3F874A378EF46F26UL, 0x3F87878F301E74F2UL,
        0x3F87C16E75800175UL, 0x3F87F7C6BEE5C47CUL, 0x3F882A89F714A6F5UL, 0x3F8859AA93024CC1UL,
        0x3F88851B913918E3UL, 0x3F88ACD07966F3C9UL, 0x3F88D0BD5C16489BUL, 0x3F88F0D6D290E249UL,
        0x3F890D11FEEC8401UL, 0x3F8925648C414ACEUL, 0x3F8939C4AF0A18B3UL, 0x3F894A2925AF7C5EUL,
        0x3F895689393DBC89UL, 0x3F895EDCBE46E3AEUL, 0x3F89631C15F1E046UL, 0x3F8963402F38083CUL,
        0x3F895F4288528BCCUL, 0x3F89571D3059A5E8UL, 0x3F894ACAC9179E9AUL, 0x3F893A4689120014UL,
        0x3F89258C3DCBB1BEUL, 0x3F890C984E43072BUL, 0x3F88EF67BDAF33B6UL, 0x3F88CDF82E81014FUL,
        0x3F88A847E5AB2278UL, 0x3F887E55CE36FE0EUL, 0x3F8850217D2B6ADDUL, 0x3F881DAB35CB7A2FUL,
        0x3F87E6F3EE342EF6UL, 0x3F87ABFD5460D6E1UL, 0x3F876CC9D39EAEBDUL, 0x3F87295C9A799167UL,
        0x3F86E1B9A12CADEDUL, 0x3F8695E5B0A3AD21UL, 0x3F8645E66A1A50C8UL, 0x3F85F1C24F6A7920UL,
        0x3F859980CC1AAC3AUL, 0x3F853D2A3F41C473UL, 0x3F84DCC8065761AEUL, 0x3F847864890C3212UL,
        0x3F84100B46493D3AUL, 0x3F83A3C8E27A321FUL, 0x3F8333AB374C7555UL, 0x3F82BFC165138763UL,
        0x3F82481BE60B937AUL, 0x3F81CCCCA3BCE1AAUL, 0x3F814DE70ECEEF8CUL, 0x3F80CB8039A8A2A7UL,
        0x3F8045AEF64D12A6UL, 0x3F7F7917EFF577E0UL, 0x3F7E6063F25D774EUL, 0x3F7D417BCDA14BA2UL,
        0x3F7C1C9E22EF8B2AUL, 0x3F7AF20ECBE64A9DUL, 0x3F79C2175D0D725AUL, 0x3F788D07BD5E7489UL,
        0x3F775336D777B52BUL, 0x3F7615036A698B88UL, 0x3F74D2D501D14284UL, 0x3F738D1D1F674C2AUL,
        0x3F724458A38F2D16UL, 0x3F70F9118752EA2CUL, 0x3F6F57C20284DE64UL, 0x3F6CBAE4725C0567UL,
        0x3F6A1D0B7D6F873DUL, 0x3F677FEC1FD31C2EUL, 0x3F64E57B4BFE305DUL, 0x3F624FFE882806C8UL,
        0x3F5F8446FBC219F6UL, 0x3F5A7E4577466923UL, 0x3F5595EB297BDD42UL, 0x3F50D55E5B32DB04UL,
        0x3F48947423FA3ED6UL, 0x3F40103CD3C6BB0BUL, 0x3F30BC678358D56FUL, 0x3F103D930B6D28D3UL
    ]);

    /// <summary>The show-up shape recomputed with the platform's Math.Pow (equal to <see cref="Show"/> within a few ULP).</summary>
    public static double[] ShowShape()
    {
        var w = new double[ShowLen];
        for (var k = 0; k < ShowLen; k++)
        {
            var x = (k + 0.5) / ShowLen;
            w[k] = Math.Pow(x, 1.6) * Math.Pow(1 - x, 1.3);
        }

        return ScenarioMath.Normalise(w);
    }

    private static double[] Bits(ulong[] bits) => bits.Select(b => BitConverter.Int64BitsToDouble((long)b)).ToArray();

    public static readonly int[] Seats = [120, 150, 162, 180, 189, 210, 220, 240, 264, 280, 300];

    public static readonly IReadOnlyList<(int From, int To, int N)> ArrBanks = [(60, 235, 13), (355, 540, 20), (600, 990, 8), (1120, 1320, 17), (1340, 1425, 3)];

    public static readonly IReadOnlyList<(int From, int To, int N)> DepBanks = [(65, 240, 11), (360, 545, 17), (620, 1060, 7), (1080, 1320, 18), (1335, 1430, 3)];

    #endregion

    #region The evening story

    public static readonly IReadOnlyList<ScriptedArrival> ScriptArrivals =
    [
        new("XR 207", "XR", 1030, 1039, 189, 0.85, 0.88, 11, 1),
        new("DM 214", "DM", 1035, 1038, 220, 0.84, 0.86, 10, 1),
        new("QL 342", "QL", 1055, 1052, 180, 0.78, 0.80, 12, -1),
        new("XR 331", "XR", 1060, 1068, 216, 0.86, 0.91, 8, 0),
        new("XR 417", "XR", 1080, 1071, 170, 0.85, 0.89, 8, 1),
        new("QL 118", "QL", 1085, 1073, 189, 0.82, 0.86, 8, 0),
        new("XR 509", "XR", 1065, 1076, 162, 0.84, 0.90, 14, 0)
    ];

    public static readonly IReadOnlyList<ScriptedDeparture> ScriptDepartures =
    [
        new("XR 332", "XR", 1210, 224, 0.88, 0.92, -3),
        new("XR 418", "XR", 1230, 204, 0.86, 0.90, 2),
        new("XR 206", "XR", 1245, 255, 0.85, 0.91, -2),
        new("XR 510", "XR", 1265, 187, 0.84, 0.88, 1)
    ];

    public static readonly IReadOnlyList<RosterLine> ScriptRoster =
    [
        new("A-VIS", 1035, 1065, 11, "Evening shift handover"),
        new("A-VIS", 1065, 1095, 12, "Evening shift handover"),
        new("A-VIS", 1095, 1170, 13, "Evening shift fully on"),
        new("CI-C", 1140, 1185, 5, "Handler B shift change"),
        new("CI-C", 1185, 1260, 11, "Handler B evening shift")
    ];

    /// <summary>Arrival e-gate AG-5 is out of service for planned maintenance, 16:40 to 19:20.</summary>
    public static readonly (string Gate, int From, int To, string Note) EgateOutOfService = ("AG-5", 1000, 1160, "Planned maintenance");

    /// <summary>Counter allocations already in place on the morning of the demo day (they match the plan).</summary>
    public static readonly IReadOnlyList<CounterAllocation> SeedAllocations =
    [
        new("AL-001", "XR 332", "C", 1, 2, 1030, 1165, "Handler B station manager"),
        new("AL-002", "XR 418", "C", 3, 4, 1050, 1185, "Handler B station manager"),
        new("AL-003", "XR 206", "C", 5, 5, 1065, 1200, "Handler B station manager")
    ];

    #endregion
}
