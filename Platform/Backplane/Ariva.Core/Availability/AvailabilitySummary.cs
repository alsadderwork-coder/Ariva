namespace Ariva.Core.Availability;

/// <summary>
/// The ledger's minutes over a period (ARV-118, formulas F18). <see cref="OperatingMinutes"/> are the minutes the calendar
/// counts (inside the operating hours, outside announced maintenance); <see cref="AvailableMinutes"/>,
/// <see cref="UnavailableMinutes"/> and <see cref="UnobservedMinutes"/> split them; maintenance and closed minutes are
/// shown apart and count in neither side of <see cref="Availability"/>. Because a pre-declared maintenance window removes
/// its minutes from that ratio, <see cref="AvailabilityMaintenanceAsUnavailable"/> reports the stricter figure beside it
/// (maintenance minutes counted as operating and not available), so maintenance cannot silently inflate the result. The reason counts are operating minutes that carried the reason
/// (a minute may carry several). <see cref="RecordedMinutes"/> is every ledger row of the period, whatever its state.
/// </summary>
public sealed record AvailabilityCounts(
    int RecordedMinutes,
    int OperatingMinutes,
    int AvailableMinutes,
    int UnavailableMinutes,
    int UnobservedMinutes,
    int MaintenanceMinutes,
    int ClosedMinutes,
    int StaleZoneMinutes,
    int MissingMinuteMinutes,
    int StreamLagMinutes,
    int NoPublishedZonesMinutes)
{
    public static AvailabilityCounts Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Available operating minutes over operating minutes; null when the period has no operating minute (never 0 or 1 by default).</summary>
    public double? Availability => AvailabilitySummary.Ratio(AvailableMinutes, OperatingMinutes);

    /// <summary>
    /// The stricter ratio: available operating minutes over operating plus maintenance minutes (maintenance counted as
    /// unavailable rather than as not operating); null when the period has neither.
    /// </summary>
    public double? AvailabilityMaintenanceAsUnavailable => AvailabilitySummary.Ratio(AvailableMinutes, OperatingMinutes + MaintenanceMinutes);

    public AvailabilityCounts Add(AvailabilityCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new AvailabilityCounts(RecordedMinutes + other.RecordedMinutes, OperatingMinutes + other.OperatingMinutes,
            AvailableMinutes + other.AvailableMinutes, UnavailableMinutes + other.UnavailableMinutes, UnobservedMinutes + other.UnobservedMinutes,
            MaintenanceMinutes + other.MaintenanceMinutes, ClosedMinutes + other.ClosedMinutes, StaleZoneMinutes + other.StaleZoneMinutes,
            MissingMinuteMinutes + other.MissingMinuteMinutes, StreamLagMinutes + other.StreamLagMinutes, NoPublishedZonesMinutes + other.NoPublishedZonesMinutes);
    }
}

/// <summary>One local day of the site and its counts.</summary>
public sealed record AvailabilityDay(DateOnly Date, AvailabilityCounts Counts);

/// <summary>One week (Monday to Sunday, local dates) and its counts; <see cref="Days"/> are the days of the week inside the range.</summary>
public sealed record AvailabilityWeek(DateOnly WeekStart, int Days, AvailabilityCounts Counts);

/// <summary>The ratio and the grouping of days into weeks and a range.</summary>
public static class AvailabilitySummary
{
    /// <summary>The pilot criterion (D6, formulas F18): availability of at least 99 percent of operating hours.</summary>
    public const double PilotTarget = 0.99;

    public static double? Ratio(int available, int operating) => operating <= 0 ? null : Math.Clamp(available / (double)operating, 0, 1);

    /// <summary>The Monday of the ISO week a date belongs to.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>Every day of [from, to] in order, with its counts (zero for a day without rows).</summary>
    public static IReadOnlyList<AvailabilityDay> Days(DateOnly from, DateOnly to, IReadOnlyDictionary<DateOnly, AvailabilityCounts> counts)
    {
        var days = new List<AvailabilityDay>();
        for (var date = from; date <= to; date = date.AddDays(1))
            days.Add(new AvailabilityDay(date, counts is not null && counts.TryGetValue(date, out var c) ? c : AvailabilityCounts.Zero));
        return days;
    }

    /// <summary>The days grouped into weeks starting on Monday; the first and last weeks of a range may hold fewer than 7 days.</summary>
    public static IReadOnlyList<AvailabilityWeek> Weeks(IReadOnlyList<AvailabilityDay> days) =>
        [.. (days ?? []).GroupBy(d => WeekStart(d.Date)).OrderBy(g => g.Key)
            .Select(g => new AvailabilityWeek(g.Key, g.Count(), g.Aggregate(AvailabilityCounts.Zero, (sum, d) => sum.Add(d.Counts))))];

    public static AvailabilityCounts Total(IReadOnlyList<AvailabilityDay> days) =>
        (days ?? []).Aggregate(AvailabilityCounts.Zero, (sum, d) => sum.Add(d.Counts));
}
