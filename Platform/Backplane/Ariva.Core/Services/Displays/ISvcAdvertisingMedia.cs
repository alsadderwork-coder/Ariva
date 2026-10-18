using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>
/// An upload to a site's advertisement library (ARV-142a, videos ARV-142b): the file as sent (its type is read from its
/// bytes, never from its name or the declared content type), its declared length, its name for people to recognise it,
/// and the item's details.
/// </summary>
public sealed record AdvertisingMediaUpload(Stream Content, long Length, string FileName, string Advertiser, string Campaign, DateOnly StartsOn, DateOnly EndsOn);

/// <summary>New details of an item: who advertises, the campaign and the site's local days it may play. Changing an approved item sends it back to draft.</summary>
public sealed record ChangeAdvertisingMediaRequest(
    [Required, MaxLength(120)] string Advertiser,
    [Required, MaxLength(120)] string Campaign,
    DateOnly StartsOn,
    DateOnly EndsOn);

/// <summary>
/// An approval: the review token of the item as its approver was shown it (<see cref="AdvertisingMediaViewModel.ReviewToken"/>).
/// It is refused (409) when the item has changed since, so an approval never lands on details its approver did not see.
/// </summary>
public sealed record ApproveAdvertisingMediaRequest([Required, MaxLength(64)] string Reviewed);

/// <summary>
/// An item of the library as the API shows it. <c>Playable</c> says whether it may play on the site's local day now
/// (approved and within its dates); <c>CanApprove</c> whether the caller may approve it (a draft that the caller neither
/// uploaded nor last changed; the endpoint still asks for <c>Advertising.Approve</c> and a second factor);
/// <c>ReviewToken</c> what an approval of what is shown here names; <c>DurationMilliseconds</c> a video's length (null for
/// an image).
/// </summary>
public sealed record AdvertisingMediaViewModel(
    Guid Id,
    string SiteCode,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int WidthPixels,
    int HeightPixels,
    string OriginalFileName,
    string Advertiser,
    string Campaign,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string Status,
    string UploadedBy,
    DateTime? UploadedOn,
    string ApprovedBy,
    DateTime? ApprovedOn,
    DateTime? RetiredOn,
    bool Playable,
    bool CanApprove,
    string ReviewToken,
    int? DurationMilliseconds = null);

/// <summary>An item's stored file: its bytes, type and hash, for the preview and the display fetch.</summary>
public sealed record AdvertisingMediaContent(Stream Content, string ContentType, string Sha256);

/// <summary>The answers of the advertisement library that are not plain validation, with their status codes.</summary>
public static class AdvertisingMediaErrors
{
    public const string UnsupportedFile =
        "The file is not a PNG, JPEG or WebP image that can be read whole, or it is animated or larger than 3,840 pixels a side or 8,294,400 pixels in all (3,840 by 2,160).";

    /// <summary>400: a file that starts like an MP4 or a WebM video but is not one the library takes (ARV-142b).</summary>
    public const string UnsupportedVideo =
        "The video is not an MP4 with H.264 video (AAC sound or none) or a WebM with VP8, VP9 or AV1 video (Opus or Vorbis sound or none), " +
        "with one picture of at most 3,840 pixels a side and 8,294,400 pixels in all, a known length, nothing after its end, and neither fragmented nor encrypted.";

    /// <summary>413: an image larger than the library takes.</summary>
    public const string TooLarge = "The file is larger than 10 MB.";

    private const string TooLargePrefix = "The file is larger than ";

    /// <summary>413: a file larger than the library takes (an image at most 10 MB, a video at most the size set for this installation).</summary>
    public static string TooLargeFor(long maxBytes) => $"{TooLargePrefix}{maxBytes / (1024 * 1024)} MB.";

    /// <summary>Whether a refusal is one of the too-large answers (413).</summary>
    public static bool IsTooLarge(string message) =>
        message is not null && message.StartsWith(TooLargePrefix, StringComparison.Ordinal) && message.EndsWith(" MB.", StringComparison.Ordinal);

    /// <summary>400: a video longer than the length set for this installation (ARV-142b).</summary>
    public static string TooLong(int maxSeconds) => $"The video is longer than {maxSeconds} seconds.";
    public const string UnsafeFileName = "The file name holds a path, control or invisible characters, or more than 200 characters.";
    public const string Required = "An item's details are required: the advertiser, the campaign and the first and last day.";

    /// <summary>403: the person who uploaded or last changed an item never approves it.</summary>
    public const string SamePerson = Ariva.Core.Domain.Entities.AdvertisingMediaRules.SamePersonMessage;

    /// <summary>409: the item is no longer a draft.</summary>
    public const string NotDraft = Ariva.Core.Domain.Entities.AdvertisingMediaRules.NotDraftMessage;

    /// <summary>409: the item has changed since its approver was shown it.</summary>
    public const string ChangedSinceReview = Ariva.Core.Domain.Entities.AdvertisingMediaRules.ChangedSinceReviewMessage;

    /// <summary>429: the caller's uploads in the current window are used up (counted on every attempt, refused ones too).</summary>
    public const string TooManyUploads = "At most 30 uploads in 10 minutes per person; try again later.";

    /// <summary>409: the item is retired.</summary>
    public const string Retired = Ariva.Core.Domain.Entities.AdvertisingMediaRules.RetiredMessage;

    /// <summary>409: the site's library is full.</summary>
    public const string LibraryFull = "The site's library holds 500 items, retired ones included; this item is not added.";

    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.Ordinal) { SamePerson };
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { NotDraft, ChangedSinceReview, Retired, LibraryFull };
}

/// <summary>
/// The advertisement library of the sites (ARV-142a), within the caller's sites: another site, and another site's item
/// named by id under this site's route, answer like one that does not exist (CWE-863, IDOR). The endpoints carry
/// <c>[AdvertisingAllowed]</c>, so they act only while the site's advertising policy allows advertising; managing needs
/// <c>Advertising.Manage</c> (reading <c>Advertising.View</c> or <c>Advertising.Manage</c>), approving <c>Advertising.Approve</c>
/// and a second factor. Every change is audited.
/// </summary>
public interface ISvcAdvertisingMedia : ISvcScoped
{
    Task<Result<IReadOnlyList<AdvertisingMediaViewModel>>> ListAsync(string siteCode, CancellationToken ct = default);
    Task<Result<AdvertisingMediaViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default);
    Task<Result<AdvertisingMediaContent>> OpenAsync(string siteCode, Guid id, CancellationToken ct = default);
    Task<Result<AdvertisingMediaViewModel>> UploadAsync(string siteCode, AdvertisingMediaUpload upload, CancellationToken ct = default);
    Task<Result<AdvertisingMediaViewModel>> ChangeAsync(string siteCode, Guid id, ChangeAdvertisingMediaRequest request, CancellationToken ct = default);
    Task<Result<AdvertisingMediaViewModel>> ApproveAsync(string siteCode, Guid id, ApproveAdvertisingMediaRequest request, CancellationToken ct = default);
    Task<Result<AdvertisingMediaViewModel>> RetireAsync(string siteCode, Guid id, CancellationToken ct = default);

    /// <summary>
    /// The file a display player fetches (display-authenticated, ARV-058): only an approved item of the display's own site
    /// within its dates, while the display is set to advertising and the site's advertising rule (ARV-145) allows it for
    /// every zone the display shows; anything else is not found.
    /// </summary>
    Task<Result<AdvertisingMediaContent>> OpenForDisplayAsync(Guid displayId, string credentialPrefix, Guid id, CancellationToken ct = default);
}
