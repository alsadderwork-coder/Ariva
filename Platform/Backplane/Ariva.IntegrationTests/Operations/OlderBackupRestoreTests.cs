using System.Text.RegularExpressions;
using Ariva.Di;
using Ariva.Infra.Timescale;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Operations;

/// <summary>
/// ARV-263 (CWE-269, CWE-345): a backup taken before this release's latest script that changes the runtime role's grants
/// (<c>ARIVA_RUNTIME_PRIVILEGES_AT</c>) restores as an operator restores it, on two servers started as the TimescaleDB chart
/// starts one (<see cref="ChartDatabase"/>). The source holds only the scripts before that one, as a deployment of an older
/// release does. The restore's own check passes the runtime role's privileges, the views and the schema as not compared (its
/// migration job follows) and its closing message says what to run; a plain check then fails on the same lines, and on the
/// runtime login the job has not made yet, until the release's migration job has run; after it, check passes, TimescaleDB's
/// jobs included. Last, the schema line on a database past <c>ARIVA_SCHEMA_AT</c> (a release's migration job run before this
/// release's ariva-db.sh) is not compared and fails, the runtime role's privileges still compared; with the database back at
/// the list, check passes again. <see cref="BackupRestoreTests"/> restores a backup at the latest script.
/// </summary>
public sealed partial class OlderBackupRestoreTests : IAsyncLifetime
{
    private const string RuntimeLogin = "ariva_app";
    private const string DumpPath = ChartDatabase.BackupDirectory + "/ariva-older.dump";
    private const string PastTheList = "9999_it_past_the_list.sql";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "ariva-it-older-restore-" + Guid.NewGuid().ToString("N"));
    private ChartDatabase _source;
    private ChartDatabase _target;

    public async ValueTask InitializeAsync()
    {
        var backup = Path.Combine(_workDirectory, "backup");
        Directory.CreateDirectory(backup);
        File.SetUnixFileMode(backup, (UnixFileMode)0b111_111_111);
        var servers = await Task.WhenAll(ChartDatabase.StartAsync(backup, Ct), ChartDatabase.StartAsync(backup, Ct));
        (_source, _target) = (servers[0], servers[1]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
            await _source.DisposeAsync();
        if (_target is not null)
            await _target.DisposeAsync();
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    [Fact(Timeout = 900_000)]
    public async Task Restore_Should_LeaveThePinnedLinesToTheMigrationJob_When_TheBackupIsOlderThanTheLists()
    {
        var script = await File.ReadAllTextAsync(Ariva.UnitTests.Setup.RepositoryPaths.Resolve("scripts/ops/ariva-db.sh"), Ct);
        var privilegesAt = Pinned(script, PrivilegesAtLine());
        var schemaAt = Pinned(script, SchemaAtLine());
        var older = SqlScriptCatalog.Embedded().Where(s => string.CompareOrdinal(s.Name, privilegesAt) < 0).ToList();
        older.Should().NotBeEmpty().And.HaveCountLessThan(SqlScriptCatalog.Embedded().Count);
        var olderAt = older.Select(s => s.Name).Order(StringComparer.Ordinal).Last();

        // The source: an older release's scripts, applied by its migration login, then its backup.
        var applied = await new SqlScriptRunner(_source.ConnectionString(ChartDatabase.MigrationLogin, _source.MigrationPassword), NullLogger<SqlScriptRunner>.Instance)
            .ApplyAsync(older, Ct);
        applied.Should().HaveCount(older.Count);
        var backup = await _source.OpsAsync(Ct, "backup", "--output", DumpPath);
        backup.ExitCode.Should().Be(0, backup.Stderr);

        // The restore: its own check passes what the migration job will bring level, says so, and fails nothing else.
        var restore = await _target.OpsAsync(Ct, "restore", "--input", DumpPath);
        restore.ExitCode.Should().Be(0, restore.Stderr + restore.Stdout);
        restore.Stdout.Should().Contain($"ok    runtime privileges|not compared: the database is at {olderAt} and the list at {privilegesAt} (run the migration job, then check again before starting the hosts)")
            .And.Contain("ok    views|not compared: the database is behind the list (as runtime privileges)")
            .And.Contain($"ok    schema|not compared: the database is at {olderAt} and the list at {schemaAt} (run the migration job, then check again before starting the hosts)")
            .And.Contain("ok    toast|").And.Contain("ok    system function grants|").And.Contain("ok    logins|")
            .And.NotContain("FAIL", restore.Stdout);
        restore.Stderr.Should().Contain("restore finished, but the backup is older than this release's scripts: the runtime role's privileges, the views and the schema are not compared yet. Next: run the migration job with api-main --migrate");

        // A plain check fails until the release's migration job has run: the same lines, and the runtime login it makes.
        var before = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        before.ExitCode.Should().NotBe(0, before.Stdout);
        before.Stdout.Should().Contain($"FAIL  runtime privileges|not compared: the database is at {olderAt} and the list at {privilegesAt} (run the migration job, then check again)")
            .And.Contain("FAIL  views|not compared: the database is behind the list (as runtime privileges)")
            .And.Contain($"FAIL  schema|not compared: the database is at {olderAt} and the list at {schemaAt} (run the migration job, then check again)")
            .And.Contain($"FAIL  runtime login|{RuntimeLogin} does not exist: run the migration job");

        // The release's migration job, then check compares everything and passes: the restored jobs and those the later scripts
        // add are the pinned ones.
        var settings = new ConfigurationBuilder().AddInMemoryCollection(_target.MigrationSettings(RuntimeLogin, RuntimePassword)).Build();
        (await DatabaseMigration.RunAsync(settings, Ct)).Should().Be(0);
        var after = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        after.ExitCode.Should().Be(0, after.Stdout + after.Stderr);
        after.Stdout.Should().Contain("ok    runtime privileges|ariva_runtime's grants on public as the scripts leave them")
            .And.Contain("ok    views|the views of public are the scripts' ")
            .And.Contain("ok    schema|Ariva's ").And.Contain("TimescaleDB's jobs included")
            .And.Contain($"ok    runtime login|{RuntimeLogin} holds ariva_runtime only (DML)")
            .And.NotContain("FAIL", after.Stdout);

        // Past the list (ARV-262 review, I3): a later release's migration job ran, this release's ariva-db.sh cannot vouch for the
        // schema; the runtime role's privileges stay compared (a later script may change none of them, ARV-257).
        var migration = _target.ConnectionString(ChartDatabase.MigrationLogin, _target.MigrationPassword);
        await ExecuteAsync(migration, "INSERT INTO schema_version (script_name, checksum, execution_ms) VALUES (@name, 'it', 0)");
        var past = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        past.ExitCode.Should().NotBe(0, past.Stdout);
        past.Stdout.Should().Contain($"FAIL  schema|not compared: the database is at {PastTheList}, past the list at {schemaAt} (run the ariva-db.sh of the release whose migration job ran)")
            .And.Contain("ok    runtime privileges|ariva_runtime's grants on public as the scripts leave them")
            .And.Contain("ok    views|the views of public are the scripts' ");
        await ExecuteAsync(migration, "DELETE FROM schema_version WHERE script_name = @name");
        var back = await _target.OpsAsync(Ct, "check", "--runtime-login", RuntimeLogin);
        back.ExitCode.Should().Be(0, back.Stdout);
    }

    #region Helpers

    private static string Pinned(string script, Regex line)
    {
        var match = line.Match(script);
        match.Success.Should().BeTrue($"ariva-db.sh declares {line}");
        return match.Groups["script"].Value;
    }

    /// <summary>Runs one of the two constant statements above, with the past-the-list script name bound.</summary>
    private static async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // both callers pass a constant statement (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("name", PastTheList);
        await command.ExecuteNonQueryAsync(Ct);
    }

    [GeneratedRegex(@"^readonly ARIVA_RUNTIME_PRIVILEGES_AT=""(?<script>[0-9]{4}_[a-z0-9_]+\.sql)""$", RegexOptions.Multiline)]
    private static partial Regex PrivilegesAtLine();

    [GeneratedRegex(@"^readonly ARIVA_SCHEMA_AT=""(?<script>[0-9]{4}_[a-z0-9_]+\.sql)""$", RegexOptions.Multiline)]
    private static partial Regex SchemaAtLine();

    #endregion
}
