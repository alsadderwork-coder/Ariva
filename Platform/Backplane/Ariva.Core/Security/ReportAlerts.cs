using static Ariva.Core.Global.Defaults;

namespace Ariva.Core.Security;

/// <summary>
/// Who reads alerts in a report (ARV-180, CWE-863, CWE-200): a reader whose roles hold <c>Alert.View</c>, the alert screens'
/// own permission. The daily report with its CSV and deliveries and the weekly summary list the alerts by the alert screens'
/// owner and escalation rule, under which an alert without an owner reaches any role; a reader whose roles hold
/// <c>Report.View</c> without <c>Alert.View</c> sees no alert at all. Every role that reads reports holds <c>Alert.View</c> in
/// the role seed today (a test pins it), so nothing changes for them; a future role that reads reports only does not see alerts.
/// </summary>
public static class ReportAlerts
{
    #region Methods

    /// <summary>Whether a caller or a recipient holding these roles sees alerts in a report: they hold <c>Alert.View</c>.</summary>
    public static bool Receives(IEnumerable<string> roles) => RolePermissions.For((roles ?? []).Where(r => r is not null)).Contains(Permissions.ViewAlert);

    #endregion
}
