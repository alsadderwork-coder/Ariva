using Ariva.Infra.NHibernate;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// The migration job (ARV-006): applies the versioned scripts with the migration login, then creates or updates the
/// runtime login the hosts use and (ARV-104g1) the validation reader login, the only login that reads the shadow nowcast. On a
/// developer machine (Database:AllowSchemaUpdate) NHibernate SchemaUpdate then adds whatever the scripts do not cover yet,
/// also with the migration login.
/// </summary>
internal sealed class DatabaseMigrator(
    DatabaseSettings settings,
    ValidationReaderSettings reader,
    NHibernateSessionFactoryProvider factoryProvider,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DatabaseMigrator>();

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        // ARV-104g1: a reader login that is the runtime or the migration login, or is otherwise unusable, stops the job before
        // anything changes (no secret in the message).
        reader.EnsureValid(settings);

        var migrationConnection = settings.BuildMigrationConnectionString();
        var runner = new SqlScriptRunner(migrationConnection, loggerFactory.CreateLogger<SqlScriptRunner>());
        var applied = await runner.ApplyAsync(SqlScriptCatalog.Embedded(), ct);
        _logger.LogInformation("Migration applied {Count} script(s)", applied.Count);

        await EnsureRuntimeLoginAsync(migrationConnection, ct);
        await EnsureValidationReaderLoginAsync(migrationConnection, ct);

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

    /// <summary>
    /// ARV-104g1: creates or updates the validation reader login (script 0049: a member of ariva_validation_reader and nothing
    /// else, CONNECT on this database) from a SCRAM-SHA-256 verifier computed here, so its password never reaches the server,
    /// then stops the job if the hosts' runtime login can read a value of the shadow nowcast (<see cref="ShadowReadAccess"/>), so
    /// an upgrade that would let a host read it fails before any pod changes (the hosts check it again at start-up).
    /// </summary>
    private async Task EnsureValidationReaderLoginAsync(string migrationConnection, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(migrationConnection);
        await connection.OpenAsync(ct);

        if (reader.IsConfigured)
        {
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_validation_reader_login(@name, @verifier)", connection);
            ensure.Parameters.AddWithValue("name", reader.Username);
            ensure.Parameters.AddWithValue("verifier", ScramVerifier.For(reader.Password));
            await ensure.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Validation reader login {Login} is a member of ariva_validation_reader only", reader.Username);
        }
        else
        {
            _logger.LogInformation("Database:ValidationReader is not configured: no login reads the shadow nowcast (an existing reader login is left as it is)");
        }

        if (!settings.UsesSeparateRuntimeLogin)
            return;

        if (await ShadowReadAccess.CanReadAsync(connection, settings.Username, ct))
        {
            throw new InvalidOperationException(
                "The runtime login (Database:Username) can read the shadow nowcast: it holds ariva_validation_reader or pg_read_all_data (directly, " +
                "through another role, or as a superuser), or may select a value column of the shadow table or one of its chunks. Remove that access " +
                "and run the migration again (CWE-269, ARV-104g1).");
        }
    }
}
