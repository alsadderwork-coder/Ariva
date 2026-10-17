using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Services.Quality;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Quality;

/// <summary>
/// A site's availability (ARV-118, formulas F18): per local day, per week and over the range, the ledger's minutes and
/// available operating minutes over operating minutes. <c>from</c> and <c>to</c> are the site's local dates (yyyy-MM-dd),
/// inclusive, at most 92 days. A site the caller cannot see answers 404 like one that does not exist, before the query is
/// checked; an invalid query answers 400 without repeating it.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/availability")]
[SiteScoped]
public sealed class AvailabilityController(ISvcAvailability availability) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewDataQuality))]
    [ProducesResponseType<AvailabilityViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, [FromQuery] string from, [FromQuery] string to, CancellationToken ct)
    {
        // Read as text and parsed by the criteria: MVC's binding error would quote a bad date back (CWE-501, no echo).
        var result = await availability.GetAsync(siteCode, new AvailabilityCriteria(from, to), ct);
        if (!result.HasErrors)
            return Ok(result.Data);
        var error = result.ErrorMessages.FirstOrDefault();
        return error is null or AvailabilityErrors.NotFound
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: error);
    }
}
