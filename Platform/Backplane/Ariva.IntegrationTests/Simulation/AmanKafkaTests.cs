using Ariva.Business.Contracts.Aman.V1;
using Ariva.Infra.Messaging.Kafka;
using Ariva.IntegrationTests.Messaging;
using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ariva.IntegrationTests.Simulation;

/// <summary>
/// ARV-029 on a real broker: the emulated AMAN publishes a demo minute on the four <c>aman.feed.*.v1</c> topics, keyed by
/// desk, gate or flight, and Ariva's own Kafka value reader (<see cref="JsonValueSerializer{T}"/>) reads every record
/// back as the V1 contract, so the emulator's wire format is the one Ariva consumes.
/// </summary>
public sealed class AmanKafkaTests(KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 3, 18, 30, 0, TimeSpan.Zero);
    }

    private static List<ConsumeResult<string, T>> Read<T>(string bootstrap, string topic, int expected)
    {
        using var consumer = new ConsumerBuilder<string, T>(new ConsumerConfig
            {
                BootstrapServers = bootstrap,
                GroupId = "aman-emulator-test-" + Guid.NewGuid().ToString("N"),
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            })
            .SetValueDeserializer(new JsonValueSerializer<T>())
            .Build();
        consumer.Subscribe(topic);
        var records = new List<ConsumeResult<string, T>>();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (records.Count < expected && DateTime.UtcNow < until)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { } record)
                records.Add(record);
        }

        consumer.Close();
        return records;
    }

    [Fact]
    public async Task AmanKafka_Should_PublishRecordsArivaReads_When_AMinutePlays()
    {
        var settings = new AmanKafkaSettings { BootstrapServers = kafka.BootstrapServers };
        using var producer = new AmanKafka(() => settings, NullLogger<AmanKafka>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Simulation:Seed"] = "9303" }).Build();
        var clock = new Clock();
        using var engine = new ScenarioEngine(configuration, NullLogger<ScenarioEngine>.Instance, clock);
        var time = new FeedTime();
        var at = time.Observe(1110, _ => clock.GetUtcNow().UtcDateTime);
        var minute = engine.Read(day => AmanFeed.Build(day, 1110, m => at.AddMinutes(m - 1110), "DMO", BorderSides.Both, firstOfRun: true));
        minute.Demand.Should().NotBeEmpty("a run's first minute publishes the lane demand already computed");

        var (delivered, error) = await producer.PublishAsync(minute, Ct);

        error.Should().BeNull();
        delivered.Should().Be(minute.Count);
        var sessions = Read<DeskSessionChanged>(kafka.BootstrapServers, AmanContracts.Topic(AmanContracts.DeskSessions), minute.Sessions.Count);
        sessions.Should().HaveCount(minute.Sessions.Count).And.OnlyContain(r => r.Message.Value != null && r.Message.Key == r.Message.Value.DeskCode);
        sessions.Select(r => r.Message.Value.SourceEventId).Should().BeEquivalentTo(minute.Sessions.Select(s => s.SourceEventId));
        var desks = Read<DeskIntervalStats>(kafka.BootstrapServers, AmanContracts.Topic(AmanContracts.DeskIntervalStats), minute.Desks.Count);
        desks.Should().HaveCount(minute.Desks.Count).And.OnlyContain(r => r.Message.Value != null && r.Message.Value.IntervalSeconds == 60);
        var gates = Read<EGateIntervalStats>(kafka.BootstrapServers, AmanContracts.Topic(AmanContracts.EgateIntervalStats), minute.Gates.Count);
        gates.Should().HaveCount(minute.Gates.Count).And.OnlyContain(r => r.Message.Value != null &&
            r.Message.Value.RejectsByCategory.Values.Sum() == r.Message.Value.Rejected && r.Message.Key == r.Message.Value.GateCode);
        var demand = Read<InboundFlightLaneDemand>(kafka.BootstrapServers, AmanContracts.Topic(AmanContracts.InboundLaneDemand), minute.Demand.Count);
        demand.Should().HaveCount(minute.Demand.Count).And.OnlyContain(r => r.Message.Value != null && r.Message.Key == r.Message.Value.FlightKey &&
            r.Message.Value.PassengersByLane.ContainsKey("VIS"));
    }
}
