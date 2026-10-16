using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Configurations;
using Testcontainers.Kafka;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// Every Kafka container of the integration suite is built here (<c>KafkaFixture</c>, <c>FaultsFixture</c>): one image and
/// one start command.
/// </summary>
public static class KafkaContainers
{
    /// <summary>The broker image of the suite.</summary>
    public const string Image = "confluentinc/cp-kafka:7.6.1";

    /// <summary>
    /// ARV-156: Testcontainers.Kafka 4.15.0 starts the container with
    /// <c>while [ ! -f /testcontainers.sh ]; do sleep 0.1; done; /testcontainers.sh</c> while its start callback copies the
    /// script in, so the shell can execute the script while the copy still holds it open: "Text file busy", exit 126, and
    /// the container stops before Kafka runs (a loaded machine widens the window). This command waits until the script is
    /// not empty and has the shell read it rather than execute the file.
    /// </summary>
    public const string StartCommand = "while [ ! -s /testcontainers.sh ]; do sleep 0.1; done; /bin/sh /testcontainers.sh";

    /// <summary>A Kafka builder with the suite's image and start command (the command replaces the module's, not appended).</summary>
    public static KafkaBuilder Builder() => new KafkaBuilder(Image).WithCommand(new OverwriteEnumerable<string>([StartCommand]));

    /// <summary>
    /// Waits until the broker names a controller. Testcontainers.Kafka 4.15.0 reports a KRaft broker ready once it logs
    /// "Transitioning from RECOVERY to RUNNING", but an admin request made at once can still find no controller and wait its
    /// whole timeout for one: QueueStreamScaleOutTests, whose first call creates topics, failed so ("Failed while waiting for
    /// controller: Local: Timed out", 60 s) in two of three PR #67 CI runs on 2026-10-16. Each try uses a new admin client, so
    /// it reads fresh metadata; the wait is bounded and fails with a fixed message.
    /// </summary>
    public static async Task UntilControllerAsync(string bootstrapServers, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            try
            {
                using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
                var cluster = await admin.DescribeClusterAsync(new DescribeClusterOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
                if (cluster.Controller is not null)
                    return;
            }
            catch (KafkaException) when (DateTime.UtcNow < until)
            {
                // Not answering yet: try again with a new client.
            }

            if (DateTime.UtcNow >= until)
                throw new TimeoutException("The Kafka container named no controller within 2 minutes of its start.");
            await Task.Delay(500, ct);
        }
    }
}
