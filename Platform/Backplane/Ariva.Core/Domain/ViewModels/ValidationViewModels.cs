namespace Ariva.Core.Domain.ViewModels;

/// <summary>A campaign's targets (ARV-104a); <c>Placeholder</c> when a target took its default because TC-04 was not answered for it.</summary>
public sealed record ValidationTargetsViewModel(int BinsPerLine, int TracerRuns, bool Placeholder);

/// <summary>A queue zone in a campaign's scope, with the tracer runs recorded in it (any observer; ARV-104b).</summary>
public sealed record ValidationZoneViewModel(Guid Id, string Name, int TracerRuns);

/// <summary>
/// A border desk in a campaign's scope (ARV-104b): its checkpoint's and its own code, and the minutes with at least one
/// observation (any observer). Border data: only in a border caller's view.
/// </summary>
public sealed record ValidationDeskViewModel(Guid Id, string Checkpoint, string Code, int MinutesObserved);

/// <summary>A line in a campaign's scope, with the 15-minute bins that have at least one count (any observer).</summary>
public sealed record ValidationLineViewModel(Guid Id, string Name, string Role, string QueueZone, int BinsCaptured);

/// <summary>A campaign in a list (ARV-104a).</summary>
public sealed record ValidationCampaignSummaryViewModel(
    Guid Id,
    string SiteCode,
    string Name,
    string Status,
    int ProfileVersion,
    IReadOnlyList<string> Days,
    int Zones,
    int Lines,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? ClosedUtc);

/// <summary>
/// A campaign with its scope and progress (ARV-104a): the profile version and its geometry hash and whether it is still the
/// published one (<c>ProfileStatus</c> Published or Retired), the site's time zone for the local days, the targets, and who
/// created, started and closed it as Ariva user ids. Since ARV-104b the zones carry their tracer runs and the border desks in
/// scope are listed with their observed minutes, for a caller who sees border desks only (<c>DesksIncluded</c>; otherwise the
/// list is empty).
/// </summary>
public sealed record ValidationCampaignViewModel(
    Guid Id,
    string SiteCode,
    string Name,
    string Status,
    Guid ProfileId,
    int ProfileVersion,
    string GeometryHash,
    string ProfileStatus,
    DateTime? ProfileRetiredUtc,
    string TimeZoneId,
    IReadOnlyList<string> Days,
    ValidationTargetsViewModel Targets,
    IReadOnlyList<ValidationZoneViewModel> Zones,
    IReadOnlyList<ValidationLineViewModel> Lines,
    bool DesksIncluded,
    IReadOnlyList<ValidationDeskViewModel> Desks,
    Guid CreatedById,
    DateTime CreatedUtc,
    Guid? StartedById,
    DateTime? StartedUtc,
    Guid? ClosedById,
    DateTime? ClosedUtc);

/// <summary>
/// A manual count revision (ARV-104a). <c>Current</c> is false once a later revision corrects it. The observer is an Ariva
/// user id, never a name.
/// </summary>
public sealed record ManualCountViewModel(
    Guid Id,
    Guid CampaignId,
    Guid LineId,
    string LineName,
    DateTime BinStartUtc,
    Guid ObserverId,
    int Revision,
    bool Current,
    int CrossingsIn,
    int CrossingsOut,
    string Reason,
    Guid? CorrectsId,
    DateTime RecordedUtc);

/// <summary>A line an observer may count.</summary>
public sealed record CaptureLineViewModel(Guid Id, string Name, string Role, string QueueZone);

/// <summary>A queue zone tracers may run in (ARV-104b).</summary>
public sealed record CaptureZoneViewModel(Guid Id, string Name);

/// <summary>A border desk an observer logs minute by minute (ARV-104b).</summary>
public sealed record CaptureDeskViewModel(Guid Id, string Checkpoint, string Code);

