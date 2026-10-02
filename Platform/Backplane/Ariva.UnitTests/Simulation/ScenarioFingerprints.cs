using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// The C# side of scripts/simulation/reference-golden.mjs: the same outputs, in the same order, fed to SHA-256 the
/// same way (float64 little endian with a canonical NaN for NaN and null and zero for negative zero; strings as UTF-8
/// plus a zero byte).
/// </summary>
internal static class ScenarioFingerprints
{
    private sealed class Feed : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _buffer = new byte[8];

        public void Num(double? v)
        {
            var bits = v is null || double.IsNaN(v.Value) ? 0x7ff8000000000000UL : v.Value == 0 ? 0UL : (ulong)BitConverter.DoubleToInt64Bits(v.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer, bits);
            _hash.AppendData(_buffer);
        }

        public void Nums(IEnumerable<double> values)
        {
            foreach (var v in values)
                Num(v);
        }

        public void Str(string s)
        {
            _hash.AppendData(Encoding.UTF8.GetBytes(s ?? ""));
            _hash.AppendData([0]);
        }

        public string Hex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }

    private static void Mix(Feed f, PaxMix m)
    {
        f.Num(m.Cit); f.Num(m.Res); f.Num(m.Vis); f.Num(m.Crw); f.Num(m.Trf);
    }

    private static IEnumerable<double> D(IEnumerable<sbyte> a) => a.Select(x => (double)x);
    private static IEnumerable<double> D(IEnumerable<byte> a) => a.Select(x => (double)x);
    private static IEnumerable<double> D(IEnumerable<short> a) => a.Select(x => (double)x);
    private static IEnumerable<double> D(IEnumerable<int> a) => a.Select(x => (double)x);
    private static IEnumerable<double> D(IEnumerable<float> a) => a.Select(x => (double)x);

    /// <summary>Runs one case and returns its fingerprints, named as in the golden file.</summary>
    public static Dictionary<string, string> Compute(ScenarioConfig cfg, out ScenarioDay day)
    {
        day = ScenarioDay.Run(cfg);
        var nq = ScenarioModel.NQ;
        var o = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var f = new Feed())
        {
            foreach (var x in day.Schedule.Arrivals)
            {
                f.Str(x.Code); f.Str(x.Carrier);
                foreach (var v in new double[] { x.Sched, x.OnBlock, x.Eibt, x.Seats, x.Booked, x.Load, x.Walk, x.EibtErr, x.Pax })
                    f.Num(v);
                Mix(f, x.Mix);
            }

            foreach (var x in day.Schedule.Departures)
            {
                f.Str(x.Code); f.Str(x.Carrier);
                foreach (var v in new double[] { x.Std, x.Seats, x.Booked, x.Load, x.Shift })
                    f.Num(v);
                f.Str(x.Island); f.Str(x.Handler); f.Num(x.Pax);
                Mix(f, x.Mix);
            }

            o["schedule"] = f.Hex();
        }

        using (var f = new Feed())
        {
            foreach (var r in day.Roster)
                f.Nums(D(r));
            o["roster"] = f.Hex();
        }

        using (var f = new Feed())
        {
            for (var q = 0; q < nq; q++)
            {
                f.Nums(day.A[q]); f.Nums(day.D[q]); f.Nums(day.L[q]); f.Nums(day.C[q]); f.Nums(day.R5[q]);
                f.Nums(D(day.Open[q])); f.Nums(D(day.Paused[q])); f.Nums(day.MeanWait[q]); f.Nums(D(day.LastExit[q]));
            }

            f.Nums(day.Rej[0]); f.Nums(day.Rej[1]);
            o["run"] = f.Hex();
        }

        using (var f = new Feed())
        {
            foreach (var list in day.Servers)
            {
                foreach (var s in list)
                {
                    f.Str(s.Id); f.Num(s.Factor); f.Nums(D(s.Pause)); f.Nums(D(s.Unknown)); f.Num(s.OutOfService ? 1 : 0);
                }
            }

            o["servers"] = f.Hex();
        }

        using (var f = new Feed())
        {
            var ex = day.Expected();
            for (var q = 0; q < nq; q++)
            {
                f.Nums(ex.A[q]); f.Nums(ex.D[q]); f.Nums(ex.L[q]); f.Nums(ex.C[q]);
            }

            o["expected"] = f.Hex();
        }

        using (var f = new Feed())
        {
            var da = day.DayAhead();
            for (var q = 0; q < nq; q++)
            {
                f.Nums(da.A[q]); f.Nums(da.W[q]);
            }

            o["dayAhead"] = f.Hex();
        }

        using (var f = new Feed())
        {
            foreach (var a in day.Alerts)
            {
                foreach (var s in new[] { a.Id, a.RuleId, a.Rule, a.Kind, a.Sensor, a.Zone, a.Severity, a.Owner, a.EscalateTo, a.Perm, a.Text })
                    f.Str(s);
                f.Num(a.Q); f.Num(a.RaisedAt); f.Num(a.ClearedAt); f.Num(a.EscalateAfter); f.Num(a.Bin);
            }

            o["alerts"] = f.Hex();
            o["alertCount"] = day.Alerts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using (var f = new Feed())
        {
            for (var q = 0; q < nq; q++)
            {
                for (var bs = 0; bs < ScenarioModel.Day; bs += 15)
                {
                    var b = day.Bin(q, bs, 1439);
                    f.Num(b.P90); f.Num(b.Pax); f.Str(b.Status);
                }
            }

            o["bins"] = f.Hex();
        }

        using (var f = new Feed())
        {
            for (var m = 0; m < ScenarioModel.Day; m += 7)
            {
                for (var q = 0; q < nq; q++)
                {
                    var st = day.State(q, m);
                    foreach (var v in new double?[] { st.Length, st.Rate, st.Capacity, st.Open, st.Paused, st.Active, st.Nowcast, st.Arrivals, st.Served })
                        f.Num(v);
                    f.Num(st.Band?[0]); f.Num(st.Band?[1]); f.Num(st.Degraded ? 1 : 0);
                }
            }

            o["states"] = f.Hex();
        }

        using (var f = new Feed())
        {
            var fc = day.ForecastFrom(1080);
            for (var q = 0; q < nq; q++)
            {
                var x = fc.Queues[q];
                f.Nums(x.P10); f.Nums(x.P50); f.Nums(x.P90); f.Nums(D(x.Raw)); f.Nums(D(x.Weights));
            }

            o["forecast"] = f.Hex();
        }

        using (var f = new Feed())
        {
            var rec = day.Recommend(1080, "arr", 24);
            foreach (var id in ScenarioModel.Areas["arr"].Queues)
            {
                f.Str(id); f.Nums(D(rec.Rec[id])); f.Nums(D(rec.Planned[id])); f.Nums(rec.LaneP90Plan[id]); f.Nums(rec.LaneP90Rec[id]);
            }

            f.Nums(rec.P90Plan); f.Nums(rec.P90Rec); f.Num(rec.From); f.Num(rec.Until);
            var rec2 = day.Recommend(-1, "ciB", 96, predict: false);
            foreach (var id in ScenarioModel.Areas["ciB"].Queues)
            {
                f.Str(id); f.Nums(D(rec2.Rec[id])); f.Nums(D(rec2.Planned[id]));
            }

            o["recommend"] = f.Hex();
        }

        using (var f = new Feed())
        {
            int aVis = ScenarioModel.Q("A-VIS"), ciC = ScenarioModel.Q("CI-C");
            foreach (var p in day.WaitSeries(aVis, 1000, 1200, 1150))
            {
                f.Num(p.M); f.Num(p.W); f.Num(p.Final ? 1 : 0); f.Num(p.Empty ? 1 : 0);
            }

            for (var q = 0; q < nq; q++)
            {
                var p = day.P90(q, 0, ScenarioModel.Day);
                f.Num(p.P90); f.Num(p.Pax);
                var p2 = day.P90(q, 1000, 1200, 1150);
                f.Num(p2.P90); f.Num(p2.Pax);
            }

            var bst = day.BinStatsFor(ciC, 1140, 60, 1300);
            f.Num(bst.Pax); f.Str(bst.Status); f.Num(bst.Pct(0.9)); f.Num(bst.Pct(0.5)); f.Num(bst.Share(15));
            o["waits"] = f.Hex();
        }

        using (var f = new Feed())
        {
            for (var q = 0; q < nq; q++)
            {
                foreach (var s in day.ServerStates(q, 1100))
                {
                    f.Str(s.Id); f.Str(s.State); f.Num(s.K); f.Num(s.Svc);
                }

                foreach (var s in day.ServerIntervals(q, 1095, 1100))
                {
                    f.Str(s.Id); f.Num(s.Pax); f.Num(s.Minutes); f.Num(s.Svc);
                }
            }

            foreach (var side in new[] { "A", "D" })
            {
                var e = day.Egate(side, 1100);
                foreach (var v in new double[] { e.Processed, e.Rejects, e.RejectRate, e.Util, e.InService, e.Total })
                    f.Num(v);
            }

            o["serversAt"] = f.Hex();
        }

        using (var f = new Feed())
        {
            var all = Enumerable.Range(0, nq).ToList();
            var dm = day.DemandFrom(1080, 24, 30, all);
            f.Nums(D(dm.Xs));
            foreach (var q in all)
                f.Nums(dm.Queues[q]);
            var fl = day.FlightsArriving(1020, 1110);
            foreach (var x in fl)
            {
                f.Str(x.Flight.Code);
                foreach (var k in ScenarioModel.Lanes)
                    f.Num(x.Lanes[k]);
                f.Num(x.HallFrom);
            }

            var hc = day.HallCurve(fl, 1020, 90);
            foreach (var k in ScenarioModel.Lanes)
                f.Nums(hc[k]);
            o["flows"] = f.Hex();
        }

        return o;
    }

    /// <summary>Reads a case configuration written by the generator (the reference's own field names).</summary>
    public static ScenarioConfig ReadConfig(JsonElement c)
    {
        static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

        return new ScenarioConfig
        {
            Seed = (uint)c.GetProperty("seed").GetDouble(),
            Allocations = Arr(c, "allocations").Select(a => new CounterAllocation(Str(a, "id"), Str(a, "flight"), Str(a, "island"),
                (int)Num(a, "fromCtr")!, (int)Num(a, "toCtr")!, (int)Num(a, "open")!, (int)Num(a, "close")!, Str(a, "by"))).ToList(),
            Flights = Arr(c, "flights").Select(x => new AdhocFlight(Str(x, "id"), Str(x, "code"), Str(x, "dir"), Num(x, "time"), Num(x, "seats"),
                Num(x, "load"), Str(x, "island"), Str(x, "handler"), null)).ToList(),
            Overrides = Arr(c, "overrides").Select(x => new RosterOverride(Str(x, "q"), (int)Num(x, "from")!, (int)Num(x, "to")!, (int)Num(x, "n")!)).ToList(),
            Accepted = Arr(c, "accepted").Select(x => new AcceptedPlan(Str(x, "area"), (int)Num(x, "from")!, (int)Num(x, "until")!,
                x.GetProperty("counts").EnumerateObject().ToDictionary(p => p.Name,
                    p => (IReadOnlyList<int>)p.Value.EnumerateArray().Select(v => v.GetInt32()).ToList()))).ToList()
        };
    }
}
