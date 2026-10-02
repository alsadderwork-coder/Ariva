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

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", false, true)]
    [InlineData("  ", true, false)]
    [InlineData("Resolved by opening desks", true, true)]
    [InlineData("Line one\nline two", true, true)]
    [InlineData("Bell\u0007", false, false)]
    [InlineData("Flip ‮", false, false)]
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
