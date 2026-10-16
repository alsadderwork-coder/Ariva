using System.Text.RegularExpressions;
using Ariva.Core.Services.Displays;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-181b2 (CWE-863, data boundary): open desks below recommendation is border data, so every place that reads alerts or the
/// lane staffing rows is listed here, and each one that serves people applies the border rule (<c>BorderAlerts</c>). A new
/// reader of the <c>alert</c> table or of <c>lane_staffing</c>, a new router of alert notices, or a display board that grows an
/// alert or staffing member fails this test until it is reviewed and added. Tests and the E2E suite are left out (they plant
/// and read rows on purpose).
/// </summary>
public sealed class BorderAlertExposureTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] Skip =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        "Ariva.UnitTests", "Ariva.IntegrationTests"
    ];

    /// <summary>The C# sources of the backplane, relative to Platform, whose text matches the pattern.</summary>
    private static List<string> Matching(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        return Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Platform, "Backplane"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Skip.Any(s => f.Contains(s, StringComparison.Ordinal)))
            .Where(f => regex.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepositoryPaths.Platform, relative));

    [Fact]
    public void Sources_Should_ReadAlertsOnlyInTheListedPlaces_When_Scanned()
    {
        var readers = Matching(@"\b(FROM|JOIN)\s+alert\b|\b(Query|QueryAsNoTracking|GetAsync|LoadAsync)<Alert>");

        readers.Should().Equal(
        [
            // The live evaluation: raises, clears, escalates (system work, no person reads here).
            "Backplane/Ariva.Infra/Alerting/AlertEvaluation.cs",
            // Email at enqueue and at send.
            "Backplane/Ariva.Infra/Notifications/AlertEmails.cs",
            // The alerts API (list, get, actions).
            "Backplane/Ariva.Infra/Services/Alerting/SvcAlerts.cs",
            // The daily report, its CSV and deliveries.
            "Backplane/Ariva.Infra/Services/Reports/ReportReader.cs",
            // The weekly summary.
            "Backplane/Ariva.Infra/Services/Reports/WeeklySummaryReader.cs",
            // The pre-pilot stored text check (ARV-159): an operator tool that prints offending code points of notes only.
            "Backplane/Ariva.Infra/Timescale/StoredNameCheck.cs"
        ], "every reader of alerts is reviewed for the border rule (ARV-181b2)");

        foreach (var served in new[]
                 {
                     "Backplane/Ariva.Infra/Notifications/AlertEmails.cs", "Backplane/Ariva.Infra/Services/Alerting/SvcAlerts.cs",
                     "Backplane/Ariva.Infra/Services/Reports/ReportReader.cs", "Backplane/Ariva.Infra/Services/Reports/WeeklySummaryReader.cs"
                 })
            Source(served).Should().Contain("BorderAlerts.", $"{served} serves people, so it applies the border rule");
        Source("Backplane/Ariva.Infra/Notifications/AlertEmails.cs").Should().Contain("(:borderMetrics)").And.Contain("(:borderRoles)",
            "the sender checks the border rule again in its eligibility query");
        Source("Backplane/Ariva.Infra/Services/Reports/ReportReader.cs").Should().Contain("AND (:border OR metric NOT IN (:borderMetrics))");
        Source("Backplane/Ariva.Infra/Services/Reports/WeeklySummaryReader.cs").Should().Contain("AND (:border OR metric NOT IN (:borderMetrics))");
        Source("Backplane/Ariva.Core/Domain/Entities/Alert.cs").Should().Contain("BorderAlerts.IsBorderOnly(Metric)", "Alert.IsFor applies it to every action");
    }

    [Fact]
    public void Sources_Should_ReadAlertRulesOnlyInTheListedPlaces_When_Scanned()
    {
        // ARV-181b2 security review: a new screen or report listing rules would otherwise not trip the alert readers' list.
        Matching(@"\b(FROM|JOIN)\s+alert_rule\b|\b(Query|QueryAsNoTracking|GetAsync|LoadAsync)<AlertRule>").Should().Equal(
        [
            // The live evaluation (system work).
            "Backplane/Ariva.Infra/Alerting/AlertEvaluation.cs",
            // Email: the rule behind an alert (system work; the recipients pass the border rule).
            "Backplane/Ariva.Infra/Notifications/AlertEmails.cs",
            // The alert rules API (search, get, create, change, delete, preview, backtest).
            "Backplane/Ariva.Infra/Services/Alerting/SvcAlertRules.cs",
            // The demo seed (writes the seed's rules; no person reads here).
            "Backplane/Ariva.Infra/Services/Seed/DemoTopologySeed.cs",
            // Site configuration export and import.
            "Backplane/Ariva.Infra/Services/Topology/SvcSiteConfiguration.cs",
            // The pre-pilot stored text check (ARV-159): an operator tool.
            "Backplane/Ariva.Infra/Timescale/StoredNameCheck.cs"
        ], "every reader of alert rules is reviewed for the border rule (ARV-181b2)");

        foreach (var served in new[] { "Backplane/Ariva.Infra/Services/Alerting/SvcAlertRules.cs", "Backplane/Ariva.Infra/Services/Topology/SvcSiteConfiguration.cs" })
            Source(served).Should().Contain("BorderAlerts.", $"{served} serves people, so it applies the border rule");
    }

    [Fact]
    public void Sources_Should_RouteAlertNoticesOnlyByTheirAudience_When_Scanned()
    {
        Matching(@"\bAlertNotice\b").Should().Equal(
        [
            "Backplane/Ariva.Api.Main/Hubs/LiveRelay.cs", "Backplane/Ariva.Infra/Alerting/AlertEvaluation.cs", "Backplane/Ariva.Infra/Live/AlertNotices.cs",
            "Backplane/Ariva.Infra/Services/Alerting/SvcAlerts.cs"
        ], "the hub relay is the one place that sends notices to screens; the evaluation and the alert service publish them");
        Source("Backplane/Ariva.Api.Main/Hubs/LiveRelay.cs").Should().Contain("notice.Audience().Select(role => LiveHub.AlertGroup(notice.SiteCode, role))",
            "a notice reaches the alert groups of its audience only");
        Source("Backplane/Ariva.Infra/Live/AlertNotices.cs").Should().Contain("BorderAlerts.Audience(Metric, roles)");
    }

    [Fact]
    public void Sources_Should_ReadLaneStaffingOnlyInTheListedPlaces_When_Scanned()
    {
        Matching(@"\b(FROM|JOIN|INTO|LIKE)\s+lane_staffing\b").Should().Equal(
        [
            // The open desks below recommendation alert's inputs (live and backtest; outputs filtered by BorderAlerts).
            "Backplane/Ariva.Infra/Alerting/AlertInputs.cs",
            // The staffing API (ArrivalWaveLanes.View only).
            "Backplane/Ariva.Infra/Services/Border/SvcStaffingRecommendations.cs",
            // The job that writes the rows.
            "Backplane/Ariva.Infra/Staffing/StaffingRecommendations.cs"
        ], "lane_staffing is border data (the lane split): every reader is reviewed (ARV-181b2)");
        Source("Backplane/Ariva.Infra/Alerting/AlertInputs.cs").Should().Contain("StaffingReadBack.Plausible(row)", "the alert accepts the rows the API accepts");
        Source("Backplane/Ariva.Infra/Services/Border/SvcStaffingRecommendations.cs").Should().Contain("StaffingReadBack.Plausible");
    }

    [Fact]
    public void DisplayBoard_Should_CarryNoAlertOrStaffingMember_When_Reflected()
    {
        // Passenger displays show nowcasts only (ARV-058): no alert, desk or staffing value reaches a public screen.
        var names = new[] { typeof(DisplayBoardViewModel), typeof(DisplayBoardEntryViewModel) }.SelectMany(t => t.GetProperties()).Select(p => p.Name).ToList();

        names.Should().NotBeEmpty();
        names.Should().NotContain(n => n.Contains("Alert", StringComparison.OrdinalIgnoreCase) || n.Contains("Staffing", StringComparison.OrdinalIgnoreCase) ||
                                       n.Contains("Desk", StringComparison.OrdinalIgnoreCase) || n.Contains("Recommend", StringComparison.OrdinalIgnoreCase));
    }
}
