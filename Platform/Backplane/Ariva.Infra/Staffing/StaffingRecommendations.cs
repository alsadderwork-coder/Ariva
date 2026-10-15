using System.Diagnostics.CodeAnalysis;
using Ariva.Core.Border;
using Ariva.Core.Flights;
using Ariva.Core.Sensing;
using Ariva.Core.Staffing;
using Ariva.Infra.Settings;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Staffing;

/// <summary>What one run of the staffing recommendation did (lanes refused on their own beyond the desk bound in <see cref="LanesRefused"/>).</summary>
public sealed record StaffingRun(int Sites, int SitesHeldElsewhere, int SitesFailed, int SitesRefused, int Lanes, int Rows, int LanesRefused = 0);

/// <summary>
/// What one site's pass did: lanes computed and rows inserted, or the bound that refused it (a fixed text), and the lanes
/// refused on their own (<see cref="StaffingInputs.TooManyLaneDesks"/>) while the rest of the site was computed.
/// </summary>
public sealed record StaffingSitePass(int Lanes, int Rows, string Refused = null, int RefusedLanes = 0);

/// <summary>
/// The indicative staffing recommendation (ARV-181a, formulas F16 pilot version), run by Ariva.Api.Cronz every minute. For
/// each site with an arrivals-hall lane in its published profile, in one transaction holding the site's advisory lock
/// (class 51, so one replica computes a site at a time; the others skip it), it reads the site's inputs
/// (<see cref="StaffingInputs"/>, bounded; a site beyond a bound is refused with one warning naming the site and the bound,
/// never cut short; a lane beyond the desk bound is refused on its own with one warning naming the site and the count),
/// computes every lane (<see cref="SiteStaffing"/>, pure) and inserts one row per lane, minute and
/// interval into <c>lane_staffing</c> (script 0053) by binary COPY into a staging table and INSERT ... ON CONFLICT DO
/// NOTHING: the first computation of a minute stands, so a second run or replica writes nothing twice. A minute the job
/// missed (the host was down) is never filled later: the arrival wave as it was then cannot be read back. No HTTP call to
/// any host. Logs name the site and counts only, never a stored value.
/// </summary>
public sealed class StaffingRecommendations(IServiceScopeFactory scopes, DatabaseSettings database, TimeProvider timeProvider, StaffingSettings settings,
    ArrivalWaveSettings wave, EgateCouplingSettings coupling, ILogger<StaffingRecommendations> logger)
{
    /// <summary>The advisory lock class of a site's pass (two-key form; 25, 26, 37, 38, 41, 42, 49 and 50 are taken).</summary>
    public const int LockClass = 51;

    /// <summary>One run over the sites, in site code order.</summary>
    public async Task<StaffingRun> RunAsync(CancellationToken ct = default)
    {
        int held = 0, failed = 0, refused = 0, lanes = 0, rows = 0, lanesRefused = 0;
        var sites = await SitesAsync(ct);
        foreach (var site in sites)
        {
            try
            {
                var pass = await SiteAsync(site, ct);
                if (pass is null)
                {
                    held++;
                    continue;
                }

                if (pass.Refused is not null)
                {
                    refused++;
                    continue;
                }

                lanes += pass.Lanes;
                rows += pass.Rows;
                lanesRefused += pass.RefusedLanes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // one site that fails is logged and tried again next run; the other sites go on
            catch (Exception e)
#pragma warning restore CA1031
            {
                failed++;
                logger.LogError(e, "Staffing recommendation of site {Site} failed; the next run tries again", site);
            }
        }

        return new StaffingRun(sites.Count, held, failed, refused, lanes, rows, lanesRefused);
    }

    /// <summary>The sites with an arrivals-hall lane in their published profile, in code order, at most <see cref="StaffingSettings.MaxSitesPerRun"/>.</summary>
    public async Task<IReadOnlyList<string>> SitesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadAsync(connection, null, """
            SELECT DISTINCT p.site_code
              FROM zone_profile p JOIN zone z ON z.profile_id = p.id
             WHERE p.status = 'Published' AND z.kind = 'Queue' AND z.lane_category IS NOT NULL
               AND EXISTS (SELECT 1 FROM checkpoint c
                            WHERE c.level_id = z.level_id AND c.site_code = p.site_code AND c.deleted_on IS NULL AND c.kind = 'Immigration')
             ORDER BY 1
             LIMIT @max
            """, [new NpgsqlParameter("max", settings.MaxSitesPerRun)], r => r.GetString(0), ct);
    }

    /// <summary>Computes and records one site's minute; null when another replica holds the site.</summary>
    public async Task<StaffingSitePass> SiteAsync(string siteCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(siteCode);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var locked = await ReadAsync(connection, transaction, "SELECT pg_try_advisory_xact_lock(51, hashtext(@site))", [new NpgsqlParameter("site", siteCode)],
            r => r.GetBoolean(0), ct);
        if (!locked[0])
            return null;

        StaffingRead read;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            try
            {
                read = await scope.ServiceProvider.GetRequiredService<StaffingInputs>().ReadAsync(siteCode, now, settings, ct);
            }
            finally
            {
                // Reads only: nothing to commit.
                await unitOfWork.RollbackAsync(CancellationToken.None);
            }
        }

        if (read.Refused is not null)
        {
            // CWE-400, CWE-120: refused, never cut short; the site and the bound's fixed text only.
            logger.LogWarning("Staffing recommendation of site {Site} refused: {Bound}; no minute is written until it is back within the bound", siteCode, read.Refused);
            await transaction.RollbackAsync(ct);
            return new StaffingSitePass(0, 0, read.Refused);
        }

        if (read.RefusedLanes > 0)
        {
            // CWE-120: those lanes only; the site and the count with the bound's fixed text, never a lane's values.
            logger.LogWarning("Staffing recommendation of site {Site}: {Lanes} lanes refused: {Bound}; they have no minute until back within the bound", siteCode,
                read.RefusedLanes, StaffingInputs.TooManyLaneDesks);
        }

        var lanes = SiteStaffing.Compute(read.Input, settings, wave, coupling);
        var inserted = lanes.Count == 0 ? 0 : await WriteAsync(connection, transaction, siteCode, lanes, now, ct);
        await transaction.CommitAsync(ct);
        return new StaffingSitePass(lanes.Count, inserted, null, read.RefusedLanes);
    }

    /// <summary>Inserts the lanes' rows not yet recorded for their minute and returns how many were inserted.</summary>
    private static async Task<int> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode, IReadOnlyList<LaneStaffingRow> lanes, DateTime now,
        CancellationToken ct)
    {
        await Execute(connection, transaction, "CREATE TEMP TABLE stage_lane_staffing (LIKE lane_staffing INCLUDING DEFAULTS) ON COMMIT DROP", ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
                wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now, open_now_degraded, desks,
                target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var lane in lanes)
            {
                var key = ZoneKeys.For(siteCode, lane.Zone);
                for (var k = 0; k < lane.Staffing.Intervals.Count; k++)
                {
                    var interval = lane.Staffing.Intervals[k];
                    await copy.StartRowAsync(ct);
                    await copy.WriteAsync(key, NpgsqlDbType.Varchar, ct);
                    await copy.WriteAsync(lane.MinuteUtc, NpgsqlDbType.TimestampTz, ct);
                    await copy.WriteAsync((short)k, NpgsqlDbType.Smallint, ct);
                    await copy.WriteAsync(lane.Lane, NpgsqlDbType.Varchar, ct);
                    await copy.WriteAsync(interval.StartUtc, NpgsqlDbType.TimestampTz, ct);
                    await copy.WriteAsync((short)interval.Minutes, NpgsqlDbType.Smallint, ct);
                    await Value(copy, interval.Arrivals, NpgsqlDbType.Double, ct);
                    await Value(copy, interval.Recommended is { } n ? (short?)n : null, NpgsqlDbType.Smallint, ct);
                    await copy.WriteAsync(interval.Bound.ToString(), NpgsqlDbType.Varchar, ct);
                    await copy.WriteAsync(interval.AtLimit, NpgsqlDbType.Boolean, ct);
                    await Value(copy, interval.ShortfallDesks, NpgsqlDbType.Integer, ct);
                    await Value(copy, interval.WaitRecommendedMinutes, NpgsqlDbType.Double, ct);
                    await Value(copy, interval.WaitOpenNowMinutes, NpgsqlDbType.Double, ct);
                    await copy.WriteAsync(interval.NoServiceOpenNow, NpgsqlDbType.Boolean, ct);
                    await Value(copy, interval.BacklogEnd, NpgsqlDbType.Double, ct);
                    await copy.WriteAsync(interval.Gap.ToString(), NpgsqlDbType.Varchar, ct);
                    await copy.WriteAsync(lane.Staffing.Gap.ToString(), NpgsqlDbType.Varchar, ct);
                    await Value(copy, lane.QueueMinuteUtc, NpgsqlDbType.TimestampTz, ct);
                    await Value(copy, lane.QueueNow, NpgsqlDbType.Double, ct);
                    await Value(copy, lane.OpenNow is { } open ? (short?)open : null, NpgsqlDbType.Smallint, ct);
                    await copy.WriteAsync(lane.OpenNowDegraded, NpgsqlDbType.Boolean, ct);
                    await copy.WriteAsync((short)lane.Desks, NpgsqlDbType.Smallint, ct);
                    await copy.WriteAsync(lane.TargetMinutes, NpgsqlDbType.Double, ct);
                    await copy.WriteAsync(lane.BusyCap, NpgsqlDbType.Double, ct);
                    await Value(copy, lane.Throughput.RatePerDeskMinute, NpgsqlDbType.Double, ct);
                    await Value(copy, lane.Throughput.Source?.ToString(), NpgsqlDbType.Varchar, ct);
                    await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
                }
            }

            await copy.CompleteAsync(ct);
        }

        var inserted = await ReadAsync(connection, transaction, """
            INSERT INTO lane_staffing SELECT * FROM stage_lane_staffing ON CONFLICT (zone_key, minute_utc, interval_index) DO NOTHING RETURNING interval_index
            """, [], r => r.GetInt16(0), ct);
        return inserted.Count;
    }

    private static Task Value(NpgsqlBinaryImporter copy, string value, NpgsqlDbType type, CancellationToken ct) =>
        value is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(value, type, ct);

    private static Task Value<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type, CancellationToken ct) where T : struct =>
        value is { } v ? copy.WriteAsync(v, type, ct) : copy.WriteNullAsync(ct);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, [ConstantExpected] string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<T>> ReadAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, [ConstantExpected] string sql, NpgsqlParameter[] parameters,
        Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }
}
