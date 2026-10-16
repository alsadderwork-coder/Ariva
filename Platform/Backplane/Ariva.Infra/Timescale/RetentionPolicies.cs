using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>What the migration job found and did for one retention period: the table or data set, the period, whether it changed and the value before.</summary>
public sealed record RetentionChange(string Name, int Days, bool Changed, string Previous);

/// <summary>
/// The migration job's retention step (ARV-111a), run with the migration login after the versioned scripts, in one transaction
/// under the script runner's advisory lock (so two jobs never interleave):
/// <list type="number">
/// <item>each hypertable of <see cref="RetentionSettings.Hypertables"/> gets a TimescaleDB retention policy of exactly its period.
/// A policy that already drops after that period is left alone (a second run changes nothing, and the policy's job keeps its
/// id and schedule); any other policy is removed and added again with the period, in the same transaction, so there is never
/// a moment without one. The scripts that created the tables (0017, 0019, 0040, 0053) set the same defaults and stay unchanged:
/// they are immutable, and this step runs after them on every release, so the configured value always wins;</item>
/// <item>each relational period of <see cref="RetentionSettings.DataSets"/> is written to <c>retention_period</c> (script 0054),
/// which the Cronz retention job's database function reads; the runtime role can read that table but never write it.</item>
/// </list>
/// Only the hypertables' owner (the migration login) can change a policy: TimescaleDB refuses every other login (42501), so the
/// runtime role stays without DDL (CWE-269). Every statement is constant and every value a bind parameter (CWE-89); table names
/// come from <see cref="RetentionSettings"/>, never from configuration.
/// </summary>
internal static class RetentionPolicies
{
    #region Constants

    /// <summary>The script runner's advisory lock key (<see cref="SqlScriptRunner"/>): one migration step at a time per database.</summary>
    internal const long LockKey = 7_262_002;

    private const string Lock = "SELECT pg_advisory_xact_lock(@key)";

    private const string TimescaleInstalled = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_extension WHERE extname = 'timescaledb')";

    private const string PlainPostgresAllowed = "SELECT coalesce(current_setting('ariva.allow_plain_postgres', true), '') = 'on'";

    private const string IsHypertable = """
        SELECT EXISTS (SELECT 1 FROM timescaledb_information.hypertables h WHERE h.hypertable_schema = 'public' AND h.hypertable_name = @table)
        """;

    /// <summary>The policy's period as TimescaleDB stores it, and whether it is the configured one (null when there is no policy).</summary>
    private const string CurrentPolicy = """
        SELECT j.config ->> 'drop_after', (j.config ->> 'drop_after')::interval = make_interval(days => @days)
          FROM timescaledb_information.jobs j
         WHERE j.proc_name = 'policy_retention' AND j.hypertable_schema = 'public' AND j.hypertable_name = @table
        """;

    private const string RemovePolicy = "SELECT remove_retention_policy(@relation::regclass, if_exists => true)";

    private const string AddPolicy = "SELECT add_retention_policy(@relation::regclass, drop_after => make_interval(days => @days))";

    private const string CurrentPeriod = "SELECT days FROM retention_period WHERE data_set = @set";

    private const string SetPeriod = """
        INSERT INTO retention_period (data_set, days, set_on, set_by) VALUES (@set, @days, now(), left(session_user, 100))
        ON CONFLICT (data_set) DO UPDATE SET days = EXCLUDED.days, set_on = EXCLUDED.set_on, set_by = EXCLUDED.set_by
        """;

    #endregion

    #region Methods

    /// <summary>Applies every period and returns what it found per table and data set, hypertables first.</summary>
    internal static async Task<IReadOnlyList<RetentionChange>> ApplyAsync(string migrationConnection, RetentionSettings retention, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(logger);
        retention.EnsureValid();

        var changes = new List<RetentionChange>();
        await using var connection = new NpgsqlConnection(migrationConnection);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ScalarAsync<object>(connection, transaction, Lock, ct, ("key", LockKey));

        if (await ScalarAsync<bool>(connection, transaction, TimescaleInstalled, ct))
        {
            foreach (var hypertable in retention.Hypertables())
                changes.Add(await HypertableAsync(connection, transaction, hypertable, ct));
        }
        else if (await ScalarAsync<bool>(connection, transaction, PlainPostgresAllowed, ct))
        {
            // As the scripts: local tests on plain PostgreSQL only. Nothing is dropped there, and this says so.
            logger.LogWarning("TimescaleDB is not installed (ariva.allow_plain_postgres): no hypertable retention policy is set, the raw tables keep every row");
        }
        else
        {
            throw new InvalidOperationException("TimescaleDB is required: the hypertables' retention policies cannot be set (ARV-111a).");
        }

        foreach (var period in retention.DataSets())
            changes.Add(await PeriodAsync(connection, transaction, period, ct));

        await transaction.CommitAsync(ct);

        foreach (var change in changes)
        {
            if (change.Changed)
                logger.LogInformation("Retention of {Name} set to {Days} days (was {Previous})", change.Name, change.Days, change.Previous ?? "not set");
            else
                logger.LogInformation("Retention of {Name} stays {Days} days", change.Name, change.Days);
        }

        return changes;
    }

    private static async Task<RetentionChange> HypertableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, HypertableRetentionPeriod period, CancellationToken ct)
    {
        if (!await ScalarAsync<bool>(connection, transaction, IsHypertable, ct, ("table", period.Table)))
            throw new InvalidOperationException($"{period.Table} is not a hypertable: its retention policy cannot be set (ARV-111a). Check the applied scripts.");

        string previous = null;
        var same = false;
        await using (var command = Command(connection, transaction, CurrentPolicy, ("table", period.Table), ("days", period.Days)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                previous = reader.IsDBNull(0) ? null : reader.GetString(0);
                same = !reader.IsDBNull(1) && reader.GetBoolean(1);
            }
        }

        if (same)
            return new RetentionChange(period.Table, period.Days, false, previous);

        var relation = "public." + period.Table;
        await ScalarAsync<object>(connection, transaction, RemovePolicy, ct, ("relation", relation));
        await ScalarAsync<object>(connection, transaction, AddPolicy, ct, ("relation", relation), ("days", period.Days));
        return new RetentionChange(period.Table, period.Days, true, previous);
    }

    private static async Task<RetentionChange> PeriodAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, RelationalRetentionPeriod period, CancellationToken ct)
    {
        var current = await ScalarAsync<int?>(connection, transaction, CurrentPeriod, ct, ("set", period.DataSet));
        if (current == period.Days)
            return new RetentionChange(period.DataSet, period.Days, false, null);

        await ScalarAsync<object>(connection, transaction, SetPeriod, ct, ("set", period.DataSet), ("days", period.Days));
        return new RetentionChange(period.DataSet, period.Days, true, current is { } days ? days.ToString(System.Globalization.CultureInfo.InvariantCulture) + " days" : null);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql,
        params (string Name, object Value)[] parameters)
    {
#pragma warning disable CA2100 // every caller passes one of the constants above (ConstantExpected)
        var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql,
        CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    #endregion
}
