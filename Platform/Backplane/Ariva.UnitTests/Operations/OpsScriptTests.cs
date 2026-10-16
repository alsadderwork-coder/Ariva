using System.Text.RegularExpressions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Timescale;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Operations;

/// <summary>
/// ARV-126: scripts/ops/ariva-db.sh stays in step with the code it serves (the compacted topics the relay keeps, the roles
/// the shipped scripts create) and keeps its command lines and SQL free of anything built from a value (CWE-78, CWE-89)
/// and its secrets off every command line (CWE-532). Its behaviour is proven against two TimescaleDB servers by
/// Ariva.IntegrationTests (BackupRestoreTests).
/// </summary>
public sealed partial class OpsScriptTests
{
    #region Fields

    private static readonly Lazy<string> Script = new(() => File.ReadAllText(RepositoryPaths.Resolve("scripts/ops/ariva-db.sh")));

    #endregion

    #region Tests

    [Fact]
    public void CompactedTopics_Should_BeTheTopicsProvisionedWithCompaction_When_Listed()
    {
        var provisioned = TopicCatalog.ForProvisioning(new TopicDefaults { Partitions = 6, ReplicationFactor = 3, MinInSyncReplicas = 2 })
            .Where(spec => spec.Compacted)
            .Select(spec => spec.Name)
            .Order(StringComparer.Ordinal);

        TopicCatalog.CompactedTopics.Should().Equal(provisioned);
        TopicCatalog.CompactedTopics.Should().Contain(Ariva.Core.Messaging.KafkaTopics.TopologyZoneProfileActivated);
    }

