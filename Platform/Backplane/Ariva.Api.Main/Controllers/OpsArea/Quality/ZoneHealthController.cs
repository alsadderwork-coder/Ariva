using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Services.Quality;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Quality;

/// <summary>
/// The continuous health checks of a queue zone (ARV-114a, formulas F18): per bin, the conservation residual, the track
/// completion rate with censored and still-open tracks apart, and the minutes whose occupancy left 0 to the zone's
/// physical capacity. <c>zone</c> is the queue zone's name; <c>from</c> and <c>to</c> bound the bin starts in UTC (ISO 8601
/// ending in Z), at most 31 days apart. A site the caller cannot see answers 404 like one that does not exist, before the
/// query is checked; an invalid query answers 400.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/zone-health")]
[SiteScoped]
public sealed class ZoneHealthController(ISvcZoneHealth health) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewDataQuality))]
    [ProducesResponseType<ZoneHealthViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(string siteCode, [FromQuery] string zone, [FromQuery] string from, [FromQuery] string to, CancellationToken ct)
    {
        // Read as text and parsed by the criteria: MVC's binding error would quote a bad time back (CWE-501, no echo).
        var result = await health.SearchAsync(siteCode, ZoneHealthCriteria.Of(zone, from, to), ct);
        if (!result.HasErrors)
            return Ok(result.Data);
        var error = result.ErrorMessages.FirstOrDefault();
        return error is null or ZoneHealthErrors.NotFound
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: error);
    }
}
