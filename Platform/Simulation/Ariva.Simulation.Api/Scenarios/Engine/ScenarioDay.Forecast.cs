using static Ariva.Simulation.Api.Scenarios.Engine.ScenarioModel;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>Monte Carlo forecast of one queue: P10, P50 and P90 joining wait per minute, plus every run (single precision, as the reference).</summary>
internal sealed record QueueForecast(double[] P10, double[] P50, double[] P90, float[] Raw, float[] Weights);

/// <summary>A Monte Carlo forecast from <see cref="Now"/> for <see cref="Horizon"/> minutes.</summary>
internal sealed record Forecast(int Now, int Horizon, int Runs, IReadOnlyDictionary<int, QueueForecast> Queues);

/// <summary>The deterministic expected run: arrivals, departures, backlog and capacity under the plan.</summary>
internal sealed record ExpectedRun(double[][] A, double[][] D, double[][] L, double[][] C, double[][] Ext);

/// <summary>The day-ahead run from the schedule only: arrivals and the joining wait.</summary>
internal sealed record DayAheadRun(double[][] A, double[][] W);

/// <summary>A staffing recommendation for an area: servers per queue per 15-minute block, with predicted P90s.</summary>
internal sealed record Recommendation(
    string Area,
    int From,
    int Until,
    int Blocks,
    IReadOnlyDictionary<string, int[]> Rec,
    IReadOnlyDictionary<string, int[]> Planned,
    double Target,
    IReadOnlyDictionary<string, double[]> LaneP90Plan,
    IReadOnlyDictionary<string, double[]> LaneP90Rec,
    double[] P90Plan,
    double[] P90Rec);

/// <summary>Expected demand per queue per slot.</summary>
internal sealed record Demand(IReadOnlyList<int> Xs, IReadOnlyDictionary<int, List<double>> Queues);

/// <summary>A flight arriving in a window, split into lanes, with the minute its passengers reach the hall.</summary>
internal sealed record ArrivingFlight(ArrivalFlight Flight, IReadOnlyDictionary<string, double> Lanes, int HallFrom);

internal sealed partial class ScenarioDay
{
    private ExpectedRun _expected;
    private DayAheadRun _dayAhead;

    /// <summary>Aggregate capacity of <paramref name="n"/> planned servers at a minute, at the expected rate.</summary>
    private double CapAgg(int q, int m, int n, double sf)
    {
        var def = Queues[q];
        n = OpenCount(q, m, n);
        if (def.Group == "egate")
        {
            double c = 0;
            var i = m + Pre;
            for (var k = 0; k < n; k++)
            {
                var sd = Servers[q][k];
                if (!(sd.OutOfService && i >= 0 && i < N && sd.Pause[i] != 0))
                    c += 60 / def.Svc;
            }

            return c * sf;
        }

        return RateSum[q][n] * (1 - PauseFraction(def.Group)) * sf;
    }

    /// <summary>Simulates from clock minute <paramref name="m0"/> (state known at m0) for <paramref name="h"/> minutes.</summary>
    private void SimAgg(int m0, int h, double[][] ext, int off, Func<int, int, int> planFn, double[] sf, Action<int, int, double, double, double> onMinute)
    {
        var i0 = m0 + Pre;
        var len = ext[0].Length;
        var dw = Zeros(len);
        var back = new double[NQ];
        for (var q = 0; q < NQ; q++)
        {
            back[q] = L[q][i0];
            for (var j = 0; j < len; j++)
            {
                var ii = off + j;
                if (ii <= i0)
                    dw[q][j] = D[q][ii];
            }
        }

        for (var step = 1; step <= h; step++)
        {
            var i = i0 + step;
            var jj = i - off;
            var m = i - Pre;
            for (var q = 0; q < NQ; q++)
            {
                var cap = CapAgg(q, m, planFn(q, m), sf is not null ? sf[q] : 1);
                var a = ext[q][jj] + InflowAt(q, jj, dw, EgateReject, EgateReject);
                var d = Math.Min(back[q] + a, cap);
                back[q] = Math.Max(0, back[q] + a - d);
                dw[q][jj] = d;
                onMinute?.Invoke(q, step, a, back[q], cap);
            }
        }
    }

