using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Creates the topics Ariva owns, and a dead-letter topic for every topic, when they are missing (ADR-0018). Existing
/// topics are never altered: a partition or retention change is an operator action. Runs only with
/// <c>Kafka:ProvisionTopics</c>; in the clusters the Helm topics job (<c>--provision-topics</c>, ARV-062) does it once per
/// release instead, so the hosts need no create rights. A broker that is not reachable yet does not stop the host:
/// provisioning retries every 10 seconds in the background while readiness reports the bus as not ready, and anything
/// produced before the topics exist waits in the outbox for its retry.
/// </summary>
internal sealed class TopicProvisioner(KafkaSettings settings, ILogger<TopicProvisioner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProvisionAsync(settings, logger, TimeSpan.FromSeconds(30));
                return;
            }
            catch (KafkaException e)
            {
                logger.LogWarning(e, "Kafka topics not provisioned yet ({Reason}); retrying in 10 seconds", e.Error.Reason);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// One pass: creates the missing topics and returns their names (empty when every topic exists). Throws
    /// <see cref="KafkaException"/> when the broker cannot be reached within <paramref name="timeout"/> or refuses a topic.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ProvisionAsync(KafkaSettings settings, ILogger logger, TimeSpan timeout)
    {
        using var admin = new AdminClientBuilder(KafkaClientConfig.Admin(settings)).Build();
        var existing = admin.GetMetadata(timeout).Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
        var missing = TopicCatalog.ForProvisioning(settings.Topics).Where(t => !existing.Contains(t.Name)).ToList();
        if (missing.Count == 0)
        {
            logger.LogInformation("Kafka topics present ({Count})", existing.Count);
            return [];
        }

        try
        {
            await admin.CreateTopicsAsync(missing.Select(Specification), new CreateTopicsOptions { RequestTimeout = timeout });
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Another pod created some of them first.
        }

        logger.LogInformation("Kafka topics created: {Topics}", string.Join(", ", missing.Select(t => t.Name)));
        return missing.Select(t => t.Name).ToList();
    }

    internal static TopicSpecification Specification(TopicSpec spec)
    {
        var configs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["min.insync.replicas"] = spec.MinInSyncReplicas.ToString(CultureInfo.InvariantCulture),
            ["max.message.bytes"] = KafkaSettings.MaxMessageBytes.ToString(CultureInfo.InvariantCulture),
            ["cleanup.policy"] = spec.Compacted ? "compact" : "delete"
        };
        if (spec.RetentionDays is { } days)
            configs["retention.ms"] = TimeSpan.FromDays(days).TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
        return new TopicSpecification { Name = spec.Name, NumPartitions = spec.Partitions, ReplicationFactor = spec.ReplicationFactor, Configs = configs };
    }
}

/// <summary>
/// Client settings shared by Ariva's producer, the admin client and the raw stream consumer; the probe's admin client runs
/// on the producer's handle, so it has the producer's settings.
/// </summary>
public static class KafkaClientConfig
{
    public static AdminClientConfig Admin(KafkaSettings settings) => Apply(new AdminClientConfig(), settings);

    /// <summary>
    /// The base of MassTransit's rider clients (ARV-177): every topic endpoint's consumer and any admin client the rider builds
    /// take it, merged with their own settings, in place of MassTransit's empty default. It carries only what no Ariva client
    /// may lack, no implicit topic creation; the brokers, TLS and SASL are set on the rider's host (Ariva.Di).
    /// </summary>
    public static ClientConfig Rider() => new() { AllowAutoCreateTopics = false };

    /// <summary>librdkafka's <c>message.timeout.ms</c> for Ariva's producer: 30 seconds.</summary>
    public const int MessageTimeoutMilliseconds = 30_000;

    /// <summary>
    /// librdkafka's <c>queue.buffering.max.kbytes</c> for Ariva's producer (ARV-167): 32 MiB, 32 messages at the 1 MB
    /// maximum. librdkafka's default (1 GiB) is larger than every host's pod memory limit (750 to 1500 MiB in the chart),
    /// so a broker outage could get a pod killed before the queue refused anything. The arithmetic is in
    /// docs/architecture/overview.md (Producing).
    /// </summary>
    public const int QueueMaxKbytes = 32 * 1024;

