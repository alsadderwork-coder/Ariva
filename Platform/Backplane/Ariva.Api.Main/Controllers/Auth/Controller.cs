using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.Auth;

/// <summary>
/// Sign-in and password change (ADR-0026, ARV-010a). Every failed sign-in answers 401 with the same problem body,
/// whatever the reason. The "auth" rate limit allows 10 attempts a minute per client address (429 after that).
/// Responses are never cached (no-store); the refresh cookie and logout of the session arrive with ARV-010b.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(ISvcAuthenticator authenticator) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<TokenViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await authenticator.LoginAsync(request, ct);
        return result.HasErrors
            ? Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: ISvcAuthenticator.InvalidCredentials, type: "https://ariva/problems/invalid-credentials")
            : Ok(result.Data);
    }

    [HttpPost("change-password")]
    [Authorize]
    [AllowPendingScope]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<TokenViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await authenticator.ChangePasswordAsync(request, ct);
        if (!result.HasErrors)
            return Ok(result.Data);

        var details = new ValidationProblemDetails(new Dictionary<string, string[]> { ["password"] = result.ErrorMessages.ToArray() })
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The password was not changed"
        };
        return BadRequest(details);
    }

    /// <summary>Ends the session. Until server-side sessions (ARV-010b) the client discards its token; this only confirms.</summary>
    [HttpPost("logout")]
    [Authorize]
    [AllowPendingScope]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Logout() => NoContent();
}
