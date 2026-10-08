namespace Ariva.Core.Domain.ViewModels;

/// <summary>A campaign's targets (ARV-104a); <c>Placeholder</c> when a target took its default because TC-04 was not answered for it.</summary>
public sealed record ValidationTargetsViewModel(int BinsPerLine, int TracerRuns, bool Placeholder);

/// <summary>A queue zone in a campaign's scope.</summary>
public sealed record ValidationZoneViewModel(Guid Id, string Name);

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
/// created, started and closed it as Ariva user ids.
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

/// <summary>
/// A running campaign as an observer sees it (ARV-104a): what to count and when, nothing of anyone else's counts, targets or
/// the profile. Bins are <c>BinMinutes</c> long, on the quarter hour; the days are local dates in <c>TimeZoneId</c>.
/// </summary>
public sealed record CaptureCampaignViewModel(
    Guid Id,
    string SiteCode,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Days,
    int BinMinutes,
    IReadOnlyList<CaptureLineViewModel> Lines);

/// <summary>A recorded count, and whether it is the stored answer to a resent request with the same Idempotency-Key.</summary>
public sealed record CapturedCountViewModel(ManualCountViewModel Count, bool Replayed);
