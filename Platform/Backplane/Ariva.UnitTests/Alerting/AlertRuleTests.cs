using System.Text.Json;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Services.Seed;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;
using ScenarioRule = Ariva.Simulation.Api.Scenarios.Engine.AlertRule;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-037: alert rules are typed data checked on every write (units and ranges per metric, comparators that fit the
/// metric, a clear threshold on the clear side, roles from the catalogue, escalation targets with their minutes, names
/// and contacts without control or invisible characters), and the demo seed's R-001 to R-005 reproduce the prototype:
/// run through the reference scenario's evaluator they raise exactly the alerts of its own seeded rules.
/// </summary>
public sealed class AlertRuleTests
{
    private static AlertRuleValues Nowcast() => new("Nowcast above 15 min", ["A-VIS"], AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, 10, 12, 1, 1,
        AlertSeverity.Critical, RoleCodes.BorderShiftSupervisor, 10, null, "Border operations duty officer", false, true);

    [Fact]
    public void Rule_Should_KeepItsTypedValues_When_Created()
    {
        var rule = new Core.Domain.Entities.AlertRule("DMO", 7, Nowcast() with { Name = "  Visitors wave  ", Zones = ["A-VIS", " A-VIS ", "D-VIS"] });

        rule.Code.Should().Be("R-007");
        rule.Name.Should().Be("Visitors wave");
        rule.Zones.Should().Equal("A-VIS", "D-VIS");
        rule.Values().Should().BeEquivalentTo(Nowcast() with { Name = "Visitors wave", Zones = ["A-VIS", "D-VIS"] });
        new Core.Domain.Entities.AlertRule("DMO", 1234, Nowcast()).Code.Should().Be("R-1234");
        new Core.Domain.Entities.AlertRule("DMO", Core.Domain.Entities.AlertRule.MaxNumber, Nowcast()).Code.Should().Be("R-999999");
        var beyond = () => new Core.Domain.Entities.AlertRule("DMO", Core.Domain.Entities.AlertRule.MaxNumber + 1, Nowcast());
        beyond.Should().Throw<ArgumentOutOfRangeException>("the table's codes stop at six digits");
    }

    [Fact]
    public void AuditSummary_Should_KeepEveryFixedField_When_TheTrailClipsALargeRule()
    {
        var zones = Enumerable.Range(0, Core.Domain.Entities.AlertRule.MaxZones).Select(i => $"Z{i:00}-" + new string('z', 196)).ToList();
        var rule = new Core.Domain.Entities.AlertRule("DMO", 9, Nowcast() with
        {
            Name = "x; owner=SystemAdministrator; enabled=True", Zones = zones, Enabled = false, OwnerRole = RoleCodes.BorderShiftSupervisor
        });

        var summary = rule.AuditSummary();
        var clipped = summary[..Ariva.Core.Domain.Entities.AuditEntry.SummaryLength];

        summary.Length.Should().BeGreaterThan(Ariva.Core.Domain.Entities.AuditEntry.SummaryLength, "the case under test: the zones alone exceed the trail's limit");
        clipped.Should().Contain("\"enabled\":false").And.Contain("\"owner\":\"BorderShiftSupervisor\"").And.Contain("\"zoneCount\":64")
            .And.MatchRegex("\"zonesSha256\":\"[0-9a-f]{64}\"", "the fixed fields and the zones' hash come before the zone list");
        using var parsed = JsonDocument.Parse(summary);
        parsed.RootElement.GetProperty("name").GetString().Should().Be("x; owner=SystemAdministrator; enabled=True", "a name stays one JSON string (CWE-117)");
        parsed.RootElement.GetProperty("owner").GetString().Should().Be(RoleCodes.BorderShiftSupervisor);
    }

    public static TheoryData<string, AlertRuleValues> Refused => new()
    {
        { "empty name", Nowcast() with { Name = " " } },
        { "long name", Nowcast() with { Name = new string('n', 201) } },
        { "control character", Nowcast() with { Name = "Rule\u0007" } },
        { "bidirectional override", Nowcast() with { Name = "Rule ‮evil" } },
        { "zero-width joiner", Nowcast() with { Name = "Ru‍le" } },
        { "no zones", Nowcast() with { Zones = [] } },
        { "too many zones", Nowcast() with { Zones = [.. Enumerable.Range(0, 65).Select(i => $"Z{i}")] } },
        { "blank zone", Nowcast() with { Zones = ["A-VIS", ""] } },
        { "undefined metric", Nowcast() with { Metric = (AlertMetric)99 } },
        { "undefined comparator", Nowcast() with { Comparator = (AlertComparator)99 } },
        { "IsTrue on a number", Nowcast() with { Comparator = AlertComparator.IsTrue } },
        { "no threshold", Nowcast() with { Threshold = null } },
        { "negative threshold", Nowcast() with { Threshold = -1 } },
        { "threshold beyond the unit", Nowcast() with { Threshold = 601 } },
        { "NaN threshold", Nowcast() with { Threshold = double.NaN } },
        { "infinite threshold", Nowcast() with { Threshold = double.PositiveInfinity } },
        { "clear above for a rule that fires above", Nowcast() with { ClearThreshold = 16 } },
        { "clear equal to the threshold", Nowcast() with { ClearThreshold = 15 } },
        { "clear below for a rule that fires below", Nowcast() with { Comparator = AlertComparator.LessThan, ClearThreshold = 12 } },
        { "minimum queue on another metric", Nowcast() with { Metric = AlertMetric.BinP90 } },
        { "negative minimum queue", Nowcast() with { MinQueueLength = -1 } },
        { "threshold on a condition", Nowcast() with { Metric = AlertMetric.SensorOffline, Comparator = AlertComparator.IsTrue, MinQueueLength = null, ClearThreshold = null } },
        { "condition compared above", Nowcast() with { Metric = AlertMetric.OverflowOccupied, Threshold = null, MinQueueLength = null, ClearThreshold = null } },
        { "zero sustain", Nowcast() with { SustainMinutes = 0 } },
        { "long clear", Nowcast() with { ClearAfterMinutes = 121 } },
        { "unknown owner role", Nowcast() with { OwnerRole = "Root" } },
        { "owner role in other case", Nowcast() with { OwnerRole = "bordershiftsupervisor" } },
        { "unknown escalation role", Nowcast() with { EscalateToRole = "Everyone" } },
        { "escalation without minutes", Nowcast() with { EscalateAfterMinutes = null } },
        { "escalation beyond a day", Nowcast() with { EscalateAfterMinutes = 1441 } },
        { "contact with a newline", Nowcast() with { EscalationContact = "Duty\nofficer" } },
        { "long contact", Nowcast() with { EscalationContact = new string('c', 101) } }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Rule_Should_BeRefused_When_AValueIsOutOfItsTypeOrRange(string why, AlertRuleValues values)
    {
        values.Problems().Should().NotBeEmpty(why);
        var create = () => new Core.Domain.Entities.AlertRule("DMO", 1, values);
        create.Should().Throw<ArgumentException>(why);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("'; DROP TABLE alert_rule; --")]
    [InlineData("{{7*7}} ${7*7} #{7*7}")]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("قاعدة الانتظار")]
    [InlineData("../../etc/passwd")]
    public void Rule_Should_KeepAHostileNameAsPlainText_When_ItHasNoControlCharacters(string name)
    {
        var rule = new Core.Domain.Entities.AlertRule("DMO", 1, Nowcast() with { Name = name, EscalationContact = name.Length <= 100 ? name : null });

        rule.Name.Should().Be(name, "names are stored and returned as text; nothing evaluates them (CWE-94)");
        JsonSerializer.Serialize(rule.Name).Should().NotContain("<script>", "JSON output escapes markup");
    }

    [Fact]
    public void Rule_Should_HaveNoExpressionField_When_ItsShapeIsInspected()
    {
        var names = typeof(Core.Domain.Entities.AlertRule).GetProperties().Select(p => p.Name)
            .Concat(typeof(Core.Domain.InputModels.AlertRuleRequest).GetProperties().Select(p => p.Name));

        names.Should().NotContain(n => n.Contains("Expression", StringComparison.OrdinalIgnoreCase) || n.Contains("Script", StringComparison.OrdinalIgnoreCase) ||
                                       n.Contains("Formula", StringComparison.OrdinalIgnoreCase) || n.Contains("Query", StringComparison.OrdinalIgnoreCase));
        typeof(Core.Domain.InputModels.AlertRuleRequest).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name)
            .Should().BeEquivalentTo(["SiteCode", "Name", "Metric", "Comparator", "Severity", "OwnerRole", "EscalateToRole", "EscalationContact"],
                "free text is the name and the contact only; the rest are names checked against closed lists");
    }

    [Fact]
    public void Conditions_Should_TakeIsTrueAndNoThreshold_When_TheyAreTrueOrFalse()
    {
        new AlertRuleValues("Overflow", ["A-VIS"], AlertMetric.OverflowOccupied, AlertComparator.IsTrue, null, null, null, 3, 3, AlertSeverity.Warning,
            null, 15, null, null, false, true).Problems().Should().BeEmpty();
        (Nowcast() with { Comparator = AlertComparator.LessThan, ClearThreshold = 20, MinQueueLength = null }).Problems().Should().BeEmpty();
        (Nowcast() with { Metric = AlertMetric.QueueLength, Threshold = 100_000, MinQueueLength = null, ClearThreshold = 50 }).Problems().Should().BeEmpty();
    }

    [Fact]
    public void SeedRules_Should_RaiseThePrototypesAlerts_When_TheReferenceDayIsEvaluated()
    {
        DemoAlertRules.Values.Should().HaveCount(5).And.OnlyContain(v => v.Problems().Count == 0);
        var reference = ScenarioDay.Run(ScenarioConfig.Reference());
        var mapped = DemoAlertRules.Values.Select((v, i) => ToScenario(Core.Domain.Entities.AlertRule.CodeOf(i + 1), v)).ToList();

        var replayed = ScenarioDay.Run(ScenarioConfig.Reference() with { Rules = mapped });

        string Key(ScenarioAlert a) => $"{a.RuleId}|{a.Zone}|{a.Sensor}|{a.RaisedAt}|{a.ClearedAt}|{a.Severity}|{a.Owner}|{a.EscalateTo}|{a.EscalateAfter}|{a.Bin}";
        replayed.Alerts.Select(Key).Should().Equal(reference.Alerts.Select(Key), "R-001 to R-005 as Ariva rules raise exactly the prototype's alerts");
        reference.Alerts.Should().Contain(a => a.RuleId == "R-001").And.Contain(a => a.RuleId == "R-003").And.Contain(a => a.RuleId == "R-004");
    }

    // The scenario's own vocabulary: metric and comparator names, owner labels, "zone" for the zone's owner and "auto"
    // for the owner's escalation path; the contact stands in for a target outside Ariva's roles.
    private static ScenarioRule ToScenario(string code, AlertRuleValues v)
    {
        static string Label(string role) => role switch
        {
            RoleCodes.BorderShiftSupervisor => "Border shift supervisor",
            RoleCodes.TerminalDutyManager => "Terminal duty manager",
            RoleCodes.HandlerStationManager => "Handler B station manager",
            _ => role
        };
        var metric = v.Metric switch
        {
            AlertMetric.Nowcast => "nowcast",
            AlertMetric.BinP90 => "p90bin",
            AlertMetric.QueueLength => "queue",
            AlertMetric.OverflowOccupied => "overflow",
            AlertMetric.SensorOffline => "sensor",
            _ => "desks"
        };
        var op = v.Comparator switch
        {
            AlertComparator.GreaterThan => "gt",
            AlertComparator.GreaterOrEqual => "ge",
            AlertComparator.LessThan => "lt",
            AlertComparator.LessOrEqual => "le",
            _ => "is"
        };
        var escalateTo = v.EscalateToRole is { } role ? Label(role) : v.EscalationContact is { } contact ? contact.ToLowerInvariant() == "systems" ? "systems" : contact : "auto";
        return new ScenarioRule(code, v.Name, v.Zones, metric, op, v.Threshold, v.MinQueueLength, v.ClearThreshold, v.SustainMinutes, v.ClearAfterMinutes,
            v.Severity.ToString().ToLowerInvariant(), v.OwnerRole is { } owner ? Label(owner) : "zone", v.EscalateAfterMinutes ?? 0, escalateTo, "", v.Enabled);
    }
}
