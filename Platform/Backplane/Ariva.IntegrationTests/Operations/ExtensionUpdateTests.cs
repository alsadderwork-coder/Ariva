using Ariva.Di;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// ARV-236: an existing database moves to the TimescaleDB of a newer image with <c>ariva-db.sh update-extensions</c>. A server
/// is started as the TimescaleDB chart starts one (<see cref="ChartDatabase"/>), its extensions put back to the newest older
/// versions the image can update from (as a database first created under the previous image has them), and the release's
/// migration job runs on them. The check then fails on the old versions. Before any statement as the superuser the update
/// refuses what the migration login could plant for the update scripts (a function and an operator under system names, another
/// extension, a setting for every session of the database), and a session of the runtime login (the
/// hosts are scaled to 0 first). It then moves both extensions to the image's versions with every hypertable, chunk, row and
/// TimescaleDB job kept; a session of another login that held the old library is closed at its next statement while a new
/// one works; the check passes and a second update changes nothing. The command takes no option.
/// </summary>
public sealed class ExtensionUpdateTests : IAsyncLifetime
{
    private const string RuntimeLogin = "ariva_app";
    private const string Superuser = "postgres";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-extensions-" + Guid.NewGuid().ToString("N"));
    private ChartDatabase _server;

    public async ValueTask InitializeAsync()
    {
        var backup = Path.Combine(_workDirectory, "backup");
        Directory.CreateDirectory(backup);
        File.SetUnixFileMode(backup, (UnixFileMode)0b111_111_111);
        _server = await ChartDatabase.StartAsync(backup, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    [Fact(Timeout = 600_000)]
    public async Task UpdateExtensions_Should_MoveTimescaleToTheImagesVersions_When_TheDatabaseWasCreatedUnderAnOlderImage()
    {
        // The command takes no option: no value reaches its statements.
        (await _server.OpsAsync(Ct, "update-extensions", "--version", "1.0")).ExitCode.Should().Be(2);

        // The database as the previous image left it: both extensions at the newest older version this image updates from.
        var shipped = await ShippedVersionsAsync();
        var timescale = await PreviousVersionAsync("timescaledb");
        var toolkit = await PreviousVersionAsync("timescaledb_toolkit");
        await SuperuserAsync("DROP EXTENSION timescaledb_toolkit");
        await SuperuserAsync("DROP EXTENSION timescaledb");
        await SuperuserAsync($"CREATE EXTENSION timescaledb VERSION '{timescale}'");
        await SuperuserAsync($"CREATE EXTENSION timescaledb_toolkit VERSION '{toolkit}'");
        (await InstalledVersionsAsync()).Should().Equal(new Dictionary<string, string>
        {
            ["timescaledb"] = timescale,
            ["timescaledb_toolkit"] = toolkit
        });

        // The release's migration job runs on the old versions (the release updates the extensions after it), and data is there.
        (await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(_server.MigrationSettings(RuntimeLogin, RuntimePassword)).Build(), Ct))
            .Should().Be(0);
        await MigrationAsync("""
            CREATE TABLE it_extension_probe (time_utc timestamptz NOT NULL, value integer NOT NULL);
            SELECT create_hypertable('it_extension_probe', 'time_utc', chunk_time_interval => INTERVAL '1 day');
            INSERT INTO it_extension_probe VALUES (now() - INTERVAL '2 days', 1), (now(), 2);
            """);
        var before = await TimescaleStateAsync();
        // The migration job's pooled connections are the job's own; in a cluster its pod has ended by now.
        NpgsqlConnection.ClearAllPools();

        var stale = await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        stale.ExitCode.Should().NotBe(0, "the extensions are behind the image");
        stale.Stdout.Should().Contain($"FAIL  extension versions|timescaledb {timescale} (the image ships {shipped["timescaledb"]}): run update-extensions")
            .And.Contain($"timescaledb_toolkit {toolkit} (the image ships {shipped["timescaledb_toolkit"]}): run update-extensions");

        // What the migration login could plant for the superuser's update scripts to pick up: a function and an operator under
        // system names, another extension (a trusted one, which the database owner may create) and a setting for every session
        // of the database. Each is named and refused before any superuser statement, and nothing changes.
        await MigrationAsync("CREATE FUNCTION public.lower(character varying) RETURNS text LANGUAGE sql IMMUTABLE AS $$ SELECT 'planted' $$");
        await MigrationAsync("CREATE FUNCTION public.it_extension_contains(text, text) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT true $$");
        await MigrationAsync("CREATE OPERATOR public.@> (LEFTARG = text, RIGHTARG = text, FUNCTION = public.it_extension_contains)");
        await MigrationAsync("CREATE EXTENSION pgcrypto");
        await MigrationAsync("ALTER DATABASE ariva SET work_mem = '8MB'");
        // ARV-262 review: an overload in pg_catalog itself (what CREATE on pg_catalog, left by a forged dump, would let the
        // migration login create), which an exact match of its argument types picks over the system's own.
        await SuperuserAsync("CREATE FUNCTION pg_catalog.format(text, name) RETURNS text LANGUAGE sql IMMUTABLE AS $$ SELECT 'planted' $$");
        var planted = await _server.OpsAsync(Ct, "update-extensions");
        planted.ExitCode.Should().NotBe(0);
        planted.Stderr.Should().Contain("refused before any statement as the superuser: ").And.Contain("function public.lower")
            .And.Contain("operator public.@>").And.Contain("extension pgcrypto").And.Contain("database setting work_mem=8MB")
            .And.Contain("function pg_catalog.format (created after initdb)").And.NotContain("updating");
        await SuperuserAsync("DROP FUNCTION pg_catalog.format(text, name)");
        (await InstalledVersionsAsync()).Should().Equal(new Dictionary<string, string> { ["timescaledb"] = timescale, ["timescaledb_toolkit"] = toolkit });
        await MigrationAsync("ALTER DATABASE ariva RESET work_mem");
        await MigrationAsync("DROP EXTENSION pgcrypto");
        await MigrationAsync("DROP OPERATOR public.@> (text, text)");
        await MigrationAsync("DROP FUNCTION public.it_extension_contains(text, text)");
        await MigrationAsync("DROP FUNCTION public.lower(character varying)");

        // A host's session (the runtime login): refused until the deployments are scaled to 0.
        await using (var host = new NpgsqlConnection(_server.ConnectionString(RuntimeLogin, RuntimePassword)))
        {
            await host.OpenAsync(Ct);
            var busy = await _server.OpsAsync(Ct, "update-extensions");
            busy.ExitCode.Should().NotBe(0);
            busy.Stderr.Should().Contain($"Ariva's logins still have sessions on {ChartDatabase.Database}: {RuntimeLogin} (1)").And.NotContain("updating");
            (await InstalledVersionsAsync()).Should().Equal(new Dictionary<string, string> { ["timescaledb"] = timescale, ["timescaledb_toolkit"] = toolkit });
        }

        // A session of another login (a monitoring client, say) that holds the old library does not stop the update.
        await using var held = new NpgsqlConnection(_server.ConnectionString(Superuser, _server.SuperuserPassword));
        await held.OpenAsync(Ct);
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM it_extension_probe", held))
            (await read.ExecuteScalarAsync(Ct)).Should().Be(2L);

        var update = await _server.OpsAsync(Ct, "update-extensions");
        update.ExitCode.Should().Be(0, update.Stderr + update.Stdout);
        update.Stderr.Should().Contain($"updating timescaledb from {timescale} to {shipped["timescaledb"]}")
            .And.Contain($"updating timescaledb_toolkit from {toolkit} to {shipped["timescaledb_toolkit"]}");
        (await InstalledVersionsAsync()).Should().Equal(shipped);
        (await TimescaleStateAsync()).Should().Be(before, "every hypertable, chunk, row and job is kept");

        // TimescaleDB ends the session that still holds the old library at its next statement; a new session works.
        var ended = async () =>
        {
            await using var again = new NpgsqlCommand("SELECT count(*) FROM it_extension_probe", held);
            await again.ExecuteScalarAsync(Ct);
        };
        (await ended.Should().ThrowAsync<PostgresException>()).Which.InvariantSeverity.Should().Be("FATAL");
        await MigrationAsync("INSERT INTO it_extension_probe VALUES (now() + INTERVAL '2 days', 3)");
        (await ScalarAsync("SELECT count(*)::text FROM it_extension_probe")).Should().Be("3");

        // ARV-262: the probe hypertable is not one of Ariva's. Without it, the database migrated under the older versions and
        // updated holds exactly the objects a fresh migration on the image's versions holds, and the image's members.
        await MigrationAsync("DROP TABLE it_extension_probe");
        var check = await _server.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        check.ExitCode.Should().Be(0, check.Stderr + check.Stdout);
        check.Stdout.Should().Contain("ok    extension versions|TimescaleDB's extensions at the versions the image ships")
            .And.Contain("ok    schema|Ariva's ").And.Contain("ok    extension members|timescaledb ").And.Contain("ok    system functions|as PostgreSQL ");

        var twice = await _server.OpsAsync(Ct, "update-extensions");
        twice.ExitCode.Should().Be(0, twice.Stderr);
        twice.Stderr.Should().Contain("nothing to update");
    }

    /// <summary>The newest version older than the image's that it ships an install script and an update path for.</summary>
    private async Task<string> PreviousVersionAsync(string extension)
    {
        var version = await SuperuserScalarAsync(extension switch
        {
            "timescaledb" => """
                SELECT p.source FROM pg_extension_update_paths('timescaledb') p
                  JOIN pg_available_extensions a ON a.name = 'timescaledb' AND p.target = a.default_version
                  JOIN pg_available_extension_versions v ON v.name = 'timescaledb' AND v.version = p.source
                 WHERE p.path IS NOT NULL AND p.source ~ '^[0-9]+(\.[0-9]+)*$'
                 ORDER BY string_to_array(p.source, '.')::int[] DESC LIMIT 1
                """,
            "timescaledb_toolkit" => """
                SELECT p.source FROM pg_extension_update_paths('timescaledb_toolkit') p
                  JOIN pg_available_extensions a ON a.name = 'timescaledb_toolkit' AND p.target = a.default_version
                  JOIN pg_available_extension_versions v ON v.name = 'timescaledb_toolkit' AND v.version = p.source
                 WHERE p.path IS NOT NULL AND p.source ~ '^[0-9]+(\.[0-9]+)*$'
                 ORDER BY string_to_array(p.source, '.')::int[] DESC LIMIT 1
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(extension))
        });
        version.Should().MatchRegex(@"^[0-9]+(\.[0-9]+)+$", $"the image ships an older {extension} it can update from");
        return version;
    }

    private async Task<Dictionary<string, string>> InstalledVersionsAsync() => Versions(await ScalarAsync(
        "SELECT string_agg(name || '=' || installed_version, ';' ORDER BY name) FROM pg_available_extensions WHERE name IN ('timescaledb', 'timescaledb_toolkit')"));

    private async Task<Dictionary<string, string>> ShippedVersionsAsync() => Versions(await ScalarAsync(
        "SELECT string_agg(name || '=' || default_version, ';' ORDER BY name) FROM pg_available_extensions WHERE name IN ('timescaledb', 'timescaledb_toolkit')"));

    private static Dictionary<string, string> Versions(string rows) => rows.Split(';').Select(r => r.Split('=')).ToDictionary(p => p[0], p => p[1]);

    private Task<string> TimescaleStateAsync() => ScalarAsync("""
        SELECT (SELECT count(*) FROM timescaledb_information.hypertables) || ' hypertables, '
            || (SELECT count(*) FROM timescaledb_information.chunks) || ' chunks, '
            || (SELECT count(*) FROM it_extension_probe) || ' rows, '
            || (SELECT string_agg(proc_name || ':' || hypertable_name, ',' ORDER BY proc_name, hypertable_name) FROM timescaledb_information.jobs WHERE hypertable_name IS NOT NULL)
        """);

    /// <summary>A statement as the migration login in a session of its own.</summary>
    private async Task MigrationAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(_server.ConnectionString(ChartDatabase.MigrationLogin, _server.MigrationPassword));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string> ScalarAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(_server.ConnectionString(ChartDatabase.MigrationLogin, _server.MigrationPassword));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (string)await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>
    /// A statement as the database superuser over the pod's socket, in a psql session of its own: a session that has loaded
    /// one version of TimescaleDB's library cannot create another.
    /// </summary>
    private async Task SuperuserAsync(string sql)
    {
        var result = await Psql(sql);
        result.ExitCode.Should().Be(0, $"{sql}: {result.Stderr}");
    }

    private async Task<string> SuperuserScalarAsync(string sql)
    {
        var result = await Psql(sql);
        result.ExitCode.Should().Be(0, result.Stderr);
        return result.Stdout.Trim();
    }

    private Task<ExecResult> Psql(string sql) => _server.ExecAsync(Ct, "psql", "--no-psqlrc", "--quiet", "--tuples-only", "--no-align", "--set=ON_ERROR_STOP=1",
        "--host=/var/run/postgresql", "--username=" + Superuser, "--dbname=" + ChartDatabase.Database, "--command=" + sql);
}
