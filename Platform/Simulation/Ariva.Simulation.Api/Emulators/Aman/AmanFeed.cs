using System.Globalization;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Emulators.Aman;

/// <summary>Which border side an emulated immigration system reports: arrival immigration, departure immigration, or both.</summary>
[Flags]
public enum BorderSides
{
    None = 0,
    Arrival = 1,
    Departure = 2,
    Both = Arrival | Departure
}

/// <summary>
/// AMAN's own codes for the demo airport's border desks and e-gates (ARV-029). AMAN numbers its positions its own way,
/// so Ariva must map them through its desk code mappings (ARV-015): arrival desks IN01 to IN22 are Ariva's AR-01 to
/// AR-22, arrival e-gates EGIN1 to EGIN6 are AG-1 to AG-6, departure desks OUT01 to OUT22 are DP-01 to DP-22 and
/// departure e-gates EGOUT1 to EGOUT4 are DG-1 to DG-4.
/// </summary>
public static class AmanCodes
{
    /// <summary>AMAN's code of a scenario server (Ariva's desk code), or null when it is not a border position.</summary>
    public static string Of(string arivaCode)
    {
        if (arivaCode is null)
            return null;
        static string Number(string code, int from) => int.TryParse(code.AsSpan(from), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null;
        return arivaCode switch
        {
            _ when arivaCode.StartsWith("AR-", StringComparison.Ordinal) => "IN" + arivaCode[3..],
            _ when arivaCode.StartsWith("DP-", StringComparison.Ordinal) => "OUT" + arivaCode[3..],
            _ when arivaCode.StartsWith("AG-", StringComparison.Ordinal) && Number(arivaCode, 3) is { } n => "EGIN" + n,
            _ when arivaCode.StartsWith("DG-", StringComparison.Ordinal) && Number(arivaCode, 3) is { } n => "EGOUT" + n,
            _ => null
        };
    }

    /// <summary>Every (AMAN code, Ariva code) pair of the demo airport, for setting up Ariva's desk code mappings.</summary>
    public static IReadOnlyList<(string Aman, string Ariva)> All() =>
    [
        .. ScenarioModel.Queues.Where(q => q.Group is "imm" or "egate").SelectMany(q => q.Servers).Select(s => (Of(s), s)).Where(p => p.Item1 is not null)
    ];
}

/// <summary>What AMAN publishes for one demo minute: desk session changes, closed one-minute desk and e-gate intervals, and lane demand.</summary>
public sealed record AmanMinute(
    int Minute,
    IReadOnlyList<DeskSessionChanged> Sessions,
    IReadOnlyList<DeskIntervalStats> Desks,
    IReadOnlyList<EGateIntervalStats> Gates,
    IReadOnlyList<InboundFlightLaneDemand> Demand)
{
    public int Count => Sessions.Count + Desks.Count + Gates.Count + Demand.Count;
}

/// <summary>
/// The AMAN feed of the demo day (ARV-029), derived from the scenario as AMAN would publish it, deterministic for a seed:
/// <list type="bullet">
/// <item>A desk session changes when the scenario's desk opens, pauses or closes (closed or out of service is Closed,
/// paused is Paused, anything else Opened), with the lane it serves. The first minute of a run states every desk that
/// is not closed, as a feed joining mid-day would.</item>
/// <item>Each desk with an open or paused session reports the interval that just closed: the people the scenario's lane
/// processed that minute, shared between its active desks by their speed; documents are people, transactions are
/// approaches (families of 1.25 on average); service and cycle times follow from the lane's service time.</item>
/// <item>Each e-gate in service reports its attempts, accepted and rejected with the scenario's reject rate, rejects split by
/// coarse category and suppressed: a category with fewer than 3 rejects goes to Other.</item>
/// <item>Each inbound flight's lane demand is computed from API data two hours before its scheduled arrival (booked
/// passengers) and again 30 minutes before (boarded), split by the carrier's mix; e-gate eligible are 40 % of citizens
/// and residents, transfers are excluded.</item>
/// </list>
/// Counts are whole numbers: a fractional share is rounded up with a deterministic probability (its fraction), so the
/// feed adds up to the scenario on average. Times come from <see cref="FeedTime"/>.
/// </summary>
public static class AmanFeed
{
    private const double FamilySize = 1.25;
    private const double EgateShare = 0.40;
    private const int SmallCell = 3;

    private static readonly (EGateRejectCategory Category, double Share)[] RejectMix =
    [
        (EGateRejectCategory.DocumentRead, 0.30), (EGateRejectCategory.BiometricCapture, 0.30), (EGateRejectCategory.Eligibility, 0.20),
        (EGateRejectCategory.ReferredToOfficer, 0.12), (EGateRejectCategory.Technical, 0.08)
    ];

    /// <summary>The flight key AMAN and the emulated AODB use: carrier, number, origin date and direction (A).</summary>
    public static string FlightKey(string code, DateTime originUtc, bool arrival)
    {
        var (carrier, number) = Split(code);
        return $"{carrier}{number}-{originUtc:yyyyMMdd}-{(arrival ? "A" : "D")}";
    }

