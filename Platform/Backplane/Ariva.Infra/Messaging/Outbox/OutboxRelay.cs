using System.Data;
using System.Text.Json;
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
    /// <summary>False until the producer side is up (the bus starts after the host); the relay waits instead of failing rows.</summary>
    bool IsReady => true;

    Task SendAsync(OutboxEnvelope message, CancellationToken ct);
}

/// <summary>What one relay pass did.</summary>
public sealed record RelayPass(bool Leader, int Claimed, int Sent, int Failed, int HeldBack)
{
    public static readonly RelayPass NotLeader = new(false, 0, 0, 0, 0);

    public static readonly RelayPass NotReady = new(false, 0, 0, 0, 0);
}

/// <summary>
/// The outbox relay (ADR-0018). Every pass runs in one PostgreSQL transaction: it takes the relay's advisory lock
/// (one leader across every pod and host; the others skip the pass), claims unsent rows in <c>seq</c> order with
/// <c>FOR UPDATE SKIP LOCKED</c>, and produces them one by one. When a row fails, its key is held back for the rest of
/// the pass and until the row's retry time, so later events for that key never overtake it; other keys carry on.
/// A broker failure ends the pass. Sent rows are marked in the same transaction, so a crash between produce and commit sends those rows again
/// (at-least-once; consumers deduplicate in the inbox). The leader also removes old sent rows and inbox entries.
/// </summary>
public sealed class OutboxRelay(
    DatabaseSettings database,
    KafkaSettings kafka,
    IOutboxTransport transport,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    /// <summary>"ARIV" and the story number, like the administrators lock of ARV-011.</summary>
    public const long LeaderLock = 0x41524956_00000020;

    private const int MaxErrorLength = 2000;
    private static readonly TimeSpan HousekeepingEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _lastHousekeeping = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = kafka.Outbox;
        logger.LogInformation("Outbox relay started: batch {BatchSize}, poll {PollMilliseconds} ms", settings.BatchSize, settings.PollMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(settings.PollMilliseconds);
            try
            {
                var pass = await RunOnceAsync(stoppingToken);
                if (pass.Leader && pass.Claimed >= settings.BatchSize && pass.Failed == 0)
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

    /// <summary>One pass: lead, claim, produce, mark. Public for the integration tests.</summary>
    public async Task<RelayPass> RunOnceAsync(CancellationToken ct)
    {
        if (!transport.IsReady)
            return RelayPass.NotReady;

        await using var connection = new NpgsqlConnection(database.BuildConnectionString());
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        if (!await TryLeadAsync(connection, transaction, ct))
        {
            await transaction.RollbackAsync(ct);
            return RelayPass.NotLeader;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await ClaimAsync(connection, transaction, now, kafka.Outbox.BatchSize, ct);
        var held = new HashSet<(string Topic, string Key)>();
        var sent = new List<long>();
        int failed = 0, heldBack = 0;

        foreach (var row in rows)
        {
            if (held.Contains((row.Topic, row.Key)))
            {
                heldBack++;
                continue;
            }

            try
            {
                await transport.SendAsync(row, ct);
                sent.Add(row.Seq);
            }
            catch (UnknownEventTypeException e)
            {
                heldBack++;
                held.Add((row.Topic, row.Key));
                await MarkUnreadableAsync(connection, transaction, row.Seq, e, ct);
                logger.LogWarning("Outbox row {Seq} names {MessageType}, which this build does not know; key held back", row.Seq, row.MessageType);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                held.Add((row.Topic, row.Key));
                await MarkFailedAsync(connection, transaction, row.Seq, now, e, ct);
                if (row.Attempts + 1 >= kafka.Outbox.StuckAfterAttempts)
                    logger.LogError(e, "Outbox row {Seq} ({MessageType} {EventId} to {Topic}) stuck after {Attempts} attempts; key held back", row.Seq, row.MessageType, row.Id, row.Topic, row.Attempts + 1);
                else
                    logger.LogWarning(e, "Outbox row {Seq} ({MessageType} {EventId} to {Topic}) not sent; key held back", row.Seq, row.MessageType, row.Id, row.Topic);

                // A row that cannot be read (unknown type, bad payload) only holds back its key. Anything else is the
                // broker or the network: the rest of the batch would fail the same way after the produce timeout, so
                // the pass ends here and the remaining rows wait untouched for the next one.
                if (e is not (InvalidOperationException or JsonException))
                {
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

    /// <summary>Retry delay after the given number of failed attempts: 2, 4, 8 ... seconds, at most five minutes.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Clamp(attempts, 1, 9))));

    private static async Task<bool> TryLeadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction);
        command.Parameters.AddWithValue("key", LeaderLock);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<List<OutboxEnvelope>> ClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime now, int batch, CancellationToken ct)
    {
        // A row waits while an earlier row of its key is waiting for its retry time, so order per key holds across passes.
        await using var command = new NpgsqlCommand(
            """
            SELECT o.seq, o.id, o.topic, o.message_key, o.message_type, o.payload::text, o.headers::text, o.attempts
              FROM outbox_message o
             WHERE o.sent_on IS NULL
               AND (o.next_attempt_on IS NULL OR o.next_attempt_on <= @now)
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

    /// <summary>Hourly, by the leader: sent rows and inbox entries past their keep time go, at most 10,000 of each per pass.</summary>
    private async Task HousekeepAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime now, CancellationToken ct)
    {
        if (timeProvider.GetUtcNow() - _lastHousekeeping < HousekeepingEvery)
            return;
        _lastHousekeeping = timeProvider.GetUtcNow();

        await using var command = new NpgsqlCommand(
            """
            DELETE FROM outbox_message WHERE seq IN (
                SELECT seq FROM outbox_message WHERE sent_on IS NOT NULL AND sent_on < @sentBefore LIMIT 10000);
            DELETE FROM processed_event WHERE ctid IN (
                SELECT ctid FROM processed_event WHERE processed_on < @processedBefore LIMIT 10000);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("sentBefore", now.AddDays(-kafka.Outbox.KeepSentDays));
        command.Parameters.AddWithValue("processedBefore", now.AddDays(-kafka.Outbox.KeepProcessedDays));
        await command.ExecuteNonQueryAsync(ct);
    }
}