    /// <summary>Monte Carlo forecast from <paramref name="now"/>: P10, P50 and P90 of the joining wait per minute.</summary>
    public Forecast ForecastFrom(int now, int horizon = 120, int runs = McRuns, IReadOnlyList<int> queues = null, Func<int, int, int> plan = null, int salt = 0)
    {
        var h = horizon != 0 ? horizon : 120;
        var r = runs != 0 ? runs : McRuns;
        var planFn = plan ?? Plan;
        var qs = queues ?? Enumerable.Range(0, NQ).ToList();
        var want = new Dictionary<int, float[]>();
        var wts = new Dictionary<int, float[]>();
        foreach (var q in qs)
        {
            want[q] = new float[r * h];
            wts[q] = new float[r * h];
        }

        for (var run = 0; run < r; run++)
        {
            uint streamSeed;
            unchecked
            {
                streamSeed = ScenarioMath.Mix32(Seed ^ (uint)((now + 7) * 7919) ^ (uint)((run + 1) * 104729) ^ (uint)salt);
            }

            var rng = new Mulberry32(streamSeed);
            var off = now + Pre - 4;
            var len = h + 6;
            var ext = BuildExt(Schedule, ExtMode.Expected, off, len, rng, now);
            var sf = new double[NQ];
            for (var q = 0; q < NQ; q++)
                sf[q] = 1 / (1 + (rng.Next() * 2 - 1) * McService);
            var rr = run;
            SimAgg(now, h, ext, off, planFn, sf, (q, step, a, back, cap) =>
            {
                if (!want.TryGetValue(q, out var wq))
                    return;
                wq[rr * h + step - 1] = (float)(cap > 0.01 ? Math.Min(120, (back + 0.5) / cap) : 120);
                wts[q][rr * h + step - 1] = (float)a;
            });
        }

        var o = new Dictionary<int, QueueForecast>();
        foreach (var q in qs)
        {
            var p10 = new double[h];
            var p50 = new double[h];
            var p90 = new double[h];
            var v = new double[r];
            for (var step = 0; step < h; step++)
            {
                for (var r2 = 0; r2 < r; r2++)
                    v[r2] = want[q][r2 * h + step];
                Array.Sort(v);
                p10[step] = v[(int)Math.Floor(0.1 * (r - 1))];
                p50[step] = v[(int)Math.Floor(0.5 * (r - 1))];
                p90[step] = v[(int)Math.Ceiling(0.9 * (r - 1))];
            }

            o[q] = new QueueForecast(p10, p50, p90, want[q], wts[q]);
        }

        return new Forecast(now, h, r, o);
    }

    /// <summary>The expected (deterministic) full-day run from the schedule, the estimated on-blocks and the plan.</summary>
    public ExpectedRun Expected()
    {
        if (_expected is not null)
            return _expected;
        var ext = BuildExt(Schedule, ExtMode.Expected, 0, N, null, 0);
        var aExp = Zeros(N);
        var lExp = Zeros(N);
        var cExp = Zeros(N);
        var dExp = Zeros(N);
        var back = new double[NQ];
        for (var i = 0; i < N; i++)
        {
            var m = i - Pre;
            for (var q = 0; q < NQ; q++)
            {
                var cap = CapAgg(q, m, Plan(q, m), 1);
                var a = ext[q][i] + InflowAt(q, i, dExp, EgateReject, EgateReject);
                var d = Math.Min(back[q] + a, cap);
                back[q] = Math.Max(0, back[q] + a - d);
                aExp[q][i] = a;
                dExp[q][i] = d;
                lExp[q][i] = back[q];
                cExp[q][i] = cap;
            }
        }

        _expected = new ExpectedRun(aExp, dExp, lExp, cExp, ext);
        return _expected;
    }

    /// <summary>The day-ahead forecast from the schedule only (generic lane mix, booked loads, planned roster).</summary>
    public DayAheadRun DayAhead()
    {
        if (_dayAhead is not null)
            return _dayAhead;
        var ext = BuildExt(Schedule, ExtMode.DayAhead, 0, N, null, 0);
        var w = Zeros(N);
        var aa = Zeros(N);
        var dd = Zeros(N);
        var back = new double[NQ];
        for (var i = 0; i < N; i++)
        {
            var m = i - Pre;
            for (var q = 0; q < NQ; q++)
            {
                var cap = CapAgg(q, m, Roster[q][ScenarioMath.Mod(m, Day) / 15], 1);
                var a = ext[q][i] + InflowAt(q, i, dd, EgateReject, EgateReject);
                var d = Math.Min(back[q] + a, cap);
                back[q] = Math.Max(0, back[q] + a - d);
                aa[q][i] = a;
                dd[q][i] = d;
                w[q][i] = cap > 0.01 ? (back[q] + 0.5) / cap : 0;
            }
        }

        _dayAhead = new DayAheadRun(aa, w);
        return _dayAhead;
    }

