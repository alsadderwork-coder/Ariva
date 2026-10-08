using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Validation;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// Validation campaigns (ARV-104a, <see cref="ISvcValidationCampaigns"/>, script 0047). The site comes first (NotFound outside
/// the caller's sites), then the request (<see cref="ValidationRules"/>, today in the site's time zone), then the stored state.
/// A campaign is planned over the site's published profile version, read with a share lock so a concurrent publish waits;
/// start and close lock the campaign's row, so they happen one at a time; create, start and close are audited in the same
/// unit of work. The people named are Ariva user ids only. Border desks in scope (ARV-104b) are border data: only a caller
/// who sees border desks puts them in a campaign (403 otherwise) or sees them and their observed minutes in its view.
/// </summary>
internal sealed class SvcValidationCampaigns(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, AuditTrail audit,
    ReportReader reader, CallerRoles callerRoles)
    : ValidationServiceBase(unitOfWork, currentUser, timeProvider, siteScope, reader), ISvcValidationCampaigns
{
    #region Reads

    public async Task<Result<PageViewModel<ValidationCampaignSummaryViewModel>>> SearchAsync(string siteCode, ValidationCampaignCriteria criteria, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<PageViewModel<ValidationCampaignSummaryViewModel>>(ValidationErrors.NotFound);
        criteria ??= new ValidationCampaignCriteria();
        var valid = await ValidationRules.Campaigns().ValidateAllAsync(criteria);
        if (valid.HasErrors)
            return Result.Error<PageViewModel<ValidationCampaignSummaryViewModel>>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidSort);

        var campaigns = QueryAsNoTracking<ValidationCampaign>().Where(c => c.SiteCode == siteCode);
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.Trim().ToLowerInvariant();
            campaigns = campaigns.Where(c => c.Name.ToLower().Contains(text));
        }

        if (criteria.Status is not null && Enum.TryParse<ValidationCampaignStatus>(criteria.Status, ignoreCase: false, out var status))
            campaigns = campaigns.Where(c => c.Status == status);
        if (criteria.FromDate is { } from)
            campaigns = campaigns.Where(c => c.CreatedUtc >= from);
        if (criteria.ToDate is { } to)
            campaigns = campaigns.Where(c => c.CreatedUtc <= to);

        var ordered = (criteria.SortBy?.ToLowerInvariant(), criteria.SortDescending) switch
        {
            ("name", false) => campaigns.OrderBy(c => c.Name).ThenBy(c => c.Id),
            ("name", true) => campaigns.OrderByDescending(c => c.Name).ThenBy(c => c.Id),
            ("status", false) => campaigns.OrderBy(c => c.Status).ThenByDescending(c => c.CreatedUtc),
            ("status", true) => campaigns.OrderByDescending(c => c.Status).ThenByDescending(c => c.CreatedUtc),
            ("firstday", false) => campaigns.OrderBy(c => c.PlannedDays).ThenBy(c => c.Id),
            ("firstday", true) => campaigns.OrderByDescending(c => c.PlannedDays).ThenBy(c => c.Id),
            ("createdutc", false) => campaigns.OrderBy(c => c.CreatedUtc).ThenBy(c => c.Id),
            _ => campaigns.OrderByDescending(c => c.CreatedUtc).ThenBy(c => c.Id)
        };

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await campaigns.CountAsync(ct);
        var rows = await ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<ValidationCampaignSummaryViewModel>>(
            new PageViewModel<ValidationCampaignSummaryViewModel>([.. rows.Select(Summary)], total, pageIndex, pageSize));
    }

    public async Task<Result<ValidationCampaignViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotFound);
        var campaign = await CampaignAsync(siteCode, id, ct);
        return campaign is null
            ? Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotFound)
            : new Result<ValidationCampaignViewModel>(await ViewAsync(campaign, ct));
    }

    public async Task<Result<PageViewModel<ManualCountViewModel>>> SearchCountsAsync(string siteCode, Guid id, ManualCountCriteria criteria, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct))
            return Result.Error<PageViewModel<ManualCountViewModel>>(ValidationErrors.NotFound);
        var campaign = await CampaignAsync(siteCode, id, ct);
        if (campaign is null)
            return Result.Error<PageViewModel<ManualCountViewModel>>(ValidationErrors.NotFound);
        criteria ??= new ManualCountCriteria();
        var valid = await ValidationRules.Counts().ValidateAllAsync(criteria);
        if (valid.HasErrors)
            return Result.Error<PageViewModel<ManualCountViewModel>>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidSort);
        return new Result<PageViewModel<ManualCountViewModel>>(await CountsAsync(campaign, criteria, criteria.ObserverId, ct));
    }

    #endregion

    #region Lifecycle

    public async Task<Result<ValidationCampaignViewModel>> CreateAsync(string siteCode, CreateValidationCampaignRequest request, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } caller)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotFound);
        var zone = await TimeZoneAsync(siteCode, ct);
        var today = Today(zone);
        request ??= new CreateValidationCampaignRequest(null, null, null, null, null, null, null);
        var valid = await ValidationRules.Create(today).ValidateAllAsync(request);
        if (valid.HasErrors)
            return Result.Error<ValidationCampaignViewModel>(valid.ErrorMessages.FirstOrDefault() ?? ValidationErrors.InvalidScope);

        // The published version, share-locked until commit: a concurrent publish (which retires it) waits for this campaign.
        var published = await ExecuteCommandAsync<GuidRow>(
            """SELECT id AS "Value" FROM zone_profile WHERE site_code = :site AND status = 'Published' AND version = :version FOR SHARE""",
            new Dictionary<string, object> { ["site"] = siteCode, ["version"] = request.ProfileVersion.GetValueOrDefault() }, ct);
        if (published.Count != 1)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotPublished);
        var profile = await GetAsync<ZoneProfile>(published[0].Value, ct);
        if (profile is null || profile.SiteCode != siteCode || profile.Status != ZoneProfileStatus.Published)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotPublished);
        if (ValidationCampaign.ScopeProblem(profile, request.ZoneIds, request.LineIds) is { } scope)
            return Result.Error<ValidationCampaignViewModel>(scope);
        var desks = await DesksAsync(siteCode, request.DeskIds, ct);
        if (desks is null)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.DesksNeedBorderRole);
        if (ValidationCampaign.DeskScopeProblem(siteCode, request.DeskIds, desks) is { } deskScope)
            return Result.Error<ValidationCampaignViewModel>(deskScope);

        var campaign = new ValidationCampaign(request.Name, profile, request.ZoneIds, request.LineIds, ValidationRules.Days(request.Days), today,
            request.TargetBinsPerLine, request.TargetTracerRuns, caller, UtcNow, desks);
        await SaveAsync(campaign, ct);
        foreach (var scopeZone in campaign.Zones)
            await SaveAsync(scopeZone, ct);
        foreach (var scopeLine in campaign.Lines)
            await SaveAsync(scopeLine, ct);
        foreach (var scopeDesk in campaign.Desks)
            await SaveAsync(scopeDesk, ct);
        if (await FlushRefusedAsync(ct) is not null)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotPublished);

        await audit.RecordAsync(Actions.Created, Target, campaign.Id, siteCode, null, campaign.AuditSummary(), ct);
        await FlushAsync(ct);
        return new Result<ValidationCampaignViewModel>(await ViewAsync(campaign, ct));
    }

    public async Task<Result<ValidationCampaignViewModel>> StartAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } caller || !await LockCampaignAsync(siteCode, id, ct))
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotFound);
        var campaign = await CampaignAsync(siteCode, id, ct);
        if (campaign is null)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotFound);
        var (profileStatus, _) = await ProfileStateAsync(campaign.ProfileId, ct);
        if (campaign.StartProblem(profileStatus) is { } problem)
            return Result.Error<ValidationCampaignViewModel>(problem);

        var before = campaign.AuditSummary();
        campaign.Start(caller, UtcNow, profileStatus);
        await UpdateAsync(campaign, ct);
        // The trigger refuses the start when a publish retired the version since it was read (23001).
        if (await FlushRefusedAsync(ct) is not null)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.ProfileRetired);
        await audit.RecordAsync(Actions.Started, Target, campaign.Id, siteCode, before, campaign.AuditSummary(), ct);
        await FlushAsync(ct);
        return new Result<ValidationCampaignViewModel>(await ViewAsync(campaign, ct));
    }

    public async Task<Result<ValidationCampaignViewModel>> CloseAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        if (!await VisibleAsync(siteCode, ct) || Caller is not { } caller || !await LockCampaignAsync(siteCode, id, ct))
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotFound);
        var campaign = await CampaignAsync(siteCode, id, ct);
        if (campaign is null)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.NotFound);
        if (campaign.CloseProblem() is { } problem)
            return Result.Error<ValidationCampaignViewModel>(problem);

        var before = campaign.AuditSummary();
        campaign.Close(caller, UtcNow);
        await UpdateAsync(campaign, ct);
        if (await FlushRefusedAsync(ct) is not null)
            return Result.Error<ValidationCampaignViewModel>(ValidationErrors.Concurrent);
        await audit.RecordAsync(Actions.Closed, Target, campaign.Id, siteCode, before, campaign.AuditSummary(), ct);
        await FlushAsync(ct);
        return new Result<ValidationCampaignViewModel>(await ViewAsync(campaign, ct));
    }

    #endregion

    #region Helpers

    private const string Target = nameof(ValidationCampaign);

    /// <summary>Stable audit action codes; never rename one that has shipped.</summary>
    internal static class Actions
    {
        public const string Created = "ValidationCampaign.Created";
        public const string Started = "ValidationCampaign.Started";
        public const string Closed = "ValidationCampaign.Closed";
    }

    private static ValidationCampaignSummaryViewModel Summary(ValidationCampaign c) =>
        new(c.Id.GetValueOrDefault(), c.SiteCode, c.Name, c.Status.ToString(), c.ProfileVersion, [.. c.Days.Select(ValidationCampaign.FormatDay)], c.Zones.Count,
            c.Lines.Count, c.CreatedUtc, c.StartedUtc, c.ClosedUtc);

    /// <summary>
    /// The desks a request names (none when it names none), or null when it names some and the caller does not see border
    /// desks (403). Desks of another site, deleted ones and missing ones are left out here and refused by the scope rule.
    /// </summary>
    private async Task<IReadOnlyCollection<Desk>> DesksAsync(string siteCode, IReadOnlyList<Guid> deskIds, CancellationToken ct)
    {
        if (deskIds is null || deskIds.Count == 0)
            return [];
        if (!(await DeskAccessAsync(callerRoles, ct)).Sees)
            return null;
        var ids = deskIds.Select(id => (Guid?)id).ToList();
        return await Query<Desk>().Where(d => ids.Contains(d.Id) && d.SiteCode == siteCode).ToListAsync(ct);
    }

    private async Task<ValidationCampaignViewModel> ViewAsync(ValidationCampaign c, CancellationToken ct)
    {
        var (profileStatus, retiredOn) = await ProfileStateAsync(c.ProfileId, ct);
        var zone = await TimeZoneAsync(c.SiteCode, ct);
        var parameters = new Dictionary<string, object> { ["id"] = c.Id.GetValueOrDefault() };
        var bins = (await ExecuteSqlAsync<KeyCount>("""
                SELECT line_id AS "Key", count(DISTINCT bin_start_utc) AS "Count" FROM manual_count WHERE campaign_id = :id GROUP BY line_id
                """, parameters, ct))
            .ToDictionary(b => b.Key, b => (int)b.Count);
        var tracers = (await ExecuteSqlAsync<KeyCount>("""
                SELECT zone_id AS "Key", count(*) AS "Count" FROM tracer_run WHERE campaign_id = :id GROUP BY zone_id
                """, parameters, ct))
            .ToDictionary(t => t.Key, t => (int)t.Count);
        // Border data: the desks and their observed minutes only for a caller who sees border desks (CWE-863, data boundary).
        var desksIncluded = (await DeskAccessAsync(callerRoles, ct)).Sees;
        var minutes = desksIncluded && c.Desks.Count > 0
            ? (await ExecuteSqlAsync<KeyCount>("""
                    SELECT desk_id AS "Key", count(DISTINCT minute_utc) AS "Count" FROM desk_observation WHERE campaign_id = :id GROUP BY desk_id
                    """, parameters, ct))
                .ToDictionary(m => m.Key, m => (int)m.Count)
            : [];
        return new ValidationCampaignViewModel(
            c.Id.GetValueOrDefault(), c.SiteCode, c.Name, c.Status.ToString(), c.ProfileId, c.ProfileVersion, c.GeometryHash, profileStatus.ToString(), retiredOn,
            zone.Id, [.. c.Days.Select(ValidationCampaign.FormatDay)], new ValidationTargetsViewModel(c.TargetBinsPerLine, c.TargetTracerRuns, c.TargetsPlaceholder),
            [.. c.Zones.OrderBy(z => z.ZoneName, StringComparer.Ordinal).Select(z => new ValidationZoneViewModel(z.ZoneId, z.ZoneName, tracers.GetValueOrDefault(z.ZoneId)))],
            [.. c.Lines.OrderBy(l => l.LineName, StringComparer.Ordinal)
                .Select(l => new ValidationLineViewModel(l.LineId, l.LineName, l.LineRole.ToString(), l.QueueZoneName, bins.GetValueOrDefault(l.LineId)))],
            desksIncluded,
            desksIncluded
                ? [.. c.Desks.OrderBy(d => d.CheckpointCode, StringComparer.Ordinal).ThenBy(d => d.DeskCode, StringComparer.Ordinal)
                    .Select(d => new ValidationDeskViewModel(d.DeskId, d.CheckpointCode, d.DeskCode, minutes.GetValueOrDefault(d.DeskId)))]
                : [],
            c.CreatedById, c.CreatedUtc, c.StartedById, c.StartedUtc, c.ClosedById, c.ClosedUtc);
    }

    private sealed class KeyCount
    {
        public Guid Key { get; set; }
        public long Count { get; set; }
    }

    #endregion
}
