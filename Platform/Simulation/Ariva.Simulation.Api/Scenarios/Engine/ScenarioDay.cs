using System.Collections.Frozen;
using static Ariva.Simulation.Api.Scenarios.Engine.ScenarioModel;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>One server's seeded behaviour over the run: pauses, unknown states, its speed factor and outage flag.</summary>
internal sealed record ServerData(string Id, double Factor, byte[] Pause, byte[] Unknown, bool OutOfService);

/// <summary>
/// One simulated day: a fluid per-minute backlog recursion per queue, next = max(0, backlog + arrivals - capacity),
/// over N minutes from 18:00 the previous evening. Realised waits come from cumulative arrival and departure curves
/// (FIFO). A port of the prototype's sim.js <c>run</c>; the same seed gives the same numbers, bit for bit.
/// ARV-139b: the site is a parameter (<see cref="ScenarioConfig.SiteCode"/>, <see cref="ScenarioSite"/>): its queues,
/// lanes, sensors, flights and scripted evening; the reference site DMO keeps its outputs bit for bit.
/// Not thread safe (bins cache as they finalise, like the reference): <see cref="ScenarioEngine"/> serialises access.
/// </summary>
internal sealed partial class ScenarioDay
{
    #region Site

    /// <summary>The scenario site this day simulates.</summary>
    public ScenarioSite Site { get; }

    private IReadOnlyList<QueueDef> Queues => Site.Queues;
    private FrozenDictionary<string, int> QueueIndex => Site.QueueIndex;
    private int NQ => Site.NQ;
    private int Q(string id) => Site.Q(id);
    private IReadOnlyList<SensorDef> Sensors => Site.Sensors;
    private IReadOnlyList<Outage> Outages => Site.Outages;
    private int[] ArrivalQueues => Site.ArrivalQueues;
    private IReadOnlyList<string> Lanes => Site.Lanes;
    private FrozenDictionary<string, AreaDef> Areas => Site.Areas;
    private FrozenDictionary<string, double> RecTarget => Site.RecTarget;
    private double EgateReject => Site.EgateReject;

    #endregion

    #region State

    public uint Seed { get; }
    public ScenarioSchedule Schedule { get; }
    public short[][] Roster { get; }
    public IReadOnlyList<NormalisedAccepted> Accepted { get; }
    public IReadOnlyList<ServerData>[] Servers { get; }
    public double[][] RateSum { get; }
    public IReadOnlyDictionary<int, List<NormalisedAllocation>> Alloc { get; }

    /// <summary>Arrivals per queue per minute index.</summary>
    public double[][] A { get; }

    /// <summary>Departures (people served) per queue per minute index.</summary>
    public double[][] D { get; }

    /// <summary>Backlog (people queuing) per queue at the end of each minute index.</summary>
    public double[][] L { get; }

    /// <summary>Capacity (people per minute) per queue per minute index.</summary>
    public double[][] C { get; }

    /// <summary>Five-minute mean capacity.</summary>
    public double[][] R5 { get; }

    public sbyte[][] Open { get; }
    public sbyte[][] Paused { get; }
    public double[][] Rej { get; }
    public double[][] CumA { get; }
    public double[][] CumD { get; }
    public int[][] LastExit { get; }
    public double[][] MeanWait { get; }

    private readonly IReadOnlyList<NormalisedOverride> _overrides;
    private readonly Dictionary<int, ScenarioBin> _binCache = [];

    #endregion

    #region Run

