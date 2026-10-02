using System.Collections.Frozen;
using System.Globalization;
using static Ariva.Simulation.Api.Scenarios.Engine.ScenarioModel;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>
/// An alert raised by a rule on the simulated day: when it was raised and cleared, who owns it, who it escalates to
/// and the permission that may see it.
/// </summary>
public sealed record ScenarioAlert
{
    public string Id { get; init; }
    public string RuleId { get; init; }
    public string Rule { get; init; }
    public string Kind { get; init; }
    public string Metric { get; init; }
    public int Q { get; init; }
    public string Sensor { get; init; }
    public string Zone { get; init; }
    public string Severity { get; init; }
    public string Owner { get; init; }
    public string EscalateTo { get; init; }
    public int EscalateAfter { get; init; }
    public int RaisedAt { get; init; }
    public int? ClearedAt { get; set; }
    public string Perm { get; init; }
    public string Text { get; init; }
    public int? Bin { get; init; }
    public string Email { get; init; }
}

internal sealed partial class ScenarioDay
{
    #region Catalogue

    private static readonly FrozenDictionary<string, string> Escalate = new Dictionary<string, string>
    {
        ["Border shift supervisor"] = "Border operations duty officer",
        ["Terminal duty manager"] = "Airport operations centre lead",
        ["Handler B station manager"] = "Terminal duty manager"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static readonly IReadOnlyList<string> ImmIds = ["A-CRW", "A-CIT", "A-RES", "A-VIS", "A-EG", "D-CRW", "D-CIT", "D-RES", "D-VIS", "D-EG"];
    internal static readonly IReadOnlyList<string> AirIds = ["CI-A", "CI-B", "CI-C", "CI-D", "SEC-N", "SEC-S"];
    internal static readonly IReadOnlyList<string> OverflowIds = ["A-OV", "D-OV", "SEC-OV"];

    /// <summary>Snake capacity per queue (people) when no floor plan gives one.</summary>
    internal static readonly FrozenDictionary<string, double> DefaultCaps = new Dictionary<string, double>
    {
        ["A-CRW"] = 10, ["A-CIT"] = 40, ["A-RES"] = 40, ["A-VIS"] = 190, ["A-EG"] = 70, ["D-CRW"] = 8, ["D-CIT"] = 30, ["D-RES"] = 30,
        ["D-VIS"] = 140, ["D-EG"] = 50, ["SEC-N"] = 90, ["SEC-S"] = 90, ["CI-A"] = 70, ["CI-B"] = 70, ["CI-C"] = 70, ["CI-D"] = 70
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The metrics a rule can watch and the alert kind each raises.</summary>
    internal static readonly FrozenDictionary<string, string> MetricKinds = new Dictionary<string, string>
    {
        ["nowcast"] = "nowcast", ["p90bin"] = "sla", ["queue"] = "queue", ["overflow"] = "overflow", ["sensor"] = "device", ["desks"] = "desks"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>R-001 to R-005: the seeded rules that reproduce the reference evening.</summary>
    internal static readonly IReadOnlyList<AlertRule> SeedRules =
    [
        new("R-001", "Nowcast above 15 min", ImmIds, "nowcast", "gt", 15, 10, 12, 1, 1, "critical", "Border shift supervisor", 10, "Border operations duty officer", ""),
        new("R-002", "Overflow band occupied", [.. ImmIds, .. AirIds], "overflow", "is", null, null, null, 3, 3, "warning", "zone", 15, "auto", ""),
        new("R-003", "Sensor offline", [.. ImmIds, .. AirIds, .. OverflowIds], "sensor", "is", null, null, null, 1, 1, "warning", "zone", 15, "systems", ""),
        new("R-004", "Check-in P90 above SLA threshold", ["CI-C", "CI-D"], "p90bin", "gt", 15, null, null, 1, 1, "critical", "Handler B station manager", 15, "Terminal duty manager", ""),
        new("R-005", "Nowcast above 15 min, airport side", AirIds, "nowcast", "gt", 15, 10, 12, 1, 1, "critical", "zone", 10, "auto", "")
    ];

    #endregion

    #region State

    private IReadOnlyDictionary<string, double> _caps;
    private readonly Dictionary<string, (double[] Values, int[] Bins)> _metricCache = new(StringComparer.Ordinal);

    /// <summary>The rules last applied.</summary>
    public IReadOnlyList<AlertRule> Rules { get; private set; } = [];

    /// <summary>Every alert of the day under <see cref="Rules"/>, by raise time then ID.</summary>
    public IReadOnlyList<ScenarioAlert> Alerts { get; private set; } = [];

    #endregion

    #region Targets and metrics

    private sealed record Target(int? Q, string Sensor, string Zone);

    private static QueueDef ZoneDef(string zone)
    {
        if (QueueIndex.TryGetValue(zone, out var q))
            return Queues[q];
        return zone switch
        {
            "A-OV" => Queues[Q("A-VIS")],
            "D-OV" => Queues[Q("D-VIS")],
            "SEC-OV" => Queues[Q("SEC-N")],
            _ => null
        };
    }

    private static string ZoneOwner(QueueDef def)
    {
        if (def.Group is "imm" or "egate")
            return "Border shift supervisor";
        if (def.Group == "sec" || def.Handler == "A")
            return "Terminal duty manager";
        return "Handler B station manager";
    }

    private static string PermFor(QueueDef def)
    {
        if (def.Group is "imm" or "egate")
            return "imm.alerts";
        if (def.Group == "sec")
            return "sec";
        return def.Handler == "A" ? "ci.A" : "ci.B";
    }

    // A missing threshold compares as zero, as null does in the reference.
    private static bool CompareOp(double v, string op, double? threshold)
    {
        var t = threshold ?? 0;
        return op switch
        {
            "gt" => v > t,
            "ge" => v >= t,
            "lt" => v < t,
            "le" => v <= t,
            _ => v > 0
        };
    }

    private static List<Target> RuleTargets(AlertRule rule)
    {
        var scope = rule.Scope ?? [];
        if (rule.Metric == "sensor")
            return Sensors.Where(s => scope.Contains(s.Zone)).Select(s => new Target(null, s.Id, s.Zone)).ToList();
        return scope.Where(id => id is not null && QueueIndex.ContainsKey(id)).Select(id => new Target(Q(id), null, id)).ToList();
    }

    private (double[] Values, int[] Bins) Metric(string metric, Target tg)
    {
        var key = metric + "|" + (tg.Sensor ?? tg.Zone);
        if (_metricCache.TryGetValue(key, out var cached))
            return cached;
        var v = new double[Day];
        int[] bins = null;
        var q = tg.Q ?? -1;
        switch (metric)
        {
            case "nowcast":
                for (var m = 0; m < Day; m++)
                {
                    var st = State(q, m);
                    v[m] = st.Nowcast is null || Degraded(Queues[q].Id, m) is not null ? double.NaN : st.Nowcast.Value;
                }

                break;
            case "queue":
                for (var m = 0; m < Day; m++)
                    v[m] = L[q][m + Pre];
                break;
            case "overflow":
                var id = Queues[q].Id;
                for (var m = 0; m < Day; m++)
                    v[m] = _caps.TryGetValue(id, out var cap) && cap != 0 && L[q][m + Pre] > cap ? 1 : 0;
                break;
            case "desks":
                for (var m = 0; m < Day; m++)
                {
                    var i = m + Pre;
                    v[m] = OpenCount(q, m, Plan(q, m)) - (Open[q][i] - Paused[q][i]);
                }

                break;
            case "sensor":
                for (var m = 0; m < Day; m++)
                    v[m] = SensorOffline(tg.Sensor, m) ? 1 : 0;
                break;
            case "p90bin":
                bins = new int[Day];
                for (var m = 0; m < Day; m++)
                {
                    double best = 0;
                    var bb = -1;
                    for (var bs = (int)Math.Floor(Math.Max(0, m - 150) / 15.0) * 15; bs <= m; bs += 15)
                    {
                        var b = Bin(q, bs, m);
                        if (b.Status == "provisional" && b.P90 is not null && b.P90.Value > best)
                        {
                            best = b.P90.Value;
                            bb = bs;
                        }
                    }

                    v[m] = best;
                    bins[m] = bb;
                }

                break;
        }

        var result = (v, bins);
        _metricCache[key] = result;
        return result;
    }

    #endregion

    #region Rules

    private static string Round0(double v) => ScenarioMath.Round(v).ToString(CultureInfo.InvariantCulture);

    private static ScenarioAlert MakeAlert(AlertRule rule, Target tg, int m, double v, int[] bins)
    {
        var def = tg.Q is not null ? Queues[tg.Q.Value] : ZoneDef(tg.Zone);
        var owner = rule.Owner == "zone" ? ZoneOwner(def) : rule.Owner;
        var imm = def.Group is "imm" or "egate";
        var esc = rule.EscalateTo switch
        {
            "auto" => Escalate.GetValueOrDefault(owner, "Terminal duty manager"),
            "systems" => imm ? "Border systems engineer" : "Airport systems engineer",
            _ => rule.EscalateTo
        };
        var perm = tg.Sensor is not null ? (imm ? "imm.devices" : "devices.air") : PermFor(def);
        var text = rule.Metric switch
        {
            "nowcast" => def.Name + ": nowcast " + Round0(v) + " min",
            "p90bin" => def.Name + ": bin " + ScenarioMath.Clock(bins[m]) + " in breach (provisional)",
            "queue" => def.Name + ": " + Round0(v) + " people queuing",
            "overflow" => def.Name + ": queue beyond snake capacity, overflow band in use",
            "desks" => def.Name + ": " + Round0(v) + " " + (def.Unit ?? "desks") + " below plan",
            _ => "Sensor " + tg.Sensor + " offline" + (Outages.Any(o => o.Sensor == tg.Sensor) ? "; zone degraded, wait shown as a band" : "")
        };
        return new ScenarioAlert
        {
            Id = rule.Id + ":" + (tg.Sensor ?? def.Id) + ":" + m.ToString(CultureInfo.InvariantCulture),
            RuleId = rule.Id,
            Rule = rule.Name,
            Kind = MetricKinds.GetValueOrDefault(rule.Metric, rule.Metric),
            Metric = rule.Metric,
            Q = def.Index,
            Sensor = tg.Sensor,
            Zone = def.Name,
            Severity = rule.Severity,
            Owner = owner,
            EscalateTo = esc,
            EscalateAfter = rule.EscalateAfter != 0 ? rule.EscalateAfter : 10,
            RaisedAt = m,
            ClearedAt = null,
            Perm = perm,
            Text = text,
            Bin = bins?[m],
            Email = rule.Email ?? ""
        };
    }

    /// <summary>Every alert one rule raises over the day, by raise time.</summary>
    public List<ScenarioAlert> EvaluateRule(AlertRule rule)
    {
        var o = new List<ScenarioAlert>();
        var sustain = Math.Max(1, rule.Sustain);
        var clearAfter = Math.Max(1, rule.ClearAfter);
        foreach (var tg in RuleTargets(rule))
        {
            var (val, bins) = Metric(rule.Metric, tg);
            // A queue-length gate needs a queue; the reference would fail on a sensor target, here the gate is skipped.
            var len = rule.MinQueue is > 0 or < 0 && tg.Q is not null ? Metric("queue", tg).Values : null;
            var armed = true;
            int cnt = 0, fcnt = 0;
            ScenarioAlert cur = null;
            for (var m = 0; m < Day; m++)
            {
                var v = val[m];
                if (double.IsNaN(v))
                    continue;
                var cond = CompareOp(v, rule.Op, rule.Threshold) && (len is null || len[m] >= rule.MinQueue!.Value);
                if (armed)
                {
                    if (cond)
                    {
                        cnt++;
                        if (cnt >= sustain)
                        {
                            cur = MakeAlert(rule, tg, m, v, bins);
                            o.Add(cur);
                            armed = false;
                            fcnt = 0;
                        }
                    }
                    else
                    {
                        cnt = 0;
                    }
                }
                else
                {
                    var clr = rule.ClearBelow is not null ? v < rule.ClearBelow.Value : !cond;
                    if (clr)
                    {
                        fcnt++;
                        if (fcnt >= clearAfter)
                        {
                            cur!.ClearedAt = m;
                            armed = true;
                            cnt = 0;
                        }
                    }
                    else
                    {
                        fcnt = 0;
                    }
                }
            }
        }

        return [.. o.OrderBy(a => a.RaisedAt)];
    }

    /// <summary>Applies rules: evaluates every enabled one and orders the alerts by raise time, then ID.</summary>
    public IReadOnlyList<ScenarioAlert> SetRules(IReadOnlyList<AlertRule> rules)
    {
        Rules = rules ?? [];
        var all = new List<ScenarioAlert>();
        foreach (var r in Rules)
            if (r is not null && r.Enabled)
                all.AddRange(EvaluateRule(r));
        Alerts = [.. all.OrderBy(a => a.RaisedAt).ThenBy(a => a.Id, StringComparer.Ordinal)];
        return Alerts;
    }

    /// <summary>Replaces the snake capacities and recomputes the overflow metric.</summary>
    public void SetCaps(IReadOnlyDictionary<string, double> caps)
    {
        _caps = caps ?? DefaultCaps;
        foreach (var key in _metricCache.Keys.Where(k => k.StartsWith("overflow|", StringComparison.Ordinal)).ToList())
            _metricCache.Remove(key);
    }

    #endregion
}
