using System.Diagnostics;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>A script already applied to the database differs from the shipped one, or the database is ahead of it.</summary>
public sealed class SchemaDriftException(string message) : InvalidOperationException(message);

/// <summary>The database lacks scripts this build ships; the migration job has not run.</summary>
public sealed class SchemaBehindException(string message) : InvalidOperationException(message);

/// <summary>
/// Applies the versioned scripts (ARV-006). One runner at a time (PostgreSQL advisory lock), scripts in ascending
/// order, each in its own transaction together with its <c>schema_version</c> row, so a failed script leaves no
/// trace and the next run retries it. Before applying anything the runner compares every recorded checksum with the
/// shipped script and stops on any difference: shipped scripts are immutable.
/// All SQL here is constant; script text is executed as written and never combined with input.
/// </summary>
public sealed class SqlScriptRunner(string connectionString, ILogger<SqlScriptRunner> logger)
{
    /// <summary>Shared by every runner of this database; only uniqueness within the database matters.</summary>
    private const long LockKey = 7_262_002;

    private const string CreateVersionTable = """
        CREATE TABLE IF NOT EXISTS schema_version (
            script_name  text        PRIMARY KEY,
            checksum     char(64)    NOT NULL,
            applied_on   timestamptz NOT NULL DEFAULT now(),
            applied_by   text        NOT NULL DEFAULT current_user,
            execution_ms integer     NOT NULL
        )
        """;

    public sealed record AppliedScript(string Name, string Checksum);

    /// <summary>Applies pending scripts and returns their names. Throws <see cref="SchemaDriftException"/> on drift.</summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<SqlScript> scripts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await AdvisoryLockAsync(connection, acquire: true, ct);
        try
        {
            await using (var create = new NpgsqlCommand(CreateVersionTable, connection))
                await create.ExecuteNonQueryAsync(ct);
            var applied = await ReadAppliedAsync(connection, ct);
            ThrowIfDrifted(scripts, applied);

            var appliedNames = applied.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var done = new List<string>();
            foreach (var script in scripts.Where(s => !appliedNames.Contains(s.Name)))
            {
                await ApplyOneAsync(connection, script, ct);
                done.Add(script.Name);
            }

            return done;
        }
        finally
        {
            await AdvisoryLockAsync(connection, acquire: false, CancellationToken.None);
        }
    }

    /// <summary>
    /// Read-only check for host startup: throws <see cref="SchemaDriftException"/> on drift and
    /// <see cref="SchemaBehindException"/> when scripts are pending.
    /// </summary>
    public async Task VerifyAsync(IReadOnlyList<SqlScript> scripts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var applied = await ReadAppliedAsync(connection, ct, tableMayBeMissing: true);
        ThrowIfDrifted(scripts, applied);

        var pending = scripts.Select(s => s.Name).Except(applied.Select(a => a.Name), StringComparer.Ordinal).ToList();
        if (pending.Count > 0)
            throw new SchemaBehindException($"The database is missing {pending.Count} script(s): {string.Join(", ", pending)}. Run the migration job (--migrate).");
    }

    public async Task<IReadOnlyList<AppliedScript>> ReadAppliedAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return await ReadAppliedAsync(connection, ct, tableMayBeMissing: true);
    }

    private async Task ApplyOneAsync(NpgsqlConnection connection, SqlScript script, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        logger.LogInformation("Applying {Script}", script.Name);

        if (script.RunInTransaction)
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await ExecuteScriptAsync(connection, script, transaction, ct);
            await RecordAsync(connection, script, watch, transaction, ct);
            await transaction.CommitAsync(ct);
        }
        else
        {
            await ExecuteScriptAsync(connection, script, null, ct);
            await RecordAsync(connection, script, watch, null, ct);
        }

        logger.LogInformation("Applied {Script} in {ElapsedMs} ms", script.Name, watch.ElapsedMilliseconds);
    }

    private static async Task ExecuteScriptAsync(NpgsqlConnection connection, SqlScript script, NpgsqlTransaction transaction, CancellationToken ct)
    {
        // A versioned script is trusted code from this repository (reviewed, checksummed, embedded at build time);
        // it is never built from input, which is what CA2100 guards against.
#pragma warning disable CA2100
        await using var command = new NpgsqlCommand(script.Sql, connection, transaction) { CommandTimeout = 0 };
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordAsync(NpgsqlConnection connection, SqlScript script, Stopwatch watch, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO schema_version (script_name, checksum, execution_ms) VALUES (@name, @checksum, @ms)", connection, transaction);
        command.Parameters.AddWithValue("name", script.Name);
        command.Parameters.AddWithValue("checksum", script.Checksum);
        command.Parameters.AddWithValue("ms", (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<AppliedScript>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken ct, bool tableMayBeMissing = false)
    {
        if (tableMayBeMissing)
        {
            await using var exists = new NpgsqlCommand("SELECT to_regclass('schema_version') IS NOT NULL", connection);
            if (await exists.ExecuteScalarAsync(ct) is not true)
                return [];
        }

        var applied = new List<AppliedScript>();
        await using var command = new NpgsqlCommand("SELECT script_name, checksum FROM schema_version ORDER BY script_name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            applied.Add(new AppliedScript(reader.GetString(0), reader.GetString(1)));
        return applied;
    }

    private static void ThrowIfDrifted(IReadOnlyList<SqlScript> scripts, IReadOnlyList<AppliedScript> applied)
    {
        var shipped = scripts.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var record in applied)
        {
            if (!shipped.TryGetValue(record.Name, out var script))
                problems.Add($"{record.Name} is applied but not shipped in this build (database is ahead, or a script was renamed)");
            else if (!string.Equals(script.Checksum, record.Checksum.Trim(), StringComparison.Ordinal))
                problems.Add($"{record.Name} was changed after it was applied (recorded {record.Checksum.Trim()[..12]}, shipped {script.Checksum[..12]})");
        }

        if (problems.Count > 0)
            throw new SchemaDriftException("Schema drift: " + string.Join("; ", problems) + ". Shipped scripts are immutable; add a new script instead.");
    }

    private static async Task AdvisoryLockAsync(NpgsqlConnection connection, bool acquire, CancellationToken ct)
    {
        await using var command = acquire
            ? new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection)
            : new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        command.Parameters.AddWithValue("key", LockKey);
        await command.ExecuteNonQueryAsync(ct);
    }
}
