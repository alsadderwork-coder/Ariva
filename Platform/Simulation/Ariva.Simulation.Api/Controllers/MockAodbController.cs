using System.Globalization;
using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Aodb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ariva.Simulation.Api.Controllers;

/// <summary>
/// The emulated AODB's ACRIS flights (ARV-029), the partner of Ariva's ACRIS pull (ARV-045): the site's schedule as
/// known at the last demo minute played, with <c>Last-Modified</c>, and 304 for an <c>If-Modified-Since</c> at or after
/// it. Authenticated by the API key Ariva's outbound endpoint presents. <c>aodb/acris/flights</c> is the reference
/// site's (DMO); ARV-139b: <c>aodb/sites/{site}/acris/flights</c> is any scenario site's (DMO, AUH-TA), 404 for another.
/// </summary>
[ApiController]
[Route("aodb")]
public sealed class MockAodbController(AodbEmulators aodbs) : ControllerBase
{
    [HttpGet("acris/flights")]
    [Authorize(Policy = MockPartnerSchemes.AodbPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult Flights() => Answer(aodbs.Reference);

    [HttpGet("sites/{site}/acris/flights")]
    [Authorize(Policy = MockPartnerSchemes.AodbPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult SiteFlights(string site) => aodbs.Of(site) is { } aodb ? Answer(aodb) : NotFound();

    private IActionResult Answer(AodbEmulator aodb)
    {
        var (body, modified) = aodb.Acris();
        if (modified is { } at)
        {
            if (Request.GetTypedHeaders().IfModifiedSince is { } since && since >= at)
                return StatusCode(StatusCodes.Status304NotModified);
            Response.Headers[HeaderNames.LastModified] = at.ToString("R", CultureInfo.InvariantCulture);
        }

        return File(body, "application/json");
    }
}
