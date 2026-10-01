using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Creates the topics Ariva owns, and a dead-letter topic for every topic, when they are missing (ADR-0018). Existing
/// topics are never altered: a partition or retention change is an operator action. Runs only with
/// <c>Kafka:ProvisionTopics</c> (the deployment may give hosts a principal without create rights). A broker that is not
/// reachable yet does not stop the host: provisioning retries every 10 seconds in the background while readiness
/// reports the bus as not ready, and anything produced before the topics exist waits in the outbox for its retry.
/// </summary>
internal sealed class TopicProvisioner(KafkaSettings settings, ILogger<TopicProvisioner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProvisionAsync();
                return;
            }
            catch (KafkaException e)
            {
                logger.LogWarning(e, "Kafka topics not provisioned yet ({Reason}); retrying in 10 seconds", e.Error.Reason);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task ProvisionAsync()
    {
        using var admin = new AdminClientBuilder(KafkaClientConfig.Admin(settings)).Build();
        var existing = admin.GetMetadata(TimeSpan.FromSeconds(30)).Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
        var missing = TopicCatalog.ForProvisioning(settings.Topics).Where(t => !existing.Contains(t.Name)).ToList();
        if (missing.Count == 0)
        {
            logger.LogInformation("Kafka topics present ({Count})", existing.Count);
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(missing.Select(Specification), new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
        }
        catch (CreateTopicsException e) when (e.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Another pod created some of them first.
        }

        logger.LogInformation("Kafka topics created: {Topics}", string.Join(", ", missing.Select(t => t.Name)));
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

/// <summary>Client settings shared by the rider, the admin client and the raw stream consumer.</summary>
public static class KafkaClientConfig
{
    public static AdminClientConfig Admin(KafkaSettings settings) => Apply(new AdminClientConfig(), settings);

    public static ProducerConfig Producer(KafkaSettings settings) => Apply(new ProducerConfig
    {
        // ADR-0018: every write acknowledged by all in-sync replicas, no duplicates from producer retries, 1 MB cap.
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageMaxBytes = KafkaSettings.MaxMessageBytes,
        // No linger: the outbox relay awaits each produce in order, so a linger would be paid per row.
        LingerMs = 0,
        // A produce that the broker has not acknowledged in 30 seconds fails, so the outbox relay backs off instead of
        // holding its transaction open for librdkafka's default five minutes.
        MessageTimeoutMs = 30_000,
        CompressionType = CompressionType.Lz4
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
