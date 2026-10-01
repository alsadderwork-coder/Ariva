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

    private static async Task<(ServiceProvider Provider, ITestHarness Harness, Ledger Ledger)> StartAsync()
    {
        var ledger = new Ledger();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ledger);
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
                ConsumePipeline.Configure(endpoint, context, Fast);
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
        var inbox = probe.IndexOf("InboxFilter", StringComparison.Ordinal);

        retry.Should().BePositive();
        deadLetter.Should().BeGreaterThan(retry, "the retry wraps the dead-letter filter");
        inbox.Should().BeGreaterThan(deadLetter, "the inbox and unit of work run inside both");
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

    public sealed class ProbeConsumer(Ledger ledger) : IConsumer<PipelineProbe>
    {
        public Task Consume(ConsumeContext<PipelineProbe> context)
        {
            var attempt = ledger.Attempts.AddOrUpdate(context.Message.Id, 1, (_, n) => n + 1);
            if (attempt > context.Message.FailTimes)
                return Task.CompletedTask;
            return context.Message.TimeOut
                ? throw new TaskCanceledException($"probe timeout {attempt}")
                : throw new InvalidOperationException($"probe failure {attempt}");
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
