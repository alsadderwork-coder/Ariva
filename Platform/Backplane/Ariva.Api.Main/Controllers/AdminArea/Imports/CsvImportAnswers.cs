using Ariva.Api.Main.Controllers.AdminArea.Uploads;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Imports;

/// <summary>
/// The answers of the CSV import endpoints (ARV-123a): 404 without a detail, 413 for a file over 1 MB, 409 for a commit
/// that is not its preview's, the malware scan's refusals (<see cref="UploadScanAnswers"/>, ARV-092b: 400, or 503 with
/// Retry-After), 400 with the service's fixed text otherwise; nothing from the request is echoed.
/// </summary>
internal static class CsvImportAnswers
{
    /// <summary>The body may carry the 1 MB file plus the multipart framing and the token.</summary>
    public const long MaxRequestBytes = CsvImportLimits.MaxFileBytes + (64 * 1024);

    /// <summary>Why the upload itself is refused before the service reads it, or null.</summary>
    public static IActionResult RefuseUpload(ControllerBase controller, IFormFile file)
    {
        if (file is null || file.Length == 0)
            return controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: CsvImportErrors.FileRequired);
        return file.Length > CsvImportLimits.MaxFileBytes
            ? controller.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: CsvImportErrors.FileTooLarge)
            : null;
    }

    /// <summary>A token of the shape Data Protection gives (base64url, bounded), or null when it is one.</summary>
    public static IActionResult RefuseToken(ControllerBase controller, string previewToken) =>
        string.IsNullOrEmpty(previewToken) || previewToken.Length > CsvImportLimits.MaxTokenLength ||
        !previewToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: CsvImportErrors.PreviewTokenNotValid)
            : null;

    public static IActionResult Answer<T>(ControllerBase controller, Fluentx.Result<T> result)
    {
        if (!result.HasErrors)
            return controller.Ok(result.Data);
        var first = result.ErrorMessages.FirstOrDefault() ?? TopologyErrors.NotFound;
        if (first == TopologyErrors.NotFound)
            return controller.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        if (UploadScanAnswers.Refusal(controller, first) is { } scan)
            return scan;
        var (status, title) = first switch
        {
            CsvImportErrors.NotThePreview => (StatusCodes.Status409Conflict, "Conflict"),
            CsvImportErrors.FileTooLarge => (StatusCodes.Status413PayloadTooLarge, "Too large"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        return controller.Problem(statusCode: status, title: title, detail: first);
    }

    /// <summary>An answer that may carry credentials, or rows of the file, is never cached.</summary>
    public static void NoStore(ControllerBase controller)
    {
        controller.Response.Headers.CacheControl = "no-store";
        controller.Response.Headers.Pragma = "no-cache";
    }
}
