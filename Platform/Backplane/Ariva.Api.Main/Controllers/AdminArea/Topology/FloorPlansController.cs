using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.AdminArea.Topology;

/// <summary>
/// The floor plan of a level (ARV-018): upload (multipart, field "file", at most 20 MB, PNG, JPEG or SVG sniffed from
/// the bytes, SVG sanitised), metadata, the image itself with a locked-down content security policy, calibration
/// (scale and origin) and delete. Site-scoped through the level.
/// </summary>
[ApiController]
[Route("api/v1/admin/levels/{levelId:guid}/floor-plan")]
[SiteScoped]
public sealed class FloorPlansController(ISvcFloorPlans plans) : ControllerBase
{
    /// <summary>The body may carry the 20 MB file plus the multipart framing.</summary>
    private const long MaxRequestBytes = FloorPlan.MaxBytes + (64 * 1024);

    /// <summary>
    /// For the image: no script, no fetches, no framing; inline styles only so a sanitised SVG still renders, and a
    /// sandbox so a browser that opens it directly treats it as an opaque origin (CWE-79).
    /// </summary>
    internal const string ContentPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox; frame-ancestors 'none'";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewFloorPlan))]
    [ProducesResponseType<FloorPlanViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid levelId, CancellationToken ct) => TopologyAnswers.Ok(this, await plans.GetAsync(levelId, ct));

    /// <summary>The image. SVG is served sanitised and under a sandboxing content security policy, never as a page.</summary>
    [HttpGet("content")]
    [Permission(nameof(Global.Defaults.Permissions.ViewFloorPlan))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Content(Guid levelId, CancellationToken ct)
    {
        var result = await plans.OpenAsync(levelId, ct);
        if (result.HasErrors)
            return TopologyAnswers.Ok(this, result);

        Response.Headers.ContentSecurityPolicy = ContentPolicy;
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition = "inline";
        return File(result.Data.Content, result.Data.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{result.Data.Sha256}\""), lastModified: null);
    }

    /// <summary>Uploads or replaces the plan. Form fields: file, metresPerPixel, originX, originY.</summary>
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.CreateFloorPlan))]
    [EnableRateLimiting(RateLimitingExtensions.UploadPolicy)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<FloorPlanViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Upload(Guid levelId, IFormFile file, [FromForm] double metresPerPixel, [FromForm] double originX, [FromForm] double originY, CancellationToken ct)
    {
        if (file is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: TopologyErrors.UnsupportedFile);
        if (file.Length > FloorPlan.MaxBytes)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: TopologyErrors.TooLarge);

        await using var content = file.OpenReadStream();
        var result = await plans.UploadAsync(levelId, new FloorPlanUpload(content, file.Length, file.FileName, metresPerPixel, originX, originY), ct);
        if (result.HasErrors && result.ErrorMessages.Contains(TopologyErrors.TooLarge))
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: TopologyErrors.TooLarge);
        return result.HasErrors ? TopologyAnswers.Ok(this, result) : StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpPut("calibration")]
    [Permission(nameof(Global.Defaults.Permissions.EditFloorPlan))]
    [ProducesResponseType<FloorPlanViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Calibrate(Guid levelId, [FromBody] CalibrateFloorPlanRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await plans.CalibrateAsync(levelId, request, ct));

    [HttpDelete]
    [Permission(nameof(Global.Defaults.Permissions.DeleteFloorPlan))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid levelId, CancellationToken ct) => TopologyAnswers.NoContent(this, await plans.DeleteAsync(levelId, ct));
}
