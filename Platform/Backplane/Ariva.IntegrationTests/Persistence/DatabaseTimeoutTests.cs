using System.Globalization;
using System.Security.Cryptography;
using Ariva.Di;
using Ariva.Infra.NHibernate;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-096d (CWE-400, ASVS 5.0 V15.2.2) against PostgreSQL 17 with TimescaleDB: a host cancels a statement that outlasts
/// Database:CommandTimeoutSeconds, on raw Npgsql commands and NHibernate's alike (the connection string carries it), and the
/// server ends a statement of the runtime login that outlasts the statement_timeout the migration job sets on that login (script
/// 0063), whose client may no longer be waiting. The setting is on the login itself (a setting of the ariva_runtime role would not
/// reach it) and in this database only; the migration login keeps none. The function refuses a login that is not a plain member of
/// ariva_runtime and a value out of range. Roles are cluster-wide, so every test uses login names of its own.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseTimeoutTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Secret() => "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    [Fact]
    public async Task StatementTimeout_Should_EndASlowerStatementOfTheRuntimeLogin_When_TheMigrationSetsIt()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.StatementTimeout);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        const string login = "it_st_app";
        var password = Secret();
        await ExecuteAsync(admin, "SELECT ariva_ensure_runtime_login(@name, @password)", ("name", login), ("password", password));
        // One second here, so the test is quick; the migration job takes Database:StatementTimeoutSeconds (301 to 3,600).
        await ExecuteAsync(admin, "SELECT ariva_set_runtime_statement_timeout(@name, @seconds)", ("name", login), ("seconds", 1));

        var asRuntime = postgres.ConnectionString(database, login, password);
        var slower = () => ExecuteAsync(asRuntime, "SELECT pg_sleep(3)");
        var quicker = () => ExecuteAsync(asRuntime, "SELECT pg_sleep(0.2)");

        (await slower.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.QueryCanceled, "the server ended it");
        await quicker.Should().NotThrowAsync();
        (await ScalarAsync(asRuntime, "SHOW statement_timeout")).Should().Be("1s");
        (await ScalarAsync(admin, "SHOW statement_timeout")).Should().Be("0", "the migration login keeps no limit, so long scripts still run");

        // CWE-269: only the migration login runs the function; PUBLIC, the runtime role and the login itself may not.
        (await ScalarAsync(admin, "SELECT has_function_privilege('public', 'ariva_set_runtime_statement_timeout(text,integer)', 'EXECUTE')::text")).Should().Be("false");
        (await ScalarAsync(admin, "SELECT has_function_privilege('ariva_runtime', 'ariva_set_runtime_statement_timeout(text,integer)', 'EXECUTE')::text")).Should().Be("false");
        (await ScalarAsync(admin, "SELECT has_function_privilege('it_st_app', 'ariva_set_runtime_statement_timeout(text,integer)', 'EXECUTE')::text")).Should().Be("false");
        var lifted = () => ExecuteAsync(asRuntime, "SELECT ariva_set_runtime_statement_timeout('it_st_app', 3600)");
        (await lifted.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task MigrationJob_Should_SetTheConfiguredStatementTimeoutOnTheRuntimeLoginOnly_When_ItRuns()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.StatementTimeoutMigration);
        const string login = "it_st_migrated";
        var password = Secret();

        (await DatabaseMigration.RunAsync(Configuration(database, login, password, statementSeconds: 420), Ct)).Should().Be(0);

        (await ScalarAsync(postgres.ConnectionString(database, login, password), "SHOW statement_timeout")).Should().Be("7min");
        var settings = await ScalarAsync(postgres.ConnectionString(database), """
            SELECT string_agg(r.rolname || ':' || array_to_string(s.setconfig, ','), ' ' ORDER BY r.rolname)
            FROM pg_db_role_setting s
            JOIN pg_roles r ON r.oid = s.setrole
            JOIN pg_database d ON d.oid = s.setdatabase
            WHERE d.datname = current_database()
            """);
        settings.Should().Be("it_st_migrated:statement_timeout=420s", "on the runtime login, in this database, and on no other role");

        // Out of range, the job stops at start, before anything changes (as an unpinned production database does).
        var outOfRange = () => DatabaseMigration.RunAsync(Configuration(database, login, password, statementSeconds: 60), Ct);
        await outOfRange.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Database:StatementTimeoutSeconds is a whole number of seconds from 301*");
        (await ScalarAsync(postgres.ConnectionString(database, login, password), "SHOW statement_timeout")).Should().Be("7min", "unchanged");
    }

    [Fact]
    public async Task SetRuntimeStatementTimeout_Should_RefuseOtherLoginsAndValuesOutOfRange_When_Called()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.StatementTimeoutRefusals);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        await ExecuteAsync(admin, "SELECT ariva_ensure_runtime_login(@name, @password)", ("name", "it_st_member"), ("password", Secret()));
        await ExecuteAsync(admin, "DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_st_outsider') THEN CREATE ROLE it_st_outsider LOGIN; END IF; END $$");

        async Task<string> RefusalOf(string name, int seconds)
        {
            var call = () => ExecuteAsync(admin, "SELECT ariva_set_runtime_statement_timeout(@name, @seconds)", ("name", name), ("seconds", seconds));
            return (await call.Should().ThrowAsync<PostgresException>()).Which.MessageText;
        }

        (await RefusalOf("it_st_member", 0)).Should().Contain("1 to 3600 seconds");
        (await RefusalOf("it_st_member", 3601)).Should().Contain("1 to 3600 seconds");
        (await RefusalOf("x; DROP ROLE ariva", 360)).Should().Contain("invalid runtime login name");
        (await RefusalOf("it_st_nobody", 360)).Should().Contain("does not exist");
        (await RefusalOf("it_st_outsider", 360)).Should().Contain("not a member of ariva_runtime");
        (await RefusalOf(postgres.AdminUsername, 360)).Should().Contain("privileged");
        (await ScalarAsync(admin, "SELECT count(*)::text FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase WHERE d.datname = current_database()"))
            .Should().Be("0", "nothing was set by a refused call");
    }

    [Fact]
    public async Task CommandTimeout_Should_CancelASlowerStatement_When_TheHostWaitsNoLonger()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.CommandTimeout);
        var settings = new DatabaseSettings
        {
            Host = postgres.Hostname,
            Port = postgres.Port,
            Name = database,
            Username = postgres.AdminUsername,
            Password = postgres.AdminPassword,
            CommandTimeoutSeconds = DatabaseSettings.MinCommandTimeoutSeconds
        };

        // A raw command on a connection the settings build.
        var raw = () => ExecuteAsync(settings.BuildConnectionString(), "SELECT pg_sleep(7)");
        (await raw.Should().ThrowAsync<NpgsqlException>()).Which.InnerException.Should().BeOfType<TimeoutException>("Npgsql cancelled it after 5 s");

        // NHibernate's sessions take the same connection string, so their commands are cancelled the same way.
        var factory = new NHibernateSessionFactoryProvider(settings, []).SessionFactory;
        var throughNHibernate = async () =>
        {
            using var session = factory.OpenSession();
            await session.CreateSQLQuery("SELECT pg_sleep(7)").ListAsync(Ct);
        };
        (await throughNHibernate.Should().ThrowAsync<Exception>()).Which.GetBaseException().Should().BeOfType<TimeoutException>();
    }

    private IConfiguration Configuration(string database, string runtime, string password, int statementSeconds) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = runtime,
            ["Database:Password"] = password,
            ["Database:Migration:Username"] = postgres.AdminUsername,
            ["Database:Migration:Password"] = postgres.AdminPassword,
            ["Database:StatementTimeoutSeconds"] = statementSeconds.ToString(CultureInfo.InvariantCulture)
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
