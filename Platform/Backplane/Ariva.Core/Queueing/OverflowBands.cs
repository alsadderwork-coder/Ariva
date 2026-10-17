using Ariva.Core.Sensing;

namespace Ariva.Core.Queueing;

/// <summary>
/// One overflow band of a queue zone in one closed minute (ARV-115, script 0039): the lowest and highest occupancy its
/// sensors reported in the minute. Closed means the engine's watermark has passed the minute's end, so the values never
/// change afterwards (events behind the watermark are late and not applied, F6). A minute is an overflow minute when the
/// band held anyone in it (<see cref="Occupied"/>: the highest reading above zero; TC-19, decided by the owner on
/// 2026-10-06). A minute without a reading has no row: the band's occupancy is unknown then, never zero.
/// </summary>
public sealed record OverflowMinute(string BandName, DateTime MinuteUtc, int MinOccupancy, int MaxOccupancy)
{
    /// <summary>The band held anyone in the minute (TC-19, decided 2026-10-06: a minute with any occupancy).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Occupied => MaxOccupancy > 0;
}

/// <summary>Whether an overflow band became occupied, emptied, or unknown (silent beyond the freshness window).</summary>
public enum OverflowChangeKind
{
    Occupied,
    Emptied,
    Unknown
}

/// <summary>
/// An overflow band became occupied (its first minute with occupancy after an empty, unknown or unheard spell), emptied
/// (its first minute with readings and no occupancy after an occupied or unknown spell), or unknown (silent beyond the
/// occupancy freshness window after it was occupied or empty), at the start of <see cref="MinuteUtc"/> (ARV-115).
/// <see cref="PeakOccupancy"/> is the highest reading of that minute when it became occupied, and of the whole occupied
/// spell when it emptied or became unknown while occupied (0 otherwise); <see cref="OccupiedSinceUtc"/> is that spell's
/// first minute, on Emptied or Unknown after an occupied spell only. Aggregates only: counts and times, no identity
/// (data boundary).
/// </summary>
public sealed record OverflowChange(string BandName, DateTime MinuteUtc, OverflowChangeKind Kind, int PeakOccupancy, DateTime? OccupiedSinceUtc);

/// <summary>An overflow band's readings in a minute still open (the watermark has not passed its end), as a snapshot holds them.</summary>
public sealed record OverflowMinuteState(string BandName, DateTime MinuteUtc, int Min, int Max);

/// <summary>
/// An overflow band's state between minutes, as a snapshot holds it: occupied since a minute (with the spell's peak so
/// far), empty, or <see cref="Unknown"/> (silent beyond the freshness window; neither occupied nor empty), and the latest
/// closed minute it had readings in. A band not listed was never heard. <see cref="Unknown"/> was added to version 6 of
/// the zone snapshot; an earlier snapshot has no such property and restores with it false, as before.
/// </summary>
public sealed record OverflowBandState(string BandName, bool Occupied, DateTime? OccupiedSinceUtc, int PeakOccupancy, DateTime LastMinuteUtc, bool Unknown = false);

/// <summary>
/// The overflow bands of one queue zone (ARV-115): each band's occupancy readings, taken from the engine's steps (the
/// lowest and highest reading per zone and minute, <see cref="QueueStep.Readings"/>), are held per minute until the
/// watermark passes the minute's end, then released as an <see cref="OverflowMinute"/> in minute and then band order.
/// Each released minute moves the band's state: the first minute with occupancy after an empty spell raises
/// <see cref="OverflowChangeKind.Occupied"/>, the first minute with readings and no occupancy after an occupied spell
/// raises <see cref="OverflowChangeKind.Emptied"/>, so each change is reported once. A band starts empty.
/// <para>
/// Silence (decided by the owner, 2026-10-06: "Unknown after timeout"): a minute without readings is not an empty minute,
/// and a band stays as it was while its latest readings are fresh. A band occupied or empty whose latest minute with
/// readings is L becomes <see cref="OverflowChangeKind.Unknown"/> at minute U, the first minute that starts after every
/// reading of minute L has gone stale (L plus one minute plus the occupancy freshness window, 2 minutes by default,
/// rounded up to a whole minute: L + 3), when no reading arrived in the minutes L + 1 through U; it is decided once the
/// watermark has passed U's end, so it is pure and replays the same. A reading in any of those minutes keeps the band's
/// state (no flapping within the window). The first minute with readings after Unknown makes the band occupied or
/// empty again, each with a change (Occupied, or Emptied with a peak of 0). A band never heard is not Unknown: it has
/// no state to lose. Changes come out in minute and then band order, silences among the band minutes.
/// </para>
/// <para>
/// Pure and bounded (CWE-120): only the bands of the geometry are kept, at most <see cref="MaxOpenBandMinutes"/> minutes
/// are held open (the engine applies events only up to its watermark, so in practice only the watermark's own minute is
/// open). Beyond the bound the earliest is released early; a later part of that minute is released as another row for
/// the same band and minute, which the store merges (lowest of the lows, highest of the highs: the same row whatever the
/// order), and it moves the band's state only when it says the band was occupied while it was empty.
/// </para>
/// </summary>
public sealed class OverflowBands
{
    /// <summary>Band minutes held open at most (a profile's 500 zones times the minutes one step can leave open).</summary>
    public const int MaxOpenBandMinutes = 5_000;

