namespace Ariva.Core.Availability;

/// <summary>What the ledger records for a minute (ARV-118).</summary>
public enum AvailabilityState
{
    /// <summary>Every published queue zone was live and has the minute.</summary>
    Available,

    /// <summary>At least one reason was proven (a stale zone, a missing minute, stream lag, no published zone).</summary>
    Unavailable,

    /// <summary>Decided after the fact (catch-up): every zone has the minute, but liveness can no longer be proven.</summary>
    Unobserved
}

/// <summary>The reasons the ledger records for a minute that is not available, by their stored names.</summary>
public static class AvailabilityReasons
{
    /// <summary>A zone's live snapshot was absent or older than the staleness threshold when the minute was decided.</summary>
    public const string StaleZone = "StaleZone";

    /// <summary>A zone has no queue_minute row for the minute.</summary>
    public const string MissingMinute = "MissingMinute";

    /// <summary>A zone's live state had not reached the minute, or trailed real time by more than the lag threshold.</summary>
    public const string StreamLag = "StreamLag";

    /// <summary>The site had no published queue zone at the minute.</summary>
    public const string NoPublishedZones = "NoPublishedZones";

    /// <summary>The minute was decided after its live window (the job was not running): liveness is not proven.</summary>
    public const string NotObservedLive = "NotObservedLive";

    public static readonly IReadOnlyList<string> All = [StaleZone, MissingMinute, StreamLag, NoPublishedZones, NotObservedLive];
}

/// <summary>
/// The thresholds of the availability rule (section <c>Availability</c>, Proposed values, formulas F18). Seconds:
/// <see cref="GraceSeconds"/> after a minute ends before it is decided (the stream's lateness allowance and checkpoint);
/// <see cref="LiveWindowSeconds"/> after that during which Redis may still be read for it; <see cref="StaleAfterSeconds"/>
/// the age of a live snapshot that makes a zone stale (the screens' threshold); <see cref="MaxLagSeconds"/> how far a zone's
/// live minute may trail real time. <see cref="MaxMinutesPerRun"/> bounds one site's catch-up in one run.
/// </summary>
public sealed class AvailabilitySettings
{
    public const string SectionName = "Availability";

    /// <summary>Run the ledger job in this host (Ariva.Api.Cronz).</summary>
    public bool Enabled { get; init; } = true;

    public int GraceSeconds { get; init; } = 120;
    public int LiveWindowSeconds { get; init; } = 180;
    public int StaleAfterSeconds { get; init; } = 150;
    public int MaxLagSeconds { get; init; } = 180;
    public int MaxMinutesPerRun { get; init; } = 1_440;

    /// <summary>Sites one run visits at most (CWE-400); the rest wait for the next run, in site code order.</summary>
    public int MaxSitesPerRun { get; init; } = 500;

    public TimeSpan Grace => TimeSpan.FromSeconds(GraceSeconds);
    public TimeSpan LiveWindow => TimeSpan.FromSeconds(LiveWindowSeconds);
    public TimeSpan StaleAfter => TimeSpan.FromSeconds(StaleAfterSeconds);
    public TimeSpan MaxLag => TimeSpan.FromSeconds(MaxLagSeconds);

    public IEnumerable<string> Problems()
    {
        if (GraceSeconds is < 30 or > 1_800)
            yield return "Availability:GraceSeconds is from 30 to 1,800.";
        if (LiveWindowSeconds is < 60 or > 1_800)
            yield return "Availability:LiveWindowSeconds is from 60 to 1,800.";
        if (StaleAfterSeconds is < 30 or > 3_600)
            yield return "Availability:StaleAfterSeconds is from 30 to 3,600.";
        if (MaxLagSeconds is < 30 or > 3_600)
            yield return "Availability:MaxLagSeconds is from 30 to 3,600.";
        if (MaxMinutesPerRun is < 10 or > 10_080)
            yield return "Availability:MaxMinutesPerRun is from 10 to 10,080.";
        if (MaxSitesPerRun is < 1 or > 10_000)
            yield return "Availability:MaxSitesPerRun is from 1 to 10,000.";
    }
}

/// <summary>A zone's live snapshot times as read from Redis: the latest minute it describes and when it was published.</summary>
public readonly record struct SnapshotTimes(DateTime MinuteUtc, DateTime PublishedUtc);

