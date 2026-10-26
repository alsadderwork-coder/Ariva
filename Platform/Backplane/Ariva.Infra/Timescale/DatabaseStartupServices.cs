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
        await using var connection = PostgresConnections.Create(settings.BuildMigrationConnectionString());
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
/// ARV-180 (CWE-269, CWE-287): a vm-local host that writes to its database at start (it migrates it, seeds the development
/// accounts or seeds the demo topology) checks first that the database is not another environment's
/// (<see cref="DatabaseEnvironment"/>), with the migration login when it migrates (a fresh developer database has no runtime
/// login yet) and with the runtime login otherwise. The check runs in <see cref="StartingAsync"/>, which the host runs for every
/// lifecycle service before it starts any hosted service, so a refusal stops the host before any seed or migration whether the
/// services start one after another or at once (the review's finding H1). The migration checks again before any script, for
/// the migration command.
/// </summary>
internal sealed class DevelopmentDatabaseGuard(DatabaseSettings settings, DatabaseEnvironment environment) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        await using var connection = PostgresConnections.Create(settings.AllowSchemaUpdate ? settings.BuildMigrationConnectionString() : settings.BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await environment.EnsureMayUseAsync(connection, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
