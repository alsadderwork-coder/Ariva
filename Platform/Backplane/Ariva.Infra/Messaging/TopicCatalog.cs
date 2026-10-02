using Ariva.Core.Messaging;

namespace Ariva.Infra.Messaging;

/// <summary>A topic as provisioned: partitions, replication and either compaction or a retention in days.</summary>
public sealed record TopicSpec(string Name, int Partitions, short ReplicationFactor, int MinInSyncReplicas, bool Compacted, int? RetentionDays);

/// <summary>
/// Retention per topic from docs/architecture/overview.md section 5 (Proposed defaults, tuned per site through
/// <c>Kafka:Topics:Overrides</c>). Short facts keep 3 days, medium 14, long 30; state topics are compacted. Dead-letter
/// topics keep 30 days, long enough for an operator to inspect and replay.
/// </summary>
public static class TopicCatalog
{
    private const int Short = 3;
    private const int Medium = 14;
    private const int Long = 30;

    private static readonly Dictionary<string, int?> Retention = new(StringComparer.Ordinal)
    {
        [KafkaTopics.DeviceTrackSample] = Short,
        [KafkaTopics.DeviceVendorLineCrossing] = Short,
        [KafkaTopics.DeviceZoneOccupancy] = Short,
        [KafkaTopics.DeviceIntervalCount] = Short,
        [KafkaTopics.DeviceHealth] = Short,
        [KafkaTopics.DeviceRegistryChanged] = null,
        [KafkaTopics.DeviceZoneHealth] = null,
        [KafkaTopics.TopologyZoneProfileActivated] = null,
        [KafkaTopics.TopologyDeskChanged] = null,
        [KafkaTopics.FlowZoneCrossing] = Short,
        [KafkaTopics.FlowQueueInterval] = Long,
        [KafkaTopics.FlowNowcast] = null,
        [KafkaTopics.FlowOverflowDetected] = Medium,
        [KafkaTopics.DeskSignal] = Short,
        [KafkaTopics.DeskStateChanged] = null,
        [KafkaTopics.DeskIntervalClosed] = Long,
        [KafkaTopics.BorderServiceRateUpdated] = Medium,
        [KafkaTopics.BorderEgateOutcomeRateUpdated] = Medium,
        [KafkaTopics.BorderLaneDemandUpdated] = Medium,
        [KafkaTopics.FlightChanged] = Medium,
        [KafkaTopics.ForecastPublished] = Long,
        [KafkaTopics.ForecastArrivalWave] = Long,
        [KafkaTopics.ForecastStaffingRecommendationIssued] = Long,
        [KafkaTopics.AlertStateChanged] = Long,
        [KafkaTopics.SlaBreachDetected] = Long,
        [KafkaTopics.SlaEvaluationFinalised] = Long,
        [KafkaTopics.SlaDisputeChanged] = Long,
        [KafkaTopics.FeedBorderLaneKpi] = Medium
    };

    /// <summary>True when the catalog has a retention for every topic Ariva owns (a unit test keeps it so).</summary>
    public static bool Covers(string topic) => Retention.ContainsKey(topic);

    /// <summary>
    /// Every topic Ariva owns plus a dead-letter topic for every topic (AMAN's feed included, under Ariva's prefix),
    /// with the defaults and overrides applied.
    /// </summary>
    public static IReadOnlyList<TopicSpec> ForProvisioning(TopicDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var specs = new List<TopicSpec>();
        foreach (var topic in KafkaTopics.Owned)
        {
            var retention = Retention.TryGetValue(topic, out var days) ? days : Medium;
            specs.Add(Spec(defaults, topic, retention is null, retention));
        }

        foreach (var topic in KafkaTopics.All)
            specs.Add(Spec(defaults, KafkaTopics.DeadLetter(topic), compacted: false, Long));
        return specs;
    }

    private static TopicSpec Spec(TopicDefaults defaults, string name, bool compacted, int? retentionDays)
    {
        defaults.Overrides.TryGetValue(name, out var custom);
        return new TopicSpec(
            name,
            custom?.Partitions ?? defaults.Partitions,
            defaults.ReplicationFactor,
            Math.Min(defaults.MinInSyncReplicas, defaults.ReplicationFactor),
            compacted,
            compacted ? null : custom?.RetentionDays ?? retentionDays);
    }
}
