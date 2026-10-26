using Ariva.Core;
using Ariva.Core.Security;
using FluentAssertions;
using static Ariva.Core.Global.Defaults;

namespace Ariva.UnitTests.Reports;

/// <summary>
/// ARV-180 (CWE-863, CWE-200): a report lists alerts only for readers whose roles hold <c>Alert.View</c>. The daily report and
/// the weekly summary read alerts by the alert screens' owner and escalation rule, under which an alert without an owner
/// reaches any role, so a role that reads reports without <c>Alert.View</c> would see those; the permission now decides
/// first. Every role that reads reports holds <c>Alert.View</c> today, which is pinned here so that a seed change shows.
/// </summary>
public sealed class ReportAlertsTests
{
    /// <summary>A role code of the tests' own: in no seed, so it holds no permission, as a role with <c>Report.View</c> only would hold no <c>Alert.View</c>.</summary>
    public const string ReportOnlyRole = "it-report-only";

    [Fact]
    public void Receives_Should_FollowAlertView_When_AskedForEachRoleOfTheSeed()
    {
        foreach (var (role, granted) in RolePermissions.ByRole)
            ReportAlerts.Receives([role]).Should().Be(granted.Contains(Permissions.ViewAlert), role);

        ReportAlerts.Receives([RoleCodes.ValidationObserver]).Should().BeFalse("the validation observer holds no Alert.View");
        ReportAlerts.Receives([ReportOnlyRole]).Should().BeFalse("a role without Alert.View sees no alert in a report");
        ReportAlerts.Receives([ReportOnlyRole, RoleCodes.BorderShiftSupervisor]).Should().BeTrue("any of the reader's roles may hold it");
        ReportAlerts.Receives([]).Should().BeFalse();
        ReportAlerts.Receives(null).Should().BeFalse();
        ReportAlerts.Receives([null]).Should().BeFalse();
    }

    [Fact]
    public void ReportReaders_Should_AllHoldAlertView_When_TheSeedIsAsToday()
    {
        var readers = RolePermissions.ByRole.Where(r => r.Value.Contains(Permissions.ViewReport)).Select(r => r.Key).ToList();

        readers.Should().NotBeEmpty();
        readers.Should().OnlyContain(r => ReportAlerts.Receives(new[] { r }),
            "today every role that reads reports also sees alerts; a role that reads reports only would see none in them (ARV-180)");
    }
}
