using System.Diagnostics.Metrics;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Kafka metrics of the host (meter <c>Ariva.Kafka</c>). ARV-167: the host's producer raising a fatal client error and
/// being recreated after one: a counter, <c>ariva.kafka.producer_fatal</c>, with a <c>kind</c> of
/// <see cref="FatalErrorKind"/> or <see cref="RecreatedKind"/>; a fatal error with no recreation after it, or recreations
/// that keep coming, is the signal to alert on (a misconfiguration such as a missing IdempotentWrite ACL). ARV-176: the
/// topics the host consumes and whether each exists: a gauge, <see cref="ConsumedTopicMissingName"/>, 1 for a consumed
/// topic the broker does not have (its consumer waits for it) and 0 for one it has, tagged with the <c>topic</c> (a
/// <c>KafkaTopics</c> constant) and the consumer <c>group</c>; any 1 is the signal to alert on. A topic is reported once the
/// broker's topics have been read; while they cannot be read the gauge keeps the last state read. No tag comes from a message.
/// <para>
/// ARV-125a: <see cref="ConsumerUnconnectedName"/>, 1 for a consumer whose topic exists but whose endpoint could not be
/// connected (it is tried again at each check and consumes nothing meanwhile; <see cref="ConsumedTopicMissingName"/> reads 0
/// for it), 0 otherwise, with the same tags; and <see cref="DeadLettersName"/>, a counter of the dead letters of every path
/// (the consume filters, the queue stream worker, the partitioned consumer) by source <c>topic</c>, consumer <c>group</c> and
/// <c>outcome</c>: <see cref="SentOutcome"/> (the broker took it), <see cref="KeptOutcome"/> (kept in the outbox, which sends it
/// later, ARV-171) or <see cref="FailedOutcome"/> (neither: the record is left unacknowledged and is delivered again after a
/// restart). The topic is a <c>KafkaTopics</c> constant and the group an Ariva group name, or <c>unknown</c>; never a
/// value from the record.
/// </para>
/// <para>
/// ARV-254: <see cref="ConsumerHoldName"/>, a gauge of the seconds each rider endpoint has been held by its oldest record in
/// hand (0 when it holds none; <see cref="KafkaConsumerHolds"/>), with the same <c>topic</c> and <c>group</c> tags. A record
/// that retries, stalls or waits for a refused dead letter holds every partition its consumer reads; a hold longer than the
/// longest the consumer settings allow (<see cref="ConsumerSettings.LongestHold"/>) is the signal to alert on.
/// </para>
/// </summary>
public sealed partial class KafkaProducerMetrics : IDisposable
{
    public const string MeterName = KafkaProduce.ActivitySourceName;
    public const string FatalName = "ariva.kafka.producer_fatal";

    public const string ConsumedTopicMissingName = "ariva.kafka.consumed_topic_missing";

    public const string ConsumerUnconnectedName = "ariva.kafka.consumer_unconnected";

    public const string DeadLettersName = "ariva.kafka.dead_letters";

    public const string ConsumerHoldName = "ariva.kafka.consumer_hold_seconds";

    public const string FatalErrorKind = "fatal_error";
    public const string RecreatedKind = "recreated";

    public const string SentOutcome = "sent";
    public const string KeptOutcome = "kept";
    public const string FailedOutcome = "failed";

    /// <summary>The tag value of a topic or group that is not one of Ariva's.</summary>
    public const string Unknown = "unknown";

    #region Fields

    private readonly Meter _meter;
    private readonly Counter<long> _fatal;
    private readonly Counter<long> _deadLetters;

    #endregion

    #region Constructors

    public KafkaProducerMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _fatal = _meter.CreateCounter<long>(FatalName,
            description: "Fatal Kafka producer errors and the producer recreations after them");
        _deadLetters = _meter.CreateCounter<long>(DeadLettersName, unit: "{letter}",
            description: "Dead letters by source topic, consumer group and outcome (sent to the dead-letter topic, kept in the outbox, or failed and left for redelivery)");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    public void FatalError() => _fatal.Add(1, new KeyValuePair<string, object>("kind", FatalErrorKind));

