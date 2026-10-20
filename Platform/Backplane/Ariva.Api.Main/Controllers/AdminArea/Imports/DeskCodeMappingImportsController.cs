using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Imports;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.AdminArea.Imports;

/// <summary>
/// The desk code mappings of a site (AMAN desk and gate codes, AODB counter codes) as a CSV file (ARV-123a): preview
/// (multipart field "file", at most 1 MB) answers each refused row by row, column and fixed text and, for a file without
/// one, a token; commit (the same file and form field "previewToken", by the same user at the same site within 15
/// minutes) creates every mapping or none. The single mapping endpoint's permission; not a critical action, like that
/// endpoint (an owner question in docs/product/decisions.md). Site-scoped; one upload at a time per host, with the transfer
/// request timeout, arriving steadily or ended (408 when too slow: <see cref="SteadyUploadAttribute"/>, ARV-096); never cached.
/// The file is scanned for malware at preview and again at commit (ARV-092b): 400 for a detection, 503 with Retry-After
/// while no scanner answers.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/desk-code-mapping-imports")]
[SiteScoped]
[EnableRateLimiting(RateLimitingExtensions.UploadPolicy)]
[RequestTimeout(RequestTimeoutsExtensions.TransferPolicy)]
[SteadyUpload(TooLarge = CsvImportErrors.FileTooLarge)]
public sealed class DeskCodeMappingImportsController(ISvcDeskCodeMappingImports imports) : ControllerBase
{
    [HttpPost("preview")]
    [Permission(nameof(Global.Defaults.Permissions.CreateDeskCodeMapping))]
    [RequestSizeLimit(CsvImportAnswers.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CsvImportAnswers.MaxRequestBytes)]
    [ProducesResponseType<DeskCodeMappingImportPreviewViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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
    [Permission(nameof(Global.Defaults.Permissions.CreateDeskCodeMapping))]
    [RequestSizeLimit(CsvImportAnswers.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CsvImportAnswers.MaxRequestBytes)]
    [ProducesResponseType<DeskCodeMappingImportCommitViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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
