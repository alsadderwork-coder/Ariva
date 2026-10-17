using System.Security.Cryptography;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Storage;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The advertisement library (ARV-142a). Every call names a site of the caller (another answers like a site that does not
/// exist) and every item is looked up by its id and that site together, so another site's item answers 404 (CWE-863,
/// IDOR). An upload is read to at most 10 MB, decoded and written anew (<see cref="AdvertisingImages"/>), stored under a
/// generated key and recorded as a draft, each person's attempts counted first (<see cref="AdvertisingUploadWindow"/>);
/// changing, approving and retiring lock the item's row first, and an approval names the review token of what its approver
/// was shown, so it never lands on details changed after the approver read them (security review). The display fetch serves
/// only what may play on that display now. Every change is audited.
/// </summary>
internal sealed class SvcAdvertisingMedia(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    IFileStorage storage,
    AuditTrail audit,
    ReportReader sites,
    AdvertisingPolicyGuard advertising,
    AdvertisingUploadWindow uploads)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAdvertisingMedia
{
    private const string Target = "AdvertisingMedia";

    public async Task<Result<IReadOnlyList<AdvertisingMediaViewModel>>> ListAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<IReadOnlyList<AdvertisingMediaViewModel>>(TopologyErrors.NotFound);
        var items = await QueryAsNoTracking<AdvertisingMedia>()
            .Where(m => m.SiteCode == siteCode)
            .OrderByDescending(m => m.CreatedOn)
            .Take(AdvertisingMedia.MaxItemsPerSite)
            .ToListAsync(ct);
        var today = await TodayAsync(siteCode, ct);
        return new Result<IReadOnlyList<AdvertisingMediaViewModel>>(items.Select(m => View(m, today)).ToList());
    }

    public async Task<Result<AdvertisingMediaViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        var item = await FindAsync(siteCode, id, ct);
        return item is null ? Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound) : new Result<AdvertisingMediaViewModel>(View(item, await TodayAsync(siteCode, ct)));
    }

    public async Task<Result<AdvertisingMediaContent>> OpenAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        var item = await FindAsync(siteCode, id, ct);
        return await ContentAsync(item, ct);
    }

    public async Task<Result<AdvertisingMediaViewModel>> UploadAsync(string siteCode, AdvertisingMediaUpload upload, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct) || CurrentUser.Id is not { } uploader)
            return Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound);
        // Every attempt counts, a refused file too: a file that is valid up to its last byte costs a whole decode (CWE-400).
        if (!uploads.TryEnter(uploader))
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.TooManyUploads);
        if (upload?.Content is null)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.UnsupportedFile);
        if (upload.Length > AdvertisingMedia.MaxImageBytes)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.TooLarge);
        var details = new AdvertisingMediaDetails(upload.Advertiser, upload.Campaign, upload.StartsOn, upload.EndsOn);
        var problems = details.Problems();
        if (problems.Count > 0)
            return Result.Error<AdvertisingMediaViewModel>(problems);
        // The name is only for people to recognise the file; one with a path or a hidden character is refused, never cleaned (CWE-22).
        if (!FloorPlan.IsSafeFileName(upload.FileName))
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.UnsafeFileName);

        var bytes = await UploadReader.ReadAtMostAsync(upload.Content, AdvertisingMedia.MaxImageBytes, ct);
        if (bytes is null)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.TooLarge);
        var image = await AdvertisingImages.ReencodeAsync(bytes, ct);
        if (image is null)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.UnsupportedFile);

        // Advisory lock class 1421, one per site's library: uploads of one site count its items one at a time.
        await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM (SELECT pg_advisory_xact_lock(1421, hashtext(:site))) l""",
            new Dictionary<string, object> { ["site"] = siteCode }, ct);
        if (await Query<AdvertisingMedia>().CountAsync(m => m.SiteCode == siteCode, ct) >= AdvertisingMedia.MaxItemsPerSite)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.LibraryFull);

        var file = new AdvertisingMediaFile($"{Guid.CreateVersion7():N}.{image.Extension}", image.ContentType, image.Bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(image.Bytes)), image.WidthPixels, image.HeightPixels,
            string.IsNullOrWhiteSpace(upload.FileName) ? null : upload.FileName.Trim());
        var item = new AdvertisingMedia(siteCode, file, details, uploader);
        var key = item.StorageKey;
        RegisterPostRollbackAction(() => storage.DeleteAsync(key));
        await using (var content = new MemoryStream(image.Bytes, writable: false))
            await storage.SaveAsync(key, content, ct);
        await SaveAsync(item, ct);
        await audit.RecordAsync($"{Target}.Uploaded", Target, item.Id, siteCode, null, item.AuditSummary(), ct);
        return new Result<AdvertisingMediaViewModel>(View(item, await TodayAsync(siteCode, ct)));
    }

    public async Task<Result<AdvertisingMediaViewModel>> ChangeAsync(string siteCode, Guid id, ChangeAdvertisingMediaRequest request, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct) || CurrentUser.Id is not { } changer)
            return Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound);
        if (request is null)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.Required);
        var details = new AdvertisingMediaDetails(request.Advertiser, request.Campaign, request.StartsOn, request.EndsOn);
        var problems = details.Problems();
        if (problems.Count > 0)
            return Result.Error<AdvertisingMediaViewModel>(problems);
        var item = await LockAsync(siteCode, id, ct);
        if (item is null)
            return Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound);
        if (item.Status == AdvertisingMediaStatus.Retired)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.Retired);

        var before = item.AuditSummary();
        item.ChangeDetails(details, changer);
        await UpdateAsync(item, ct);
        await audit.RecordAsync($"{Target}.Changed", Target, item.Id, siteCode, before, item.AuditSummary(), ct);
        return new Result<AdvertisingMediaViewModel>(View(item, await TodayAsync(siteCode, ct)));
    }

    public async Task<Result<AdvertisingMediaViewModel>> ApproveAsync(string siteCode, Guid id, ApproveAdvertisingMediaRequest request, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct) || CurrentUser.Id is not { } approver)
            return Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound);
        var item = await LockAsync(siteCode, id, ct);
        if (item is null)
            return Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound);
        if (item.Status != AdvertisingMediaStatus.Draft)
            return Result.Error<AdvertisingMediaViewModel>(item.Status == AdvertisingMediaStatus.Retired ? AdvertisingMediaErrors.Retired : AdvertisingMediaErrors.NotDraft);
        // Judged under the row lock: what the approver was shown must be what is approved (as a profile publish names its geometry).
        if (!string.Equals(request?.Reviewed, item.ReviewToken(), StringComparison.Ordinal))
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.ChangedSinceReview);
        if (!item.CanBeApprovedBy(approver))
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.SamePerson);

        var before = item.AuditSummary();
        item.Approve(approver, CurrentUser.UserName, UtcNow, request.Reviewed);
        await UpdateAsync(item, ct);
        await audit.RecordAsync($"{Target}.Approved", Target, item.Id, siteCode, before, item.AuditSummary(), ct);
        return new Result<AdvertisingMediaViewModel>(View(item, await TodayAsync(siteCode, ct)));
    }

    public async Task<Result<AdvertisingMediaViewModel>> RetireAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound);
        var item = await LockAsync(siteCode, id, ct);
        if (item is null)
            return Result.Error<AdvertisingMediaViewModel>(TopologyErrors.NotFound);
        if (item.Status == AdvertisingMediaStatus.Retired)
            return Result.Error<AdvertisingMediaViewModel>(AdvertisingMediaErrors.Retired);

        var before = item.AuditSummary();
        item.Retire(UtcNow);
        await UpdateAsync(item, ct);
        await audit.RecordAsync($"{Target}.Retired", Target, item.Id, siteCode, before, item.AuditSummary(), ct);
        return new Result<AdvertisingMediaViewModel>(View(item, await TodayAsync(siteCode, ct)));
    }

    public async Task<Result<AdvertisingMediaContent>> OpenForDisplayAsync(Guid displayId, string credentialPrefix, Guid id, CancellationToken ct = default)
    {
        var display = await QueryAsNoTracking<Display>()
            .Where(d => d.Id == displayId && d.DeletedOn == null && d.Enabled && d.CredentialPrefix == credentialPrefix)
            .FirstOrDefaultAsync(ct);
        if (display is null || display.SecondRegion != DisplaySecondRegion.Advertising || id == Guid.Empty)
            return Result.Error<AdvertisingMediaContent>(TopologyErrors.NotFound);
        // The display's own site only: an item of another site is not found, whatever its id (IDOR).
        var item = await QueryAsNoTracking<AdvertisingMedia>().Where(m => m.Id == id && m.SiteCode == display.SiteCode).FirstOrDefaultAsync(ct);
        if (item is null || !item.CanPlayOn(await TodayAsync(display.SiteCode, ct)))
            return Result.Error<AdvertisingMediaContent>(TopologyErrors.NotFound);
        // ARV-145: the site's policy allows advertising for every zone the display shows, read now, on every fetch.
        if (!await advertising.AllowsAsync(display.SiteCode, display.EntryList.Select(e => e.Zone), ct))
            return Result.Error<AdvertisingMediaContent>(TopologyErrors.NotFound);
        return await ContentAsync(item, ct);
    }

    private async Task<Result<AdvertisingMediaContent>> ContentAsync(AdvertisingMedia item, CancellationToken ct)
    {
        var stream = item is null ? null : await storage.OpenReadAsync(item.StorageKey, ct);
        return stream is null
            ? Result.Error<AdvertisingMediaContent>(TopologyErrors.NotFound)
            : new Result<AdvertisingMediaContent>(new AdvertisingMediaContent(stream, item.ContentType, item.Sha256));
    }

    private async Task<AdvertisingMedia> FindAsync(string siteCode, Guid id, CancellationToken ct) =>
        id == Guid.Empty || !await VisibleSiteAsync(siteCode, ct)
            ? null
            : await QueryAsNoTracking<AdvertisingMedia>().Where(m => m.Id == id && m.SiteCode == siteCode).FirstOrDefaultAsync(ct);

    /// <summary>The item of this site, read after its row is locked for the rest of the transaction; null when there is none.</summary>
    private async Task<AdvertisingMedia> LockAsync(string siteCode, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return null;
        var locked = await ExecuteCommandAsync<LockRow>(
            """SELECT 1 AS "Value" FROM advertising_media WHERE id = :id AND site_code = :site FOR UPDATE""",
            new Dictionary<string, object> { ["id"] = id, ["site"] = siteCode }, ct);
        return locked.Count == 0 ? null : await Query<AdvertisingMedia>().Where(m => m.Id == id && m.SiteCode == siteCode).FirstOrDefaultAsync(ct);
    }

    private async Task<bool> VisibleSiteAsync(string siteCode, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(siteCode) && (await siteScope.GetAsync(ct)).Allows(siteCode) &&
        await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == siteCode, ct);

    /// <summary>The site's local day now (its airports' time zone, UTC for a site without one).</summary>
    private async Task<DateOnly> TodayAsync(string siteCode, CancellationToken ct) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, await sites.TimeZoneAsync(siteCode, ct)));

    private AdvertisingMediaViewModel View(AdvertisingMedia m, DateOnly today) =>
        new(m.Id.GetValueOrDefault(), m.SiteCode, m.ContentType, m.SizeBytes, m.Sha256, m.WidthPixels, m.HeightPixels, m.OriginalFileName, m.Advertiser,
            m.Campaign, m.StartDate, m.EndDate, m.Status.ToString(), m.CreatedBy, m.CreatedOn, m.ApprovedBy, m.ApprovedOn, m.RetiredOn, m.CanPlayOn(today),
            m.CanBeApprovedBy(CurrentUser.Id), m.ReviewToken());

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