    private ScenarioDay(ScenarioConfig cfg)
    {
        Site = ScenarioSites.Find(cfg.SiteCode) ?? throw new ArgumentException("The scenario site is " + ScenarioSites.Codes + ".", nameof(cfg));
        var seed = cfg.Seed;
        Seed = seed;
        Schedule = Site.BuildSchedule(seed);
        Roster = BuildRoster(Schedule);
        Schedule.AddAdhoc(seed, cfg.Flights, Site);
        Accepted = NormaliseAccepted(cfg.Accepted);
        _overrides = NormaliseOverrides(cfg.Overrides);
        Alloc = NormaliseAllocations(cfg.Allocations);

        var ext = BuildExt(Schedule, ExtMode.Actual, 0, N, null, 0);
        for (var qq = 0; qq < NQ; qq++)
            for (var ii = 0; ii < N; ii++)
                ext[qq][ii] *= 0.5 + ScenarioMath.H3(seed ^ 0xa11ceu, qq, ii);

        Servers = BuildServerData(seed);
        A = Zeros(N); D = Zeros(N); L = Zeros(N); C = Zeros(N); R5 = Zeros(N);
        Open = new sbyte[NQ][];
        Paused = new sbyte[NQ][];
        for (var q = 0; q < NQ; q++)
        {
            Open[q] = new sbyte[N];
            Paused[q] = new sbyte[N];
        }

        Rej = [new double[N], new double[N]];
        RateSum = new double[NQ][];
        for (var q = 0; q < NQ; q++)
        {
            var def = Queues[q];
            var o = new double[def.Servers.Count + 1];
            for (var k = 0; k < def.Servers.Count; k++)
                o[k + 1] = o[k] + 60 / (def.Svc * Servers[q][k].Factor);
            RateSum[q] = o;
        }

        for (var i = 0; i < N; i++)
        {
            var m = i - Pre;
            Rej[0][i] = RejectRate(seed, 1, i);
            Rej[1][i] = RejectRate(seed, 2, i);
            for (var q = 0; q < NQ; q++)
            {
                var def = Queues[q];
                var n = Math.Min(Plan(q, m), def.Servers.Count);
                var cap = 0.0;
                var np = 0;
                var ks = Alloc.TryGetValue(q, out var al) ? OpenSetFor(al, m, n, def.Servers.Count) : null;
                if (ks is not null)
                {
                    foreach (var k in ks)
                    {
                        var sd = Servers[q][k];
                        if (sd.Pause[i] != 0)
                        {
                            np++;
                            continue;
                        }

                        cap += 60 / (def.Svc * sd.Factor);
                    }

                    n = ks.Count;
                }
                else
                {
                    for (var k = 0; k < n; k++)
                    {
                        var sd = Servers[q][k];
                        if (sd.Pause[i] != 0)
                        {
                            np++;
                            continue;
                        }

                        cap += 60 / (def.Svc * sd.Factor);
                    }
                }

                cap *= 1 + 0.08 * (ScenarioMath.H3(seed ^ 0xc0ffeeu, q, i) - 0.5);
                var a = ext[q][i] + InflowAt(q, i, D, i != 0 ? Rej[0][i - 1] : 0.07, i != 0 ? Rej[1][i - 1] : 0.07);
                var back = i != 0 ? L[q][i - 1] : 0;
                var d = Math.Min(back + a, cap);
                A[q][i] = a;
                D[q][i] = d;
                L[q][i] = Math.Max(0, back + a - d);
                C[q][i] = cap;
                Open[q][i] = (sbyte)n;
                Paused[q][i] = (sbyte)np;
                double s5 = 0;
                var c5 = 0;
                for (var z = Math.Max(0, i - 4); z <= i; z++)
                {
                    s5 += C[q][z];
                    c5++;
                }

                R5[q][i] = s5 / c5;
            }
        }

        CumA = new double[NQ][];
        CumD = new double[NQ][];
        LastExit = new int[NQ][];
        MeanWait = new double[NQ][];
        for (var q = 0; q < NQ; q++)
        {
            var ca = new double[N + 1];
            var cd = new double[N + 1];
            for (var i = 0; i < N; i++)
            {
                ca[i + 1] = ca[i] + A[q][i];
                cd[i + 1] = cd[i] + D[q][i];
            }

            CumA[q] = ca;
            CumD[q] = cd;
            var le = new int[N];
            var mw = new double[N];
            var j = 0;
            for (var i = 0; i < N; i++)
            {
                if (A[q][i] < 1e-9)
                {
                    le[i] = i;
                    mw[i] = double.NaN;
                    continue;
                }

                var pEnd = ca[i + 1] - 1e-7;
                while (j < N && cd[j + 1] < pEnd)
                    j++;
                le[i] = j;
                mw[i] = Math.Max(0, ExitTimeIn(cd, D[q], ca[i] + A[q][i] * 0.5) - (i + 0.5));
            }

            LastExit[q] = le;
            MeanWait[q] = mw;
        }

        _caps = cfg.Caps ?? Site.DefaultCaps;
        if (!cfg.Lite)
            SetRules(cfg.Rules ?? Site.SeedRules);
    }

