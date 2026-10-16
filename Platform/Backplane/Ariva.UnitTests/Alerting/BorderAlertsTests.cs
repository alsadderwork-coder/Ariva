using Ariva.Core;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using FluentAssertions;
using static Ariva.Core.Global.Defaults;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-181b2 (CWE-863, data boundary): who receives border alerts is the permission, not a list kept by hand. The border roles
/// are the alert roles holding ArrivalWaveLanes.View in the seed that are not airport-side, today the border shift supervisor
/// and the system administrator; every holder of the permission is a border role, so the role list and the permission cannot
/// drift apart without failing here. Open desks below recommendation (ARV-181b2) and the predicted nowcast (ARV-187, owner
/// decision 2026-10-16) are the border-only metrics.
/// </summary>
public sealed class BorderAlertsTests
{
    [Fact]
    public void Roles_Should_BeTheBorderShiftSupervisorAndTheAdministrator_When_DerivedFromTheSeed()
    {
        BorderAlerts.Roles.Should().Equal(RoleCodes.BorderShiftSupervisor, RoleCodes.SystemAdministrator);
        BorderAlerts.Metrics.Should().BeEquivalentTo([AlertMetric.DesksBelowPlan, AlertMetric.PredictedNowcast]);
        BorderAlerts.MetricNames.Should().Equal(["DesksBelowPlan", "PredictedNowcast"], "ordinal order, the SQL parameter of every channel's filter");
    }

    [Fact]
    public void Roles_Should_MatchThePermissionExactly_When_EveryRoleIsChecked()
    {
        foreach (var role in RoleCodes.All.Append("Root").Append("bordershiftsupervisor"))
        {
            var holds = RolePermissions.For([role]).Contains(Permissions.ViewArrivalWaveLanes);
            BorderAlerts.Receives([role]).Should().Be(holds, role);
            if (RoleCodes.AlertRoles.Contains(role))
                BorderAlerts.IsBorderRole(role).Should().Be(holds, $"{role}: an alert role is a border role exactly when it holds ArrivalWaveLanes.View");
            else
                BorderAlerts.IsBorderRole(role).Should().BeFalse(role);
        }

        RolePermissions.ByRole.Where(r => r.Value.Contains(Permissions.ViewArrivalWaveLanes)).Select(r => r.Key)
            .Should().OnlyContain(r => !BorderDeskAccess.IsAirportSide(r), "a role holding the lane split is on the border side of the data boundary");
        RolePermissions.ByRole.Where(r => r.Value.Contains(Permissions.TransferSiteConfiguration)).Select(r => r.Key)
            .Should().OnlyContain(r => BorderAlerts.IsBorderRole(r), "an export or import carries border rules: only border roles transfer a site");
    }

    [Fact]
    public void Receives_Should_FollowTheAccountsRoles_When_AnAccountHoldsSeveral()
    {
        BorderAlerts.Receives([RoleCodes.TerminalDutyManager, RoleCodes.BorderShiftSupervisor]).Should().BeTrue("one border role is enough");
        BorderAlerts.Receives([RoleCodes.TerminalDutyManager, RoleCodes.HandlerStationManager]).Should().BeFalse();
        BorderAlerts.Receives([RoleCodes.ValidationObserver]).Should().BeFalse();
        BorderAlerts.Receives([]).Should().BeFalse();
        BorderAlerts.Receives(null).Should().BeFalse();
        BorderAlerts.Receives([null, "Root"]).Should().BeFalse();
    }

    [Fact]
    public void Audience_Should_KeepBorderRolesOnly_When_TheMetricIsBorderOnly()
    {
        IReadOnlyList<string> every = RoleCodes.AlertRoles;

        BorderAlerts.Audience(AlertMetric.DesksBelowPlan, every).Should().Equal(RoleCodes.BorderShiftSupervisor, RoleCodes.SystemAdministrator);
        BorderAlerts.Audience("DesksBelowPlan", [RoleCodes.TerminalDutyManager]).Should().BeEmpty();
        BorderAlerts.Audience(AlertMetric.DesksBelowPlan, [RoleCodes.HandlerStationManager, null, RoleCodes.SystemAdministrator]).Should().Equal(RoleCodes.SystemAdministrator);
        BorderAlerts.Audience(AlertMetric.Nowcast, every).Should().Equal(every);
        BorderAlerts.Audience("desksbelowplan", [RoleCodes.TerminalDutyManager]).Should().Equal([RoleCodes.TerminalDutyManager],
            "only the exact stored name is a metric; a notice with any other name is dropped before it is routed (AlertNotice.Plausible)");
        BorderAlerts.Audience(AlertMetric.DesksBelowPlan, null).Should().BeEmpty();
        BorderAlerts.Audience(AlertMetric.PredictedNowcast, every).Should().Equal(RoleCodes.BorderShiftSupervisor, RoleCodes.SystemAdministrator);
        BorderAlerts.Audience("PredictedNowcast", [RoleCodes.TerminalDutyManager]).Should().BeEmpty("ARV-187: the predicted nowcast carries the lane split");
        BorderAlerts.Audience("PredictedNowcast", [RoleCodes.HandlerStationManager, RoleCodes.ValidationObserver]).Should().BeEmpty();
        BorderAlerts.Audience("predictednowcast", [RoleCodes.TerminalDutyManager]).Should().Equal([RoleCodes.TerminalDutyManager], "only the exact stored name");
        Enum.GetValues<AlertMetric>().Where(BorderAlerts.IsBorderOnly).Should().Equal(AlertMetric.DesksBelowPlan, AlertMetric.PredictedNowcast);
        Enum.GetNames<AlertMetric>().Where(BorderAlerts.IsBorderOnly).Should().Equal("DesksBelowPlan", "PredictedNowcast");
        BorderAlerts.IsBorderOnly((string)null).Should().BeFalse();
        BorderAlerts.IsBorderRole(null).Should().BeFalse();
    }
}
