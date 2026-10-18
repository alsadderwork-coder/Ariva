using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// An advertisement of a site's media library (ARV-142a and ARV-142b, wiki 11 section 9): an image decoded and written anew
/// on upload, or a video whose container was checked and stripped of its metadata, kept in file storage under a generated
/// key, with who advertises, the campaign and the site's local dates it may play.
/// It plays only once approved, and only by a person other than the one who uploaded it and the one who last changed it
/// (two people, CWE-863); a change to an approved item sends it back to draft, so nothing that plays was seen by one person
/// only, and an approval names what its approver reviewed (<see cref="ReviewToken"/>), so it never lands on details changed
/// after the approver read them. A retired item is kept with its history and never plays again; its file stays until retention removes it. The
/// words are plain text: checked here (bounded, no control or invisible characters) and only ever shown as text.
/// Script 0058 holds the same rules in the database.
/// </summary>
public class AdvertisingMedia : BaseAuditableEntity<AdvertisingMedia>, ISiteBound
{
    /// <summary>The largest image taken, as uploaded.</summary>
    public const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>
    /// The longest side and the most pixels an image may have, checked from its header before anything is decoded: a 4K
    /// screen (3,840 by 2,160) in either orientation, which is what a passenger display shows. Decoded, that is 33 MB; the
    /// whole decode and encode of the costliest kind (a lossy WebP) peaks at about 200 MB of native memory, one at a time
    /// per host (CWE-400, security review of ARV-142a).
    /// </summary>
    public const int MaxSide = 3840;

    public const long MaxPixels = 8_294_400;

    /// <summary>
    /// The largest video any installation takes (ARV-142b): <c>Advertising:Videos:MaxMegabytes</c> sets the size taken, at
    /// most this; the upload endpoint's request limit is this plus the form's framing.
    /// </summary>
    public const long MaxVideoBytes = 100L * 1024 * 1024;

    /// <summary>The longest video any installation takes; <c>Advertising:Videos:MaxSeconds</c> sets the length taken, at most this.</summary>
    public const int MaxVideoSeconds = 120;

    public const int MaxNameLength = 120;

    /// <summary>The longest an item's dates may span: two years.</summary>
    public const int MaxDays = 731;

    /// <summary>The most items a site's library lists (retired ones included).</summary>
    public const int MaxItemsPerSite = 500;

    protected AdvertisingMedia()
    {
    }

