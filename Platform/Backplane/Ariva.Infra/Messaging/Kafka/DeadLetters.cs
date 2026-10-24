using Ariva.Core.Messaging;
using MassTransit;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// A message that could not be processed, as produced to <c>&lt;topic&gt;.dlq.v1</c> (ADR-0018): everything needed to
/// inspect it and to replay it to its source topic with the same key. The error carries the exception type and
/// message only, no stack trace. The body is cut at <see cref="DeadLetters.MaxBodyBytes"/> (flagged, with the full
/// length), and further if the whole letter would pass <see cref="DeadLetters.MaxLetterBytes"/> (ARV-171), so the letter
/// always fits Kafka's 1 MB limit; a cut body cannot be replayed from the letter and is read from the source topic by
/// partition and offset while its retention lasts. Likewise a letter flagged in <see cref="DeadLetters.TextReplacedHeader"/>
/// (ARV-175) no longer holds the record's key or headers exactly: they too are read from the source topic.
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

/// <summary>
/// Keeps a dead letter durably when Kafka did not take it before its endpoint stopped (ARV-171): an outbox row for the
/// dead-letter topic, which the outbox relay sends later. Returns once the letter is stored (or was already, for a record
/// delivered again); throws when it could not be, and the record is then left unacknowledged (ARV-166).
/// </summary>
public interface IDeadLetterStore
{
    Task KeepAsync(DeadLetter letter, CancellationToken ct);
}

public static class DeadLetters
{
    /// <summary>512 KB of body is about 683 KB of base64, which leaves room for the rest under 1 MB.</summary>
    public const int MaxBodyBytes = 512 * 1024;

    /// <summary>
    /// The most a dead letter's JSON may take (ARV-171): Kafka's message limit less 16 KB for the key and headers, the same
    /// headroom the outbox keeps for events. <see cref="Create"/> guarantees it, so a letter always fits the topic and an
    /// outbox row.
    /// </summary>
    public const int MaxLetterBytes = KafkaSettings.MaxMessageBytes - 16 * 1024;

    /// <summary>
    /// The <c>message_type</c> of an outbox row that holds a dead letter (ARV-171). The dot keeps it apart from every event
    /// name, which is a class name.
    /// </summary>
    public const string OutboxMessageType = "ariva.dead-letter";

    /// <summary>Marks a letter whose headers were left out so that it fits (their number is the value).</summary>
    public const string HeadersDroppedHeader = "ariva-dead-letter-headers-dropped";

    /// <summary>
    /// Marks a letter whose key, a header name or value, or error message held a NUL character or a lone surrogate (ARV-175):
    /// each was replaced with U+FFFD, since PostgreSQL text refuses NUL and jsonb refuses both, so the letter could not be
    /// kept otherwise. The value names the parts that held one (<c>key</c>, <c>headers</c>, <c>error</c>), never the
    /// characters; the record itself is fetched from the source topic by the letter's exact topic, partition and offset.
    /// </summary>
    public const string TextReplacedHeader = "ariva-dead-letter-text-replaced";

    /// <summary>
    /// Put before a record's own header whose name starts like the letter's flags (<c>ariva-dead-letter-</c>), so a record
    /// cannot set a flag such as <see cref="TextReplacedHeader"/> (ARV-175): the flags are the letter's, never the record's.
    /// </summary>
    public const string RecordHeaderPrefix = "record:";

    private const string FlagPrefix = "ariva-dead-letter-";
    private const int MaxErrorLength = 2000;
    private const int MaxHeaders = 50;
    private const int MaxHeaderLength = 1000;
    private const int LastResortLength = 200;

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

