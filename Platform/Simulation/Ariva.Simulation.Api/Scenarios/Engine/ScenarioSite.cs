using System.Collections.Frozen;

namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>A server out of service on the demo day, in clock minutes [From, To): an e-gate's maintenance or fault.</summary>
internal sealed record OutOfService(string Server, int From, int To, string Note);

/// <summary>
/// A scenario site (ARV-139b): everything the scenario engine needs to know about one airport's queues to simulate its
/// day. The engine (<see cref="ScenarioDay"/>) takes the site as a parameter; the queues and their servers, the staffing
/// areas, the arrival lanes and how a flight's passengers split between them, the sensors and what each reports, the
/// scripted evening (roster lines, servers out of service, sensor outages), the seeded alert rules and snake capacities,
/// and how the day's flights are drawn from a seed. Two sites exist: the fictional reference airport
/// (<see cref="DmoScenarioSite"/>, seed 9303, ported from the prototype's sim.js bit for bit) and the illustrative AUH
/// Terminal A arrivals hall (<see cref="AuhTerminalAScenarioSite"/>, seed 9304).
/// </summary>
internal abstract class ScenarioSite
{
    #region Identity

    /// <summary>The Ariva site code the scenario plays (DMO, AUH-TA).</summary>
    public string Code { get; protected init; }

    public string Name { get; protected init; }

    public uint DefaultSeed { get; protected init; }

    /// <summary>The demo date (the day's flights carry it in the feeds).</summary>
    public string Date { get; protected init; }

    /// <summary>The site airport's IATA code, as the emulated AODB publishes it.</summary>
    public string Airport { get; protected init; }

    /// <summary>The terminal the emulated AODB names on every leg.</summary>
    public string AodbTerminal { get; protected init; }

    /// <summary>The other airports of the emulated AODB's legs (picked per flight code).</summary>
    public IReadOnlyList<string> AodbAirports { get; protected init; }

    #endregion

    #region Queues and lanes

    public IReadOnlyList<QueueDef> Queues
    {
        get => _queues;
        protected init
        {
            _queues = value;
            QueueIndex = value.ToFrozenDictionary(q => q.Id, q => q.Index, StringComparer.Ordinal);
        }
    }

    private readonly IReadOnlyList<QueueDef> _queues;

    public FrozenDictionary<string, int> QueueIndex { get; private init; }

    public int NQ => Queues.Count;

    /// <summary>The index of a queue (throws for a queue the site does not have).</summary>
    public int Q(string id) => QueueIndex[id];

    public FrozenDictionary<string, AreaDef> Areas { get; protected init; }

    /// <summary>The wait target per staffing area that recommendations aim for, in minutes.</summary>
    public FrozenDictionary<string, double> RecTarget { get; protected init; }

    /// <summary>The arrival lanes, in the order of <see cref="ArrivalQueues"/> and <see cref="SplitArrivals"/> (e-gates last).</summary>
    public IReadOnlyList<string> Lanes { get; protected init; }

    /// <summary>The arrival lanes' queues, in lane order.</summary>
    public int[] ArrivalQueues { get; protected init; }

    /// <summary>The generic passenger mix of the day-ahead forecast.</summary>
    public PaxMix BaseMix { get; protected init; }

    /// <summary>The mean share of e-gate attempts rejected (and sent to the manual lanes).</summary>
    public double EgateReject { get; protected init; }

    /// <summary>The minutes from on-block to the hall that the expected and day-ahead runs assume.</summary>
    public int StandardWalk { get; protected init; }

    /// <summary>Whether the site has departures (departure immigration, check-in and security).</summary>
    public bool HasDepartures { get; protected init; }

    /// <summary>Lane shares of departures after security (transfers excluded); null for a site without departures.</summary>
    public FrozenDictionary<string, double> EgShareDep { get; protected init; }

    /// <summary>Splits a flight's arriving passengers between the arrival lanes, in <see cref="Lanes"/> order.</summary>
    public abstract double[] SplitArrivals(double pax, PaxMix mix);

    /// <summary>The fewest servers an immigration lane is staffed with (the roster's and the recommendations' floor).</summary>
    public virtual int MinimumServers(QueueDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return def.Lane == "VIS" ? 2 : 1;
    }

    #endregion

    #region Sensors

    /// <summary>The site's sensors and what each reports.</summary>
    public IReadOnlyList<SensorDef> Sensors
    {
        get => _sensors;
        protected init
        {
            _sensors = value;
            _sensorIndex = value.ToFrozenDictionary(s => s.Id, StringComparer.Ordinal);
        }
    }

    private readonly IReadOnlyList<SensorDef> _sensors;
    private readonly FrozenDictionary<string, SensorDef> _sensorIndex;

