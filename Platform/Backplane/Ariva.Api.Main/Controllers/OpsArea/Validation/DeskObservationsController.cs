using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Ariva.Api.Main.Controllers.OpsArea.Validation;

/// <summary>
/// Observer logs of border desk states (ARV-104b; formulas F10, F18). The observer's side, under <c>Validation.Capture</c>: a
/// 15-minute batch of per-minute states (Closed, Idle, Serving, Paused) for desks in the campaign's scope, with a required
/// <c>Idempotency-Key</c> that belongs to its observer (a resend returns the stored batch, 200; the key with another batch is
/// 409); a correction of the caller's own latest state as the next revision with a reason (201, audited); and the caller's
/// own states. A manager (<c>Validation.View</c>) reads every observer's states only when it also sees border desks
/// (<c>BorderDesks.View</c>): desk-level data is border data, so anyone else gets an empty page with <c>desksIncluded</c>
/// false. The observer's side (batch, correction, own states) answers 403 to an account that holds an airport role without
/// <c>BorderDesks.View</c>, even with the Validation observer role (<see cref="Core.Security.BorderDeskAccess"/>). A site the
/// caller cannot see, a campaign of another site and another observer's state answer 404; the campaign's creator or starter is
/// refused (403); a campaign that is not running answers 409; minutes outside the planned days or in the future answer 400
/// without repeating them.
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteCode}/validation")]
[SiteScoped]
public sealed class DeskObservationsController(ISvcDeskObservations observations) : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Every observer's desk states (border callers only): current revisions (default) or all, by desk, observer and minute range (UTC).</summary>
    [HttpGet("campaigns/{id:guid}/desk-observations")]
    [Permission(nameof(Global.Defaults.Permissions.ViewValidation))]
    [ProducesResponseType<DeskObservationPageViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(string siteCode, Guid id, [FromQuery] DeskObservationCriteria criteria, CancellationToken ct) =>
        ValidationAnswers.Ok(this, await observations.SearchAsync(siteCode, id, criteria, ct));

    /// <summary>The caller's own desk states of the campaign.</summary>
    [HttpGet("capture/campaigns/{id:guid}/desk-observations")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [ProducesResponseType<PageViewModel<DeskObservationViewModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OwnObservations(string siteCode, Guid id, [FromQuery] DeskObservationCriteria criteria, CancellationToken ct) =>
        ValidationAnswers.Ok(this, await observations.OwnObservationsAsync(siteCode, id, criteria, ct));

    /// <summary>Records a 15-minute batch (201), or returns the stored batch for a resend with the same Idempotency-Key (200).</summary>
    [HttpPost("capture/campaigns/{id:guid}/desk-observations")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [RequestSizeLimit(ValidationAnswers.MaxBatchBodyBytes)]
    [ProducesResponseType<DeskObservationBatchViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<DeskObservationBatchViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Capture(string siteCode, Guid id, [FromBody] CaptureDeskObservationsRequest request,
        [FromHeader(Name = IdempotencyHeader)] string idempotencyKey, CancellationToken ct) =>
        ValidationAnswers.Recorded(this, await observations.CaptureAsync(siteCode, id, request, idempotencyKey, ct), r => r.Replayed, r => r.Batch);

    /// <summary>Corrects the caller's own latest state of a desk and minute as the next revision with a reason (201); an optional Idempotency-Key returns the stored correction (200).</summary>
    [HttpPost("capture/campaigns/{id:guid}/desk-observations/{observationId:guid}/corrections")]
    [Permission(nameof(Global.Defaults.Permissions.CaptureValidation))]
    [RequestSizeLimit(ValidationAnswers.MaxCountBodyBytes)]
    [ProducesResponseType<DeskObservationViewModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<DeskObservationViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Correct(string siteCode, Guid id, Guid observationId, [FromBody] CorrectDeskObservationRequest request,
        [FromHeader(Name = IdempotencyHeader)] string idempotencyKey, CancellationToken ct) =>
        ValidationAnswers.Recorded(this, await observations.CorrectAsync(siteCode, id, observationId, request, idempotencyKey, ct), r => r.Replayed, r => r.Observation);
}
