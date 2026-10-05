using Ariva.Core.Messaging;
using Ariva.Di;
using Ariva.Infra.Messaging;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.IntegrationTests.Messaging;

/// <summary>
/// ARV-062 on a real broker: the Helm topics Job's command (<c>api-main --provision-topics</c>) creates every topic the
/// catalog provisions, dead-letter topics included, with the configured partitions and retention; a second run (the
/// next release) succeeds and changes nothing; and a topic that already exists is never altered, even when it differs
/// from the catalog (a partition or retention change is an operator action, ADR-0018).
/// </summary>
public sealed class TopicProvisioningTests(KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
    {
        ["Kafka:Enabled"] = "true",
        ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
        ["Kafka:ServiceName"] = "topics-job",
        ["Kafka:Topics:Partitions"] = "2",
        ["Kafka:Topics:ReplicationFactor"] = "1",
        ["Kafka:Topics:MinInSyncReplicas"] = "1"
    }).Build();

    [Fact(Timeout = 300_000)]
    public async Task ProvisionTopics_Should_CreateEveryTopicOnceAndNeverAlterOne_When_RunForEachRelease()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        // An operator made this one by hand with one partition: the job must leave it as it is.
        var handMade = KafkaTopics.DeviceHealth;
        await admin.CreateTopicsAsync([new TopicSpecification { Name = handMade, NumPartitions = 1, ReplicationFactor = 1 }]);

        var first = await TopicProvisioning.RunAsync(Configuration(), TimeSpan.FromMinutes(2), Ct);

        first.Should().Be(0);
        var expected = TopicCatalog.ForProvisioning(new TopicDefaults { Partitions = 2, ReplicationFactor = 1, MinInSyncReplicas = 1 });
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(30)).Topics.ToDictionary(t => t.Topic, StringComparer.Ordinal);
        metadata.Keys.Should().Contain(expected.Select(t => t.Name));
        expected.Should().Contain(t => t.Name == KafkaTopics.DeadLetter(KafkaTopics.FlowQueueInterval), "every topic gets its dead-letter topic");
        metadata[KafkaTopics.FlowQueueInterval].Partitions.Should().HaveCount(2);
        metadata[handMade].Partitions.Should().HaveCount(1, "an existing topic is never changed");

        var described = await admin.DescribeConfigsAsync([new ConfigResource { Type = ResourceType.Topic, Name = KafkaTopics.FlowQueueInterval }]);
        described.Single().Entries["retention.ms"].Value.Should().Be(TimeSpan.FromDays(30).TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        described.Single().Entries["min.insync.replicas"].Value.Should().Be("1");

        var second = await TopicProvisioning.RunAsync(Configuration(), TimeSpan.FromMinutes(2), Ct);

        second.Should().Be(0, "the next release's job finds every topic and succeeds");
        admin.GetMetadata(handMade, TimeSpan.FromSeconds(30)).Topics.Single().Partitions.Should().HaveCount(1);
    }
}