    [Fact]
    public void AfterKafkaLoss_Should_ResendExactlyTheCompactedTopics_When_TheScriptListsThem()
    {
        var block = CompactedTopicsBlock().Match(Script.Value);
        block.Success.Should().BeTrue("the script lists the compacted topics in COMPACTED_TOPICS");

        var listed = block.Groups["topics"].Value.Split((char[])['\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        listed.Order(StringComparer.Ordinal).Should().Equal(TopicCatalog.CompactedTopics,
            "the recovery after a Kafka loss re-sends the state of every compacted topic the relay keeps, and nothing else");
    }

    [Fact]
    public void Restore_Should_CreateEveryRoleTheShippedScriptsCreate_When_TheRolesComeFirst()
    {
        var shipped = SqlScriptCatalog.Embedded()
            .SelectMany(script => CreateRole().Matches(script.Sql).Select(m => m.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        shipped.Should().NotBeEmpty();
        foreach (var statement in shipped)
            Script.Value.Should().Contain(statement, "a restore creates each group role exactly as the script that first created it");
        Script.Value.Should().Contain("GRANT ariva_migration TO CURRENT_USER;", "as script 0001 does");
    }

    [Fact]
    public void Script_Should_NeverBuildACommandOrSqlFromAValue_When_Read()
    {
        var text = Script.Value;

        text.Should().StartWith("#!/usr/bin/env bash");
        text.Should().Contain("set -euo pipefail").And.Contain("umask 077");
        ForbiddenShell().Matches(text).Select(m => m.Value).Should().BeEmpty("no eval, no shell started with a string, no backticks");
        // Every SQL text is a quoted heredoc, so the shell expands nothing in it; values reach psql as variables.
        Heredoc().Matches(text).Select(m => m.Groups["tag"].Value).Should().NotBeEmpty().And.OnlyContain(tag => tag == "'SQL'");
        PsqlOutsideHelpers().Matches(text).Should().HaveCount(2, "psql runs only through as_migration and as_superuser");
    }

    [Fact]
    public void Script_Should_KeepTheMigrationPasswordOffEveryCommandLine_When_Read()
    {
        var uses = Script.Value.Split('\n').Where(line => line.Contains("MIGRATION_PASSWORD", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();

        uses.Should().NotBeEmpty();
        uses.Should().OnlyContain(line =>
                line.StartsWith("MIGRATION_PASSWORD=\"${ARIVA_MIGRATION_PASSWORD:-}\"", StringComparison.Ordinal)
                || line.StartsWith("unset ARIVA_MIGRATION_PASSWORD", StringComparison.Ordinal)
                || line.StartsWith("export -n ARIVA_MIGRATION_PASSWORD", StringComparison.Ordinal)
                || line.StartsWith("PGPASSWORD=\"$MIGRATION_PASSWORD\" ", StringComparison.Ordinal)
                || line.StartsWith("[[ -n \"$MIGRATION_PASSWORD\" ]]", StringComparison.Ordinal)
                || line.StartsWith("# ", StringComparison.Ordinal),
            "the password reaches a child only as PGPASSWORD of the migration login's own connections");
    }

    [Fact]
    public void Check_Should_PassOnlyTheRetentionPurgeAsShipped_When_ItLooksForSecurityDefinerFunctions()
    {
        // ARV-111a: the shipped scripts hold exactly one SECURITY DEFINER function, and the check after a restore lets it pass
        // only with the body script 0054 ships (PostgreSQL stores the text between the dollar quotes as the function's source).
        var definers = SqlScriptCatalog.Embedded().Where(script => script.Sql.Contains("SECURITY DEFINER", StringComparison.Ordinal)).Select(script => script.Name);
        definers.Should().Equal(["0054_retention_period.sql"], "a new SECURITY DEFINER function needs its own entry in the restore check");

        var sql = SqlScriptCatalog.Embedded().Single(script => script.Name == "0054_retention_period.sql").Sql;
        var start = sql.IndexOf("AS $$", sql.IndexOf("CREATE FUNCTION ariva_retention_purge(", StringComparison.Ordinal), StringComparison.Ordinal) + "AS $$".Length;
        var body = sql[start..sql.IndexOf("$$", start, StringComparison.Ordinal)];
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));

        Script.Value.Should().Contain($"encode(sha256(convert_to(p.prosrc, 'UTF8')), 'hex') = '{hash}'", "the check pins the purge's body as shipped")
            .And.Contain("p.oid = to_regprocedure('public.ariva_retention_purge(text, integer)') AND p.proowner = (SELECT oid FROM me)")
            .And.Contain("p.proconfig = ARRAY['search_path=pg_catalog, pg_temp', 'TimeZone=UTC']")
            .And.Contain("AND p.proacl IS NOT NULL")
            .And.Contain("WHERE a.grantee NOT IN ((SELECT oid FROM me), 'ariva_runtime'::regrole) OR a.is_grantable",
                "EXECUTE for the owner and ariva_runtime only, without grant option (a NULL ACL means PUBLIC may run it)");
        sql.Should().Contain("SET search_path = pg_catalog, pg_temp\n    SET TimeZone = 'UTC'", "the settings the check pins, in that order");
        sql.Should().Contain("REVOKE ALL ON FUNCTION ariva_retention_purge(text, integer) FROM PUBLIC")
            .And.Contain("GRANT EXECUTE ON FUNCTION ariva_retention_purge(text, integer) TO ariva_runtime", "the grants the check pins");
    }

    #endregion

    #region Patterns

    [GeneratedRegex(@"readonly COMPACTED_TOPICS=\((?<topics>[^)]*)\)")]
    private static partial Regex CompactedTopicsBlock();

    [GeneratedRegex(@"CREATE ROLE ariva_[a-z_]+ NOLOGIN[A-Z ]*;")]
    private static partial Regex CreateRole();

    [GeneratedRegex(@"(^|[\s;|&(])(eval|(ba)?sh\s+-c)\s|`", RegexOptions.Multiline)]
    private static partial Regex ForbiddenShell();

    [GeneratedRegex(@"(?<!<)<<-?\s*(?<tag>'[^']*'|""[^""]*""|[A-Za-z_]+)")]
    private static partial Regex Heredoc();

    [GeneratedRegex(@"\bpsql --no-psqlrc")]
    private static partial Regex PsqlOutsideHelpers();

    #endregion
}
