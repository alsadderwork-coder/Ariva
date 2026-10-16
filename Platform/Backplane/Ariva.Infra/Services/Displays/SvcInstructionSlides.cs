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
/// Instruction slides of the passenger displays (ARV-141). A slide is created in one of the caller's sites, at the end of
/// its order, and stays there; reads and writes outside the caller's sites answer like a slide that does not exist
/// (CWE-863). Its zones must be queue zones of the site's published profile and its displays live displays of the site.
/// Its words are plain text, checked by the entity and only ever shown as text. Every change is audited.
/// </summary>
internal sealed class SvcInstructionSlides(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, AuditTrail audit)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcInstructionSlides
{
    private const string Target = "InstructionSlide";

    public async Task<Result<IReadOnlyList<InstructionSlideViewModel>>> SearchAsync(string siteCode, CancellationToken ct = default)
    {
        var query = QueryAsNoTracking<InstructionSlide>().WithinSites(await siteScope.GetAsync(ct));
        if (!string.IsNullOrWhiteSpace(siteCode))
            query = query.Where(s => s.SiteCode == siteCode);
        var slides = await query.OrderBy(s => s.SiteCode).ThenBy(s => s.Position).Take(InstructionSlide.MaxSlidesPerSite * 4).ToListAsync(ct);
        return new Result<IReadOnlyList<InstructionSlideViewModel>>(slides.Select(View).ToList());
    }

