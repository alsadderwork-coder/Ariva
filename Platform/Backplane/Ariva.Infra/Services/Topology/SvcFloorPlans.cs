using System.Globalization;
using System.Security.Cryptography;
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
/// most 20 MB, inspected by its bytes, sanitised when SVG, stored under a generated key and recorded; the previous plan
/// of the level is soft deleted and its file removed after commit. Changes are audited.
/// </summary>
internal sealed class SvcFloorPlans(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    IFileStorage storage,
    AuditTrail audit) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcFloorPlans
{
    public async Task<Result<FloorPlanViewModel>> GetAsync(Guid levelId, CancellationToken ct = default)
    {
        var plan = await CurrentAsync(levelId, ct);
        return plan is null ? Result.Error<FloorPlanViewModel>(TopologyErrors.NotFound) : new Result<FloorPlanViewModel>(View(plan));
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
        var fileName = SafeFileName(upload.FileName);
        if (fileName is null)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.UnsafeFileName);

        var bytes = await ReadAtMostAsync(upload.Content, FloorPlan.MaxBytes, ct);
        if (bytes is null)
            return Result.Error<FloorPlanViewModel>(TopologyErrors.TooLarge);
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

        // Row lock on the level: concurrent uploads for one level queue here instead of racing to the unique index.
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

    /// <summary>The bytes, or null when the stream holds more than <paramref name="limit"/>.</summary>
    private static async Task<byte[]> ReadAtMostAsync(Stream content, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The name for display, or null when it carries a path (CWE-22), control characters, or invisible format characters
    /// such as a right-to-left override that would make <c>plan[RLO]gvs.png</c> read as another name.
    /// </summary>
    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "floor-plan";
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal) || name.Contains(':') ||
            name.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) || HasLoneSurrogate(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var info = new StringInfo(trimmed);
        // Cut by text element, so no surrogate pair or combining sequence is split.
        return info.LengthInTextElements <= 200 ? trimmed : info.SubstringByTextElements(0, 200);
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return true;
        }

        return false;
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
