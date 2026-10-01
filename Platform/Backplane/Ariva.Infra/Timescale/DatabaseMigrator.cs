using Ariva.Infra.NHibernate;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// The migration job (ARV-006): applies the versioned scripts with the migration login, then creates or updates the
/// runtime login the hosts use. On a developer machine (Database:AllowSchemaUpdate) NHibernate SchemaUpdate then adds
/// whatever the scripts do not cover yet, also with the migration login.
/// </summary>
internal sealed class DatabaseMigrator(
    DatabaseSettings settings,
    NHibernateSessionFactoryProvider factoryProvider,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DatabaseMigrator>();

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        var migrationConnection = settings.BuildMigrationConnectionString();
        var runner = new SqlScriptRunner(migrationConnection, loggerFactory.CreateLogger<SqlScriptRunner>());
        var applied = await runner.ApplyAsync(SqlScriptCatalog.Embedded(), ct);
        _logger.LogInformation("Migration applied {Count} script(s)", applied.Count);

        await EnsureRuntimeLoginAsync(migrationConnection, ct);

        if (settings.AllowSchemaUpdate)
            factoryProvider.UpdateSchema(migrationConnection);

        return applied;
    }

    private async Task EnsureRuntimeLoginAsync(string migrationConnection, CancellationToken ct)
    {
        if (!settings.UsesSeparateRuntimeLogin)
        {
            _logger.LogWarning("Database:Username equals the migration login; hosts will connect with DDL rights. Configure a separate runtime login.");
            return;
        }

        await using var connection = new NpgsqlConnection(migrationConnection);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@name, @password)", connection);
        command.Parameters.AddWithValue("name", settings.Username);
        command.Parameters.AddWithValue("password", settings.Password);
        await command.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Runtime login {Login} is a member of ariva_runtime", settings.Username);
    }
}
