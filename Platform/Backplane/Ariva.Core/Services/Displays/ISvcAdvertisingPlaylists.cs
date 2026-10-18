using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>
/// One item of a playlist as a request (ARV-143a): <c>Kind</c> is <c>Media</c> (an advertisement of the site's library) or
/// <c>Slide</c> (an instruction slide of the site), <c>Id</c> the item's id, <c>Seconds</c> its time on screen (5 to 60;
/// ignored for a video, whose slot is its own length) and <c>Weight</c> its share of airtime (1 to 10, 1 when left out).
/// </summary>
public sealed record AdvertisingPlaylistItemRequest([Required, MaxLength(8)] string Kind, Guid Id, int? Seconds, int? Weight);

/// <summary>A playlist as a request: its name and its items in playing order (the whole list each time).</summary>
public sealed record AdvertisingPlaylistRequest(
    [Required, MaxLength(120)] string Name,
    [Required, MaxLength(40)] IReadOnlyList<AdvertisingPlaylistItemRequest> Items);

/// <summary>
/// One item of a playlist as the API shows it: what it is (<c>Label</c>, the advertiser and campaign of an advertisement or the
/// title of a slide, as text), its slot and weight, and whether it would play on the site's local day now (<c>Playable</c>: an
/// approved advertisement within its dates, an active slide). <c>MediaStatus</c> is the advertisement's state (null for a slide).
/// </summary>
public sealed record AdvertisingPlaylistItemViewModel(
    string Kind,
    Guid Id,
    int Seconds,
    int Weight,
    string Label,
    bool Playable,
    string MediaStatus,
    string ContentType);

/// <summary>A playlist as the API shows it, with its items in playing order and the length of one pass (<c>TotalSeconds</c>).</summary>
public sealed record AdvertisingPlaylistViewModel(
    Guid Id,
    string SiteCode,
    string Name,
    string Status,
    IReadOnlyList<AdvertisingPlaylistItemViewModel> Items,
    int TotalSeconds,
    DateTime? CreatedOn,
    DateTime? ModifiedOn);

/// <summary>The answers of the playlists and schedules that are not plain validation, with their status codes.</summary>
public static class AdvertisingPlanErrors
{
    public const string Required = "A playlist is required: its name and its items.";

    public const string UnknownItems =
        "Every item is an advertisement of the site's library that is not retired, or an active instruction slide of the site, named once.";

    public const string UnknownKind = "An item is Media (an advertisement) or Slide (an instruction slide).";

    public const string ImageSeconds = "An image or a slide stays on screen 5 to 60 seconds.";

    /// <summary>409: the site has as many playlists as it may (retired ones included).</summary>
    public const string TooManyPlaylists = "The site has 100 playlists, retired ones included; this one is not added.";

    public const string ScheduleRequired = "A schedule is required: its playlist, displays, days and window.";

    /// <summary>A schedule names a playlist that is not an active playlist of the site (another site's answers the same).</summary>
    public const string UnknownPlaylist = "A schedule plays an active playlist of the site.";

    public const string UnknownScope = "A schedule applies to its Site, a Zone or a Display.";

    /// <summary>409: the site has as many schedules as it may (retired ones included).</summary>
    public const string TooManySchedules = "The site has 200 schedules, retired ones included; this one is not added.";

    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { TooManyPlaylists, TooManySchedules };
}

/// <summary>
/// The advertising playlists of the sites (ARV-143a), within the caller's sites: another site, and another site's playlist
/// named by id under this site's route, answer like one that does not exist (CWE-863, IDOR). The endpoints carry
/// <c>[AdvertisingAllowed]</c>; reading needs <c>Advertising.View</c> or <c>Advertising.Manage</c>, changing
/// <c>Advertising.Manage</c>. Every change is audited; a playlist is retired, never deleted.
/// </summary>
public interface ISvcAdvertisingPlaylists : ISvcScoped
{
    Task<Result<IReadOnlyList<AdvertisingPlaylistViewModel>>> ListAsync(string siteCode, CancellationToken ct = default);
    Task<Result<AdvertisingPlaylistViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default);
    Task<Result<AdvertisingPlaylistViewModel>> CreateAsync(string siteCode, AdvertisingPlaylistRequest request, CancellationToken ct = default);
    Task<Result<AdvertisingPlaylistViewModel>> UpdateAsync(string siteCode, Guid id, AdvertisingPlaylistRequest request, CancellationToken ct = default);
    Task<Result<AdvertisingPlaylistViewModel>> RetireAsync(string siteCode, Guid id, CancellationToken ct = default);
    Task<Result<AdvertisingPlaylistViewModel>> RestoreAsync(string siteCode, Guid id, CancellationToken ct = default);
}
