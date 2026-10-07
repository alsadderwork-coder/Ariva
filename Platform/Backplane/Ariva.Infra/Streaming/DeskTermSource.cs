using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Settings;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Streaming;

/// <summary>
/// The desk terms of the nowcast (F8, ARV-064) for the queue zones a stream worker holds. A queue zone of the site's
/// published profile with a lane category is served by the desks (kind Desk, not e-gates) of the immigration and
/// emigration checkpoints on its level whose lane categories include it, the same match as the immigration view
/// (<c>SvcImmigrationView</c>): there is no explicit link from a queue to its desks. Their closed minutes
/// (<c>desk_minute</c>, written by the desk feed under the key site/checkpoint/desk) over the last
/// <see cref="LookbackMinutes"/> give n_open, and AMAN's interval statistics of the same desks (<c>border_desk_interval</c>)
/// over the exit window the lane cycle time (F10, <see cref="Ariva.Core.Desks.LaneCycle"/>): together a
/// <see cref="DeskTerm"/> (<see cref="DeskTerms"/>), with its sensor-only part from the minutes' sensor-derived seconds for
/// the shadow nowcast (ARV-117, <see cref="DeskTerms.SensorOnly"/>). A zone without a lane, or
/// whose desks have no recent minute, has none and its nowcast stays on the exit rate. Parameterised SQL only.
/// </summary>
public class DeskTermSource(DatabaseSettings database, TimeProvider timeProvider, ILogger<DeskTermSource> logger = null)
{
    /// <summary>How far back desk minutes are read: the freshness allowance and the exit window, with room.</summary>
    public const int LookbackMinutes = 15;

    /// <summary>
    /// Desk minute rows, and interval rows, read per site at most (CWE-400): 15 minutes of 2,000 desks. A site that reaches
    /// it gets no desk terms (its queues keep the exit rate), never terms from an arbitrary part of its desks.
    /// </summary>
    public const int MaxRows = 30_000;

    /// <summary>The desk terms of the given zone keys (site/zone) that have one.</summary>
    public virtual async Task<IReadOnlyDictionary<string, DeskTerm>> LoadAsync(IReadOnlyCollection<string> zoneKeys, int windowMinutes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zoneKeys);
        var result = new Dictionary<string, DeskTerm>(StringComparer.Ordinal);
        var bySite = zoneKeys.Select(Split).Where(k => k is not null).GroupBy(k => k.Value.Site, StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Short timeouts: the stream worker reads this inline, and a database that is away must not hold its consumer long.
        var connectionString = new NpgsqlConnectionStringBuilder(database.BuildConnectionString()) { Timeout = 5, CommandTimeout = 10 }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        foreach (var site in bySite)
        {
            await using var command = new NpgsqlCommand("""
                WITH queues AS (
                    SELECT z.name AS zone, z.lane_category AS lane, z.level_id
                      FROM zone z JOIN zone_profile p ON p.id = z.profile_id
                     WHERE p.site_code = @site AND p.status = 'Published' AND z.kind = 'Queue' AND z.lane_category IS NOT NULL
                       AND z.name = ANY(@zones)
                ), desks AS (
                    SELECT DISTINCT q.zone, d.id AS desk_id, d.site_code || '/' || c.code || '/' || d.code AS desk_key
                      FROM queues q
                      JOIN checkpoint c ON c.level_id = q.level_id AND c.site_code = @site AND c.deleted_on IS NULL
                                       AND c.kind IN ('Immigration', 'Emigration')
                      JOIN desk d ON d.checkpoint_id = c.id AND d.site_code = @site AND d.deleted_on IS NULL AND d.kind = 'Desk'
                     WHERE q.lane = ANY(string_to_array(COALESCE(d.lane_category_codes, ''), ','))
                )
                (SELECT 'minute' AS what, ds.zone, m.desk_code, m.minute_utc, m.idle_seconds::float8, m.serving_seconds::float8,
                        m.unknown_seconds::float8, m.transactions::int, m.degraded, m.sensor_derived_seconds::float8
                   FROM desks ds JOIN desk_minute m ON m.desk_code = ds.desk_key
                  WHERE m.minute_utc >= @from AND m.minute_utc <= @to
                  ORDER BY m.minute_utc DESC
                  LIMIT @max)
                UNION ALL
                (SELECT 'interval', ds.zone, NULL, i.interval_start_utc, i.mean_cycle_seconds::float8, 0, 0, i.transactions_processed::int, false, 0::float8
                   FROM desks ds JOIN border_desk_interval i ON i.desk_id = ds.desk_id AND i.site_code = @site
                  WHERE i.interval_start_utc >= @cycleFrom AND i.interval_start_utc <= @to
                  ORDER BY i.interval_start_utc DESC
                  LIMIT @max)
                """, connection);
            command.Parameters.AddWithValue("site", site.Key);
            command.Parameters.Add(new NpgsqlParameter("zones", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = site.Select(k => k.Value.Zone).Distinct(StringComparer.Ordinal).ToArray() });
            command.Parameters.AddWithValue("from", now.AddMinutes(-LookbackMinutes));
            command.Parameters.AddWithValue("to", now);
            // AMAN's intervals over the exit window and the minutes they arrive late by (F10's lane cycle time).
            command.Parameters.AddWithValue("cycleFrom", now.AddMinutes(-windowMinutes - 2));
            command.Parameters.AddWithValue("max", MaxRows);
            var minutes = new List<(string Zone, DeskMinuteSample Minute)>();
            var intervals = new List<(string Zone, double MeanCycleSeconds, int Transactions)>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var zone = reader.GetString(1);
                    if (reader.GetString(0) == "interval")
                        intervals.Add((zone, reader.GetDouble(4), reader.GetInt32(7)));
                    else
                        minutes.Add((zone, new DeskMinuteSample(reader.GetString(2), DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                            reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetInt32(7), reader.GetBoolean(8), reader.GetDouble(9))));
                }
            }

            if (minutes.Count >= MaxRows || intervals.Count >= MaxRows)
            {
                logger?.LogWarning("Desk terms of site {Site}: {Max} rows or more in the window; its queues keep the exit rate", site.Key, MaxRows);
                continue;
            }

            foreach (var zone in minutes.GroupBy(r => r.Zone, StringComparer.Ordinal))
            {
                var cycle = Ariva.Core.Desks.LaneCycle.Minutes(intervals.Where(i => i.Zone == zone.Key).Select(i => (i.MeanCycleSeconds, i.Transactions)));
                if (DeskTerms.Compute(zone.Select(r => r.Minute), windowMinutes, now, cycle) is { } term)
                    result[ZoneKeys.For(site.Key, zone.Key)] = term;
            }
        }

        return result;
    }

    private static (string Site, string Zone)? Split(string zoneKey)
    {
        var slash = zoneKey?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return slash <= 0 || slash == zoneKey.Length - 1 ? null : (zoneKey[..slash], zoneKey[(slash + 1)..]);
    }
}
