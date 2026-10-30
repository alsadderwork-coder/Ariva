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
/// <c>kind</c> (a <c>QueueRejections</c> property: BufferFull, DeviceShareFull, ForcedAdvances, Late and the others; or, counting
/// declared line names rather than events, the zone's <c>DeclaredLineNotInZone</c>, ARV-259e5: a fixed set either way) and the
/// <c>site</c> code, added once a checkpoint has written the zone's outputs. <see cref="CheckpointAgeName"/> (ARV-258) is the
/// seconds since this instance's queue engine last wrote everything it owed (a checkpoint, or nothing to write), tagged with the
/// consumer <c>group</c>: a checkpoint that keeps failing while the database answers (a statement timeout, a lock wait, a
/// zone's state above its bound) makes it grow, which the lag cannot show once reading pauses. Reported from the worker's
/// start until it stops. <see cref="BinsOverdueName"/> (ARV-259b) is the queue bins still provisional past their maturity, by
/// <c>site</c>, from the last bin maturity sweep: only the replica that won the sweep's lock computes it, and reports it for two
/// sweep intervals, so a dashboard takes the largest copy of each site, never the sum. <see cref="ZonesUnknownName"/> (ARV-259e3,
/// formulas F11) is the queue zones this instance holds that are Unknown now (an entry or exit line without a live covering
/// device) and still in use (<c>ZoneProcessor.UnknownInUse</c>), by <c>site</c>: every site it holds a zone of, 0 when none
/// is Unknown, at most <see cref="MaxSites"/> (those with the most Unknown first), measured with the lag. Each instance reports
/// only its own zones, so the instances of a group add up; a measurement older than its freshness window, or one from a worker
/// that stopped, is not reported.
/// </summary>
public sealed class StreamMetrics : IDisposable
{
    public const string MeterName = "Ariva.Stream";
    public const string ConsumerLagName = "ariva.stream.consumer_lag";
    public const string QueueRefusedName = "ariva.stream.queue_refused";
    public const string CheckpointAgeName = "ariva.stream.checkpoint_age_seconds";
    public const string BinsOverdueName = "ariva.stream.bins_overdue";
    public const string ZonesUnknownName = "ariva.stream.zones_unknown";

    /// <summary>The sites one reading of <see cref="ZonesUnknownName"/> carries at most.</summary>
    public const int MaxSites = 100;

    #region Fields

    private readonly Meter _meter;
    private readonly Counter<long> _refused;
    private readonly TimeProvider _time;
    private volatile Snapshot _last;
    private volatile CheckpointState _checkpoint;
    private volatile OverdueState _overdue;
    private volatile UnknownState _unknown;

    #endregion

    private sealed record Snapshot(string Group, IReadOnlyList<(string Topic, long Lag)> Lags, DateTimeOffset At, TimeSpan FreshFor);

    private sealed record CheckpointState(string Group, DateTimeOffset At);

    private sealed record OverdueState(IReadOnlyList<(string Site, int Count)> Sites, DateTimeOffset At, TimeSpan FreshFor);

