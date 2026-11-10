using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-006: the versioned script runner against PostgreSQL (TimescaleDB image): ordering, idempotent re-run, tamper
/// detection, a failing script leaving no trace, the startup check, concurrent runners, and the roles script.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SqlScriptRunnerTests(PostgresFixture fixture)
{
    private static readonly SqlScript CreateTable = SqlScript.From("0001_create_probe.sql", "CREATE TABLE probe (id int PRIMARY KEY, note text);");
    private static readonly SqlScript InsertRow = SqlScript.From("0002_insert_probe.sql", "INSERT INTO probe (id, note) VALUES (1, 'after 0001');");
    private static readonly SqlScript AddColumn = SqlScript.From("0010_add_probe_column.sql", "ALTER TABLE probe ADD COLUMN added_on timestamptz;");

    private SqlScriptRunner Runner(string database) =>
        new(fixture.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance);

    [Fact]
    public async Task ApplyAsync_Should_ApplyInNumericOrderAndRecordChecksums_When_ScriptsArePending()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.RunnerOrder);
        var scripts = SqlScriptCatalog.Order([AddColumn, InsertRow, CreateTable]);

        var applied = await Runner(database).ApplyAsync(scripts);

        applied.Should().Equal("0001_create_probe.sql", "0002_insert_probe.sql", "0010_add_probe_column.sql");
        var recorded = await Runner(database).ReadAppliedAsync();
        recorded.Select(r => (r.Name, r.Checksum.Trim())).Should().Equal(scripts.Select(s => (s.Name, s.Checksum)));
    }

    [Fact]
    public async Task ApplyAsync_Should_ApplyNothing_When_RunAgain()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.RunnerRerun);
        var scripts = SqlScriptCatalog.Order([CreateTable, InsertRow]);
        await Runner(database).ApplyAsync(scripts);

        var second = await Runner(database).ApplyAsync(scripts);

        second.Should().BeEmpty();
        (await CountAsync(database, "SELECT count(*) FROM probe")).Should().Be(1, "the insert script ran exactly once");
    }

    [Fact]
    public async Task ApplyAsync_Should_StopWithDrift_When_AppliedScriptWasChanged()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.RunnerTamper);
        await Runner(database).ApplyAsync(SqlScriptCatalog.Order([CreateTable, InsertRow]));
        var tampered = SqlScript.From("0002_insert_probe.sql", "INSERT INTO probe (id, note) VALUES (2, 'edited after release');");
        var next = SqlScript.From("0003_more.sql", "INSERT INTO probe (id, note) VALUES (3, 'never applied');");

        var act = () => Runner(database).ApplyAsync(SqlScriptCatalog.Order([CreateTable, tampered, next]));

        await act.Should().ThrowAsync<SchemaDriftException>().WithMessage("*0002_insert_probe.sql was changed after it was applied*");
        (await CountAsync(database, "SELECT count(*) FROM schema_version")).Should().Be(2, "nothing runs once drift is found");
    }

    [Fact]
    public async Task ApplyAsync_Should_LeaveNoTrace_When_ScriptFails()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.RunnerFailure);
        var broken = SqlScript.From("0002_broken.sql", "INSERT INTO probe (id, note) VALUES (1, 'half');\nSELECT * FROM table_that_does_not_exist;");

        var act = () => Runner(database).ApplyAsync(SqlScriptCatalog.Order([CreateTable, broken]));

        await act.Should().ThrowAsync<PostgresException>();
        (await CountAsync(database, "SELECT count(*) FROM probe")).Should().Be(0, "the failed script's first statement is rolled back");
        (await Runner(database).ReadAppliedAsync()).Select(r => r.Name).Should().Equal("0001_create_probe.sql");
    }

    [Fact]
    public async Task VerifyAsync_Should_RejectPendingScripts_When_MigrationHasNotRun()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.RunnerVerify);
        var scripts = SqlScriptCatalog.Order([CreateTable, InsertRow]);

        var beforeMigration = () => Runner(database).VerifyAsync(scripts);
        await beforeMigration.Should().ThrowAsync<SchemaBehindException>().WithMessage("*0001_create_probe.sql*--migrate*");

        await Runner(database).ApplyAsync(scripts);
        var afterMigration = () => Runner(database).VerifyAsync(scripts);
        await afterMigration.Should().NotThrowAsync();

        var tampered = SqlScriptCatalog.Order([CreateTable, SqlScript.From("0002_insert_probe.sql", "SELECT 1;")]);
        var afterTamper = () => Runner(database).VerifyAsync(tampered);
        await afterTamper.Should().ThrowAsync<SchemaDriftException>();
    }

    [Fact]
    public async Task ApplyAsync_Should_ApplyEachScriptOnce_When_RunnersStartTogether()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.RunnerConcurrent);
        var scripts = SqlScriptCatalog.Order([CreateTable, InsertRow, AddColumn]);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Runner(database).ApplyAsync(scripts)));

        results.SelectMany(r => r).Should().HaveCount(3, "the advisory lock lets one runner apply and the others find nothing pending");
        (await CountAsync(database, "SELECT count(*) FROM probe")).Should().Be(1);
    }

    [Fact]
    public async Task RolesScripts_Should_GiveAHostLoginItsRolesGrantsAndNoDdl_When_Migrated()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.Roles);
        var migration = fixture.ConnectionString(database);
        var runtimePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        await new SqlScriptRunner(migration, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        // ARV-083a: a host login (here Ariva.Api.Stream's), from a SCRAM-SHA-256 verifier, as the migration job creates it.
        await HostLogins.EnsureAsync(migration, "ariva_app_it", runtimePassword, HostDatabaseAccess.StreamRole, TestContext.Current.CancellationToken);
        // A table created by the migration login after the scripts, as later scripts will: since script 0074 no default
        // privilege gives a host anything on it until its script grants it.
        await ExecuteAsync(migration, "CREATE TABLE zone_probe (id int PRIMARY KEY, name text)");

        var runtime = fixture.ConnectionString(database, "ariva_app_it", runtimePassword);
        await ExecuteAsync(runtime, "INSERT INTO stream_partition_count (topic_set, partitions, first_seen) VALUES ('it-roles', 1, now())");
        (await CountAsync(database, "SELECT count(*) FROM schema_version", runtime)).Should().BeGreaterThan(0, "hosts read schema_version at startup");

        var probe = () => ExecuteAsync(runtime, "INSERT INTO zone_probe (id, name) VALUES (1, 'A')");
        var configuration = () => ExecuteAsync(runtime, "UPDATE zone_profile SET name = name");
        var createTable = () => ExecuteAsync(runtime, "CREATE TABLE forbidden (id int)");
        var writeVersions = () => ExecuteAsync(runtime, "DELETE FROM schema_version");
        var dropTable = () => ExecuteAsync(runtime, "DROP TABLE zone_probe");

        (await probe.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "no default privilege");
        (await configuration.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "the stream reads profiles only");
        (await createTable.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await writeVersions.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await dropTable.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task EnsureHostLogin_Should_RefuseAPasswordABadNameOrRoleOrTheMigrationLogin_When_Called()
    {
        // Its own database: the shipped scripts create "user", which the sample schema in ariva_it also has.
        var migration = fixture.ConnectionString(await fixture.CreateDatabaseAsync(TestDatabase.RuntimeLogin));
        await new SqlScriptRunner(migration, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        var verifier = ScramVerifier.For(new string('p', 24));

        var password = () => ExecuteAsync(migration, "SELECT ariva_ensure_host_login('ariva_app_plain', @password, 'ariva_main')", new string('p', 24));
        var badName = () => ExecuteAsync(migration, "SELECT ariva_ensure_host_login('x; DROP ROLE ariva', @password, 'ariva_main')", verifier);
        var badRole = () => ExecuteAsync(migration, "SELECT ariva_ensure_host_login('ariva_app_role', @password, 'ariva_migration')", verifier);
        var migrationLogin = () => ExecuteAsync(migration, "SELECT ariva_ensure_host_login('ariva', @password, 'ariva_main')", verifier);

        await password.Should().ThrowAsync<PostgresException>().WithMessage("*SCRAM-SHA-256 verifier*never a password*");
        await badName.Should().ThrowAsync<PostgresException>().WithMessage("*invalid host login name*");
        await badRole.Should().ThrowAsync<PostgresException>().WithMessage("*invalid host role*");
        await migrationLogin.Should().ThrowAsync<PostgresException>().WithMessage("*login of its own*");
        (await CountAsync(string.Empty, "SELECT count(*) FROM pg_proc WHERE proname = 'ariva_ensure_runtime_login'", migration))
            .Should().Be(0, "script 0074 removes the function that took a plain password");
    }

    private async Task<long> CountAsync(string database, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, string connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? fixture.ConnectionString(database));
        await connection.OpenAsync();
        // Test helper: every caller passes a literal (ConstantExpected, CA1857 is an error), so CA2100 does not apply.
#pragma warning disable CA2100
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (long)await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, string password = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
#pragma warning disable CA2100 // test helper with literal SQL only, as above
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        if (password is not null)
            command.Parameters.AddWithValue("password", password);
        await command.ExecuteNonQueryAsync();
    }
}
