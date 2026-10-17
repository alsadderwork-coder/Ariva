using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Ariva.Core.Availability;

namespace Ariva.Core.Services.Quality;

/// <summary>An interval of a weekday in a request: "Monday" to "Sunday", "HH:MM" opening and closing.</summary>
public sealed record OperatingDayHoursRequest([MaxLength(9)] string Day, [MaxLength(5)] string Opens, [MaxLength(5)] string Closes);

/// <summary>An interval of a dated day in a request.</summary>
public sealed record OperatingHoursRequest([MaxLength(5)] string Opens, [MaxLength(5)] string Closes);

/// <summary>Sets the weekly hours in force from a local day after today ("yyyy-MM-dd"); replaces a version of the same day that has not started.</summary>
public sealed record SetOperatingWeekRequest(
    [MaxLength(10)] string EffectiveFrom,
    [MaxLength(7 * WeeklyHours.MaxIntervalsPerDay)] IReadOnlyList<OperatingDayHoursRequest> Hours);

/// <summary>A dated exception for a local day after today: closed all day (no hours), or open in the given hours only.</summary>
public sealed record AddOperatingExceptionRequest(
    [MaxLength(10)] string Date,
    bool Closed,
    [MaxLength(WeeklyHours.MaxIntervalsPerDay)] IReadOnlyList<OperatingHoursRequest> Hours,
    [MaxLength(OperatingDayException.MaxReasonLength)] string Reason);

/// <summary>An announced maintenance window in whole UTC minutes ("2026-10-12T01:00:00Z"), starting after now.</summary>
public sealed record AddMaintenanceWindowRequest(
    [MaxLength(40)] string StartsUtc,
    [MaxLength(40)] string EndsUtc,
    [MaxLength(MaintenanceWindow.MaxReasonLength)] string Reason);

public sealed record OperatingWeekViewModel(Guid Id, string EffectiveFrom, IReadOnlyList<OperatingDayHours> Hours, DateTime RecordedUtc);

public sealed record OperatingExceptionViewModel(Guid Id, string Date, bool Closed, IReadOnlyList<OperatingHoursText> Hours, string Reason, DateTime RecordedUtc);

public sealed record MaintenanceWindowViewModel(Guid Id, DateTime StartsUtc, DateTime EndsUtc, string Reason, DateTime RecordedUtc);

/// <summary>
/// A site's operating calendar: its time zone (its airport's), today's local date, the weekly versions (newest first),
/// and the exceptions and maintenance windows from <see cref="ISvcSiteCalendar.HistoryDays"/> days ago on.
/// </summary>
public sealed record SiteCalendarViewModel(
    string SiteCode,
    string TimeZoneId,
    string Today,
    IReadOnlyList<OperatingWeekViewModel> Weeks,
    IReadOnlyList<OperatingExceptionViewModel> Exceptions,
    IReadOnlyList<MaintenanceWindowViewModel> MaintenanceWindows);

public static class SiteCalendarErrors
{
    /// <summary>A site the caller does not reach or that does not exist, or an entry that is not the site's: one answer (CWE-204).</summary>
    public const string NotFound = "The site or calendar entry does not exist.";

    public const string InvalidDate = "The date is a local date yyyy-MM-dd after today, at most 366 days ahead.";
    public const string InvalidReason = "The reason is 1 to 200 characters, without control, invisible or broken characters.";
    public const string InvalidClosed = "A closed day has no hours; an open day has 1 to 4 intervals.";
    public const string InvalidWindow = "A maintenance window is whole UTC minutes (ISO 8601 ending in Z), starting after now and at most 366 days ahead, ending after its start, at most 7 days long.";
    public const string Started = "The entry has taken effect and is part of the record; it can no longer be changed.";
    public const string ExceptionTaken = "The day already has an exception; remove it first.";
    public const string TooMany = "The site has too many future calendar entries.";
    public const string WeekConcurrent = "The version was set concurrently; read the calendar and try again.";

    /// <summary>Conflicts (409) rather than invalid requests (400).</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal) { ExceptionTaken, Started, TooMany, WeekConcurrent };
}

/// <summary>
/// The validation of calendar requests (ARV-118, Fx.Specification). Today and now are the server's (TimeProvider) in the
/// site's time zone: an entry can only be recorded before it takes effect, so a minute's calendar state is fixed before
/// the minute.
/// </summary>
public static class SiteCalendarRules
{
    /// <summary>The furthest ahead an entry may take effect.</summary>
    public const int MaxDaysAhead = 366;

    public static ISpecification<SetOperatingWeekRequest> Week(DateOnly today) =>
        Fx.Specification<SetOperatingWeekRequest>()
            .And(r => IsFutureDate(r.EffectiveFrom, today), SiteCalendarErrors.InvalidDate)
            .And(r => r.Hours is { Count: <= 7 * WeeklyHours.MaxIntervalsPerDay }, WeeklyHours.InvalidInterval)
            .And(r => WeekProblem(r) != WeeklyHours.InvalidDay, WeeklyHours.InvalidDay)
            .And(r => WeekProblem(r) != WeeklyHours.InvalidInterval, WeeklyHours.InvalidInterval)
            .And(r => WeekProblem(r) != WeeklyHours.TooManyIntervals, WeeklyHours.TooManyIntervals)
            .And(r => WeekProblem(r) != WeeklyHours.Overlapping, WeeklyHours.Overlapping);

