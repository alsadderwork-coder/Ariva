using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ariva.Core.Queueing;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-117 (CWE-862, CWE-863): the shadow nowcast without AMAN inputs is written by the stream and read only by the
/// validation comparison (ARV-104f, not built yet). No new read path exists: no type outside the stream's write path can
/// hold it (field graph over every Ariva assembly), the live snapshot, the hub, displays, alert inputs, reports and view
/// models have no shadow member, and no source outside the stream store names its columns. A read path added later must
/// change these tests on purpose, with its own authorization.
/// </summary>
public sealed class ShadowNowcastExposureTests
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(Ariva.Core._IAssemblyMark).Assembly,
        typeof(Ariva.Infra._IAssemblyMark).Assembly,
        typeof(Ariva.Di._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Common._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Integration._IAssemblyMark).Assembly,
        typeof(Ariva.Business.Contracts._IAssemblyMark).Assembly
    ];

    // The stream's write path: the engine and its outputs (the replay ledger reads the outputs but never serialises the
    // shadow, ShadowNowcastTests); the checkpoint, the worker and the store are added in the test.
    private static readonly string[] WritePath =
    [
        "Ariva.Core.Queueing.ShadowNowcast",
        "Ariva.Core.Queueing.QueueLiveMinute",
        "Ariva.Core.Queueing.ZoneOutputs",
        "Ariva.Core.Queueing.ZoneProcessor"
    ];

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<Type> Mentioned(Type type)
    {
        if (type.IsArray || type.IsByRef || type.IsPointer)
        {
            foreach (var t in Mentioned(type.GetElementType()!))
                yield return t;
            yield break;
        }

        yield return type;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var t in Mentioned(argument))
                    yield return t;
    }

    // The outermost type a compiler-generated type (a closure, an async state machine) belongs to.
    private static Type Owner(Type type)
    {
        while (type.DeclaringType is not null && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            type = type.DeclaringType;
        return type;
    }

    /// <summary>Every Ariva type whose instance or static fields can hold a shadow nowcast, directly or through other types.</summary>
    private static HashSet<Type> Holders()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var types = Assemblies.SelectMany(TypesOf).Distinct().ToList();
        var fields = types.ToDictionary(t => t, t =>
        {
            var mentioned = new HashSet<Type>();
            for (var current = t; current is not null && current != typeof(object); current = current.BaseType)
                foreach (var field in current.GetFields(all))
                    foreach (var m in Mentioned(field.FieldType))
                        mentioned.Add(m.IsGenericType ? m.GetGenericTypeDefinition() : m);
            return mentioned;
        });
        var holders = new HashSet<Type> { typeof(ShadowNowcast) };
        bool grown;
        do
        {
            grown = false;
            foreach (var (type, mentioned) in fields)
            {
                if (!holders.Contains(type) && mentioned.Any(holders.Contains))
                    grown = holders.Add(type) || grown;
            }
        }
        while (grown);

        return holders;
    }

    [Fact]
    public void Holders_Should_StayOnTheStreamsWritePath_When_TheShadowIsComputed()
    {
        var owners = Holders().Select(Owner).Select(t => t.FullName).Distinct().Order(StringComparer.Ordinal).ToList();

        owners.Should().BeEquivalentTo(WritePath.Concat(
        [
            "Ariva.Infra.Streaming.StreamCheckpoint",
            "Ariva.Infra.Streaming.StreamStore",
            "Ariva.Infra.Streaming.QueueStreamWorker"
        ]), "only the engine and the stream's checkpoint, worker and store may hold the shadow nowcast");
    }

    [Fact]
    public void ExposedTypes_Should_NotHoldTheShadow_When_TheyServeScreensRulesOrReports()
    {
        var holders = Holders();
        var exposed = Assemblies.SelectMany(TypesOf).Where(t => t.FullName is { } name && (
            name == typeof(Ariva.Infra.Live.LiveZoneSnapshot).FullName ||
            name.StartsWith("Ariva.Infra.Live.", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Api.Main.Hubs.", StringComparison.Ordinal) ||
            name.Contains(".Displays.", StringComparison.Ordinal) ||
            name.Contains(".Alerting.", StringComparison.Ordinal) ||
            name.Contains(".Reports.", StringComparison.Ordinal) ||
            name.Contains(".ViewModels.", StringComparison.Ordinal) ||
            name.EndsWith("ViewModel", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Api.Main.Controllers.", StringComparison.Ordinal) ||
            name.StartsWith("Ariva.Business.Contracts.", StringComparison.Ordinal))).ToList();

        exposed.Should().Contain(typeof(Ariva.Infra.Live.LiveZoneSnapshot)).And.Contain(typeof(Ariva.Infra.Alerting.AlertInputs))
            .And.Contain(t => t.FullName == "Ariva.Api.Main.Hubs.LiveHub").And.Contain(typeof(Ariva.Core.Reports.ReportMinute));
        exposed.Where(holders.Contains).Should().BeEmpty("the live snapshot, the hub, displays, alert inputs, reports and view models never carry the shadow");
        exposed.SelectMany(t => t.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Shadow", StringComparison.OrdinalIgnoreCase)).Select(m => $"{t.FullName}.{m.Name}"))
            .Should().BeEmpty("no member of a screen, rule or report type is named after the shadow");
    }

    [Fact]
    public void Members_Should_BeNamedAfterTheShadowOnlyOnTheWritePath_When_EveryArivaTypeIsReflected()
    {
        // ARV-117 second review: every member (field, property, method, event, nested type, including compiler-generated
        // closure and state machine fields) of every Ariva type is checked, not only the exposed ones. A property such as
        // ShadowNowcastMinutes on an entity or a row type would be mapped to shadow_nowcast_minutes by a naming convention
        // and read without the column name ever appearing in source: it fails here, however its name was built.
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var allowed = WritePath.Concat(["Ariva.Infra.Streaming.StreamCheckpoint", "Ariva.Infra.Streaming.StreamStore", "Ariva.Infra.Streaming.QueueStreamWorker"])
            .ToHashSet(StringComparer.Ordinal);
        var named = Assemblies.SelectMany(TypesOf)
            .SelectMany(t => t.GetMembers(all).Where(m => m.Name.Contains("Shadow", StringComparison.OrdinalIgnoreCase))
                .Select(m => (Owner: Owner(t).FullName, Member: $"{t.FullName}.{m.Name}")))
            .ToList();

        named.Should().Contain(n => n.Member == "Ariva.Core.Queueing.QueueLiveMinute.Shadow", "the reflection sees the write path's own member");
        named.Where(n => !allowed.Contains(n.Owner)).Select(n => n.Member).Should().BeEmpty(
            "only the engine, its outputs and the stream's checkpoint, worker and store have members named after the shadow");
    }

    [Fact]
    public void Sources_Should_NameTheShadowColumnsOnlyInTheStreamStore_When_Scanned()
    {
        // The columns (script 0041) are written by the stream store; nothing reads them yet. Tests, docs and the script are
        // not production readers.
        // Case-insensitive with optional underscores (ARV-117 second review): the snake_case columns and the PascalCase or
        // camelCase names a convention mapper turns into them (ShadowNowcastMinutes, shadowNoService) are both caught.
        var column = new Regex(@"shadow_?(nowcast|no_?service)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var skip = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.svelte-kit{Path.DirectorySeparatorChar}",
            "Ariva.UnitTests", "Ariva.IntegrationTests", "Ariva.E2E", "Ariva.LoadTests" };
        var extensions = new[] { ".cs", ".ts", ".svelte", ".js", ".mjs", ".sql", ".json" };
        var readers = Directory.EnumerateFiles(RepositoryPaths.Platform, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !skip.Any(s => f.Contains(s, StringComparison.Ordinal)))
            .Where(f => column.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        readers.Should().Equal(
            ["Backplane/Ariva.Core/Queueing/ShadowNowcast.cs", "Backplane/Ariva.Core/Queueing/ZoneProcessor.cs", "Backplane/Ariva.Infra/Streaming/StreamStore.cs",
                "Backplane/Ariva.Infra/Timescale/Scripts/0041_shadow_nowcast.sql"],
            "only the engine names the shadow and only the stream store writes its columns; no service, screen, rule or report reads them");

        // The value itself: only the engine that computes it and the store that writes it name it.
        // \bShadow\s*: catches property patterns ({ Shadow: var s }) and named arguments that reach it without a member access.
        var value = new Regex(@"\bShadowNowcasts?\b|\.Shadow\b|\bShadow\s*:", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Directory.EnumerateFiles(RepositoryPaths.Platform, "*.cs", SearchOption.AllDirectories)
            .Where(f => !skip.Any(s => f.Contains(s, StringComparison.Ordinal)) && value.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepositoryPaths.Platform, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .Should().Equal(["Backplane/Ariva.Core/Queueing/ShadowNowcast.cs", "Backplane/Ariva.Core/Queueing/ZoneProcessor.cs", "Backplane/Ariva.Infra/Streaming/StreamStore.cs"]);
    }

    #region Wildcard reads of queue_minute

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // queue_minute as a whole word: stage_queue_minute (the store's staging table) and queue_minute_15m are other relations.
    private static readonly Regex QueueMinute = new(@"\bqueue_minute\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    // queue_minute as written in SQL: an optional schema prefix and optional double quotes (public.queue_minute,
    // "queue_minute"), never inside a string literal ('queue_minute'::regclass names the table, it reads no row).
    private const string QueueMinuteName = @"(?:""?[A-Za-z_][A-Za-z0-9_]*""?\s*\.\s*)?""?queue_minute""?(?![A-Za-z0-9_'])";

    // Reads that carry every column of a row, so they would reach the shadow columns without naming them: SELECT *,
    // RETURNING *, alias.*, the JSON functions of a row, COPY of the table (schema prefix and quotes allowed) and the TABLE
    // command (TABLE queue_minute is SELECT * FROM queue_minute; ALTER, CREATE, LOCK TABLE and the like, and ON TABLE in
    // GRANT, REVOKE or COMMENT, are not reads).
    private static readonly Regex Wildcard = new(
        @"\bSELECT\s+(?:DISTINCT\s+|ALL\s+)?\*|\bRETURNING\s+\*|\b[A-Za-z_][A-Za-z0-9_]*\s*\.\s*\*|\brow_to_json\b|\bto_jsonb?\s*\(|\bjsonb?_agg\s*\(\s*[A-Za-z_][A-Za-z0-9_]*\s*\)" +
        @"|\bCOPY\s+" + QueueMinuteName +
        @"|(?<!\b(?:ALTER|CREATE|DROP|LOCK|TRUNCATE|ON|TEMP|TEMPORARY|UNLOGGED|FOREIGN)\s+)\bTABLE\s+(?:ONLY\s+)?" + QueueMinuteName,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    // A reference to queue_minute in a statement, with the alias declared for it if any (FROM queue_minute q,
    // JOIN public.queue_minute AS q, UPDATE "queue_minute" q).
    private static readonly Regex QueueMinuteReference = new(
        @"(?<![A-Za-z0-9_.'""])(?<table>" + QueueMinuteName + @")(?:\s+(?:AS\s+)?""?(?<alias>[A-Za-z_][A-Za-z0-9_]*)""?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    // Words that may follow a table name and so are not its alias. A word missing here only adds findings: it is taken
    // for an alias, and its other uses in the statement are flagged.
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALTER", "AND", "AS", "ATTACH", "CLUSTER", "CROSS", "DEFAULT", "DETACH", "DISABLE", "DO", "DROP", "ENABLE", "EXCEPT", "FETCH",
        "FOR", "FORCE", "FROM", "FULL", "GROUP", "HAVING", "IN", "INHERIT", "INNER", "INTERSECT", "IS", "JOIN", "LATERAL", "LEFT", "LIMIT",
        "NATURAL", "NO", "NOT", "OF", "OFFSET", "ON", "OR", "ORDER", "OUTER", "OVERRIDING", "OWNER", "RENAME", "REPLICA", "RESET", "RETURNING",
        "RIGHT", "SELECT", "SET", "TABLESAMPLE", "TO", "UNION", "USING", "VALIDATE", "VALUES", "WHERE", "WINDOW", "WITH"
    };

    // The words after which queue_minute is a table reference rather than a value: FROM, JOIN, INTO, UPDATE, TABLE (whose
    // read form the Wildcard rule flags), ONLY, COPY, ON (GRANT and REVOKE), EXISTS, LATERAL.
    private static readonly Regex TableContext = new(@"\b(?:FROM|JOIN|INTO|UPDATE|TABLE|ONLY|COPY|ON|EXISTS|LATERAL)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex ColumnAccess = new(@"^\s*\.", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// Whether a statement uses a whole row of queue_minute through its name or an alias declared for it (ARV-117 second
    /// review): any use that is not followed by <c>.column</c> and is not the declaration itself (SELECT q, f(q),
    /// f(q ORDER BY ...), (q).*, q::text, json_build_object('r', q), array_agg(q), q IS NOT NULL). Parameters (:q, @q) and
    /// string literals are not uses. The bare table name is a use unless it follows a word that makes it a table
    /// reference, so a comma-separated table list (FROM a, queue_minute) is flagged too: write it as a JOIN.
    /// </summary>
    private static bool UsesAWholeRow(string statement)
    {
        var declarations = new HashSet<int>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match reference in QueueMinuteReference.Matches(statement))
        {
            var alias = reference.Groups["alias"];
            if (alias.Success && !NotAliases.Contains(alias.Value))
            {
                aliases.Add(alias.Value);
                declarations.Add(alias.Index);
            }

            var table = reference.Groups["table"];
            if (!TableContext.IsMatch(statement[..table.Index]) && !ColumnAccess.IsMatch(statement[(table.Index + table.Length)..]))
                return true;
        }

        foreach (var alias in aliases)
        {
            var uses = new Regex(@"(?<![A-Za-z0-9_.'""$:@])""?(?<name>" + Regex.Escape(alias) + @")""?(?![A-Za-z0-9_'])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            foreach (Match use in uses.Matches(statement))
            {
                if (!declarations.Contains(use.Groups["name"].Index) && !ColumnAccess.IsMatch(statement[(use.Index + use.Length)..]))
                    return true;
            }
        }

        return false;
    }

    private static readonly Regex SqlComment = new(@"--[^\n]*", RegexOptions.CultureInvariant, RegexTimeout);
    // In C#: whole-line // comments, and SQL comments inside command text (two hyphens followed by a blank, so a C#
    // decrement keeps the semicolon after it). Leaving a comment in only adds findings, never hides one.
    private static readonly Regex CsComment = new(@"^[ \t]*//[^\n]*$|--[ \t][^\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// The statements of a source in which queue_minute and a wildcard read meet. A statement ends at a semicolon: in SQL
    /// that is the SQL statement; in C# it is the C# statement, which holds the whole command text however it is built
    /// (a raw literal, concatenation or interpolation), so a wildcard on another table in the same file is not a finding.
    /// Comments are removed first.
    /// </summary>
    internal static IEnumerable<string> WildcardReadsOfQueueMinute(string source, bool sql) =>
        (sql ? SqlComment : CsComment).Replace(source, " ")
        .Split(';')
        .Where(statement => QueueMinute.IsMatch(statement) && (Wildcard.IsMatch(statement) || UsesAWholeRow(statement)))
        .Select(statement => Regex.Replace(statement.Trim(), @"\s+", " ", RegexOptions.None, RegexTimeout));

    [Theory]
    [InlineData("SELECT * FROM queue_minute WHERE zone_key = :k", true)]
    [InlineData("SELECT DISTINCT * FROM queue_minute", true)]
    [InlineData("SELECT q.* FROM queue_minute q", true)]
    [InlineData("SELECT q . * FROM queue_minute AS q", true)]
    [InlineData("SELECT row_to_json(q) FROM queue_minute q", true)]
    [InlineData("SELECT to_json(q) FROM queue_minute q", true)]
    [InlineData("SELECT to_jsonb(q) FROM queue_minute q", true)]
    [InlineData("SELECT json_agg(q) FROM queue_minute q", true)]
    [InlineData("SELECT jsonb_agg( q ) FROM queue_minute q", true)]
    [InlineData("COPY queue_minute TO STDOUT (FORMAT BINARY)", true)]
    [InlineData("UPDATE queue_minute SET status = 'Final' RETURNING *", true)]
    [InlineData("CREATE VIEW leak AS SELECT * FROM queue_minute", true)]
    // ARV-117 second review: whole-row references through an alias or the table name, the TABLE command, COPY with a schema
    // prefix or quotes.
    [InlineData("SELECT q FROM queue_minute q", true)]
    [InlineData("SELECT DISTINCT q FROM public.queue_minute AS q", true)]
    [InlineData("SELECT hstore(q) FROM queue_minute q", true)]
    [InlineData("SELECT jsonb_agg(q ORDER BY q.minute_utc) FROM queue_minute q", true)]
    [InlineData("SELECT (q).* FROM queue_minute q", true)]
    [InlineData("SELECT q::text FROM queue_minute AS q", true)]
    [InlineData("SELECT json_build_object('r', q) FROM queue_minute q", true)]
    [InlineData("SELECT array_agg(q) FROM queue_minute q", true)]
    [InlineData("SELECT zone_key FROM queue_minute q WHERE q IS NOT NULL", true)]
    [InlineData("SELECT json_build_object('r', \"Q\") FROM \"queue_minute\" AS \"Q\"", true)]
    [InlineData("SELECT z.zone_key, array_agg(m) FROM zone z JOIN queue_minute m ON m.zone_key = z.zone_key GROUP BY z.zone_key", true)]
    [InlineData("SELECT queue_minute FROM queue_minute", true)]
    [InlineData("SELECT to_json(queue_minute) FROM queue_minute", true)]
    [InlineData("SELECT (queue_minute).* FROM queue_minute", true)]
    [InlineData("SELECT array_agg(queue_minute) FROM public.queue_minute", true)]
    [InlineData("SELECT zone.code FROM zone, queue_minute", true)]
    [InlineData("TABLE queue_minute", true)]
    [InlineData("TABLE ONLY public.queue_minute", true)]
    [InlineData("CREATE VIEW leak AS TABLE \"queue_minute\"", true)]
    [InlineData("COPY public.queue_minute TO STDOUT (FORMAT BINARY)", true)]
    [InlineData("COPY \"queue_minute\" TO STDOUT", true)]
    [InlineData("COPY \"public\".\"queue_minute\" TO STDOUT", true)]
    [InlineData("COPY (SELECT q FROM queue_minute q) TO STDOUT", true)]
    [InlineData("SELECT q.zone_key, q.nowcast_minutes FROM queue_minute q WHERE q.minute_utc > :q AND q.zone_key = @q", false)]
    [InlineData("SELECT jsonb_agg(q.nowcast_minutes ORDER BY q.minute_utc) FROM public.queue_minute AS q", false)]
    [InlineData("SELECT z.code FROM zone z JOIN \"queue_minute\" m ON m.zone_key = z.zone_key", false)]
    [InlineData("ALTER TABLE queue_minute ADD COLUMN x integer", false)]
    [InlineData("REVOKE DELETE, TRUNCATE ON queue_minute, queue_bin FROM ariva_runtime", false)]
    [InlineData("SELECT create_hypertable('queue_minute', 'minute_utc')", false)]
    [InlineData("SELECT zone_key, count(*) FROM queue_minute GROUP BY zone_key", false)]
    [InlineData("SELECT json_agg(q.nowcast_minutes) FROM queue_minute q", false)]
    [InlineData("INSERT INTO queue_bin SELECT * FROM stage_queue_bin", false)]
    [InlineData("COPY stage_queue_minute FROM STDIN (FORMAT BINARY)", false)]
    [InlineData("SELECT * FROM queue_minute_15m", false)]
    [InlineData("-- SELECT * FROM queue_minute\nSELECT zone_key FROM queue_minute", false)]
    public void WildcardReads_Should_FlagOnlyQueueMinuteWildcards_When_AStatementIsScanned(string statement, bool flagged)
    {
        WildcardReadsOfQueueMinute(statement, sql: true).Any().Should().Be(flagged, statement);
        WildcardReadsOfQueueMinute($"await Execute(connection, \"\"\"\n{statement}\n\"\"\", ct);", sql: false).Any().Should().Be(flagged, statement);
    }

    [Fact]
    public void WildcardReads_Should_StayInTheirStatement_When_AFileHasOthers()
    {
        // StreamStore's staging copies use wildcards beside the upsert into queue_minute, each in its own statement.
        const string source = """
            await Execute(connection, "INSERT INTO queue_minute (zone_key) SELECT zone_key FROM stage_queue_minute", ct);
            await Execute(connection, "INSERT INTO queue_bin SELECT * FROM stage_queue_bin", ct);
            var leak = "SELECT q.* " + "FROM queue_minute q";
            """;

        WildcardReadsOfQueueMinute(source, sql: false).Should().ContainSingle().Which.Should().Contain("q.*");
    }

    [Fact]
    public void Sources_Should_NotReadQueueMinuteThroughAWildcard_When_Scanned()
    {
        // CWE-862, CWE-863 (ARV-117 review): a wildcard read of queue_minute (SELECT *, alias.*, row_to_json, to_json,
        // json_agg of a whole row, COPY queue_minute) would carry the shadow columns without naming them, past the column
        // scan above. The database side (views, materialized views and continuous aggregates over the shadow columns) is
        // checked after the migrations by QueueStreamTests in Ariva.IntegrationTests.
        var skip = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            "Ariva.UnitTests", "Ariva.IntegrationTests", "Ariva.E2E", "Ariva.LoadTests" };
        var scanned = 0;
        var findings = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepositoryPaths.Platform, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f) is ".cs" or ".sql" && !skip.Any(s => f.Contains(s, StringComparison.Ordinal))))
        {
            var source = File.ReadAllText(file);
            if (!QueueMinute.IsMatch(source))
                continue;
            scanned++;
            findings.AddRange(WildcardReadsOfQueueMinute(source, Path.GetExtension(file) == ".sql")
                .Select(s => $"{Path.GetRelativePath(RepositoryPaths.Platform, file).Replace(Path.DirectorySeparatorChar, '/')}: {s}"));
        }

        scanned.Should().BeGreaterThan(5, "the store, the report reader, the alert inputs and the scripts name queue_minute");
        findings.Should().BeEmpty("no production source reads queue_minute through a wildcard");
    }

    #endregion

    #region Script 0041 against the engine

    [Fact]
    public void Script0041_Should_AllowExactlyTheNoServiceReasons_When_ItsCheckIsParsed()
    {
        // The CHECK on shadow_no_service names the reasons literally; a reason added to the enum must be added there too,
        // or the stream's checkpoint would fail on it (23514) and stall.
        var script = File.ReadAllText(Path.Combine(RepositoryPaths.Platform, "Backplane", "Ariva.Infra", "Timescale", "Scripts", "0041_shadow_nowcast.sql"));
        var list = Regex.Match(script, @"shadow_no_service\s+IN\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        list.Success.Should().BeTrue("script 0041 checks shadow_no_service against a literal list");
        var names = Regex.Matches(list.Groups[1].Value, "'([^']*)'", RegexOptions.CultureInvariant, RegexTimeout).Select(m => m.Groups[1].Value).ToList();

        names.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Enum.GetNames<NoServiceReason>());
        names.Should().OnlyContain(n => n.Length <= 20, "shadow_no_service is varchar(20)");
    }

    #endregion
}
