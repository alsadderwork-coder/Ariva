using System.Diagnostics.CodeAnalysis;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Retention;

/// <summary>What one run did to one data set: its period (null when none is set), the rows deleted, the batches, whether nothing older is left, and whether it failed.</summary>
public sealed record RetentionDataSetRun(string DataSet, int? Days, long Deleted, int Batches, bool Finished, bool Failed);

/// <summary>What one run did: nothing when another replica held the job, otherwise one entry per data set.</summary>
public sealed record RetentionRun(bool HeldElsewhere, IReadOnlyList<RetentionDataSetRun> DataSets)
{
    public long Deleted => DataSets.Sum(d => d.Deleted);
}

/// <summary>
/// The relational retention job (ARV-111a), run by Ariva.Api.Cronz once a day: for each data set of
/// <see cref="RetentionSettings.DataSetNames"/> (alert emails, Integration API call records, report deliveries) it deletes the
/// rows older than the data set's period, in batches of <see cref="RetentionJobSettings.BatchSize"/> rows, each batch one call
/// of <c>ariva_retention_purge</c> (script 0054) in its own transaction, until a batch comes back short or
/// <see cref="RetentionJobSettings.MaxBatchesPerRun"/> batches ran (the rest waits for the next run). One replica at a time: the
/// run holds a session advisory lock (class <see cref="LockClass"/>) and a replica that cannot take it skips the run.
/// <para>
/// The job connects with the hosts' runtime login, which may not delete these rows itself (0023, 0025, 0036). The database
/// function decides what goes: it reads the period the migration job wrote (the runtime login cannot write it), so this class
/// passes only the data set's name and the batch size, as bind parameters of constant statements (CWE-89, CWE-269). Logs name
/// the data set, the period and counts, never a row's values (no address, route or recipient).
/// </para>
/// </summary>
public sealed class RetentionRound(DatabaseSettings database, RetentionJobSettings settings, ILogger<RetentionRound> logger)
{
    #region Constants

    /// <summary>The advisory lock class of a run (two-key form; 25, 26, 36, 37, 38, 41, 42, 49, 50 and 51 are taken).</summary>
    public const int LockClass = 52;

    private const string TryLock = "SELECT pg_try_advisory_lock(52, 0)";

    private const string Unlock = "SELECT pg_advisory_unlock(52, 0)";

    private const string Periods = "SELECT data_set, days FROM retention_period";

    private const string Purge = "SELECT ariva_retention_purge(@set, @limit)";

    #endregion

    #region Methods

    /// <summary>One run over the data sets; <see cref="RetentionRun.HeldElsewhere"/> when another replica is running it.</summary>
    public async Task<RetentionRun> RunAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        if (!await ScalarAsync<bool>(connection, TryLock, ct))
            return new RetentionRun(true, []);

        try
        {
            var periods = new Dictionary<string, int>(StringComparer.Ordinal);
            await using (var command = Command(connection, Periods))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    periods[reader.GetString(0)] = reader.GetInt32(1);
            }

            var runs = new List<RetentionDataSetRun>();
            foreach (var dataSet in RetentionSettings.DataSetNames)
                runs.Add(await DataSetAsync(connection, dataSet, periods.TryGetValue(dataSet, out var days) ? days : null, ct));
            return new RetentionRun(false, runs);
        }
        finally
        {
            await UnlockAsync(connection);
        }
    }

    /// <summary>
    /// Releases the run's lock without hiding what the run threw: a failure here is logged (no row content) and dropped, and the
    /// lock goes with the session anyway (a broken connection ends it; a pooled one is reset before its next use).
    /// </summary>
    private async Task UnlockAsync(NpgsqlConnection connection)
    {
        if (connection.FullState != System.Data.ConnectionState.Open)
            return;
        try
        {
            await ScalarAsync<bool>(connection, Unlock, CancellationToken.None);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(e, "Retention: the run's advisory lock could not be released; it is released with the connection's session");
        }
    }

    private async Task<RetentionDataSetRun> DataSetAsync(NpgsqlConnection connection, string dataSet, int? days, CancellationToken ct)
    {
        if (days is null)
        {
            logger.LogWarning("Retention of {DataSet} skipped: no period is set; the migration job sets it (Database:Retention)", dataSet);
            return new RetentionDataSetRun(dataSet, null, 0, 0, false, false);
        }

        long deleted = 0;
        var batches = 0;
        try
        {
            while (batches < settings.MaxBatchesPerRun)
            {
                var rows = await ScalarAsync<int>(connection, Purge, ct, ("set", dataSet), ("limit", settings.BatchSize));
                batches++;
                deleted += rows;
                if (rows < settings.BatchSize)
                {
                    Report(dataSet, days.Value, deleted, batches, finished: true);
                    return new RetentionDataSetRun(dataSet, days, deleted, batches, true, false);
                }
            }

            Report(dataSet, days.Value, deleted, batches, finished: false);
            return new RetentionDataSetRun(dataSet, days, deleted, batches, false, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // one data set that fails is logged and tried again by the next run; the others go on
        catch (Exception e)
#pragma warning restore CA1031
        {
            logger.LogError(e, "Retention of {DataSet} failed after {Rows} rows in {Batches} batches; the next run tries again", dataSet, deleted, batches);
            return new RetentionDataSetRun(dataSet, days, deleted, batches, false, true);
        }
    }

    private void Report(string dataSet, int days, long deleted, int batches, bool finished)
    {
        if (!finished)
        {
            logger.LogWarning(
                "Retention of {DataSet}: {Rows} rows older than {Days} days deleted in {Batches} batches, the most one run takes; the next run deletes the rest",
                dataSet, deleted, days, batches);
        }
        else if (deleted > 0)
        {
            logger.LogInformation("Retention of {DataSet}: {Rows} rows older than {Days} days deleted in {Batches} batches", dataSet, deleted, days, batches);
        }
        else
        {
            logger.LogDebug("Retention of {DataSet}: nothing older than {Days} days", dataSet, days);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, [ConstantExpected] string sql, params (string Name, object Value)[] parameters)
    {
#pragma warning disable CA2100 // every caller passes one of the constants above (ConstantExpected)
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, [ConstantExpected] string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    #endregion
}
