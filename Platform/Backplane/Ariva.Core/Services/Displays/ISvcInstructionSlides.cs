using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>A slide's words in one language as a request: a title and an optional body, plain text.</summary>
public sealed record InstructionTextRequest([Required, MaxLength(80)] string Title, [MaxLength(240)] string Body);

/// <summary>
/// An instruction slide as a request (ARV-141). The site is given on creation and never changes; the targets are queue
/// zone names (Zone) or display codes (Display), none for Site; the words are per language (en, ar, pt, sw).
/// </summary>
public sealed record InstructionSlideRequest(
    [MaxLength(17)] string SiteCode,
    [Required, MaxLength(16)] string Scope,
    [MaxLength(20)] IReadOnlyList<string> Targets,
    [Required, MaxLength(32)] string Icon,
    [Range(5, 60)] int DurationSeconds,
    bool Pinned,
    [Required, MaxLength(4)] IReadOnlyDictionary<string, InstructionTextRequest> Texts);

/// <summary>A site's slides in their new order: every slide of the site, active or retired, each once.</summary>
public sealed record InstructionSlideOrderRequest([Required, MaxLength(100)] IReadOnlyList<Guid> Ids);

public sealed record InstructionTextViewModel(string Title, string Body);

public sealed record InstructionSlideViewModel(
    Guid Id,
    string SiteCode,
    int Position,
    string Scope,
    IReadOnlyList<string> Targets,
    string Icon,
    int DurationSeconds,
    bool Pinned,
    string Status,
    IReadOnlyDictionary<string, InstructionTextViewModel> Texts,
    DateTime? CreatedOn,
    DateTime? ModifiedOn);

/// <summary>
/// A slide as a display's player gets it on its board (ARV-141): the icon's name, how long it stays, and its words in the
/// display's languages only, in the display's order. No id, site or target goes out.
/// </summary>
public sealed record DisplayBoardSlideViewModel(int Position, string Icon, int DurationSeconds, IReadOnlyList<DisplayBoardSlideTextViewModel> Texts);

public sealed record DisplayBoardSlideTextViewModel(string Language, string Title, string Body);

/// <summary>The answers of the slide service that are not plain validation.</summary>
public static class InstructionSlideErrors
{
    public const string UnknownZones = "Every zone of a slide must be a queue zone of the site's published zone profile.";
    public const string UnknownDisplays = "Every display of a slide must be a display of the site.";
    public const string StaysInSite = "A slide stays in its site.";
    public const string TooMany = "A site has at most 100 instruction slides.";
    public const string OrderMismatch = "The order must list every slide of the site once.";
}

/// <summary>
/// Instruction slides for passenger displays (ARV-141), within the caller's sites (another site's slide answers like one
/// that does not exist); every change audited.
/// </summary>
public interface ISvcInstructionSlides : ISvcScoped
{
    Task<Result<IReadOnlyList<InstructionSlideViewModel>>> SearchAsync(string siteCode, CancellationToken ct = default);
    Task<Result<InstructionSlideViewModel>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<InstructionSlideViewModel>> CreateAsync(InstructionSlideRequest request, CancellationToken ct = default);
    Task<Result<InstructionSlideViewModel>> UpdateAsync(Guid id, InstructionSlideRequest request, CancellationToken ct = default);
    Task<Result<InstructionSlideViewModel>> RetireAsync(Guid id, CancellationToken ct = default);
    Task<Result<InstructionSlideViewModel>> RestoreAsync(Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<InstructionSlideViewModel>>> ReorderAsync(string siteCode, InstructionSlideOrderRequest request, CancellationToken ct = default);
}
