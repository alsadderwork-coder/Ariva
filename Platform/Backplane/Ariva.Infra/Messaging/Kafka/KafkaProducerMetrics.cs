using System.Diagnostics.Metrics;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Producer metrics (meter <c>Ariva.Kafka</c>, ARV-167): the host's producer raising a fatal client error and being
/// recreated after one. One counter, <c>ariva.kafka.producer_fatal</c>, with a <c>kind</c> of <see cref="FatalErrorKind"/>
/// or <see cref="RecreatedKind"/>; no tag comes from a message. A fatal error with no recreation after it, or recreations
/// that keep coming, is the signal to alert on (a misconfiguration such as a missing IdempotentWrite ACL).
/// </summary>
public sealed class KafkaProducerMetrics : IDisposable
{
    public const string MeterName = KafkaProduce.ActivitySourceName;
    public const string FatalName = "ariva.kafka.producer_fatal";

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

    public void Dispose() => _meter.Dispose();

    #endregion
}
