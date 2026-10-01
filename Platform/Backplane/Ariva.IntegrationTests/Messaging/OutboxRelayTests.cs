using System.Data;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Inbox;
using Ariva.Infra.Messaging.Outbox;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Messaging;

[KafkaTopic(KafkaTopics.AlertStateChanged)]
public sealed class OutboxProbe : EventBase
{
    public string AlertId { get; set; }
    public int Step { get; set; }

    public override string GetPartitionKey() => AlertId;
}

/// <summary>
/// ARV-020 against PostgreSQL with script 0011: the outbox writes in the caller's transaction, the relay produces in
/// order per key and holds a failed key back until its retry time while other keys carry on, only one relay leads at a
/// time, and the inbox claims an event once, with a failed consumer's claim rolled back.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxRelayTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Messaging);
    private readonly EventCatalog _catalog = new([typeof(OutboxProbe).Assembly]);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ResetAsync()
    {
        await _host.CreateUserAsync("it.outbox." + Guid.NewGuid().ToString("N")[..8]);
        await _host.ReadAsync<object>("DELETE FROM outbox_message");
        await _host.ReadAsync<object>("DELETE FROM processed_event");
    }

    private Task WriteAsync(bool commit, params OutboxProbe[] events) =>
        _host.AsCallerAsync<bool>(null, async services =>
        {
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            unitOfWork.StorageProvider.BeginTransaction(IsolationLevel.ReadCommitted);
            await new NHibernateDomainEventOutbox(_catalog, _host.Clock).WriteAsync(unitOfWork.StorageProvider, events, Ct);
            if (commit)
                unitOfWork.PromiseToCommit();
            return true;
        });

    private OutboxRelay Relay(IOutboxTransport transport) => new(
        _host.Provider.GetRequiredService<DatabaseSettings>(),
        new KafkaSettings { Outbox = new OutboxRelaySettings { BatchSize = 50 } },
        transport,
        _host.Clock,
        NullLogger<OutboxRelay>.Instance);

    private static OutboxProbe Probe(string alert, int step) => new() { AlertId = alert, Step = step, CorrelationId = "corr-" + alert };

    [Fact]
    public async Task Outbox_Should_WriteInTheCallersTransaction_When_EventsAreRaised()
    {
        await ResetAsync();
        var kept = Probe("A-1", 1);

        await WriteAsync(commit: true, kept);
        await WriteAsync(commit: false, Probe("A-rolled-back", 1));

        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message")).Should().Be(1, "the rolled back transaction took its row with it");
        (await _host.ReadAsync<string>("SELECT topic FROM outbox_message WHERE id = @id", kept.Id)).Should().Be(KafkaTopics.AlertStateChanged);
        (await _host.ReadAsync<string>("SELECT message_key FROM outbox_message WHERE id = @id", kept.Id)).Should().Be("A-1");
        (await _host.ReadAsync<string>("SELECT message_type FROM outbox_message WHERE id = @id", kept.Id)).Should().Be(nameof(OutboxProbe));
        (await _host.ReadAsync<string>("SELECT payload->>'alertId' FROM outbox_message WHERE id = @id", kept.Id)).Should().Be("A-1");
        (await _host.ReadAsync<string>("SELECT headers->>'ariva-correlation-id' FROM outbox_message WHERE id = @id", kept.Id)).Should().Be("corr-A-1");
    }

    [Fact]
    public async Task Relay_Should_SendInOrderPerKeyAndHoldAFailedKeyBack_When_OneRowFails()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("A", 1), Probe("B", 1), Probe("A", 2), Probe("B", 2), Probe("A", 3));
        var transport = new RecordingTransport { FailOnce = ("A", 2) };

        var first = await Relay(transport).RunOnceAsync(Ct);

        first.Should().Be(new RelayPass(true, 5, 3, 1, 1));
        transport.Sent.Select(Describe).Should().Equal("A1", "B1", "B2");
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE payload->>'step' = '2' AND message_key = 'A'")).Should().Be(1);
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE payload->>'step' = '2' AND message_key = 'A'")).Should().Contain("broker down");

        var waiting = await Relay(transport).RunOnceAsync(Ct);
        waiting.Claimed.Should().Be(0, "A2 waits for its retry time and A3 waits behind it");

        _host.Clock.Advance(OutboxRelay.Backoff(1) + TimeSpan.FromSeconds(1));
        var retried = await Relay(transport).RunOnceAsync(Ct);

        retried.Sent.Should().Be(2);
        transport.Sent.Select(Describe).Should().Equal("A1", "B1", "B2", "A2", "A3");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE sent_on IS NULL")).Should().Be(0);
    }

    [Fact]
    public async Task Relay_Should_EndThePass_When_TheBrokerFails()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("F", 1), Probe("G", 1), Probe("H", 1));
        var transport = new RecordingTransport { BrokerDown = true };

        var pass = await Relay(transport).RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 3, 0, 1, 2), "the first failure ends the pass instead of waiting out a timeout per row");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE attempts = 0")).Should().Be(2, "the rows after it were not touched");
    }

    [Fact]
    public async Task Relay_Should_SkipThePass_When_AnotherRelayLeads()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("C", 1));
        var settings = _host.Provider.GetRequiredService<DatabaseSettings>();
        await using var leader = new NpgsqlConnection(settings.BuildConnectionString());
        await leader.OpenAsync(Ct);
        await using var transaction = await leader.BeginTransactionAsync(Ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", leader, transaction))
        {
            take.Parameters.AddWithValue("key", OutboxRelay.LeaderLock);
            await take.ExecuteScalarAsync(Ct);
        }

        var transport = new RecordingTransport();
        (await Relay(transport).RunOnceAsync(Ct)).Should().Be(RelayPass.NotLeader);
        transport.Sent.Should().BeEmpty();

        await transaction.RollbackAsync(Ct);
        (await Relay(transport).RunOnceAsync(Ct)).Sent.Should().Be(1, "the lock is free once the other leader's transaction ends");
    }

    [Fact]
    public async Task Relay_Should_HoldTheKeyBack_When_TheEventTypeIsUnknown()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("D", 1), Probe("D", 2), Probe("E", 1));
        await _host.ReadAsync<object>("UPDATE outbox_message SET message_type = 'RenamedProbe' WHERE message_key = 'D' AND payload->>'step' = '1'");
        var transport = new RecordingTransport { Catalog = _catalog };

        var pass = await Relay(transport).RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 3, 1, 0, 2), "the unknown row and the row behind it wait; the other key goes");
        transport.Sent.Select(Describe).Should().Equal("E1");
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE message_type = 'RenamedProbe'")).Should().Contain("RenamedProbe");
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE message_type = 'RenamedProbe'")).Should().Be(0, "not the row's fault: no attempts, no backoff");
    }

    [Fact]
    public async Task Inbox_Should_ClaimOnceAndForgetAFailedClaim_When_TheTransactionRollsBack()
    {
        await ResetAsync();
        var eventId = Guid.CreateVersion7();

        Task<bool> ClaimAsync(bool commit) => _host.AsCallerAsync(null, async services =>
        {
            var inbox = new PostgresInbox(services.GetRequiredService<IStorageProvider>(), _host.Clock);
            var claimed = await inbox.TryClaimAsync("ariva-main.it-probe", eventId, Ct);
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            if (commit)
                unitOfWork.PromiseToCommit();
            else
                await unitOfWork.RollbackAsync(Ct);
            return claimed;
        });

        (await ClaimAsync(commit: false)).Should().BeTrue("the first delivery claims the event");
        (await ClaimAsync(commit: true)).Should().BeTrue("the failed consumer's claim was rolled back, so the redelivery claims again");
        (await ClaimAsync(commit: true)).Should().BeFalse("a duplicate after a committed claim is skipped");
        (await _host.ReadAsync<long>("SELECT count(*) FROM processed_event WHERE event_id = @id", eventId)).Should().Be(1);
    }

    private static string Describe(OutboxEnvelope envelope)
    {
        using var payload = JsonDocument.Parse(envelope.Payload);
        return envelope.Key + payload.RootElement.GetProperty("step").GetInt32();
    }

    private sealed class RecordingTransport : IOutboxTransport
    {
        public List<OutboxEnvelope> Sent { get; } = [];
        public (string Key, int Step)? FailOnce { get; set; }
        public EventCatalog Catalog { get; set; }
        public bool BrokerDown { get; set; }

        public Task SendAsync(OutboxEnvelope message, CancellationToken ct)
        {
            if (BrokerDown)
                throw new TimeoutException("Local: Message timed out");
            if (Catalog is not null && !Catalog.TryResolve(message.MessageType, out _))
                throw new UnknownEventTypeException(message.MessageType);
            using var payload = JsonDocument.Parse(message.Payload);
            if (FailOnce is { } fail && fail.Key == message.Key && fail.Step == payload.RootElement.GetProperty("step").GetInt32())
            {
                FailOnce = null;
                throw new InvalidOperationException("broker down");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
