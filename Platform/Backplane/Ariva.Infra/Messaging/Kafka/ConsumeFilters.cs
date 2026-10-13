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
/// Shutdown (cancellation) is not a failure: it rethrows and the message is delivered again after restart. A dead letter
/// the broker refuses is retried until accepted or until the endpoint stops (<paramref name="stopping"/>, ARV-166), so
/// nothing is lost silently and nothing keeps a stopping host alive.
/// <para>
/// MassTransit always places <c>UseMessageRetry</c> first in the message pipe, whatever the configuration order
/// (proven by the pipe probe in ConsumePipelineTests), so this filter cannot wrap the retry. It runs inside it and
/// dead-letters only on the final attempt (<see cref="RetryContextExtensions.GetRetryAttempt"/> equal to the limit),
/// which gives the same behaviour: dead letter once, after every retry.
/// </para>
/// </summary>
public sealed class DeadLetterFilter(int retryLimit, IDeadLetterSink sink, TimeProvider timeProvider, ILogger<DeadLetterFilter> logger,
    EndpointStopping stopping = null) : IFilter<ConsumeContext>
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
    /// accepted. Shutdown ends the wait and leaves the message unacknowledged: the message's own token, or the endpoint
    /// beginning to stop, since MassTransit does not cancel the message's token when its endpoint stops (ARV-166). Either
    /// way the loop ends as a cancellation of the message's token (<see cref="DeadLetterRetry"/> says why).
    /// </summary>
    private Task SendUntilAcceptedAsync(DeadLetter letter, CancellationToken messageToken) =>
        DeadLetterRetry.SendUntilAcceptedAsync(sink, letter, timeProvider, logger, messageToken, stopping?.Token ?? CancellationToken.None);

    public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-dead-letter");
}

/// <summary>
/// The endpoint's dead-letter pipe: a record no consumer could take because its value is not a readable message (the
/// rider skipped it) goes to the dead-letter topic with its raw bytes, not dropped and not blocking the partition.
/// Records skipped for other reasons (an inbox duplicate, one already dead-lettered after its retries) pass through.
/// </summary>
public sealed class UnreadableFilter(IDeadLetterSink sink, TimeProvider timeProvider, ILogger<UnreadableFilter> logger, EndpointStopping stopping = null)
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
        await DeadLetterRetry.SendUntilAcceptedAsync(sink, letter, timeProvider, logger, context.CancellationToken, stopping?.Token ?? CancellationToken.None);

        await next.Send(context);
    }

    public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-unreadable");
}

/// <summary>
/// Sends a dead letter until the broker accepts it: each attempt ends within the produce bound (ARV-166), and failed
/// attempts are retried after 2, 4 ... 30 seconds until the message's token or <c>stopping</c> (the endpoint beginning to
/// stop, <see cref="EndpointStopping"/>) is cancelled.
/// <para>
/// The loop then ends with an <see cref="OperationCanceledException"/> that carries the message's own token, whichever of
/// the two ended it (ARV-166). MassTransit's retry filter passes on only a cancellation of that token and retries anything
/// else, and the receive pipe's rescue filter sends every exception but an <see cref="OperationCanceledException"/> to the
/// error pipe, which acknowledges the message. Ended with the linked token, a stop went to the retry policy: with no
/// retries configured the policy failed on it and the rescue acknowledged the message (lost), with retries the exhausted
/// policy published a fault. Carrying the message's token, the cancellation leaves
/// the pipe, the Kafka receive marks the record faulted rather than consumed, and its offset is not committed for it, so
/// the message is delivered again after restart. MassTransit's checkpointer still commits past a faulted record once a
/// later record of its partition completes and is checkpointed before the endpoint stops; the warning names the record
/// so that it can be replayed (docs/architecture/overview.md, Producing).
/// </para>
/// </summary>
internal static class DeadLetterRetry
{
    public static async Task SendUntilAcceptedAsync(IDeadLetterSink sink, DeadLetter letter, TimeProvider timeProvider, ILogger logger,
        CancellationToken messageToken, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(messageToken, stopping);
        var ct = linked.Token;
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan wait;
            try
            {
                ct.ThrowIfCancellationRequested();
                await sink.SendAsync(letter, ct);
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
                throw Stopping(logger, letter, e, messageToken);
            }

            try
            {
                await Task.Delay(wait, timeProvider, ct);
            }
            catch (OperationCanceledException e)
            {
                throw Stopping(logger, letter, e, messageToken);
            }
        }
    }

    /// <summary>The loop ends: not a failure of the message, which is left unacknowledged (see the summary).</summary>
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
