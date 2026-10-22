using System.Diagnostics.Metrics;

namespace Ariva.Infra.Streaming;

/// <summary>
/// The queue stream worker's metrics (meter <c>Ariva.Stream</c>, ARV-125a): <see cref="ConsumerLagName"/>, the records
/// behind the end of each sensing topic for the partitions this instance owns, summed per <c>topic</c> (a
/// <c>KafkaTopics</c> constant) and tagged with the consumer <c>group</c>. The worker measures it at most every checkpoint
/// interval from what its consumer knows (the high watermark of each owned partition, from the fetch responses, less the
/// next offset it reads), so it costs no broker call. Each instance reports only its own partitions, so the instances of a
/// group add up; a measurement older than its freshness window, or one from a worker that stopped, is not reported.
/// <see cref="QueueRefusedName"/> (ARV-231) counts the events each zone's queue engine refused or applied early, tagged with the
/// <c>kind</c> (a <c>QueueRejections</c> property: BufferFull, DeviceShareFull, ForcedAdvances, Late and the others) and the
/// <c>site</c> code, added once a checkpoint has written the zone's outputs.
/// </summary>
public sealed class StreamMetrics : IDisposable
{
    public const string MeterName = "Ariva.Stream";
    public const string ConsumerLagName = "ariva.stream.consumer_lag";
    public const string QueueRefusedName = "ariva.stream.queue_refused";

    #region Fields

    private readonly Meter _meter;
    private readonly Counter<long> _refused;
    private readonly TimeProvider _time;
    private volatile Snapshot _last;

    #endregion

    private sealed record Snapshot(string Group, IReadOnlyList<(string Topic, long Lag)> Lags, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public StreamMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(ConsumerLagName, Observe, unit: "{record}",
            description: "Records behind the end of each sensing topic for the partitions this Stream instance owns");
        _refused = _meter.CreateCounter<long>(QueueRefusedName, unit: "{event}",
            description: "Events a zone's queue engine refused or applied early, by kind and site");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>Reports the lag per topic of <paramref name="group"/>, valid for <paramref name="freshFor"/>.</summary>
    public void Report(string group, IReadOnlyDictionary<string, long> lagByTopic, TimeSpan freshFor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(lagByTopic);
        _last = new Snapshot(group, [.. lagByTopic.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => (l.Key, l.Value))], _time.GetUtcNow(), freshFor);
    }

    /// <summary>Counts <paramref name="count"/> events of <paramref name="kind"/> refused at <paramref name="site"/> (ARV-231).</summary>
    public void Refused(string site, string kind, long count)
    {
        if (count > 0)
            _refused.Add(count, new KeyValuePair<string, object>("kind", kind), new KeyValuePair<string, object>("site", site));
    }

    /// <summary>Nothing more to report (the worker stopped).</summary>
    public void Clear() => _last = null;

    /// <summary>
    /// The lag per topic of the given partitions: the high watermark less the next offset to read, at least 0, summed over
    /// each topic's partitions; a partition whose high watermark or position is not known yet (negative) is left out, and so
    /// is a topic none of whose partitions is known.
    /// </summary>
    public static IReadOnlyDictionary<string, long> LagByTopic(IEnumerable<(string Topic, long High, long Next)> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        var lags = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (topic, high, next) in partitions)
        {
            if (topic is null || high < 0 || next < 0)
                continue;
            lags[topic] = lags.GetValueOrDefault(topic) + Math.Max(0, high - next);
        }

        return lags;
    }

    private IEnumerable<Measurement<long>> Observe()
    {
        var last = _last;
        if (last is null || _time.GetUtcNow() - last.At > last.FreshFor)
            return [];
        return last.Lags.Select(l => new Measurement<long>(l.Lag, new KeyValuePair<string, object>("topic", l.Topic),
            new KeyValuePair<string, object>("group", last.Group)));
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
