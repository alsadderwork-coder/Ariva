using Ariva.Core.Availability;

namespace Ariva.Core.Reports;

/// <summary>One local day of the week: its UTC bounds (23 or 25 hours on a clock change), whether it has started and ended.</summary>
public sealed record WeeklyDay(DateOnly Date, DateTime FromUtc, DateTime ToUtc, int Hours, bool Started, bool Ended);

/// <summary>
/// One local hour of a lane: passengers, waits, P50 and P90 from the hour's merged histogram (exact to 30 seconds, F7), the
/// longest queue, and whether it holds provisional minutes or minutes stored without a histogram. <see cref="Start"/> and
/// <see cref="End"/> are local "HH:mm" (a repeated hour on a clock change has the same label twice; <see cref="StartUtc"/>
/// tells them apart).
/// </summary>
public sealed record WeeklyHour(
    string Start,
    string End,
    DateTime StartUtc,
    long Passengers,
    long Waits,
    double? P50Minutes,
    double? P90Minutes,
    int? MaxQueueLength,
    bool Provisional,
    bool HistogramMissing);

/// <summary>
/// A lane's local day, as the daily report computes it (ARV-060): totals, the day's P50 and P90 from the merged histogram,
/// its peak hour (null without an hour of enough waits), overflow minutes, the bins stored for the day and how many were
/// Degraded (null without <c>DataQuality.View</c>), the alerts raised on the lane, and every local hour.
/// </summary>
public sealed record WeeklyLaneDay(
    DateOnly Date,
    long Passengers,
    long Waits,
    double? P50Minutes,
    double? P90Minutes,
    int? MaxQueueLength,
    WeeklyHour Peak,
    bool Provisional,
    bool HistogramMissing,
    int OverflowMinutes,
    int OverflowObservedMinutes,
    int? Bins,
    int? DegradedBins,
    int Alerts,
    IReadOnlyList<WeeklyHour> Hours);

/// <summary>A peak hour of the week: the lane, its local date and hour, and the hour's waits, P50 and P90.</summary>
public sealed record WeeklyPeakHour(
    string Zone,
    string LaneCategory,
    DateOnly Date,
    string Start,
    string End,
    DateTime StartUtc,
    long Waits,
    double? P50Minutes,
    double P90Minutes);

/// <summary>
/// Data quality over the week (F11, F18): the latest revision of every stored 15-minute bin (<c>queue_bin</c>) by status and
/// quality, Degraded bins flagged for their own track completion alone (ARV-114b) apart; and the continuous health checks
/// (<c>zone_health_bin</c>, ARV-114a): track completion over final bins (exited tracks, each bin capped at its entered tracks,
/// over entered tracks; null without a track), final bins below the target (0.90), the conservation residual's size over the
/// bins where it was measured, and the occupancy minutes checked against a physical capacity and outside it.
/// </summary>
public sealed record WeeklyQuality(
    int Bins,
    int FinalBins,
    int GoodBins,
    int DegradedBins,
    int DegradedByCompletionOnlyBins,
    int UnknownBins,
    int HealthBins,
    int FinalHealthBins,
    long TracksEntered,
    long TracksExited,
    double? TrackCompletion,
    int BinsBelowTrackTarget,
    int ResidualBins,
    long SumAbsResidual,
    long MaxAbsResidual,
    double? MeanAbsResidual,
    int OccupancyMinutes,
    int CapacityMinutes,
    int MinutesOutsideCapacity);

/// <summary>
/// A lane (queue zone) over the week: totals and P50 and P90 from every hour's histogram merged, the week's peak hour,
/// overflow minutes (TC-19: a minute when any band held anyone), the most people one band held, the alerts raised on it, its
/// data quality (null without <c>DataQuality.View</c>) and its seven local days.
/// </summary>
public sealed record WeeklyLane(
    string Zone,
    string LaneCategory,
    long Passengers,
    long Waits,
    double? P50Minutes,
    double? P90Minutes,
    int? MaxQueueLength,
    WeeklyPeakHour Peak,
    bool Provisional,
    bool HistogramMissing,
    int OverflowMinutes,
    int OverflowObservedMinutes,
    int? PeakBandOccupancy,
    int Alerts,
    WeeklyQuality Quality,
    IReadOnlyList<WeeklyLaneDay> Days);

/// <summary>Alerts of one rule raised in the week: its latest name and severity, how many, how many were acknowledged.</summary>
public sealed record WeeklyAlertRule(string RuleCode, string RuleName, string Severity, int Count, int Acknowledged);

/// <summary>A count for one local date.</summary>
public sealed record WeeklyCount(DateOnly Date, int Count);

