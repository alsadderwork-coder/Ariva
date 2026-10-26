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

    [Fact]
    public void Check_Should_PinEveryFunctionBodyTheShippedScriptsCreate_When_ItLooksForOthers()
    {
        // ARV-183 (CWE-269) and its review: check passes a function outside the extensions only when it is Ariva's own with a
        // body one of the scripts ships, so its list is every CREATE FUNCTION of the scripts as name:SHA-256 of the body, the
        // bodies a later script replaced included (a backup taken before that script holds the older one). A script that adds
        // or changes a function adds its line here, or check fails after a restore; the expected list is in the message.
        var shipped = SqlScriptCatalog.Embedded()
            .SelectMany(script => FunctionBodies(script.Sql))
            .Select(function => $"{function.Name}:{Sha256(function.Body)}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var listed = ArivaFunctionsList().Match(Script.Value);
        listed.Success.Should().BeTrue("the script lists Ariva's functions in ARIVA_FUNCTIONS");

        listed.Groups["entries"].Value.Replace("\\\n", string.Empty, StringComparison.Ordinal).Split(',').Order(StringComparer.Ordinal)
            .Should().Equal(shipped, "every function body the shipped scripts create, and nothing else:\n" + string.Join(",\\\n", shipped));
        shipped.Should().Contain("ariva_retention_purge:535ad38b539587c302aa4ea85e1c3508d55e5d5df6bf4f0675bbadf0076f78d7", "the body the purge's own check pins");
        shipped.Select(entry => entry[..entry.IndexOf(':', StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal).Should().HaveCountGreaterThan(20);
    }

    [Fact]
    public void FunctionBodies_Should_ReadOnlyDefinitions_When_CommentsStringsAndOtherQuotesHoldTheWords()
    {
        // The reader behind the list above: a CREATE FUNCTION in a comment, a string or a DO block's body is not one, and a body
        // is exactly the text between its own dollar quotes, another tag inside it included.
        const string sql = """
            -- CREATE FUNCTION in_a_comment() AS $$ no $$
            /* CREATE FUNCTION in_a_block() AS $$ no $$ */
            SELECT 'CREATE FUNCTION in_a_string() AS $$ no $$';
            DO $$ BEGIN EXECUTE 'CREATE FUNCTION in_a_do() RETURNS int AS $f$ SELECT 1 $f$'; END $$;
            CREATE OR REPLACE FUNCTION public.Real_One(a int) RETURNS text LANGUAGE plpgsql AS $fn$ BEGIN RETURN '$$'; END $fn$;
            """;

        FunctionBodies(sql).Should().Equal([("real_one", " BEGIN RETURN '$$'; END ")]);
    }

    [Fact]
    public void Superuser_Should_RunOnlyTimescaleDbsFixedStatements_When_TheScriptIsRead()
    {
        // ARV-126 and ARV-236 (CWE-269): the database superuser runs TimescaleDB's restore mode steps and the extensions' update,
        // each a constant statement in a quoted heredoc with no psql variable, option or command string that could carry a value,
        // and pg_restore of a dump whose origin the signed manifest decides. Nothing else runs as the superuser.
        var text = Script.Value;
        var calls = SuperuserCall().Matches(text).ToList();

        calls.Select(call => call.Groups["sql"].Value.Trim()).Should().BeEquivalentTo(
            ["SELECT public.timescaledb_pre_restore();", "SELECT public.timescaledb_post_restore();", "ALTER EXTENSION timescaledb UPDATE;",
             "ALTER EXTENSION timescaledb_toolkit UPDATE;",
             // ARV-183: check reads the ALTER SYSTEM settings, a view only the superuser may read; its review: with nothing named
             // through the search path (the operator qualified, no function), and the restore mode functions with their schema.
             "SELECT DISTINCT s.name FROM pg_catalog.pg_file_settings s WHERE s.sourcefile OPERATOR(pg_catalog.~~) '%/postgresql.auto.conf' ORDER BY 1;"],
            "each superuser session runs one fixed statement");
        calls.Select(call => call.Groups["args"].Value.Trim()).Should().OnlyContain(args => args.Length == 0 || args == "--tuples-only --no-align");
        calls.Select(call => call.Groups["prefix"].Value.Trim()).Should().OnlyContain(prefix => prefix.Length == 0 || prefix == "PGOPTIONS='-c lock_timeout=30s'",
            "nothing but a startup option precedes a call on its line, so the update stays its session's first statement");
        Regex.Matches(text, @"\bas_superuser\b").Count.Should().Be(calls.Count + 1, "as_superuser is defined once and called only as above");

        // Every line of code that names the superuser, signs in to PostgreSQL, dumps or restores: the two helpers, the backup's
        // pg_dump (the migration login), the restore's pg_restore (and its table of contents reads, which open no connection),
        // the setting and its checks. No -U and no PGUSER anywhere.
        var code = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToList();
        code.Where(line => SuperuserName().IsMatch(line)).Should().BeEquivalentTo(
        [
            "SUPERUSER=\"${POSTGRES_USER:-postgres}\"",
            "valid_identifier \"$SUPERUSER\" || die \"the superuser name is not a plain lower-case identifier\"",
            "--host=\"$SOCKET_DIR\" --port=\"$PORT\" --username=\"$SUPERUSER\" --dbname=\"$DATABASE\" \"$@\"",
            "local args=(--exit-on-error --no-password --host=\"$SOCKET_DIR\" --port=\"$PORT\" --username=\"$SUPERUSER\" --dbname=\"$DATABASE\")",
            "--superuser) (($# >= 2)) || usage; SUPERUSER=\"$2\"; shift 2 ;;"
        ]);
        // ARV-183 review: every superuser session has the catalog's search path, a startup option the database owner's settings
        // cannot override (the superuser psql line below), so an object the migration login planted is never picked up.
        code.Where(line => PostgresClient().IsMatch(line)).Should().BeEquivalentTo(
        [
            "PGPASSWORD=\"$MIGRATION_PASSWORD\" PGAPPNAME=ariva-ops PGCONNECT_TIMEOUT=15 psql --no-psqlrc --quiet --no-password \\",
            "PGOPTIONS=\"${PGOPTIONS:+$PGOPTIONS }-c search_path=pg_catalog,pg_temp\" PGAPPNAME=ariva-ops psql --no-psqlrc --quiet --no-password --set=ON_ERROR_STOP=1 \\",
            "PGPASSWORD=\"$MIGRATION_PASSWORD\" PGAPPNAME=ariva-ops pg_dump \"${args[@]}\"",
            "toc=\"$(pg_restore --list)\" || die \"the input is not a custom-format dump\"",
            "toc=\"$(pg_restore --list \"$input\")\" || die \"$input is not a custom-format dump\"",
            "PGAPPNAME=ariva-ops pg_restore \"${args[@]}\"",
            "PGAPPNAME=ariva-ops pg_restore \"${args[@]}\" \"$input\""
        ]);
        code.Should().NotContain(line => line.Contains("PGUSER", StringComparison.Ordinal) || Regex.IsMatch(line, @"(^|\s)-U(\s|$)"));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Each CREATE FUNCTION of a script with its body, the text between its dollar quotes (what PostgreSQL stores as the
    /// function's source). Comments, string literals and dollar-quoted text outside a definition (a DO block) are skipped.
    /// </summary>
    private static List<(string Name, string Body)> FunctionBodies(string sql)
    {
        var found = new List<(string Name, string Body)>();
        string pending = null;
        var i = 0;
        while (i < sql.Length)
        {
            if (string.CompareOrdinal(sql, i, "--", 0, 2) == 0)
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end;
                continue;
            }
            if (string.CompareOrdinal(sql, i, "/*", 0, 2) == 0)
            {
                var end = sql.IndexOf("*/", i, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (sql[i] == '\'')
            {
                var end = i + 1;
                while ((end = sql.IndexOf('\'', end)) >= 0 && end + 1 < sql.Length && sql[end + 1] == '\'')
                    end += 2;
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }
            var quote = DollarQuote().Match(sql, i);
            if (quote.Success)
            {
                var close = sql.IndexOf(quote.Value, i + quote.Length, StringComparison.Ordinal);
                close.Should().BeGreaterThan(0, "every dollar quote is closed");
                if (pending is not null)
                    found.Add((pending, sql[(i + quote.Length)..close]));
                pending = null;
                i = close + quote.Length;
                continue;
            }
            var create = FunctionStart().Match(sql, i);
            if (create.Success && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_')))
            {
                pending = create.Groups["name"].Value.ToLowerInvariant();
                i += create.Length;
                continue;
            }
            i++;
        }
        return found;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

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

    [GeneratedRegex(@"\GCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionStart();

    [GeneratedRegex(@"\G\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$")]
    private static partial Regex DollarQuote();

    [GeneratedRegex(@"^readonly ARIVA_FUNCTIONS=""(?<entries>[a-z0-9_:,\\\n]+)""$", RegexOptions.Multiline)]
    private static partial Regex ArivaFunctionsList();

    [GeneratedRegex(@"^(?<prefix>[^\n]*?)\bas_superuser\b(?<args>[^<\n]*)<<'SQL'[^\n]*\n(?<sql>.*?)\nSQL\n", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex SuperuserCall();

    [GeneratedRegex(@"\bSUPERUSER\b")]
    private static partial Regex SuperuserName();

    [GeneratedRegex(@"\b(psql|pg_restore|pg_dump)\b")]
    private static partial Regex PostgresClient();

    #endregion
}
