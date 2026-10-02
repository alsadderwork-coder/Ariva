using static Ariva.Simulation.Api.Scenarios.Engine.ScenarioModel;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>The live state of one queue at a clock minute.</summary>
internal sealed record QueueState(
    int Q,
    string Id,
    double Length,
    double Rate,
    double Capacity,
    int Open,
    int Paused,
    int Active,
    double? Nowcast,
    bool NoService,
    bool Degraded,
    int[] Band,
    double Arrivals,
    double Served);

/// <summary>One server at a clock minute: oos, closed, unknown, paused, serving or idle.</summary>
internal sealed record ServerState(string Id, string State, int K, double Svc);

/// <summary>One server's aggregates over a 15-minute interval.</summary>
internal sealed record ServerInterval(string Id, double Pax, int Minutes, double? Svc);

/// <summary>One 15-minute bin as seen at a clock minute: P90 wait, people, provisional or final.</summary>
internal sealed record ScenarioBin(int Q, int Start, double? P90, double Pax, string Status, bool IsOpen, double FinalAt);

/// <summary>Wait statistics over a bin of any size.</summary>
internal sealed class BinStats(double pax, string status, List<(double W, double Weight)> samples)
{
    public double Pax { get; } = pax;
    public string Status { get; } = status;
    public IReadOnlyList<(double W, double Weight)> Samples => samples;

    public double? Pct(double p) => Pax >= 0.5 ? ScenarioDay.WeightedQuantile([.. samples], p) : null;

    public double? Share(double t)
    {
        double tot = 0, ok = 0;
        foreach (var (w, weight) in samples)
        {
            tot += weight;
            if (w <= t)
                ok += weight;
        }

        return tot > 0 ? ok / tot : null;
    }
}

/// <summary>A realised (or provisional) mean wait for one entry minute.</summary>
internal sealed record WaitPoint(int M, double W, bool Final, bool Empty);

/// <summary>E-gate throughput over a window.</summary>
internal sealed record EgateStats(double Processed, double Rejects, double RejectRate, double Util, int InService, int Total, IReadOnlyList<ServerState> Servers);

internal sealed partial class ScenarioDay
{
    #region Throughput and live state

    /// <summary>Current throughput: staffed servers at their expected rate (what the border or DCS system reports).</summary>
    public double Throughput(int q, int m)
    {
        var i = m + Pre;
        var def = Queues[q];
        if (Open[q][i] - Paused[q][i] <= 0)
            return 0;
        if (def.Group == "egate")
            return C[q][i];
        return RateSum[q][Math.Min(Open[q][i], def.Servers.Count)] * (1 - PauseFraction(def.Group));
    }

    /// <summary>The outage degrading a zone at a minute, if any.</summary>
    public static Outage Degraded(string zone, int m)
    {
        foreach (var o in Outages)
            if (o.Zone == zone && m >= o.From && m < o.To)
                return o;
        return null;
    }

    /// <summary>Whether a sensor is offline at a minute.</summary>
    public static bool SensorOffline(string sensor, int m)
    {
        foreach (var o in Outages)
            if (o.Sensor == sensor && m >= o.From && m < o.To)
                return true;
        return false;
    }

    /// <summary>The live state of one queue at clock minute <paramref name="m"/>: nowcast = (length + 1) / throughput.</summary>
    public QueueState State(int q, int m)
    {
        var i = m + Pre;
        var len = L[q][i];
        var rate = Throughput(q, m);
        var active = Open[q][i] - Paused[q][i];
        double? nc = active > 0 && rate > 0.01 ? (len + 1) / rate : null;
        var dg = Degraded(Queues[q].Id, m);
        int[] band = null;
        if (dg is not null && nc is not null)
        {
            var lo = (int)(Math.Floor(nc.Value * 0.75 / 5) * 5);
            var hi = (int)(Math.Ceiling(nc.Value * 1.25 / 5) * 5);
            if (hi - lo < 10)
                hi = lo + 10;
            band = [lo, hi];
        }

        return new QueueState(q, Queues[q].Id, len, rate, C[q][i], Open[q][i], Paused[q][i], active, nc, nc is null, dg is not null, band, A[q][i], D[q][i]);
    }

    public List<QueueState> States(int m)
    {
        var o = new List<QueueState>(NQ);
        for (var q = 0; q < NQ; q++)
            o.Add(State(q, m));
        return o;
    }

    #endregion

    #region Servers

    /// <summary>Which servers are open at <paramref name="m"/>: allocated counters first, then the lowest others.</summary>
    public bool[] OpenSet(int q, int m)
    {
        var i = m + Pre;
        var def = Queues[q];
        var o = new bool[def.Servers.Count];
        if (Alloc.TryGetValue(q, out var al))
        {
            var ks = OpenSetFor(al, m, Math.Min(Plan(q, m), def.Servers.Count), def.Servers.Count);
            if (ks is not null)
            {
                foreach (var k in ks)
                    o[k] = true;
                return o;
            }
        }

        int n = Open[q][i];
        for (var k = 0; k < n && k < o.Length; k++)
            o[k] = true;
        return o;
    }

