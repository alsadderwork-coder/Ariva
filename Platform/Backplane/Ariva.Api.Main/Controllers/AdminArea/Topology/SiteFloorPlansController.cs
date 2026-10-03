using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// The floor plans of a site's levels (ARV-055): the screens ask once which levels have a plan instead of asking each
/// level and getting 404 for the ones without. A site the caller cannot see answers 404, like one that does not exist.
/// </summary>
[ApiController]
[Route("api/v1/admin/floor-plans")]
[SiteScoped]
public sealed class SiteFloorPlansController(ISvcFloorPlans plans) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.SearchFloorPlan))]
    [ProducesResponseType<IReadOnlyList<FloorPlanViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List([FromQuery] string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await plans.ListAsync(siteCode, ct));
}
