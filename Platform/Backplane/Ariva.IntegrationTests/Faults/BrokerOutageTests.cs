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
/// the relay sends nothing while the broker is unreachable and loses nothing (since ARV-167 it tries; since ARV-165 a failed
/// row is tried again 5 seconds later without an attempt counted, and holds its key meanwhile); the readiness checks no longer report the
/// bus healthy (MassTransit reports it Degraded while its Kafka endpoint is faulted, never Unhealthy); a direct publish,
/// which is how Ingest hands sensor batches on, fails, and Ingest answers the device 503 with Retry-After. Once the
/// broker is back the relay resumes by itself and the consumer gets every event of a key once, in the order they were
/// written. The outage lasts until the broker has evicted the consumer that lost its connection (its session timed out),
/// so the endpoint comes back by joining its group again and resuming from the committed offsets.
/// <para>
/// The test sequences around two production races on purpose, and these conditions are not proof that the races are
/// handled: the broker is cut only once the consumer has committed what it read (a cut during an offset commit leaves the
/// bus Healthy until the commit returns; ARV-165 owns that race), and events are written and the direct publish made only
/// once readiness reports the outage, so that no produce is in flight at the cut. ARV-166 bounded the second race (a
/// produce queued behind one in flight when the connection drops was discarded with the producer MassTransit's rider
/// disposed after the first failed send) and ARV-167 removed its cause (Ariva's own producer for sends, StuckProduceTests).
/// The sequencing stays so that the direct publish here fails on the producer's own message timeout.
/// </para>
/// <para>
/// ARV-156: every wait is for a condition the test asserts, never for a time or a status that may not come, and all of
/// them share one budget inside the fact's timeout, so a step that is too slow fails with its name and the time each
/// earlier step took rather than as a timed out test.
/// </para>
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class BrokerOutageTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "kafka";
    private const string Purpose = "it-faults";

    /// <summary>The probe consumer's group: <c>ariva-&lt;Kafka:ServiceName&gt;.&lt;purpose&gt;</c> (ArivaMessagingBuilder).</summary>
    private const string Group = "ariva-it." + Purpose;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What every wait of the test shares: inside the fact's 300 s, so the step that runs out names itself.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);

    private readonly EventCatalog _catalog = new([typeof(OutageProbe).Assembly]);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _steps = [];

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
        await host.StartAsync(Ct); // held until the consumed Ariva topics exist and their consumers are ready (KafkaConsumerEndpoints)
        Done("the host started");
        (await ReadinessAsync(host, status => status == HealthStatus.Healthy, "the bus ready before the test")).Should().Be(HealthStatus.Healthy);
        return host;
    }

    /// <summary>Waits, within the budget, until the readiness checks give a status <paramref name="wanted"/> takes.</summary>
    private async Task<HealthStatus> ReadinessAsync(IHost host, Func<HealthStatus, bool> wanted, string what)
    {
        var checks = host.Services.GetRequiredService<HealthCheckService>();
        var status = HealthStatus.Healthy;
        await WaitForAsync(async () => wanted(status = (await checks.CheckHealthAsync(c => c.Tags.Contains("ready"), Ct)).Status), what);
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

    /// <summary>Whether the probe consumer's committed offset has reached the topic's latest offset (nothing left to commit).</summary>
    private static async Task<bool> CommittedAllAsync(Confluent.Kafka.IAdminClient admin)
    {
        var partition = new Confluent.Kafka.TopicPartition(KafkaTopics.SlaBreachDetected, new Confluent.Kafka.Partition(0));
        // Each admin call has its own 10 second bound, as DescribeConsumerGroups has, so a slow broker fails one poll of the
        // condition rather than holding it past the budget.
        var latest = await Confluent.Kafka.IAdminClientExtensions.ListOffsetsAsync(admin,
            [new Confluent.Kafka.Admin.TopicPartitionOffsetSpec { TopicPartition = partition, OffsetSpec = Confluent.Kafka.Admin.OffsetSpec.Latest() }],
            new Confluent.Kafka.Admin.ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var committed = await admin.ListConsumerGroupOffsetsAsync([new Confluent.Kafka.ConsumerGroupTopicPartitions(Group, [partition])],
            new Confluent.Kafka.Admin.ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var end = latest.ResultInfos.Single().TopicPartitionOffsetError.Offset.Value;
        return end > 0 && committed.Single().Partitions.Single().Offset.Value == end;
    }

    /// <summary>
    /// Records that a step is done and when: in the test's output as it happens (kept with the result, also when the fact
    /// times out) and for the message of a step that runs out of the budget.
    /// </summary>
    private void Done(string step)
    {
        var entry = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step} at {_clock.Elapsed.TotalSeconds:F1} s");
        _steps.Add(entry);
        TestContext.Current.TestOutputHelper?.WriteLine(entry);
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, within what is left of the budget; then records the step.</summary>
    private async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        while (!await condition())
        {
            if (_clock.Elapsed > Budget)
                throw new TimeoutException($"{what}: not within the test's {Budget.TotalSeconds:F0} s (done: {string.Join(", ", _steps)})");
            await Task.Delay(500, Ct);
        }

        Done(what);
    }

    /// <summary>
    /// Stops and disposes the host within <paramref name="within"/>: the stop waits for any message its consumer is handling.
    /// A dead letter that could never be sent used to hold that message for ever; since ARV-166 its retry loop ends when the
    /// host stops (DeadLetterShutdownTests), and the bound stays so that a hang anywhere in the stop fails the test with its
    /// own message rather than the fact's timeout. The test's budget (270 s) and this bound (25 s) stay inside the fact's 300 s.
    /// </summary>
    private static (Task Stop, Task<bool> Within) StopWithin(IHost host, TimeSpan within)
    {
        var stop = Task.Run(async () =>
        {
            using var shutdown = new CancellationTokenSource(within - TimeSpan.FromSeconds(5));
            await host.StopAsync(shutdown.Token);
            host.Dispose();
        });
        return (stop, WithinAsync());

        async Task<bool> WithinAsync() => await Task.WhenAny(stop, Task.Delay(within)) == stop;
    }

    [Fact(Timeout = 300_000)] // a fault test never hangs the run: a dependency that does not come back fails it
    public async Task Outbox_Should_HoldEventsWhileTheBrokerIsDownAndRelayThemInOrder_When_ItIsBack()
    {
        var host = await StartHostAsync();
        var completed = false;
        try
        {
            // The broker's own view, read past the proxy (also while the proxy is cut).
            using var admin = new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
            try
            {
                var key = "K-" + Guid.NewGuid().ToString("N")[..8];
                await WriteAsync(host, key, [1, 2, 3]);
                await WaitForAsync(() => Task.FromResult(StepsOf(key).Length == 3), "the first three before the outage");

                // Not cosmetic, and not proof that the race is handled (ARV-165 owns it): a cut while the consumer's offset
                // commit is in flight keeps MassTransit's Kafka endpoint ready and the bus Healthy until that commit returns
                // (45 s, or minutes), and the host cannot stop meanwhile. This test is about the outbox during an outage, so
                // it sequences around the race: the broker is cut only once the first three are committed.
                await WaitForAsync(() => CommittedAllAsync(admin), "the first three committed");
                await faults.CutAsync(Proxy);
                Done("the broker cut off");

                // Not cosmetic either: readiness reports the outage once the endpoint has faulted, so the rows below are
                // written during the outage. Since ARV-167 the relay waits for the producer, not the bus, so it tries them
                // during the outage too: each pass ends at its first failed row (the message timeout), which is tried again
                // after the broker retry (ARV-165) and holds its key, so none is sent and none is lost; the direct publish below shares the host's one producer and
                // fails on its own message timeout (before ARV-167 a produce queued behind a failed one could be discarded
                // with the producer the rider disposed). The bus reports the outage as Degraded, not Unhealthy (ARV-156): the
                // wait is for what is asserted, any status but Healthy.
                (await ReadinessAsync(host, status => status != HealthStatus.Healthy, "readiness reporting the outage")).Should().NotBe(HealthStatus.Healthy,
                    "the bus's readiness check reports the outage");
                await WriteAsync(host, key, [4, 5]);
                await WriteAsync(host, key, [6, 7, 8]);

                // A direct publish (Ingest's sensing sink) cannot reach the broker: Ingest turns that into 503 with Retry-After.
                var clock = Stopwatch.StartNew();
                var publish = async () => await host.Services.GetRequiredService<ISvcMessageBus>()
                    .PublishAsync(new OutageProbe { Key = key + "-direct", Step = 1, CorrelationId = "corr-direct" }, Ct);
                await publish.Should().ThrowAsync<Exception>("nothing is acknowledged without the broker");
                clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the producer gives up within its message timeout (30 seconds)");
                Done("the direct publish refused");

                // The outage outlasts the consumer's session (librdkafka's 45 s): the broker reports the group empty once it
                // has evicted the member that lost its connection, so after the restore the endpoint joins again.
                var describe = new Confluent.Kafka.Admin.DescribeConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(10) };
                await WaitForAsync(async () => (await admin.DescribeConsumerGroupsAsync([Group], describe)).ConsumerGroupDescriptions.Single().State ==
                                               Confluent.Kafka.ConsumerGroupState.Empty, "the consumer's session expired on the broker");

                (await OutboxAsync(key)).Should().Be((5, 3), "the five written during the outage wait in the outbox, none lost and none sent");
                StepsOf(key).Should().Equal([1, 2, 3]);

                await faults.RestoreAsync(Proxy);
                Done("the broker back");

                // No restart: the relay sends once its rows' backoff has passed, and the consumer resumes once its endpoint has
                // rejoined its group; per key, in the order written, each once (a record delivered again after the rejoin is
                // dropped by the inbox).
                await WaitForAsync(async () => (await OutboxAsync(key)).Unsent == 0 && StepsOf(key).Length >= 8, "the relay after the outage");
                await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                StepsOf(key).Should().Equal([1, 2, 3, 4, 5, 6, 7, 8]);
                (await ReadinessAsync(host, status => status == HealthStatus.Healthy, "the bus healthy again")).Should().Be(HealthStatus.Healthy);
                completed = true;
            }
            finally
            {
                // A failing step must not leave the broker cut while the host stops: a consumer stuck in an offset commit
                // holds the stop until the broker answers (ARV-165), and the fact's timeout would hide the step's own message.
                await faults.RestoreAsync(Proxy);
            }
        }
        finally
        {
            var (stop, within) = StopWithin(host, TimeSpan.FromSeconds(25));
            if (await within)
            {
                Done("the host stopped");
                if (completed)
                    await stop;
            }
            else
            {
                Done("the host not stopped within 25 s");
                if (completed)
                    throw new TimeoutException("The host did not stop and dispose within 25 s.");
            }
        }
    }
}
