namespace Ariva.Core.Desks;

/// <summary>
/// The key of a desk in the stream's desk tables (<c>desk_minute</c>, <c>desk_zone_reading</c>): site, checkpoint and
/// desk code (unique while the desk lives). The AMAN desk feed and the zone processor build it the same way, so a desk's
/// AMAN signals and its staff and service zone readings meet in one engine (ARV-049, ARV-116).
/// </summary>
public static class DeskKeys
{
    /// <summary>The longest key the desk tables hold (<c>desk_minute.desk_code</c>, varchar(64)); the desk engine refuses longer codes.</summary>
    public const int MaxLength = 64;

    public static string For(string siteCode, string checkpointCode, string deskCode) => $"{siteCode}/{checkpointCode}/{deskCode}";

    /// <summary>Whether a key fits the desk tables and the engine.</summary>
    public static bool Fits(string key) => key is { Length: > 0 and <= MaxLength } && !string.IsNullOrWhiteSpace(key);
}

/// <summary>
/// A staff or service zone of the published zone profile matched to its desk through the profile (the zone names its
/// desk, ARV-016): readings of the zone are the desk's rank 3 (<see cref="DeskSource.StaffZone"/>) or rank 4
/// (<see cref="DeskSource.ServiceZone"/>) signal (F10).
/// </summary>
public sealed record DeskZoneLink(string DeskKey, DeskSource Source);

/// <summary>
/// One occupancy reading of a desk's staff or service zone as it crosses from the zone processor to the desk engine
/// (ARV-116): the desk, the zone's role, the reading's time, the count, and whether the sensing pipeline flagged it (a
/// corrected or unreliable clock, F11). Counts only: no track, officer, traveller or document identity crosses.
/// </summary>
public sealed record DeskZoneSample(string DeskKey, DeskSource Source, DateTime TimeUtc, int Count, bool Degraded)
{
    /// <summary>The desk engine's signal for this reading.</summary>
    public DeskZoneReading ToSignal() => new(DeskKey, TimeUtc, Source, Count, Degraded);
}

/// <summary>What a queue zone remembers of one desk zone's latest reading passed on, as a snapshot holds it.</summary>
public sealed record DeskZoneMemoState(string DeskKey, DeskSource Source, DateTime LastTimeUtc, int LastCount, bool LastDegraded, int InMinute);

/// <summary>Counts of what <see cref="DeskZoneReadings"/> did with the readings offered (for health).</summary>
public sealed record DeskZoneReadingCounters(long Passed, long Unchanged, long Superseded, long Capped, long Invalid);

/// <summary>
/// The staff and service zone readings of one queue zone on their way to the desk engine (ARV-116, F10 ranks 3 and 4).
/// The queue zone's processor hands over each reading of a zone that names a desk; this passes on a
/// <see cref="DeskZoneSample"/> (counts only) and keeps the volume bounded whatever a device sends (CWE-120):
/// <list type="bullet">
/// <item>a reading not later than the latest passed on for its desk and zone role is superseded (the desk engine would
/// supersede it too), so each desk zone's samples are in time order;</item>
/// <item>a reading equal to the latest passed on (count and flag) within <see cref="KeepAliveEvery"/> of it adds nothing
/// and is dropped; the source still counts as heard, since the desk engine's staleness (T_stale, 2 minutes) is far longer;</item>
/// <item>at most <see cref="MaxPerDeskZoneMinute"/> samples per desk zone and minute; beyond, the rest of that minute is
/// dropped and counted (the next minute's first reading brings the state back);</item>
/// <item>a count above <see cref="MaxCount"/> is not a desk (the desk engine's own limit) and is refused.</item>
/// </list>
/// The memory is one entry per linked desk zone, so it is bounded by the profile. Pure: no I/O and no clock.
/// </summary>
public sealed class DeskZoneReadings
{
    /// <summary>A reading equal to the latest passed on within this time is dropped as adding nothing.</summary>
    public static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(15);

    /// <summary>Samples passed on per desk zone and minute at most (Proposed).</summary>
    public const int MaxPerDeskZoneMinute = 60;

    /// <summary>People a desk zone may count; more is not a desk (as <see cref="DeskStateSettings.MaxZoneCount"/> by default).</summary>
    public const int MaxCount = 50;

    /// <summary>Desk zones one queue zone links at most (a zone profile holds at most 500 zones).</summary>
    public const int MaxLinks = 1_000;

    private readonly IReadOnlyDictionary<string, DeskZoneLink> _links;
    private readonly Dictionary<(string Desk, DeskSource Source), Memo> _memo = [];
    private long _passed, _unchanged, _superseded, _capped, _invalid;

    private sealed record Memo(DateTime LastTimeUtc, int LastCount, bool LastDegraded, int InMinute);

