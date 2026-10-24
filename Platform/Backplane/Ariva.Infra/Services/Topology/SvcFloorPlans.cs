using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Storage;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Topology;

/// <summary>
/// Floor plans (ARV-018). The level must be live and inside the caller's sites (404 otherwise). An upload is read to at
/// most 20 MB, scanned for malware (ARV-092b, <see cref="UploadScreening"/>), inspected by its bytes, sanitised when SVG,
/// stored under a generated key and recorded; the previous plan of the level is soft deleted and its file removed after
/// commit. Changes are audited.
/// </summary>
internal sealed class SvcFloorPlans(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    IFileStorage storage,
    AuditTrail audit,
    UploadScreening screening) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcFloorPlans
{
    public async Task<Result<FloorPlanViewModel>> GetAsync(Guid levelId, CancellationToken ct = default)
    {
        var plan = await CurrentAsync(levelId, ct);
        return plan is null ? Result.Error<FloorPlanViewModel>(TopologyErrors.NotFound) : new Result<FloorPlanViewModel>(View(plan));
    }

    public async Task<Result<IReadOnlyList<FloorPlanViewModel>>> ListAsync(string siteCode, CancellationToken ct = default)
    {
        // A site the caller cannot see and a site that does not exist answer the same (CWE-863).
        if (siteCode is null || !(await siteScope.GetAsync(ct)).Allows(siteCode) || !await Query<Site>().AnyAsync(s => s.Code == siteCode, ct))
            return Result.Error<IReadOnlyList<FloorPlanViewModel>>(TopologyErrors.NotFound);
        var plans = await Query<FloorPlan>()
            .Where(p => p.SiteCode == siteCode && p.DeletedOn == null && p.Level.DeletedOn == null)
            .ToListAsync(ct);
        return new Result<IReadOnlyList<FloorPlanViewModel>>(plans.Select(View).OrderBy(p => p.LevelId).ToList());
    }

    public async Task<Result<FloorPlanContent>> OpenAsync(Guid levelId, CancellationToken ct = default)
    {
        var plan = await CurrentAsync(levelId, ct);
        var stream = plan is null ? null : await storage.OpenReadAsync(plan.StorageKey, ct);
        return stream is null
            ? Result.Error<FloorPlanContent>(TopologyErrors.NotFound)
            : new Result<FloorPlanContent>(new FloorPlanContent(stream, plan.ContentType, plan.Sha256));
    }

    public async Task<Result<FloorPlanViewModel>> UploadAsync(Guid levelId, FloorPlanUpload upload, CancellationToken ct = default)
    {
        var level = await LevelAsync(levelId, ct);
        if (level is null)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.NotFound);
        if (upload?.Content is null)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.UnsupportedFile);
        if (upload.Length > FloorPlan.MaxBytes)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.TooLarge);
        var (fileName, nameError) = CheckFileName(upload.FileName);
        if (nameError is not null)
            return Result.Error<FloorPlanViewModel>(nameError);

        var bytes = await UploadReader.ReadAtMostAsync(upload.Content, FloorPlan.MaxBytes, ct);
        if (bytes is null)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.TooLarge);
        // The bytes read are the bytes scanned, inspected and stored: nothing reads the file before the scan.
        var screened = await screening.ScreenAsync(UploadKind.FloorPlan, bytes, ct);
        if (screened.HasErrors)
            return Result.Error<FloorPlanViewModel>(screened.ErrorMessages);
        var file = FloorPlanFiles.Inspect(bytes);
        if (file is null)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.UnsupportedFile);

        FloorPlan plan;
        try
        {
            plan = new FloorPlan(level, $"{Guid.CreateVersion7():N}.{file.Extension}", file.ContentType, file.Bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), fileName, file.WidthPixels, file.HeightPixels,
                upload.MetresPerPixel, upload.OriginX, upload.OriginY);
        }
        catch (ArgumentException e)
        {
            return Result.Error<FloorPlanViewModel>(e.Message.Split(" (Parameter", 2)[0]);
        }

        // ARV-154: the site's row first (a site configuration import holds it while it checks that the site is empty), then
        // the level's: concurrent uploads for one level queue here instead of racing to the unique index.
        await ShareSiteAsync(level.SiteCode, ct);
        await ExecuteCommandAsync<LockRow>(
            """SELECT 1 AS "Value" FROM level WHERE id = :id FOR UPDATE""",
            new Dictionary<string, object> { ["id"] = levelId },
            ct);
        var previous = await Query<FloorPlan>().Where(p => p.Level.Id == levelId).ToListAsync(ct);
        var storedKey = plan.StorageKey;
        RegisterPostRollbackAction(() => storage.DeleteAsync(storedKey));
        await using (var content = new MemoryStream(file.Bytes, writable: false))
            await storage.SaveAsync(plan.StorageKey, content, ct);

        foreach (var old in previous)
        {
            old.SoftDelete(CurrentUser.UserName, UtcNow);
            await UpdateAsync(old, ct);
            var oldKey = old.StorageKey;
            RegisterPostCommitAction(() => storage.DeleteAsync(oldKey));
        }

        // The soft delete must reach the database before the insert, or the partial unique index sees two live plans.
        if (previous.Count > 0)
            await FlushAsync(ct);
        await SaveAsync(plan, ct);
        await audit.RecordAsync("FloorPlan.Uploaded", "FloorPlan", plan.Id, $"{level.Code}", previous.Count == 0 ? null : Summary(previous[0]), Summary(plan), ct);
        return new Result<FloorPlanViewModel>(View(plan));
    }

    public async Task<Result<FloorPlanViewModel>> CalibrateAsync(Guid levelId, CalibrateFloorPlanRequest request, CancellationToken ct = default)
    {
        var plan = await CurrentAsync(levelId, ct);
        if (plan is null)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.NotFound);

        var before = Summary(plan);
        try
        {
            plan.Calibrate(request?.MetresPerPixel ?? 0, request?.OriginX ?? 0, request?.OriginY ?? 0);
        }
        catch (ArgumentException e)
        {
            return Result.Error<FloorPlanViewModel>(e.Message.Split(" (Parameter", 2)[0]);
        }

        await UpdateAsync(plan, ct);
        await audit.RecordAsync("FloorPlan.Calibrated", "FloorPlan", plan.Id, plan.Level.Code, before, Summary(plan), ct);
        return new Result<FloorPlanViewModel>(View(plan));
    }

    public async Task<Result<bool>> DeleteAsync(Guid levelId, CancellationToken ct = default)
    {
        var plan = await CurrentAsync(levelId, ct);
        if (plan is null)
            return Result.Error<bool>(TopologyErrors.NotFound);

        plan.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(plan, ct);
        var key = plan.StorageKey;
        RegisterPostCommitAction(() => storage.DeleteAsync(key));
        await audit.RecordAsync("FloorPlan.Deleted", "FloorPlan", plan.Id, plan.Level.Code, Summary(plan), null, ct);
        return new Result<bool>(true);
    }

    private async Task<Level> LevelAsync(Guid levelId, CancellationToken ct)
    {
        var level = levelId == Guid.Empty ? null : await GetAsync<Level>(levelId, ct);
        return level is null || level.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(level.SiteCode) ? null : level;
    }

    private async Task<FloorPlan> CurrentAsync(Guid levelId, CancellationToken ct) =>
        await LevelAsync(levelId, ct) is null ? null : await Query<FloorPlan>().FirstOrDefaultAsync(p => p.Level.Id == levelId, ct);

    /// <summary>
    /// The name for display, or null when it carries a path (CWE-22), control characters, invisible format characters of
    /// any plane, such as a right-to-left override that would make <c>plan[RLO]gvs.png</c> read as another name, or more than 200
    /// code points (<see cref="CheckFileName"/>). The site configuration import (ARV-122a) applies it again to every floor
    /// plan's name.
    /// </summary>
    internal static string SafeFileName(string name) => CheckFileName(name).Name;

    /// <summary>
    /// The trimmed name, or the fixed reason it is refused (ARV-153, CWE-120): a name that is unsafe as above, or longer
    /// than the column takes (<see cref="FloorPlan.MaxFileNameLength"/> code points; PostgreSQL counts each combining mark
    /// and each surrogate pair once). Refused rather than cut, as the site configuration import refuses it: nothing is
    /// stored, and nothing reaches the insert that the column would refuse. A blank name becomes "floor-plan".
    /// </summary>
    internal static (string Name, string Error) CheckFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ("floor-plan", null);
        // One rule in Ariva.Core (ARV-162): paths, then a lone surrogate, control or format character judged per code point,
        // so the invisible ones outside the basic plane are refused too: tag characters (U+E0001, U+E0020 to U+E007F),
        // shorthand format controls (U+1BCA0 to U+1BCA3), musical formatting (U+1D173 to U+1D17A); the three subdivision flag
        // emoji are taken whole (ARV-169).
        if (!FloorPlan.HasSafeFileNameCharacters(name))
            return (null, TopologyErrors.UnsafeFileName);

        var trimmed = name.Trim();
        return FloorPlan.FileNameFits(trimmed) ? (trimmed, null) : (null, TopologyErrors.FileNameTooLong);
    }

    private static string Summary(FloorPlan p) =>
        $"type={p.ContentType}; bytes={p.SizeBytes}; sha256={p.Sha256}; metresPerPixel={p.MetresPerPixel}; origin={p.OriginX},{p.OriginY}";

    private static FloorPlanViewModel View(FloorPlan p) => new(p.Id.Value, p.Level.Id.Value, p.ContentType, p.SizeBytes, p.Sha256, p.OriginalFileName,
        p.WidthPixels, p.HeightPixels, p.MetresPerPixel, p.OriginX, p.OriginY, p.SiteCode, p.CreatedOn);

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
