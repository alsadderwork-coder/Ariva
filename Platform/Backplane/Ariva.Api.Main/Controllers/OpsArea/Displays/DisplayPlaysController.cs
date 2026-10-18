using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.OpsArea.Displays;

/// <summary>
/// The proof of play a display player sends (ARV-144): batches of 1 to 100 plays, authenticated with the display's code in
/// the query and its credential in the <c>X-Ariva-Display-Key</c> header only (<see cref="DisplayAuthentication"/>; a user
/// token answers 401), at most 64 KB a batch and a few batches a minute per display (the <c>display-plays</c> policy). The
/// server takes only plays of advertisements on the playlist that applied to that display when they started, and counts a
/// batch sent twice once.
/// </summary>
[ApiController]
[Route("api/v1/display")]
[EnableRateLimiting(RateLimitingExtensions.DisplayPlaysPolicy)]
public sealed class DisplayPlaysController(ISvcAdvertisingPlays plays) : ControllerBase
{
    /// <summary>A batch of plays is a few kilobytes: 100 plays of about 130 bytes each, with room to spare.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    [HttpPost("plays")]
    [DisplayAuthenticated]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType<AdvertisingPlaysViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Record([FromBody] AdvertisingPlaysRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var (id, prefix) = DisplayAuthentication.DisplayOf(User);
        if (id is not { } displayId)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated");
        var result = await plays.RecordAsync(displayId, prefix, request, ct);
        if (!result.HasErrors)
            return Ok(result.Data);
        return result.ErrorMessages.Contains(Core.Services.Topology.TopologyErrors.NotFound)
            ? Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated")
            : ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["plays"] = [.. result.ErrorMessages] }));
    }
}
