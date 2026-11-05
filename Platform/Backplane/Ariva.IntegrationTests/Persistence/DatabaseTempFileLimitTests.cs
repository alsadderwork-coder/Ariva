using System.Globalization;
using System.Security.Cryptography;
using Ariva.Di;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-290 (CWE-400) against PostgreSQL 17 with TimescaleDB: the runtime login's temporary files are capped on the server
/// (<c>temp_file_limit</c>, set on the login by the migration job through script 0069), so a statement whose sorts spill past the
/// cap is ended with 53400 instead of filling the database's disk, a statement within it runs, and the login cannot lift the cap
/// itself. The migration job sets the configured cap on the runtime login only (a migration login that may not set it, a
/// superuser setting, is <c>GrantSettingsTests</c>' case, on a server started as the chart starts one). Roles are cluster-wide,
/// so every test uses login names of its own.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseTempFileLimitTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Secret() => "tf-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    [Fact]
    public async Task TempFileLimit_Should_EndAStatementWhoseTemporaryFilesPassIt_When_TheMigrationSetsIt()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.TempFileLimit);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        const string login = "it_tf_app";
        var password = Secret();
        await ExecuteAsync(admin, "SELECT ariva_ensure_runtime_login(@name, @password)", ("name", login), ("password", password));
        // One megabyte here, so the test is quick; the migration job takes Database:TempFileLimitMegabytes (64 to 102,400).
        await ExecuteAsync(admin, "SELECT ariva_set_runtime_temp_file_limit(@name, @megabytes)", ("name", login), ("megabytes", 1));
        var asRuntime = postgres.ConnectionString(database, login, password);

        // With work_mem at its least, sorting two million numbers spills tens of megabytes; sorting a thousand spills nothing.
        var past = () => ExecuteAsync(asRuntime, "SET work_mem = '64kB'; SELECT count(*) FROM (SELECT g FROM generate_series(1, 2000000) g ORDER BY g DESC OFFSET 0) s");
        var within = () => ExecuteAsync(asRuntime, "SET work_mem = '64kB'; SELECT count(*) FROM (SELECT g FROM generate_series(1, 1000) g ORDER BY g DESC OFFSET 0) s");

        (await past.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ConfigurationLimitExceeded,
            "the server ended the statement at the cap instead of filling the disk");
        await within.Should().NotThrowAsync();
        (await ScalarAsync(asRuntime, "SHOW temp_file_limit")).Should().Be("1MB");
        (await ScalarAsync(admin, "SHOW temp_file_limit")).Should().Be("-1", "the migration login keeps no cap, so long scripts still run");

        // A superuser setting: the runtime login cannot lift it for its session, nor call the function (CWE-269).
        var lift = () => ExecuteAsync(asRuntime, "SET temp_file_limit = -1");
        (await lift.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        var call = () => ExecuteAsync(asRuntime, "SELECT ariva_set_runtime_temp_file_limit('it_tf_app', 102400)");
        (await call.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await ScalarAsync(admin, "SELECT has_function_privilege('public', 'ariva_set_runtime_temp_file_limit(text,integer)', 'EXECUTE')::text")).Should().Be("false");
        (await ScalarAsync(admin, "SELECT has_function_privilege('ariva_runtime', 'ariva_set_runtime_temp_file_limit(text,integer)', 'EXECUTE')::text")).Should().Be("false");
    }

    [Fact]
    public async Task SetRuntimeTempFileLimit_Should_RefuseOtherLoginsAndValuesOutOfRange_When_Called()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.TempFileLimitRefusals);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        await ExecuteAsync(admin, "SELECT ariva_ensure_runtime_login(@name, @password)", ("name", "it_tf_member"), ("password", Secret()));
        await ExecuteAsync(admin, "DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_tf_outsider') THEN CREATE ROLE it_tf_outsider LOGIN; END IF; END $$");

        async Task<string> RefusalOf(string name, int megabytes)
        {
            var call = () => ExecuteAsync(admin, "SELECT ariva_set_runtime_temp_file_limit(@name, @megabytes)", ("name", name), ("megabytes", megabytes));
            return (await call.Should().ThrowAsync<PostgresException>()).Which.MessageText;
        }

        (await RefusalOf("it_tf_member", 0)).Should().Contain("1 to 1048576 megabytes");
        (await RefusalOf("it_tf_member", 1_048_577)).Should().Contain("1 to 1048576 megabytes");
        (await RefusalOf("x; DROP ROLE ariva", 1024)).Should().Contain("invalid runtime login name");
        (await RefusalOf("it_tf_nobody", 1024)).Should().Contain("does not exist");
        (await RefusalOf("it_tf_outsider", 1024)).Should().Contain("not a member of ariva_runtime");
        (await RefusalOf(postgres.AdminUsername, 1024)).Should().Contain("privileged");
        (await ScalarAsync(admin, "SELECT count(*)::text FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase WHERE d.datname = current_database()"))
            .Should().Be("0", "nothing was set by a refused call");
    }

    [Fact]
    public async Task MigrationJob_Should_SetTheConfiguredTempFileLimitOnTheRuntimeLoginOnly_When_ItRuns()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.TempFileLimitMigration);
        const string runtime = "it_tf_migrated";
        var password = Secret();

        (await DatabaseMigration.RunAsync(Configuration(database, postgres.AdminUsername, postgres.AdminPassword, runtime, password, megabytes: 2048), Ct)).Should().Be(0);

        (await ScalarAsync(postgres.ConnectionString(database, runtime, password), "SHOW temp_file_limit")).Should().Be("2GB");
        (await ScalarAsync(postgres.ConnectionString(database), """
            SELECT string_agg(c, ',' ORDER BY c) FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole JOIN pg_database d ON d.oid = s.setdatabase
            CROSS JOIN LATERAL unnest(s.setconfig) c WHERE d.datname = current_database() AND c LIKE 'temp_file_limit=%' AND r.rolname <> 'it_tf_migrated'
            """)).Should().BeEmpty("on the runtime login only");

        // Out of range, the job stops at start, before anything changes.
        var outOfRange = () => DatabaseMigration.RunAsync(Configuration(database, postgres.AdminUsername, postgres.AdminPassword, runtime, password, megabytes: 32), Ct);
        await outOfRange.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Database:TempFileLimitMegabytes is a whole number of megabytes from 64*");
        (await ScalarAsync(postgres.ConnectionString(database, runtime, password), "SHOW temp_file_limit")).Should().Be("2GB", "unchanged");
    }

    private IConfiguration Configuration(string database, string migrationLogin, string migrationPassword, string runtime, string password, int megabytes = 1024) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = runtime,
            ["Database:Password"] = password,
            ["Database:Migration:Username"] = migrationLogin,
            ["Database:Migration:Password"] = migrationPassword,
            ["Database:TempFileLimitMegabytes"] = megabytes.ToString(CultureInfo.InvariantCulture)
        }).Build();

    private static async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> ScalarAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }
}