    public AdvertisingMedia(string siteCode, AdvertisingMediaFile file, AdvertisingMediaDetails details, Guid uploaderId)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        ArgumentNullException.ThrowIfNull(file);
        var fileProblems = file.Problems();
        if (fileProblems.Count > 0)
            throw new ArgumentException(string.Join(" ", fileProblems), nameof(file));
        if (uploaderId == Guid.Empty)
            throw new ArgumentException("An item is uploaded by a known person.", nameof(uploaderId));
        SiteCode = siteCode;
        StorageKey = file.StorageKey;
        ContentType = file.ContentType;
        SizeBytes = file.SizeBytes;
        Sha256 = file.Sha256;
        WidthPixels = file.WidthPixels;
        HeightPixels = file.HeightPixels;
        DurationMilliseconds = file.DurationMilliseconds;
        OriginalFileName = string.IsNullOrWhiteSpace(file.OriginalFileName) ? null : file.OriginalFileName.Trim();
        UploadedById = uploaderId;
        Status = AdvertisingMediaStatus.Draft;
        SetDetails(details, uploaderId);
    }

    public virtual string SiteCode { get; protected set; }

    /// <summary>The generated file storage key (32 hex characters, a dot and the extension); never from the request.</summary>
    [MaxLength(40)]
    public virtual string StorageKey { get; protected set; }

    [MaxLength(32)]
    public virtual string ContentType { get; protected set; }

    public virtual long SizeBytes { get; protected set; }

    /// <summary>The SHA-256 of the stored bytes (as written anew, not as uploaded), lower-case hex.</summary>
    [MaxLength(64)]
    public virtual string Sha256 { get; protected set; }

    public virtual int WidthPixels { get; protected set; }
    public virtual int HeightPixels { get; protected set; }

    /// <summary>A video's length, as its container states it (rounded up to the millisecond); null for an image.</summary>
    public virtual int? DurationMilliseconds { get; protected set; }

    /// <summary>The uploaded file's name, for people to recognise it; never a path or a storage key.</summary>
    [MaxLength(FloorPlan.MaxFileNameLength)]
    public virtual string OriginalFileName { get; protected set; }

    [MaxLength(MaxNameLength)]
    public virtual string Advertiser { get; protected set; }

    [MaxLength(MaxNameLength)]
    public virtual string Campaign { get; protected set; }

    /// <summary>The first local day of the site the item may play, "yyyy-MM-dd".</summary>
    [MaxLength(10)]
    public virtual string StartsOn { get; protected set; }

    /// <summary>The last local day of the site the item may play, "yyyy-MM-dd".</summary>
    [MaxLength(10)]
    public virtual string EndsOn { get; protected set; }

    public virtual AdvertisingMediaStatus Status { get; protected set; }

    /// <summary>The account that uploaded the item; it never approves it.</summary>
    public virtual Guid UploadedById { get; protected set; }

    /// <summary>The account that last set the item's details (the uploader at first); it never approves it either.</summary>
    public virtual Guid ChangedById { get; protected set; }

    public virtual Guid? ApprovedById { get; protected set; }

    [MaxLength(200)]
    public virtual string ApprovedBy { get; protected set; }

    public virtual DateTime? ApprovedOn { get; protected set; }

    public virtual DateTime? RetiredOn { get; protected set; }

    public virtual DateOnly StartDate => CalendarDates.TryParse(StartsOn, out var date) ? date : DateOnly.MaxValue;

    public virtual DateOnly EndDate => CalendarDates.TryParse(EndsOn, out var date) ? date : DateOnly.MinValue;

    public virtual AdvertisingMediaDetails Details() => new(Advertiser, Campaign, StartDate, EndDate);

    /// <summary>
    /// Sets who advertises, the campaign and the dates. An approved item goes back to draft (its approval withdrawn), so a
    /// second person approves what plays; a retired item is not changed. True when an approval was withdrawn.
    /// </summary>
    public virtual bool ChangeDetails(AdvertisingMediaDetails details, Guid changedById)
    {
        if (Status == AdvertisingMediaStatus.Retired)
            throw new InvalidOperationException(AdvertisingMediaRules.RetiredMessage);
        if (changedById == Guid.Empty)
            throw new ArgumentException("An item is changed by a known person.", nameof(changedById));
        var withdrawn = Status == AdvertisingMediaStatus.Approved;
        SetDetails(details, changedById);
        if (withdrawn)
        {
            Status = AdvertisingMediaStatus.Draft;
            ApprovedById = null;
            ApprovedBy = null;
            ApprovedOn = null;
        }

        return withdrawn;
    }

    /// <summary>Whether this person may approve the item: a draft, and neither its uploader nor the last person who changed it.</summary>
    public virtual bool CanBeApprovedBy(Guid? approverId) =>
        Status == AdvertisingMediaStatus.Draft && approverId is { } id && id != Guid.Empty && id != UploadedById && id != ChangedById;

    /// <summary>
    /// What a reviewer sees of the item, as a token: a hash of the stored file, every detail that decides what plays where and
    /// when, its state and who last changed it. An approval names the token of the item it was shown (as a zone profile's
    /// publish names the geometry it reviewed), and is refused when the item has changed since.
    /// </summary>
    public virtual string ReviewToken() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Sha256, Advertiser, Campaign, StartsOn, EndsOn, Status = Status.ToString(), ChangedById
        }, Display.Json))));

    /// <summary>Approves the draft its approver reviewed (<paramref name="reviewedToken"/>, <see cref="ReviewToken"/>).</summary>
    public virtual void Approve(Guid approverId, string approverName, DateTime nowUtc, string reviewedToken)
    {
        if (Status != AdvertisingMediaStatus.Draft)
            throw new InvalidOperationException(AdvertisingMediaRules.NotDraftMessage);
        if (!string.Equals(reviewedToken, ReviewToken(), StringComparison.Ordinal))
            throw new InvalidOperationException(AdvertisingMediaRules.ChangedSinceReviewMessage);
        if (!CanBeApprovedBy(approverId))
            throw new InvalidOperationException(AdvertisingMediaRules.SamePersonMessage);
        ApprovedById = approverId;
        ApprovedBy = DisplayText.Require(approverName, 200, nameof(approverName));
        ApprovedOn = nowUtc;
        Status = AdvertisingMediaStatus.Approved;
    }

    /// <summary>Retires a draft or an approved item; it never plays again. A retired item stays retired.</summary>
    public virtual void Retire(DateTime nowUtc)
    {
        if (Status == AdvertisingMediaStatus.Retired)
            throw new InvalidOperationException(AdvertisingMediaRules.RetiredMessage);
        Status = AdvertisingMediaStatus.Retired;
        RetiredOn = nowUtc;
    }

    /// <summary>Whether the item may play on this local day of its site: approved, and the day within its dates.</summary>
    public virtual bool CanPlayOn(DateOnly siteToday) =>
        Status == AdvertisingMediaStatus.Approved && StartDate <= siteToday && siteToday <= EndDate;

    /// <summary>
    /// What the audit trail keeps of the item: its file (by its hash), its details, its state and who uploaded, changed and
    /// approved it; whole for the longest values (an audit entry keeps 2,000 characters). The file name, which never plays
    /// and could take 2,400 characters escaped, stays on the item only.
    /// </summary>
    public virtual string AuditSummary() =>
        JsonSerializer.Serialize(new
        {
            SiteCode, ContentType, SizeBytes, Sha256, WidthPixels, HeightPixels, DurationMilliseconds, Advertiser, Campaign, StartsOn, EndsOn,
            Status = Status.ToString(), UploadedById, ChangedById, ApprovedById
        }, Display.Json);

    private void SetDetails(AdvertisingMediaDetails details, Guid changedById)
    {
        ArgumentNullException.ThrowIfNull(details);
        var problems = details.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(details));
        Advertiser = details.Advertiser.Trim();
        Campaign = details.Campaign.Trim();
        StartsOn = CalendarDates.Format(details.StartsOn);
        EndsOn = CalendarDates.Format(details.EndsOn);
        ChangedById = changedById;
    }
}