    /// <summary>Runs one day.</summary>
    public static ScenarioDay Run(ScenarioConfig cfg) => new(cfg ?? ScenarioConfig.Reference());

    private double RejectRate(uint seed, int side, int i) => EgateReject * (0.6 + 0.8 * ScenarioMath.H3(seed ^ 0xe6a7eu, side, i));

    /// <summary>Clock time at which the cumulative departures reach <paramref name="p"/> people (infinity if never).</summary>
    public static double ExitTimeIn(double[] cd, double[] d, double p)
    {
        if (p > cd[N])
            return double.PositiveInfinity;
        int lo = 0, hi = N - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (cd[mid + 1] < p)
                lo = mid + 1;
            else
                hi = mid;
        }

        var dd = d[lo];
        return lo + (dd > 1e-12 ? (p - cd[lo]) / dd : 0);
    }

    internal double[][] Zeros(int len)
    {
        var o = new double[NQ][];
        for (var q = 0; q < NQ; q++)
            o[q] = new double[len];
        return o;
    }

    #endregion

    #region Exogenous arrivals

    internal enum ExtMode
    {
        Actual,
        Expected,
        DayAhead
    }

    /// <summary>A flight's arriving passengers by arrival lane, in the site's lane order (DMO: the reference's split).</summary>
    private double[] LaneSplitArr(double pax, PaxMix mix) => Site.SplitArrivals(pax, mix);

    private void AddArr(double[][] ext, int off, int len, int seats, double ob, double load, double walk, PaxMix mix)
    {
        var lanes = LaneSplitArr(seats * load, mix);
        for (var cp = -1; cp <= 1; cp++)
        {
            var start = ScenarioMath.RoundToInt(ob + walk) + cp * Day + Pre - off;
            if (start + 12 <= 0 || start >= len)
                continue;
            for (var k = 0; k < 12; k++)
            {
                var j = start + k;
                if (j < 0 || j >= len)
                    continue;
                for (var x = 0; x < ArrivalQueues.Length; x++)
                    ext[ArrivalQueues[x]][j] += lanes[x] * W12[k];
            }
        }
    }

    private void AddDep(double[][] ext, int off, int len, DepartureFlight f, double load, double shift)
    {
        var pax = f.Seats * load;
        var m = f.Mix;
        var nt = pax * (1 - m.Trf);
        var crew = pax * m.Crw;
        var counter = (nt - crew) * (1 - Online);
        var direct = nt - counter;
        var ci = Q("CI-" + f.Island);
        var sec = f.Island is "A" or "B" ? Q("SEC-N") : Q("SEC-S");
        const int wk = WalkToSecurity;
        for (var cp = -1; cp <= 1; cp++)
        {
            var b = ScenarioMath.RoundToInt(f.Std - ShowFrom + shift) + cp * Day + Pre - off;
            if (b + ShowLen + wk <= 0 || b >= len)
                continue;
            for (var k = 0; k < ShowLen; k++)
            {
                var j = b + k;
                if (j >= 0 && j < len)
                    ext[ci][j] += counter * Show[k];
                var j2 = j + wk;
                if (j2 >= 0 && j2 < len)
                    ext[sec][j2] += direct * Show[k];
            }
        }
    }

    /// <summary>
    /// Exogenous arrivals per queue over [off, off + len): actual (on-block, actual load, real walk), expected
    /// (estimated on-block, standard walk) or day-ahead (schedule and booked load). With <paramref name="pert"/>,
    /// one Monte Carlo draw from the state known at <paramref name="now"/>.
    /// </summary>
    internal double[][] BuildExt(ScenarioSchedule s, ExtMode mode, int off, int len, Mulberry32 pert, int now)
    {
        var ext = Zeros(len);
        var walkStd = Site.StandardWalk;
        foreach (var f in s.Arrivals)
        {
            double ob, load, walk;
            PaxMix mix;
            switch (mode)
            {
                case ExtMode.Actual:
                    ob = f.OnBlock; load = f.Load; walk = f.Walk; mix = f.Mix;
                    break;
                case ExtMode.Expected:
                    ob = f.Eibt; load = f.Load; walk = walkStd; mix = f.Mix;
                    break;
                default:
                    ob = f.Sched; load = f.Booked; walk = walkStd; mix = Site.BaseMix;
                    break;
            }

            if (pert is not null)
            {
                var landed = f.OnBlock <= now;
                if (landed)
                {
                    ob = f.OnBlock;
                    walk = f.OnBlock + f.Walk <= now
                        ? f.Walk
                        : Math.Max(now - f.OnBlock + 1, ScenarioMath.Clamp(walkStd + ScenarioMath.Round((pert.Next() * 2 - 1) * 3), walkStd - 3, walkStd + 4));
                }
                else
                {
                    ob = Math.Max(now + 1, f.Eibt + ScenarioMath.Round((pert.Next() * 2 - 1) * McOnBlock));
                    walk = ScenarioMath.Clamp(walkStd + ScenarioMath.Round((pert.Next() * 2 - 1) * 3), walkStd - 3, walkStd + 4);
                }

                load = f.Load * (1 + (pert.Next() * 2 - 1) * McLoad);
            }

            AddArr(ext, off, len, f.Seats, ob, load, walk, mix);
        }

        foreach (var f in s.Departures)
        {
            double load, shift;
            if (mode == ExtMode.Actual)
            {
                load = f.Load;
                shift = f.Shift;
            }
            else
            {
                load = f.Booked;
                shift = 0;
            }

            if (pert is not null)
            {
                load = f.Booked * (1 + (pert.Next() * 2 - 1) * 0.05);
                shift = ScenarioMath.Round((pert.Next() * 2 - 1) * McOnBlock);
            }

            AddDep(ext, off, len, f, load, shift);
        }

        return ext;
    }

    #endregion

    #region Roster and plan

    /// <summary>The day-ahead roster: servers per queue per 15-minute block, from the schedule only.</summary>
    private short[][] BuildRoster(ScenarioSchedule s)
    {
        var ext = BuildExt(s, ExtMode.DayAhead, 0, N, null, 0);
        var dem = ext.Select(a => (double[])a.Clone()).ToArray();
        if (Site.HasDepartures)
        {
            int secN = Q("SEC-N"), secS = Q("SEC-S");
            int ciA = Q("CI-A"), ciB = Q("CI-B"), ciC = Q("CI-C"), ciD = Q("CI-D");
            for (var i = 0; i < N; i++)
            {
                var j = i + 8;
                if (j < N)
                {
                    dem[secN][j] += ext[ciA][i] + ext[ciB][i];
                    dem[secS][j] += ext[ciC][i] + ext[ciD][i];
                }
            }

            for (var i = 0; i < N; i++)
            {
                var tot = dem[secN][i] + dem[secS][i];
                var j2 = i + 6;
                if (j2 >= N)
                    continue;
                foreach (var ln in Lanes)
                    dem[Q("D-" + ln)][j2] += tot * Site.EgShareDep[ln];
            }
        }

        // Reject coupling: rejected e-gate attempts join the manual visitors' lane of the same side.
        int aVis = Q("A-VIS"), aEg = Q("A-EG");
        int dVis = Site.HasDepartures ? Q("D-VIS") : -1, dEg = Site.HasDepartures ? Q("D-EG") : -1;
        for (var i = 1; i < N; i++)
        {
            dem[aVis][i] += EgateReject * dem[aEg][i - 1];
            if (dVis >= 0)
                dem[dVis][i] += EgateReject * dem[dEg][i - 1];
        }

        var roster = new short[NQ][];
        for (var q = 0; q < NQ; q++)
        {
            var def = Queues[q];
            var r = new short[96];
            var max = def.Servers.Count;
            var need = new double[96];
            for (var b = 0; b < 96; b++)
            {
                var sum = 0.0;
                for (var k = 0; k < 15; k++)
                    sum += dem[q][Pre + b * 15 + k];
                need[b] = sum / 15 * def.Svc / 60 / 0.88 * 1.04;
            }

            for (var h = 0; h < 24; h++)
            {
                double n = 0, any = 0;
                for (var bb = h * 4 - 1; bb < h * 4 + 4; bb++)
                {
                    var v = need[ScenarioMath.Mod(bb, 96)];
                    n = Math.Max(n, v);
                    any += v;
                }

                if (def.Group == "sec")
                    n = Math.Ceiling(any / 5 * 0.95 - 0.1);
                else if (def.Area == "dep")
                    n = Math.Ceiling(n * 0.92 - 0.1);
                else if (def.Group == "ci")
                    n = Math.Ceiling(n * 0.8 - 0.1);
                else
                    n = Math.Ceiling(n - 0.05);

                if (def.Group == "imm")
                    n = Math.Max(n, Site.MinimumServers(def));
                else if (def.Group == "egate")
                    n = max;
                else if (def.Group == "sec")
                    n = Math.Max(n, 1);
                else if (def.Group == "ci")
                    n = any > 0.02 ? Math.Max(n, 2) : 0;
                n = ScenarioMath.Clamp(n, 0, max);
                for (var z = 0; z < 4; z++)
                    r[h * 4 + z] = (short)n;
            }

            roster[q] = r;
        }

        foreach (var o in Site.ScriptRoster)
            for (var m = o.From; m < o.To; m += 15)
                roster[Q(o.Queue)][m / 15] = (short)o.Count;
        return roster;
    }

    internal sealed record NormalisedOverride(int Q, int From, int To, int N);

    internal sealed record NormalisedAccepted(string Area, int From, int Until, IReadOnlyDictionary<int, IReadOnlyList<int>> Counts);

    internal sealed record NormalisedAllocation(int From, int To, IReadOnlyList<int> Ks, string Code, string Id);

    private List<NormalisedOverride> NormaliseOverrides(IReadOnlyList<RosterOverride> list) =>
        (list ?? [])
        .Where(o => o is not null && o.Queue is not null && QueueIndex.ContainsKey(o.Queue) && o.To > o.From)
        .Select(o => new NormalisedOverride(Q(o.Queue), o.From, o.To, Math.Max(0, o.Count)))
        .ToList();

    private List<NormalisedAccepted> NormaliseAccepted(IReadOnlyList<AcceptedPlan> list) =>
        (list ?? [])
        .Where(a => a?.Counts is not null)
        .Select(a => new NormalisedAccepted(a.Area, a.From, a.Until,
            a.Counts.Where(kv => kv.Key is not null && QueueIndex.ContainsKey(kv.Key) && kv.Value is not null)
                .ToDictionary(kv => Q(kv.Key), kv => kv.Value)))
        .ToList();

    /// <summary>Counter allocations per check-in queue: the allocated counters open during their window.</summary>
    private Dictionary<int, List<NormalisedAllocation>> NormaliseAllocations(IReadOnlyList<CounterAllocation> list)
    {
        var o = new Dictionary<int, List<NormalisedAllocation>>();
        foreach (var a in list ?? [])
        {
            // Check-in islands A to D exist at the reference site only.
            if (a is null || a.Island is not ("A" or "B" or "C" or "D") || !QueueIndex.ContainsKey("CI-" + a.Island))
                continue;
            var q = Q("CI-" + a.Island);
            var ks = new List<int>();
            for (var k = Math.Max(1, a.FromCtr); k <= Math.Min(12, a.ToCtr); k++)
                ks.Add(k - 1);
            if (ks.Count == 0 || !(a.Close > a.Open))
                continue;
            if (!o.TryGetValue(q, out var bucket))
                o[q] = bucket = [];
            bucket.Add(new NormalisedAllocation(a.Open, a.Close, ks, a.Flight, a.Id));
        }

        return o;
    }

    /// <summary>Which servers are open at <paramref name="m"/> under allocations: allocated first, then the lowest others.</summary>
    internal static List<int> OpenSetFor(List<NormalisedAllocation> al, int m, int n, int max)
    {
        var set = new bool[max];
        var cnt = 0;
        foreach (var a in al)
        {
            if (m < a.From || m >= a.To)
                continue;
            foreach (var k in a.Ks)
            {
                if (!set[k])
                {
                    set[k] = true;
                    cnt++;
                }
            }
        }

        if (cnt == 0)
            return null;
        var total = Math.Min(max, Math.Max(n, cnt));
        var need = total - cnt;
        var o = new List<int>();
        for (var k = 0; k < max; k++)
        {
            if (set[k])
            {
                o.Add(k);
            }
            else if (need > 0)
            {
                o.Add(k);
                need -= 1;
            }
        }

        return o;
    }

    /// <summary>The staffing plan: the roster, then accepted recommendations, then overrides (last wins).</summary>
    public int Plan(int q, int m)
    {
        int n = Roster[q][ScenarioMath.Mod(m, Day) / 15];
        foreach (var a in Accepted)
        {
            if (m >= a.From && m < a.Until && a.Counts.TryGetValue(q, out var counts))
            {
                var idx = (int)Math.Floor((m - a.From) / 15.0);
                if (idx >= 0 && idx < counts.Count)
                    n = counts[idx];
            }
        }

        foreach (var o in _overrides)
            if (o.Q == q && m >= o.From && m < o.To)
                n = o.N;
        return n;
    }

    #endregion

    #region Servers

    private IReadOnlyList<ServerData>[] BuildServerData(uint seed)
    {
        var o = new IReadOnlyList<ServerData>[NQ];
        for (var q = 0; q < NQ; q++)
        {
            var def = Queues[q];
            var list = new List<ServerData>();
            for (var k = 0; k < def.Servers.Count; k++)
            {
                var sid = def.Servers[k];
                var key = q * 64 + k;
                var pause = new byte[N];
                var unknown = new byte[N];
                var rate = def.Group == "imm" ? 1.0 / 110 : def.Group == "ci" ? 1.0 / 140 : def.Group == "sec" ? 1.0 / 240 : 0;
                var i = 0;
                while (i < N && rate > 0)
                {
                    if (ScenarioMath.H3(seed ^ 0x1234567u, key, i) < rate)
                    {
                        var len = 2 + (int)Math.Floor(ScenarioMath.H3(seed ^ 0x7654321u, key, i) * 5);
                        for (var z = 0; z < len && i + z < N; z++)
                            pause[i + z] = 1;
                        i += len + 3;
                    }
                    else
                    {
                        i++;
                    }
                }

                if (def.Group is "imm" or "ci")
                {
                    for (i = 0; i < N; i++)
                    {
                        if (ScenarioMath.H3(seed ^ 0x2468aceu, key, i) < 1.0 / 1100)
                        {
                            var ul = 1 + (int)Math.Floor(ScenarioMath.H3(seed ^ 0x13579bdu, key, i) * 3);
                            for (var z = 0; z < ul && i + z < N; z++)
                                unknown[i + z] = 1;
                            i += ul;
                        }
                    }
                }

                var oos = false;
                foreach (var window in Site.OutOfService)
                {
                    if (window.Server != sid)
                        continue;
                    oos = true;
                    for (var m = window.From; m < window.To; m++)
                        pause[m + Pre] = 1;
                }

                var factor = def.Group == "egate" ? 1
                    : def.Group == "sec" ? 0.94 + ScenarioMath.H3(seed ^ 0x55aau, key, 1) * 0.12
                    : 0.88 + ScenarioMath.H3(seed ^ 0x55aau, key, 1) * 0.3;
                list.Add(new ServerData(sid, factor, pause, unknown, oos));
            }

            o[q] = list;
        }

        return o;
    }

    /// <summary>The network step: people reaching a queue from upstream queues (rejected e-gates, check-in, security).</summary>
    internal double InflowAt(int q, int j, double[][] d, double rejA, double rejD)
    {
        var def = Queues[q];
        if (def.Area == "arr")
            return def.Lane == "VIS" && j >= 1 ? rejA * d[Q("A-EG")][j - 1] : 0;
        if (def.Area == "dep")
        {
            var sec = j >= 2 ? d[Q("SEC-N")][j - 2] + d[Q("SEC-S")][j - 2] : 0;
            var v = sec * Site.EgShareDep[def.Lane];
            if (def.Lane == "VIS" && j >= 1)
                v += rejD * d[Q("D-EG")][j - 1];
            return v;
        }

        if (def.Id == "SEC-N")
            return j >= 3 ? d[Q("CI-A")][j - 3] + d[Q("CI-B")][j - 3] : 0;
        if (def.Id == "SEC-S")
            return j >= 3 ? d[Q("CI-C")][j - 3] + d[Q("CI-D")][j - 3] : 0;
        return 0;
    }

    #endregion
}
