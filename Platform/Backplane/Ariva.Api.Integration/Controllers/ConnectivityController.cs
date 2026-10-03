using Ariva.Api.Common.Security;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Integration;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Integration.Controllers;

/// <summary>
/// Connectivity checks for integrators (ARV-042): with a token, does this client hold the scope for this site? 200 with
/// who the caller is; 401 without a valid token (or a fresh TOTP code when its policy needs one); 403 for a scope or a
/// site the client does not hold. They read and change nothing, and every call is recorded like any other. The route's
/// site is checked against the token's sites by [IntegrationScope] (IntegrationScopeHandler), before the action runs; the
/// actions themselves do not use it.
/// </summary>
[ApiController]
[Route("api/v1/integration/sites/{siteCode}")]
public sealed class ConnectivityController : ControllerBase
{
    [HttpGet("flights/check")]
    [IntegrationScope(IntegrationScopes.FlightsWrite)]
    [ProducesResponseType<IntegrationCallerViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Flights() => Ok(IntegrationAuthentication.CallerOf(HttpContext));

    [HttpGet("immigration/check")]
    [IntegrationScope(IntegrationScopes.ImmigrationWrite)]
    [ProducesResponseType<IntegrationCallerViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Immigration() => Ok(IntegrationAuthentication.CallerOf(HttpContext));
}
