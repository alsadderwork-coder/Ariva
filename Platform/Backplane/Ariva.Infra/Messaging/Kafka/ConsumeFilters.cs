using Ariva.Infra.Messaging.Inbox;
using MassTransit;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Inbox and unit of work around a consumer (ADR-0018), innermost of the Ariva filters. Claims the event for this
/// consumer in the scope's transaction, runs the consumer, and commits both together. A duplicate is acknowledged
/// without running the consumer. A failure rolls everything back, claim included, and rethrows to the retry filter.
/// The filter owns the commit: a consumer that must not commit throws (and is retried, then dead-lettered); calling
/// <see cref="IUnitOfWork.PromiseNotToCommit"/> has no effect here.
/// </summary>
public sealed class InboxFilter<T>(IInbox inbox, IUnitOfWork unitOfWork, ILogger<InboxFilter<T>> logger, IEnumerable<IInboxKey<T>> keys = null)
    : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var consumer = ConsumerName.Of(context);
        var eventId = context.Message is IEvent message && message.Id != Guid.Empty ? message.Id
            : keys?.FirstOrDefault() is { } key ? key.KeyOf(context.Message)
            : context.MessageId ?? Guid.Empty;
        if (eventId == Guid.Empty)
            throw new InvalidOperationException($"{typeof(T).Name} carries no event id; the inbox cannot deduplicate it.");

        try
        {
            if (!await inbox.TryClaimAsync(consumer, eventId, context.CancellationToken))
            {
                await unitOfWork.RollbackAsync(context.CancellationToken);
                logger.LogInformation("Duplicate {MessageType} {EventId} for {Consumer} skipped", typeof(T).Name, eventId, consumer);
                return;
            }

            await next.Send(context);
            unitOfWork.PromiseToCommit();
            await unitOfWork.EndAsync(context.CancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-inbox");
}

/// <summary>
/// The dead-letter filter (ADR-0018). When the last attempt of a message fails, the message goes to the dead-letter
/// topic with its key, body, headers, source topic, partition, offset and the error, and the filter returns so the
/// offset commits and the partition moves on. Earlier attempts rethrow, so the retry around it tries again.
/// Shutdown (cancellation) is not a failure: it rethrows and the message is delivered again after restart. Nor is a failure
/// the stop caused (ARV-177a): one after the message's own token was cancelled (MassTransit cancels it, with the record's
/// confirmation, when the bus's stop wait ends with the message still in hand, so an attempt that ran on fails into a host
/// being disposed), or an <see cref="ObjectDisposedException"/> while the endpoint stops. Such a record is not committed
/// (one lane per consumer, ARV-253: nothing after it completes) and the next host gets it again, so a dead letter would
/// only be a second copy of a valid message: it is left unacknowledged instead, as the message's own cancellation. A dead
/// letter the broker refuses is retried until accepted or until the endpoint stops (<paramref name="stopping"/>, ARV-166),
/// so nothing is lost silently and nothing keeps a stopping host alive.
/// <para>
/// MassTransit always places <c>UseMessageRetry</c> first in the message pipe, whatever the configuration order
/// (proven by the pipe probe in ConsumePipelineTests), so this filter cannot wrap the retry. It runs inside it and
/// dead-letters only on the final attempt (<see cref="RetryContextExtensions.GetRetryAttempt"/> equal to the limit),
/// which gives the same behaviour: dead letter once, after every retry.
/// </para>
/// </summary>
public sealed class DeadLetterFilter(int retryLimit, IDeadLetterSink sink, TimeProvider timeProvider, ILogger<DeadLetterFilter> logger,
    EndpointStopping stopping = null, IDeadLetterStore store = null, KafkaProducerMetrics metrics = null) : IFilter<ConsumeContext>
{
    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            await next.Send(context);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsTheStops(context, error))
        {
            throw LeftForTheRestart(context, error);
        }
        catch (Exception) when (context.GetRetryAttempt() < retryLimit)
        {
            throw;
        }
        catch (Exception error)
        {
            var letter = DeadLetters.From(context, ConsumerName.Of(context), error, timeProvider.GetUtcNow().UtcDateTime);
            logger.LogError(error, "Message from {Topic} partition {Partition} offset {Offset} failed after retries; sending it to the dead-letter topic",
                letter.Topic, letter.Partition, letter.Offset);
            await SendUntilAcceptedAsync(letter, context.CancellationToken);
        }
    }

    /// <summary>
    /// The v8 rider discards a message whose fault escapes the pipe, so a dead letter that the broker refuses is tried
    /// again (2, 4 ... 30 seconds; each attempt ends within the produce bound, ARV-166) and holds the partition until it is
    /// accepted. Shutdown ends the wait: the message's own token, or the endpoint beginning to stop, since MassTransit does
    /// not cancel the message's token when its endpoint stops (ARV-166). The letter is then kept in the outbox and the
    /// message completes (ARV-171); only when that fails too is the message left unacknowledged
    /// (<see cref="DeadLetterRetry"/> says why).
    /// </summary>
    private Task SendUntilAcceptedAsync(DeadLetter letter, CancellationToken messageToken) =>
        DeadLetterRetry.SendUntilAcceptedAsync(sink, letter, timeProvider, logger, messageToken, stopping?.Token ?? CancellationToken.None, store, metrics);

    /// <summary>
    /// ARV-177a: whether a failure is the stop's rather than the message's: the message's own token is cancelled, or its
    /// endpoint is stopping and the failure comes from a disposed service (the host's container, a connection or a session
    /// it held).
    /// </summary>
    internal bool IsTheStops(ConsumeContext context, Exception error) =>
        context.CancellationToken.IsCancellationRequested || (stopping is { IsStopping: true } && IsDisposal(error));

    /// <summary>
    /// An <see cref="ObjectDisposedException"/> anywhere in the failure's chain, aggregates included, to a depth of 16 and at
    /// most 256 exceptions; past either bound the failure is not taken for the stop's (dead-lettered: a copy, not a loss).
    /// </summary>
    internal static bool IsDisposal(Exception error)
    {
        var pending = new Stack<(Exception Error, int Depth)>();
        pending.Push((error, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < 256)
        {
            var (current, depth) = pending.Pop();
            if (current is null || depth > 16)
                continue;
            visited++;
            if (current is ObjectDisposedException)
                return true;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push((inner, depth + 1));
            }
            else
            {
                pending.Push((current.InnerException, depth + 1));
            }
        }

        return false;
    }

    /// <summary>The message is left unacknowledged, as its own cancellation, which MassTransit neither retries nor rescues.</summary>
    private OperationCanceledException LeftForTheRestart(ConsumeContext context, Exception error)
    {
        context.TryGetPayload(out KafkaConsumeContext kafka);
        logger.LogWarning(error, "Message from {Topic} partition {Partition} offset {Offset} failed after its endpoint began to stop past it; not dead-lettered, it is delivered again after restart (ARV-177a)",
            kafka?.Topic, kafka?.Partition, kafka?.Offset);
        return new OperationCanceledException("The message failed after its endpoint stopped; it was left unacknowledged for the restart (ARV-177a).", error, context.CancellationToken);
    }

    public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-dead-letter");
}

