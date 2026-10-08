using System.Globalization;
using System.Security.Cryptography;
using Ariva.Core.Validation.Comparison;
using Ariva.Di;
using Ariva.Di.Extensions;
using Ariva.Infra.Services.Validation;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Validation;

/// <summary>
/// ARV-104g1 against PostgreSQL 17 with TimescaleDB (CWE-269, CWE-863, CWE-287): the migration job creates the validation
/// reader login from Database:ValidationReader (script 0049) as a member of ariva_validation_reader and nothing else; through
/// the validation service's shadow read it reads exactly the requested site's zones in the window, while the hosts' runtime login
/// still cannot read a shadow value (42501) and the reader cannot read or write anything else; a second migration rotates its
/// password; the function refuses every login that is not a reader of its own; and a host refuses to start (and the migration
/// job to finish) when the runtime login holds the reader role, directly or through another role. Roles are cluster-wide, so
/// every test uses login names of its own.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ValidationReaderLoginTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Secret(string prefix) => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private IConfiguration Configuration(string database, string runtime, string runtimePassword, string reader = null, string readerPassword = null,
        bool verifySchema = false)
    {
        var values = new Dictionary<string, string>
        {
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = runtime,
            ["Database:Password"] = runtimePassword,
            ["Database:Migration:Username"] = postgres.AdminUsername,
            ["Database:Migration:Password"] = postgres.AdminPassword,
            ["Database:VerifySchemaOnStartup"] = verifySchema ? "true" : "false"
        };
        if (reader is not null)
        {
            values["Database:ValidationReader:Username"] = reader;
            values["Database:ValidationReader:Password"] = readerPassword;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Runs a literal statement as the given connection and returns its rows, one string per row (CWE-89: no input in it).</summary>
    private static Task<List<string>> RunAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql) => QueryAsync(connectionString, sql);

    // The statements are this class's literals (RunAsync, and the arrays of literals passed to RefusedAsync).
    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(k => reader.IsDBNull(k) ? "null" : Convert.ToString(reader.GetValue(k), CultureInfo.InvariantCulture))));
        return rows;
    }

    /// <summary>Runs literal statements (several allowed) as the given connection.</summary>
    private static async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> RefusedAsync(string connectionString, string sql)
    {
        var act = () => QueryAsync(connectionString, sql);
        return (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState;
    }

    /// <summary>The function as the migration job calls it: the login's name and a SCRAM-SHA-256 verifier of its password.</summary>
    private static Task EnsureReaderAsync(string admin, string login, string password) => EnsureWithVerifierAsync(admin, login, ScramVerifier.For(password));

    private static async Task EnsureWithVerifierAsync(string admin, string login, string verifier)
    {
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT ariva_ensure_validation_reader_login(@login, @verifier)", connection);
        command.Parameters.AddWithValue("login", login);
        command.Parameters.AddWithValue("verifier", verifier);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Runs statements the caller writes as literals (several allowed); for the grant and revoke pairs of one test.</summary>
    private static async Task ExecuteLiteralAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task MigrationJob_Should_CreateAReaderThatReadsTheShadowWhileTheRuntimeLoginCannot_When_TheReaderIsConfigured()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.ValidationReader);
        const string runtime = "it_vr_app";
        const string reader = "it_vr_reader";
        var runtimePassword = Secret("rt-");
        var readerPassword = Secret("vr-");
        (await DatabaseMigration.RunAsync(Configuration(database, runtime, runtimePassword, reader, readerPassword), Ct)).Should().Be(0);

        var admin = postgres.ConnectionString(database);
        var asRuntime = postgres.ConnectionString(database, runtime, runtimePassword);
        var asReader = postgres.ConnectionString(database, reader, readerPassword);

        // Shadows of two DMO zones, the same zone name at another site, minutes in two chunks and one outside the window.
        await ExecuteAsync(admin, """
            INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, no_service, nowcast_degraded, updated_on, sensor_cycle_minutes) VALUES
                ('DMO/Q1', '2026-10-01T18:05:00Z', 7.5, NULL, false, now(), 1.25),
                ('DMO/Q1', '2026-10-01T18:06:00Z', NULL, 'NothingOpen', true, now(), NULL),
                ('DMO/Q2', '2026-10-09T08:00:00Z', 2.5, NULL, true, now(), NULL),
                ('DMO/Q3', '2026-10-01T18:05:00Z', 9.0, NULL, false, now(), NULL),
                ('XS/Q1', '2026-10-01T18:05:00Z', 99.0, NULL, false, now(), NULL),
                ('DMO/Q1', '2026-10-10T00:00:00Z', 1.0, NULL, false, now(), NULL)
            """);

        // The validation service's read, through the reader login: exact keys of the site, the half-open window.
        var settings = DatabaseSettings.FromConfiguration(Configuration(database, runtime, runtimePassword));
        var service = new SvcValidationResults(settings, new ValidationReaderSettings { Username = reader, Password = readerPassword });
        var read = await service.ReadShadowAsync("DMO", ["Q1", "Q2"], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), Ct);
        read.HasErrors.Should().BeFalse(string.Join(" ", read.ErrorMessages ?? []));
        read.Data.Should().Equal(
            new ShadowMinuteRow("Q1", new DateTime(2026, 10, 1, 18, 5, 0, DateTimeKind.Utc), 7.5, null, false, 1.25),
            new ShadowMinuteRow("Q1", new DateTime(2026, 10, 1, 18, 6, 0, DateTimeKind.Utc), null, "NothingOpen", true, null),
            new ShadowMinuteRow("Q2", new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc), 2.5, null, true, null));
        read.Data.Should().OnlyContain(r => r.MinuteUtc.Kind == DateTimeKind.Utc);

        // CWE-120: beyond the row limit the read is refused, never cut short.
        var limited = await new SvcValidationResults(settings, new ValidationReaderSettings { Username = reader, Password = readerPassword }) { RowLimit = 2 }
            .ReadShadowAsync("DMO", ["Q1", "Q2"], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), Ct);
        limited.ErrorMessages.Should().ContainSingle().Which.Should().Contain("read per zone or per day");

        // The runtime login still cannot read a shadow value, and given as the reader it is refused before any connection.
        (await RefusedAsync(asRuntime, "SELECT nowcast_minutes FROM queue_minute_shadow")).Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await RefusedAsync(asRuntime, "SELECT * FROM queue_minute_shadow")).Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        var asRuntimeReader = await new SvcValidationResults(settings, new ValidationReaderSettings { Username = runtime, Password = Secret("other-") })
            .ReadShadowAsync("DMO", ["Q1"], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), Ct);
        asRuntimeReader.ErrorMessages.Should().ContainSingle().Which.Should().Contain("misconfigured");

        // The reader reads the shadow and nothing else, and writes nothing.
        (await RunAsync(asReader, "SELECT count(nowcast_degraded) FROM queue_minute_shadow")).Should().Equal("6");
        foreach (var statement in new[]
                 {
                     "SELECT count(*) FROM queue_minute", "SELECT count(*) FROM desk_minute", "SELECT count(*) FROM desk_sensor_minute",
                     "SELECT count(*) FROM \"user\"", "SELECT count(*) FROM validation_campaign", "SELECT count(*) FROM schema_version",
                     "INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, nowcast_degraded, updated_on) VALUES ('DMO/Q9', now(), 1, true, now())",
                     "UPDATE queue_minute_shadow SET nowcast_degraded = true", "DELETE FROM queue_minute_shadow", "TRUNCATE queue_minute_shadow",
                     "CREATE TABLE it_vr_forbidden (id int)", "CREATE VIEW it_vr_view AS SELECT * FROM queue_minute_shadow",
                     "SELECT ariva_ensure_validation_reader_login('it_vr_other', 'a-password-of-24-characters')",
                     "SELECT ariva_ensure_runtime_login('it_vr_other', 'a-password-of-24-characters')"
                 })
        {
            (await RefusedAsync(asReader, statement)).Should().Be(PostgresErrorCodes.InsufficientPrivilege, statement);
        }

        // The review's L1 (CWE-532): the server holds a SCRAM-SHA-256 verifier the migration job computed, never the password.
        var stored = (await RunAsync(admin, "SELECT rolpassword FROM pg_authid WHERE rolname = 'it_vr_reader'")).Should().ContainSingle().Subject;
        stored.Should().StartWith("SCRAM-SHA-256$4096:").And.NotContain(readerPassword);

        // Its attributes, memberships and database privileges: a login of its own, a member of the reader role only.
        (await RunAsync(admin, """
            SELECT rolcanlogin, rolinherit, rolsuper, rolcreaterole, rolcreatedb, rolbypassrls, rolreplication FROM pg_roles WHERE rolname = 'it_vr_reader'
            """)).Should().Equal("True|True|False|False|False|False|False");
        (await RunAsync(admin, """
            SELECT g.rolname FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid WHERE m.member = 'it_vr_reader'::regrole ORDER BY 1
            """)).Should().Equal("ariva_validation_reader");
        (await RunAsync(admin, """
            SELECT has_database_privilege('it_vr_reader', current_database(), 'CONNECT'), has_database_privilege('it_vr_reader', current_database(), 'CREATE'),
                   pg_has_role('it_vr_reader', 'ariva_runtime', 'MEMBER'), pg_has_role('it_vr_reader', 'ariva_migration', 'MEMBER'),
                   pg_has_role('it_vr_app', 'ariva_validation_reader', 'MEMBER'), has_schema_privilege('it_vr_reader', 'public', 'CREATE')
            """)).Should().Equal("True|False|False|False|False|False");

        // A second migration with a new password rotates it; the old one no longer signs in, the grants stay as they were.
        var rotated = Secret("vr2-");
        (await DatabaseMigration.RunAsync(Configuration(database, runtime, runtimePassword, reader, rotated), Ct)).Should().Be(0);
        var stale = async () => await RunAsync(asReader, "SELECT 1");
        (await stale.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidPassword);
        (await RunAsync(postgres.ConnectionString(database, reader, rotated), "SELECT count(nowcast_degraded) FROM queue_minute_shadow")).Should().Equal("6");
        (await RunAsync(admin, """
            SELECT g.rolname FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid WHERE m.member = 'it_vr_reader'::regrole ORDER BY 1
            """)).Should().Equal("ariva_validation_reader");

        // Without the section the job leaves the login as it is (no drop, no change).
        (await DatabaseMigration.RunAsync(Configuration(database, runtime, runtimePassword), Ct)).Should().Be(0);
        (await RunAsync(postgres.ConnectionString(database, reader, rotated), "SELECT count(nowcast_degraded) FROM queue_minute_shadow")).Should().Equal("6");

        // The review's L2 (CWE-89): a password with a quote, a backslash, a dollar quote and a statement of its own signs in exactly
        // as written and changes nothing else; only its verifier reaches SQL.
        const string hostile = "it-quote'back\\slash$$dollar\"; ALTER ROLE it_vr_quote SUPERUSER;";
        (await DatabaseMigration.RunAsync(Configuration(database, runtime, runtimePassword, "it_vr_quote", hostile), Ct)).Should().Be(0);
        (await RunAsync(postgres.ConnectionString(database, "it_vr_quote", hostile), "SELECT current_user::text")).Should().Equal("it_vr_quote");
        (await RunAsync(admin, "SELECT rolsuper, left(rolpassword, 19) FROM pg_authid WHERE rolname = 'it_vr_quote'")).Should().Equal("False|SCRAM-SHA-256$4096:");
        var wrong = async () => await RunAsync(postgres.ConnectionString(database, "it_vr_quote", hostile + "x"), "SELECT 1");
        (await wrong.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidPassword);
    }

    [Fact]
    public async Task EnsureValidationReaderLogin_Should_RefuseEveryLoginThatIsNotOnlyAReader_When_Called()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.ValidationReaderRefusals);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        var password = Secret("pw-");

        // Roles this test needs (cluster-wide, so created only when missing): a privileged one, a runtime login, a login granted
        // to another role, a login that owns a table of this database, and (the review's M1) roles that own nothing and hold no
        // role but have privileges of their own here: SELECT on a table, SELECT on a column, a default privilege (as the
        // development read-only role has), CREATE on schema public, and CREATE on this database.
        await ExecuteAsync(admin, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_createdb') THEN CREATE ROLE it_vr_createdb LOGIN CREATEDB; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_granted') THEN CREATE ROLE it_vr_granted LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_holder') THEN CREATE ROLE it_vr_holder NOLOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_owner') THEN CREATE ROLE it_vr_owner LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_table') THEN CREATE ROLE it_vr_table LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_column') THEN CREATE ROLE it_vr_column LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_default') THEN CREATE ROLE it_vr_default LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_schema') THEN CREATE ROLE it_vr_schema LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_database') THEN CREATE ROLE it_vr_database LOGIN; END IF;
                EXECUTE format('GRANT CREATE ON DATABASE %I TO it_vr_database', current_database());
            END
            $$;
            GRANT it_vr_granted TO it_vr_holder;
            CREATE TABLE it_vr_owned (id int);
            ALTER TABLE it_vr_owned OWNER TO it_vr_owner;
            GRANT SELECT ON "user" TO it_vr_table;
            GRANT SELECT (zone_key) ON queue_minute TO it_vr_column;
            ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO it_vr_default;
            GRANT CREATE ON SCHEMA public TO it_vr_schema;
            SELECT ariva_ensure_runtime_login('it_vr_refused_runtime', 'runtime-password-0123456789');
            """);

        foreach (var (login, secret, message) in new[]
                 {
                     ("x; DROP ROLE ariva", password, "invalid validation reader login name"),
                     ("It_Vr_Upper", password, "invalid validation reader login name"),
                     ("pg_reader", password, "invalid validation reader login name"),
                     ("ariva_runtime", password, "must be a login of its own"),
                     ("ariva_migration", password, "must be a login of its own"),
                     ("ariva_validation_reader", password, "must be a login of its own"),
                     (postgres.AdminUsername, password, "must be a login of its own"),
                     ("it_vr_createdb", password, "is privileged"),
                     ("it_vr_refused_runtime", password, "is a member of ariva_runtime"),
                     ("it_vr_granted", password, "is granted to it_vr_holder"),
                     ("it_vr_owner", password, "owns objects or holds privileges"),
                     ("it_vr_table", password, "owns objects or holds privileges"),
                     ("it_vr_column", password, "owns objects or holds privileges"),
                     ("it_vr_default", password, "owns objects or holds privileges"),
                     ("it_vr_schema", password, "owns objects or holds privileges"),
                     ("it_vr_database", password, "beyond CONNECT")
                 })
        {
            var act = () => EnsureReaderAsync(admin, login, secret);
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain(message, login).And.NotContain(secret);
        }

        // The review's L1: the function takes a SCRAM-SHA-256 verifier of at least 4096 iterations and nothing else, so a plain
        // password is never stored or sent to it; a forged verifier with a statement in it is refused by its shape.
        foreach (var verifier in new[]
                 {
                     password, "SCRAM-SHA-256$4096:" + new string('A', 22) + "==$" + new string('B', 43) + "=", "md5" + new string('a', 32),
                     "SCRAM-SHA-256$1000:" + new string('A', 22) + "==$" + new string('B', 43) + "=:" + new string('C', 43) + "=",
                     "SCRAM-SHA-256$4096:" + new string('A', 22) + "==$" + new string('B', 43) + "=:" + new string('C', 43) + "='; ALTER ROLE it_vr_plain SUPERUSER; '"
                 })
        {
            var act = () => EnsureWithVerifierAsync(admin, "it_vr_plain", verifier);
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("SCRAM-SHA-256 verifier").And.NotContain(password);
        }

        // The re-check's Low (CWE-269): another database of the cluster counts too, since the reader's password would open it. A
        // role that owns a table there, holds a table grant there, or has CREATE or TEMP on it is refused here.
        var other = await postgres.CreateDatabaseAsync(TestDatabase.ValidationReaderOther);
        var otherAdmin = postgres.ConnectionString(other);
        await new SqlScriptRunner(otherAdmin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        await ExecuteAsync(otherAdmin, """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_far_owner') THEN CREATE ROLE it_vr_far_owner LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_far_grant') THEN CREATE ROLE it_vr_far_grant LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_far_create') THEN CREATE ROLE it_vr_far_create LOGIN; END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_far_temp') THEN CREATE ROLE it_vr_far_temp LOGIN; END IF;
                EXECUTE format('GRANT CREATE ON DATABASE %I TO it_vr_far_create', current_database());
                EXECUTE format('GRANT TEMPORARY ON DATABASE %I TO it_vr_far_temp', current_database());
            END
            $$;
            CREATE TABLE it_vr_far_owned (id int);
            ALTER TABLE it_vr_far_owned OWNER TO it_vr_far_owner;
            GRANT SELECT ON "user" TO it_vr_far_grant;
            """);
        foreach (var (login, message) in new[]
                 {
                     ("it_vr_far_owner", "owns objects or holds privileges"), ("it_vr_far_grant", "owns objects or holds privileges"),
                     ("it_vr_far_create", "beyond CONNECT"), ("it_vr_far_temp", "beyond CONNECT")
                 })
        {
            var act = () => EnsureReaderAsync(admin, login, password);
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain(message, login);
        }

        (await RunAsync(admin, """
            SELECT rolname FROM pg_authid WHERE rolname IN ('it_vr_far_owner', 'it_vr_far_grant', 'it_vr_far_create', 'it_vr_far_temp')
               AND (rolpassword IS NOT NULL OR pg_has_role(oid, 'ariva_validation_reader', 'MEMBER'))
            """)).Should().BeEmpty("no refused role got a password or the reader role");

        // One reader shared by both databases passes in each and on a re-run: in each it holds only CONNECT and the reader role.
        await EnsureReaderAsync(admin, "it_vr_shared", password);
        await EnsureReaderAsync(otherAdmin, "it_vr_shared", password);
        await EnsureReaderAsync(admin, "it_vr_shared", password);
        (await RunAsync(postgres.ConnectionString(other, "it_vr_shared", password), "SELECT count(nowcast_degraded) FROM queue_minute_shadow")).Should().Equal("0");

        // The reader itself passes again on a re-run: its CONNECT on this database is the one privilege it may hold.
        await EnsureReaderAsync(admin, "it_vr_ok", password);
        await EnsureReaderAsync(admin, "it_vr_ok", Secret("pw2-"));
        (await RunAsync(admin, """
            SELECT g.rolname FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid WHERE m.member = 'it_vr_ok'::regrole
            """)).Should().Equal("ariva_validation_reader");

        // Nothing refused was made a reader, no login was created for the refused names, and no refused role got a password.
        (await RunAsync(admin, """
            SELECT r.rolname FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member
             WHERE m.roleid = 'ariva_validation_reader'::regrole AND r.rolname IN ('it_vr_createdb', 'it_vr_refused_runtime', 'it_vr_granted', 'it_vr_owner',
                   'it_vr_table', 'it_vr_column', 'it_vr_default', 'it_vr_schema', 'it_vr_database')
            UNION ALL SELECT rolname FROM pg_roles WHERE rolname IN ('it_vr_plain', 'pg_reader', 'It_Vr_Upper')
            UNION ALL SELECT rolname FROM pg_authid WHERE rolname IN ('it_vr_table', 'it_vr_column', 'it_vr_default', 'it_vr_schema', 'it_vr_database') AND rolpassword IS NOT NULL
            """)).Should().BeEmpty();

        // The function is the migration login's: the runtime login may not run it.
        var asRuntime = postgres.ConnectionString(database, "it_vr_refused_runtime", "runtime-password-0123456789");
        (await RefusedAsync(asRuntime, "SELECT ariva_ensure_validation_reader_login('it_vr_sneak', 'a-password-of-24-characters')"))
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Host_Should_RefuseToStart_When_TheRuntimeLoginCanReadTheShadow()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.ValidationReaderGuard);
        const string runtime = "it_vr_guard_app";
        var runtimePassword = Secret("rt-");
        var readerPassword = Secret("vr-");
        var migration = Configuration(database, runtime, runtimePassword, "it_vr_guard_reader", readerPassword);
        (await DatabaseMigration.RunAsync(migration, Ct)).Should().Be(0);
        var admin = postgres.ConnectionString(database);
        // A chunk, for the grant to PUBLIC on a chunk below.
        await ExecuteAsync(admin, """
            INSERT INTO queue_minute_shadow (zone_key, minute_utc, nowcast_minutes, nowcast_degraded, updated_on) VALUES ('DMO/Q1', '2026-10-01T18:05:00Z', 7.5, false, now())
            """);

        // A host as every host composes its persistence: the schema check outside vm-local, then the guard.
        async Task StartAsync(IConfiguration configuration)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddConfiguration(configuration);
            builder.Services.AddArivaPersistence(builder.Configuration);
            using var host = builder.Build();
            await host.StartAsync(Ct);
            await host.StopAsync(Ct);
        }

        var hosted = Configuration(database, runtime, runtimePassword, verifySchema: true);
        await StartAsync(hosted);

        // Every way the runtime login could read a shadow value (the review's M2): the host refuses to start and the migration job
        // stops, each time; revoked, the host starts again.
        foreach (var (what, grant, revoke) in new[]
                 {
                     ("the reader role", "GRANT ariva_validation_reader TO it_vr_guard_app", "REVOKE ariva_validation_reader FROM it_vr_guard_app"),
                     ("the reader role through another role", """
                         DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_guard_middle') THEN CREATE ROLE it_vr_guard_middle NOLOGIN; END IF; END $$;
                         GRANT ariva_validation_reader TO it_vr_guard_middle;
                         GRANT it_vr_guard_middle TO it_vr_guard_app;
                         """, "REVOKE it_vr_guard_middle FROM it_vr_guard_app; REVOKE ariva_validation_reader FROM it_vr_guard_middle"),
                     ("pg_read_all_data", "GRANT pg_read_all_data TO it_vr_guard_app", "REVOKE pg_read_all_data FROM it_vr_guard_app"),
                     ("a value column granted to it", "GRANT SELECT (nowcast_minutes) ON queue_minute_shadow TO it_vr_guard_app",
                         "REVOKE SELECT (nowcast_minutes) ON queue_minute_shadow FROM it_vr_guard_app"),
                     ("a value column granted to ariva_runtime", "GRANT SELECT (no_service) ON queue_minute_shadow TO ariva_runtime",
                         "REVOKE SELECT (no_service) ON queue_minute_shadow FROM ariva_runtime"),
                     ("the table granted to PUBLIC", "GRANT SELECT ON queue_minute_shadow TO PUBLIC", "REVOKE SELECT ON queue_minute_shadow FROM PUBLIC"),
                     ("a chunk granted to PUBLIC",
                         "DO $$ BEGIN EXECUTE format('GRANT SELECT ON %s TO PUBLIC', (SELECT show_chunks('queue_minute_shadow') LIMIT 1)); END $$",
                         "DO $$ BEGIN EXECUTE format('REVOKE SELECT ON %s FROM PUBLIC', (SELECT show_chunks('queue_minute_shadow') LIMIT 1)); END $$")
                 })
        {
            await ExecuteLiteralAsync(admin, grant);
            try
            {
                var start = () => StartAsync(hosted);
                (await start.Should().ThrowAsync<InvalidOperationException>(what)).Which.Message.Should().Contain("can read the shadow nowcast", what).And.NotContain(runtimePassword);
                (await DatabaseMigration.RunAsync(migration, Ct)).Should().Be(1, what);
            }
            finally
            {
                await ExecuteLiteralAsync(admin, revoke);
            }

            await StartAsync(hosted);
        }

        // A superuser as the runtime login (it holds every role and reads every column): the host refuses to start, and the
        // migration job refuses such a runtime login altogether.
        var superPassword = Secret("su-");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(Ct);
            await using (var set = new NpgsqlCommand("SELECT set_config('ariva_it.password', @password, false)", connection))
            {
                set.Parameters.AddWithValue("password", superPassword);
                await set.ExecuteNonQueryAsync(Ct);
            }

            await using var create = new NpgsqlCommand("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_vr_guard_super') THEN
                        EXECUTE format('CREATE ROLE it_vr_guard_super LOGIN SUPERUSER PASSWORD %L', current_setting('ariva_it.password'));
                    ELSE
                        EXECUTE format('ALTER ROLE it_vr_guard_super LOGIN SUPERUSER PASSWORD %L', current_setting('ariva_it.password'));
                    END IF;
                END $$
                """, connection);
            await create.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            var asSuperuser = () => StartAsync(Configuration(database, "it_vr_guard_super", superPassword, verifySchema: true));
            await asSuperuser.Should().ThrowAsync<InvalidOperationException>().WithMessage("*can read the shadow nowcast*");
            (await DatabaseMigration.RunAsync(Configuration(database, "it_vr_guard_super", superPassword, "it_vr_guard_reader", readerPassword), Ct)).Should().Be(1);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin, "DROP ROLE IF EXISTS it_vr_guard_super");
        }

        // A reader configured as the runtime login stops the host and the job by name alone.
        var sameLogin = Configuration(database, runtime, runtimePassword, runtime, readerPassword, verifySchema: true);
        var named = () => StartAsync(sameLogin);
        await named.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is the runtime login*");
        (await DatabaseMigration.RunAsync(sameLogin, Ct)).Should().Be(1);
        (await RunAsync(admin, "SELECT pg_has_role('it_vr_guard_app', 'ariva_validation_reader', 'MEMBER')")).Should().Equal("False");
    }
}