    /// <summary>
    /// librdkafka's <c>queue.buffering.max.messages</c> for Ariva's producer (ARV-167): 10,000 (default 100,000). A queue
    /// that is full refuses the next message at once (<c>Local_QueueFull</c>): Ingest answers 503 with Retry-After, the
    /// relay ends its pass and tries the row again after its broker retry (ARV-165).
    /// </summary>
    public const int QueueMaxMessages = 10_000;

    /// <summary>
    /// librdkafka's <c>topic.metadata.propagation.max.ms</c> for Ariva's producer (ARV-177): 5 seconds (default 30). How long
    /// the client takes a topic the brokers do not know for one still being created before it fails what it holds for the
    /// topic (the broker's <c>UnknownTopicOrPart</c>, 5.5 s measured), and every later send for it at once
    /// (<c>Local_UnknownTopic</c>); a send once the topic exists asks the brokers again and goes. At the default it equals
    /// the message timeout, so a send for a missing topic failed as a message timeout, which reads as the broker being away:
    /// the relay then ended every pass at that row. A topic the topics job created has reached every broker long before a
    /// host sends to it (propagation takes milliseconds); one being created meanwhile only fails a send, which its sender
    /// sends again.
    /// </summary>
    public const int TopicPropagationMilliseconds = 5_000;

    public static ProducerConfig Producer(KafkaSettings settings) => Apply(new ProducerConfig
    {
        // ADR-0018: every write acknowledged by all in-sync replicas, no duplicates from producer retries, 1 MB cap.
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageMaxBytes = KafkaSettings.MaxMessageBytes,
        // No linger: the outbox relay awaits each produce in order, so a linger would be paid per row.
        LingerMs = 0,
        // A produce that the broker has not acknowledged in 30 seconds fails, so the outbox relay ends its pass and retries
        // the row later instead of holding its transaction open for librdkafka's default five minutes.
        MessageTimeoutMs = MessageTimeoutMilliseconds,
        // ARV-166: the delivery report keeps no copy of the key, value or headers. Nothing reads them (the producer awaits
        // the report only for its outcome), and each copy would cost up to the message itself (1 MB) until the report is
        // handled.
        DeliveryReportFields = "none",
        CompressionType = CompressionType.Lz4,
        // ARV-167: the queue is bounded to fit the pods' memory limits, and a full queue refuses at once.
        QueueBufferingMaxKbytes = QueueMaxKbytes,
        QueueBufferingMaxMessages = QueueMaxMessages,
        // ARV-177: a send for a missing topic fails as its own failure after about 5 s (UnknownTopicOrPart), not as a timeout.
        TopicMetadataPropagationMaxMs = TopicPropagationMilliseconds
    }, settings);

    public static ConsumerConfig Consumer(KafkaSettings settings, string groupId) => Apply(new ConsumerConfig
    {
        GroupId = groupId,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        // Offsets are committed by the stream consumer after its handler has checkpointed state (ARV-020 review).
        EnableAutoCommit = false,
        EnableAutoOffsetStore = false,
        PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        FetchMaxBytes = 16 * KafkaSettings.MaxMessageBytes
    }, settings);

    private static T Apply<T>(T config, KafkaSettings settings) where T : ClientConfig
    {
        ArgumentNullException.ThrowIfNull(settings);
        config.BootstrapServers = settings.BootstrapServers;
        config.ClientId = $"ariva-{settings.ServiceName}";
        // ARV-177: no Ariva client creates a topic implicitly, whatever the cluster's auto.create.topics.enable. librdkafka
        // lets a producer-type client (the admin client is one) ask for that by default, so a send or a metadata request for a
        // missing topic created it with the broker's defaults; topics come from the provisioner or the topics job only. A
        // send for a missing topic fails as that send's own failure (UnknownTopicOrPart within TopicPropagationMilliseconds,
        // then Local_UnknownTopic at once), which the relay backs off per row, holding the topic until that backoff ends, and Ingest
        // answers with 503.
        config.AllowAutoCreateTopics = false;
        config.SecurityProtocol = Enum.Parse<SecurityProtocol>(settings.SecurityProtocol, ignoreCase: true);
        if (!string.IsNullOrEmpty(settings.SslCaLocation))
            config.SslCaLocation = settings.SslCaLocation;
        if (!string.IsNullOrEmpty(settings.SaslMechanism))
        {
            config.SaslMechanism = Enum.Parse<SaslMechanism>(settings.SaslMechanism, ignoreCase: true);
            config.SaslUsername = settings.SaslUsername;
            config.SaslPassword = settings.SaslPassword;
        }

        return config;
    }
}
