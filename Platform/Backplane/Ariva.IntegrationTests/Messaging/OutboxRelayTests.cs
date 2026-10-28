using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
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

/// <summary>An event of another topic than <see cref="OutboxProbe"/>'s (ARV-177: one topic missing, the other present).</summary>
[KafkaTopic(KafkaTopics.FlowNowcast)]
public sealed class OutboxOtherProbe : EventBase
{
    public string ZoneId { get; set; }
    public int Step { get; set; }

    public override string GetPartitionKey() => ZoneId;
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

    private Task WriteAsync(bool commit, params Ariva.Core.Domain.Contracts.IEvent[] events) =>
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

    /// <summary>A relay that keeps its state across passes (ARV-245), with a one-minute broker streak, a log and metrics.</summary>
    private OutboxRelay StreakRelay(IOutboxTransport transport, RelayLog log, OutboxMetrics metrics) => new(
        _host.Provider.GetRequiredService<DatabaseSettings>(),
        new KafkaSettings { Outbox = new OutboxRelaySettings { BatchSize = 50, BrokerStreakMinutes = 1 } },
        transport,
        _host.Clock,
        log,
        metrics);

    private static OutboxProbe Probe(string alert, int step) => new() { AlertId = alert, Step = step, CorrelationId = "corr-" + alert };

    private static OutboxOtherProbe Other(string zone, int step) => new() { ZoneId = zone, Step = step, CorrelationId = "corr-" + zone };

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

