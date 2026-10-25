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
/// partition. The first two hold whether or not the consumer ends during the outage; the third needs it to end, which
/// librdkafka does not always bring about (see that test).
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

    /// <summary>
    /// The stray test tries the race at most this many times, each with its own host and consumer group, and starts an
    /// attempt only while <see cref="StrayBudget"/> still holds <see cref="AttemptBound"/>. An attempt whose consumer is not
    /// told takes about 15 s; a told one about 98 s when its endpoint stays Ready, and up to about 115 s when it reaches the
    /// premise.
    /// </summary>
    private const int StrayAttempts = 8;

    /// <summary>When librdkafka tells the consumer that every broker is down, it does so within milliseconds of the cut.</summary>
    private static readonly TimeSpan ToldBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A told consumer's endpoint is between restarts once its deferred commit fails, librdkafka's session timeout after the
    /// cut (<c>session.timeout.ms</c>, librdkafka's default of 45 s: Ariva does not set it). Locally that is 45.2 to 45.3 s
    /// every time; in PR #67's CI on 33f86f2 four told attempts in a row were still Ready 65 s after the cut (the bound then),
    /// so a slower runner gets 45 s of slack.
    /// </summary>
    private static readonly TimeSpan PremiseBound = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The group is watched this long after the stop when the endpoint was between restarts: a stray consumer rejoins within
    /// a few seconds (one second in the baseline).
    /// </summary>
    private static readonly TimeSpan StrayWatch = TimeSpan.FromSeconds(20);

    /// <summary>The group is watched this long after an ordinary stop.</summary>
    private static readonly TimeSpan OrdinaryWatch = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The most one attempt of the stray test may need, which must be left of <see cref="StrayBudget"/> before it starts:
    /// <see cref="PremiseBound"/>, the <see cref="StrayWatch"/> after a stop between restarts, and 25 s for the host's start,
    /// the race, the stop and the dispose (about 5 s together in CI).
    /// </summary>
    private static readonly TimeSpan AttemptBound = PremiseBound + StrayWatch + TimeSpan.FromSeconds(25);

    /// <summary>The health and stop tests' budget, within their 300 s timeout.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);

    /// <summary>
    /// The stray test's budget: four told attempts whose endpoints stay Ready (about 98 s each with the 90 s
    /// <see cref="PremiseBound"/>) and a fifth that reaches the premise (up to about 115 s) fit, since told consumers stayed
    /// Ready past 65 s four times in a row in CI. Its [Fact] timeout is 40 s more, so the test ends on its own assertion with
    /// the steps, never on the timeout.
    /// </summary>
    private static readonly TimeSpan StrayBudget = TimeSpan.FromSeconds(560);

    /// <summary>
    /// librdkafka's debug contexts for the stray test's hosts: the consumer group (coordinator, offset commits, rebalances)
    /// and the consumer. Set through a <c>ClientConfig</c> in the host's container: since ARV-177 the rider is given Ariva's own
    /// base (<c>KafkaClientConfig.Rider</c>), so MassTransit no longer takes the container's, and Ariva.Di copies its
    /// <c>Debug</c> onto the rider (MessagingExtensions), so no Ariva setting changes and only the rider's consumers get it
    /// (Ariva's producer has its own configuration). The rider logs librdkafka's lines as <c>MassTransit.ReceiveTransport</c>
    /// at Debug.
    /// </summary>
    private const string LibrdkafkaDebug = "cgrp,consumer";

    /// <summary>The most host log lines one told attempt whose endpoint stayed Ready writes to the test output.</summary>
    private const int HostLogLines = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EventCatalog _catalog = new([typeof(OutageProbe).Assembly]);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _steps = [];

    /// <summary>The running test's budget for <see cref="WaitForAsync"/>: <see cref="Budget"/>, or <see cref="StrayBudget"/>.</summary>
    private TimeSpan _budget = Budget;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        CommitRaceConsumer.Trigger = null;
        CommitRaceConsumer.Hook = null;
        await faults.RestoreAsync(Proxy);
    }

    private static string GroupOf(string purpose) => "ariva-race." + purpose;

    /// <summary>
    /// Starts a host consuming the race's topic with its own group. With <paramref name="log"/> (the stray test), that
    /// capture gets MassTransit's Debug lines, and the rider's consumers log librdkafka's <see cref="LibrdkafkaDebug"/>
    /// contexts.
    /// </summary>
    private async Task<IHost> StartHostAsync(string purpose, StrayHostLog log = null)
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
        if (Environment.GetEnvironmentVariable("ARIVA_IT_LOG") is { Length: > 0 } file)
            builder.Logging.AddProvider(new Ariva.IntegrationTests.Streaming.FileLoggerProvider(file));
        if (log is not null)
        {
            builder.Logging.AddProvider(log);
            // Debug for this capture only: the host's other providers keep their levels.
            builder.Logging.AddFilter<StrayHostLog>("MassTransit", LogLevel.Debug);
            builder.Services.AddSingleton(new Confluent.Kafka.ClientConfig { Debug = LibrdkafkaDebug });
        }

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
    /// <para>
    /// The consumer ends only when librdkafka tells it that every broker is down (<c>Local_AllBrokersDown</c>, which
    /// MassTransit's error handler logs as it cancels the consume loop; a failed commit alone ends nothing). librdkafka 2.15
    /// re-bootstraps during an outage and its count of brokers down races that sequence, so nothing guarantees the error:
    /// twice in PR #67's CI on 2026-10-16 the endpoint stayed Ready for the whole 270 s (locally, with librdkafka's debug
    /// log, every told consumer had the error within milliseconds of the cut). A consumer that is not told polls on, with
    /// nothing between restarts. So the race is tried up to <see cref="StrayAttempts"/> times, each with its own host and
    /// group: an attempt whose consumer is not told within <see cref="ToldBound"/> of the cut ends there (the broker back,
    /// the host stopped, its group empty), and the first told one goes on to the stop with the endpoint between restarts.
    /// </para>
    /// <para>
    /// A told consumer's endpoint is not always between restarts soon either: in PR #67's CI on 33f86f2 (2026-10-20) four
    /// told attempts in a row were still Ready 65 s after the cut, about 72 s each, and the test reached its 300 s timeout
    /// before its own assertion. So a told attempt waits <see cref="PremiseBound"/> (90 s; the session timeout is
    /// librdkafka's), an attempt starts only while the test's budget (<see cref="StrayBudget"/>) still holds the most it may
    /// need (<see cref="AttemptBound"/>), and when the attempts or the budget run out the test fails on its assertion with
    /// the steps. A told attempt whose endpoint stayed Ready writes its host's log about the consumer, the commit and the
    /// endpoint to the test output (<see cref="WriteHostLog"/>), librdkafka's consumer group lines included.
    /// </para>
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task Host_Should_LeaveNoConsumerRunning_When_ItStopsWhileAnEndpointIsBetweenRestarts()
    {
        _budget = StrayBudget;
        var tried = 0;
        var between = false;
        while (!between && tried < StrayAttempts)
        {
            var left = _budget - _clock.Elapsed;
            if (left < AttemptBound)
            {
                Done($"no attempt {tried + 1}: {left.TotalSeconds:F0} s of the budget left, less than an attempt may need ({AttemptBound.TotalSeconds:F0} s)");
                break;
            }

            between = await StrayAttemptAsync(++tried);
        }

        between.Should().BeTrue($"an attempt's endpoint is between restarts in one of {tried} attempts (done: {string.Join(", ", _steps)})");
    }

    /// <summary>
    /// One attempt of the stray test, with its own host and consumer group: the race; whether the consumer is told within
    /// <see cref="ToldBound"/> of the cut and its endpoint then between restarts within <see cref="PremiseBound"/>; the
    /// broker back, the host stopped, and its group watched (<see cref="StrayWatch"/> when the endpoint was between restarts,
    /// <see cref="OrdinaryWatch"/> otherwise). Returns whether the endpoint was between restarts when the host stopped.
    /// </summary>
    private async Task<bool> StrayAttemptAsync(int attempt)
    {
        var purpose = attempt == 1 ? "commit-race-stray" : $"commit-race-stray-{attempt}";
        var log = new StrayHostLog(_clock);
        var host = await StartHostAsync(purpose, log);
        var stopped = false;
        var between = false;
        var toldButReady = false;
        var cutAt = TimeSpan.Zero;
        try
        {
            var key = "S" + attempt + "-" + Guid.NewGuid().ToString("N")[..8];
            var since = await RaceAsync(host, purpose, key);
            cutAt = _clock.Elapsed - since.Elapsed;
            var bus = host.Services.GetRequiredService<IBusControl>();
            if (!await WithinAsync(() => log.Told > 0, since, ToldBound))
                Done($"attempt {attempt}: its consumer not told that every broker is down within {ToldBound.TotalSeconds:F0} s of the cut");
            else
            {
                // The commit fails with the session (about 45 s after the cut), and the endpoint's consumer ends with it.
                between = await WithinAsync(() => bus.CheckHealth().Status != BusHealthStatus.Healthy, since, PremiseBound);
                toldButReady = !between;
                Done(between
                    ? $"attempt {attempt}: its consumer told, the endpoint between restarts {since.Elapsed.TotalSeconds:F1} s after the cut"
                    : $"attempt {attempt}: its consumer told, the endpoint still Ready {since.Elapsed.TotalSeconds:F1} s after the cut");
            }

            log.Note("the test restores the broker");
            await faults.RestoreAsync(Proxy);
            stopped = true;
            log.Note("the test stops the host");
            await StopAsync(host, assert: false);
            log.Note("the host stopped and disposed");
        }
        finally
        {
            await faults.RestoreAsync(Proxy);
            if (!stopped)
                await StopAsync(host, assert: false);
        }

        if (toldButReady)
            WriteHostLog(attempt, log, cutAt);

        // Nothing of the stopped host may join the group again.
        var watchFor = between ? StrayWatch : OrdinaryWatch;
        using var admin = new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = faults.KafkaDirectBootstrap }).Build();
        var describe = new Confluent.Kafka.Admin.DescribeConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(10) };
        var watch = Stopwatch.StartNew();
        var members = new List<int>();
        while (watch.Elapsed < watchFor)
        {
            var group = (await admin.DescribeConsumerGroupsAsync([GroupOf(purpose)], describe)).ConsumerGroupDescriptions.Single();
            members.Add(group.Members.Count);
            await Task.Delay(500, Ct);
        }

        Done($"attempt {attempt}: the group watched for {watchFor.TotalSeconds:F0} s after the stop, at most {members.Max()} members");
        members.Should().OnlyContain(count => count == 0, "no consumer of the stopped host is left running");
        return between;
    }

    /// <summary>Whether <paramref name="condition"/> holds within <paramref name="bound"/> of <paramref name="since"/>.</summary>
    private static async Task<bool> WithinAsync(Func<bool> condition, Stopwatch since, TimeSpan bound)
    {
        while (!condition())
        {
            if (since.Elapsed > bound)
                return false;
            await Task.Delay(250, Ct);
        }

        return true;
    }

    /// <summary>
    /// Writes to the test output what the attempt's host logged about the consumer, the commit and the endpoint from the cut
    /// to the end of its dispose, for a told attempt whose endpoint stayed Ready: lines alike merged, each with how often it
    /// came and its first and last time from the cut, in the order they first came; at most <see cref="HostLogLines"/>
    /// (<see cref="StrayHostLog.AboutTheConsumerSince"/> chooses).
    /// </summary>
    private static void WriteHostLog(int attempt, StrayHostLog log, TimeSpan cutAt)
    {
        var output = TestContext.Current.TestOutputHelper;
        if (output is null)
            return;
        var (lines, about) = log.AboutTheConsumerSince(cutAt, HostLogLines);
        output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"attempt {attempt}: its host's log about the consumer, the commit and the endpoint from the cut on, times from the cut ({lines.Count} of {about} such lines; lines alike merged, the latest text shown; {log.Seen} lines logged in all, {log.Dropped} not kept):"));
        foreach (var line in lines)
            output.WriteLine($"  {FromCut(line.First)} s x{line.Count} (last {FromCut(line.Last)} s) {line.Level} {line.Category}: {line.Text}");

        // Signed tenths of a second from the cut, counted as a whole number so that a time just before the cut reads -0.0.
        string FromCut(TimeSpan at)
        {
            var tenths = (long)Math.Round((at - cutAt).TotalSeconds * 10, MidpointRounding.AwayFromZero);
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(at < cutAt ? '-' : '+')}{Math.Abs(tenths) / 10}.{Math.Abs(tenths) % 10}");
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
        await unitOfWork.StorageProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted);
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
            if (_clock.Elapsed > _budget)
                throw new TimeoutException($"{what}: not within the test's {_budget.TotalSeconds:F0} s (done: {string.Join(", ", _steps)})");
            await Task.Delay(250, Ct);
        }

        Done(what);
    }

    /// <summary>
    /// One stray attempt's capture of its host's log (each attempt has its own host and its own capture). It counts
    /// MassTransit's log of a Kafka consumer's local or fatal error, which its error handler writes as it cancels the consume
    /// loop (MassTransit.Kafka 8.5.11, <c>KafkaMessageConsumer.HandleKafkaError</c>: "Consumer [member] error (code): reason
    /// on topic. IsFatal(...)", at Warning, or Error when fatal). It also keeps the MassTransit lines (librdkafka's among
    /// them, from Debug) and the Ariva messaging lines (from Information) for <see cref="WriteHostLog"/>. Lines alike (the
    /// same but for their numbers) are kept once with a count and their first and last time, since librdkafka repeats some
    /// many times a second while a commit waits; at most <see cref="MaxKept"/> lines of at most <see cref="MaxText"/>
    /// characters are kept (CWE-120), and a line that mentions a password is never kept (none does: librdkafka's
    /// <c>conf</c> context, which lists settings, is not on).
    /// </summary>
    private sealed class StrayHostLog(Stopwatch clock) : ILoggerProvider
    {
        private const int MaxKept = 2_000;
        private const int MaxText = 300;

        /// <summary>librdkafka's lines that are about the group, the commit or the consumer (its group and consumer contexts).</summary>
        private static readonly string[] Words =
            ["commit", "coordinator", "session", "assign", "revok", "lost", "rebalanc", "terminat", "heartbeat", "join", "leave", "close", "offset", "error", "fail"];

        private readonly Lock _gate = new();
        private readonly Dictionary<string, Entry> _alike = new(StringComparer.Ordinal);
        private readonly List<Entry> _kept = [];
        private int _told;
        private long _seen;
        private long _dropped;

        /// <summary>How many times the consumer logged a local or fatal error (it was told that every broker is down).</summary>
        public int Told => Volatile.Read(ref _told);

        /// <summary>Lines logged to this capture in all.</summary>
        public long Seen => Interlocked.Read(ref _seen);

        /// <summary>Lines not kept because <see cref="MaxKept"/> different lines were kept already.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("MassTransit", StringComparison.Ordinal) || categoryName.StartsWith("Ariva.Infra.Messaging", StringComparison.Ordinal)
                ? new CategoryLog(this, categoryName)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        /// <summary>A step of the test, kept among the host's lines so that their order shows.</summary>
        public void Note(string step) => Keep(LogLevel.Information, "test", step);

        /// <summary>
        /// The kept lines about the consumer, the commit or the endpoint that came at or after <paramref name="cutAt"/> (on
        /// the test's clock), in the order they first came, and how many there were. When there are more than
        /// <paramref name="most"/>: every line of MassTransit, Ariva and the test, then librdkafka's, those about the commit
        /// first, then its failures, timeouts, revokes and lost partitions, then the five it repeated most (what it waited on),
        /// then its rarest (its state changes).
        /// </summary>
        public (List<Line> Lines, int About) AboutTheConsumerSince(TimeSpan cutAt, int most)
        {
            List<Line> about;
            lock (_gate)
                about = [.. _kept.Select(entry => entry.Line).Where(line => line.Last >= cutAt && AboutTheConsumer(line))];
            if (about.Count <= most)
                return (about, about.Count);

            var library = about.Where(FromLibrdkafka).ToList();
            var ranked = about.Where(line => !FromLibrdkafka(line))
                .Concat(library.Where(line => Mentions(line, "commit")))
                .Concat(library.Where(line => Mentions(line, "fail", "error", "timed out", "timeout", "session", "revok", "lost")))
                .Concat(library.OrderByDescending(line => line.Count).Take(5))
                .Concat(library.OrderBy(line => line.Count).ThenBy(line => line.First));
            var chosen = new HashSet<Line>();
            foreach (var line in ranked)
            {
                if (chosen.Count >= most)
                    break;
                chosen.Add(line);
            }

            return ([.. about.Where(chosen.Contains)], about.Count);
        }

        public void Dispose()
        {
        }

        private static bool FromLibrdkafka(Line line) => line.Category == "MassTransit.ReceiveTransport";

        private static bool Mentions(Line line, params string[] words) => words.Any(word => line.Text.Contains(word, StringComparison.OrdinalIgnoreCase));

        private static bool AboutTheConsumer(Line line) =>
            line.Category == "test" ||
            (FromLibrdkafka(line)
                ? Mentions(line, Words)
                : line.Category.StartsWith("MassTransit", StringComparison.Ordinal)
                    // Not the in-memory bus's own receive endpoint (loopback://localhost/vm_...).
                    ? !line.Text.Contains("/vm_", StringComparison.Ordinal)
                    : line.Category.EndsWith(".KafkaConsumerEndpoints", StringComparison.Ordinal) ||
                      line.Category.EndsWith(".KafkaEndpointsLifetime", StringComparison.Ordinal) ||
                      // The producer and the relay: their warnings and errors only, which show when they saw the outage.
                      line.Level >= LogLevel.Warning);

        private void Keep(LogLevel level, string category, string text)
        {
            Interlocked.Increment(ref _seen);
            if (text.Contains("password", StringComparison.OrdinalIgnoreCase))
                return;
            if (text.Length > MaxText)
                text = text[..MaxText];
            var now = clock.Elapsed;
            var shape = Shape(level, category, text);
            lock (_gate)
            {
                if (_alike.TryGetValue(shape, out var entry))
                    entry.Line = entry.Line with { Text = text, Last = now, Count = entry.Line.Count + 1 };
                else if (_kept.Count < MaxKept)
                {
                    entry = new Entry { Line = new Line(level, category, text, now, now, 1) };
                    _alike.Add(shape, entry);
                    _kept.Add(entry);
                }
                else
                    Interlocked.Increment(ref _dropped);
            }
        }

        /// <summary>The line with each run of digits as one <c>#</c>, so that lines differing only in numbers are alike.</summary>
        private static string Shape(LogLevel level, string category, string text)
        {
            var shape = new System.Text.StringBuilder(category.Length + text.Length + 16).Append(level).Append(' ').Append(category).Append(' ');
            var digits = false;
            foreach (var c in text)
            {
                if (!char.IsAsciiDigit(c))
                    shape.Append(c);
                else if (!digits)
                    shape.Append('#');
                digits = char.IsAsciiDigit(c);
            }

            return shape.ToString();
        }

        /// <summary>A kept line: the latest text of lines alike, how often they came, and the first and last time on the test's clock.</summary>
        public sealed record Line(LogLevel Level, string Category, string Text, TimeSpan First, TimeSpan Last, int Count);

        private sealed class Entry
        {
            public Line Line { get; set; }
        }

        private sealed class CategoryLog(StrayHostLog owner, string category) : ILogger
        {
            private readonly bool _massTransit = category.StartsWith("MassTransit", StringComparison.Ordinal);

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= (_massTransit ? LogLevel.Debug : LogLevel.Information);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                var message = formatter(state, exception) ?? string.Empty;
                if (_massTransit && logLevel >= LogLevel.Warning && message.StartsWith("Consumer [", StringComparison.Ordinal) &&
                    (message.Contains("] error (Local_", StringComparison.Ordinal) || message.Contains("IsFatal(True)", StringComparison.Ordinal)))
                    Interlocked.Increment(ref owner._told);
                var text = FirstLine(message);
                if (exception is not null)
                    text += " (" + exception.GetType().Name + ": " + FirstLine(exception.Message) + ")";
                owner.Keep(logLevel, category, text);
            }

            private static string FirstLine(string text) => text.IndexOfAny(['\r', '\n']) is var end and >= 0 ? text[..end] : text;
        }
    }
}
