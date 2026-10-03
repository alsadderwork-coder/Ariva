namespace Ariva.Infra.Messaging;

/// <summary>
/// The "Kafka" configuration section (ADR-0018). With <see cref="Enabled"/> false (unit tests, a laptop without a
/// broker) no bus starts; domain events still go to the outbox table and wait there for a relay.
/// </summary>
public sealed class KafkaSettings
{
    public const string SectionName = "Kafka";

    public bool Enabled { get; init; }

    /// <summary>Comma-separated host:port list.</summary>
    public string BootstrapServers { get; init; } = "localhost:9092";

    /// <summary>
    /// The service part of consumer group names, <c>ariva-&lt;service&gt;.&lt;purpose&gt;</c>, one group per service and
    /// endpoint so a deploy of one service never rebalances another. Each host sets it (main, stream, ingest, cronz,
    /// integration).
    /// </summary>
    public string ServiceName { get; init; } = "main";

    /// <summary>
    /// Plaintext in vm-local and the dev and demo clusters; production must use <c>SaslSsl</c> (appsettings.base.k8s-prd
    /// sets it, and a k8s-prd host with Kafka on refuses to start with anything else, CWE-501).
    /// </summary>
    public string SecurityProtocol { get; init; } = "Plaintext";

    /// <summary>PEM file of the CA that signed the brokers' certificates (a Strimzi cluster CA), mounted from a secret.</summary>
    public string SslCaLocation { get; init; }

    public string SaslMechanism { get; init; }
    public string SaslUsername { get; init; }

    /// <summary>From the Kubernetes secret (Kafka__SaslPassword), never from a committed file.</summary>
    public string SaslPassword { get; init; }

    /// <summary>Creates missing topics (and their dead-letter topics) at startup; existing topics are never changed.</summary>
    public bool ProvisionTopics { get; init; }

    public TopicDefaults Topics { get; init; } = new();

    public OutboxRelaySettings Outbox { get; init; } = new();

    public ConsumerSettings Consumers { get; init; } = new();

    /// <summary>Kafka's own limit is enforced on the producer too (ADR-0018): 1 MB.</summary>
    public const int MaxMessageBytes = 1_048_576;
}

/// <summary>Partitions, replication and retention for provisioned topics; per-topic overrides by name.</summary>
public sealed class TopicDefaults
{
    /// <summary>Zone-keyed topics need at least as many partitions as Stream replicas.</summary>
    public int Partitions { get; init; } = 6;

    /// <summary>Three brokers for a standard site; one for the small-site profile and vm-local.</summary>
    public short ReplicationFactor { get; init; } = 3;

    public int MinInSyncReplicas { get; init; } = 2;

    public Dictionary<string, TopicOverride> Overrides { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Also creates AMAN's feed topics (<c>aman.feed.*.v1</c>, ARV-048). AMAN owns them in a shared cluster; set this only
    /// where no AMAN creates them (vm-local, dev and demo, where the simulator plays AMAN).
    /// </summary>
    public bool ProvisionAmanFeed { get; init; }
}

public sealed class TopicOverride
{
    public int? Partitions { get; init; }
    public int? RetentionDays { get; init; }
}

/// <summary>The outbox relay (ADR-0018).</summary>
public sealed class OutboxRelaySettings
{
    /// <summary>Runs the relay in this host. Every host that writes domain events can; one leader relays at a time.</summary>
    public bool Enabled { get; init; } = true;

    public int BatchSize { get; init; } = 100;

    public int PollMilliseconds { get; init; } = 500;

    /// <summary>Sent rows are kept this long for tracing, then removed.</summary>
    public int KeepSentDays { get; init; } = 7;

    /// <summary>
    /// Processed-event rows (the inbox) are kept this long. Longer than the longest retention of a deleting topic (30
    /// days), so a replay from the earliest offset never applies an event twice; compacted topics carry state and their
    /// consumers apply it by version.
    /// </summary>
    public int KeepProcessedDays { get; init; } = 35;

    /// <summary>A row this many attempts old is logged as an error on every further failure, for the alert.</summary>
    public int StuckAfterAttempts { get; init; } = 10;
}

/// <summary>Endpoint defaults (ADR-0018).</summary>
public sealed class ConsumerSettings
{
    public int RetryCount { get; init; } = 3;
    public int RetryMinMilliseconds { get; init; } = 200;
    public int RetryMaxMilliseconds { get; init; } = 5_000;
    public int CheckpointSeconds { get; init; } = 5;
    public ushort CheckpointMessageCount { get; init; } = 500;

    /// <summary>Parallel lanes per endpoint, split by key so order holds per key.</summary>
    public int ConcurrentMessageLimit { get; init; } = 8;

    /// <summary>How long a host waits at start for the topics it consumes to exist before starting the bus anyway.</summary>
    public int StartWaitSeconds { get; init; } = 300;
}