/// <summary>
/// A running campaign as an observer sees it (ARV-104a): what to count and when, nothing of anyone else's counts, targets or
/// the profile. Bins are <c>BinMinutes</c> long, on the quarter hour; the days are local dates in <c>TimeZoneId</c>. Since
/// ARV-104b also the queue zones tracers run in, the border desks to log (none when the campaign has none) and the largest
/// clock offset a tracer batch may have. Desks are border data: <c>DesksIncluded</c> is false, and the list empty, for an
/// account that holds an airport role without <c>BorderDesks.View</c>, even with the Validation observer role.
/// </summary>
public sealed record CaptureCampaignViewModel(
    Guid Id,
    string SiteCode,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Days,
    int BinMinutes,
    IReadOnlyList<CaptureLineViewModel> Lines,
    IReadOnlyList<CaptureZoneViewModel> Zones,
    bool DesksIncluded,
    IReadOnlyList<CaptureDeskViewModel> Desks,
    int MaxClockOffsetSeconds);

/// <summary>A recorded count, and whether it is the stored answer to a resent request with the same Idempotency-Key.</summary>
public sealed record CapturedCountViewModel(ManualCountViewModel Count, bool Replayed);

/// <summary>
/// A tracer run (ARV-104b): the zone, the tracer's label (never a name), the observer as an Ariva user id, the times on the
/// server's clock (<c>JoinedUtc</c>, <c>ExitedUtc</c>, their difference <c>WaitSeconds</c>) and as the device read them with
/// the batch's measured offset (device minus server, milliseconds), and the abandoned flag.
/// </summary>
public sealed record TracerRunViewModel(
    Guid Id,
    Guid CampaignId,
    Guid BatchId,
    Guid ZoneId,
    string ZoneName,
    string TracerCode,
    Guid ObserverId,
    DateTime JoinedUtc,
    DateTime ExitedUtc,
    double WaitSeconds,
    bool Abandoned,
    DateTime JoinedRawUtc,
    DateTime ExitedRawUtc,
    int ClockOffsetMs,
    DateTime RecordedUtc);

/// <summary>A recorded tracer batch with its measured clock offset and its runs (ARV-104b).</summary>
public sealed record TracerBatchViewModel(
    Guid Id,
    Guid CampaignId,
    Guid ObserverId,
    DateTime DeviceClockUtc,
    DateTime ReceivedUtc,
    int ClockOffsetMs,
    IReadOnlyList<TracerRunViewModel> Runs);

/// <summary>A tracer batch, and whether it is the stored answer to a resent batch with the same Idempotency-Key.</summary>
public sealed record CapturedTracerBatchViewModel(TracerBatchViewModel Batch, bool Replayed);

/// <summary>
/// A desk state for one minute (ARV-104b). <c>Current</c> is false once a later revision corrects it. The observer is an
/// Ariva user id, never a name. Border data.
/// </summary>
public sealed record DeskObservationViewModel(
    Guid Id,
    Guid CampaignId,
    Guid? BatchId,
    Guid DeskId,
    string Checkpoint,
    string DeskCode,
    DateTime MinuteUtc,
    Guid ObserverId,
    int Revision,
    bool Current,
    string State,
    string Reason,
    Guid? CorrectsId,
    DateTime RecordedUtc);

/// <summary>A recorded desk batch: the bin and its observations (ARV-104b).</summary>
public sealed record DeskObservationBatchViewModel(
    Guid Id,
    Guid CampaignId,
    Guid ObserverId,
    DateTime BinStartUtc,
    DateTime ReceivedUtc,
    IReadOnlyList<DeskObservationViewModel> Observations);

/// <summary>A desk batch, and whether it is the stored answer to a resent batch with the same Idempotency-Key.</summary>
public sealed record CapturedDeskBatchViewModel(DeskObservationBatchViewModel Batch, bool Replayed);

/// <summary>A desk state correction, and whether it is the stored answer to a resent request with the same Idempotency-Key.</summary>
public sealed record CapturedDeskObservationViewModel(DeskObservationViewModel Observation, bool Replayed);

/// <summary>
/// A page of a campaign's desk observations for a manager (ARV-104b). Border data: <c>DesksIncluded</c> is false, and the page
/// empty, for a caller who does not see border desks (a terminal duty manager), whatever is stored.
/// </summary>
public sealed record DeskObservationPageViewModel(
    bool DesksIncluded,
    IReadOnlyList<DeskObservationViewModel> Data,
    int TotalCount,
    int PageIndex,
    int PageSize);
