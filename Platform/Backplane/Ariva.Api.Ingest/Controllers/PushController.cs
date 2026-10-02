using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Sensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Ingest.Controllers;

/// <summary>
/// Sensor pushes (ARV-023) for the device's own zone: the canonical dialect and the Xovis dialect, JSON of at most 256 KB
/// (413 beyond) and 2,000 events. 202 with what was accepted, refused, flagged and ignored; 400 when the message is
/// malformed or the device speaks another dialect; 503 when the events could not be stored, so the device sends again.
/// </summary>
[ApiController]
[Route("api/v1/ingest/zones/{zone}")]
[EnableRateLimiting(RateLimitingExtensions.DevicePolicy)]
public sealed class PushController(SensingIngest ingest, TimeProvider timeProvider, ILogger<PushController> logger) : ControllerBase
{
    [HttpPost("events")]
    [DeviceAuthenticated(ownZoneOnly: true)]
    [RequestSizeLimit(IngestSettings.MaxBodyBytes)]
    [ProducesResponseType<IngestOutcome>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Canonical(string zone, CancellationToken ct) => PushAsync(DeviceDialect.Canonical, ct);

    [HttpPost("xovis")]
    [DeviceAuthenticated(ownZoneOnly: true)]
    [RequestSizeLimit(IngestSettings.MaxBodyBytes)]
    [ProducesResponseType<IngestOutcome>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Xovis(string zone, CancellationToken ct) => PushAsync(DeviceDialect.Xovis, ct);

    private async Task<IActionResult> PushAsync(DeviceDialect dialect, CancellationToken ct)
    {
        var device = DeviceAuthentication.RecordOf(HttpContext);
        if (device is null)
            return Forbid();
        var received = timeProvider.GetUtcNow().UtcDateTime;
        // Checked here rather than with [Consumes]: a mismatch there leaves no endpoint, and the caller would get the
        // fallback 401 instead of being told the body must be JSON.
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(Request.ContentType, out var type) ||
            !(type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase)))
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported media type", detail: "A push is application/json.");

        // A body trickling in slower than 1 KB a second (after a 5 second grace) is cut off, so a slow sender cannot hold
        // connections open for minutes (Kestrel's default is 240 bytes a second).
        if (HttpContext.Features.Get<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>() is { } rate)
            rate.MinDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(1024, TimeSpan.FromSeconds(5));

        byte[] body;
        try
        {
            body = await ReadAsync(ct);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: "A push is at most 256 KB.");
        }

        if (body is null)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: "A push is at most 256 KB.");

        Fluentx.Result<IngestOutcome> result;
        try
        {
            result = await ingest.IngestAsync(device, dialect, body, received, ct);
        }
        catch (SensingSinkUnavailableException e)
        {
            logger.LogError(e, "Push from device {Device} could not be stored", device.Code);
            Response.Headers.RetryAfter = "5";
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unavailable", detail: "The push could not be stored; send it again.");
        }

        if (result.HasErrors)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: result.ErrorMessages.First());
        return StatusCode(StatusCodes.Status202Accepted, result.Data);
    }

    /// <summary>The body, or null when it is larger than allowed (a chunked body has no length to check up front).</summary>
    private async Task<byte[]> ReadAsync(CancellationToken ct)
    {
        if (Request.ContentLength is > IngestSettings.MaxBodyBytes)
            return null;
        using var buffer = new MemoryStream(Request.ContentLength is { } length ? (int)length : 16 * 1024);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > IngestSettings.MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
