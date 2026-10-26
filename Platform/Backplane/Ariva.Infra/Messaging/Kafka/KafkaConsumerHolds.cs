using System.Collections.Concurrent;
using MassTransit;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// ARV-254: how long each rider endpoint has been held by the record it is handling. Each Kafka consumer handles one record at
/// a time (ARV-253), so a record that retries, stalls or waits for a refused dead letter holds every partition its consumer
/// reads. A filter on each endpoint's receive pipe (<see cref="For"/>) notes when each record's receive begins and ends, which
/// spans its whole handling: every attempt, the retry waits and its dead letter (MassTransit runs the retry inside it). The
/// gauge <see cref="KafkaProducerMetrics.ConsumerHoldName"/> reports, per endpoint, the seconds the oldest record in hand has
/// been held (0 when none is), tagged with the <c>topic</c> (a <c>KafkaTopics</c> constant) and the consumer <c>group</c>
/// (<see cref="KafkaProducerMetrics.TopicTag"/>, <see cref="KafkaProducerMetrics.GroupTag"/>), never a value from the record.
/// One instance per host.
/// </summary>
public sealed class KafkaConsumerHolds
{
    #region Fields

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(string Topic, string Group), Endpoint> _endpoints = new();

    #endregion

    public KafkaConsumerHolds(TimeProvider time, KafkaProducerMetrics metrics = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        metrics?.ObserveConsumerHolds(Holds);
    }

    #region Methods

    /// <summary>The receive filter of the endpoint that reads <paramref name="topic"/> as <paramref name="group"/>; one per endpoint.</summary>
    public IFilter<ReceiveContext> For(string topic, string group) =>
        _endpoints.GetOrAdd((KafkaProducerMetrics.TopicTag(topic), KafkaProducerMetrics.GroupTag(group)), _ => new Endpoint(_time));

    /// <summary>Each endpoint's topic and group with the seconds its oldest record in hand has been held, 0 when it holds none.</summary>
    public IEnumerable<(string Topic, string Group, double Seconds)> Holds() =>
        _endpoints.Select(e => (e.Key.Topic, e.Key.Group, e.Value.LongestSeconds())).ToList();

    #endregion

    /// <summary>One endpoint's records in hand, each with the time its receive began.</summary>
    private sealed class Endpoint(TimeProvider time) : IFilter<ReceiveContext>
    {
        private readonly ConcurrentDictionary<long, long> _held = new();
        private long _next;

        public async Task Send(ReceiveContext context, IPipe<ReceiveContext> next)
        {
            ArgumentNullException.ThrowIfNull(next);
            var record = Interlocked.Increment(ref _next);
            _held[record] = time.GetTimestamp();
            try
            {
                await next.Send(context);
            }
            finally
            {
                _held.TryRemove(record, out _);
            }
        }

        public void Probe(ProbeContext context) => context?.CreateFilterScope("ariva-consumer-hold");

        public double LongestSeconds()
        {
            var starts = _held.Values.ToList();
            return starts.Count == 0 ? 0 : time.GetElapsedTime(starts.Min()).TotalSeconds;
        }
    }
}
