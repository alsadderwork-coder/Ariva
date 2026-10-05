using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Alerting;

/// <summary>The answers of the alert rule service that are not plain validation (ARV-037).</summary>
public static class AlertRuleErrors
{
    public const string NotEvaluated = "Ariva does not evaluate this metric yet (the stream stores no overflow occupancy and there is no staffing plan), so a new rule on it could never fire.";

    /// <summary>
    /// Metrics a rule can name but the evaluation has nothing to judge for yet (AlertInputs): no new rule may use them, and
    /// an existing rule on one may keep it (the demo seed's R-002) while it is shown as not evaluated.
    /// </summary>
    public static readonly IReadOnlySet<Ariva.Core.Domain.Enums.AlertMetric> NotEvaluatedMetrics =
        new HashSet<Ariva.Core.Domain.Enums.AlertMetric> { Ariva.Core.Domain.Enums.AlertMetric.OverflowOccupied, Ariva.Core.Domain.Enums.AlertMetric.DesksBelowPlan };

    public const string UnknownZones = "Every zone in scope must be a zone of the site's published zone profile.";
    public const string RoleNotHeld = "Only an administrator can give a rule to, escalate it to, or take it from a role the caller does not hold.";
    public const string BacktestRange = "A backtest covers 1 minute to 24 hours that have ended, in the last 90 days, in UTC (ending in Z).";
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

    /// <summary>
    /// Judges a rule (as it would be created, checked like a create) on the stored minutes of a range: the alerts it
    /// would have raised, the same fold the live evaluation makes from the range's start (ARV-038).
    /// </summary>
    Task<Result<AlertBacktestViewModel>> BacktestAsync(AlertBacktestRequest request, CancellationToken ct = default);
}
