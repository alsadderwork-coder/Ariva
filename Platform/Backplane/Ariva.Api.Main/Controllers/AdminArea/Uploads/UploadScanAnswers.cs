using System.Globalization;
using Ariva.Core.Services.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.AdminArea.Uploads;

/// <summary>
/// The answers of every upload endpoint to the malware scan's refusals (ARV-092b): a detection is 400 with its fixed text,
/// as any other refused file; no verdict outside vm-local is 503 with its fixed text and Retry-After, so a client waits and
/// sends the upload again. Nothing from the file or the scanner is echoed.
/// </summary>
internal static class UploadScanAnswers
{
    /// <summary>The answer to <paramref name="error"/> when it is one of the scan's refusals, or null for any other error.</summary>
    public static ObjectResult Refusal(ControllerBase controller, string error)
    {
        ArgumentNullException.ThrowIfNull(controller);
        switch (error)
        {
            case UploadScanErrors.Malware:
                return controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: UploadScanErrors.Malware);
            case UploadScanErrors.ScannerUnavailable:
                controller.Response.Headers.RetryAfter = UploadScanErrors.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return controller.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unavailable", detail: UploadScanErrors.ScannerUnavailable);
            default:
                return null;
        }
    }
}
