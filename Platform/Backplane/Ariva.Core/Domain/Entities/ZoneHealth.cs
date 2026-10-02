using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.Events;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// The health of one queue zone's sensing (ARV-025), from the states of the commissioned devices that own the zone:
/// Healthy when all are online, Degraded while any is offline or degraded, Unmonitored when there are none. A change of
/// state raises <see cref="ZoneHealthChanged"/>; counts that change without a change of state are kept quietly.
/// </summary>
public class ZoneHealth : EntityBase<ZoneHealth>, ISiteBound
{
    protected ZoneHealth()
    {
    }

    public ZoneHealth(string siteCode, string queueZoneName, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueZoneName);
        SiteCode = siteCode;
        QueueZoneName = queueZoneName;
        State = ZoneHealthState.Unmonitored;
        ChangedOn = Utc(utcNow);
    }

    public virtual string SiteCode { get; protected set; }
    public virtual string QueueZoneName { get; protected set; }
    public virtual ZoneHealthState State { get; protected set; }
    public virtual int Devices { get; protected set; }
    public virtual int DevicesOffline { get; protected set; }
    public virtual int DevicesDegraded { get; protected set; }

    /// <summary>When the state last changed.</summary>
    public virtual DateTime ChangedOn { get; protected set; }

    /// <summary>What the state is for these counts of commissioned devices.</summary>
    public static ZoneHealthState StateFor(int devices, int offline, int degraded) =>
        devices == 0 ? ZoneHealthState.Unmonitored
        : offline > 0 || degraded > 0 ? ZoneHealthState.Degraded
        : ZoneHealthState.Healthy;

    /// <summary>Takes the zone's current device counts; true when the state changed (and the event was raised).</summary>
    public virtual bool Assess(int devices, int offline, int degraded, DateTime utcNow)
    {
        if (devices < 0 || offline < 0 || degraded < 0 || offline + degraded > devices)
            throw new ArgumentOutOfRangeException(nameof(devices), "Offline and degraded devices are among the zone's devices.");
        var state = StateFor(devices, offline, degraded);
        (Devices, DevicesOffline, DevicesDegraded) = (devices, offline, degraded);
        if (state == State)
            return false;

        var previous = State;
        State = state;
        ChangedOn = Utc(utcNow);
        RaiseDomainEvent(new ZoneHealthChanged
        {
            SiteCode = SiteCode,
            QueueZoneName = QueueZoneName,
            State = state.ToString(),
            PreviousState = previous.ToString(),
            Devices = devices,
            DevicesOffline = offline,
            DevicesDegraded = degraded,
            OccurredOn = ChangedOn
        });
        return true;
    }

    private static DateTime Utc(DateTime value) => Device.Utc(value);
}
