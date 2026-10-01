using Ariva.Core.Messaging;
using MassTransit;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// A message that could not be processed, as produced to <c>&lt;topic&gt;.dlq.v1</c> (ADR-0018): everything needed to
/// inspect it and to replay it to its source topic with the same key. The error carries the exception type and
/// message only, no stack trace. The body is cut at <see cref="DeadLetters.MaxBodyBytes"/> (flagged, with the full
/// length) so the letter always fits Kafka's 1 MB limit; a cut body cannot be replayed from the letter and is read from
/// the source topic by partition and offset while its retention lasts.
/// </summary>
public sealed record DeadLetter(
    string Topic,
    int? Partition,
    long? Offset,
    string Key,
    string BodyBase64,
    int BodyBytes,
    bool BodyTruncated,
    IReadOnlyDictionary<string, string> Headers,
    string Consumer,
    string ErrorType,
    string ErrorMessage,
    DateTime FailedOn);

/// <summary>Produces dead letters. Throws when the broker did not take one, so the source message is not committed.</summary>
public interface IDeadLetterSink
{
    Task SendAsync(DeadLetter letter, CancellationToken ct);
}

public static class DeadLetters
{
    /// <summary>512 KB of body is about 683 KB of base64, which leaves room for the rest under 1 MB.</summary>
    public const int MaxBodyBytes = 512 * 1024;

    private const int MaxErrorLength = 2000;
    private const int MaxHeaders = 50;
    private const int MaxHeaderLength = 1000;

    public static DeadLetter From(ConsumeContext context, string consumer, Exception error, DateTime failedOn)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);
        context.TryGetPayload(out KafkaConsumeContext kafka);
        context.TryGetPayload(out KafkaConsumeContext<string> keyed);

        return Create(
            kafka?.Topic ?? context.ReceiveContext?.InputAddress?.AbsolutePath.Trim('/') ?? "unknown",
            kafka?.Partition,
            kafka?.Offset,
            keyed?.Key,
            context.ReceiveContext?.Body?.GetBytes(),
            context.Headers.GetAll().Select(h => new KeyValuePair<string, string>(h.Key, h.Value?.ToString())),
            consumer,
            error,
            failedOn);
    }

    /// <summary>A dead letter with every part bounded: body, headers (count and length) and error message.</summary>
    public static DeadLetter Create(string topic, int? partition, long? offset, string key, byte[] body,
        IEnumerable<KeyValuePair<string, string>> headers, string consumer, Exception error, DateTime failedOn)
    {
        ArgumentNullException.ThrowIfNull(error);
        body ??= [];
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in (headers ?? []).Take(MaxHeaders))
        {
            if (name is null)
                continue;
            var text = value ?? string.Empty;
            kept[name.Length <= MaxHeaderLength ? name : name[..MaxHeaderLength]] = text.Length <= MaxHeaderLength ? text : text[..MaxHeaderLength];
        }

        var truncated = body.Length > MaxBodyBytes;
        var message = error.Message ?? string.Empty;
        return new DeadLetter(
            topic ?? "unknown",
            partition,
            offset,
            key ?? string.Empty,
            Convert.ToBase64String(truncated ? body.AsSpan(0, MaxBodyBytes) : body),
            body.Length,
            truncated,
            kept,
            consumer,
            error.GetType().FullName,
            message.Length <= MaxErrorLength ? message : message[..MaxErrorLength],
            failedOn);
    }

    /// <summary>The dead-letter topic of the letter's source topic.</summary>
    public static string TopicFor(DeadLetter letter)
    {
        ArgumentNullException.ThrowIfNull(letter);
        return KafkaTopics.DeadLetter(letter.Topic);
    }
}