    /// <summary>How many servers <paramref name="n"/> planned servers open, counting allocations.</summary>
    public int OpenCount(int q, int m, int n)
    {
        var def = Queues[q];
        n = Math.Min(n, def.Servers.Count);
        if (!Alloc.TryGetValue(q, out var al))
            return n;
        var ks = OpenSetFor(al, m, n, def.Servers.Count);
        return ks?.Count ?? n;
    }

    /// <summary>The allocation holding counter <paramref name="k"/> at <paramref name="m"/>, if any.</summary>
    public NormalisedAllocation AllocAt(int q, int k, int m)
    {
        if (!Alloc.TryGetValue(q, out var al))
            return null;
        foreach (var a in al)
            if (m >= a.From && m < a.To && a.Ks.Contains(k))
                return a;
        return null;
    }

    /// <summary>Every server of a queue at a minute.</summary>
    public List<ServerState> ServerStates(int q, int m)
    {
        var i = m + Pre;
        var def = Queues[q];
        var o = new List<ServerState>();
        var busyAll = L[q][i] > 0.5;
        var act = new List<int>();
        var open = OpenSet(q, m);
        for (var k = 0; k < def.Servers.Count; k++)
            if (open[k] && Servers[q][k].Pause[i] == 0)
                act.Add(k);
        var perRate = act.Count != 0 ? C[q][i] / act.Count : 1;
        var busy = busyAll ? act.Count : Math.Min(act.Count, (int)Math.Ceiling(D[q][i] / Math.Max(perRate, 0.01) - 0.05));
        var bi = 0;
        for (var k = 0; k < def.Servers.Count; k++)
        {
            var sd = Servers[q][k];
            string st;
            if (sd.OutOfService && sd.Pause[i] != 0)
                st = "oos";
            else if (!open[k])
                st = "closed";
            else if (sd.Unknown[i] != 0)
                st = "unknown";
            else if (sd.Pause[i] != 0)
                st = "paused";
            else
            {
                st = bi < busy ? "serving" : "idle";
                bi++;
            }

            o.Add(new ServerState(def.Servers[k], st, k, def.Svc * sd.Factor));
        }

        return o;
    }

    /// <summary>Per-server aggregates for the 15-minute interval starting at <paramref name="bs"/>, up to now.</summary>
    public List<ServerInterval> ServerIntervals(int q, int bs, int now)
    {
        var def = Queues[q];
        var o = new List<ServerInterval>();
        var end = Math.Min(bs + 15, now + 1);
        for (var k = 0; k < def.Servers.Count; k++)
        {
            var sd = Servers[q][k];
            double pax = 0;
            var mins = 0;
            for (var m = bs; m < end; m++)
            {
                var i = m + Pre;
                if (!OpenSet(q, m)[k] || sd.Pause[i] != 0)
                    continue;
                var r = 60 / (def.Svc * sd.Factor);
                var totalR = C[q][i] > 0 ? C[q][i] : 1;
                pax += D[q][i] * (r * (1 + 0.08 * (ScenarioMath.H3(Seed ^ 0xc0ffeeu, q, i) - 0.5))) / totalR;
                mins++;
            }

            double? svc = mins != 0 ? def.Svc * sd.Factor * (0.96 + 0.08 * ScenarioMath.H3(Seed ^ 0xbeefu, q * 64 + k, bs)) : null;
            o.Add(new ServerInterval(def.Servers[k], pax, mins, pax >= 1 ? svc : null));
        }

        return o;
    }

    #endregion

    #region Waits and bins

    public double ExitTime(int q, double p) => ExitTimeIn(CumD[q], D[q], p);

    private double Dispersion(int q, int i, int k) => 0.88 + 0.3 * ScenarioMath.H3(Seed ^ 0x3c6ef372u, q * 8 + k, i);

    /// <summary>Estimated or realised wait samples for the cohort entering at index <paramref name="i"/>, seen at index <paramref name="ni"/>.</summary>
    private double CohortSamples(int q, int i, int ni, List<(double W, double Weight)> o)
    {
        var a = A[q][i];
        if (a < 1e-6)
            return 0;
        var ca = CumA[q][i];
        var cd = CumD[q];
        var rate = Math.Max(R5[q][ni], 0.05);
        for (var k = 0; k < 5; k++)
        {
            var f = (k + 0.5) / 5;
            var p = ca + a * f;
            var entry = i + f;
            var ex = ExitTime(q, p);
            double w;
            if (ex <= ni + 1)
                w = ex - entry;
            else
                w = ni + 1 + (p - cd[ni + 1]) / rate - entry;
            o.Add((Math.Max(0, w) * Dispersion(q, i, k), a / 5));
        }

        return a;
    }

