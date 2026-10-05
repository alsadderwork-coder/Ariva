using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ariva.Di;

/// <summary>
/// The one-shot topic command (ARV-062). Ariva.Api.Main runs it instead of the web host when started with
/// <c>--provision-topics</c>; the Helm chart runs that as a Job before each release, so the hosts can run with
/// <c>Kafka:ProvisionTopics</c> off and a Kafka principal that cannot create topics. It creates what is missing
/// (ADR-0018 settings: partitions, replication, retention, dead-letter topics) and never changes an existing topic.
/// </summary>
public static class TopicProvisioning
{
    public const string Flag = "--provision-topics";

    /// <summary>How long one attempt waits for the broker's metadata and for the topics to be created.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    public static bool IsRequested(string[] args) => args?.Contains(Flag, StringComparer.Ordinal) == true;

    /// <summary>The arguments without the flag, so the configuration command line provider never sees it.</summary>
    public static string[] WithoutFlag(string[] args) => (args ?? []).Where(a => !string.Equals(a, Flag, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Creates the missing topics and returns the process exit code: 0 when every topic exists afterwards (or Kafka is
    /// off, so no host needs them), 1 on a configuration error or when the broker is still unreachable after
    /// <paramref name="deadline"/> (default five minutes; the Job's own deadline is the outer bound).
    /// </summary>
    public static async Task<int> RunAsync(IConfiguration configuration, TimeSpan? deadline = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
        var logger = loggerFactory.CreateLogger(typeof(TopicProvisioning));
        var settings = configuration.GetSection(KafkaSettings.SectionName).Get<KafkaSettings>() ?? new KafkaSettings();
        try
        {
            MessagingExtensions.EnsureProductionTransport(settings, configuration);
        }
        catch (InvalidOperationException e)
        {
            logger.LogCritical("Topic provisioning refused: {Reason}", e.Message);
            return 1;
        }

        if (!settings.Enabled)
        {
            logger.LogWarning("Kafka:Enabled is false: no host uses Kafka, so no topics are created");
            return 0;
        }

        var until = DateTime.UtcNow + (deadline ?? TimeSpan.FromMinutes(5));
        while (true)
        {
            var remaining = until - DateTime.UtcNow;
            var attempt = remaining < AttemptTimeout ? remaining : AttemptTimeout;
            try
            {
                if (attempt <= TimeSpan.Zero)
                    throw new KafkaException(ErrorCode.Local_TimedOut);
                var created = await TopicProvisioner.ProvisionAsync(settings, logger, attempt);
                logger.LogInformation("Topic provisioning finished: {Count} topic(s) created", created.Count);
                return 0;
            }
            catch (KafkaException e) when (DateTime.UtcNow + RetryDelay < until && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Kafka not ready ({Reason}); retrying in {Delay} seconds", e.Error.Reason, RetryDelay.TotalSeconds);
            }
            catch (KafkaException e)
            {
                logger.LogCritical(e, "Topic provisioning failed: {Reason}", e.Error.Reason);
                return 1;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // A Kafka section the client cannot use (an unknown security protocol or SASL mechanism).
                logger.LogCritical(e, "Topic provisioning failed: the Kafka settings are not usable");
                return 1;
            }

            try
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                logger.LogCritical("Topic provisioning cancelled");
                return 1;
            }
        }
    }
}
