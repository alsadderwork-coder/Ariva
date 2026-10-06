using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Queueing;

/// <summary>
/// The lines and zones of one queue zone in the published zone profile: where people enter (the queue's entry lines
/// and the entry lines of the overflow bands that feed it), where they leave, and the zones whose occupancy makes up
/// the queue length (the queue zone and its overflow bands). <see cref="CountLines"/> are the count lines of those
/// zones (ARV-113): their crossings are counted per line and minute, and never move the queue.
/// </summary>
public sealed record QueueZoneGeometry(
    string QueueZone,
    IReadOnlySet<string> EntryLines,
    IReadOnlySet<string> ExitLines,
    IReadOnlySet<string> OverflowEntryLines,
    IReadOnlySet<string> OverflowZones,
    IReadOnlySet<string> CountLines = null)
{
    private static readonly IReadOnlySet<string> NoLines = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The count lines of the queue zone and its overflow bands (none when not given).</summary>
    public IReadOnlySet<string> CountLines { get; init; } = CountLines ?? NoLines;

    /// <summary>The roles of the queue zone's lines, as the engine reads them.</summary>
    public QueueLineRole RoleOf(string lineName) =>
        lineName is null ? QueueLineRole.Unknown
        : EntryLines.Contains(lineName) ? QueueLineRole.Entry
        : ExitLines.Contains(lineName) ? QueueLineRole.Exit
        : OverflowEntryLines.Contains(lineName) ? QueueLineRole.OverflowEntry
        : CountLines.Contains(lineName) ? QueueLineRole.Count
        : QueueLineRole.Unknown;

    /// <summary>Whether a zone's occupancy counts in the queue length.</summary>
    public bool CountsInQueue(string zoneName) =>
        zoneName is not null && (string.Equals(zoneName, QueueZone, StringComparison.Ordinal) || OverflowZones.Contains(zoneName));
}

/// <summary>A line's role for the queue engine.</summary>
public enum QueueLineRole
{
    Unknown,
    Entry,
    Exit,
    OverflowEntry,

    /// <summary>A count line (ARV-113): counted per line and minute, never an entry or an exit of the queue.</summary>
    Count
}

/// <summary>
/// One sensing event for the queue engine, in event time (UTC, after the device clock correction of F19).
/// <see cref="Degraded"/> carries a data-quality concern from upstream (a skewed or corrected clock, F11).
/// </summary>
public abstract record QueueInput(DateTime TimeUtc, bool Degraded);

/// <summary>A line crossing; <see cref="TrackKey"/> is the device-namespaced track id, or null for an anonymous count.</summary>
public sealed record QueueCrossing(string LineName, CrossingDirection Direction, string TrackKey, DateTime TimeUtc, bool Degraded = false)
    : QueueInput(TimeUtc, Degraded);

/// <summary>A zone occupancy reading.</summary>
public sealed record QueueOccupancy(string ZoneName, int Count, DateTime TimeUtc, bool Degraded = false)
    : QueueInput(TimeUtc, Degraded);

/// <summary>Counts over [FromUtc, TimeUtc) on one line (T1 devices); people in and out, spread evenly over the interval.</summary>
public sealed record QueueInterval(string LineName, int In, int Out, DateTime FromUtc, DateTime TimeUtc, bool Degraded = false)
    : QueueInput(TimeUtc, Degraded);

/// <summary>A track was seen inside the zone (a position sample); used to tell a lost track (fragmented) from a waiting one.</summary>
public sealed record QueueTrackSeen(string TrackKey, DateTime TimeUtc, bool Degraded = false)
    : QueueInput(TimeUtc, Degraded);
