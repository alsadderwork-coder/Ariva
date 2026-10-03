using Ariva.Core.Flights;
using Ariva.Core.Domain.ViewModels;

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

    /// <summary>
    /// Creates legs from a schedule file, the fallback feed (ARV-046): a leg is created, or updated only when a schedule
    /// file set its schedule; a leg any live feed has reported is left as it is (unchanged), whatever the message times.
    /// </summary>
    Task<IReadOnlyList<FlightItemResult>> ApplyScheduleLegsAsync(string siteCode, string feed, IReadOnlyList<FlightLegData> legs, DateTime sourceUtc,
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
/// AIDX 22.1 inbound (ARV-044): a read message's legs, mapped for the site (<see cref="AidxMapping"/>; its airports
/// decide each leg's direction) and applied through <see cref="ISvcFlightIntake"/> with the message's TimeStamp as
/// the message time. One result per FlightLeg of the message, in order. System calls.
/// </summary>
public interface ISvcAidxIntake : ISvcScoped
{
    Task<IReadOnlyList<FlightItemResult>> ApplyAsync(string siteCode, string feed, AidxMessage message, CancellationToken ct = default);
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

/// <summary>
/// SSIM schedule files for a site (ARV-046, Ariva.Api.Main): preview what a file would do, then import exactly what was
/// previewed (the preview token binds the file's SHA-256, the site, the horizon, the window and the user). The schedule
/// is a fallback feed: it creates legs and updates only legs a schedule file set; a leg any live feed has reported is
/// never changed by it. Calls of an administrator within the site (the controller is site-scoped).
/// </summary>
public interface ISvcFlightSchedules : ISvcScoped
{
    /// <summary>Whether the site exists (checked before the upload is read, so an unknown site answers 404 like one the caller does not hold).</summary>
    Task<bool> SiteExistsAsync(string siteCode, CancellationToken ct = default);

    Task<Result<SsimPreviewViewModel>> PreviewAsync(string siteCode, Stream file, int horizonDays, CancellationToken ct = default);

    Task<Result<SsimImportViewModel>> ImportAsync(string siteCode, Stream file, int horizonDays, string previewToken, CancellationToken ct = default);
}

public static class FlightScheduleErrors
{
    public const string NotThePreview =
        "This is not what was previewed (the file, the site, the horizon or the day differs, or the preview is older than two hours); preview it again.";
}
