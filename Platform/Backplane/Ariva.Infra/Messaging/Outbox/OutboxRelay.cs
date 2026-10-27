using System.Data;
using System.Text.Json;
using Ariva.Core.Messaging;
using Ariva.Infra.Settings;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ariva.Infra.Messaging.Outbox;

/// <summary>One outbox row as the relay hands it to the transport.</summary>
public sealed record OutboxEnvelope(long Seq, Guid Id, string Topic, string Key, string MessageType, string Payload, IReadOnlyDictionary<string, string> Headers, int Attempts = 0);

/// <summary>
/// The row names an event type this build does not know (a rolling deploy, or a rollback across a rename). Not the row's
/// fault and not the broker's: the key waits for a build that knows the type, without counting attempts.
/// </summary>
public sealed class UnknownEventTypeException(string messageType)
    : InvalidOperationException($"Unknown event type '{messageType}' in the outbox; a build that knows it will send the row.");

/// <summary>Produces one outbox row to Kafka. Throws when the broker did not take it.</summary>
public interface IOutboxTransport
{
    /// <summary>
    /// False until the producer can send (the host's producer started and not in the gap after a fatal client error,
    /// ARV-167); the relay waits instead of failing rows.
    /// </summary>
    bool IsReady => true;

    Task SendAsync(OutboxEnvelope message, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="error"/> from <see cref="SendAsync"/> says the broker is unavailable rather than that the row
    /// is wrong (ARV-165): such a failure does not count against the row (no attempt, no growing backoff).
    /// </summary>
    bool IsBrokerUnavailable(Exception error) => false;

    /// <summary>
    /// Whether <paramref name="error"/> from <see cref="SendAsync"/> says the row's topic is missing or refused to this
    /// principal (ARV-177): the row's own failure, which every row of that topic would meet in the same way, so the relay
    /// holds the topic until the row's backoff ends and the other topics carry on.
    /// </summary>
    bool IsTopicRefused(Exception error) => false;

    /// <summary>
    /// Whether <paramref name="error"/> from <see cref="SendAsync"/> says the row was not delivered within the client's message
    /// timeout (ARV-264): a partition of its topic without a leader, or no broker at all, which the producer's probe
    /// (<see cref="BrokersAnswerAsync"/>) tells apart. While the brokers answer, the relay holds that topic alone.
    /// </summary>
    bool IsPartitionUnavailable(Exception error) => false;

    /// <summary>
    /// Whether <paramref name="error"/> from <see cref="SendAsync"/> is this host's producer's own condition (ARV-246): disposed,
    /// <c>Local_Destroy</c> (stopping, or retired after a fatal error), a fatal client error, or a message the client purged at
    /// that error. The failure is the producer's, not the topic's, so holding the topic would cure nothing: a dead letter's such
    /// failure holds no topic, and the pass ends as for an event's.
    /// </summary>
    bool IsProducerUnavailable(Exception error) => error is ObjectDisposedException;

    /// <summary>
    /// Whether the brokers answer now (ARV-245): the producer's cached probe (<c>KafkaProduce.BrokersAnswerAsync</c>), which
    /// asks the cluster at most once a second and never throws. After a pass met the broker's absence the relay asks this
    /// before each pass and skips the pass while it is false.
    /// </summary>
    Task<bool> BrokersAnswerAsync(CancellationToken ct) => Task.FromResult(true);
}

/// <summary>A send that did not end within what was left of the relay pass's time (ARV-166): the broker is slow or away.</summary>
public sealed class OutboxPassTimeoutException(string message, Exception inner) : TimeoutException(message, inner);

/// <summary>What one relay pass did; <paramref name="BrokersAway"/> when it was skipped because the brokers did not answer (ARV-245).</summary>
public sealed record RelayPass(bool Leader, int Claimed, int Sent, int Failed, int HeldBack, bool BrokersAway = false)
{
    public static readonly RelayPass NotLeader = new(false, 0, 0, 0, 0);

    public static readonly RelayPass NotReady = new(false, 0, 0, 0, 0);

