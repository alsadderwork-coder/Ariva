using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Alerting;

/// <summary>
/// Alert rules (ARV-037). A rule is created in one of the caller's sites and stays there; reads and writes outside the
/// caller's sites answer like a rule that does not exist (CWE-863). Every value is typed and checked by the entity
/// (CWE-94: there is no expression to evaluate; enums are taken by their exact names only); the zones in scope must be
/// queue or overflow zones of the site's published profile, and a caller who is not an administrator can neither give a
/// rule to nor escalate it to a role they do not hold, nor change or remove such a role on an existing rule. Codes
/// R-001, R-002 and so on are given per site in order, under a lock, deleted rules keeping theirs. Every change is
/// audited with the rule's values before and after.
/// </summary>
internal sealed class SvcAlertRules(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    CallerRoles callerRoles,
    AuditTrail audit) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAlertRules
{
    public const int MaxRulesPerSite = 500;

    public const int MaxCode = AlertRule.MaxNumber;
    private const string Target = "AlertRule";

    public async Task<Result<PageViewModel<AlertRuleViewModel>>> SearchAsync(AlertRuleCriteria criteria, CancellationToken ct = default)
    {
        criteria ??= new AlertRuleCriteria();
        AlertMetric? metric = null;
        if (criteria.Metric is not null)
        {
            if (!TryParse<AlertMetric>(criteria.Metric, out var parsed))
                return Result.Error<PageViewModel<AlertRuleViewModel>>(TopologyErrors.InvalidCriteria);
            metric = parsed;
        }

        var query = QueryAsNoTracking<AlertRule>().WithinSites(await siteScope.GetAsync(ct)).Where(r => r.DeletedOn == null);
        if (!string.IsNullOrWhiteSpace(criteria.SiteCode))
            query = query.Where(r => r.SiteCode == criteria.SiteCode);
        if (metric is { } m)
            query = query.Where(r => r.Metric == m);
        if (criteria.Enabled is { } enabled)
            query = query.Where(r => r.Enabled == enabled);
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.Trim();
            query = query.Where(r => r.Code.Contains(text) || r.Name.Contains(text));
        }

        var pageSize = Math.Clamp(criteria.PageSize, 1, MaxPageSize);
        var pageIndex = Math.Max(1, criteria.PageIndex);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.SiteCode).ThenBy(r => r.Code).Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Result<PageViewModel<AlertRuleViewModel>>(new PageViewModel<AlertRuleViewModel>(rows.Select(View).ToList(), total, pageIndex, pageSize));
    }

    public async Task<Result<AlertRuleViewModel>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await VisibleAsync(id, ct);
        return rule is null ? Result.Error<AlertRuleViewModel>(TopologyErrors.NotFound) : new Result<AlertRuleViewModel>(View(rule));
    }

    public async Task<Result<AlertRuleViewModel>> CreateAsync(AlertRuleRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SiteCode) || !(await siteScope.GetAsync(ct)).Allows(request.SiteCode) ||
            !await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == request.SiteCode, ct))
            return Result.Error<AlertRuleViewModel>(TopologyErrors.UnknownSite);
        var (values, problems) = await CheckAsync(request.SiteCode, request, null, ct);
        if (problems.Count > 0)
            return Result.Error<AlertRuleViewModel>(problems);

        // One writer per site gives the next code; codes of deleted rules are not reused.
        // The session hides deleted rows, so the codes are read from the table itself.
        await LockAsync(request.SiteCode, ct);
        var codes = (await ExecuteCommandAsync<CodeRow>("""
            SELECT CAST(count(*) FILTER (WHERE deleted_on IS NULL) AS integer) AS "Live", CAST(coalesce(max(CAST(substring(code FROM 3) AS integer)), 0) AS integer) AS "Last"
            FROM alert_rule WHERE site_code = :site
            """, new Dictionary<string, object> { ["site"] = request.SiteCode }, ct)).Single();
        if (codes.Live >= MaxRulesPerSite || codes.Last >= MaxCode)
            return Result.Error<AlertRuleViewModel>(AlertRuleErrors.TooManyRules);
        var rule = new AlertRule(request.SiteCode, codes.Last + 1, values);
        await SaveAsync(rule, ct);
        await audit.RecordAsync($"{Target}.Created", Target, rule.Id, Name(rule), null, rule.AuditSummary(), ct);
        return new Result<AlertRuleViewModel>(View(rule));
    }

    public async Task<Result<AlertRuleViewModel>> UpdateAsync(Guid id, AlertRuleRequest request, CancellationToken ct = default)
    {
        var rule = await VisibleAsync(id, ct);
        if (rule is null || request is null)
            return Result.Error<AlertRuleViewModel>(TopologyErrors.NotFound);
        if (!string.IsNullOrWhiteSpace(request.SiteCode) && !string.Equals(request.SiteCode, rule.SiteCode, StringComparison.Ordinal))
            return Result.Error<AlertRuleViewModel>("A rule stays in its site.");
        var (values, problems) = await CheckAsync(rule.SiteCode, request, rule, ct);
        if (problems.Count > 0)
            return Result.Error<AlertRuleViewModel>(problems);

        var before = rule.AuditSummary();
        rule.Set(values);
        await UpdateAsync(rule, ct);
        await audit.RecordAsync($"{Target}.Updated", Target, rule.Id, Name(rule), before, rule.AuditSummary(), ct);
        return new Result<AlertRuleViewModel>(View(rule));
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await VisibleAsync(id, ct);
        if (rule is null)
            return Result.Error<bool>(TopologyErrors.NotFound);
        rule.SoftDelete(CurrentUser.UserName, UtcNow);
        await UpdateAsync(rule, ct);
        await audit.RecordAsync($"{Target}.Deleted", Target, rule.Id, Name(rule), rule.AuditSummary(), null, ct);
        return new Result<bool>(true);
    }

    // The request as typed values (exact names only for the enums), the entity's checks, the zones against the site's
    // published profile, and the roles against the caller's (for an update, against the rule's current roles too).
    private async Task<(AlertRuleValues Values, IReadOnlyList<string> Problems)> CheckAsync(string siteCode, AlertRuleRequest request, AlertRule existing, CancellationToken ct)
    {
        var problems = new List<string>();
        if (!TryParse<AlertMetric>(request.Metric, out var metric) | !TryParse<AlertComparator>(request.Comparator, out var comparator) |
            !TryParse<AlertSeverity>(request.Severity, out var severity))
        {
            problems.Add("Unknown metric, comparator or severity.");
            return (null, problems);
        }

        var values = new AlertRuleValues(request.Name, request.Zones ?? [], metric, comparator, request.Threshold, request.MinQueueLength, request.ClearThreshold,
            request.SustainMinutes, request.ClearAfterMinutes, severity, Blank(request.OwnerRole), request.EscalateAfterMinutes, Blank(request.EscalateToRole),
            request.EscalationContact, request.NotifyByEmail, request.Enabled);
        problems.AddRange(values.Problems());
        if (problems.Count > 0)
            return (values, problems);

        var zones = values.Zones.Select(z => z.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var known = await Query<Zone>()
            .Where(z => z.Profile.SiteCode == siteCode && z.Profile.Status == ZoneProfileStatus.Published && zones.Contains(z.Name) &&
                        (z.Kind == ZoneKind.Queue || z.Kind == ZoneKind.Overflow))
            .Select(z => z.Name)
            .ToListAsync(ct);
        if (known.Distinct(StringComparer.Ordinal).Count() != zones.Count)
            problems.Add(AlertRuleErrors.UnknownZones);

        // A role the caller does not hold can be neither given nor taken away: an unchanged role is fine, a changed one
        // must be held before and after.
        var touched = new List<string>();
        if (!string.Equals(values.OwnerRole, existing?.OwnerRole, StringComparison.Ordinal))
            touched.AddRange(new[] { values.OwnerRole, existing?.OwnerRole }.Where(r => r is not null));
        if (!string.Equals(values.EscalateToRole, existing?.EscalateToRole, StringComparison.Ordinal))
            touched.AddRange(new[] { values.EscalateToRole, existing?.EscalateToRole }.Where(r => r is not null));
        if (touched.Count > 0)
        {
            var held = (await callerRoles.GetAsync(ct)).ToHashSet(StringComparer.Ordinal);
            if (!held.Contains(Ariva.Core.RoleCodes.SystemAdministrator) && touched.Any(r => !held.Contains(r)))
                problems.Add(AlertRuleErrors.RoleNotHeld);
        }

        return (values, problems);
    }

    private async Task<AlertRule> VisibleAsync(Guid id, CancellationToken ct)
    {
        var rule = id == Guid.Empty ? null : await GetAsync<AlertRule>(id, ct);
        return rule is null || rule.IsDeleted || !(await siteScope.GetAsync(ct)).Allows(rule.SiteCode) ? null : rule;
    }

    private Task LockAsync(string siteCode, CancellationToken ct) =>
        ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM (SELECT pg_advisory_xact_lock(37, hashtext(:site))) l""",
            new Dictionary<string, object> { ["site"] = siteCode }, ct);

    private sealed class LockRow
    {
        public int Value { get; set; }
    }

    private sealed class CodeRow
    {
        public int Live { get; set; }

        public int Last { get; set; }
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// An enum value by its exact name only: numbers, other cases, padding and comma-joined names (which Enum.TryParse
    /// would combine into another value) are refused.
    /// </summary>
    private static bool TryParse<T>(string value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        return value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) && Enum.TryParse(value, ignoreCase: false, out parsed);
    }

    private static string Name(AlertRule r) => $"{r.SiteCode}:{r.Code}";

    private static AlertRuleViewModel View(AlertRule r) =>
        new(r.Id.Value, r.SiteCode, r.Code, r.Name, r.Zones, r.Metric.ToString(), r.Comparator.ToString(), r.Threshold, r.MinQueueLength, r.ClearThreshold,
            r.SustainMinutes, r.ClearAfterMinutes, r.Severity.ToString(), r.OwnerRole, r.EscalateAfterMinutes, r.EscalateToRole, r.EscalationContact,
            r.NotifyByEmail, r.Enabled, r.CreatedOn, r.ModifiedOn);
}
