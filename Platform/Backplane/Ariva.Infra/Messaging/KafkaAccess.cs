using System.Text;
using Ariva.Core.Messaging;

namespace Ariva.Infra.Messaging;

/// <summary>
/// The Kafka ACLs of each host's principal (ARV-083b; CWE-269, CWE-285; ASVS 5.0 V13.2.1, V13.2.2): one declared manifest of
/// the topics each host writes and reads and the consumer groups it joins, no more. Ariva does not manage the site's brokers, so
/// the site's Kafka administrator grants them with the commands <see cref="Listing"/> writes (wiki 04, section 6.2a, generated
/// from here and held equal by KafkaAccessTests); each host signs in as its own principal (Kafka:SaslUsername and
/// Kafka:SaslPassword from its own secret).
/// <para>
/// What each host writes, all through its one producer (<c>KafkaProduce</c>, ARV-167):
/// <list type="bullet">
/// <item>every topic a domain event goes to through the outbox (<see cref="OutboxTopics"/>), whichever host wrote the row: every
/// host runs the outbox relay (Kafka:Outbox:Enabled) and one of them, any, leads it (a PostgreSQL advisory lock), so each
/// relays every host's rows. Each host's database login may write outbox_message too (ARV-083a, ariva_runtime), so a narrower
/// ACL here would only hold a row back, never keep a host from producing an event;</item>
/// <item>every dead-letter topic (<see cref="DeadLetterTopics"/>): any host's relay may send a dead letter another host kept in
/// the outbox (ARV-171), besides the dead letters of its own consumers;</item>
/// <item>the topics it sends directly through <c>ISvcMessageBus</c> (<see cref="DirectTopics"/>): Ariva.Api.Ingest's sensing
/// batches and device health reports alone.</item>
/// </list>
/// What each host reads: the topics of its MassTransit topic endpoints, each with its group <c>ariva-&lt;service&gt;.&lt;purpose&gt;</c>
/// (the hosts' <c>Consume&lt;TMessage, TConsumer&gt;</c> declarations), and Ariva.Api.Stream's queue engine's raw consumer
/// (<c>StreamFrom</c>, group <c>ariva-stream.queue-engine</c>). No host reads a dead-letter topic: only the operators and the
/// dead-letter replay tool do (<see cref="Operators"/>; the replay tool is not built: wiki 10 section 4.5a). The topics job
/// creates the topics (<see cref="TopicsJob"/>). READ and WRITE imply DESCRIBE, which the metadata, offset and lag requests need;
/// the producer's readiness probe (DescribeCluster) needs no ACL.
/// </para>
/// A new topic a host produces or consumes, or a new consumer group, adds its grant here in the same change: KafkaAccessTests
/// holds the manifest equal to the event types, the hosts' declared consumers and the topic catalog.
/// </summary>
internal static class KafkaAccess
{
    #region Types

    /// <summary>An ACL operation Ariva grants.</summary>
    public enum Operation
    {
        Read,
        Write,
        Create,
        Describe
    }

    /// <summary>A consumed topic and its consumer group; <paramref name="Raw"/>: the Stream engine's own consumer, not a MassTransit endpoint.</summary>
    public sealed record Consumer(string Topic, string Group, bool Raw);

