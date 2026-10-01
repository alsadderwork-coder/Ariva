using Ariva.Api.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.UnitTests.Security;

/// <summary>Fixture: a controller that takes site references without [SiteScoped]; SiteScopeTests proves the rule reports it.</summary>
[Authorize]
[Route("fixture/terminals/{terminalId:guid}")]
public sealed class FixtureUnscopedController : ControllerBase
{
    [HttpGet("site/{siteCode}")]
    public IActionResult ByCode(string siteCode) => Ok(siteCode);

    [HttpGet("airport")]
    public IActionResult ByAirport(Guid airportId) => Ok(airportId);

    [HttpGet("route")]
    public IActionResult ByRoute() => Ok();
}
