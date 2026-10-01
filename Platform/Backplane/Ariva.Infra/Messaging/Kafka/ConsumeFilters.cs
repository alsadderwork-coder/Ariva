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
public sealed class InboxFilter<T>(IInbox inbox, IUnitOfWork unitOfWork, ILogger<InboxFilter<T>> logger) : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var consumer = ConsumerName.Of(context);
        var eventId = context.Message is IEvent message && message.Id != Guid.Empty ? message.Id : context.MessageId ?? Guid.Empty;
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
/// the broker refuses is retried until accepted, so nothing is lost silently.
/// <para>
/// MassTransit always places <c>UseMessageRetry</c> first in the message pipe, whatever the configuration order
/// (proven by the pipe probe in ConsumePipelineTests), so this filter cannot wrap the retry. It runs inside it and
/// dead-letters only on the final attempt (<see cref="RetryContextExtensions.GetRetryAttempt"/> equal to the limit),
/// which gives the same behaviour: dead letter once, after every retry.
/// </para>
/// </summary>
public sealed class DeadLetterFilter(int retryLimit, IDeadLetterSink sink, TimeProvider timeProvider, ILogger<DeadLetterFilter> logger) : IFilter<ConsumeContext>
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
    /// again (2, 4 ... 30 seconds) and holds the partition until it is accepted. Shutdown ends the wait and the message is
    /// redelivered after restart.
    /// </summary>
    private async Task SendUntilAcceptedAsync(DeadLetter letter, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await sink.SendAsync(letter, ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                var wait = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                logger.LogError(e, "Dead letter for {Topic} offset {Offset} not accepted (attempt {Attempt}); retrying in {Wait}", letter.Topic, letter.Offset, attempt, wait);
                await Task.Delay(wait, timeProvider, ct);
            }
        }
    }

    public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-dead-letter");
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
