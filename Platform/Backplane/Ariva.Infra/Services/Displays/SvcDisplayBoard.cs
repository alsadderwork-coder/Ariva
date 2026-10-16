using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Live;
using Ariva.Infra.Security;
using NHibernate.Linq;

namespace Ariva.Infra.Services.Displays;

/// <summary>
/// The board of a display for its player (ARV-058). <see cref="FindAsync"/> finds the live, enabled display by its code and
/// the credential's prefix and compares the presented credential with the stored hash in constant time (a miss costs the
/// same hash); a deleted, disabled or unknown display and a wrong credential are the same miss. The board carries each
/// entry's queue zone's latest live snapshot (Redis, as the live hub reads it) with its age by this server's clock; the
/// player turns it into a band, keeps it within the hysteresis and shows the neutral message once it is older than the
/// display's stale threshold. Nothing but the board's own labels, messages and numbers goes out.
/// </summary>
internal sealed class SvcDisplayBoard(IUnitOfWork unitOfWork, ILiveSnapshotStore snapshots, TimeProvider timeProvider) : ISvcDisplayBoard
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
        return new Result<DisplayBoardViewModel>(new DisplayBoardViewModel(display.Code, display.Name, display.Orientation.ToString(), display.LanguageList,
            display.BandMinutes, display.HysteresisMinutes, display.StaleSeconds, display.FallbackMap, entries, now, illustrative, display.Layout.ToString(),
            display.QueueShare, display.SecondRegion.ToString(), pinned is null ? null : Slide(pinned), rotation.Select(Slide).ToList()));
    }

    /// <summary>A slide as the player gets it: its icon's name, its time on screen and its words in the display's languages.</summary>
    private static DisplayBoardSlideViewModel Slide(InstructionSlides.Shown shown) =>
        new(shown.Slide.Position, shown.Slide.Icon.ToString(), shown.Slide.DurationSeconds,
            shown.Texts.Select(t => new DisplayBoardSlideTextViewModel(t.Language, t.Text.Title, t.Text.Body)).ToList());
}
