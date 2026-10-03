using Ariva.Api.Common.Security;
using Ariva.Core.Integration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.UnitTests.Security;

/// <summary>
/// Fixture: Integration API actions ([IntegrationScope], ARV-042). The scope handler checks the client's sites against
/// the <c>{siteCode}</c> route token only, so any other site reference (a body property, a query parameter, another
/// route token) would reach the service unchecked; SiteScopeTests proves the rule reports each one.
/// </summary>
[Route("fixture/integration/sites/{siteCode}")]
public sealed class FixtureIntegrationController : ControllerBase
{
    [HttpGet("ok")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    public IActionResult RouteOnly(string siteCode) => Ok(siteCode);

    [HttpPost("body")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    public IActionResult Body([FromBody] FixtureSiteRequest request) => Ok(request);

    [HttpGet("query")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    public IActionResult Query(string airportCode) => Ok(airportCode);

    [HttpGet("route")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    public IActionResult ExplicitRoute([FromRoute] string siteCode) => Ok(siteCode);

    [HttpGet("header")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    public IActionResult Header([FromHeader(Name = "X-Site")] string siteCode) => Ok(siteCode);

    [HttpGet("terminals/{terminalId}")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    public IActionResult Terminal() => Ok();
}

/// <summary>Fixture: an Integration API action whose route has no <c>{siteCode}</c> but whose parameter names one (bound from the query).</summary>
[Route("fixture/integration/flat")]
public sealed class FixtureIntegrationFlatController : ControllerBase
{
    [HttpGet]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    public IActionResult QuerySite(string siteCode) => Ok(siteCode);
}
