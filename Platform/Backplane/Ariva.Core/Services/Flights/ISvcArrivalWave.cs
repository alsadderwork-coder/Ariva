using Ariva.Core.Flights;
using Fluentx;

namespace Ariva.Core.Services.Flights;

/// <summary>
/// Passengers per arrival lane category (CIT, RES, VIS, CRW at manual desks; EGate eligible) and their total. The lane
/// counts are null for a caller who sees totals only (no <c>ArrivalWaveLanes.View</c>).
/// </summary>
public sealed record LaneCountsViewModel(double? Cit, double? Res, double? Vis, double? Crw, double? EGate, double Total)
{
    public static LaneCountsViewModel Of(LaneCounts counts, bool lanes = true)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var total = Math.Round(counts.Total, 2);
        if (!lanes)
            return new(null, null, null, null, null, total);
        var rounded = counts.Rounded();
        return new(rounded.Cit, rounded.Res, rounded.Vis, rounded.Crw, rounded.EGate, total);
    }
}

/// <summary>
/// One arriving flight of the wave: its in-block time and where it came from (<c>OnBlock</c>, <c>Landed</c> plus
/// taxi-in, <c>Estimated</c>, <c>Scheduled</c>), whether it has landed, its passengers (<c>Aman</c>, <c>PaxEstimate</c>,
/// <c>Seats</c>; null when the feeds give none), the lane split (<c>Aman</c> or <c>DefaultMix</c>; null with the lane
/// counts for a caller who sees totals only) and the first and last minute its passengers reach the hall.
/// </summary>
public sealed record ArrivalWaveFlightViewModel(
    string FlightKey,
    string Flight,
    string Origin,
    string Terminal,
    string Stand,
    DateTime ScheduledUtc,
    DateTime InBlockUtc,
    string InBlockSource,
    bool Landed,
    double? Passengers,
    string PassengerSource,
    string LaneSource,
    LaneCountsViewModel Lanes,
    DateTime HallFirstUtc,
    DateTime HallLastUtc);

/// <summary>Hall arrivals in the minute starting at <see cref="MinuteUtc"/>, per lane category.</summary>
public sealed record ArrivalWaveMinuteViewModel(DateTime MinuteUtc, LaneCountsViewModel Lanes);

/// <summary>
/// The arrival-wave projection of a site (ARV-047, formulas.md F14): the flights landing within the window (and those
/// landed whose passengers are still reaching the hall), the predicted hall arrivals per minute and lane from this
/// minute on, the alert window's sum (now + 5 to now + 25 minutes), how many flights have no passenger figure, and
/// whether the site had more arriving legs than are read (<c>Truncated</c>: the latest scheduled were left out).
/// </summary>
public sealed record ArrivalWaveViewModel(
    string SiteCode,
    DateTime NowUtc,
    int WindowMinutes,
    int DelayMinutes,
    IReadOnlyList<ArrivalWaveFlightViewModel> Flights,
    IReadOnlyList<ArrivalWaveMinuteViewModel> Minutes,
    LaneCountsViewModel AlertWindow,
    int FlightsWithoutPassengers,
    bool Truncated);

/// <summary>
/// The arrival-wave projection (ARV-047): a site's arriving legs that are neither cancelled nor diverted, with AMAN's
/// lane demand where it was received (ARV-048), projected by <see cref="ArrivalWave"/>. A site the caller cannot see
/// answers not found, like an unknown one (CWE-863; also checked by the endpoint); the lane split is for callers with
/// <c>ArrivalWaveLanes.View</c> (border roles), totals otherwise.
/// </summary>
public interface ISvcArrivalWave : ISvcScoped
{
    Task<Result<ArrivalWaveViewModel>> GetAsync(string siteCode, int windowMinutes, CancellationToken ct = default);
}
