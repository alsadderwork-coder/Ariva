using Ariva.Core.Sensing;
using Ariva.Core.Validation.Comparison;
using Ariva.Infra.Settings;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Services.Validation;

/// <summary>
/// The validation results service: one campaign's stored rows, the F18 comparison over them and the campaign verdicts
/// (ARV-104g2), served by Ariva.Api.Main to <c>Validation.View</c> holders of the campaign's site (ARV-104g). This part
/// (ARV-104g1) is the service's one read of the shadow nowcast, and the only code that opens a connection with the validation
/// reader login.
/// <para>
/// How the service gets that connection without any runtime code path using it: the login is its own settings section
/// (<see cref="ValidationReaderSettings"/>, Database:ValidationReader), never part of <see cref="DatabaseSettings"/>, from
/// which every runtime connection is built; the connection string comes only from
/// <see cref="ValidationReaderSettings.BuildReaderConnectionString"/>, called only here (source scan), with its own
/// application name and its own small pool; and in the clusters the secret reaches only api-main and the migration job (chart
/// test). The login is a member of <c>ariva_validation_reader</c> and nothing else (script 0049), so it reads this one table;
/// every other read of the service goes through the runtime login like any other service's.
/// </para>
/// <para>
/// Reads are bounded (CWE-120, CWE-400): one site, 1 to <see cref="MaxZones"/> queue zones named exactly (zone keys built with
/// <see cref="ZoneKeys.For"/>, never a prefix or pattern, CWE-863), a UTC window of at most <see cref="MaxWindowDays"/> days, and
/// at most <see cref="MaxRows"/> rows, the comparison engine's bound for one kind of row; a larger read is refused, so the
/// caller reads in slices. Rows are passed on as stored: the F18 engine checks every row again before use (CWE-501, ARV-104f).
/// </para>
/// </summary>
internal sealed partial class SvcValidationResults(DatabaseSettings database, ValidationReaderSettings reader)
{
    #region Constants

    /// <summary>A campaign's bound on queue zones in its scope, which the comparison engine keeps too (ARV-104a, ARV-104e).</summary>
    public const int MaxZones = ValidationCampaign.MaxZones;

    /// <summary>The longest window one read covers: a campaign's planned days fit in a month and a day either side.</summary>
    public const int MaxWindowDays = 33;

    /// <summary>The comparison engine's bound on rows of one kind (ARV-104e): beyond it the caller reads per zone or per day.</summary>
    public const int MaxRows = 1_000_000;

    /// <summary>How long one read may run on the server.</summary>
    private const int CommandTimeoutSeconds = 120;

    private const string NotConfigured = "The validation reader login is not configured (Database:ValidationReader); the shadow nowcast cannot be read.";
    private const string Misconfigured = "The validation reader login is misconfigured; see the host's start-up check (ARV-104g1).";
    private const string InvalidSite = "The site code is not valid.";
    private const string InvalidZones = "Give 1 to 50 distinct queue zone names whose zone keys fit (ARV-114c).";
    private const string InvalidWindow = "Give a UTC window, its start before its end, of at most 33 days.";
    private const string TooManyRows = "The read would return more shadow minutes than one read may (1,000,000); read per zone or per day.";

    // Exact zone keys and a half-open window, in key and minute order (the engine takes them in any order).
    private const string ReadSql = """
        SELECT zone_key, minute_utc, nowcast_minutes, no_service, nowcast_degraded, sensor_cycle_minutes
          FROM queue_minute_shadow
         WHERE zone_key = ANY(@keys) AND minute_utc >= @from AND minute_utc < @to
         ORDER BY zone_key, minute_utc
         LIMIT @limit
        """;

    #endregion

    #region Properties

    /// <summary>The most rows one read returns (<see cref="MaxRows"/>; tests lower it to prove the bound).</summary>
    internal int RowLimit { get; init; } = MaxRows;

    #endregion

    #region Shadow read

    /// <summary>
    /// The stored shadow nowcasts of <paramref name="queueZones"/> at <paramref name="siteCode"/> from <paramref name="fromUtc"/>
    /// (inclusive) to <paramref name="toUtc"/> (exclusive), as the comparison engine's rows (queue zone by name, without the
    /// site). Read through the validation reader login only; an error when it is not configured or the read is out of bounds.
    /// The caller has already checked the campaign and the site against the caller's sites (ISiteScope, ARV-104g2).
    /// </summary>
    internal async Task<Result<IReadOnlyList<ShadowMinuteRow>>> ReadShadowAsync(string siteCode, IReadOnlyCollection<string> queueZones, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct)
    {
        if (!reader.IsConfigured)
            return Result.Error<IReadOnlyList<ShadowMinuteRow>>(NotConfigured);
        if (reader.Problems(database).Count > 0)
            return Result.Error<IReadOnlyList<ShadowMinuteRow>>(Misconfigured);
        if (!Site.IsValidCode(siteCode))
            return Result.Error<IReadOnlyList<ShadowMinuteRow>>(InvalidSite);
        if (queueZones is null || queueZones.Count is 0 or > MaxZones || queueZones.Any(z => !ZoneKeys.Fits(siteCode, z)) ||
            queueZones.Distinct(StringComparer.Ordinal).Count() != queueZones.Count)
            return Result.Error<IReadOnlyList<ShadowMinuteRow>>(InvalidZones);
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(MaxWindowDays))
            return Result.Error<IReadOnlyList<ShadowMinuteRow>>(InvalidWindow);

        // Each requested key maps back to the zone name it was built from (exact match only).
        var zonesByKey = queueZones.ToDictionary(z => ZoneKeys.For(siteCode, z), z => z, StringComparer.Ordinal);
        var connectionString = new NpgsqlConnectionStringBuilder(reader.BuildReaderConnectionString(database)) { CommandTimeout = CommandTimeoutSeconds }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(ReadSql, connection);
        command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = zonesByKey.Keys.ToArray() });
        command.Parameters.AddWithValue("from", fromUtc);
        command.Parameters.AddWithValue("to", toUtc);
        command.Parameters.AddWithValue("limit", RowLimit + 1);

        var rows = new List<ShadowMinuteRow>();
        await using var data = await command.ExecuteReaderAsync(ct);
        while (await data.ReadAsync(ct))
        {
            if (rows.Count == RowLimit)
                return Result.Error<IReadOnlyList<ShadowMinuteRow>>(TooManyRows);
            if (!zonesByKey.TryGetValue(data.GetString(0), out var zone))
                continue;
            rows.Add(new ShadowMinuteRow(zone, data.GetDateTime(1), data.IsDBNull(2) ? null : data.GetDouble(2), data.IsDBNull(3) ? null : data.GetString(3),
                data.GetBoolean(4), data.IsDBNull(5) ? null : data.GetDouble(5)));
        }

        return new Result<IReadOnlyList<ShadowMinuteRow>>(rows.AsReadOnly());
    }

    #endregion
}