/// <summary>What the job found for one published queue zone and one minute.</summary>
public sealed record ZoneObservation(string ZoneKey, bool HasMinute, SnapshotTimes? Snapshot);

/// <summary>The ledger's decision for a minute, with the number of zones behind each reason.</summary>
public sealed record AvailabilityDecision(AvailabilityState State, IReadOnlyList<string> Reasons, int ZonesExpected, int ZonesStale, int ZonesMissing, int ZonesLagging);

/// <summary>
/// The availability rule (ARV-118, formulas F18, Proposed pending the owner and TC-83): a minute is available when every
/// queue zone of the site's zone profile in force at the minute has a live snapshot younger than the staleness threshold,
/// whose minute has reached the decided minute and trails real time by no more than the lag threshold, and a queue_minute
/// row for the minute. Decided once, after the grace period, from Redis and the database only.
/// <para>
/// Catch-up (deterministic): a minute decided after its live window cannot use Redis, which holds only the present. From
/// the database alone a missing queue_minute row proves the minute unavailable; when every zone has its row, liveness can
/// no longer be proven and the minute is Unobserved, which the ratio counts as not available. Past minutes are never
/// marked available from current Redis state.
/// </para>
/// </summary>
public static class AvailabilityRule
{
    /// <summary>The rule's version, stored with every row: a new rule writes new rows with a new version, never edits old ones.</summary>
    public const int Version = 1;

    /// <summary>
    /// A snapshot published further ahead of the job's clock than this, or describing a minute that ends further ahead than
    /// this, is implausible (clocks of two hosts, or a value no live stream could have written) and counts as stale: it is
    /// not evidence that the zone was live, and a minute far in the future would otherwise pass the reached and lag checks
    /// for as long as it stays in Redis (CWE-501).
    /// </summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromSeconds(60);

    public static DateTime FloorMinute(DateTime utc) => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);

    /// <summary>The latest minute that may be decided at <paramref name="nowUtc"/>: one whose end is at least the grace period ago.</summary>
    public static DateTime LatestDecidable(DateTime nowUtc, AvailabilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return FloorMinute(nowUtc - settings.Grace - TimeSpan.FromMinutes(1));
    }

    /// <summary>True while Redis may still be read for the minute: no later than the grace period plus the live window after its end.</summary>
    public static bool IsLive(DateTime minuteUtc, DateTime nowUtc, AvailabilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return nowUtc <= minuteUtc + TimeSpan.FromMinutes(1) + settings.Grace + settings.LiveWindow;
    }

    public static AvailabilityDecision Decide(DateTime minuteUtc, IReadOnlyCollection<ZoneObservation> zones, DateTime nowUtc, bool live, AvailabilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        zones ??= [];
        if (zones.Count == 0)
            return new AvailabilityDecision(AvailabilityState.Unavailable, [AvailabilityReasons.NoPublishedZones], 0, 0, 0, 0);

        var missing = zones.Count(z => !z.HasMinute);
        if (!live)
        {
            return missing > 0
                ? new AvailabilityDecision(AvailabilityState.Unavailable, [AvailabilityReasons.MissingMinute, AvailabilityReasons.NotObservedLive], zones.Count, 0, missing, 0)
                : new AvailabilityDecision(AvailabilityState.Unobserved, [AvailabilityReasons.NotObservedLive], zones.Count, 0, 0, 0);
        }

        int stale = 0, lagging = 0;
        foreach (var zone in zones)
        {
            if (zone.Snapshot is not { } snapshot || nowUtc - snapshot.PublishedUtc > settings.StaleAfter || snapshot.PublishedUtc - nowUtc > MaxAhead ||
                snapshot.MinuteUtc + TimeSpan.FromMinutes(1) - nowUtc > MaxAhead)
            {
                stale++;
                continue;
            }

            if (snapshot.MinuteUtc < minuteUtc || nowUtc - (snapshot.MinuteUtc + TimeSpan.FromMinutes(1)) > settings.MaxLag)
                lagging++;
        }

        var reasons = new List<string>(3);
        if (stale > 0)
            reasons.Add(AvailabilityReasons.StaleZone);
        if (missing > 0)
            reasons.Add(AvailabilityReasons.MissingMinute);
        if (lagging > 0)
            reasons.Add(AvailabilityReasons.StreamLag);
        return new AvailabilityDecision(reasons.Count == 0 ? AvailabilityState.Available : AvailabilityState.Unavailable, reasons, zones.Count, stale, missing, lagging);
    }
}
