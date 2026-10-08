using System.Globalization;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// What the validation campaign and capture services share (ARV-104a, script 0047): the site check that comes before
/// anything else (outside the caller's sites, or unknown, answers NotFound, CWE-863, CWE-204), the campaign read within the
/// site, the site's time zone, the profile version's state, the count searches and the views. Raw SQL only through
/// parameterised constant statements (CWE-89).
/// </summary>
internal abstract class ValidationServiceBase(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, ReportReader reader)
    : SvcBase(unitOfWork, currentUser, timeProvider)
{
    #region Constants

    /// <summary>PostgreSQL: a unique index refused a row.</summary>
    protected const string UniqueViolation = "23505";

    /// <summary>PostgreSQL: a trigger of script 0047 refused a change (campaign not running, profile not published).</summary>
    protected const string RestrictViolation = "23001";

    #endregion

    #region Site and campaign

    /// <summary>The site is the caller's and exists; checked before anything else, one answer for both (CWE-204).</summary>
    protected async Task<bool> VisibleAsync(string siteCode, CancellationToken ct) =>
        siteCode is not null && Site.IsValidCode(siteCode) && (await siteScope.GetAsync(ct)).Allows(siteCode) && await reader.SiteExistsAsync(siteCode, ct);

    /// <summary>The campaign with this id at this site, or null (a campaign of another site answers like a missing one).</summary>
    protected async Task<ValidationCampaign> CampaignAsync(string siteCode, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return null;
        var access = await siteScope.GetAsync(ct);
        return await Query<ValidationCampaign>().WithinSites(access).FirstOrDefaultAsync(c => c.Id == id && c.SiteCode == siteCode, ct);
    }

    /// <summary>
    /// Locks the campaign's row for this transaction (start and close one at a time) and says whether it is this site's.
    /// </summary>
    protected async Task<bool> LockCampaignAsync(string siteCode, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return false;
        var rows = await ExecuteCommandAsync<GuidRow>("""SELECT id AS "Value" FROM validation_campaign WHERE id = :id AND site_code = :site FOR UPDATE""",
            new Dictionary<string, object> { ["id"] = id, ["site"] = siteCode }, ct);
        return rows.Count == 1;
    }

    protected Task<TimeZoneInfo> TimeZoneAsync(string siteCode, CancellationToken ct) => reader.TimeZoneAsync(siteCode, ct);

    protected DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, zone));

    /// <summary>The state of a profile version: its status and when it was retired.</summary>
    protected async Task<(ZoneProfileStatus Status, DateTime? RetiredOn)> ProfileStateAsync(Guid profileId, CancellationToken ct)
    {
        var state = await QueryAsNoTracking<ZoneProfile>().Where(p => p.Id == profileId).Select(p => new { p.Status, p.RetiredOn }).FirstOrDefaultAsync(ct);
        return state is null ? (ZoneProfileStatus.Retired, null) : (state.Status, state.RetiredOn);
    }

    /// <summary>The caller's Ariva user id; null for a caller without one (never past the permission check in a host).</summary>
    protected Guid? Caller => CurrentUser.Id is { } id && id != Guid.Empty ? id : null;

    #endregion

    #region Counts

    /// <summary>
    /// A page of a campaign's counts: the current revisions only (no later revision corrects them) or every revision, in a
    /// line, of an observer, of bins starting in [FromDate, ToDate), sorted by an allowlisted field (CWE-89).
    /// </summary>
    protected async Task<PageViewModel<ManualCountViewModel>> CountsAsync(ValidationCampaign campaign, ManualCountCriteria criteria, Guid? observerId, CancellationToken ct)
    {
        var campaignId = campaign.Id.GetValueOrDefault();
        var counts = QueryAsNoTracking<ManualCount>().Where(c => c.CampaignId == campaignId && c.SiteCode == campaign.SiteCode);
        if (observerId is { } observer)
            counts = counts.Where(c => c.ObserverId == observer);
        if (criteria.LineId is { } lineId)
            counts = counts.Where(c => c.LineId == lineId);
        if (criteria.FromDate is { } from)
            counts = counts.Where(c => c.BinStartUtc >= from);
        if (criteria.ToDate is { } to)
            counts = counts.Where(c => c.BinStartUtc < to);
        if (criteria.CurrentOnly)
        {
            var corrections = QueryAsNoTracking<ManualCount>().Where(n => n.CampaignId == campaignId && n.CorrectsId != null);
            counts = counts.Where(c => !corrections.Any(n => n.CorrectsId == c.Id));
        }

        var ordered = (criteria.SortBy?.ToLowerInvariant(), criteria.SortDescending) switch
        {
            ("recordedutc", false) => counts.OrderBy(c => c.RecordedUtc).ThenBy(c => c.Id),
            ("recordedutc", true) => counts.OrderByDescending(c => c.RecordedUtc).ThenBy(c => c.Id),
            (_, true) => counts.OrderByDescending(c => c.BinStartUtc).ThenBy(c => c.LineId).ThenBy(c => c.ObserverId).ThenBy(c => c.Revision),
            _ => counts.OrderBy(c => c.BinStartUtc).ThenBy(c => c.LineId).ThenBy(c => c.ObserverId).ThenBy(c => c.Revision)
        };

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await counts.CountAsync(ct);
        var rows = await ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var corrected = new HashSet<Guid>();
        if (!criteria.CurrentOnly && rows.Count > 0)
        {
            var ids = rows.Select(r => (Guid?)r.Id).ToList();
            corrected = (await QueryAsNoTracking<ManualCount>().Where(n => n.CampaignId == campaignId && ids.Contains(n.CorrectsId))
                .Select(n => n.CorrectsId.Value).ToListAsync(ct)).ToHashSet();
        }

        return new PageViewModel<ManualCountViewModel>(
            [.. rows.Select(r => View(campaign, r, current: !corrected.Contains(r.Id.GetValueOrDefault())))], total, pageIndex, pageSize);
    }

    protected static ManualCountViewModel View(ValidationCampaign campaign, ManualCount count, bool current) =>
        new(count.Id.GetValueOrDefault(), count.CampaignId, count.LineId, campaign.LineOf(count.LineId)?.LineName, count.BinStartUtc, count.ObserverId, count.Revision,
            current, count.CrossingsIn, count.CrossingsOut, count.Reason, count.CorrectsId, count.RecordedUtc);

    /// <summary>What the audit log keeps of a count: ids, the bin, the crossings and the reason as a JSON string; no names.</summary>
    protected static string AuditSummary(ValidationCampaign campaign, ManualCount count) =>
        string.Create(CultureInfo.InvariantCulture,
            $"campaign={count.CampaignId}; line={System.Text.Json.JsonSerializer.Serialize(campaign.LineOf(count.LineId)?.LineName)}; bin={count.BinStartUtc:yyyy-MM-ddTHH:mm}Z; observer={count.ObserverId}; " +
            $"revision={count.Revision}; in={count.CrossingsIn}; out={count.CrossingsOut}; reason={System.Text.Json.JsonSerializer.Serialize(count.Reason)}");

    #endregion

    #region Flush

    /// <summary>
    /// Flushes the pending inserts; on a unique index (23505) or a script 0047 trigger (23001) refusing them, promises the unit of
    /// work not to commit and returns the SQLSTATE and the constraint, so the request answers 409 with nothing of it committed.
    /// </summary>
    protected async Task<(string State, string Constraint)?> FlushRefusedAsync(CancellationToken ct)
    {
        try
        {
            await FlushAsync(ct);
            return null;
        }
        catch (global::NHibernate.Exceptions.GenericADOException e) when (e.InnerException is global::Npgsql.PostgresException { SqlState: UniqueViolation or RestrictViolation } refused)
        {
            UnitOfWork.PromiseNotToCommit();
            return (refused.SqlState, refused.ConstraintName);
        }
    }

    protected sealed class GuidRow
    {
        public Guid Value { get; set; }
    }

    #endregion
}