/// <summary>
/// ARV-096e (CWE-400, ASVS 5.0 V15.2.2): each attempt of a message gets at most <c>Kafka:Consumers:HandlerTimeoutSeconds</c>. The
/// filter runs inside the retry and the dead-letter filter and around the inbox, unit of work and consumer, which see a
/// token that the timeout cancels as well as the message's own; so a handler stalled on the database or a partner frees its
/// consumer's one lane (ARV-253) at the timeout. An attempt cut there fails with
/// <see cref="HandlerTimeoutException"/>: it is rolled back and retried like any failure, and the last attempt goes to the
/// dead-letter topic. The message's own cancellation (a stop) is passed on unchanged. A handler that never looks at its token
/// cannot be cut; every Ariva consumer passes it to its reads and writes.
/// </summary>
public sealed class HandlerTimeoutFilter(TimeSpan timeout, TimeProvider timeProvider, ILogger<HandlerTimeoutFilter> logger) : IFilter<ConsumeContext>
{
    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        using var limit = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, limit.Token);
        try
        {
            await next.Send(new TimedConsumeContext(context, linked.Token));
        }
        // Whatever the cut ended in (a cancellation, MassTransit's ConsumerCanceledException, a wrapped database error) is the cut.
        catch (Exception e) when (limit.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            throw Cut(context, e);
        }

        if (limit.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
            throw Cut(context, null);
    }

    private HandlerTimeoutException Cut(ConsumeContext context, Exception inner)
    {
        var consumer = ConsumerName.Of(context);
        logger.LogWarning("A message for {Consumer} ran past its handler timeout of {Seconds} s and was cancelled; it is retried, then dead-lettered",
            consumer, timeout.TotalSeconds);
        return new HandlerTimeoutException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"The handler of {consumer} ran past its timeout of {timeout.TotalSeconds:0} s and was cancelled (ARV-096e)."), inner);
    }

    public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-handler-timeout");

    /// <summary>The message's context with the attempt's token in place of the message's own.</summary>
    private sealed class TimedConsumeContext(ConsumeContext context, CancellationToken token) : MassTransit.Context.ConsumeContextProxy(context)
    {
        public override CancellationToken CancellationToken => token;
    }
}

