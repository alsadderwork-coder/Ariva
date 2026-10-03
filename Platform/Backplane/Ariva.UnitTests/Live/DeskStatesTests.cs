using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Infra.Services.Live;
using FluentAssertions;

namespace Ariva.UnitTests.Live;

/// <summary>ARV-055: a desk minute shows the state that held most of it; border desk states are border roles' data, airport desk states the duty manager's.</summary>
public sealed class DeskStatesTests
{
    private static SvcDeskStates.DeskRow Row(double closed = 0, double idle = 0, double serving = 0, double paused = 0, double unknown = 0) =>
        new() { Closed = closed, Idle = idle, Serving = serving, Paused = paused, Unknown = unknown };

    [Theory]
    [InlineData(0, 10, 50, 0, 0, "Serving")]
    [InlineData(0, 40, 20, 0, 0, "Idle")]
    [InlineData(60, 0, 0, 0, 0, "Closed")]
    [InlineData(0, 0, 0, 45, 15, "Paused")]
    [InlineData(0, 0, 0, 0, 60, "Unknown")]
    [InlineData(0, 30, 30, 0, 0, "Serving")]
    [InlineData(0, 0, 0, 0, 0, "Unknown")]
    public void StateOf_Should_TakeTheStateThatHeldMostOfTheMinute_When_ATieGoesToTheMoreActive(double closed, double idle, double serving, double paused, double unknown,
        string expected) =>
        SvcDeskStates.StateOf(Row(closed, idle, serving, paused, unknown)).Should().Be(expected);

    [Fact]
    public void BorderDesks_Should_BeBorderData_When_RolesAreSeeded()
    {
        var permission = Global.Defaults.Permissions.ViewBorderDesks;
        RolePermissions.BorderShiftSupervisor.Should().Contain(permission);
        RolePermissions.SystemAdministrator.Should().Contain(permission);
        RolePermissions.TerminalDutyManager.Should().NotContain(permission, "airport roles see immigration halls as aggregates, never desks");
        RolePermissions.HandlerStationManager.Should().NotContain(permission);
        permission.Code.Should().Be("BorderDesks.View");
    }

    [Fact]
    public void AirportDesks_Should_BeTheDutyManagersView_When_RolesAreSeeded()
    {
        var permission = Global.Defaults.Permissions.ViewAirportDesks;
        RolePermissions.TerminalDutyManager.Should().Contain(permission);
        RolePermissions.SystemAdministrator.Should().Contain(permission);
        RolePermissions.BorderShiftSupervisor.Should().NotContain(permission, "border roles do not see check-in data");
        RolePermissions.HandlerStationManager.Should().NotContain(permission, "a handler sees its own counters only, which needs handler tenancy");
        permission.Code.Should().Be("AirportDesks.View");
    }
}
