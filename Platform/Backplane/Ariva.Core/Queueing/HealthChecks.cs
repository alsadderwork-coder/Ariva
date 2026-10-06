namespace Ariva.Core.Queueing;

/// <summary>What became of the tracks (T3: people with a track of their own) that entered in one bin (F18, ARV-114a).</summary>
public sealed record TrackOutcomes(long Entered, long Exited, long Abandoned, long Fragmented, long Censored, long Rejected)
{
    public static readonly TrackOutcomes None = new(0, 0, 0, 0, 0, 0);

    /// <summary>Tracks that entered and are not resolved yet (never below zero).</summary>
    public long Open => Math.Max(0, Entered - Exited - Abandoned - Fragmented - Censored - Rejected);
}

/// <summary>The occupancy minutes of one bin (F18 occupancy sanity, ARV-114a).</summary>
/// <param name="Observed">Minutes with at least one occupancy reading of a zone of the queue.</param>
/// <param name="Checked">Minutes with a reading of a zone that has a physical capacity, so checked against it.</param>
/// <param name="Outside">Minutes with a reading below 0 or above its zone's physical capacity.</param>
public sealed record OccupancyMinutes(int Observed, int Checked, int Outside)
{
    public static readonly OccupancyMinutes None = new(0, 0, 0);
}

/// <summary>
/// The continuous health checks of one queue zone's bin (F18, ARV-114a), produced with every result of the bin
/// (<see cref="BinResult"/>, same start, revision and status) and stored beside it (zone_health_bin):
/// <list type="bullet">
/// <item>the conservation residual <c>(entries - exits) - (Occ(end) - Occ(start))</c>, from the bin's entries and exits
/// and the queue's sensor occupancy at the bin's two boundaries; null when either boundary has no sensor occupancy;</item>
/// <item>the track completion rate, tracks exited over tracks entered in the bin, with the tracks censored and still open
/// reported apart; null when no track entered;</item>
/// <item>the minutes whose occupancy readings left 0 to the zone's physical capacity.</item>
/// </list>
/// Thresholds and alarms are not applied here (Proposed values, ARV-114b).
/// </summary>
public sealed record ZoneHealthBin(
    string QueueZone,
    DateTime StartUtc,
    TimeSpan Length,
    int Revision,
    BinStatus Status,
    int ZoneProfileVersion,
    long Entries,
    long Exits,
    int? OccupancyStart,
    int? OccupancyEnd,
    long? ConservationResidual,
    long TracksEntered,
    long TracksExited,
    long TracksAbandoned,
    long TracksFragmented,
    long TracksCensored,
    long TracksRejected,
    long TracksOpen,
    double? TrackCompletionRate,
    int OccupancyMinutes,
    int CapacityMinutes,
    int MinutesOutsideCapacity);

/// <summary>The formulas of the continuous health checks (F18, ARV-114a). Pure: no I/O, no clock.</summary>
public static class HealthChecks
{
    /// <summary>
    /// r = (entries - exits) - (Occ(end) - Occ(start)); 0 when every person counted in or out shows in the occupancy.
    /// Positive: more counted in than the occupancy grew by (people the occupancy misses, or entries counted twice);
    /// negative: the occupancy grew by more than was counted in. Null without the occupancy at both boundaries.
    /// </summary>
    public static long? ConservationResidual(long entries, long exits, int? occupancyStart, int? occupancyEnd) =>
        occupancyStart is { } start && occupancyEnd is { } end ? entries - exits - ((long)end - start) : null;

    /// <summary>Tracks with outcome Exited over tracks that entered; null when none entered (no rate, never 0 or 1).</summary>
    public static double? TrackCompletionRate(long tracksEntered, long tracksExited) =>
        tracksEntered > 0 ? (double)Math.Min(tracksExited, tracksEntered) / tracksEntered : null;

    /// <summary>Whether a zone's readings in a minute left 0 to its physical capacity (no capacity: only below 0 counts).</summary>
    public static bool OutsideCapacity(int min, int max, int? capacity) => min < 0 || (capacity is { } c && max > c);

    /// <summary>The health of a bin, from its result, its tracks, the occupancy at its boundaries and its occupancy minutes.</summary>
    public static ZoneHealthBin Of(BinResult bin, TrackOutcomes tracks, int? occupancyStart, int? occupancyEnd, OccupancyMinutes minutes)
    {
        ArgumentNullException.ThrowIfNull(bin);
        tracks ??= TrackOutcomes.None;
        minutes ??= OccupancyMinutes.None;
        return new ZoneHealthBin(bin.QueueZone, bin.StartUtc, bin.Length, bin.Revision, bin.Status, bin.ZoneProfileVersion, bin.Entries, bin.Exits,
            occupancyStart, occupancyEnd, ConservationResidual(bin.Entries, bin.Exits, occupancyStart, occupancyEnd),
            tracks.Entered, tracks.Exited, tracks.Abandoned, tracks.Fragmented, tracks.Censored, tracks.Rejected, tracks.Open,
            TrackCompletionRate(tracks.Entered, tracks.Exited), minutes.Observed, minutes.Checked, minutes.Outside);
    }
}
