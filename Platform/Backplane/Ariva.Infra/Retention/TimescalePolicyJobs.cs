using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Retention;

/// <summary>
/// TimescaleDB's own policy jobs (ARV-259c), which the scripts and the migration job set and the database runs in its background
/// workers: the hypertables' retention (dropping old chunks), their compression and the continuous aggregates' refresh. Per kind
/// with jobs, the number whose last run failed (<c>timescaledb_information.job_stats</c>), read with the hosts' runtime login in one
/// constant statement (the views list every job of the database, whoever owns it, and are readable by every login): the jobs of
/// TimescaleDB's own procedures, grouped by procedure, at most <see cref="MaxProcedures"/> rows, of which only the three policy
/// procedures are kept, each mapped to its kind here, so the <c>kind</c> tag takes these constants only. A job that has not run
/// yet counts as not failed; a paused job counts by its last run (the pause itself is not seen; nothing in Ariva pauses one).
/// Without the extension (a plain PostgreSQL that <c>ariva.allow_plain_postgres</c> lets the hosts use) there is no job and
/// nothing to read. It never reads <c>job_errors</c>, whose error text is for the job's owner.
/// </summary>
public sealed class TimescalePolicyJobs(DatabaseSettings database)
{
    #region Constants

    /// <summary>The kinds of policy job, the <c>kind</c> tag of <see cref="RetentionMetrics.PolicyJobsFailedName"/>.</summary>
    public const string Retention = "retention";

    /// <inheritdoc cref="Retention"/>
    public const string Compression = "compression";

    /// <inheritdoc cref="Retention"/>
    public const string Refresh = "refresh";

    /// <summary>
    /// The most procedures one read takes: TimescaleDB's own job procedures, a handful (its policies, telemetry and job history),
    /// and only a superuser can add one to its schemas. The statement's <c>LIMIT</c> is this number (a unit test holds them together).
    /// </summary>
    public const int MaxProcedures = 20;

    private const string Installed = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_extension WHERE extname = 'timescaledb')";

    internal const string Failed = """
        SELECT j.proc_name::text, CAST(count(s.job_id) FILTER (WHERE s.last_run_status = 'Failed') AS integer)
          FROM timescaledb_information.jobs j
          LEFT JOIN timescaledb_information.job_stats s ON s.job_id = j.job_id
         WHERE j.proc_schema IN ('_timescaledb_functions', '_timescaledb_internal')
         GROUP BY 1
         ORDER BY 1
         LIMIT 20
        """;

    /// <summary>TimescaleDB's policy procedures and their kinds; the jobs of any other procedure are not counted.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["policy_retention"] = Retention,
        ["policy_compression"] = Compression,
        ["policy_refresh_continuous_aggregate"] = Refresh
    };

    #endregion

    #region Methods

    /// <summary>Per kind with jobs, the jobs whose last run failed; empty without TimescaleDB.</summary>
    public async Task<IReadOnlyList<(string Kind, int Failed)>> FailedAsync(CancellationToken ct)
    {
        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using (var installed = new NpgsqlCommand(Installed, connection))
        {
            if (await installed.ExecuteScalarAsync(ct) is not true)
                return [];
        }

        await using var command = new NpgsqlCommand(Failed, connection);
        var kinds = new List<(string Kind, int Failed)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Kinds.TryGetValue(reader.GetString(0), out var kind))
                kinds.Add((kind, reader.GetInt32(1)));
        }

        return [.. kinds.OrderBy(k => k.Kind, StringComparer.Ordinal)];
    }

    #endregion
}
