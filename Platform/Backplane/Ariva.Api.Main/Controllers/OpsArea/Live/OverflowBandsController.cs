using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Services.Live;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Live;

/// <summary>
/// The overflow bands of a site's queue zones (ARV-120): for each queue zone of the published profile that has overflow
/// bands, each band's latest minute with a reading within the last 6 minutes, the most people it held then, and whether it
/// is Occupied, Empty or NotHeard; the zone is Occupied when any band is. Zone-level occupancy for the live and immigration
/// screens (<c>LiveQueue.View</c>, as the live hub's zone snapshots). A site the caller cannot see answers 404, like one
/// that does not exist. A published profile with more than 500 bands answers 409 with a fixed text: refused, never cut short
/// (CWE-120). No query string is read.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/overflow-bands")]
[SiteScoped]
public sealed class OverflowBandsController(ISvcOverflowBands overflowBands) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewLiveQueue))]
    [ProducesResponseType<OverflowBandsViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Get(string siteCode, CancellationToken ct)
    {
        var result = await overflowBands.GetAsync(siteCode, ct);
        if (!result.HasErrors)
            return Ok(result.Data);
        // The bound's refusal has its fixed text; anything else is a site the caller cannot see or that does not exist.
        return result.ErrorMessages.FirstOrDefault() == OverflowBandErrors.TooMany
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: OverflowBandErrors.TooMany)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }
}
