using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Ariva.Core.Availability;

namespace Ariva.Core.Domain.Entities;

/// <summary>Dates of the site calendar (ARV-118): local dates written "yyyy-MM-dd".</summary>
public static class CalendarDates
{
    public static bool TryParse(string text, out DateOnly date)
    {
        date = default;
        return text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// A version of a site's weekly operating hours (ARV-118), in force from the local day <see cref="EffectiveFrom"/> until
/// a later version's day. Recorded before that day starts (the service refuses anything else, and the calendar ignores a
/// version recorded later), so a minute's operating state never changes after the minute. Removed (soft deleted) only
/// before its day starts. The hours are stored as canonical JSON (<see cref="WeeklyHours.ToJson"/>).
/// </summary>
public class OperatingWeek : BaseSoftDeletableEntity<OperatingWeek>, ISiteBound
{
    protected OperatingWeek()
    {
    }

    public OperatingWeek(string siteCode, DateOnly effectiveFrom, WeeklyHours hours, DateTime recordedUtc)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        SiteCode = siteCode;
        EffectiveFrom = CalendarDates.Format(effectiveFrom);
        Replace(hours, recordedUtc);
    }

    public virtual string SiteCode { get; protected set; }

    /// <summary>The first local day the version is in force, "yyyy-MM-dd".</summary>
    public virtual string EffectiveFrom { get; protected set; }

    [MaxLength(WeeklyHours.MaxJsonLength)]
    public virtual string Hours { get; protected set; }

    /// <summary>When the hours were last set (server time, UTC).</summary>
    public virtual DateTime RecordedUtc { get; protected set; }

    public virtual DateOnly EffectiveDate => CalendarDates.TryParse(EffectiveFrom, out var date) ? date : DateOnly.MinValue;

    /// <summary>The weekly hours, or null when the stored text is not valid.</summary>
    public virtual WeeklyHours Week => WeeklyHours.TryFromJson(Hours);

    /// <summary>Sets the hours; the caller has checked that the version's day has not started.</summary>
    public virtual void Replace(WeeklyHours hours, DateTime recordedUtc)
    {
        ArgumentNullException.ThrowIfNull(hours);
        Hours = hours.ToJson();
        RecordedUtc = recordedUtc;
    }

    public virtual string AuditSummary() => $"site={SiteCode}; effectiveFrom={EffectiveFrom}; hours={Hours}";
}

/// <summary>
/// A dated exception to a site's weekly hours (ARV-118): the intervals that open on the local day <see cref="Date"/>
/// instead of the week's, or none (closed all day). Recorded before the day starts and removed only before then.
/// </summary>
public class OperatingDayException : BaseSoftDeletableEntity<OperatingDayException>, ISiteBound
{
    public const int MaxReasonLength = 200;

    protected OperatingDayException()
    {
    }

    public OperatingDayException(string siteCode, DateOnly date, IReadOnlyList<OperatingInterval> hours, string reason, DateTime recordedUtc)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        ArgumentNullException.ThrowIfNull(hours);
        SiteCode = siteCode;
        Date = CalendarDates.Format(date);
        Hours = WeeklyHours.DayToJson(hours);
        Reason = DisplayText.Require(reason, MaxReasonLength, nameof(reason));
        RecordedUtc = recordedUtc;
    }

    public virtual string SiteCode { get; protected set; }

    /// <summary>The local day, "yyyy-MM-dd".</summary>
    public virtual string Date { get; protected set; }

    /// <summary>The day's intervals as JSON; [] when the site is closed all day.</summary>
    [MaxLength(WeeklyHours.MaxJsonLength)]
    public virtual string Hours { get; protected set; }

    public virtual string Reason { get; protected set; }

    public virtual DateTime RecordedUtc { get; protected set; }

    public virtual DateOnly LocalDate => CalendarDates.TryParse(Date, out var date) ? date : DateOnly.MinValue;

    /// <summary>The intervals, or null when the stored text is not valid.</summary>
    public virtual IReadOnlyList<OperatingInterval> Intervals => WeeklyHours.TryDayFromJson(Hours);

    public virtual string AuditSummary() => $"site={SiteCode}; date={Date}; hours={Hours}; reason={System.Text.Json.JsonSerializer.Serialize(Reason)}";
}

/// <summary>
/// An announced maintenance window of a site (ARV-118): operating minutes in [StartsUtc, EndsUtc) do not count in
/// availability. Counts only when recorded before it starts (the service refuses a window that has started, and the
/// calendar ignores one recorded at or after its start); cancelled only before it starts. Whole minutes, at most
/// <see cref="MaxLength"/> long.
/// </summary>
public class MaintenanceWindow : BaseSoftDeletableEntity<MaintenanceWindow>, ISiteBound
{
    public const int MaxReasonLength = 200;
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(7);

    protected MaintenanceWindow()
    {
    }

    public MaintenanceWindow(string siteCode, DateTime startsUtc, DateTime endsUtc, string reason, DateTime recordedUtc)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("The site code is not valid.", nameof(siteCode));
        if (startsUtc.Kind != DateTimeKind.Utc || endsUtc.Kind != DateTimeKind.Utc || startsUtc.Ticks % TimeSpan.TicksPerMinute != 0 ||
            endsUtc.Ticks % TimeSpan.TicksPerMinute != 0 || endsUtc <= startsUtc || endsUtc - startsUtc > MaxLength)
            throw new ArgumentException("A maintenance window is whole UTC minutes, ending after it starts, at most 7 days long.", nameof(endsUtc));
        if (recordedUtc >= startsUtc)
            throw new ArgumentException("A maintenance window is recorded before it starts.", nameof(recordedUtc));
        SiteCode = siteCode;
        StartsUtc = startsUtc;
        EndsUtc = endsUtc;
        Reason = DisplayText.Require(reason, MaxReasonLength, nameof(reason));
        RecordedUtc = recordedUtc;
    }

    public virtual string SiteCode { get; protected set; }
    public virtual DateTime StartsUtc { get; protected set; }
    public virtual DateTime EndsUtc { get; protected set; }
    public virtual string Reason { get; protected set; }
    public virtual DateTime RecordedUtc { get; protected set; }

    public virtual string AuditSummary() =>
        string.Create(CultureInfo.InvariantCulture, $"site={SiteCode}; startsUtc={StartsUtc:yyyy-MM-ddTHH:mm}Z; endsUtc={EndsUtc:yyyy-MM-ddTHH:mm}Z; reason={System.Text.Json.JsonSerializer.Serialize(Reason)}");
}
