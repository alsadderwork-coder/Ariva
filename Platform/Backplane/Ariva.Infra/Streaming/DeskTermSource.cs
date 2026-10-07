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
/// over the exit window the lane cycle time (F10, <see cref="Ariva.Core.Desks.LaneCycle"/>; since ARV-117d per person in
/// working time, every interval checked against contract V1's bounds first, CWE-501): together a
/// <see cref="DeskTerm"/> (<see cref="DeskTerms"/>), with its sensor-only part for the shadow nowcast (ARV-117,
/// <see cref="DeskTerms.SensorOnly"/>) from the sensor-only desk engine's minutes of the same desks
/// (<c>desk_sensor_minute</c>, ARV-117a; the only reader of that table besides the validation comparison), or the
/// minutes' sensor-derived seconds for a desk without one. Since ARV-117b the sensor-only part also carries the lane's
/// sensor-only busy time over the shadow's cycle window (<see cref="SensorCycle"/>; the published term never reads it),
/// and every sensor-only minute read back is checked against script 0044's bounds first (CWE-501): a row that fails is
/// left out and counted, a lane over the window's cap gets no busy time (CWE-120), each with a warning that names the site
/// and the count only. A zone without a lane, or
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

    /// <summary>
    /// The desk terms of the given zone keys (site/zone) that have one; their sensor-only parts carry the busy time of the
    /// shadow's sensor cycle window (ARV-117b, <paramref name="sensorCycle"/>).
    /// </summary>
    public virtual async Task<IReadOnlyDictionary<string, DeskTerm>> LoadAsync(IReadOnlyCollection<string> zoneKeys, int windowMinutes, SensorCycleSettings sensorCycle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zoneKeys);
        ArgumentNullException.ThrowIfNull(sensorCycle);
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
                        m.unknown_seconds::float8, m.transactions::int, m.degraded, m.sensor_derived_seconds::float8,
                        s.desk_code IS NOT NULL AS sensed, s.closed_seconds::float8, s.idle_seconds::float8, s.serving_seconds::float8,
                        s.paused_seconds::float8, s.unknown_seconds::float8, s.degraded
                   FROM desks ds JOIN desk_minute m ON m.desk_code = ds.desk_key
                   LEFT JOIN desk_sensor_minute s ON s.desk_code = m.desk_code AND s.minute_utc = m.minute_utc
                  WHERE m.minute_utc >= @from AND m.minute_utc <= @to
                  ORDER BY m.minute_utc DESC
                  LIMIT @max)
                UNION ALL
                (SELECT 'interval', ds.zone, i.id::text, i.interval_start_utc, i.mean_cycle_seconds::float8, i.mean_service_seconds::float8,
                        i.p90_service_seconds::float8, i.transactions_processed::int, false, i.documents_processed::float8,
                        false, NULL, NULL, NULL, NULL, NULL, NULL
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
            var intervals = new List<(string Zone, string Id, Ariva.Core.Desks.DeskIntervalSample Interval)>();
            var refused = 0;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var zone = reader.GetString(1);
                    if (reader.GetString(0) == "interval")
                    {
                        // ARV-117d: every value is checked against contract V1's bounds before use, in LaneCycle (CWE-501).
                        intervals.Add((zone, reader.GetString(2), new Ariva.Core.Desks.DeskIntervalSample(reader.GetInt32(7), Count(reader.GetDouble(9)), reader.GetDouble(5),
                            reader.GetDouble(6), reader.GetDouble(4))));
                        continue;
                    }

                    // ARV-117a: the sensor-only engine's minute of the same desk, for the shadow's desk term only. ARV-117b
                    // (CWE-501): checked against its bounds as read back (seconds per state from 0 to 60, together at most a
                    // minute, a flag); a row that fails is left out (the desk has no sensor-only minute there) and counted.
                    DeskSensorSample sensor = null;
                    if (reader.GetBoolean(10))
                    {
                        sensor = SensorSample(reader.GetDouble(11), reader.GetDouble(12), reader.GetDouble(13), reader.GetDouble(14), reader.GetDouble(15),
                            reader.IsDBNull(16) ? null : reader.GetBoolean(16));
                        refused += sensor is null ? 1 : 0;
                    }

                    minutes.Add((zone, new DeskMinuteSample(reader.GetString(2), DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                        reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetInt32(7), reader.GetBoolean(8), reader.GetDouble(9), sensor)));
                }
            }

            if (minutes.Count >= MaxRows || intervals.Count >= MaxRows)
            {
                logger?.LogWarning("Desk terms of site {Site}: {Max} rows or more in the window; its queues keep the exit rate", site.Key, MaxRows);
                continue;
            }

            if (refused > 0)
            {
                RefusedSensorMinutes += refused;
                logger?.LogWarning("Desk terms of site {Site}: {Count} sensor-only desk minutes failed their bounds and were left out", site.Key, refused);
            }

            var capped = 0;
            foreach (var zone in minutes.GroupBy(r => r.Zone, StringComparer.Ordinal))
            {
                // F10 (ARV-117d): the lane cycle time per person in working time from AMAN's intervals of the lane's desks.
                var cycle = Ariva.Core.Desks.LaneCycle.PerPerson(intervals.Where(i => i.Zone == zone.Key).Select(i => i.Interval), CycleMethod);
                if (DeskTerms.Compute(zone.Select(r => r.Minute), windowMinutes, now, cycle, sensorCycle) is not { } term)
                    continue;
                result[ZoneKeys.For(site.Key, zone.Key)] = term;
                capped += term.SensorOnly?.SensorBusy?.Missing == SensorCycleFallback.TooManyMinutes ? 1 : 0;
            }

            // A desk listed under several lanes reads each of its interval rows once per lane, and each lane's term is flagged by
            // it; the rows left out are counted once each (by row id) over the lanes that have desk minutes in the read.
            var withMinutes = minutes.Select(m => m.Zone).ToHashSet(StringComparer.Ordinal);
            var refusedIntervals = Ariva.Core.Desks.LaneCycle.PerPerson(intervals.Where(i => withMinutes.Contains(i.Zone)).DistinctBy(i => i.Id, StringComparer.Ordinal)
                .Select(i => i.Interval), CycleMethod).Refused;
            if (refusedIntervals > 0)
            {
                // CWE-501: AMAN interval rows outside contract V1's bounds as read back (or with transactions and no time) are left out and flag the lane's term.
                RefusedIntervals += refusedIntervals;
                logger?.LogWarning("Desk terms of site {Site}: {Count} AMAN desk intervals were outside contract V1's bounds or had transactions without time, and were left out", site.Key, refusedIntervals);
            }

            if (capped > 0)
            {
                // CWE-120: a lane with more sensor-only desk minutes in the window than the cap gets no sensor cycle time.
                CappedSensorWindows += capped;
                logger?.LogWarning("Desk terms of site {Site}: {Count} queues over the cap of sensor-only desk minutes in the cycle window; their shadow keeps the exit rate",
                    site.Key, capped);
            }
        }

        return result;
    }

    /// <summary>The desk terms of the given zone keys, the shadow's busy windows with the default sensor cycle settings.</summary>
    public virtual Task<IReadOnlyDictionary<string, DeskTerm>> LoadAsync(IReadOnlyCollection<string> zoneKeys, int windowMinutes, CancellationToken ct) =>
        LoadAsync(zoneKeys, windowMinutes, new SensorCycleSettings(), ct);

    /// <summary>Sensor-only desk minutes read back and left out because they failed their bounds (CWE-501), since start.</summary>
    public long RefusedSensorMinutes { get; private set; }

    /// <summary>AMAN desk intervals read back and left out (outside contract V1's bounds, CWE-501, or transactions without time), since start (ARV-117d).</summary>
    public long RefusedIntervals { get; private set; }

    /// <summary>
    /// How the lane cycle time leaves out idle time (ARV-117d, Proposed; accepted by the owner on 2026-10-07). The switch to
    /// <see cref="Ariva.Core.Desks.LaneCycleMethod.CycleCappedAtP90"/> if the walk-up gap re-measured on pilot data (ARV-104) is long.
    /// </summary>
    public const Ariva.Core.Desks.LaneCycleMethod CycleMethod = Ariva.Core.Desks.LaneCycleMethod.MeanService;

    /// <summary>A count read back as a number: the integer when it is one within an int, else -1 (outside the contract, so left out).</summary>
    private static int Count(double value) => double.IsFinite(value) && value is >= 0 and <= int.MaxValue && Math.Floor(value) == value ? (int)value : -1;

    /// <summary>Queues whose sensor cycle window held more desk minutes than the cap (CWE-120), since start.</summary>
    public long CappedSensorWindows { get; private set; }

    /// <summary>
    /// A sensor-only desk minute as read back (CWE-501), or null when it fails the bounds script 0044 checks on write: each
    /// state's seconds a finite number from 0 to 60, together at most a minute (with 0044's rounding room), and a flag.
    /// </summary>
    internal static DeskSensorSample SensorSample(double closed, double idle, double serving, double paused, double unknown, bool? degraded)
    {
        static bool Second(double v) => double.IsFinite(v) && v is >= 0 and <= 60;
        if (degraded is not { } flag || !Second(closed) || !Second(idle) || !Second(serving) || !Second(paused) || !Second(unknown) ||
            closed + idle + serving + paused + unknown > 60.001)
            return null;
        return new DeskSensorSample(idle + serving, unknown, flag, serving);
    }

    private static (string Site, string Zone)? Split(string zoneKey)
    {
        var slash = zoneKey?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return slash <= 0 || slash == zoneKey.Length - 1 ? null : (zoneKey[..slash], zoneKey[(slash + 1)..]);
    }
}
