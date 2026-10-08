using System.Globalization;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>An arriving flight: scheduled, actual on-block and estimated on-block minutes, load and walk.</summary>
internal sealed class ArrivalFlight
{
    public string Code { get; init; }
    public string Carrier { get; init; }
    public int Sched { get; init; }
    public int OnBlock { get; init; }
    public int Seats { get; init; }
    public double Booked { get; init; }
    public double Load { get; init; }
    public int Walk { get; init; }
    public int EibtErr { get; init; }
    public PaxMix Mix { get; init; }
    public bool Scripted { get; init; }
    public string Adhoc { get; init; }
    public int Pax { get; set; }
    public int Eibt { get; set; }
}

/// <summary>A departing flight: scheduled departure, island, handler, load and show-up shift.</summary>
internal sealed class DepartureFlight
{
    public string Code { get; init; }
    public string Carrier { get; init; }
    public int Std { get; init; }
    public int Seats { get; init; }
    public double Booked { get; init; }
    public double Load { get; init; }
    public int Shift { get; init; }
    public string Island { get; init; }
    public string Handler { get; init; }
    public PaxMix Mix { get; init; }
    public bool Scripted { get; init; }
    public string Adhoc { get; init; }
    public int Pax { get; set; }
}

/// <summary>The day's flights, arrivals sorted by on-block, departures by scheduled departure.</summary>
internal sealed class ScenarioSchedule
{
    public List<ArrivalFlight> Arrivals { get; private set; } = [];
    public List<DepartureFlight> Departures { get; private set; } = [];

    /// <summary>The seeded schedule: banks of random flights from two streams, plus the scripted evening.</summary>
    public static ScenarioSchedule Build(uint seed)
    {
        var ra = new Mulberry32(ScenarioMath.Mix32(seed ^ 0x5eed1234u));
        var rd = new Mulberry32(ScenarioMath.Mix32(seed ^ 0x0de9a27u));
        var r = ra;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in ScenarioModel.ScriptArrivals)
            used.Add(f.Code);
        foreach (var f in ScenarioModel.ScriptDepartures)
            used.Add(f.Code);

        string Code(string c)
        {
            while (true)
            {
                var s = c + " " + (100 + (int)Math.Floor(r.Next() * 880)).ToString(CultureInfo.InvariantCulture);
                if (used.Add(s))
                    return s;
            }
        }

        int PickSeats() => ScenarioModel.Seats[(int)Math.Floor(r.Next() * ScenarioModel.Seats.Length)];

        string PickCarrier(double w0, double w1)
        {
            var x = r.Next();
            return x < w0 ? "DM" : x < w0 + w1 ? "XR" : "QL";
        }

        PaxMix JitterMix(PaxMix b)
        {
            var cit = b.Cit * (0.9 + r.Next() * 0.2);
            var s = cit;
            var res = b.Res * (0.9 + r.Next() * 0.2);
            s += res;
            var vis = b.Vis * (0.9 + r.Next() * 0.2);
            s += vis;
            var crw = b.Crw * (0.9 + r.Next() * 0.2);
            s += crw;
            var f = (1 - b.Trf) / s;
            return new PaxMix(cit * f, res * f, vis * f, crw * f, b.Trf);
        }

        var arr = new List<ArrivalFlight>();
        foreach (var b in ScenarioModel.ArrBanks)
        {
            for (var j = 0; j < b.N; j++)
            {
                var sched = ScenarioMath.RoundToInt(b.From + (b.To - b.From) * (j + r.Next()) / b.N);
                var c = PickCarrier(0.4, 0.3);
                var booked = ScenarioMath.Round((0.70 + r.Next() * 0.25) * 100) / 100;
                var load = ScenarioMath.Clamp(ScenarioMath.Round((booked + (r.Next() - 0.5) * 0.08) * 100) / 100, 0.70, 0.95);
                var delay = (int)ScenarioMath.Clamp(ScenarioMath.Round((r.Next() + r.Next() + r.Next() - 1.5) * 12), -20, 20);
                var code = Code(c);
                var seats = PickSeats();
                var walk = 8 + (int)Math.Floor(r.Next() * 8);
                var eibtErr = ScenarioMath.RoundToInt((r.Next() - 0.5) * 6);
                var mix = JitterMix(ScenarioModel.Carriers[c].Mix);
                arr.Add(new ArrivalFlight
                {
                    Code = code, Carrier = c, Sched = sched, OnBlock = sched + delay, Seats = seats, Booked = booked, Load = load,
                    Walk = walk, EibtErr = eibtErr, Mix = mix
                });
            }
        }

        foreach (var s in ScenarioModel.ScriptArrivals)
        {
            arr.Add(new ArrivalFlight
            {
                Code = s.Code, Carrier = s.Carrier, Sched = s.Sched, OnBlock = s.OnBlock, Seats = s.Seats, Booked = s.Booked, Load = s.Load,
                Walk = s.Walk, EibtErr = s.EibtErr, Mix = ScenarioModel.Carriers[s.Carrier].Mix, Scripted = true
            });
        }

