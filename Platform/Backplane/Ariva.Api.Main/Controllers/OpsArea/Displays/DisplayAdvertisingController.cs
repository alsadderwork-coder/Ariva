using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Advertising;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.OpsArea.Displays;

/// <summary>
/// The advertisement a display player fetches (ARV-142a), authenticated as the player's board is (ARV-058: the display's
/// code in the query, its credential in the <c>X-Ariva-Display-Key</c> header; a user token answers 401). There is no
/// public URL for any item. Unlike the library's endpoints it carries no <c>[AdvertisingAllowed]</c>, which judges a user's
/// sites: it applies the advertising rule of ARV-145 to the display itself, on every fetch, and serves only an approved item
/// of the display's own site within its dates while the display is set to advertising; anything else answers 404.
/// </summary>
[ApiController]
[Route("api/v1/display/advertising")]
[EnableRateLimiting(RateLimitingExtensions.DisplayPolicy)]
public sealed class DisplayAdvertisingController(ISvcAdvertisingMedia media) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [DisplayAuthenticated]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Content(Guid id, CancellationToken ct)
    {
        var (displayId, prefix) = DisplayAuthentication.DisplayOf(User);
        if (displayId is not { } display)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not authenticated");
        var result = await media.OpenForDisplayAsync(display, prefix, id, ct);
        return result.HasErrors
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : AdvertisingMediaController.MediaFile(this, id, result.Data);
    }
}
