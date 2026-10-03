using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Services.Live;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Live;

/// <summary>
/// The live desk states of a site (ARV-055): each desk's latest closed minute within the last 15 minutes and the state
/// that held most of it. Check-in counters and security lanes appear for callers with <c>AirportDesks.View</c>, immigration
/// and emigration desks and e-gates for callers with <c>BorderDesks.View</c> (border data); a caller with neither gets an
/// empty list. A site the caller cannot see answers 404, like one that does not exist.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/desk-states")]
[SiteScoped]
public sealed class DeskStatesController(ISvcDeskStates deskStates) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewLiveQueue))]
    [ProducesResponseType<DeskStatesViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await deskStates.GetAsync(siteCode, ct));
}
