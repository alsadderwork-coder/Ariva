using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Services.Border;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Live;

/// <summary>
/// The indicative staffing recommendation of a site (ARV-181a, formulas F16 pilot version): per arrivals-hall lane, the next
/// 3 hours by 15 minutes (13 intervals): predicted arrivals, desks open now (the desk-state count), recommended open desks
/// and what set them, the shortfall at the lane's desk limit, and the predicted waits with the recommendation and with the
/// desks open now, or why there is none. Border data (the lane split of the arrival wave): <c>ArrivalWaveLanes.View</c>
/// only. A site the caller cannot see answers 404, like one that does not exist; a profile with more than 100
/// arrivals-hall lanes answers 409 with a fixed text: refused, never cut short (CWE-120). No query string is read.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/staffing-recommendation")]
[SiteScoped]
public sealed class StaffingRecommendationController(ISvcStaffingRecommendations staffing) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewArrivalWaveLanes))]
    [ProducesResponseType<StaffingRecommendationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Get(string siteCode, CancellationToken ct)
    {
        var result = await staffing.GetAsync(siteCode, ct);
        if (!result.HasErrors)
            return Ok(result.Data);
        // The bound's refusal has its fixed text; anything else is a site the caller cannot see or that does not exist.
        return result.ErrorMessages.FirstOrDefault() == StaffingErrors.TooManyLanes
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: StaffingErrors.TooManyLanes)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }
}