    /// <summary>A scenario flight code ("DM 214") as carrier and number.</summary>
    public static (string Carrier, string Number) Split(string code)
    {
        var parts = (code ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (code, string.Empty);
    }

    /// <summary>
    /// What AMAN publishes when demo minute <paramref name="minute"/> completes, for <paramref name="sides"/>.
    /// <paramref name="timeOf"/> gives a demo minute's UTC time; <paramref name="firstOfRun"/> states every open desk.
    /// </summary>
    internal static AmanMinute Build(ScenarioDay day, int minute, Func<int, DateTime> timeOf, string siteCode, BorderSides sides, bool firstOfRun,
        string system = "aman")
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(timeOf);
        var at = new DateTimeOffset(timeOf(minute), TimeSpan.Zero);
        // Source event ids are the system's own: AMAN's and another immigration system's never coincide.
        var tag = $"{system}-{day.Seed.ToString(CultureInfo.InvariantCulture)}-{timeOf(0):yyyyMMdd}";
        var sessions = new List<DeskSessionChanged>();
        var desks = new List<DeskIntervalStats>();
        var gates = new List<EGateIntervalStats>();
        var demand = new List<InboundFlightLaneDemand>();

        foreach (var (side, sideFlag) in new[] { ("A", BorderSides.Arrival), ("D", BorderSides.Departure) })
        {
            if ((sides & sideFlag) == 0)
                continue;
            foreach (var lane in new[] { "CRW", "CIT", "RES", "VIS" })
                Desks(day, ScenarioModel.Q(side + "-" + lane), lane, minute, at, siteCode, tag, firstOfRun, sessions, desks);
            Gates(day, side, minute, at, siteCode, tag, gates);
        }

        if ((sides & BorderSides.Arrival) != 0)
            Demand(day, minute, timeOf, siteCode, tag, firstOfRun, demand);
        return new AmanMinute(minute, sessions, desks, gates, demand);
    }

    private static DeskSessionState? SessionOf(string state) => state switch
    {
        "closed" or "oos" => null,
        "paused" => DeskSessionState.Paused,
        _ => DeskSessionState.Opened
    };

    private static void Desks(ScenarioDay day, int q, string lane, int minute, DateTimeOffset at, string siteCode, string tag, bool firstOfRun,
        List<DeskSessionChanged> sessions, List<DeskIntervalStats> desks)
    {
        var now = day.ServerStates(q, minute);
        var before = day.ServerStates(q, minute - 1);
        var served = day.D[q][minute + ScenarioModel.Pre];
        var active = now.Where(s => s.State is "serving" or "idle" or "unknown").ToList();
        var speed = active.Sum(s => 1 / Math.Max(s.Svc, 1));
        for (var k = 0; k < now.Count; k++)
        {
            var code = AmanCodes.Of(now[k].Id);
            var state = SessionOf(now[k].State);
            var previous = SessionOf(before[k].State);
            // A run's first minute states the desks with a session, as a feed joining mid-day would; then changes only.
            if (firstOfRun ? state is not null : state != previous)
            {
                sessions.Add(new DeskSessionChanged(siteCode, code, state ?? DeskSessionState.Closed, state is null ? string.Empty : lane, at,
                    $"{tag}-ds-{code}-{minute.ToString("D4", CultureInfo.InvariantCulture)}"));
            }

            if (state is null)
                continue;
            var share = state == DeskSessionState.Opened && speed > 0 ? served * (1 / Math.Max(now[k].Svc, 1)) / speed : 0;
            var documents = Whole(share, tag, code, minute, 1);
            var transactions = documents == 0 ? 0 : Math.Clamp(Whole(documents / FamilySize, tag, code, minute, 2), 1, documents);
            var service = transactions == 0 ? 0 : Math.Round(now[k].Svc * documents / transactions, 1);
            desks.Add(new DeskIntervalStats(siteCode, code, at, 60, transactions, documents, service, Math.Round(service * 1.6, 1),
                transactions == 0 ? 0 : Math.Round(60.0 / transactions, 1), lane, $"{tag}-dk-{code}-{minute.ToString("D4", CultureInfo.InvariantCulture)}"));
        }
    }

    private static void Gates(ScenarioDay day, string side, int minute, DateTimeOffset at, string siteCode, string tag, List<EGateIntervalStats> gates)
    {
        var q = ScenarioModel.Q(side + "-EG");
        var i = minute + ScenarioModel.Pre;
        var now = day.ServerStates(q, minute);
        var active = now.Where(s => s.State is "serving" or "idle" or "unknown").ToList();
        if (active.Count == 0)
            return;
        var rate = day.Rej[side == "A" ? 0 : 1][i];
        var each = day.D[q][i] / active.Count;
        foreach (var gate in active)
        {
            var code = AmanCodes.Of(gate.Id);
            var attempts = Whole(each, tag, code, minute, 3);
            var rejected = Math.Min(attempts, Whole(each * rate, tag, code, minute, 4));
            gates.Add(new EGateIntervalStats(siteCode, code, at, 60, attempts, attempts - rejected, rejected, Categories(rejected, tag, code, minute),
                Math.Round(gate.Svc, 1), $"{tag}-eg-{code}-{minute.ToString("D4", CultureInfo.InvariantCulture)}"));
        }
    }

