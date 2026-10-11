using Ariva.Core.Availability;

namespace Ariva.Core.Reports;

/// <summary>
/// A lane's stored minutes of one local hour of the week (<c>queue_minute</c>, grouped by the database): entries, waits, the
/// longest queue, whether any minute was provisional or had waits without a histogram, and the hour's merged histogram
/// (30-second bucket indexes in ascending order and their counts). <see cref="Hour"/> is the hour's index in
/// <see cref="WeeklyCalendar.Hours"/>.
/// </summary>
public sealed record WeeklyHourRow(
    string ZoneKey,
    int Hour,
    long Entries,
    long Waits,
    int? MaxQueueLength,
    bool Provisional,
    bool HistogramMissing,
    IReadOnlyList<int> Buckets,
    IReadOnlyList<long> Counts);

/// <summary>A lane's stored bins of one local day (the latest revision of each), by status and quality. <see cref="Day"/> is 0 to 6.</summary>
public sealed record WeeklyBinRow(string ZoneKey, int Day, int Bins, int FinalBins, int GoodBins, int DegradedBins, int DegradedByCompletionOnlyBins, int UnknownBins);

/// <summary>
/// A lane's health checks of one local day (the latest revision of each bin): bins, final bins, tracks entered and exited over
/// final bins (each bin's exits capped at its entries), final bins below the track completion target, the residual over the
/// bins where it was measured, and the occupancy minutes.
/// </summary>
public sealed record WeeklyHealthRow(
    string ZoneKey,
    int Day,
    int Bins,
    int FinalBins,
    long TracksEntered,
    long TracksExited,
    int BinsBelowTrackTarget,
    int ResidualBins,
    long SumAbsResidual,
    long MaxAbsResidual,
    int OccupancyMinutes,
    int CapacityMinutes,
    int MinutesOutsideCapacity);

/// <summary>A lane's overflow minutes of one local day (TC-19), the minutes a band reading was observed, and the most people one band held.</summary>
public sealed record WeeklyOverflowRow(string ZoneKey, int Day, int OverflowMinutes, int ObservedMinutes, int? PeakBandOccupancy);

/// <summary>An alert raised in the week that the reader's roles see, with its lifecycle times.</summary>
public sealed record WeeklyAlertRow(
    string RuleCode,
    string RuleName,
    string ZoneName,
    string Severity,
    string State,
    DateTime RaisedUtc,
    DateTime? AcknowledgedUtc,
    DateTime? EscalatedUtc);

/// <summary>One desk's stored minutes (<c>desk_minute</c>) of one lane and local day, summed per state in seconds.</summary>
public sealed record WeeklyDeskRow(
    string CheckpointKind,
    string Checkpoint,
    string Desk,
    string Lane,
    int Day,
    int Minutes,
    double Closed,
    double Idle,
    double Serving,
    double Paused,
    double Unknown);

/// <summary>
/// Everything a weekly summary is built from: the site, its local week and time zone, what the reader may see (border desks,
/// airport desks, data quality), and the rows read for the week. The reader decides who may read the site and reads only
/// what the reader's roles allow; <see cref="WeeklySummaries.Build"/> projects again (fail closed).
/// </summary>
public sealed record WeeklySummaryInput(
    string SiteCode,
    DateOnly WeekStart,
    TimeZoneInfo TimeZone,
    DateTime GeneratedUtc,
    bool SeesBorderDesks,
    bool SeesAirportDesks,
    bool SeesDataQuality,
    IReadOnlyList<ReportZone> Zones,
    IReadOnlyList<WeeklyHourRow> Hours,
    IReadOnlyList<WeeklyBinRow> Bins,
    IReadOnlyList<WeeklyHealthRow> Health,
    IReadOnlyList<WeeklyOverflowRow> Overflow,
    IReadOnlyList<WeeklyAlertRow> Alerts,
    IReadOnlyList<ReportDevice> Devices,
    IReadOnlyDictionary<DateOnly, AvailabilityCounts> Availability,
    IReadOnlyList<WeeklyDeskRow> Desks);

/// <summary>A local day of the week and its UTC bounds.</summary>
public sealed record WeeklyCalendarDay(DateOnly Date, DateTime FromUtc, DateTime ToUtc);

/// <summary>A local hour of the week: its day (0 to 6), UTC bounds and local labels (the last hour of a day may be shorter).</summary>
public sealed record WeeklyCalendarHour(int Day, DateTime StartUtc, DateTime EndUtc, string Start, string End);

/// <summary>
/// The local week in UTC: seven days of 23 to 25 hours and every local hour in order (167 to 169 on a clock change). The
/// database groups minutes by these bounds, so the site's time zone is applied once, in .NET (SiteTimeZones).
/// </summary>
public sealed record WeeklyCalendar(DateOnly WeekStart, DateTime FromUtc, DateTime ToUtc, IReadOnlyList<WeeklyCalendarDay> Days, IReadOnlyList<WeeklyCalendarHour> Hours);

/// <summary>
/// The bounds of one weekly summary's reads (CWE-120): a read that would pass one is refused, never cut short. The defaults
/// are Proposed; tests lower them.
/// </summary>
/// <param name="MaxLanes">Queue zones of the site (published, or with minutes in the week).</param>
/// <param name="MaxDeskRows">Desk, lane and day rows of desk minutes.</param>
/// <param name="MaxAlerts">Alerts raised in the week that the reader sees.</param>
/// <param name="MaxDevices">Devices of the site.</param>
/// <param name="MaxOutages">Device outages that overlap the week.</param>
public sealed record WeeklySummaryLimits(int MaxLanes = 200, int MaxDeskRows = 30_000, int MaxAlerts = 20_000, int MaxDevices = 2_000, int MaxOutages = 20_000)
{
    public static WeeklySummaryLimits Default { get; } = new();
}
