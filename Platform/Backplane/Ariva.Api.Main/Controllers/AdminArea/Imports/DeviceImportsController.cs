using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Imports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.AdminArea.Imports;

/// <summary>
/// The device list of a site as a CSV file (ARV-123a, wiki 07 section 2): preview (multipart field "file", at most 1 MB)
/// answers each refused row by row, column and fixed text and, for a file without one, a token; commit (the same file and
/// form field "previewToken", by the same user at the same site within 15 minutes) registers every device in
/// Commissioning or none, and answers each new credential once. Both need the registration's permission and, since
/// registering devices is a critical action (CreateDevice), a second factor in the last 15 minutes. Site-scoped; one
/// upload at a time per host (the upload policy); never cached.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/device-imports")]
[SiteScoped]
[EnableRateLimiting(RateLimitingExtensions.UploadPolicy)]
public sealed class DeviceImportsController(ISvcDeviceImports imports) : ControllerBase
{
    [HttpPost("preview")]
    [Permission(nameof(Global.Defaults.Permissions.CreateDevice))]
    [RequiresRecentMfa]
    [RequestSizeLimit(CsvImportAnswers.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CsvImportAnswers.MaxRequestBytes)]
    [ProducesResponseType<DeviceImportPreviewViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Preview(string siteCode, IFormFile file, CancellationToken ct)
    {
        CsvImportAnswers.NoStore(this);
        // An unknown site, or one out of reach, answers 404 before the upload is processed (model binding has received it, at most 1 MB plus 64 KB).
        if (!await imports.SiteReachableAsync(siteCode, ct))
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        if (CsvImportAnswers.RefuseUpload(this, file) is { } refused)
            return refused;
        return CsvImportAnswers.Answer(this, await imports.PreviewAsync(siteCode, file.OpenReadStream, ct));
    }

    [HttpPost("commit")]
    [Permission(nameof(Global.Defaults.Permissions.CreateDevice))]
    [RequiresRecentMfa]
    [RequestSizeLimit(CsvImportAnswers.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CsvImportAnswers.MaxRequestBytes)]
    [ProducesResponseType<DeviceImportCommitViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Commit(string siteCode, IFormFile file, [FromForm] string previewToken, CancellationToken ct)
    {
        CsvImportAnswers.NoStore(this);
        if (!await imports.SiteReachableAsync(siteCode, ct))
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        if ((CsvImportAnswers.RefuseUpload(this, file) ?? CsvImportAnswers.RefuseToken(this, previewToken)) is { } refused)
            return refused;
        return CsvImportAnswers.Answer(this, await imports.CommitAsync(siteCode, file.OpenReadStream, previewToken, ct));
    }
}
