namespace Ariva.Core.Availability;

/// <summary>What the calendar says about one UTC minute (ARV-118).</summary>
public enum CalendarState
{
    /// <summary>Inside the site's operating hours: the minute counts in availability.</summary>
    Operating,

    /// <summary>Outside them: the minute does not count.</summary>
    Closed,

    /// <summary>Inside them, but in a maintenance window announced before it started: the minute does not count.</summary>
    Maintenance
}

/// <summary>
/// A version of the weekly hours, in force from the local day <see cref="EffectiveFrom"/>, recorded at
/// <see cref="RecordedUtc"/>, removed at <see cref="CancelledUtc"/> (null while it stands).
/// </summary>
public sealed record CalendarWeek(DateOnly EffectiveFrom, DateTime RecordedUtc, DateTime? CancelledUtc, WeeklyHours Hours);

/// <summary>
/// A dated exception: the hours that open on the local day <see cref="Date"/> instead of the week's (none: closed all
/// day), recorded at <see cref="RecordedUtc"/>, removed at <see cref="CancelledUtc"/> (null while it stands).
/// </summary>
public sealed record CalendarException(DateOnly Date, DateTime RecordedUtc, DateTime? CancelledUtc, IReadOnlyList<OperatingInterval> Hours);

/// <summary>An announced maintenance window [StartUtc, EndUtc), recorded at <see cref="RecordedUtc"/>, cancelled at <see cref="CancelledUtc"/>.</summary>
public sealed record CalendarMaintenance(DateTime StartUtc, DateTime EndUtc, DateTime RecordedUtc, DateTime? CancelledUtc);

/// <summary>A UTC minute's local date (in the site's time zone) and its calendar state.</summary>
public readonly record struct CalendarMinute(DateOnly LocalDate, CalendarState State);

/// <summary>
/// A site's operating calendar (ARV-118, formulas F18, Proposed): weekly hours in the site's local time, dated exceptions
/// and announced maintenance windows, evaluated per UTC minute. Pure: no clock, no I/O.
/// <para>
/// Every entry counts only from a time fixed before it takes effect, so a minute's state never changes after the minute
/// (the ledger is written once, and a catch-up after downtime gives the answer the live run would have given):
/// a weekly version counts from its first local day when it was recorded before that day started and not removed before
/// then; an exception counts
/// when it was recorded before its day started and not removed before then; a maintenance window counts only when it was
/// recorded before its start (a window recorded at or after its start does not count at all) and not cancelled before its
/// start. A site without a version in force is open around the clock (every minute counts).
/// </para>
/// <para>
/// Local time: a minute is operating when its local wall-clock time lies in an interval that opened on its local day, or in
/// the overnight part of one that opened on the day before. A day's hours are the intervals that open on it, so an
/// exception for a day replaces the intervals that open on that day (and their overnight part), not the overnight part of
/// the day before. On a daylight-saving change the skipped local hour has no minutes and the repeated hour counts twice,
/// so a 06:00 to 22:00 day lasts 15 or 17 hours of UTC minutes on those days. Minutes are stored in UTC.
/// </para>
/// </summary>
public sealed class OperatingCalendar
{
    private readonly List<CalendarWeek> _weeks;
    private readonly List<CalendarException> _exceptions;
    private readonly List<CalendarMaintenance> _windows;
    private readonly Dictionary<DateOnly, DateTime> _dayStarts = [];

    public OperatingCalendar(TimeZoneInfo zone, IEnumerable<CalendarWeek> weeks, IEnumerable<CalendarException> exceptions, IEnumerable<CalendarMaintenance> windows)
    {
        Zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _weeks = [.. (weeks ?? []).Where(w => w?.Hours is not null)];
        _exceptions = [.. (exceptions ?? []).Where(e => e?.Hours is not null)];
        _windows = [.. (windows ?? []).Where(w => w is not null && w.EndUtc > w.StartUtc)];
    }

    public TimeZoneInfo Zone { get; }

    /// <summary>
    /// The UTC instant a local day starts: its local midnight, or the first valid local minute after it when a clock
    /// change skips midnight.
    /// </summary>
    public static DateTime DayStartUtc(DateOnly date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = date.ToDateTime(TimeOnly.MinValue);
        for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(local); i++)
            local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
    }

    /// <summary>The local date and time of a UTC instant in the site's zone.</summary>
    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>The intervals that open on a local day: its exception's when one counts, otherwise its weekday's in the version in force.</summary>
    public IReadOnlyList<OperatingInterval> HoursOpeningOn(DateOnly date)
    {
        var start = DayStart(date);
        CalendarException exception = null;
        foreach (var e in _exceptions)
        {
            if (e.Date == date && e.RecordedUtc < start && (e.CancelledUtc is null || e.CancelledUtc >= start) &&
                (exception is null || e.RecordedUtc > exception.RecordedUtc))
                exception = e;
        }

        return exception is not null ? exception.Hours : WeekOn(date).On(date.DayOfWeek);
    }

    /// <summary>The weekly hours in force on a local day (<see cref="WeeklyHours.AlwaysOpen"/> before the first version counts).</summary>
    public WeeklyHours WeekOn(DateOnly date)
    {
        CalendarWeek chosen = null;
        foreach (var week in _weeks)
        {
            if (week.EffectiveFrom > date)
                continue;
            var start = DayStart(week.EffectiveFrom);
            if (week.RecordedUtc >= start || week.CancelledUtc < start)
                continue;
            if (chosen is null || week.EffectiveFrom > chosen.EffectiveFrom ||
                (week.EffectiveFrom == chosen.EffectiveFrom && week.RecordedUtc > chosen.RecordedUtc))
                chosen = week;
        }

        return chosen?.Hours ?? WeeklyHours.AlwaysOpen;
    }

    /// <summary>True when a counted maintenance window covers the minute.</summary>
    public bool InMaintenance(DateTime minuteUtc) =>
        _windows.Any(w => w.StartUtc <= minuteUtc && minuteUtc < w.EndUtc && w.RecordedUtc < w.StartUtc && (w.CancelledUtc is null || w.CancelledUtc >= w.StartUtc));

    /// <summary>The minute's local date and state.</summary>
    public CalendarMinute Classify(DateTime minuteUtc)
    {
        var local = Local(minuteUtc);
        var date = DateOnly.FromDateTime(local);
        var minute = (local.Hour * 60) + local.Minute;
        var operating = HoursOpeningOn(date).Any(i => i.CoversSameDay(minute)) || HoursOpeningOn(date.AddDays(-1)).Any(i => i.CoversNextDay(minute));
        if (!operating)
            return new CalendarMinute(date, CalendarState.Closed);
        return new CalendarMinute(date, InMaintenance(minuteUtc) ? CalendarState.Maintenance : CalendarState.Operating);
    }

    private DateTime DayStart(DateOnly date)
    {
        if (!_dayStarts.TryGetValue(date, out var start))
        {
            start = DayStartUtc(date, Zone);
            if (_dayStarts.Count < 10_000)
                _dayStarts[date] = start;
        }

        return start;
    }
}
