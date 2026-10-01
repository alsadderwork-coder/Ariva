using Ariva.Api.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.UnitTests.Security;

/// <summary>Fixture: the same actions with [SiteScoped] on the controller; nothing to report.</summary>
[SiteScoped]
[Authorize]
[Route("fixture/scoped/{terminalId:guid}")]
public sealed class FixtureScopedController : ControllerBase
{
    [HttpGet("site/{siteCode}")]
    public IActionResult ByCode(string siteCode) => Ok(siteCode);
}