    public void Recreated() => _fatal.Add(1, new KeyValuePair<string, object>("kind", RecreatedKind));

    /// <summary>
    /// Observes the consumed topics (ARV-176): <paramref name="topics"/> gives each consumer's topic and group and whether
    /// the topic is missing, read when the gauge is collected (a consumer whose topic has not been read yet is left out).
    /// Called once, by the host's consumer endpoints.
    /// </summary>
    public void ObserveConsumedTopics(Func<IEnumerable<(string Topic, string Group, bool Missing)>> topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        _meter.CreateObservableGauge(ConsumedTopicMissingName,
            () => topics().Select(t => new Measurement<int>(t.Missing ? 1 : 0,
                new KeyValuePair<string, object>("topic", t.Topic), new KeyValuePair<string, object>("group", t.Group))),
            description: "1 for a topic this host consumes that the broker does not have (its consumer starts when the topic appears), 0 for one it has");
    }

    /// <summary>
    /// Observes the consumers whose connect failed (ARV-125a): <paramref name="consumers"/> gives each consumer's topic and
    /// group and whether its topic exists but its endpoint could not be connected, read when the gauge is collected (a
    /// consumer whose topic has not been read yet is left out). Called once, by the host's consumer endpoints.
    /// </summary>
    public void ObserveUnconnectedConsumers(Func<IEnumerable<(string Topic, string Group, bool Unconnected)>> consumers)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        _meter.CreateObservableGauge(ConsumerUnconnectedName,
            () => consumers().Select(c => new Measurement<int>(c.Unconnected ? 1 : 0,
                new KeyValuePair<string, object>("topic", c.Topic), new KeyValuePair<string, object>("group", c.Group))),
            description: "1 for a consumer whose topic exists but whose endpoint could not be connected (tried again at each check), 0 otherwise");
    }

    /// <summary>
    /// Observes the rider endpoints' holds (ARV-254): <paramref name="holds"/> gives each endpoint's topic and group and the
    /// seconds its oldest record in hand has been held, read when the gauge is collected. Called once, by
    /// <see cref="KafkaConsumerHolds"/>.
    /// </summary>
    public void ObserveConsumerHolds(Func<IEnumerable<(string Topic, string Group, double Seconds)>> holds)
    {
        ArgumentNullException.ThrowIfNull(holds);
        _meter.CreateObservableGauge(ConsumerHoldName,
            () => holds().Select(h => new Measurement<double>(h.Seconds,
                new KeyValuePair<string, object>("topic", h.Topic), new KeyValuePair<string, object>("group", h.Group))),
            unit: "s",
            description: "Seconds the oldest record a rider endpoint is handling has held it (every attempt, the retry waits and its dead letter), 0 when it holds none");
    }

    /// <summary>Counts one dead letter of <paramref name="letter"/>'s source topic and consumer with the given outcome.</summary>
    public void DeadLetter(DeadLetter letter, string outcome)
    {
        ArgumentNullException.ThrowIfNull(letter);
        if (outcome is not (SentOutcome or KeptOutcome or FailedOutcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A dead letter is sent, kept or failed.");
        _deadLetters.Add(1, new KeyValuePair<string, object>("topic", TopicTag(letter.Topic)), new KeyValuePair<string, object>("group", GroupTag(letter.Consumer)),
            new KeyValuePair<string, object>("outcome", outcome));
    }

    /// <summary>A topic as a tag value: a <c>KafkaTopics</c> constant, else <see cref="Unknown"/>.</summary>
    public static string TopicTag(string topic) => Ariva.Core.Messaging.KafkaTopics.IsKnown(topic) ? topic : Unknown;

    /// <summary>A consumer group as a tag value: an Ariva group name (<c>ariva-&lt;service&gt;.&lt;purpose&gt;</c>), else <see cref="Unknown"/>.</summary>
    public static string GroupTag(string group) => group is { Length: <= 120 } && GroupRule().IsMatch(group) ? group : Unknown;

    [System.Text.RegularExpressions.GeneratedRegex("^ariva-[a-z0-9]+(-[a-z0-9]+)*\\.[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex GroupRule();

    public void Dispose() => _meter.Dispose();

    #endregion
}