    public async Task<Result<InstructionSlideViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var slide = await VisibleAsync(id, ct);
        return slide is null ? Result.Error<InstructionSlideViewModel>(TopologyErrors.NotFound) : new Result<InstructionSlideViewModel>(View(slide));
    }

    public async Task<Result<InstructionSlideViewModel>> CreateAsync(InstructionSlideRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SiteCode) || !(await siteScope.GetAsync(ct)).Allows(request.SiteCode) ||
            !await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == request.SiteCode, ct))
            return Result.Error<InstructionSlideViewModel>(TopologyErrors.UnknownSite);
        var (values, problems) = await CheckAsync(request.SiteCode, request, ct);
        if (problems.Count > 0)
            return Result.Error<InstructionSlideViewModel>(problems);
        // One creation or reorder per site at a time, to the commit: the cap holds and each new slide takes its own position.
        await LockSiteAsync(request.SiteCode, ct);
        var count = await QueryAsNoTracking<InstructionSlide>().CountAsync(s => s.SiteCode == request.SiteCode, ct);
        if (count >= InstructionSlide.MaxSlidesPerSite)
            return Result.Error<InstructionSlideViewModel>(InstructionSlideErrors.TooMany);
        var last = count == 0 ? 0 : await QueryAsNoTracking<InstructionSlide>().Where(s => s.SiteCode == request.SiteCode).MaxAsync(s => s.Position, ct);

        var slide = new InstructionSlide(request.SiteCode, last + 1, values);
        await SaveAsync(slide, ct);
        await audit.RecordAsync($"{Target}.Created", Target, slide.Id, Describe(slide), null, slide.AuditSummary(), ct);
        return new Result<InstructionSlideViewModel>(View(slide));
    }

    public async Task<Result<InstructionSlideViewModel>> UpdateAsync(Guid id, InstructionSlideRequest request, CancellationToken ct = default)
    {
        var slide = await VisibleAsync(id, ct);
        if (slide is null || request is null)
            return Result.Error<InstructionSlideViewModel>(TopologyErrors.NotFound);
        if (!string.IsNullOrWhiteSpace(request.SiteCode) && !string.Equals(request.SiteCode, slide.SiteCode, StringComparison.Ordinal))
            return Result.Error<InstructionSlideViewModel>(InstructionSlideErrors.StaysInSite);
        var (values, problems) = await CheckAsync(slide.SiteCode, request, ct);
        if (problems.Count > 0)
            return Result.Error<InstructionSlideViewModel>(problems);

        var before = slide.AuditSummary();
        slide.Set(values);
        await UpdateAsync(slide, ct);
        await audit.RecordAsync($"{Target}.Updated", Target, slide.Id, Describe(slide), before, slide.AuditSummary(), ct);
        return new Result<InstructionSlideViewModel>(View(slide));
    }

    public Task<Result<InstructionSlideViewModel>> RetireAsync(Guid id, CancellationToken ct = default) =>
        ChangeStatusAsync(id, InstructionStatus.Retired, "Retired", ct);

    public Task<Result<InstructionSlideViewModel>> RestoreAsync(Guid id, CancellationToken ct = default) =>
        ChangeStatusAsync(id, InstructionStatus.Active, "Restored", ct);

    public async Task<Result<IReadOnlyList<InstructionSlideViewModel>>> ReorderAsync(string siteCode, InstructionSlideOrderRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) ||
            !await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == siteCode, ct))
            return Result.Error<IReadOnlyList<InstructionSlideViewModel>>(TopologyErrors.UnknownSite);
        var ids = request?.Ids ?? [];
        await LockSiteAsync(siteCode, ct);
        var slides = await Query<InstructionSlide>().Where(s => s.SiteCode == siteCode).ToListAsync(ct);
        if (ids.Count != slides.Count || ids.Distinct().Count() != ids.Count || !slides.All(s => ids.Contains(s.Id.GetValueOrDefault())))
            return Result.Error<IReadOnlyList<InstructionSlideViewModel>>(InstructionSlideErrors.OrderMismatch);

        // Each slide that moves is audited on its own, with its place before and after: a site's whole order would not fit
        // one audit entry.
        var byId = slides.ToDictionary(s => s.Id.GetValueOrDefault());
        for (var index = 0; index < ids.Count; index++)
        {
            var slide = byId[ids[index]];
            if (slide.Position == index + 1)
                continue;
            var from = slide.Position;
            slide.MoveTo(index + 1);
            await UpdateAsync(slide, ct);
            await audit.RecordAsync($"{Target}.Moved", Target, slide.Id, Describe(slide), $"position={from}", $"position={slide.Position}", ct);
        }

        return new Result<IReadOnlyList<InstructionSlideViewModel>>(ids.Select(id => View(byId[id])).ToList());
    }

    private async Task<Result<InstructionSlideViewModel>> ChangeStatusAsync(Guid id, InstructionStatus status, string action, CancellationToken ct)
    {
        var slide = await VisibleAsync(id, ct);
        if (slide is null)
            return Result.Error<InstructionSlideViewModel>(TopologyErrors.NotFound);
        if (slide.Status == status)
            return new Result<InstructionSlideViewModel>(View(slide));
        var before = slide.AuditSummary();
        if (status == InstructionStatus.Retired)
            slide.Retire();
        else
            slide.Restore();
        await UpdateAsync(slide, ct);
        await audit.RecordAsync($"{Target}.{action}", Target, slide.Id, Describe(slide), before, slide.AuditSummary(), ct);
        return new Result<InstructionSlideViewModel>(View(slide));
    }

    private async Task<(InstructionSlideValues Values, IReadOnlyList<string> Problems)> CheckAsync(string siteCode, InstructionSlideRequest request,
        CancellationToken ct)
    {
        var problems = new List<string>();
        if (!Exactly<InstructionScope>(request.Scope, out var scope))
            problems.Add("A slide applies to its Site, a Zone or a Display.");
        if (!Exactly<InstructionIcon>(request.Icon, out var icon))
            problems.Add("The icon is one of the built-in set.");
        if (problems.Count > 0)
            return (null, problems);

        var texts = (request.Texts ?? new Dictionary<string, InstructionTextRequest>())
            .ToDictionary(p => p.Key, p => p.Value is null ? null : new InstructionText(p.Value.Title, p.Value.Body), StringComparer.Ordinal);
        var values = new InstructionSlideValues(scope, request.Targets ?? [], icon, request.DurationSeconds, request.Pinned, texts);
        problems.AddRange(values.Problems());
        if (problems.Count > 0)
            return (values, problems);

        var targets = values.Targets.Select(t => t.Trim()).ToList();
        if (scope == InstructionScope.Zone)
        {
            var known = await Query<Zone>()
                .Where(z => z.Profile.SiteCode == siteCode && z.Profile.Status == ZoneProfileStatus.Published && targets.Contains(z.Name) &&
                            z.Kind == ZoneKind.Queue)
                .Select(z => z.Name)
                .ToListAsync(ct);
            if (known.Distinct(StringComparer.Ordinal).Count() != targets.Count)
                problems.Add(InstructionSlideErrors.UnknownZones);
        }
        else if (scope == InstructionScope.Display)
        {
            var known = await QueryAsNoTracking<Display>()
                .Where(d => d.SiteCode == siteCode && d.DeletedOn == null && targets.Contains(d.Code))
                .Select(d => d.Code)
                .ToListAsync(ct);
            if (known.Distinct(StringComparer.Ordinal).Count() != targets.Count)
                problems.Add(InstructionSlideErrors.UnknownDisplays);
        }

        return (values, problems);
    }

    /// <summary>A transaction-scoped lock on the site's slides (class 141, the site code's hash), released at the commit.</summary>
    private async Task LockSiteAsync(string siteCode, CancellationToken ct)
    {
        await FlushAsync(ct);
        await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM (SELECT pg_advisory_xact_lock(141, hashtext(:site))) l""",
            new Dictionary<string, object> { ["site"] = siteCode }, ct);
    }

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    /// <summary>An enum value by its exact name, as stored (never a number or another casing).</summary>
    private static bool Exactly<TEnum>(string text, out TEnum value) where TEnum : struct, Enum =>
        Enum.TryParse(text, ignoreCase: false, out value) && string.Equals(value.ToString(), text, StringComparison.Ordinal) && Enum.IsDefined(value);

    private async Task<InstructionSlide> VisibleAsync(Guid id, CancellationToken ct)
    {
        var slide = id == Guid.Empty ? null : await GetAsync<InstructionSlide>(id, ct);
        return slide is null || !(await siteScope.GetAsync(ct)).Allows(slide.SiteCode) ? null : slide;
    }

    /// <summary>The audit entry's target name: the site and the slide's place, never its words (they are in the summary).</summary>
    private static string Describe(InstructionSlide slide) => $"{slide.SiteCode}:{slide.Position}";

    internal static InstructionSlideViewModel View(InstructionSlide s) =>
        new(s.Id.GetValueOrDefault(), s.SiteCode, s.Position, s.Scope.ToString(), s.TargetList, s.Icon.ToString(), s.DurationSeconds, s.Pinned,
            s.Status.ToString(), s.TextMap.ToDictionary(p => p.Key, p => new InstructionTextViewModel(p.Value.Title, p.Value.Body)), s.CreatedOn,
            s.ModifiedOn);
}
