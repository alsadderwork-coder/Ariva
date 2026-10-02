using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Sensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Ingest.Controllers;

/// <summary>
/// What a device may ask Ariva (ARV-022), authenticated with its own credential only (a user token answers 401):
/// who it is, with the server's clock for its offset estimate, and the published geometry of its own queue zone, so a
/// device or gateway reports lines and zones by Ariva's names. Another zone answers 403. Rate limited per device.
/// The pushes themselves arrive in ARV-023.
/// </summary>
[ApiController]
[Route("api/v1/ingest")]
[EnableRateLimiting(RateLimitingExtensions.DevicePolicy)]
public sealed class DeviceController(ISvcDeviceGateway gateway, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("device")]
    [DeviceAuthenticated]
    [ProducesResponseType<DeviceSelfViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Self()
    {
        var (code, site, zone, state) = DeviceAuthentication.DeviceOf(User);
        Response.Headers.CacheControl = "no-store";
        return Ok(new DeviceSelfViewModel(code, site, zone, state, timeProvider.GetUtcNow().UtcDateTime));
    }

    /// <summary>The published geometry of the device's own queue zone: the zone, the zones that hang off it, their lines.</summary>
    [HttpGet("zones/{zone}")]
    [DeviceAuthenticated(ownZoneOnly: true)]
    [ProducesResponseType<DeviceZoneViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Zone(string zone, CancellationToken ct)
    {
        var (_, site, own, _) = DeviceAuthentication.DeviceOf(User);
        var result = await gateway.PublishedZoneAsync(site, own, ct);
        return result.HasErrors ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found") : Ok(result.Data);
    }
}