/// <summary>An attempt of a message that ran past its handler timeout and was cancelled (ARV-096e).</summary>
public sealed class HandlerTimeoutException : TimeoutException
{
    public HandlerTimeoutException()
    {
    }

    public HandlerTimeoutException(string message) : base(message)
    {
    }

    public HandlerTimeoutException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The endpoint's dead-letter pipe: a record no consumer could take because its value is not a readable message (the
/// rider skipped it) goes to the dead-letter topic with its raw bytes, not dropped and not blocking the partition.
/// Records skipped for other reasons (an inbox duplicate, one already dead-lettered after its retries) pass through.
/// </summary>
public sealed class UnreadableFilter(IDeadLetterSink sink, TimeProvider timeProvider, ILogger<UnreadableFilter> logger, EndpointStopping stopping = null,
    IDeadLetterStore store = null, KafkaProducerMetrics metrics = null)
    : IFilter<ReceiveContext>
{
    public async Task Send(ReceiveContext context, IPipe<ReceiveContext> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Only a Kafka record whose value the deserializer could not read (it gave null) is dead-lettered here; a record
        // skipped for another reason (an inbox duplicate, one already dead-lettered after its retries) is readable.
        // A tombstone (a null value on a compacted topic) is not unreadable either.
        if (!context.TryGetPayload(out KafkaConsumeContext kafka) || context.Body?.GetBytes() is not { Length: > 0 } ||
            (context.TryGetPayload(out ConsumeContext consume) && consume.TryGetMessage<object>(out _)))
        {
            await next.Send(context);
            return;
        }

        var group = !string.IsNullOrEmpty(kafka.GroupId) ? kafka.GroupId : context.InputAddress?.AbsolutePath.Trim('/') ?? "unknown";
        var letter = DeadLetters.FromReceive(context, group, new InvalidDataException("The record's value is not a readable message."),
            timeProvider.GetUtcNow().UtcDateTime);
        logger.LogError("Unreadable record from {Topic} partition {Partition} offset {Offset}; sending it to the dead-letter topic",
            letter.Topic, letter.Partition, letter.Offset);
        await DeadLetterRetry.SendUntilAcceptedAsync(sink, letter, timeProvider, logger, context.CancellationToken, stopping?.Token ?? CancellationToken.None, store, metrics);

        await next.Send(context);
    }

    public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-unreadable");
}

/// <summary>
/// Sends a dead letter until the broker accepts it: each attempt ends within the produce bound (ARV-166), and failed
/// attempts are retried after 2, 4 ... 30 seconds until the message's token or <c>stopping</c> (the endpoint beginning to
/// stop, <see cref="EndpointStopping"/>) is cancelled.
/// <para>
/// When the loop ends without Kafka having taken the letter, the letter is first kept durably (ARV-171): an outbox row for
/// the dead-letter topic (<see cref="IDeadLetterStore"/>), written within <see cref="KeepTimeout"/>, and the loop returns
/// so that the record completes and its offset may be committed. A loop that begins when its endpoint is already stopping
/// (the last attempt of a retry schedule, which a stop does not cancel, ConsumePipeline) keeps the letter at once, without
/// a Kafka attempt that could hold the stop for the produce bound. Faulting the record instead let MassTransit's
/// checkpointer commit past it once a later record of its partition (another key) completed during the stop, so the
/// record was neither processed nor dead-lettered (ARV-166's residual, closed here).
/// </para>
/// <para>
/// Only when the letter cannot be kept (no store, the database away, its bound, or a host already disposed; a NUL or lone
/// surrogate in the record's key, headers or error text no longer stops it: <see cref="DeadLetters.Create"/> replaces them,
/// ARV-175) does the loop end
/// as ARV-166 did: with an <see cref="OperationCanceledException"/> that carries the message's own token, whichever of the
/// two tokens ended it. MassTransit's retry filter passes on only a cancellation of that token and retries anything else,
/// and the receive pipe's rescue filter sends every exception but an <see cref="OperationCanceledException"/> to the error
/// pipe, which acknowledges the message. Ended with the linked token, a stop went to the retry policy: with no retries
/// configured the policy failed on it and the rescue acknowledged the message (lost), with retries the exhausted policy
/// published a fault. Carrying the message's token, the cancellation leaves the pipe, the Kafka receive marks the record
/// faulted rather than consumed, and its offset is not committed for it, so the message is delivered again after restart.
/// MassTransit's checkpointer passes a faulted record, but since ARV-253 nothing after it completes before the stop (one
/// lane per Kafka consumer), so its offset stays uncommitted and the next host gets it again. The warning names the
/// record (docs/architecture/overview.md, Producing).
/// </para>
/// <para>
/// Each letter is counted once on <see cref="KafkaProducerMetrics.DeadLettersName"/> (ARV-125a) by how it ended: sent, kept
/// in the outbox or failed (left unacknowledged); a refused attempt that is retried is not counted.
/// </para>
/// </summary>
internal static class DeadLetterRetry
{
    /// <summary>The longest the outbox write of a dead letter may hold an endpoint's stop (ARV-171).</summary>
    public static readonly TimeSpan KeepTimeout = TimeSpan.FromSeconds(10);

