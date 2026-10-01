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
    public async Task RolesScript_Should_GiveRuntimeLoginDmlOnly_When_Migrated()
    {
        var database = await fixture.CreateDatabaseAsync(TestDatabase.Roles);
        var migration = fixture.ConnectionString(database);
        var runtimePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        await new SqlScriptRunner(migration, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        await ExecuteAsync(migration, "SELECT ariva_ensure_runtime_login('ariva_app_it', @password)", runtimePassword);
        // A table created by the migration login after 0001, as later scripts will: default privileges apply.
        await ExecuteAsync(migration, "CREATE TABLE zone_probe (id int PRIMARY KEY, name text)");

        var runtime = fixture.ConnectionString(database, "ariva_app_it", runtimePassword);
        await ExecuteAsync(runtime, "INSERT INTO zone_probe (id, name) VALUES (1, 'A')");
        await ExecuteAsync(runtime, "UPDATE zone_probe SET name = 'B' WHERE id = 1");
        (await CountAsync(database, "SELECT count(*) FROM schema_version", runtime)).Should().BeGreaterThan(0, "hosts read schema_version at startup");

        var createTable = () => ExecuteAsync(runtime, "CREATE TABLE forbidden (id int)");
        var writeVersions = () => ExecuteAsync(runtime, "DELETE FROM schema_version");
        var dropTable = () => ExecuteAsync(runtime, "DROP TABLE zone_probe");

        (await createTable.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await writeVersions.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await dropTable.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task EnsureRuntimeLogin_Should_RefuseWeakOrPrivilegedLogins_When_Called()
    {
        var migration = fixture.ConnectionString("ariva_it");
        await new SqlScriptRunner(migration, NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());

        var shortPassword = () => ExecuteAsync(migration, "SELECT ariva_ensure_runtime_login('ariva_app_short', @password)", "too-short");
        var badName = () => ExecuteAsync(migration, "SELECT ariva_ensure_runtime_login('x; DROP ROLE ariva', @password)", new string('p', 24));
        var superuser = () => ExecuteAsync(migration, "SELECT ariva_ensure_runtime_login('ariva', @password)", new string('p', 24));

        await shortPassword.Should().ThrowAsync<PostgresException>().WithMessage("*at least 16 characters*");
        await badName.Should().ThrowAsync<PostgresException>().WithMessage("*invalid runtime login name*");
        await superuser.Should().ThrowAsync<PostgresException>().WithMessage("*privileged*");
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