    private sealed record UnknownState(IReadOnlyList<(string Site, int Count)> Sites, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public StreamMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(ConsumerLagName, Observe, unit: "{record}",
            description: "Records behind the end of each sensing topic for the partitions this Stream instance owns");
        _refused = _meter.CreateCounter<long>(QueueRefusedName, unit: "{event}",
            description: "Events a zone's queue engine refused or applied early, by kind and site");
        _meter.CreateObservableGauge(CheckpointAgeName, ObserveCheckpointAge, unit: "s",
            description: "Seconds since this Stream instance's queue engine last wrote everything it owed (a checkpoint, or nothing to write)");
        _meter.CreateObservableGauge(BinsOverdueName, ObserveOverdue, unit: "{bin}",
            description: "Queue bins still provisional past their maturity (bin end, the censoring timeout and the lateness), by site, at the last bin maturity sweep");
        _meter.CreateObservableGauge(ZonesUnknownName, ObserveUnknown, unit: "{zone}",
            description: "Queue zones this Stream instance holds that are Unknown now (an entry or exit line without a live covering device) and still track a device, by site");
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

    /// <summary>
    /// ARV-258: <paramref name="group"/>'s queue engine has written everything it owed now (a checkpoint that succeeded, or
    /// nothing read since the partitions were assigned); the checkpoint age counts from here.
    /// </summary>
    public void Checkpointed(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        _checkpoint = new CheckpointState(group, _time.GetUtcNow());
    }

    /// <summary>
    /// ARV-259b: the sites with queue bins still provisional past their maturity and their counts, from a bin maturity sweep
    /// this replica just ran, reported for <paramref name="freshFor"/> (two sweep intervals). A site left out has none.
    /// </summary>
    public void OverdueBins(IReadOnlyList<(string Site, int Count)> sites, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(sites);
        _overdue = new OverdueState([.. sites], _time.GetUtcNow(), freshFor);
    }

    /// <summary>
    /// ARV-259e3: the zones this instance holds, each with its site and whether it is Unknown now, reported by site for
    /// <paramref name="freshFor"/> (see <see cref="UnknownBySite"/>).
    /// </summary>
    public void ZonesUnknown(IEnumerable<(string Site, bool Unknown)> zones, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _unknown = new UnknownState(UnknownBySite(zones), _time.GetUtcNow(), freshFor);
    }

    /// <summary>Nothing more to report from the queue engine (the worker stopped); the bin maturity sweep's copy is its own.</summary>
    public void Clear()
    {
        _last = null;
        _checkpoint = null;
        _unknown = null;
    }

    /// <summary>Nothing more to report from the bin maturity sweep (it stopped).</summary>
    public void ClearOverdueBins() => _overdue = null;

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

    /// <summary>
    /// The zones Unknown by site: every site with a zone, 0 when none of its zones is Unknown; a zone whose site is not a site
    /// code (<see cref="Ariva.Core.Domain.Entities.Site.IsValidCode"/>) is left out, so a tag carries a site code only (the
    /// stream holds zones of published profiles only; this does not rely on it); at most <see cref="MaxSites"/> sites, those with
    /// the most Unknown zones first, then by code; in code order.
    /// </summary>
    public static IReadOnlyList<(string Site, int Count)> UnknownBySite(IEnumerable<(string Site, bool Unknown)> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (site, unknown) in zones)
        {
            if (!Ariva.Core.Domain.Entities.Site.IsValidCode(site))
                continue;
            counts[site] = counts.GetValueOrDefault(site) + (unknown ? 1 : 0);
        }

        return [.. counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(MaxSites)
            .OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, c.Value))];
    }

    private IEnumerable<Measurement<long>> Observe()
    {
        var last = _last;
        if (last is null || _time.GetUtcNow() - last.At > last.FreshFor)
            return [];
        return last.Lags.Select(l => new Measurement<long>(l.Lag, new KeyValuePair<string, object>("topic", l.Topic),
            new KeyValuePair<string, object>("group", last.Group)));
    }

    private IEnumerable<Measurement<int>> ObserveOverdue()
    {
        var overdue = _overdue;
        if (overdue is null || _time.GetUtcNow() - overdue.At > overdue.FreshFor)
            return [];
        return overdue.Sites.Select(s => new Measurement<int>(s.Count, new KeyValuePair<string, object>("site", s.Site)));
    }

    private IEnumerable<Measurement<int>> ObserveUnknown()
    {
        var unknown = _unknown;
        if (unknown is null || _time.GetUtcNow() - unknown.At > unknown.FreshFor)
            return [];
        return unknown.Sites.Select(s => new Measurement<int>(s.Count, new KeyValuePair<string, object>("site", s.Site)));
    }

    private IEnumerable<Measurement<double>> ObserveCheckpointAge()
    {
        var checkpoint = _checkpoint;
        if (checkpoint is null)
            return [];
        return [new Measurement<double>(Math.Max(0, (_time.GetUtcNow() - checkpoint.At).TotalSeconds), new KeyValuePair<string, object>("group", checkpoint.Group))];
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
