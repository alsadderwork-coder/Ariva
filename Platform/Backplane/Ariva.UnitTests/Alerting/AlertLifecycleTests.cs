using Ariva.Core;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-039: an alert's life. Acknowledged from Raised or Escalated; escalated once, from Raised or Acknowledged, by hand
/// or when due (still Raised past the rule's minutes); resolved by hand with a note, or by itself; it never moves back,
/// and a resolved alert stays resolved. Who may see and act: the owner role, the escalation role once escalated, every
/// role without an owner, administrators.
/// </summary>
public sealed class AlertLifecycleTests
{
    private static readonly DateTime Raised = new(2026, 9, 28, 18, 5, 0, DateTimeKind.Utc);

    private static Alert NewAlert(string owner = RoleCodes.BorderShiftSupervisor, string escalateTo = RoleCodes.TerminalDutyManager, int? after = 10)
    {
        var rule = new AlertRule("DMO", 1, new AlertRuleValues("Nowcast above 15 min", ["A-VIS"], AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, 10, 12, 1, 1,
            AlertSeverity.Critical, owner, after, after is null ? null : escalateTo, null, false, true)) { Id = Guid.NewGuid() };
        return new Alert(rule, "A-VIS", null, new AlertTransition(AlertTransitionKind.Raised, Raised, 16.3, null, null));
    }

    [Fact]
    public void Alert_Should_MoveForwardOnly_When_AcknowledgedEscalatedAndResolved()
    {
        var alert = NewAlert();

        alert.Acknowledge("border.sup", Raised.AddMinutes(2), "On it").Should().BeTrue();
        alert.Acknowledge("border.sup", Raised.AddMinutes(3), null).Should().BeFalse("acknowledged once");
        alert.Escalate("border.sup", Raised.AddMinutes(4), "Need more desks").Should().BeTrue();
        alert.Escalate("terminal.dm", Raised.AddMinutes(5), null).Should().BeFalse("escalated once");
        alert.Acknowledge("terminal.dm", Raised.AddMinutes(6), null).Should().BeFalse("an acknowledgement is not rewritten");
        alert.ResolveManually("terminal.dm", Raised.AddMinutes(7), "Desks 9 to 12 opened").Should().BeTrue();
        alert.ResolveManually("terminal.dm", Raised.AddMinutes(8), "Again").Should().BeFalse();
        alert.Escalate("x", Raised.AddMinutes(9), null).Should().BeFalse("a resolved alert stays resolved");

        alert.Should().Match<Alert>(a => a.State == AlertState.Resolved && a.Resolution == AlertResolution.Manual && a.ResolutionNote == "Desks 9 to 12 opened" &&
                                         a.AcknowledgedBy == "border.sup" && a.AcknowledgedNote == "On it" && a.EscalatedNote == "Need more desks" &&
                                         a.EscalatedUtc == Raised.AddMinutes(4) && a.ResolvedBy == "terminal.dm");
    }

    [Fact]
    public void Escalated_Should_BeAcknowledgedByTheEscalationRole_When_ItStayedUnacknowledged()
    {
        var alert = NewAlert(after: 10);

        alert.IsDueForEscalation(Raised.AddMinutes(9)).Should().BeFalse();
        alert.IsDueForEscalation(Raised.AddMinutes(10)).Should().BeTrue("unacknowledged for the rule's 10 minutes");
        alert.Escalate(Alert.AutomaticResolver, Raised.AddMinutes(10), null).Should().BeTrue();
        alert.IsDueForEscalation(Raised.AddMinutes(30)).Should().BeFalse("once");

        alert.IsFor([RoleCodes.TerminalDutyManager]).Should().BeTrue("the escalation role takes it on once escalated");
        alert.Acknowledge("terminal.dm", Raised.AddMinutes(12), null).Should().BeTrue();
        alert.State.Should().Be(AlertState.Acknowledged);
        NewAlert(after: null).IsDueForEscalation(Raised.AddDays(1)).Should().BeFalse("no escalation minutes, no escalation");
        var acknowledged = NewAlert();
        acknowledged.Acknowledge("b", Raised.AddMinutes(1), null);
        acknowledged.IsDueForEscalation(Raised.AddMinutes(30)).Should().BeFalse("an acknowledged alert does not escalate by itself");
    }

    [Fact]
    public void Responsibility_Should_FollowTheOwnerAndTheEscalation_When_RolesAreChecked()
    {
        var owned = NewAlert();
        owned.IsFor([RoleCodes.BorderShiftSupervisor]).Should().BeTrue();
        owned.IsFor([RoleCodes.TerminalDutyManager]).Should().BeFalse("not escalated yet");
        owned.IsFor([RoleCodes.HandlerStationManager]).Should().BeFalse();
        owned.IsFor([RoleCodes.SystemAdministrator]).Should().BeTrue();
        owned.IsFor([]).Should().BeFalse();
        NewAlert(owner: null).IsFor([RoleCodes.HandlerStationManager]).Should().BeTrue("no owner: every role of the site");
    }

    /// <summary>The border-only metrics (ARV-181b2, ARV-187), pinned by <c>BorderAlertsTests</c>.</summary>
    public static TheoryData<AlertMetric> BorderMetrics => [.. Ariva.Core.Security.BorderAlerts.Metrics.Order()];

