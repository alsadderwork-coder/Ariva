using Ariva.Core.Flights;

namespace Ariva.Core.Services.Flights;

/// <summary>
/// Where every flight feed's data enters the model (ARV-041): the Integration API (ARV-043), AIDX (ARV-044), ACRIS
/// (ARV-045) and SSIM (ARV-046) adapters parse their format and hand the result here, for one site and one named feed.
/// Each item is checked (<see cref="FlightRules"/>) and applied on its own; a bad item never stops the others. Messages
/// are applied by their own time (<paramref name="sourceUtc"/>, the feed's message timestamp; Ariva's clock when the
/// feed gives none), so late and out-of-order delivery is safe. Every call counts as a message from the feed for its
/// freshness. System calls (no user): the caller has already authenticated the feed and bound it to the site.
/// </summary>
public interface ISvcFlightIntake : ISvcScoped
{
    /// <summary>Creates or updates flight legs. Unknown sites are refused as a whole.</summary>
    Task<IReadOnlyList<FlightItemResult>> ApplyLegsAsync(string siteCode, string feed, IReadOnlyList<FlightLegData> legs, DateTime? sourceUtc,
        CancellationToken ct = default);

    /// <summary>Records milestones of known legs; an event for a key the site does not know is refused (a feed must send the leg first).</summary>
    Task<IReadOnlyList<FlightItemResult>> ApplyEventsAsync(string siteCode, string feed, IReadOnlyList<FlightEventData> events, DateTime? sourceUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Sets the check-in counters of known departing legs at check-in checkpoints of the site. Counter codes that no AODB
    /// desk code mapping of the checkpoint resolves are kept apart and reported as a warning, never guessed.
    /// </summary>
    Task<IReadOnlyList<FlightItemResult>> ApplyAllocationsAsync(string siteCode, string feed, IReadOnlyList<CounterAllocationData> allocations, DateTime? sourceUtc,
        CancellationToken ct = default);
}

/// <summary>
/// The stale-feed alarm (ARV-041, runbook 4.3): every sweep judges each feed of each site with
/// <see cref="FeedFreshnessRule"/> against the flights due at the site, records the state and reports changes (a
/// warning in the log and the <c>Ariva.Flights</c> metrics). System calls (no user).
/// </summary>
public interface ISvcFeedFreshness : ISvcScoped
{
    Task<FeedFreshnessSweep> SweepAsync(CancellationToken ct = default);
}

/// <summary>What a sweep did: whether it ran (one replica at a time), the feeds judged, stale now, and changes of state.</summary>
public sealed record FeedFreshnessSweep(bool Ran, int Feeds, int Stale, int Changed);