    /// <param name="links">The queue zone's desk zones by zone name; each desk has at most one staff and one service zone.</param>
    public DeskZoneReadings(IReadOnlyDictionary<string, DeskZoneLink> links)
    {
        _links = links ?? new Dictionary<string, DeskZoneLink>(StringComparer.Ordinal);
        if (_links.Count > MaxLinks)
            throw new ArgumentException($"A queue zone links at most {MaxLinks} desk zones.", nameof(links));
        var seen = new HashSet<(string, DeskSource)>();
        foreach (var (name, link) in _links)
        {
            if (string.IsNullOrEmpty(name) || link is null || !DeskKeys.Fits(link.DeskKey) || link.Source is not (DeskSource.StaffZone or DeskSource.ServiceZone))
                throw new ArgumentException("Every desk zone names a desk key of at most 64 characters and is a staff or service zone.", nameof(links));
            if (!seen.Add((link.DeskKey, link.Source)))
                throw new ArgumentException("A desk has at most one staff zone and one service zone.", nameof(links));
        }
    }

    /// <summary>Whether <paramref name="zoneName"/> is a desk zone of this queue zone.</summary>
    public bool Links(string zoneName) => zoneName is not null && _links.ContainsKey(zoneName);

    public DeskZoneReadingCounters Counters => new(_passed, _unchanged, _superseded, _capped, _invalid);

    /// <summary>
    /// Takes one reading of a linked desk zone; returns the sample to pass on, or null when it is dropped (not a desk
    /// zone, invalid, superseded, unchanged or over the minute's bound).
    /// </summary>
    public DeskZoneSample Accept(string zoneName, int count, DateTime timeUtc, bool degraded)
    {
        if (zoneName is null || !_links.TryGetValue(zoneName, out var link))
            return null;
        if (count is < 0 or > MaxCount || timeUtc.Kind != DateTimeKind.Utc)
        {
            _invalid++;
            return null;
        }

        var key = (link.DeskKey, link.Source);
        var minute = Floor(timeUtc);
        var inMinute = 1;
        if (_memo.TryGetValue(key, out var last))
        {
            if (timeUtc <= last.LastTimeUtc)
            {
                _superseded++;
                return null;
            }

            if (count == last.LastCount && degraded == last.LastDegraded && timeUtc - last.LastTimeUtc < KeepAliveEvery)
            {
                _unchanged++;
                return null;
            }

            if (Floor(last.LastTimeUtc) == minute)
            {
                if (last.InMinute >= MaxPerDeskZoneMinute)
                {
                    _capped++;
                    return null;
                }

                inMinute = last.InMinute + 1;
            }
        }

        _memo[key] = new Memo(timeUtc, count, degraded, inMinute);
        _passed++;
        return new DeskZoneSample(link.DeskKey, link.Source, timeUtc, count, degraded);
    }

    /// <summary>The memory, in desk and role order (plain data for the zone snapshot).</summary>
    public IReadOnlyList<DeskZoneMemoState> Capture() =>
    [
        .. _memo.OrderBy(m => m.Key.Desk, StringComparer.Ordinal).ThenBy(m => m.Key.Source)
            .Select(m => new DeskZoneMemoState(m.Key.Desk, m.Key.Source, m.Value.LastTimeUtc, m.Value.LastCount, m.Value.LastDegraded, m.Value.InMinute))
    ];

    /// <summary>
    /// The readings filter with a captured memory. A snapshot is data from storage (CWE-501): at most <see cref="MaxLinks"/>
    /// entries, each desk zone once, with a count and per-minute tally within bounds and a plausible time not more than
    /// a day after <paramref name="notAfterUtc"/> (as the rest of the zone snapshot); anything else is an <see cref="InvalidDataException"/>. An entry for a desk zone the
    /// links no longer hold (a desk renamed) is forgotten.
    /// </summary>
    public static DeskZoneReadings Restore(IReadOnlyDictionary<string, DeskZoneLink> links, IReadOnlyList<DeskZoneMemoState> memo, DateTime notAfterUtc)
    {
        var restored = new DeskZoneReadings(links);
        if ((memo?.Count ?? 0) > MaxLinks)
            throw new InvalidDataException($"The zone snapshot remembers more than {MaxLinks} desk zones.");
        var linked = restored._links.Values.Select(l => (l.DeskKey, l.Source)).ToHashSet();
        // As for the rest of the zone snapshot: not after the host's clock plus a day (SnapshotChecks).
        var latest = notAfterUtc < DateTime.MaxValue.AddDays(-1) ? notAfterUtc.AddDays(1) : DateTime.MaxValue;
        foreach (var m in memo ?? [])
        {
            if (m is null)
                throw new InvalidDataException("The zone snapshot has an empty desk zone entry.");
            // A desk renamed or unlinked since (same profile version, another desk key) is forgotten: its next reading passes.
            if (!linked.Contains((m.DeskKey, m.Source)))
                continue;
            var time = DateTime.SpecifyKind(m.LastTimeUtc, DateTimeKind.Utc);
            if (time < Earliest || time > latest || m.LastCount is < 0 or > MaxCount || m.InMinute is < 1 or > MaxPerDeskZoneMinute)
                throw new InvalidDataException("The zone snapshot has a desk zone reading with a time, count or tally out of bounds.");
            if (!restored._memo.TryAdd((m.DeskKey, m.Source), new Memo(time, m.LastCount, m.LastDegraded, m.InMinute)))
                throw new InvalidDataException("The zone snapshot remembers a desk zone twice.");
        }

        return restored;
    }

    private static readonly DateTime Earliest = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}
