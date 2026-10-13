using Ariva.Core.Sensing;

namespace Ariva.Core.Queueing;

/// <summary>A batch a zone remembers, in its snapshot (ARV-168): the device, the batch id and the end of its window.</summary>
public sealed record RecentBatchState(string DeviceCode, Guid BatchId, DateTime UntilUtc);

/// <summary>
/// The sensing batches a queue zone has applied within their window (ARV-168, formulas F5), so that a batch offered
/// again is applied once. A device that sends a push again whose request it dropped sends the same body, and Ingest
/// gives the same body from the same device the same batch id; without this memory a repeated interval-count batch
/// (or an anonymous vendor crossing) would be counted twice.
/// <para>
/// The key is the device and the batch id together, never the id alone: an id is device-supplied data in effect (it is
/// derived from the body), so one device's ids never drop another device's batches (CWE-501). A batch is remembered
/// until the zone's watermark reaches its latest event time plus the lateness; until then a repeat could still have
/// events ahead of the watermark, even with its times corrected by up to a lateness differently (another Ingest
/// replica's clock estimate), and after it every event of a repeat is behind the watermark: late, counted as late and
/// never applied.
/// </para>
/// <para>
/// Bounded (CWE-120): at most <c>max</c> batches per zone and <c>maxPerDevice</c> per device, so a device that floods a
/// zone with batches forgets only its own. Beyond a bound the batch whose window ends first is forgotten first (ties by
/// device, then id), an order of the remembered keys alone, so a zone restored from its snapshot forgets exactly what an
/// uninterrupted one would. Pure: no I/O and no clock.
/// </para>
/// </summary>
public sealed class RecentBatches
{
    private readonly record struct Entry(DateTime Until, string Device, Guid Batch);

    /// <summary>The end of the window first, then the device and the id, so the order never depends on arrival.</summary>
    private sealed class EntryOrder : IComparer<Entry>
    {
        public static readonly EntryOrder Instance = new();

        public int Compare(Entry x, Entry y)
        {
            var c = x.Until.CompareTo(y.Until);
            if (c == 0)
                c = string.CompareOrdinal(x.Device, y.Device);
            if (c == 0)
                c = x.Batch.CompareTo(y.Batch);
            return c;
        }
    }

    private readonly int _max;
    private readonly int _maxPerDevice;
    private readonly Dictionary<(string Device, Guid Batch), DateTime> _until = [];
    private readonly SortedSet<Entry> _byUntil = new(EntryOrder.Instance);
    private readonly Dictionary<string, SortedSet<Entry>> _byDevice = new(StringComparer.Ordinal);

    public RecentBatches(int max, int maxPerDevice)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerDevice, 1);
        _max = max;
        _maxPerDevice = Math.Min(maxPerDevice, max);
    }

    /// <summary>Batches remembered now.</summary>
    public int Count => _until.Count;

    /// <summary>Whether the zone applied the batch <paramref name="batch"/> of <paramref name="device"/> within its window.</summary>
    public bool Contains(string device, Guid batch) => device is not null && _until.ContainsKey((device, batch));

    /// <summary>
    /// Remembers a batch until the watermark reaches <paramref name="untilUtc"/> (a batch remembered already keeps the
    /// later of its two ends). Returns how many batches a bound made the zone forget before their window ended.
    /// </summary>
    public int Remember(string device, Guid batch, DateTime untilUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_until.TryGetValue((device, batch), out var known))
        {
            if (untilUtc <= known)
                return 0;
            Remove(new Entry(known, device, batch));
        }

        Add(new Entry(untilUtc, device, batch));
        var forgotten = 0;
        if (_byDevice[device].Count > _maxPerDevice)
        {
            Remove(_byDevice[device].Min);
            forgotten++;
        }

        if (_until.Count > _max)
        {
            Remove(_byUntil.Min);
            forgotten++;
        }

        return forgotten;
    }

    /// <summary>Forgets every batch whose window ends at or before <paramref name="watermarkUtc"/>: a repeat of it would be late.</summary>
    public void Expire(DateTime watermarkUtc)
    {
        while (_byUntil.Count > 0 && _byUntil.Min.Until <= watermarkUtc)
            Remove(_byUntil.Min);
    }

    private void Add(Entry entry)
    {
        _until[(entry.Device, entry.Batch)] = entry.Until;
        _byUntil.Add(entry);
        if (!_byDevice.TryGetValue(entry.Device, out var own))
            _byDevice[entry.Device] = own = new SortedSet<Entry>(EntryOrder.Instance);
        own.Add(entry);
    }

    private void Remove(Entry entry)
    {
        _until.Remove((entry.Device, entry.Batch));
        _byUntil.Remove(entry);
        if (_byDevice.TryGetValue(entry.Device, out var own) && own.Remove(entry) && own.Count == 0)
            _byDevice.Remove(entry.Device);
    }

    /// <summary>The remembered batches in the order they would be forgotten.</summary>
    public IReadOnlyList<RecentBatchState> Capture() => [.. _byUntil.Select(e => new RecentBatchState(e.Device, e.Batch, e.Until))];

    /// <summary>
    /// The memory of a snapshot (CWE-501: data from storage). Every batch must name a device as the registry writes its
    /// codes and a non-empty id, once; its window must end after <paramref name="watermarkUtc"/> (the zone forgets a
    /// batch as soon as the watermark reaches its end) and no later than <paramref name="latestUtc"/> (the zone's receive
    /// clock plus how far ahead an event may be and the lateness); and the bounds hold. Anything else is an
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    public static RecentBatches Restore(IReadOnlyList<RecentBatchState> saved, int max, int maxPerDevice, DateTime watermarkUtc, DateTime latestUtc)
    {
        var memory = new RecentBatches(max, maxPerDevice);
        if (saved is null || saved.Count == 0)
            return memory;
        if (saved.Count > memory._max)
            throw new InvalidDataException("The zone snapshot remembers more batches than a zone keeps.");
        foreach (var b in saved)
        {
            if (b is null || !DeviceCodes.IsValid(b.DeviceCode) || b.BatchId == Guid.Empty)
                throw new InvalidDataException("The zone snapshot remembers a batch without a valid device code or id.");
            var until = DateTime.SpecifyKind(b.UntilUtc, DateTimeKind.Utc);
            if (until <= watermarkUtc || until > latestUtc || until < ZoneProcessor.EarliestUtc)
                throw new InvalidDataException("The zone snapshot remembers a batch whose window is not plausible.");
            if (memory._until.ContainsKey((b.DeviceCode, b.BatchId)))
                throw new InvalidDataException("The zone snapshot remembers a batch twice.");
            memory.Add(new Entry(until, b.DeviceCode, b.BatchId));
            if (memory._byDevice[b.DeviceCode].Count > memory._maxPerDevice)
                throw new InvalidDataException("The zone snapshot remembers more batches of one device than a zone keeps.");
        }

        return memory;
    }
}