    /// <summary>
    /// ARV-181b2 and ARV-187 (CWE-863, CWE-200, data boundary): an alert on open desks below recommendation or on the
    /// predicted nowcast is for holders of ArrivalWaveLanes.View only (the border shift supervisor and the administrator, or
    /// an account holding either besides an airport-side role), whatever its owner and escalation roles say; a row without an
    /// owner or with an airport-side owner (from before the story, or changed in the database) reaches no airport-side role,
    /// escalated or not.
    /// </summary>
    [Theory]
    [MemberData(nameof(BorderMetrics))]
    public void IsFor_Should_AnswerBorderRolesOnly_When_TheAlertIsOnABorderMetric(AlertMetric metric)
    {
        var predicted = metric == AlertMetric.PredictedNowcast;
        var rule = new AlertRule("DMO", 6, new AlertRuleValues("Border alert", ["A-VIS"], metric, AlertComparator.GreaterOrEqual, 2, null, null, 3, 2,
            AlertSeverity.Warning, RoleCodes.BorderShiftSupervisor, 10, RoleCodes.SystemAdministrator, null, false, true, predicted ? 30 : null)) { Id = Guid.NewGuid() };
        var owned = new Alert(rule, "A-VIS", null, new AlertTransition(AlertTransitionKind.Raised, Raised, 3, null, predicted ? Raised.AddMinutes(20) : null));
        owned.Metric.Should().Be(metric);

        owned.IsFor([RoleCodes.BorderShiftSupervisor]).Should().BeTrue();
        owned.IsFor([RoleCodes.SystemAdministrator]).Should().BeTrue();
        owned.IsFor([RoleCodes.TerminalDutyManager, RoleCodes.BorderShiftSupervisor]).Should().BeTrue("the account holds a border role");
        owned.IsFor([RoleCodes.TerminalDutyManager]).Should().BeFalse();
        owned.IsFor([RoleCodes.HandlerStationManager]).Should().BeFalse();
        owned.IsFor([RoleCodes.ValidationObserver]).Should().BeFalse();
        owned.IsFor([]).Should().BeFalse();
        owned.IsFor(null).Should().BeFalse();

        foreach (var (owner, escalateTo, escalated) in new (string, string, bool)[]
                 {
                     (null, null, false), (RoleCodes.TerminalDutyManager, null, false), (RoleCodes.HandlerStationManager, RoleCodes.TerminalDutyManager, true),
                     (null, RoleCodes.HandlerStationManager, true)
                 })
        {
            var stored = new StoredAlert(metric, owner, escalateTo, escalated);
            var row = $"owner {owner ?? "none"}, escalated to {escalateTo ?? "none"}";
            stored.IsFor([RoleCodes.TerminalDutyManager]).Should().BeFalse(row);
            stored.IsFor([RoleCodes.HandlerStationManager]).Should().BeFalse(row);
            stored.IsFor([RoleCodes.TerminalDutyManager, RoleCodes.HandlerStationManager]).Should().BeFalse(row);
            stored.IsFor([RoleCodes.SystemAdministrator]).Should().BeTrue(row + ": administrators see every alert of their sites");
            new StoredAlert(AlertMetric.Nowcast, owner, escalateTo, escalated).IsFor([RoleCodes.SystemAdministrator]).Should().BeTrue(row);
        }

        new StoredAlert(metric, null, null, false).IsFor([RoleCodes.BorderShiftSupervisor]).Should().BeTrue("no owner: every border role");
        new StoredAlert(metric, RoleCodes.TerminalDutyManager, null, false).IsFor([RoleCodes.BorderShiftSupervisor])
            .Should().BeFalse("a border role that is neither owner nor escalation role is not responsible");
        new StoredAlert(AlertMetric.Nowcast, null, null, false).IsFor([RoleCodes.TerminalDutyManager]).Should().BeTrue("other metrics keep their rule");
    }

    /// <summary>An alert as a stored row may hold it (no rule check): from before ARV-181b2, or changed in the database.</summary>
    private sealed class StoredAlert : Alert
    {
        public StoredAlert(AlertMetric metric, string owner, string escalateTo, bool escalated)
        {
            Metric = metric;
            OwnerRole = owner;
            EscalateToRole = escalateTo;
            EscalatedUtc = escalated ? Raised.AddMinutes(10) : null;
            State = escalated ? AlertState.Escalated : AlertState.Raised;
            RaisedUtc = Raised;
        }
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", false, true)]
    [InlineData("  ", true, false)]
    [InlineData("Resolved by opening desks", true, true)]
    [InlineData("Line one\nline two", true, true)]
    [InlineData("Bell\u0007", false, false)]
    [InlineData("Flip \u202E", false, false)]
    public void Note_Should_BeCheckedForLengthAndCharacters_When_GivenWithAnAction(string note, bool required, bool valid)
    {
        Alert.IsValidNote(note, required).Should().Be(valid);
        Alert.IsValidNote(new string('n', 501), false).Should().BeFalse();
        Alert.IsValidNote("<script>alert(1)</script>", true).Should().BeTrue("stored and shown as plain text");
    }

    [Fact]
    public void Resolve_Should_NeedANote_When_DoneByHand()
    {
        var alert = NewAlert();

        var noNote = () => alert.ResolveManually("b", Raised.AddMinutes(1), " ");
        noNote.Should().Throw<ArgumentException>();
        alert.State.Should().Be(AlertState.Raised);
        var local = () => alert.Acknowledge("b", DateTime.SpecifyKind(Raised, DateTimeKind.Local), null);
        local.Should().Throw<ArgumentException>("times are UTC");
    }
}
