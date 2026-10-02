using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Alerting;

/// <summary>The answers of the alert rule service that are not plain validation (ARV-037).</summary>
public static class AlertRuleErrors
{
    public const string UnknownZones = "Every zone in scope must be a zone of the site's published zone profile.";
    public const string RoleNotHeld = "Only an administrator can give a rule to, escalate it to, or take it from a role the caller does not hold.";
    public const string TooManyRules = "A site has at most 500 alert rules, and its codes end at R-999999.";
}

/// <summary>
/// Alert rules (ARV-037): typed rules per site, read and written within the caller's sites (a rule elsewhere answers like
/// one that does not exist), every change audited. Zones in scope must belong to the site's published zone profile;
/// owner and escalation roles are roles the caller holds unless the caller is an administrator.
/// </summary>
public interface ISvcAlertRules : ISvcScoped
{
    Task<Result<PageViewModel<AlertRuleViewModel>>> SearchAsync(AlertRuleCriteria criteria, CancellationToken ct = default);
    Task<Result<AlertRuleViewModel>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<AlertRuleViewModel>> CreateAsync(AlertRuleRequest request, CancellationToken ct = default);
    Task<Result<AlertRuleViewModel>> UpdateAsync(Guid id, AlertRuleRequest request, CancellationToken ct = default);
    Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
