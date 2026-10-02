using System.Reflection;
using System.Text.RegularExpressions;

namespace Ariva.Core.Messaging;

/// <summary>
/// Every Kafka topic Ariva produces or consumes (ADR-0019): <c>ariva.&lt;context&gt;.&lt;event&gt;.v1</c>, lowercase, with
/// kebab-case event names, plus the AMAN feed topics AMAN produces. Names are never built from input; code uses these
/// constants. The key of each topic is in docs/architecture/overview.md section 5.
/// </summary>
public static partial class KafkaTopics
{
    // Device (Ingest)
    public const string DeviceTrackSample = "ariva.device.track-sample.v1";
    public const string DeviceVendorLineCrossing = "ariva.device.vendor-line-crossing.v1";

    // Counting (T1) and occupancy (T2) devices (ARV-023): not in D5, which assumed tracks everywhere.
    public const string DeviceZoneOccupancy = "ariva.device.zone-occupancy.v1";
    public const string DeviceIntervalCount = "ariva.device.interval-count.v1";
    public const string DeviceHealth = "ariva.device.health.v1";
    public const string DeviceRegistryChanged = "ariva.device.registry-changed.v1";

    // Topology (Main)
    public const string TopologyZoneProfileActivated = "ariva.topology.zone-profile-activated.v1";
    public const string TopologyDeskChanged = "ariva.topology.desk-changed.v1";

    // Flow (Stream)
    public const string FlowZoneCrossing = "ariva.flow.zone-crossing.v1";
    public const string FlowQueueInterval = "ariva.flow.queue-interval.v1";
    public const string FlowNowcast = "ariva.flow.nowcast.v1";
    public const string FlowOverflowDetected = "ariva.flow.overflow-detected.v1";

    // Desks
    public const string DeskSignal = "ariva.desk.signal.v1";
    public const string DeskStateChanged = "ariva.desk.state-changed.v1";
    public const string DeskIntervalClosed = "ariva.desk.interval-closed.v1";

    // Border (Integration)
    public const string BorderServiceRateUpdated = "ariva.border.service-rate-updated.v1";
    public const string BorderEgateOutcomeRateUpdated = "ariva.border.egate-outcome-rate-updated.v1";
    public const string BorderLaneDemandUpdated = "ariva.border.lane-demand-updated.v1";

    // Flights, forecasts, alerts, SLA, feeds
    public const string FlightChanged = "ariva.flight.flight-changed.v1";
    public const string ForecastPublished = "ariva.forecast.published.v1";
    public const string ForecastArrivalWave = "ariva.forecast.arrival-wave.v1";
    public const string ForecastStaffingRecommendationIssued = "ariva.forecast.staffing-recommendation-issued.v1";
    public const string AlertStateChanged = "ariva.alert.state-changed.v1";
    public const string SlaBreachDetected = "ariva.sla.breach-detected.v1";
    public const string SlaEvaluationFinalised = "ariva.sla.evaluation-finalised.v1";
    public const string SlaDisputeChanged = "ariva.sla.dispute-changed.v1";
    public const string FeedBorderLaneKpi = "ariva.feed.border-lane-kpi.v1";

    // Produced by AMAN, consumed by Ariva.Api.Integration in border deployments (read only for Ariva).
    public const string AmanDeskSessionChanged = "aman.feed.desk-session-changed.v1";
    public const string AmanDeskIntervalStats = "aman.feed.desk-interval-stats.v1";
    public const string AmanEgateIntervalStats = "aman.feed.egate-interval-stats.v1";
    public const string AmanInboundFlightLaneDemand = "aman.feed.inbound-flight-lane-demand.v1";

    /// <summary>Suffix of the dead-letter topic of a consumed topic (ADR-0018).</summary>
    public const string DeadLetterSuffix = ".dlq.v1";

    private static readonly Lazy<IReadOnlyList<string>> AllTopics = new(() =>
        [.. typeof(KafkaTopics).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(DeadLetterSuffix))
            .Select(f => (string)f.GetRawConstantValue())]);

    /// <summary>Every topic above.</summary>
    public static IReadOnlyList<string> All => AllTopics.Value;

    /// <summary>Topics Ariva produces (the <c>ariva.</c> prefix); AMAN feed topics are not Ariva's to create.</summary>
    public static IEnumerable<string> Owned => All.Where(t => t.StartsWith("ariva.", StringComparison.Ordinal));

    /// <summary>
    /// The dead-letter topic of <paramref name="topic"/>: <c>&lt;topic&gt;.dlq.v1</c>. A topic outside the <c>ariva.</c>
    /// prefix (AMAN's feed) gets the prefix in front, so every dead letter stays under Ariva's ACLs:
    /// <c>ariva.aman.feed.desk-session-changed.v1.dlq.v1</c>.
    /// </summary>
    public static string DeadLetter(string topic)
    {
        if (!IsKnown(topic))
            throw new ArgumentException($"Unknown topic '{topic}'. Use a KafkaTopics constant.", nameof(topic));
        return (topic.StartsWith("ariva.", StringComparison.Ordinal) ? topic : "ariva." + topic) + DeadLetterSuffix;
    }

    public static bool IsKnown(string topic) => topic is not null && Enumerable.Contains(All, topic, StringComparer.Ordinal);

    /// <summary>The naming rule of ADR-0019 for Ariva topics.</summary>
    public static bool FollowsNamingRule(string topic) => topic is not null && NamingRule().IsMatch(topic);

    [GeneratedRegex(@"^ariva\.(device|topology|flow|desk|flight|forecast|alert|sla|border|feed)\.[a-z0-9]+(-[a-z0-9]+)*\.v[1-9][0-9]*$")]
    private static partial Regex NamingRule();
}
