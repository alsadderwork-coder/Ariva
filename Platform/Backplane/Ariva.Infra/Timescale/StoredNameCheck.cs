using System.Globalization;
using System.Text;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>One stored name the name rule refuses: where it is, and the name with every character outside printable ASCII escaped.</summary>
/// <param name="Place">The table and column, for example <c>zone.name</c> or <c>device.queue_zone_name</c>.</param>
/// <param name="SiteCode">The row's site; none for an airport, which belongs to no site.</param>
/// <param name="Id">The row's id.</param>
/// <param name="Deleted">True for a soft-deleted row (it no longer shows on screens but stays in history).</param>
/// <param name="Escaped">The name as <see cref="StoredNameCheck.Escape"/> writes it: safe to print on any terminal.</param>
public sealed record StoredNameFinding(string Place, string SiteCode, Guid Id, bool Deleted, string Escaped);

/// <summary>What the check read and what it found.</summary>
public sealed record StoredNameCheckResult(int Checked, IReadOnlyList<StoredNameFinding> Findings);

/// <summary>
/// The pre-pilot check of ARV-159: lists the stored site, airport, terminal, level, checkpoint, desk, zone profile, zone and
/// line names and device queue zone names that the name rule now refuses (<see cref="TopologyCodes.IsName"/>: control
/// characters, invisible format characters of any plane, broken characters, judged per code point), so they can be renamed
/// before the pilot. Such a name was accepted until ARV-159; it is kept, but an edit of its record is refused until it is
/// renamed, and the archive and Ingest skip sensing data that carries it as a queue zone name.
/// <para>
/// It only reads. It runs on the hosts' runtime login (Database:Username), which reads these tables already; the connection
/// asks PostgreSQL for read-only transactions (<c>default_transaction_read_only</c>) and the one statement runs in a
/// <c>READ ONLY</c> transaction that is rolled back, so any write would fail (SQLSTATE 25006). The statement is a constant;
/// nothing is built from data (CWE-89).
/// </para>
/// </summary>
public sealed class StoredNameCheck(DatabaseSettings database)
{
    #region Constants

    /// <summary>Every stored name the rule covers, one row per name. Soft-deleted rows are read too, and marked.</summary>
    internal const string Sql = """
        SELECT 'site.name' AS place, code AS site_code, id, name AS value, false AS deleted FROM site
        UNION ALL SELECT 'airport.name', NULL, id, name, deleted_on IS NOT NULL FROM airport
        UNION ALL SELECT 'terminal.name', site_code, id, name, deleted_on IS NOT NULL FROM terminal
        UNION ALL SELECT 'level.name', site_code, id, name, deleted_on IS NOT NULL FROM level
        UNION ALL SELECT 'checkpoint.name', site_code, id, name, deleted_on IS NOT NULL FROM checkpoint
        UNION ALL SELECT 'desk.name', site_code, id, name, deleted_on IS NOT NULL FROM desk
        UNION ALL SELECT 'zone_profile.name', site_code, id, name, false FROM zone_profile
        UNION ALL SELECT 'zone.name', p.site_code, z.id, z.name, false FROM zone z JOIN zone_profile p ON p.id = z.profile_id
        UNION ALL SELECT 'line.name', p.site_code, l.id, l.name, false FROM line l JOIN zone_profile p ON p.id = l.profile_id
        UNION ALL SELECT 'device.queue_zone_name', site_code, id, queue_zone_name, deleted_on IS NOT NULL FROM device
        ORDER BY 1, 2, 3
        """;

    #endregion

    #region Methods

    /// <summary>Reads every covered name once and returns those the name rule refuses, in place, site and id order.</summary>
    public async Task<StoredNameCheckResult> RunAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(ReadOnlyConnectionString(database));
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);

        var findings = new List<StoredNameFinding>();
        var read = 0;
        await using (var command = new NpgsqlCommand(Sql, connection, transaction) { CommandTimeout = 120 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                read++;
                var value = reader.GetString(3);
                if (TopologyCodes.IsName(value))
                    continue;
                findings.Add(new StoredNameFinding(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetGuid(2), reader.GetBoolean(4),
                    Escape(value)));
            }
        }

        await transaction.RollbackAsync(ct);
        return new StoredNameCheckResult(read, findings);
    }

    /// <summary>
    /// The runtime login's connection with every transaction read-only by default (PostgreSQL's
    /// <c>default_transaction_read_only</c>), under its own application name.
    /// </summary>
    internal static string ReadOnlyConnectionString(DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new NpgsqlConnectionStringBuilder(database.BuildConnectionString())
        {
            ApplicationName = "ariva-stored-name-check",
            Options = "-c default_transaction_read_only=on"
        }.ConnectionString;
    }

    /// <summary>
    /// The text with printable ASCII kept (a backslash doubled) and every other code point written as <c>\uXXXX</c> or
    /// <c>\UXXXXXXXX</c>, a lone surrogate as its own <c>\uXXXX</c>: what an operator reads is what is stored, and no
    /// override or invisible character reaches the terminal.
    /// </summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= ' ' and <= '~')
            {
                builder.Append(c == '\\' ? "\\\\" : c.ToString());
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\U{char.ConvertToUtf32(c, text[i + 1]):X8}");
                i++;
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
        }

        return builder.ToString();
    }

    #endregion
}
