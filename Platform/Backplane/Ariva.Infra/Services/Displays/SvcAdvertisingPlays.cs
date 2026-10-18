using System.Diagnostics.CodeAnalysis;
using Ariva.Core.Advertising;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Settings;
using NHibernate.Linq;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The proof of play (ARV-144). A batch comes from an authenticated display (its credential's prefix must still be the
/// display's) and holds 1 to 100 plays. The display must be able to play advertising now, as its board would (split, set to
/// advertising, the site's policy allowing it for every zone it shows, ARV-145): otherwise every play is refused. Then each
/// play is checked on its own and refused, never stored, unless
/// <list type="bullet">
/// <item>it is possible (<see cref="AdvertisingPlays.Problem"/>: started within the last day, ended, 1 to 125 seconds long);</item>
/// <item>its advertisement is on the playlist that applied to that display when it started, by the plans as they are now
/// (<see cref="AdvertisingPlays.ScheduledItem"/>), and may play on the site's local day it started (approved, within its dates);</item>
/// <item>it lasted no longer than that item's slot and counts as completed only when it filled it (<see cref="AdvertisingPlays.SlotProblem"/>);</item>
/// <item>it overlaps no other play of the display, in the batch or stored (one advertising region shows one item at a time,
/// <see cref="AdvertisingPlays.Overlap"/>).</item>
/// </list>
/// A play the server already has (the same display, advertisement and start) counts as a duplicate. The stored plays are read,
/// the duplicates and overlaps decided and the plays taken written in one transaction under the display's advisory lock
/// (class 1441, the display id's hash), so two batches of one display sent at once wait for each other and cannot both claim
/// the same airtime. Only the stored plays within each candidate's reach are read (its start, less a play's longest length,
/// to its end; the reaches merged). The plays taken go to <c>advertising_play</c> (script 0062) by binary COPY into a staging
/// table and INSERT ... ON CONFLICT DO NOTHING. A batch with refused plays is logged as a security event (the display, its site
/// and the counts, never the body).
/// </summary>
internal sealed class SvcAdvertisingPlays(
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ReportReader sites,
    AdvertisingPolicyGuard advertising,
    DatabaseSettings database,
    ILogger<SvcAdvertisingPlays> logger) : ISvcAdvertisingPlays
{
    public static readonly EventId PlaysRefused = new(9126, "SecurityEvent.DisplayPlaysRefused");

    /// <summary>The stored plays a batch is checked against for overlaps: from a play's longest length before its first start.</summary>
    private static readonly TimeSpan OverlapLookBack = TimeSpan.FromMilliseconds(AdvertisingPlays.MaxDurationMilliseconds);

    public async Task<Result<AdvertisingPlaysViewModel>> RecordAsync(Guid displayId, string credentialPrefix, AdvertisingPlaysRequest request,
        CancellationToken ct = default)
    {
        if (request?.Plays is not { Count: > 0 and <= AdvertisingPlays.MaxBatch } plays)
            return Result.Error<AdvertisingPlaysViewModel>(AdvertisingPlays.TooMany);
        var display = await unitOfWork.StorageProvider.Query<Display>()
            .Where(d => d.Id == displayId && d.DeletedOn == null && d.Enabled && d.CredentialPrefix == credentialPrefix)
            .FirstOrDefaultAsync(ct);
        if (display is null)
            return Result.Error<AdvertisingPlaysViewModel>(TopologyErrors.NotFound);

        var zones = display.EntryList.Select(e => e.Zone).ToList();
        // As the board: a display that cannot play advertising now played none (ARV-145; a plan is never itself permission).
        if (display.Layout != DisplayLayout.Split || display.SecondRegion != DisplaySecondRegion.Advertising ||
            !await advertising.AllowsAsync(display.SiteCode, zones, ct))
            return Answer(display, 0, 0, plays.Count);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var possible = plays
            // Whole microseconds, as the database keeps them, so a play sent again is the same key (its kind kept for the UTC check).
            .Select(p => p is null
                ? null
                : new AdvertisingPlayReport(p.MediaId, new DateTime(p.StartedUtc.Ticks - (p.StartedUtc.Ticks % 10), p.StartedUtc.Kind), p.DurationMilliseconds, p.Completed))
            .Where(r => AdvertisingPlays.Problem(r, now) is null)
            .OrderBy(r => r.StartedUtc)
            .ToList();
        var candidates = possible.Count == 0 ? [] : await ScheduledAsync(display, zones, possible, ct);
        var (inserted, duplicates) = candidates.Count == 0 ? (0, 0) : await WriteAsync(display.Id.GetValueOrDefault(), candidates, now, ct);
        return Answer(display, inserted, duplicates, plays.Count - inserted - duplicates);
    }

    /// <summary>The plays whose advertisement was scheduled on the display when they started, could play that day, and kept its slot.</summary>
    private async Task<List<AdvertisingPlay>> ScheduledAsync(Display display, IReadOnlyList<string> zones, IReadOnlyList<AdvertisingPlayReport> plays, CancellationToken ct)
    {
        var schedules = await unitOfWork.StorageProvider.Query<AdvertisingSchedule>()
            .Where(s => s.SiteCode == display.SiteCode && s.Status == InstructionStatus.Active)
            .OrderBy(s => s.CreatedOn).ThenBy(s => s.Id)
            .Take(AdvertisingSchedule.MaxSchedulesPerSite)
            .ToListAsync(ct);
        var ids = schedules.Select(s => s.PlaylistId).Distinct().ToList();
        var playlists = ids.Count == 0
            ? new Dictionary<Guid, AdvertisingPlaylist>()
            : (await unitOfWork.StorageProvider.Query<AdvertisingPlaylist>()
                .Where(p => p.SiteCode == display.SiteCode && p.Status == InstructionStatus.Active && ids.Contains(p.Id.Value))
                .ToListAsync(ct))
            .ToDictionary(p => p.Id.GetValueOrDefault());
        // The display's candidates read once for the whole batch: each play is then two picks among them.
        var prepared = AdvertisingSchedules.Prepare(schedules, playlists.Keys.ToHashSet(), display.SiteCode, display.Code, zones);
        var mediaIds = plays.Select(p => p.MediaId).Distinct().ToList();
        var media = (await unitOfWork.StorageProvider.Query<AdvertisingMedia>()
                .Where(m => m.SiteCode == display.SiteCode && mediaIds.Contains(m.Id.Value))
                .ToListAsync(ct))
            .ToDictionary(m => m.Id.GetValueOrDefault());
        var zone = await sites.TimeZoneAsync(display.SiteCode, ct);

        var scheduled = new List<AdvertisingPlay>();
        foreach (var play in plays)
        {
            if (AdvertisingPlays.ScheduledItem(play, prepared, playlists, zone) is not var (playlist, _) ||
                !media.TryGetValue(play.MediaId, out var advertisement) ||
                !advertisement.CanPlayOn(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(play.StartedUtc, zone))))
                continue;
            scheduled.Add(new AdvertisingPlay(play.StartedUtc, display.SiteCode, display.Id.GetValueOrDefault(), play.MediaId, playlist, play.DurationMilliseconds,
                play.Completed));
        }

        return scheduled;
    }

    private Result<AdvertisingPlaysViewModel> Answer(Display display, int accepted, int duplicates, int refused)
    {
        if (refused > 0)
            logger.LogWarning(PlaysRefused, "Display {DisplayId} of site {SiteCode} reported plays the server refused: {Refused} refused, {Accepted} taken",
                display.Id, display.SiteCode, refused, accepted);
        return new Result<AdvertisingPlaysViewModel>(new AdvertisingPlaysViewModel(accepted, duplicates, refused));
    }

    /// <summary>
    /// One transaction under the display's lock: the stored plays near the batch read as committed, each candidate (in start
    /// order) a duplicate when the server has it, refused when it overlaps a stored play or one taken before it, and the rest
    /// written (COPY into a staging table, then INSERT ... ON CONFLICT DO NOTHING). The plays new and the duplicates.
    /// </summary>
    private async Task<(int Inserted, int Duplicates)> WriteAsync(Guid displayId, IReadOnlyList<AdvertisingPlay> candidates, DateTime now, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var locking = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1441, hashtext(CAST(@display AS text)))", connection, transaction))
        {
            locking.Parameters.AddWithValue("display", displayId);
            await locking.ExecuteNonQueryAsync(ct);
        }

        var stored = await StoredAsync(connection, transaction, displayId, candidates, ct);
        var storedKeys = stored.Select(p => (p.MediaId, p.StartedUtc)).ToHashSet();
        var acceptedKeys = new HashSet<(Guid, DateTime)>();
        var accepted = new List<AdvertisingPlay>();
        var duplicates = 0;
        foreach (var play in candidates)
        {
            var key = (play.MediaId, play.StartedUtc);
            if (storedKeys.Contains(key) || acceptedKeys.Contains(key))
            {
                duplicates++;
                continue;
            }

            if (AdvertisingPlays.OverlapsAny(play.StartedUtc, play.DurationMilliseconds, Near(stored, play)) ||
                AdvertisingPlays.OverlapsAny(play.StartedUtc, play.DurationMilliseconds, accepted.Select(p => (p.StartedUtc, p.DurationMilliseconds))))
                continue;
            acceptedKeys.Add(key);
            accepted.Add(play);
        }

        var inserted = accepted.Count == 0 ? 0 : await InsertAsync(connection, transaction, accepted, now, ct);
        await transaction.CommitAsync(ct);
        return (inserted, duplicates + accepted.Count - inserted);
    }

    /// <summary>
    /// The display's stored plays that a candidate could overlap (started from its start less a play's longest length to its
    /// end), in start order: one indexed read per stretch of candidates whose reaches meet (<c>ix_advertising_play_display</c>).
    /// </summary>
    private static async Task<List<(Guid MediaId, DateTime StartedUtc, int DurationMilliseconds)>> StoredAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid displayId, IReadOnlyList<AdvertisingPlay> candidates, CancellationToken ct)
    {
        var reaches = new List<(DateTime From, DateTime To)>();
        foreach (var play in candidates.OrderBy(p => p.StartedUtc))
        {
            var (from, to) = (play.StartedUtc - OverlapLookBack, play.StartedUtc.AddMilliseconds(play.DurationMilliseconds));
            if (reaches.Count > 0 && from <= reaches[^1].To)
                reaches[^1] = (reaches[^1].From, to > reaches[^1].To ? to : reaches[^1].To);
            else
                reaches.Add((from, to));
        }

        var stored = new List<(Guid MediaId, DateTime StartedUtc, int DurationMilliseconds)>();
        foreach (var (from, to) in reaches)
        {
            await using var read = new NpgsqlCommand("""
                SELECT media_id, started_utc, duration_ms FROM advertising_play
                 WHERE display_id = @display AND started_utc >= @from AND started_utc <= @to
                 ORDER BY started_utc
                """, connection, transaction);
            read.Parameters.AddWithValue("display", displayId);
            read.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from);
            read.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                stored.Add((reader.GetGuid(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), reader.GetInt32(2)));
        }

        return stored;
    }

    /// <summary>The stored plays (in start order) that started within a play's longest length before the candidate and up to its end.</summary>
    private static IEnumerable<(DateTime StartedUtc, int DurationMilliseconds)> Near(List<(Guid MediaId, DateTime StartedUtc, int DurationMilliseconds)> stored,
        AdvertisingPlay play)
    {
        var from = play.StartedUtc - OverlapLookBack;
        var to = play.StartedUtc.AddMilliseconds(play.DurationMilliseconds);
        var (low, high) = (0, stored.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (stored[middle].StartedUtc < from)
                low = middle + 1;
            else
                high = middle;
        }

        for (var i = low; i < stored.Count && stored[i].StartedUtc <= to; i++)
            yield return (stored[i].StartedUtc, stored[i].DurationMilliseconds);
    }

    /// <summary>Writes the plays taken (COPY into a staging table, then INSERT ... ON CONFLICT DO NOTHING); the number new.</summary>
    private static async Task<int> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<AdvertisingPlay> plays, DateTime now,
        CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "CREATE TEMP TABLE stage_advertising_play (LIKE advertising_play INCLUDING DEFAULTS) ON COMMIT DROP", ct);
        await using (var copy = await connection.BeginBinaryImportAsync("""
                         COPY stage_advertising_play (started_utc, site_code, display_id, media_id, playlist_id, duration_ms, completed, received_utc)
                         FROM STDIN (FORMAT BINARY)
                         """, ct))
        {
            foreach (var play in plays)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(play.StartedUtc, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(play.SiteCode, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(play.DisplayId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(play.MediaId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(play.PlaylistId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(play.DurationMilliseconds, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(play.Completed, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            }

            await copy.CompleteAsync(ct);
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO advertising_play SELECT * FROM stage_advertising_play ON CONFLICT (display_id, media_id, started_utc) DO NOTHING
            """, connection, transaction);
        return await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, [ConstantExpected] string sql, CancellationToken ct)
    {
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(ct);
    }
}