    /// <summary>One host's principal: the host (its Kafka:ServiceName), the topics it writes and what it consumes.</summary>
    public sealed record HostAccess(string Service, IReadOnlyList<string> Writes, IReadOnlyList<Consumer> Reads)
    {
        /// <summary>The consumer groups the host joins.</summary>
        public IReadOnlyList<string> Groups => [.. Reads.Select(r => r.Group).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        /// <summary>The topics the host reads.</summary>
        public IReadOnlyList<string> ReadTopics => [.. Reads.Select(r => r.Topic).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    #endregion

    #region Constants

    /// <summary>The hosts, by their Kafka:ServiceName (the chart's host keys).</summary>
    public static readonly IReadOnlyList<string> Hosts = ["main", "ingest", "stream", "cronz", "integration"];

    /// <summary>The consumer group prefix of the operators' own tools (a console consumer of the dead-letter topics, for one).</summary>
    public const string OperatorGroupPrefix = "ariva-operator.";

    /// <summary>
    /// The topics of the domain events the outbox carries: devices, zone health and zone profiles (Ariva.Api.Main), flights
    /// (Ariva.Api.Integration's feeds and Main's schedule imports) and overflow bands (Ariva.Api.Stream's checkpoint).
    /// </summary>
    public static readonly IReadOnlyList<string> OutboxTopics =
    [
        KafkaTopics.DeviceRegistryChanged,
        KafkaTopics.DeviceZoneHealth,
        KafkaTopics.TopologyZoneProfileActivated,
        KafkaTopics.FlowOverflowDetected,
        KafkaTopics.FlightChanged
    ];

    /// <summary>The topics a host sends through ISvcMessageBus, by host: Ariva.Api.Ingest's sensing sink.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DirectTopics = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["ingest"] =
        [
            KafkaTopics.DeviceTrackSample,
            KafkaTopics.DeviceVendorLineCrossing,
            KafkaTopics.DeviceZoneOccupancy,
            KafkaTopics.DeviceIntervalCount,
            KafkaTopics.DeviceHealth
        ]
    };

    /// <summary>What each host consumes: (topic, purpose, raw); the group is <c>ariva-&lt;service&gt;.&lt;purpose&gt;</c>.</summary>
    private static readonly Dictionary<string, (string Topic, string Purpose, bool Raw)[]> Consumed = new(StringComparer.Ordinal)
    {
        // The device health consumer (ARV-025).
        ["main"] = [(KafkaTopics.DeviceHealth, "device-health", false)],
        // The raw sensing archive and the health archive (ARV-026, ARV-036), and the queue engine (ARV-034).
        ["stream"] =
        [
            (KafkaTopics.DeviceTrackSample, "sensing-archive-tracks", false),
            (KafkaTopics.DeviceVendorLineCrossing, "sensing-archive-crossings", false),
            (KafkaTopics.DeviceZoneOccupancy, "sensing-archive-occupancy", false),
            (KafkaTopics.DeviceIntervalCount, "sensing-archive-intervals", false),
            (KafkaTopics.DeviceHealth, "health-archive", false),
            (KafkaTopics.DeviceVendorLineCrossing, "queue-engine", true),
            (KafkaTopics.DeviceZoneOccupancy, "queue-engine", true),
            (KafkaTopics.DeviceIntervalCount, "queue-engine", true),
            (KafkaTopics.DeviceTrackSample, "queue-engine", true),
            (KafkaTopics.DeviceHealth, "queue-engine", true)
        ],
        // AMAN's feed (ARV-048), read only: AMAN owns the topics.
        ["integration"] =
        [
            (KafkaTopics.AmanDeskSessionChanged, "aman-desk-sessions", false),
            (KafkaTopics.AmanDeskIntervalStats, "aman-desk-intervals", false),
            (KafkaTopics.AmanEgateIntervalStats, "aman-egate-intervals", false),
            (KafkaTopics.AmanInboundFlightLaneDemand, "aman-lane-demand", false)
        ],
        ["ingest"] = [],
        ["cronz"] = []
    };

    #endregion

    #region Grants

    /// <summary>Every dead-letter topic: one per topic, AMAN's feed included under Ariva's prefix (<see cref="KafkaTopics.DeadLetter"/>).</summary>
    public static IReadOnlyList<string> DeadLetterTopics => [.. KafkaTopics.All.Select(KafkaTopics.DeadLetter)];

    /// <summary>Each host's principal.</summary>
    public static IReadOnlyList<HostAccess> All => [.. Hosts.Select(For)];

    /// <summary>The principal of <paramref name="service"/> (main, ingest, stream, cronz or integration).</summary>
    public static HostAccess For(string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        if (!Consumed.TryGetValue(service, out var consumed))
            throw new ArgumentOutOfRangeException(nameof(service), service, "Not an Ariva host.");
        var writes = OutboxTopics.Concat(DirectTopics.GetValueOrDefault(service) ?? []).Concat(DeadLetterTopics).Distinct(StringComparer.Ordinal).ToList();
        var reads = consumed.Select(c => new Consumer(c.Topic, $"ariva-{service}.{c.Purpose}", c.Raw)).ToList();
        return new HostAccess(service, writes, reads);
    }

    /// <summary>
    /// The topics job's principal (kafkaTopics.adminSecretName, required in k8s-prd since the ARV-083b review; elsewhere, without
    /// one, the job runs as Ariva.Api.Main's, which then needs these too): CREATE and DESCRIBE on every topic under <c>ariva.</c> (prefixed), and under <c>aman.feed.</c> where it creates AMAN's
    /// feed (Kafka:Topics:ProvisionAmanFeed, dev and demo only).
    /// </summary>
    public static IReadOnlyList<(Operation Operation, string Prefix)> TopicsJob(bool amanFeed) =>
        [(Operation.Create, "ariva."), (Operation.Describe, "ariva."), .. amanFeed ? new[] { (Operation.Create, "aman.feed."), (Operation.Describe, "aman.feed.") } : []];

    /// <summary>The operators' principal and the dead-letter replay tool's: READ on every dead-letter topic and on their own consumer groups.</summary>
    public static IReadOnlyList<string> Operators => DeadLetterTopics;

    #endregion

    #region Listing

    /// <summary>
    /// The kafka-acls commands that grant every principal its ACLs and nothing else, as wiki 04 section 6.2a lists them (a shell
    /// script: the operator sets the brokers, the admin client's properties and the principals first). Literal topic names, one
    /// ACL each, except the topics job's prefixes and the operators' group prefix.
    /// </summary>
    public static string Listing()
    {
        var text = new StringBuilder();
        text.Append("""
            # The Kafka ACLs of Ariva's principals (ARV-083b), generated from Ariva.Infra/Messaging/KafkaAccess.cs by KafkaAccessTests.
            # Set the brokers, the admin client's settings and each principal (User:<the SCRAM user of that host's secret>) first.
            BROKERS=kafka:9093
            ADMIN=/path/to/admin.properties
            MAIN=User:ariva-main INGEST=User:ariva-ingest STREAM=User:ariva-stream CRONZ=User:ariva-cronz INTEGRATION=User:ariva-integration
            TOPICS=User:ariva-topics OPERATORS=User:ariva-operator
            acl() { kafka-acls.sh --bootstrap-server "$BROKERS" --command-config "$ADMIN" --add "$@"; }
            topics() { for name in "$@"; do printf '%s\n' "--topic=$name"; done; }
            groups() { for name in "$@"; do printf '%s\n' "--group=$name"; done; }

            """);
        text.Append("# Every host writes these: the outbox's events (any host leads the relay) and every dead letter.\n");
        text.Append("OUTBOX=(").AppendJoin(' ', OutboxTopics).Append(")\n");
        text.Append("DEAD_LETTERS=(\n");
        foreach (var topic in DeadLetterTopics)
            text.Append("  ").Append(topic).Append('\n');
        text.Append(")\n");
        foreach (var host in All)
        {
            var variable = host.Service.ToUpperInvariant();
            text.Append('\n').Append("# api-").Append(host.Service).Append('\n');
            var direct = DirectTopics.GetValueOrDefault(host.Service) ?? [];
            text.Append("acl --allow-principal \"$").Append(variable).Append("\" --operation Write $(topics \"${OUTBOX[@]}\" \"${DEAD_LETTERS[@]}\"");
            foreach (var topic in direct)
                text.Append(' ').Append(topic);
            text.Append(")\n");
            if (host.Reads.Count == 0)
                continue;
            text.Append("acl --allow-principal \"$").Append(variable).Append("\" --operation Read $(topics");
            foreach (var topic in host.ReadTopics)
                text.Append(' ').Append(topic);
            text.Append(") $(groups");
            foreach (var group in host.Groups)
                text.Append(' ').Append(group);
            text.Append(")\n");
        }

        text.Append("\n# The topics job's own principal (kafkaTopics.adminSecretName, required in k8s-prd; in dev and demo without one, api-main's\n");
        text.Append("# principal needs this instead). Where it creates AMAN's\n");
        text.Append("# feed (dev and demo), also: acl --allow-principal \"$TOPICS\" --operation Create --operation Describe --resource-pattern-type prefixed --topic aman.feed.\n");
        text.Append("acl --allow-principal \"$TOPICS\" --operation Create --operation Describe --resource-pattern-type prefixed --topic ariva.\n");
        text.Append("\n# The operators and the dead-letter replay tool: read every dead-letter topic, with consumer groups of their own.\n");
        text.Append("acl --allow-principal \"$OPERATORS\" --operation Read $(topics \"${DEAD_LETTERS[@]}\")\n");
        text.Append("acl --allow-principal \"$OPERATORS\" --operation Read --resource-pattern-type prefixed --group ").Append(OperatorGroupPrefix).Append('\n');
        return text.ToString();
    }

    #endregion
}
