using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Outbox;
using Ariva.IntegrationTests.Security;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ariva.IntegrationTests.Faults;

[KafkaTopic(KafkaTopics.SlaBreachDetected)]
public sealed class OutageProbe : EventBase
{
    public string Key { get; set; }
    public int Step { get; set; }

    public override string GetPartitionKey() => Key;
}

public sealed class OutageProbeConsumer : IConsumer<OutageProbe>
{
    public static readonly ConcurrentQueue<(string Key, int Step)> Received = new();

    public Task Consume(ConsumeContext<OutageProbe> context)
    {
        Received.Enqueue((context.Message.Key, context.Message.Step));
        return Task.CompletedTask;
    }
}

/// <summary>
/// ARV-072, the broker cut off under a running host. Domain events keep going into the outbox in their own transactions;
/// the relay sends nothing while the broker is unreachable and loses nothing; the readiness check reports the bus
/// unhealthy (what the orchestrator and the operations dashboards show); a direct publish, which is how Ingest hands
/// sensor batches on, fails, and Ingest answers the device 503 with Retry-After. Once the broker is back the relay
/// resumes by itself and the consumer gets every event of a key once, in the order they were written.
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class BrokerOutageTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "kafka";
    private const string Purpose = "it-faults";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly EventCatalog _catalog = new([typeof(OutageProbe).Assembly]);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await faults.RestoreAsync(Proxy);

    private async Task<IHost> StartHostAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Database:Host"] = faults.Host,
            ["Database:Port"] = faults.PostgresPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Name"] = FaultsFixture.Database,
            ["Database:Username"] = faults.PostgresUsername,
            ["Database:Password"] = faults.PostgresPassword,
            ["Kafka:Enabled"] = "true",
            ["Kafka:BootstrapServers"] = faults.KafkaBootstrap,
            ["Kafka:ServiceName"] = "it",
            ["Kafka:ProvisionTopics"] = "true",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Outbox:PollMilliseconds"] = "200",
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1"
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(OutageProbe).Assembly)
            .Consume<OutageProbe, OutageProbeConsumer>(KafkaTopics.SlaBreachDetected, Purpose));
        var host = builder.Build();
        await host.StartAsync(Ct);
        (await ReadinessAsync(host, HealthStatus.Healthy, TimeSpan.FromSeconds(240))).Should().Be(HealthStatus.Healthy, "the bus is ready before the test");
        return host;
    }

    private static async Task<HealthStatus> ReadinessAsync(IHost host, HealthStatus wanted, TimeSpan within)
    {
        var checks = host.Services.GetRequiredService<HealthCheckService>();
        var until = DateTime.UtcNow + within;
        HealthStatus status;
        while ((status = (await checks.CheckHealthAsync(c => c.Tags.Contains("ready"), Ct)).Status) != wanted && DateTime.UtcNow < until)
            await Task.Delay(500, Ct);
        return status;
    }

    /// <summary>What a service does when it commits a change that raises events: the rows go in with the change.</summary>
    private async Task WriteAsync(IHost host, string key, IEnumerable<int> steps)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        unitOfWork.StorageProvider.BeginTransaction(IsolationLevel.ReadCommitted);
        await new NHibernateDomainEventOutbox(_catalog, TimeProvider.System).WriteAsync(unitOfWork.StorageProvider,
            [.. steps.Select(step => new OutageProbe { Key = key, Step = step, CorrelationId = "corr-" + key })], Ct);
        unitOfWork.PromiseToCommit();
        await unitOfWork.EndAsync(Ct);
    }

    private async Task<(long Unsent, long Sent)> OutboxAsync(string key)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE sent_on IS NULL), count(*) FILTER (WHERE sent_on IS NOT NULL) FROM outbox_message WHERE message_key = @key", sql);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static int[] StepsOf(string key) => [.. OutageProbeConsumer.Received.Where(r => r.Key == key).Select(r => r.Step)];

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"{what}: not within {timeout}");
            await Task.Delay(500, Ct);
        }
    }

    [Fact]
    public async Task Outbox_Should_HoldEventsWhileTheBrokerIsDownAndRelayThemInOrder_When_ItIsBack()
    {
        using var host = await StartHostAsync();
        var key = "K-" + Guid.NewGuid().ToString("N")[..8];
        await WriteAsync(host, key, [1, 2, 3]);
        await WaitForAsync(() => Task.FromResult(StepsOf(key).Length == 3), TimeSpan.FromSeconds(60), "the first three before the outage");

        await faults.CutAsync(Proxy);
        await WriteAsync(host, key, [4, 5]);
        await WriteAsync(host, key, [6, 7, 8]);

        // A direct publish (Ingest's sensing sink) cannot reach the broker: Ingest turns that into 503 with Retry-After.
        var clock = Stopwatch.StartNew();
        var publish = async () => await host.Services.GetRequiredService<ISvcMessageBus>()
            .PublishAsync(new OutageProbe { Key = key + "-direct", Step = 1, CorrelationId = "corr-direct" }, Ct);
        await publish.Should().ThrowAsync<Exception>("nothing is acknowledged without the broker");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the producer gives up within its message timeout (30 seconds)");

        (await ReadinessAsync(host, HealthStatus.Unhealthy, TimeSpan.FromSeconds(90))).Should().NotBe(HealthStatus.Healthy,
            "readiness reports the bus, so the orchestrator and the dashboards see the outage");
        (await OutboxAsync(key)).Should().Be((5, 3), "the five written during the outage wait in the outbox, none lost and none sent");
        StepsOf(key).Should().Equal([1, 2, 3]);

        await faults.RestoreAsync(Proxy);

        // No restart: the relay resumes once the bus is ready; per key, in the order written, each once.
        await WaitForAsync(async () => (await OutboxAsync(key)).Unsent == 0 && StepsOf(key).Length >= 8, TimeSpan.FromSeconds(300), "the relay after the outage");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        StepsOf(key).Should().Equal([1, 2, 3, 4, 5, 6, 7, 8]);
        (await ReadinessAsync(host, HealthStatus.Healthy, TimeSpan.FromSeconds(120))).Should().Be(HealthStatus.Healthy);
        await host.StopAsync(Ct);
    }
}
