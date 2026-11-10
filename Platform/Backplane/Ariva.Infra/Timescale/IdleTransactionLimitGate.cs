using System.Globalization;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// ARV-294 (CWE-400, the ARV-292 review's recommendation 8): every host, at start-up, whenever it already reaches the database then (it
/// verifies the schema outside vm-local, or migrates it in vm-local, and starts after that), reads the idle transaction limit the server
/// applies to its runtime login's sessions (<c>SHOW idle_in_transaction_session_timeout</c> on a session of that login) and refuses to
/// start when it is not above the longest a transaction of this host may wait on other work (<see cref="IdleTransactionBudget"/>). The
/// host's own setting is checked at composition; this catches a limit the migration job set from another copy of the settings, or one
/// changed on the server since. A limit of 0 (none) ends no transaction, so the host starts; when a separate runtime login is configured
/// (the migration job sets a limit on it) it says so in a warning: the job has not run since ARV-292, or the limit was taken off.
/// </summary>
internal sealed class IdleTransactionLimitGate(DatabaseSettings database, IdleTransactionBudget budget, ILogger<IdleTransactionLimitGate> logger) : IHostedService
{
    #region Constants

    /// <summary>The most digits <see cref="Milliseconds"/> reads: 9,999,999,999 days is about 8.6e17 ms, within a long (9.2e18).</summary>
    internal const int MaxDigits = 10;

    #endregion

    #region IHostedService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!database.VerifySchemaOnStartup && !database.AllowSchemaUpdate)
            return;

        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var show = new NpgsqlCommand("SHOW idle_in_transaction_session_timeout", connection);
        var shown = Convert.ToString(await show.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var problem = Problem(shown, budget, database.IdleInTransactionTimeoutSeconds);
        if (problem is not null)
            throw new InvalidOperationException(problem);
        if (Milliseconds(shown) == 0 && database.UsesSeparateRuntimeLogin)
            logger.LogWarning("The runtime login has no idle transaction limit on the server: run the migration job, which sets Database:IdleInTransactionTimeoutSeconds ({Seconds} s) on it (ARV-292)",
                database.IdleInTransactionTimeoutSeconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    #endregion

    #region Methods

    /// <summary>
    /// The limit SHOW gives, in milliseconds: a whole number and one of PostgreSQL's units (ms, s, min, h, d), milliseconds when it has
    /// none; 0 for no limit. Null when it is not one. At most ten digits are read (the setting's largest value, 2,147,483,647 ms, has
    /// ten), so even ten digits of days (under 8.7e17 ms) fit a long.
    /// </summary>
    internal static long? Milliseconds(string shown)
    {
        if (string.IsNullOrEmpty(shown))
            return null;
        var digits = 0;
        while (digits < shown.Length && char.IsAsciiDigit(shown[digits]))
            digits++;
        if (digits is 0 or > MaxDigits)
            return null;
        var value = long.Parse(shown.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture);
        long? unit = shown[digits..] switch
        {
            "" or "ms" => 1,
            "s" => 1_000,
            "min" => 60_000,
            "h" => 3_600_000,
            "d" => 86_400_000,
            _ => null
        };
        return unit is { } factor ? value * factor : null;
    }

    /// <summary>
    /// Why a host whose waits are <paramref name="budget"/>'s may not start under the limit the server shows (<paramref name="shown"/>),
    /// or null: a limit it cannot read, or one that is not above the longest wait. No limit (0) ends no transaction.
    /// </summary>
    internal static string Problem(string shown, IdleTransactionBudget budget, int configuredSeconds)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var milliseconds = Milliseconds(shown);
        if (milliseconds is null)
            return "The runtime login's idle_in_transaction_session_timeout on the server could not be read as a time (ARV-294); the host does not start without knowing it.";
        var longest = budget.Longest;
        if (milliseconds.Value == 0 || milliseconds.Value > longest.Seconds * 1_000L)
            return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"The runtime login's idle_in_transaction_session_timeout on the server ({milliseconds.Value / 1_000d:0.###} s) must be above the longest a transaction of this host may wait on other work: {longest.What}, {longest.Seconds:N0} s (ARV-294). Run the migration job with Database:IdleInTransactionTimeoutSeconds ({configuredSeconds:N0} s), or set the login's limit above that wait.");
    }

    #endregion
}
