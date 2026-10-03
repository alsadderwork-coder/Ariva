using Fluentx;

namespace Ariva.Core.Services.Border;

/// <summary>
/// One lane category of a border hall over the window (ARV-057): desks staffed now (open or paused, from AMAN's latest
/// session events), people processed, the mean service and cycle times weighted by transactions, and the highest P90
/// service time any desk reported. Aggregates only: no desk, officer or traveller identifier.
/// </summary>
public sealed record ImmigrationLaneViewModel(
    string Lane,
    int DesksOpen,
    int DesksPaused,
    int Transactions,
    double? MeanServiceSeconds,
    double? MaxP90ServiceSeconds,
    double? MeanCycleSeconds);

/// <summary>
/// One border desk over the window (ARV-057, border roles only): its Ariva code, the lane it last served, its latest AMAN
/// session state (Opened, Paused, Closed or Unknown), the people processed and its service times as interval aggregates.
/// </summary>
public sealed record ImmigrationDeskViewModel(
    string Desk,
    string Lane,
    string State,
    int Transactions,
    double? MeanServiceSeconds,
    double? P90ServiceSeconds,
    DateTime? LastIntervalUtc);

/// <summary>Rejects by AMAN's coarse category (the feed suppresses a category below 3 in an interval).</summary>
public sealed record ImmigrationRejectsViewModel(int DocumentRead, int BiometricCapture, int Eligibility, int ReferredToOfficer, int Technical, int Other);

/// <summary>
/// The e-gates of a hall over the window (ARV-057): gates configured and gates used, attempts, accepted and rejected,
/// rejects by category, utilisation (busy time of the used gates over the configured gates' time), and the extra load the
/// rejects put on manual desks: each rejected traveller takes a manual desk's mean service time, in desk-minutes, and
/// spread over the desks open now, the minutes it adds to a manual queue. Both are estimates.
/// </summary>
public sealed record ImmigrationEGatesViewModel(
    int GatesConfigured,
    int GatesUsed,
    int Attempts,
    int Accepted,
    int Rejected,
    double? RejectRate,
    double? Utilisation,
    ImmigrationRejectsViewModel Rejects,
    double ExtraManualDeskMinutes,
    double? ExtraManualWaitMinutes);

/// <summary>One e-gate over the window (ARV-057, border roles only).</summary>
public sealed record ImmigrationGateViewModel(string Gate, int Attempts, int Rejected, double? Utilisation, double? MeanCycleSeconds);

/// <summary>
/// A queue zone of the hall that is a lane's queue (the zone profile says which, ARV-057): the screen joins its live
/// snapshot on the live hub by <see cref="ZoneKey"/> for the lane's wait.
/// </summary>
public sealed record ImmigrationQueueViewModel(string ZoneKey, string Zone, string Lane);

/// <summary>A hall: arrivals (Immigration checkpoints) or departures (Emigration checkpoints).</summary>
public sealed record ImmigrationHallViewModel(
    string Kind,
    IReadOnlyList<ImmigrationQueueViewModel> Queues,
    IReadOnlyList<ImmigrationLaneViewModel> Lanes,
    ImmigrationEGatesViewModel EGates,
    IReadOnlyList<ImmigrationDeskViewModel> Desks,
    IReadOnlyList<ImmigrationGateViewModel> Gates);

/// <summary>
/// The immigration view of a site at <see cref="AsOfUtc"/> over the last <see cref="WindowMinutes"/> minutes of AMAN's
/// interval aggregates. <see cref="DesksIncluded"/> says whether per-desk and per-gate figures are in it (border roles,
/// <c>BorderDesks.View</c>); otherwise the halls hold lane and e-gate totals only.
/// </summary>
public sealed record ImmigrationViewModel(string SiteCode, DateTime AsOfUtc, int WindowMinutes, bool DesksIncluded, IReadOnlyList<ImmigrationHallViewModel> Halls);

/// <summary>The immigration screen's figures (ARV-057), limited to the caller's sites; desk-level data to border roles.</summary>
public interface ISvcImmigrationView : ISvcScoped
{
    /// <summary>The window of AMAN's interval aggregates the view sums.</summary>
    const int WindowMinutes = 15;

    /// <summary>At most this many desks or gates per hall.</summary>
    const int MaxRows = 500;

    Task<Result<ImmigrationViewModel>> GetAsync(string siteCode, CancellationToken ct = default);
}
