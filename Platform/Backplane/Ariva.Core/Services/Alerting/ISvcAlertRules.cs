using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Alerting;

/// <summary>The answers of the alert rule service that are not plain validation (ARV-037).</summary>
public static class AlertRuleErrors
{
    public const string NotEvaluated = "Ariva does not evaluate this metric yet, so a new rule on it could never fire.";

    /// <summary>
    /// Metrics a rule can name but the evaluation has nothing to judge for yet (AlertInputs): no new rule may use them, and
    /// an existing rule on one may keep it while it is shown as not evaluated. OverflowOccupied left the set with ARV-115
    /// (the stream stores each overflow band's occupancy per minute), so the demo seed's R-002 is evaluated; DesksBelowPlan
    /// left it with ARV-181b2 (open desks below the recommendation of <c>lane_staffing</c>'s current interval), so the set is
    /// empty and kept for a metric added before its inputs exist.
    /// </summary>
    public static readonly IReadOnlySet<Ariva.Core.Domain.Enums.AlertMetric> NotEvaluatedMetrics = new HashSet<Ariva.Core.Domain.Enums.AlertMetric>();

    /// <summary>
    /// ARV-181b2 and ARV-187 (CWE-862, CWE-863): a caller without <c>ArrivalWaveLanes.View</c> creating a rule on a
    /// border-only metric (open desks below recommendation or the predicted nowcast), changing a rule to one, or backtesting
    /// or previewing one (403). Rules on such a metric are hidden from that caller otherwise (404). One text for every border
    /// metric (ARV-187, lead decision P1), which <c>TopologyAnswers</c> maps to 403 by its exact value.
    /// </summary>
    public const string BorderOnly =
        "Open desks below recommendation and predicted nowcast are border data: only a caller with ArrivalWaveLanes.View creates, changes, imports or backtests a rule on them.";

    /// <summary>
    /// ARV-181b2 and ARV-187: a rule on a border-only metric names a role that is not a border role, or no owner (every
    /// role). One text for every border metric (lead decision P1).
    /// </summary>
    public const string BorderRolesOnly =
        "A rule on open desks below recommendation or predicted nowcast belongs to a border role: its owner is the Border shift supervisor or the system administrator (never empty), and it escalates to one of them or to no role.";

    /// <summary>
    /// ARV-181b2: open desks below recommendation counts desks short, so a rule fires above its threshold. The predicted
    /// nowcast keeps all four comparators (ARV-187, lead decision P2; <see cref="Ariva.Core.Domain.Entities.AlertRuleValues.FiresAboveOnly"/>).
    /// </summary>
    public const string BorderComparator = "A rule on open desks below recommendation fires at or above its threshold: compare with GreaterThan or GreaterOrEqual.";

    public const string UnknownZones = "Every zone in scope must be a zone of the site's published zone profile.";
    public const string QueueZonesOnly =
        "ConservationResidual, TrackCompletion, OccupancyOutOfRange and DesksBelowPlan are judged per queue zone: choose queue zones only.";
    public const string RoleNotHeld = "Only an administrator can give a rule to, escalate it to, or take it from a role the caller does not hold.";
    public const string BacktestRange = "A backtest covers 1 minute to 24 hours that have ended, in the last 90 days, in UTC (ending in Z).";
    public const string BacktestTooLarge = "The range holds more stored bins than one backtest reads: choose a shorter range or fewer zones.";
    public const string TooManyRules = "A site has at most 500 alert rules, and its codes end at R-999999.";
}

/// <summary>
/// Alert rules (ARV-037): typed rules per site, read and written within the caller's sites (a rule elsewhere answers like
/// one that does not exist), every change audited. Zones in scope must belong to the site's published zone profile;
/// owner and escalation roles are roles the caller holds unless the caller is an administrator. A rule on a border-only
/// metric (<see cref="Ariva.Core.Security.BorderAlerts"/>: open desks below recommendation since ARV-181b2, the predicted
/// nowcast since ARV-187) is for callers holding <c>ArrivalWaveLanes.View</c> only: others are refused 403
/// (<see cref="AlertRuleErrors.BorderOnly"/>) when they create, change, backtest or preview one, and never find one.
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