/// <summary>
/// The alerts raised in the week that the reader's roles see (the alert screens' rule): counts by severity, rule and local
/// day; time to acknowledge over the acknowledged ones (raised to acknowledged, in minutes: nearest-rank P50 and P90, mean
/// and longest); alerts never acknowledged counted apart, still open or closed without an acknowledgement.
/// </summary>
public sealed record WeeklyAlerts(
    int Total,
    int Critical,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyList<WeeklyAlertRule> ByRule,
    IReadOnlyList<WeeklyCount> ByDay,
    int Acknowledged,
    int Escalated,
    int UnacknowledgedOpen,
    int UnacknowledgedClosed,
    double? AcknowledgeP50Minutes,
    double? AcknowledgeP90Minutes,
    double? AcknowledgeMeanMinutes,
    double? AcknowledgeMaxMinutes);

/// <summary>The availability ledger over the week's local dates (ARV-118, ARV-150): per day and in total, with the ratios.</summary>
public sealed record WeeklyAvailability(double PilotTarget, AvailabilityCounts Total, IReadOnlyList<AvailabilityDay> Days);

/// <summary>
/// Time in each desk state, in seconds (F10; <c>desk_minute</c>), over the desk minutes stored: no utilisation figure (its
/// definition, TC-45, is open).
/// </summary>
public sealed record DeskStateSeconds(int Minutes, long Closed, long Idle, long Serving, long Paused, long Unknown);

/// <summary>
/// Desk-state seconds of one local date, with the desks that had open time in it (idle, serving or paused above 0; for a desk
/// row, 1 or 0).
/// </summary>
public sealed record WeeklyDeskDay(DateOnly Date, int Desks, DeskStateSeconds Seconds);

/// <summary>
/// A lane's desks together, per checkpoint kind: the desks with open time (idle, serving or paused above 0) and the seconds per
/// state. For a reader without <c>BorderDesks.View</c>, a border lane is shown cell by cell only where at least three desks had
/// open time in the cell, so no figure is one or two desks' own (a small-cell rule; the immigration screen counts desks with
/// transactions instead): <see cref="Week"/> and <see cref="Desks"/> are null when fewer than three desks had open time in
/// the week (<see cref="WeekWithheld"/>), and <see cref="Days"/> is empty unless every day with open time had at least three
/// (<see cref="DaysWithheld"/>; never only some days, so the week less the days shown never gives a withheld day).
/// </summary>
public sealed record WeeklyDeskLane(
    string CheckpointKind,
    string Lane,
    int? Desks,
    bool WeekWithheld,
    bool DaysWithheld,
    DeskStateSeconds Week,
    IReadOnlyList<WeeklyDeskDay> Days);

/// <summary>One border desk's week (border data: <c>BorderDesks.View</c> only): the lanes it served and its seconds per state.</summary>
public sealed record WeeklyDesk(string CheckpointKind, string Checkpoint, string Desk, IReadOnlyList<string> Lanes, DeskStateSeconds Week, IReadOnlyList<WeeklyDeskDay> Days);

/// <summary>
/// Time in each desk state: lane aggregates for every reader (airport checkpoints' lanes only with <c>AirportDesks.View</c>),
/// and per-desk rows of the border desks only when <see cref="DesksIncluded"/> (the reader holds <c>BorderDesks.View</c>).
/// </summary>
public sealed record WeeklyDeskStates(bool DesksIncluded, IReadOnlyList<WeeklyDeskLane> Lanes, IReadOnlyList<WeeklyDesk> Desks);

/// <summary>
/// The weekly summary of a site (ARV-119): figures only, per site and local week (Monday to Sunday in the site's time zone,
/// Proposed), for the report tool of ARV-146 to lay out. Built from stored data: lane waits per day and hour, peak hours,
/// alerts with time to acknowledge, device uptime, data quality, overflow minutes, availability and time in each desk
/// state, each projected for the reader's roles. <see cref="Complete"/> is false while the week is still running (its
/// figures run to <see cref="GeneratedUtc"/>).
/// </summary>
public sealed record WeeklySummary(
    string SiteCode,
    string TimeZoneId,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    DateTime FromUtc,
    DateTime ToUtc,
    DateTime GeneratedUtc,
    bool Complete,
    int Hours,
    IReadOnlyList<WeeklyDay> Days,
    IReadOnlyList<WeeklyLane> Lanes,
    IReadOnlyList<WeeklyPeakHour> PeakHours,
    WeeklyAlerts Alerts,
    IReadOnlyList<DeviceUptime> Devices,
    WeeklyQuality DataQuality,
    WeeklyAvailability Availability,
    WeeklyDeskStates DeskStates);