    /// <summary>Rejects by category (largest remainder), with every category under 3 folded into Other.</summary>
    internal static IReadOnlyDictionary<EGateRejectCategory, int> Categories(int rejected, string tag, string code, int minute)
    {
        var result = new Dictionary<EGateRejectCategory, int>();
        if (rejected == 0)
            return result;
        var exact = RejectMix.Select(m => (m.Category, Value: rejected * m.Share)).ToList();
        var counts = exact.ToDictionary(e => e.Category, e => (int)Math.Floor(e.Value));
        var left = rejected - counts.Values.Sum();
        foreach (var (category, _) in exact.OrderByDescending(e => e.Value - Math.Floor(e.Value) + Unit(tag, code, minute, 10 + (int)e.Category) * 1e-6).Take(left))
            counts[category]++;
        foreach (var (category, count) in counts.Where(c => c.Value > 0))
        {
            var key = count < SmallCell ? EGateRejectCategory.Other : category;
            result[key] = result.GetValueOrDefault(key) + count;
        }

        return result;
    }

    private static void Demand(ScenarioDay day, int minute, Func<int, DateTime> timeOf, string siteCode, string tag, bool firstOfRun, List<InboundFlightLaneDemand> demand)
    {
        foreach (var flight in day.Schedule.Arrivals)
        {
            // Two computations: from bookings two hours out, from boarding 30 minutes out. A run starting in between
            // publishes the latest one due.
            var first = flight.Sched - 120;
            var second = flight.Sched - 30;
            var which = minute == second || (firstOfRun && minute > second && minute < flight.Sched) ? 2
                : minute == first || (firstOfRun && minute > first && minute < second) ? 1
                : 0;
            if (which == 0)
                continue;
            var passengers = which == 1 ? (int)Math.Round(flight.Seats * flight.Booked) : flight.Pax;
            var origin = timeOf(flight.Sched - BlockMinutes(flight.Code));
            var key = FlightKey(flight.Code, origin, arrival: true);
            demand.Add(LaneDemand(siteCode, key, timeOf(flight.Sched), passengers, flight.Mix, new DateTimeOffset(timeOf(minute), TimeSpan.Zero),
                $"{tag}-ld-{key}-{which.ToString(CultureInfo.InvariantCulture)}"));
        }
    }

    /// <summary>A flight's lane demand: the carrier's mix of the boarded passengers, transfers excluded, 40 % of citizens and residents e-gate eligible.</summary>
    public static InboundFlightLaneDemand LaneDemand(string siteCode, string flightKey, DateTime scheduledUtc, int boarded, PaxMix mix, DateTimeOffset computedAt,
        string sourceEventId)
    {
        ArgumentNullException.ThrowIfNull(mix);
        var cit = (int)Math.Round(boarded * mix.Cit, MidpointRounding.AwayFromZero);
        var res = (int)Math.Round(boarded * mix.Res, MidpointRounding.AwayFromZero);
        var vis = (int)Math.Round(boarded * mix.Vis, MidpointRounding.AwayFromZero);
        var crw = (int)Math.Round(boarded * mix.Crw, MidpointRounding.AwayFromZero);
        var eligible = (int)Math.Round((cit + res) * EgateShare, MidpointRounding.AwayFromZero);
        var eligibleCit = (int)Math.Round(cit * EgateShare, MidpointRounding.AwayFromZero);
        var lanes = new Dictionary<string, int> { ["CIT"] = cit - eligibleCit, ["RES"] = res - (eligible - eligibleCit), ["VIS"] = vis, ["CRW"] = crw };
        return new InboundFlightLaneDemand(siteCode, flightKey, new DateTimeOffset(scheduledUtc, TimeSpan.Zero), boarded, lanes, eligible, computedAt, sourceEventId);
    }

    /// <summary>The scheduled flight time of an inbound flight, 1 to 6 hours, fixed per flight code.</summary>
    public static int BlockMinutes(string code) => 60 + (int)(Fnv(code ?? string.Empty) % 300);

    /// <summary>A share as a whole count: rounded up with probability equal to its fraction, the same way every time.</summary>
    private static int Whole(double value, string tag, string code, int minute, int salt)
    {
        if (!(value > 0))
            return 0;
        var floor = Math.Floor(value);
        return (int)floor + (Unit(tag, code, minute, salt) < value - floor ? 1 : 0);
    }

    private static double Unit(string tag, string code, int minute, int salt) =>
        Fnv(string.Create(CultureInfo.InvariantCulture, $"{tag}|{code}|{minute}|{salt}")) / 4294967296.0;

    private static uint Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }
}
