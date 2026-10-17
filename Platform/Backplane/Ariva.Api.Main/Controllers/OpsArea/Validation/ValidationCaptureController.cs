using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Validation;

/// <summary>
/// Manual count capture (ARV-104a): the observer's side of a validation campaign, all under <c>Validation.Capture</c> (the
/// validation observer's only permission). The running campaigns of a site with their lines; the caller's count of a line
/// for one 15-minute bin (201; one per line, bin and observer, a second is 409); a correction of the caller's own latest
/// count as the next revision with a reason (201); the caller's own counts. An optional <c>Idempotency-Key</c> header
/// returns the stored count (200) when the same request is sent again; a key belongs to its observer, so another observer's
/// identical key is a new request. A site the caller cannot see, a campaign of another site and another observer's count
/// answer 404; the account that created or started the campaign is refused (403, separation of duties); a campaign that
/// is not running answers 409.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/validation/capture/campaigns")]
[SiteScoped]
public sealed class ValidationCaptureController(ISvcValidationCapture capture) : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    [HttpGet]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [ProducesResponseType<IReadOnlyList<CaptureCampaignViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Running(string siteCode, CancellationToken ct) => ValidationAnswers.Ok(this, await capture.RunningAsync(siteCode, ct));

    [HttpGet("{id:guid}/counts")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [ProducesResponseType<PageViewModel<ManualCountViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OwnCounts(string siteCode, Guid id, [FromQuery] ManualCountCriteria criteria, CancellationToken ct) =>
        ValidationAnswers.Ok(this, await capture.OwnCountsAsync(siteCode, id, criteria, ct));

    [HttpPost("{id:guid}/counts")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [RequestSizeLimit(ValidationAnswers.MaxCountBodyBytes)]
    [ProducesResponseType<ManualCountViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ManualCountViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Capture(string siteCode, Guid id, [FromBody] CaptureManualCountRequest request,
        [FromHeader(Name = IdempotencyHeader)] string idempotencyKey, CancellationToken ct) =>
        ValidationAnswers.Captured(this, await capture.CaptureAsync(siteCode, id, request, idempotencyKey, ct));

    [HttpPost("{id:guid}/counts/{countId:guid}/corrections")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [RequestSizeLimit(ValidationAnswers.MaxCountBodyBytes)]
    [ProducesResponseType<ManualCountViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ManualCountViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Correct(string siteCode, Guid id, Guid countId, [FromBody] CorrectManualCountRequest request,
        [FromHeader(Name = IdempotencyHeader)] string idempotencyKey, CancellationToken ct) =>
        ValidationAnswers.Captured(this, await capture.CorrectAsync(siteCode, id, countId, request, idempotencyKey, ct));
}
