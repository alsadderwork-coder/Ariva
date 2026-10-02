using Ariva.Core.Messaging;

namespace Ariva.Core.Domain.Events;

/// <summary>
/// A queue zone's sensing became Healthy, Degraded or Unmonitored (ARV-025): a device of the zone lost its heartbeat
/// or reported itself unwell, or recovered. Keyed by the zone key (<c>&lt;site&gt;/&lt;queue zone name&gt;</c>) on a compacted
/// topic, so the latest value per zone is its current health. Stream flags the zone's bins Degraded while it is not
/// Healthy (docs/architecture/overview.md, "degrade and flag, never guess").
/// </summary>
[KafkaTopic(KafkaTopics.DeviceZoneHealth)]
public sealed class ZoneHealthChanged : EventBase
{
    public string SiteCode { get; set; }
    public string QueueZoneName { get; set; }

    /// <summary>Healthy, Degraded or Unmonitored.</summary>
    public string State { get; set; }

    public string PreviousState { get; set; }

    /// <summary>Commissioned devices of the zone, and how many of them are offline and degraded.</summary>
    public int Devices { get; set; }

    public int DevicesOffline { get; set; }
    public int DevicesDegraded { get; set; }

    public override string GetPartitionKey() => Ariva.Core.Sensing.ZoneKeys.For(SiteCode, QueueZoneName);
}
