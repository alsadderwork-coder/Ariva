using System.Text.Json;
using Ariva.Core.Availability;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A schedule of a site's advertising region (ARV-143a, wiki 11 section 9): it assigns a playlist to displays of the site
/// (every display, those showing one of its queue zones, or those named: the instruction slides' scopes, which stand for
/// display groups) on chosen days of the week within a window of the site's local time. The window is kept as the site's
/// local wall-clock time (the minute it opens and how long it lasts, possibly past midnight into the next day) and judged
/// against the site's time zone at each instant, so it keeps its local hours across daylight saving: a window whose local
/// minutes are skipped when the clocks go forward does not apply that day, and one in the hour the clocks go back applies
/// both times. A retired schedule is kept and applies nowhere. <see cref="AdvertisingSchedules"/> decides which applies.
/// </summary>
public class AdvertisingSchedule : BaseAuditableEntity<AdvertisingSchedule>, ISiteBound
{
    public const int MaxSchedulesPerSite = 200;

    protected AdvertisingSchedule()
    {
    }

    public AdvertisingSchedule(string siteCode, AdvertisingScheduleValues values)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        SiteCode = siteCode;
        Status = InstructionStatus.Active;
        Set(values);
    }

    public virtual string SiteCode { get; protected set; }

    public virtual Guid PlaylistId { get; protected set; }

    public virtual InstructionScope Scope { get; protected set; }

    /// <summary>The queue zone names (Zone) or display codes (Display) it applies to, as a JSON array; empty for Site.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(InstructionSlide.MaxTargetsJsonLength)]
    public virtual string Targets { get; protected set; }

    /// <summary>The days the window opens on, as English day names separated by commas, Monday first ("Monday,Friday").</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public virtual string Days { get; protected set; }

    /// <summary>The minute of the site's local day the window opens (0 to 1,439).</summary>
    public virtual int OpensMinute { get; protected set; }

    /// <summary>How long the window lasts (1 to 1,440 minutes; past the day's end it runs into the next day).</summary>
    public virtual int LengthMinutes { get; protected set; }

    public virtual InstructionStatus Status { get; protected set; }

    public virtual bool IsActive => Status == InstructionStatus.Active;

    public virtual IReadOnlyList<string> TargetList => Targets is null ? [] : JsonSerializer.Deserialize<List<string>>(Targets, Display.Json) ?? [];

    public virtual IReadOnlyList<DayOfWeek> DayList => AdvertisingScheduleValues.ParseDays(Days) ?? [];

    public virtual OperatingInterval Window => new(OpensMinute, LengthMinutes);

    public virtual AdvertisingScheduleValues Values() =>
        new(PlaylistId, Scope, TargetList, DayList.Select(d => d.ToString()).ToList(), Window.Opens, Window.Closes);

    public virtual void Set(AdvertisingScheduleValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = values.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(values));
        OperatingInterval.TryParse(values.Opens, values.Closes, out var window);
        PlaylistId = values.PlaylistId;
        Scope = values.Scope;
        Targets = InstructionSlide.TargetsJson(values.Targets);
        Days = AdvertisingScheduleValues.DaysText(AdvertisingScheduleValues.ParseDays(values.Days));
        OpensMinute = window.OpensMinute;
        LengthMinutes = window.LengthMinutes;
    }

    public virtual void Retire() => Status = InstructionStatus.Retired;

    public virtual void Restore() => Status = InstructionStatus.Active;

    /// <summary>How specific its target is: a named display (3) before a zone (2) before the site (1).</summary>
    public virtual int Specificity => Scope switch
    {
        InstructionScope.Display => 3,
        InstructionScope.Zone => 2,
        _ => 1
    };

    /// <summary>Whether it targets this display of its site (the slides' rule: Site, Zone or Display); never while retired.</summary>
    public virtual bool AppliesTo(string siteCode, string displayCode, IReadOnlyCollection<string> zones)
    {
        if (!IsActive || !string.Equals(siteCode, SiteCode, StringComparison.Ordinal))
            return false;
        return Scope switch
        {
            InstructionScope.Site => true,
            InstructionScope.Zone => TargetList.Any(t => Enumerable.Contains(zones, t, StringComparer.Ordinal)),
            InstructionScope.Display => Enumerable.Contains(TargetList, displayCode, StringComparer.Ordinal),
            _ => false
        };
    }

    /// <summary>
    /// When the window covering this local moment opened (local wall-clock time), or null when no window of it covers the
    /// moment: a window opened on one of its days, or the day before for the part of an overnight window after midnight.
    /// </summary>
    public virtual DateTime? OpenedBefore(DateTime local) => OpenedBefore(local, DayList, Window);

    internal static DateTime? OpenedBefore(DateTime local, IReadOnlyList<DayOfWeek> days, OperatingInterval window)
    {
        var minute = (local.Hour * 60) + local.Minute;
        if (days.Contains(local.DayOfWeek) && window.CoversSameDay(minute))
            return local.Date.AddMinutes(window.OpensMinute);
        var yesterday = local.Date.AddDays(-1);
        return days.Contains(yesterday.DayOfWeek) && window.CoversNextDay(minute) ? yesterday.AddMinutes(window.OpensMinute) : null;
    }

    /// <summary>What the audit trail keeps of a schedule: its playlist, target, days, window and state.</summary>
    public virtual string AuditSummary() => SummaryOf(SiteCode, PlaylistId, Scope, TargetList, Days, Window, Status);

    internal static string SummaryOf(string siteCode, Guid playlistId, InstructionScope scope, IEnumerable<string> targets, string days,
        OperatingInterval window, InstructionStatus status) =>
        JsonSerializer.Serialize(new
        {
            SiteCode = siteCode, PlaylistId = playlistId, Scope = scope.ToString(), Targets = targets, Days = days, window.Opens, window.Closes,
            Status = status.ToString()
        }, Display.Json);
}

