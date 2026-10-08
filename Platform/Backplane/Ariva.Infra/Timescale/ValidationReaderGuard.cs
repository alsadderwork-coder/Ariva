using Ariva.Infra.Settings;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// Every host, at start-up (ARV-104g1, CWE-269, CWE-863): the shadow nowcast is read only through the validation reader login.
/// A host refuses to start:
/// <list type="bullet">
/// <item>when that login is misconfigured next to its own (the same name as the runtime or the migration login, an invalid
/// name, an Ariva role, a short, non-ASCII or reused password: <see cref="ValidationReaderSettings.Problems"/>);</item>
/// <item>when Database:ValidationReader is set for a host that does not run the validation service (only Ariva.Api.Main
/// registers <see cref="ValidationReaderHost"/>), outside vm-local, where a developer's git-ignored settings file reaches every
/// host;</item>
/// <item>whenever it already reaches the database at start-up (it verifies the schema outside vm-local, or migrates it in
/// vm-local, and starts after that), when its runtime login can read a value of the shadow nowcast
/// (<see cref="ShadowReadAccess"/>: a member of ariva_validation_reader or pg_read_all_data, a superuser, or SELECT on a value
/// column of the table or a chunk through any grant, PUBLIC or ownership). A host started without a database still starts.</item>
/// </list>
/// Only names are compared; no password is read, logged or put in a message.
/// </summary>
internal sealed class ValidationReaderGuard(
    DatabaseSettings database,
    ValidationReaderSettings reader,
    IHostEnvironment environment = null,
    ValidationReaderHost validationHost = null) : IHostedService
{
    #region Constants

    /// <summary>The developer machine's environment (ArivaEnvironment.VmLocal in Ariva.Api.Common).</summary>
    internal const string VmLocal = "vm-local";

    #endregion

    #region IHostedService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        reader.EnsureValid(database);
        if (reader.IsConfigured && validationHost is null && environment is not null && !string.Equals(environment.EnvironmentName, VmLocal, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Database:ValidationReader is set for a host that does not run the validation service. Outside vm-local only Ariva.Api.Main " +
                "and the migration job may hold the validation reader login; remove it from this host's settings (CWE-863, ARV-104g1).");
        }

        if (!database.VerifySchemaOnStartup && !database.AllowSchemaUpdate)
            return;

        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        var login = await ShadowReadAccess.CurrentLoginAsync(connection, cancellationToken);
        if (await ShadowReadAccess.CanReadAsync(connection, login, cancellationToken))
        {
            throw new InvalidOperationException(
                "The runtime login (Database:Username) can read the shadow nowcast: it holds ariva_validation_reader or pg_read_all_data (directly, " +
                "through another role, or as a superuser), or may select a value column of the shadow table or one of its chunks (a grant to it, " +
                "to a role it holds or to PUBLIC, or ownership). Remove that access; only the validation reader login (Database:ValidationReader) " +
                "may read the shadow (CWE-269, ARV-104g1).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    #endregion
}