/// <summary>The fixed refusals of the item's rules, which the service answers with.</summary>
public static class AdvertisingMediaRules
{
    public const string SamePersonMessage = "The person who uploaded an item or last changed it cannot approve it; another person approves it.";
    public const string NotDraftMessage = "Only a draft can be approved.";

    public const string ChangedSinceReviewMessage =
        "This item has changed since you reviewed it; look at it again before approving it.";
    public const string RetiredMessage = "A retired item cannot be changed.";
}

/// <summary>What an item shows about itself (ARV-142a): who advertises, the campaign and the site's local days it may play.</summary>
public sealed record AdvertisingMediaDetails(string Advertiser, string Campaign, DateOnly StartsOn, DateOnly EndsOn)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!IsName(Advertiser))
            problems.Add($"The advertiser is named, in at most {AdvertisingMedia.MaxNameLength} characters on one line.");
        if (!IsName(Campaign))
            problems.Add($"The campaign is named, in at most {AdvertisingMedia.MaxNameLength} characters on one line.");
        if (StartsOn == default || EndsOn == default || EndsOn < StartsOn)
            problems.Add("The last day is on or after the first day.");
        else if (EndsOn.DayNumber - StartsOn.DayNumber >= AdvertisingMedia.MaxDays)
            problems.Add($"An item's dates span at most {AdvertisingMedia.MaxDays} days.");
        return problems;
    }

    private static bool IsName(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= AdvertisingMedia.MaxNameLength && DisplayText.IsClean(text.Trim());
}

/// <summary>
/// The stored file of an item: an image as the re-encoder wrote it (ARV-142a), or a video as its container check left it,
/// with its length (ARV-142b).
/// </summary>
public sealed record AdvertisingMediaFile(
    string StorageKey,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int WidthPixels,
    int HeightPixels,
    string OriginalFileName,
    int? DurationMilliseconds = null)
{
    /// <summary>The image types an item may hold, by content type, with the extension of their storage keys.</summary>
    public static IReadOnlyDictionary<string, string> ImageTypes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/webp"] = "webp"
    };

    /// <summary>The video types an item may hold (ARV-142b), by content type, with the extension of their storage keys.</summary>
    public static IReadOnlyDictionary<string, string> VideoTypes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["video/mp4"] = "mp4",
        ["video/webm"] = "webm"
    };

    public bool IsVideo => ContentType is not null && VideoTypes.ContainsKey(ContentType);

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var video = IsVideo;
        if (ContentType is null || !(video ? VideoTypes : ImageTypes).TryGetValue(ContentType, out var extension) || !IsStorageKey(StorageKey, extension))
            problems.Add("The file is a PNG, JPEG or WebP image, or an MP4 or WebM video, under a generated key.");
        if (!video && SizeBytes is <= 0 or > AdvertisingMedia.MaxImageBytes)
            problems.Add("An image is not empty and at most 10 MB.");
        if (video && SizeBytes is <= 0 or > AdvertisingMedia.MaxVideoBytes)
            problems.Add("A video is not empty and at most 100 MB.");
        if (video ? DurationMilliseconds is not (> 0 and <= AdvertisingMedia.MaxVideoSeconds * 1000) : DurationMilliseconds is not null)
            problems.Add($"A video lasts at most {AdvertisingMedia.MaxVideoSeconds} seconds; an image has no length.");
        if (Sha256 is not { Length: 64 } || !Sha256.All(char.IsAsciiHexDigitLower))
            problems.Add("The file's SHA-256 is 64 lower-case hex characters.");
        if (WidthPixels is <= 0 or > AdvertisingMedia.MaxSide || HeightPixels is <= 0 or > AdvertisingMedia.MaxSide ||
            (long)WidthPixels * HeightPixels > AdvertisingMedia.MaxPixels)
            problems.Add($"A picture is at most {AdvertisingMedia.MaxSide} pixels a side and {AdvertisingMedia.MaxPixels} pixels in all (3,840 by 2,160).");
        if (!FloorPlan.IsSafeFileName(OriginalFileName))
            problems.Add("The file name holds no path, no control or invisible characters, and at most 200 characters.");
        return problems;
    }

    private static bool IsStorageKey(string key, string extension) =>
        key is not null && key.Length == 33 + extension.Length && key[32] == '.' && key.AsSpan(33).SequenceEqual(extension) &&
        key[..32].All(char.IsAsciiHexDigitLower);
}
