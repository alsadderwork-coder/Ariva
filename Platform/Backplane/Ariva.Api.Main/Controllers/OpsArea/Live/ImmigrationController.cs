using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Services.Border;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Live;

/// <summary>
/// The immigration view of a site (ARV-057): lane aggregates of the border desks and e-gate totals over the last 15
/// minutes of AMAN's interval aggregates, per hall (arrivals, departures); per-desk and per-gate figures only for callers
/// with <c>BorderDesks.View</c>. A site the caller cannot see answers 404, like one that does not exist.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/immigration")]
[SiteScoped]
public sealed class ImmigrationController(ISvcImmigrationView immigration) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewImmigration))]
    [ProducesResponseType<ImmigrationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await immigration.GetAsync(siteCode, ct));
}
