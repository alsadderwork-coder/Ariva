using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Api.Common.Settings;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ariva.Api.Main.Controllers.Auth;

/// <summary>
/// Sign-in, refresh, logout, password change and TOTP (ADR-0026, ARV-010a to ARV-010c). An account with TOTP sends
/// its code with the password; without one the answer is 401 mfa_required. Every failed sign-in answers 401
/// with the same problem body, whatever the reason; every failed refresh answers 401 session_expired and clears the
/// cookie. The "auth" rate limit allows 10 attempts a minute per client address (429 after that). Responses are never
/// cached (no-store). The refresh token travels only in the __Secure-ariva_rt cookie (<see cref="RefreshCookie"/>).
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(ISvcAuthenticator authenticator, IOptions<CorsSettings> cors) : ControllerBase
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
        var result = await authenticator.LoginAsync(request, RefreshCookie.ContextOf(HttpContext), ct);
        if (result.HasErrors && result.ErrorMessages.Contains(ISvcAuthenticator.MfaRequired))
            return Unauthorized("Enter the code from your authenticator app", "https://ariva/problems/mfa-required", ISvcAuthenticator.MfaRequired);
        if (result.HasErrors)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: ISvcAuthenticator.InvalidCredentials, type: "https://ariva/problems/invalid-credentials");

        RefreshCookie.Write(Response, result.Data.RefreshToken, result.Data.RefreshLifetime);
        return Ok(result.Data.Token);
    }

    /// <summary>
    /// A new access token for the session in the refresh cookie, which is rotated. Anonymous by design (the cookie is
    /// the credential); needs the X-Ariva-Csrf header and an allow-listed Origin.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<TokenViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RefreshCookie.PassesCsrfCheck(Request, cors))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Refresh refused",
                detail: "Send the X-Ariva-Csrf header from the Ariva web origin.", type: "https://ariva/problems/csrf");
        }

        var result = await authenticator.RefreshAsync(RefreshCookie.ContextOf(HttpContext), ct);
        if (result.HasErrors)
        {
            RefreshCookie.Clear(Response);
            return SessionExpired();
        }

        RefreshCookie.Write(Response, result.Data.RefreshToken, result.Data.RefreshLifetime);
        return Ok(result.Data.Token);
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

    /// <summary>Revokes the session (and the session of the refresh cookie sent along) and clears the cookie.</summary>
    [HttpPost("logout")]
    [Authorize]
    [AllowPendingScope]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        await authenticator.LogoutAsync(RefreshCookie.ContextOf(HttpContext), ct);
        RefreshCookie.Clear(Response);
        return NoContent();
    }

    /// <summary>Starts TOTP enrolment: the secret and otpauth URI, shown this once (ARV-010c).</summary>
    [HttpPost("totp/enroll")]
    [Authorize]
    [AllowPendingScope]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<TotpEnrolmentViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EnrolTotp(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await authenticator.EnrolTotpAsync(ct);
        if (!result.HasErrors)
            return Ok(result.Data);
        return result.ErrorMessages.Contains(ISvcAuthenticator.AlreadyEnrolled)
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "Already enrolled", detail: ISvcAuthenticator.AlreadyEnrolled)
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: "Enrolment refused");
    }

    /// <summary>Confirms the enrolment with the first code; returns a new token and the recovery codes, shown once.</summary>
    [HttpPost("totp/confirm")]
    [Authorize]
    [AllowPendingScope]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<TotpConfirmedViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmTotp([FromBody] TotpCodeRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await authenticator.ConfirmTotpAsync(request, ct);
        return result.HasErrors
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "The code was not accepted", detail: result.ErrorMessages.FirstOrDefault())
            : Ok(result.Data);
    }

    /// <summary>New recovery codes after a valid TOTP code; the old ones stop working. ARV-010d adds step-up.</summary>
    [HttpPost("totp/recovery-codes")]
    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<RecoveryCodesViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegenerateRecoveryCodes([FromBody] TotpCodeRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await authenticator.RegenerateRecoveryCodesAsync(request, ct);
        return result.HasErrors
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "The code was not accepted", detail: result.ErrorMessages.FirstOrDefault())
            : Ok(result.Data);
    }

    private ObjectResult SessionExpired() =>
        Unauthorized("Your session has ended", SessionValidationMiddleware.ProblemType, ISvcAuthenticator.SessionExpired, "Sign in again.");

    private static ObjectResult Unauthorized(string title, string type, string error, string detail = null)
    {
        var problem = new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = title, Type = type, Detail = detail };
        problem.Extensions["error"] = error;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status401Unauthorized, ContentTypes = { "application/problem+json" } };
    }
}
