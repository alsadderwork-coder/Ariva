using System.Globalization;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Services.Sensing;

/// <summary>
/// The device registry (ARV-021). Devices belong to the site of their level: searches are filtered to the caller's
/// sites and another site's device answers NotFound (CWE-863). Registration and rotation generate the credential here
/// and return it once; only its prefix and SHA-256 are stored (CWE-287). The owning queue zone must exist in the site's
/// published profile or its draft, lie on the device's level and be reached by the device's footprint; a device goes
/// online only when its zone is published. Registrations in a site are serialised by the site row, and changes to one
/// device by its row, so two requests cannot both pass a check that only one of them may pass. Every change is audited
/// in the same transaction and raises DeviceRegistryChanged through the outbox.
/// </summary>
internal sealed class SvcDevices(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    IFusionCache cache) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDevices
{
    private const string Target = "Device";

    /// <summary>The cache tag of everything Ingest reads about devices (SvcDeviceGateway); every change evicts it.</summary>
    public const string CacheTag = "devices";

    #region Reads

    public async Task<Result<PageViewModel<DeviceViewModel>>> SearchAsync(DeviceCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new DeviceCriteria();
        DeviceState? state = null;
        if (criteria.State is not null)
        {
            if (!TryKind<DeviceState>(criteria.State, out var parsed))
                return Result.Error<PageViewModel<DeviceViewModel>>(DeviceErrors.UnknownState);
            state = parsed;
        }

        var query = QueryAsNoTracking<Device>().WithinSites(await siteScope.GetAsync(ct));
        if (!string.IsNullOrWhiteSpace(criteria.SiteCode))
            query = query.Where(d => d.SiteCode == criteria.SiteCode);
        if (criteria.LevelId is { } levelId)
            query = query.Where(d => d.Level.Id == levelId);
        if (state is { } s)
            query = query.Where(d => d.State == s);
        if (!string.IsNullOrWhiteSpace(criteria.QueueZoneName))
            query = query.Where(d => d.QueueZoneName == criteria.QueueZoneName.Trim());
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var upper = criteria.Text.Trim().ToUpperInvariant();
            var lower = criteria.Text.Trim().ToLowerInvariant();
            query = query.Where(d => d.Code.Contains(upper) || d.Model.ToLower().Contains(lower));
        }

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(d => d.SiteCode).ThenBy(d => d.Code).Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<DeviceViewModel>>(new PageViewModel<DeviceViewModel>([.. rows.Select(View)], total, pageIndex, pageSize));
    }

    public async Task<Result<DeviceViewModel>> GetAsync(Guid id, CancellationToken ct = default) =>
        await ScopedAsync(id, ct) is { } device ? new Result<DeviceViewModel>(View(device)) : Result.Error<DeviceViewModel>(TopologyErrors.NotFound);

    public async Task<Result<IReadOnlyList<CalibrationViewModel>>> CalibrationsAsync(Guid id, CancellationToken ct = default)
    {
        var device = await ScopedAsync(id, ct);
        if (device is null)
            return Result.Error<IReadOnlyList<CalibrationViewModel>>(TopologyErrors.NotFound);
        IReadOnlyList<CalibrationViewModel> list = [.. device.Calibrations.OrderByDescending(c => c.PerformedOn).Select(c => View(c, device))];
        return new Result<IReadOnlyList<CalibrationViewModel>>(list);
    }

    public Result<FootprintViewModel> AssumedFootprint(string family, double mountingHeightMetres)
    {
        if (!TryKind<DeviceFamily>(family, out var parsed))
            return Result.Error<FootprintViewModel>(DeviceErrors.UnknownFamily);
        if (!double.IsFinite(mountingHeightMetres) || mountingHeightMetres < Device.MinMountingHeightMetres || mountingHeightMetres > Device.MaxMountingHeightMetres)
            return Result.Error<FootprintViewModel>(string.Create(CultureInfo.InvariantCulture,
                $"The mounting height is from {Device.MinMountingHeightMetres} to {Device.MaxMountingHeightMetres} m."));
        return new Result<FootprintViewModel>(View(CoverageFootprint.Assumed(parsed, mountingHeightMetres)));
    }

    #endregion

    #region Changes

    public Task<Result<DeviceCredentialViewModel>> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default) =>
        GuardedAsync(async () =>
        {
            if (!TryKind<DeviceFamily>(request.Family, out var family))
                return Result.Error<DeviceCredentialViewModel>(DeviceErrors.UnknownFamily);
            if (Kinds(request.Transport, request.Dialect, request.ClockSource) is { } kindError)
                return Result.Error<DeviceCredentialViewModel>(kindError);
            var level = await ScopedLevelAsync(request.Placement.LevelId, ct);
            if (level is null)
                return Result.Error<DeviceCredentialViewModel>(TopologyErrors.NotFound);

            // One registration at a time per site: the code check and the insert cannot interleave.
            await LockSiteAsync(level.SiteCode, ct);
            if (await Query<Device>().AnyAsync(d => d.SiteCode == level.SiteCode && d.Code == request.Code, ct))
                return Result.Error<DeviceCredentialViewModel>(TopologyErrors.Duplicate);

            var footprint = Footprint(family, request.Placement);
            if (await CoverageErrorAsync(level, request.Placement, footprint, ct) is { } coverage)
                return Result.Error<DeviceCredentialViewModel>(coverage);
            var device = new Device(request.Code, family, request.Model, Parse<DeviceTransport>(request.Transport), Parse<DeviceDialect>(request.Dialect),
                Parse<ClockSource>(request.ClockSource), level, request.Placement.X, request.Placement.Y, request.Placement.MountingHeightMetres,
                request.Placement.OrientationDegrees, footprint, request.Placement.QueueZoneName, UtcNow);

            var issued = await NewCredentialAsync(ct);
            device.IssueCredential(issued.Prefix, issued.Hash, UtcNow);
            await SaveAsync(device, ct);
            await AuditAsync("Registered", device, null, ct);
            return new Result<DeviceCredentialViewModel>(new DeviceCredentialViewModel(View(device), issued.Credential));
        });

    public Task<Result<DeviceViewModel>> UpdateAsync(Guid id, UpdateDeviceRequest request, CancellationToken ct = default) =>
        ChangeAsync(id, ct, async device =>
        {
            if (Kinds(request.Transport, request.Dialect, request.ClockSource) is { } kindError)
                return Result.Error<DeviceViewModel>(kindError);
            var before = Summary(device);
            device.UpdateDetails(request.Model, Parse<DeviceTransport>(request.Transport), Parse<DeviceDialect>(request.Dialect), Parse<ClockSource>(request.ClockSource), UtcNow);
            if (before == Summary(device))
                return new Result<DeviceViewModel>(View(device));
            await UpdateAsync(device, ct);
            await AuditAsync("Updated", device, before, ct);
            return new Result<DeviceViewModel>(View(device));
        });

    public Task<Result<DeviceViewModel>> MoveAsync(Guid id, DevicePlacement request, CancellationToken ct = default) =>
        ChangeAsync(id, ct, async device =>
        {
            var level = await ScopedLevelAsync(request.LevelId, ct);
            if (level is null || level.SiteCode != device.SiteCode)
                return Result.Error<DeviceViewModel>(TopologyErrors.NotFound);

            var footprint = Footprint(device.Family, request);
            if (await CoverageErrorAsync(level, request, footprint, ct) is { } coverage)
                return Result.Error<DeviceViewModel>(coverage);

            var before = Summary(device);
            var stateBefore = device.State;
            device.Move(level, request.X, request.Y, request.MountingHeightMetres, request.OrientationDegrees, footprint, request.QueueZoneName, UtcNow);
            if (before == Summary(device) && stateBefore == device.State)
                return new Result<DeviceViewModel>(View(device));

            await UpdateAsync(device, ct);
            await AuditAsync("Moved", device, before, ct);
            return new Result<DeviceViewModel>(View(device));
        });

    public Task<Result<DeviceCredentialViewModel>> RotateCredentialAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, ct, async device =>
        {
            var before = Summary(device);
            var issued = await NewCredentialAsync(ct);
            device.IssueCredential(issued.Prefix, issued.Hash, UtcNow);
            await UpdateAsync(device, ct);
            await AuditAsync("CredentialRotated", device, before, ct);
            return new Result<DeviceCredentialViewModel>(new DeviceCredentialViewModel(View(device), issued.Credential));
        });

    public Task<Result<CalibrationViewModel>> RecordCalibrationAsync(Guid id, RecordCalibrationRequest request, CancellationToken ct = default) =>
        ChangeAsync(id, ct, async device =>
        {
            if (!TryKind<CalibrationMethod>(request.Method, out var method))
                return Result.Error<CalibrationViewModel>(DeviceErrors.UnknownMethod);
            var before = Summary(device);
            // The zone must be live where the device actually looks: published, on its level and reached by its footprint
            // (a device registered against a draft may not reach the geometry that was published). The site lock keeps a
            // publish from changing that geometry while this runs.
            await LockSiteAsync(device.SiteCode, ct);
            var zone = await QueueZoneAsync(device.SiteCode, device.QueueZoneName, ct);
            var published = zone is { Published: true } found && found.Queue.LevelId == device.Level.Id &&
                            found.Process.Any(z => z.LevelId == device.Level.Id && Geometry.Overlaps(device.FootprintRing, z.Points));
            var calibration = device.RecordCalibration(method, request.SampleSize, request.CountingAccuracyPercent, request.WaitTimeErrorMinutes,
                request.ThresholdPercent ?? DeviceCalibration.DefaultThresholdPercent, request.Notes, published, UtcNow);
            await SaveAsync(calibration, ct);
            await UpdateAsync(device, ct);
            EvictAfterCommit();
            await audit.RecordAsync($"{Target}.{(calibration.Passed ? "CalibrationPassed" : "CalibrationFailed")}", Target, device.Id, Name(device), before,
                string.Create(CultureInfo.InvariantCulture,
                    $"{Summary(device)}; calibration={calibration.Id}; method={calibration.Method}; sample={calibration.SampleSize}; accuracy={calibration.CountingAccuracyPercent}; " +
                    $"waitError={calibration.WaitTimeErrorMinutes}; threshold={calibration.ThresholdPercent}; passed={calibration.Passed}"), ct);
            return new Result<CalibrationViewModel>(View(calibration, device));
        });

    public Task<Result<DeviceViewModel>> RetireAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, ct, async device =>
        {
            var before = Summary(device);
            device.Retire(UtcNow);
            await UpdateAsync(device, ct);
            await AuditAsync("Retired", device, before, ct);
            return new Result<DeviceViewModel>(View(device));
        });

    public Task<Result<DeviceViewModel>> SetAccessAsync(Guid id, SetDeviceAccessRequest request, CancellationToken ct = default) =>
        ChangeAsync(id, ct, async device =>
        {
            var before = Summary(device);
            device.SetAccess(request.AllowedSources, request.ClientCertificateSha256, UtcNow);
            if (before == Summary(device))
                return new Result<DeviceViewModel>(View(device));
            await UpdateAsync(device, ct);
            await AuditAsync("AccessChanged", device, before, ct);
            return new Result<DeviceViewModel>(View(device));
        });

    public Task<Result<bool>> RemoveAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, ct, async device =>
        {
            if (device.State != DeviceState.Commissioning || device.Calibrations.Count > 0)
                return Result.Error<bool>(DeviceErrors.NotRemovable);
            var before = Summary(device);
            device.Remove(CurrentUser.UserName, UtcNow);
            await UpdateAsync(device, ct);
            EvictAfterCommit();
            await audit.RecordAsync($"{Target}.Removed", Target, device.Id, Name(device), before, null, ct);
            return new Result<bool>(true);
        });

    #endregion

    #region Helpers

    /// <summary>Loads the device under its row lock, within the caller's sites; retired devices refuse every change.</summary>
    private Task<Result<T>> ChangeAsync<T>(Guid id, CancellationToken ct, Func<Device, Task<Result<T>>> change) =>
        GuardedAsync(async () =>
        {
            if (id == Guid.Empty)
                return Result.Error<T>(TopologyErrors.NotFound);
            await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM device WHERE id = :id FOR UPDATE""", new Dictionary<string, object> { ["id"] = id }, ct);
            var device = await ScopedAsync(id, ct);
            if (device is null)
                return Result.Error<T>(TopologyErrors.NotFound);
            if (device.State == DeviceState.Retired)
                return Result.Error<T>(DeviceErrors.Retired);
            return await change(device);
        });

    private async Task<Device> ScopedAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return null;
        var device = await GetAsync<Device>(id, ct);
        return device is null || device.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(device.SiteCode) ? null : device;
    }

    private async Task<Level> ScopedLevelAsync(Guid? id, CancellationToken ct)
    {
        if (id is not { } levelId || levelId == Guid.Empty)
            return null;
        var level = await GetAsync<Level>(levelId, ct);
        return level is null || level.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(level.SiteCode) ? null : level;
    }

    /// <summary>A credential whose prefix no live device uses (48 random bits make a clash vanishingly rare; this makes it impossible).</summary>
    private async Task<DeviceCredentials.Issued> NewCredentialAsync(CancellationToken ct)
    {
        while (true)
        {
            var issued = DeviceCredentials.New();
            if (!await Query<Device>().AnyAsync(d => d.CredentialPrefix == issued.Prefix && d.CredentialHash != null, ct))
                return issued;
        }
    }

    private Task LockSiteAsync(string siteCode, CancellationToken ct) =>
        ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM site WHERE code = :code FOR UPDATE""", new Dictionary<string, object> { ["code"] = siteCode }, ct);

    /// <summary>
    /// The queue zone of that name in the site's published profile, else in its draft, with the zones that hang off it
    /// (overflow, service, staff). Null when neither has a queue zone of that name.
    /// </summary>
    private async Task<(Zone Queue, IReadOnlyList<Zone> Process, bool Published)?> QueueZoneAsync(string siteCode, string name, CancellationToken ct)
    {
        var profiles = await Query<ZoneProfile>()
            .Where(p => p.SiteCode == siteCode && (p.Status == ZoneProfileStatus.Published || p.Status == ZoneProfileStatus.Draft))
            .ToListAsync(ct);
        foreach (var profile in profiles.OrderBy(p => p.Status == ZoneProfileStatus.Published ? 0 : 1))
        {
            var queue = profile.Zones.FirstOrDefault(z => z.Kind == ZoneKind.Queue && string.Equals(z.Name, name, StringComparison.Ordinal));
            if (queue is not null)
                return (queue, [queue, .. profile.Zones.Where(z => z.QueueZone == queue)], profile.Status == ZoneProfileStatus.Published);
        }

        return null;
    }

    /// <summary>The zone must exist, lie on the device's level and be reached by its footprint (the prototype's rule).</summary>
    private async Task<string> CoverageErrorAsync(Level level, DevicePlacement placement, CoverageFootprint footprint, CancellationToken ct)
    {
        var zone = await QueueZoneAsync(level.SiteCode, placement.QueueZoneName?.Trim(), ct);
        if (zone is not { } found)
            return DeviceErrors.ZoneNotFound;
        if (found.Queue.LevelId != level.Id)
            return DeviceErrors.ZoneOnAnotherLevel;
        if (!double.IsFinite(placement.X) || !double.IsFinite(placement.Y) || !double.IsFinite(placement.OrientationDegrees))
            return null; // the entity refuses these with its own message
        var ring = footprint.Ring(new FloorPoint(placement.X, placement.Y), placement.OrientationDegrees);
        return found.Process.Any(z => z.LevelId == level.Id && Geometry.Overlaps(ring, z.Points)) ? null : DeviceErrors.OutOfReach;
    }

    private static CoverageFootprint Footprint(DeviceFamily family, DevicePlacement placement) =>
        placement.FootprintLengthMetres is null && placement.FootprintWidthMetres is null && placement.FootprintRadiusMetres is null
            ? CoverageFootprint.Assumed(family, placement.MountingHeightMetres)
            : CoverageFootprint.FromVendor(placement.FootprintLengthMetres, placement.FootprintWidthMetres, placement.FootprintRadiusMetres);

    private static string Kinds(string transport, string dialect, string clockSource) =>
        !TryKind<DeviceTransport>(transport, out _) ? DeviceErrors.UnknownTransport
        : !TryKind<DeviceDialect>(dialect, out _) ? DeviceErrors.UnknownDialect
        : !TryKind<ClockSource>(clockSource, out _) ? DeviceErrors.UnknownClockSource
        : null;

    private static TEnum Parse<TEnum>(string value) where TEnum : struct, Enum => TryKind<TEnum>(value, out var kind) ? kind : throw new ArgumentException($"Unknown {typeof(TEnum).Name}.");

    /// <summary>Kinds by name only; numbers and unknown names are refused.</summary>
    private static bool TryKind<TEnum>(string value, out TEnum kind) where TEnum : struct, Enum
    {
        kind = default;
        return !string.IsNullOrEmpty(value) && char.IsLetter(value[0]) && Enum.TryParse(value, ignoreCase: false, out kind) && Enum.IsDefined(kind);
    }

    /// <summary>
    /// Domain rule violations become 400 answers with the entity's message (never a raw exception text otherwise). An
    /// answer with errors never commits: the unit of work commits whatever a command touched (the row lock promises a
    /// commit, and NHibernate flushes changed entities), so a refused request rolls back explicitly.
    /// </summary>
    private async Task<Result<T>> GuardedAsync<T>(Func<Task<Result<T>>> work)
    {
        Result<T> result;
        try
        {
            result = await work();
        }
        catch (ArgumentException e)
        {
            result = Result.Error<T>(e.Message.Split(" (Parameter", 2)[0]);
        }
        catch (InvalidOperationException e) when (e.Source == typeof(Device).Assembly.GetName().Name)
        {
            result = Result.Error<T>(e.Message);
        }

        if (result.HasErrors)
            UnitOfWork.PromiseNotToCommit();
        return result;
    }

    private Task AuditAsync(string verb, Device device, string before, CancellationToken ct)
    {
        EvictAfterCommit();
        return audit.RecordAsync($"{Target}.{verb}", Target, device.Id, Name(device), before, Summary(device), ct);
    }

    /// <summary>Ingest authenticates from a cached copy of the device (SvcDeviceGateway); a committed change evicts it on every host (backplane).</summary>
    private void EvictAfterCommit() => RegisterPostCommitAction(() => cache.RemoveByTagAsync(CacheTag).AsTask());

    private static string Name(Device d) => $"{d.SiteCode}/{d.Code}";

    /// <summary>
    /// The device as audit text. Free text (model, zone name) is escaped, so a model like "X; state=Online" cannot pass
    /// for another field to someone reading the audit trail.
    /// </summary>
    private static string Summary(Device d) => string.Create(CultureInfo.InvariantCulture,
        $"code={d.Code}; family={d.Family}; model={Escape(d.Model)}; transport={d.Transport}; dialect={d.Dialect}; clock={d.ClockSource}; state={d.State}; " +
        $"level={d.Level?.Id}; x={d.X}; y={d.Y}; height={d.MountingHeightMetres}; orientation={d.OrientationDegrees}; footprint={d.Footprint.Text} ({d.FootprintSource}); " +
        $"zone={Escape(d.QueueZoneName)}; credential={d.CredentialPrefix}; credentialIssued={d.CredentialIssuedOn:O}; sources={d.AllowedSources}; certificate={d.ClientCertificateSha256}");

    private static string Escape(string text) => text?.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace("=", "\\=", StringComparison.Ordinal);

    private static FootprintViewModel View(CoverageFootprint f) => new(f.LengthMetres, f.WidthMetres, f.RadiusMetres, f.Source.ToString(), f.Text, f.Note);

    private static DeviceViewModel View(Device d)
    {
        var last = d.Calibrations.OrderByDescending(c => c.PerformedOn).FirstOrDefault();
        return new DeviceViewModel(d.Id.GetValueOrDefault(), d.Code, d.SiteCode, d.Family.ToString(), d.Model, d.Transport.ToString(), d.Dialect.ToString(),
            d.ClockSource.ToString(), d.State.ToString(), d.Level?.Id ?? Guid.Empty, d.X, d.Y, d.MountingHeightMetres, d.OrientationDegrees, View(d.Footprint),
            d.QueueZoneName, d.CredentialPrefix, d.CredentialIssuedOn, last?.PerformedOn, last?.Passed, d.RetiredOn, d.CreatedOn,
            string.IsNullOrEmpty(d.AllowedSources) ? [] : d.AllowedSources.Split(','), d.ClientCertificateSha256);
    }

    private static CalibrationViewModel View(DeviceCalibration c, Device d) => new(
        c.Id.GetValueOrDefault(), d.Id.GetValueOrDefault(), c.Method.ToString(), c.SampleSize, c.CountingAccuracyPercent, c.WaitTimeErrorMinutes,
        c.ThresholdPercent, c.Passed, c.Notes, c.PerformedOn, c.CreatedBy, d.State.ToString());

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    #endregion
}
