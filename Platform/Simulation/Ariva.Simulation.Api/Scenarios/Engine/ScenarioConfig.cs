namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>An ad-hoc flight added to the day: direction "arr" or "dep", clock minute, seats and booked load.</summary>
public sealed record AdhocFlight(string Id, string Code, string Direction, double? Time, double? Seats, double? Load, string Island, string Handler, PaxMix Mix);

/// <summary>A roster override: <see cref="Count"/> servers on <see cref="Queue"/> in [From, To).</summary>
public sealed record RosterOverride(string Queue, int From, int To, int Count);

/// <summary>An accepted staffing recommendation: per queue, one server count per 15-minute block from <see cref="From"/>.</summary>
public sealed record AcceptedPlan(string Area, int From, int Until, IReadOnlyDictionary<string, IReadOnlyList<int>> Counts);

/// <summary>
/// An alert rule (R-001 to R-005 are the seeded ones): a metric compared with a threshold for every target in scope,
/// raised after <see cref="Sustain"/> minutes and cleared after <see cref="ClearAfter"/> (or below <see cref="ClearBelow"/>).
/// </summary>
public sealed record AlertRule(
    string Id,
    string Name,
    IReadOnlyList<string> Scope,
    string Metric,
    string Op,
    double? Threshold,
    double? MinQueue,
    double? ClearBelow,
    int Sustain,
    int ClearAfter,
    string Severity,
    string Owner,
    int EscalateAfter,
    string EscalateTo,
    string Email,
    bool Enabled = true);

/// <summary>
/// What one run of the day takes. Everything is optional: the reference day is the default seed, the morning's
/// counter allocations, the seeded rules and the default snake capacities.
/// </summary>
public sealed record ScenarioConfig
{
    public uint Seed { get; init; } = ScenarioModel.DefaultSeed;
    public IReadOnlyList<AdhocFlight> Flights { get; init; } = [];
    public IReadOnlyList<AcceptedPlan> Accepted { get; init; } = [];
    public IReadOnlyList<RosterOverride> Overrides { get; init; } = [];
    public IReadOnlyList<CounterAllocation> Allocations { get; init; } = [];

    /// <summary>Snake capacity per queue (people); null for the defaults.</summary>
    public IReadOnlyDictionary<string, double> Caps { get; init; }

    /// <summary>Alert rules; null for the seeded rules.</summary>
    public IReadOnlyList<AlertRule> Rules { get; init; }

    /// <summary>Skip rule evaluation (no alerts), for a quick run.</summary>
    public bool Lite { get; init; }

    /// <summary>The reference day: default seed, the morning's allocations, seeded rules and default capacities.</summary>
    public static ScenarioConfig Reference(uint seed = ScenarioModel.DefaultSeed) => new()
    {
        Seed = seed,
        Allocations = ScenarioModel.SeedAllocations
    };

    /// <summary>The arrivals Visitors snake capacity in <see cref="OverflowEvening"/>: 60 people instead of 190.</summary>
    public const double OverflowEveningVisitorsCapacity = 60;

    /// <summary>
    /// A scenario case for overflow bands (ARV-115): the reference day (seed 9303, its scripted events unchanged) with the
    /// arrivals Visitors snake (A-VIS) holding 60 people instead of 190, so its queue spills into the A-OV band in both
    /// evening waves (about 18:02 to 18:32 and 19:32 to 19:51), and the band's lead sensor (S-25) reports people there.
    /// With the default capacities the reference day never fills a band.
    /// </summary>
    public static ScenarioConfig OverflowEvening(uint seed = ScenarioModel.DefaultSeed) => Reference(seed) with
    {
        Caps = new Dictionary<string, double>(ScenarioDay.DefaultCaps, StringComparer.Ordinal) { ["A-VIS"] = OverflowEveningVisitorsCapacity }
    };
}
