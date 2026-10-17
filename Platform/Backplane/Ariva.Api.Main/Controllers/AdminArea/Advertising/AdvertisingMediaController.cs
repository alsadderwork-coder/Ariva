using System.ComponentModel.DataAnnotations;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Security;
using Ariva.Api.Main.Controllers.AdminArea.Topology;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Services.Displays;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.Api.Main.Controllers.AdminArea.Advertising;

/// <summary>
/// A site's advertisement library (ARV-142a, wiki 11 section 9): images typed by their bytes and written anew on upload,
/// each with its advertiser, campaign and the site's local days it may play. Only approved items play, and the approver is
/// never the person who uploaded or last changed the item. Every action acts only while the site's advertising policy
/// allows advertising (<see cref="AdvertisingAllowedAttribute"/>: 403 while it is off, 404 alike for a site outside the
/// caller's sites and one that does not exist); an item of another site answers 404 under this site's route. Every change is
/// audited.
/// </summary>
[ApiController]
[Route("api/v1/admin/sites/{siteCode}/advertising/media")]
[SiteScoped]
[AdvertisingAllowed]
public sealed class AdvertisingMediaController(ISvcAdvertisingMedia media) : ControllerBase
{
    /// <summary>The body may carry the 10 MB file, the details and the multipart framing.</summary>
    private const long MaxRequestBytes = AdvertisingMedia.MaxImageBytes + (64 * 1024);

    /// <summary>
    /// For the image itself: no script, no fetches, no framing, a sandbox so a browser that opens it directly treats it as an
    /// opaque origin, and readable by this origin only (CWE-79).
    /// </summary>
    internal const string ContentPolicy = "default-src 'none'; sandbox; frame-ancestors 'none'";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<IReadOnlyList<AdvertisingMediaViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(string siteCode, CancellationToken ct) => TopologyAnswers.Ok(this, await media.ListAsync(siteCode, ct));

    [HttpGet("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingMediaViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await media.GetAsync(siteCode, id, ct));

    /// <summary>The stored image (as written anew), for the library's preview; never cached, never framed.</summary>
    [HttpGet("{id:guid}/content")]
    [Permission(nameof(Global.Defaults.Permissions.ViewAdvertising), nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Content(string siteCode, Guid id, CancellationToken ct)
    {
        var result = await media.OpenAsync(siteCode, id, ct);
        return result.HasErrors ? TopologyAnswers.Ok(this, result) : MediaFile(this, result.Data);
    }

    /// <summary>Uploads an image as a draft. Form fields: file, advertiser, campaign, startsOn and endsOn ("yyyy-MM-dd", the site's local days).</summary>
    [HttpPost]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [EnableRateLimiting(RateLimitingExtensions.UploadPolicy)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueLengthLimit = 1024, ValueCountLimit = 16)]
    [ProducesResponseType<AdvertisingMediaViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Upload(string siteCode, IFormFile file, [FromForm, MaxLength(AdvertisingMedia.MaxNameLength)] string advertiser,
        [FromForm, MaxLength(AdvertisingMedia.MaxNameLength)] string campaign, [FromForm] DateOnly startsOn, [FromForm] DateOnly endsOn, CancellationToken ct)
    {
        if (file is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not valid", detail: AdvertisingMediaErrors.UnsupportedFile);
        if (file.Length > AdvertisingMedia.MaxImageBytes)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: AdvertisingMediaErrors.TooLarge);

        await using var content = file.OpenReadStream();
        var result = await media.UploadAsync(siteCode, new AdvertisingMediaUpload(content, file.Length, file.FileName, advertiser, campaign, startsOn, endsOn), ct);
        if (result.HasErrors && result.ErrorMessages.Contains(AdvertisingMediaErrors.TooLarge))
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Too large", detail: AdvertisingMediaErrors.TooLarge);
        if (result.HasErrors && result.ErrorMessages.Contains(AdvertisingMediaErrors.TooManyUploads))
        {
            Response.Headers.RetryAfter = "60";
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many uploads", detail: AdvertisingMediaErrors.TooManyUploads);
        }

        return TopologyAnswers.Created(this, result, item => item.Id, $"/api/v1/admin/sites/{siteCode}/advertising/media");
    }

    /// <summary>Changes who advertises, the campaign or the dates; an approved item goes back to draft for a second person to approve again.</summary>
    [HttpPut("{id:guid}")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingMediaViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Change(string siteCode, Guid id, [FromBody] ChangeAdvertisingMediaRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await media.ChangeAsync(siteCode, id, request, ct));

    /// <summary>
    /// Approves a draft so it plays within its dates: a critical action (a second factor in the last 15 minutes), refused
    /// with 403 for the person who uploaded the item or last changed it, and with 409 when the item has changed since the
    /// approver was shown it (the body names the review token of what was shown).
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [Permission(nameof(Global.Defaults.Permissions.ApproveAdvertising))]
    [RequiresRecentMfa]
    [ProducesResponseType<AdvertisingMediaViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(string siteCode, Guid id, [FromBody] ApproveAdvertisingMediaRequest request, CancellationToken ct) =>
        TopologyAnswers.Ok(this, await media.ApproveAsync(siteCode, id, request, ct));

    /// <summary>Retires a draft or an approved item: it never plays again and is kept with its history.</summary>
    [HttpPost("{id:guid}/retire")]
    [Permission(nameof(Global.Defaults.Permissions.ManageAdvertising))]
    [ProducesResponseType<AdvertisingMediaViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retire(string siteCode, Guid id, CancellationToken ct) => TopologyAnswers.Ok(this, await media.RetireAsync(siteCode, id, ct));

    /// <summary>
    /// An item's stored image with the headers both the preview and the display fetch send: its own type (read from its
    /// bytes at upload), never sniffed, sandboxed, readable by this origin only, and kept by no cache.
    /// </summary>
    internal static IActionResult MediaFile(ControllerBase controller, AdvertisingMediaContent content)
    {
        var headers = controller.Response.Headers;
        headers.ContentSecurityPolicy = ContentPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentDisposition = "inline";
        headers.CacheControl = "private, no-store";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return controller.File(content.Content, content.ContentType);
    }
}
