using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Live;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Reports;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The board of a display for its player (ARV-058). <see cref="FindAsync"/> finds the live, enabled display by its code and
/// the credential's prefix and compares the presented credential with the stored hash in constant time (a miss costs the
/// same hash); a deleted, disabled or unknown display and a wrong credential are the same miss. The board carries each
/// entry's queue zone's latest live snapshot (Redis, as the live hub reads it) with its age by this server's clock; the
/// player turns it into a band, keeps it within the hysteresis and shows the neutral message once it is older than the
/// display's stale threshold. Nothing but the board's own labels, messages and numbers goes out. The second region is the
/// one the site's advertising policy allows now (ARV-145): a display set to advertising shows instructions where it does not.
/// Where it does, the board carries the playlist that applies to the display now (ARV-143b), with its playable items only.
/// </summary>
internal sealed class SvcDisplayBoard(
    IUnitOfWork unitOfWork,
    ILiveSnapshotStore snapshots,
    TimeProvider timeProvider,
    AdvertisingPolicyGuard advertising,
    ReportReader sites)
    : ISvcDisplayBoard
{
    private static readonly string NoDisplayHash = new('0', 64);

    public async Task<DisplayPlayer> FindAsync(string code, string credential, CancellationToken ct = default)
    {
        if (!TopologyCodes.IsValid(code) || !DisplayCredentials.IsWellFormed(credential))
            return null;
        var prefix = credential[..DisplayCredentials.PrefixLength];
        var display = await unitOfWork.StorageProvider.Query<Display>()
            .Where(d => d.Code == code && d.DeletedOn == null && d.Enabled && d.CredentialPrefix == prefix)
            .FirstOrDefaultAsync(ct);
        var matches = DisplayCredentials.Matches(credential, display?.CredentialHash ?? NoDisplayHash);
        return display is not null && matches ? new DisplayPlayer(display.Id.GetValueOrDefault(), display.Code, display.SiteCode, display.CredentialPrefix) : null;
    }

    public async Task<Result<DisplayBoardViewModel>> GetAsync(Guid displayId, string credentialPrefix, CancellationToken ct = default)
    {
        var display = await unitOfWork.StorageProvider.Query<Display>()
            .Where(d => d.Id == displayId && d.DeletedOn == null && d.Enabled && d.CredentialPrefix == credentialPrefix)
            .FirstOrDefaultAsync(ct);
        if (display is null)
            return Result.Error<DisplayBoardViewModel>(TopologyErrors.NotFound);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entries = new List<DisplayBoardEntryViewModel>();
        foreach (var entry in display.EntryList)
        {
            var snapshot = await snapshots.GetAsync(ZoneKeys.For(display.SiteCode, entry.Zone), ct);
            entries.Add(snapshot is null
                ? new DisplayBoardEntryViewModel(entry.Labels, null, false, null, null)
                : new DisplayBoardEntryViewModel(entry.Labels, snapshot.NowcastMinutes, snapshot.NowcastDegraded || snapshot.LengthDegraded, snapshot.NoService,
                    Math.Max(0, Math.Round((now - snapshot.PublishedUtc.ToUniversalTime()).TotalSeconds, 1))));
        }

        var illustrative = await unitOfWork.StorageProvider.Query<Site>().AnyAsync(s => s.Code == display.SiteCode && s.IsIllustrative, ct);
        // ARV-141: the active slides of the display's site; which apply, and in which languages, is the entity's rule.
        var slides = await unitOfWork.StorageProvider.Query<InstructionSlide>()
            .Where(s => s.SiteCode == display.SiteCode && s.Status == InstructionStatus.Active)
            .OrderBy(s => s.Position)
            .Take(InstructionSlide.MaxSlidesPerSite)
            .ToListAsync(ct);
        var (pinned, rotation) = InstructionSlides.ForDisplay(slides, display);
        // ARV-145: advertising only while the site's policy allows it for every zone the display shows, read on every refresh,
        // so a display falls back to instructions with the next board after the policy or the published profile changes.
        var secondRegion = display.SecondRegion;
        if (secondRegion == DisplaySecondRegion.Advertising && !await advertising.AllowsAsync(display.SiteCode, display.EntryList.Select(e => e.Zone), ct))
            secondRegion = DisplaySecondRegion.Instructions;
        // ARV-143b: a plan plays only where the policy allows advertising now; a plan alone is never permission.
        var plan = secondRegion == DisplaySecondRegion.Advertising && display.Layout == DisplayLayout.Split ? await PlanAsync(display, now, ct) : null;

        return new Result<DisplayBoardViewModel>(new DisplayBoardViewModel(display.Code, display.Name, display.Orientation.ToString(), display.LanguageList,
            display.BandMinutes, display.HysteresisMinutes, display.StaleSeconds, display.FallbackMap, entries, now, illustrative, display.Layout.ToString(),
            display.QueueShare, secondRegion.ToString(), pinned is null ? null : Slide(pinned), rotation.Select(s => Slide(s)).ToList(), plan));
    }

    /// <summary>
    /// The playlist that applies to the display now (ARV-143a's precedence), with the items that may play: advertisements
    /// approved and within their dates on the site's local day, active slides with words in the display's languages, in the
    /// playlist's order. None, or none playable: null, and the display shows instructions.
    /// </summary>
    private async Task<DisplayBoardAdvertisingViewModel> PlanAsync(Display display, DateTime now, CancellationToken ct)
    {
        var schedules = await unitOfWork.StorageProvider.Query<AdvertisingSchedule>()
            .Where(s => s.SiteCode == display.SiteCode && s.Status == InstructionStatus.Active)
            .OrderBy(s => s.CreatedOn).ThenBy(s => s.Id)
            .Take(AdvertisingSchedule.MaxSchedulesPerSite)
            .ToListAsync(ct);
        if (schedules.Count == 0)
            return null;
        var active = (await unitOfWork.StorageProvider.Query<AdvertisingPlaylist>()
                .Where(p => p.SiteCode == display.SiteCode && p.Status == InstructionStatus.Active)
                .Select(p => p.Id)
                .ToListAsync(ct))
            .Select(id => id.GetValueOrDefault())
            .ToHashSet();
        var zone = await sites.TimeZoneAsync(display.SiteCode, ct);
        // A board looks ahead no further than it may be shown: the display's stale threshold (at most 30 minutes, 30 steps).
        var (schedule, until) = AdvertisingSchedules.Now(schedules, active, display.SiteCode, display.Code, display.EntryList.Select(e => e.Zone).ToList(), zone, now,
            TimeSpan.FromSeconds(display.StaleSeconds));
        if (schedule is null)
            return null;
        var playlist = await unitOfWork.StorageProvider.Query<AdvertisingPlaylist>()
            .Where(p => p.Id == schedule.PlaylistId && p.SiteCode == display.SiteCode && p.Status == InstructionStatus.Active)
            .FirstOrDefaultAsync(ct);
        if (playlist is null)
            return null;

        var items = playlist.ItemList;
        var mediaIds = items.Where(i => i.Kind == AdvertisingItemKind.Media).Select(i => i.Id).ToList();
        var slideIds = items.Where(i => i.Kind == AdvertisingItemKind.Slide).Select(i => i.Id).ToList();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        var media = mediaIds.Count == 0
            ? []
            : (await unitOfWork.StorageProvider.Query<AdvertisingMedia>()
                .Where(m => m.SiteCode == display.SiteCode && mediaIds.Contains(m.Id.Value))
                .ToListAsync(ct))
            .Where(m => m.CanPlayOn(today))
            .ToDictionary(m => m.Id.GetValueOrDefault());
        var languages = display.LanguageList;
        var slides = slideIds.Count == 0
            ? []
            : (await unitOfWork.StorageProvider.Query<InstructionSlide>()
                .Where(s => s.SiteCode == display.SiteCode && s.Status == InstructionStatus.Active && slideIds.Contains(s.Id.Value))
                .ToListAsync(ct))
            .Select(s => (Slide: s, Texts: languages.Where(s.TextMap.ContainsKey).Select(l => (l, s.TextMap[l])).ToList()))
            .Where(s => s.Texts.Count > 0)
            .ToDictionary(s => s.Slide.Id.GetValueOrDefault());

        var played = new List<(DisplayBoardAdvertisingItemViewModel View, int Weight)>();
        foreach (var item in items)
        {
            if (item.Kind == AdvertisingItemKind.Media && media.TryGetValue(item.Id, out var advertisement))
                played.Add((new DisplayBoardAdvertisingItemViewModel(nameof(AdvertisingItemKind.Media), item.Id, advertisement.ContentType, item.Seconds, null), item.Weight));
            else if (item.Kind == AdvertisingItemKind.Slide && slides.TryGetValue(item.Id, out var slide))
                played.Add((new DisplayBoardAdvertisingItemViewModel(nameof(AdvertisingItemKind.Slide), null, null, item.Seconds,
                    Slide(new InstructionSlides.Shown(slide.Slide, slide.Texts), item.Seconds)), item.Weight));
        }

        return played.Count == 0
            ? null
            : new DisplayBoardAdvertisingViewModel(playlist.Id.GetValueOrDefault(), until, display.AdvertisingPauseMinutes, played.Select(p => p.View).ToList(),
                AdvertisingPlaylist.PlayingOrder(played.Select(p => p.Weight).ToList()));
    }

    /// <summary>A slide as the player gets it: its icon's name, its time on screen and its words in the display's languages.</summary>
    private static DisplayBoardSlideViewModel Slide(InstructionSlides.Shown shown, int? seconds = null) =>
        new(shown.Slide.Position, shown.Slide.Icon.ToString(), seconds ?? shown.Slide.DurationSeconds,
            shown.Texts.Select(t => new DisplayBoardSlideTextViewModel(t.Language, t.Text.Title, t.Text.Body)).ToList());
}
