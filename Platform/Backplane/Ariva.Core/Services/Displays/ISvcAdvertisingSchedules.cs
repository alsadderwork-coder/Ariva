using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>
/// A schedule as a request (ARV-143a): the playlist it plays, the displays it applies to (<c>Scope</c> Site, Zone or Display,
/// with the queue zone names or display codes it names), the days of the week (English names) and the site's local window
/// (<c>Opens</c> and <c>Closes</c> as "HH:MM"; a closing time at or before the opening time is on the next day).
/// </summary>
public sealed record AdvertisingScheduleRequest(
    Guid PlaylistId,
    [Required, MaxLength(16)] string Scope,
    [MaxLength(20)] IReadOnlyList<string> Targets,
    [Required, MaxLength(7)] IReadOnlyList<string> Days,
    [Required, MaxLength(5)] string Opens,
    [Required, MaxLength(5)] string Closes);

/// <summary>A schedule as the API shows it, with its playlist's name and whether that playlist is active (a retired one stops it applying).</summary>
public sealed record AdvertisingScheduleViewModel(
    Guid Id,
    string SiteCode,
    Guid PlaylistId,
    string PlaylistName,
    bool PlaylistActive,
    string Scope,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Days,
    string Opens,
    string Closes,
    string Status,
    DateTime? CreatedOn,
    DateTime? ModifiedOn);

/// <summary>
/// A stretch of a display's week with one outcome: from one instant to the next (UTC, and the site's local wall-clock time
/// as "yyyy-MM-ddTHH:mm"), the schedule and playlist that apply, or none (the display shows its instructions).
/// </summary>
public sealed record AdvertisingTimelineSegmentViewModel(
    DateTime FromUtc,
    DateTime ToUtc,
    string FromLocal,
    string ToLocal,
    Guid? ScheduleId,
    Guid? PlaylistId,
    string PlaylistName);

/// <summary>
/// What a display of the site plays over the week from the site's local midnight today (<c>TimeZone</c> is the site's).
/// <c>SecondRegion</c> is the display's (<c>Advertising</c> or <c>Instructions</c>): a display set to instructions shows them
/// whatever the plan says, so the screen tells the planner.
/// </summary>
public sealed record AdvertisingTimelineViewModel(
    string SiteCode,
    string DisplayCode,
    string TimeZone,
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<AdvertisingTimelineSegmentViewModel> Segments,
    string SecondRegion);

/// <summary>
/// The advertising schedules of the sites (ARV-143a), within the caller's sites (another site, and another site's schedule
/// named by id under this site's route, answer like one that does not exist). A schedule's zones are queue zones of the
/// site's published profile and its displays live displays of the site, as for instruction slides. The timeline shows a
/// display's week as the board will resolve it (ARV-143b). Every change is audited; a schedule is retired, never deleted.
/// </summary>
public interface ISvcAdvertisingSchedules : ISvcScoped
{
    Task<Result<IReadOnlyList<AdvertisingScheduleViewModel>>> ListAsync(string siteCode, CancellationToken ct = default);
    Task<Result<AdvertisingScheduleViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default);
    Task<Result<AdvertisingScheduleViewModel>> CreateAsync(string siteCode, AdvertisingScheduleRequest request, CancellationToken ct = default);
    Task<Result<AdvertisingScheduleViewModel>> UpdateAsync(string siteCode, Guid id, AdvertisingScheduleRequest request, CancellationToken ct = default);
    Task<Result<AdvertisingScheduleViewModel>> RetireAsync(string siteCode, Guid id, CancellationToken ct = default);
    Task<Result<AdvertisingScheduleViewModel>> RestoreAsync(string siteCode, Guid id, CancellationToken ct = default);

    /// <summary>A display's week (7 days from the site's local midnight today); the display must be a live display of the site.</summary>
    Task<Result<AdvertisingTimelineViewModel>> TimelineAsync(string siteCode, string displayCode, CancellationToken ct = default);
}
