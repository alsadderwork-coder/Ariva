using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Di.Extensions;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Ariva.IntegrationTests.Security;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ariva.IntegrationTests.Faults;

/// <summary>
/// ARV-166, reproduced deterministically: the connection to the broker stalls (nothing passes, nothing is closed) while
/// the outbox relay produces a row and a direct publish follows it, so both are in flight; 2 seconds later the broker is
/// cut off (the connection drops); 12 seconds later a second direct publish is queued behind them on the same producer.
/// Before ARV-166 that second publish never completed, not even once the broker was back: the first failed send made
/// MassTransit's rider dispose the topic's producer, and librdkafka discards what is still outstanding on a disposed
/// producer without a delivery report (the cause, in backlog/progress.md). The 12 seconds make it certain: librdkafka
/// times out a sent message when the broker's thread wakes, at each reconnect attempt (at most 10 seconds apart), so a
/// message queued closer behind the first can be timed out in the same wake-up and reported before the dispose (the
/// ARV-156 diagnosis queued it 0.5 seconds after the cut, which strands it only when a wake-up falls between the two).
/// The test proves each bound and the recovery:
/// every awaited direct publish (Ingest's, which is <see cref="ISvcMessageBus"/>) ends within
/// <see cref="KafkaProduce.SendTimeout"/>, failed and not as a cancellation (Ingest answers that 503 with Retry-After); the
/// relay's pass ends within its own bound (<c>Kafka:Outbox:PassSeconds</c>, 20 seconds here so that it ends before the
/// produce bound would) and lets go of its transaction, its leader lock and its row; after the restore a new produce
/// succeeds and the relay sends the row, which the consumer gets once.
/// The second test proves that a caller who gives up no longer strands the other sends on its topic's producer.
/// <para>
/// The cause is bounded here, not removed: the rider still disposes a topic's producer when a send on it fails, so B is
/// still discarded with it and fails at its bound (the device or the relay sends it again). ARV-167 removes the cause.
/// </para>
/// <para>
/// Every wait is for a condition, within one budget inside the fact's timeout (ARV-156), and the host's stop has a
/// bound of its own, after the broker is restored.
/// </para>
/// </summary>
[Collection(FaultsCollection.Name)]
public sealed class StuckProduceTests(FaultsFixture faults) : IAsyncLifetime
{
    private const string Proxy = "kafka";
    private const string Purpose = "stuck-produce";

    /// <summary>The relay's pass bound in these tests, below the produce bound so that the pass bound is the one measured.</summary>
    private const int PassSeconds = 20;

