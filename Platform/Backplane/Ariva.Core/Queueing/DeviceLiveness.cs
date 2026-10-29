namespace Ariva.Core.Queueing;

/// <summary>
/// A period a device of a queue zone was out (ARV-036): silent for longer than the zone's silence limit, or reporting
/// itself offline. From the minute the device was last heard in (or the minute it reported offline) to the start of the
/// minute its first push after it covers (ARV-148: the minute the push interval before that push's receive time falls
/// in); <see cref="Closed"/> is false only for a period still open when a replay ended.
/// </summary>
public sealed record DeviceOutage(string ZoneKey, string DeviceCode, DateTime FromUtc, DateTime ToUtc, bool Closed = true);

/// <summary>A device as the zone last heard it, in a snapshot.</summary>
public sealed record DeviceLivenessState(string DeviceCode, DateTime LastHeardUtc, DateTime? OutSinceUtc, DateTime? MarkedThroughUtc);

/// <summary>An outage that ended but whose minutes may not all have been published live yet, in a snapshot.</summary>
public sealed record RecentOutageState(string DeviceCode, DateTime FromUtc, DateTime ToUtc);

/// <summary>
/// Which devices of a queue zone are out (ARV-036), from what the zone's own record stream says: every sensing batch and
/// device health report of a commissioned device means it was heard at the batch's receive time, and a health report
/// may say the device is offline. A device heard before and silent for longer than the silence limit is out from the
/// minute it was last heard in, until it is heard again online: the outage ends at the start of the minute that push
/// covers (the minute the push interval before its receive time falls in, ARV-148), so the minute after an outage is
/// judged again. Pure and driven by the zone's reference clock only, so a replay of the same records gives the same
/// outages. A device silent beyond the forget limit is treated as removed: its outage ends there and it is no longer
/// tracked.
/// </summary>
internal sealed class DeviceLiveness(TimeSpan silence, TimeSpan forget, int maxDevices, TimeSpan pushInterval)
{
    private sealed class Device
    {
        public DateTime LastHeard;
        public DateTime? OutSince;
        public DateTime? MarkedThrough;
    }

    private readonly SortedDictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly List<RecentOutageState> _recent = [];

    public int Count => _devices.Count;

    /// <summary>
    /// How often a device's live state changed (tracked for the first time, gone out, live again, forgotten, or an outage ended at a
    /// replay's end), so a follower of it (<see cref="LineCoverage"/>, ARV-259e1) looks again only after a change.
    /// </summary>
    public long Changes { get; private set; }

    /// <summary>
    /// What covers the zone's lines now (ARV-259e1: every device the zone tracks covers every entry and exit line until devices
    /// declare theirs): whether a device the zone tracks is live, and where the latest outage of the devices it tracks started
    /// (null when it tracks none, or none is out).
    /// </summary>
    public (bool AnyLive, DateTime? LatestOutSince) Covering()
    {
        var anyLive = false;
        DateTime? latest = null;
        foreach (var device in _devices.Values)
        {
            // As in MarksDue: no pattern variables in the conditions (ARV-069a).
            if (device.OutSince.HasValue)
                latest = latest.HasValue && latest.Value >= device.OutSince.Value ? latest : device.OutSince;
            else
                anyLive = true;
        }

        return (anyLive, latest);
    }

