using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// security/allowlist.json holds the only exceptions to the security gate. An incomplete entry would hide a finding
/// without a reason or an approver, so CI fails on any missing field.
/// </summary>
public sealed class AllowlistTests
{
    #region Tests

    [Fact]
    public void Validate_Should_FindNoProblem_When_RepositoryAllowlistIsRead()
    {
        var json = File.ReadAllText(RepositoryPaths.Allowlist);

        var problems = SecurityAllowlist.Validate(json, RepositoryPaths.Root);

        problems.Should().BeEmpty("every entry needs rule, path, reason, proposedBy, approvedBy and date (security/README.md)");
    }

    [Fact]
    public void Load_Should_KeepApprovalPending_When_EntryWasProposedByAnAgent()
    {
        var entries = SecurityAllowlist.Load();

        entries.Should().NotBeEmpty();
        entries
            .Where(entry => entry.ProposedBy.StartsWith("Claude", StringComparison.Ordinal))
            .Should()
            .OnlyContain(entry => entry.ApprovedBy.StartsWith("PENDING", StringComparison.Ordinal) || !entry.ApprovedBy.Contains("Claude"),
                "agents may propose exceptions but never approve them");
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("path")]
    [InlineData("reason")]
    [InlineData("proposedBy")]
    [InlineData("approvedBy")]
    [InlineData("date")]
    public void Validate_Should_ReportField_When_RequiredFieldIsMissing(string field)
    {
        var json = CompleteEntryJson().Replace($"\"{field}\":", $"\"x{field}\":", StringComparison.Ordinal);

        var problems = SecurityAllowlist.Validate(json, repositoryRoot: null);

        problems.Should().Contain($"entry 0: '{field}' is missing or empty");
    }

    [Fact]
    public void Validate_Should_ReportRoutes_When_AnonymousEntryListsNoRoute()
    {
        var json = CompleteEntryJson().Replace("\"routes\": [\"/health/liveness\"],", string.Empty, StringComparison.Ordinal);

        var problems = SecurityAllowlist.Validate(json, repositoryRoot: null);

        problems.Should().ContainSingle().Which.Should().Contain("routes");
    }

    [Fact]
    public void Validate_Should_ReportFormat_When_RuleAndDateAreMalformed()
    {
        var json = CompleteEntryJson()
            .Replace("SEC-052", "SEC52", StringComparison.Ordinal)
            .Replace("2026-10-01", "01/10/2026", StringComparison.Ordinal);

        var problems = SecurityAllowlist.Validate(json, repositoryRoot: null);

        problems.Should().HaveCount(2);
    }

    [Fact]
    public void Validate_Should_FindNoProblem_When_EntryIsComplete()
    {
        var problems = SecurityAllowlist.Validate(CompleteEntryJson(), repositoryRoot: null);

        problems.Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private static string CompleteEntryJson() => """
        [
          {
            "rule": "SEC-052",
            "path": "Platform/Backplane/Ariva.Api.Common/HealthChecks/HealthEndpoints.cs",
            "contains": "AllowAnonymous",
            "routes": ["/health/liveness"],
            "reason": "Probes cannot authenticate.",
            "proposedBy": "Claude (test)",
            "approvedBy": "PENDING: Ahmad",
            "date": "2026-10-01"
          }
        ]
        """;

    #endregion
}