    /// <summary>A dead letter for a record that never became a message (an unreadable value), from its receive context.</summary>
    public static DeadLetter FromReceive(ReceiveContext context, string consumer, Exception error, DateTime failedOn)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);
        context.TryGetPayload(out KafkaConsumeContext kafka);
        context.TryGetPayload(out KafkaConsumeContext<string> keyed);
        return Create(
            kafka?.Topic ?? context.InputAddress?.AbsolutePath.Trim('/') ?? "unknown",
            kafka?.Partition,
            kafka?.Offset,
            keyed?.Key,
            context.Body?.GetBytes(),
            context.TransportHeaders.GetAll().Select(h => new KeyValuePair<string, string>(h.Key, h.Value?.ToString())),
            consumer,
            error,
            failedOn);
    }

    /// <summary>
    /// A dead letter with every part bounded: body, headers (count and length) and error message, and as a whole: its JSON
    /// fits <see cref="MaxLetterBytes"/> (ARV-171). The JSON encoder writes a character outside ASCII as six bytes, so long
    /// headers could otherwise take a letter past Kafka's limit, where every send of it fails for good. The body gives way
    /// first (flagged as cut, with its full length), then the headers (left out, their number in
    /// <see cref="HeadersDroppedHeader"/>), and last the key and the error message. Every text part is then made printable
    /// for PostgreSQL (ARV-175, <see cref="Printable"/>, flagged in <see cref="TextReplacedHeader"/>), after it is cut, since a
    /// cut can split a surrogate pair. A record's header named like a flag is kept under <see cref="RecordHeaderPrefix"/>.
    /// The topic, partition, offset and consumer are kept exactly.
    /// </summary>
    public static DeadLetter Create(string topic, int? partition, long? offset, string key, byte[] body,
        IEnumerable<KeyValuePair<string, string>> headers, string consumer, Exception error, DateTime failedOn)
    {
        ArgumentNullException.ThrowIfNull(error);
        body ??= [];
        var replaced = new SortedSet<string>(StringComparer.Ordinal);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in (headers ?? []).Take(MaxHeaders))
        {
            if (name is null)
                continue;
            var text = value ?? string.Empty;
            var keptName = Printable(name.Length <= MaxHeaderLength ? name : name[..MaxHeaderLength], out var nameReplaced);
            if (keptName.StartsWith(FlagPrefix, StringComparison.OrdinalIgnoreCase))
                keptName = RecordHeaderPrefix + keptName;
            var keptValue = Printable(text.Length <= MaxHeaderLength ? text : text[..MaxHeaderLength], out var valueReplaced);
            if (nameReplaced || valueReplaced)
                replaced.Add("headers");
            kept[keptName] = keptValue;
        }

        var message = error.Message ?? string.Empty;
        message = Printable(message.Length <= MaxErrorLength ? message : message[..MaxErrorLength], out var messageReplaced);
        if (messageReplaced)
            replaced.Add("error");
        key = Printable(key ?? string.Empty, out var keyReplaced);
        if (keyReplaced)
            replaced.Add("key");
        if (replaced.Count > 0)
            kept[TextReplacedHeader] = string.Join(',', replaced);

        var bodyKept = Math.Min(body.Length, MaxBodyBytes);
        DeadLetter Build(int bodyBytes, IReadOnlyDictionary<string, string> withHeaders, string withKey, string withMessage) => new(
            topic ?? "unknown",
            partition,
            offset,
            withKey,
            Convert.ToBase64String(body.AsSpan(0, bodyBytes)),
            body.Length,
            bodyBytes < body.Length,
            withHeaders,
            consumer,
            error.GetType().FullName,
            withMessage,
            failedOn);

        var letter = Build(bodyKept, kept, key, message);
        var size = SizeOf(letter);
        while (size > MaxLetterBytes && bodyKept > 0)
        {
            // Base64 writes four characters for three bytes, each at least one byte of JSON: cutting three quarters of
            // the excess (and a margin) from the body takes off at least the excess.
            bodyKept = (int)Math.Max(0, bodyKept - ((size - MaxLetterBytes) * 3L / 4 + 4096));
            letter = Build(bodyKept, kept, key, message);
            size = SizeOf(letter);
        }

        if (size > MaxLetterBytes)
        {
            // The record's headers left out are counted; the letter's own flag is not one of them.
            var droppedCount = kept.Count - (replaced.Count > 0 ? 1 : 0);
            var dropped = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HeadersDroppedHeader] = droppedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (replaced.Count > 0)
                dropped[TextReplacedHeader] = string.Join(',', replaced);
            letter = Build(0, dropped, key, message);
            if (SizeOf(letter) > MaxLetterBytes)
            {
                // The last cut can split a surrogate pair too: the half left is replaced and flagged.
                var lastKey = Printable(key.Length <= LastResortLength ? key : key[..LastResortLength], out var keyCutReplaced);
                var lastMessage = Printable(message.Length <= LastResortLength ? message : message[..LastResortLength], out var messageCutReplaced);
                if (keyCutReplaced)
                    replaced.Add("key");
                if (messageCutReplaced)
                    replaced.Add("error");
                if (replaced.Count > 0)
                    dropped[TextReplacedHeader] = string.Join(',', replaced);
                letter = Build(0, dropped, lastKey, lastMessage);
            }
        }

        return letter;
    }

    /// <summary>
    /// The text with every NUL character and every unpaired surrogate replaced with U+FFFD (ARV-175), so PostgreSQL text and
    /// jsonb take it; the same string when it held none. A surrogate pair is kept.
    /// </summary>
    public static string Printable(string text, out bool replaced)
    {
        replaced = false;
        if (string.IsNullOrEmpty(text))
            return text;
        System.Text.StringBuilder builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var pairedHigh = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (pairedHigh)
            {
                builder?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '\0' || char.IsSurrogate(c))
            {
                builder ??= new System.Text.StringBuilder(text.Length).Append(text, 0, i);
                builder.Append('\uFFFD');
                replaced = true;
                continue;
            }

            builder?.Append(c);
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>The size of the letter's JSON as it goes to Kafka and into an outbox row.</summary>
    public static int SizeOf(DeadLetter letter) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(letter, EventCatalog.Json).Length;

    /// <summary>
    /// The outbox id of a letter (ARV-171): derived from the consumer and the record's place (topic, partition, offset), so a
    /// record that is delivered again and kept again adds no second row; a new id when the place is unknown.
    /// </summary>
    public static Guid IdOf(DeadLetter letter)
    {
        ArgumentNullException.ThrowIfNull(letter);
        if (letter.Partition is not { } partition || letter.Offset is not { } offset)
            return Guid.CreateVersion7();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"ariva-dead-letter|{letter.Consumer}|{letter.Topic}|{partition}|{offset}")));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (custom, RFC 9562)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>
    /// The outbox row's key for a letter (ARV-171): the record's key when it fits <c>message_key</c>, <c>none</c> when it is
    /// empty (as the direct send keys it), else a digest of it. The letter itself keeps the full key, for a replay. A key in
    /// which <see cref="Create"/> replaced a character (ARV-175) is no longer the record's, so it is keyed by its digest too.
    /// </summary>
    public static string OutboxKeyOf(DeadLetter letter)
    {
        ArgumentNullException.ThrowIfNull(letter);
        if (string.IsNullOrEmpty(letter.Key))
            return "none";
        if (Outbox.OutboxLimits.FitsMessageKey(letter.Key) && !KeyWasReplaced(letter))
            return letter.Key;
        return "sha256-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(letter.Key)));
    }

    private static bool KeyWasReplaced(DeadLetter letter) =>
        letter.Headers is not null && letter.Headers.TryGetValue(TextReplacedHeader, out var parts)
        && parts.Split(',').Contains("key", StringComparer.Ordinal);

    /// <summary>The dead-letter topic of the letter's source topic.</summary>
    public static string TopicFor(DeadLetter letter)
    {
        ArgumentNullException.ThrowIfNull(letter);
        return KafkaTopics.DeadLetter(letter.Topic);
    }
}
