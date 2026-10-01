using Ariva.Infra.Settings;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// vm-local only (Database:AllowSchemaUpdate): every host migrates the development database at startup, one at a
/// time behind an advisory lock, so a developer never runs the migration job by hand.
/// </summary>
internal sealed class DevelopmentMigrationService(DatabaseMigrator migrator, DatabaseSettings settings) : IHostedService
{
    private const long HostLockKey = 7_262_001;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.BuildMigrationConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            acquire.Parameters.AddWithValue("key", HostLockKey);
            await acquire.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await migrator.MigrateAsync(cancellationToken);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            release.Parameters.AddWithValue("key", HostLockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Every host outside vm-local (Database:VerifySchemaOnStartup): stop at startup when the database lacks a shipped
/// script or an applied script was changed, instead of running new code against the wrong schema.
/// </summary>
internal sealed class SchemaVersionGate(DatabaseSettings settings, ILoggerFactory loggerFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var runner = new SqlScriptRunner(settings.BuildConnectionString(), loggerFactory.CreateLogger<SqlScriptRunner>());
        await runner.VerifyAsync(SqlScriptCatalog.Embedded(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
