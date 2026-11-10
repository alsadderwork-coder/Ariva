using System.Globalization;
using System.Security.Cryptography;
using Ariva.Di;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Settings;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-292 (CWE-400) against PostgreSQL 17 with TimescaleDB: a runtime login session left idle inside its transaction (a host
/// whose connection was lost on the network) is ended by the server past the login's limit (idle_in_transaction_session_timeout,
/// set by the migration job through script 0070), so its locks go: a report delivery's row and the deliveries' report slot,
/// which another session then takes, and nothing of its transaction commits. A transaction whose idle stretches each stay below
/// the limit commits however long it lasts in all. The migration job sets the limit and the TCP keepalives on the runtime login
/// only. ARV-294: a host refuses to start when the limit the server applies to its runtime login is not above its longest wait. Roles
/// are cluster-wide, so every test uses login names of its own.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseSessionLimitTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Delivery = Guid.Parse("a2920000-0000-0000-0000-000000000004");

    private static string Secret() => "sl-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    [Fact(Timeout = 120_000)]
    public async Task IdleLimit_Should_EndASessionIdleInItsTransactionAndFreeItsLocks_When_ItPasses()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.SessionLimits);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        const string login = "it_sl_app";
        var password = Secret();
        // ARV-083a: a delivery round is Cronz's (it locks and updates report_delivery), so the round's hold runs as Cronz's login.
        await ExecuteAsync(admin, "SELECT ariva_ensure_host_login(@name, @verifier, 'ariva_cronz')", ("name", login), ("verifier", ScramVerifier.For(password)));
        // Three seconds here, so the test is quick; the migration job takes Database:IdleInTransactionTimeoutSeconds (300 to 7,200).
        await ExecuteAsync(admin, "SELECT ariva_set_runtime_session_limits(@name, 3, 60, 10, 6)", ("name", login));
        await ExecuteAsync(admin, """
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'SLM', 'Session limits');
            INSERT INTO "user" (id, user_name, password_hash, password_salt, password_algorithm, password_iterations)
            VALUES ('a2920000-0000-0000-0000-000000000001', 'it.session.owner', 'x', 'x', 'PBKDF2-SHA256', 600000);
            INSERT INTO report_schedule (id, site_code, name, template, send_at, enabled, owner_id)
            VALUES ('a2920000-0000-0000-0000-000000000002', 'SLM', 'Daily', 'DailyPeaks', '06:00', true, 'a2920000-0000-0000-0000-000000000001');
            INSERT INTO report_delivery (id, schedule_id, report_date, recipient_id, status, created_utc)
            VALUES ('a2920000-0000-0000-0000-000000000004', 'a2920000-0000-0000-0000-000000000002', DATE '2026-11-04', 'a2920000-0000-0000-0000-000000000001', 'Pending', now());
            """);
        var asRuntime = postgres.ConnectionString(database, login, password);

        // A delivery round's hold: the row FOR UPDATE and the last report slot, then the host goes away without a word.
        await using var lost = new NpgsqlConnection(asRuntime);
        await lost.OpenAsync(Ct);
        await using var held = await lost.BeginTransactionAsync(Ct);
        await using (var claim = new NpgsqlCommand("SELECT id FROM report_delivery WHERE id = @id FOR UPDATE", lost, held))
        {
            claim.Parameters.AddWithValue("id", Delivery);
            (await claim.ExecuteScalarAsync(Ct)).Should().Be(Delivery);
        }

        await using (var write = new NpgsqlCommand("UPDATE report_delivery SET status = 'Sent', sent_utc = now() WHERE id = @id", lost, held))
        {
            write.Parameters.AddWithValue("id", Delivery);
            (await write.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        await using (var slot = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@class, @slot)", lost, held))
        {
            slot.Parameters.AddWithValue("class", ReportReader.SlotLockClass);
            slot.Parameters.AddWithValue("slot", 2);
            (await slot.ExecuteScalarAsync(Ct)).Should().Be(true);
        }

        (await TryTakeAsync(asRuntime)).Should().Be("row locked, slot held", "while the session is within its limit, its locks hold");
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);

        (await TryTakeAsync(asRuntime)).Should().Be("row taken, slot taken", "the server ended the idle session past its limit, and its locks went with it");
        var reuse = () => new NpgsqlCommand("SELECT 1", lost, held).ExecuteScalarAsync(Ct);
        await reuse.Should().ThrowAsync<NpgsqlException>("the server closed the session");
        (await ScalarAsync(admin, "SELECT status FROM report_delivery WHERE id = 'a2920000-0000-0000-0000-000000000004'")).Should().Be("Pending", "nothing of the ended transaction committed");
        (await ScalarAsync(admin, "SELECT count(*)::text FROM pg_stat_activity WHERE usename = 'it_sl_app' AND state = 'idle in transaction'")).Should().Be("0");
    }

    [Fact(Timeout = 120_000)]
    public async Task IdleLimit_Should_LetATransactionCommit_When_EachIdleStretchStaysBelowIt()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.SessionLimitLong);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        const string login = "it_sl_long";
        var password = Secret();
        await ExecuteAsync(admin, "SELECT ariva_ensure_host_login(@name, @verifier, 'ariva_main')", ("name", login), ("verifier", ScramVerifier.For(password)));
        await ExecuteAsync(admin, "SELECT ariva_set_runtime_session_limits(@name, 3, 60, 10, 6)", ("name", login));
        await ExecuteAsync(admin, "INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'SLL', 'Session long')");

        // Six seconds in all, twice the limit, but never idle for two seconds at a stretch: the limit counts each idle stretch.
        await using (var connection = new NpgsqlConnection(postgres.ConnectionString(database, login, password)))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            for (var step = 0; step < 4; step++)
            {
                await using var touch = new NpgsqlCommand("UPDATE site SET name = @name WHERE code = 'SLL'", connection, transaction);
                touch.Parameters.AddWithValue("name", "Session long " + step.ToString(CultureInfo.InvariantCulture));
                await touch.ExecuteNonQueryAsync(Ct);
                await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
            }

            await transaction.CommitAsync(Ct);
        }

        (await ScalarAsync(admin, "SELECT name FROM site WHERE code = 'SLL'")).Should().Be("Session long 3");
    }

    [Fact]
    public async Task SetRuntimeSessionLimits_Should_RefuseOtherLoginsAndValuesOutOfRange_When_Called()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.SessionLimitRefusals);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        await ExecuteAsync(admin, "SELECT ariva_ensure_host_login(@name, @verifier, 'ariva_main')", ("name", "it_sl_member"), ("verifier", ScramVerifier.For(Secret())));
        await ExecuteAsync(admin, "DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'it_sl_outsider') THEN CREATE ROLE it_sl_outsider LOGIN; END IF; END $$");

        async Task<string> RefusalOf(string name, int idle, int keepIdle = 60, int keepInterval = 10, int keepCount = 6)
        {
            var call = () => ExecuteAsync(admin, "SELECT ariva_set_runtime_session_limits(@name, @idle, @keepIdle, @keepInterval, @keepCount)",
                ("name", name), ("idle", idle), ("keepIdle", keepIdle), ("keepInterval", keepInterval), ("keepCount", keepCount));
            return (await call.Should().ThrowAsync<PostgresException>()).Which.MessageText;
        }

        (await RefusalOf("it_sl_member", 0)).Should().Contain("1 to 7200 seconds");
        (await RefusalOf("it_sl_member", 7201)).Should().Contain("1 to 7200 seconds");
        (await RefusalOf("it_sl_member", 960, keepIdle: 0)).Should().Contain("keepalives");
        (await RefusalOf("it_sl_member", 960, keepInterval: 61)).Should().Contain("keepalives");
        (await RefusalOf("it_sl_member", 960, keepCount: 21)).Should().Contain("keepalives");
        (await RefusalOf("x; DROP ROLE ariva", 960)).Should().Contain("invalid runtime login name");
        (await RefusalOf("it_sl_nobody", 960)).Should().Contain("does not exist");
        (await RefusalOf("it_sl_outsider", 960)).Should().Contain("not a member of ariva_runtime");
        (await RefusalOf(postgres.AdminUsername, 960)).Should().Contain("privileged");
        (await ScalarAsync(admin, "SELECT count(*)::text FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase WHERE d.datname = current_database()"))
            .Should().Be("0", "nothing was set by a refused call");
        (await ScalarAsync(admin, "SELECT has_function_privilege('public', 'ariva_set_runtime_session_limits(text,integer,integer,integer,integer)', 'EXECUTE')::text")).Should().Be("false");
        (await ScalarAsync(admin, "SELECT has_function_privilege('ariva_runtime', 'ariva_set_runtime_session_limits(text,integer,integer,integer,integer)', 'EXECUTE')::text")).Should().Be("false");
    }

    [Fact]
    public async Task MigrationJob_Should_SetTheIdleLimitAndKeepalivesOnTheRuntimeLoginOnly_When_ItRuns()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.SessionLimitMigration);
        const string runtime = "it_sl_migrated";
        var password = Secret();

        (await DatabaseMigration.RunAsync(Configuration(database, runtime, password, idleSeconds: 1200), Ct)).Should().Be(0);

        var asRuntime = postgres.ConnectionString(database, runtime, password);
        (await ScalarAsync(asRuntime, "SHOW idle_in_transaction_session_timeout")).Should().Be("20min");
        (await ScalarAsync(asRuntime, "SHOW tcp_keepalives_idle")).Should().Be(DatabaseSettings.KeepaliveIdleSeconds.ToString(CultureInfo.InvariantCulture));
        (await ScalarAsync(asRuntime, "SHOW tcp_keepalives_interval")).Should().Be(DatabaseSettings.KeepaliveIntervalSeconds.ToString(CultureInfo.InvariantCulture));
        (await ScalarAsync(asRuntime, "SHOW tcp_keepalives_count")).Should().Be(DatabaseSettings.KeepaliveCount.ToString(CultureInfo.InvariantCulture));
        (await ScalarAsync(postgres.ConnectionString(database), "SHOW idle_in_transaction_session_timeout")).Should().Be("0", "the migration login keeps no limit");
        (await ScalarAsync(postgres.ConnectionString(database), """
            SELECT string_agg(r.rolname || ' ' || c, ',' ORDER BY c) FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole JOIN pg_database d ON d.oid = s.setdatabase
            CROSS JOIN LATERAL unnest(s.setconfig) c WHERE d.datname = current_database() AND (c LIKE 'idle_in_transaction%' OR c LIKE 'tcp_keepalives%') AND r.rolname <> 'it_sl_migrated'
            """)).Should().BeEmpty("on the runtime login only");

        // Out of range, the job stops at start, before anything changes.
        var outOfRange = () => DatabaseMigration.RunAsync(Configuration(database, runtime, password, idleSeconds: 120), Ct);
        await outOfRange.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Database:IdleInTransactionTimeoutSeconds is a whole number of seconds from 300*");
        (await ScalarAsync(asRuntime, "SHOW idle_in_transaction_session_timeout")).Should().Be("20min", "unchanged");
    }

    /// <summary>
    /// ARV-294 (the ARV-292 review's recommendation 8): a host reads at start the idle transaction limit the server applies to its
    /// runtime login (SHOW, on a session of that login) and refuses to start when it is not above its longest wait (a report's send
    /// within its budget, 180 s, at the defaults), so a limit set from another copy of the settings, or by hand, is caught: 100 s and
    /// 180 s refuse, naming the wait; 181 s and the default 300 s start; and a login with no limit (0) starts too.
    /// </summary>
    [Fact]
    public async Task Gate_Should_RefuseToStart_When_TheServersLimitOnTheRuntimeLoginIsNotAboveTheLongestWait()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.SessionLimitGate);
        var admin = postgres.ConnectionString(database);
        await new SqlScriptRunner(admin, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
        const string login = "it_sl_gate";
        var password = Secret();
        // ARV-083a: a host's login as the migration job creates it (script 0074 removed the function that took a plain password).
        await HostLogins.EnsureAsync(admin, login, password, HostDatabaseAccess.MainRole, Ct);
        var settings = DatabaseSettings.FromConfiguration(Configuration(database, login, password, idleSeconds: 300, verifySchema: true));
        var budget = IdleTransactionBudget.FromConfiguration(new ConfigurationBuilder().Build(), settings);
        budget.Longest.Seconds.Should().Be(180);

        async Task<Exception> StartWith(int idleSeconds)
        {
            if (idleSeconds > 0)
                await ExecuteAsync(admin, "SELECT ariva_set_runtime_session_limits(@name, @idle, 60, 10, 6)", ("name", login), ("idle", idleSeconds));
            // A new session of the login takes the limit just set.
            NpgsqlConnection.ClearAllPools();
            return await Record.ExceptionAsync(() => new IdleTransactionLimitGate(settings, budget, NullLogger<IdleTransactionLimitGate>.Instance).StartAsync(Ct));
        }

        (await StartWith(0)).Should().BeNull("no limit on the login ends no transaction");
        (await StartWith(100)).Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(
            "The runtime login's idle_in_transaction_session_timeout on the server (100 s) must be above the longest a transaction of this host may wait on other work: "
            + "a scheduled report's send, its delivery row and the report slot held, 180 s (ARV-294). Run the migration job with Database:IdleInTransactionTimeoutSeconds (300 s), "
            + "or set the login's limit above that wait.");
        (await StartWith(180)).Should().BeOfType<InvalidOperationException>("a limit equal to the longest wait is not above it");
        (await StartWith(181)).Should().BeNull();
        (await StartWith(300)).Should().BeNull("the default");
        (await ScalarAsync(postgres.ConnectionString(database, login, password), "SHOW idle_in_transaction_session_timeout")).Should().Be("5min", "what the gate read");
    }

    /// <summary>Whether another runtime session takes the delivery's row and the report slot without waiting.</summary>
    private static async Task<string> TryTakeAsync(string asRuntime)
    {
        await using var connection = new NpgsqlConnection(asRuntime);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var row = new NpgsqlCommand("SELECT count(*)::int FROM (SELECT id FROM report_delivery WHERE id = @id FOR UPDATE SKIP LOCKED) free", connection, transaction);
        row.Parameters.AddWithValue("id", Delivery);
        var rowTaken = Convert.ToInt32(await row.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture) == 1;
        await using var slot = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@class, 2)", connection, transaction);
        slot.Parameters.AddWithValue("class", ReportReader.SlotLockClass);
        var slotTaken = Convert.ToBoolean(await slot.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
        await transaction.RollbackAsync(Ct);
        return $"{(rowTaken ? "row taken" : "row locked")}, {(slotTaken ? "slot taken" : "slot held")}";
    }

    private IConfiguration Configuration(string database, string runtime, string password, int idleSeconds, bool verifySchema = false) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:VerifySchemaOnStartup"] = verifySchema ? "true" : "false",
            ["Database:Host"] = postgres.Hostname,
            ["Database:Port"] = postgres.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = database,
            ["Database:Username"] = runtime,
            ["Database:Password"] = password,
            ["Database:Migration:Username"] = postgres.AdminUsername,
            ["Database:Migration:Password"] = postgres.AdminPassword,
            ["Database:IdleInTransactionTimeoutSeconds"] = idleSeconds.ToString(CultureInfo.InvariantCulture)
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
