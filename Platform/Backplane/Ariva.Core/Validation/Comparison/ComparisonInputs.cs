using Ariva.Core.Availability;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;

namespace Ariva.Core.Validation.Comparison;

/// <summary>A queue zone in a campaign's scope: its id (tracer runs name it) and its name (the stored outputs are keyed by it).</summary>
public sealed record ScopeZone(Guid ZoneId, string Name);

/// <summary>
/// A line in a campaign's scope (ARV-104a): its id (manual counts name it), its name and role in the campaign's profile version,
/// and the queue zone whose key counts it (its own zone, or the queue zone of its overflow band), as line_minute keys it.
/// </summary>
public sealed record ScopeLine(Guid LineId, string Name, LineRole Role, string QueueZone);

/// <summary>A UTC interval [<see cref="FromUtc"/>, <see cref="ToUtc"/>): one planned local day of a campaign.</summary>
public sealed record UtcWindow(DateTime FromUtc, DateTime ToUtc);

/// <summary>
/// A border desk in a campaign's scope (ARV-104b, ARV-104f): its id (desk observations name it) and its checkpoint and desk
/// codes, the key of its <c>desk_minute</c> rows (site/checkpoint/desk) without the site. A desk code names a counter, never
/// a person; desk-level border data all the same (data boundary).
/// </summary>
public sealed record ScopeDesk(Guid DeskId, string CheckpointCode, string DeskCode);

/// <summary>
/// What a campaign compares (ARV-104e, ARV-104f, formulas F18): the profile version every stored output must carry, the
/// queue zones and lines in scope, the planned local days as UTC windows (track completion is computed over their 15-minute
/// bins, the nowcast error over their minutes) and the border desks in scope (<see cref="Desks"/>).
/// </summary>
public sealed record ComparisonScope(int ProfileVersion, IReadOnlyList<ScopeZone> Zones, IReadOnlyList<ScopeLine> Lines, IReadOnlyList<UtcWindow> Windows)
{
    /// <summary>
    /// The border desks whose observed minutes the desk-state agreement compares (ARV-104f); none for a campaign planned
    /// without desks. A null list counts as none.
    /// </summary>
    public IReadOnlyList<ScopeDesk> Desks { get; init; } = [];

    /// <summary>The scope of a campaign: its version, zones, lines and desks as planned, and its planned days in the site's time zone.</summary>
    public static ComparisonScope Of(ValidationCampaign campaign, TimeZoneInfo siteZone)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return new ComparisonScope(campaign.ProfileVersion,
            [.. campaign.Zones.Select(z => new ScopeZone(z.ZoneId, z.ZoneName))],
            [.. campaign.Lines.Select(l => new ScopeLine(l.LineId, l.LineName, l.LineRole, l.QueueZoneName))],
            WindowsOf(campaign.Days, siteZone))
        {
            Desks = [.. campaign.Desks.Select(d => new ScopeDesk(d.DeskId, d.CheckpointCode, d.DeskCode))]
        };
    }

    /// <summary>
    /// The UTC windows of local days in a time zone: each from its local start to the next day's (a day whose midnight a clock
    /// change skips starts at its first valid minute, as <see cref="OperatingCalendar.DayStartUtc"/>). A 15-minute UTC bin
    /// belongs to the day its start falls in, as a manual count's bin does (<see cref="ValidationCampaign.LocalDay"/>).
    /// </summary>
    public static IReadOnlyList<UtcWindow> WindowsOf(IEnumerable<DateOnly> days, TimeZoneInfo siteZone)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(siteZone);
        return [.. days.Distinct().Order().Select(day => new UtcWindow(OperatingCalendar.DayStartUtc(day, siteZone), OperatingCalendar.DayStartUtc(day.AddDays(1), siteZone)))];
    }
}