        var dmToggle = 0;
        r = rd;
        var dep = new List<DepartureFlight>();
        foreach (var b in ScenarioModel.DepBanks)
        {
            for (var j = 0; j < b.N; j++)
            {
                var std = ScenarioMath.RoundToInt(b.From + (b.To - b.From) * (j + r.Next()) / b.N);
                var c = PickCarrier(0.5, 0.25);
                if (c == "XR" && std >= 1140 && std <= 1335)
                    c = "QL";
                var booked = ScenarioMath.Round((0.70 + r.Next() * 0.25) * 100) / 100;
                var load = ScenarioMath.Clamp(ScenarioMath.Round((booked + (r.Next() - 0.5) * 0.08) * 100) / 100, 0.70, 0.95);
                var island = c == "DM" ? (dmToggle++ % 2 != 0 ? "B" : "A") : ScenarioModel.Carriers[c].Island;
                var code = Code(c);
                var seats = PickSeats();
                var shift = ScenarioMath.RoundToInt((r.Next() - 0.5) * 10);
                dep.Add(new DepartureFlight
                {
                    Code = code, Carrier = c, Std = std, Seats = seats, Booked = booked, Load = load, Shift = shift, Island = island,
                    Handler = ScenarioModel.Carriers[c].Handler, Mix = ScenarioModel.Carriers[c].Mix
                });
            }
        }

        foreach (var s in ScenarioModel.ScriptDepartures)
        {
            dep.Add(new DepartureFlight
            {
                Code = s.Code, Carrier = s.Carrier, Std = s.Std, Seats = s.Seats, Booked = s.Booked, Load = s.Load, Shift = s.Shift,
                Island = "C", Handler = "B", Mix = ScenarioModel.Carriers["XR"].Mix, Scripted = true
            });
        }

        foreach (var f in arr)
        {
            f.Pax = ScenarioMath.RoundToInt(f.Seats * f.Load);
            f.Eibt = f.OnBlock + f.EibtErr;
        }

        foreach (var f in dep)
            f.Pax = ScenarioMath.RoundToInt(f.Seats * f.Load);

        var schedule = new ScenarioSchedule { Arrivals = arr, Departures = dep };
        schedule.Sort();
        return schedule;
    }

    /// <summary>A schedule of the given flights (a site's own draw, ARV-139b), sorted as the reference sorts.</summary>
    public static ScenarioSchedule Of(IEnumerable<ArrivalFlight> arrivals, IEnumerable<DepartureFlight> departures)
    {
        var schedule = new ScenarioSchedule { Arrivals = [.. arrivals ?? []], Departures = [.. departures ?? []] };
        schedule.Sort();
        return schedule;
    }

    /// <summary>
    /// Adds ad-hoc flights. Each draws from its own stream (seed plus flight ID), so existing flights never shift.
    /// Invalid entries are skipped, as in the reference; so are departures at a site without them (ARV-139b).
    /// </summary>
    public void AddAdhoc(uint seed, IReadOnlyList<AdhocFlight> flights, ScenarioSite site = null)
    {
        site ??= ScenarioSites.Dmo;
        foreach (var x in flights ?? [])
        {
            if (x is null || string.IsNullOrEmpty(x.Id) || string.IsNullOrEmpty(x.Code) || (x.Direction != "arr" && x.Direction != "dep"))
                continue;
            if (x.Direction == "dep" && !site.HasDepartures)
                continue;
            var r = new Mulberry32(ScenarioMath.Mix32(seed ^ ScenarioMath.StrHash(x.Id)));
            var mix = x.Mix ?? site.BaseMix;
            var load = ScenarioMath.Clamp(x.Load is > 0 or < 0 ? x.Load.Value : 0.8, 0.3, 1);
            var actual = ScenarioMath.Clamp(ScenarioMath.Round((load + (r.Next() - 0.5) * 0.04) * 100) / 100, 0.3, 1);
            var seats = ScenarioMath.RoundToInt(x.Seats is > 0 or < 0 ? x.Seats.Value : 180);
            var t = ScenarioMath.RoundToInt(x.Time ?? 0);
            var carrier = x.Code.Length > 2 ? x.Code[..2] : x.Code;
            if (x.Direction == "arr")
            {
                Arrivals.Add(new ArrivalFlight
                {
                    Code = x.Code, Carrier = carrier, Sched = t, OnBlock = t, Seats = seats, Booked = load, Load = actual,
                    Walk = 8 + (int)Math.Floor(r.Next() * 8), EibtErr = 0, Eibt = t, Mix = mix, Pax = ScenarioMath.RoundToInt(seats * actual), Adhoc = x.Id
                });
            }
            else
            {
                var isl = x.Island is "A" or "B" or "C" or "D" ? x.Island : x.Handler == "A" ? "A" : "C";
                Departures.Add(new DepartureFlight
                {
                    Code = x.Code, Carrier = carrier, Std = t, Seats = seats, Booked = load, Load = actual, Shift = ScenarioMath.RoundToInt((r.Next() - 0.5) * 10),
                    Island = isl, Handler = string.CompareOrdinal(isl, "C") < 0 ? "A" : "B", Mix = mix, Pax = ScenarioMath.RoundToInt(seats * actual), Adhoc = x.Id
                });
            }
        }

        Sort();
    }

    private void Sort()
    {
        // Stable, as Array.prototype.sort is: flights with the same minute keep their insertion order.
        Arrivals = [.. Arrivals.OrderBy(f => f.OnBlock)];
        Departures = [.. Departures.OrderBy(f => f.Std)];
    }
}
