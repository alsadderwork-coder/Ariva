using System.Data;
using System.Diagnostics;
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

/// <summary>The consumer of the race: it runs the test's hook on the record that triggers it, inside its handling.</summary>
public sealed class CommitRaceConsumer : IConsumer<OutageProbe>
{
    /// <summary>The key and step whose handling runs <see cref="Hook"/>.</summary>
    public static (string Key, int Step)? Trigger { get; set; }

    public static Func<Task> Hook { get; set; }

    public static readonly System.Collections.Concurrent.ConcurrentQueue<(string Key, int Step)> Received = new();

    public async Task Consume(ConsumeContext<OutageProbe> context)
    {
        if (Trigger is { } trigger && trigger.Key == context.Message.Key && trigger.Step == context.Message.Step && Hook is { } hook)
        {
            Trigger = null;
            await hook();
        }

        Received.Enqueue((context.Message.Key, context.Message.Step));
    }
}

/// <summary>
/// ARV-165, the race of ARV-156 reproduced deterministically: the broker is cut from inside the consumer while it handles
/// a record, so before that record's offset commit (as the ARV-156 diagnostic did). MassTransit 8.5.11 commits with the
/// synchronous <c>Commit</c>, which waits for a coordinator the broker no longer gives, and its consumer agent completes
/// only after that commit returns (its <c>Close</c> waits for the partition's checkpointer), so the endpoint stays Ready and
/// MassTransit's bus check stays Healthy meanwhile (45 seconds in the diagnostic, for good in one loop run). The tests
/// prove the three bounds of the story while that commit is in flight: the readiness report turns Degraded within
/// <see cref="HealthBound"/> of the cut (Ariva's own <c>kafka</c> check asks a broker through the producer); the outbox
/// relay does not count the broker's absence against its rows, so it sends what was written during the outage within
/// <see cref="RelayBound"/> of the restore; and the host stops within its shutdown timeout and disposes within
/// <see cref="DisposeBound"/>. Each asserts that MassTransit's bus check was still Healthy at the moment that matters,
/// which shows the race was reproduced. The third test proves the stray consumer of ARV-166's residual (b) gone: an endpoint
/// between restarts when the host stops (its consumer ended on the failed commit) is stopped after the bus, so no consumer
/// of the stopped host rejoins its group. Each test has its own consumer group, so a stray of one cannot hold another's
/// partition.
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class CommitRaceTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "kafka";

    /// <summary>The readiness report turns Degraded within this of the cut (the check's own bound is 3 seconds).</summary>
    private static readonly TimeSpan HealthBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Rows written during the outage are sent within this of the restore: librdkafka's reconnect backoff (at most 10 s),
    /// the relay's retry after a broker failure (5 s), a pass's poll, and 5 s of slack.
    /// </summary>
    private static readonly TimeSpan RelayBound = TimeSpan.FromSeconds(25);

    /// <summary>The hosts' shutdown timeout (<c>Hosting:ShutdownSeconds</c>, 50 s), which a stop must end within.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(new Ariva.Infra.Settings.HostingSettings().ShutdownSeconds);

    private static readonly TimeSpan DisposeBound = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EventCatalog _catalog = new([typeof(OutageProbe).Assembly]);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _steps = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        CommitRaceConsumer.Trigger = null;
        CommitRaceConsumer.Hook = null;
        await faults.RestoreAsync(Proxy);
    }

    private static string GroupOf(string purpose) => "ariva-race." + purpose;

    private async Task<IHost> StartHostAsync(string purpose)
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
            ["Kafka:ServiceName"] = "race",
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
            .Consume<OutageProbe, CommitRaceConsumer>(KafkaTopics.SlaBreachDetected, purpose));
        var host = builder.Build();
        await host.StartAsync(Ct);
        Done("the host started");
        await WaitForAsync(async () => await ReadyAsync(host) == HealthStatus.Healthy, "the readiness report Healthy before the test");
        return host;
    }

    private static async Task<HealthStatus> ReadyAsync(IHost host) =>
        (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Tags.Contains("ready"), Ct)).Status;

    /// <summary>
    /// The race: a first record consumed and committed (the group is settled), then the broker cut from inside the consumer
    /// while it handles the second, before its offset commit. Returns a clock started at the cut.
    /// </summary>
    private async Task<Stopwatch> RaceAsync(IHost host, string purpose, string key)
    {
        using var admin = new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        var bus = host.Services.GetRequiredService<ISvcMessageBus>();
        await bus.PublishAsync(new OutageProbe { Key = key, Step = 0, CorrelationId = "corr-" + key }, Ct);
        await WaitForAsync(() => CommittedAllAsync(admin, GroupOf(purpose)), "the first record consumed and committed");

        var cut = new TaskCompletionSource<Stopwatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        CommitRaceConsumer.Hook = async () =>
        {
            await faults.CutAsync(Proxy);
            cut.TrySetResult(Stopwatch.StartNew());
        };
        CommitRaceConsumer.Trigger = (key, 1);
        await bus.PublishAsync(new OutageProbe { Key = key, Step = 1, CorrelationId = "corr-" + key }, Ct);
        var since = await cut.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Done("the broker cut from inside the consumer, before the record's commit");
        return since;
    }

    /// <summary>
    /// Watches the readiness report and MassTransit's bus check from the cut until the report is no longer Healthy, and
    /// returns how long that took and the bus check's status then.
    /// </summary>
    private async Task<(TimeSpan Took, HealthStatus Report, BusHealthStatus Bus)> UntilReportedAsync(IHost host, Stopwatch since)
    {
        var busControl = host.Services.GetRequiredService<IBusControl>();
        while (true)
        {
            var report = await ReadyAsync(host);
            var bus = busControl.CheckHealth().Status;
            if (report != HealthStatus.Healthy || since.Elapsed > HealthBound + TimeSpan.FromSeconds(50))
            {
                Done($"the readiness report {report} {since.Elapsed.TotalSeconds:F1} s after the cut (MassTransit's bus check {bus})");
                return (since.Elapsed, report, bus);
            }

            await Task.Delay(250, Ct);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task Health_Should_TurnDegradedAndTheRelayResume_When_TheBrokerIsCutDuringAnOffsetCommit()
    {
        const string purpose = "commit-race-health";
        var host = await StartHostAsync(purpose);
        try
        {
            try
            {
                var key = "R-" + Guid.NewGuid().ToString("N")[..8];
                var since = await RaceAsync(host, purpose, key);

                var (took, report, bus) = await UntilReportedAsync(host, since);
                report.Should().Be(HealthStatus.Degraded, "the outage raises the alert, readiness does not fail (the owner's ARV-125 decision)");
                took.Should().BeLessThan(HealthBound, "the readiness report sees the outage within its bound, commit or not");
                bus.Should().Be(BusHealthStatus.Healthy, "MassTransit's own check was still blind then, the commit in flight: the race was reproduced");

                // Rows of three keys written during the outage: the relay tries them, each failing on the producer's message
                // timeout; a broker that is away does not count against a row (no attempt, no growing backoff).
                var keys = Enumerable.Range(1, 3).Select(k => "W" + k + "-" + Guid.NewGuid().ToString("N")[..6]).ToList();
                foreach (var rowKey in keys)
                    await WriteAsync(host, rowKey, [1, 2]);
                await WaitForAsync(async () => (await RowsAsync(keys[0])).Any(r => r.LastError is not null) || (await RowsAsync(keys[1])).Any(r => r.LastError is not null),
                    "a relay send during the outage failed");
                // Long enough for the old backoff to have grown (2, 4, 8 s and on): the relay keeps trying through the outage.
                await Task.Delay(TimeSpan.FromSeconds(40), Ct);
                foreach (var rowKey in keys)
                    (await RowsAsync(rowKey)).Should().OnlyContain(r => r.Attempts == 0 && !r.Sent, "the broker's absence is not the row's failure, and nothing was sent");

                await faults.RestoreAsync(Proxy);
                var restored = Stopwatch.StartNew();
                Done("the broker back");
                await WaitForAsync(async () =>
                {
                    foreach (var rowKey in keys)
                    {
                        if (!(await RowsAsync(rowKey)).All(r => r.Sent))
                            return false;
                    }

                    return true;
                }, "every key's rows written during the outage sent");
                restored.Elapsed.Should().BeLessThan(RelayBound, "the relay resumes every key within its bound of the restore, however long the outage was");
                await WaitForAsync(async () => await ReadyAsync(host) == HealthStatus.Healthy, "the readiness report Healthy again");
            }
            finally
            {
                await faults.RestoreAsync(Proxy);
            }
        }
        finally
        {
            await StopAsync(host, assert: false);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task Host_Should_StopWithinItsShutdownTimeout_When_TheBrokerIsCutDuringAnOffsetCommit()
    {
        const string purpose = "commit-race-stop";
        var host = await StartHostAsync(purpose);
        var stopped = false;
        try
        {
            var key = "T-" + Guid.NewGuid().ToString("N")[..8];
            var since = await RaceAsync(host, purpose, key);
            // Stopped while the commit is in flight: 5 seconds after the cut, MassTransit's bus check still Healthy.
            var left = TimeSpan.FromSeconds(5) - since.Elapsed;
            if (left > TimeSpan.Zero)
                await Task.Delay(left, Ct);
            host.Services.GetRequiredService<IBusControl>().CheckHealth().Status.Should().Be(BusHealthStatus.Healthy,
                "the commit is still in flight when the host is stopped: the race was reproduced");

            stopped = true;
            await StopAsync(host, assert: true);
        }
        finally
        {
            await faults.RestoreAsync(Proxy);
            if (!stopped)
                await StopAsync(host, assert: false);
        }
    }

    /// <summary>
    /// ARV-166's residual (b): the commit fails once librdkafka's session times out (45 s), the consumer ends and its
    /// endpoint is between restarts; the host is stopped then, with the broker back. MassTransit's stop skips that endpoint
    /// (it stops only started, ready or faulted ones), and before ARV-165 its transport started a new consumer in the
    /// stopped host, which rejoined the group and consumed the uncommitted record with a disposed service provider (the
    /// baseline in backlog/progress.md). Now the endpoint is stopped after the bus: the group stays empty after the stop.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Host_Should_LeaveNoConsumerRunning_When_ItStopsWhileAnEndpointIsBetweenRestarts()
    {
        const string purpose = "commit-race-stray";
        var host = await StartHostAsync(purpose);
        var stopped = false;
        try
        {
            var key = "S-" + Guid.NewGuid().ToString("N")[..8];
            var since = await RaceAsync(host, purpose, key);
            var bus = host.Services.GetRequiredService<IBusControl>();
            // The commit fails with the session (about 45 s after the cut), and the endpoint's consumer ends with it.
            await WaitForAsync(() => Task.FromResult(bus.CheckHealth().Status != BusHealthStatus.Healthy), "the commit failed and the endpoint's consumer ended");
            Done($"the endpoint between restarts {since.Elapsed.TotalSeconds:F1} s after the cut");
            await faults.RestoreAsync(Proxy);

            stopped = true;
            await StopAsync(host, assert: false);

            // Nothing of the stopped host may join the group again. A stray consumer rejoins within a few seconds (one second
            // in the baseline); the group is watched for 20 seconds after the stop.
            using var admin = new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
            var describe = new Confluent.Kafka.Admin.DescribeConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(10) };
            var watch = Stopwatch.StartNew();
            var members = new List<int>();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                var group = (await admin.DescribeConsumerGroupsAsync([GroupOf(purpose)], describe)).ConsumerGroupDescriptions.Single();
                members.Add(group.Members.Count);
                await Task.Delay(500, Ct);
            }

            Done($"the group watched for 20 s after the stop: at most {members.Max()} members");
            members.Should().OnlyContain(count => count == 0, "no consumer of the stopped host is left running");
        }
        finally
        {
            await faults.RestoreAsync(Proxy);
            if (!stopped)
                await StopAsync(host, assert: false);
        }
    }

    /// <summary>
    /// Stops the host as the generic host does at a SIGTERM (its shutdown timeout applies inside <c>StopAsync</c>), then
    /// disposes it, each timed; with <paramref name="assert"/> the bounds are asserted.
    /// </summary>
    private async Task StopAsync(IHost host, bool assert)
    {
        var clock = Stopwatch.StartNew();
        Exception stopError = null;
        var stop = Task.Run(async () =>
        {
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            catch (Exception e) when (e is OperationCanceledException or AggregateException)
            {
                stopError = e;
            }
        });
        var stopEnded = await Task.WhenAny(stop, Task.Delay(ShutdownTimeout + TimeSpan.FromSeconds(30))) == stop;
        var stopTook = clock.Elapsed;
        Done($"the host's stop {(stopEnded ? "ended" : "still running")} after {stopTook.TotalSeconds:F1} s{(stopError is null ? string.Empty : " with " + stopError.GetType().Name)}");

        clock.Restart();
        var dispose = Task.Run(host.Dispose);
        var disposeEnded = await Task.WhenAny(dispose, Task.Delay(DisposeBound + TimeSpan.FromSeconds(50))) == dispose;
        var disposeTook = clock.Elapsed;
        Done($"the host's dispose {(disposeEnded ? "ended" : "still running")} after {disposeTook.TotalSeconds:F1} s");
        if (!assert)
            return;

        stopEnded.Should().BeTrue();
        stopTook.Should().BeLessThan(ShutdownTimeout, "the host stops within its shutdown timeout while the commit is in flight");
        stopError.Should().BeNull("the stop completes without failing");
        disposeEnded.Should().BeTrue();
        disposeTook.Should().BeLessThan(DisposeBound, "and then disposes without waiting for the commit");
    }

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

    /// <summary>The key's rows as last committed, read past the proxy.</summary>
    private async Task<List<(int Attempts, bool Sent, string LastError)>> RowsAsync(string key)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT attempts, sent_on IS NOT NULL, last_error FROM outbox_message WHERE message_key = @key ORDER BY seq", sql);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(int, bool, string)>();
        while (await reader.ReadAsync(Ct))
            rows.Add((reader.GetInt32(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    private static async Task<bool> CommittedAllAsync(Confluent.Kafka.IAdminClient admin, string group)
    {
        var partition = new Confluent.Kafka.TopicPartition(KafkaTopics.SlaBreachDetected, new Confluent.Kafka.Partition(0));
        var latest = await Confluent.Kafka.IAdminClientExtensions.ListOffsetsAsync(admin,
            [new Confluent.Kafka.Admin.TopicPartitionOffsetSpec { TopicPartition = partition, OffsetSpec = Confluent.Kafka.Admin.OffsetSpec.Latest() }],
            new Confluent.Kafka.Admin.ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var committed = await admin.ListConsumerGroupOffsetsAsync([new Confluent.Kafka.ConsumerGroupTopicPartitions(group, [partition])],
            new Confluent.Kafka.Admin.ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        var end = latest.ResultInfos.Single().TopicPartitionOffsetError.Offset.Value;
        return end > 0 && committed.Single().Partitions.Single().Offset.Value == end;
    }

    private void Done(string step)
    {
        var entry = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{step} at {_clock.Elapsed.TotalSeconds:F1} s");
        _steps.Add(entry);
        TestContext.Current.TestOutputHelper?.WriteLine(entry);
    }

    private async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        while (!await condition())
        {
            if (_clock.Elapsed > Budget)
                throw new TimeoutException($"{what}: not within the test's {Budget.TotalSeconds:F0} s (done: {string.Join(", ", _steps)})");
            await Task.Delay(250, Ct);
        }

        Done(what);
    }
}
