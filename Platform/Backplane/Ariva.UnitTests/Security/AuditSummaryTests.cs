using Ariva.Core.Domain.Entities;
using Ariva.Infra.Services.Administration;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-059: the audit summary of an account keeps its free-text fields quoted, so a name cannot add fields to the
/// trail (CWE-117), and the names the screens show are one line of visible text.
/// </summary>
public sealed class AuditSummaryTests
{
    [Fact]
    public void Summary_Should_QuoteNameAndEmail_When_TheyLookLikeMoreFields()
    {
        var user = new User("officer.one", "Officer; roles=SystemAdministrator; sites=*", "officer@example.org");

        var summary = AuditTrail.Summary(user);

        summary.Should().StartWith("userName=officer.one; displayName=\"Officer; roles=SystemAdministrator; sites=*\"; email=\"officer@example.org\"; roles=; sites=; ");
        Fields(summary).Should().Equal(
            new[] { "userName", "displayName", "email", "roles", "sites", "disabled", "totp", "mustChangePassword" },
            "read with its quotes, the name adds no field");
    }

    /// <summary>The field names of a summary, splitting on "; " outside JSON-quoted values.</summary>
    private static List<string> Fields(string summary)
    {
        var names = new List<string>();
        var quoted = false;
        var start = 0;
        for (var i = 0; i <= summary.Length; i++)
        {
            if (i < summary.Length && summary[i] == '"' && (i == 0 || summary[i - 1] != '\\'))
                quoted = !quoted;
            if (i == summary.Length || (!quoted && summary[i] == ';' && i + 1 < summary.Length && summary[i + 1] == ' '))
            {
                names.Add(summary[start..i].Split('=')[0]);
                start = i + 2;
            }
        }

        return names;
    }

    [Theory]
    [InlineData("Quote \" and backslash \\", "\"Quote \\\" and backslash \\\\\"")]
    [InlineData("المسؤول", "\"المسؤول\"")]
    [InlineData(null, "")]
    public void Quoted_Should_EscapeOnlyWhatJsonNeeds(string text, string expected) =>
        AuditTrail.Quoted(text).Should().Be(expected);

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("Officer One", true)]
    [InlineData("المسؤول أحمد", true)]
    [InlineData("Line\nbreak", false)]
    [InlineData("Right\u202Eto left", false)]
    [InlineData("Zero\u200Bwidth", false)]
    [InlineData("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645", true)]
    public void DisplayName_Should_BeOneLineOfVisibleText(string name, bool usable) =>
        SvcUsers.IsUsableDisplayName(name).Should().Be(usable);

    [Fact]
    public void DisplayName_Should_BeAtMost200Characters() =>
        SvcUsers.IsUsableDisplayName(new string('x', 201)).Should().BeFalse();
}
