using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;

namespace Ariva.Core.Sensing;

/// <summary>How far the device's clock can be trusted for a batch (F19).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ClockState>))]
public enum ClockState
{
    /// <summary>The estimated offset is within the tolerance; event times are used as sent.</summary>
    Ok,

    /// <summary>Beyond the tolerance but stable: event times are corrected by the estimated offset.</summary>
    Corrected,

    /// <summary>Beyond the tolerance and not stable: times are used as sent and waits across sensors are unreliable.</summary>
    Unreliable
}

/// <summary>The device clock estimate attached to every batch (ARV-023, F19).</summary>
public sealed record ClockReading(double OffsetMilliseconds, bool Stable, ClockState State);

/// <summary>
/// One push from one device, mapped to canonical events (ARV-023). Common to every sensing batch: who sent it, for
/// which zone, when Ariva received it, the clock estimate and whether the device was commissioned (events from a
/// device in commissioning are for checking the installation, never for KPIs). Keyed by the zone key
/// (<c>&lt;site&gt;/&lt;queue zone name&gt;</c>, ADR-0019 and ARV-021), so a zone's events stay on one partition in order.
/// The id is derived from the device and the vendor's package id when there is one, so a resent package has the same
/// id and consumers drop it.
/// </summary>
public abstract class SensingBatch : EventBase
{
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; }
    public string SiteCode { get; set; }
    public string QueueZoneName { get; set; }
    public string Dialect { get; set; }
    public bool Commissioned { get; set; }
    public DateTime ReceivedUtc { get; set; }

    /// <summary>The vendor's package or sequence number, for duplicates and gaps; null when the payload has none.</summary>
    public long? PackageId { get; set; }

    public ClockReading Clock { get; set; }

    public string ZoneKey => ZoneKeys.For(SiteCode, QueueZoneName);

    public override string GetPartitionKey() => ZoneKey;
}

/// <summary>An event inside a batch: the canonical event, its time after any clock correction, and its flags.</summary>
public sealed record Sensed<T>(T Event, DateTime TimeUtc, SensedFlags Flags) where T : CanonicalEvent;

/// <summary>Why an event is less than fully trustworthy; consumers decide what to do with it.</summary>
[Flags]
public enum SensedFlags
{
    None = 0,

    /// <summary>The event's time (after correction) lies beyond the tolerance from when Ariva received it.</summary>
    Skewed = 1,

    /// <summary>The time was corrected by the device's estimated clock offset.</summary>
    Corrected = 2
}

[KafkaTopic(KafkaTopics.DeviceTrackSample)]
public sealed class TrackSampleBatch : SensingBatch
{
    public IReadOnlyList<Sensed<TrackPosition>> Samples { get; set; } = [];
}

/// <summary>Line crossings the vendor computed; Ariva's own crossings come from tracks (ADR-0003), these are a cross-check.</summary>
[KafkaTopic(KafkaTopics.DeviceVendorLineCrossing)]
public sealed class VendorLineCrossingBatch : SensingBatch
{
    public IReadOnlyList<Sensed<LineCrossing>> Crossings { get; set; } = [];
}

[KafkaTopic(KafkaTopics.DeviceZoneOccupancy)]
public sealed class ZoneOccupancyBatch : SensingBatch
{
    public IReadOnlyList<Sensed<ZoneOccupancy>> Occupancy { get; set; } = [];
}

[KafkaTopic(KafkaTopics.DeviceIntervalCount)]
public sealed class IntervalCountBatch : SensingBatch
{
    public IReadOnlyList<Sensed<IntervalCount>> Intervals { get; set; } = [];
}

/// <summary>A device's health (every 10 to 30 seconds), keyed by device id: what it reported, or Online with Ariva's clock estimate when it sent data but no status.</summary>
[KafkaTopic(KafkaTopics.DeviceHealth)]
public sealed class DeviceHealthReported : EventBase
{
    public Guid DeviceId { get; set; }
    public string DeviceCode { get; set; }
    public string SiteCode { get; set; }
    public string QueueZoneName { get; set; }
    public DeviceStatus Status { get; set; }
    public ClockReading Clock { get; set; }
    public DateTime ReceivedUtc { get; set; }

    public override string GetPartitionKey() => DeviceId.ToString();
}

/// <summary>The partition key of a zone's sensor events: the site and the owning queue zone's name.</summary>
public static class ZoneKeys
{
    public static string For(string siteCode, string queueZoneName) => $"{siteCode}/{queueZoneName}";
}
