using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// Passenger displays (ARV-058). A display is created in one of the caller's sites and stays there with its code; reads
/// and writes outside the caller's sites answer like a display that does not exist (CWE-863). Its entries must be queue
/// zones of the site's published profile. The player's credential is made here, shown once and kept as a prefix and a
/// hash; every change is audited without it.
/// </summary>
internal sealed class SvcDisplays(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, AuditTrail audit)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcDisplays
{
    private const string Target = "Display";
    public const int MaxDisplaysPerSite = 200;

    public async Task<Result<IReadOnlyList<DisplayViewModel>>> SearchAsync(string siteCode, CancellationToken ct = default)
    {
        var query = QueryAsNoTracking<Display>().WithinSites(await siteScope.GetAsync(ct)).Where(d => d.DeletedOn == null);
        if (!string.IsNullOrWhiteSpace(siteCode))
            query = query.Where(d => d.SiteCode == siteCode);
        var displays = await query.OrderBy(d => d.SiteCode).ThenBy(d => d.Code).Take(MaxDisplaysPerSite * 4).ToListAsync(ct);
        return new Result<IReadOnlyList<DisplayViewModel>>(displays.Select(View).ToList());
    }

    public async Task<Result<DisplayViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var display = await VisibleAsync(id, ct);
        return display is null ? Result.Error<DisplayViewModel>(TopologyErrors.NotFound) : new Result<DisplayViewModel>(View(display));
    }

    public async Task<Result<DisplayIssuedViewModel>> CreateAsync(DisplayRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SiteCode) || !(await siteScope.GetAsync(ct)).Allows(request.SiteCode) ||
            !await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == request.SiteCode, ct))
            return Result.Error<DisplayIssuedViewModel>(TopologyErrors.UnknownSite);
        if (!TopologyCodes.IsValid(request.Code))
            return Result.Error<DisplayIssuedViewModel>(DisplayErrors.InvalidCode);
        var (values, problems) = await CheckAsync(request.SiteCode, request, ct);
        if (problems.Count > 0)
            return Result.Error<DisplayIssuedViewModel>(problems);
        if (await QueryAsNoTracking<Display>().AnyAsync(d => d.Code == request.Code && d.DeletedOn == null, ct))
            return Result.Error<DisplayIssuedViewModel>(DisplayErrors.DuplicateCode);
        if (await QueryAsNoTracking<Display>().CountAsync(d => d.SiteCode == request.SiteCode && d.DeletedOn == null, ct) >= MaxDisplaysPerSite)
            return Result.Error<DisplayIssuedViewModel>(DisplayErrors.TooMany);

        var display = new Display(request.SiteCode, request.Code, values);
        var issued = DisplayCredentials.New();
        display.SetCredential(issued.Prefix, issued.Hash, UtcNow);
        await SaveAsync(display, ct);
        await audit.RecordAsync($"{Target}.Created", Target, display.Id, display.Code, null, display.AuditSummary(), ct);
        return new Result<DisplayIssuedViewModel>(new DisplayIssuedViewModel(View(display), issued.Credential));
    }

    public async Task<Result<DisplayViewModel>> UpdateAsync(Guid id, DisplayRequest request, CancellationToken ct = default)
    {
        var display = await VisibleAsync(id, ct);
        if (display is null || request is null)
            return Result.Error<DisplayViewModel>(TopologyErrors.NotFound);
        if ((!string.IsNullOrWhiteSpace(request.SiteCode) && !string.Equals(request.SiteCode, display.SiteCode, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(request.Code) && !string.Equals(request.Code, display.Code, StringComparison.Ordinal)))
            return Result.Error<DisplayViewModel>(DisplayErrors.StaysInSite);
        var (values, problems) = await CheckAsync(display.SiteCode, request, ct);
        if (problems.Count > 0)
            return Result.Error<DisplayViewModel>(problems);

        var before = display.AuditSummary();
        display.Set(values);
        await UpdateAsync(display, ct);
        await audit.RecordAsync($"{Target}.Updated", Target, display.Id, display.Code, before, display.AuditSummary(), ct);
        return new Result<DisplayViewModel>(View(display));
    }

    public async Task<Result<DisplayIssuedViewModel>> NewCredentialAsync(Guid id, CancellationToken ct = default)
    {
        var display = await VisibleAsync(id, ct);
        if (display is null)
            return Result.Error<DisplayIssuedViewModel>(TopologyErrors.NotFound);
        var before = display.AuditSummary();
        var issued = DisplayCredentials.New();
        display.SetCredential(issued.Prefix, issued.Hash, UtcNow);
        await UpdateAsync(display, ct);
        await audit.RecordAsync($"{Target}.CredentialIssued", Target, display.Id, display.Code, before, display.AuditSummary(), ct);
        return new Result<DisplayIssuedViewModel>(new DisplayIssuedViewModel(View(display), issued.Credential));
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var display = await VisibleAsync(id, ct);
        if (display is null)
            return Result.Error<bool>(TopologyErrors.NotFound);
        display.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(display, ct);
        await audit.RecordAsync($"{Target}.Deleted", Target, display.Id, display.Code, display.AuditSummary(), null, ct);
        return new Result<bool>(true);
    }

    private async Task<(DisplayValues Values, IReadOnlyList<string> Problems)> CheckAsync(string siteCode, DisplayRequest request, CancellationToken ct)
    {
        var problems = new List<string>();
        if (!Enum.TryParse<DisplayOrientation>(request.Orientation, ignoreCase: false, out var orientation) ||
            !string.Equals(orientation.ToString(), request.Orientation, StringComparison.Ordinal))
        {
            problems.Add("Orientation is Landscape or Portrait.");
            return (null, problems);
        }

        var values = new DisplayValues(request.Name, request.Location, orientation, request.Languages ?? [], request.BandMinutes, request.HysteresisMinutes,
            request.StaleSeconds, (request.Entries ?? []).Select(e => e is null ? null : new DisplayEntry(e.Zone, e.Labels)).ToList(),
            request.Fallback, request.Enabled);
        problems.AddRange(values.Problems());
        if (problems.Count > 0)
            return (values, problems);

        var zones = values.Entries.Select(e => e.Zone.Trim()).ToList();
        var known = await Query<Zone>()
            .Where(z => z.Profile.SiteCode == siteCode && z.Profile.Status == ZoneProfileStatus.Published && zones.Contains(z.Name) && z.Kind == ZoneKind.Queue)
            .Select(z => z.Name)
            .ToListAsync(ct);
        if (known.Distinct(StringComparer.Ordinal).Count() != zones.Count)
            problems.Add(DisplayErrors.UnknownZones);
        return (values, problems);
    }

    private async Task<Display> VisibleAsync(Guid id, CancellationToken ct)
    {
        var display = id == Guid.Empty ? null : await GetAsync<Display>(id, ct);
        return display is null || display.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(display.SiteCode) ? null : display;
    }

    internal static DisplayViewModel View(Display d) =>
        new(d.Id.GetValueOrDefault(), d.SiteCode, d.Code, d.Name, d.Location, d.Orientation.ToString(), d.LanguageList, d.BandMinutes, d.HysteresisMinutes,
            d.StaleSeconds, d.EntryList.Select(e => new DisplayEntryViewModel(e.Zone, e.Labels)).ToList(), d.FallbackMap, d.Enabled, d.CredentialPrefix,
            d.CredentialIssuedOn, d.CreatedOn, d.ModifiedOn);
}