    /// <summary>
    /// A staffing recommendation for an area in 15-minute blocks from the next block: the fewest servers that keep the
    /// expected wait under the area's target, then checked (and topped up) against the Monte Carlo P90.
    /// </summary>
    public Recommendation Recommend(int now, string areaId, int blocks = 24, bool predict = true, int runs = 16)
    {
        var area = Areas[areaId];
        var nb = blocks != 0 ? blocks : 24;
        var target = RecTarget[areaId];
        var b0 = (int)Math.Floor(now / 15.0) + 1;
        var start = b0 * 15;
        var exp = Expected();
        var qs = area.Queues.Select(Q).ToList();
        var rec = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var planned = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var q in qs)
        {
            var def = Queues[q];
            var maxK = def.Servers.Count;
            var back0 = L[q][now + Pre];
            for (var m = now + 1; m < start; m++)
                back0 = Math.Max(0, back0 + exp.A[q][m + Pre] - CapAgg(q, m, Plan(q, m), 1));
            var minBase = def.Group == "imm" ? (def.Lane == "VIS" ? 2 : 1) : def.Group == "sec" ? 1 : 0;
            var lam = new double[nb];
            var k = new int[nb];
            for (var b = 0; b < nb; b++)
            {
                double sum = 0;
                for (var t = 0; t < 15; t++)
                    sum += exp.A[q][start + b * 15 + t + Pre];
                lam[b] = sum / 15;
            }

            for (var b = 0; b < nb; b++)
            {
                var kk = minBase;
                if (def.Group == "ci")
                    kk = lam[b] < 0.02 && (b == 0 || lam[b - 1] < 0.02) ? 0 : 1;
                while (kk < maxK && CapAgg(q, start + b * 15, kk, 1) * 0.85 < lam[b])
                    kk++;
                k[b] = kk;
            }

            (double Back, double Worst) BlockRun(double bk, int bs, int kk)
            {
                double worst = 0;
                for (var tt = 0; tt < 15; tt++)
                {
                    var mm = bs + tt;
                    var capk = CapAgg(q, mm, kk, 1);
                    var a = exp.A[q][mm + Pre];
                    bk = Math.Max(0, bk + a - capk);
                    var wv = capk > 0.01 ? (bk + 0.5) / capk : bk > 0.3 ? 999 : 0;
                    if (wv > worst)
                        worst = wv;
                }

                return (bk, worst);
            }

            for (var iter = 0; iter < 8; iter++)
            {
                var back = back0;
                var bad = -1;
                for (var b = 0; b < nb; b++)
                {
                    var r0 = BlockRun(back, start + b * 15, k[b]);
                    while (r0.Worst > target && k[b] < maxK)
                    {
                        k[b]++;
                        r0 = BlockRun(back, start + b * 15, k[b]);
                    }

                    if (r0.Worst > target && bad < 0)
                        bad = b;
                    back = r0.Back;
                }

                if (bad <= 0)
                    break;
                var j = bad - 1;
                while (j >= 0 && k[j] >= maxK)
                    j -= 1;
                if (j < 0)
                    break;
                k[j] = maxK;
            }

            rec[def.Id] = k;
            var pl = new int[nb];
            for (var b = 0; b < nb; b++)
                pl[b] = OpenCount(q, start + b * 15, Plan(q, start + b * 15));
            planned[def.Id] = pl;
        }

        var until = start + nb * 15;
        if (!predict)
            return new Recommendation(areaId, start, until, nb, rec, planned, target, null, null, null, null);

        int RecPlan(int q, int m)
        {
            var id = Queues[q].Id;
            if (rec.TryGetValue(id, out var ks) && m >= start && m < until)
                return ks[(int)Math.Floor((m - start) / 15.0)];
            return Plan(q, m);
        }

        var h = until - now;
        var nRuns = runs != 0 ? runs : 16;

