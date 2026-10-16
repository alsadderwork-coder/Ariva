using System.Text.RegularExpressions;
using Ariva.Core.Services.Displays;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-181b2 and ARV-187 (CWE-863, CWE-200, data boundary): open desks below recommendation and the predicted nowcast are
/// border data, so every place that reads alerts, alert rules, the lane staffing rows or the lane split of the arrival wave
/// is listed here, and each one that serves people applies the border rule (<c>BorderAlerts</c>). A new reader of the
/// <c>alert</c> table, of <c>lane_staffing</c> or of <c>inbound_lane_demand</c>, a new consumer of the arrival wave's lane
/// split, a new router of alert notices, a new alert input that reads the lane split for a metric that is not border-only,
/// or a display board that grows an alert or staffing member fails this test until it is reviewed and added. Tests and the
/// E2E suite are left out (they plant and read rows on purpose).
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
        var readers = Matching(@"\b(?i:FROM|JOIN)\s+(?i:(?:""?public""?\s*\.\s*)?""?alert""?)(?!\w)|\b(Query|QueryAsNoTracking|GetAsync|LoadAsync)<Alert>");

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
        Matching(@"\b(?i:FROM|JOIN)\s+(?i:(?:""?public""?\s*\.\s*)?""?alert_rule""?)(?!\w)|\b(Query|QueryAsNoTracking|GetAsync|LoadAsync)<AlertRule>").Should().Equal(
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
        Matching(@"\b(?i:FROM|JOIN|INTO|LIKE)\s+(?i:(?:""?public""?\s*\.\s*)?""?lane_staffing""?)(?!\w)").Should().Equal(
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

    /// <summary>
    /// ARV-187: the lane split of the arrival wave (AMAN's lane demand per flight, projected per minute and lane) reaches
    /// people through the arrival wave API (lane counts for holders of ArrivalWaveLanes.View only), the staffing job (its rows
    /// are border data, above), and the alert inputs of border-only metrics only. Each place that reads or projects it is
    /// listed, and every metric whose alert input reads it (the predicted nowcast through <c>IArrivalWaveSource</c>, open
    /// desks below recommendation through <c>lane_staffing</c>) must be border-only.
    /// </summary>
    [Fact]
    public void Sources_Should_ProjectTheArrivalWaveLaneSplitOnlyInTheListedPlaces_When_Scanned()
    {
        Matching(@"\b(?i:FROM|JOIN)\s+(?i:(?:""?public""?\s*\.\s*)?""?inbound_lane_demand""?)(?!\w)").Should().Equal(
            ["Backplane/Ariva.Infra/Flights/ArrivingLegs.cs"], "the arriving legs are the one reader of AMAN's lane demand");
        Matching(@"\bArrivingLegs\.ReadAsync\(").Should().Equal(
        [
            // The arrival wave for predicted-breach rules (ProjectedArrivalWave).
            "Backplane/Ariva.Infra/Flights/ArrivingLegs.cs",
            // The arrival wave API: lane counts for holders of ArrivalWaveLanes.View only, totals otherwise.
            "Backplane/Ariva.Infra/Services/Flights/SvcArrivalWave.cs",
            // The staffing job, which writes lane_staffing (border data).
            "Backplane/Ariva.Infra/Staffing/StaffingInputs.cs"
        ], "every reader of the arriving legs is reviewed for the lane split (ARV-187)");
        Matching(@"\bArrivalWave\.Project\(").Should().Equal(
            ["Backplane/Ariva.Core/Staffing/SiteStaffing.cs", "Backplane/Ariva.Infra/Flights/ArrivingLegs.cs", "Backplane/Ariva.Infra/Services/Flights/SvcArrivalWave.cs"]);
        Source("Backplane/Ariva.Infra/Services/Flights/SvcArrivalWave.cs").Should().Contain("Permissions.ViewArrivalWaveLanes", "the API withholds the lanes otherwise");
        Matching(@"\bIArrivalWaveSource\b").Should().Equal(
        [
            // The interface.
            "Backplane/Ariva.Core/Alerting/AlertEvaluator.cs",
            // Its registration.
            "Backplane/Ariva.Di/Extensions/FlightExtensions.cs",
            // Its one consumer: the predicted nowcast's input (live and backtest).
            "Backplane/Ariva.Infra/Alerting/AlertInputs.cs",
            // Its implementation, ProjectedArrivalWave.
            "Backplane/Ariva.Infra/Flights/ArrivingLegs.cs"
        ], "the lane shares of the arrival wave are read by the predicted nowcast only (ARV-187)");
        Matching(@"\.ArrivalsAsync\(").Should().Equal(["Backplane/Ariva.Infra/Alerting/AlertInputs.cs"]);

        var inputs = Source("Backplane/Ariva.Infra/Alerting/AlertInputs.cs");
        Regex.Matches(inputs, @"\barrivals\.ArrivalsAsync\(", RegexOptions.CultureInvariant, RegexTimeout).Should().ContainSingle("one call, in PredictedAsync");
        var predicted = inputs.IndexOf("Task<IReadOnlyList<AlertTargetSeries>> PredictedAsync(", StringComparison.Ordinal);
        predicted.Should().BePositive();
        var call = inputs.IndexOf("arrivals.ArrivalsAsync(", StringComparison.Ordinal);
        var next = inputs.IndexOf("    private ", predicted + 1, StringComparison.Ordinal);
        call.Should().BeGreaterThan(predicted).And.BeLessThan(next < 0 ? int.MaxValue : next, "the wave is read inside PredictedAsync");

        var routed = Regex.Matches(inputs, @"(?<arm>(?:AlertMetric\.\w+\s*(?:or\s+)?)+)=>\s*await\s+(?<reader>PredictedAsync|StaffingAsync)\(",
                RegexOptions.CultureInvariant, RegexTimeout)
            .SelectMany(m => Regex.Matches(m.Groups["arm"].Value, @"AlertMetric\.(\w+)", RegexOptions.CultureInvariant, RegexTimeout).Select(a => a.Groups[1].Value))
            .ToList();
        routed.Should().BeEquivalentTo(["PredictedNowcast", "DesksBelowPlan"], "the two inputs that read the lane split");
        routed.Should().OnlyContain(m => Ariva.Core.Security.BorderAlerts.IsBorderOnly(m), "a metric judged on the lane split is border data");
        Source("Backplane/Ariva.Infra/Alerting/AlertEvaluation.cs").Should().Contain("BorderAlerts.IsBorderOnly(values.Metric) && values.Problems().Count > 0",
            "a stored border rule that fails its checks is never judged");

        // ARV-187 security review: the projection built from the lane shares (AlertInputs.ReadAsync, the predicted value and
        // minute) is held only by the live evaluation and the rule service, and the service's backtest refuses before it
        // projects. A new holder of AlertInputs (a parameter, a field, GetService, typeof or new; comments aside) fails here.
        Matching(@"(?m)^(?!\s*//).*\bAlertInputs\b").Should().Equal(
        [
            // Its registration in the alerting hosts and in Main.
            "Backplane/Ariva.Di/Extensions/AccountExtensions.cs",
            "Backplane/Ariva.Di/Extensions/AlertingExtensions.cs",
            // The live evaluation (system work; every output filtered by BorderAlerts).
            "Backplane/Ariva.Infra/Alerting/AlertEvaluation.cs",
            // The class itself.
            "Backplane/Ariva.Infra/Alerting/AlertInputs.cs",
            // The alert rules API: the backtest and preview.
            "Backplane/Ariva.Infra/Services/Alerting/SvcAlertRules.cs"
        ], "the predicted nowcast's projection is held by the live evaluation and the rule service only (ARV-187)");
        var service = Source("Backplane/Ariva.Infra/Services/Alerting/SvcAlertRules.cs");
        var refusal = service.IndexOf("RefusesBorderAsync(rule.Metric, ct)", StringComparison.Ordinal);
        refusal.Should().BePositive();
        Regex.Matches(service, @"\binputs\.ReadAsync\(", RegexOptions.CultureInvariant, RegexTimeout).Should().ContainSingle("one projection, in the backtest");
        refusal.Should().BeLessThan(service.IndexOf("inputs.ReadAsync(", StringComparison.Ordinal), "the backtest refuses before it projects");
        Matching(@"\bSiteStaffing\.Compute\(").Should().Equal(["Backplane/Ariva.Infra/Staffing/StaffingRecommendations.cs"],
            "the staffing engine runs in the staffing job only, whose rows are border data");
        // The staffing inputs carry each arriving flight's AMAN lane demand (StaffingRead.Input.Flights, ArrivingFlight.Aman):
        // held by their registration, the class and the staffing job only; the staffing API uses the static LanesAsync only.
        Matching(@"(?m)^(?!\s*//).*\bStaffingInputs\b(?!\.(?:LanesAsync|TooMany\w+)\b)").Should().Equal(
        [
            "Backplane/Ariva.Di/Extensions/StaffingExtensions.cs",
            "Backplane/Ariva.Infra/Staffing/StaffingInputs.cs",
            "Backplane/Ariva.Infra/Staffing/StaffingRecommendations.cs"
        ], "the per-flight lane demand is held by the staffing job only (ARV-187)");
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
