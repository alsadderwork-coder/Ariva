using System.Globalization;
using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Flights;
using Ariva.Core.Services.Flights;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Flights;

/// <summary>
/// The arrival-wave projection of a site (ARV-047, formulas.md F14): flights landing within <c>minutes</c> (5 to 120,
/// default 30) and those landed whose passengers are still reaching the hall, each with its lane split (AMAN's lane
/// demand when received, the default mix otherwise), and the predicted hall arrivals per minute and lane. A site the
/// caller cannot see answers 404, like one that does not exist.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/arrival-wave")]
[SiteScoped]
public sealed class ArrivalWaveController(ISvcArrivalWave arrivalWave) : ControllerBase
{
    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewArrivalWave))]
    [ProducesResponseType<ArrivalWaveViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, [FromQuery] string minutes, CancellationToken ct)
    {
        // Read here rather than bound as an int: MVC's binding error would quote the value back (CWE-501, no echo).
        var window = ArrivalWave.DefaultWindow;
        if (minutes is not null && !int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out window))
            window = -1;
        return TopologyAnswers.Ok(this, await arrivalWave.GetAsync(siteCode, window, ct));
    }
}
