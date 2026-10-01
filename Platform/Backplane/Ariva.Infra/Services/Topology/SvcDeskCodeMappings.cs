using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Services.Topology;

/// <summary>
/// Desk code mappings (ARV-015). A code is unique per system and site among live mappings, a desk has one live mapping
/// per system, and the entity checks that the system fits the desk kind. Site-scoped and audited like the topology;
/// resolution for feed consumers is cached under the topology tag.
/// </summary>
internal sealed class SvcDeskCodeMappings(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    IFusionCache cache) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDeskCodeMappings
{
    public async Task<Result<PageViewModel<DeskCodeMappingViewModel>>> SearchAsync(DeskCodeMappingCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new DeskCodeMappingCriteria();
        ExternalSystem? system = null;
        if (criteria.System is not null)
        {
            if (!TryParseSystem(criteria.System, out var parsed))
                return Result.Error<PageViewModel<DeskCodeMappingViewModel>>(TopologyErrors.InvalidCriteria);
            system = parsed;
        }

        var query = QueryAsNoTracking<DeskCodeMapping>().WithinSites(await siteScope.GetAsync(ct));
        if (system is { } s)
            query = query.Where(m => m.System == s);
        if (!string.IsNullOrWhiteSpace(criteria.SiteCode))
            query = query.Where(m => m.SiteCode == criteria.SiteCode);
        if (criteria.DeskId is { } deskId)
            query = query.Where(m => m.Desk.Id == deskId);
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.Trim().ToUpperInvariant();
            query = query.Where(m => m.ExternalCode.Contains(text));
        }

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(m => m.SiteCode).ThenBy(m => m.System).ThenBy(m => m.ExternalCode)
            .Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<DeskCodeMappingViewModel>>(new PageViewModel<DeskCodeMappingViewModel>(rows.Select(View).ToList(), total, pageIndex, pageSize));
    }

    public async Task<Result<DeskCodeMappingViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var mapping = await VisibleAsync(id, ct);
        return mapping is null ? Result.Error<DeskCodeMappingViewModel>(TopologyErrors.NotFound) : new Result<DeskCodeMappingViewModel>(View(mapping));
    }

    public async Task<Result<DeskCodeMappingViewModel>> CreateAsync(CreateDeskCodeMappingRequest request, CancellationToken ct = default)
    {
        if (request is null || !TryParseSystem(request.System, out var system))
            return Result.Error<DeskCodeMappingViewModel>(TopologyErrors.UnknownKind);
        var desk = await VisibleDeskAsync(request.DeskId.GetValueOrDefault(), ct);
        if (desk is null)
            return Result.Error<DeskCodeMappingViewModel>(TopologyErrors.NotFound);
        var code = DeskCodeMapping.NormalizeCode(request.ExternalCode);
        if (code is null)
            return Result.Error<DeskCodeMappingViewModel>("An external code is 1 to 32 letters, digits or . _ / - characters.");
        if (await Query<DeskCodeMapping>().AnyAsync(m => m.System == system && ((m.SiteCode == desk.SiteCode && m.ExternalCode == code) || m.Desk.Id == desk.Id), ct))
            return Result.Error<DeskCodeMappingViewModel>(TopologyErrors.Duplicate);

        try
        {
            var mapping = new DeskCodeMapping(system, code, desk);
            await SaveAsync(mapping, ct);
            await RecordAsync("Created", mapping, null, ct);
            return new Result<DeskCodeMappingViewModel>(View(mapping));
        }
        catch (InvalidOperationException e)
        {
            return Result.Error<DeskCodeMappingViewModel>(e.Message);
        }
    }

    public async Task<Result<DeskCodeMappingViewModel>> UpdateAsync(Guid id, UpdateDeskCodeMappingRequest request, CancellationToken ct = default)
    {
        var mapping = await VisibleAsync(id, ct);
        var desk = mapping is null ? null : await VisibleDeskAsync(request?.DeskId ?? Guid.Empty, ct);
        if (mapping is null || desk is null)
            return Result.Error<DeskCodeMappingViewModel>(TopologyErrors.NotFound);
        if (desk.Id != mapping.Desk.Id && await Query<DeskCodeMapping>().AnyAsync(m => m.System == mapping.System && m.Desk.Id == desk.Id, ct))
            return Result.Error<DeskCodeMappingViewModel>(TopologyErrors.Duplicate);

        var before = Summary(mapping);
        try
        {
            mapping.Assign(desk);
        }
        catch (InvalidOperationException e)
        {
            return Result.Error<DeskCodeMappingViewModel>(e.Message);
        }

        await UpdateAsync(mapping, ct);
        await RecordAsync("Updated", mapping, before, ct);
        return new Result<DeskCodeMappingViewModel>(View(mapping));
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var mapping = await VisibleAsync(id, ct);
        if (mapping is null)
            return Result.Error<bool>(TopologyErrors.NotFound);

        mapping.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(mapping, ct);
        await audit.RecordAsync("DeskCodeMapping.Deleted", "DeskCodeMapping", mapping.Id, Name(mapping), Summary(mapping), null, ct);
        RegisterPostCommitAction(() => cache.RemoveByTagAsync(SvcTopology.CacheTag).AsTask());
        return new Result<bool>(true);
    }

    public async Task<Guid?> ResolveAsync(ExternalSystem system, string siteCode, string externalCode, CancellationToken ct = default)
    {
        var code = DeskCodeMapping.NormalizeCode(externalCode);
        if (code is null || siteCode is null)
            return null;
        return await cache.GetOrSetAsync<Guid?>(
            $"topology:mapping:{system}:{siteCode}:{code}",
            async (_, token) => await QueryAsNoTracking<DeskCodeMapping>()
                .Where(m => m.System == system && m.SiteCode == siteCode && m.ExternalCode == code && m.Desk.DeletedOn == null)
                .Select(m => m.Desk.Id)
                .FirstOrDefaultAsync(token),
            options => options.SetDuration(TimeSpan.FromMinutes(5)),
            tags: [SvcTopology.CacheTag],
            token: ct);
    }

    private async Task<DeskCodeMapping> VisibleAsync(Guid id, CancellationToken ct)
    {
        var mapping = id == Guid.Empty ? null : await GetAsync<DeskCodeMapping>(id, ct);
        return mapping is null || mapping.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(mapping.SiteCode) ? null : mapping;
    }

    private async Task<Desk> VisibleDeskAsync(Guid id, CancellationToken ct)
    {
        var desk = id == Guid.Empty ? null : await GetAsync<Desk>(id, ct);
        return desk is null || desk.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(desk.SiteCode) ? null : desk;
    }

    private async Task RecordAsync(string verb, DeskCodeMapping mapping, string before, CancellationToken ct)
    {
        await audit.RecordAsync($"DeskCodeMapping.{verb}", "DeskCodeMapping", mapping.Id, Name(mapping), before, Summary(mapping), ct);
        RegisterPostCommitAction(() => cache.RemoveByTagAsync(SvcTopology.CacheTag).AsTask());
    }

    /// <summary>Systems by name only (Aman, Aodb); numbers are refused.</summary>
    private static bool TryParseSystem(string value, out ExternalSystem system)
    {
        system = default;
        return !string.IsNullOrEmpty(value) && char.IsLetter(value[0]) && Enum.TryParse(value, ignoreCase: false, out system) && Enum.IsDefined(system);
    }

    private static string Name(DeskCodeMapping m) => $"{m.System}:{m.SiteCode}:{m.ExternalCode}";

    private static string Summary(DeskCodeMapping m) => $"system={m.System}; code={m.ExternalCode}; desk={m.Desk.Code}; site={m.SiteCode}";

    private static DeskCodeMappingViewModel View(DeskCodeMapping m) =>
        new(m.Id.Value, m.System.ToString(), m.ExternalCode, m.Desk.Id.Value, m.Desk.Code, m.SiteCode, m.CreatedOn);
}