    public static readonly RelayPass WaitingForBrokers = new(false, 0, 0, 0, 0, BrokersAway: true);
}

/// <summary>
/// The outbox relay (ADR-0018). Every pass runs in one PostgreSQL transaction: it takes the relay's advisory lock
/// (one leader across every pod and host; the others skip the pass), claims unsent rows in <c>seq</c> order with
/// <c>FOR UPDATE SKIP LOCKED</c>, and produces them one by one. When a row fails, its key is held back for the rest of
/// the pass and until the row's retry time, so later events for that key never overtake it; other keys carry on.
/// A broker failure ends the pass (a dead-letter row's holds back its topic instead, ARV-175, below). Sent rows are marked in the same transaction, so a crash between produce and commit sends those rows again
/// (at-least-once; consumers deduplicate in the inbox). The leader also removes old sent rows and inbox entries, keeping the
/// newest row of each key on a compacted topic (ARV-126).
/// <para>
/// ARV-166: a pass never holds its transaction, its leader lock and its claimed rows for longer than
/// <see cref="OutboxRelaySettings.PassSeconds"/> of producing. Every send is awaited for at most what is left of that
/// time, whatever the transport does (a send that ignores its token included), and one that runs out counts as a broker
/// failure: its key is held back, the pass ends and commits, and a later pass sends the row again. A pass that has used its
/// time sends nothing more; the rows not reached wait untouched.
/// </para>
/// <para>
/// ARV-165: a failure that says the broker or the producer is unavailable (the transport's
/// <see cref="IOutboxTransport.IsBrokerUnavailable"/>, or the pass's own bound, <see cref="OutboxPassTimeoutException"/>)
/// does not count against the row: no attempt, no growing backoff, the row is due again after
/// <see cref="OutboxRelaySettings.BrokerRetrySeconds"/>. So after an outage every key resumes within that wait of the
/// broker's return, whatever the outage's length. Any other failure counts and backs off exponentially, up to five minutes.
/// An event row whose topic is missing or refused to this principal (<see cref="IOutboxTransport.IsTopicRefused"/>, ARV-177)
/// counts as well, and its topic is held until that row's backoff ends (kept on the topic's unsent rows, as a dead-letter
/// topic's hold is), so the topic's rows cannot fill a pass's batch meanwhile and the other topics carry on.
/// </para>
/// <para>
/// ARV-175: dead letters never hold events up for long. A pass claims up to a batch of event rows and then up to a batch of
/// dead-letter rows (<c>.dlq.v1</c> topics), each claim in <c>seq</c> order, and sends the events first, so a backlog of dead
/// letters neither crowds events out of the batch nor goes before them (rows of different topics are independent, so no key's
/// order changes). The dead letters get at most <see cref="DeadLetterPassBound"/> of the pass: each send is bounded by what is
/// left of it, and when it is used (or a send was cut at the share's end, ARV-164b) the rest wait untouched for the next pass. A dead-letter row that fails for a reason that is
/// not its own (the broker unavailable, the dead letters' share, or any failure that is not the row's) holds back its topic for
/// <see cref="DeadLetterTopicHold"/>, as every topic hold does (ARV-264, below): the relay claims none of the topic's rows until
/// the hold's end, and each key's earliest unsent row is due no earlier, so no relay claims the rows there were meanwhile,
/// whichever leads, also after a restart. The other dead-letter topics carry on. So a dead-letter topic that cannot be reached
/// delays the events written meanwhile by at most <see cref="DeadLetterPassBound"/> once per <see cref="DeadLetterTopicHold"/>,
/// and once more on another relay for a letter kept after the hold began (at a host's stop). A dead-letter send cut short by the
/// pass's own time is not the topic's fault: the pass ends without holding it, and so does a failure of this host's own
/// producer (<see cref="IOutboxTransport.IsProducerUnavailable"/>: a fatal client error, a message purged at it,
/// <c>Local_Destroy</c>, disposed; ARV-246), which a hold would not cure. An event row's broker failure ends the pass, unless its
/// topic is held for a partition without a leader (ARV-264).
/// </para>
/// <para>
/// ARV-246: the relay passes again at once only after a full batch with a row sent and no failure (more rows wait); a pass that
/// sent nothing, however full its batch (rows of an event type this build does not know, held back without a failure), waits
/// <see cref="OutboxRelaySettings.PollMilliseconds"/> like any other, so an older leader does not run back-to-back
/// transactions on rows it cannot send and keep the leader lock from the pods that can.
/// </para>
/// <para>
/// ARV-245: a row the broker keeps refusing for <see cref="OutboxRelaySettings.BrokerStreakMinutes"/> without a break (a
/// partition with no leader while the brokers answer) is logged once as an Error and counted (<see cref="OutboxBrokerStreaks"/>,
/// <see cref="OutboxMetrics.BrokerStreaksName"/>). And once a pass has met the broker's absence the relay asks the producer's
/// cached probe (<see cref="IOutboxTransport.BrokersAnswerAsync"/>) before each pass, and skips the pass while the brokers do
/// not answer: no connection, no lock, no claim, so a full outage no longer holds a pass for its produce bound at each poll.
/// The backlog is still measured. Once the probe answers, passes resume, and every row is due within the broker retry. While
/// passes are skipped no streak grows; a row still refused once they resume is reported then if its streak has passed.
/// </para>
/// <para>
/// ARV-264: an event row whose send timed out in the client (<see cref="IOutboxTransport.IsPartitionUnavailable"/>) while the
/// producer's probe says the brokers answer is a partition of its topic without a leader, not the cluster's absence: the relay
/// holds that topic alone for <see cref="EventTopicHold"/> and the pass carries on with the other topics, so one leaderless
/// partition no longer starts and ends every pass. A hold (this one, a missing topic's and a dead-letter topic's) applies to
/// the topic: this relay claims none of its rows until the hold ends, a row written meanwhile under a key of its own included,
/// and each key's earliest unsent row is due no earlier than the end, so another relay (another pod, or this one after a
/// restart) claims none of the rows there were and tries the topic once more at most, for a row written after the hold began.
/// The relay cannot tell which partition a row goes to, so every row of the topic waits for as long as its partition has no
/// leader, on every partition, and each key keeps its order. The topic's oldest row tries it again when the hold ends, which
/// costs a pass up to the client's message timeout (30 seconds, reported within about 40) once per hold, so about once every
/// 90 seconds for each such topic: with two of them the relay spends about two thirds of its time in those tries and with
/// three or more nearly all of it, the other topics' rows going between them, and every pass still produces for at most
/// <c>Kafka:Outbox:PassSeconds</c>, its probe of the brokers adding at most 4 seconds (a second such try cut by the pass's
/// end holds nothing: the pass ends there, and the next one tries that topic and holds it). The oldest row's broker streak (ARV-245) grows at each of its tries, so it is reported
/// once it has lasted. When the probe says the brokers do not answer, the pass ends as before and the next ones are skipped.
/// Every topic hold is counted by its kind (<see cref="OutboxMetrics.TopicHoldsName"/>) and logged. A <c>PassSeconds</c>
/// below <see cref="OutboxRelaySettings.MinPassSecondsForTopicHolds"/> leaves no room for the client's timeout, so no such
/// hold is taken (the pass ends at the row, as before); the relay warns at start.
/// </para>
/// <para>
/// ARV-125a: every relay, leader or not, measures the backlog (<see cref="MeasureAsync"/>) every <see cref="MeasureEvery"/>
/// on a short connection of its own, also while the producer is not ready, so a Kafka outage shows as a growing backlog
/// (<see cref="OutboxMetrics"/>). Since ARV-179 it does so on a loop of its own, beside the passes: a slow count in a large
/// backlog (after an outage, when draining matters) never delays a pass, where before it held each pass up to its 10 second
/// bound once every 30 seconds. The relay's stop waits for a measurement at most <see cref="MeasureStopBound"/>, whatever the
/// measurement does (one that does not take its token included), so it never eats into the time the host's later services have to stop.
/// </para>
/// </summary>
public sealed class OutboxRelay(
    DatabaseSettings database,
    KafkaSettings kafka,
    IOutboxTransport transport,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger,
    OutboxMetrics metrics = null) : BackgroundService
{
    /// <summary>"ARIV" and the story number, like the administrators lock of ARV-011.</summary>
    public const long LeaderLock = 0x41524956_00000020;

    /// <summary>How often the backlog is measured (ARV-125a); a measurement is reported for three of these.</summary>
    public static readonly TimeSpan MeasureEvery = TimeSpan.FromSeconds(30);

    /// <summary>The most the relay's stop waits for a measurement in hand (ARV-179).</summary>
    public static readonly TimeSpan MeasureStopBound = TimeSpan.FromSeconds(5);

    /// <summary>How the backlog is measured: <see cref="MeasureAsync"/>, or a test's own (ARV-179).</summary>
    internal Func<CancellationToken, Task<OutboxBacklog>> MeasureBacklog { get; init; }

    /// <summary>How often the backlog is measured: <see cref="MeasureEvery"/>, or a test's own (ARV-179).</summary>
    internal TimeSpan MeasureInterval { get; init; } = MeasureEvery;

    /// <summary>The most of a pass spent on dead letters (ARV-175): events written meanwhile wait at most this much more.</summary>
    public static readonly TimeSpan DeadLetterPassBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a dead-letter topic is held back after a failure that is not its row's own (ARV-175), kept on the topic's
    /// unsent rows so that every relay honours it.
    /// </summary>
    public static readonly TimeSpan DeadLetterTopicHold = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an event topic is held back after a send to it timed out in the client while the brokers answered (ARV-264).
    /// Its oldest row tries it again when the hold ends, so a partition that stays without a leader costs a pass one client
    /// message timeout per hold; every row of the topic waits while that lasts (the relay does not know a row's partition).
    /// </summary>
    public static readonly TimeSpan EventTopicHold = TimeSpan.FromMinutes(1);

    private const int MaxErrorLength = 2000;
    private static readonly TimeSpan HousekeepingEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _lastHousekeeping = DateTimeOffset.MinValue;
    private readonly OutboxBrokerStreaks _streaks = new(TimeSpan.FromMinutes(kafka.Outbox.BrokerStreakMinutes));

    /// <summary>Set when a pass met the broker's absence (ARV-245): the next pass asks the brokers first.</summary>
    private bool _brokersAway;

    /// <summary>
    /// The topics this relay holds back and until when (ARV-264, from its security review): a claim skips them, so a row written
    /// to a held topic under a key of its own is not tried either, which the rows' due times alone did not ensure. One entry a
    /// topic the outbox holds rows for, each dropped once its hold has ended. Set before the pass commits and kept if it rolls
    /// back, which only makes this relay wait for the topic a little longer (at most the hold), never sends out of order.
    /// </summary>
    private readonly Dictionary<string, DateTime> _topicHolds = new(StringComparer.Ordinal);

    /// <summary>Set while passes are skipped for the brokers, so the wait and the resumption are each logged once.</summary>
    private bool _waitingForBrokers;

    /// <summary>The rows whose broker-unavailable streak the relay follows now (ARV-245; for the tests).</summary>
    internal int BrokerStreaksFollowed => _streaks.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = kafka.Outbox;
        logger.LogInformation("Outbox relay started: batch {BatchSize}, poll {PollMilliseconds} ms", settings.BatchSize, settings.PollMilliseconds);
        if (settings.PassSeconds < OutboxRelaySettings.MinPassSecondsForTopicHolds)
            logger.LogWarning("Kafka:Outbox:PassSeconds {PassSeconds} is below {Minimum}: the pass ends before the producer reports a send that waited for a partition without a leader, so such a topic ends each pass at its row instead of being held (ARV-264)",
                settings.PassSeconds, OutboxRelaySettings.MinPassSecondsForTopicHolds);
        // ARV-179: on a task of its own, so that neither its first connection nor a slow count runs on the passes' path.
        var measuring = metrics is null ? Task.CompletedTask : Task.Run(() => MeasureLoopAsync(stoppingToken), CancellationToken.None);
        try
        {
            await RelayLoopAsync(settings, stoppingToken);
        }
        finally
        {
            await measuring.WaitAsync(MeasureStopBound, timeProvider, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RelayLoopAsync(OutboxRelaySettings settings, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(settings.PollMilliseconds);
            try
            {
                var pass = await RunOnceAsync(stoppingToken);
                // A full batch with no failure means more rows wait, unless none of it was sent (ARV-246): rows of an event type
                // this build does not know (a rolling deploy's older leader) are held back without a failure, and passing again
                // at once would only claim them again, holding the leader lock against the pods that know them.
                if (pass.Leader && pass.Claimed >= settings.BatchSize && pass.Failed == 0 && pass.Sent > 0)
                    delay = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // The relay must outlive a database restart or a bad row; stopping it would strand every event.
                logger.LogError(e, "Outbox relay pass failed; retrying in 5 seconds");
                delay = TimeSpan.FromSeconds(5);
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Measures the backlog at once and then every <see cref="MeasureInterval"/> until the host stops (ARV-179); a failure leaves
    /// the last measurement to expire. A measurement that outlasts the interval delays only the next one (the timer does not
    /// queue ticks).
    /// </summary>
    private async Task MeasureLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(MeasureInterval, timeProvider);
        do
        {
            try
            {
                metrics.Report(await (MeasureBacklog ?? MeasureAsync)(ct), MeasureInterval * 3);
            }
#pragma warning disable CA1031 // a failed measurement must not stop the measuring; the relay's pass meets the same cause and logs it
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
            {
                // The database is away: the backlog is not reported until it is measured again.
                logger.LogDebug(e, "Outbox backlog not measured");
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        while (await NextTickAsync(timer, ct));
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The rows not yet sent and when the oldest was written (ARV-125a): one constant statement on the partial index of
    /// unsent rows (script 0011, <c>ix_outbox_message_unsent</c>), with a 10 second bound. Public for the integration tests.
    /// </summary>
    public async Task<OutboxBacklog> MeasureAsync(CancellationToken ct)
    {
        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM outbox_message WHERE sent_on IS NULL),
                   (SELECT created_on FROM outbox_message WHERE sent_on IS NULL ORDER BY seq LIMIT 1)
            """,
            connection) { CommandTimeout = 10 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new OutboxBacklog(reader.GetInt64(0), reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
    }

    /// <summary>One pass: lead, claim, produce, mark. Public for the integration tests.</summary>
    public async Task<RelayPass> RunOnceAsync(CancellationToken ct)
    {
        if (!transport.IsReady)
            return RelayPass.NotReady;
        if (_brokersAway && !await BrokersAnswerAsync(ct))
            return RelayPass.WaitingForBrokers;

        var passTime = TimeSpan.FromSeconds(Math.Max(1, kafka.Outbox.PassSeconds));
        var passStarted = timeProvider.GetTimestamp();
        await using var connection = PostgresConnections.Create(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        if (!await TryLeadAsync(connection, transaction, ct))
        {
            await transaction.RollbackAsync(ct);
            return RelayPass.NotLeader;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var topicsHeld = TopicsHeldAt(now);
        // ARV-175: a batch of events, then a batch of dead letters (a held topic's rows are not claimed); events are sent first.
        List<OutboxEnvelope> rows =
        [
            .. await ClaimAsync(connection, transaction, now, kafka.Outbox.BatchSize, deadLetters: false, topicsHeld, ct),
            .. await ClaimAsync(connection, transaction, now, kafka.Outbox.BatchSize, deadLetters: true, topicsHeld, ct),
        ];
        var held = new HashSet<(string Topic, string Key)>();
        var heldTopics = new HashSet<string>(StringComparer.Ordinal);
        var sent = new List<long>();
        int failed = 0, heldBack = 0;
        long? deadLettersStarted = null;
        // Set once a dead letter's send has run to the end of the dead letters' share (ARV-164b): the share is spent then,
        // whatever the time stamps read a moment later. The send's bound is a timer, which can fire a few milliseconds before
        // the time stamps show the share's end, and the next letter would be sent with those milliseconds.
        var deadLettersSpent = false;

        foreach (var row in rows)
        {
            var deadLetter = IsDeadLetterTopic(row.Topic);
            if (held.Contains((row.Topic, row.Key)) || heldTopics.Contains(row.Topic))
            {
                heldBack++;
                continue;
            }

            var left = passTime - timeProvider.GetElapsedTime(passStarted);
            var shareBound = false;
            if (deadLetter)
            {
                deadLettersStarted ??= timeProvider.GetTimestamp();
                var deadLettersLeft = deadLettersSpent ? TimeSpan.Zero : DeadLetterPassBound - timeProvider.GetElapsedTime(deadLettersStarted.Value);
                if (deadLettersLeft < left)
                    (left, shareBound) = (deadLettersLeft, true);
            }

            if (left <= TimeSpan.Zero)
            {
                // The pass (or its dead letters' share) has used its time: the rest wait, untouched, for the next pass.
                heldBack += rows.Count - rows.IndexOf(row);
                break;
            }

            try
            {
                await SendWithinAsync(row, left, shareBound ? DeadLetterBound : PassBound, ct);
                sent.Add(row.Seq);
                _streaks.Ended(row.Seq);
            }
            catch (UnknownEventTypeException e)
            {
                heldBack++;
                held.Add((row.Topic, row.Key));
                await MarkUnreadableAsync(connection, transaction, row.Seq, e, ct);
                logger.LogWarning("Outbox row {Seq} names {MessageType}, which this build does not know; key held back", row.Seq, row.MessageType);
            }
            catch (Exception e) when ((e is not OperationCanceledException || !ct.IsCancellationRequested) && (e is OutboxPassTimeoutException || transport.IsBrokerUnavailable(e)))
            {
                // ARV-165: the broker is away (or slow), not the row: no attempt is counted and the row is tried again after a
                // short fixed wait, so once the broker is back every key resumes within that wait, however long the outage.
                failed++;
                held.Add((row.Topic, row.Key));
                _brokersAway = true;
                await MarkBrokerUnavailableAsync(connection, transaction, row.Seq, now, BrokerRetry, e, ct);
                // A send cut short by the pass's own time is not its topic's fault: the pass ends, as for an event (ARV-175).
                var passTimeUsed = e is OutboxPassTimeoutException && !shareBound;
                deadLettersSpent |= e is OutboxPassTimeoutException && shareBound;
                // ARV-246: this host's producer failing (a fatal client error, a message it purged at that error, Local_Destroy
                // when it is retired, disposed) is not the topic's either: a dead letter's ends the pass as an event's does, and
                // holds the topic for no relay.
                var producersOwn = transport.IsProducerUnavailable(e);
                // ARV-264: an event row timed out in the client while the brokers answer: a partition of its topic has no
                // leader. Only that topic waits, so the other topics are not held behind it at every pass.
                var partitionHeld = !passTimeUsed && !deadLetter && transport.IsPartitionUnavailable(e) && await BrokersAnswerAsync(ct);
                ReportBrokerUnavailable(row, e, now, topicHeld: partitionHeld || (!passTimeUsed && !producersOwn && deadLetter));
                if (!passTimeUsed && !producersOwn && await HoldTopicAsync(connection, transaction, row, heldTopics, ct))
                    continue;
                if (partitionHeld)
                {
                    var until = (timeProvider.GetUtcNow() + EventTopicHold).UtcDateTime;
                    await HoldTopicUntilAsync(connection, transaction, row.Topic, until, heldTopics, ct);
                    metrics?.TopicHeld(row.Topic, OutboxMetrics.PartitionUnavailableHold);
                    logger.LogWarning("Topic {Topic} held back until {Until:O}: row {Seq} ({MessageType} {EventId}) timed out while the brokers answer (a partition of the topic may have no leader); the other topics carry on, and the topic's oldest row tries it again then",
                        row.Topic, until, row.Seq, row.MessageType, row.Id);
                    continue;
                }

                heldBack += rows.Count - rows.IndexOf(row) - 1;
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                held.Add((row.Topic, row.Key));
                // The broker answered this row, so a streak of its absence ends (ARV-245).
                _streaks.Ended(row.Seq);
                await MarkFailedAsync(connection, transaction, row.Seq, now, e, ct);
                if (row.Attempts + 1 >= kafka.Outbox.StuckAfterAttempts)
                    logger.LogError(e, "Outbox row {Seq} ({MessageType} {EventId} to {Topic}) stuck after {Attempts} attempts; key held back", row.Seq, row.MessageType, row.Id, row.Topic, row.Attempts + 1);
                else
                    logger.LogWarning(e, "Outbox row {Seq} ({MessageType} {EventId} to {Topic}) not sent; key held back", row.Seq, row.MessageType, row.Id, row.Topic);

                // ARV-177: an event's topic missing or refused to this principal fails every row of it the same way (at once
                // after the first). The topic is held until this row's backoff ends, on its unsent rows as well, so no claim
                // fills its batch with them meanwhile and the other topics carry on; this row alone tries the topic again, as
                // its attempts and backoff grow (at most five minutes). A dead letter's keeps ARV-175's topic hold below.
                if (!deadLetter && transport.IsTopicRefused(e))
                {
                    var until = now + Backoff(row.Attempts + 1);
                    await HoldTopicUntilAsync(connection, transaction, row.Topic, until, heldTopics, ct);
                    metrics?.TopicHeld(row.Topic, OutboxMetrics.TopicRefusedHold);
                    logger.LogWarning("Topic {Topic} is missing or refused to this principal; its rows wait until {Until:O} (the failed row's backoff) while the other topics carry on",
                        row.Topic, until);
                    continue;
                }

                // A row that cannot be read (unknown type, bad payload) only holds back its key. Anything else is the
                // broker or the network (a send past the pass's time included, and a transport already disposed): the rest
                // of the batch would fail the same way after the produce timeout, so the pass ends here and the remaining
                // rows wait untouched for the next one; a dead letter's holds back only its topic (ARV-175), unless the failure is
                // this host's producer's own (ARV-246: a disposed transport), which ends the pass and holds nothing.
                if (e is not (InvalidOperationException or JsonException) || e is ObjectDisposedException)
                {
                    if (!transport.IsProducerUnavailable(e) && await HoldTopicAsync(connection, transaction, row, heldTopics, ct))
                        continue;
                    heldBack += rows.Count - rows.IndexOf(row) - 1;
                    break;
                }
            }
        }

        if (sent.Count > 0)
            await MarkSentAsync(connection, transaction, sent, now, ct);
        await HousekeepAsync(connection, transaction, now, ct);
        await transaction.CommitAsync(ct);

        if (rows.Count > 0)
            logger.LogDebug("Outbox relay pass: {Claimed} claimed, {Sent} sent, {Failed} failed, {HeldBack} held back", rows.Count, sent.Count, failed, heldBack);
        return new RelayPass(true, rows.Count, sent.Count, failed, heldBack);
    }

    /// <summary>
    /// Whether the brokers answer (ARV-245), after a pass met their absence: the transport's cached probe. Logs the wait once
    /// when it starts and once when the brokers answer again; a probe that fails or does not answer reads as false.
    /// </summary>
    private async Task<bool> BrokersAnswerAsync(CancellationToken ct)
    {
        bool answered;
        try
        {
            answered = await transport.BrokersAnswerAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(e, "Kafka broker probe failed");
            answered = false;
        }

        if (!answered)
        {
            if (!_waitingForBrokers)
                logger.LogWarning("Kafka brokers do not answer: the outbox relay skips its passes until they do (the backlog is still measured)");
            _waitingForBrokers = true;
            return false;
        }

        if (_waitingForBrokers)
            logger.LogInformation("Kafka brokers answer again: the outbox relay resumes its passes");
        _waitingForBrokers = false;
        _brokersAway = false;
        return true;
    }

    /// <summary>
    /// Logs a row the broker did not take (ARV-165) and, once, a row whose streak has lasted <c>Kafka:Outbox:BrokerStreakMinutes</c>
    /// as an Error, counted by its topic (ARV-245: no attempt counts against such a row, so this is what raises a partition that
    /// stays without a leader while the brokers answer). A row whose topic is held now (ARV-175, ARV-264) is not tried again after
    /// the broker retry but when the hold ends, which the hold's own Warning says, so it adds no Warning of its own.
    /// </summary>
    private void ReportBrokerUnavailable(OutboxEnvelope row, Exception e, DateTime now, bool topicHeld)
    {
        if (_streaks.BrokerUnavailable(row.Seq, now) is { } streak)
        {
            if (topicHeld)
                logger.LogError(e, "Outbox row {Seq} ({MessageType} {EventId} to {Topic}) has failed with the broker unavailable for {Minutes:F0} minutes without a break (Kafka:Outbox:BrokerStreakMinutes {StreakMinutes}); the topic's partition may have no leader. Its topic is held back and the row tries it again when the hold ends",
                    row.Seq, row.MessageType, row.Id, row.Topic, streak.TotalMinutes, kafka.Outbox.BrokerStreakMinutes);
            else
                logger.LogError(e, "Outbox row {Seq} ({MessageType} {EventId} to {Topic}) has failed with the broker unavailable for {Minutes:F0} minutes without a break (Kafka:Outbox:BrokerStreakMinutes {StreakMinutes}); the topic's partition may have no leader. Trying again every {Seconds} s",
                    row.Seq, row.MessageType, row.Id, row.Topic, streak.TotalMinutes, kafka.Outbox.BrokerStreakMinutes, BrokerRetry.TotalSeconds);
            metrics?.BrokerStreak(row.Topic);
        }
        else if (!topicHeld)
        {
            logger.LogWarning(e, "Outbox row {Seq} ({MessageType} {EventId} to {Topic}) not sent: the broker is unavailable; trying again in {Seconds} s",
                row.Seq, row.MessageType, row.Id, row.Topic, BrokerRetry.TotalSeconds);
        }
    }

    /// <summary>The topics held at <paramref name="now"/> (ARV-264); the holds that have ended are dropped.</summary>
    private string[] TopicsHeldAt(DateTime now)
    {
        foreach (var ended in _topicHolds.Where(hold => hold.Value <= now).Select(hold => hold.Key).ToList())
            _topicHolds.Remove(ended);
        return [.. _topicHolds.Keys];
    }

    /// <summary>A dead-letter topic (<c>.dlq.v1</c>, ARV-175).</summary>
    internal static bool IsDeadLetterTopic(string topic) => topic is not null && topic.EndsWith(KafkaTopics.DeadLetterSuffix, StringComparison.Ordinal);

    /// <summary>
    /// After a failure that is not the row's own: a dead-letter row holds back its topic and the pass goes on (ARV-175); an
    /// event row's failure ends the pass unless its topic is held for a partition without a leader (ARV-264). The topic is
    /// held for <see cref="DeadLetterTopicHold"/> from now (<see cref="HoldTopicUntilAsync"/>). Returns whether the pass goes on.
    /// </summary>
    private async Task<bool> HoldTopicAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, OutboxEnvelope row, HashSet<string> heldTopics,
        CancellationToken ct)
    {
        if (!IsDeadLetterTopic(row.Topic))
            return false;
        await HoldTopicUntilAsync(connection, transaction, row.Topic, (timeProvider.GetUtcNow() + DeadLetterTopicHold).UtcDateTime, heldTopics, ct);
        metrics?.TopicHeld(row.Topic, OutboxMetrics.DeadLetterHold);
        logger.LogWarning("Dead-letter topic {Topic} held back for {Seconds} s after a failure that is not its letter's own; events and the other topics carry on",
            row.Topic, DeadLetterTopicHold.TotalSeconds);
        return true;
    }

    /// <summary>
    /// Holds <paramref name="topic"/> until <paramref name="until"/>: skipped for the rest of the pass, and claimed by none of
    /// this relay's passes until then (ARV-264), a row written meanwhile included; and each key's earliest unsent row is due no
    /// earlier (one constant statement, in the pass's transaction), so another relay claims none of the rows there were either:
    /// the claim leaves every later row of a key behind its earliest one. One write a key, not a row (ARV-264, from its security
    /// review: the statement runs at each hold, and a held topic's backlog grows).
    /// </summary>
    private async Task HoldTopicUntilAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string topic, DateTime until,
        HashSet<string> heldTopics, CancellationToken ct)
    {
        heldTopics.Add(topic);
        _topicHolds[topic] = _topicHolds.TryGetValue(topic, out var holding) && holding > until ? holding : until;
        await using var command = new NpgsqlCommand(
            """
            UPDATE outbox_message o
               SET next_attempt_on = GREATEST(COALESCE(o.next_attempt_on, @until), @until)
             WHERE o.topic = @topic AND o.sent_on IS NULL
               AND NOT EXISTS (SELECT 1 FROM outbox_message e
                                WHERE e.sent_on IS NULL AND e.topic = o.topic AND e.message_key = o.message_key AND e.seq < o.seq)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("until", until);
        command.Parameters.AddWithValue("topic", topic);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The bound a send ran out of, for the row's error: the pass's time.</summary>
    private string PassBound => $"what was left of the relay pass's {kafka.Outbox.PassSeconds} seconds";

    /// <summary>The bound a send ran out of, for the row's error: the dead letters' share of the pass (ARV-175).</summary>
    private static string DeadLetterBound => $"what was left of the {DeadLetterPassBound.TotalSeconds} seconds a relay pass gives dead letters";

    /// <summary>
    /// Sends one row, waiting at most <paramref name="left"/>: the transport gets a token that cancels then, and the wait
    /// ends on the same token even if the transport ignores it (ARV-166), as a <see cref="TimeoutException"/> that ends the
    /// pass (or, for a dead letter, holds back its topic). One timer decides both, so the row's error always names the bound,
    /// <paramref name="bound"/>. The transport's own failures, <c>KafkaProduce</c>'s 45 second bound included, pass through as
    /// they are.
    /// </summary>
    private async Task SendWithinAsync(OutboxEnvelope row, TimeSpan left, string bound, CancellationToken ct)
    {
        using var within = new CancellationTokenSource(left, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, within.Token);
        try
        {
            await transport.SendAsync(row, linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested && within.IsCancellationRequested)
        {
            throw new OutboxPassTimeoutException($"Outbox row {row.Seq} was not sent within {bound}.", e);
        }
    }

    /// <summary>The wait before a row is tried again after the broker was unavailable (ARV-165), <c>Kafka:Outbox:BrokerRetrySeconds</c>.</summary>
    private TimeSpan BrokerRetry => TimeSpan.FromSeconds(kafka.Outbox.BrokerRetrySeconds);

    /// <summary>Retry delay after the given number of failed attempts: 2, 4, 8 ... seconds, at most five minutes.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Clamp(attempts, 1, 9))));

    private static async Task<bool> TryLeadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction);
        command.Parameters.AddWithValue("key", LeaderLock);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Claims up to <paramref name="batch"/> due rows in <c>seq</c> order: event rows, or dead-letter rows (ARV-175), none of a
    /// topic this relay holds (ARV-264). One constant statement; the kind and the held topics are parameters.
    /// </summary>
    private static async Task<List<OutboxEnvelope>> ClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime now, int batch,
        bool deadLetters, string[] heldTopics, CancellationToken ct)
    {
        // A row waits while an earlier row of its key is waiting for its retry time, so order per key holds across passes.
        await using var command = new NpgsqlCommand(
            """
            SELECT o.seq, o.id, o.topic, o.message_key, o.message_type, o.payload::text, o.headers::text, o.attempts
              FROM outbox_message o
             WHERE o.sent_on IS NULL
               AND (o.next_attempt_on IS NULL OR o.next_attempt_on <= @now)
               AND (o.topic LIKE @deadLetterTopics) = @deadLetters
               AND NOT (o.topic = ANY(@heldTopics))
               AND NOT EXISTS (SELECT 1 FROM outbox_message e
                                WHERE e.sent_on IS NULL AND e.topic = o.topic AND e.message_key = o.message_key
                                  AND e.seq < o.seq AND e.next_attempt_on > @now)
             ORDER BY o.seq
             LIMIT @batch
               FOR UPDATE OF o SKIP LOCKED
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch", batch);
        command.Parameters.AddWithValue("deadLetterTopics", "%" + KafkaTopics.DeadLetterSuffix);
        command.Parameters.AddWithValue("deadLetters", deadLetters);
        command.Parameters.AddWithValue("heldTopics", heldTopics);

        var rows = new List<OutboxEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OutboxEnvelope(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? [],
                reader.GetInt32(7)));
        }

        return rows;
    }

    private static async Task MarkFailedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long seq, DateTime now, Exception error, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE outbox_message
               SET attempts = attempts + 1,
                   next_attempt_on = @now + make_interval(secs => LEAST(300, power(2, LEAST(attempts + 1, 9)))),
                   last_error = @error
             WHERE seq = @seq
            """,
            connection,
            transaction);
        var message = $"{error.GetType().Name}: {error.Message}";
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("error", message.Length <= MaxErrorLength ? message : message[..MaxErrorLength]);
        command.Parameters.AddWithValue("seq", seq);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The broker was unavailable (ARV-165): the row keeps its attempts and is due again after <paramref name="retry"/>.</summary>
    private static async Task MarkBrokerUnavailableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long seq, DateTime now, TimeSpan retry,
        Exception error, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE outbox_message SET next_attempt_on = @next, last_error = @error WHERE seq = @seq", connection, transaction);
        var message = $"{error.GetType().Name}: {error.Message}";
        command.Parameters.AddWithValue("next", now + retry);
        command.Parameters.AddWithValue("error", message.Length <= MaxErrorLength ? message : message[..MaxErrorLength]);
        command.Parameters.AddWithValue("seq", seq);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkUnreadableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long seq, Exception error, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE outbox_message SET last_error = @error WHERE seq = @seq", connection, transaction);
        command.Parameters.AddWithValue("error", error.Message.Length <= MaxErrorLength ? error.Message : error.Message[..MaxErrorLength]);
        command.Parameters.AddWithValue("seq", seq);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkSentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, List<long> seqs, DateTime now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE outbox_message SET sent_on = @now, last_error = NULL WHERE seq = ANY(@seqs)", connection, transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("seqs", seqs.ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Hourly, by the leader: sent rows and inbox entries past their keep time go, at most 10,000 of each per pass. The newest
    /// row of each key on a compacted topic stays however old it is (ARV-126): it is the state that topic carries, which
    /// scripts/ops/ariva-db.sh sends again after a Kafka loss. An older row of such a key goes once a newer one exists.
    /// </summary>
    private async Task HousekeepAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime now, CancellationToken ct)
    {
        if (timeProvider.GetUtcNow() - _lastHousekeeping < HousekeepingEvery)
            return;
        _lastHousekeeping = timeProvider.GetUtcNow();

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM outbox_message WHERE seq IN (
                SELECT o.seq FROM outbox_message o
                 WHERE o.sent_on IS NOT NULL AND o.sent_on < @sentBefore
                   AND (o.topic <> ALL (@compacted)
                        OR EXISTS (SELECT 1 FROM outbox_message n WHERE n.topic = o.topic AND n.message_key = o.message_key AND n.seq > o.seq))
                 LIMIT 10000);
            DELETE FROM processed_event WHERE ctid IN (
                SELECT ctid FROM processed_event WHERE processed_on < @processedBefore LIMIT 10000);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("sentBefore", now.AddDays(-kafka.Outbox.KeepSentDays));
        command.Parameters.AddWithValue("compacted", TopicCatalog.CompactedTopics.ToArray());
        command.Parameters.AddWithValue("processedBefore", now.AddDays(-kafka.Outbox.KeepProcessedDays));
        await command.ExecuteNonQueryAsync(ct);
    }
}