    /// <summary>The site's sensor with this id, or null.</summary>
    public SensorDef Sensor(string id) => id is not null && _sensorIndex.TryGetValue(id, out var sensor) ? sensor : null;

    /// <summary>The overflow bands and the queue each feeds.</summary>
    public FrozenDictionary<string, string> Bands { get; protected init; }

    /// <summary>The demo day's sensor outages.</summary>
    public IReadOnlyList<Outage> Outages { get; protected init; }

    public IReadOnlyList<PastOutage> OutageHistory { get; protected init; }

    #endregion

    #region The scripted day

    public IReadOnlyList<RosterLine> ScriptRoster { get; protected init; }

    /// <summary>Servers (e-gates) out of service on the demo day.</summary>
    public IReadOnlyList<OutOfService> OutOfService { get; protected init; }

    /// <summary>Counter allocations already in place on the morning of the demo day.</summary>
    public IReadOnlyList<CounterAllocation> SeedAllocations { get; protected init; }

    /// <summary>The seeded alert rules that reproduce the site's scripted evening.</summary>
    public IReadOnlyList<AlertRule> SeedRules { get; protected init; }

    /// <summary>Snake capacity per queue (people) when no floor plan gives one.</summary>
    public FrozenDictionary<string, double> DefaultCaps { get; protected init; }

    /// <summary>The day's flights from a seed.</summary>
    public abstract ScenarioSchedule BuildSchedule(uint seed);

    #endregion
}

/// <summary>
/// The fictional reference airport, Demo International Airport (DMO): the prototype's seeded day (ARV-027), with every
/// value from <see cref="ScenarioModel"/> and the reference's own lane split, so seed 9303 gives the same outputs bit for
/// bit as before the engine took the site as a parameter.
/// </summary>
internal sealed class DmoScenarioSite : ScenarioSite
{
    public DmoScenarioSite()
    {
        Code = ScenarioModel.SiteCode;
        Name = "Demo International Airport";
        DefaultSeed = ScenarioModel.DefaultSeed;
        Date = ScenarioModel.Date;
        Airport = "DMO";
        AodbTerminal = "T1";
        AodbAirports = ["DOH", "DXB", "AMM", "CAI", "IST", "LHR", "BEY", "KWI", "JED", "BAH", "MCT", "CDG"];
        Queues = ScenarioModel.Queues;
        Areas = ScenarioModel.Areas;
        RecTarget = ScenarioModel.RecTarget;
        Lanes = ScenarioModel.Lanes;
        ArrivalQueues = ScenarioModel.ArrivalQueues;
        BaseMix = ScenarioModel.BaseMix;
        EgateReject = ScenarioModel.EgateReject;
        StandardWalk = 11;
        HasDepartures = true;
        EgShareDep = ScenarioModel.EgShareDep;
        Sensors = ScenarioModel.Sensors;
        Bands = ScenarioModel.Bands;
        Outages = ScenarioModel.Outages;
        OutageHistory = ScenarioModel.OutageHistory;
        ScriptRoster = ScenarioModel.ScriptRoster;
        OutOfService = [new(ScenarioModel.EgateOutOfService.Gate, ScenarioModel.EgateOutOfService.From, ScenarioModel.EgateOutOfService.To, ScenarioModel.EgateOutOfService.Note)];
        SeedAllocations = ScenarioModel.SeedAllocations;
        SeedRules = ScenarioDay.SeedRules;
        DefaultCaps = ScenarioDay.DefaultCaps;
    }

    /// <summary>The reference's split: crew, citizens and residents not using e-gates, visitors, then 40 % of citizens and residents at the e-gates.</summary>
    public override double[] SplitArrivals(double pax, PaxMix mix)
    {
        const double e = ScenarioModel.EgateShare;
        return [pax * mix.Crw, pax * mix.Cit * (1 - e), pax * mix.Res * (1 - e), pax * mix.Vis, pax * (mix.Cit + mix.Res) * e];
    }

    public override ScenarioSchedule BuildSchedule(uint seed) => ScenarioSchedule.Build(seed);
}

/// <summary>The scenario sites the simulator plays, by Ariva site code.</summary>
internal static class ScenarioSites
{
    public static readonly ScenarioSite Dmo = new DmoScenarioSite();

    public static readonly ScenarioSite AuhTerminalA = new AuhTerminalAScenarioSite();

    /// <summary>Every scenario site, the reference first.</summary>
    public static readonly IReadOnlyList<ScenarioSite> All = [Dmo, AuhTerminalA];

    /// <summary>The scenario site of an Ariva site code (ordinal, exact), or null.</summary>
    public static ScenarioSite Find(string code) => All.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.Ordinal));

    /// <summary>The site codes, for messages that never quote a caller's value.</summary>
    public static string Codes => string.Join(" or ", All.Select(s => s.Code));
}
