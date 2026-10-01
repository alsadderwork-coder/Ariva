using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Security;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Administration;

/// <summary>
/// The audit trail, read-only, newest first (ARV-011). There is no write path here or anywhere in the API. A
/// site-limited administrator reads only entries about accounts it may administer and sites it can access (ARV-012).
/// </summary>
internal sealed class SvcAuditEntries(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAuditEntries
{
    public async Task<Result<AuditEntryViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await (await VisibleAsync(ct)).Where(e => e.Id == id).FirstOrDefaultAsync(ct);
        return entry is null ? Result.Error<AuditEntryViewModel>(AdministrationErrors.NotFound) : new Result<AuditEntryViewModel>(View(entry));
    }

    /// <summary>Every entry for an all-sites caller; otherwise entries about administrable accounts and own sites.</summary>
    private async Task<IQueryable<AuditEntry>> VisibleAsync(CancellationToken ct)
    {
        var access = await siteScope.GetAsync(ct);
        var entries = QueryAsNoTracking<AuditEntry>();
        if (access.AllSites)
            return entries;
        var codes = access.SiteCodes.ToList();
        var users = AdministrationGuards.Administrable(QueryAsNoTracking<User>().Where(u => !u.IsBreakGlass), access).Select(u => u.Id);
        return entries.Where(e =>
            (e.TargetType == AuditActions.UserTarget && users.Contains(e.TargetId)) ||
            (e.TargetType == AuditActions.SiteTarget && codes.Contains(e.TargetName)));
    }

    public async Task<Result<PageViewModel<AuditEntryViewModel>>> SearchAsync(AuditEntryCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new AuditEntryCriteria();
        if ((criteria.FromDate is { } from && from.Kind != DateTimeKind.Utc) || (criteria.ToDate is { } to && to.Kind != DateTimeKind.Utc) ||
            (criteria.FromDate is { } f && criteria.ToDate is { } t && f > t))
        {
            return Result.Error<PageViewModel<AuditEntryViewModel>>(AdministrationErrors.InvalidCriteria);
        }

        var query = await VisibleAsync(ct);
        if (criteria.ActorId is { } actor)
            query = query.Where(e => e.ActorId == actor);
        if (criteria.TargetId is { } target)
            query = query.Where(e => e.TargetId == target);
        if (!string.IsNullOrWhiteSpace(criteria.Action))
            query = query.Where(e => e.Action == criteria.Action);
        if (criteria.FromDate is { } start)
            query = query.Where(e => e.OccurredOn >= start);
        if (criteria.ToDate is { } end)
            query = query.Where(e => e.OccurredOn <= end);

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await query.CountAsync(ct);
        var entries = await query.OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id)
            .Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<AuditEntryViewModel>>(new PageViewModel<AuditEntryViewModel>(entries.Select(View).ToList(), total, pageIndex, pageSize));
    }

    private static AuditEntryViewModel View(AuditEntry e) => new(
        e.Id.Value, e.OccurredOn, e.ActorId, e.ActorName, e.Action, e.TargetType, e.TargetId, e.TargetName,
        e.BeforeSummary, e.AfterSummary, e.IpAddress, e.TraceId);
}