/// <summary>
/// One revision of one observer's manual count of a line and 15-minute bin (manual_count, ARV-104a). The engine takes the
/// highest revision of each line, bin and observer, so the caller may pass the latest revisions or every revision.
/// </summary>
public sealed record ManualCountRow(Guid LineId, DateTime BinStartUtc, Guid ObserverId, int Revision, int CrossingsIn, int CrossingsOut)
{
    public static ManualCountRow Of(ManualCount count)
    {
        ArgumentNullException.ThrowIfNull(count);
        return new ManualCountRow(count.LineId, count.BinStartUtc, count.ObserverId, count.Revision, count.CrossingsIn, count.CrossingsOut);
    }
}

/// <summary>
/// A tracer run as stored (tracer_run, ARV-104b): the device's times, the batch's measured clock offset (device minus server,
/// milliseconds) and the corrected times (device time minus the offset), the abandoned flag. The tracer is a campaign label
/// and the observer an Ariva user id (data boundary). Runs are never corrected (owner decision 2026-10-08).
/// </summary>
public sealed record TracerRunRow(
    Guid RunId,
    Guid BatchId,
    Guid ObserverId,
    Guid ZoneId,
    string TracerCode,
    DateTime JoinedRawUtc,
    DateTime ExitedRawUtc,
    int ClockOffsetMs,
    DateTime JoinedUtc,
    DateTime ExitedUtc,
    bool Abandoned)
{
    public static TracerRunRow Of(TracerRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new TracerRunRow(run.Id.GetValueOrDefault(), run.BatchId, run.ObserverId, run.ZoneId, run.TracerCode, run.JoinedRawUtc, run.ExitedRawUtc,
            run.ClockOffsetMs, run.JoinedUtc, run.ExitedUtc, run.Abandoned);
    }
}

/// <summary>
/// A line's crossings in one 15-minute UTC bin under one profile version, as line_minute_15m sums line_minute (ARV-113): the
/// queue zone (the zone key without its site), the line name, the bin, the version and the crossings in and out. Only
/// <see cref="LineCountSource.Ariva"/> rows are compared; a vendor's crossings are a cross-check, never N_system.
/// </summary>
public sealed record LineBinCount(string QueueZone, string LineName, DateTime BinStartUtc, int ProfileVersion, long CrossingsIn, long CrossingsOut,
    LineCountSource Source = LineCountSource.Ariva);

/// <summary>
/// A stored minute of a queue zone (queue_minute, F5 to F8): the realised waits of the people who entered in it (how many, and
/// their mean in minutes; null with none) and the status (null for a row that holds only the live queue length and nowcast),
/// and its live part (ARV-104f): the published nowcast computed at the minute's end, in minutes, or why there is none
/// (<see cref="NoService"/>, a <see cref="NoServiceReason"/> name as stored: <c>queue_minute.no_service</c> has no check, so
/// the engine checks the name exactly), and the nowcast's own F11 flag. A row written without a live part has all three null;
/// a live part holds the flag and exactly one of a number and a reason.
/// </summary>
public sealed record QueueMinuteRow(
    string QueueZone,
    DateTime MinuteUtc,
    int ProfileVersion,
    BinStatus? Status,
    long Waits,
    double? MeanWaitMinutes,
    double? NowcastMinutes = null,
    string NoService = null,
    bool? NowcastDegraded = null);

/// <summary>
/// The shadow nowcast of a queue zone's minute (its own table since ARV-117a, script 0043; ARV-117, F8): the nowcast without AMAN
/// inputs, computed with the published one at the minute's end, in minutes, or why there is none (a
/// <see cref="NoServiceReason"/> name), its F11 flag, and the sensor cycle time it took (ARV-117b, script 0045; null when it
/// fell back). Keyed like queue_minute; it carries no profile version of its own, so the version is that of the queue_minute
/// row of the same key. Validation data only: readable only through the <c>ariva_validation_reader</c> role (ARV-104g),
/// never shown, alerted on or reported elsewhere. A lane aggregate: no desk code, no identity.
/// </summary>
public sealed record ShadowMinuteRow(string QueueZone, DateTime MinuteUtc, double? NowcastMinutes, string NoService, bool NowcastDegraded, double? SensorCycleMinutes);

