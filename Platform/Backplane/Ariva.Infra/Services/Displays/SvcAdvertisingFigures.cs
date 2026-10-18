using System.Globalization;
using Ariva.Core.Advertising;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Settings;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The proof of play figures (ARV-144), within the caller's sites and for a caller whose roles view advertising
/// (<c>Advertising.View</c>); another site, or a caller without it, answers like a site that does not exist (CWE-204, CWE-863).
/// No endpoint carries this yet (ARV-146 adds one with its own permission), so the service checks the caller itself (fail
/// closed). Per local day of the site, display and advertisement: the plays stored (<c>advertising_play</c>, script 0062),
/// those that played their whole slot and their airtime, against the airtime the plans scheduled (<see cref="AdvertisingAirtime"/>):
/// each display that plays advertising (split, set to advertising, enabled) over the day's stretches where a playlist applied,
/// with that day's playable items, by the plans as they are now. Then the same summed by campaign, advertiser, display and
/// day. 1 to 31 local days a read, none after the site's today and none older than the longest retention: at most 31 plays
/// queries, and one timeline over the range for each set of schedules the displays share (displays with the same candidate
/// schedules share it), at most a minute step per minute of the range.
/// </summary>
internal sealed class SvcAdvertisingFigures(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ISiteScope siteScope, CallerRoles callerRoles,
    AuthSettings authSettings, ReportReader sites)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAdvertisingFigures
{
    public const int MaxDays = 31;

    /// <summary>How far back figures reach: the longest a play is kept (<see cref="RetentionSettings.MaxDays"/>).</summary>
    public const int MaxBackDays = RetentionSettings.MaxDays;

    public async Task<Result<AdvertisingFiguresViewModel>> ReadAsync(string siteCode, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (siteCode is null || !Site.IsValidCode(siteCode) || !(await siteScope.GetAsync(ct)).Allows(siteCode) ||
            !await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == siteCode, ct))
            return Result.Error<AdvertisingFiguresViewModel>(TopologyErrors.NotFound);
        if (!RolePermissions.For(await RolesAsync(ct)).Contains(Ariva.Core.Global.Defaults.Permissions.ViewAdvertising))
            return Result.Error<AdvertisingFiguresViewModel>(TopologyErrors.NotFound);

        var zone = await sites.TimeZoneAsync(siteCode, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, zone));
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxDays || to > today || from < today.AddDays(-MaxBackDays))
            return Result.Error<AdvertisingFiguresViewModel>(AdvertisingFiguresErrors.Range);
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToList();
        var starts = days.Append(to.AddDays(1)).Select(d => AdvertisingSchedules.LocalMidnightUtc(d, zone)).ToList();
        var displays = await QueryAsNoTracking<Display>().Where(d => d.SiteCode == siteCode).ToListAsync(ct);
        var media = (await QueryAsNoTracking<AdvertisingMedia>().Where(m => m.SiteCode == siteCode).ToListAsync(ct)).ToDictionary(m => m.Id.GetValueOrDefault());
        var cells = new Dictionary<(DateOnly Day, Guid Display, Guid Media), Cell>();

        // The plays, per local day: what the displays reported and the server took.
        for (var i = 0; i < days.Count; i++)
        {
            var rows = await ExecuteSqlAsync<PlayRow>("""
                SELECT display_id AS "DisplayId", media_id AS "MediaId", CAST(count(*) AS integer) AS "Plays",
                       CAST(count(*) FILTER (WHERE completed) AS integer) AS "Completed", CAST(coalesce(sum(duration_ms), 0) AS bigint) AS "Milliseconds"
                  FROM advertising_play
                 WHERE site_code = :site AND started_utc >= :from AND started_utc < :to
                 GROUP BY display_id, media_id
                """, new Dictionary<string, object> { ["site"] = siteCode, ["from"] = starts[i], ["to"] = starts[i + 1] }, ct);
            foreach (var row in rows)
            {
                var cell = Of(cells, (days[i], row.DisplayId, row.MediaId));
                cell.Plays += row.Plays;
                cell.Completed += row.Completed;
                cell.Airtime += row.Milliseconds;
            }
        }

        // What the plans scheduled, per display that plays advertising and local day.
        var schedules = await QueryAsNoTracking<AdvertisingSchedule>()
            .Where(s => s.SiteCode == siteCode && s.Status == InstructionStatus.Active)
            .OrderBy(s => s.CreatedOn).ThenBy(s => s.Id)
            .Take(AdvertisingSchedule.MaxSchedulesPerSite)
            .ToListAsync(ct);
        var playlists = (await QueryAsNoTracking<AdvertisingPlaylist>().Where(p => p.SiteCode == siteCode && p.Status == InstructionStatus.Active).ToListAsync(ct))
            .ToDictionary(p => p.Id.GetValueOrDefault());
        var slides = (await QueryAsNoTracking<InstructionSlide>().Where(s => s.SiteCode == siteCode && s.Status == InstructionStatus.Active).ToListAsync(ct))
            .ToDictionary(s => s.Id.GetValueOrDefault());
        var active = playlists.Keys.ToHashSet();
        var timelines = new Dictionary<string, List<AdvertisingSchedules.Segment>>(StringComparer.Ordinal);
        foreach (var display in displays.Where(d => d.DeletedOn == null && d.Enabled && d.Layout == DisplayLayout.Split && d.SecondRegion == DisplaySecondRegion.Advertising))
        {
            ct.ThrowIfCancellationRequested();
            var prepared = AdvertisingSchedules.Prepare(schedules, active, siteCode, display.Code, display.EntryList.Select(e => e.Zone).ToList());
            if (!timelines.TryGetValue(prepared.Key, out var segments))
                timelines[prepared.Key] = segments = Segments(prepared, zone, starts[0], starts[^1], ct);
            var languages = display.LanguageList;
            for (var i = 0; i < days.Count; i++)
            {
                var day = days[i];
                var scheduled = AdvertisingAirtime.Scheduled(Clip(segments, starts[i], starts[i + 1]), id => Playable(id, day, languages, playlists, media, slides));
                foreach (var (mediaId, milliseconds) in scheduled)
                    Of(cells, (day, display.Id.GetValueOrDefault(), mediaId)).Scheduled += milliseconds;
            }
        }

        var codes = displays.ToDictionary(d => d.Id.GetValueOrDefault(), d => d.Code);
        var lines = cells
            .Select(c => new Line(c.Key.Day, codes.GetValueOrDefault(c.Key.Display, c.Key.Display.ToString("N")), media.GetValueOrDefault(c.Key.Media), c.Key.Media, c.Value))
            .OrderBy(l => l.Day).ThenBy(l => l.Code, StringComparer.Ordinal).ThenBy(l => l.Media?.Advertiser, StringComparer.Ordinal).ThenBy(l => l.MediaId)
            .ToList();
        var rowsOut = lines.Select(l => new AdvertisingFigureRowViewModel(l.Day, l.Code, l.MediaId, l.Media?.Advertiser, l.Media?.Campaign, l.Cell.Plays, l.Cell.Completed,
            Seconds(l.Cell.Airtime), Seconds(l.Cell.Scheduled), AdvertisingAirtime.Delivered(l.Cell.Airtime, l.Cell.Scheduled))).ToList();

        return new Result<AdvertisingFiguresViewModel>(new AdvertisingFiguresViewModel(siteCode, from, to, zone.Id,
            Sum(lines, l => l.Media is null ? l.MediaId.ToString("N") : $"{l.Media.Advertiser}: {l.Media.Campaign}"),
            Sum(lines, l => l.Media?.Advertiser ?? l.MediaId.ToString("N")),
            Sum(lines, l => l.Code),
            Sum(lines, l => l.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            rowsOut));
    }

    /// <summary>A timeline over the range, in pieces of at most 14 days (the timeline's longest), none longer than what is left.</summary>
    private static List<AdvertisingSchedules.Segment> Segments(AdvertisingSchedules.Prepared prepared, TimeZoneInfo zone, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct)
    {
        var segments = new List<AdvertisingSchedules.Segment>();
        for (var start = fromUtc; start < toUtc; start = start.AddDays(14))
        {
            ct.ThrowIfCancellationRequested();
            var days = (int)Math.Min(14, Math.Ceiling((toUtc - start).TotalDays));
            segments.AddRange(Clip(prepared.Timeline(zone, start, days), start, toUtc));
        }

        return segments;
    }

    /// <summary>The parts of the stretches between two instants (a local day is 23 to 25 hours across daylight saving).</summary>
    private static IEnumerable<AdvertisingSchedules.Segment> Clip(IEnumerable<AdvertisingSchedules.Segment> segments, DateTime fromUtc, DateTime toUtc) =>
        segments.Where(s => s.ToUtc > fromUtc && s.FromUtc < toUtc)
            .Select(s => s with { FromUtc = s.FromUtc < fromUtc ? fromUtc : s.FromUtc, ToUtc = s.ToUtc > toUtc ? toUtc : s.ToUtc });

    /// <summary>A playlist's items that could play on the day: advertisements that may play then, active slides in the display's languages.</summary>
    private static IReadOnlyList<AdvertisingPlaylistItem> Playable(Guid playlistId, DateOnly day, IReadOnlyList<string> languages,
        IReadOnlyDictionary<Guid, AdvertisingPlaylist> playlists, IReadOnlyDictionary<Guid, AdvertisingMedia> media, IReadOnlyDictionary<Guid, InstructionSlide> slides) =>
        playlists.TryGetValue(playlistId, out var playlist)
            ? playlist.ItemList.Where(i => i.Kind == AdvertisingItemKind.Media
                    ? media.TryGetValue(i.Id, out var advertisement) && advertisement.CanPlayOn(day)
                    : slides.TryGetValue(i.Id, out var slide) && languages.Any(slide.TextMap.ContainsKey))
                .ToList()
            : [];

    private async Task<IReadOnlyList<string>> RolesAsync(CancellationToken ct)
    {
        var id = CurrentUser.Id;
        if (!id.HasValue)
            return [];
        var user = await GetAsync<User>(id.Value, ct);
        if (user is null || user.IsDisabled || user.IsPending(authSettings.TotpRequired))
            return [];
        return await callerRoles.GetAsync(ct);
    }

    private static IReadOnlyList<AdvertisingFigureViewModel> Sum(IEnumerable<Line> lines, Func<Line, string> key) =>
        lines.GroupBy(key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var airtime = g.Sum(l => l.Cell.Airtime);
                var scheduled = g.Sum(l => l.Cell.Scheduled);
                return new AdvertisingFigureViewModel(g.Key, g.Sum(l => l.Cell.Plays), g.Sum(l => l.Cell.Completed), Seconds(airtime), Seconds(scheduled),
                    AdvertisingAirtime.Delivered(airtime, scheduled));
            })
            .ToList();

    private static double Seconds(double milliseconds) => Math.Round(milliseconds / 1000, 1);

    private static Cell Of(Dictionary<(DateOnly, Guid, Guid), Cell> cells, (DateOnly, Guid, Guid) key)
    {
        if (!cells.TryGetValue(key, out var cell))
            cells[key] = cell = new Cell();
        return cell;
    }

    private sealed record Line(DateOnly Day, string Code, AdvertisingMedia Media, Guid MediaId, Cell Cell);

    private sealed class Cell
    {
        public int Plays { get; set; }
        public int Completed { get; set; }
        public double Airtime { get; set; }
        public double Scheduled { get; set; }
    }

    private sealed class PlayRow
    {
        public Guid DisplayId { get; set; }
        public Guid MediaId { get; set; }
        public int Plays { get; set; }
        public int Completed { get; set; }
        public long Milliseconds { get; set; }
    }
}
