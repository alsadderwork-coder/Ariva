using Ariva.Core.Domain.Criteria;
using Fluentx;

namespace Ariva.Core.Services.Quality;

public static class ZoneHealthErrors
{
    /// <summary>A site the caller does not reach, or that does not exist, or a zone that is no queue zone of it: one answer (CWE-204).</summary>
    public const string NotFound = "The site or zone does not exist.";
}

/// <summary>
/// The continuous health checks of one bin of a queue zone (F18, ARV-114a), its latest revision: the conservation residual
/// with the sensor occupancy at the bin's start and end (null when either is unknown), the tracks that entered and what
/// became of them with the completion rate (null when none entered), and the occupancy minutes checked against the
/// zones' physical capacity. Status Provisional values may still change; Final ones change only through a revision.
/// </summary>
public sealed record ZoneHealthBinViewModel(
    DateTime StartUtc,
    int LengthMinutes,
    int Revision,
    string Status,
    int ZoneProfileVersion,
    long Entries,
    long Exits,
    int? OccupancyStart,
    int? OccupancyEnd,
    long? ConservationResidual,
    long TracksEntered,
    long TracksExited,
    long TracksAbandoned,
    long TracksFragmented,
    long TracksCensored,
    long TracksRejected,
    long TracksOpen,
    double? TrackCompletionRate,
    int OccupancyMinutes,
    int CapacityMinutes,
    int MinutesOutsideCapacity);

/// <summary>
/// A queue zone's health bins whose start lies in [FromUtc, ToUtc), in start order; <see cref="Truncated"/> when the range
/// held more than <see cref="ISvcZoneHealth.MaxBins"/> (shorter bins than 15 minutes), the earliest then being returned.
/// </summary>
public sealed record ZoneHealthViewModel(string SiteCode, string Zone, DateTime FromUtc, DateTime ToUtc, IReadOnlyList<ZoneHealthBinViewModel> Bins, bool Truncated);

/// <summary>
/// The stored health checks of a queue zone (ARV-114a, zone_health_bin), for the caller's sites (ISiteScope): a site the
/// caller does not reach answers like one that does not exist, before anything else is checked.
/// </summary>
public interface ISvcZoneHealth : ISvcScoped
{
    /// <summary>Bins one answer holds at most: 31 days of 15-minute bins (2,976) fit.</summary>
    const int MaxBins = 3_000;

    Task<Result<ZoneHealthViewModel>> SearchAsync(string siteCode, ZoneHealthCriteria criteria, CancellationToken ct = default);
}
