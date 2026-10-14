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
/// </summary>
public sealed class KafkaProducerMetrics : IDisposable
{
    public const string MeterName = KafkaProduce.ActivitySourceName;
    public const string FatalName = "ariva.kafka.producer_fatal";

    public const string ConsumedTopicMissingName = "ariva.kafka.consumed_topic_missing";

    public const string FatalErrorKind = "fatal_error";
    public const string RecreatedKind = "recreated";

    #region Fields

    private readonly Meter _meter;
    private readonly Counter<long> _fatal;

    #endregion

    #region Constructors

    public KafkaProducerMetrics(IMeterFactory meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _fatal = _meter.CreateCounter<long>(FatalName,
            description: "Fatal Kafka producer errors and the producer recreations after them");
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

    public void Dispose() => _meter.Dispose();

    #endregion
}
