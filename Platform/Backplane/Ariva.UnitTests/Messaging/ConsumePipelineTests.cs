using System.Collections.Concurrent;
using Ariva.Core.Domain.Components;
using Ariva.Core.Services;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Inbox;
using Ariva.Infra.Messaging.Kafka;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Messaging;

/// <summary>An event for the pipe tests; it has no topic because it never leaves the in-memory bus.</summary>
public sealed class PipelineProbe : EventBase
{
    public string ZoneId { get; set; } = "zone-1";
    public int FailTimes { get; set; }

    /// <summary>Fail with the consumer's own timeout (TaskCanceledException) instead of an ordinary error.</summary>
    public bool TimeOut { get; set; }

    /// <summary>The first attempts that wait on their token until it is cancelled (a stalled handler, ARV-096e).</summary>
    public int StallTimes { get; set; }

    public override string GetPartitionKey() => ZoneId;
}

/// <summary>
/// ADR-0018 left the filter order on rider endpoints to be proven. The pipe probe showed that MassTransit puts the
/// retry first whatever the configuration order, so the dead-letter filter acts on the final attempt only; these tests
/// prove the behaviour that matters: retries first, then one dead letter, a fresh unit of work per attempt, and
/// duplicates skipped. <see cref="ConsumePipeline.Configure"/> builds the same pipe here on an in-memory endpoint as on
/// every Kafka topic endpoint.
/// </summary>
public sealed class ConsumePipelineTests
{
    private static readonly ConsumerSettings Fast = new() { RetryCount = 2, RetryMinMilliseconds = 1, RetryMaxMilliseconds = 5 };

    /// <summary>As <see cref="Fast"/>, with the shortest handler timeout a test can wait for (ARV-096e).</summary>
    private static readonly ConsumerSettings Timed = new() { RetryCount = 2, RetryMinMilliseconds = 1, RetryMaxMilliseconds = 5, HandlerTimeoutSeconds = 1 };

