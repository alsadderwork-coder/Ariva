using Ariva.Infra.Settings;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// The environment a database belongs to (ARV-180, CWE-269; script 0064, <c>database_environment</c>). The migration job
/// records its <c>Application:Environment</c> after the scripts (<see cref="RecordAsync"/>). A vm-local host migrates the
/// database it is given and seeds the development accounts, whose passwords are known; it reads the record before either
/// (<see cref="RecordedAsync"/>, in <see cref="DatabaseMigrator"/> and <see cref="DevelopmentDatabaseGuard"/>) and refuses a
/// database that names another environment (<see cref="ProblemWith"/>): its Database settings reach a deployed store, through
/// a tunnel or a port forward. Nothing is recorded for a process without an environment (the integration tests' own runs).
/// </summary>
internal sealed class DatabaseEnvironment(string name)
{
    #region Fields

    public const string VmLocal = "vm-local";

    /// <summary>The setting the environment comes from, as for every other environment check of the hosts.</summary>
    public const string Setting = "Application:Environment";

    #endregion

    #region Properties

    /// <summary>The process's environment, or null when it names none.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? null : name;

    /// <summary>Whether this process runs as a developer machine's host, which migrates and seeds its own database.</summary>
    public bool IsVmLocal => string.Equals(Name, VmLocal, StringComparison.Ordinal);

    #endregion

    #region Methods

    public static DatabaseEnvironment FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new DatabaseEnvironment(configuration[Setting]);
    }

    /// <summary>Stops the migration before anything changes when the environment cannot be recorded (the script's own rule).</summary>
    public void EnsureValid()
    {
        if (Name is not null && !IsValidName(Name))
            throw new InvalidOperationException($"{Setting} must be at most 32 lower-case letters, digits and hyphens to be recorded in the database (ARV-180).");
    }

    /// <summary>
    /// Why this process may not migrate or seed a database that records <paramref name="recorded"/>, or null when it may: only a
    /// vm-local process is refused, and only by a database that names another environment.
    /// </summary>
    public string ProblemWith(string recorded) =>
        IsVmLocal && recorded is not null && !string.Equals(recorded, VmLocal, StringComparison.Ordinal)
            ? $"The database belongs to the environment '{recorded}' (database_environment, recorded by its migration job), and this " +
              "process runs as vm-local, which migrates its database and seeds the development accounts: it does not start against " +
              "another environment's database (ARV-180). Check Database:Host, Database:Port and Database:Name (a tunnel or a port " +
              "forward to a deployed store?)."
            : null;

    /// <summary>The environment the database records, or null: none recorded yet, or a database without script 0064.</summary>
    public static async Task<string> RecordedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using (var exists = new NpgsqlCommand("SELECT pg_catalog.to_regclass('public.database_environment') IS NOT NULL", connection))
        {
            if (await exists.ExecuteScalarAsync(ct) is not true)
                return null;
        }

        await using var read = new NpgsqlCommand("SELECT environment FROM public.database_environment WHERE id = 1", connection);
        return await read.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Stops the process when <see cref="ProblemWith"/> the database's record says so.</summary>
    public async Task EnsureMayUseAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (ProblemWith(await RecordedAsync(connection, ct)) is { } problem)
            throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// Records this environment (the migration job, with the migration login, right after the scripts), or nothing when the
    /// process names none. A change from another environment is logged as a warning: a backup restored into another environment,
    /// or a migration job pointed at the wrong database. A cluster's job that finds vm-local recorded logs an error instead: a
    /// developer's process migrated this database and may have seeded the development accounts into it (wiki 10 section 4.23).
    /// </summary>
    public async Task RecordAsync(string migrationConnection, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (Name is null)
        {
            logger.LogWarning("{Setting} is not set: the database's environment is not recorded, so a vm-local host cannot tell it from its own (ARV-180)", Setting);
            return;
        }

        await using var connection = PostgresConnections.Create(migrationConnection);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            WITH previous AS (SELECT environment FROM public.database_environment WHERE id = 1)
            INSERT INTO public.database_environment (id, environment, set_on, set_by) VALUES (1, @environment, now(), left(session_user, 100))
            ON CONFLICT (id) DO UPDATE SET environment = EXCLUDED.environment, set_on = EXCLUDED.set_on, set_by = EXCLUDED.set_by
            RETURNING (SELECT environment FROM previous)
            """, connection);
        command.Parameters.AddWithValue("environment", Name);
        var previous = await command.ExecuteScalarAsync(ct) as string;
        if (string.Equals(previous, VmLocal, StringComparison.Ordinal) && !IsVmLocal)
            logger.LogError("The database recorded the environment vm-local and now records {Environment}: a developer's vm-local process migrated it and may have seeded the development accounts (known passwords). Disable every account created or changed by development-seed and check the audit trail (wiki 10 section 4.23, ARV-180)", Name);
        else if (previous is not null && !string.Equals(previous, Name, StringComparison.Ordinal))
            logger.LogWarning("The database's environment changed from {Previous} to {Environment}: a restored backup, or a migration job pointed at another environment's database?", previous, Name);
        else
            logger.LogInformation("The database records the environment {Environment}", Name);
    }

    /// <summary>The script's own check: 1 to 32 lower-case ASCII letters, digits and hyphens, not starting with a hyphen.</summary>
    internal static bool IsValidName(string name) =>
        name is { Length: > 0 and <= 32 } && name[0] != '-' && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    #endregion
}
