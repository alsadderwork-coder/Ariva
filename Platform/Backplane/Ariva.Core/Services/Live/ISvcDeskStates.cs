using Fluentx;

namespace Ariva.Core.Services.Live;

/// <summary>
/// One desk's latest closed minute (ARV-055): its checkpoint, the state that held most of the minute (Closed, Idle,
/// Serving, Paused or Unknown, from Ariva.Api.Stream's desk engine), the transactions and whether the minute was degraded.
/// </summary>
public sealed record DeskStateViewModel(
    string Checkpoint,
    string CheckpointKind,
    string Desk,
    string DeskKind,
    string Lane,
    DateTime MinuteUtc,
    string State,
    int Transactions,
    bool Degraded);

/// <summary>
/// The desk states of a site at <see cref="AsOfUtc"/>: each desk's latest minute within the last
/// <see cref="ISvcDeskStates.WindowMinutes"/> minutes. <see cref="AirportIncluded"/> says whether check-in counters and
/// security lanes are in the list (<c>AirportDesks.View</c>), <see cref="BorderIncluded"/> whether immigration and emigration
/// desks and e-gates are (border data, <c>BorderDesks.View</c>); a caller with neither gets an empty list.
/// <see cref="Truncated"/> says there were more than 2,000.
/// </summary>
public sealed record DeskStatesViewModel(string SiteCode, DateTime AsOfUtc, IReadOnlyList<DeskStateViewModel> Desks, bool AirportIncluded, bool BorderIncluded, bool Truncated);

/// <summary>The live desk states of a site (ARV-055), limited to the caller's sites and to the desk kinds the caller's roles may see.</summary>
public interface ISvcDeskStates : ISvcScoped
{
    /// <summary>How far back a desk's latest minute may lie to be shown.</summary>
    const int WindowMinutes = 15;

    /// <summary>At most this many desks.</summary>
    const int MaxDesks = 2000;

    Task<Result<DeskStatesViewModel>> GetAsync(string siteCode, CancellationToken ct = default);
}
