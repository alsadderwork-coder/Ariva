using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.OpsArea.Displays;

/// <summary>
/// What a display player asks Ariva (ARV-058): its board, authenticated with the display's code in the query and its
/// credential in the <c>X-Ariva-Display-Key</c> header only (<see cref="DisplayAuthentication"/>). A user token answers
/// 401 here. Rate limited per presented credential. Never cached.
/// </summary>
[ApiController]
[Route("api/v1/display")]
[EnableRateLimiting(RateLimitingExtensions.DisplayPolicy)]
public sealed class DisplayBoardController(ISvcDisplayBoard board) : ControllerBase
{
    [HttpGet("board")]
    [DisplayAuthenticated]
    [ProducesResponseType<DisplayBoardViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Board(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var (id, prefix) = DisplayAuthentication.DisplayOf(User);
        var result = id is { } displayId ? await board.GetAsync(displayId, prefix, ct) : null;
        return result is null || result.HasErrors ? Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated") : Ok(result.Data);
    }
}
