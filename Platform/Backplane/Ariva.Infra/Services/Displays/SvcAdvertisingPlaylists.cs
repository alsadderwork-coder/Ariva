using Ariva.Core.Domain.Enums;
using Ariva.Core.Security;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Services.Reports;
using Microsoft.Extensions.Options;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The advertising playlists (ARV-143a). Every call names a site of the caller (another answers like a site that does not
/// exist) and every playlist is looked up by its id and that site together, so another site's playlist answers 404
/// (CWE-863, IDOR). Each item must be the site's own: an advertisement of its library that is not retired, or an active
/// instruction slide; a video's slot is its stored length rounded up, at most the installation's video length
/// (<see cref="AdvertisingVideoSettings.MaxSeconds"/>), an image's or a slide's 5 to 60 seconds. Creation takes a lock
/// on the site's playlists (class 1431) so the cap of 100 holds; a change locks the playlist's row first. Every change is
/// audited, and a playlist is retired rather than deleted.
/// </summary>
internal sealed class SvcAdvertisingPlaylists(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    AuditTrail audit,
    ReportReader sites,
    IOptions<AdvertisingVideoSettings> videoSettings)
    : SvcBase(unitOfWork, currentUser, timeProvider), ISvcAdvertisingPlaylists
{
    private const string Target = "AdvertisingPlaylist";

    public async Task<Result<IReadOnlyList<AdvertisingPlaylistViewModel>>> ListAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<IReadOnlyList<AdvertisingPlaylistViewModel>>(TopologyErrors.NotFound);
        var playlists = await QueryAsNoTracking<AdvertisingPlaylist>()
            .Where(p => p.SiteCode == siteCode)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.CreatedOn)
            .Take(AdvertisingPlaylist.MaxPlaylistsPerSite)
            .ToListAsync(ct);
        return new Result<IReadOnlyList<AdvertisingPlaylistViewModel>>(await ViewsAsync(siteCode, playlists, ct));
    }

    public async Task<Result<AdvertisingPlaylistViewModel>> GetAsync(string siteCode, Guid id, CancellationToken ct = default)
    {
        var playlist = await FindAsync(siteCode, id, ct);
        return playlist is null
            ? Result.Error<AdvertisingPlaylistViewModel>(TopologyErrors.NotFound)
            : new Result<AdvertisingPlaylistViewModel>((await ViewsAsync(siteCode, [playlist], ct))[0]);
    }

    public async Task<Result<AdvertisingPlaylistViewModel>> CreateAsync(string siteCode, AdvertisingPlaylistRequest request, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingPlaylistViewModel>(TopologyErrors.NotFound);
        var (values, problems) = await CheckAsync(siteCode, request, ct);
        if (problems.Count > 0)
            return Result.Error<AdvertisingPlaylistViewModel>(problems);
        // One creation per site at a time, to the commit: the cap holds.
        await LockSiteAsync(siteCode, ct);
        if (await QueryAsNoTracking<AdvertisingPlaylist>().CountAsync(p => p.SiteCode == siteCode, ct) >= AdvertisingPlaylist.MaxPlaylistsPerSite)
            return Result.Error<AdvertisingPlaylistViewModel>(AdvertisingPlanErrors.TooManyPlaylists);

        var playlist = new AdvertisingPlaylist(siteCode, values);
        await SaveAsync(playlist, ct);
        await audit.RecordAsync($"{Target}.Created", Target, playlist.Id, siteCode, null, playlist.AuditSummary(), ct);
        return new Result<AdvertisingPlaylistViewModel>((await ViewsAsync(siteCode, [playlist], ct))[0]);
    }

    public async Task<Result<AdvertisingPlaylistViewModel>> UpdateAsync(string siteCode, Guid id, AdvertisingPlaylistRequest request, CancellationToken ct = default)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingPlaylistViewModel>(TopologyErrors.NotFound);
        var (values, problems) = await CheckAsync(siteCode, request, ct);
        var playlist = await LockAsync(siteCode, id, ct);
        if (playlist is null)
            return Result.Error<AdvertisingPlaylistViewModel>(TopologyErrors.NotFound);
        if (problems.Count > 0)
            return Result.Error<AdvertisingPlaylistViewModel>(problems);

        var before = playlist.AuditSummary();
        playlist.Set(values);
        await UpdateAsync(playlist, ct);
        await audit.RecordAsync($"{Target}.Updated", Target, playlist.Id, siteCode, before, playlist.AuditSummary(), ct);
        return new Result<AdvertisingPlaylistViewModel>((await ViewsAsync(siteCode, [playlist], ct))[0]);
    }

    public Task<Result<AdvertisingPlaylistViewModel>> RetireAsync(string siteCode, Guid id, CancellationToken ct = default) =>
        ChangeStatusAsync(siteCode, id, InstructionStatus.Retired, "Retired", ct);

    public Task<Result<AdvertisingPlaylistViewModel>> RestoreAsync(string siteCode, Guid id, CancellationToken ct = default) =>
        ChangeStatusAsync(siteCode, id, InstructionStatus.Active, "Restored", ct);

    private async Task<Result<AdvertisingPlaylistViewModel>> ChangeStatusAsync(string siteCode, Guid id, InstructionStatus status, string action, CancellationToken ct)
    {
        if (!await VisibleSiteAsync(siteCode, ct))
            return Result.Error<AdvertisingPlaylistViewModel>(TopologyErrors.NotFound);
        var playlist = await LockAsync(siteCode, id, ct);
        if (playlist is null)
            return Result.Error<AdvertisingPlaylistViewModel>(TopologyErrors.NotFound);
        if (playlist.Status != status)
        {
            var before = playlist.AuditSummary();
            if (status == InstructionStatus.Retired)
                playlist.Retire();
            else
                playlist.Restore();
            await UpdateAsync(playlist, ct);
            await audit.RecordAsync($"{Target}.{action}", Target, playlist.Id, siteCode, before, playlist.AuditSummary(), ct);
        }

        return new Result<AdvertisingPlaylistViewModel>((await ViewsAsync(siteCode, [playlist], ct))[0]);
    }

    /// <summary>
    /// The playlist's values with each item checked against the site: the kind by its exact name, the advertisement or slide
    /// the site's own (an advertisement not retired, an active slide), each named once, and its slot (a video's own length).
    /// </summary>
    private async Task<(AdvertisingPlaylistValues Values, IReadOnlyList<string> Problems)> CheckAsync(string siteCode, AdvertisingPlaylistRequest request,
        CancellationToken ct)
    {
        if (request?.Items is null || request.Items.Any(i => i is null))
            return (null, [AdvertisingPlanErrors.Required]);
        // The count before any query: the model's limit holds it at the endpoint, and this holds it for every other caller.
        if (request.Items.Count is 0 or > AdvertisingPlaylist.MaxItems)
            return (null, [$"A playlist has 1 to {AdvertisingPlaylist.MaxItems} items."]);
        var kinds = new List<AdvertisingItemKind>();
        foreach (var item in request.Items)
        {
            if (!Exactly<AdvertisingItemKind>(item.Kind, out var kind))
                return (null, [AdvertisingPlanErrors.UnknownKind]);
            kinds.Add(kind);
        }

        var mediaIds = request.Items.Where((_, i) => kinds[i] == AdvertisingItemKind.Media).Select(i => i.Id).Distinct().ToList();
        var slideIds = request.Items.Where((_, i) => kinds[i] == AdvertisingItemKind.Slide).Select(i => i.Id).Distinct().ToList();
        var media = mediaIds.Count == 0
            ? []
            : await QueryAsNoTracking<AdvertisingMedia>()
                .Where(m => m.SiteCode == siteCode && m.Status != AdvertisingMediaStatus.Retired && mediaIds.Contains(m.Id.Value))
                .ToListAsync(ct);
        var slides = slideIds.Count == 0
            ? 0
            : await QueryAsNoTracking<InstructionSlide>()
                .CountAsync(s => s.SiteCode == siteCode && s.Status == InstructionStatus.Active && slideIds.Contains(s.Id.Value), ct);
        var named = request.Items.Select((i, n) => (kinds[n], i.Id)).Distinct().Count();
        if (media.Count != mediaIds.Count || slides != slideIds.Count || named != request.Items.Count)
            return (null, [AdvertisingPlanErrors.UnknownItems]);

        var videos = media.Where(m => m.DurationMilliseconds is not null).ToDictionary(m => m.Id.GetValueOrDefault(), m => m.DurationMilliseconds.Value);
        // The length an installation takes now: a video kept under a longer setting plays for this long at most.
        var longest = Math.Min(AdvertisingMedia.MaxVideoSeconds, videoSettings.Value.MaxSeconds);
        var items = new List<AdvertisingPlaylistItem>();
        for (var n = 0; n < request.Items.Count; n++)
        {
            var item = request.Items[n];
            var weight = item.Weight ?? 1;
            if (kinds[n] == AdvertisingItemKind.Media && videos.TryGetValue(item.Id, out var milliseconds))
            {
                items.Add(new AdvertisingPlaylistItem(kinds[n], item.Id, (int)Math.Min(longest, Math.Ceiling(milliseconds / 1000d)), weight));
                continue;
            }

            if (item.Seconds is not { } seconds || seconds is < AdvertisingPlaylist.MinSeconds or > AdvertisingPlaylist.MaxSeconds)
                return (null, [AdvertisingPlanErrors.ImageSeconds]);
            items.Add(new AdvertisingPlaylistItem(kinds[n], item.Id, seconds, weight));
        }

        var values = new AdvertisingPlaylistValues(request.Name, items);
        return (values, values.Problems());
    }

    private async Task<IReadOnlyList<AdvertisingPlaylistViewModel>> ViewsAsync(string siteCode, IReadOnlyList<AdvertisingPlaylist> playlists, CancellationToken ct)
    {
        var items = playlists.SelectMany(p => p.ItemList).ToList();
        var mediaIds = items.Where(i => i.Kind == AdvertisingItemKind.Media).Select(i => i.Id).Distinct().ToList();
        var slideIds = items.Where(i => i.Kind == AdvertisingItemKind.Slide).Select(i => i.Id).Distinct().ToList();
        var media = mediaIds.Count == 0
            ? new Dictionary<Guid, AdvertisingMedia>()
            : (await QueryAsNoTracking<AdvertisingMedia>().Where(m => m.SiteCode == siteCode && mediaIds.Contains(m.Id.Value)).ToListAsync(ct))
            .ToDictionary(m => m.Id.GetValueOrDefault());
        var slides = slideIds.Count == 0
            ? new Dictionary<Guid, InstructionSlide>()
            : (await QueryAsNoTracking<InstructionSlide>().Where(s => s.SiteCode == siteCode && slideIds.Contains(s.Id.Value)).ToListAsync(ct))
            .ToDictionary(s => s.Id.GetValueOrDefault());
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, await sites.TimeZoneAsync(siteCode, ct)));

        return playlists.Select(p => new AdvertisingPlaylistViewModel(p.Id.GetValueOrDefault(), p.SiteCode, p.Name, p.Status.ToString(),
            p.ItemList.Select(i => ItemView(i, media, slides, today)).ToList(), p.ItemList.Sum(i => i.Seconds), p.CreatedOn, p.ModifiedOn)).ToList();
    }

    private static AdvertisingPlaylistItemViewModel ItemView(AdvertisingPlaylistItem item, IReadOnlyDictionary<Guid, AdvertisingMedia> media,
        IReadOnlyDictionary<Guid, InstructionSlide> slides, DateOnly today)
    {
        if (item.Kind == AdvertisingItemKind.Media)
        {
            var found = media.GetValueOrDefault(item.Id);
            return new AdvertisingPlaylistItemViewModel(item.Kind.ToString(), item.Id, item.Seconds, item.Weight,
                found is null ? null : $"{found.Advertiser}: {found.Campaign}", found?.CanPlayOn(today) ?? false, found?.Status.ToString(), found?.ContentType);
        }

        var slide = slides.GetValueOrDefault(item.Id);
        var title = slide?.TextMap.OrderBy(t => Array.IndexOf(Display.SupportedLanguages.ToArray(), t.Key)).Select(t => t.Value.Title).FirstOrDefault();
        return new AdvertisingPlaylistItemViewModel(item.Kind.ToString(), item.Id, item.Seconds, item.Weight, title, slide?.IsActive ?? false, null, null);
    }

    private async Task<AdvertisingPlaylist> FindAsync(string siteCode, Guid id, CancellationToken ct) =>
        id == Guid.Empty || !await VisibleSiteAsync(siteCode, ct)
            ? null
            : await QueryAsNoTracking<AdvertisingPlaylist>().Where(p => p.Id == id && p.SiteCode == siteCode).FirstOrDefaultAsync(ct);

    /// <summary>The playlist of this site, read after its row is locked for the rest of the transaction; null when there is none.</summary>
    private async Task<AdvertisingPlaylist> LockAsync(string siteCode, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return null;
        var locked = await ExecuteCommandAsync<LockRow>(
            """SELECT 1 AS "Value" FROM advertising_playlist WHERE id = :id AND site_code = :site FOR UPDATE""",
            new Dictionary<string, object> { ["id"] = id, ["site"] = siteCode }, ct);
        return locked.Count == 0 ? null : await Query<AdvertisingPlaylist>().Where(p => p.Id == id && p.SiteCode == siteCode).FirstOrDefaultAsync(ct);
    }

    /// <summary>A transaction-scoped lock on the site's playlists (class 1431, the site code's hash), released at the commit.</summary>
    private async Task LockSiteAsync(string siteCode, CancellationToken ct)
    {
        await FlushAsync(ct);
        await ExecuteCommandAsync<LockRow>("""SELECT 1 AS "Value" FROM (SELECT pg_advisory_xact_lock(1431, hashtext(:site))) l""",
            new Dictionary<string, object> { ["site"] = siteCode }, ct);
    }

    private async Task<bool> VisibleSiteAsync(string siteCode, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(siteCode) && (await siteScope.GetAsync(ct)).Allows(siteCode) &&
        await QueryAsNoTracking<Site>().AnyAsync(s => s.Code == siteCode, ct);

    /// <summary>An enum value by its exact name, as stored (never a number or another casing).</summary>
    internal static bool Exactly<TEnum>(string text, out TEnum value) where TEnum : struct, Enum =>
        Enum.TryParse(text, ignoreCase: false, out value) && string.Equals(value.ToString(), text, StringComparison.Ordinal) && Enum.IsDefined(value);

    private sealed class LockRow
    {
        public int Value { get; set; }
    }
}
