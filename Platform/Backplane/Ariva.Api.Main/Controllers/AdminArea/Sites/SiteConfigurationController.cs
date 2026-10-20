using System.ComponentModel.DataAnnotations;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Uploads;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Topology;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.AdminArea.Sites;

/// <summary>
/// A site's configuration as one bundle (ARV-122a, ARV-122b): export it (topology, desk code mappings, the published zone
/// profile, floor plans, devices, alert rules and displays, with a SHA-256 manifest and the bundle hash; never a credential,
/// calibration, access setting, escalation contact or pairing secret) and import a bundle into an empty site (the zone
/// profile arrives as a draft, each device in Commissioning with a new credential shown once in the answer, displays
/// unpaired). Both are critical actions: <c>SiteConfiguration.Transfer</c> (system administrators) with a second
/// factor in the last 15 minutes. Site-scoped (404 outside the caller's sites, nothing echoed) and audited. The import's
/// body is at most 20 MB (the file-import class, 413 beyond) and runs one at a time per host with the other uploads, under
/// their request timeout (the <c>transfer</c> policy, ARV-096); it is read whole with the request's token before model binding and
/// must arrive steadily (408 when too slow: <see cref="SteadyUploadAttribute"/>). Every refusal is a fixed text; answers are never cached.
/// Every decoded part of a bundle is scanned for malware before it is read (ARV-092b): 400 for a detection, 503 with
/// Retry-After while no scanner answers.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/configuration")]
[SiteScoped]
[EnableRateLimiting(RateLimitingExtensions.UploadPolicy)]
[RequestTimeout(RequestTimeoutsExtensions.TransferPolicy)]
[SteadyUpload(TooLarge = BundleTooLarge)]
public sealed class SiteConfigurationController(ISvcSiteConfiguration configuration) : ControllerBase
{
    /// <summary>The refusal of a bundle over 20 MB (the steady upload's 413, declared or growing past the limit while read).</summary>
    internal const string BundleTooLarge = "A site configuration bundle is at most 20 MB.";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.TransferSiteConfiguration))]
    [RequiresRecentMfa]
    [ProducesResponseType<SiteConfigurationExportViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Export(string siteCode, CancellationToken ct)
    {
        NoStore();
        return Answer(await configuration.ExportAsync(siteCode, ct), Ok);
    }

    /// <summary>
    /// Imports a bundle (the <c>bundle</c> of an export) into an empty site. <paramref name="airportCode"/>, when given,
    /// places every terminal at that existing airport instead of the bundle's own (a copy within one deployment).
    /// </summary>
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.TransferSiteConfiguration))]
    [RequiresRecentMfa]
    [RequestSizeLimit(SiteConfigurationLimits.MaxBundleBytes)]
    [ProducesResponseType<SiteConfigurationImportViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Import(string siteCode, [FromBody] ImportSiteConfigurationRequest request, [FromQuery, MaxLength(3)] string airportCode,
        CancellationToken ct)
    {
        NoStore();
        return Answer(await configuration.ImportAsync(siteCode, request, airportCode, ct),
            view => Created($"/api/v1/admin/sites/{siteCode}/configuration", view));
    }

    /// <summary>
    /// 404 without a detail; 409, 413 and 400 with the service's fixed text, the malware scan's refusals as every upload's
    /// (<see cref="UploadScanAnswers"/>, ARV-092b), and nothing else from the request.
    /// </summary>
    private IActionResult Answer<T>(Fluentx.Result<T> result, Func<T, IActionResult> ok)
    {
        if (!result.HasErrors)
            return ok(result.Data);
        var first = result.ErrorMessages.FirstOrDefault() ?? TopologyErrors.NotFound;
        if (first == TopologyErrors.NotFound)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        if (UploadScanAnswers.Refusal(this, first) is { } scan)
            return scan;
        var (status, title) = first switch
        {
            _ when SiteConfigurationErrors.Conflicts.Contains(first) => (StatusCodes.Status409Conflict, "Conflict"),
            SiteConfigurationErrors.PartTooLarge => (StatusCodes.Status413PayloadTooLarge, "Too large"),
            _ => (StatusCodes.Status400BadRequest, "Not valid")
        };
        return Problem(statusCode: status, title: title, detail: first);
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
