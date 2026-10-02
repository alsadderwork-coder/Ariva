namespace Ariva.Core.Sensing;

/// <summary>The kind of an archived sensing event (ARV-026); stored by name.</summary>
public enum SensingKind
{
    Track,
    Crossing,
    Occupancy,
    Interval
}

/// <summary>
/// One canonical event as archived (ARV-026): where and when, from which device and batch, its flags, and the fields of
/// its kind (a track's id and position, a crossing's line and direction, an occupancy's zone and count, an interval's
/// line, counts and start). <see cref="ToCanonical"/> gives the canonical event back for replay.
/// </summary>
public sealed record ArchivedSensingEvent(
    DateTime TimeUtc,
    string SiteCode,
    string QueueZoneName,
    SensingKind Kind,
    Guid BatchId,
    int Ordinal,
    Guid DeviceId,
    string DeviceCode,
    DateTime ReceivedUtc,
    SensedFlags Flags,
    bool Commissioned,
    string TrackId = null,
    double? X = null,
    double? Y = null,
    double? HeightMetres = null,
    string Name = null,
    Ariva.Core.Domain.Enums.CrossingDirection? Direction = null,
    int? Count = null,
    int? In = null,
    int? Out = null,
    DateTime? FromUtc = null)
{
    public CanonicalEvent ToCanonical() => Kind switch
    {
        SensingKind.Track => new TrackPosition(TrackId, X ?? 0, Y ?? 0, HeightMetres, TimeUtc),
        SensingKind.Crossing => new LineCrossing(Name, Direction ?? Ariva.Core.Domain.Enums.CrossingDirection.In, TrackId, TimeUtc),
        SensingKind.Occupancy => new ZoneOccupancy(Name, Count ?? 0, TimeUtc),
        _ => new IntervalCount(Name, In ?? 0, Out ?? 0, FromUtc ?? TimeUtc, TimeUtc)
    };
}

/// <summary>A replay request: one queue zone of a site over a time range (at most 31 days), optionally some kinds only.</summary>
public sealed record SensingReplayQuery(string SiteCode, string QueueZoneName, DateTime FromUtc, DateTime ToUtc, IReadOnlySet<SensingKind> Kinds = null)
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);
}

/// <summary>What a write did: batches archived, batches skipped as already archived, events written, and events skipped as out of bounds.</summary>
public sealed record SensingArchiveWrite(int Batches, int Duplicates, int Events, int Skipped = 0);

/// <summary>
/// The raw sensing event archive (ARV-026): every canonical event Ingest accepted, written by binary COPY in batches,
/// each sensing batch at most once; read back in time order for one zone and range, for replay and recomputation.
/// </summary>
public interface ISensingArchive
{
    Task<SensingArchiveWrite> WriteAsync(IReadOnlyList<SensingBatch> batches, CancellationToken ct = default);

    IAsyncEnumerable<ArchivedSensingEvent> ReadAsync(SensingReplayQuery query, CancellationToken ct = default);
}
