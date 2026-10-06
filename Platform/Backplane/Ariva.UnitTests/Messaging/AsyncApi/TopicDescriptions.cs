using Ariva.Core.Messaging;

namespace Ariva.UnitTests.Messaging.AsyncApi;

/// <summary>A consumer group on a topic: the host (by its Kafka:ServiceName) and the group's purpose.</summary>
public sealed record TopicConsumer(string Service, string Purpose, string Description)
{
    /// <summary>The group id as the hosts build it: ariva-&lt;service&gt;.&lt;purpose&gt; (MessagingExtensions, PartitionedConsumer).</summary>
    public string GroupId => $"ariva-{Service}.{Purpose}";
}

/// <summary>A record key: a short label for the wiki table and the sentence the document carries.</summary>
public sealed record TopicKey(string Label, string Description);

/// <summary>What the code cannot say about a topic by itself: who produces it, who consumes it, and what its key is.</summary>
public sealed record TopicDescription(string Summary, string Producer, TopicKey Key, IReadOnlyList<TopicConsumer> Consumers, Type Payload = null)
{
    /// <summary>Declared in KafkaTopics, provisioned, with no producer or consumer yet.</summary>
    public bool Reserved => Payload is null && Consumers.Count == 0 && Producer is null;
}

/// <summary>
/// The hand-kept half of the AsyncAPI document (ARV-067): one entry per KafkaTopics constant. The payload type of an
/// Ariva topic comes from its [KafkaTopic] attribute, its retention from TopicCatalog and its schema from the type
/// itself, so only producers, consumers and keys are written here; AsyncApiTests fails when a topic, a group or a type is
/// missing or no longer matches the code.
/// </summary>
public static class TopicDescriptions
{
    private const string ZoneKeyText = "The zone key <site>/<queue zone name> (ZoneKeys.For): every record of a zone stays on one partition, in order.";
    private static readonly TopicKey ZoneKey = new("<site>/<zone>", ZoneKeyText);
    private static readonly TopicKey CompactedZoneKey = new("<site>/<zone>, compacted", ZoneKeyText + " Compacted: the latest value per zone is its state.");
    private const string AmanDedup = " Ariva deduplicates on the source event id, not the key.";
    private const string QueueEngine = "Queue state engine (QueueStreamWorker): merges the five device topics of each zone in receive order.";

    private static TopicConsumer Stream(string purpose, string description) => new("stream", purpose, description);

    private static IReadOnlyList<TopicConsumer> Sensing(string archive) =>
    [
        Stream("queue-engine", QueueEngine),
        Stream(archive, "Archives every record for disputes and golden replay (sensing_event, 90 days).")
    ];

    private static TopicDescription Reserved(string summary) => new(summary, null, null, []);

