using Ariva.Infra.Settings;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.Infra.NHibernate;

/// <summary>
/// Runs NHibernate SchemaUpdate at startup on a developer machine. Registered only when Database:AllowSchemaUpdate is
/// true (vm-local). Every host starts it, so a PostgreSQL advisory lock lets one host change the schema at a time.
/// </summary>
internal sealed class DevelopmentSchemaUpdateService(
    NHibernateSessionFactoryProvider factoryProvider,
    DatabaseSettings settings,
    ILogger<DevelopmentSchemaUpdateService> logger) : IHostedService
{
    /// <summary>Arbitrary constant shared by all hosts; only its uniqueness within the database matters.</summary>
    private const long SchemaLockKey = 7_262_001;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(settings.BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            lockCommand.Parameters.AddWithValue("key", SchemaLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var script = factoryProvider.UpdateSchema();
            logger.LogInformation("Development schema update applied ({Length} characters of DDL)", script.Length);
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCommand.Parameters.AddWithValue("key", SchemaLockKey);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
