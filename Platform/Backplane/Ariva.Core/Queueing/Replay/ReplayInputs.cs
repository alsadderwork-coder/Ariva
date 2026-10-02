using Ariva.Core.Sensing;

namespace Ariva.Core.Queueing.Replay;

/// <summary>
/// One archived sensing batch (ARV-026) put back together for a replay (ARV-036): its rows in their original order and
/// what the batch said about its device. <see cref="ToBatch"/> gives the batch the queue stream processed.
/// </summary>
public sealed record ArchivedBatch(
    Guid BatchId,
    SensingKind Kind,
    string SiteCode,
    string QueueZoneName,
    Guid DeviceId,
    string DeviceCode,
    DateTime ReceivedUtc,
    bool Commissioned,
    IReadOnlyList<ArchivedSensingEvent> Events)
{
    public string ZoneKey => ZoneKeys.For(SiteCode, QueueZoneName);

    /// <summary>
    /// The archive's rows of each batch, the rows of a batch in their original order. A batch is its id, kind, device and
    /// receive time together, so rows that share an id but not the rest (an id reused beyond the archive's three-day
    /// check, or a forged row) never mix kinds or move to another batch's time (CWE-501).
    /// </summary>
    public static IReadOnlyList<ArchivedBatch> Group(IEnumerable<ArchivedSensingEvent> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            .. rows.Where(r => r is not null).GroupBy(r => (r.BatchId, r.Kind, r.DeviceId, r.DeviceCode, r.ReceivedUtc, r.SiteCode, r.QueueZoneName, r.Commissioned)).Select(g =>
            {
                var ordered = g.OrderBy(r => r.Ordinal).ToList();
                var first = ordered[0];
                return new ArchivedBatch(g.Key.BatchId, first.Kind, first.SiteCode, first.QueueZoneName, first.DeviceId, first.DeviceCode, first.ReceivedUtc,
                    first.Commissioned, ordered);
            })
        ];
    }

    public SensingBatch ToBatch()
    {
        // Rows of another kind than the batch's are left out rather than cast (a batch is grouped by kind anyway).
        IReadOnlyList<Sensed<T>> Of<T>() where T : CanonicalEvent =>
            [.. Events.Where(e => e is not null && e.Kind == Kind).Select(e => e.ToCanonical() is T canonical ? new Sensed<T>(canonical, e.TimeUtc, e.Flags) : null).Where(e => e is not null)];
        SensingBatch batch = Kind switch
        {
            SensingKind.Track => new TrackSampleBatch { Samples = Of<TrackPosition>() },
            SensingKind.Crossing => new VendorLineCrossingBatch { Crossings = Of<LineCrossing>() },
            SensingKind.Occupancy => new ZoneOccupancyBatch { Occupancy = Of<ZoneOccupancy>() },
            _ => new IntervalCountBatch { Intervals = Of<IntervalCount>() }
        };
        batch.Id = BatchId;
        batch.OccurredOn = ReceivedUtc;
        batch.DeviceId = DeviceId;
        batch.DeviceCode = DeviceCode;
        batch.SiteCode = SiteCode;
        batch.QueueZoneName = QueueZoneName;
        batch.Dialect = "archive";
        batch.Commissioned = Commissioned;
        batch.ReceivedUtc = ReceivedUtc;
        return batch;
    }
}

/// <summary>One input of a replay: an archived sensing batch or an archived device health report.</summary>
public sealed record ReplayInput
{
    private ReplayInput(ArchivedBatch batch, ArchivedDeviceHealth health) => (Batch, Health) = (batch, health);

    public ArchivedBatch Batch { get; }

    public ArchivedDeviceHealth Health { get; }

    public static ReplayInput Of(ArchivedBatch batch) => new(batch ?? throw new ArgumentNullException(nameof(batch)), null);

    public static ReplayInput Of(ArchivedDeviceHealth health) => new(null, health ?? throw new ArgumentNullException(nameof(health)));

    public DateTime ReceivedUtc => Batch?.ReceivedUtc ?? Health.ReceivedUtc;

    public Guid Id => Batch?.BatchId ?? Health.EventId;

    public string ZoneKey => Batch?.ZoneKey ?? ZoneKeys.For(Health.SiteCode, Health.QueueZoneName);

    /// <summary>
    /// The queue stream's order for records that share a receive time: crossings, occupancy, intervals, tracks, health
    /// (the stream worker's topic order), then the id.
    /// </summary>
    public int Rank => Batch?.Kind switch
    {
        SensingKind.Crossing => 0,
        SensingKind.Occupancy => 1,
        SensingKind.Interval => 2,
        SensingKind.Track => 3,
        _ => 4
    };

    public SensingBatch ToBatch() => Batch is not null ? Batch.ToBatch() : Health.ToBatch();

    /// <summary>The inputs in the order the stream applies them: receive time, then <see cref="Rank"/>, then id.</summary>
    public static IReadOnlyList<ReplayInput> InOrder(IEnumerable<ArchivedBatch> batches, IEnumerable<ArchivedDeviceHealth> health) =>
    [
        .. (batches ?? []).Where(b => b is not null).Select(Of).Concat((health ?? []).Where(h => h is not null).Select(Of))
            .OrderBy(i => i.ReceivedUtc).ThenBy(i => i.Rank).ThenBy(i => i.Id)
    ];
}
