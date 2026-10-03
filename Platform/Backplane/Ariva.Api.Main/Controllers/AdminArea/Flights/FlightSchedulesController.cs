using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Flights;
using Ariva.Infra.Flights.Ssim;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.AdminArea.Flights;

/// <summary>
/// SSIM schedule files for a site (ARV-046, wiki 08 section 9): preview what a file holds for the site (multipart field
/// "file", at most 20 MB, form field "horizonDays", 1 to 200, default 60), then import exactly what was previewed (the
/// same file and horizon at the same site, by the same user, within two hours: form field "previewToken", from the
/// preview). The site's airports place each leg; a schedule creates legs and never changes one a live feed has reported.
/// One upload at a time per host (the upload policy). Site-scoped.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/flight-schedules")]
[SiteScoped]
[EnableRateLimiting(RateLimitingExtensions.UploadPolicy)]
public sealed class FlightSchedulesController(ISvcFlightSchedules schedules) : ControllerBase
{
    /// <summary>The body may carry the 20 MB file plus the multipart framing and the form fields.</summary>
    private const long MaxRequestBytes = SsimReader.MaxBytes + (64 * 1024);

    private const int MaxTokenLength = 2048;

    [HttpPost("preview")]
    [Permission(nameof(Global.Defaults.Permissions.CreateFlightSchedule))]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<SsimPreviewViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Preview(string siteCode, IFormFile file, [FromForm] int? horizonDays, CancellationToken ct)
    {
        if (await RefuseAsync(siteCode, file, ct) is { } refused)
            return refused;
        await using var content = file.OpenReadStream();
        return Answer(await schedules.PreviewAsync(siteCode, content, horizonDays ?? 60, ct));
    }

    [HttpPost("import")]
    [Permission(nameof(Global.Defaults.Permissions.CreateFlightSchedule))]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<SsimImportViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Import(string siteCode, IFormFile file, [FromForm] int? horizonDays, [FromForm] string previewToken, CancellationToken ct)
    {
        if (await RefuseAsync(siteCode, file, ct) is { } refused)
            return refused;
        if (string.IsNullOrEmpty(previewToken) || previewToken.Length > MaxTokenLength || !previewToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: "previewToken is the token the preview gave.");
        await using var content = file.OpenReadStream();
        return Answer(await schedules.ImportAsync(siteCode, content, horizonDays ?? 60, previewToken, ct));
    }

    private async Task<ObjectResult> RefuseAsync(string siteCode, IFormFile file, CancellationToken ct)
    {
        // An unknown site answers 404 like one the caller does not hold ([SiteScoped]), before anything about the upload.
        if (!await schedules.SiteExistsAsync(siteCode, ct))
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        if (file is null || file.Length == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: "An SSIM file is required in the form field \"file\".");
        return file.Length > SsimReader.MaxBytes
            ? Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: "An SSIM file is at most 20 MB.")
            : null;
    }

    private IActionResult Answer<T>(Fluentx.Result<T> result)
    {
        if (!result.HasErrors)
            return Ok(result.Data);
        var first = result.ErrorMessages.First();
        return first switch
        {
            FlightScheduleErrors.NotThePreview => Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: first),
            _ when first.Contains("20 MB", StringComparison.Ordinal) => Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: first),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: first)
        };
    }
}