    private readonly QueueZoneGeometry _geometry;
    private readonly TimeSpan _freshFor;
    private readonly SortedDictionary<(DateTime Minute, string Band), (int Min, int Max)> _open = new(KeyOrder.Instance);
    private readonly SortedDictionary<string, OverflowBandState> _bands = new(StringComparer.Ordinal);
    private readonly List<OverflowChange> _changes = [];
    private bool _watchSilence = true;

    /// <param name="geometry">The queue zone and its overflow bands.</param>
    /// <param name="occupancyFreshFor">
    /// How long an occupancy reading stays fresh (<see cref="QueueEngineSettings.OccupancyFreshFor"/>, 2 minutes by
    /// default; from 1 tick to 1 hour); a band silent beyond it becomes Unknown.
    /// </param>
    public OverflowBands(QueueZoneGeometry geometry, TimeSpan? occupancyFreshFor = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var fresh = occupancyFreshFor ?? DefaultFreshFor;
        if (fresh <= TimeSpan.Zero || fresh > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(occupancyFreshFor), "The occupancy freshness is from 1 tick to 1 hour.");
        _geometry = geometry;
        _freshFor = fresh;
    }

    private static readonly TimeSpan DefaultFreshFor = TimeSpan.FromMinutes(2);

    /// <summary>Band minutes still open.</summary>
    public int Open => _open.Count;

    /// <summary>Whether a band of the zone is occupied now (as of its latest closed minute with readings).</summary>
    public bool IsOccupied(string bandName) => bandName is not null && _bands.TryGetValue(bandName, out var band) && band.Occupied;

    /// <summary>Whether a band of the zone is Unknown now (silent beyond the freshness window since its latest readings).</summary>
    public bool IsUnknown(string bandName) => bandName is not null && _bands.TryGetValue(bandName, out var band) && band.Unknown;