        Dictionary<string, double[]> LanesP90(Forecast fc)
        {
            var o = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var q in qs)
            {
                var arr = new double[nb];
                for (var b2 = 0; b2 < nb; b2++)
                {
                    var sp = new List<(double W, double Weight)>();
                    for (var r = 0; r < nRuns; r++)
                    {
                        for (var t2 = 0; t2 < 15; t2++)
                        {
                            var hIdx = start + b2 * 15 + t2 - now - 1;
                            if (hIdx < 0 || hIdx >= h)
                                continue;
                            sp.Add((fc.Queues[q].Raw[r * h + hIdx], fc.Queues[q].Weights[r * h + hIdx] + 1e-6));
                        }
                    }

                    // The reference writes wq(...) || 0: an empty or zero quantile both read as zero.
                    arr[b2] = WeightedQuantile(sp, 0.9) ?? 0;
                }

                o[Queues[q].Id] = arr;
            }

            return o;
        }

        var fp = ForecastFrom(now, h, nRuns, qs, null, 11);
        var lp = LanesP90(fp);
        Dictionary<string, double[]> lr = null;
        for (var pass = 0; pass < 4; pass++)
        {
            var fr = ForecastFrom(now, h, nRuns, qs, RecPlan, 11);
            lr = LanesP90(fr);
            var changed = false;
            foreach (var q in qs)
            {
                var id = Queues[q].Id;
                var maxK = Queues[q].Servers.Count;
                for (var b3 = 0; b3 < nb; b3++)
                {
                    if (lr[id][b3] > 13 && rec[id][b3] < maxK)
                    {
                        rec[id][b3]++;
                        changed = true;
                        if (b3 > 0 && rec[id][b3 - 1] < maxK && lr[id][b3] > 18)
                            rec[id][b3 - 1]++;
                    }
                }
            }

            if (!changed)
                break;
            if (pass == 3)
            {
                fr = ForecastFrom(now, h, nRuns, qs, RecPlan, 11);
                lr = LanesP90(fr);
            }
        }

        var p90Plan = new double[nb];
        var p90Rec = new double[nb];
        for (var b4 = 0; b4 < nb; b4++)
        {
            double wp = 0, wr = 0;
            foreach (var q in qs)
            {
                var id = Queues[q].Id;
                wp = Math.Max(wp, lp[id][b4]);
                wr = Math.Max(wr, lr[id][b4]);
            }

            p90Plan[b4] = wp;
            p90Rec[b4] = wr;
        }

        return new Recommendation(areaId, start, until, nb, rec, planned, target, lp, lr, p90Plan, p90Rec);
    }

    /// <summary>Expected demand per queue per slot (people per hour) for the next hours, on a periodic day.</summary>
    public Demand DemandFrom(int now, int hours, int slot, IReadOnlyList<int> qs)
    {
        var exp = Expected();
        var s = slot != 0 ? slot : 30;
        var xs = new List<int>();
        var o = qs.ToDictionary(q => q, _ => new List<double>());
        var s0 = (int)Math.Floor(now / (double)s) * s;
        for (var t = s0; t < now + hours * 60; t += s)
        {
            xs.Add(t);
            foreach (var q in qs)
            {
                double sum = 0;
                for (var k = 0; k < s; k++)
                    sum += exp.A[q][ScenarioMath.Mod(t + k, Day) + Pre];
                o[q].Add(sum * 60 / s);
            }
        }

        return new Demand(xs, o);
    }

    /// <summary>Flights whose on-block estimate falls in [from, to), split by lane.</summary>
    public List<ArrivingFlight> FlightsArriving(int from, int to) =>
        Schedule.Arrivals
            .Where(f => f.Eibt >= from && f.Eibt < to)
            .OrderBy(f => f.Eibt)
            .Select(f =>
            {
                var lanes = LaneSplitArr(f.Pax, f.Mix);
                return new ArrivingFlight(f, new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["CRW"] = lanes[0], ["CIT"] = lanes[1], ["RES"] = lanes[2], ["VIS"] = lanes[3], ["EG"] = lanes[4]
                }, f.Eibt + 11);
            })
            .ToList();

    /// <summary>Predicted hall arrivals per lane per minute for [from, from + len) from the given flights.</summary>
    public Dictionary<string, double[]> HallCurve(IEnumerable<ArrivingFlight> flights, int from, int len)
    {
        var ext = Zeros(len + 6);
        var off = from + Pre;
        foreach (var x in flights)
            AddArr(ext, off, len + 6, x.Flight.Seats, x.Flight.Eibt, x.Flight.Load, 11, x.Flight.Mix);
        var o = new Dictionary<string, double[]>(StringComparer.Ordinal);
        for (var k = 0; k < Lanes.Count; k++)
            o[Lanes[k]] = ext[ArrivalQueues[k]][..len];
        return o;
    }
}