    public static IReadOnlyDictionary<string, TopicDescription> All { get; } = new Dictionary<string, TopicDescription>(StringComparer.Ordinal)
    {
        [KafkaTopics.DeviceTrackSample] = new("Track positions from T3 sensors (ARV-023), batched per device push.", "api-ingest", ZoneKey, Sensing("sensing-archive-tracks")),
        [KafkaTopics.DeviceVendorLineCrossing] = new("Line crossings a sensor counted itself, batched per device push.", "api-ingest", ZoneKey, Sensing("sensing-archive-crossings")),
        [KafkaTopics.DeviceZoneOccupancy] = new("Zone occupancy counts from T2 devices (ARV-023).", "api-ingest", ZoneKey, Sensing("sensing-archive-occupancy")),
        [KafkaTopics.DeviceIntervalCount] = new("In and out counts per interval from T1 devices (ARV-023).", "api-ingest", ZoneKey, Sensing("sensing-archive-intervals")),
        [KafkaTopics.DeviceHealth] = new("A device's own health report (ARV-025, ARV-036).", "api-ingest", ZoneKey,
        [
            Stream("queue-engine", QueueEngine + " Health reports drive device liveness (F11) and zone outages."),
            Stream("health-archive", "Archives every health report (device_health_event, 90 days)."),
            new("main", "device-health", "Keeps each device's heartbeat and state (DeviceHealthConsumer).")
        ]),
        [KafkaTopics.DeviceRegistryChanged] = new("A device was registered, moved, rotated, calibrated, changed health state or retired (ARV-022, ARV-025).",
            "api-main", new("device id, compacted", "The device id (a GUID); compacted, so the latest value per device is its state."), []),
        [KafkaTopics.DeviceZoneHealth] = new("A queue zone's sensing health changed: Healthy, Degraded or Unmonitored (ARV-025).",
            "api-main", CompactedZoneKey, []),
        [KafkaTopics.TopologyZoneProfileActivated] = new("A zone profile version was published and is now the site's active geometry (ARV-017).",
            "api-main", new("site code, compacted", "The site code; compacted, so the latest value per site is the active profile."), []),
        [KafkaTopics.TopologyDeskChanged] = Reserved("A desk's configuration changed (planned; desk id key, compacted)."),
        [KafkaTopics.FlowZoneCrossing] = Reserved("Zone crossings derived by Stream (planned)."),
        [KafkaTopics.FlowQueueInterval] = Reserved("Closed queue bins by zone (planned; Stream writes them to TimescaleDB today)."),
        [KafkaTopics.FlowNowcast] = Reserved("Each zone's latest nowcast (planned; Redis carries it today, ARV-035)."),
        [KafkaTopics.FlowOverflowDetected] = new("An overflow band of a queue zone became occupied, emptied or unknown (silent beyond the occupancy freshness window), decided per closed minute by the queue engine (ARV-115); written to the outbox in the stream's checkpoint transaction.",
            "api-stream", ZoneKey, []),
        [KafkaTopics.DeskSignal] = Reserved("Raw desk signals (planned)."),
        [KafkaTopics.DeskStateChanged] = Reserved("A desk's state changed (planned; compacted)."),
        [KafkaTopics.DeskIntervalClosed] = Reserved("Closed desk intervals (planned)."),
        [KafkaTopics.BorderServiceRateUpdated] = Reserved("Service rates per lane from AMAN aggregates (planned)."),
        [KafkaTopics.BorderEgateOutcomeRateUpdated] = Reserved("E-gate outcome rates from AMAN aggregates (planned)."),
        [KafkaTopics.BorderLaneDemandUpdated] = Reserved("Lane demand from AMAN aggregates (planned)."),
        [KafkaTopics.FlightChanged] = new("A flight leg was created or changed by the AODB feeds (AIDX, ACRIS, the REST feed) or an SSIM import (ARV-043 to ARV-046).",
            "api-integration, api-main", new("<site>/<flight key>", "<site>/<flight key>: every change of a leg stays on one partition, in order."), []),
        [KafkaTopics.ForecastPublished] = Reserved("A forecast run was published (planned)."),
        [KafkaTopics.ForecastArrivalWave] = Reserved("The arrival wave forecast (planned)."),
        [KafkaTopics.ForecastStaffingRecommendationIssued] = Reserved("A staffing recommendation (planned)."),
        [KafkaTopics.AlertStateChanged] = Reserved("An alert changed state (planned; alerts go through the database and Redis today)."),
        [KafkaTopics.SlaBreachDetected] = Reserved("An SLA breach (planned)."),
        [KafkaTopics.SlaEvaluationFinalised] = Reserved("An SLA evaluation was finalised (planned)."),
        [KafkaTopics.SlaDisputeChanged] = Reserved("An SLA dispute changed (planned)."),
        [KafkaTopics.FeedBorderLaneKpi] = Reserved("Border lane KPIs for outbound feeds (planned)."),
        [KafkaTopics.AmanDeskSessionChanged] = new(AmanSummary("A desk session opened, paused or closed, with the lane category it serves."), "AMAN",
            new("desk code", "The desk code (AMAN's)." + AmanDedup),
            [new("integration", "aman-desk-sessions", "AMAN intake (AmanFeedConsumers): checks, maps the desk and stores border_desk_session.")],
            typeof(Ariva.Business.Contracts.Aman.V1.DeskSessionChanged)),
        [KafkaTopics.AmanDeskIntervalStats] = new(AmanSummary("A desk's one-minute totals and service times."), "AMAN",
            new("desk code", "The desk code (AMAN's)." + AmanDedup),
            [new("integration", "aman-desk-intervals", "AMAN intake: stores border_desk_interval.")],
            typeof(Ariva.Business.Contracts.Aman.V1.DeskIntervalStats)),
        [KafkaTopics.AmanEgateIntervalStats] = new(AmanSummary("An e-gate's one-minute attempts and outcomes, rejects by category with small cells suppressed (a category other than Other holds 0 or at least 3)."), "AMAN",
            new("gate code", "The gate code (AMAN's)." + AmanDedup),
            [new("integration", "aman-egate-intervals", "AMAN intake: stores border_egate_interval.")],
            typeof(Ariva.Business.Contracts.Aman.V1.EGateIntervalStats)),
        [KafkaTopics.AmanInboundFlightLaneDemand] = new(AmanSummary("An inbound flight's boarded total and passengers by lane category (no names, documents or nationalities)."), "AMAN",
            new("flight key", "The flight key." + AmanDedup),
            [new("integration", "aman-lane-demand", "AMAN intake: stores inbound_lane_demand.")],
            typeof(Ariva.Business.Contracts.Aman.V1.InboundFlightLaneDemand))
    };

    private static string AmanSummary(string what) =>
        $"{what} AMAN feed contract V1 (Ariva.Business.Contracts, ADR-0010): aggregate only, never an officer, traveller or document " +
        "identifier; produced by AMAN (by the simulator's AMAN where none is connected), read only for Ariva. In a shared cluster Ariva " +
        "holds read ACLs on aman.feed. and write ACLs only on ariva.; a member outside the schema is dead-lettered, not read.";
}
