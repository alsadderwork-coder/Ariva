using System.Text.RegularExpressions;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Persistence;

/// <summary>
/// ARV-111a: docs/privacy/data-inventory.md lists every table, hypertable, continuous aggregate and view Ariva stores data in,
/// with its kind, category, personal data, retention, where the retention is set and its owner. The objects come from the
/// shipped scripts (their CREATE TABLE, create_hypertable, CREATE MATERIALIZED VIEW ... timescaledb.continuous and CREATE VIEW
/// statements, comments removed), from the NHibernate mappings (every mapped class's table and every collection table) and from
/// the script runner (<c>schema_version</c>). A script or a mapping that adds a table the inventory lacks fails here, and so does
/// an inventory row for an object that no longer exists. The parser is deliberately plain: one regular expression per kind of
/// statement (plain materialized views and <c>ALTER ... RENAME TO</c> included), applied in the order the statements appear, and
/// one inventory row per object, its name in backticks in the first cell. It does not read DROP statements (no shipped script
/// drops a table): a script that drops one makes this test ask for its row until the parser learns them.
/// </summary>
public sealed partial class DataInventoryTests
{
    #region Fields

    private const string Inventory = "docs/privacy/data-inventory.md";

    /// <summary>Created by <see cref="SqlScriptRunner"/> itself before the first script, not by a script.</summary>
    private const string RunnerTable = "schema_version";

    /// <summary>The cells of an inventory row: the name, kind, category, personal data, retention, where it is set, owner.</summary>
    private const int Columns = 7;

    #endregion

    #region Parsers

