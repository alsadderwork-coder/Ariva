using System.ComponentModel.DataAnnotations;
using Ariva.Simulation.Api.Emulators;
using Ariva.Simulation.Api.Emulators.Aman;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Simulation.Api.Controllers;

/// <summary>The AMAN-style token request: client id, client secret and the current TOTP code.</summary>
public sealed record MockAmanAuthRequest(
    [Required, MaxLength(64)] string ClientId,
    [Required, MaxLength(256)] string ClientSecret,
    [Required, MaxLength(6)] string TotpCode);

/// <summary>
/// The mock AMAN Integration API (ARV-029), the partner of Ariva's outbound AMAN connector (TotpClientCredentials,
/// ARV-050). <c>POST aman/api/v1/auth</c> exchanges client id, secret and a TOTP code for a token (AMAN's shape:
/// <c>accessToken</c>, <c>expiresAt</c>, <c>sessionId</c>), limited per address; every failure is the same 401. The feed
/// API answers what the emulated AMAN published, oldest first after a sequence number, in AMAN's <c>Result</c> envelope
/// (<c>data</c>, <c>hasErrors</c>, <c>errorMessages</c>; the real AMAN API's paths and paging are To confirm).
/// </summary>
[ApiController]
[Route("aman/api/v1")]
public sealed class MockAmanController(MockAmanTokens tokens, AmanFeedBuffer buffer, ILogger<MockAmanController> logger) : ControllerBase
{
    private const int MaxPage = 500;

    [HttpPost("auth")]
    [AllowAnonymous]
    [EnableRateLimiting(MockPartnerSchemes.AmanAuthLimit)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Auth([FromBody] MockAmanAuthRequest request)
    {
        if (tokens.Exchange(request?.ClientId, request?.ClientSecret, request?.TotpCode) is not { } issued)
        {
            logger.LogWarning("Mock AMAN refused a token exchange");
            return Unauthorized(new { error = "invalid_client" });
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new { accessToken = issued.Token, expiresAt = issued.Expires, sessionId = Guid.NewGuid() });
    }

    /// <summary>One contract's records after <paramref name="after"/>: desk-sessions, desk-interval-stats, egate-interval-stats or inbound-lane-demand.</summary>
    [HttpGet("feed/{contract}")]
    [Authorize(Policy = MockPartnerSchemes.AmanPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Feed(string contract, [FromQuery] long? after, [FromQuery] int? limit)
    {
        if (!AmanContracts.All.Contains(contract, StringComparer.Ordinal))
            return NotFound();
        if (after is < 0 || limit is < 1 or > MaxPage)
            return BadRequest(new { data = (object)null, hasErrors = true, errorMessages = new[] { $"after is 0 or more and limit 1 to {MaxPage}." } });
        var (items, next) = buffer.After(contract, after ?? 0, limit ?? MaxPage);
        return new JsonResult(new { data = new { items, next }, hasErrors = false, errorMessages = Array.Empty<string>() }, AmanContracts.Json);
    }
}
