using System.ComponentModel.DataAnnotations;
using Fluentx;

namespace Ariva.Core.Services.Displays;

/// <summary>
/// One play as a display player reports it (ARV-144): the advertisement (<c>MediaId</c>), when it started (<c>StartedUtc</c>,
/// UTC, by the server's clock as the player keeps it from its boards), how long it was on screen and whether it played its
/// whole slot (<c>Completed</c>) or was cut short.
/// </summary>
public sealed record AdvertisingPlayRequest(Guid MediaId, DateTime StartedUtc, int DurationMilliseconds, bool Completed);

/// <summary>A batch of plays: 1 to 100.</summary>
public sealed record AdvertisingPlaysRequest([Required, MinLength(1), MaxLength(100)] IReadOnlyList<AdvertisingPlayRequest> Plays);

/// <summary>
/// What the server did with a batch: plays taken (<c>Accepted</c>), plays it already had (<c>Duplicates</c>: a batch sent
/// again counts once) and plays it refused (<c>Refused</c>: not what they say, or not of the playlist that applied to the
/// display when they started). The player lets the whole batch go once it has an answer.
/// </summary>
public sealed record AdvertisingPlaysViewModel(int Accepted, int Duplicates, int Refused);

/// <summary>
/// The proof of play of the display players (ARV-144), authenticated by the display's credential only: a display reports its
/// own plays, of advertisements of its own site's playlist that applied to it, and nothing else.
/// </summary>
public interface ISvcAdvertisingPlays
{
    /// <summary>Records a batch for an authenticated display; not found when the display is gone, disabled or has a new credential.</summary>
    Task<Result<AdvertisingPlaysViewModel>> RecordAsync(Guid displayId, string credentialPrefix, AdvertisingPlaysRequest request, CancellationToken ct = default);
}
