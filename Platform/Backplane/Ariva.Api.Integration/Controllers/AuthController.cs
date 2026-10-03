using Ariva.Api.Common.Extensions;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Integration.Controllers;

/// <summary>
/// The integration token exchange (ARV-042, AMAN-compatible): client id, client secret and the current TOTP code for a
/// 15-minute token of the Integration API. Anonymous by nature (allowlisted, security/allowlist.json); 20 attempts a
/// minute per address and 5 per client; every failure, whatever failed, is the same 401 <c>{"error":"invalid_client"}</c>.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(ISvcIntegrationAuth auth) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.IntegrationAuthPolicy)]
    [ProducesResponseType<IntegrationTokenViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Exchange([FromBody] IntegrationTokenRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var result = await auth.ExchangeAsync(request, HttpContext.Connection.RemoteIpAddress, ct);
        return result.HasErrors
            ? new ObjectResult(new { error = IntegrationErrors.InvalidClient }) { StatusCode = StatusCodes.Status401Unauthorized }
            : Ok(result.Data);
    }
}