    [GeneratedRegex(@"\bCREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?""?([a-z_][a-z0-9_]*)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"\bcreate_hypertable\s*\(\s*'(?:public\.)?([a-z_][a-z0-9_]*)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateHypertable();

    [GeneratedRegex(@"\bCREATE\s+MATERIALIZED\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?([a-z_][a-z0-9_]*)\s+WITH\s*\(\s*timescaledb\.continuous", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateContinuousAggregate();

    [GeneratedRegex(@"\bCREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+(?:public\.)?([a-z_][a-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateView();

    [GeneratedRegex(@"\bCREATE\s+MATERIALIZED\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?([a-z_][a-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateMaterializedView();

    /// <summary><c>ALTER TABLE | VIEW | MATERIALIZED VIEW name RENAME TO other</c>; never <c>RENAME COLUMN</c>.</summary>
    [GeneratedRegex(@"\bALTER\s+(?:TABLE|VIEW|MATERIALIZED\s+VIEW)\s+(?:IF\s+EXISTS\s+)?(?:public\.)?""?([a-z_][a-z0-9_]*)""?\s+RENAME\s+TO\s+""?([a-z_][a-z0-9_]*)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Rename();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    /// <summary>An inventory row: <c>| `name` | cell | ... |</c>.</summary>
    [GeneratedRegex(@"^\|\s*`([a-z_][a-z0-9_]*)`\s*\|(.*)\|\s*$")]
    private static partial Regex InventoryRow();

    #endregion

    #region Tests

    [Fact]
    public void Inventory_Should_ListEveryTableHypertableAggregateAndView_When_ScriptsAndMappingsCreateThem()
    {
        var stored = StoredObjects();
        var listed = ListedObjects();

        var missing = stored.Keys.Except(listed.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        missing.Should().BeEmpty($"every stored object needs a row in {Inventory} (kind, category, personal data, retention, where set, owner); missing: " +
                                 string.Join(", ", missing.Select(m => $"{m} ({stored[m]})")));
    }

    [Fact]
    public void Inventory_Should_NameOnlyObjectsThatExist_When_Read()
    {
        var stored = StoredObjects();

        var stale = ListedObjects().Keys.Except(stored.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty($"{Inventory} lists objects no script, mapping or the script runner creates");
    }

    [Fact]
    public void Inventory_Should_GiveEachObjectItsKind_When_TheScriptsSayWhatItIs()
    {
        var stored = StoredObjects();
        var listed = ListedObjects();

        foreach (var (name, kind) in stored.Where(s => listed.ContainsKey(s.Key)))
            listed[name].Kind.Should().Be(kind, $"{name} is a {kind.ToLowerInvariant()} in the scripts");
    }

    [Fact]
    public void Inventory_Should_FillEveryColumnOfEveryRow_When_Read()
    {
        foreach (var (name, row) in ListedObjects())
        {
            row.Cells.Should().HaveCount(Columns - 1, $"{name}: kind, category, personal data, retention, where set and owner");
            row.Cells.Should().NotContain(string.Empty, $"{name}: every column is filled");
        }
    }

    [Fact]
    public void Inventory_Should_NameTheMigrationParameter_When_AHypertableRetentionIsConfigurable()
    {
        var listed = ListedObjects();

        foreach (var hypertable in new RetentionSettings().Hypertables())
        {
            listed.Should().ContainKey(hypertable.Table);
            listed[hypertable.Table].Cells[4].Should().Contain("Database:Retention", $"{hypertable.Table}'s retention is a migration-job parameter (ARV-111a)");
        }

        foreach (var dataSet in new RetentionSettings().DataSets())
        {
            listed.Should().ContainKey(dataSet.DataSet);
            listed[dataSet.DataSet].Cells[4].Should().Contain("Database:Retention", $"{dataSet.DataSet}'s period is a migration-job parameter (ARV-111a)");
        }
    }

    [Fact]
    public void StoredObjects_Should_FindTablesHypertablesAggregatesAndViews_When_TheParserReadsAScript()
    {
        // The parser on a script of every kind of statement it reads, comments and temporary tables included.
        var found = Parse("""
            -- CREATE TABLE commented_out (id int);
            CREATE TABLE plain_one (id int);
            CREATE TABLE IF NOT EXISTS "quoted_one" (id int);
            CREATE TEMP TABLE staging_one (id int) ON COMMIT DROP;
            DO $$ BEGIN
                PERFORM create_hypertable('plain_one', 'time_utc');
                EXECUTE 'CREATE MATERIALIZED VIEW plain_15m WITH (timescaledb.continuous) AS SELECT 1';
            END $$;
            CREATE VIEW plain_view AS SELECT 1;
            CREATE MATERIALIZED VIEW plain_mv AS SELECT 1;
            CREATE TABLE old_name (id int);
            ALTER TABLE old_name RENAME TO new_name;
            ALTER TABLE quoted_one RENAME COLUMN id TO other_id;
            CREATE TABLE old_name (id int);
            ALTER MATERIALIZED VIEW plain_mv RENAME TO plain_mv_v2;
            """);

        found.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["plain_one"] = "Hypertable",
            ["quoted_one"] = "Table",
            ["plain_15m"] = "Continuous aggregate",
            ["plain_view"] = "View",
            ["plain_mv_v2"] = "Materialized view",
            ["new_name"] = "Table",
            ["old_name"] = "Table"
        }, "renames follow the order of the statements, so a name created again after a rename is its own table");
    }

    [Fact]
    public void MappedTables_Should_ComeFromTheNHibernateMappings_When_TheConfigurationIsBuilt()
    {
        // The mapping half of the check is real: the configuration's classes and collections, quoted names unquoted.
        var mapped = MappedTables().ToList();

        mapped.Should().Contain(["user", "user_session", "zone_profile", "audit_entry", "email_message"]);
        mapped.Should().HaveCountGreaterThan(30).And.OnlyHaveUniqueItems();
    }

    #endregion

    #region Helpers

    private sealed record Row(string Kind, IReadOnlyList<string> Cells);

    /// <summary>Every object Ariva stores data in, with its kind.</summary>
    private static Dictionary<string, string> StoredObjects()
    {
        var objects = Parse(string.Join("\n", SqlScriptCatalog.Embedded().Select(s => s.Sql)));
        foreach (var table in MappedTables())
            objects.TryAdd(table, "Table");
        objects.TryAdd(RunnerTable, "Table");
        return objects;
    }

    /// <summary>The objects a script text creates, by name, applying its statements in the order they appear.</summary>
    private static Dictionary<string, string> Parse(string sql)
    {
        var text = LineComment().Replace(sql, string.Empty);
        static string Name(Match match, int group) => match.Groups[group].Value.ToLowerInvariant();

        // (position, order at the same position, effect): a continuous aggregate is also a materialized view at the same place.
        var statements = new List<(int At, int Order, Action<Dictionary<string, string>> Apply)>();
        foreach (Match match in CreateTable().Matches(text))
            statements.Add((match.Index, 0, objects => objects.TryAdd(Name(match, 1), "Table")));
        foreach (Match match in CreateView().Matches(text))
            statements.Add((match.Index, 0, objects => objects[Name(match, 1)] = "View"));
        foreach (Match match in CreateMaterializedView().Matches(text))
            statements.Add((match.Index, 0, objects => objects[Name(match, 1)] = "Materialized view"));
        foreach (Match match in CreateContinuousAggregate().Matches(text))
            statements.Add((match.Index, 1, objects => objects[Name(match, 1)] = "Continuous aggregate"));
        foreach (Match match in CreateHypertable().Matches(text))
            statements.Add((match.Index, 0, objects => objects[Name(match, 1)] = "Hypertable"));
        foreach (Match match in Rename().Matches(text))
        {
            statements.Add((match.Index, 0, objects =>
            {
                if (objects.Remove(Name(match, 1), out var kind))
                    objects[Name(match, 2)] = kind;
            }));
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var statement in statements.OrderBy(s => s.At).ThenBy(s => s.Order))
            statement.Apply(found);
        return found;
    }

    /// <summary>The tables of the NHibernate mappings of Ariva.Core's entities: each class's table and each collection table.</summary>
    private static IEnumerable<string> MappedTables()
    {
        var configuration = new NHibernateSessionFactoryProvider(new DatabaseSettings()).Configuration;
        return configuration.ClassMappings.Select(c => c.Table.Name)
            .Concat(configuration.CollectionMappings.Select(c => c.CollectionTable.Name))
            .Select(name => name.Trim('`', '"').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>The inventory's rows by object name; a name listed twice fails.</summary>
    private static Dictionary<string, Row> ListedObjects()
    {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(RepositoryPaths.Resolve(Inventory)))
        {
            var match = InventoryRow().Match(line);
            if (!match.Success)
                continue;
            var cells = match.Groups[2].Value.Split('|').Select(c => c.Trim()).ToList();
            var name = match.Groups[1].Value;
            rows.ContainsKey(name).Should().BeFalse($"{name} has one row in {Inventory}");
            rows[name] = new Row(cells.Count > 0 ? cells[0] : string.Empty, cells);
        }

        rows.Should().NotBeEmpty($"{Inventory} has its table of objects");
        return rows;
    }

    #endregion
}