/// <summary>
/// One revision of one observer's state of a border desk for one minute (desk_observation, ARV-104b). The engine takes the
/// highest revision of each desk, minute and observer, so a correction (a later revision) replaces the state it corrects and
/// the caller may pass the latest revisions or every revision. The observer is an Ariva user id, never a name; the reason a
/// correction gives is not read. Desk-level border data (data boundary).
/// </summary>
public sealed record DeskObservationRow(Guid DeskId, DateTime MinuteUtc, Guid ObserverId, int Revision, ObservedDeskState State)
{
    public static DeskObservationRow Of(DeskObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new DeskObservationRow(observation.DeskId, observation.MinuteUtc, observation.ObserverId, observation.Revision, observation.State);
    }
}

/// <summary>
/// A border desk's stored minute (desk_minute, F10, F11): the seconds it spent in each state and its F11 flag, naming the desk
/// by its checkpoint and desk codes (the key site/checkpoint/desk without the site). Transactions, sensor-derived and
/// present-not-processing time are not read. Per-desk aggregates with no officer, traveller or document identity, and
/// desk-level border data all the same (data boundary).
/// </summary>
public sealed record DeskMinuteRow(
    string CheckpointCode,
    string DeskCode,
    DateTime MinuteUtc,
    double ClosedSeconds,
    double IdleSeconds,
    double ServingSeconds,
    double PausedSeconds,
    double UnknownSeconds,
    bool Degraded);

/// <summary>
/// One revision of a stored bin of a queue zone (queue_bin, F6, F11): its status, data quality, version, entries and the
/// entrants who abandoned. The engine takes each bin's highest revision.
/// </summary>
public sealed record QueueBinRow(string QueueZone, DateTime StartUtc, TimeSpan Length, int Revision, BinStatus Status, BinQuality Quality, int ProfileVersion,
    long Entries, long Abandoned);

/// <summary>
/// A period of degraded or unknown data quality of a queue zone (F11): a device outage of zone_outage is
/// <see cref="BinQuality.Degraded"/> from its start to its end (an outage still open ends where the caller reads to); an
/// entry or exit line without coverage is <see cref="BinQuality.Unknown"/>.
/// </summary>
public sealed record QualityInterval(string QueueZone, DateTime FromUtc, DateTime ToUtc, BinQuality Quality);

/// <summary>
/// Everything the comparison engine reads (ARV-104e, ARV-104f): the campaign's scope, its ground truth (manual counts, tracer
/// runs and desk observations) and the stored outputs it is judged against (line counts per bin, queue minutes with their
/// published nowcast, the shadow nowcast, queue bins, zone health bins as <see cref="ZoneHealthBin"/>, desk minutes, and
/// data-quality intervals). Stored rows name their queue zone without the site (the zone key's second part), and desk minutes
/// their checkpoint and desk codes without it. A list left null counts as empty; every list is read row by row, its own
/// <c>Count</c> never used.
/// </summary>
public sealed record ComparisonInput
{
    public ComparisonScope Scope { get; init; }
    public IReadOnlyList<ManualCountRow> ManualCounts { get; init; } = [];
    public IReadOnlyList<TracerRunRow> TracerRuns { get; init; } = [];
    public IReadOnlyList<LineBinCount> LineBins { get; init; } = [];
    public IReadOnlyList<QueueMinuteRow> QueueMinutes { get; init; } = [];
    public IReadOnlyList<QueueBinRow> QueueBins { get; init; } = [];
    public IReadOnlyList<ZoneHealthBin> HealthBins { get; init; } = [];
    public IReadOnlyList<QualityInterval> QualityIntervals { get; init; } = [];

    /// <summary>The observers' desk states (ARV-104b), any revisions: the ground truth of the desk-state agreement.</summary>
    public IReadOnlyList<DeskObservationRow> DeskObservations { get; init; } = [];

    /// <summary>The stored desk minutes (desk_minute) of the desks in scope: what the desk-state agreement judges.</summary>
    public IReadOnlyList<DeskMinuteRow> DeskMinutes { get; init; } = [];

    /// <summary>The stored shadow nowcasts (ARV-117a, script 0043): the ground-truth proof's nowcast without AMAN.</summary>
    public IReadOnlyList<ShadowMinuteRow> ShadowMinutes { get; init; } = [];
}
