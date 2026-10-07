using System.Diagnostics.CodeAnalysis;
using Ariva.Core.Availability;
using Ariva.Core.Sensing;
using Ariva.Infra.Live;
using Ariva.Infra.Settings;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Availability;

/// <summary>What one run of the ledger did.</summary>
public sealed record AvailabilityRun(int Sites, int SitesHeldElsewhere, int SitesFailed, int Minutes, int Available, int Unavailable, int Unobserved, int SitesSkipped = 0);

/// <summary>
/// What one site's pass wrote (minutes inserted, by state), how many minutes it decided after their live window, and
/// whether Redis could not be read. <paramref name="TimeZoneUnresolved"/>: the site's airport time zone is not known on
/// this host, so nothing was decided and the cursor did not move.
/// </summary>
public sealed record AvailabilitySitePass(int Minutes, int Available, int Unavailable, int Unobserved, int CaughtUp, bool RedisFailed = false, bool TimeZoneUnresolved = false);

/// <summary>
/// The availability ledger (ARV-118, formulas F18, script 0042), run by Ariva.Api.Cronz every minute. For each site with a
/// published or retired zone profile, in one transaction holding the site's advisory lock (class 50, so one replica
/// decides a site at a time; the others skip it), it decides every minute not yet recorded, up to the latest minute whose
/// grace period has passed, and inserts one row per minute (binary COPY into a staging table, then INSERT ... ON CONFLICT
/// DO NOTHING: the first decision of a minute stands, so a second run or replica writes nothing twice).
/// <para>
/// Inputs are the database (the zone profile in force at each minute, the queue_minute rows, the site's calendar) and the
/// live snapshots in Redis (<see cref="ILiveSnapshotStore"/>), read once per pass. No HTTP call to any host.
/// </para>
/// <para>
/// Order: minutes still in their live window are decided first, with Redis; the backlog behind the site's cursor (after
/// the job was down) is then filled from the cursor, at most <see cref="AvailabilitySettings.MaxMinutesPerRun"/> minutes
/// per pass, from the database alone (<see cref="AvailabilityRule"/>: a missing minute is Unavailable, otherwise
/// Unobserved; never Available from today's Redis state). A site seen for the first time starts at the latest decidable
/// minute. Values read back from storage are checked again (CWE-501): calendar JSON that a request could not have
/// produced is skipped with a warning, and snapshots come through the store's plausibility checks.
/// </para>
/// </summary>
public sealed class AvailabilityLedger(DatabaseSettings database, ILiveSnapshotStore snapshots, TimeProvider timeProvider, AvailabilitySettings settings,
    ILogger<AvailabilityLedger> logger)
{
    /// <summary>The advisory lock class of a site's pass (two-key form; 41, 42 and 49 are taken).</summary>
    public const int LockClass = 50;

    /// <summary>Calendar rows of one kind read for a site at most (CWE-400).</summary>
    private const int MaxCalendarRows = 5_000;

    /// <summary>Zones of one profile read at most (a profile's queue zones; the stream holds 2,000 zones per instance).</summary>
    private const int MaxProfileZones = 2_000;

    /// <summary>Sites whose unresolved time zone has been logged, so the warning is written once per site (again after it resolves).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> unresolvedZones = new(StringComparer.Ordinal);

    /// <summary>One run over the sites, in site code order.</summary>
    public async Task<AvailabilityRun> RunAsync(CancellationToken ct = default)
    {
        int held = 0, failed = 0, skipped = 0, minutes = 0, available = 0, unavailable = 0, unobserved = 0;
        var redisDown = false;
        var sites = await SitesAsync(ct);
        foreach (var site in sites)
        {
            try
            {
                // Once Redis failed in this run, the other sites do not wait for it again: their zones count as stale too.
                var pass = await SiteAsync(site, redisDown, ct);
                if (pass is null)
                {
                    held++;
                    continue;
                }

                if (pass.TimeZoneUnresolved)
                {
                    skipped++;
                    continue;
                }

                redisDown |= pass.RedisFailed;
                minutes += pass.Minutes;
                available += pass.Available;
                unavailable += pass.Unavailable;
                unobserved += pass.Unobserved;
                if (pass.CaughtUp > 0)
                    logger.LogInformation("Availability ledger of site {Site} caught up {Minutes} minutes from the database alone", site, pass.CaughtUp);
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
                logger.LogError(e, "Availability ledger of site {Site} failed; the next run tries again", site);
            }
        }

        return new AvailabilityRun(sites.Count, held, failed, minutes, available, unavailable, unobserved, skipped);
    }

    /// <summary>The sites with a published or retired zone profile, in code order, at most <see cref="AvailabilitySettings.MaxSitesPerRun"/>.</summary>
    public async Task<IReadOnlyList<string>> SitesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadAsync(connection, null, """
            SELECT DISTINCT site_code FROM zone_profile WHERE status IN ('Published', 'Retired') ORDER BY 1 LIMIT @max
            """, [new NpgsqlParameter("max", settings.MaxSitesPerRun)], r => r.GetString(0), ct);
    }

    /// <summary>Decides and records one site's open minutes; null when another replica holds the site.</summary>
    public Task<AvailabilitySitePass> SiteAsync(string siteCode, CancellationToken ct = default) => SiteAsync(siteCode, false, ct);

    /// <summary>
    /// Decides and records one site's open minutes; null when another replica holds the site. With
    /// <paramref name="redisDown"/> the live snapshots are not read (Redis failed earlier in the run), so every zone of a live
    /// minute counts as stale.
    /// </summary>
    public async Task<AvailabilitySitePass> SiteAsync(string siteCode, bool redisDown, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(siteCode);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var latest = AvailabilityRule.LatestDecidable(now, settings);
        // The earliest minute still in its live window at now.
        var liveStart = AvailabilityRule.FloorMinute(now - TimeSpan.FromMinutes(1) - settings.Grace - settings.LiveWindow) + TimeSpan.FromMinutes(1);

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var site = new NpgsqlParameter("site", siteCode);
        var locked = await ReadAsync(connection, transaction, "SELECT pg_try_advisory_xact_lock(50, hashtext(@site))", [site], r => r.GetBoolean(0), ct);
        if (!locked[0])
            return null;

        // The site's local time decides its calendar; an airport time zone this host does not know is never replaced by UTC
        // (that would classify the minutes against the wrong hours, permanently). The site waits, cursor unmoved.
        var (zone, resolved) = await TimeZoneAsync(connection, transaction, siteCode, ct);
        if (!resolved)
        {
            await transaction.RollbackAsync(ct);
            return new AvailabilitySitePass(0, 0, 0, 0, 0, TimeZoneUnresolved: true);
        }

        var cursors = await ReadAsync(connection, transaction, "SELECT next_minute_utc FROM availability_cursor WHERE site_code = @site",
            [site.Clone()], r => Utc(r.GetDateTime(0)), ct);
        var cursor = cursors.Count > 0 ? cursors[0] : latest;
        if (cursor > latest)
        {
            await transaction.CommitAsync(ct);
            return new AvailabilitySitePass(0, 0, 0, 0, 0);
        }

        // Live minutes first, then the backlog from the cursor (bounded); the cursor moves past what was filled.
        var liveFrom = cursor > liveStart ? cursor : liveStart;
        var live = Minutes(liveFrom, latest);
        var backlogEnd = Min(liveFrom - TimeSpan.FromMinutes(1), cursor + TimeSpan.FromMinutes(settings.MaxMinutesPerRun - 1));
        var backlog = Minutes(cursor, backlogEnd);
        var nextCursor = backlog.Count > 0 && backlogEnd < liveFrom - TimeSpan.FromMinutes(1) ? backlogEnd + TimeSpan.FromMinutes(1) : latest + TimeSpan.FromMinutes(1);
        var all = backlog.Concat(live).ToList();
        if (all.Count == 0)
        {
            await SaveCursorAsync(connection, transaction, siteCode, nextCursor, now, ct);
            await transaction.CommitAsync(ct);
            return new AvailabilitySitePass(0, 0, 0, 0, 0);
        }

        var from = all[0];
        var to = all[^1];
        var calendar = await CalendarAsync(connection, transaction, siteCode, zone, from, to, ct);
        var profiles = await ProfilesAsync(connection, transaction, siteCode, from, to + TimeSpan.FromMinutes(1), ct);
        var keys = profiles.SelectMany(p => p.ZoneKeys).Distinct(StringComparer.Ordinal).ToArray();
        var present = new HashSet<(string, DateTime)>();
        if (keys.Length > 0)
        {
            foreach (var (start, end) in new[] { (backlog.FirstOrDefault(), backlog.LastOrDefault()), (live.FirstOrDefault(), live.LastOrDefault()) })
            {
                if (start == default)
                    continue;
                var rows = await ReadAsync(connection, transaction, """
                    SELECT zone_key, minute_utc FROM queue_minute WHERE zone_key = ANY (@keys) AND minute_utc >= @from AND minute_utc <= @to
                    """, [new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = keys },
                        new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = start },
                        new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = end }], r => (r.GetString(0), Utc(r.GetDateTime(1))), ct);
                present.UnionWith(rows);
            }
        }

        // Redis only for live minutes, once per pass; a failed read leaves the zones without a snapshot (stale), and is logged.
        var (liveSnapshots, redisFailed) = live.Count > 0 && !redisDown
            ? await SnapshotsAsync(profiles.Where(p => live.Any(p.InForce)).SelectMany(p => p.ZoneKeys).Distinct(StringComparer.Ordinal), siteCode, ct)
            : ([], redisDown);

        var rowsToWrite = new List<LedgerRow>(all.Count);
        var liveSet = live.ToHashSet();
        foreach (var minute in all)
        {
            var isLive = liveSet.Contains(minute) && AvailabilityRule.IsLive(minute, now, settings);
            var profile = Enumerable.MaxBy(profiles.Where(p => p.InForce(minute)), p => p.Version);
            var zones = (profile?.ZoneKeys ?? []).Select(k => new ZoneObservation(k, present.Contains((k, minute)),
                isLive && liveSnapshots.TryGetValue(k, out var s) ? s : null)).ToList();
            var decision = AvailabilityRule.Decide(minute, zones, now, isLive, settings);
            rowsToWrite.Add(new LedgerRow(minute, calendar.Classify(minute), decision, profile?.Version));
        }

        var inserted = await WriteAsync(connection, transaction, siteCode, rowsToWrite, now, ct);
        await SaveCursorAsync(connection, transaction, siteCode, nextCursor, now, ct);
        await transaction.CommitAsync(ct);
        return new AvailabilitySitePass(inserted.Count, inserted.Count(r => r.Decision.State == AvailabilityState.Available),
            inserted.Count(r => r.Decision.State == AvailabilityState.Unavailable), inserted.Count(r => r.Decision.State == AvailabilityState.Unobserved),
            inserted.Count(r => r.Decision.Reasons.Contains(AvailabilityReasons.NotObservedLive)), redisFailed);
    }

    #region Reads

    private sealed record Profile(int Version, DateTime PublishedUtc, DateTime? RetiredUtc, IReadOnlyList<string> ZoneKeys)
    {
        public bool InForce(DateTime minute) => PublishedUtc <= minute && (RetiredUtc is null || RetiredUtc > minute);
    }

    /// <summary>The site's profiles in force at some point of [from, to), with the zone keys of their queue zones.</summary>
    private async Task<List<Profile>> ProfilesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await ReadAsync(connection, transaction, """
            SELECT p.version, p.published_on, p.retired_on, z.name
              FROM zone_profile p LEFT JOIN zone z ON z.profile_id = p.id AND z.kind = 'Queue'
             WHERE p.site_code = @site AND p.status IN ('Published', 'Retired') AND p.version IS NOT NULL AND p.published_on IS NOT NULL
               AND p.published_on < @to AND (p.retired_on IS NULL OR p.retired_on > @from)
             ORDER BY p.version, z.name
             LIMIT @max
            """, [new NpgsqlParameter("site", siteCode), new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from },
                new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to }, new NpgsqlParameter("max", MaxProfileZones * 4)],
            r => (Version: r.GetInt32(0), Published: Utc(r.GetDateTime(1)), Retired: r.IsDBNull(2) ? (DateTime?)null : Utc(r.GetDateTime(2)),
                Zone: r.IsDBNull(3) ? null : r.GetString(3)), ct);
        return [.. rows.GroupBy(r => (r.Version, r.Published, r.Retired)).Select(g => new Profile(g.Key.Version, g.Key.Published, g.Key.Retired,
            [.. g.Where(r => r.Zone is not null && ZoneKeys.Fits(siteCode, r.Zone)).Select(r => ZoneKeys.For(siteCode, r.Zone)).Distinct(StringComparer.Ordinal).Take(MaxProfileZones)]))];
    }

    private async Task<(Dictionary<string, SnapshotTimes> Found, bool Failed)> SnapshotsAsync(IEnumerable<string> zoneKeys, string siteCode, CancellationToken ct)
    {
        var found = new Dictionary<string, SnapshotTimes>(StringComparer.Ordinal);
        try
        {
            foreach (var key in zoneKeys)
            {
                if (await snapshots.GetAsync(key, ct) is { } snapshot)
                    found[key] = new SnapshotTimes(Utc(snapshot.MinuteUtc), Utc(snapshot.PublishedUtc));
            }
        }
        catch (Exception e) when (e is StackExchange.Redis.RedisException or TimeoutException)
        {
            // Without Redis no zone can be shown live: every zone of the minute is stale, as the screens would show it.
            logger.LogWarning(e, "Availability ledger of site {Site}: live snapshots not readable; the zones count as stale", siteCode);
            return ([], true);
        }

        return (found, false);
    }

    /// <summary>
    /// The site's time zone: its airport's, or UTC for a site without an airport. Not resolved (false) when the airport's
    /// stored IANA id is unknown on this host; logged as a warning once per site until it resolves again.
    /// </summary>
    private async Task<(TimeZoneInfo Zone, bool Resolved)> TimeZoneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode, CancellationToken ct)
    {
        var zoneIds = await ReadAsync(connection, transaction, """
            SELECT a.time_zone_id FROM terminal t JOIN airport a ON a.id = t.airport_id
             WHERE t.site_code = @site AND t.deleted_on IS NULL AND a.deleted_on IS NULL ORDER BY a.iata_code LIMIT 1
            """, [new NpgsqlParameter("site", siteCode)], r => r.IsDBNull(0) ? null : r.GetString(0), ct);
        if (zoneIds.Count == 0)
        {
            unresolvedZones.TryRemove(siteCode, out _);
            return (TimeZoneInfo.Utc, true);
        }

        if (zoneIds[0] is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
        {
            unresolvedZones.TryRemove(siteCode, out _);
            return (found, true);
        }

        // The stored id is not logged: it is data from storage; the site code identifies the airport to fix.
        if (unresolvedZones.TryAdd(siteCode, 0))
            logger.LogWarning("Availability ledger of site {Site}: its airport's time zone is not known on this host; the site is skipped until it resolves (no minutes written)", siteCode);
        return (null, false);
    }

    /// <summary>The site's calendar entries that can touch [from, to], in the site's time zone.</summary>
    private async Task<OperatingCalendar> CalendarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode, TimeZoneInfo zone, DateTime from, DateTime to,
        CancellationToken ct)
    {

        // Local dates a day either side of the range cover any offset and overnight intervals of the day before.
        var firstDate = CalendarDates.Format(DateOnly.FromDateTime(from).AddDays(-2));
        var lastDate = CalendarDates.Format(DateOnly.FromDateTime(to).AddDays(2));
        var weekRows = await ReadAsync(connection, transaction, """
            SELECT effective_from, hours, recorded_utc, deleted_on FROM operating_week
             WHERE site_code = @site AND effective_from <= @last ORDER BY effective_from DESC, recorded_utc DESC LIMIT @max
            """, [new NpgsqlParameter("site", siteCode), new NpgsqlParameter("last", lastDate), new NpgsqlParameter("max", MaxCalendarRows)],
            r => (From: r.GetString(0), Hours: r.GetString(1), Recorded: Utc(r.GetDateTime(2)), Deleted: r.IsDBNull(3) ? (DateTime?)null : Utc(r.GetDateTime(3))), ct);
        var exceptionRows = await ReadAsync(connection, transaction, """
            SELECT date, hours, recorded_utc, deleted_on FROM operating_day_exception
             WHERE site_code = @site AND date >= @first AND date <= @last ORDER BY date LIMIT @max
            """, [new NpgsqlParameter("site", siteCode), new NpgsqlParameter("first", firstDate), new NpgsqlParameter("last", lastDate),
                new NpgsqlParameter("max", MaxCalendarRows)],
            r => (Date: r.GetString(0), Hours: r.GetString(1), Recorded: Utc(r.GetDateTime(2)), Deleted: r.IsDBNull(3) ? (DateTime?)null : Utc(r.GetDateTime(3))), ct);
        var windowRows = await ReadAsync(connection, transaction, """
            SELECT starts_utc, ends_utc, recorded_utc, deleted_on FROM maintenance_window
             WHERE site_code = @site AND starts_utc <= @to AND ends_utc > @from ORDER BY starts_utc LIMIT @max
            """, [new NpgsqlParameter("site", siteCode), new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from },
                new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to }, new NpgsqlParameter("max", MaxCalendarRows)],
            r => new CalendarMaintenance(Utc(r.GetDateTime(0)), Utc(r.GetDateTime(1)), Utc(r.GetDateTime(2)), r.IsDBNull(3) ? null : Utc(r.GetDateTime(3))), ct);

        var weeks = new List<CalendarWeek>();
        foreach (var row in weekRows)
        {
            if (CalendarDates.TryParse(row.From, out var date) && WeeklyHours.TryFromJson(row.Hours) is { } hours)
                weeks.Add(new CalendarWeek(date, row.Recorded, row.Deleted, hours));
            else
                logger.LogWarning("Availability ledger of site {Site}: a stored weekly version is not valid and is left out", siteCode);
        }

        var exceptions = new List<CalendarException>();
        foreach (var row in exceptionRows)
        {
            if (CalendarDates.TryParse(row.Date, out var date) && WeeklyHours.TryDayFromJson(row.Hours) is { } hours)
                exceptions.Add(new CalendarException(date, row.Recorded, row.Deleted, hours));
            else
                logger.LogWarning("Availability ledger of site {Site}: a stored exception is not valid and is left out", siteCode);
        }

        return new OperatingCalendar(zone, weeks, exceptions, windowRows);
    }

    #endregion

    #region Writes

    private sealed record LedgerRow(DateTime MinuteUtc, CalendarMinute Calendar, AvailabilityDecision Decision, int? ProfileVersion);

    /// <summary>Inserts the rows not yet recorded and returns those inserted (the first decision of a minute stands).</summary>
    private static async Task<List<LedgerRow>> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode, List<LedgerRow> rows, DateTime now,
        CancellationToken ct)
    {
        await Execute(connection, transaction, "CREATE TEMP TABLE stage_availability_minute (LIKE availability_minute INCLUDING DEFAULTS) ON COMMIT DROP", ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY stage_availability_minute (site_code, minute_utc, local_date, calendar, state, reasons, zones_expected, zones_stale, zones_missing,
                zones_lagging, profile_version, rule_version, decided_utc) FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var row in rows)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(siteCode, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(row.MinuteUtc, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(row.Calendar.LocalDate, NpgsqlDbType.Date, ct);
                await copy.WriteAsync(row.Calendar.State.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(row.Decision.State.ToString(), NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(row.Decision.Reasons.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await copy.WriteAsync(Small(row.Decision.ZonesExpected), NpgsqlDbType.Smallint, ct);
                await copy.WriteAsync(Small(row.Decision.ZonesStale), NpgsqlDbType.Smallint, ct);
                await copy.WriteAsync(Small(row.Decision.ZonesMissing), NpgsqlDbType.Smallint, ct);
                await copy.WriteAsync(Small(row.Decision.ZonesLagging), NpgsqlDbType.Smallint, ct);
                if (row.ProfileVersion is { } version)
                    await copy.WriteAsync(version, NpgsqlDbType.Integer, ct);
                else
                    await copy.WriteNullAsync(ct);
                await copy.WriteAsync((short)AvailabilityRule.Version, NpgsqlDbType.Smallint, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        var inserted = await ReadAsync(connection, transaction, """
            INSERT INTO availability_minute SELECT * FROM stage_availability_minute ON CONFLICT (site_code, minute_utc) DO NOTHING RETURNING minute_utc
            """, [], r => Utc(r.GetDateTime(0)), ct);
        var set = inserted.ToHashSet();
        return [.. rows.Where(r => set.Contains(r.MinuteUtc))];
    }

    private static async Task SaveCursorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteCode, DateTime next, DateTime now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO availability_cursor (site_code, next_minute_utc, updated_on) VALUES (@site, @next, @now)
            ON CONFLICT (site_code) DO UPDATE SET next_minute_utc = GREATEST(availability_cursor.next_minute_utc, EXCLUDED.next_minute_utc), updated_on = EXCLUDED.updated_on
            """, connection, transaction);
        command.Parameters.AddWithValue("site", siteCode);
        command.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = next });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        await command.ExecuteNonQueryAsync(ct);
    }

    #endregion

    #region Helpers

    private static short Small(int value) => (short)Math.Clamp(value, 0, short.MaxValue);

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Every minute of [from, to] (empty when to is before from).</summary>
    private static List<DateTime> Minutes(DateTime from, DateTime to)
    {
        var minutes = new List<DateTime>();
        for (var m = from; m <= to; m = m.AddMinutes(1))
            minutes.Add(m);
        return minutes;
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

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

    #endregion
}
