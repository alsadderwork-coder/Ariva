using System.Data;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Inbox;
using Ariva.Infra.Messaging.Kafka;
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
            await unitOfWork.StorageProvider.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await new NHibernateDomainEventOutbox(_catalog, _host.Clock).WriteAsync(unitOfWork.StorageProvider, events, Ct);
            if (commit)
                unitOfWork.PromiseToCommit();
            return true;
        });

    /// <summary>The relay's retry after a broker failure in these tests (the default).</summary>
    private static readonly TimeSpan BrokerRetry = TimeSpan.FromSeconds(new OutboxRelaySettings().BrokerRetrySeconds);

    private OutboxRelay Relay(IOutboxTransport transport, int passSeconds = 60, short batchSize = 50, TimeProvider clock = null) => new(
        _host.Provider.GetRequiredService<DatabaseSettings>(),
        new KafkaSettings { Outbox = new OutboxRelaySettings { BatchSize = batchSize, PassSeconds = passSeconds } },
        transport,
        clock ?? _host.Clock,
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
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE last_error IS NULL")).Should().Be(2, "the rows after it were not touched");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE attempts = 0")).Should().Be(3,
            "a broker that is away does not count against the row (ARV-165)");
    }

    /// <summary>
    /// ARV-165: a broker outage no longer grows the rows' backoff. Six passes fail on the broker, each on the next key's head
    /// row as the earlier ones wait their short retry; under the old rule those rows would wait 2, 4, 8 ... seconds more at
    /// each failure (up to five minutes) and resume that long after the broker came back. Now no attempt is counted and each
    /// row is due again within <see cref="BrokerRetry"/>, so the first pass after the broker's return and that wait sends
    /// every key, in order per key. A row's own failure keeps its exponential backoff (the control, last).
    /// </summary>
    [Fact]
    public async Task Relay_Should_ResumeEveryKeyWithinTheBrokerRetry_When_TheBrokerComesBackAfterManyFailedPasses()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("K1", 1), Probe("K2", 1), Probe("K3", 1), Probe("K1", 2), Probe("K2", 2), Probe("K3", 2));
        var transport = new RecordingTransport { BrokerDown = true };

        for (var pass = 0; pass < 6; pass++)
        {
            (await Relay(transport).RunOnceAsync(Ct)).Failed.Should().BeLessThanOrEqualTo(1);
            _host.Clock.Advance(TimeSpan.FromSeconds(2));
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE attempts > 0")).Should().Be(0, "no failure was the rows' own");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE last_error IS NOT NULL")).Should().BeGreaterThanOrEqualTo(3,
            "every key's head row was tried during the outage");

        transport.BrokerDown = false;
        _host.Clock.Advance(BrokerRetry);
        var resumed = await Relay(transport).RunOnceAsync(Ct);

        resumed.Sent.Should().Be(6, "every key resumes within the broker retry of the broker's return");
        transport.Sent.Select(Describe).Should().Equal("K1" + "1", "K2" + "1", "K3" + "1", "K1" + "2", "K2" + "2", "K3" + "2");

        // The control: a failure of the row itself still counts and backs off exponentially (2, 4, 8, 16 s), so after four
        // of them the row waits longer than the broker retry.
        await WriteAsync(true, Probe("K4", 1));
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            transport.FailOnce = ("K4", 1);
            (await Relay(transport).RunOnceAsync(Ct)).Failed.Should().Be(1);
            _host.Clock.Advance(attempt < 4 ? OutboxRelay.Backoff(attempt) + TimeSpan.FromSeconds(1) : BrokerRetry);
        }

        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE message_key = 'K4'")).Should().Be(4);
        (await Relay(transport).RunOnceAsync(Ct)).Claimed.Should().Be(0, "a row's own failure waits its backoff (16 s now), not the broker retry");
    }

    /// <summary>
    /// ARV-166: a send that never ends (the rider's producer can strand a produce for good, and a transport may ignore its
    /// token) cannot hold the relay's transaction, leader lock and rows: the pass ends within its bound, marks the row to be
    /// tried again after the broker retry (ARV-165: a send that outlived the pass is the broker's slowness, not the row's
    /// failure, so no attempt is counted), commits, and the next pass after that sends the row, in order before its key's
    /// next.
    /// </summary>
    [Fact]
    public async Task Relay_Should_EndThePassWithinItsBoundAndLetGo_When_ASendNeverEnds()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("S", 1), Probe("S", 2), Probe("T", 1));
        var transport = new RecordingTransport { Hang = true };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var pass = await Relay(transport, passSeconds: 2).RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the pass ends at its 2 second bound, not when the send does");
        pass.Should().Be(new RelayPass(true, 3, 0, 1, 2), "the stuck send fails the pass like a broker failure; the rows after it wait untouched");
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE message_key = 'S' AND payload->>'step' = '1'")).Should().Be(0,
            "a send that outlived the pass is the broker's slowness, not the row's failure (ARV-165)");
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE message_key = 'S' AND payload->>'step' = '1'")).Should().Contain("OutboxPassTimeoutException");

        // Nothing is held any more: another relay leads at once and the rows can be locked.
        var settings = _host.Provider.GetRequiredService<DatabaseSettings>();
        await using (var other = new NpgsqlConnection(settings.BuildConnectionString()))
        {
            await other.OpenAsync(Ct);
            await using var transaction = await other.BeginTransactionAsync(Ct);
            await using var lead = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", other, transaction);
            lead.Parameters.AddWithValue("key", OutboxRelay.LeaderLock);
            ((bool)(await lead.ExecuteScalarAsync(Ct))!).Should().BeTrue("the pass's transaction ended, and its leader lock with it");
            await using var rows = new NpgsqlCommand("SELECT count(*) FROM (SELECT seq FROM outbox_message FOR UPDATE NOWAIT) locked", other, transaction);
            ((long)(await rows.ExecuteScalarAsync(Ct))!).Should().Be(3, "no row is still locked by the pass");
            await transaction.RollbackAsync(Ct);
        }

        transport.Hang = false;
        _host.Clock.Advance(BrokerRetry + TimeSpan.FromSeconds(1));
        var next = await Relay(transport).RunOnceAsync(Ct);

        next.Sent.Should().Be(3);
        transport.Sent.Select(Describe).Should().Equal("S1", "S2", "T1");
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

    [Fact]
    public async Task Housekeeping_Should_KeepTheNewestRowOfEachCompactedKey_When_SentRowsAreOld()
    {
        // ARV-126: the newest sent row of each key on a compacted topic is the state that topic carries; it stays however old,
        // so ariva-db.sh can send it again after a Kafka loss. Older rows of such a key, and every old row of any other topic,
        // go as before; recent rows stay. The clock is 2026-10-01, so rows sent in August are past the 7 days.
        await ResetAsync();
        await _host.ReadAsync<object>("""
            INSERT INTO outbox_message (id, topic, message_key, message_type, payload, created_on, sent_on) VALUES
              (gen_random_uuid(), 'ariva.device.registry-changed.v1', 'device-1', 'DeviceRegistryChanged', '{"step": 1}', '2026-08-01Z', '2026-08-01Z'),
              (gen_random_uuid(), 'ariva.device.registry-changed.v1', 'device-1', 'DeviceRegistryChanged', '{"step": 2}', '2026-08-02Z', '2026-08-02Z'),
              (gen_random_uuid(), 'ariva.device.registry-changed.v1', 'device-2', 'DeviceRegistryChanged', '{"step": 1}', '2026-08-01Z', '2026-08-01Z'),
              (gen_random_uuid(), 'ariva.topology.zone-profile-activated.v1', 'site-1', 'ZoneProfilePublished', '{"step": 1}', '2026-08-01Z', '2026-08-01Z'),
              (gen_random_uuid(), 'ariva.device.zone-health.v1', 'DMO/Q1', 'ZoneHealthChanged', '{"step": 1}', '2026-08-01Z', '2026-08-01Z'),
              (gen_random_uuid(), 'ariva.device.zone-health.v1', 'DMO/Q1', 'ZoneHealthChanged', '{"step": 2}', '2026-08-03Z', NULL),
              (gen_random_uuid(), 'ariva.alert.state-changed.v1', 'alert-1', 'OutboxProbe', '{"alertId": "alert-1", "step": 1}', '2026-08-01Z', '2026-08-01Z'),
              (gen_random_uuid(), 'ariva.alert.state-changed.v1', 'alert-2', 'OutboxProbe', '{"alertId": "alert-2", "step": 1}', '2026-09-30Z', '2026-09-30Z')
            """);

        await Relay(new RecordingTransport()).RunOnceAsync(Ct);

        (await _host.ReadAsync<string>("SELECT string_agg(message_key || ':' || (payload->>'step'), ',' ORDER BY seq) FROM outbox_message"))
            .Should().Be("device-1:2,device-2:1,site-1:1,DMO/Q1:2,alert-2:1",
                "the newest row of each compacted key stays (the zone's unsent row was sent in the pass and is now its newest), the rest goes");
    }

    [Fact]
    public async Task Measure_Should_CountTheUnsentRowsAndDateTheOldest_When_RowsWait()
    {
        // ARV-125a: one constant statement on the partial index of unsent rows (script 0011), kept dead letters included.
        await ResetAsync();
        (await Relay(new RecordingTransport()).MeasureAsync(Ct)).Should().Be(new OutboxBacklog(0, null), "an empty outbox has no oldest row");
        var firstWritten = _host.Clock.GetUtcNow().UtcDateTime;
        await WriteAsync(true, Probe("M", 1), Probe("N", 1));
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await WriteAsync(true, Probe("M", 2));

        var backlog = await Relay(new RecordingTransport()).MeasureAsync(Ct);

        backlog.Unsent.Should().Be(3);
        backlog.OldestUtc.Should().BeCloseTo(firstWritten, TimeSpan.FromMilliseconds(1), "the oldest unsent row by its order of writing");
        await Relay(new RecordingTransport()).RunOnceAsync(Ct);
        (await Relay(new RecordingTransport()).MeasureAsync(Ct)).Should().Be(new OutboxBacklog(0, null), "the relay sent them all");
    }

    [Fact]
    public async Task Relay_Should_ReportTheBacklog_When_ItRunsAndTheProducerIsNotReady()
    {
        // ARV-125a: every relay measures, leader or not and whatever the producer's state, so a Kafka outage shows as a
        // growing backlog; the gauges read the measurement.
        await ResetAsync();
        await WriteAsync(true, Probe("R", 1), Probe("R", 2));
        _host.Clock.Advance(TimeSpan.FromMinutes(2));
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        using var relay = new OutboxRelay(_host.Provider.GetRequiredService<DatabaseSettings>(), new KafkaSettings(), new NotReadyTransport(), _host.Clock,
            NullLogger<OutboxRelay>.Instance, metrics);

        await relay.StartAsync(Ct);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (reader.Observe(OutboxMetrics.UnsentName).Count == 0 && DateTime.UtcNow < until)
                await Task.Delay(100, Ct);
        }
        finally
        {
            await relay.StopAsync(Ct);
        }

        reader.Observe(OutboxMetrics.UnsentName).Should().ContainSingle().Which.Value.Should().Be(2);
        reader.Observe(OutboxMetrics.OldestUnsentAgeName).Should().ContainSingle().Which.Value.Should().BeApproximately(120, 1);
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE sent_on IS NULL")).Should().Be(2, "measuring sends nothing");
    }

    /// <summary>
    /// ARV-175: a dead-letter topic whose broker is away holds back only itself. The pass sends its events first (though they
    /// were written after the letters), then the dead letters; the failing letter's topic is held, its second letter waits
    /// untouched, and the other dead-letter topic is sent. The hold outlives the pass and the relay: it is kept on the topic's
    /// rows, so another relay (another pod, or this one after a restart) claims none of them for
    /// <see cref="OutboxRelay.DeadLetterTopicHold"/>, though the failed letter's broker retry has passed, and then sends both.
    /// An event's broker failure still ends the pass.
    /// </summary>
    [Fact]
    public async Task Relay_Should_HoldBackOnlyTheDeadLetterTopic_When_ItsBrokerIsAway()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        var first = Letter(KafkaTopics.SlaDisputeChanged, 1);
        await store.KeepAsync(first, Ct);
        await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, 2), Ct);
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 3), Ct);
        await WriteAsync(true, Probe("D", 1), Probe("D", 2));
        var away = KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged);
        var transport = new RecordingTransport { BrokerDownFor = away };

        var pass = await Relay(transport).RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 5, 3, 1, 1), "two events and the other topic's letter sent, the away topic's second letter held");
        transport.Sent.Select(m => m.Topic).Should().Equal(KafkaTopics.AlertStateChanged, KafkaTopics.AlertStateChanged,
            KafkaTopics.DeadLetter(KafkaTopics.AlertStateChanged));
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE id = @id", DeadLetters.IdOf(first))).Should().Be(0,
            "the broker's absence is not the letter's failure (ARV-165)");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE sent_on IS NULL")).Should().Be(2);

        (await ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = @topic AND sent_on IS NULL AND next_attempt_on = @at",
            away, (_host.Clock.GetUtcNow() + OutboxRelay.DeadLetterTopicHold).UtcDateTime)).Should().Be(2, "both of the topic's letters are held on their rows");

        // The hold outlives the pass and the relay: past the broker retry the first letter would be due again, and the second
        // always was, but another relay claims neither until the hold has passed.
        transport.BrokerDownFor = null;
        await WriteAsync(true, Probe("D", 3));
        _host.Clock.Advance(BrokerRetry + TimeSpan.FromSeconds(1));
        (await Relay(transport).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 1, 1, 0, 0), "only the new event is claimed");
        _host.Clock.Advance(OutboxRelay.DeadLetterTopicHold);
        (await Relay(transport).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 2, 2, 0, 0), "the hold has passed");
        transport.Sent.Skip(4).Select(m => m.Topic).Should().Equal(away, away);

        // The control: an event's broker failure ends the pass before any letter.
        await ResetAsync();
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 4), Ct);
        await WriteAsync(true, Probe("E", 1), Probe("F", 1));
        var down = new RecordingTransport { BrokerDownFor = KafkaTopics.AlertStateChanged };

        (await Relay(down).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 3, 0, 1, 2), "the first event's failure ends the pass");
        down.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// ARV-175: a dead-letter topic that fails for a reason that is neither the broker's absence nor the letter's own (here the
    /// topic's authorization, a non-row failure) holds back only itself as well: the letter counts an attempt and backs off,
    /// its topic's second letter waits untouched, the events and the other topic's letter are sent. The same failure on an
    /// event ends the pass.
    /// </summary>
    [Fact]
    public async Task Relay_Should_HoldBackOnlyTheDeadLetterTopic_When_ItFailsForAReasonThatIsNotTheLetters()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        var first = Letter(KafkaTopics.SlaDisputeChanged, 1);
        await store.KeepAsync(first, Ct);
        await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, 2), Ct);
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 3), Ct);
        await WriteAsync(true, Probe("D", 1), Probe("D", 2));
        var transport = new RecordingTransport { RefusedFor = KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged) };

        var pass = await Relay(transport).RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 5, 3, 1, 1), "two events and the other topic's letter sent, the refused topic's second letter held");
        transport.Sent.Select(m => m.Topic).Should().Equal(KafkaTopics.AlertStateChanged, KafkaTopics.AlertStateChanged,
            KafkaTopics.DeadLetter(KafkaTopics.AlertStateChanged));
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE id = @id", DeadLetters.IdOf(first))).Should().Be(1, "not the broker's absence: it counts");
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE id = @id", DeadLetters.IdOf(first))).Should().Contain("KafkaException");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE sent_on IS NULL AND last_error IS NULL")).Should().Be(1,
            "the topic's second letter was not tried");

        // The control: the same failure on an event ends the pass before any letter.
        await ResetAsync();
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 4), Ct);
        await WriteAsync(true, Probe("E", 1), Probe("F", 1));
        var refused = new RecordingTransport { RefusedFor = KafkaTopics.AlertStateChanged };

        (await Relay(refused).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 3, 0, 1, 2), "the first event's failure ends the pass");
        refused.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// ARV-175: a dead-letter send that never ends (a topic whose partition leader is away strands the produce) costs a pass at
    /// most <see cref="OutboxRelay.DeadLetterPassBound"/>, not the pass's 60 seconds: the events go first, the stuck letter
    /// fails on the dead letters' share (no attempt counted), the rest of the letters wait untouched, and the next pass of this
    /// relay (any relay) sends the other topic's letter without trying the stuck topic again.
    /// </summary>
    [Fact]
    public async Task Relay_Should_GiveDeadLettersOnlyTheirShareOfThePass_When_ADeadLetterSendNeverEnds()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        var stuck = Letter(KafkaTopics.SlaDisputeChanged, 1);
        await store.KeepAsync(stuck, Ct);
        await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, 2), Ct);
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 3), Ct);
        await WriteAsync(true, Probe("H", 1), Probe("H", 2));
        var away = KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged);
        var transport = new RecordingTransport { HangFor = away };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var pass = await Relay(transport).RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(50), Ct);

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(OutboxRelay.DeadLetterPassBound - TimeSpan.FromMilliseconds(100))
            .And.BeLessThan(OutboxRelay.DeadLetterPassBound + TimeSpan.FromSeconds(10), "the dead letters' share ends the wait, not the pass's 60 seconds");
        pass.Should().Be(new RelayPass(true, 5, 2, 1, 2), "the events sent; the stuck letter failed; its topic's second letter and the other topic's wait");
        (pass.Sent + pass.Failed + pass.HeldBack).Should().Be(pass.Claimed);
        transport.Sent.Select(m => m.Topic).Should().Equal(KafkaTopics.AlertStateChanged, KafkaTopics.AlertStateChanged);
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE id = @id", DeadLetters.IdOf(stuck))).Should().Be(0,
            "a send that outlived its bound is the broker's slowness, not the letter's failure (ARV-165)");
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE id = @id", DeadLetters.IdOf(stuck)))
            .Should().Contain("OutboxPassTimeoutException").And.Contain("a relay pass gives dead letters");

        var next = await Relay(transport).RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        next.Should().Be(new RelayPass(true, 1, 1, 0, 0), "the stuck topic is held back, so only the other topic's letter is claimed");
        transport.Sent.Select(m => m.Topic).Should().EndWith(KafkaTopics.DeadLetter(KafkaTopics.AlertStateChanged));
    }

    /// <summary>
    /// ARV-164b: a dead-letter send cut at the end of the dead letters' share spends the share, even when the time stamps read
    /// less: the send's bound is a timer, which can fire a few milliseconds before the time stamps show the share's end (a full
    /// integration run once sent the next topic's letter with those milliseconds). Here the relay's time stamps stand still while
    /// its timers run, so the stuck send is cut with the whole share left on the time stamps, and the other topic's letter still
    /// waits (the old relay sent it, whatever the catch path took).
    /// </summary>
    [Fact]
    public async Task Relay_Should_SpendTheDeadLettersShare_When_ASendIsCutAtItsEndAndTheTimeStampsReadLess()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, 1), Ct);
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 2), Ct);
        var transport = new RecordingTransport { HangFor = KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged) };

        var pass = await Relay(transport, clock: new SteppedClock(_host.Clock)).RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(50), Ct);

        pass.Should().Be(new RelayPass(true, 2, 0, 1, 1), "the stuck letter was cut at the share's end, which spends it; the other topic's letter waits");
        transport.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// ARV-175: a backlog of dead letters cannot crowd events out of a pass. With a batch of two, three letters kept before two
    /// events: the pass claims a batch of each, so both events are claimed and sent first (one claim of two rows in order of
    /// writing would have taken two letters and no event).
    /// </summary>
    [Fact]
    public async Task Relay_Should_ClaimTheEvents_When_ABatchOfDeadLettersWasWrittenBeforeThem()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        for (var offset = 1; offset <= 3; offset++)
            await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, offset), Ct);
        await WriteAsync(true, Probe("C", 1), Probe("C", 2));
        var transport = new RecordingTransport();

        var pass = await Relay(transport, batchSize: 2).RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 4, 4, 0, 0), "a batch of events and a batch of letters");
        transport.Sent.Select(m => m.Topic).Should().Equal(KafkaTopics.AlertStateChanged, KafkaTopics.AlertStateChanged,
            KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged), KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged));
    }

    /// <summary>
    /// ARV-175: a dead-letter send cut short by the pass's own time (the events used it) is not its topic's fault: the pass ends
    /// as for an event, the letter is due again after the broker retry, and the topic is not held. The pass's time is measured
    /// on a clock the events' sends move, so the cut is exact.
    /// </summary>
    [Fact]
    public async Task Relay_Should_EndThePassWithoutHoldingTheTopic_When_ThePassesOwnTimeRunsOutOnADeadLetter()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        var cut = Letter(KafkaTopics.SlaDisputeChanged, 1);
        await store.KeepAsync(cut, Ct);
        await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, 2), Ct);
        await WriteAsync(true, Probe("P", 1));
        var away = KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged);
        var stepped = new SteppedClock(_host.Clock);
        var transport = new RecordingTransport
        {
            HangFor = away,
            OnSend = m => { if (m.Topic == KafkaTopics.AlertStateChanged) stepped.Step(TimeSpan.FromMilliseconds(1900)); }
        };

        var pass = await Relay(transport, passSeconds: 2, clock: stepped).RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        pass.Should().Be(new RelayPass(true, 3, 1, 1, 1), "the event sent; the letter cut by the pass's last 100 ms; the pass ended there");
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE id = @id", DeadLetters.IdOf(cut))).Should().Contain("relay pass's 2 seconds");
        (await _host.ReadAsync<DateTime>("SELECT next_attempt_on FROM outbox_message WHERE id = @id", DeadLetters.IdOf(cut)))
            .Should().Be((_host.Clock.GetUtcNow() + BrokerRetry).UtcDateTime, "due again after the broker retry, not held");
        (await ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = @topic AND next_attempt_on IS NULL", away)).Should().Be(1,
            "the topic's other letter is not held");

        transport.HangFor = null;
        _host.Clock.Advance(BrokerRetry);
        (await Relay(transport).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 2, 2, 0, 0), "both letters sent as soon as the broker retry passed");
    }

    /// <summary>
    /// ARV-175: a letter whose key, header names and values and error message carry NUL characters and lone surrogates is kept
    /// at a stop (PostgreSQL text refuses NUL and jsonb refuses both, so it was not before), with each replaced by U+FFFD and
    /// flagged; the row is keyed by the key's digest, the record's place stays exact, and the relay then sends it as JSON.
    /// </summary>
    [Fact]
    public async Task Keep_Should_StoreAndSendTheLetter_When_ItsTextHoldsNulAndLoneSurrogates()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        var letter = DeadLetters.Create(KafkaTopics.SlaDisputeChanged, 2, 77, "k\0ey", [9], [new("h\0", "v\0"), new("pair", "\uD800")],
            "it-consumer", new InvalidOperationException("bad \uDC00 text"), _host.Clock.GetUtcNow().UtcDateTime);

        await store.KeepAsync(letter, Ct);

        var id = DeadLetters.IdOf(letter);
        (await _host.ReadAsync<string>("SELECT message_key FROM outbox_message WHERE id = @id", id)).Should().StartWith("sha256-");
        (await _host.ReadAsync<string>("SELECT payload->>'key' FROM outbox_message WHERE id = @id", id)).Should().Be("k\uFFFDey");
        (await _host.ReadAsync<string>("SELECT payload->'headers'->>'ariva-dead-letter-text-replaced' FROM outbox_message WHERE id = @id", id))
            .Should().Be("error,headers,key");
        (await _host.ReadAsync<string>("SELECT payload->'headers'->>'h\uFFFD' FROM outbox_message WHERE id = @id", id)).Should().Be("v\uFFFD");
        (await _host.ReadAsync<string>("SELECT payload->>'errorMessage' FROM outbox_message WHERE id = @id", id)).Should().Be("bad \uFFFD text");
        (await _host.ReadAsync<string>("SELECT (payload->>'partition') || '/' || (payload->>'offset') || '/' || (payload->>'consumer') FROM outbox_message WHERE id = @id", id))
            .Should().Be("2/77/it-consumer", "a replay fetches the original record by its exact place");

        var transport = new RecordingTransport();
        (await Relay(transport).RunOnceAsync(Ct)).Sent.Should().Be(1);
        transport.Sent.Should().ContainSingle().Which.Topic.Should().Be(KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged));
    }

    /// <summary>One value read with a topic and a time bound as parameters (ARV-175).</summary>
    private async Task<T> ReadAsync<T>([System.Diagnostics.CodeAnalysis.ConstantExpected] string sql, string topic, DateTime? at = null)
    {
        await using var connection = new NpgsqlConnection(_host.Provider.GetRequiredService<DatabaseSettings>().BuildConnectionString());
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("topic", topic);
        if (at is { } value)
            command.Parameters.AddWithValue("at", value);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is DBNull or null ? default : (T)result;
    }

    private DeadLetter Letter(string topic, long offset) => DeadLetters.Create(topic, 0, offset, "key-" + offset, [1, 2, 3], [new("h", "v")],
        "it-consumer", new InvalidOperationException("boom"), _host.Clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The host's clock for dates, and a time stamp that moves only when a test steps it (ARV-175): the relay measures its pass's
    /// time on it. Timers still run in real time, so a send's bound ends after what was left of the stepped time.
    /// </summary>
    private sealed class SteppedClock(ManualClock clock) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Step(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A producer that has not started (ARV-167): the relay waits, and still measures.</summary>
    private sealed class NotReadyTransport : IOutboxTransport
    {
        public bool IsReady => false;

        public Task SendAsync(OutboxEnvelope message, CancellationToken ct) => throw new InvalidOperationException("The producer is not started.");
    }

    private sealed class RecordingTransport : IOutboxTransport
    {
        public List<OutboxEnvelope> Sent { get; } = [];
        public (string Key, int Step)? FailOnce { get; set; }
        public EventCatalog Catalog { get; set; }
        public bool BrokerDown { get; set; }

        /// <summary>A send that never ends and ignores its token (a stranded produce).</summary>
        public bool Hang { get; set; }

        /// <summary>A topic whose broker is away (ARV-175): its sends fail as <see cref="BrokerDown"/> makes every send fail.</summary>
        public string BrokerDownFor { get; set; }

        /// <summary>A topic the broker refuses (ARV-175): a failure that is neither the broker's absence nor the row's own.</summary>
        public string RefusedFor { get; set; }

        /// <summary>A topic whose sends never end and ignore their token (ARV-175: its partition leader is away).</summary>
        public string HangFor { get; set; }

        /// <summary>Called as each send starts (ARV-175: to move a test clock).</summary>
        public Action<OutboxEnvelope> OnSend { get; set; }

        /// <summary>The broker's absence, as the Kafka transport classifies it (ARV-165).</summary>
        public bool IsBrokerUnavailable(Exception error) => error is TimeoutException { Message: "Local: Message timed out" };

        public Task SendAsync(OutboxEnvelope message, CancellationToken ct)
        {
            OnSend?.Invoke(message);
            if (Hang || string.Equals(message.Topic, HangFor, StringComparison.Ordinal))
                return new TaskCompletionSource().Task;
            if (BrokerDown || string.Equals(message.Topic, BrokerDownFor, StringComparison.Ordinal))
                throw new TimeoutException("Local: Message timed out");
            if (string.Equals(message.Topic, RefusedFor, StringComparison.Ordinal))
                throw new Confluent.Kafka.KafkaException(Confluent.Kafka.ErrorCode.TopicAuthorizationFailed);
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
