using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Administration;

/// <summary>
/// Sites (ARV-012). Reads return only the caller's sites, and a site outside them answers exactly like one that does
/// not exist (CWE-863, no existence oracle). Creating and renaming are audited.
/// </summary>
internal sealed class SvcSites(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope scope, AuditTrail audit)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcSites
{
    public async Task<Result<IReadOnlyList<SiteViewModel>>> ListAsync(CancellationToken ct = default)
    {
        var access = await scope.GetAsync(ct);
        var query = QueryAsNoTracking<Site>();
        if (!access.AllSites)
        {
            var codes = access.SiteCodes.ToList();
            query = query.Where(s => codes.Contains(s.Code));
        }

        var sites = await query.OrderBy(s => s.Code).ToListAsync(ct);
        return new Result<IReadOnlyList<SiteViewModel>>(sites.Select(View).ToList());
    }

    public async Task<Result<SiteViewModel>> GetAsync(string code, CancellationToken ct = default)
    {
        var site = await VisibleAsync(code, ct);
        return site is null ? Result.Error<SiteViewModel>(AdministrationErrors.NotFound) : new Result<SiteViewModel>(View(site));
    }

    public async Task<Result<SiteViewModel>> CreateAsync(CreateSiteRequest request, CancellationToken ct = default)
    {
        // Creating a site is deployment-wide: a site-limited administrator cannot, and so never learns which codes exist.
        if (!(await scope.GetAsync(ct)).AllSites)
            return Result.Error<SiteViewModel>(AdministrationErrors.BeyondOwnSites);
        if (request is null || !Site.IsValidCode(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            return Result.Error<SiteViewModel>(AdministrationErrors.UnknownSite);
        if (await Query<Site>().AnyAsync(s => s.Code == request.Code, ct))
            return Result.Error<SiteViewModel>(AdministrationErrors.SiteTaken);

        var site = new Site(request.Code, request.Name);
        await SaveAsync(site, ct);
        await audit.RecordAsync(AuditActions.SiteCreated, AuditActions.SiteTarget, site.Id, site.Code, null, $"code={site.Code}; name={site.Name}", ct);
        return new Result<SiteViewModel>(View(site));
    }

    public async Task<Result<SiteViewModel>> UpdateAsync(string code, UpdateSiteRequest request, CancellationToken ct = default)
    {
        var site = await VisibleAsync(code, ct);
        if (site is null)
            return Result.Error<SiteViewModel>(AdministrationErrors.NotFound);

        var before = $"code={site.Code}; name={site.Name}";
        site.Rename(request?.Name);
        await UpdateAsync(site, ct);
        await audit.RecordAsync(AuditActions.SiteUpdated, AuditActions.SiteTarget, site.Id, site.Code, before, $"code={site.Code}; name={site.Name}", ct);
        return new Result<SiteViewModel>(View(site));
    }

    private async Task<Site> VisibleAsync(string code, CancellationToken ct)
    {
        if (!Site.IsValidCode(code) || !(await scope.GetAsync(ct)).Allows(code))
            return null;
        return await Query<Site>().FirstOrDefaultAsync(s => s.Code == code, ct);
    }

    private static SiteViewModel View(Site site) => new(site.Code, site.Name, site.CreatedOn);
}
