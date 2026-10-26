using System.Diagnostics;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.IntegrationTests.Messaging;

/// <summary>
/// ARV-177 on a real broker whose <c>auto.create.topics.enable</c> is on (Kafka's default; each test's control shows that a
/// client with librdkafka's defaults does create the topic): no Ariva client creates a topic implicitly. Ariva's producer
/// (<see cref="KafkaClientConfig.Producer"/>) fails a send for a missing topic as the send's own failure, not as the broker's
/// absence, and creates nothing; its admin client (<see cref="KafkaClientConfig.Admin"/>) reads a missing topic's metadata
/// without creating it; and the readiness probe answers through the producer's handle with a request that names no topic.
/// Topics are compared through a metadata request for every topic, which never creates one. Each class gets its own broker
/// (<see cref="KafkaFixture"/>), so no other test's topic is in the way.
/// </summary>
public sealed class ImplicitTopicTests(KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private KafkaSettings Settings => new() { Enabled = true, BootstrapServers = kafka.BootstrapServers, ServiceName = "implicit" };

    private static void Note(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    /// <summary>The topics the broker has, read with a metadata request for every topic (which creates none).</summary>
    private HashSet<string> Topics()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers, AllowAutoCreateTopics = false }).Build();
        return admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Where(t => t.Error.Code == ErrorCode.NoError).Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
    }

    private static byte[] Value(string text) => System.Text.Encoding.UTF8.GetBytes("{\"probe\":\"" + text + "\"}");

    [Fact(Timeout = 180_000)]
    public async Task Send_Should_FailAsTheSendsOwnFailureAndCreateNoTopic_When_TheTopicIsMissing()
    {
        var missing = KafkaTopics.SlaDisputeChanged;
        var control = KafkaTopics.SlaBreachDetected;
        Topics().Should().NotContain([missing, control], "the broker starts without them");

        // The control: librdkafka's default for a producer asks the broker to create a missing topic, and this broker does.
        using (var plain = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build())
            await plain.ProduceAsync(control, new Message<string, byte[]> { Key = "c", Value = Value("control") }, Ct);
        Topics().Should().Contain(control, "a client with librdkafka's defaults creates the topic on this broker");

        using var produce = new KafkaProduce(Settings, TimeProvider.System, NullLogger<KafkaProduce>.Instance);
        var clock = Stopwatch.StartNew();
        var first = await FluentActions.Awaiting(() => produce.SendAsync(missing, "k", Value("first"), Guid.NewGuid(), null, Ct))
            .Should().ThrowAsync<KafkaException>();
        var firstTook = clock.Elapsed;
        Note($"first send: {first.Which.Error.Code} ({first.Which.GetType().Name}) after {firstTook.TotalSeconds:F1} s");
        // The broker's answer once the propagation bound has passed (5.5 s measured), well before the 30 s message timeout,
        // which would read as the broker being away (ARV-165) and end every relay pass at the row.
        first.Which.Error.Code.Should().BeOneOf(ErrorCode.UnknownTopicOrPart, ErrorCode.Local_UnknownTopic);
        firstTook.Should().BeLessThan(TimeSpan.FromMilliseconds(KafkaClientConfig.TopicPropagationMilliseconds) + TimeSpan.FromSeconds(3),
            "the propagation bound and librdkafka's one-second topic scan, not the message timeout");
        KafkaProduce.IsBrokerUnavailable(first.Which).Should().BeFalse("a missing topic is the send's own failure (its row backs off; Ingest answers 503)");
        Topics().Should().NotContain(missing, "Ariva's producer asks no broker to create a topic (AllowAutoCreateTopics false)");

        clock.Restart();
        var second = await FluentActions.Awaiting(() => produce.SendAsync(missing, "k", Value("second"), Guid.NewGuid(), null, Ct))
            .Should().ThrowAsync<KafkaException>();
        Note($"second send: {second.Which.Error.Code} ({second.Which.GetType().Name}) after {clock.Elapsed.TotalSeconds:F1} s");
        // Once the client knows the topic is missing, a send for it is refused at once.
        second.Which.Error.Code.Should().Be(ErrorCode.Local_UnknownTopic);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        KafkaProduce.IsBrokerUnavailable(second.Which).Should().BeFalse();
        Topics().Should().NotContain(missing);

        // The topics job creates the topic: the same producer sends to it, with no restart.
        using (var admin = new AdminClientBuilder(KafkaClientConfig.Admin(Settings)).Build())
            await admin.CreateTopicsAsync([new TopicSpecification { Name = missing, NumPartitions = 1, ReplicationFactor = 1 }]);
        clock.Restart();
        Exception last = null;
        var tries = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            tries++;
            try
            {
                await produce.SendAsync(missing, "k", Value("after"), Guid.NewGuid(), null, Ct);
                last = null;
                break;
            }
            catch (KafkaException e)
            {
                last = e;
                await Task.Delay(500, Ct);
            }
        }

        Note($"after the topic was created: sent after {clock.Elapsed.TotalSeconds:F1} s and {tries} tries (last failure {(last as KafkaException)?.Error.Code})");
        // A send for a topic the client took for missing asks the brokers again: the first try after the creation went (0.4 s).
        last.Should().BeNull("the producer sends to the topic once it exists, without a restart");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact(Timeout = 120_000)]
    public async Task MetadataRequest_Should_CreateNoTopic_When_ArivasAdminClientNamesAMissingTopic()
    {
        var missing = KafkaTopics.AlertStateChanged;
        var control = KafkaTopics.FlightChanged;
        Topics().Should().NotContain([missing, control], "the broker starts without them");

        // Ariva's admin client names the missing topic first, then a default client names the control.
        using (var admin = new AdminClientBuilder(KafkaClientConfig.Admin(Settings)).Build())
        {
            var answer = admin.GetMetadata(missing, TimeSpan.FromSeconds(10)).Topics.Single();
            Note($"Ariva's admin client metadata request: {answer.Topic} {answer.Error.Code}");
            answer.Error.Code.Should().NotBe(ErrorCode.NoError, "the topic is missing");
        }

        // The control: an admin client with librdkafka's defaults (a producer-type client) has the broker create the topic it
        // names. The broker creates it asynchronously (the answer is LeaderNotAvailable meanwhile), so the test waits for it,
        // within a bound; once it exists, a creation the first request had asked for would have had as long to happen.
        using (var plain = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            var named = plain.GetMetadata(control, TimeSpan.FromSeconds(10)).Topics.Single();
            Note($"control metadata request: {named.Topic} {named.Error.Code}");
        }

        var waited = Stopwatch.StartNew();
        while (!Topics().Contains(control) && waited.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(250, Ct);
        Note($"control topic created by the default admin client after {waited.Elapsed.TotalSeconds:F1} s: {Topics().Contains(control)}");
        Topics().Should().Contain(control, "the control shows this broker creates a topic a default client names, so the result below is the setting's");
        Topics().Should().NotContain(missing, "Ariva's admin client asks no broker to create a topic (AllowAutoCreateTopics false)");
    }

    [Fact(Timeout = 120_000)]
    public async Task BrokersAnswer_Should_DescribeTheClusterWithoutNamingATopic()
    {
        var before = Topics();
        using var produce = new KafkaProduce(Settings, TimeProvider.System, NullLogger<KafkaProduce>.Instance);
        produce.Started();
        var clock = Stopwatch.StartNew();
        (await produce.BrokersAnswerAsync(KafkaReadiness.ProbeTimeout)).Should().BeTrue("the broker answers DescribeCluster through the producer's handle");
        Note($"probe answered in {clock.Elapsed.TotalMilliseconds:F0} ms");
        // ARV-245: the outbox relay's gate reads the same probe through its transport.
        IOutboxTransport transport = new KafkaOutboxTransport(produce, new EventCatalog([]));
        (await transport.BrokersAnswerAsync(TestContext.Current.CancellationToken)).Should().BeTrue("the relay sees the brokers answer");
        Topics().Should().BeEquivalentTo(before, "the probe creates nothing");

        // No broker at the address: false within the probe's bound, never an exception.
        var away = new KafkaSettings { Enabled = true, BootstrapServers = "127.0.0.1:9", ServiceName = "implicit-away" };
        using var nowhere = new KafkaProduce(away, TimeProvider.System, NullLogger<KafkaProduce>.Instance);
        nowhere.Started();
        clock.Restart();
        (await nowhere.BrokersAnswerAsync(KafkaReadiness.ProbeTimeout)).Should().BeFalse();
        Note($"probe without a broker ended in {clock.Elapsed.TotalMilliseconds:F0} ms");
        clock.Elapsed.Should().BeLessThan(KafkaReadiness.ProbeTimeout + TimeSpan.FromSeconds(1), "the probe keeps to its bound");
        IOutboxTransport waiting = new KafkaOutboxTransport(nowhere, new EventCatalog([]));
        (await waiting.BrokersAnswerAsync(TestContext.Current.CancellationToken)).Should().BeFalse("the relay waits for the brokers");
    }
}
