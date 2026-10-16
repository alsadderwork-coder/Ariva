using Ariva.Core.Domain.Enums;
using static Ariva.Core.Global.Defaults;

namespace Ariva.Core.Security;

/// <summary>
/// Which alerts are border data and who receives them (ARV-181b2; data boundary, CWE-863). An alert on a border-only metric
/// (<see cref="Metrics"/>: <see cref="AlertMetric.DesksBelowPlan"/>, the lane split of the arrival wave against the desks
/// open in each immigration lane) reaches a caller or a role only when it holds <c>ArrivalWaveLanes.View</c>
/// (<see cref="Receives"/>), on every channel and whatever the rule's owner and escalation roles say: the alerts API, the
/// live hub, email when queued and when sent, the daily report with its CSV and deliveries, and the weekly summary. The
/// rule itself names border roles only (<see cref="Roles"/>, checked by <c>AlertRuleValues.Problems</c>), never an empty
/// owner, which would mean every alert role.
/// <para>
/// "Border" is the permission, not a list kept by hand: <see cref="Roles"/> are the alert roles that hold
/// <c>ArrivalWaveLanes.View</c> in the role seed (<see cref="RolePermissions"/>, from which stored grants are resolved) and
/// are not airport-side (<see cref="BorderDeskAccess.IsAirportSide"/>). A test pins both to the border shift supervisor and
/// the system administrator, so a seed change that gives the permission to an airport-side role fails the build instead of
/// widening the audience.
/// </para>
/// </summary>
public static class BorderAlerts
{
    #region Properties

    /// <summary>The metrics whose alerts and rules are border data.</summary>
    public static IReadOnlySet<AlertMetric> Metrics { get; } = new HashSet<AlertMetric> { AlertMetric.DesksBelowPlan };

    /// <summary>The names of <see cref="Metrics"/> as stored (<c>alert.metric</c>, <c>alert_rule.metric</c>), for SQL parameters.</summary>
    public static IReadOnlyList<string> MetricNames { get; } = [.. Metrics.Select(m => m.ToString()).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The alert roles that receive border alerts: those holding <c>ArrivalWaveLanes.View</c> in the seed that are not
    /// airport-side, in <see cref="RoleCodes.AlertRoles"/> order (the border shift supervisor and the system administrator).
    /// </summary>
    public static IReadOnlyList<string> Roles { get; } =
    [
        .. RoleCodes.AlertRoles.Where(r => RolePermissions.ByRole.TryGetValue(r, out var granted) && granted.Contains(Permissions.ViewArrivalWaveLanes) &&
                                           !BorderDeskAccess.IsAirportSide(r))
    ];

    #endregion

    #region Methods

    /// <summary>Whether alerts and rules on this metric are border data.</summary>
    public static bool IsBorderOnly(AlertMetric metric) => Metrics.Contains(metric);

    /// <summary>Whether a stored or sent metric name is a border-only metric's exact name.</summary>
    public static bool IsBorderOnly(string metric) => metric is not null && Enumerable.Contains(MetricNames, metric, StringComparer.Ordinal);

    /// <summary>Whether a role code is one of <see cref="Roles"/> (exact, case-sensitive).</summary>
    public static bool IsBorderRole(string role) => role is not null && Enumerable.Contains(Roles, role, StringComparer.Ordinal);

    /// <summary>Whether a caller or a recipient holding these roles receives border alerts: they hold <c>ArrivalWaveLanes.View</c>.</summary>
    public static bool Receives(IEnumerable<string> roles) => RolePermissions.For((roles ?? []).Where(r => r is not null)).Contains(Permissions.ViewArrivalWaveLanes);

    /// <summary>
    /// The roles an alert on <paramref name="metric"/> may reach of <paramref name="roles"/>: all of them, or for a border-only
    /// metric the border roles among them only (possibly none).
    /// </summary>
    public static IReadOnlyList<string> Audience(AlertMetric metric, IEnumerable<string> roles) => Audience(IsBorderOnly(metric), roles);

    /// <summary>As <see cref="Audience(AlertMetric, IEnumerable{string})"/> for a metric by its stored or sent name.</summary>
    public static IReadOnlyList<string> Audience(string metric, IEnumerable<string> roles) => Audience(IsBorderOnly(metric), roles);

    private static IReadOnlyList<string> Audience(bool borderOnly, IEnumerable<string> roles)
    {
        var all = (roles ?? []).Where(r => r is not null).Distinct(StringComparer.Ordinal);
        return borderOnly ? [.. all.Where(IsBorderRole)] : [.. all];
    }

    #endregion
}