    /// <summary>A weighted quantile: sorts the samples by wait (stable) and returns the first reaching the share.</summary>
    public static double? WeightedQuantile(List<(double W, double Weight)> samples, double pct)
    {
        if (samples.Count == 0)
            return null;
        var sorted = samples.OrderBy(x => x.W).ToList();
        samples.Clear();
        samples.AddRange(sorted);
        double tot = 0;
        foreach (var s in samples)
            tot += s.Weight;
        double acc = 0;
        foreach (var s in samples)
        {
            acc += s.Weight;
            if (acc >= pct * tot - 1e-9)
                return s.W;
        }

        return samples[^1].W;
    }

    /// <summary>One 15-minute bin starting at <paramref name="bs"/> as seen at clock minute <paramref name="now"/>.</summary>
    public ScenarioBin Bin(int q, int bs, int now)
    {
        var key = q * 100000 + bs + 5000;
        if (_binCache.TryGetValue(key, out var c) && now >= c.FinalAt)
            return c;
        var ni = now + Pre;
        var samples = new List<(double W, double Weight)>();
        double pax = 0;
        var fin = true;
        double lastEx = 0;
        var open = now < bs + 14;
        var end = Math.Min(bs + 15, now + 1);
        for (var m = bs; m < end; m++)
        {
            var i = m + Pre;
            var a = CohortSamples(q, i, ni, samples);
            if (a > 0)
            {
                pax += a;
                lastEx = Math.Max(lastEx, LastExit[q][i] - Pre);
                if (LastExit[q][i] > ni)
                    fin = false;
            }
        }

        if (open)
            fin = false;
        var res = new ScenarioBin(q, bs, pax >= 0.5 ? WeightedQuantile(samples, 0.9) : null, pax, fin ? "final" : "provisional", open,
            fin ? Math.Max(bs + 14, lastEx) : double.PositiveInfinity);
        if (fin)
            _binCache[key] = res;
        return res;
    }

    /// <summary>Samples for a bin of any size, for contract evaluation.</summary>
    public BinStats BinStatsFor(int q, int bs, int size, int now)
    {
        var ni = now + Pre;
        var samples = new List<(double W, double Weight)>();
        double pax = 0;
        var fin = true;
        var end = Math.Min(bs + size, now + 1);
        for (var m = bs; m < end; m++)
        {
            var i = m + Pre;
            var a = CohortSamples(q, i, ni, samples);
            if (a > 0)
            {
                pax += a;
                if (LastExit[q][i] > ni)
                    fin = false;
            }
        }

        if (now < bs + size - 1)
            fin = false;
        return new BinStats(pax, fin ? "final" : "provisional", samples);
    }

    /// <summary>Realised mean wait per entry minute; provisional ones estimated at now.</summary>
    public List<WaitPoint> WaitSeries(int q, int from, int to, int now)
    {
        var o = new List<WaitPoint>();
        var ni = now + Pre;
        for (var m = from; m <= Math.Min(to, now); m++)
        {
            var i = m + Pre;
            if (A[q][i] < 1e-6)
            {
                o.Add(new WaitPoint(m, 0, true, true));
                continue;
            }

            if (LastExit[q][i] <= ni)
            {
                o.Add(new WaitPoint(m, MeanWait[q][i], true, false));
            }
            else
            {
                var p = CumA[q][i] + A[q][i] * 0.5;
                var ex = ExitTime(q, p);
                double w;
                if (ex <= ni + 1)
                    w = ex - (i + 0.5);
                else
                    w = ni + 1 + (p - CumD[q][ni + 1]) / Math.Max(R5[q][ni], 0.05) - (i + 0.5);
                o.Add(new WaitPoint(m, Math.Max(0, w), false, false));
            }
        }

        return o;
    }

    /// <summary>Arrival-weighted P90 over entry minutes [from, to), final data only unless <paramref name="now"/> is given.</summary>
    public (double? P90, double Pax) P90(int q, int from, int to, int? now = null)
    {
        var samples = new List<(double W, double Weight)>();
        var ni = (now ?? Day + Post - 2) + Pre;
        double pax = 0;
        for (var m = from; m < to; m++)
            pax += CohortSamples(q, m + Pre, ni, samples);
        return (pax >= 0.5 ? WeightedQuantile(samples, 0.9) : null, pax);
    }

    public double Sum(double[][] arr, int q, int from, int to)
    {
        double s = 0;
        for (var m = from; m < to; m++)
            s += arr[q][m + Pre];
        return s;
    }

    /// <summary>E-gate throughput, rejects and utilisation over the <paramref name="window"/> minutes ending at now.</summary>
    public EgateStats Egate(string side, int now, int window = 60)
    {
        var q = Q(side + "-EG");
        var w = window != 0 ? window : 60;
        double proc = 0, rej = 0, capS = 0;
        for (var m = now - w + 1; m <= now; m++)
        {
            var i = m + Pre;
            proc += D[q][i];
            rej += D[q][i] * Rej[side == "A" ? 0 : 1][i];
            capS += C[q][i];
        }

        var sv = ServerStates(q, now);
        var inService = sv.Count(s => s.State is not "oos" and not "closed");
        return new EgateStats(proc, rej, proc > 0 ? rej / proc : 0, capS > 0 ? proc / capS : 0, inService, sv.Count, sv);
    }

    #endregion
}