    /// <summary>Scheduling slack on a loaded machine, on top of each bound.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EventCatalog _catalog = new([typeof(OutageProbe).Assembly]);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _steps = [];
    private readonly ConcurrentQueue<string> _log = new();

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
            ["Kafka:ServiceName"] = "stuck",
            ["Kafka:ProvisionTopics"] = "true",
            ["Kafka:Topics:Partitions"] = "1",
            ["Kafka:Topics:ReplicationFactor"] = "1",
            ["Kafka:Topics:MinInSyncReplicas"] = "1",
            ["Kafka:Outbox:PollMilliseconds"] = "200",
            ["Kafka:Outbox:PassSeconds"] = PassSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Kafka:Consumers:CheckpointSeconds"] = "1",
            ["Kafka:Consumers:CheckpointMessageCount"] = "1"
        });
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } log)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(log));
        builder.Logging.AddProvider(new WarningRecorder(_log));
        builder.Services.AddScoped<ICurrentUser, TestCurrentUser>();
        builder.Services.AddArivaPersistence(builder.Configuration);
        builder.Services.AddArivaMessaging(builder.Configuration, messaging => messaging
            .AddEventsFrom(typeof(OutageProbe).Assembly)
            .Consume<OutageProbe, OutageProbeConsumer>(KafkaTopics.SlaBreachDetected, Purpose));
        var host = builder.Build();
        await host.StartAsync(Ct); // held until the consumed topics exist (KafkaTopicsReady)
        Done("the host started");
        return host;
    }

    private async Task WriteAsync(IHost host, string key, int step)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        unitOfWork.StorageProvider.BeginTransaction(IsolationLevel.ReadCommitted);
        await new NHibernateDomainEventOutbox(_catalog, TimeProvider.System).WriteAsync(unitOfWork.StorageProvider,
            [new OutageProbe { Key = key, Step = step, CorrelationId = "corr-" + key }], Ct);
        unitOfWork.PromiseToCommit();
        await unitOfWork.EndAsync(Ct);
    }

    private static Task PublishAsync(IHost host, string key, int step) =>
        host.Services.GetRequiredService<ISvcMessageBus>().PublishAsync(new OutageProbe { Key = key, Step = step, CorrelationId = "corr-" + key }, CancellationToken.None);

    /// <summary>The row's state, read past the proxy: whether a transaction holds it, its attempts and whether it was sent.</summary>
    private async Task<(bool Locked, int Attempts, bool Sent)> RowAsync(string key)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var transaction = await sql.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT attempts, sent_on IS NOT NULL FROM outbox_message WHERE message_key = @key FOR UPDATE NOWAIT", sql, transaction);
        command.Parameters.AddWithValue("key", key);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(Ct);
            if (!await reader.ReadAsync(Ct))
                throw new InvalidOperationException($"No outbox row for {key}.");
            return (false, reader.GetInt32(0), reader.GetBoolean(1));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return (true, -1, false);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The row as last committed, read past the proxy without a lock (a pass that holds the row does not hide it): its
    /// attempts, whether it was sent and its last error.
    /// </summary>
    private async Task<(int Attempts, bool Sent, string LastError)> CommittedRowAsync(string key)
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT attempts, sent_on IS NOT NULL, coalesce(last_error, '') FROM outbox_message WHERE message_key = @key", sql);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
            throw new InvalidOperationException($"No outbox row for {key}.");
        return (reader.GetInt32(0), reader.GetBoolean(1), reader.GetString(2));
    }

    /// <summary>Whether the relay's leader lock is free: another session can take it.</summary>
    private async Task<bool> LeaderLockFreeAsync()
    {
        await using var sql = new NpgsqlConnection(faults.DirectConnectionString);
        await sql.OpenAsync(Ct);
        await using var transaction = await sql.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", sql, transaction);
        command.Parameters.AddWithValue("key", OutboxRelay.LeaderLock);
        var free = (bool)(await command.ExecuteScalarAsync(Ct))!;
        await transaction.RollbackAsync(Ct);
        return free;
    }

    private static int[] StepsOf(string key) => [.. OutageProbeConsumer.Received.Where(r => r.Key == key).Select(r => r.Step)];

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

    /// <summary>
    /// Waits for a produce that the test started, never past <paramref name="bound"/> plus the slack from its start, and
    /// returns how it ended: the elapsed time and the exception (null when it succeeded).
    /// </summary>
    private async Task<(TimeSpan Elapsed, Exception Error)> EndedAsync(Task produce, Stopwatch since, TimeSpan bound, string what)
    {
        var left = bound + Slack - since.Elapsed;
        if (left > TimeSpan.Zero)
            await Task.WhenAny(produce, Task.Delay(left, Ct));
        produce.IsCompleted.Should().BeTrue($"{what} ends within its bound of {bound.TotalSeconds:F0} s (ARV-166); it was still waiting after {since.Elapsed.TotalSeconds:F1} s");
        Done($"{what} ended after {since.Elapsed.TotalSeconds:F1} s ({(produce.IsFaulted ? produce.Exception!.GetBaseException().GetType().Name : produce.Status.ToString())})");
        return (since.Elapsed, produce.IsFaulted ? produce.Exception!.GetBaseException() : null);
    }

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

    [Fact(Timeout = 300_000)]
    public async Task Produce_Should_EndWithinItsBoundAndRecover_When_QueuedBehindAMessageInFlightOnADroppedConnection()
    {
        var host = await StartHostAsync();
        var completed = false;
        try
        {
            try
            {
                var key = "S-" + Guid.NewGuid().ToString("N")[..8];
                var direct = key + "-direct";
                await PublishAsync(host, key + "-warm", 0).WaitAsync(TimeSpan.FromSeconds(60), Ct);
                Done("the warm-up publish delivered");
                // The relay sends only while the bus is Healthy, which it is once the consumer has joined its group.
                var bus = host.Services.GetRequiredService<IBusControl>();
                await WaitForAsync(() => Task.FromResult(bus.CheckHealth().Status == BusHealthStatus.Healthy), "the bus healthy");

                // The connection stalls: what is sent now stays in flight, unacknowledged.
                await faults.StallAsync(Proxy);
                Done("the broker connection stalled");
                await WriteAsync(host, key, 1);
                var relayed = Stopwatch.StartNew();
                await WaitForAsync(async () => (await RowAsync(key)).Locked, "the relay producing the row (its pass holds it)");
                // When the relay's pass lets go of the row with the row failed: watched from now on, so the time is the pass's own.
                // The pass's failure becomes visible when it commits, which also releases its row and its leader lock. A later
                // pass may claim the row again at once (the bus can stay Healthy through the cut), so the committed state is
                // read without a lock.
                var passEnded = Task.Run(async () =>
                {
                    while (true)
                    {
                        if (await CommittedRowAsync(key) is { Attempts: >= 1 } failed)
                            return (Took: relayed.Elapsed, failed.LastError);
                        await Task.Delay(100, Ct);
                    }
                }, Ct);
                var a = Stopwatch.StartNew();
                var publishA = PublishAsync(host, direct, 1);
                await Task.Delay(TimeSpan.FromSeconds(2), Ct);
                await faults.CutAsync(Proxy);
                Done("the broker cut off");
                // More than librdkafka's 10 second reconnect backoff behind the messages in flight, so that it is still waiting
                // when the first of them fails (see the summary).
                await Task.Delay(TimeSpan.FromSeconds(12), Ct);
                var b = Stopwatch.StartNew();
                var publishB = PublishAsync(host, direct, 2);
                Done("a second publish queued behind them");

                // Ingest's direct publish: both end within the produce bound, failed, and not as a cancellation, which Ingest
                // answers 503 with Retry-After (PushEndpointTests). Before ARV-166, B never ended.
                foreach (var (produce, since, what) in new[] { (publishA, a, "the publish in flight"), (publishB, b, "the publish queued behind it") })
                {
                    var (_, error) = await EndedAsync(produce, since, KafkaProduce.SendTimeout, what);
                    error.Should().NotBeNull($"{what} cannot be acknowledged without the broker");
                    error.Should().NotBeAssignableTo<OperationCanceledException>($"{what} fails as a broker failure, which Ingest answers 503");
                }

                // The relay's pass ended within its own bound, with the row marked failed and nothing held: the row, and the lock.
                // Its produce was still waiting: the pass stopped waiting for it (the produce runs on, without the pass's token).
                var (passTook, passError) = await passEnded.WaitAsync(TimeSpan.FromSeconds(PassSeconds) + Slack, Ct);
                Done($"the relay's pass ended {passTook.TotalSeconds:F1} s after the row was written");
                passTook.Should().BeLessThan(TimeSpan.FromSeconds(PassSeconds) + Slack, "the relay's pass ends within Kafka:Outbox:PassSeconds");
                passError.Should().Contain("relay pass", "the pass bound ended the wait, and the row failed like a broker failure");
                (await CommittedRowAsync(key)).Sent.Should().BeFalse("nothing was acknowledged without the broker");
                // Between two passes the lock is free for the relay's poll interval (200 ms here), so it is polled faster.
                while (!await LeaderLockFreeAsync())
                {
                    if (_clock.Elapsed > Budget)
                        throw new TimeoutException($"the relay's leader lock free: not within the test's {Budget.TotalSeconds:F0} s (done: {string.Join(", ", _steps)})");
                    await Task.Delay(25, Ct);
                }

                Done("the relay's leader lock free");

                await faults.RestoreAsync(Proxy);
                Done("the broker back");

                // Recovery without a restart: a new produce succeeds, and the relay sends the row (after its backoff, once the
                // bus is ready again), which the consumer gets once, however often it reached the broker.
                await WaitForAsync(async () =>
                {
                    var c = PublishAsync(host, key + "-after", 1);
                    return await Task.WhenAny(c, Task.Delay(KafkaProduce.SendTimeout + Slack, Ct)) == c && c.IsCompletedSuccessfully;
                }, "a new publish delivered after the restore");
                await WaitForAsync(async () => (await CommittedRowAsync(key)).Sent && StepsOf(key).Length >= 1, "the relay sent the row after the restore");
                await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                StepsOf(key).Should().Equal([1], "the row is consumed once (the inbox drops a copy that also reached the broker)");
                completed = true;
            }
            finally
            {
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

    /// <summary>
    /// ARV-166: a caller who gives up no longer strands the other sends on its topic's producer. Before, the caller's token
    /// reached the rider, the cancelled send faulted the topic's shared producer and MassTransit disposed it; once the
    /// producer has reported a local error (the cut here) the dispose skips its flush, and librdkafka drops what is still
    /// outstanding without a report. Now the caller stops waiting at once, its produce runs on, and nothing is disposed:
    /// the send that was outstanding is delivered once the broker is back, well inside its 30 second message timeout,
    /// and so is the message whose caller gave up (at-least-once: a device that dropped its request sends it again, and
    /// consumers drop the copy). On the unfixed code the outstanding send never ended; with only the produce bound it
    /// would fail at 45 seconds instead of being delivered.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task Produce_Should_NotStrandTheOtherSends_When_ACallerGivesUp()
    {
        var host = await StartHostAsync();
        var completed = false;
        try
        {
            try
            {
                var key = "C-" + Guid.NewGuid().ToString("N")[..8];
                await PublishAsync(host, key + "-warm", 0).WaitAsync(TimeSpan.FromSeconds(60), Ct);
                Done("the warm-up publish delivered");
                var bus = host.Services.GetRequiredService<IBusControl>();
                await WaitForAsync(() => Task.FromResult(bus.CheckHealth().Status == BusHealthStatus.Healthy), "the bus healthy");

                // A send outstanding on the topic's producer: the connection stalls, so it stays in flight.
                await faults.StallAsync(Proxy);
                Done("the broker connection stalled");
                var since = Stopwatch.StartNew();
                var outstanding = PublishAsync(host, key, 1);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);

                // The cut makes the producer report a local error, after which MassTransit's dispose of it skips its flush.
                var downBefore = Logged(BrokersDown);
                await faults.CutAsync(Proxy);
                Done("the broker cut off");
                await WaitForAsync(() => Task.FromResult(Logged(BrokersDown) > downBefore), "the producer reporting its broker down");

                // Another caller gives up on its send to the same topic (a device dropping its request).
                using var caller = new CancellationTokenSource();
                var givenUp = host.Services.GetRequiredService<ISvcMessageBus>()
                    .PublishAsync(new OutageProbe { Key = key, Step = 2, CorrelationId = "corr-" + key }, caller.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
                await caller.CancelAsync();
                (await Task.WhenAny(givenUp, Task.Delay(TimeSpan.FromSeconds(5), Ct)) == givenUp).Should().BeTrue("the caller stops waiting at once");
                givenUp.IsCanceled.Should().BeTrue("the caller's wait ends as its own cancellation");
                Done("the caller gave up");

                // Time for a dispose to take the producer down, were the cancellation to reach the rider; then the broker is back.
                await Task.Delay(TimeSpan.FromSeconds(2), Ct);
                await faults.RestoreAsync(Proxy);
                Done("the broker back");

                var (_, error) = await EndedAsync(outstanding, since, KafkaProduce.SendTimeout, "the send outstanding when the caller gave up");
                error.Should().BeNull("its producer was not disposed under it, so it is delivered once the broker is back");
                await WaitForAsync(() => Task.FromResult(StepsOf(key).Length >= 2), "both messages consumed, the one whose caller gave up included");
                await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                StepsOf(key).Should().Equal([1, 2], "each once, in the order they were produced on the one producer");
                completed = true;
            }
            finally
            {
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

    /// <summary>librdkafka's error when the producer's connections are all down, as MassTransit logs it for a producer.</summary>
    private const string BrokersDown = "brokers are down on producer";

    private int Logged(string text) => _log.Count(l => l.Contains(text, StringComparison.Ordinal));

    /// <summary>Records MassTransit's warnings and errors (no message content: MassTransit logs ids, types and client errors).</summary>
    private sealed class WarningRecorder(ConcurrentQueue<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Recorder(log, categoryName);

        public void Dispose()
        {
        }

        private sealed class Recorder(ConcurrentQueue<string> log, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category.StartsWith("MassTransit", StringComparison.Ordinal) && logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (IsEnabled(logLevel))
                    log.Enqueue(formatter(state, exception));
            }
        }
    }
}