    /// <summary>
    /// The minute a band whose latest minute with readings is <paramref name="lastMinuteUtc"/> becomes Unknown if it stays
    /// silent: the first minute that starts after the latest possible reading of that minute is stale.
    /// </summary>
    public DateTime UnknownFrom(DateTime lastMinuteUtc)
    {
        var stale = lastMinuteUtc.AddMinutes(1).AddTicks(-1) + _freshFor;
        var floor = new DateTime(stale.Ticks - stale.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        return floor == stale ? floor : floor.AddMinutes(1);
    }

    /// <summary>
    /// Adds a step's readings of the overflow bands and returns the band minutes the step closed (minute, then band
    /// order) and the changes they made, in the same order.
    /// </summary>
    public (IReadOnlyList<OverflowMinute> Minutes, IReadOnlyList<OverflowChange> Changes) Accept(QueueStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        foreach (var r in step.Readings ?? [])
        {
            if (r is null || !IsBand(r.ZoneName) || r.Min < 0 || r.Max < r.Min || r.Max > CanonicalEventRules.MaxOccupancy)
                continue;
            var key = (r.MinuteUtc, r.ZoneName);
            _open[key] = _open.TryGetValue(key, out var seen) ? (Math.Min(seen.Min, r.Min), Math.Max(seen.Max, r.Max)) : (r.Min, r.Max);
        }

        var closed = new List<OverflowMinute>();
        _changes.Clear();
        // A minute is closed when every event inside it is at or before the watermark.
        while (_open.Count > 0)
        {
            var (key, value) = _open.First();
            if (!Passed(key.Minute, step.WatermarkUtc) && _open.Count <= MaxOpenBandMinutes)
                break;
            // Silences that come before this band minute go first, so changes stay in minute and then band order.
            Silence(key, step.WatermarkUtc);
            _open.Remove(key);
            var minute = new OverflowMinute(key.Band, key.Minute, value.Min, value.Max);
            closed.Add(minute);
            Move(minute);
        }

        Silence((DateTime.MaxValue, null), step.WatermarkUtc);
        return (closed, [.. _changes]);
    }

    /// <summary>
    /// Stops deciding silences (a replay at the end of its range, <see cref="ZoneProcessor.Finish"/>): the inputs end there,
    /// which is not a silent sensor, as device outages are not reported for it either. Band minutes still close.
    /// </summary>
    public void StopWatchingSilence() => _watchSilence = false;

    // Every band occupied or empty whose silence minute U has closed (the watermark passed its end) and comes before
    // `before` in minute and then band order becomes Unknown at U. A reading of the band in a minute up to U would have
    // been released before U closed (the open minutes are released in order), so such a band is never silent here.
    private void Silence((DateTime Minute, string Band) before, DateTime watermarkUtc)
    {
        if (!_watchSilence)
            return;
        List<(DateTime Minute, string Band)> silent = null;
        foreach (var band in _bands.Values)
        {
            if (band.Unknown || band.LastMinuteUtc == DateTime.MinValue)
                continue;
            var at = UnknownFrom(band.LastMinuteUtc);
            if (Passed(at, watermarkUtc) && (before.Band is null || KeyOrder.Instance.Compare((at, band.BandName), before) < 0))
                (silent ??= []).Add((at, band.BandName));
        }

        if (silent is null)
            return;
        silent.Sort(KeyOrder.Instance);
        foreach (var (at, name) in silent)
        {
            var band = _bands[name];
            _changes.Add(new OverflowChange(name, at, OverflowChangeKind.Unknown, band.Occupied ? band.PeakOccupancy : 0, band.Occupied ? band.OccupiedSinceUtc : null));
            _bands[name] = new OverflowBandState(name, false, null, 0, band.LastMinuteUtc, Unknown: true);
        }
    }

    private bool IsBand(string zoneName) => zoneName is not null && _geometry.OverflowZones.Contains(zoneName);

    private void Move(OverflowMinute minute)
    {
        var band = _bands.TryGetValue(minute.BandName, out var held) ? held : new OverflowBandState(minute.BandName, false, null, 0, DateTime.MinValue);
        if (minute.MinuteUtc <= band.LastMinuteUtc)
        {
            // A later part of a minute released early: it can only add occupancy the earlier part missed. A band that has
            // gone Unknown since stays Unknown: the part is older than the silence.
            if (band.Unknown)
                return;
            if (minute.Occupied && !band.Occupied)
                Occupy(band, minute);
            else if (minute.Occupied)
                _bands[minute.BandName] = band with { PeakOccupancy = Math.Max(band.PeakOccupancy, minute.MaxOccupancy) };
            return;
        }

        if (minute.Occupied && !band.Occupied)
        {
            Occupy(band, minute);
        }
        else if (minute.Occupied)
        {
            _bands[minute.BandName] = band with { PeakOccupancy = Math.Max(band.PeakOccupancy, minute.MaxOccupancy), LastMinuteUtc = minute.MinuteUtc };
        }
        else if (band.Occupied || band.Unknown)
        {
            // Emptied after an occupied spell (with its first minute and peak), or empty again after an unknown one (peak 0).
            _changes.Add(new OverflowChange(minute.BandName, minute.MinuteUtc, OverflowChangeKind.Emptied, band.PeakOccupancy, band.OccupiedSinceUtc));
            _bands[minute.BandName] = new OverflowBandState(minute.BandName, false, null, 0, minute.MinuteUtc);
        }
        else
        {
            _bands[minute.BandName] = band with { LastMinuteUtc = minute.MinuteUtc };
        }
    }

    private void Occupy(OverflowBandState band, OverflowMinute minute)
    {
        _changes.Add(new OverflowChange(minute.BandName, minute.MinuteUtc, OverflowChangeKind.Occupied, minute.MaxOccupancy, null));
        var last = minute.MinuteUtc > band.LastMinuteUtc ? minute.MinuteUtc : band.LastMinuteUtc;
        _bands[minute.BandName] = new OverflowBandState(minute.BandName, true, minute.MinuteUtc, minute.MaxOccupancy, last);
    }

    /// <summary>The open band minutes, for the zone's snapshot.</summary>
    public IReadOnlyList<OverflowMinuteState> CaptureOpen() =>
        [.. _open.Select(o => new OverflowMinuteState(o.Key.Band, o.Key.Minute, o.Value.Min, o.Value.Max))];

    /// <summary>The bands' states, for the zone's snapshot (bands that never had a reading are not listed).</summary>
    public IReadOnlyList<OverflowBandState> CaptureBands() => [.. _bands.Values];

    /// <summary>
    /// The overflow state of a snapshot, checked (CWE-501, CWE-120): every band is a band of <paramref name="geometry"/>;
    /// at most <see cref="MaxOpenBandMinutes"/> open minutes, each an aligned plausible UTC minute held once, with readings
    /// from 0 to the canonical bound and the lowest not above the highest; at most one state per band, its times aligned
    /// and plausible, an occupied band with its first minute and a peak from 1 to the bound, an empty one with neither,
    /// an unknown one neither occupied nor with a spell or peak, and with the minute it was last heard in. Any problem is
    /// an <see cref="InvalidDataException"/>.
    /// </summary>
    public static OverflowBands Restore(QueueZoneGeometry geometry, IReadOnlyList<OverflowMinuteState> open, IReadOnlyList<OverflowBandState> bands,
        TimeSpan? occupancyFreshFor = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var restored = new OverflowBands(geometry, occupancyFreshFor);
        if ((open?.Count ?? 0) > MaxOpenBandMinutes)
            throw new InvalidDataException($"The zone snapshot holds more than {MaxOpenBandMinutes} open overflow minutes.");
        foreach (var o in open ?? [])
        {
            if (o is null || !restored.IsBand(o.BandName))
                throw new InvalidDataException("The zone snapshot holds an overflow minute of a zone that is not one of the queue's overflow bands.");
            var minute = DateTime.SpecifyKind(o.MinuteUtc, DateTimeKind.Utc);
            if (!LineCounts.IsMinute(minute) || o.Min < 0 || o.Max < o.Min || o.Max > CanonicalEventRules.MaxOccupancy)
                throw new InvalidDataException("The zone snapshot has an overflow minute with an unaligned time or an occupancy out of bounds.");
            if (!restored._open.TryAdd((minute, o.BandName), (o.Min, o.Max)))
                throw new InvalidDataException("The zone snapshot holds an overflow minute twice.");
        }

        if ((bands?.Count ?? 0) > geometry.OverflowZones.Count)
            throw new InvalidDataException("The zone snapshot holds more overflow band states than the queue has bands.");
        foreach (var b in bands ?? [])
        {
            if (b is null || !restored.IsBand(b.BandName))
                throw new InvalidDataException("The zone snapshot holds the state of a zone that is not one of the queue's overflow bands.");
            var last = DateTime.SpecifyKind(b.LastMinuteUtc, DateTimeKind.Utc);
            var since = b.OccupiedSinceUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : (DateTime?)null;
            var valid = (last == DateTime.MinValue || LineCounts.IsMinute(last)) && b.PeakOccupancy is >= 0 and <= CanonicalEventRules.MaxOccupancy &&
                        (b.Occupied
                            ? !b.Unknown && since is { } first && LineCounts.IsMinute(first) && first <= last && b.PeakOccupancy >= 1
                            : since is null && b.PeakOccupancy == 0 && (!b.Unknown || last != DateTime.MinValue));
            if (!valid)
                throw new InvalidDataException("The zone snapshot has an overflow band state with an unaligned time or an inconsistent spell.");
            if (!restored._bands.TryAdd(b.BandName, b with { LastMinuteUtc = last, OccupiedSinceUtc = since }))
                throw new InvalidDataException("The zone snapshot holds an overflow band state twice.");
        }

        return restored;
    }

    private static bool Passed(DateTime minute, DateTime watermarkUtc) => minute.AddMinutes(1).AddTicks(-1) <= watermarkUtc;

    private sealed class KeyOrder : IComparer<(DateTime Minute, string Band)>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare((DateTime Minute, string Band) x, (DateTime Minute, string Band) y)
        {
            var byMinute = x.Minute.CompareTo(y.Minute);
            return byMinute != 0 ? byMinute : string.CompareOrdinal(x.Band, y.Band);
        }
    }
}
