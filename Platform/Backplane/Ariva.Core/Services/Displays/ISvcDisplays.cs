using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>One line of a display as a request: the queue zone and its label per language.</summary>
public sealed record DisplayEntryRequest([Required, MaxLength(200)] string Zone, [Required] IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// A passenger display as a request (ARV-058). The site and code are given on creation and never change; the credential
/// is the server's, returned once on creation and on a new credential. The layout (ARV-140) is optional: a request without
/// it keeps the full queue view, with the default share and instructions in the second region.
/// <c>AdvertisingPauseMinutes</c> (ARV-143b) is the wait at which advertising gives way to instructions, or none when left out.
/// </summary>
public sealed record DisplayRequest(
    [MaxLength(17)] string SiteCode,
    [MaxLength(16)] string Code,
    [Required, MaxLength(200)] string Name,
    [MaxLength(200)] string Location,
    [Required, MaxLength(16)] string Orientation,
    [Required, MaxLength(4)] IReadOnlyList<string> Languages,
    [Range(1, 30)] int BandMinutes,
    [Range(0, 30)] double HysteresisMinutes,
    [Range(60, 1800)] int StaleSeconds,
    [Required, MaxLength(12)] IReadOnlyList<DisplayEntryRequest> Entries,
    [Required] IReadOnlyDictionary<string, string> Fallback,
    bool Enabled = true,
    [MaxLength(16)] string Layout = "Full",
    [Range(50, 75)] int QueueShare = 60,
    [MaxLength(16)] string SecondRegion = "Instructions",
    [Range(1, 240)] int? AdvertisingPauseMinutes = null);

public sealed record DisplayEntryViewModel(string Zone, IReadOnlyDictionary<string, string> Labels);

/// <summary>A display's settings as the API shows them: the credential's prefix only, never the credential.</summary>
public sealed record DisplayViewModel(
    Guid Id,
    string SiteCode,
    string Code,
    string Name,
    string Location,
    string Orientation,
    IReadOnlyList<string> Languages,
    int BandMinutes,
    double HysteresisMinutes,
    int StaleSeconds,
    IReadOnlyList<DisplayEntryViewModel> Entries,
    IReadOnlyDictionary<string, string> Fallback,
    bool Enabled,
    string Layout,
    int QueueShare,
    string SecondRegion,
    int? AdvertisingPauseMinutes,
    string CredentialPrefix,
    DateTime? CredentialIssuedOn,
    DateTime? CreatedOn,
    DateTime? ModifiedOn);

/// <summary>A display with the player's credential, shown this once (creation or a new credential).</summary>
public sealed record DisplayIssuedViewModel(DisplayViewModel Display, string Credential);

/// <summary>The answers of the display service that are not plain validation.</summary>
public static class DisplayErrors
{
    public const string UnknownZones = "Every entry must be a queue zone of the site's published zone profile.";
    public const string DuplicateCode = "A display with this code exists.";
    public const string StaysInSite = "A display stays in its site and keeps its code.";
    public const string InvalidCode = "A display code is 1 to 16 upper case letters or digits, with single hyphens inside.";
    public const string TooMany = "A site has at most 200 displays.";

    /// <summary>The site's advertising policy (ARV-145) does not allow advertising on this display: off for the site, or one of its zones excluded.</summary>
    public const string AdvertisingOff = "Advertising is off for this display's site or for one of its zones; the second region shows instructions.";

    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { DuplicateCode };
}

/// <summary>
/// Passenger displays (ARV-058), within the caller's sites (another site's display answers like one that does not
/// exist); every change audited; creating one and issuing a new credential are critical actions.
/// </summary>
public interface ISvcDisplays : ISvcScoped
{
    Task<Result<IReadOnlyList<DisplayViewModel>>> SearchAsync(string siteCode, CancellationToken ct = default);
    Task<Result<DisplayViewModel>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<DisplayIssuedViewModel>> CreateAsync(DisplayRequest request, CancellationToken ct = default);
    Task<Result<DisplayViewModel>> UpdateAsync(Guid id, DisplayRequest request, CancellationToken ct = default);
    Task<Result<DisplayIssuedViewModel>> NewCredentialAsync(Guid id, CancellationToken ct = default);
    Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// One entry of a board as the player gets it: its labels, and the nowcast of its queue zone with its age in seconds by
/// the server's clock (so a player's own clock cannot make old data look fresh). Null nowcast: no data or no service.
/// </summary>
public sealed record DisplayBoardEntryViewModel(
    IReadOnlyDictionary<string, string> Labels,
    double? NowcastMinutes,
    bool Degraded,
    string NoService,
    double? AgeSeconds);

/// <summary>
/// One item a display plays in its second region (ARV-143b): an advertisement (<c>Kind</c> Media, fetched by
/// <c>MediaId</c> with the display's credential from <c>api/v1/display/advertising/{id}</c>, an image or a video by its
/// <c>ContentType</c>) or an instruction slide (<c>Kind</c> Slide, its words in the display's languages). <c>Seconds</c> is
/// its slot: the player ends it then, whatever the media holds.
/// </summary>
public sealed record DisplayBoardAdvertisingItemViewModel(string Kind, Guid? MediaId, string ContentType, int Seconds, DisplayBoardSlideViewModel Slide);

/// <summary>
/// The playlist a display plays now (ARV-143b), only while the site's advertising policy allows it for the display (read on
/// every board): its playable items, one cycle's playing <c>Order</c> (indexes into <c>Items</c>, each as often as its weight),
/// the instant the plan next changes (<c>UntilUtc</c>: a player without a newer board stops it then), and the wait at which
/// the display shows instructions instead (<c>PauseMinutes</c>, held with the display's hysteresis; null for none).
/// </summary>
public sealed record DisplayBoardAdvertisingViewModel(
    Guid PlaylistId,
    DateTime UntilUtc,
    int? PauseMinutes,
    IReadOnlyList<DisplayBoardAdvertisingItemViewModel> Items,
    IReadOnlyList<int> Order);

/// <summary>
/// What a player shows (ARV-058): the display's settings and its entries' latest nowcasts. <paramref name="Illustrative"/>:
/// the display's site is an illustrative demo site (ARV-139a), so the player shows the "Illustrative, not surveyed" banner.
/// The layout (ARV-140) comes with every answer, so a change reaches a running player at its next poll, without a restart.
/// So do the instruction slides that apply to the display (ARV-141): the pinned one, shown under the waits, and the ones the
/// second region rotates. And the playlist the second region plays (ARV-143b), or null: instructions.
/// <paramref name="ServerUtc"/> is this server's clock once the board is composed (ARV-197), which the player counts on from.
/// </summary>
public sealed record DisplayBoardViewModel(
    string Code,
    string Name,
    string Orientation,
    IReadOnlyList<string> Languages,
    int BandMinutes,
    double HysteresisMinutes,
    int StaleSeconds,
    IReadOnlyDictionary<string, string> Fallback,
    IReadOnlyList<DisplayBoardEntryViewModel> Entries,
    DateTime ServerUtc,
    bool Illustrative = false,
    string Layout = "Full",
    int QueueShare = 60,
    string SecondRegion = "Instructions",
    DisplayBoardSlideViewModel PinnedSlide = null,
    IReadOnlyList<DisplayBoardSlideViewModel> Slides = null,
    DisplayBoardAdvertisingViewModel Advertising = null);

/// <summary>A display player that presented its display's code and credential (ARV-058).</summary>
public sealed record DisplayPlayer(Guid Id, string Code, string SiteCode, string CredentialPrefix);

/// <summary>The board of a display for its player, authenticated by the display's credential only (ARV-058).</summary>
public interface ISvcDisplayBoard
{
    /// <summary>The live, enabled display with this code whose credential matches, or null (the same for every miss).</summary>
    Task<DisplayPlayer> FindAsync(string code, string credential, CancellationToken ct = default);

    /// <summary>
    /// The board of an authenticated display, or not found when it was deleted, disabled or given a new credential since
    /// the request was authenticated (the credential's prefix must still be the display's).
    /// </summary>
    Task<Result<DisplayBoardViewModel>> GetAsync(Guid displayId, string credentialPrefix, CancellationToken ct = default);
}
