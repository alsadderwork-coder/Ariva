using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Advertising;

/// <summary>
/// One play a display reports (ARV-144): which advertisement of its playlist it showed, when it started (UTC, by the server's
/// clock as the player keeps it), how long it was on screen in milliseconds, and whether it played its whole slot (completed)
/// or was cut short (interrupted: the wait rule, a stale board, the plan's end or the page going). Advertisements only: an
/// instruction slide in a playlist is not a play. Nothing about who was near the screen, ever.
/// </summary>
public sealed record AdvertisingPlayReport(Guid MediaId, DateTime StartedUtc, int DurationMilliseconds, bool Completed);

/// <summary>A play as stored: the report with the display, its site and the playlist it was scheduled from.</summary>
public sealed record AdvertisingPlay(DateTime StartedUtc, string SiteCode, Guid DisplayId, Guid MediaId, Guid PlaylistId, int DurationMilliseconds, bool Completed);

/// <summary>
/// The rules a reported play keeps (ARV-144) and the checks that it could have been played. A batch holds at most
/// <see cref="MaxBatch"/> plays; a play started at most <see cref="MaxAge"/> ago and ended by the time it is reported (within
/// <see cref="EndSkew"/> for the player's timing, so none starts in the future), lasted at least a second, names an
/// advertisement of the playlist that applied to the display when it started (by the plans as they are when it is reported),
/// lasted no longer than that item's slot, counts as completed only when it filled the slot (<see cref="SlotTolerance"/> either
/// way), and overlaps no other play of the display (it has one advertising region): two plays may share no more than a
/// quarter of the shorter one, and never more than <see cref="OverlapTolerance"/>, so a play inside another is always refused
/// and the airtime taken over any stretch exceeds the stretch by at most that much at each change of play.
/// </summary>
public static class AdvertisingPlays
{
    public const int MaxBatch = 100;

    /// <summary>The longest a play lasts: a video's slot at its ceiling (120 seconds), and a little for the player's timing.</summary>
    public const int MaxDurationMilliseconds = (AdvertisingMedia.MaxVideoSeconds + 5) * 1000;

    /// <summary>The shortest play: less than a second on screen is no play (the player does not report it), and none can stack.</summary>
    public const int MinDurationMilliseconds = 1_000;

    /// <summary>How long a player may hold a play before it reports it (a day without the network).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// A play has ended by the time it is reported. The player keeps the server's clock from its boards, so a few seconds
    /// ahead are its timing (the board's time and its trip), not a forgery.
    /// </summary>
    public static readonly TimeSpan EndSkew = TimeSpan.FromSeconds(5);

    /// <summary>A slot's length either way: what the player's timers and the media's start take.</summary>
    public const int SlotTolerance = 2_000;

    /// <summary>
    /// Two plays of a display may share this much at most (one ends as the next starts, by a clock the player re-reads from
    /// each board), and no more than a quarter of the shorter one; more is two at once.
    /// </summary>
    public const int OverlapTolerance = 1_000;

    /// <summary>
    /// How far back from a play's start its plan is looked for as well: the player plays the board it last read (up to a poll
    /// old) by a clock taken from it, so a play started on the minute a plan changed is judged against the minute before too.
    /// </summary>
    public static readonly TimeSpan PlanTolerance = TimeSpan.FromMinutes(1);

    public const string TooMany = "A batch holds 1 to 100 plays.";

    private const string Unfinished = "A play has ended before it is reported.";

    /// <summary>Why a play cannot be what it says, or null when it may be.</summary>
    public static string Problem(AdvertisingPlayReport play, DateTime nowUtc)
    {
        if (play is null || play.MediaId == Guid.Empty)
            return "A play names an advertisement.";
        if (play.StartedUtc.Kind != DateTimeKind.Utc)
            return "A play starts at a UTC instant.";
        if (play.StartedUtc < nowUtc - MaxAge)
            return "A play started within the last 24 hours.";
        // Compared before any arithmetic on the start, so no instant the body names can overflow.
        if (play.StartedUtc > nowUtc + EndSkew)
            return Unfinished;
        if (play.DurationMilliseconds is < MinDurationMilliseconds or > MaxDurationMilliseconds)
            return "A play lasts 1 to 125 seconds.";
        if (play.StartedUtc.AddMilliseconds(play.DurationMilliseconds) > nowUtc + EndSkew)
            return Unfinished;
        return null;
    }

    /// <summary>Why a play cannot have filled its item's slot as it says, or null: no longer than the slot, completed only when it filled it.</summary>
    public static string SlotProblem(AdvertisingPlayReport play, int slotSeconds)
    {
        ArgumentNullException.ThrowIfNull(play);
        var slot = slotSeconds * 1000;
        if (play.DurationMilliseconds > slot + SlotTolerance)
            return "A play lasts no longer than its slot.";
        if (play.Completed && play.DurationMilliseconds < slot - SlotTolerance)
            return "A completed play fills its slot.";
        return null;
    }

    /// <summary>
    /// Whether two plays of one display were on screen at once: they share more than a quarter of the shorter one, or more than
    /// <see cref="OverlapTolerance"/>. A play inside another always overlaps it.
    /// </summary>
    public static bool Overlap(DateTime aStartUtc, int aMilliseconds, DateTime bStartUtc, int bMilliseconds)
    {
        var aEnd = aStartUtc.AddMilliseconds(aMilliseconds);
        var bEnd = bStartUtc.AddMilliseconds(bMilliseconds);
        var shared = ((aEnd < bEnd ? aEnd : bEnd) - (aStartUtc > bStartUtc ? aStartUtc : bStartUtc)).TotalMilliseconds;
        return shared > Math.Min(OverlapTolerance, Math.Min(aMilliseconds, bMilliseconds) / 4d);
    }

    /// <summary>Whether a play overlaps any of the others (see <see cref="Overlap"/>).</summary>
    public static bool OverlapsAny(DateTime startedUtc, int milliseconds, IEnumerable<(DateTime StartedUtc, int DurationMilliseconds)> others) =>
        (others ?? []).Any(o => Overlap(o.StartedUtc, o.DurationMilliseconds, startedUtc, milliseconds));

    /// <summary>
    /// The playlist and item a display played the advertisement from: the playlist that applied to it when the play started
    /// (or a minute before, <see cref="PlanTolerance"/>) and has the advertisement as an item whose slot the play kept
    /// (<see cref="SlotProblem"/>; a playlist may hold an advertisement more than once, with different slots); null when none
    /// did, and the play is refused.
    /// </summary>
    public static (Guid PlaylistId, AdvertisingPlaylistItem Item)? ScheduledItem(AdvertisingPlayReport play, AdvertisingSchedules.Prepared schedules,
        IReadOnlyDictionary<Guid, AdvertisingPlaylist> activePlaylists, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(activePlaylists);
        ArgumentNullException.ThrowIfNull(zone);
        foreach (var instant in new[] { play.StartedUtc, play.StartedUtc - PlanTolerance })
        {
            var schedule = schedules.At(TimeZoneInfo.ConvertTimeFromUtc(instant, zone));
            if (schedule is not null && activePlaylists.TryGetValue(schedule.PlaylistId, out var playlist) &&
                playlist.ItemList.FirstOrDefault(i => i.Kind == AdvertisingItemKind.Media && i.Id == play.MediaId && SlotProblem(play, i.Seconds) is null) is { } item)
                return (playlist.Id.GetValueOrDefault(), item);
        }

        return null;
    }
}
