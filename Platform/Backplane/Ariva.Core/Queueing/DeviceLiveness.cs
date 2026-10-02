namespace Ariva.Core.Queueing;

/// <summary>
/// A period a device of a queue zone was out (ARV-036): silent for longer than the zone's silence limit, or reporting
/// itself offline. From the end of the minute the device was last heard in (or the minute it reported offline) to the
/// minute it was heard again; <see cref="Closed"/> is false only for a period still open when a replay ended.
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
/// minute it was last heard in, until it is heard again online. Pure and driven by the zone's reference clock only, so a
/// replay of the same records gives the same outages. A device silent beyond the forget limit is treated as removed: its
/// outage ends there and it is no longer tracked.
/// </summary>
internal sealed class DeviceLiveness(TimeSpan silence, TimeSpan forget, int maxDevices)
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

    private static DateTime MinuteOf(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    /// <summary>
    /// The device was heard at <paramref name="atUtc"/>; returns the outage this ends, if any, and where its bins still
    /// need marking (the part after the marks made while it was open).
    /// </summary>
    public (DeviceOutage Outage, DateTime MarkFromUtc)? Heard(string zoneKey, string code, DateTime atUtc, bool online)
    {
        if (!_devices.TryGetValue(code, out var device))
        {
            if (_devices.Count >= maxDevices)
                return null;
            _devices[code] = device = new Device { LastHeard = atUtc };
        }

        if (atUtc > device.LastHeard)
            device.LastHeard = atUtc;
        if (!online)
        {
            device.OutSince ??= MinuteOf(atUtc);
            return null;
        }

        if (device.OutSince is not { } from)
            return null;
        var to = MinuteOf(atUtc);
        if (to <= from)
            to = from.AddMinutes(1);
        var markFrom = device.MarkedThrough is { } marked && marked > from ? marked : from;
        device.OutSince = null;
        device.MarkedThrough = null;
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
                device.OutSince = MinuteOf(device.LastHeard);
            if (quiet <= forget)
                continue;
            (forgotten ??= []).Add(code);
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
    /// The open outages whose bins should be marked through <paramref name="referenceUtc"/>, each from where its last
    /// mark ended: at most once every <paramref name="every"/> per device, so the marks (and the recomputations of final
    /// bins they ask for) are few and do not repeat.
    /// </summary>
    public IEnumerable<(DateTime FromUtc, DateTime ToUtc)> MarksDue(DateTime referenceUtc, TimeSpan every)
    {
        foreach (var device in _devices.Values)
        {
            if (device.OutSince is not { } from || referenceUtc <= from)
                continue;
            if (device.MarkedThrough is { } marked && referenceUtc - marked < every)
                continue;
            var start = device.MarkedThrough is { } through && through > from ? through : from;
            device.MarkedThrough = referenceUtc;
            yield return (start, referenceUtc);
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
            if (device.OutSince is not { } from)
                continue;
            if (from < end)
            {
                ended.Add((new DeviceOutage(zoneKey, code, from, end, false), device.MarkedThrough is { } m && m > from ? m : from));
                Remember(new RecentOutageState(code, from, end));
            }

            device.OutSince = null;
            device.MarkedThrough = null;
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