/// <summary>A schedule's settings as checked values (ARV-143a): its playlist, target, days and local window ("HH:MM").</summary>
public sealed record AdvertisingScheduleValues(
    Guid PlaylistId,
    InstructionScope Scope,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Days,
    string Opens,
    string Closes)
{
    /// <summary>The days in their order of the week, Monday first, as the screen shows them.</summary>
    public static readonly IReadOnlyList<DayOfWeek> Week = WeeklyHours.Days;

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var targets = Targets ?? [];
        if (PlaylistId == Guid.Empty)
            problems.Add("A schedule plays one of the site's playlists.");
        // The slides' rules for the target: Site names nothing, Zone and Display name 1 to 20 queue zones or display codes.
        problems.AddRange(DisplayTargets.Problems("A schedule", Scope, targets));
        var days = ParseDays(Days);
        if (days is not { Count: > 0 })
            problems.Add("A schedule names 1 to 7 different days: Monday, Tuesday, Wednesday, Thursday, Friday, Saturday or Sunday.");
        if (!OperatingInterval.TryParse(Opens, Closes, out var window))
            problems.Add(WeeklyHours.InvalidInterval);
        // The audit trail keeps a schedule whole: its summary at its longest (a site code of 17 characters) must fit an entry.
        if (problems.Count == 0 && AdvertisingSchedule.SummaryOf(new string('X', 17), PlaylistId, Scope, targets.Select(t => t.Trim()), DaysText(days),
                window, InstructionStatus.Retired).Length > AuditEntry.SummaryLength)
            problems.Add(TooLongToAudit);
        return problems;
    }

    public const string TooLongToAudit = "The targets are too long to keep whole in the audit trail; name fewer or shorter zones or displays.";

    /// <summary>The days named, in the week's order, or null when one is not a day name or a day is named twice.</summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(IEnumerable<string> days)
    {
        var parsed = new List<DayOfWeek>();
        foreach (var day in days ?? [])
        {
            if (day is null || !Enum.TryParse<DayOfWeek>(day.Trim(), ignoreCase: false, out var value) || !Enum.IsDefined(value) ||
                !string.Equals(value.ToString(), day.Trim(), StringComparison.Ordinal) || parsed.Contains(value))
                return null;
            parsed.Add(value);
        }

        return parsed.Count is 0 or > 7 ? null : Week.Where(parsed.Contains).ToList();
    }

    /// <summary>The days as stored text, from the stored text's own separator.</summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(string stored) => stored is null ? null : ParseDays(stored.Split(','));

    public static string DaysText(IEnumerable<DayOfWeek> days) => string.Join(',', (days ?? []).Select(d => d.ToString()));
}

/// <summary>
/// Which schedule applies to a display (ARV-143a). At a moment of the site's local time, among the active schedules that
/// target the display, whose window covers the moment and whose playlist is active: the most specific (a named display,
/// then a zone, then the site), then the one whose window opened last, then the newest. None on an empty schedule, which
/// leaves the display's instructions.
/// </summary>
public static class AdvertisingSchedules
{
    /// <summary>A stretch of time with one outcome: the schedule (and its playlist) that applies from one instant to the next, or none.</summary>
    public sealed record Segment(DateTime FromUtc, DateTime ToUtc, AdvertisingSchedule Schedule);

    /// <summary>A schedule that targets the display, read once: its days, window and specificity.</summary>
    private sealed record Candidate(AdvertisingSchedule Schedule, IReadOnlyList<DayOfWeek> Days, OperatingInterval Window, int Specificity);

    public static AdvertisingSchedule Resolve(IEnumerable<AdvertisingSchedule> schedules, IReadOnlySet<Guid> activePlaylists, string siteCode,
        string displayCode, IReadOnlyCollection<string> zones, DateTime local) =>
        Pick(Candidates(schedules, activePlaylists, siteCode, displayCode, zones), local);