    public static async Task SendUntilAcceptedAsync(IDeadLetterSink sink, DeadLetter letter, TimeProvider timeProvider, ILogger logger,
        CancellationToken messageToken, CancellationToken stopping, IDeadLetterStore store = null, KafkaProducerMetrics metrics = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(messageToken, stopping);
        var ct = linked.Token;
        Exception ended;
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan wait;
            try
            {
                ct.ThrowIfCancellationRequested();
                await sink.SendAsync(letter, ct);
                metrics?.DeadLetter(letter, KafkaProducerMetrics.SentOutcome);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                logger.LogError(e, "Dead letter for {Topic} partition {Partition} offset {Offset} not accepted (attempt {Attempt}); retrying in {Wait}",
                    letter.Topic, letter.Partition, letter.Offset, attempt, wait);
            }
            catch (Exception e)
            {
                ended = e;
                break;
            }

            try
            {
                await Task.Delay(wait, timeProvider, ct);
            }
            catch (OperationCanceledException e)
            {
                ended = e;
                break;
            }
        }

        if (store is not null && await KeepAsync(store, letter, timeProvider, logger))
        {
            metrics?.DeadLetter(letter, KafkaProducerMetrics.KeptOutcome);
            return;
        }

        metrics?.DeadLetter(letter, KafkaProducerMetrics.FailedOutcome);
        throw Stopping(logger, letter, ended, messageToken);
    }

    /// <summary>Writes the letter to the outbox within <see cref="KeepTimeout"/>; false when it could not.</summary>
    private static async Task<bool> KeepAsync(IDeadLetterStore store, DeadLetter letter, TimeProvider timeProvider, ILogger logger)
    {
        try
        {
            using var bound = new CancellationTokenSource(KeepTimeout, timeProvider);
            var keep = store.KeepAsync(letter, bound.Token);
            try
            {
                await keep.WaitAsync(bound.Token);
            }
            catch (OperationCanceledException) when (!keep.IsCompleted)
            {
                // The bound passed with the write still going (it should have honoured the token): observe its end.
                _ = keep.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw;
            }

            logger.LogWarning("Dead letter for {Topic} partition {Partition} offset {Offset} not sent before messaging stopped; kept in the outbox, which sends it later",
                letter.Topic, letter.Partition, letter.Offset);
            return true;
        }
        catch (Exception e)
        {
            // The database away, the bound or a host already disposed (text PostgreSQL refuses no longer reaches here since
            // ARV-175): nothing durable was written, so the record must not complete. The exception names the failure, not the letter.
            logger.LogError(e, "Dead letter for {Topic} partition {Partition} offset {Offset} could not be kept in the outbox either",
                letter.Topic, letter.Partition, letter.Offset);
            return false;
        }
    }

    /// <summary>The loop ends with nothing durable: not a failure of the message, which is left unacknowledged.</summary>
    private static OperationCanceledException Stopping(ILogger logger, DeadLetter letter, Exception cause, CancellationToken messageToken)
    {
        logger.LogWarning("Dead letter for {Topic} partition {Partition} offset {Offset} not sent: messaging is stopping; the message is left unacknowledged and is delivered again after restart",
            letter.Topic, letter.Partition, letter.Offset);
        return new OperationCanceledException("Messaging is stopping; the dead letter was not sent and the message was left unacknowledged.", cause, messageToken);
    }
}

/// <summary>Who consumed: the consumer group on Kafka (one per service and endpoint), the endpoint name otherwise.</summary>
public static class ConsumerName
{
    public static string Of(ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TryGetPayload(out KafkaConsumeContext kafka) && !string.IsNullOrEmpty(kafka.GroupId))
            return kafka.GroupId;
        return context.ReceiveContext?.InputAddress?.AbsolutePath.Trim('/') ?? "unknown";
    }
}
