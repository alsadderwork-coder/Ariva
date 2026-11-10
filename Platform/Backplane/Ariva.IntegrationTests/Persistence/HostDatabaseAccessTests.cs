using System.Globalization;
using Ariva.Di;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-083a (CWE-269, CWE-285; ASVS 5.0 V13.2.2): one database login per host, each with the grants its code needs. A cluster's
/// migration job (k8s-dev, every Database:HostLogins and Database:Availability) creates each login from a SCRAM-SHA-256
/// verifier as a member of ariva_runtime and its one host role, with the runtime limits; the roles then hold exactly what
/// HostDatabaseAccess declares, no more and no less, and each login is refused a write its host never makes (Ingest a
/// configuration table, Stream an account, every host but the availability ledger a minute or a cursor) while the writes its
/// host makes go through. A cluster without the host logins stops the job before anything changes; a vm-local database's
/// hosts share one login that holds every host role, which no cluster database accepts.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HostDatabaseAccessTests(PostgresFixture postgres)
{
    #region Fields

    private const string Prefix = "it_ha_";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string _database;

    private static Dictionary<string, string> _logins;

    #endregion

    #region Properties

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Tests

    [Fact]
    public async Task Migration_Should_GrantEveryRoleExactlyItsManifest_When_AClusterJobRuns()
    {
        var (database, _) = await MigratedAsync();
        var admin = postgres.ConnectionString(database);

        var granted = await RowsAsync(admin, Privileges);
        foreach (var role in HostDatabaseAccess.Roles)
        {
            granted.Where(row => row.StartsWith(role + " ", StringComparison.Ordinal)).Select(row => row[(role.Length + 1)..])
                .Should().Equal(HostDatabaseAccess.Grants[role], $"{role} holds exactly what HostDatabaseAccess declares, on no other relation");
        }

        // Ariva's functions: only Cronz's jobs may run the two purges (retention, key ring); no role runs a login or limit function.
        var functions = await RowsAsync(admin, """
            SELECT r.name || ' ' || p.oid::regprocedure::text
              FROM unnest(ARRAY['ariva_runtime', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability']) r(name)
              CROSS JOIN pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace AND p.proname LIKE 'ariva%' AND has_function_privilege(r.name, p.oid, 'EXECUTE')
             ORDER BY 1
            """);
        functions.Should().Equal(HostDatabaseAccess.Functions.SelectMany(f => f.Value.Select(signature => $"{f.Key} {signature}")));
        (await RowsAsync(admin, "SELECT count(*)::text FROM pg_proc WHERE proname = 'ariva_ensure_runtime_login'")).Should().Equal(["0"],
            "the function that took a plain password is gone");

        // No sequence, and no default privilege: a table a later script creates grants no host anything until its script does.
        (await RowsAsync(admin, """
            SELECT count(*)::text
              FROM unnest(ARRAY['ariva_runtime', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability']) r(name)
              CROSS JOIN pg_class c
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'S'
               AND (has_sequence_privilege(r.name, c.oid, 'USAGE') OR has_sequence_privilege(r.name, c.oid, 'SELECT') OR has_sequence_privilege(r.name, c.oid, 'UPDATE'))
            """)).Should().Equal("0");
        (await RowsAsync(admin, "SELECT count(*)::text FROM pg_default_acl WHERE defaclnamespace = 'public'::regnamespace")).Should().Equal("0");

        // The host roles: NOLOGIN, unprivileged, holding no role.
        (await RowsAsync(admin, """
            SELECT r.rolname || ':' || r.rolcanlogin || r.rolsuper || r.rolcreaterole || r.rolcreatedb || r.rolbypassrls || r.rolreplication || ':'
                   || (SELECT count(*) FROM pg_auth_members m WHERE m.member = r.oid)
              FROM pg_roles r
             WHERE r.rolname IN ('ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability')
             ORDER BY 1
            """)).Should().Equal(
            "ariva_availability:falsefalsefalsefalsefalsefalse:0", "ariva_cronz:falsefalsefalsefalsefalsefalse:0", "ariva_ingest:falsefalsefalsefalsefalsefalse:0",
            "ariva_integration:falsefalsefalsefalsefalsefalse:0", "ariva_main:falsefalsefalsefalsefalsefalse:0", "ariva_stream:falsefalsefalsefalsefalsefalse:0");
    }

    [Theory]
    [InlineData("Main", "ariva_main")]
    [InlineData("Ingest", "ariva_ingest")]
    [InlineData("Stream", "ariva_stream")]
    [InlineData("Cronz", "ariva_cronz")]
    [InlineData("Integration", "ariva_integration")]
    [InlineData("Availability", "ariva_availability")]
    public async Task MigrationJob_Should_EnsureEachLoginFromAVerifierWithItsTwoRolesAndLimits_When_AClusterJobRuns(string host, string role)
    {
        var (database, logins) = await MigratedAsync();
        var admin = postgres.ConnectionString(database);
        var (login, password) = LoginOf(logins, host);

        (await RowsAsync(admin, Memberships, ("login", login))).Should().Equal([string.Join(',', new[] { role, HostDatabaseAccess.RuntimeRole }.Order(StringComparer.Ordinal))],
            "a host login holds ariva_runtime and its host role, nothing else");
        (await RowsAsync(admin, "SELECT rolcanlogin::text || rolsuper || rolcreaterole || rolcreatedb || rolbypassrls || rolreplication FROM pg_roles WHERE rolname = @login",
            ("login", login))).Should().Equal("truefalsefalsefalsefalsefalse");

        // CWE-532: the server holds the verifier the job computed, never the password.
        var stored = (await RowsAsync(admin, "SELECT rolpassword FROM pg_authid WHERE rolname = @login", ("login", login))).Should().ContainSingle().Subject;
        stored.Should().StartWith("SCRAM-SHA-256$4096:");
        stored.Contains(password, StringComparison.Ordinal).Should().BeFalse("the server holds a verifier, never the password");

        // ARV-096d, ARV-290, ARV-292: the limits on the login itself, since a group role's settings do not reach its members.
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database, login, password));
        await connection.OpenAsync(Ct);
        (await ScalarAsync(connection, "SHOW statement_timeout")).Should().Be("6min");
        (await ScalarAsync(connection, "SHOW temp_file_limit")).Should().Be("1GB");
        (await ScalarAsync(connection, "SHOW idle_in_transaction_session_timeout")).Should().Be("5min", "300 s by default since ARV-294");
        (await ScalarAsync(connection, "SHOW tcp_keepalives_idle")).Should().Be("60");
    }

    /// <summary>
    /// The criterion's refusals (CWE-269): each host's login is refused a write its host never makes. Ingest writes no
    /// configuration table and Stream no account; every host but the availability ledger may record no minute and move no cursor
    /// (the ARV-150 review: a compromised host could otherwise record minutes ahead of the ledger or move a site's cursor past
    /// minutes); each is refused a write another host makes; and no host but Main reads an account's credentials (Cronz only the
    /// TOTP seed it re-protects), since every pod mounts the Data Protection certificate.
    /// </summary>
    [Theory]
    [InlineData("Ingest", "INSERT INTO zone_profile (id) VALUES (gen_random_uuid())")]
    [InlineData("Ingest", "UPDATE zone_profile SET id = id")]
    [InlineData("Ingest", "UPDATE device SET id = id")]
    [InlineData("Ingest", "DELETE FROM device")]
    [InlineData("Ingest", "INSERT INTO site (id) VALUES (gen_random_uuid())")]
    [InlineData("Ingest", "UPDATE alert_rule SET id = id")]
    [InlineData("Ingest", "UPDATE display SET id = id")]
    [InlineData("Ingest", "UPDATE zone SET id = id")]
    [InlineData("Ingest", "UPDATE line SET id = id")]
    [InlineData("Ingest", "INSERT INTO desk_code_mapping (id) VALUES (gen_random_uuid())")]
    [InlineData("Ingest", "INSERT INTO queue_minute (zone_key) VALUES ('X')")]
    [InlineData("Stream", "UPDATE \"user\" SET id = id")]
    [InlineData("Stream", "INSERT INTO \"user\" (id) VALUES (gen_random_uuid())")]
    [InlineData("Stream", "UPDATE user_session SET id = id")]
    [InlineData("Stream", "INSERT INTO user_session (id) VALUES (gen_random_uuid())")]
    [InlineData("Stream", "INSERT INTO user_role (id) VALUES (gen_random_uuid())")]
    [InlineData("Stream", "DELETE FROM user_site")]
    [InlineData("Stream", "UPDATE refresh_token SET id = id")]
    [InlineData("Stream", "UPDATE recovery_code SET id = id")]
    [InlineData("Stream", "UPDATE zone_profile SET id = id")]
    [InlineData("Main", "INSERT INTO queue_minute (zone_key) VALUES ('X')")]
    [InlineData("Main", "INSERT INTO border_desk_session (id) VALUES (gen_random_uuid())")]
    [InlineData("Main", "UPDATE integration_idempotency SET status_code = status_code")]
    [InlineData("Cronz", "UPDATE \"user\" SET id = id")]
    [InlineData("Cronz", "UPDATE integration_client SET id = id")]
    [InlineData("Cronz", "UPDATE zone_profile SET id = id")]
    [InlineData("Integration", "UPDATE outbound_endpoint SET id = id")]
    [InlineData("Integration", "UPDATE integration_client SET id = id")]
    [InlineData("Integration", "UPDATE zone_profile SET id = id")]
    [InlineData("Integration", "UPDATE \"user\" SET id = id")]
    [InlineData("Availability", "UPDATE zone_profile SET id = id")]
    [InlineData("Availability", "UPDATE availability_minute SET site_code = site_code")]
    [InlineData("Availability", "DELETE FROM availability_minute")]
    // The review of ARV-083a (CWE-269): no host but Main reads an account's credentials, and Cronz only the seed it re-protects.
    [InlineData("Ingest", "SELECT password_hash FROM \"user\"")]
    [InlineData("Ingest", "SELECT * FROM \"user\"")]
    [InlineData("Ingest", "SELECT totp_secret_protected FROM \"user\"")]
    [InlineData("Stream", "SELECT password_salt FROM \"user\"")]
    [InlineData("Stream", "SELECT totp_secret_protected FROM \"user\"")]
    [InlineData("Cronz", "SELECT password_hash, password_salt FROM \"user\"")]
    [InlineData("Cronz", "SELECT * FROM \"user\"")]
    [InlineData("Integration", "SELECT password_iterations, password_algorithm FROM \"user\"")]
    [InlineData("Integration", "SELECT totp_secret_protected FROM \"user\"")]
    [InlineData("Availability", "SELECT password_hash FROM \"user\"")]
    // The review's R2-2: the key ring purge is Cronz's key lifecycle job's alone, and the stored payloads the key ring upgrade check
    // reads are out of reach of the hosts that do not run it; Cronz reads no client secret hash and no other display column.
    [InlineData("Main", "SELECT * FROM ariva_data_protection_key_purge(30, 1)")]
    [InlineData("Ingest", "SELECT * FROM ariva_data_protection_key_purge(30, 1)")]
    [InlineData("Stream", "SELECT * FROM ariva_data_protection_key_purge(30, 1)")]
    [InlineData("Integration", "SELECT * FROM ariva_data_protection_key_purge(30, 1)")]
    [InlineData("Availability", "SELECT * FROM ariva_data_protection_key_purge(30, 1)")]
    [InlineData("Ingest", "SELECT totp_secret_protected FROM integration_client")]
    [InlineData("Ingest", "SELECT secret_protected FROM outbound_endpoint")]
    [InlineData("Stream", "SELECT totp_secret_protected FROM integration_client")]
    [InlineData("Stream", "SELECT secret_protected FROM outbound_endpoint")]
    [InlineData("Cronz", "SELECT secret_hash FROM integration_client")]
    [InlineData("Cronz", "SELECT name FROM display")]
    [InlineData("Cronz", "UPDATE display SET deleted_on = deleted_on")]
    public async Task HostLogin_Should_BeRefused_When_ItWritesOrReadsWhatItsHostNeverDoes(string host, string sql)
    {
        var (database, logins) = await MigratedAsync();
        var (login, password) = LoginOf(logins, host);

        (await RefusedAsync(postgres.ConnectionString(database, login, password), sql)).Should().Be(PostgresErrorCodes.InsufficientPrivilege, $"{host}: {sql}");
    }

    [Theory]
    [InlineData("Main")]
    [InlineData("Ingest")]
    [InlineData("Stream")]
    [InlineData("Cronz")]
    [InlineData("Integration")]
    public async Task HostLogin_Should_NeitherRecordAMinuteNorMoveACursor_When_ItIsNotTheAvailabilityLedgers(string host)
    {
        var (database, logins) = await MigratedAsync();
        var (login, password) = LoginOf(logins, host);
        var connection = postgres.ConnectionString(database, login, password);

        foreach (var sql in new[]
                 {
                     "INSERT INTO availability_minute (site_code) VALUES ('HA')",
                     "INSERT INTO availability_cursor (site_code, next_minute_utc, updated_on) VALUES ('HA', now(), now())",
                     "UPDATE availability_cursor SET next_minute_utc = next_minute_utc + interval '1 day'",
                     "INSERT INTO availability_cursor (site_code, next_minute_utc, updated_on) VALUES ('HA', now(), now()) ON CONFLICT (site_code) DO UPDATE SET next_minute_utc = EXCLUDED.next_minute_utc"
                 })
        {
            (await RefusedAsync(connection, sql)).Should().Be(PostgresErrorCodes.InsufficientPrivilege, $"{host}: {sql}");
        }
    }

    /// <summary>The other side: what each host's code does goes through its login (in a transaction rolled back).</summary>
    [Theory]
    [InlineData("Main", "UPDATE zone_profile SET name = name WHERE false")]
    [InlineData("Main", "SELECT count(*) FROM availability_minute")]
    [InlineData("Main", "UPDATE site SET name = name WHERE false")]
    [InlineData("Ingest", "SELECT count(*) FROM device")]
    [InlineData("Ingest", "SELECT count(*) FROM level JOIN terminal ON terminal.id = level.terminal_id JOIN airport ON airport.id = terminal.airport_id")]
    [InlineData("Stream", "INSERT INTO stream_partition_count (topic_set, partitions, first_seen) VALUES ('it-ha', 1, now())")]
    [InlineData("Stream", "SELECT count(*) FROM queue_minute_shadow WHERE zone_key = 'X' AND minute_utc = now()")]
    [InlineData("Cronz", "SELECT has_function_privilege('ariva_retention_purge(text, integer)', 'EXECUTE') AND count(*) >= 0 FROM retention_period")]
    [InlineData("Cronz", "UPDATE \"user\" SET totp_secret_protected = totp_secret_protected WHERE false")]
    [InlineData("Cronz", "UPDATE report_delivery SET status = status, attempts = attempts WHERE false")]
    // The review's R2-2: the key lifecycle round's purge and dates (KeyLifecycleRound) and the key ring upgrade check's reads.
    [InlineData("Cronz", "SELECT deleted, active, kept, held FROM ariva_data_protection_key_purge(30, 1)")]
    [InlineData("Cronz", "SELECT min(c.secret_changed_utc), min(c.totp_changed_utc) FROM integration_client c WHERE c.status = 'Active'")]
    [InlineData("Cronz", "SELECT min(p.credential_issued_on) FROM display p WHERE p.credential_hash IS NOT NULL AND p.deleted_on IS NULL")]
    [InlineData("Cronz", "SELECT min(o.secret_changed_utc) FROM outbound_endpoint o WHERE o.status = 'Active'")]
    [InlineData("Cronz", "SELECT u.totp_secret_protected FROM \"user\" u UNION ALL SELECT c.totp_secret_protected FROM integration_client c UNION ALL SELECT o.secret_protected FROM outbound_endpoint o")]
    [InlineData("Main", "SELECT u.totp_secret_protected FROM \"user\" u UNION ALL SELECT c.totp_secret_protected FROM integration_client c UNION ALL SELECT o.secret_protected FROM outbound_endpoint o")]
    [InlineData("Integration", "UPDATE email_message SET status = status WHERE false")]
    [InlineData("Integration", "SELECT count(*) FROM integration_idempotency")]
    [InlineData("Availability", "UPDATE availability_cursor SET next_minute_utc = next_minute_utc WHERE false")]
    [InlineData("Availability", "SELECT count(*) FROM availability_minute")]
    [InlineData("Availability", "SELECT count(*) FROM schema_version")]
    // The account columns every host's checks and recipients read (the base), and the credentials Main and Cronz read.
    [InlineData("Main", "SELECT password_hash, password_salt, password_algorithm, password_iterations, totp_secret_protected FROM \"user\"")]
    [InlineData("Ingest", AccountColumns)]
    [InlineData("Stream", AccountColumns)]
    [InlineData("Cronz", AccountColumns)]
    [InlineData("Cronz", "SELECT id, totp_secret_protected FROM \"user\" WHERE totp_secret_protected IS NOT NULL")]
    [InlineData("Integration", AccountColumns)]
    public async Task HostLogin_Should_MakeTheReadsAndWritesItsHostMakes_When_ItRunsThem(string host, string sql)
    {
        var (database, logins) = await MigratedAsync();
        var (login, password) = LoginOf(logins, host);

        (await RefusedAsync(postgres.ConnectionString(database, login, password), sql)).Should().BeNull($"{host}: {sql}");
    }

    [Fact]
    public async Task MigrationJob_Should_RefuseAClusterWithoutHostLogins_When_TheyAreMissing_And_GiveVmLocalsSharedLoginEveryRole()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.HostAccessDevelopment);
        var admin = postgres.ConnectionString(database);
        const string shared = "it_ha_shared_app";
        var password = HostLogins.NewPassword("dev-");

        // A cluster without the host logins: the job stops before anything changes.
        (await DatabaseMigration.RunAsync(Configuration(Settings(database, "k8s-dev", shared, password)), Ct)).Should().Be(1);
        (await RowsAsync(admin, "SELECT (to_regclass('public.schema_version') IS NULL)::text")).Should().Equal(["true"], "no script ran");

        // vm-local without them: the hosts share Database:Username, which holds every host role.
        (await DatabaseMigration.RunAsync(Configuration(Settings(database, "vm-local", shared, password)), Ct)).Should().Be(0);
        (await RowsAsync(admin, Memberships, ("login", shared))).Should().Equal("ariva_availability,ariva_cronz,ariva_ingest,ariva_integration,ariva_main,ariva_runtime,ariva_stream");
        (await RowsAsync(admin, "SELECT left(rolpassword, 19) FROM pg_authid WHERE rolname = @login", ("login", shared))).Should().Equal("SCRAM-SHA-256$4096:");

        // It is no host's login (a cluster's job refuses it as one), and a database that records a cluster's environment refuses
        // a development login altogether.
        var asHost = () => ExecuteAsync(admin, "SELECT ariva_ensure_host_login(@login, @verifier, 'ariva_main')", ("login", shared), ("verifier", ScramVerifier.For(password)));
        (await asHost.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("is a member of");
        await ExecuteAsync(admin, "UPDATE database_environment SET environment = 'k8s-dev' WHERE id = 1");
        var development = () => ExecuteAsync(admin, "SELECT ariva_ensure_development_login('it_ha_other_dev', @verifier)", ("verifier", ScramVerifier.For(password)));
        (await development.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("only a vm-local database");
        (await RowsAsync(admin, "SELECT count(*)::text FROM pg_roles WHERE rolname = 'it_ha_other_dev'")).Should().Equal("0");
    }

    #endregion

    #region Helpers

    /// <summary>What the session, permission and site checks, the alert emails and the report deliveries read of an account.</summary>
    private const string AccountColumns = """
        SELECT u.id, u.user_name, u.display_name, u.email, u.is_disabled, u.all_sites, u.is_break_glass, u.must_change_password, u.totp_enrolled
          FROM "user" u JOIN user_role r ON r.user_id = u.id LEFT JOIN user_site s ON s.user_id = u.id
          JOIN user_session x ON x.user_id = u.id WHERE NOT u.is_disabled
        """;

    /// <summary>Every role's privileges on the relations of public, as HostDatabaseAccess and ariva-db.sh write them.</summary>
    private const string Privileges = """
        WITH table_privileges(p, i) AS (
            SELECT t.p, t.i FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']
                   || CASE WHEN current_setting('server_version_num')::integer >= 170000 THEN ARRAY['MAINTAIN'] ELSE ARRAY[]::text[] END) WITH ORDINALITY t(p, i)),
        column_privileges(p, i) AS (
            SELECT t.p, t.i FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'REFERENCES']) WITH ORDINALITY t(p, i)),
        roles(name) AS (
            SELECT unnest(ARRAY['ariva_runtime', 'ariva_main', 'ariva_ingest', 'ariva_stream', 'ariva_cronz', 'ariva_integration', 'ariva_availability'])),
        granted AS (
            SELECT r.name AS role, c.relname::text AS relation,
                   concat_ws(',', VARIADIC ARRAY(
                       SELECT t.p FROM table_privileges t WHERE has_table_privilege(r.name, c.oid, t.p) ORDER BY t.i)
                     || ARRAY(
                       SELECT t.p || '(' || string_agg(a.attname::text, ' ' ORDER BY a.attname) || ')'
                         FROM column_privileges t
                         JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                        WHERE NOT has_table_privilege(r.name, c.oid, t.p) AND has_column_privilege(r.name, c.oid, a.attnum, t.p)
                        GROUP BY t.p, t.i ORDER BY t.i)) AS privileges
              FROM roles r CROSS JOIN pg_class c
             WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
               AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e'))
        SELECT role || ' ' || relation || '=' || privileges FROM granted WHERE privileges <> '' ORDER BY role COLLATE "C", relation COLLATE "C"
        """;

    /// <summary>The roles a login holds, in byte order, comma separated.</summary>
    private const string Memberships = """
        SELECT string_agg(g.rolname, ',' ORDER BY g.rolname) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
         WHERE m.member = (SELECT oid FROM pg_roles WHERE rolname = @login)
        """;

    /// <summary>One database migrated by a cluster's job with every host's login, for the class.</summary>
    private async Task<(string Database, Dictionary<string, string> Logins)> MigratedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_database is null)
            {
                var database = await postgres.CreateDatabaseAsync(TestDatabase.HostAccess);
                var logins = HostLogins.ClusterSettings(Prefix);
                var settings = Settings(database, "k8s-dev", logins["Database:HostLogins:Main:Username"], logins["Database:HostLogins:Main:Password"]);
                foreach (var (key, value) in logins)
                    settings[key] = value;
                (await DatabaseMigration.RunAsync(Configuration(settings), Ct)).Should().Be(0, "the cluster's job with every host login");
                _logins = logins;
                _database = database;
            }

            return (_database, _logins);
        }
        finally
        {
            Gate.Release();
        }
    }

    private Dictionary<string, string> Settings(string database, string environment, string runtime, string runtimePassword) => new(StringComparer.Ordinal)
    {
        ["Application:Environment"] = environment,
        ["Database:Host"] = postgres.Hostname,
        ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
        ["Database:Name"] = database,
        ["Database:Migration:Username"] = postgres.AdminUsername,
        ["Database:Migration:Password"] = postgres.AdminPassword,
        ["Database:Username"] = runtime,
        ["Database:Password"] = runtimePassword
    };

    private static IConfiguration Configuration(Dictionary<string, string> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static (string Login, string Password) LoginOf(Dictionary<string, string> logins, string host) =>
        host == "Availability"
            ? (logins["Database:Availability:Username"], logins["Database:Availability:Password"])
            : (logins[$"Database:HostLogins:{host}:Username"], logins[$"Database:HostLogins:{host}:Password"]);

    // The statements are this class's literals (the inline data and the arrays above); values reach them as parameters.
    private static async Task<List<string>> RowsAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // literal statements of this class
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
            rows.Add(reader.IsDBNull(0) ? null : reader.GetValue(0).ToString());
        return rows;
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // literal statements of this class
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (await command.ExecuteScalarAsync(Ct))?.ToString();
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // literal statements of this class, values as parameters
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The statement in a transaction rolled back: the SQLSTATE it is refused with, or null when it runs.</summary>
    private static async Task<string> RefusedAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // literal statements of this class
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        try
        {
            await command.ExecuteNonQueryAsync(Ct);
            return null;
        }
        catch (PostgresException e)
        {
            return e.SqlState;
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    #endregion
}