    /// <summary>
    /// ARV-246: a full batch held back without a failure (rows of an event type this build does not know, as an older leader
    /// reads them during a rolling deploy) sends nothing, so the relay waits its poll interval before its next pass. Before, a
    /// full batch with no failure skipped the wait, and the leader ran back-to-back transactions on the same rows, taking the
    /// leader lock again before the pods that know the type could.
    /// </summary>
    [Fact]
    public async Task Relay_Should_WaitThePollInterval_When_AFullBatchIsHeldBackWithoutAFailure()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("U", 1), Probe("V", 1));
        await _host.ReadAsync<object>("UPDATE outbox_message SET message_type = 'RenamedProbe'");
        var tried = new List<TimeSpan>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        // Two keys, so each pass tries two sends: the first of each pair starts a pass.
        var transport = new RecordingTransport
        {
            Catalog = _catalog,
            OnSend = _ =>
            {
                lock (tried)
                    tried.Add(elapsed.Elapsed);
            }
        };
        var poll = TimeSpan.FromSeconds(1);
        using var relay = new OutboxRelay(_host.Provider.GetRequiredService<DatabaseSettings>(),
            new KafkaSettings { Outbox = new OutboxRelaySettings { BatchSize = 2, PollMilliseconds = (int)poll.TotalMilliseconds } },
            transport, _host.Clock, NullLogger<OutboxRelay>.Instance);

        await relay.StartAsync(Ct);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (Tried() < 6 && DateTime.UtcNow < until)
                await Task.Delay(50, Ct);
        }
        finally
        {
            await relay.StopAsync(Ct);
        }

        List<TimeSpan> starts;
        lock (tried)
            starts = [.. tried.Where((_, i) => i % 2 == 0)];
        starts.Should().HaveCountGreaterThanOrEqualTo(3, "three passes within 30 s");
        starts.Zip(starts.Skip(1), (first, next) => next - first).Should().OnlyContain(gap => gap >= poll * 0.9,
            "a pass that sent nothing waits the poll interval, however full its batch");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE sent_on IS NULL")).Should().Be(2, "held back, never dropped");

        int Tried()
        {
            lock (tried)
                return tried.Count;
        }
    }

    /// <summary>
    /// ARV-246, the other side of the loop's condition: a full batch with a row sent and no failure still passes again at once,
    /// though a row of it is held back (an unknown type ahead of three known keys, a batch of two): the three passes that each
    /// send one known row follow each other well within the poll interval, and only the pass that claims the unknown row alone
    /// waits.
    /// </summary>
    [Fact]
    public async Task Relay_Should_PassAgainAtOnce_When_AFullBatchSentARowThoughAnotherWasHeldBack()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("U", 1), Probe("A", 1), Probe("B", 1), Probe("C", 1));
        await _host.ReadAsync<object>("UPDATE outbox_message SET message_type = 'RenamedProbe' WHERE message_key = 'U'");
        var sent = new List<TimeSpan>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var transport = new RecordingTransport
        {
            Catalog = _catalog,
            OnSend = message =>
            {
                if (message.Key == "U")
                    return;
                lock (sent)
                    sent.Add(elapsed.Elapsed);
            }
        };
        var poll = TimeSpan.FromSeconds(10);
        using var relay = new OutboxRelay(_host.Provider.GetRequiredService<DatabaseSettings>(),
            new KafkaSettings { Outbox = new OutboxRelaySettings { BatchSize = 2, PollMilliseconds = (int)poll.TotalMilliseconds } },
            transport, _host.Clock, NullLogger<OutboxRelay>.Instance);

        await relay.StartAsync(Ct);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (Sent() < 3 && DateTime.UtcNow < until)
                await Task.Delay(50, Ct);
        }
        finally
        {
            await relay.StopAsync(Ct);
        }

        List<TimeSpan> times;
        lock (sent)
            times = [.. sent];
        times.Should().HaveCount(3, "A, B and C, one a pass");
        (times[2] - times[0]).Should().BeLessThan(poll / 2, "a pass that sent a row is followed by the next at once, not after the poll");
        (await _host.ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE sent_on IS NULL")).Should().Be(1, "the unknown row waits");

        int Sent()
        {
            lock (sent)
                return sent.Count;
        }
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
    /// ARV-177: an event whose topic is missing (Ariva's producer creates no topic, so the send fails as the client's
    /// <c>Local_UnknownTopic</c>, classified as the Kafka transport classifies it) fails as its own failure: an attempt counted,
    /// the row's backoff and its last error. Its topic is held until that backoff ends, on its other rows too (its other key
    /// is not tried, and waits as long), and the other topic's events are sent in the same pass, in order per key. Before, the
    /// failure ended the pass at the row, and every later row of any topic waited for the next pass.
    /// </summary>
    [Fact]
    public async Task Relay_Should_SkipOnlyTheMissingTopicAndSendTheOthers_When_AnEventsTopicIsMissing()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("M", 1), Other("O", 1), Probe("N", 1), Other("O", 2), Other("P", 1));
        var transport = new RecordingTransport { UnknownTopicFor = KafkaTopics.AlertStateChanged };

        var pass = await Relay(transport).RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 5, 3, 1, 1), "the missing topic's first row failed, its other key was skipped, the other topic's three rows sent");
        transport.Sent.Select(Describe).Should().Equal("O1", "O2", "P1");
        transport.Sent.Should().OnlyContain(m => m.Topic == KafkaTopics.FlowNowcast);
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE message_key = 'M'")).Should().Be(1, "the row's own failure counts");
        (await _host.ReadAsync<DateTime>("SELECT next_attempt_on FROM outbox_message WHERE message_key = 'M'"))
            .Should().Be((_host.Clock.GetUtcNow() + OutboxRelay.Backoff(1)).UtcDateTime, "it backs off as its own failure");
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE message_key = 'M'")).Should().Contain("Unknown topic");
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE message_key = 'N'")).Should().Be(0, "the topic's other key was not tried");
        (await _host.ReadAsync<string>("SELECT last_error FROM outbox_message WHERE message_key = 'N'")).Should().BeNull();
        (await _host.ReadAsync<DateTime>("SELECT next_attempt_on FROM outbox_message WHERE message_key = 'N'"))
            .Should().Be((_host.Clock.GetUtcNow() + OutboxRelay.Backoff(1)).UtcDateTime, "the topic is held as long as its failed row");

        // The topics job creates the topic: the topic's rows go once the hold has ended, in order.
        transport.UnknownTopicFor = null;
        (await Relay(transport).RunOnceAsync(Ct)).Claimed.Should().Be(0, "every row of the topic waits for the hold");
        _host.Clock.Advance(OutboxRelay.Backoff(1) + TimeSpan.FromSeconds(1));
        (await Relay(transport).RunOnceAsync(Ct)).Sent.Should().Be(2);
        transport.Sent.Select(Describe).Should().Equal("O1", "O2", "P1", "M1", "N1");
    }

    /// <summary>
    /// ARV-177, from the security review: a missing topic with more due rows than a pass claims (a batch of 2 here, 100 in a
    /// host) cannot take every pass's batch. The first pass claims only the topic's rows and tries one; the topic is then held
    /// on its rows, so the next pass, without any time passing, claims the other topic's rows and sends them. While the topic
    /// stays missing only its oldest row tries it again, at the end of each hold, and the hold grows with that row's backoff.
    /// </summary>
    [Fact]
    public async Task Relay_Should_ClaimTheOtherTopics_When_AMissingTopicHasMoreDueRowsThanABatch()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("M", 1), Probe("N", 1), Probe("Q", 1), Other("O", 1), Other("P", 1));
        var transport = new RecordingTransport { UnknownTopicFor = KafkaTopics.AlertStateChanged };

        (await Relay(transport, batchSize: 2).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 2, 0, 1, 1), "the batch held only the missing topic's rows");
        (await Relay(transport, batchSize: 2).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 2, 2, 0, 0), "the held topic is not claimed: the other topic's rows are");
        transport.Sent.Select(Describe).Should().Equal("O1", "P1");
        (await ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = @topic AND attempts = 0 AND last_error IS NULL", KafkaTopics.AlertStateChanged))
            .Should().Be(2, "only the oldest row tried the topic");

        // Still missing when the hold ends: the oldest row tries again and the hold grows with its backoff.
        _host.Clock.Advance(OutboxRelay.Backoff(1) + TimeSpan.FromSeconds(1));
        (await Relay(transport, batchSize: 2).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 2, 0, 1, 1));
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE message_key = 'M'")).Should().Be(2);
        (await ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = @topic AND next_attempt_on = @at", KafkaTopics.AlertStateChanged,
            (_host.Clock.GetUtcNow() + OutboxRelay.Backoff(2)).UtcDateTime)).Should().Be(3, "every row of the topic is held as long as the oldest one's backoff");
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

        // The control: a refusal that is not a topic's (the cluster's authorization) ends the pass at the first event, before
        // any letter. A refusal of the event's own topic skips only that topic since ARV-177
        // (Relay_Should_SkipOnlyTheMissingTopicAndSendTheOthers_When_AnEventsTopicIsMissing).
        await ResetAsync();
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 4), Ct);
        await WriteAsync(true, Probe("E", 1), Probe("F", 1));
        var refused = new RecordingTransport { ClusterRefused = true };

        (await Relay(refused).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 3, 0, 1, 2), "the first event's failure ends the pass");
        refused.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// ARV-246: a dead letter whose send fails on this host's own producer (disposed, Local_Destroy when it is retired, a message
    /// it purged at a fatal error, the fatal client error itself) holds back no topic: the failure is the producer's, not the
    /// topic's, so a hold would cure nothing. The pass ends as for an event's failure (this producer cannot send the rest
    /// either), the letter counts no attempt and waits the broker retry, and the topic's other letter goes at the next pass, of
    /// this relay or another, without a minute's hold. The broker's own absence still holds the topic
    /// (<see cref="Relay_Should_HoldBackOnlyTheDeadLetterTopic_When_ItsBrokerIsAway"/>).
    /// </summary>
    [Theory]
    [InlineData("disposed")]
    [InlineData("destroyed")]
    [InlineData("purged")]
    [InlineData("fatal")]
    public async Task Relay_Should_EndThePassWithoutHoldingTheTopic_When_ADeadLetterFailsOnThisHostsProducer(string failure)
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        var first = Letter(KafkaTopics.SlaDisputeChanged, 1);
        await store.KeepAsync(first, Ct);
        await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, 2), Ct);
        await store.KeepAsync(Letter(KafkaTopics.AlertStateChanged, 3), Ct);
        await WriteAsync(true, Probe("D", 1), Probe("D", 2));
        var topic = KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged);
        var transport = new RecordingTransport
        {
            FailFor = topic,
            Failure = failure switch
            {
                "disposed" => () => new ObjectDisposedException(nameof(KafkaProduce)),
                "destroyed" => () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_Destroy,
                    "The producer was replaced after a fatal error before Kafka reported on the message.")),
                "purged" => () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_PurgeQueue, "Local: Purged in queue")),
                _ => () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_Fatal, "fatal", true))
            }
        };
        var relay = Relay(transport);

        var pass = await relay.RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 5, 2, 1, 2), "the events sent; the letter's failure ends the pass, the other two letters untouched");
        transport.Sent.Select(m => m.Topic).Should().Equal(KafkaTopics.AlertStateChanged, KafkaTopics.AlertStateChanged);
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE id = @id", DeadLetters.IdOf(first))).Should().Be(0,
            "not the letter's failure (ARV-165)");
        (await ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = @topic AND sent_on IS NULL AND next_attempt_on = @at",
            topic, (_host.Clock.GetUtcNow() + OutboxRelay.DeadLetterTopicHold).UtcDateTime)).Should().Be(0, "no letter of the topic is held");

        // The next pass of this relay (its own holds) sends the topic's other letter and the other topic's at once; another
        // relay (the rows) sends the failed letter after its broker retry.
        transport.FailFor = null;
        (await relay.RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 2, 2, 0, 0), "nothing of the topic is held");
        transport.Sent.Skip(2).Select(m => m.Topic).Should().Equal(topic, KafkaTopics.DeadLetter(KafkaTopics.AlertStateChanged));
        _host.Clock.Advance(BrokerRetry + TimeSpan.FromSeconds(1));
        (await Relay(transport).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 1, 1, 0, 0), "the failed letter after its broker retry");
    }

    /// <summary>
    /// ARV-246: with a transport that keeps <see cref="IOutboxTransport"/>'s defaults (no failure is the broker's), a disposed
    /// transport's failure reaches the relay's last catch: it counts an attempt, as any failure there does, but ends the pass
    /// without holding the dead letter's topic, since <see cref="IOutboxTransport.IsProducerUnavailable"/> names it the producer's.
    /// </summary>
    [Fact]
    public async Task Relay_Should_HoldNoTopic_When_ADisposedTransportWithTheDefaultsFailsADeadLetter()
    {
        await ResetAsync();
        var store = new OutboxDeadLetterStore(_host.Provider.GetRequiredService<DatabaseSettings>(), _host.Clock);
        var first = Letter(KafkaTopics.SlaDisputeChanged, 1);
        await store.KeepAsync(first, Ct);
        await store.KeepAsync(Letter(KafkaTopics.SlaDisputeChanged, 2), Ct);
        await WriteAsync(true, Probe("D", 1));
        var topic = KafkaTopics.DeadLetter(KafkaTopics.SlaDisputeChanged);
        var transport = new DefaultsTransport { DisposedFor = topic };

        var pass = await Relay(transport).RunOnceAsync(Ct);

        pass.Should().Be(new RelayPass(true, 3, 1, 1, 1), "the event sent; the letter's failure ends the pass, the other letter untouched");
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message WHERE id = @id", DeadLetters.IdOf(first))).Should().Be(1,
            "not the broker's absence by these defaults, so it counts");
        (await ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = @topic AND sent_on IS NULL AND next_attempt_on = @at",
            topic, (_host.Clock.GetUtcNow() + OutboxRelay.DeadLetterTopicHold).UtcDateTime)).Should().Be(0, "no letter of the topic is held");

        transport.DisposedFor = null;
        (await Relay(transport).RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 1, 1, 0, 0), "the topic's other letter goes at once");
        transport.Sent.Should().Equal(KafkaTopics.AlertStateChanged, topic);
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
    /// ARV-269: a send this host's own producer failed (disposed, <c>Local_Destroy</c> as a retired client fails it, a message it
    /// purged at a fatal error, the fatal error itself) is logged as the producer's and kept out of the row's broker streak:
    /// past the one-minute streak no Error points at a partition without a leader and nothing is counted by topic; every
    /// warning names the producer, none the broker; no attempt is counted.
    /// </summary>
    [Theory]
    [InlineData("disposed")]
    [InlineData("destroyed")]
    [InlineData("purged")]
    [InlineData("fatal")]
    public async Task Relay_Should_ReportTheProducersOwnFailureAsItsOwn_When_ItLastsPastTheBrokerStreak(string failure)
    {
        await ResetAsync();
        await WriteAsync(true, Probe("P", 1));
        var transport = new RecordingTransport { FailFor = KafkaTopics.AlertStateChanged, Failure = ProducersOwn(failure) };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = StreakRelay(transport, log, metrics);

        // Fifteen broker retries, 75 seconds: past the one-minute streak a broker failure would have reached.
        for (var pass = 0; pass < 15; pass++)
        {
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
            _host.Clock.Advance(BrokerRetry);
        }

        log.Errors.Should().BeEmpty("the producer's own failure is not the broker's absence, so no streak points at a partition");
        reader.Sum(OutboxMetrics.BrokerStreaksName).Should().Be(0);
        relay.BrokerStreaksFollowed.Should().Be(0);
        log.Warnings.Should().HaveCount(15).And.OnlyContain(w => w.Contains("this host's Kafka producer is unavailable", StringComparison.Ordinal) &&
            !w.Contains("the broker is unavailable", StringComparison.Ordinal));
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message")).Should().Be(0, "not the row's failure (ARV-165)");

        // The recreated client sends it.
        transport.FailFor = null;
        (await relay.RunOnceAsync(Ct)).Sent.Should().Be(1);
    }

    /// <summary>
    /// ARV-269: a producer's own failure ends a broker streak already running for the row, since the broker never saw those
    /// sends: one broker failure, then the producer failing it for 65 seconds (past the one-minute streak), then the broker
    /// again: that failure starts a new streak, so no Error points at a partition without a leader and nothing is counted.
    /// The new streak is then reported as usual once it lasts the minute.
    /// </summary>
    [Fact]
    public async Task Relay_Should_EndTheBrokerStreak_When_TheProducerFailsTheRowInBetween()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("Q", 1));
        var transport = new RecordingTransport { BrokerDownFor = KafkaTopics.AlertStateChanged };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = StreakRelay(transport, log, metrics);

        (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1, "the broker's absence starts the row's streak");
        relay.BrokerStreaksFollowed.Should().Be(1);
        _host.Clock.Advance(BrokerRetry);
        (transport.BrokerDownFor, transport.FailFor, transport.Failure) = (null, KafkaTopics.AlertStateChanged, ProducersOwn("destroyed"));
        for (var pass = 0; pass < 13; pass++)
        {
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
            _host.Clock.Advance(BrokerRetry);
        }

        relay.BrokerStreaksFollowed.Should().Be(0, "the producer's failure ended the streak");
        (transport.BrokerDownFor, transport.FailFor) = (KafkaTopics.AlertStateChanged, null);
        (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);

        log.Errors.Should().BeEmpty("70 seconds since the first broker failure, but the producer's 65 broke the streak");
        reader.Sum(OutboxMetrics.BrokerStreaksName).Should().Be(0);

        // The broker's absence alone for the minute is still reported.
        for (var pass = 0; pass < 12; pass++)
        {
            _host.Clock.Advance(BrokerRetry);
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
        }

        log.Errors.Should().ContainSingle().Which.Should().Contain("for 1 minutes without a break");
        reader.Sum(OutboxMetrics.BrokerStreaksName).Should().Be(1);
    }

    /// <summary>
    /// ARV-270: a broker streak's Error names its last failure's cause, and points at a partition without a leader only for the
    /// client's message timeout. A full producer queue (not the row's failure, so no attempt, and no topic hold) lasting the
    /// minute is reported as the queue; a partition without a leader as the partition, the topic held between tries; the
    /// producer's own bound and the relay pass's bound each as what they are; a missing delivery report (the producer's sweep)
    /// as the brokers not reached; another timeout (here the test transport's) as a send not answered within its bound.
    /// </summary>
    [Theory]
    [InlineData("queue full", "Local_QueueFull", "this host's producer queue was full")]
    [InlineData("leaderless", "Local_MsgTimedOut", "the topic's partition may have no leader")]
    [InlineData("producer bound", "KafkaProduceTimeoutException", "the producer did not report the send within its bound")]
    [InlineData("pass bound", "OutboxPassTimeoutException", "the relay pass's bound cut the send before the client reported")]
    [InlineData("no report", "Local_TimedOut", "the brokers were not reached or did not take it")]
    [InlineData("broker down", "TimeoutException", "the send was not answered within its bound")]
    public async Task Relay_Should_NameTheStreaksCause_When_ARowFailsForTheMinute(string failure, string code, string hint)
    {
        await ResetAsync();
        await WriteAsync(true, Probe("N", 1));
        var transport = new RecordingTransport();
        switch (failure)
        {
            case "queue full":
                (transport.FailFor, transport.Failure) = (KafkaTopics.AlertStateChanged,
                    () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_QueueFull, "Local: Queue full")));
                break;
            case "producer bound":
                (transport.FailFor, transport.Failure) = (KafkaTopics.AlertStateChanged,
                    () => new KafkaProduceTimeoutException(KafkaTopics.AlertStateChanged, KafkaProduce.SendTimeout));
                break;
            case "pass bound":
                (transport.FailFor, transport.Failure) = (KafkaTopics.AlertStateChanged,
                    () => new OutboxPassTimeoutException("Not sent within what was left of the relay pass's 60 seconds.", new TimeoutException()));
                break;
            case "no report":
                (transport.FailFor, transport.Failure) = (KafkaTopics.AlertStateChanged,
                    () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_TimedOut,
                        "Kafka gave no delivery report for the message; its sender sends it again.")));
                break;
            case "leaderless":
                transport.Leaderless.Add(KafkaTopics.AlertStateChanged);
                break;
            default:
                transport.BrokerDownFor = KafkaTopics.AlertStateChanged;
                break;
        }
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = StreakRelay(transport, log, metrics);

        // Tried again after the broker retry, or when the partition's topic hold ends: past the one-minute streak either way.
        var step = failure == "leaderless" ? OutboxRelay.EventTopicHold + TimeSpan.FromSeconds(1) : BrokerRetry;
        for (var pass = 0; pass < 20 && log.Errors.Count == 0; pass++)
        {
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
            _host.Clock.Advance(step);
        }

        var error = log.Errors.Should().ContainSingle("the streak is reported once").Subject;
        error.Should().Contain($"the last time with {code}: {hint}");
        if (failure != "leaderless")
            error.Should().NotContain("partition", "only the client's message timeout points at a partition");
        reader.Sum(OutboxMetrics.BrokerStreaksName, ("topic", KafkaTopics.AlertStateChanged)).Should().Be(1);
        (await _host.ReadAsync<int>("SELECT attempts FROM outbox_message")).Should().Be(0, "the broker's or the queue's, not the row's (ARV-165)");
    }

    /// <summary>This host's producer's own failure, as the Kafka client and <c>KafkaProduce</c> raise it (ARV-246, ARV-269).</summary>
    private static Func<Exception> ProducersOwn(string failure) => failure switch
    {
        "disposed" => () => new ObjectDisposedException(nameof(KafkaProduce)),
        "destroyed" => () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_Destroy,
            "The producer was replaced after a fatal error; send the message again.")),
        "purged" => () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_PurgeQueue, "Local: Purged in queue")),
        _ => () => new Confluent.Kafka.KafkaException(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_Fatal, "fatal", true))
    };

    /// <summary>
    /// ARV-245: a row the broker keeps refusing (a partition without a leader while the brokers answer: no attempt counts, so
    /// the "stuck after N attempts" Error never fires) warns at each try until its streak reaches
    /// <c>Kafka:Outbox:BrokerStreakMinutes</c> (one minute here), is then logged once as an Error naming its topic and the
    /// streak's length and counted by topic, and is not reported again while it keeps failing. Once sent, the relay no longer
    /// follows it.
    /// </summary>
    [Fact]
    public async Task Relay_Should_ReportARowOnceAsAnErrorAndCountItsTopic_When_TheBrokerRefusesItForTheStreak()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("S", 1));
        var transport = new RecordingTransport { BrokerDown = true };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = StreakRelay(transport, log, metrics);

        // Every broker retry for the 55 seconds before the streak: a warning each time, no Error, nothing counted.
        for (var pass = 0; pass < 12; pass++)
        {
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
            _host.Clock.Advance(BrokerRetry);
        }

        log.Errors.Should().BeEmpty("the row has failed for 55 seconds, short of the one-minute streak");
        log.Warnings.Should().HaveCount(12);
        reader.Sum(OutboxMetrics.BrokerStreaksName).Should().Be(0);

        // At one minute: one Error, one count for the row's topic.
        (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
        log.Errors.Should().ContainSingle().Which.Should().Contain(KafkaTopics.AlertStateChanged)
            .And.Contain("for 1 minutes without a break").And.Contain("Kafka:Outbox:BrokerStreakMinutes 1");
        reader.Sum(OutboxMetrics.BrokerStreaksName, ("topic", KafkaTopics.AlertStateChanged)).Should().Be(1);

        // It keeps failing: warnings again, never a second Error or count for the same row.
        for (var pass = 0; pass < 3; pass++)
        {
            _host.Clock.Advance(BrokerRetry);
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
        }

        log.Errors.Should().ContainSingle("a row is reported once");
        reader.Sum(OutboxMetrics.BrokerStreaksName).Should().Be(1);
        relay.BrokerStreaksFollowed.Should().Be(1);

        // Sent: its streak ends.
        transport.BrokerDown = false;
        _host.Clock.Advance(BrokerRetry);
        (await relay.RunOnceAsync(Ct)).Sent.Should().Be(1);
        relay.BrokerStreaksFollowed.Should().Be(0, "the streak ends when the row is sent");
    }

    /// <summary>
    /// ARV-245: a failure of the row's own (the broker answered it) ends its streak, so the minute starts again at its next
    /// broker failure: 50 seconds refused, its own failure, then 55 seconds refused report nothing; the minute's end does.
    /// </summary>
    [Fact]
    public async Task Relay_Should_StartTheStreakAgain_When_TheRowFailsForAReasonOfItsOwn()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("T", 1));
        var transport = new RecordingTransport { BrokerDown = true };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = StreakRelay(transport, log, metrics);

        for (var pass = 0; pass < 11; pass++)
        {
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
            _host.Clock.Advance(BrokerRetry);
        }

        transport.BrokerDown = false;
        transport.FailOnce = ("T", 1);
        (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1, "the row's own failure");
        relay.BrokerStreaksFollowed.Should().Be(0, "the broker answered the row");
        _host.Clock.Advance(OutboxRelay.Backoff(1) + TimeSpan.FromSeconds(1));

        transport.BrokerDown = true;
        for (var pass = 0; pass < 12; pass++)
        {
            (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
            _host.Clock.Advance(BrokerRetry);
        }

        log.Errors.Should().BeEmpty("the new streak is 55 seconds long, though the row was first refused almost two minutes ago");
        (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1);
        log.Errors.Should().ContainSingle();
        reader.Sum(OutboxMetrics.BrokerStreaksName, ("topic", KafkaTopics.AlertStateChanged)).Should().Be(1);
    }

    /// <summary>
    /// ARV-245: once a pass met the broker's absence, the relay asks the producer's probe before each pass and skips the pass
    /// while the brokers do not answer: nothing is claimed, sent or marked, the wait is logged once, and the backlog is still
    /// measured. When the probe answers again the next pass runs and sends every row
    /// (they are due within the broker retry), the resumption is logged once, and later passes ask no probe.
    /// </summary>
    [Fact]
    public async Task Relay_Should_SkipItsPassesAndStillMeasure_When_TheBrokersDoNotAnswer()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("W", 1), Probe("W", 2), Other("Z", 1));
        var transport = new RecordingTransport { BrokerDown = true, BrokersAnswer = false };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        using var relay = StreakRelay(transport, log, metrics);

        (await relay.RunOnceAsync(Ct)).Failed.Should().Be(1, "the first pass meets the broker's absence");
        transport.Probes.Should().Be(0, "the probe is asked only after a pass met the broker's absence");
        var marked = await _host.ReadAsync<string>("SELECT string_agg(coalesce(next_attempt_on::text, '-') || '/' || coalesce(last_error, '-'), ',' ORDER BY seq) FROM outbox_message");

        _host.Clock.Advance(BrokerRetry);
        for (var pass = 0; pass < 3; pass++)
        {
            var skipped = await relay.RunOnceAsync(Ct);
            skipped.Should().Be(RelayPass.WaitingForBrokers);
            skipped.BrokersAway.Should().BeTrue();
        }

        transport.Probes.Should().Be(3);
        transport.Attempts.Should().Be(1, "no pass ran while the brokers did not answer");
        (await _host.ReadAsync<string>("SELECT string_agg(coalesce(next_attempt_on::text, '-') || '/' || coalesce(last_error, '-'), ',' ORDER BY seq) FROM outbox_message"))
            .Should().Be(marked, "a skipped pass claims and marks nothing");
        log.Warnings.Count(w => w.Contains("do not answer", StringComparison.Ordinal)).Should().Be(1, "the wait is logged once");

        // The backlog is measured meanwhile, by the relay's own loop beside its passes.
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

        reader.Observe(OutboxMetrics.UnsentName).Should().ContainSingle().Which.Value.Should().Be(3);
        transport.Attempts.Should().Be(1, "the running relay skipped its passes too");

        transport.BrokersAnswer = true;
        transport.BrokerDown = false;
        var resumed = await relay.RunOnceAsync(Ct);
        resumed.Sent.Should().Be(3, "every row is due within the broker retry of the brokers' return");
        transport.Sent.Select(Describe).Should().Equal("W" + "1", "W" + "2", "Z" + "1");
        log.Infos.Count(i => i.Contains("answer again", StringComparison.Ordinal)).Should().Be(1);

        // Once the brokers answered, passes no longer ask the probe until one meets their absence again.
        var probes = transport.Probes;
        await WriteAsync(true, Probe("W", 3));
        (await relay.RunOnceAsync(Ct)).Sent.Should().Be(1);
        transport.Probes.Should().Be(probes, "a pass after the brokers answered asks no probe");
    }

    /// <summary>
    /// ARV-264: a topic whose send times out in the client while the brokers answer (a partition without a leader) holds only
    /// itself. The pass asks the producer's probe once, holds the topic for <see cref="OutboxRelay.EventTopicHold"/> (none of
    /// its rows goes meanwhile, so each key keeps its order), counts and logs the hold, and sends the other topic's rows in the
    /// same pass and in the next ones without any time passing. The timed-out row counts no attempt, and its "trying again"
    /// Warning gives way to the hold's. The hold is the topic's (the security review's M1): a row written to it meanwhile under
    /// a key of its own is neither claimed nor tried, where the rows' due times alone let the next pass try it, holding the topic
    /// again at each such row. On the rows, each key's earliest is due at the hold's end and the claim keeps the later ones
    /// behind it (one write a key, the review's L3). Once the leader is back, the topic's rows go when the hold ends, in order.
    /// </summary>
    [Fact]
    public async Task Relay_Should_HoldOnlyTheTopicAndSendTheOthers_When_ASendTimesOutWhileTheBrokersAnswer()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("L", 1), Other("O", 1), Probe("K", 1), Other("O", 2), Probe("L", 2), Other("P", 1));
        var transport = new RecordingTransport { Leaderless = { KafkaTopics.AlertStateChanged } };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = StreakRelay(transport, log, metrics);

        (await relay.RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 6, 3, 1, 2),
            "the topic's first row timed out, its two other rows were skipped, and the other topic's three rows were sent");
        transport.Sent.Select(Describe).Should().Equal("O1", "O2", "P1");
        transport.Probes.Should().Be(1, "the probe tells a partition without a leader from the brokers' absence");
        reader.Sum(OutboxMetrics.TopicHoldsName, ("topic", KafkaTopics.AlertStateChanged), ("kind", OutboxMetrics.PartitionUnavailableHold)).Should().Be(1);
        log.Warnings.Should().Contain(w => w.Contains("held back until", StringComparison.Ordinal) && w.Contains(KafkaTopics.AlertStateChanged, StringComparison.Ordinal));
        log.Warnings.Should().NotContain(w => w.Contains("trying again in", StringComparison.Ordinal), "the held row is tried again when the hold ends, not after the broker retry");
        (await ReadAsync<long>("SELECT count(*) FROM outbox_message WHERE topic = @topic AND attempts = 0 AND next_attempt_on = @at", KafkaTopics.AlertStateChanged,
            (_host.Clock.GetUtcNow() + OutboxRelay.EventTopicHold).UtcDateTime)).Should().Be(2, "each key's earliest row (L1, K1) waits for the hold, L2 behind L1, and no attempt is counted");

        // Without any time passing, the other topic's later rows go; the held topic is neither claimed nor tried, a row written to
        // it meanwhile under a new key included.
        await WriteAsync(true, Probe("N", 1), Other("O", 3));
        (await relay.RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 1, 1, 0, 0), "only the other topic's row is claimed");
        transport.Attempts.Should().Be(5, "the held topic was tried once, by its oldest row");

        // Another relay (another pod, or this one after a restart) claims none of the rows there were: L2 waits behind L1.
        (await Relay(new RecordingTransport()).RunOnceAsync(Ct)).Sent.Should().Be(1, "only the row written after the hold began, under a key of its own");

        // The partition has its leader again: when the hold ends the topic's rows go, in order per key.
        transport.Leaderless.Clear();
        _host.Clock.Advance(OutboxRelay.EventTopicHold + TimeSpan.FromSeconds(1));
        (await relay.RunOnceAsync(Ct)).Sent.Should().Be(3);
        transport.Sent.Select(Describe).Should().Equal("O1", "O2", "P1", "O3", "L1", "K1", "L2");
    }

    /// <summary>
    /// ARV-264, from the security review (L1): two topics without a partition leader. Each try costs the pass the client's
    /// message timeout (here three quarters of a two-second pass, a step of the pass's clock, as 30 of 60 seconds in a host).
    /// The first topic is held; the other's send, a row behind it, is cut at the pass's end, which is not its topic's fault, so
    /// the pass ends there without holding that topic, within its bound. The next pass, once the broker retry has passed, tries
    /// that topic and holds it; with both held, a pass claims neither. So each pass ends within <c>PassSeconds</c>, and each
    /// held topic costs one try a hold.
    /// </summary>
    [Fact]
    public async Task Relay_Should_HoldEachTopicInTurnWithinThePassBound_When_TwoTopicsHaveAPartitionWithoutALeader()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("L", 1), Other("O", 1), Probe("L", 2));
        var stepped = new SteppedClock(_host.Clock);
        var transport = new RecordingTransport
        {
            Leaderless = { KafkaTopics.AlertStateChanged },
            // The other topic's send waits for its partition's leader until the pass's own bound cuts it.
            HangFor = KafkaTopics.FlowNowcast,
            OnSend = m => { if (m.Topic == KafkaTopics.AlertStateChanged) stepped.Step(TimeSpan.FromMilliseconds(1500)); }
        };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        using var relay = new OutboxRelay(_host.Provider.GetRequiredService<DatabaseSettings>(),
            new KafkaSettings { Outbox = new OutboxRelaySettings { BatchSize = 50, PassSeconds = 2 } }, transport, stepped, log, metrics);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var first = await relay.RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        first.Should().Be(new RelayPass(true, 3, 0, 2, 1), "the first topic's row timed out and was held, the other's was cut at the pass's end, the pass ended");
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the cut came at the pass's end: half a second left of it");
        reader.Sum(OutboxMetrics.TopicHoldsName, ("topic", KafkaTopics.AlertStateChanged), ("kind", OutboxMetrics.PartitionUnavailableHold)).Should().Be(1);
        reader.Sum(OutboxMetrics.TopicHoldsName, ("topic", KafkaTopics.FlowNowcast)).Should().Be(0, "a send cut by the pass's own time holds nothing");
        (await _host.ReadAsync<DateTime>("SELECT next_attempt_on FROM outbox_message WHERE message_key = 'O'"))
            .Should().Be((_host.Clock.GetUtcNow() + BrokerRetry).UtcDateTime, "the cut row is due again after the broker retry");

        // The next pass, after the broker retry: the other topic's send now times out in the client, and that topic is held too.
        transport.HangFor = null;
        transport.Leaderless.Add(KafkaTopics.FlowNowcast);
        transport.OnSend = _ => stepped.Step(TimeSpan.FromMilliseconds(1500));
        _host.Clock.Advance(BrokerRetry);
        (await relay.RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 1, 0, 1, 0), "the first topic is still held: only the other's row is claimed");
        reader.Sum(OutboxMetrics.TopicHoldsName, ("topic", KafkaTopics.FlowNowcast), ("kind", OutboxMetrics.PartitionUnavailableHold)).Should().Be(1);

        // Both held: a pass claims and tries neither.
        (await relay.RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 0, 0, 0, 0));
        transport.Attempts.Should().Be(3);
    }

    /// <summary>
    /// ARV-264: the same timeout while the probe says the brokers do not answer is the cluster's absence, not a partition's:
    /// the pass ends at the row as before (the other topic's row waits untouched, no topic is held or counted), and the next
    /// pass is skipped until the brokers answer (ARV-245).
    /// </summary>
    [Fact]
    public async Task Relay_Should_EndThePassAsBefore_When_ASendTimesOutAndTheBrokersDoNotAnswer()
    {
        await ResetAsync();
        await WriteAsync(true, Probe("L", 1), Other("O", 1));
        var transport = new RecordingTransport { Leaderless = { KafkaTopics.AlertStateChanged }, BrokersAnswer = false };
        var log = new RelayLog();
        using var metrics = new OutboxMetrics(timeProvider: _host.Clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var relay = StreakRelay(transport, log, metrics);

        (await relay.RunOnceAsync(Ct)).Should().Be(new RelayPass(true, 2, 0, 1, 1), "the brokers do not answer: the pass ends at the row");
        transport.Probes.Should().Be(1);
        transport.Sent.Should().BeEmpty();
        reader.Sum(OutboxMetrics.TopicHoldsName).Should().Be(0, "no topic was held");
        (await _host.ReadAsync<string>("SELECT coalesce(next_attempt_on::text, '-') FROM outbox_message WHERE message_key = 'O'")).Should().Be("-", "the other topic's row was not touched");
        (await _host.ReadAsync<DateTime>("SELECT next_attempt_on FROM outbox_message WHERE message_key = 'L'"))
            .Should().Be((_host.Clock.GetUtcNow() + BrokerRetry).UtcDateTime, "the row is due again after the broker retry, as before");
        log.Warnings.Should().Contain(w => w.Contains("do not answer", StringComparison.Ordinal));

        _host.Clock.Advance(BrokerRetry);
        (await relay.RunOnceAsync(Ct)).Should().Be(RelayPass.WaitingForBrokers);
    }

    /// <summary>The relay's log by level (ARV-245).</summary>
    private sealed class RelayLog : ILogger<OutboxRelay>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Errors => [.. _entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message)];
        public IReadOnlyList<string> Warnings => [.. _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];
        public IReadOnlyList<string> Infos => [.. _entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message)];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (IsEnabled(logLevel))
                _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

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

    /// <summary>
    /// A transport that keeps <see cref="IOutboxTransport"/>'s defaults (ARV-246): no failure is the broker's, and only a disposed
    /// transport's is the producer's own. Its sends of <see cref="DisposedFor"/> fail as a disposed producer's would.
    /// </summary>
    private sealed class DefaultsTransport : IOutboxTransport
    {
        public string DisposedFor { get; set; }

        /// <summary>The topics of the rows sent.</summary>
        public List<string> Sent { get; } = [];

        public Task SendAsync(OutboxEnvelope message, CancellationToken ct)
        {
            if (string.Equals(message.Topic, DisposedFor, StringComparison.Ordinal))
                throw new ObjectDisposedException(nameof(KafkaProduce));
            Sent.Add(message.Topic);
            return Task.CompletedTask;
        }
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

        /// <summary>A topic the broker does not have (ARV-177): its sends fail as Ariva's producer fails them, at once.</summary>
        public string UnknownTopicFor { get; set; }

        /// <summary>Every send refused by the cluster's authorization: a failure of no topic in particular.</summary>
        public bool ClusterRefused { get; set; }

        /// <summary>
        /// A topic whose sends fail with <see cref="Failure"/>'s exception, thrown as it is: this host's producer's own failures
        /// (ARV-246, ARV-269) or another client failure the relay classifies by its type and code (ARV-270, a full queue).
        /// </summary>
        public string FailFor { get; set; }

        /// <summary>The exception a send to <see cref="FailFor"/> fails with.</summary>
        public Func<Exception> Failure { get; set; }

        /// <summary>A topic whose sends never end and ignore their token (ARV-175: its partition leader is away).</summary>
        public string HangFor { get; set; }

        /// <summary>
        /// The topics with a partition without a leader (ARV-264): their sends fail as librdkafka fails them after its message
        /// timeout, while the probe may still say the brokers answer.
        /// </summary>
        public HashSet<string> Leaderless { get; } = new(StringComparer.Ordinal);

        /// <summary>Called as each send starts (ARV-175: to move a test clock).</summary>
        public Action<OutboxEnvelope> OnSend { get; set; }

        /// <summary>What the producer's probe answers (ARV-245), and how often it was asked.</summary>
        public bool BrokersAnswer { get; set; } = true;

        public int Probes { get; private set; }

        /// <summary>Every send tried, sent or not.</summary>
        public int Attempts { get; private set; }

        public Task<bool> BrokersAnswerAsync(CancellationToken ct)
        {
            Probes++;
            return Task.FromResult(BrokersAnswer);
        }

        /// <summary>The broker's absence, as the Kafka transport classifies it (ARV-165).</summary>
        public bool IsBrokerUnavailable(Exception error) => error is TimeoutException { Message: "Local: Message timed out" } || KafkaProduce.IsBrokerUnavailable(error);

        /// <summary>A topic missing or refused, as the Kafka transport classifies it (ARV-177).</summary>
        public bool IsTopicRefused(Exception error) => KafkaProduce.IsTopicRefused(error);

        /// <summary>The client's message timeout, as the Kafka transport classifies it (ARV-264).</summary>
        public bool IsPartitionUnavailable(Exception error) => KafkaProduce.IsPartitionUnavailable(error);

        /// <summary>This host's producer's own condition, as the Kafka transport classifies it (ARV-246).</summary>
        public bool IsProducerUnavailable(Exception error) => KafkaProduce.IsProducerUnavailable(error);

        /// <summary>A full producer queue, the producer's own bound and a failure's code, as the Kafka transport names them (ARV-270).</summary>
        public bool IsQueueFull(Exception error) => KafkaProduce.IsQueueFull(error);

        public bool IsReportOverdue(Exception error) => KafkaProduce.IsReportOverdue(error);

        public string FailureCode(Exception error) => KafkaProduce.FailureCode(error);

        public Task SendAsync(OutboxEnvelope message, CancellationToken ct)
        {
            Attempts++;
            OnSend?.Invoke(message);
            if (Hang || string.Equals(message.Topic, HangFor, StringComparison.Ordinal))
                return new TaskCompletionSource().Task;
            if (Leaderless.Contains(message.Topic))
                throw new Confluent.Kafka.ProduceException<string, byte[]>(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_MsgTimedOut), null);
            if (BrokerDown || string.Equals(message.Topic, BrokerDownFor, StringComparison.Ordinal))
                throw new TimeoutException("Local: Message timed out");
            if (string.Equals(message.Topic, FailFor, StringComparison.Ordinal))
                throw Failure();
            if (string.Equals(message.Topic, RefusedFor, StringComparison.Ordinal))
                throw new Confluent.Kafka.KafkaException(Confluent.Kafka.ErrorCode.TopicAuthorizationFailed);
            if (string.Equals(message.Topic, UnknownTopicFor, StringComparison.Ordinal))
                throw new Confluent.Kafka.ProduceException<string, byte[]>(new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.Local_UnknownTopic), null);
            if (ClusterRefused)
                throw new Confluent.Kafka.KafkaException(Confluent.Kafka.ErrorCode.ClusterAuthorizationFailed);
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