    public static ISpecification<AddOperatingExceptionRequest> Exception(DateOnly today) =>
        Fx.Specification<AddOperatingExceptionRequest>()
            .And(r => IsFutureDate(r.Date, today), SiteCalendarErrors.InvalidDate)
            .And(r => r.Closed ? r.Hours is null or { Count: 0 } : r.Hours is { Count: >= 1 and <= WeeklyHours.MaxIntervalsPerDay }, SiteCalendarErrors.InvalidClosed)
            .And(r => DayProblem(r) != WeeklyHours.InvalidInterval, WeeklyHours.InvalidInterval)
            .And(r => DayProblem(r) != WeeklyHours.TooManyIntervals, WeeklyHours.TooManyIntervals)
            .And(r => DayProblem(r) != WeeklyHours.Overlapping, WeeklyHours.Overlapping)
            .And(r => IsReason(r.Reason), SiteCalendarErrors.InvalidReason);

    /// <summary>The week's hours from a request, or the first problem (null hours are a problem of their own rule).</summary>
    public static (WeeklyHours Hours, string Problem) WeekHours(SetOperatingWeekRequest request) =>
        request?.Hours is null
            ? (null, null)
            : WeeklyHours.Parse(request.Hours.Select(h => h is null ? null : new OperatingDayHours(h.Day, h.Opens, h.Closes)));

    /// <summary>The exception's intervals from a request, or the first problem.</summary>
    public static (IReadOnlyList<OperatingInterval> Hours, string Problem) DayHours(AddOperatingExceptionRequest request) =>
        WeeklyHours.ParseDay((request?.Hours ?? []).Select(Text));

    private static string WeekProblem(SetOperatingWeekRequest request) => WeekHours(request).Problem;

    private static string DayProblem(AddOperatingExceptionRequest request) => DayHours(request).Problem;

    public static ISpecification<AddMaintenanceWindowRequest> Maintenance(DateTime nowUtc) =>
        Fx.Specification<AddMaintenanceWindowRequest>()
            .And(r => Window(r, nowUtc) is not null, SiteCalendarErrors.InvalidWindow)
            .And(r => IsReason(r.Reason), SiteCalendarErrors.InvalidReason);

    /// <summary>The window's bounds when valid at <paramref name="nowUtc"/>, otherwise null.</summary>
    public static (DateTime Starts, DateTime Ends)? Window(AddMaintenanceWindowRequest request, DateTime nowUtc)
    {
        if (request is null || Utc(request.StartsUtc) is not { } starts || Utc(request.EndsUtc) is not { } ends)
            return null;
        if (starts.Ticks % TimeSpan.TicksPerMinute != 0 || ends.Ticks % TimeSpan.TicksPerMinute != 0 || starts <= nowUtc || ends <= starts ||
            ends - starts > MaintenanceWindow.MaxLength || starts > nowUtc.AddDays(MaxDaysAhead))
            return null;
        return (starts, ends);
    }

    public static bool IsFutureDate(string text, DateOnly today) =>
        CalendarDates.TryParse(text, out var date) && date > today && date <= today.AddDays(MaxDaysAhead);

    public static bool IsReason(string text) =>
        text is not null && text.Trim() is { Length: > 0 and <= OperatingDayException.MaxReasonLength } && DisplayText.IsClean(text.Trim());

    public static OperatingHoursText Text(OperatingHoursRequest request) => request is null ? null : new OperatingHoursText(request.Opens, request.Closes);

    /// <summary>A UTC time in ISO 8601 ending in Z, or null (an offset or a local time is refused).</summary>
    public static DateTime? Utc(string value) =>
        value is { Length: >= 11 and <= 40 } && value.EndsWith('Z') &&
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) &&
        parsed.Kind == DateTimeKind.Utc
            ? parsed
            : null;
}

/// <summary>
/// A site's operating calendar (ARV-118): weekly hours in the site's local time, dated exceptions and announced
/// maintenance windows, which decide the operating minutes of the availability ledger. Reading needs the site's view
/// permission, changing it the site's edit permission; every change is audited; a site the caller does not reach answers
/// NotFound before anything else is checked (CWE-863). Entries are recorded before they take effect and change only
/// until then.
/// </summary>
public interface ISvcSiteCalendar : ISvcScoped
{
    /// <summary>How far back the calendar lists exceptions and maintenance windows.</summary>
    const int HistoryDays = 92;

    /// <summary>Future entries of each kind a site holds at most (CWE-400).</summary>
    const int MaxFutureEntries = 400;

    Task<Result<SiteCalendarViewModel>> GetAsync(string siteCode, CancellationToken ct = default);

    Task<Result<SiteCalendarViewModel>> SetWeekAsync(string siteCode, SetOperatingWeekRequest request, CancellationToken ct = default);

    Task<Result<SiteCalendarViewModel>> RemoveWeekAsync(string siteCode, Guid id, CancellationToken ct = default);

    Task<Result<SiteCalendarViewModel>> AddExceptionAsync(string siteCode, AddOperatingExceptionRequest request, CancellationToken ct = default);

    Task<Result<SiteCalendarViewModel>> RemoveExceptionAsync(string siteCode, Guid id, CancellationToken ct = default);

    Task<Result<SiteCalendarViewModel>> AddMaintenanceAsync(string siteCode, AddMaintenanceWindowRequest request, CancellationToken ct = default);

    Task<Result<SiteCalendarViewModel>> CancelMaintenanceAsync(string siteCode, Guid id, CancellationToken ct = default);
}
