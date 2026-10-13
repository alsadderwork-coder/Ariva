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
}
