using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>One line of a display as a request: the queue zone and its label per language.</summary>
public sealed record DisplayEntryRequest([Required, MaxLength(200)] string Zone, [Required] IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// A passenger display as a request (ARV-058). The site and code are given on creation and never change; the credential
/// is the server's, returned once on creation and on a new credential.
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
    bool Enabled = true);

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

/// <summary>What a player shows (ARV-058): the display's settings and its entries' latest nowcasts.</summary>
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
    DateTime ServerUtc);

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
