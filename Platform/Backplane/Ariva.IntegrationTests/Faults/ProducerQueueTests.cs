using System.Diagnostics;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Sensing;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.IntegrationTests.Faults;

/// <summary>
/// ARV-167 on a real broker and the real client: the host's producer has a bounded queue, and during an outage a full queue
/// refuses the next message at once (<c>Local_QueueFull</c>) instead of holding it, so Ingest answers the push 503 with
/// Retry-After without waiting (the 503 itself: PushEndpointTests). The queue here holds three messages (the hosts use
/// <see cref="KafkaClientConfig.QueueMaxMessages"/>); the three it took while the broker was away show that the refusal is
/// the bound, not the outage. Once the broker is back the three are delivered and a new send succeeds, on the same producer.
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class ProducerQueueTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "kafka";
    private const int Queue = 3;
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await faults.RestoreAsync(Proxy);

    [Fact(Timeout = 240_000)]
    public async Task Produce_Should_RefuseAtOnceAndRecover_When_TheQueueIsFullDuringAnOutage()
    {
        var settings = new KafkaSettings
        {
            Enabled = true,
            BootstrapServers = faults.KafkaBootstrap,
            ServiceName = "queue",
            Topics = new TopicDefaults { Partitions = 1, ReplicationFactor = 1, MinInSyncReplicas = 1 }
        };
        await TopicProvisioner.ProvisionAsync(settings, NullLogger.Instance, TimeSpan.FromSeconds(60));
        var config = KafkaClientConfig.Producer(settings);
        config.QueueBufferingMaxMessages = Queue;
        using var produce = new KafkaProduce(config, TimeProvider.System, NullLogger.Instance);
        var bus = new KafkaMessageBus(produce, new EventCatalog([typeof(OutageProbe).Assembly]));
        var key = "Q-" + Guid.NewGuid().ToString("N")[..8];
        OutageProbe Probe(int step) => new() { Key = key, Step = step, CorrelationId = "corr-" + key };

        await bus.PublishAsync(Probe(0), Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        try
        {
            await faults.CutAsync(Proxy);
            var queued = Enumerable.Range(1, Queue).Select(step => bus.PublishAsync(Probe(step), CancellationToken.None)).ToList();
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            queued.Should().OnlyContain(t => !t.IsCompleted, "the queue took them and holds them while the broker is away");

            var clock = Stopwatch.StartNew();
            Action refused = () => _ = bus.PublishAsync(Probe(Queue + 1), Ct);
            refused.Should().Throw<ProduceException<string, byte[]>>("the call itself refuses when the queue is full")
                .Which.Error.Code.Should().Be(ErrorCode.Local_QueueFull);
            var sink = new MessageBusSensingSink(bus);
            Action ingest = () => sink.PublishAsync([Probe(Queue + 2)], Ct).GetAwaiter().GetResult();
            ingest.Should().Throw<ProduceException<string, byte[]>>("Ingest's sink fails the same way, which Ingest answers 503 with Retry-After");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "nothing waited for the 30 second message timeout or the 45 second bound");

            await faults.RestoreAsync(Proxy);
            await Task.WhenAll(queued).WaitAsync(KafkaProduce.SendTimeout + Slack, Ct);
            await bus.PublishAsync(Probe(Queue + 3), Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            produce.Unreported.Should().Be(0, "every send got its report");
        }
        finally
        {
            await faults.RestoreAsync(Proxy);
        }
    }
}
