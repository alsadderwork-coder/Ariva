using Confluent.Kafka;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Holds the host's start until the topics its rider consumes exist. The rider subscribes its topic endpoints when the
/// bus starts, and a topic that does not exist yet (Ariva.Api.Main provisions topics in the background on a fresh
/// cluster) faults the bus for good: the v8 rider does not subscribe again by itself. Registered before the bus, so the
/// bus starts once the topics are there. After <see cref="ConsumerSettings.StartWaitSeconds"/> it lets the host start
/// anyway (the readiness check then reports the bus) rather than wait forever.
/// </summary>
internal sealed class KafkaTopicsReady(KafkaSettings settings, IReadOnlyList<string> topics, TimeProvider timeProvider, ILogger<KafkaTopicsReady> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (topics.Count == 0)
            return;
        var until = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(0, settings.Consumers.StartWaitSeconds));
        using var admin = new AdminClientBuilder(KafkaClientConfig.Admin(settings)).Build();
        var announced = false;
        while (true)
        {
            try
            {
                var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
                var present = metadata.Topics.Where(t => t.Error.Code == ErrorCode.NoError && t.Partitions.Count > 0).Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
                var missing = topics.Where(t => !present.Contains(t)).ToList();
                if (missing.Count == 0)
                {
                    if (announced)
                        logger.LogInformation("Kafka topics for this host's consumers are present; starting the bus");
                    return;
                }

                if (!announced)
                {
                    logger.LogWarning("Waiting for Kafka topics before starting the bus: {Topics}", string.Join(", ", missing));
                    announced = true;
                }
            }
            catch (KafkaException e)
            {
                logger.LogWarning(e, "Kafka not reachable yet; the bus waits");
            }

            if (timeProvider.GetUtcNow() >= until)
            {
                logger.LogError("Kafka topics still missing after {Seconds} seconds; starting the bus anyway", settings.Consumers.StartWaitSeconds);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
