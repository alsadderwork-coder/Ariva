using Ariva.Infra.NHibernate;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// The migration job (ARV-006): applies the versioned scripts with the migration login, sets the retention periods of this
/// environment (ARV-111a, <see cref="RetentionPolicies"/>: the hypertables' TimescaleDB policies and the relational periods
/// the Cronz retention job reads), then creates or updates the hosts' logins (ARV-083a): each host's own (Database:HostLogins)
/// and the availability ledger's (Database:Availability), each a member of ariva_runtime and its one host role (script 0074), or
/// on a vm-local database without them the login the hosts share (Database:Username, every host role), each from a
/// SCRAM-SHA-256 verifier computed here so no password reaches the server, and each with the server's limit on one of its
/// statements (ARV-096d, script 0063), on a session's temporary files (ARV-290, script 0069; the job stops before the scripts
/// when the migration login may not set that one) and on a session idle in a transaction or whose client is gone (ARV-292,
/// script 0070); and (ARV-104g1) the validation
/// reader login, the only login that reads the shadow nowcast. On a developer machine (Database:AllowSchemaUpdate) NHibernate
/// SchemaUpdate then adds whatever the scripts do not cover yet, also with the migration login. ARV-180: the job records its
/// environment in the database after the scripts (<see cref="DatabaseEnvironment"/>), and a vm-local migration refuses, before
/// anything changes, a database that records another environment.
/// </summary>
internal sealed class DatabaseMigrator(
    DatabaseSettings settings,
    ValidationReaderSettings reader,
    NHibernateSessionFactoryProvider factoryProvider,
    ILoggerFactory loggerFactory,
    DatabaseEnvironment environment,
    HostLoginSettings hostLogins = null,
    AvailabilityLoginSettings availability = null)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DatabaseMigrator>();

    private readonly HostLoginSettings _hostLogins = hostLogins ?? new HostLoginSettings();

    private readonly AvailabilityLoginSettings _availability = availability ?? new AvailabilityLoginSettings();

    /// <summary>The job ensures a login: the hosts' own, the availability ledger's, or the shared vm-local one.</summary>
    private bool EnsuresLogins => _hostLogins.IsConfigured || _availability.IsConfigured || settings.UsesSeparateRuntimeLogin;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        // ARV-104g1: a reader login that is the runtime or the migration login, or is otherwise unusable, stops the job before
        // anything changes (no secret in the message).
        reader.EnsureValid(settings);
        // ARV-111a: a retention period out of range stops the job before anything changes, too.
        settings.Retention.EnsureValid();
        // ARV-096d: and a command or statement timeout out of range.
        settings.EnsureValidLimits();
        // ARV-180: and an environment that cannot be recorded.
        environment.EnsureValid();
        // ARV-083a: and host logins that are missing in a cluster, shared, or the migration or the reader login.
        _hostLogins.EnsureValid(settings, _availability, environment.Name, reader.Username, reader.Password);
        EnsureDevelopmentLoginMayBeUsed();

        var migrationConnection = settings.BuildMigrationConnectionString();
        // ARV-180: a vm-local migration (a developer's host, the E2E suites) stops before anything changes on a database that
        // records another environment: its settings reach a deployed store.
        if (environment.IsVmLocal)
        {
            await using var connection = PostgresConnections.Create(migrationConnection);
            await connection.OpenAsync(ct);
            await environment.EnsureMayUseAsync(connection, ct);
        }

        // ARV-290: and a migration login that may not cap the runtime login's temporary files, before any script runs.
        if (EnsuresLogins)
            await EnsureMaySetTempFileLimitAsync(migrationConnection, ct);

        var runner = new SqlScriptRunner(migrationConnection, loggerFactory.CreateLogger<SqlScriptRunner>());
        var applied = await runner.ApplyAsync(SqlScriptCatalog.Embedded(), ct);
        _logger.LogInformation("Migration applied {Count} script(s)", applied.Count);
        // ARV-180: right after the scripts, so a job that fails later still leaves the record.
        await environment.RecordAsync(migrationConnection, _logger, ct);

        // ARV-111a: after the scripts, so the configured periods replace the scripts' defaults on every release.
        await RetentionPolicies.ApplyAsync(migrationConnection, settings.Retention, _logger, ct);

        var logins = await EnsureLoginsAsync(migrationConnection, ct);
        await EnsureValidationReaderLoginAsync(migrationConnection, logins, ct);

        if (settings.AllowSchemaUpdate)
            factoryProvider.UpdateSchema(migrationConnection);

        return applied;
    }

    /// <summary>
    /// ARV-290: temp_file_limit is a superuser setting, so the migration login (never a superuser) sets it on the runtime login only
    /// once a superuser has granted it SET on the parameter (the chart's init script on a new volume, <c>ariva-db.sh
    /// grant-settings</c> on a server first started before). Checked before the scripts: without it the job stops before anything
    /// changes, so the release's pre-upgrade hook fails and the hosts already running keep serving, and no host starts uncapped.
    /// </summary>
    private static async Task EnsureMaySetTempFileLimitAsync(string migrationConnection, CancellationToken ct)
    {
        await using var connection = PostgresConnections.Create(migrationConnection);
        await connection.OpenAsync(ct);
        await using var may = new NpgsqlCommand("SELECT has_parameter_privilege('temp_file_limit', 'SET') AS may, current_user::text AS login", connection);
        await using var reader = await may.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (!reader.GetBoolean(0))
            throw new InvalidOperationException(
                $"The migration login {reader.GetString(1)} may not set temp_file_limit, the runtime login's cap on temporary files (ARV-290): as the database's superuser run "
                + "GRANT SET ON PARAMETER temp_file_limit TO the migration login (ariva-db.sh grant-settings in the database pod, wiki 04), then run the migration again.");
    }

    /// <summary>
    /// ARV-083a: without host logins, the hosts of this database share Database:Username, which the job gives every host role
    /// (ariva_ensure_development_login). Only a vm-local process or one without an environment (the integration tests) may,
    /// and the database refuses it too once it records another environment; the password follows the host logins' rules, since
    /// the job derives its verifier from it.
    /// </summary>
    private void EnsureDevelopmentLoginMayBeUsed()
    {
        if (_hostLogins.IsConfigured || !settings.UsesSeparateRuntimeLogin)
            return;
        if (environment.Name is not null && !environment.IsVmLocal)
            throw new InvalidOperationException($"Database:HostLogins is not configured: outside vm-local each host has a login of its own (ARV-083a), and this process runs as '{environment.Name}'.");
        var problems = new DatabaseLoginSettings { Username = settings.Username, Password = settings.Password }.Problems("Database");
        if (problems.Count > 0)
            throw new InvalidOperationException($"The hosts' shared development login is misconfigured (ARV-083a): {string.Join(" ", problems)}");
    }

    /// <summary>
    /// ARV-083a: creates or updates every login the hosts use, each from a SCRAM-SHA-256 verifier computed here (so its password
    /// never reaches the server, CWE-532), then sets the runtime limits on it, since a setting of a group role does not reach its
    /// members. Returns the logins it ensured.
    /// </summary>
    private async Task<IReadOnlyList<string>> EnsureLoginsAsync(string migrationConnection, CancellationToken ct)
    {
        var ensured = new List<string>();
        if (!EnsuresLogins)
        {
            _logger.LogWarning("Database:Username equals the migration login; hosts will connect with DDL rights. Configure a separate runtime login.");
            return ensured;
        }

        await using var connection = PostgresConnections.Create(migrationConnection);
        await connection.OpenAsync(ct);
        if (_hostLogins.IsConfigured)
        {
            foreach (var host in _hostLogins.All)
            {
                await EnsureHostLoginAsync(connection, host.Login, host.Role, ct);
                ensured.Add(host.Login.Username);
            }

            if (settings.UsesSeparateRuntimeLogin && !_hostLogins.All.Any(h => h.Login.Names(settings.Username)))
                _logger.LogWarning("Database:Username {Login} is none of the host logins: the job leaves it as it is. Revoke or drop a runtime login the hosts no longer use (wiki 04 section 6.2)",
                    settings.Username);
        }
        else if (settings.UsesSeparateRuntimeLogin)
        {
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_development_login(@name, @verifier)", connection);
            ensure.Parameters.AddWithValue("name", settings.Username);
            ensure.Parameters.AddWithValue("verifier", ScramVerifier.For(settings.Password));
            await ensure.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Development login {Login} is a member of ariva_runtime and every host role (vm-local: no Database:HostLogins)", settings.Username);
            await ApplyLimitsAsync(connection, settings.Username, ct);
            ensured.Add(settings.Username);
        }

        if (_availability.IsConfigured)
        {
            await EnsureHostLoginAsync(connection, _availability.Login, HostDatabaseAccess.AvailabilityRole, ct);
            ensured.Add(_availability.Username);
        }

        return ensured;
    }

    private async Task EnsureHostLoginAsync(NpgsqlConnection connection, DatabaseLoginSettings login, string role, CancellationToken ct)
    {
        await using (var ensure = new NpgsqlCommand("SELECT ariva_ensure_host_login(@name, @verifier, @role)", connection))
        {
            ensure.Parameters.AddWithValue("name", login.Username);
            ensure.Parameters.AddWithValue("verifier", ScramVerifier.For(login.Password));
            ensure.Parameters.AddWithValue("role", role);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Host login {Login} is a member of ariva_runtime and {Role} only", login.Username, role);
        await ApplyLimitsAsync(connection, login.Username, ct);
    }

    /// <summary>The runtime limits of scripts 0063, 0069 and 0070, on the login itself; new sessions take them.</summary>
    private async Task ApplyLimitsAsync(NpgsqlConnection connection, string login, CancellationToken ct)
    {
        // ARV-096d: on the login itself, since a setting of the ariva_runtime role would not reach it.
        await using var timeout = new NpgsqlCommand("SELECT ariva_set_runtime_statement_timeout(@name, @seconds)", connection);
        timeout.Parameters.AddWithValue("name", login);
        timeout.Parameters.AddWithValue("seconds", settings.StatementTimeoutSeconds);
        await timeout.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Runtime login {Login}: its statements limited to {Seconds} s on the server", login, settings.StatementTimeoutSeconds);

        // ARV-290: the job checked before the scripts that the migration login may set temp_file_limit (EnsureMaySetTempFileLimitAsync).
        await using var tempFiles = new NpgsqlCommand("SELECT ariva_set_runtime_temp_file_limit(@name, @megabytes)", connection);
        tempFiles.Parameters.AddWithValue("name", login);
        tempFiles.Parameters.AddWithValue("megabytes", settings.TempFileLimitMegabytes);
        await tempFiles.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Runtime login {Login}: each of its server processes holds at most {Megabytes} MB of temporary files on the server", login, settings.TempFileLimitMegabytes);

        // ARV-292: a session idle in a transaction past the limit is ended with its locks, and one whose client is gone sooner.
        await using var session = new NpgsqlCommand("SELECT ariva_set_runtime_session_limits(@name, @idle, @keepIdle, @keepInterval, @keepCount)", connection);
        session.Parameters.AddWithValue("name", login);
        session.Parameters.AddWithValue("idle", settings.IdleInTransactionTimeoutSeconds);
        session.Parameters.AddWithValue("keepIdle", DatabaseSettings.KeepaliveIdleSeconds);
        session.Parameters.AddWithValue("keepInterval", DatabaseSettings.KeepaliveIntervalSeconds);
        session.Parameters.AddWithValue("keepCount", DatabaseSettings.KeepaliveCount);
        await session.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Runtime login {Login}: a session idle in a transaction for {Seconds} s is ended, and one whose client answers no TCP keepalive",
            login, settings.IdleInTransactionTimeoutSeconds);
    }

    /// <summary>
    /// ARV-104g1: creates or updates the validation reader login (script 0049: a member of ariva_validation_reader and nothing
    /// else, CONNECT on this database) from a SCRAM-SHA-256 verifier computed here, so its password never reaches the server,
    /// then stops the job if a login of the hosts can read a value of the shadow nowcast (<see cref="ShadowReadAccess"/>): each
    /// login it ensured (ARV-083a) and Database:Username, so an upgrade that would let a host read it fails before any pod changes
    /// (the hosts check it again at start-up).
    /// </summary>
    private async Task EnsureValidationReaderLoginAsync(string migrationConnection, IReadOnlyList<string> logins, CancellationToken ct)
    {
        await using var connection = PostgresConnections.Create(migrationConnection);
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

        var hostLogins = settings.UsesSeparateRuntimeLogin ? logins.Append(settings.Username) : logins;
        foreach (var login in hostLogins.Distinct(StringComparer.Ordinal))
        {
            if (await ShadowReadAccess.CanReadAsync(connection, login, ct))
            {
                throw new InvalidOperationException(
                    $"The hosts' login {login} can read the shadow nowcast: it holds ariva_validation_reader or pg_read_all_data (directly, " +
                    "through another role, or as a superuser), or may select a value column of the shadow table or one of its chunks. Remove that access " +
                    "and run the migration again (CWE-269, ARV-104g1).");
            }
        }
    }
}