    private static async Task<(ServiceProvider Provider, ITestHarness Harness, Ledger Ledger)> StartAsync(ConsumerSettings settings = null,
        IDeadLetterStore store = null)
    {
        var ledger = new Ledger();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ledger);
        if (store is not null)
            services.AddSingleton(store);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDeadLetterSink>(ledger);
        services.AddScoped<FakeUnitOfWork>();
        services.AddScoped<IUnitOfWork>(s => s.GetRequiredService<FakeUnitOfWork>());
        services.AddScoped<IInbox, FakeInbox>();
        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<ProbeConsumer>();
            bus.UsingInMemory((context, memory) => memory.ReceiveEndpoint("ariva-pipeline-probe", endpoint =>
            {
                ConsumePipeline.Configure(endpoint, context, settings ?? Fast);
                endpoint.ConfigureConsumer<ProbeConsumer>(context);
            }));
        });

        var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return (provider, harness, ledger);
    }

    private static async Task SettleAsync(ITestHarness harness, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await harness.InactivityTask;
    }

    [Fact]
    public async Task Pipe_Should_RunTheRetryOutermost_When_Probed()
    {
        var (provider, harness, _) = await StartAsync();
        await using var __ = provider;

        var probe = System.Text.Json.JsonSerializer.Serialize(harness.Bus.GetProbeResult().Results);
        var retry = probe.IndexOf("\"filterType\":\"retry\"", StringComparison.Ordinal);
        var deadLetter = probe.IndexOf("ariva-dead-letter", StringComparison.Ordinal);
        var timeout = probe.IndexOf("ariva-handler-timeout", StringComparison.Ordinal);
        var inbox = probe.IndexOf("InboxFilter", StringComparison.Ordinal);

        retry.Should().BePositive();
        deadLetter.Should().BeGreaterThan(retry, "the retry wraps the dead-letter filter");
        timeout.Should().BeGreaterThan(deadLetter, "each attempt's handler timeout runs inside the retry and the dead-letter filter (ARV-096e)");
        inbox.Should().BeGreaterThan(timeout, "the inbox and unit of work run inside all three, so the timeout covers the claim and the commit too");
    }

    [Fact]
    public async Task Pipe_Should_RetryThenDeadLetterOnceAndAcknowledge_When_TheConsumerKeepsFailing()
    {
        var (provider, harness, ledger) = await StartAsync();
        await using var _ = provider;
        var probe = new PipelineProbe { FailTimes = 99 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => !ledger.Letters.IsEmpty);

        ledger.Attempts[probe.Id].Should().Be(3, "one delivery and two retries run inside the dead-letter filter");
        ledger.Letters.Should().ContainSingle().Which.ErrorType.Should().Be(typeof(InvalidOperationException).FullName);
        ledger.Letters.Single().ErrorMessage.Should().Be("probe failure 3");
        ledger.Letters.Single().BodyBase64.Should().NotBeEmpty("the original body travels with the dead letter");
        ledger.Rollbacks.Should().Be(3, "each failed attempt rolled back its unit of work, claim included");
        ledger.Committed.Should().BeEmpty();
        (await harness.Published.Any<Fault<PipelineProbe>>(TestContext.Current.CancellationToken)).Should().BeFalse("the dead-letter filter handled the failure, so the offset moves on");
    }

    [Fact]
    public async Task Pipe_Should_DeadLetterAConsumerTimeout_When_ItKeepsTimingOut()
    {
        var (provider, harness, ledger) = await StartAsync();
        await using var _ = provider;
        var probe = new PipelineProbe { FailTimes = 99, TimeOut = true };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => !ledger.Letters.IsEmpty);

        ledger.Attempts[probe.Id].Should().Be(3, "a consumer's own cancellation is retried like any failure");
        // MassTransit reports a consumer's own cancellation as ConsumerCanceledException.
        ledger.Letters.Should().ContainSingle().Which.ErrorType.Should().Be(typeof(ConsumerCanceledException).FullName);
    }

    [Fact]
    public async Task Pipe_Should_CancelAStalledAttemptAtTheHandlerTimeoutAndRetryIt_When_TheNextAttemptSucceeds()
    {
        // ARV-096e: a handler stalled on the database or a partner frees its slot at the timeout; the attempt is rolled back and retried.
        var (provider, harness, ledger) = await StartAsync(Timed);
        await using var _ = provider;
        var probe = new PipelineProbe { StallTimes = 1 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => ledger.Committed.Contains(probe.Id));

        ledger.Attempts[probe.Id].Should().Be(2);
        ledger.Cut.Should().Be(1, "the stalled attempt's own token was cancelled at the handler timeout");
        ledger.Rollbacks.Should().Be(1, "the cut attempt rolled its unit of work back, claim included");
        ledger.Committed.Should().ContainSingle();
        ledger.Letters.Should().BeEmpty();
    }

    [Fact]
    public async Task Pipe_Should_DeadLetterTheHandlerTimeout_When_EveryAttemptStalls()
    {
        var (provider, harness, ledger) = await StartAsync(Timed);
        await using var _ = provider;
        var probe = new PipelineProbe { StallTimes = 99 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => !ledger.Letters.IsEmpty);

        ledger.Attempts[probe.Id].Should().Be(3, "each attempt is cut at the timeout and retried like any failure");
        ledger.Cut.Should().Be(3);
        var letter = ledger.Letters.Should().ContainSingle().Subject;
        letter.ErrorType.Should().Be(typeof(HandlerTimeoutException).FullName);
        letter.ErrorMessage.Should().Contain("ran past its timeout of 1 s");
        ledger.Committed.Should().BeEmpty();
        (await harness.Published.Any<Fault<PipelineProbe>>(TestContext.Current.CancellationToken)).Should().BeFalse("the dead letter ends the message, so the offset moves on");
    }

    [Fact]
    public async Task Pipe_Should_RetryTheDeadLetter_When_TheBrokerRefusesIt()
    {
        var (provider, harness, ledger) = await StartAsync();
        await using var _ = provider;
        ledger.RefuseLetters = 1;
        var probe = new PipelineProbe { FailTimes = 99 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => !ledger.Letters.IsEmpty);

        ledger.Letters.Should().ContainSingle("the refused dead letter was sent again, not dropped with the message");
        ledger.Refused.Should().Be(1);
        (await harness.Published.Any<Fault<PipelineProbe>>(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Pipe_Should_CommitOnceWithoutDeadLetter_When_ARetrySucceeds()
    {
        var (provider, harness, ledger) = await StartAsync();
        await using var _ = provider;
        var probe = new PipelineProbe { FailTimes = 1 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => ledger.Committed.Contains(probe.Id));

        ledger.Attempts[probe.Id].Should().Be(2);
        ledger.Committed.Should().ContainSingle().Which.Should().Be(probe.Id);
        ledger.Letters.Should().BeEmpty();
    }

    [Fact]
    public async Task Pipe_Should_SkipTheConsumer_When_TheEventWasAppliedBefore()
    {
        var (provider, harness, ledger) = await StartAsync();
        await using var _ = provider;
        var probe = new PipelineProbe();

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => ledger.Committed.Contains(probe.Id));
        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        await SettleAsync(harness, () => ledger.Duplicates > 0);

        ledger.Attempts[probe.Id].Should().Be(1, "the redelivery found the inbox claim and did not run the consumer");
        ledger.Committed.Should().ContainSingle();
        ledger.Letters.Should().BeEmpty();
    }

    /// <summary>
    /// ARV-166: a dead letter that cannot be sent is retried until its endpoint stops, and the stop must leave the message
    /// unacknowledged so that it is delivered again after restart. The bus is stopped for real: the endpoint's Stopping
    /// event ends the loop (EndpointStopping), and the bus's stop, which waits for the message in hand, then completes. The
    /// loop ends as a cancellation of the message's own token: MassTransit's retry filter passes that on without another
    /// attempt (anything else it retries; with no retries configured its exponential policy even failed on it), and the
    /// receive pipe's rescue filter, which sends any other exception to the error pipe (a published fault and an
    /// acknowledged message), lets it through to the transport as a faulted receive. Before the fix the loop ended with its
    /// linked token: with no retries the message was rescued, with retries a fault was published.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Pipe_Should_LeaveTheMessageUnacknowledgedWithoutRetryOrFault_When_TheEndpointStopsWhileItsDeadLetterIsRefused(int retries)
    {
        var settings = new ConsumerSettings { RetryCount = retries, RetryMinMilliseconds = 1, RetryMaxMilliseconds = 5 };
        var (provider, harness, ledger) = await StartAsync(settings);
        await using var _ = provider;
        var receive = new ReceiveOutcome();
        harness.Bus.ConnectReceiveObserver(receive);
        ledger.RefuseLetters = int.MaxValue;
        var probe = new PipelineProbe { FailTimes = 99 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref ledger.Refused) < 1 && DateTime.UtcNow < until)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        ledger.Refused.Should().Be(1, "the dead letter was refused once and the loop waits out its backoff");
        var stop = provider.GetRequiredService<IBusControl>().StopAsync(TestContext.Current.CancellationToken);

        var outcome = await receive.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        outcome.Should().BeAssignableTo<OperationCanceledException>("the receive faults as a cancellation, so the transport does not acknowledge it");
        ledger.Attempts[probe.Id].Should().Be(retries + 1, "the stop is not retried");
        ledger.Refused.Should().Be(1, "nothing is tried after the stop");
        ledger.Letters.Should().BeEmpty();
        (await harness.Published.Any<Fault<PipelineProbe>>(TestContext.Current.CancellationToken)).Should().BeFalse(
            "the error pipe, which would publish a fault and acknowledge the message, is not reached");
    }

    /// <summary>
    /// ARV-171: with a store for dead letters (the outbox), a stop while the dead letter is refused keeps the letter and
    /// completes the message, so MassTransit's checkpointer never commits past a record that was neither processed nor
    /// dead-lettered. No fault is published, the consumer is not run again, and Kafka is not asked again.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Pipe_Should_KeepTheDeadLetterAndCompleteTheMessage_When_TheEndpointStopsWhileItsDeadLetterIsRefused(int retries)
    {
        var settings = new ConsumerSettings { RetryCount = retries, RetryMinMilliseconds = 1, RetryMaxMilliseconds = 5 };
        var store = new KeptLetters();
        var (provider, harness, ledger) = await StartAsync(settings, store);
        await using var _ = provider;
        var receive = new ReceiveOutcome();
        harness.Bus.ConnectReceiveObserver(receive);
        ledger.RefuseLetters = int.MaxValue;
        var probe = new PipelineProbe { FailTimes = 99 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref ledger.Refused) < 1 && DateTime.UtcNow < until)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        var stop = provider.GetRequiredService<IBusControl>().StopAsync(TestContext.Current.CancellationToken);

        var outcome = await receive.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        outcome.Should().BeNull("the letter is kept durably, so the message completes");
        store.Letters.Should().ContainSingle().Which.ErrorMessage.Should().Be($"probe failure {retries + 1}");
        ledger.Attempts[probe.Id].Should().Be(retries + 1, "the stop is not retried");
        ledger.Refused.Should().Be(1, "Kafka is not asked again after the stop");
        ledger.Letters.Should().BeEmpty();
        (await harness.Published.Any<Fault<PipelineProbe>>(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    /// <summary>
    /// ARV-171: the database away as well (the store fails) leaves ARV-166's path: the message is not acknowledged.
    /// </summary>
    [Fact]
    public async Task Pipe_Should_LeaveTheMessageUnacknowledged_When_TheDeadLetterCannotBeKeptEither()
    {
        var store = new KeptLetters { Fail = true };
        var (provider, harness, ledger) = await StartAsync(new ConsumerSettings { RetryCount = 0, RetryMinMilliseconds = 1, RetryMaxMilliseconds = 5 }, store);
        await using var _ = provider;
        var receive = new ReceiveOutcome();
        harness.Bus.ConnectReceiveObserver(receive);
        ledger.RefuseLetters = int.MaxValue;

        await harness.Bus.Publish(new PipelineProbe { FailTimes = 99 }, TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref ledger.Refused) < 1 && DateTime.UtcNow < until)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        var stop = provider.GetRequiredService<IBusControl>().StopAsync(TestContext.Current.CancellationToken);

        var outcome = await receive.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        outcome.Should().BeAssignableTo<OperationCanceledException>("nothing durable was written, so the message must not complete");
        store.Attempts.Should().Be(1);
        (await harness.Published.Any<Fault<PipelineProbe>>(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    /// <summary>
    /// From the ARV-166 review: the bus stops while the retry filter waits between two attempts. The endpoint's retry is
    /// not cancelled by a stop (MassTransit builds <c>UseMessageRetry</c> on an endpoint with <c>CancellationToken.None</c>):
    /// the stop waits out the schedule, the remaining attempts run, and the final one keeps the dead letter in the outbox
    /// at once (the endpoint is already stopping, so Kafka is not asked within the stop) and the message completes. So a
    /// retry wait never leaves a faulted record behind for the checkpointer to commit past (ARV-171).
    /// </summary>
    [Fact]
    public async Task Pipe_Should_FinishItsRetriesAndKeepTheDeadLetter_When_TheBusStopsDuringARetryWait()
    {
        var store = new KeptLetters();
        var settings = new ConsumerSettings { RetryCount = 2, RetryMinMilliseconds = 1500, RetryMaxMilliseconds = 1500 };
        var (provider, harness, ledger) = await StartAsync(settings, store);
        await using var _ = provider;
        var receive = new ReceiveOutcome();
        harness.Bus.ConnectReceiveObserver(receive);
        var probe = new PipelineProbe { FailTimes = 99 };

        await harness.Bus.Publish(probe, TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!ledger.Attempts.ContainsKey(probe.Id) && DateTime.UtcNow < until)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // into the first retry wait
        ledger.Attempts[probe.Id].Should().Be(1);
        var stop = provider.GetRequiredService<IBusControl>().StopAsync(TestContext.Current.CancellationToken);

        var outcome = await receive.Ended.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stop.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        outcome.Should().BeNull("the message completes once its dead letter is kept");
        ledger.Attempts[probe.Id].Should().Be(3, "the stop waits for the remaining attempts");
        store.Letters.Should().ContainSingle("the final attempt keeps the dead letter in the outbox, which sends it later");
        ledger.Letters.Should().BeEmpty("the endpoint was already stopping, so Kafka is not asked");
        (await harness.Published.Any<Fault<PipelineProbe>>(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    /// <summary>The outbox for dead letters, in memory.</summary>
    private sealed class KeptLetters : IDeadLetterStore
    {
        private int _attempts;

        public bool Fail { get; init; }
        public System.Collections.Concurrent.ConcurrentQueue<DeadLetter> Letters { get; } = new();
        public int Attempts => Volatile.Read(ref _attempts);

        public Task KeepAsync(DeadLetter letter, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            if (Fail)
                return Task.FromException(new TimeoutException("The database is away."));
            Letters.Enqueue(letter);
            return Task.CompletedTask;
        }
    }

    /// <summary>How the receive of a message ended: its exception, or null when it completed (acknowledged).</summary>
    private sealed class ReceiveOutcome : IReceiveObserver
    {
        public TaskCompletionSource<Exception> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PreReceive(ReceiveContext context) => Task.CompletedTask;

        public Task PostReceive(ReceiveContext context)
        {
            Ended.TrySetResult(null);
            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class => Task.CompletedTask;

        public Task ConsumeFault<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception) where T : class =>
            Task.CompletedTask;

        public Task ReceiveFault(ReceiveContext context, Exception exception)
        {
            Ended.TrySetResult(exception);
            return Task.CompletedTask;
        }
    }

    public sealed class ProbeConsumer(Ledger ledger) : IConsumer<PipelineProbe>
    {
        public async Task Consume(ConsumeContext<PipelineProbe> context)
        {
            var attempt = ledger.Attempts.AddOrUpdate(context.Message.Id, 1, (_, n) => n + 1);
            if (attempt <= context.Message.StallTimes)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref ledger.Cut);
                    throw;
                }
            }

            if (attempt > context.Message.FailTimes)
                return;
            if (context.Message.TimeOut)
                throw new TaskCanceledException($"probe timeout {attempt}");
            throw new InvalidOperationException($"probe failure {attempt}");
        }
    }

    /// <summary>What the pipe did, shared across scopes.</summary>
    public sealed class Ledger : IDeadLetterSink
    {
        public ConcurrentDictionary<Guid, int> Attempts { get; } = new();
        public ConcurrentQueue<DeadLetter> Letters { get; } = new();
        public ConcurrentBag<Guid> Committed { get; } = [];
        public HashSet<Guid> Claimed { get; } = [];
        public int Rollbacks;
        public int Duplicates;
        public int Cut;
        public int RefuseLetters;
        public int Refused;

        public Task SendAsync(DeadLetter letter, CancellationToken ct)
        {
            if (Interlocked.Decrement(ref RefuseLetters) >= 0)
            {
                Interlocked.Increment(ref Refused);
                throw new InvalidOperationException("Local: Message timed out");
            }

            Letters.Enqueue(letter);
            return Task.CompletedTask;
        }
    }

    /// <summary>A claim stays tentative until the unit of work commits, as in PostgreSQL.</summary>
    public sealed class FakeInbox(Ledger ledger, FakeUnitOfWork unitOfWork) : IInbox
    {
        public Task<bool> TryClaimAsync(string consumer, Guid eventId, CancellationToken ct)
        {
            lock (ledger.Claimed)
            {
                if (ledger.Claimed.Contains(eventId))
                {
                    Interlocked.Increment(ref ledger.Duplicates);
                    return Task.FromResult(false);
                }
            }

            unitOfWork.Pending = eventId;
            return Task.FromResult(true);
        }
    }

    public sealed class FakeUnitOfWork(Ledger ledger) : IUnitOfWork
    {
        public Guid? Pending { get; set; }
        public Guid Id { get; } = Guid.NewGuid();
        public IStorageProvider StorageProvider => null;
        public bool IsToBeCommitted { get; private set; }

        public Task EndAsync(CancellationToken ct = default)
        {
            if (IsToBeCommitted && Pending is { } id)
            {
                lock (ledger.Claimed)
                    ledger.Claimed.Add(id);
                ledger.Committed.Add(id);
            }

            Pending = null;
            IsToBeCommitted = false;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            if (Pending is not null)
                Interlocked.Increment(ref ledger.Rollbacks);
            Pending = null;
            IsToBeCommitted = false;
            return Task.CompletedTask;
        }

        public void RegisterPostCommitAction(Func<Task> action)
        {
        }

        public void RegisterPostRollbackAction(Func<Task> action)
        {
        }

        public void PromiseToCommit() => IsToBeCommitted = true;

        public void PromiseNotToCommit() => IsToBeCommitted = false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