    /// <summary>
    /// What applies to a display from <paramref name="fromUtc"/> for <paramref name="days"/> days, minute by minute in the
    /// site's time zone (every offset is a whole number of minutes, so no change is missed, daylight saving included),
    /// merged into stretches with one outcome each.
    /// </summary>
    public static IReadOnlyList<Segment> Timeline(IEnumerable<AdvertisingSchedule> schedules, IReadOnlySet<Guid> activePlaylists,
        string siteCode, string displayCode, IReadOnlyCollection<string> zones, TimeZoneInfo zone, DateTime fromUtc, int days)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (fromUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The timeline starts at a UTC instant.", nameof(fromUtc));
        if (days is < 1 or > 14)
            throw new ArgumentOutOfRangeException(nameof(days), "A timeline covers 1 to 14 days.");
        var start = new DateTime(fromUtc.Ticks - (fromUtc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
        var end = start.AddDays(days);
        var candidates = Candidates(schedules, activePlaylists, siteCode, displayCode, zones);
        var segments = new List<Segment>();
        var current = Pick(candidates, TimeZoneInfo.ConvertTimeFromUtc(start, zone));
        var since = start;
        for (var instant = start.AddMinutes(1); instant < end; instant = instant.AddMinutes(1))
        {
            var applies = candidates.Count == 0 ? null : Pick(candidates, TimeZoneInfo.ConvertTimeFromUtc(instant, zone));
            if (ReferenceEquals(applies, current))
                continue;
            segments.Add(new Segment(since, instant, current));
            since = instant;
            current = applies;
        }

        segments.Add(new Segment(since, end, current));
        return segments;
    }

    /// <summary>The furthest a board looks ahead: a display's longest stale threshold (ARV-058), 30 minutes.</summary>
    public static readonly TimeSpan MaxHorizon = TimeSpan.FromSeconds(DisplayValues.MaxStale);

    /// <summary>
    /// What applies to a display at <paramref name="nowUtc"/> and until when (ARV-143b): the first later minute whose outcome
    /// differs, looked for at most <paramref name="horizon"/> ahead (the display's stale threshold, at most 30 minutes, so a
    /// board costs at most 30 steps) and never past the site's next local midnight (an advertisement's dates turn then). A
    /// player that loses the server stops the plan by then, so it never plays one that would have changed, nor one older
    /// than its board may be.
    /// </summary>
    public static (AdvertisingSchedule Schedule, DateTime UntilUtc) Now(IEnumerable<AdvertisingSchedule> schedules, IReadOnlySet<Guid> activePlaylists,
        string siteCode, string displayCode, IReadOnlyCollection<string> zones, TimeZoneInfo zone, DateTime nowUtc, TimeSpan horizon)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A board is judged at a UTC instant.", nameof(nowUtc));
        if (horizon <= TimeSpan.Zero || horizon > MaxHorizon)
            throw new ArgumentOutOfRangeException(nameof(horizon), "A board looks ahead up to 30 minutes.");
        var candidates = Candidates(schedules, activePlaylists, siteCode, displayCode, zones);
        var midnight = NextLocalMidnightUtc(nowUtc, zone);
        var end = nowUtc + horizon < midnight ? nowUtc + horizon : midnight;
        if (candidates.Count == 0)
            return (null, end);
        var current = Pick(candidates, TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var minute = new DateTime(nowUtc.Ticks - (nowUtc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
        for (var instant = minute.AddMinutes(1); instant < end; instant = instant.AddMinutes(1))
        {
            if (!ReferenceEquals(Pick(candidates, TimeZoneInfo.ConvertTimeFromUtc(instant, zone)), current))
                return (current, instant);
        }

        return (current, end);
    }

    /// <summary>The instant the site's next local day starts: midnight, or the first minute after it when the clocks skip midnight.</summary>
    public static DateTime NextLocalMidnightUtc(DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date.AddDays(1), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return utc > nowUtc ? utc : nowUtc.AddMinutes(1);
    }

    private static List<Candidate> Candidates(IEnumerable<AdvertisingSchedule> schedules, IReadOnlySet<Guid> activePlaylists, string siteCode,
        string displayCode, IReadOnlyCollection<string> zones)
    {
        ArgumentNullException.ThrowIfNull(activePlaylists);
        return (schedules ?? [])
            .Where(s => s.AppliesTo(siteCode, displayCode, zones ?? []) && activePlaylists.Contains(s.PlaylistId))
            .Select(s => new Candidate(s, s.DayList, s.Window, s.Specificity))
            .ToList();
    }

    /// <summary>The most specific candidate whose window covers the moment, then the one opened last, then the newest.</summary>
    private static AdvertisingSchedule Pick(IReadOnlyList<Candidate> candidates, DateTime local)
    {
        Candidate best = null;
        var bestOpened = DateTime.MinValue;
        foreach (var candidate in candidates)
        {
            if (AdvertisingSchedule.OpenedBefore(local, candidate.Days, candidate.Window) is not { } opened)
                continue;
            if (best is null || candidate.Specificity > best.Specificity ||
                (candidate.Specificity == best.Specificity && (opened > bestOpened || (opened == bestOpened && Newer(candidate.Schedule, best.Schedule)))))
            {
                best = candidate;
                bestOpened = opened;
            }
        }

        return best?.Schedule;
    }

    private static bool Newer(AdvertisingSchedule a, AdvertisingSchedule b) =>
        Nullable.Compare(a.CreatedOn, b.CreatedOn) is var created && (created > 0 || (created == 0 && a.Id.GetValueOrDefault().CompareTo(b.Id.GetValueOrDefault()) > 0));
}