    private static DateTime MinuteOf(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    /// <summary>The start of the minute a push received at <paramref name="atUtc"/> covers: the push interval before it.</summary>
    public DateTime CoveredFrom(DateTime atUtc) => MinuteOf(atUtc > DateTime.MinValue + pushInterval ? atUtc - pushInterval : DateTime.MinValue);

    /// <summary>
    /// The device was heard at <paramref name="atUtc"/>; returns the outage this ends, if any (at the start of the minute
    /// the push covers, and at least a minute after it began), and where its bins still need marking (the part after the
    /// marks made while it was open).
    /// </summary>
    public (DeviceOutage Outage, DateTime MarkFromUtc)? Heard(string zoneKey, string code, DateTime atUtc, bool online)
    {
        if (!_devices.TryGetValue(code, out var device))
        {
            if (_devices.Count >= maxDevices)
                return null;
            _devices[code] = device = new Device { LastHeard = atUtc };
            Changes++;
        }

        if (atUtc > device.LastHeard)
            device.LastHeard = atUtc;
        if (!online)
        {
            if (device.OutSince is null)
            {
                device.OutSince = MinuteOf(atUtc);
                Changes++;
            }

            return null;
        }

        if (device.OutSince is not { } from)
            return null;
        // F4 of ARV-104j: the push after the outage covers the minute the push interval before its receive time, so the
        // outage ends at that minute's start, not at the minute the push arrived in.
        var to = CoveredFrom(atUtc);
        if (to <= from)
            to = from.AddMinutes(1);
        var markFrom = device.MarkedThrough is { } marked && marked > from ? marked : from;
        device.OutSince = null;
        device.MarkedThrough = null;
        Changes++;
        Remember(new RecentOutageState(code, from, to));
        return (new DeviceOutage(zoneKey, code, from, to), markFrom);
    }

    /// <summary>
    /// At <paramref name="referenceUtc"/>: devices silent beyond the limit go out (from the minute they were last heard in),
    /// and devices silent beyond the forget limit are dropped with their outage closed; returns those closed outages.
    /// </summary>
    public IReadOnlyList<(DeviceOutage Outage, DateTime MarkFromUtc)> Observe(string zoneKey, DateTime referenceUtc)
    {
        List<(DeviceOutage, DateTime)> closed = null;
        List<string> forgotten = null;
        foreach (var (code, device) in _devices)
        {
            var quiet = referenceUtc - device.LastHeard;
            if (quiet > silence && device.OutSince is null)
            {
                device.OutSince = MinuteOf(device.LastHeard);
                Changes++;
            }

            if (quiet <= forget)
                continue;
            (forgotten ??= []).Add(code);
            Changes++;
            if (device.OutSince is { } from)
            {
                var to = MinuteOf(device.LastHeard + forget);
                if (to <= from)
                    to = from.AddMinutes(1);
                Remember(new RecentOutageState(code, from, to));
                (closed ??= []).Add((new DeviceOutage(zoneKey, code, from, to), device.MarkedThrough is { } m && m > from ? m : from));
            }
        }

        foreach (var code in forgotten ?? [])
            _devices.Remove(code);
        return closed ?? [];
    }

    /// <summary>
    /// The open outages whose bins should be marked through <paramref name="throughUtc"/> (ARV-148: the start of the
    /// minute the push interval before the receive clock falls in, where an outage ending now would end at the earliest),
    /// each from where its last mark ended: once per new minute per device, so the marks never reach a minute the device's
    /// next push may still cover, and a bin is marked before it can become final (the zone keeps bins after that minute
    /// provisional), so only the first mark of an outage can ask for a recomputation.
    /// </summary>
    public IEnumerable<(DateTime FromUtc, DateTime ToUtc)> MarksDue(DateTime throughUtc)
    {
        foreach (var device in _devices.Values)
        {
            // Nullable values are read through HasValue and Value, never pattern variables of a condition (ARV-069a), so
            // every mutant of these conditions compiles.
            if (device.OutSince is null)
                continue;
            var from = device.OutSince.Value;
            if (throughUtc <= from)
                continue;
            var marked = device.MarkedThrough;
            if (marked.HasValue && throughUtc <= marked.Value)
                continue;
            var start = marked.HasValue && marked.Value > from ? marked.Value : from;
            device.MarkedThrough = throughUtc;
            yield return (start, throughUtc);
        }
    }

    // Ended outages wait for their live minutes; a stuck watermark cannot grow the list beyond a bound.
    private void Remember(RecentOutageState outage)
    {
        _recent.Add(outage);
        if (_recent.Count > maxDevices * 4)
            _recent.RemoveAt(0);
    }

    /// <summary>Whether any device was out during the minute starting at <paramref name="minuteUtc"/>.</summary>
    public bool OutDuring(DateTime minuteUtc)
    {
        foreach (var device in _devices.Values)
        {
            if (device.OutSince is { } from && from <= minuteUtc)
                return true;
        }

        foreach (var r in _recent)
        {
            if (r.FromUtc <= minuteUtc && minuteUtc < r.ToUtc)
                return true;
        }

        return false;
    }

    /// <summary>Forgets ended outages whose minutes were all published live (up to <paramref name="lastLiveUtc"/>).</summary>
    public void Published(DateTime lastLiveUtc) => _recent.RemoveAll(r => r.ToUtc <= lastLiveUtc.AddMinutes(1));

    /// <summary>
    /// Ends the outages still open at <paramref name="endUtc"/> (a replay's end): they are returned open
    /// (<see cref="DeviceOutage.Closed"/> false), count for the live minutes before the end only, and the devices are
    /// no longer out.
    /// </summary>
    public IReadOnlyList<(DeviceOutage Outage, DateTime MarkFromUtc)> EndAll(string zoneKey, DateTime endUtc)
    {
        var end = MinuteOf(endUtc);
        var ended = new List<(DeviceOutage, DateTime)>();
        foreach (var (code, device) in _devices)
        {
            // As in MarksDue: no pattern variables in the conditions (ARV-069a).
            if (device.OutSince is null)
                continue;
            var from = device.OutSince.Value;
            if (from < end)
            {
                var marked = device.MarkedThrough;
                ended.Add((new DeviceOutage(zoneKey, code, from, end, false), marked.HasValue && marked.Value > from ? marked.Value : from));
                Remember(new RecentOutageState(code, from, end));
            }

            device.OutSince = null;
            device.MarkedThrough = null;
            Changes++;
        }

        return ended;
    }

    public (IReadOnlyList<DeviceLivenessState> Devices, IReadOnlyList<RecentOutageState> Recent) Capture() =>
        ([.. _devices.Select(d => new DeviceLivenessState(d.Key, d.Value.LastHeard, d.Value.OutSince, d.Value.MarkedThrough))], [.. _recent]);

    public void Restore(IEnumerable<DeviceLivenessState> devices, IEnumerable<RecentOutageState> recent)
    {
        foreach (var d in (devices ?? []).Where(d => d is not null).Take(maxDevices))
        {
            _devices[d.DeviceCode] = new Device
            {
                LastHeard = DateTime.SpecifyKind(d.LastHeardUtc, DateTimeKind.Utc),
                OutSince = d.OutSinceUtc is { } o ? DateTime.SpecifyKind(o, DateTimeKind.Utc) : null,
                MarkedThrough = d.MarkedThroughUtc is { } m ? DateTime.SpecifyKind(m, DateTimeKind.Utc) : null
            };
        }

        _recent.AddRange((recent ?? []).Where(r => r is not null).Take(maxDevices * 4)
            .Select(r => r with { FromUtc = DateTime.SpecifyKind(r.FromUtc, DateTimeKind.Utc), ToUtc = DateTime.SpecifyKind(r.ToUtc, DateTimeKind.Utc) }));
    }
}
