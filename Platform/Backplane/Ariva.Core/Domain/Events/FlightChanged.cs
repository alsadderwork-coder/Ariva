using Ariva.Core.Messaging;

namespace Ariva.Core.Domain.Events;

/// <summary>
/// A flight leg changed (ARV-041): its schedule, a milestone or its status, from any feed. Keyed by
/// <c>&lt;site&gt;/&lt;flight key&gt;</c>, so the latest value per leg is its current state. Flight data only: no passenger
/// or crew data ever reaches Ariva (docs/domain/data-boundary.md).
/// </summary>
[KafkaTopic(KafkaTopics.FlightChanged)]
public sealed class FlightChanged : EventBase
{
    public string SiteCode { get; set; }
    public string FlightKey { get; set; }

    /// <summary>Arrival or Departure.</summary>
    public string Direction { get; set; }

    public string Carrier { get; set; }
    public string Number { get; set; }
    public string Suffix { get; set; }
    public DateTime ScheduledUtc { get; set; }
    public DateTime? EstimatedUtc { get; set; }
    public DateTime? ActualUtc { get; set; }
    public DateTime? OnBlockUtc { get; set; }
    public DateTime? OffBlockUtc { get; set; }

    /// <summary>A <see cref="Ariva.Core.Domain.Enums.FlightStatus"/> name.</summary>
    public string Status { get; set; }

    public string Terminal { get; set; }
    public string Stand { get; set; }
    public string Gate { get; set; }
    public int? Seats { get; set; }
    public int? PaxEstimate { get; set; }

    public override string GetPartitionKey() => $"{SiteCode}/{FlightKey}";
}
